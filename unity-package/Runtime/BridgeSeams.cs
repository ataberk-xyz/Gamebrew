using UnityEngine;

namespace Gamebrew.Bridge
{
    // ─────────────────────────────────────────────────────────────────────────────────────────────
    // GAME-AGNOSTIC EXTENSION SEAMS for the play.* movement / look / nav bridge verbs.
    //
    // The bridge's Editor coordinators (PlayMoveCoordinator, PlayLookCoordinator, PlayNavCoordinator,
    // TestObstacleCoordinator) drive gameplay by DIRECT INVOCATION on the main thread — they never
    // synthesise input for movement, because EditorApplication.QueuePlayerLoopUpdate() does not tick
    // MonoBehaviour.Update in an unfocused editor. To stay game-agnostic, they locate these abstract
    // components via FindAnyObjectByType<T>() (which resolves your concrete subclass) and call the
    // integrator directly.
    //
    // TO WIRE YOUR GAME: subclass the relevant seam on (or beside) your player / camera / navmesh
    // objects and forward each method to your own controller. Example:
    //
    //     public sealed class MyPlayerLocomotor : BridgeLocomotor
    //     {
    //         [SerializeField] MyPlayerController _player;
    //         public override void DriveLocomotion(Vector2 move, float dt) => _player.Integrate(move, dt);
    //     }
    //
    // If no subclass is present in the scene, the corresponding verb returns a clean
    // "No <seam> in the scene" error — it never throws. These are the ONLY game coupling points of
    // the play.move / play.look / play.navTo / test.spawnWall verbs; everything else in those
    // coordinators is engine-generic (Transform math, UnityEngine.AI NavMesh planning, capture).
    // ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Per-frame locomotion integrator seam. The bridge calls <see cref="DriveLocomotion"/> N times at
    /// a fixed dt to move the player headlessly (no Update tick, no synthetic keys). Attach a concrete
    /// subclass to your player and forward to your movement integrator.
    /// Used by play.move / play.moveTo / play.navTo.
    /// </summary>
    public abstract class BridgeLocomotor : MonoBehaviour
    {
        /// <summary>
        /// Advance the player one integration step. <paramref name="move"/> is a normalised-ish
        /// planar input: x = strafe (right positive), y = forward (positive). <paramref name="dt"/>
        /// is the step time in seconds. Apply gravity / grounding / collision synchronously inside
        /// this call (e.g. via CharacterController.Move) so movement resolves without an Update loop.
        /// </summary>
        public abstract void DriveLocomotion(Vector2 move, float dt);
    }

    /// <summary>
    /// First-person camera-rig seam. The bridge aims / snaps the look direction directly (no synthetic
    /// mouse) via these members. Attach a concrete subclass to your camera rig and forward to your
    /// look controller. Used by play.aimAt / play.aimAtObject / play.setLook / play.navTo.
    /// </summary>
    public abstract class BridgeCameraRig : MonoBehaviour
    {
        /// <summary>Snap the rig to look at a world-space point (yaw the body + pitch the camera).</summary>
        public abstract void AimAt(Vector3 worldPoint);

        /// <summary>
        /// Snap to an absolute look orientation in degrees. Implementations should clamp
        /// <paramref name="pitchDegrees"/> to their configured pitch limits.
        /// </summary>
        public abstract void SetLook(float yawDegrees, float pitchDegrees);

        /// <summary>
        /// The pitch pivot / camera transform (the child that carries pitch), used for read-back of
        /// resulting pitch and forward vector. May return null if the rig has no separate pivot, in
        /// which case the bridge falls back to this component's own transform.
        /// </summary>
        public abstract Transform CameraTransform { get; }
    }

    /// <summary>
    /// Runtime NavMesh (re)bake seam. play.navTo plans over a live NavMesh (UnityEngine.AI, a built-in
    /// module — no package needed), but SOMETHING must bake that surface at runtime over your
    /// procedurally-built scene. The AI-test detour verbs (test.spawnWall / test.despawnWall) call
    /// <see cref="Rebake"/> after adding/removing an obstacle. Attach a concrete subclass that owns a
    /// baker (e.g. a com.unity.ai.navigation NavMeshSurface). If none is present, the detour verbs
    /// report bakeFound=false and skip the rebake — they never throw.
    /// </summary>
    public abstract class BridgeNavMeshBaker : MonoBehaviour
    {
        /// <summary>(Re)bake the NavMesh over the current scene geometry. Return true if a NavMesh is present afterward.</summary>
        public abstract bool Rebake();
    }
}
