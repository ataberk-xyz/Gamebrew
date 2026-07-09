using System;
using System.IO;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Gamebrew.Bridge
{
    /// <summary>
    /// Watches <c>.unity-bridge/control.json</c> so MCP can start/stop the HTTP listener
    /// when it cannot reach the bridge.
    /// </summary>
    [InitializeOnLoad]
    public static class BridgeControlPoller
    {
        private static double _lastPollTime;
        private const double PollIntervalSec = 0.5;

        static BridgeControlPoller()
        {
            EditorApplication.update += OnEditorUpdate;
        }

        public static string ControlFilePath =>
            Path.Combine(Directory.GetCurrentDirectory(), ".unity-bridge", "control.json");

        private static void OnEditorUpdate()
        {
            if (EditorApplication.timeSinceStartup - _lastPollTime < PollIntervalSec)
                return;
            _lastPollTime = EditorApplication.timeSinceStartup;
            PollOnce();
        }

        /// <summary>Process one control-file request (used by tests and <see cref="OnEditorUpdate"/>).</summary>
        public static void PollOnce()
        {
            TryPollFile(ControlFilePath);
        }

        private static bool TryPollFile(string path)
        {
            if (!File.Exists(path))
                return false;

            try
            {
                var json = JObject.Parse(File.ReadAllText(path));
                File.Delete(path);

                var action = json["action"]?.Value<string>();
                if (string.Equals(action, "start", StringComparison.OrdinalIgnoreCase))
                {
                    if (!BridgeServer.IsRunning)
                        BridgeServer.StartServer();
                }
                else if (string.Equals(action, "stop", StringComparison.OrdinalIgnoreCase))
                {
                    if (BridgeServer.IsRunning)
                        BridgeServer.StopServer();
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[unity-bridge] control file poll failed: " + ex.Message);
                try { if (File.Exists(path)) File.Delete(path); } catch { /* ignore */ }
            }

            return true;
        }
    }
}
