using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Gamebrew.Bridge
{
    // ── Recompile-survive: keep the bridge alive across every domain reload ──
    // Static ctor runs once after each domain reload. AssemblyReloadEvents lets
    // us cleanly stop the listener before the reload tears down the AppDomain
    // (freeing the socket), then re-start it after the new domain is ready.
    // SessionState (key "Gamebrew.Bridge.Running") stores the user's
    // intent so that a manual Stop (menu) stays stopped after recompile while
    // an auto-started bridge restarts transparently.
    [InitializeOnLoad]
    public static class BridgeServer
    {
        public const string DefaultPrefix = "http://127.0.0.1:8787/";
        private const string SessionStateKey = "Gamebrew.Bridge.Running";

        private static HttpListener _listener;
        private static readonly CommandRouter Router = new CommandRouter();

        // ── Static constructor — runs once per domain reload ─────────────────
        static BridgeServer()
        {
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeReload;
            AssemblyReloadEvents.afterAssemblyReload  += OnAfterReload;

            // If a previous domain was running (SessionState survived), re-start.
            if (SessionState.GetBool(SessionStateKey, false))
                StartServer();
        }

        private static void OnBeforeReload()
        {
            // Cleanly dispose the listener so the OS socket is freed before the
            // new domain starts.  Do NOT clear SessionState — we need it to know
            // whether to restart.
            Stop();
        }

        private static void OnAfterReload()
        {
            // afterAssemblyReload fires in the new domain, but [InitializeOnLoad]
            // already ran the static ctor which handles the restart.  Nothing else
            // needed here; the hook is registered so the pattern is symmetric.
        }

        [MenuItem("Tools/Unity Bridge/Start")]
        public static void StartFromMenu() => StartServer();

        [MenuItem("Tools/Unity Bridge/Stop")]
        public static void StopFromMenu() => StopServer();

        public static bool IsRunning => _listener != null && _listener.IsListening;

        public static JObject GetStatus()
        {
            return new JObject
            {
                ["ok"] = true,
                ["data"] = new JObject
                {
                    ["isRunning"] = IsRunning,
                    ["url"] = DefaultPrefix,
                },
            };
        }

        /// <summary>Start the HTTP listener (idempotent). Sets the SessionState flag so the
        /// bridge auto-restarts after every domain reload until the user explicitly stops it.</summary>
        public static void StartServer()
        {
            // Record intent BEFORE attempting to start so a restart after reload
            // will retry even if this call is re-entrant.
            SessionState.SetBool(SessionStateKey, true);

            if (IsRunning)
            {
                Debug.Log("[unity-bridge] already running on " + DefaultPrefix);
                return;
            }

            try
            {
                _listener = new HttpListener();
                _listener.Prefixes.Add(DefaultPrefix);
                _listener.Start();
                Task.Run(ListenLoop);
                Debug.Log("[unity-bridge] listening on " + DefaultPrefix);
            }
            catch (Exception ex)
            {
                Debug.LogError("[unity-bridge] failed to start: " + ex.Message);
                Stop(); // dispose without clearing SessionState flag
            }
        }

        /// <summary>Stop the HTTP listener (idempotent). Clears the SessionState flag so the
        /// bridge stays stopped after the next domain reload.</summary>
        public static void StopServer()
        {
            // Clear intent so reloads respect the user's explicit stop.
            SessionState.SetBool(SessionStateKey, false);
            Stop();
        }

        /// <summary>Schedule stop after the current HTTP response is flushed.</summary>
        public static void StopServerDeferred()
        {
            if (!IsRunning)
                return;
            MainThreadDispatcher.Enqueue(StopServer);
        }

        private static async Task ListenLoop()
        {
            while (_listener != null && _listener.IsListening)
            {
                try
                {
                    var context = await _listener.GetContextAsync().ConfigureAwait(false);
                    _ = Task.Run(() => HandleRequest(context));
                }
                catch (HttpListenerException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[unity-bridge] listener error: " + ex.Message);
                }
            }
        }

        private static void HandleRequest(HttpListenerContext context)
        {
            try
            {
                if (!IsLoopback(context.Request.RemoteEndPoint))
                {
                    WriteJson(context.Response, 403, new JObject { ["ok"] = false, ["error"] = "non-loopback rejected" });
                    return;
                }

                string body;
                using (var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding))
                {
                    body = reader.ReadToEnd();
                }

                var payload = string.IsNullOrWhiteSpace(body) ? new JObject() : JObject.Parse(body);
                var command = payload["command"]?.Value<string>();
                var args = payload["args"] as JObject ?? new JObject();

                // editor.runTests / editor.getTestResults are non-blocking: runTests enqueues the
                // TestRunnerApi run on the main thread and returns immediately; getTestResults reads
                // the static run-state. Both must bypass MainThreadDispatcher.Run — runTests because
                // its callbacks fire on the main thread (a main-thread block would deadlock), and
                // both because the fast path avoids needless main-thread round-trips.
                JObject result;
                if (command == "ping")
                {
                    result = new JObject
                    {
                        ["ok"] = true,
                        ["data"] = new JObject { ["pong"] = true },
                    };
                }
                else if (command == "editor.runTests")
                    result = TestRunCoordinator.Execute(args);
                else if (command == "editor.getTestResults")
                    result = TestRunCoordinator.GetResults(args);
                else if (command == "editor.waitForCompile")
                    result = CompileCoordinator.Execute(args);
                else if (command == "editor.setPlayMode")
                    result = EditorPlayModeCoordinator.ExecuteSetPlaying(args);
                else if (command == "play.sendKey")
                    result = PlayModeInputCoordinator.SendKey(args);
                else
                    result = MainThreadDispatcher.Run(() => Router.Execute(command, args), timeoutMs: 120_000);

                WriteJson(context.Response, 200, result);
            }
            catch (Exception ex)
            {
                WriteJson(context.Response, 500, new JObject { ["ok"] = false, ["error"] = ex.Message });
            }
        }

        private static bool IsLoopback(IPEndPoint endpoint)
        {
            if (endpoint?.Address == null) return false;
            return IPAddress.IsLoopback(endpoint.Address);
        }

        private static void WriteJson(HttpListenerResponse response, int statusCode, JObject json)
        {
            var bytes = Encoding.UTF8.GetBytes(json.ToString());
            response.StatusCode = statusCode;
            response.ContentType = "application/json";
            response.ContentEncoding = Encoding.UTF8;
            response.ContentLength64 = bytes.Length;
            using (var stream = response.OutputStream)
            {
                stream.Write(bytes, 0, bytes.Length);
            }
            response.Close();
        }

        private static void Stop()
        {
            if (_listener == null) return;
            try
            {
                _listener.Stop();
                _listener.Close();
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[unity-bridge] stop: " + ex.Message);
            }
            finally
            {
                _listener = null;
            }
        }
    }
}
