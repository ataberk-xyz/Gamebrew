# Gamebrew Unity package (drop-in)

Copy this whole folder into your project (e.g. `Assets/Gamebrew/`), keeping **both** subfolders:

```
unity-package/
├── Editor/Bridge/    → Gamebrew.Bridge.Editor  (editor-only; stripped from player builds)
└── Runtime/          → Gamebrew.Bridge.Runtime  (all platforms)
```

The Editor asmdef references the Runtime asmdef **by name** (`Gamebrew.Bridge.Runtime`), so both
folders must be present for it to compile.

## Requirements (UPM packages)

| Package | Manifest id | Assembly | Needed by | Why |
|---|---|---|---|---|
| Input System | `com.unity.inputsystem` | `Unity.InputSystem` | Editor **and** Runtime | `play.sendKey` / `play.mouse*` + `BridgeInputRelay` |
| Newtonsoft Json | `com.unity.nuget.newtonsoft-json` | `Newtonsoft.Json.dll` | Editor only | JSON command envelope |

Also set **Project Settings → Player → Active Input Handling** to *Input System* (or *Both*).

The built-in **AI (NavMesh)** module (`UnityEngine.AIModule`, no package) is used by `play.navTo`
for path *planning*. *Baking* a runtime NavMesh is your game's job — implement the
`BridgeNavMeshBaker` seam.

## Movement / camera / navmesh seams (optional)

`Runtime/BridgeSeams.cs` defines three abstract MonoBehaviours the play-driver verbs locate via
`FindAnyObjectByType`:

| Seam | Verbs that use it | Implement by |
|---|---|---|
| `BridgeLocomotor` | `play.move`, `play.moveTo`, `play.navTo` | subclass on your player; forward `DriveLocomotion(Vector2, float)` |
| `BridgeCameraRig` | `play.aimAt`, `play.aimAtObject`, `play.setLook`, `play.navTo` | subclass on your camera rig; forward `AimAt` / `SetLook` / `CameraTransform` |
| `BridgeNavMeshBaker` | `test.spawnWall`, `test.despawnWall` | subclass on your navmesh baker; forward `Rebake()` |

If a seam is absent, the matching verb returns a clean `"No <seam> in the scene"` error and never
throws; all non-movement verbs (scene/gameobject/component/capture/dump/tests) work regardless.

See the top-level `README.md` for the full architecture, MCP setup, and safety model.
