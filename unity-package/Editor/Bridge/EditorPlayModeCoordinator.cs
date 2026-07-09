using System;
using System.Threading;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace Gamebrew.Bridge
{
    /// <summary>
    /// Read and change Editor Play Mode from bridge commands (main thread only).
    /// </summary>
    public static class EditorPlayModeCoordinator
    {
        private const int DefaultTimeoutMs = 30_000;

        // --- Settled / changing helpers (main-thread reads only) -------------
        //
        // EditorApplication.isPlayingOrWillChangePlaymode is TRUE for the entire
        // play session ("is playing OR about to play"), so it is NOT a valid
        // "transition finished" signal. The real editor-busy flags are isCompiling
        // and isUpdating. These helpers touch EditorApplication and must run on the
        // Unity main thread (call directly on main-thread paths, or wrap in
        // MainThreadDispatcher.Run from the HTTP caller thread).

        /// <summary>True once the editor has fully settled into <paramref name="wantPlaying"/>.</summary>
        private static bool IsSettled(bool wantPlaying)
            => EditorApplication.isPlaying == wantPlaying
               && !EditorApplication.isCompiling
               && !EditorApplication.isUpdating;

        /// <summary>
        /// Honest "transitioning" signal for the reported isChanging field: busy
        /// compiling/updating, or a play-mode change has been requested but is not
        /// yet fully playing. Reports false once fully playing and settled.
        /// </summary>
        private static bool IsChanging()
            => EditorApplication.isCompiling
               || EditorApplication.isUpdating
               || (EditorApplication.isPlayingOrWillChangePlaymode && !EditorApplication.isPlaying);

        public static JObject GetState()
        {
            return Ok(new JObject
            {
                ["isPlaying"] = EditorApplication.isPlaying,
                ["isPaused"] = EditorApplication.isPaused,
                ["isChanging"] = IsChanging(),
            });
        }

        /// <param name="playing">Desired Play Mode state.</param>
        /// <param name="wait">When true, block until the transition completes or times out.</param>
        /// <param name="timeoutMs">Max wait when <paramref name="wait"/> is true.</param>
        public static JObject SetPlaying(bool playing, bool wait = true, int timeoutMs = DefaultTimeoutMs)
        {
            bool wasPlaying = EditorApplication.isPlaying;

            if (wasPlaying == playing && IsSettled(playing))
            {
                return Ok(new JObject
                {
                    ["isPlaying"] = wasPlaying,
                    ["isPaused"] = EditorApplication.isPaused,
                    ["wasPlaying"] = wasPlaying,
                    ["changed"] = false,
                });
            }

            if (!wait)
            {
                EditorApplication.isPlaying = playing;
                return Ok(new JObject
                {
                    ["isPlaying"] = EditorApplication.isPlaying,
                    ["isPaused"] = EditorApplication.isPaused,
                    ["wasPlaying"] = wasPlaying,
                    ["changed"] = true,
                    ["isChanging"] = IsChanging(),
                });
            }

            if (EditorApplication.isPlaying != playing)
                EditorApplication.isPlaying = playing;

            if (!PollForPlayMode(playing, timeoutMs, out string waitError))
                return Error(waitError);

            return Ok(new JObject
            {
                ["isPlaying"] = EditorApplication.isPlaying,
                ["isPaused"] = EditorApplication.isPaused,
                ["wasPlaying"] = wasPlaying,
                ["changed"] = true,
            });
        }

        /// <summary>HTTP-safe entry: marshals to main thread and polls from the caller thread.</summary>
        public static JObject ExecuteSetPlaying(JObject args)
        {
            if (args?["playing"] == null)
                return Error("playing is required (boolean)");

            bool playing = args["playing"].Value<bool>();
            bool wait = args?["wait"]?.Value<bool>() ?? true;
            int timeoutMs = args?["timeoutMs"]?.Value<int>() ?? DefaultTimeoutMs;
            if (timeoutMs < 1)
                return Error("timeoutMs must be positive");

            bool wasPlaying = MainThreadDispatcher.Run(() => EditorApplication.isPlaying);
            bool settled = MainThreadDispatcher.Run(() => IsSettled(playing));

            if (wasPlaying == playing && settled)
            {
                return Ok(new JObject
                {
                    ["isPlaying"] = wasPlaying,
                    ["isPaused"] = MainThreadDispatcher.Run(() => EditorApplication.isPaused),
                    ["wasPlaying"] = wasPlaying,
                    ["changed"] = false,
                });
            }

            if (!wait)
            {
                MainThreadDispatcher.Run(() => EditorApplication.isPlaying = playing);
                return Ok(new JObject
                {
                    ["isPlaying"] = MainThreadDispatcher.Run(() => EditorApplication.isPlaying),
                    ["isPaused"] = MainThreadDispatcher.Run(() => EditorApplication.isPaused),
                    ["wasPlaying"] = wasPlaying,
                    ["changed"] = true,
                    ["isChanging"] = MainThreadDispatcher.Run(() => IsChanging()),
                });
            }

            MainThreadDispatcher.Run(() =>
            {
                if (EditorApplication.isPlaying != playing)
                    EditorApplication.isPlaying = playing;
            });

            if (!PollForPlayMode(playing, timeoutMs, out string waitError))
                return Error(waitError);

            return Ok(new JObject
            {
                ["isPlaying"] = MainThreadDispatcher.Run(() => EditorApplication.isPlaying),
                ["isPaused"] = MainThreadDispatcher.Run(() => EditorApplication.isPaused),
                ["wasPlaying"] = wasPlaying,
                ["changed"] = true,
            });
        }

        private static bool PollForPlayMode(bool wantPlaying, int timeoutMs, out string error)
        {
            error = null;
            int deadline = Environment.TickCount + timeoutMs;

            while (Environment.TickCount < deadline)
            {
                if (MainThreadDispatcher.Run(() => IsSettled(wantPlaying)))
                    return true;
                Thread.Sleep(50);
            }

            bool finalPlaying = MainThreadDispatcher.Run(() => EditorApplication.isPlaying);
            bool finalChanging = MainThreadDispatcher.Run(() => IsChanging());
            error = $"Play Mode did not reach isPlaying={wantPlaying} within {timeoutMs / 1000}s " +
                    $"(current isPlaying={finalPlaying}, isChanging={finalChanging})";
            return false;
        }

        /// <summary>
        /// Blocks until Play Mode matches <paramref name="wantPlaying"/> or timeout.
        /// Must run on the Unity main thread (EditMode tests only).
        /// </summary>
        public static bool WaitForPlayMode(bool wantPlaying, int timeoutMs, out string error)
        {
            error = null;

            if (IsSettled(wantPlaying))
                return true;

            var gate = new ManualResetEventSlim(false);
            var timedOut = false;
            Timer timer = null;

            void OnPlayModeStateChanged(PlayModeStateChange change)
            {
                // Complete on the concrete "entered" transition matching the request,
                // or if the editor is already settled into the desired state.
                bool entered = wantPlaying
                    ? change == PlayModeStateChange.EnteredPlayMode
                    : change == PlayModeStateChange.EnteredEditMode;
                if (entered || IsSettled(wantPlaying))
                    gate.Set();
            }

            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            try
            {
                if (EditorApplication.isPlaying != wantPlaying)
                    EditorApplication.isPlaying = wantPlaying;

                if (IsSettled(wantPlaying))
                    return true;

                timer = new Timer(_ =>
                {
                    timedOut = true;
                    gate.Set();
                }, null, timeoutMs, Timeout.Infinite);

                gate.Wait();
            }
            finally
            {
                timer?.Dispose();
                EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            }

            if (timedOut)
            {
                error = $"Play Mode did not reach isPlaying={wantPlaying} within {timeoutMs / 1000}s " +
                        $"(current isPlaying={EditorApplication.isPlaying}, isChanging={IsChanging()})";
                return false;
            }

            return EditorApplication.isPlaying == wantPlaying;
        }

        private static JObject Ok(JObject data) => new JObject { ["ok"] = true, ["data"] = data };
        private static JObject Error(string message) => new JObject { ["ok"] = false, ["error"] = message };
    }
}
