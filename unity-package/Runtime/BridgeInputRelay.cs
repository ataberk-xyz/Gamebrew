using System.Threading;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;

namespace Gamebrew.Bridge
{
    /// <summary>
    /// Play-mode keyboard injection for the editor bridge. Hooks the Input System
    /// player loop so <c>wasPressedThisFrame</c> works in gameplay scripts.
    /// Mouse look and mouse button injection are driven directly by
    /// <c>PlayModeInputCoordinator</c> (in the companion Editor assembly) and do not
    /// use this relay.
    ///
    /// GENERIC / GAME-AGNOSTIC: this type depends only on UnityEngine + the Input System
    /// (UnityEngine.InputSystem / .LowLevel) and System.Threading. It has NO game
    /// references, which is why it lives in the shared runtime assembly rather than the
    /// game. The bridge's Editor assembly drives it during Play Mode via a direct
    /// (Editor → Runtime) reference.
    /// </summary>
    public static class BridgeInputRelay
    {
        private sealed class PendingTap
        {
            public Keyboard Keyboard;
            public Key[] Modifiers;
            public Key Key;
            public string Action;
            public int HoldFrames;
            public int PostFrames;
            public int Phase;
            public int Counter;
            public ManualResetEventSlim Gate;
        }

        private sealed class PendingHold
        {
            public Keyboard Keyboard;
            public Key[] Modifiers;
            public Key Key;
        }

        private static PendingTap _pending;
        private static PendingHold _hold;
        private static bool _hooked;

        public static bool TryBegin(
            Keyboard keyboard,
            Key[] modifiers,
            Key key,
            string action,
            int holdFrames,
            int postFrames,
            ManualResetEventSlim gate)
        {
            if (_pending != null)
                return false;

            EnsureHook();
            _pending = new PendingTap
            {
                Keyboard = keyboard,
                Modifiers = modifiers,
                Key = key,
                Action = action,
                HoldFrames = holdFrames,
                PostFrames = postFrames,
                Gate = gate,
            };
            return true;
        }

        public static void Cancel() => _pending = null;

        public static void BeginHold(Keyboard keyboard, Key[] modifiers, Key key)
        {
            EnsureHook();
            _hold = new PendingHold { Keyboard = keyboard, Modifiers = modifiers, Key = key };
        }

        public static void EndHold(Keyboard keyboard)
        {
            _hold = null;
            ReleaseAll(keyboard);
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetDomain()
        {
            _pending = null;
            _hold = null;
            _hooked = false;
        }

        static void EnsureHook()
        {
            if (_hooked)
                return;

            InputSystem.onBeforeUpdate += OnBeforeUpdate;
            _hooked = true;
        }

        static void OnBeforeUpdate()
        {
            // Re-apply hold EVERY InputSystem update so the hold key stays pressed across
            // all player-loop frames — this must run regardless of whether a tap is pending.
            var hold = _hold;
            if (hold != null)
                ApplyPressed(hold.Keyboard, hold.Modifiers, hold.Key);

            var pending = _pending;
            if (pending == null)
                return;

            if (pending.Action == "down")
            {
                ApplyPressed(pending.Keyboard, pending.Modifiers, pending.Key);
                Complete(pending);
                return;
            }

            if (pending.Action == "up")
            {
                ReleaseAll(pending.Keyboard);
                Complete(pending);
                return;
            }

            switch (pending.Phase)
            {
                case 0:
                    ReleaseAll(pending.Keyboard);
                    pending.Phase = 1;
                    break;
                case 1:
                    ApplyPressed(pending.Keyboard, pending.Modifiers, pending.Key);
                    pending.Phase = 2;
                    pending.Counter = 0;
                    break;
                case 2:
                    pending.Counter++;
                    if (pending.Counter >= pending.HoldFrames)
                        pending.Phase = 3;
                    break;
                case 3:
                    ReleaseAll(pending.Keyboard);
                    pending.Phase = 4;
                    pending.Counter = 0;
                    break;
                case 4:
                    pending.Counter++;
                    if (pending.Counter >= pending.PostFrames)
                        Complete(pending);
                    break;
            }
        }

        static void Complete(PendingTap pending)
        {
            pending.Gate?.Set();
            _pending = null;
        }

        static void ApplyPressed(Keyboard keyboard, Key[] modifiers, Key key)
        {
            var pressedKeys = new Key[modifiers.Length + 1];
            modifiers.CopyTo(pressedKeys, 0);
            pressedKeys[modifiers.Length] = key;
            InputSystem.QueueStateEvent(keyboard, new KeyboardState(pressedKeys));
        }

        static void ReleaseAll(Keyboard keyboard)
        {
            InputSystem.QueueStateEvent(keyboard, new KeyboardState());
        }

        /// <summary>
        /// Re-assert a held keyboard state from outside the frame-pump loop.
        /// NOTE: the synthetic-keys hold pump that drove this (PlayModeInputCoordinator.HoldKey)
        /// was removed as dead code (Update does not tick under an unfocused-editor pump). This
        /// member currently has no callers — kept pending a decision to retire the hold API.
        /// </summary>
        public static void AssertPressed(Keyboard keyboard, Key[] modifiers, Key key)
            => ApplyPressed(keyboard, modifiers, key);

        /// <summary>
        /// Release all keyboard keys. Paired with <see cref="AssertPressed"/> to end a hold.
        /// </summary>
        public static void ReleaseAllKeys(Keyboard keyboard)
            => ReleaseAll(keyboard);
    }
}
