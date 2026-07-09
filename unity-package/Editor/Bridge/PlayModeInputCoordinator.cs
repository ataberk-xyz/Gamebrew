using System;
using System.Collections.Generic;
using System.Threading;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;

namespace Gamebrew.Bridge
{
    /// <summary>
    /// Editor-side API for <see cref="BridgeInputRelay"/> during Play Mode.
    /// </summary>
    // BridgeInputRelay + BridgeSimTime are GENERIC (not game-specific) and live in the bundled
    // companion RUNTIME assembly `Gamebrew.Bridge.Runtime` (unity-package/Runtime/). The Editor
    // asmdef references that assembly by name, so play.sendKey / keyboard injection compile & run
    // out of the box — no game code required.
    public static class PlayModeInputCoordinator
    {
        private const int DefaultHoldMs = 50;
        private const int DefaultWaitFrames = 1;
        private const int DefaultTimeoutMs = 10_000;
        private const int MsPerFrame = 33;

        /// <summary>
        /// Fixed dt (seconds) injected via <see cref="UnityEngine.Time.captureDeltaTime"/> for
        /// every frame pumped by <see cref="PumpPlayerLoopUntil"/>.
        /// Set to 0 to disable the override and fall back to engine-managed Time.deltaTime
        /// (which collapses to ~0 during editor-pump frames — the dt-wall).
        /// Default 0.016 f (~60 fps) is ON so movement smoke-tests work out of the box.
        /// </summary>
        public static float CaptureDtStep = 0.016f;

        /// <summary>
        /// Holds the active focus override across a pump. Only one pump runs at a time (guarded by
        /// BridgeInputRelay's single-flight tap/hold state), so a single static slot is sufficient.
        /// </summary>
        private static GameViewInputFocusScope _focusScope;

        /// <summary>
        /// EDITOR-ONLY. Temporarily forces the Input System to treat the player as ALWAYS focused
        /// while the bridge pumps the player loop, restoring the prior values on Dispose.
        ///
        /// ROOT CAUSE this defeats (verified against InputManager.cs in this project's PackageCache,
        /// com.unity.inputsystem@21a28c3a6c83):
        ///   1. With the default editorInputBehaviorInPlayMode == PointersAndKeyboardsRespectGameViewFocus,
        ///      an UNFOCUSED editor in Play Mode leaves keyboard/pointer events in the buffer on player
        ///      (Dynamic) updates and defers them to a later Editor update (InputManager.cs:3321-3349).
        ///   2. BUT flipping ONLY editorInputBehaviorInPlayMode to AllDeviceInputAlwaysGoesToGameView is
        ///      NOT enough and actually makes it WORSE: ShouldFlushEventBuffer() (InputManager.cs:3673-3688)
        ///      returns TRUE when (!gameHasFocus && setting==AllDeviceInputAlwaysGoesToGameView &&
        ///      !runInBackground). OnUpdate then takes the shouldExitEarly path (InputManager.cs:3204-3228)
        ///      and calls eventBuffer.Reset() (line 3225) — the queued W press is DISCARDED unprocessed,
        ///      so wKey.isPressed reads FALSE on every pumped frame. This is the observed H-input failure.
        ///
        /// THE FIX: make gameHasFocus itself TRUE for the duration of the pump. gameHasFocus
        /// (InputManager.cs:402-407) is true when gameShouldGetInputRegardlessOfFocus
        /// (InputManager.cs:409-414) is true, which requires BOTH:
        ///   - backgroundBehavior == IgnoreFocus, AND
        ///   - editorInputBehaviorInPlayMode == AllDeviceInputAlwaysGoesToGameView.
        /// With gameHasFocus true: ShouldFlushEventBuffer returns false (line 3677 first conjunct fails),
        /// the focus gate at 3321 is skipped (line 3321 requires !gameHasFocus), and Dynamic-update event
        /// processing is NOT early-exited (ShouldExitEarlyBasedOnBackgroundBehavior, 3723-3741, does not
        /// trip for IgnoreFocus + AllDeviceInputAlwaysGoesToGameView on a non-Editor update). The queued
        /// keyboard/mouse events are therefore applied to device state on the pumped player-loop frame.
        ///
        /// Both InputSettings properties are global runtime state, but they are ONLY ever mutated from
        /// this editor-bridge assembly and always restored in a finally/Dispose, so the shipped player
        /// build is unaffected.
        /// </summary>
        private readonly struct GameViewInputFocusScope : IDisposable
        {
            private readonly InputSettings.EditorInputBehaviorInPlayMode _prevBehavior;
            private readonly InputSettings.BackgroundBehavior _prevBackground;
            private readonly bool _behaviorChanged;
            private readonly bool _backgroundChanged;

            private GameViewInputFocusScope(
                InputSettings.EditorInputBehaviorInPlayMode prevBehavior,
                InputSettings.BackgroundBehavior prevBackground,
                bool behaviorChanged,
                bool backgroundChanged)
            {
                _prevBehavior = prevBehavior;
                _prevBackground = prevBackground;
                _behaviorChanged = behaviorChanged;
                _backgroundChanged = backgroundChanged;
            }

            public static GameViewInputFocusScope Enter()
            {
                var settings = InputSystem.settings;

                var prevBehavior = settings.editorInputBehaviorInPlayMode;
                bool behaviorChanged = prevBehavior !=
                    InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView;
                if (behaviorChanged)
                    settings.editorInputBehaviorInPlayMode =
                        InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView;

                var prevBackground = settings.backgroundBehavior;
                bool backgroundChanged = prevBackground != InputSettings.BackgroundBehavior.IgnoreFocus;
                if (backgroundChanged)
                    settings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;

                return new GameViewInputFocusScope(
                    prevBehavior, prevBackground, behaviorChanged, backgroundChanged);
            }

            public void Dispose()
            {
                var settings = InputSystem.settings;
                // Restore in reverse order of application; the property setters early-out when the
                // value is unchanged (InputSettings.cs:521-527), so this is safe to call unconditionally.
                if (_backgroundChanged)
                    settings.backgroundBehavior = _prevBackground;
                if (_behaviorChanged)
                    settings.editorInputBehaviorInPlayMode = _prevBehavior;
            }
        }

