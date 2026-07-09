# Example — adding your own `play.*` verb

The generic core is game-agnostic: it gives agents navigation, camera/orbit, capture, component
inspection, test/compile control, and the lease dispatcher. To let an agent drive **your game's**
mechanics (advance a clock, run a minigame, pick up an item, …) you add your own verbs. This
folder shows the pattern with a single trivial, fully self-contained verb.

`ExampleVerbCoordinator.cs` adds `play.example.countActive` — it references nothing but
`UnityEngine`, so it compiles anywhere. A real verb would call into your own systems instead.

## The pattern

1. **Write a static coordinator** in your Editor bridge assembly. Each public static method takes
   a `JObject args` and returns a `JObject` (`ok` / `data` / `error`), does its Unity work inside
   `MainThreadDispatcher.Run(...)`, and reads/writes only detached JSON across the thread boundary
   — never a live Unity reference (the HTTP handler runs off the main thread).

2. **Register the verb** in `CommandRouter.Execute`, at the marked extension point:

   ```csharp
   // ── GAME-SPECIFIC VERB EXTENSION POINT ──
   case "play.example.countActive": return ExampleVerbCoordinator.CountActive(args);
   ```

   Because it lives under the `play.*` namespace, the lease dispatcher's default-deny allowlist
   **auto-allows** it — no `browseAllowlist.mjs` edit needed. (Verbs outside `play.*` that should be
   browse-safe must be added to the allowlist explicitly.)

3. **(Optional) Add a matching MCP tool** in `mcp/src/index.mjs` if you want the agent to call the
   verb by a friendly `bridge_*` name instead of the raw `play.*` command.

## Why verbs live outside the core

The core stays game-agnostic: a game should be able to add verbs **without editing** generic files
(beyond the one registration line). If you find yourself copying game logic into the core, that's a
signal the core needs a proper verb-registration API — see the "Extension API" decision in the
top-level `README.md`.
