#if UNITY_EDITOR
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Gamebrew.Bridge
{
    /// <summary>
    /// EDITOR-ONLY. The <c>play.orbitView</c> verb: capture any world object from an ARBITRARY
    /// perspective (not the player's eye). Where <see cref="PlayViewCoordinator.ViewObject"/>
    /// navigates the PLAYER to the target and shoots through the player camera (low / flat),
    /// this spawns a TRANSIENT camera at a spherical offset around the target's world center,
    /// aims it at the target, and renders THAT camera through the shared
    /// <see cref="GameViewCapture.CaptureFromCamera"/> body.
    ///
    /// SPHERICAL CONVENTION (see <see cref="GameViewCapture.SphericalOffset"/>):
    ///   yaw   — rotation around world Y (0 = behind the target on −Z)
    ///   pitch — elevation above the horizon (0 = eye-level, +90 = top-down looking DOWN)
    ///   distance — camera→center separation in world units
    ///   offset = Quaternion.Euler(pitch, yaw, 0) * (Vector3.back * distance)
    ///   cameraPos = center + offset ; camera LookAt(center).
    ///
    /// Command route (CommandRouter.cs):
    ///   "play.orbitView" { target | path, pitch, yaw, distance, fov?, path? }
    ///
    /// Arg shape:
    ///   target    string  required (unless path given) — GameObjectResolver name/path
    ///   path      string  optional dual-use: when no target, used to RESOLVE the object by
    ///                      hierarchy path; ALSO the PNG output path. To disambiguate, prefer
    ///                      passing target for the object and path for the PNG. If only path is
    ///                      given it is treated as BOTH the object path and... no — see below.
    ///   pitch     float   required — degrees, 0 = horizontal, +90 = top-down
    ///   yaw       float   required — degrees around world Y
    ///   distance  float   required — world units, must be &gt; 0
    ///   fov       float   optional — vertical FOV (default 50)
    ///   savePath  string  optional — PNG output path (default "Logs/orbit.png")
    ///
    /// NOTE on path overloading: to avoid colliding "object path" with "PNG path", this verb
    /// uses <c>target</c> (name OR hierarchy path) to RESOLVE the object and <c>savePath</c>
    /// for the PNG. For backward-compat with the task's stated shape, if <c>target</c> is
    /// absent we fall back to <c>path</c> as the object selector, and the PNG defaults to
    /// "Logs/orbit.png" unless <c>savePath</c> is supplied.
    ///
    /// Return shape (ok=true):
    ///   data.path       string  — project-relative PNG path written
    ///   data.fullPath   string  — absolute path written
    ///   data.bytes      long    — PNG byte count
    ///   data.cameraPos  object  — {x,y,z} transient-camera world position
    ///   data.lookAt     object  — {x,y,z} target world center the camera aimed at
    ///   data.pitch      float   — echo
    ///   data.yaw        float   — echo
    ///   data.distance   float   — echo
    ///   data.fov        float   — echo (effective fov used)
    /// </summary>
    public static class PlayOrbitCoordinator
    {
        private const float DefaultFov = 50f;
        private const string DefaultSavePath = "Logs/orbit.png";

        public static JObject OrbitView(JObject args)
        {
            // ── arg validation (pre-flight, off the main thread) ──────────────
            // target selects the object; fall back to path if target is absent.
            string selector = args?["target"]?.Value<string>();
            if (string.IsNullOrWhiteSpace(selector))
                selector = args?["path"]?.Value<string>();
            if (string.IsNullOrWhiteSpace(selector))
                return Error("target (or path) is required to resolve the object to orbit");

            if (args?["pitch"] == null) return Error("pitch is required (degrees, 0=horizontal, 90=top-down)");
            if (args?["yaw"] == null) return Error("yaw is required (degrees around world Y)");
            if (args?["distance"] == null) return Error("distance is required (world units, > 0)");

            float pitch = args["pitch"].Value<float>();
            float yaw = args["yaw"].Value<float>();
            float distance = args["distance"].Value<float>();
            if (!(distance > 0f) || float.IsNaN(distance) || float.IsInfinity(distance))
                return Error("distance must be a finite value greater than 0");
            if (float.IsNaN(pitch) || float.IsInfinity(pitch) || float.IsNaN(yaw) || float.IsInfinity(yaw))
                return Error("pitch and yaw must be finite values");

            float fov = args?["fov"]?.Value<float>() ?? DefaultFov;
            if (!(fov > 0f) || fov >= 180f || float.IsNaN(fov) || float.IsInfinity(fov))
                return Error("fov must be a finite value in (0, 180)");

            // PNG output path: explicit savePath, else default. (Object selector lives in
            // `target`/`path`; the PNG never reuses the object path to avoid collisions.)
            string savePath = args?["savePath"]?.Value<string>();
            if (string.IsNullOrWhiteSpace(savePath))
                savePath = DefaultSavePath;

            // Validate PNG path early (mirrors GameViewCapture validation) so the caller gets a
            // clear pre-flight error before we enter the main-thread render block.
            if (savePath.StartsWith("/", System.StringComparison.Ordinal))
                return Error("savePath must be relative to the project root (must not start with '/')");
            if (savePath.Contains("..", System.StringComparison.Ordinal))
                return Error("savePath must not contain '..'");

            // ── play mode guard (cheap pre-flight; mirrors PlayViewCoordinator) ─
            if (!EditorApplication.isPlaying)
                return Error("Editor must be in Play Mode for play.orbitView (use editor.setPlayMode first)");

            // ── main-thread render block (ONE Run; no nesting) ────────────────
            GameViewCapture.Result capture = default;
            string error = null;
            Vector3 center = Vector3.zero;
            Vector3 camPos = Vector3.zero;

            MainThreadDispatcher.Run(() =>
            {
                // Re-check inside the Run: domain reload / exit could have flipped Play Mode
                // between the pre-flight check and the dispatch.
                if (!EditorApplication.isPlaying)
                {
                    error = "Editor left Play Mode before play.orbitView could render";
                    return;
                }

                var go = GameObjectResolver.Find(selector);
                if (go == null)
                {
                    error = $"not found: {selector}";
                    return;
                }

                center = ResolveWorldCenter(go);
                Vector3 offset = GameViewCapture.SphericalOffset(pitch, yaw, distance);
                camPos = center + offset;

                GameObject camGo = null;
                try
                {
                    camGo = new GameObject("__OrbitViewCamera")
                    {
                        // DontSave: never serialized into the scene / never persisted, and
                        // survives nothing — we destroy it in finally regardless.
                        hideFlags = HideFlags.HideAndDontSave,
                    };

                    var cam = camGo.AddComponent<Camera>();
                    cam.fieldOfView = fov;
                    // A near plane that won't clip a close-up prop; far plane generous for wide shots.
                    cam.nearClipPlane = 0.01f;
                    cam.farClipPlane = Mathf.Max(1000f, distance * 4f);

                    camGo.transform.position = camPos;
                    camGo.transform.LookAt(center);

                    // Pump one player-loop update so the freshly-positioned camera and any
                    // HUD/widget Update have settled before the shared body renders. The
                    // shared body ALSO calls DriveHudVisuals(force:true) (harmless for a pure
                    // world-shot) so the path stays uniform with play.viewObject.
                    EditorApplication.QueuePlayerLoopUpdate();

                    capture = GameViewCapture.CaptureFromCamera(cam, savePath);
                }
                finally
                {
                    // ALWAYS destroy the transient camera, even if CaptureFromCamera throws.
                    if (camGo != null)
                        UnityEngine.Object.DestroyImmediate(camGo);
                }
            });

            if (error != null)
                return Error(error);

            if (!capture.Success)
                return Error($"capture failed: {capture.Error}");

            var data = new JObject
            {
                ["path"] = savePath,
                ["fullPath"] = capture.FullPath,
                ["bytes"] = capture.Bytes,
                ["cameraPos"] = new JObject { ["x"] = camPos.x, ["y"] = camPos.y, ["z"] = camPos.z },
                ["lookAt"] = new JObject { ["x"] = center.x, ["y"] = center.y, ["z"] = center.z },
                ["pitch"] = pitch,
                ["yaw"] = yaw,
                ["distance"] = distance,
                ["fov"] = fov,
            };

            BridgeRunLog.Ok(
                "play.orbitView",
                $"target={selector} pitch={pitch} yaw={yaw} distance={distance} fov={fov} " +
                $"path={savePath} bytes={capture.Bytes}");

            return Ok(data);
        }

        /// <summary>
        /// Resolve the world-space center to aim at: the combined bounds center of all enabled
        /// Renderers under <paramref name="go"/> (so we frame the whole visible prop, not its
        /// pivot), falling back to <c>transform.position</c> when there is no renderer.
        /// Main-thread only (touches Unity objects).
        /// </summary>
        private static Vector3 ResolveWorldCenter(GameObject go)
        {
            var renderers = go.GetComponentsInChildren<Renderer>();
            bool any = false;
            Bounds bounds = default;
            foreach (var r in renderers)
            {
                if (r == null || !r.enabled)
                    continue;
                if (!any)
                {
                    bounds = r.bounds;
                    any = true;
                }
                else
                {
                    bounds.Encapsulate(r.bounds);
                }
            }

            return any ? bounds.center : go.transform.position;
        }

        private static JObject Ok(JObject data) => new JObject { ["ok"] = true, ["data"] = data };
        private static JObject Error(string msg) => new JObject { ["ok"] = false, ["error"] = msg };
    }
}
#endif
