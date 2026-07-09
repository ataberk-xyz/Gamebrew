using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.AI;

namespace Gamebrew.Bridge
{
    /// <summary>
    /// EDITOR-ONLY. Phase 2 of the "AI views objects in-game" tool: NavMesh path-following that
    /// drives the player around walls/obstacles, headless, with NO MonoBehaviour Update tick.
    ///
    /// COMPOSITION (no new movement primitive): this reuses the two shipped, directly-callable
    /// seams proven in Phase 1 —
    ///   * <see cref="BridgeLocomotor.DriveLocomotion"/> (the per-frame movement integrator), and
    ///   * <see cref="BridgeCameraRig.AimAt"/> (snap body yaw + camera pitch toward a point).
    /// Per path segment we aim at the next corner (which yaws the body, since DriveLocomotion
    /// projects motion onto the camera's flattened forward) then step DriveLocomotion(forward) at a
    /// fixed dt until we arrive, exactly mirroring PlayMoveCoordinator / PlayLookCoordinator.
    ///
    /// WHY this can't use NavMeshAgent: a NavMeshAgent moves itself inside its own internal Update,
    /// which does not tick in an unfocused editor (the same focus/dt wall that motivated the whole
    /// direct-invocation harness). So we only use NavMesh for the *plan* (NavMesh.CalculatePath →
    /// corners) and drive the *follow* ourselves through the shipped CharacterController integrator.
    /// NavMesh.CalculatePath / NavMeshHit are engine (UnityEngine.AIModule) types, so this editor
    /// assembly needs no extra package reference; the runtime bake (BridgeNavMeshBaker) owns that.
    ///
    /// Command routes (CommandRouter.cs switch):
    ///
    ///   "play.navTo"        {target, durationMs?, arriveRadius?, standoff?, paced?, pacedStepMs?, runId?}  → <see cref="NavTo"/>
    ///   "play.navTo.status" {}  → <see cref="NavStatus"/>   (poll the active paced walk)
    ///
    /// paced==false (default) walks the whole path synchronously in one call and returns the final
    /// result. paced==true is FIRE-AND-POLL: NavTo returns {started:true} immediately and the caller
    /// polls play.navTo.status (~200ms) until data.done==true — because the bridge runs every command
    /// on the MAIN thread, a paced walk must NOT block (a main-thread block freezes the very
    /// EditorApplication.update loop that drives the walk → deadlock).
    ///
    /// Handler signatures match the CommandRouter delegate: public static JObject NavTo(JObject) /
    /// NavStatus(JObject).
    /// </summary>
    public static class PlayNavCoordinator
    {
        private const float FixedDt = 0.016f;
        private const int MaxSteps = 8192;            // ~131 s budget; guards against runaway loops.

        // Default arrival radius for intermediate corners — half the player capsule diameter plus a
        // margin, so we register "reached this corner" before clipping it.
        private const float DefaultArriveRadius = 0.6f;

        // Default viewing standoff: stop this far (planar) from the FINAL target so we look AT it
        // rather than walking into it. ~drag-reach range keeps the object framed and interactable.
        // TODO(decouple): the original sourced this from the game's interaction range
        // (a drag/reach distance of 3.25f, scaled by 0.9f → 2.925f). Hardcoded here to
        // keep the core game-agnostic; repoint at your own reach/interaction range if
        // you have one.
        private const float DefaultStandoff = 3.25f * 0.9f;

        // How far off the NavMesh a point may be and still snap onto it (player/target projection).
        private const float SampleMaxDistance = 4f;

