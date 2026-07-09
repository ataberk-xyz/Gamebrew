# Gamebrew — an AI-agent bridge for your Unity game

**Gamebrew lets an AI agent drive a *running* Unity Editor.** The agent can walk the player,
look around, orbit a free camera to inspect a prop, capture the game view as a PNG, read the
scene graph, dump a live component's private fields as JSON, and (with your game's own verbs)
interact with the world — all over a tiny loopback HTTP bridge. A **lease dispatcher** lets
several agents **take turns** driving the one shared Editor without stepping on each other.

It is a lightweight, self-contained alternative to third-party Unity MCP packages: ~20 small
Editor C# files + one Node MCP server. No cloud, no auth, loopback only. Works with any
Unity 6 project that uses the Input System.

> **Snapshot status.** This repo was extracted from a real game project. The generic core is
> decoupled from that game and the `unity-package/` is now **self-contained**: it ships both an
> Editor assembly (`Gamebrew.Bridge.Editor`) and its companion runtime assembly
> (`Gamebrew.Bridge.Runtime`, holding `BridgeInputRelay`, `BridgeSimTime`, and the game-agnostic
> movement/camera/navmesh **seams**). It should compile on drop-in given the two UPM packages in
> §2. The only remaining game hooks are the **optional** stubbed verbs (perception, clock,
> playtest report) — see [Status / what's decoupled vs TODO](#8-status--whats-decoupled-vs-todo).
> Each is marked in-source with `TODO(decouple)`.

---

## 1. Architecture

Two halves. The Unity Editor side hosts an HTTP listener; the Node side speaks MCP to your
agent client and optionally serializes multiple agents through a lease dispatcher.

```
   ┌─────────────┐   MCP/stdio    ┌───────────────────────┐
   │  AI agent   │ ─────────────▶ │  Gamebrew MCP server  │   (mcp/src/index.mjs)
   │ (Claude,    │                │  bridge_* tools       │
   │  Cursor, …) │ ◀───────────── │                       │
   └─────────────┘                └───────────┬───────────┘
                                              │ POST { command, args }
                                              ▼
                                   http://127.0.0.1:8787/           ← Editor HTTP bridge
                                   ┌───────────────────────┐          (unity-package/…/BridgeServer.cs)
                                   │  Unity Editor (Play)  │
                                   │  CommandRouter switch │ ── drives ──▶  your running game
                                   └───────────────────────┘

   ── Multiple agents? put the lease DISPATCHER in front of 8787 ──────────────────────────

   agent A ─┐                        ┌──────────────────────┐
   agent B ─┼── POST /lease, /cmd ─▶ │ dispatcher  :8788    │ ─ only the lease HOLDER ─▶ :8787
   agent C ─┘   (take turns)         │ lease + queue +      │
                                     │ default-deny allow   │
                                     └──────────────────────┘
```

- **Editor bridge (`:8787`)** — a `HttpListener` bound to `127.0.0.1`. A `CommandRouter`
  dispatches `{command, args}` to coordinators (scene, gameobject, component, play-mode
  input, navigation, orbit camera, capture, component dump, tests).
- **MCP server (`mcp/src/index.mjs`)** — exposes the bridge as `bridge_*` MCP tools; POSTs a
  bare `{command,args}` body to `:8787`. This is the **orchestrator / lifecycle** path.
- **Lease dispatcher (`mcp/src/dispatcher.mjs`, `:8788`)** — optional, additive. Sits in
  front of `:8787` and serializes turns so parallel agents don't interleave and desync the
  one shared world. Enforces a **default-deny browse allowlist**.
- **`mcp/browse.sh`** — a thin shell wrapper an agent uses to acquire/drive/release a lease.

---

## 2. Install — the Unity half

1. Copy the whole **`unity-package/`** into your project (e.g. `Assets/Gamebrew/`), keeping the
   `Editor/` and `Runtime/` subfolders intact. Unity regenerates the `.meta` files. Both
   assemblies (`Gamebrew.Bridge.Editor`, `Gamebrew.Bridge.Runtime`) are self-contained — the
   Editor asmdef references the Runtime asmdef **by name**, which resolves once both folders are
   present.
2. **Requirements (UPM packages)** — install both via **Window → Package Manager**:
   - **Input System** — `com.unity.inputsystem` (assembly `Unity.InputSystem`). Referenced by
     both assemblies; drives `play.sendKey` / `play.mouse*` and the input relay. Set
     **Edit → Project Settings → Player → Active Input Handling** to *Input System* (or *Both*).
   - **Newtonsoft Json** — `com.unity.nuget.newtonsoft-json` (`Newtonsoft.Json.dll`). Used by the
     Editor assembly's JSON command envelope. (The Runtime assembly needs neither Newtonsoft nor
     any other package.)

   The built-in **AI (NavMesh)** module (`UnityEngine.AIModule`) is used by `play.navTo` for
   *path planning* and needs no package. *Baking* a runtime NavMesh is your game's job — see the
   `BridgeNavMeshBaker` seam in §8.
