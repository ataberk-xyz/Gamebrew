using System;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Gamebrew.Bridge
{
    /// <summary>
    /// EDITOR-ONLY. Drives the player by DIRECT INVOCATION of the shipped movement integrator
    /// (<see cref="BridgeLocomotor.DriveLocomotion"/>) — no synthetic keys, no
    /// QueuePlayerLoopUpdate-for-movement, no Game-view focus scope.
    ///
    /// WHY: <c>EditorApplication.QueuePlayerLoopUpdate()</c> does NOT tick MonoBehaviour.Update in an
    /// unfocused editor (ground-truth probe: PlayerProbe.EnteredCount stayed 0 while
    /// isActiveAndEnabled was true). So "send keys → Update integrates them → player moves" is
    /// physically impossible via the editor pump. Instead we call the real per-frame integration
    /// directly on the main thread for N fixed-dt steps, exactly how the rest of the headless harness
    /// drives gameplay via component.callMethod. CharacterController.Move applies collision/grounding
    /// synchronously inside that call, so movement, gravity and collision all resolve without an
    /// Update loop, keys, or focus.
    /// </summary>
    public static class PlayMoveCoordinator
    {
        private const float FixedDt = 0.016f;
        private const int MaxSteps = 4096; // ~65 s at 16 ms; guards against runaway loops.

        /// <summary>play.move {dirX, dirZ, durationMs} — drive DriveLocomotion forward for the run.</summary>
        public static JObject Move(JObject args)
        {
            float dirX = args?["dirX"]?.Value<float>() ?? 0f;
            float dirZ = args?["dirZ"]?.Value<float>() ?? 0f;
            int durationMs = args?["durationMs"]?.Value<int>() ?? 1000;
            bool sprint = args?["sprint"]?.Value<bool>() ?? false;

            if (durationMs < 1)
                return Error("durationMs must be >= 1");

            // dirX = strafe, dirZ = forward — match DriveLocomotion's Vector2(x=strafe, y=forward).
            var dir = new Vector2(dirX, dirZ);

            int steps = Mathf.Clamp(Mathf.RoundToInt(durationMs / 1000f / FixedDt), 1, MaxSteps);

            string error = null;
            JObject result = null;

            MainThreadDispatcher.Run(() =>
            {
                if (!EditorApplication.isPlaying)
                {
                    error = "Editor must be in Play Mode for play.move (use editor.setPlayMode first)";
                    return;
                }

                var player = UnityEngine.Object.FindAnyObjectByType<BridgeLocomotor>();
                if (player == null)
                {
                    error = "No BridgeLocomotor in the scene (add a BridgeLocomotor subclass to your player)";
                    return;
                }

                var tr = player.transform;
                Vector3 start = tr.position;

                // Optional sprint: DriveLocomotion reads _sprintToggled internally. We do not flip it
                // here (it has no public setter); callers wanting sprint speed should set it via the
                // existing component.setProperty path. sprint arg is accepted for forward-compat but
                // only annotates the marker today.

                for (int i = 0; i < steps; i++)
                    player.DriveLocomotion(dir, FixedDt);

                // Pump ONE frame so any physics/collision settle is reflected before we read back.
                // Movement already happened via the direct Move calls above — this is read-back only.
                EditorApplication.QueuePlayerLoopUpdate();

                Vector3 end = tr.position;
                float planar = new Vector2(end.x - start.x, end.z - start.z).magnitude;

                // runId-gated [Playtest] marker so a stale-session report can be rejected by runId.
                BridgeRunLog.Ok(
                    "play.move",
                    $"runId={BridgeRunLog.RunId} steps={steps} dt={FixedDt} sprint={sprint} " +
                    $"start=({start.x:F3},{start.y:F3},{start.z:F3}) " +
                    $"end=({end.x:F3},{end.y:F3},{end.z:F3}) planar={planar:F4}");

                result = new JObject
                {
                    ["runId"] = BridgeRunLog.RunId,
                    ["steps"] = steps,
                    ["dt"] = FixedDt,
                    ["sprint"] = sprint,
                    ["start"] = new JObject { ["x"] = start.x, ["y"] = start.y, ["z"] = start.z },
                    ["end"] = new JObject { ["x"] = end.x, ["y"] = end.y, ["z"] = end.z },
                    ["planar"] = planar,
                };
            });

            return error != null ? Error(error) : Ok(result);
        }

        /// <summary>
        /// OPTIONAL play.moveTo {target} — navigate toward a located object using a game perception helper.
        /// Navigation is a MEANS, not the system-under-test, so this is a thin convenience wrapper:
        /// it derives a planar forward/strafe heading from the camera-relative viewport offset and
        /// drives DriveLocomotion for the requested duration. It does NOT path-find around obstacles.
        /// </summary>
        public static JObject MoveTo(JObject args)
        {
            var path = args?["target"]?.Value<string>();
            if (string.IsNullOrWhiteSpace(path))
                return Error("target is required");

            int durationMs = args?["durationMs"]?.Value<int>() ?? 1000;
            if (durationMs < 1)
                return Error("durationMs must be >= 1");

            int steps = Mathf.Clamp(Mathf.RoundToInt(durationMs / 1000f / FixedDt), 1, MaxSteps);

            string error = null;
            JObject result = null;

            MainThreadDispatcher.Run(() =>
            {
                if (!EditorApplication.isPlaying)
                {
                    error = "Editor must be in Play Mode for play.moveTo";
                    return;
                }

                var go = GameObjectResolver.Find(path);
                if (go == null)
                {
                    error = $"not found: {path}";
                    return;
                }

                var player = UnityEngine.Object.FindAnyObjectByType<BridgeLocomotor>();
                if (player == null)
                {
                    error = "No BridgeLocomotor in the scene (add a BridgeLocomotor subclass to your player)";
                    return;
                }

                // Heading from player to target, projected onto the ground plane, expressed in the
                // player's local frame so dirZ=forward / dirX=strafe match DriveLocomotion's basis.
                var tr = player.transform;
                Vector3 start = tr.position;
                Vector3 toTarget = Vector3.ProjectOnPlane(go.transform.position - start, Vector3.up);
                if (toTarget.sqrMagnitude < 1e-4f)
                {
                    error = "target is coincident with the player (no heading)";
                    return;
                }

                Vector3 localDir = tr.InverseTransformDirection(toTarget.normalized);
                var dir = new Vector2(localDir.x, localDir.z).normalized;

                for (int i = 0; i < steps; i++)
                    player.DriveLocomotion(dir, FixedDt);

                EditorApplication.QueuePlayerLoopUpdate();

                Vector3 end = tr.position;
                float planar = new Vector2(end.x - start.x, end.z - start.z).magnitude;
                float remaining = Vector3.ProjectOnPlane(go.transform.position - end, Vector3.up).magnitude;

                BridgeRunLog.Ok(
                    "play.moveTo",
                    $"runId={BridgeRunLog.RunId} target={path} steps={steps} " +
                    $"planar={planar:F4} remaining={remaining:F4}");

                result = new JObject
                {
                    ["runId"] = BridgeRunLog.RunId,
                    ["target"] = path,
                    ["steps"] = steps,
                    ["planar"] = planar,
                    ["remaining"] = remaining,
                    ["end"] = new JObject { ["x"] = end.x, ["y"] = end.y, ["z"] = end.z },
                };
            });

            return error != null ? Error(error) : Ok(result);
        }

        private static JObject Ok(JObject data) => new JObject { ["ok"] = true, ["data"] = data };
        private static JObject Error(string message) => new JObject { ["ok"] = false, ["error"] = message };
    }
}
