using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Gamebrew.Bridge
{
    /// <summary>
    /// EDITOR-ONLY. Drives the player camera by DIRECT INVOCATION of the shipped look integrators
    /// (<see cref="BridgeCameraRig.SetLook"/> / <see cref="BridgeCameraRig.AimAt"/>) — no
    /// synthetic mouse input, no Game-view focus, no MonoBehaviour Update tick required.
    ///
    /// WHY: <c>EditorApplication.QueuePlayerLoopUpdate()</c> does NOT tick MonoBehaviour.Update in
    /// an unfocused editor (same constraint that led to PlayMoveCoordinator). So "send mouse delta
    /// → Update integrates it → camera turns" is physically impossible via the editor pump. Instead
    /// we call the real orientation setters directly on the main thread, exactly mirroring
    /// PlayMoveCoordinator's pattern for DriveLocomotion.
    ///
    /// Command routes (add to CommandRouter.cs switch — do NOT edit CommandRouter directly here):
    ///
    ///   "play.aimAt"      {x, y, z}            → <see cref="AimAt"/>
    ///   "play.aimAtObject" {target}             → <see cref="AimAtObject"/>
    ///   "play.setLook"    {yaw, pitch}          → <see cref="SetLook"/>
    ///
    /// Each handler is a public static JObject method that matches the CommandRouter delegate
    /// signature: <c>public static JObject Xxx(JObject args)</c>.
    /// </summary>
    public static class PlayLookCoordinator
    {
        /// <summary>
        /// play.aimAt {x, y, z} — aim the camera directly at a world-space point.
        /// Returns resulting yaw, pitch, and camera forward vector.
        /// </summary>
        public static JObject AimAt(JObject args)
        {
            float? x = args?["x"]?.Value<float>();
            float? y = args?["y"]?.Value<float>();
            float? z = args?["z"]?.Value<float>();

            if (x == null || y == null || z == null)
                return Error("x, y, z are required");

            var target = new Vector3(x.Value, y.Value, z.Value);

            string error = null;
            JObject result = null;

            MainThreadDispatcher.Run(() =>
            {
                if (!EditorApplication.isPlaying)
                {
                    error = "Editor must be in Play Mode for play.aimAt (use editor.setPlayMode first)";
                    return;
                }

                var cam = FindCamera(out string findError);
                if (cam == null)
                {
                    error = findError;
                    return;
                }

                cam.AimAt(target);

                // Pump one frame so the render reflects the new orientation before we read back.
                EditorApplication.QueuePlayerLoopUpdate();

                result = BuildLookResult(cam);
            });

            return error != null ? Error(error) : Ok(result);
        }

        /// <summary>
        /// play.aimAtObject {target} — locate a named GameObject and aim the camera at its
        /// world-space position. Uses GameObjectResolver so "path/like/this" and plain name
        /// lookups both work.
        /// Returns resulting yaw, pitch, and camera forward vector plus the resolved object path.
        /// </summary>
        public static JObject AimAtObject(JObject args)
        {
            var path = args?["target"]?.Value<string>();
            if (string.IsNullOrWhiteSpace(path))
                return Error("target is required");

            string error = null;
            JObject result = null;

            MainThreadDispatcher.Run(() =>
            {
                if (!EditorApplication.isPlaying)
                {
                    error = "Editor must be in Play Mode for play.aimAtObject";
                    return;
                }

                var go = GameObjectResolver.Find(path);
                if (go == null)
                {
                    error = $"not found: {path}";
                    return;
                }

                var cam = FindCamera(out string findError);
                if (cam == null)
                {
                    error = findError;
                    return;
                }

                Vector3 targetPos = go.transform.position;
                cam.AimAt(targetPos);

                // Pump one frame so the render reflects the new orientation.
                EditorApplication.QueuePlayerLoopUpdate();

                result = BuildLookResult(cam);
                result["target"] = path;
                result["targetPos"] = new JObject
                {
                    ["x"] = targetPos.x,
                    ["y"] = targetPos.y,
                    ["z"] = targetPos.z,
                };
            });

            return error != null ? Error(error) : Ok(result);
        }

        /// <summary>
        /// play.setLook {yaw, pitch} — snap the camera to an absolute yaw/pitch (degrees).
        /// Pitch is clamped by the camera rig to its configured [minPitch, maxPitch].
        /// Returns resulting yaw, pitch, and camera forward vector.
        /// </summary>
        public static JObject SetLook(JObject args)
        {
            if (args?["yaw"] == null || args["pitch"] == null)
                return Error("yaw and pitch are required");

            float yaw = args["yaw"].Value<float>();
            float pitch = args["pitch"].Value<float>();

            string error = null;
            JObject result = null;

            MainThreadDispatcher.Run(() =>
            {
                if (!EditorApplication.isPlaying)
                {
                    error = "Editor must be in Play Mode for play.setLook";
                    return;
                }

                var cam = FindCamera(out string findError);
                if (cam == null)
                {
                    error = findError;
                    return;
                }

                cam.SetLook(yaw, pitch);

                EditorApplication.QueuePlayerLoopUpdate();

                result = BuildLookResult(cam);
            });

            return error != null ? Error(error) : Ok(result);
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        /// <summary>
        /// Locate the active BridgeCameraRig. Returns null and sets <paramref name="error"/>
        /// on failure, mirroring PlayMoveCoordinator's BridgeLocomotor lookup pattern.
        /// </summary>
        private static BridgeCameraRig FindCamera(out string error)
        {
            var cam = UnityEngine.Object.FindAnyObjectByType<BridgeCameraRig>();
            if (cam == null)
            {
                error = "No BridgeCameraRig in the scene (add a BridgeCameraRig subclass to your camera rig)";
                return null;
            }

            error = null;
            return cam;
        }

        /// <summary>
        /// Read back yaw / pitch / forward from the camera after a SetLook/AimAt call so the
        /// bridge caller can verify the result without a separate perceive call.
        /// </summary>
        private static JObject BuildLookResult(BridgeCameraRig cam)
        {
            float yawOut = cam.transform.eulerAngles.y;

            // cameraPivot pitch — reported as signed degrees in [-180, 180].
            float pitchOut = 0f;
            Vector3 forward = cam.transform.forward;
            if (cam.CameraTransform != null)
            {
                pitchOut = cam.CameraTransform.localEulerAngles.x;
                // Normalise from [0,360) to (-180,180].
                if (pitchOut > 180f)
                    pitchOut -= 360f;
                forward = cam.CameraTransform.forward;
            }

            return new JObject
            {
                ["runId"] = BridgeRunLog.RunId,
                ["yaw"] = yawOut,
                ["pitch"] = pitchOut,
                ["forward"] = new JObject
                {
                    ["x"] = forward.x,
                    ["y"] = forward.y,
                    ["z"] = forward.z,
                },
            };
        }

        private static JObject Ok(JObject data) => new JObject { ["ok"] = true, ["data"] = data };
        private static JObject Error(string msg) => new JObject { ["ok"] = false, ["error"] = msg };
    }
}