3. **Wire the movement seams (optional, only if you use `play.move`/`play.navTo`/`play.aimAt`).**
   The play-driver verbs locate game-agnostic abstract components — `BridgeLocomotor`,
   `BridgeCameraRig`, `BridgeNavMeshBaker` (in `Runtime/BridgeSeams.cs`) — via
   `FindAnyObjectByType`. Subclass each on your player / camera / navmesh baker and forward to
   your own controllers (a 3-line adapter each; example in `BridgeSeams.cs`). If a seam is
   absent, the matching verb returns a clean `"No <seam> in the scene"` error — it never throws,
   and every other verb still works.
4. In Unity: **Tools → Unity Bridge → Start**. The listener comes up on
   `http://127.0.0.1:8787/`. It survives domain reloads (remembers your Start/Stop intent in
   `SessionState`).

## 3. Install — the MCP / Node half

```bash
cd mcp
npm install
```

Register the server with your MCP client (Cursor `.cursor/mcp.json`, Claude Code MCP config, …):

```json
{
  "mcpServers": {
    "gamebrew-bridge": {
      "command": "node",
      "args": ["/absolute/path/to/Gamebrew/mcp/src/index.mjs"]
    }
  }
}
```

Restart / reconnect the client after config changes. If the client shows a stale tool count
after you add tools, regenerate descriptors with `npm run export-mcp-tools` and toggle the
server off/on.

### Environment variables

| Variable | Default | Purpose |
|----------|---------|---------|
| `UNITY_BRIDGE_URL` | `http://127.0.0.1:8787/` | Editor bridge endpoint (also read by the dispatcher). |
| `GAMEBREW_BRIDGE_URL` | — | Legacy alias for `UNITY_BRIDGE_URL`. |
| `UNITY_BRIDGE_CONTROL_FILE` | `.unity-bridge/control.json` | Start/stop the bridge when HTTP is down (Editor polls this file). |
| `UNITY_BRIDGE_MENU` | `Tools → Unity Bridge → Start` | Shown in error messages. |
| `DISPATCHER_PORT` | `8788` | Lease dispatcher port. |
| `DISPATCHER_LEASE_TTL` | `90` (seconds) | Idle auto-release timeout. |
| `GAMEBREW_BROWSE_RUN` | `default` | Per-session nonce so two parallel runs of the *same* agent id don't fight over one lease. |

---

## 4. The agent browse workflow

The bridge cannot stream video. An agent **sees** the world by capturing a still and Reading
the PNG; it reads exact state with a component dump. A typical single-agent loop:

```bash
cd mcp
# 1. ACQUIRE the lease (only granted if free; otherwise you're queued)
./browse.sh my-agent acquire

# 2. NAVIGATE / LOOK  (play.* verbs; require the Editor to already be in Play Mode)
./browse.sh my-agent play.navTo    '{"target":"Environment/Player","standoff":2}'
./browse.sh my-agent play.orbitView '{"target":"Environment/Player","pitch":18,"yaw":45,"distance":2.5}'

# 3. CAPTURE → then Read the printed path to SEE the frame
./browse.sh my-agent capture before        #  → Logs/browse-my-agent-before.png

# 4. DUMP live state as JSON (beats squinting at a low-contrast capture)
./browse.sh my-agent dump Environment/Player Rigidbody

# 5. RELEASE promptly when done — do NOT sit on the lease
./browse.sh my-agent release
```

### ⚠️ The `orbitView`-vs-`capture` gotcha

There are **two different cameras** and it trips people up:

- `play.orbitView` moves a **free inspection camera** and writes its own frame to
  **`Logs/orbit.png`** (a fixed path). It does **not** move the player rig.
