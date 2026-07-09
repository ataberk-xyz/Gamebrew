using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Gamebrew.Bridge.Examples
{
    /// <summary>
    /// A minimal, self-contained example of a GAME-SPECIFIC play verb. It is NOT part of the
    /// generic core — it lives here to show the pattern for adding your own verbs on top of the
    /// bridge. This one references nothing but UnityEngine, so it compiles anywhere; a real game
    /// verb would call into your own systems (drive a minigame, advance a clock, pick up an item…).
    ///
    /// Every verb method takes a <c>JObject args</c>, does its Unity work inside
    /// <see cref="MainThreadDispatcher.Run"/>, and returns a detached <c>JObject</c> — never a live
    /// Unity reference across the thread boundary (the HTTP handler runs off the main thread).
    /// </summary>
    public static class ExampleVerbCoordinator
    {
        /// <summary>`play.example.countActive` — returns how many active GameObjects are in the
        /// loaded scenes. Illustrates the shape of a read-only verb.</summary>
        public static JObject CountActive(JObject args)
        {
            return MainThreadDispatcher.Run(() =>
            {
                int active = 0;
                foreach (var go in Object.FindObjectsByType<GameObject>(FindObjectsSortMode.None))
                    if (go.activeInHierarchy) active++;
                return new JObject { ["ok"] = true, ["data"] = new JObject { ["activeCount"] = active } };
            });
        }
    }
}