        /// <summary>
        /// play.navTo {target, durationMs?, arriveRadius?, standoff?, paced?, pacedStepMs?} — plan a
        /// NavMesh path from the player to a located object and walk it corner-by-corner, stopping at
        /// a viewing standoff.
        ///
        /// When paced==false (default) the entire walk runs in a single MainThreadDispatcher.Run —
        /// fast, headless, CI-safe — and returns {path corners, final pos, planar travelled, planar
        /// dist to target, arrived} in that one call.
        ///
        /// When paced==true the walk renders as an organic REAL-TIME walk and is FIRE-AND-POLL: this
        /// call plans, registers an EditorApplication.update callback, and returns {started:true}
        /// IMMEDIATELY (it must not block — the bridge already runs us on the main thread, so blocking
        /// would freeze the update loop that drives the walk → deadlock). The update callback then
        /// advances ONE DriveLocomotion substep per ACTUALLY-PRESENTED editor frame (consuming real
        /// elapsed wall-clock time in FixedDt substeps) and forces an explicit Game-view repaint each
        /// frame, so visible motion == integrated motion. The caller polls play.navTo.status (see
        /// <see cref="NavStatus"/>) until done, then reads arrived / planarToTarget. The paced path
        /// focuses the Game view at start so the editor presents at full rate; pacedStepMs is retained
        /// for back-compat but no longer governs speed (the real-time accumulator does). The
        /// player's own move speed is unchanged.
        /// </summary>
        public static JObject NavTo(JObject args)
        {
            var path = args?["target"]?.Value<string>();
            if (string.IsNullOrWhiteSpace(path))
                return Error("target is required");

            int durationMs = args?["durationMs"]?.Value<int>() ?? 12000;
            if (durationMs < 1)
                return Error("durationMs must be >= 1");

            float arriveRadius = args?["arriveRadius"]?.Value<float>() ?? DefaultArriveRadius;
            float standoff     = args?["standoff"]?.Value<float>()     ?? DefaultStandoff;
            if (arriveRadius <= 0f)
                return Error("arriveRadius must be > 0");
            if (standoff < 0f)
                return Error("standoff must be >= 0");

            bool paced       = args?["paced"]?.Value<bool>() ?? false;
            int pacedStepMs  = args?["pacedStepMs"]?.Value<int>() ?? Mathf.RoundToInt(FixedDt * 1000f);
            if (pacedStepMs < 0)
                return Error("pacedStepMs must be >= 0");

            int stepBudget = Mathf.Clamp(Mathf.RoundToInt(durationMs / 1000f / FixedDt), 1, MaxSteps);

            // ── FAST PATH (paced == false) ─────────────────────────────────────────────────────────
            // Entire walk runs in a single MainThreadDispatcher.Run so the caller blocks until done.
            // Behaviour is identical to the original implementation — do NOT change this branch.
            if (!paced)
                return NavToHeadless(path, arriveRadius, standoff, stepBudget);

            // ── PACED PATH (paced == true) ─────────────────────────────────────────────────────────
            // Plan, register an EditorApplication.update tick, and return {started:true} immediately.
            // The tick drives the walk one substep per presented frame; the caller polls
            // play.navTo.status until done. Fire-and-poll, NOT blocking (would deadlock the main thread).
            // pacedStepMs is parsed/validated above for back-compat but no longer governs speed (the
            // real-time accumulator does), so it is not forwarded into the walk.
            return NavToPaced(path, arriveRadius, standoff, stepBudget);
        }

        // ── HEADLESS (original single-Run) implementation ──────────────────────────────────────────