- `capture` (`editor.captureGameView`) reads whatever the **player / main game camera**
  currently sees, to the path you name.

So after an `orbitView`, **Read `Logs/orbit.png`** to see the orbit result — a plain
`capture` will show the *player's* view, which orbitView never moved. To judge a prop's
silhouette, orbit it from a few yaws (`yaw:0` front, `yaw:60` 3/4, `yaw:120` side) and Read
each `Logs/orbit.png`. One head-on `navTo` capture is not enough to say a prop "reads".

---

## 5. Safety model

- **Loopback only.** The Editor bridge binds `127.0.0.1:8787`; the dispatcher binds
  `127.0.0.1:8788`. Nothing listens on a routable interface.
- **No authentication.** This is a local dev tool. Reflection can invoke component methods —
  run it only on projects you trust.
- **Default-deny browse allowlist.** The dispatcher's `/cmd` forwards a command only if it is
  a `play.*` verb (whole namespace) or an exact member of a small read/inspect/perceive set.
  Everything else — Play-Mode toggle, scene load/save, tests, structural mutation, killing the
  bridge — is **rejected without reaching the Editor**. (See `mcp/src/browseAllowlist.mjs`.)
- **Orchestrator owns lifecycle.** Doctrine: the orchestrator drives Play enter/exit, scene
  I/O, tests, compiles, and structural mutation on the **direct `:8787` path**. Agents get
  **lease windows for gameplay only** on `:8788`. An agent physically cannot enter Play Mode
  or mutate the scene graph — so ask the orchestrator to enter Play Mode before your window.
- **The lease is COOPERATIVE, not enforced.** The dispatcher gives advisory turn-taking +
  observability, not access control. `:8787` stays open, so a client that talks to it
  directly bypasses the lease entirely. The lease works only because everyone agrees to go
  through `:8788`. The orchestrator must `GET :8788/status` and not drive while an agent holds
  the lease. **Do not repoint the MCP client's `UNITY_BRIDGE_URL` at `:8788`** — the client
  POSTs a bare body to `/`, which the dispatcher doesn't route (it only knows `/lease/*`,
  `/cmd`, `/status`); keep the MCP path on `:8787`.

### Lease etiquette (the short version)

1. **Acquire** before driving; you're queued if it's held.
2. **Drive**, then **release promptly** — never hold more than 1–2 minutes.
3. Idle leases **auto-reclaim** at `DISPATCHER_LEASE_TTL` (90s) and pass to the next agent.
4. **Two-phase promotion:** on release the next queued agent is *reserved* and must re-`acquire`
   within ~12s to confirm, so a dead agent can't block the queue for the full TTL.
5. Same agent id in two parallel runs → give each a distinct `GAMEBREW_BROWSE_RUN` nonce.

Validate the whole lease/allowlist machine with the self-contained test (spins up the
dispatcher + a stub Editor, asserts turn-taking + allowlist):

```bash
cd mcp && npm run test:dispatcher     # → "37 passed, 0 failed"
```

---

## 6. Command vocabulary (generic verbs)

`<target>` is a scene path like `Environment/Player`. `play.*` verbs require Play Mode.

| Command | Args | Purpose | Browse-safe? |
|---|---|---|:--:|
| `ping` | `{}` | liveness / Unity version | ✅ |
| `editor.getBridgeStatus` | `{}` | bridge health | ✅ |
| `editor.getPlayMode` / `editor.setPlayMode` | `{}` / `{playing}` | read / **set** Play Mode | read ✅ / set 🚫 |
| `editor.captureGameView` | `{path}` | write a PNG of the game camera | ✅ |
| `editor.getConsoleLogs` | `{level?, count?}` | recent console output | ✅ |
| `editor.executeMenuItem` | `{path}` | run a menu item | 🚫 |
| `editor.stopBridge` | `{}` | stop the listener | 🚫 |
| `scene.getHierarchy` | `{}` | active scene tree | ✅ |
| `scene.open` / `scene.save` | `{path}` / `{}` | scene I/O | 🚫 |
| `gameobject.find` / `gameobject.get` | `{name}` / `{path}` | read a GameObject | ✅ |
| `gameobject.create` / `.rename` / `.reparent` / `.setActive` | varies | mutate the scene graph | 🚫 |
| `component.add` / `.setProperty` / `.getProperty` / `.callMethod` | varies | mutate / invoke components | 🚫 |
| `debug.dumpComponent` | `{type}` or `{path, component}` | live component fields → JSON | ✅ |
| `play.move` / `play.moveTo` | varies | move the player | ✅ |
| `play.navTo` / `play.navTo.status` | `{target, standoff?, …}` / `{}` | NavMesh-walk the player to an object | ✅ |
| `play.viewObject` | `{target, standoff?}` | frame an object from the player's eye | ✅ |
| `play.orbitView` | `{target, pitch, yaw, distance}` | orbit a **free** camera → `Logs/orbit.png` | ✅ |
| `play.aimAt` / `play.aimAtObject` / `play.setLook` | varies | aim / look | ✅ |
| `play.sendKey` | `{key, action?, shift?, …}` | Input-System keyboard in Play Mode | ✅ |
| `play.mouseLook` / `play.mouseButton` | varies | mouse look / click | ✅ |
| `test.spawnWall` / `test.despawnWall` | varies | test obstacles | 🚫 |
| `editor.runTests` / `editor.getTestResults` / `editor.waitForCompile` | varies | EditMode tests / compile gate | 🚫 (orchestrator) |

