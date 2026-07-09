using UnityEngine;

namespace Gamebrew.Bridge
{
    /// <summary>
    /// Neutral run-marker log for the bridge.
    ///
    /// The play/nav/view coordinators emit a one-line "success marker" for each
    /// completed operation and stamp responses with a <see cref="RunId"/> so an
    /// agent can correlate a sequence of commands within one play session. The
    /// original extraction wired these calls to a game-side acceptance-test log;
    /// this generic shim keeps the exact same call surface (<c>Ok</c> / <c>RunId</c> /
    /// <c>Clear</c>) so the core stays decoupled from any specific game.
    ///
    /// Markers go to the Unity console; <see cref="RunId"/> is a monotonic session
    /// counter that bumps on <see cref="Clear"/>. Swap this out for your own
    /// playtest/telemetry log if you have one — the coordinators only depend on
    /// these three members.
    /// </summary>
    internal static class BridgeRunLog
    {
        /// <summary>Monotonic run counter, bumped by <see cref="Clear"/>. Stamped onto responses.</summary>
        public static int RunId { get; private set; }

        /// <summary>Record a success marker for the current run.</summary>
        public static void Ok(string op, string message = null)
        {
            Debug.Log(string.IsNullOrEmpty(message)
                ? $"[bridge] run={RunId} {op}"
                : $"[bridge] run={RunId} {op} {message}");
        }

        /// <summary>Begin a new run (bumps <see cref="RunId"/>).</summary>
        public static void Clear()
        {
            RunId++;
        }
    }
}