        public static JObject SendKey(JObject args)
        {
            var keyName = args?["key"]?.Value<string>();
            if (string.IsNullOrWhiteSpace(keyName))
                return Error("key is required");

            if (!InputKeyNames.TryParse(keyName, out Key key))
                return Error($"unknown key: {keyName}");

            string action = args?["action"]?.Value<string>() ?? "tap";
            bool shift = args?["shift"]?.Value<bool>() ?? false;
            bool ctrl = args?["ctrl"]?.Value<bool>() ?? false;
            bool alt = args?["alt"]?.Value<bool>() ?? false;
            int holdMs = args?["holdMs"]?.Value<int>() ?? DefaultHoldMs;
            int waitFrames = args?["waitFrames"]?.Value<int>() ?? DefaultWaitFrames;
            int timeoutMs = args?["timeoutMs"]?.Value<int>() ?? DefaultTimeoutMs;

            if (holdMs < 0)
                return Error("holdMs must be >= 0");
            if (waitFrames < 0)
                return Error("waitFrames must be >= 0");
            if (timeoutMs < 1)
                return Error("timeoutMs must be positive");

            if (!MainThreadDispatcher.Run(() => EditorApplication.isPlaying))
                return Error("Editor must be in Play Mode to send input (use editor.setPlayMode first)");

            var keyboard = MainThreadDispatcher.Run(ResolveKeyboard);
            if (keyboard == null)
                return Error("No active Keyboard device (enable Input System package / active input handling)");

            int holdFrames = action.Equals("tap", StringComparison.OrdinalIgnoreCase)
                ? Math.Max(1, 1 + holdMs / MsPerFrame)
                : 1;

            var gate = new ManualResetEventSlim(false);
            bool begun = MainThreadDispatcher.Run(() =>
                BridgeInputRelay.TryBegin(
                    keyboard,
                    BuildModifierKeys(shift, ctrl, alt),
                    key,
                    action.ToLowerInvariant(),
                    holdFrames,
                    waitFrames,
                    gate));

            if (!begun)
                return Error("Another key operation is in progress");

            PumpPlayerLoopUntil(gate, timeoutMs);

            if (!gate.IsSet)
            {
                MainThreadDispatcher.Run(BridgeInputRelay.Cancel);
                return Error($"key operation timed out after {timeoutMs}ms");
            }

            return Ok(new JObject
            {
                ["key"] = key.ToString(),
                ["action"] = action,
                ["shift"] = shift,
                ["ctrl"] = ctrl,
                ["alt"] = alt,
            });
        }