### Adding your own game-specific verbs

New `play.*` verbs are **auto-allowed** by the dispatcher — you do not touch the allowlist.
To add one:

1. Write a static coordinator in the Editor assembly (e.g. `MyGamePlayCoordinator.cs`).
2. Add a `case "play.myVerb": return MyGamePlayCoordinator.Do(args);` in
   `CommandRouter.Execute` (there is a clearly-marked **extension point** where the game verbs
   used to be).
3. Optionally add a matching `bridge_*` MCP tool in `mcp/src/index.mjs`.

A minimal, self-contained worked example (a single `play.example.*` verb) lives in
**`examples/custom-verb/`** — copy the *pattern* for adding your own `play.*` verbs.

---

## 7. Repo layout

```
Gamebrew/
├── README.md                      ← you are here
├── LICENSE                        ← MIT placeholder (owner to confirm)
├── .gitignore
├── unity-package/                ← drop this whole folder into your project (keep both subfolders)
│   ├── Editor/Bridge/             ← the Editor-only assembly (Gamebrew.Bridge.Editor)
│   │   ├── Gamebrew.Bridge.Editor.asmdef   ← refs Gamebrew.Bridge.Runtime by name
│   │   ├── BridgeServer.cs        ← HttpListener on 127.0.0.1:8787 + Tools menu
│   │   ├── CommandRouter.cs       ← the command switch (+ game-verb extension point)
│   │   ├── MainThreadDispatcher.cs, CompileCoordinator.cs, ConsoleLogBuffer.cs …
│   │   ├── GameObjectResolver.cs, ComponentResolver.cs, JsonCoercion.cs
│   │   ├── DebugDumpCoordinator.cs (debug.dumpComponent), GameViewCapture.cs
│   │   ├── Play{Move,Nav,Orbit,View,Look}Coordinator.cs, PlayModeInputCoordinator.cs
│   │   ├── EditorPlayModeCoordinator.cs, Test{Run,Obstacle}Coordinator.cs
│   │   └── … (Tests/ folder NOT copied)
│   └── Runtime/                   ← the companion runtime assembly (Gamebrew.Bridge.Runtime)
│       ├── Gamebrew.Bridge.Runtime.asmdef   ← refs Unity.InputSystem; all platforms
│       ├── BridgeInputRelay.cs    ← generic Play-Mode keyboard injector (play.sendKey)
│       ├── BridgeSimTime.cs       ← generic deterministic-dt seam (#if UNITY_EDITOR)
│       └── BridgeSeams.cs         ← BridgeLocomotor / BridgeCameraRig / BridgeNavMeshBaker
├── mcp/
│   ├── package.json               ← "gamebrew-mcp"
│   ├── browse.sh                  ← agent lease wrapper
│   ├── src/index.mjs              ← MCP stdio server (bridge_* tools)
│   ├── src/dispatcher.mjs         ← lease dispatcher on :8788
│   ├── src/browseAllowlist.mjs    ← default-deny browse-safe command set
│   ├── src/playModeUtils.mjs      ← enter/exit Play Mode helpers
│   └── scripts/{test-dispatcher,export-mcp-tool-descriptors}.mjs
└── examples/
    └── custom-verb/               ← "how a game adds its own play verbs" (worked example)
        ├── README.md
        └── ExampleVerbCoordinator.cs
```

