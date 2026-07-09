#if UNITY_EDITOR
namespace Gamebrew.Bridge
{
    /// <summary>
    /// EDITOR-ONLY deterministic-time seam for the AI-test bridge. Entirely compiled out of player
    /// builds (#if UNITY_EDITOR), so it can never ship.
    ///
    /// WHY THIS EXISTS: <see cref="UnityEngine.Time.captureDeltaTime"/> is NOT honored on frames
    /// forced by <c>EditorApplication.QueuePlayerLoopUpdate()</c> — those frames use the editor's
    /// real wall-clock delta (~0.002 s when idle), so any gameplay scaled by Time.deltaTime moves a
    /// tiny, non-deterministic amount during an unfocused bridge pump (the "dt-wall"). Managed code
    /// cannot overwrite the engine-owned Time.deltaTime, so runtime hot paths instead read
    /// <see cref="DeltaTime"/>, which substitutes <see cref="SimDt"/> only while a bridge pump is
    /// driving frames (<see cref="Active"/>). Outside a pump it falls through to Time.deltaTime, so
    /// normal Play Mode and editor scrubbing are unaffected.
    ///
    /// GENERIC / GAME-AGNOSTIC: this type depends only on UnityEngine and has NO game references.
    /// It lives in the shared runtime assembly (Gamebrew.Bridge.Runtime) so your gameplay hot paths
    /// can read <see cref="DeltaTime"/> without an assembly cycle. The editor bridge
    /// (PlayModeInputCoordinator) writes <see cref="Active"/>/<see cref="SimDt"/> via a direct
    /// reference (Editor → Runtime is a legal dependency).
    ///
    /// TO USE (optional): in your movement / time-scaled hot paths, read
    /// <c>Gamebrew.Bridge.BridgeSimTime.DeltaTime</c> instead of <c>Time.deltaTime</c>, guarded by
    /// #if UNITY_EDITOR with a Time.deltaTime fallback in player builds. If you do not, bridge pumps
    /// still work — they just use the engine's (near-zero, non-deterministic) editor-pump delta.
    /// </summary>
    public static class BridgeSimTime
    {
        /// <summary>True only while the bridge is pumping player-loop frames.</summary>
        public static volatile bool Active;

        /// <summary>Deterministic delta (seconds) to feed gameplay during a pump.</summary>
        public static float SimDt = 0.016f;

        /// <summary>
        /// Frame delta for gameplay: <see cref="SimDt"/> during a bridge pump, otherwise the
        /// engine's <see cref="UnityEngine.Time.deltaTime"/>. Callers MUST guard the use site with
        /// #if UNITY_EDITOR and fall back to Time.deltaTime in player builds.
        /// </summary>
        public static float DeltaTime => (Active && SimDt > 0f) ? SimDt : UnityEngine.Time.deltaTime;
    }
}
#endif