        private static void PumpPlayerLoopUntil(ManualResetEventSlim gate, int timeoutMs)
        {
            float savedCaptureDt = 0f;
            bool applyCapture = CaptureDtStep > 0f;
            if (applyCapture)
                MainThreadDispatcher.Run(() => { savedCaptureDt = UnityEngine.Time.captureDeltaTime; UnityEngine.Time.captureDeltaTime = CaptureDtStep; });
            BridgeSimTime.SimDt = applyCapture ? CaptureDtStep : 0f;
            BridgeSimTime.Active = true;
            // Lift the unfocused-keyboard gate for the whole pump (QueuePlayerLoopUpdate only
            // *requests* an update that runs on Unity's own tick, so the focus setting must stay
            // applied across the entire loop, not just the dispatched action that queued it), so the
            // relay's queued KeyboardState event is applied when the requested Dynamic update runs.
            // Without this, taps never register wasPressedThisFrame on an unfocused editor.
            MainThreadDispatcher.Run(() => _focusScope = GameViewInputFocusScope.Enter());
            try
            {
                int deadline = Environment.TickCount + timeoutMs;
                while (!gate.IsSet)
                {
                    if (Environment.TickCount >= deadline)
                        return;

                    MainThreadDispatcher.Run(() => EditorApplication.QueuePlayerLoopUpdate());
                    Thread.Sleep(8);
                }
            }
            finally
            {
                BridgeSimTime.Active = false;
                MainThreadDispatcher.Run(() => { _focusScope.Dispose(); _focusScope = default; });
                if (applyCapture)
                    MainThreadDispatcher.Run(() => UnityEngine.Time.captureDeltaTime = savedCaptureDt);
            }
        }

        public static JObject SendMouseLook(JObject args)
        {
            float dx = args?["dx"]?.Value<float>() ?? 0f;
            float dy = args?["dy"]?.Value<float>() ?? 0f;
            int frames = args?["frames"]?.Value<int>() ?? 1;
            int timeoutMs = args?["timeoutMs"]?.Value<int>() ?? DefaultTimeoutMs;

            if (frames < 1)
                return Error("frames must be >= 1");
            if (timeoutMs < 1)
                return Error("timeoutMs must be positive");

            if (!MainThreadDispatcher.Run(() => EditorApplication.isPlaying))
                return Error("Editor must be in Play Mode to send input (use editor.setPlayMode first)");

            var mouse = MainThreadDispatcher.Run(ResolveMouse);
            if (mouse == null)
                return Error("No active Mouse device (enable Input System package / active input handling)");

            // Drive mouse-delta injection directly: bypass onBeforeUpdate (which does not fire
            // during a mouse pump) by queuing state events and calling InputSystem.Update() on
            // the main thread each frame, then triggering a player-loop update so gameplay
            // scripts (e.g. a first-person camera controller) read the delta that same frame.
            MainThreadDispatcher.Run(() =>
            {
                // Lift the unfocused-pointer gate (see GameViewInputFocusScope) so queued mouse
                // events are applied on the player-loop Dynamic update even when the editor is
                // unfocused — same root cause as the keyboard path.
                using (GameViewInputFocusScope.Enter())
                {
                    var deltaState = new MouseState { delta = new UnityEngine.Vector2(dx, dy) };
                    for (int i = 0; i < frames; i++)
                    {
                        // Queue the delta, then run ONE player-loop update. The player loop's input
                        // phase processes the queued event (delta live) and the camera controller's
                        // Update reads it the SAME frame. Do NOT call InputSystem.Update() here — it would
                        // consume the delta before the player loop, which then resets it to zero.
                        InputSystem.QueueStateEvent(mouse, deltaState);
                        EditorApplication.QueuePlayerLoopUpdate();
                    }
                    // Reset delta so subsequent frames read zero.
                    InputSystem.QueueStateEvent(mouse, new MouseState());
                    EditorApplication.QueuePlayerLoopUpdate();
                }
            });

            return Ok(new JObject { ["dx"] = dx, ["dy"] = dy, ["frames"] = frames });
        }

