using System.IO;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace Gamebrew.Bridge
{
    /// <summary>
    /// EDITOR-ONLY. Phase 3 of the "AI views objects in-game" tool: the composite
    /// <c>play.viewObject</c> verb that combines navigation (Phase 2) with off-screen
    /// capture (editor.captureGameView) into a single round-trip call.
    ///
    /// COMPOSITION (no new primitives — thin wrapper only):
    ///   navigate=true  → <see cref="PlayNavCoordinator.NavTo"/>   (path-follow + re-aim)
    ///   navigate=false → <see cref="PlayLookCoordinator.AimAtObject"/> (aim only, no nav)
    ///   capture        → <see cref="GameViewCapture.Capture"/>      (off-screen render-to-PNG)
    ///
    /// Command route (add to CommandRouter.cs switch — do NOT edit CommandRouter here):
    ///
    ///   "play.viewObject"  { target, path?, durationMs?, arriveRadius?, standoff?, navigate? }
    ///                      → <see cref="ViewObject"/>
    ///
    /// Handler signature matches the CommandRouter delegate:
    ///   public static JObject ViewObject(JObject args)
    ///
    /// Arg shape:
    ///   target      string  required  — GameObjectResolver path or name
    ///   path        string  optional  — project-relative output PNG path
    ///                                   default: "Logs/view-&lt;sanitised-target&gt;.png"
    ///   navigate    bool    optional  default true
    ///                                   false → AimAtObject only (no nav, no NavMesh needed)
    ///   durationMs  int     optional  passed through to NavTo (default 12000)
    ///   arriveRadius float  optional  passed through to NavTo
    ///   standoff    float   optional  passed through to NavTo
    ///
    /// Return shape (ok=true):
    ///   data.target         string   — echo of input target
    ///   data.navigate       bool     — whether navigation was attempted
    ///   data.arrived        bool     — true if NavTo reported arrived; false for aim-only
    ///   data.status         string   — NavMesh path status (navigate=true) or "aimed"
    ///   data.cornerCount    int      — number of NavMesh corners (navigate=true) or 0
    ///   data.planarToTarget float    — planar distance to target after nav/aim
    ///   data.standoff       float    — standoff used (navigate=true) or 0
    ///   data.final          object   — {x,y,z} player position after nav/aim
    ///   data.capturePath    string   — project-relative PNG path written
    ///   data.captureBytes   long     — file size in bytes
    /// </summary>
    public static class PlayViewCoordinator
    {
        /// <summary>
        /// play.viewObject — move the player to (or just aim at) a named object, then
        /// capture the game view to a PNG, returning navigation metadata + capture info.
        /// </summary>
        public static JObject ViewObject(JObject args)
        {
            // ── arg validation ────────────────────────────────────────────────
            var target = args?["target"]?.Value<string>();
            if (string.IsNullOrWhiteSpace(target))
                return Error("target is required");

            // Resolve output path before touching Play Mode so we can reject bad paths early.
            string capturePath = args?["path"]?.Value<string>();
            if (string.IsNullOrWhiteSpace(capturePath))
                capturePath = $"Logs/view-{GameViewCapture.SanitiseForFilename(target)}.png";

            // Validate capture path (mirrors GameViewCapture.Capture validation, but we
            // surface the error before entering Play Mode so the caller gets a clear message).
            if (capturePath.StartsWith("/", System.StringComparison.Ordinal))
                return Error("path must be relative to the project root (must not start with '/')");

            if (capturePath.Contains("..", System.StringComparison.Ordinal))
                return Error("path must not contain '..'");

            bool navigate = args?["navigate"]?.Value<bool>() ?? true;

            // ── play mode guard ───────────────────────────────────────────────
            // Check synchronously (MainThreadDispatcher.Run is not needed here because
            // EditorApplication.isPlaying is safe to read from any thread and we want a
            // fast pre-flight error before delegating to the heavier nav/aim coordinators).
            if (!EditorApplication.isPlaying)
                return Error("Editor must be in Play Mode for play.viewObject (use editor.setPlayMode first)");

            // ── step 1: navigate or aim ───────────────────────────────────────
            JObject navResult;
            if (navigate)
            {
                // Build a NavTo-compatible args object, forwarding optional tuning params.
                var navArgs = new JObject { ["target"] = target };
                if (args?["durationMs"] != null)    navArgs["durationMs"] = args["durationMs"];
                if (args?["arriveRadius"] != null)  navArgs["arriveRadius"] = args["arriveRadius"];
                if (args?["standoff"] != null)      navArgs["standoff"] = args["standoff"];

                navResult = PlayNavCoordinator.NavTo(navArgs);
                if (navResult["ok"]?.Value<bool>() != true)
                    return Error($"navigation failed: {navResult["error"]?.Value<string>()}");
            }
            else
            {
                // Aim-only: use PlayLookCoordinator.AimAtObject so no NavMesh is required.
                var aimArgs = new JObject { ["target"] = target };
                var aimResult = PlayLookCoordinator.AimAtObject(aimArgs);
                if (aimResult["ok"]?.Value<bool>() != true)
                    return Error($"aim failed: {aimResult["error"]?.Value<string>()}");

                // Synthesise a nav-like result structure so the merge below is uniform.
                navResult = new JObject
                {
                    ["ok"] = true,
                    ["data"] = new JObject
                    {
                        ["target"] = target,
                        ["arrived"] = false,
                        ["status"] = "aimed",
                        ["cornerCount"] = 0,
                        ["planarToTarget"] = aimResult["data"]?["targetPos"] != null
                            ? (JToken)0f       // exact position unknown in aim-only mode (no nav readback)
                            : (JToken)0f,
                        ["standoff"] = 0f,
                        ["final"] = aimResult["data"]?["targetPos"] ?? new JObject
                        {
                            ["x"] = 0f, ["y"] = 0f, ["z"] = 0f,
                        },
                    },
                };
            }

            var navData = navResult["data"] as JObject;

            // ── step 2: capture ───────────────────────────────────────────────
            // Capture must run on the main thread. MainThreadDispatcher.Run is synchronous
            // when called from the main thread (which the CommandRouter always uses).
            GameViewCapture.Result capture = default;
            MainThreadDispatcher.Run(() =>
            {
                capture = GameViewCapture.Capture(capturePath);
            });

            if (!capture.Success)
                return Error($"capture failed: {capture.Error}");

            // ── step 3: compose result ────────────────────────────────────────
            var data = new JObject
            {
                ["target"]       = target,
                ["navigate"]     = navigate,
                ["arrived"]      = navData?["arrived"]?.Value<bool>() ?? false,
                ["status"]       = navData?["status"]?.Value<string>() ?? "unknown",
                ["cornerCount"]  = navData?["cornerCount"]?.Value<int>() ?? 0,
                ["planarToTarget"] = navData?["planarToTarget"]?.Value<float>() ?? 0f,
                ["standoff"]     = navData?["standoff"]?.Value<float>() ?? 0f,
                ["final"]        = navData?["final"] ?? new JObject { ["x"] = 0f, ["y"] = 0f, ["z"] = 0f },
                ["capturePath"]  = capturePath,
                ["captureBytes"] = capture.Bytes,
            };

            BridgeRunLog.Ok(
                "play.viewObject",
                $"target={target} navigate={navigate} arrived={data["arrived"]} " +
                $"capturePath={capturePath} captureBytes={capture.Bytes}");

            return Ok(data);
        }

        private static JObject Ok(JObject data) => new JObject { ["ok"] = true, ["data"] = data };
        private static JObject Error(string msg) => new JObject { ["ok"] = false, ["error"] = msg };
    }
}
