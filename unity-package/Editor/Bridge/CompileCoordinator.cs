using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace Gamebrew.Bridge
{
    /// <summary>
    /// Waits for Unity asset import and script compilation without blocking the main thread.
    /// Safe to call from the bridge HTTP handler thread (same pattern as <see cref="TestRunCoordinator"/>).
    /// </summary>
    public static class CompileCoordinator
    {
        private const int DefaultTimeoutMs = 120_000;

        public static JObject Execute(JObject args)
        {
            bool refresh = args?["refresh"]?.Value<bool>() ?? false;
            int timeoutMs = args?["timeoutMs"]?.Value<int>() ?? DefaultTimeoutMs;
            if (timeoutMs <= 0)
                return Error("timeoutMs must be positive");

            var result = Wait(refresh, timeoutMs);
            if (!result.Success)
                return Error(result.Error);

            return Ok(new JObject
            {
                ["isReady"] = true,
                ["hadCompileErrors"] = result.HadCompileErrors,
                ["waitedMs"] = result.WaitedMs,
                ["refreshed"] = refresh,
                ["errors"] = new JArray(result.CompileErrors),
            });
        }

        public static WaitResult Wait(bool refresh = false, int timeoutMs = DefaultTimeoutMs)
        {
            var gate = new ManualResetEventSlim(false);
            long startMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var session = new WaitSession(gate);

            MainThreadDispatcher.Enqueue(() => session.Begin(refresh));

            bool timedOut = !gate.Wait(timeoutMs);
            if (timedOut)
                MainThreadDispatcher.Enqueue(session.Cancel);

            long waitedMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - startMs;

            if (timedOut)
            {
                return WaitResult.Fail(
                    $"editor.waitForCompile timed out after {timeoutMs / 1000}s " +
                    $"(isCompiling={EditorApplication.isCompiling}, isUpdating={EditorApplication.isUpdating})",
                    waitedMs);
            }

            if (session.HadCompileErrors)
            {
                string detail = session.CompileErrors.Count > 0
                    ? session.CompileErrors[0]
                    : "Script compilation failed";
                return WaitResult.Fail(
                    "Script compilation failed: " + detail,
                    waitedMs,
                    hadCompileErrors: true,
                    session.CompileErrors);
            }

            return WaitResult.Ok(waitedMs);
        }

        private static IEnumerable<string> CollectRecentScriptErrors()
        {
            return ConsoleLogBuffer.Recent(100)
                .Where(e => e.Type == "error" && e.Message.IndexOf("error CS", StringComparison.Ordinal) >= 0)
                .Select(e => e.Message)
                .Distinct()
                .Take(5);
        }

        private sealed class WaitSession
        {
            private readonly ManualResetEventSlim _gate;
            private readonly List<string> _compileErrors = new List<string>();

            public bool HadCompileErrors { get; private set; }
            public IReadOnlyList<string> CompileErrors => _compileErrors;

            public WaitSession(ManualResetEventSlim gate) => _gate = gate;

            public void Begin(bool refresh)
            {
                if (refresh)
                    AssetDatabase.Refresh();

                EditorApplication.update += OnEditorUpdate;
                OnEditorUpdate();
            }

            public void Cancel() => EditorApplication.update -= OnEditorUpdate;

            private void OnEditorUpdate()
            {
                if (EditorApplication.isCompiling || EditorApplication.isUpdating)
                    return;

                EditorApplication.update -= OnEditorUpdate;

                HadCompileErrors = EditorUtility.scriptCompilationFailed;
                if (HadCompileErrors)
                    _compileErrors.AddRange(CollectRecentScriptErrors());

                _gate.Set();
            }
        }

        private static JObject Ok(JObject data) => new JObject { ["ok"] = true, ["data"] = data };
        private static JObject Error(string message) => new JObject { ["ok"] = false, ["error"] = message };

        public readonly struct WaitResult
        {
            public bool Success { get; }
            public bool HadCompileErrors { get; }
            public string Error { get; }
            public long WaitedMs { get; }
            public IReadOnlyList<string> CompileErrors { get; }

            private WaitResult(
                bool success,
                bool hadCompileErrors,
                string error,
                long waitedMs,
                IReadOnlyList<string> compileErrors)
            {
                Success = success;
                HadCompileErrors = hadCompileErrors;
                Error = error;
                WaitedMs = waitedMs;
                CompileErrors = compileErrors ?? Array.Empty<string>();
            }

            public static WaitResult Ok(long waitedMs) =>
                new WaitResult(true, false, null, waitedMs, Array.Empty<string>());

            public static WaitResult Fail(
                string error,
                long waitedMs,
                bool hadCompileErrors = false,
                IReadOnlyList<string> compileErrors = null) =>
                new WaitResult(false, hadCompileErrors, error, waitedMs, compileErrors);
        }
    }
}