---

## 8. Status / what's decoupled vs TODO

**Decoupled and generic:** the whole transport (HttpListener, CommandRouter, main-thread
dispatch), scene/gameobject/component verbs, `debug.dumpComponent`, all `play.*` navigation /
orbit / view / look coordinators, capture, tests, the console buffer — and the entire Node
half (MCP server, lease dispatcher, allowlist, browse wrapper). The Editor namespace set is
`Gamebrew.Bridge`. The companion **runtime assembly** `Gamebrew.Bridge.Runtime` is now bundled
(`unity-package/Runtime/`) and holds the three game-agnostic pieces the Editor coordinators
need: `BridgeInputRelay` (keyboard injection), `BridgeSimTime` (deterministic dt), and the
`BridgeLocomotor` / `BridgeCameraRig` / `BridgeNavMeshBaker` **seams** the play-driver verbs
locate via `FindAnyObjectByType`. The Node lease + allowlist logic passes its self-test 37/37.

**What still needs a decision or hookup is marked `TODO(decouple)` in-source, or is an
optional seam you implement.** The full list:

| Marker location | What it is | What the owner must do |
|---|---|---|
| `Runtime/BridgeSeams.cs` | `BridgeLocomotor` / `BridgeCameraRig` / `BridgeNavMeshBaker` — abstract MonoBehaviour seams that replace the game's `PlayerController` / `FirstPersonCamera` / `NavMeshBootstrap`. The play-driver verbs call these by direct invocation. | Subclass each on your player / camera / navmesh baker (3-line adapters) **only if** you use `play.move` / `play.navTo` / `play.aimAt` / `test.spawnWall`. Absent seam → clean error, no crash. |
| `CommandRouter.cs` (extension point ~line 140) | The game's own `play.*` verb cases were **removed**; a marked extension point remains. | Add your own `play.*` cases here (see `examples/`). |
| `CommandRouter.cs` (`time.advance`) | Drove the game's clock system. Body **stubbed** to return an error. | Wire to your own time system, or delete the case. |
| `CommandRouter.cs` (`perceive.aimHit` / `perceive.locate`) | Relied on a game perception/interaction helper (companion runtime, not in snapshot). Bodies **stubbed**. | Port a small perception helper, or delete the cases. |
| `CommandRouter.cs` (`editor.getPlaytestReport`, `playtest.clear`) | Read the game's acceptance-test log. Bodies **stubbed**. | Wire to your own test log, or delete the cases. |
| `PlayNavCoordinator.cs` | Default nav standoff was the game's interaction range × 0.9; **hardcoded** to `3.25f * 0.9f`. | Repoint at your own interaction range if you have one. |
| `ComponentResolver.cs` & `DebugDumpCoordinator.cs` | Simple-name type resolution preferred the game's root namespace; replaced with a `PreferredNamespacePrefix` const **defaulting to `""` (preference off)**. | Set it to your game's root namespace for the convenience, or leave empty. |
| `mcp/src/index.mjs:11, 293` | Two game-specific MCP tools (`bridge_run_playtest_scenario`, `bridge_get_playtest_report`) + their `playtestScenario.mjs` import were **removed**. | Add your own scenario tool if you want a one-shot acceptance run. |

### Owner decisions to make

1. **License** — this ships an MIT placeholder; confirm the real license (`LICENSE` has a
   `TODO: owner to confirm`).
2. **Final package name / namespace** — "Gamebrew" and `Gamebrew.Bridge` are working names.
3. **Extension API** — decide whether game verbs stay a hand-edited `switch` in
   `CommandRouter` (simple, current) or become a **registration API** (e.g. coordinators
   self-register verb→handler at load) so a game can add verbs without editing core files.
   The latter is the cleaner long-term boundary; the former is what exists today.

> The `unity-package/` is now **self-contained** (Editor + bundled `Gamebrew.Bridge.Runtime`).
> It could not be compiled *here* (no Unity toolchain in the extraction environment), but every
> referenced symbol resolves to the package itself, UnityEngine/UnityEditor, Newtonsoft.Json,
> System.*, or the Input System — so it is expected to compile on drop-in once the two UPM
> packages in §2 are present. The remaining `TODO(decouple)` items are **optional** game hooks.
