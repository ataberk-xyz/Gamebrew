using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;

namespace Gamebrew.Bridge
{
    /// <summary>
    /// Runs Unity EditMode tests from the bridge WITHOUT blocking.
    /// <para>
    /// <b>editor.runTests</b> starts a run via <see cref="TestRunnerApi"/> (callback-driven,
    /// progressing over main-thread frames) and returns immediately with
    /// <c>{ ok, started:true, runId }</c>. It does NOT wait for completion, so the bridge's
    /// HTTP-handler thread never blocks and the Editor stays responsive while tests run.
    /// </para>
    /// <para>
    /// <b>editor.getTestResults</b> reports the live state of the current/last run:
    /// <c>running</c>, or <c>done</c> with counts + failures, or <c>none</c>.
    /// The Node MCP layer polls getTestResults until done, lifting the old 120s wall.
    /// </para>
    /// Called directly from <see cref="BridgeServer"/> — NOT through MainThreadDispatcher.Run —
    /// but only the lightweight enqueue/read work happens on the HTTP-handler thread; the
    /// TestRunnerApi.Execute call and all ICallbacks run on the main thread.
    /// </summary>
    public static class TestRunCoordinator
    {
        private static readonly object _stateLock = new object();
        private static RunCallbacks _current;   // non-null while a run is live or finished-but-unread
        private static int _runCounter;

        // ── editor.runTests : START (non-blocking) ───────────────────────────
        public static JObject Execute(JObject args)
        {
            var platform   = args?["platform"]?.Value<string>() ?? "EditMode";
            var assembly   = args?["assembly"]?.Value<string>();
            var nameFilter = args?["filter"]?.Value<string>();

            if (!string.Equals(platform, "EditMode", StringComparison.OrdinalIgnoreCase))
                return Error($"platform '{platform}' is not supported yet; only EditMode is available");

            lock (_stateLock)
            {
                if (_current != null && !_current.IsDone)
                {
                    // A run is already live — do NOT double-start. Report it.
                    return Ok(new JObject
                    {
                        ["started"] = false,
                        ["runId"]   = _current.RunId,
                        ["status"]  = "running",
                        ["note"]    = "a test run is already in progress; poll editor.getTestResults",
                    });
                }
            }

            // Compile must be settled before we can run. This is a bounded wait on the
            // background thread (no main-thread block) and is fast when already idle.
            var compile = CompileCoordinator.Wait(refresh: false, timeoutMs: 120_000);
            if (!compile.Success)
                return Error(compile.Error);

            // Exit Play Mode on the main thread — EditMode TestRunner fails while playing.
            JObject exitPlay = MainThreadDispatcher.Run(() =>
            {
                if (!EditorApplication.isPlaying)
                    return null;
                return EditorPlayModeCoordinator.SetPlaying(false, wait: true, timeoutMs: 10_000);
            }, timeoutMs: 15_000);

            if (exitPlay != null && exitPlay["ok"]?.Value<bool>() != true)
                return Error("EditMode tests require exiting Play Mode first: " +
                             exitPlay["error"]?.Value<string>());

            string runId;
            RunCallbacks callbacks;
            lock (_stateLock)
            {
                runId     = "run-" + (++_runCounter) + "-" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                callbacks = new RunCallbacks(runId, platform, nameFilter);
                _current  = callbacks;
            }

            // Schedule the test run on the main thread (non-blocking enqueue).
            MainThreadDispatcher.Enqueue(() =>
            {
                try
                {
                    var api = ScriptableObject.CreateInstance<TestRunnerApi>();
                    callbacks.Api = api;
                    api.RegisterCallbacks(callbacks);

                    var filter = new Filter { testMode = TestMode.EditMode };
                    if (!string.IsNullOrEmpty(assembly))
                        filter.assemblyNames = new[] { assembly };

                    api.Execute(new ExecutionSettings(filter));
                }
                catch (Exception ex)
                {
                    callbacks.MarkStartError(ex.Message);
                }
            });

            return Ok(new JObject
            {
                ["started"] = true,
                ["runId"]   = runId,
                ["status"]  = "running",
            });
        }