        public static JObject SendMouseButton(JObject args)
        {
            string buttonName = args?["button"]?.Value<string>() ?? "left";
            string action = args?["action"]?.Value<string>() ?? "click";
            int holdMs = args?["holdMs"]?.Value<int>() ?? DefaultHoldMs;
            int timeoutMs = args?["timeoutMs"]?.Value<int>() ?? DefaultTimeoutMs;

            if (holdMs < 0)
                return Error("holdMs must be >= 0");
            if (timeoutMs < 1)
                return Error("timeoutMs must be positive");

            MouseButton button;
            switch (buttonName.ToLowerInvariant())
            {
                case "left":   button = MouseButton.Left;   break;
                case "right":  button = MouseButton.Right;  break;
                case "middle": button = MouseButton.Middle; break;
                default:       return Error($"unknown button: {buttonName}");
            }

            action = action.ToLowerInvariant();
            if (action != "click" && action != "down" && action != "up")
                return Error($"unknown action: {action} (expected click, down, up)");

            if (!MainThreadDispatcher.Run(() => EditorApplication.isPlaying))
                return Error("Editor must be in Play Mode to send input (use editor.setPlayMode first)");

            var mouse = MainThreadDispatcher.Run(ResolveMouse);
            if (mouse == null)
                return Error("No active Mouse device (enable Input System package / active input handling)");

            int holdFrames = action == "click" ? Math.Max(1, 1 + holdMs / MsPerFrame) : 1;

            // Drive mouse-button injection directly: same rationale as SendMouseLook —
            // onBeforeUpdate does not fire during a mouse pump, so we inject and update inline.
            MainThreadDispatcher.Run(() =>
            {
                // Lift the unfocused-pointer gate (see GameViewInputFocusScope) so queued mouse
                // button events are applied on the player-loop Dynamic update even when unfocused.
                using (GameViewInputFocusScope.Enter())
                {
                    // Queue state, then run a player-loop update so the input phase processes the
                    // event and gameplay scripts (PlotClickHandler) see wasPressedThisFrame that
                    // frame. No standalone InputSystem.Update() — it would consume the press before
                    // the player loop reads it.
                    if (action == "up")
                    {
                        InputSystem.QueueStateEvent(mouse, new MouseState());
                        EditorApplication.QueuePlayerLoopUpdate();
                        return;
                    }

                    // "down" or "click": press the button.
                    var pressedState = new MouseState().WithButton(button, true);
                    for (int i = 0; i < holdFrames; i++)
                    {
                        InputSystem.QueueStateEvent(mouse, pressedState);
                        EditorApplication.QueuePlayerLoopUpdate();
                    }

                    if (action == "click")
                    {
                        // Release after hold.
                        InputSystem.QueueStateEvent(mouse, new MouseState());
                        EditorApplication.QueuePlayerLoopUpdate();
                    }
                    // For "down", leave the button held; caller issues "up" separately.
                }
            });

            return Ok(new JObject { ["button"] = buttonName, ["action"] = action });
        }

        private static Keyboard ResolveKeyboard()
        {
            var keyboard = Keyboard.current;
            if (keyboard == null)
                keyboard = InputSystem.AddDevice<Keyboard>();
            else if (!keyboard.added)
                InputSystem.AddDevice(keyboard);

            // DEVICE-MISMATCH GUARD: gameplay scripts typically read Keyboard.current. Force the
            // device we assert into to be current so the bridge press is the press the game reads.
            // Idempotent when already current.
            if (keyboard != null && Keyboard.current != keyboard)
                keyboard.MakeCurrent();

            return keyboard;
        }

        private static Mouse ResolveMouse()
        {
            var mouse = Mouse.current;
            if (mouse == null)
                mouse = InputSystem.AddDevice<Mouse>();
            else if (!mouse.added)
                InputSystem.AddDevice(mouse);

            return mouse;
        }

        private static Key[] BuildModifierKeys(bool shift, bool ctrl, bool alt)
        {
            var keys = new List<Key>(3);
            if (shift) keys.Add(Key.LeftShift);
            if (ctrl) keys.Add(Key.LeftCtrl);
            if (alt) keys.Add(Key.LeftAlt);
            return keys.ToArray();
        }

        private static JObject Ok(JObject data) => new JObject { ["ok"] = true, ["data"] = data };
        private static JObject Error(string message) => new JObject { ["ok"] = false, ["error"] = message };
    }

    public static class InputKeyNames
    {
        public static bool TryParse(string name, out Key key)
        {
            key = default;
            if (string.IsNullOrWhiteSpace(name))
                return false;

            var trimmed = name.Trim();
            if (trimmed.Length == 1)
            {
                if (char.IsLetter(trimmed[0]))
                    return Enum.TryParse(trimmed.ToUpperInvariant(), out key);

                if (char.IsDigit(trimmed[0]))
                    return Enum.TryParse($"Digit{trimmed}", out key);
            }

            if (trimmed.Equals("shift", StringComparison.OrdinalIgnoreCase))
            {
                key = Key.LeftShift;
                return true;
            }

            if (trimmed.Equals("ctrl", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Equals("control", StringComparison.OrdinalIgnoreCase))
            {
                key = Key.LeftCtrl;
                return true;
            }

            if (trimmed.Equals("alt", StringComparison.OrdinalIgnoreCase))
            {
                key = Key.LeftAlt;
                return true;
            }

            return Enum.TryParse(trimmed, true, out key);
        }
    }
}