        private static JObject NavToHeadless(string path, float arriveRadius, float standoff, int stepBudget)
        {
            string error = null;
            JObject result = null;

            MainThreadDispatcher.Run(() =>
            {
                if (!EditorApplication.isPlaying)
                {
                    error = "Editor must be in Play Mode for play.navTo (use editor.setPlayMode first)";
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

                var cam = UnityEngine.Object.FindAnyObjectByType<BridgeCameraRig>();
                if (cam == null)
                {
                    error = "No BridgeCameraRig in the scene (add a BridgeCameraRig subclass to your camera rig)";
                    return;
                }

                var tr = player.transform;
                Vector3 start     = tr.position;
                Vector3 targetPos = go.transform.position;

                // ── PLAN ──────────────────────────────────────────────────────
                if (!NavMesh.SamplePosition(start, out var startHit, SampleMaxDistance, NavMesh.AllAreas))
                {
                    error = "player is not on a NavMesh (is a BridgeNavMeshBaker present and baked?)";
                    return;
                }

                if (!NavMesh.SamplePosition(targetPos, out var targetHit, SampleMaxDistance, NavMesh.AllAreas))
                {
                    error = $"target '{path}' is not near any NavMesh (unreachable or no bake)";
                    return;
                }

                var navPath = new NavMeshPath();
                bool found = NavMesh.CalculatePath(startHit.position, targetHit.position, NavMesh.AllAreas, navPath);
                if (!found || navPath.status == NavMeshPathStatus.PathInvalid)
                {
                    error = $"no NavMesh path to '{path}' (status={navPath.status})";
                    return;
                }

                var corners = new List<Vector3>(navPath.corners);
                bool partial = navPath.status == NavMeshPathStatus.PathPartial;

                // ── FOLLOW ────────────────────────────────────────────────────
                int stepsUsed    = 0;
                int cornersReached = 0;

                for (int c = 1; c < corners.Count; c++)
                {
                    bool isFinal   = c == corners.Count - 1;
                    Vector3 corner = corners[c];
                    float stopRadius = isFinal ? Mathf.Max(arriveRadius, standoff) : arriveRadius;

                    while (stepsUsed < stepBudget)
                    {
                        Vector3 pos         = tr.position;
                        Vector3 toCorner    = Vector3.ProjectOnPlane(corner - pos, Vector3.up);
                        float planarToCorner = toCorner.magnitude;

                        float gate = isFinal
                            ? Vector3.ProjectOnPlane(targetPos - pos, Vector3.up).magnitude
                            : planarToCorner;

                        if (gate <= stopRadius)
                            break;

                        cam.AimAt(corner);
                        player.DriveLocomotion(new Vector2(0f, 1f), FixedDt);
                        stepsUsed++;
                    }

                    if (Vector3.ProjectOnPlane(corner - tr.position, Vector3.up).magnitude <= stopRadius
                        || (isFinal && Vector3.ProjectOnPlane(targetPos - tr.position, Vector3.up).magnitude <= stopRadius))
                        cornersReached++;

                    if (stepsUsed >= stepBudget)
                        break;
                }

                // Settle one frame so collision/grounding from the final Move is reflected on read-back.
                EditorApplication.QueuePlayerLoopUpdate();

                // Leave the camera framing the actual target (not the last corner) for Phase 3 view.
                cam.AimAt(targetPos);
                EditorApplication.QueuePlayerLoopUpdate();

                Vector3 end = tr.position;
                float planarTravelled = new Vector2(end.x - start.x, end.z - start.z).magnitude;
                float planarToTarget  = Vector3.ProjectOnPlane(targetPos - end, Vector3.up).magnitude;

                bool arrived = !partial
                    && stepsUsed < stepBudget
                    && planarToTarget <= (standoff + arriveRadius);

                var cornerArray = new JArray();
                foreach (var corner in corners)
                    cornerArray.Add(new JObject { ["x"] = corner.x, ["y"] = corner.y, ["z"] = corner.z });

                BridgeRunLog.Ok(
                    "play.navTo",
                    $"runId={BridgeRunLog.RunId} target={path} status={navPath.status} " +
                    $"corners={corners.Count} cornersReached={cornersReached} steps={stepsUsed} " +
                    $"travelled={planarTravelled:F3} toTarget={planarToTarget:F3} arrived={arrived}");

                result = new JObject
                {
                    ["runId"]          = BridgeRunLog.RunId,
                    ["target"]         = path,
                    ["status"]         = navPath.status.ToString(),
                    ["partial"]        = partial,
                    ["corners"]        = cornerArray,
                    ["cornerCount"]    = corners.Count,
                    ["cornersReached"] = cornersReached,
                    ["steps"]          = stepsUsed,
                    ["stepBudget"]     = stepBudget,
                    ["planarTravelled"] = planarTravelled,
                    ["planarToTarget"] = planarToTarget,
                    ["standoff"]       = standoff,
                    ["arrived"]        = arrived,
                    ["start"]          = new JObject { ["x"] = start.x, ["y"] = start.y, ["z"] = start.z },
                    ["final"]          = new JObject { ["x"] = end.x,   ["y"] = end.y,   ["z"] = end.z   },
                };
            });

            return error != null ? Error(error) : Ok(result);
        }

        // ── PACED (per-step Run) implementation ────────────────────────────────────────────────────

        // Struct to ferry planning results from the initial main-thread Run back to the background.
        // All Vector3 fields are plain value types — safe to copy across the thread boundary.
        private struct PlanResult
        {
            public string Error;
            public List<Vector3> Corners;   // null on error
            public bool Partial;
            public Vector3 Start;
            public Vector3 TargetPos;
            public string NavStatus;        // navPath.status.ToString()
        }

        // ── Real-time paced walker ───────────────────────────────────────────────────────────────
        // Drives ONE DriveLocomotion substep per ACTUALLY-PRESENTED editor frame (via
        // EditorApplication.update), with an explicit per-frame Game-view repaint, so visible motion
        // == integrated motion (organic walk). The old per-step background Run+Sleep model paced
        // INTEGRATION, not PRESENTATION: it advanced logic & dirtied the Scene view but the editor's
        // repaint scheduler coalesced ~60 steps into a handful of presents → the human saw a teleport.
        //
        // ASYNC (fire-and-poll): the bridge runs EVERY command on the MAIN thread (BridgeServer
        // wraps Router.Execute in MainThreadDispatcher.Run), so a paced walk MUST NOT block — a
        // main-thread block freezes EditorApplication.update, the very loop that drives this walker
        // (deadlock). Instead NavToPaced plans, registers Tick, and returns {started:true}
        // immediately; the caller polls play.navTo.status until done. All fields are touched ONLY on
        // the main thread (inside Tick / the registering call / NavStatus), so no cross-thread races.
        private sealed class PacedWalker
        {
            // Plan (immutable after construction).
            public List<Vector3> Corners;
            public Vector3 TargetPos;
            public Vector3 Start;
            public float ArriveRadius;
            public float Standoff;
            public int StepBudget;
            public bool Partial;
            public string NavStatus;
            public string Path;

            // Unity refs (main-thread-only dereference).
            public BridgeLocomotor Player;
            public BridgeCameraRig Cam;
            public Transform Tr;

            // Walk state (main-thread-only).
            public int CornerIndex = 1;     // corners[0] is the start; follow from 1.
            public int StepsUsed;
            public int CornersReached;
            public double LastTime;         // EditorApplication.timeSinceStartup seed.
            public float Accumulator;

            // Result (written on the main thread by TickWalker on finish; read by NavStatus, also on
            // the main thread).
            public bool Done;
            public string Error;            // non-null => the walk ended in an error condition.
            public bool Arrived;
            public Vector3 FinalPos;
            public float PlanarTravelled;
            public float PlanarToTarget;

            // Bound delegate so we can unsubscribe the exact same instance.
            public EditorApplication.CallbackFunction Tick;
        }

        // The single in-flight (or last-finished) paced walker. Replaced only when a NEW paced walk
        // starts; left set after finish so play.navTo.status can read the final result. Touched ONLY
        // on the main thread. A domain reload clears it to null → NavStatus reports idle (acceptable).
        private static PacedWalker _activeWalker;

        // Turn rate for the body-yaw slerp (deg/s). BridgeCameraRig.AimAt SNAPS yaw; here we rotate
        // the body toward the heading at a bounded rate so the walk reads as an organic turn-then-go
        // rather than an instant facing flip. DriveLocomotion projects on the (flattened) camera
        // forward, which is parented under the body, so yawing the body steers the motion.
        private const float TurnRateDegPerSec = 540f;

        private static JObject NavToPaced(string path, float arriveRadius, float standoff, int stepBudget)
        {
            // ── Phase 1: planning on the main thread ──────────────────────────────────────────────
            var plan = MainThreadDispatcher.Run<PlanResult>(() =>
            {
                var pr = new PlanResult();

                if (!EditorApplication.isPlaying)
                {
                    pr.Error = "Editor must be in Play Mode for play.navTo (use editor.setPlayMode first)";
                    return pr;
                }

                var go = GameObjectResolver.Find(path);
                if (go == null)
                {
                    pr.Error = $"not found: {path}";
                    return pr;
                }

                var player = UnityEngine.Object.FindAnyObjectByType<BridgeLocomotor>();
                if (player == null)
                {
                    pr.Error = "No BridgeLocomotor in the scene (add a BridgeLocomotor subclass to your player)";
                    return pr;
                }

                var cam = UnityEngine.Object.FindAnyObjectByType<BridgeCameraRig>();
                if (cam == null)
                {
                    pr.Error = "No BridgeCameraRig in the scene (add a BridgeCameraRig subclass to your camera rig)";
                    return pr;
                }

                var tr        = player.transform;
                pr.Start      = tr.position;
                pr.TargetPos  = go.transform.position;

                if (!NavMesh.SamplePosition(pr.Start, out var startHit, SampleMaxDistance, NavMesh.AllAreas))
                {
                    pr.Error = "player is not on a NavMesh (is a BridgeNavMeshBaker present and baked?)";
                    return pr;
                }

                if (!NavMesh.SamplePosition(pr.TargetPos, out var targetHit, SampleMaxDistance, NavMesh.AllAreas))
                {
                    pr.Error = $"target '{path}' is not near any NavMesh (unreachable or no bake)";
                    return pr;
                }

                var navPath = new NavMeshPath();
                bool found  = NavMesh.CalculatePath(startHit.position, targetHit.position, NavMesh.AllAreas, navPath);
                if (!found || navPath.status == NavMeshPathStatus.PathInvalid)
                {
                    pr.Error = $"no NavMesh path to '{path}' (status={navPath.status})";
                    return pr;
                }

                pr.Corners   = new List<Vector3>(navPath.corners);
                pr.Partial   = navPath.status == NavMeshPathStatus.PathPartial;
                pr.NavStatus = navPath.status.ToString();
                return pr;
            });

            if (plan.Error != null)
                return Error(plan.Error);

            // ── Phase 2: build the walker, register the update tick, and RETURN IMMEDIATELY ───────
            // The bridge runs us ON THE MAIN THREAD, so blocking here would freeze the very
            // EditorApplication.update loop that drives the walker → deadlock. Fire-and-poll instead:
            // hand the walk to EditorApplication.update and return {started:true}; the caller polls
            // play.navTo.status until done.
            //
            // A degenerate path (player already at the target) has only the start corner; the walker
            // sees CornerIndex (1) >= Corners.Count on its first tick, finishes with zero motion, and
            // still emits the normal log marker — no special-casing needed here.
            var walker = new PacedWalker
            {
                Corners      = plan.Corners,
                TargetPos    = plan.TargetPos,
                Start        = plan.Start,
                ArriveRadius = arriveRadius,
                Standoff     = standoff,
                StepBudget   = stepBudget,
                Partial      = plan.Partial,
                NavStatus    = plan.NavStatus,
                Path         = path,
            };

            string error = null;
            MainThreadDispatcher.Run(() =>
            {
                // If a previous walk is still subscribed, finish it first so we never leak two
                // EditorApplication.update subscriptions onto _activeWalker's slot.
                if (_activeWalker != null && _activeWalker.Tick != null)
                {
                    EditorApplication.update -= _activeWalker.Tick;
                    _activeWalker.Tick = null;
                    _activeWalker.Done  = true;
                }

                walker.Player = UnityEngine.Object.FindAnyObjectByType<BridgeLocomotor>();
                walker.Cam    = UnityEngine.Object.FindAnyObjectByType<BridgeCameraRig>();
                walker.Tr     = walker.Player != null ? walker.Player.transform : null;

                if (walker.Player == null || walker.Cam == null || walker.Tr == null)
                {
                    error = "Player/camera disappeared between planning and follow phase";
                    return;
                }

                // Focus the Game view so the editor presents at full rate (unfocused windows are
                // throttled/coalesced by the repaint scheduler — the root cause of the "teleport").
                FocusGameView();

                // Seed the real-time clock so the FIRST tick's dt is ~one frame, not the whole gap
                // since editor startup.
                walker.LastTime = EditorApplication.timeSinceStartup;

                walker.Tick = () => TickWalker(walker);
                EditorApplication.update += walker.Tick;
                _activeWalker = walker;   // publish as the active walk (overwrites any prior result).

                // Kick an immediate present so the focused Game view starts drawing without waiting
                // for the scheduler's next idle repaint.
                EditorApplication.QueuePlayerLoopUpdate();
            });

            if (error != null)
                return Error(error);

            var startObj = new JObject { ["x"] = plan.Start.x, ["y"] = plan.Start.y, ["z"] = plan.Start.z };
            return Ok(new JObject
            {
                ["started"]     = true,
                ["runId"]       = BridgeRunLog.RunId,
                ["target"]      = path,
                ["status"]      = plan.NavStatus,
                ["partial"]     = plan.Partial,
                ["cornerCount"] = plan.Corners.Count,
                ["stepBudget"]  = stepBudget,
                ["standoff"]    = standoff,
                ["start"]       = startObj,
            });
        }

        // ── play.navTo.status: poll the active paced walk (fire-and-poll readback) ────────────────
        // Caller polls this (~200ms) after play.navTo {paced:true} returns {started:true}, until
        // data.done==true, then reads arrived / planarToTarget. Returns the SAME field set
        // NavToHeadless returns, plus walking/done. Reads _activeWalker on the main thread (no race).
        public static JObject NavStatus(JObject args)
        {
            return MainThreadDispatcher.Run(() =>
            {
                var w = _activeWalker;
                if (w == null)
                {
                    // No walk has started this domain (or a reload cleared it) → idle.
                    return Ok(new JObject { ["idle"] = true, ["done"] = true, ["walking"] = false });
                }

                var cornerArray = new JArray();
                foreach (var corner in w.Corners)
                    cornerArray.Add(new JObject { ["x"] = corner.x, ["y"] = corner.y, ["z"] = corner.z });

                var data = new JObject
                {
                    ["walking"]         = !w.Done,
                    ["done"]            = w.Done,
                    ["arrived"]         = w.Arrived,
                    ["runId"]           = BridgeRunLog.RunId,
                    ["target"]          = w.Path,
                    ["status"]          = w.NavStatus,
                    ["partial"]         = w.Partial,
                    ["corners"]         = cornerArray,
                    ["cornerCount"]     = w.Corners.Count,
                    ["cornersReached"]  = w.CornersReached,
                    ["steps"]           = w.StepsUsed,
                    ["stepBudget"]      = w.StepBudget,
                    ["planarTravelled"] = w.PlanarTravelled,
                    ["planarToTarget"]  = w.PlanarToTarget,
                    ["standoff"]        = w.Standoff,
                    ["start"]           = new JObject { ["x"] = w.Start.x,    ["y"] = w.Start.y,    ["z"] = w.Start.z },
                    ["final"]           = new JObject { ["x"] = w.FinalPos.x, ["y"] = w.FinalPos.y, ["z"] = w.FinalPos.z },
                };

                if (w.Error != null)
                    data["error"] = w.Error;

                return Ok(data);
            });
        }

        // ── EditorApplication.update tick: one real-time frame of walking ─────────────────────────
        // RUNS ON THE MAIN THREAD (EditorApplication.update is main-thread-only, and the bridge
        // command that started us also ran on the main thread, so there is no nested dispatch here).
        // Consumes real elapsed wall-clock time in FixedDt substeps so the walk plays at the true
        // gameplay speed (~moveSpeed u/s), then presents exactly one Game-view frame. When the walk
        // ends it writes the result fields, emits the log marker, and unsubscribes itself; the result
        // stays readable via play.navTo.status.
        private static void TickWalker(PacedWalker w)
        {
            // Guard: player/cam destroyed mid-walk (Unity fake-null). Fail closed: finish with
            // arrived=false and an error field on the walker (NavStatus surfaces it).
            if (w.Player == null || w.Cam == null || w.Tr == null)
            {
                w.FinalPos = w.Tr != null ? w.Tr.position : w.Start;
                w.Error    = "player/camera was destroyed mid-walk";
                FinishWalker(w);
                return;
            }

            // Real elapsed time since the last presented frame → real game speed.
            double now = EditorApplication.timeSinceStartup;
            float dt   = (float)(now - w.LastTime);
            w.LastTime = now;
            dt = Mathf.Clamp(dt, 0f, 0.1f);   // swallow editor stalls / first-frame spikes.

            w.Accumulator += dt;

            // Integrate in fixed substeps. Advancing corners happens inside the substep loop so a
            // single rich frame can cross several short corners without overshoot.
            for (; w.Accumulator >= FixedDt && w.StepsUsed < w.StepBudget; w.Accumulator -= FixedDt)
            {
                if (w.CornerIndex >= w.Corners.Count)
                    break;   // ran out of corners → arrived; finish below.

                bool isFinal      = w.CornerIndex == w.Corners.Count - 1;
                Vector3 corner    = w.Corners[w.CornerIndex];
                float stopRadius  = isFinal ? Mathf.Max(w.ArriveRadius, w.Standoff) : w.ArriveRadius;

                Vector3 pos  = w.Tr.position;
                float gate   = isFinal
                    ? Vector3.ProjectOnPlane(w.TargetPos - pos, Vector3.up).magnitude
                    : Vector3.ProjectOnPlane(corner - pos, Vector3.up).magnitude;

                if (gate <= stopRadius)
                {
                    // Reached this corner — count it and advance. Do NOT consume a locomotion step.
                    w.CornersReached++;
                    w.CornerIndex++;
                    continue;
                }

                // Bounded turn toward the corner (organic yaw) then one locomotion substep.
                SlerpAimAt(w.Cam, corner, FixedDt);
                w.Player.DriveLocomotion(new Vector2(0f, 1f), FixedDt);
                w.StepsUsed++;
            }

            // Did we finish this frame? (all corners reached, or budget exhausted)
            bool reachedEnd = w.CornerIndex >= w.Corners.Count;
            bool budgetOut  = w.StepsUsed >= w.StepBudget;

            if (reachedEnd || budgetOut)
            {
                // Leave the camera framing the actual target (not the last corner) for Phase 3 view.
                w.Cam.AimAt(w.TargetPos);
                w.FinalPos = w.Tr.position;
                PresentFrame();
                FinishWalker(w);
                return;
            }

            // Otherwise present this frame and wait for the next presented frame.
            PresentFrame();
        }

        // Settle physics/grounding for this substep batch and force the Game view to actually present
        // (the scheduler otherwise coalesces presents, especially when nominally unfocused).
        private static void PresentFrame()
        {
            EditorApplication.QueuePlayerLoopUpdate();
            RepaintGameViews();
            SceneView.RepaintAll();   // also repaint scene watchers.
        }

        // Finalize the walk: unsubscribe from update, compute the value-type result fields, emit the
        // SAME BridgeRunLog.Ok("play.navTo", ...) marker the headless path emits, and mark Done so
        // play.navTo.status reports the final result. RUNS ON THE MAIN THREAD (called from Tick).
        // Leaves _activeWalker pointing at this walker so status can keep reading the result.
        private static void FinishWalker(PacedWalker w)
        {
            if (w.Tick != null)
            {
                EditorApplication.update -= w.Tick;
                w.Tick = null;
            }

            Vector3 start = w.Start;
            Vector3 end   = w.FinalPos;
            w.PlanarTravelled = new Vector2(end.x - start.x, end.z - start.z).magnitude;
            w.PlanarToTarget  = Vector3.ProjectOnPlane(w.TargetPos - end, Vector3.up).magnitude;

            // An error finish (player/cam destroyed) never counts as arrived.
            w.Arrived = w.Error == null
                && !w.Partial
                && w.StepsUsed < w.StepBudget
                && w.PlanarToTarget <= (w.Standoff + w.ArriveRadius);

            BridgeRunLog.Ok(
                "play.navTo",
                $"runId={BridgeRunLog.RunId} target={w.Path} status={w.NavStatus} " +
                $"corners={w.Corners.Count} cornersReached={w.CornersReached} steps={w.StepsUsed} " +
                $"travelled={w.PlanarTravelled:F3} toTarget={w.PlanarToTarget:F3} arrived={w.Arrived}");

            w.Done = true;
        }

        // Bounded body-yaw slerp toward worldPoint (vs BridgeCameraRig.AimAt's hard snap). Computes
        // the same target yaw/pitch AimAt would, but rotates the body yaw at TurnRateDegPerSec; pitch
        // tracks immediately (vertical framing reads fine snapped, and DriveLocomotion ignores pitch).
        private static void SlerpAimAt(BridgeCameraRig cam, Vector3 worldPoint, float dt)
        {
            var pivot  = cam.CameraTransform;
            Vector3 origin = pivot != null ? pivot.position : cam.transform.position;
            Vector3 delta  = worldPoint - origin;

            float targetYaw = Mathf.Atan2(delta.x, delta.z) * Mathf.Rad2Deg;
            float horizontal = new Vector2(delta.x, delta.z).magnitude;
            float targetPitch = -Mathf.Atan2(delta.y, horizontal) * Mathf.Rad2Deg;

            float currentYaw = cam.transform.eulerAngles.y;
            float maxStep    = TurnRateDegPerSec * dt;
            float newYaw     = Mathf.MoveTowardsAngle(currentYaw, targetYaw, maxStep);

            // SetLook snaps yaw+pitch and syncs the smooth-damp accumulators so real-play look resumes
            // cleanly; we feed it the bounded yaw so the turn is incremental frame-to-frame.
            cam.SetLook(newYaw, targetPitch);
        }

        // ── GameView reflection (the type is internal to the UnityEditor assembly) ────────────────
        private static System.Type _gameViewType;

        private static System.Type GameViewType =>
            _gameViewType ??= typeof(EditorWindow).Assembly.GetType("UnityEditor.GameView");

        // Focus the Game view so the editor presents at full rate (no unfocused-window throttling).
        private static void FocusGameView()
        {
            var t = GameViewType;
            if (t != null)
                EditorWindow.FocusWindowIfItsOpen(t);
        }

        // Force every open Game view to present THIS frame. Reflect over all loaded EditorWindows and
        // Repaint the ones whose type is GameView (the scheduler coalesces idle presents otherwise).
        private static void RepaintGameViews()
        {
            var t = GameViewType;
            if (t == null)
                return;

            foreach (var win in Resources.FindObjectsOfTypeAll<EditorWindow>())
            {
                if (win != null && t.IsInstanceOfType(win))
                    win.Repaint();
            }
        }

        private static JObject Ok(JObject data) => new JObject { ["ok"] = true, ["data"] = data };
        private static JObject Error(string message) => new JObject { ["ok"] = false, ["error"] = message };
    }
}