        // ── editor.getTestResults : POLL ─────────────────────────────────────
        public static JObject GetResults(JObject args)
        {
            var wantId = args?["runId"]?.Value<string>();

            RunCallbacks cb;
            lock (_stateLock) { cb = _current; }

            if (cb == null)
                return Ok(new JObject { ["status"] = "none" });

            if (!string.IsNullOrEmpty(wantId) && !string.Equals(wantId, cb.RunId, StringComparison.Ordinal))
            {
                // Asked about a run we no longer track (superseded by a newer one).
                return Ok(new JObject { ["status"] = "none", ["runId"] = wantId });
            }

            if (!cb.IsDone)
                return Ok(new JObject { ["status"] = "running", ["runId"] = cb.RunId });

            if (cb.StartError != null)
                return Error("Failed to start test run: " + cb.StartError);

            return Ok(cb.BuildResult());
        }

        // ── Callbacks ─────────────────────────────────────────────────────
        private sealed class RunCallbacks : ICallbacks
        {
            private const int MaxFailures = 20;

            private readonly string _platform;
            private readonly string _nameFilter;
            private readonly object _statsLock = new object();

            private int _total;
            private int _passed;
            private int _failed;
            private int _skipped;
            private readonly List<JObject> _failures = new List<JObject>();
            private long _startMs;
            private long _endMs;
            private volatile bool _done;
            private string _startError;

            public string RunId { get; }
            public TestRunnerApi Api { get; set; }
            public bool IsDone => _done;
            public string StartError { get { lock (_statsLock) { return _startError; } } }

            public RunCallbacks(string runId, string platform, string nameFilter)
            {
                RunId       = runId;
                _platform   = platform;
                _nameFilter = nameFilter;
                _startMs    = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            }

            public void MarkStartError(string msg)
            {
                lock (_statsLock)
                {
                    _startError = msg;
                    _endMs      = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                }
                _done = true; // volatile last-write — unblock pollers; getTestResults surfaces the error
            }

            public void RunStarted(ITestAdaptor testsToRun)
            {
                _startMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            }

            public void RunFinished(ITestResultAdaptor result)
            {
                lock (_statsLock) { _endMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); }

                // Clean up the TestRunnerApi on the main thread before signalling done.
                try
                {
                    if (Api != null)
                    {
                        Api.UnregisterCallbacks(this);
                        UnityEngine.Object.DestroyImmediate(Api);
                        Api = null;
                    }
                }
                catch { /* ignore teardown errors */ }

                _done = true; // last write — pollers reading _done see fully-populated stats
            }

            public void TestStarted(ITestAdaptor test) { }

            public void TestFinished(ITestResultAdaptor result)
            {
                // Skip suite nodes — we count individual leaf tests only.
                if (result.Test.IsSuite) return;

                // Client-side substring filter on full test name.
                if (!string.IsNullOrEmpty(_nameFilter))
                {
                    var fullName = result.Test.FullName ?? string.Empty;
                    if (fullName.IndexOf(_nameFilter, StringComparison.OrdinalIgnoreCase) < 0)
                        return;
                }

                lock (_statsLock)
                {
                    _total++;
                    switch (result.TestStatus)
                    {
                        case TestStatus.Passed:
                            _passed++;
                            break;
                        case TestStatus.Failed:
                            _failed++;
                            if (_failures.Count < MaxFailures)
                            {
                                _failures.Add(new JObject
                                {
                                    ["name"]    = result.Test.FullName,
                                    ["message"] = result.Message ?? string.Empty,
                                });
                            }
                            break;
                        default:
                            _skipped++;
                            break;
                    }
                }
            }

            public JObject BuildResult()
            {
                lock (_statsLock)
                {
                    long durationMs = (_endMs > 0 ? _endMs : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) - _startMs;
                    return new JObject
                    {
                        ["status"]     = "done",
                        ["runId"]      = RunId,
                        ["platform"]   = _platform,
                        ["total"]      = _total,
                        ["passed"]     = _passed,
                        ["failed"]     = _failed,
                        ["skipped"]    = _skipped,
                        ["durationMs"] = durationMs,
                        ["failures"]   = new JArray(_failures),
                    };
                }
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────
        private static JObject Ok(JObject data)  => new JObject { ["ok"] = true,  ["data"]  = data };
        private static JObject Error(string msg) => new JObject { ["ok"] = false, ["error"] = msg };
    }
}
