using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
// TODO(decouple): the original imported the game's own namespace for the
// game-specific handlers below (perceive.* via a game perception helper,
// time.advance via the game's clock system). Removed for the generic core —
// restore (or point at your own game namespace) only if you re-enable those handlers.
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Gamebrew.Bridge
{
    public sealed class CommandRouter
    {
        public JObject Execute(string command, JObject args)
        {
            if (string.IsNullOrWhiteSpace(command))
            {
                return Error("missing command");
            }

            try
            {
                switch (command)
                {
                    case "ping":
                        return Ok(new JObject
                        {
                            ["pong"] = true,
                            ["project"] = Application.productName,
                            ["unityVersion"] = Application.unityVersion,
                        });

                    case "editor.getConsoleLogs":
                        return HandleGetConsoleLogs(args);

                    case "editor.getPlaytestReport":
                        return HandleGetPlaytestReport();

                    case "editor.captureGameView":
                        return HandleCaptureGameView(args);

                    case "editor.getPlayMode":
                        return EditorPlayModeCoordinator.GetState();

                    case "editor.setPlayMode":
                        return HandleSetPlayMode(args);

                    case "editor.getBridgeStatus":
                        return BridgeServer.GetStatus();

                    case "editor.stopBridge":
                        return HandleStopBridge();

                    case "scene.getHierarchy":
                        return HandleSceneGetHierarchy();

                    case "gameobject.create":
                        return HandleGameObjectCreate(args);

                    case "gameobject.find":
                        return HandleGameObjectFind(args);

                    case "gameobject.get":
                        return HandleGameObjectGet(args);

                    case "gameobject.setActive":
                        return HandleGameObjectSetActive(args);

                    case "gameobject.rename":
                        return HandleGameObjectRename(args);

                    case "gameobject.reparent":
                        return HandleGameObjectReparent(args);

                    case "component.add":
                        return HandleComponentAdd(args);

                    case "component.setProperty":
                        return HandleComponentSetProperty(args);

                    case "component.getProperty":
                        return HandleComponentGetProperty(args);

                    case "component.callMethod":
                        return HandleComponentCallMethod(args);

                    case "scene.open":
                        return HandleSceneOpen(args);

                    case "scene.save":
                        return HandleSceneSave(args);

                    case "play.sendKey":
                        return PlayModeInputCoordinator.SendKey(args);

                    case "play.mouseLook":
                        return PlayModeInputCoordinator.SendMouseLook(args);

                    case "play.mouseButton":
                        return PlayModeInputCoordinator.SendMouseButton(args);

                    case "play.move":
                        return PlayMoveCoordinator.Move(args);

                    case "play.moveTo":
                        return PlayMoveCoordinator.MoveTo(args);

                    case "play.aimAt":
                        return PlayLookCoordinator.AimAt(args);

                    case "play.aimAtObject":
                        return PlayLookCoordinator.AimAtObject(args);

                    case "play.setLook":
                        return PlayLookCoordinator.SetLook(args);

                    case "play.navTo":
                        return PlayNavCoordinator.NavTo(args);

                    case "play.navTo.status":
                        return PlayNavCoordinator.NavStatus(args);

                    case "play.viewObject":
                        return PlayViewCoordinator.ViewObject(args);

                    case "play.orbitView":
                        return PlayOrbitCoordinator.OrbitView(args);

                    case "debug.dumpComponent":
                        return DebugDumpCoordinator.DumpComponent(args);

                    // ── GAME-SPECIFIC VERB EXTENSION POINT ────────────────────
                    // The original project wired game verbs here, e.g. minigame begin/hit
                    // and pickup verbs, each delegating to a game-specific static coordinator.
                    // Those coordinators reference game types, so they are NOT part of
                    // the generic core. To add your own gameplay verbs, drop a static
                    // coordinator into this Editor assembly and add a `case` here. Any
                    // verb under the `play.*` namespace is auto-allowed by the browse
                    // dispatcher's allowlist, so new play verbs need no allowlist edit.

                    case "test.spawnWall":
                        return TestObstacleCoordinator.SpawnWall(args);

                    case "test.despawnWall":
                        return TestObstacleCoordinator.DespawnWall(args);

                    case "perceive.aimHit":
                        return HandlePerceiveAimHit(args);

                    case "perceive.locate":
                        return HandlePerceiveLocate(args);

                    case "editor.isFocused":
                        return HandleEditorIsFocused();

                    case "playtest.clear":
                        return HandlePlaytestClear();

                    case "editor.executeMenuItem":
                        return HandleExecuteMenuItem(args);

                    case "time.advance":
                        return HandleTimeAdvance(args);

                    default:
                        return Error($"unknown command: {command}");
                }
            }
            catch (Exception ex)
            {
                return Error(ex.Message);
            }
        }

        private static JObject HandleGameObjectCreate(JObject args)
        {
            var path = RequireString(args, "path");
            var created = GameObjectResolver.CreateAtPath(path);
            return Ok(new JObject { ["path"] = GameObjectResolver.PathOf(created) });
        }

        private static JObject HandleGameObjectFind(JObject args)
        {
            var path = RequireString(args, "path");
            var found = GameObjectResolver.Find(path);
            return Ok(new JObject
            {
                ["found"] = found != null,
                ["path"] = path,
            });
        }

        private static JObject HandleGameObjectGet(JObject args)
        {
            var path = RequireString(args, "path");
            var go = GameObjectResolver.Find(path);
            if (go == null)
            {
                return Error($"not found: {path}");
            }

            return Ok(BuildGameObjectInfo(go));
        }

        private static JObject HandleGameObjectSetActive(JObject args)
        {
            var path = RequireString(args, "path");
            if (args?["active"] == null)
            {
                return Error("active is required");
            }

            var active = args["active"].Value<bool>();
            var go = GameObjectResolver.Find(path);
            if (go == null)
            {
                return Error($"not found: {path}");
            }

            Undo.RecordObject(go, "Bridge setActive");
            go.SetActive(active);
            EditorSceneManager.MarkSceneDirty(go.scene);
            return Ok(new JObject { ["path"] = path, ["active"] = go.activeSelf });
        }

        private static JObject HandleGameObjectRename(JObject args)
        {
            var path = RequireString(args, "path");
            var name = RequireString(args, "name");
            var go = GameObjectResolver.Find(path);
            if (go == null)
            {
                return Error($"not found: {path}");
            }

            Undo.RecordObject(go, "Bridge rename");
            go.name = name;
            EditorSceneManager.MarkSceneDirty(go.scene);
            return Ok(new JObject { ["path"] = GameObjectResolver.PathOf(go) });
        }

        private static JObject HandleGameObjectReparent(JObject args)
        {
            var path = RequireString(args, "path");
            var parentPath = args?["parentPath"]?.Value<string>();
            var go = GameObjectResolver.Find(path);
            if (go == null)
            {
                return Error($"not found: {path}");
            }

            Transform newParent = null;
            if (!string.IsNullOrEmpty(parentPath))
            {
                var parentGo = GameObjectResolver.Find(parentPath);
                if (parentGo == null)
                {
                    return Error($"parent not found: {parentPath}");
                }

                newParent = parentGo.transform;
            }

            Undo.SetTransformParent(go.transform, newParent, "Bridge reparent");
            EditorSceneManager.MarkSceneDirty(go.scene);
            return Ok(new JObject { ["path"] = GameObjectResolver.PathOf(go) });
        }

        private static JObject BuildGameObjectInfo(GameObject go)
        {
            var components = go.GetComponents<Component>()
                .Where(c => c != null)
                .Select(c => c.GetType().Name)
                .ToArray();

            var children = Enumerable.Range(0, go.transform.childCount)
                .Select(i => go.transform.GetChild(i).name)
                .ToArray();

            return new JObject
            {
                ["name"] = go.name,
                ["active"] = go.activeSelf,
                ["tag"] = go.tag,
                ["layer"] = go.layer,
                ["components"] = new JArray(components),
                ["children"] = new JArray(children),
            };
        }

        private static JObject HandleGetConsoleLogs(JObject args)
        {
            int count = args?["count"]?.Value<int>() ?? 50;
            string typeFilter = args?["type"]?.Value<string>() ?? "all";

            var logs = ConsoleLogBuffer.Recent(count)
                .Where(e => typeFilter == "all" || string.Equals(e.Type, typeFilter, StringComparison.OrdinalIgnoreCase))
                .Select(e => new JObject
                {
                    ["type"] = e.Type,
                    ["message"] = e.Message,
                })
                .ToArray();

            return Ok(new JObject { ["logs"] = new JArray(logs) });
        }

        private static JObject HandleGetPlaytestReport()
        {
            if (!EditorApplication.isPlaying)
                return Error("Editor must be in Play Mode to read playtest report");

            // TODO(decouple): the original read the game's structured happy-path
            // acceptance-test log and returned isHappyPath + missing/failed/warning
            // steps + the event queue. That is a game-authored acceptance harness, not
            // a generic bridge capability. Wire this to your own playtest log if you
            // want structured reports, or drop the `editor.getPlaytestReport` case in
            // Execute(). Original body preserved in git.
            return Error("editor.getPlaytestReport is game-specific — not wired in the generic core (see TODO(decouple) in CommandRouter)");
        }

        private static JObject HandleCaptureGameView(JObject args)
        {
            if (!EditorApplication.isPlaying)
                return Error("Editor must be in Play Mode to capture Game view");

            // D1 (collapse): delegate to the single shared capture body in GameViewCapture
            // (which pumps the live HUD via DriveHudVisuals(force:true) before the canvas
            // flip, validates the path, and renders 1280×720 — the SAME constants this body
            // used). One MainThreadDispatcher.Run wraps the whole Capture (Capture has no Run
            // of its own → single Run, no nesting). Re-derive {path,fullPath,bytes} from the
            // Result (which has no relativePath) + the original arg.
            string relativePath = args?["path"]?.Value<string>() ?? "Logs/playtest-capture.png";

            GameViewCapture.Result result = default;
            MainThreadDispatcher.Run(() => { result = GameViewCapture.Capture(relativePath); });

            if (!result.Success)
                return Error(result.Error);

            return Ok(new JObject
            {
                ["path"] = relativePath,
                ["fullPath"] = result.FullPath,
                ["bytes"] = result.Bytes,
            });
        }

        private static JObject HandleSetPlayMode(JObject args)
        {
            if (args?["playing"] == null)
                return Error("playing is required (boolean)");

            return EditorPlayModeCoordinator.ExecuteSetPlaying(args);
        }

        private static JObject HandleTimeAdvance(JObject args)
        {
            double hours = args?["hours"]?.Value<double>() ?? 0.0;
            if (hours <= 0.0)
                return Error("time.advance requires a positive 'hours'");

            string error = null;
            JObject result = null;

            MainThreadDispatcher.Run(() =>
            {
                if (!EditorApplication.isPlaying)
                {
                    error = "Editor must be in Play Mode for time.advance";
                    return;
                }

                // TODO(decouple): this handler drove the game's in-scene clock. The
                // original body resolved the game's clock system, advanced it by `hours`,
                // and returned the resulting day / hour-of-day / total game-hours.
                // There is no generic "game clock" — wire this to your own time system
                // (or delete the `time.advance` case in Execute()). Until then it no-ops.
                error = "time.advance is a game-specific verb — not wired in the generic core (see TODO(decouple) in CommandRouter.HandleTimeAdvance)";
            });

            return error != null ? Error(error) : Ok(result);
        }

        // ── Perception handlers ───────────────────────────────────────────

        // TODO(decouple): the two perceive.* handlers below relied on a game perception
        // helper (in a companion runtime assembly) plus its aim-hit / locate result
        // structs, and resolved the play camera via a game interaction helper. None of
        // that ships in the generic core. The *camera raycast* is generic — the
        // game-specific parts are (a) how you find the play camera and (b) the
        // classification of what was hit (kind = "plot"/"interactable"/…) and the
        // interaction prompt. To re-enable, port a small perception helper into this
        // assembly (or your own runtime assembly) and restore the bodies from git
        // history. Until then both no-op.
        private static JObject HandlePerceiveAimHit(JObject args)
        {
            if (!EditorApplication.isPlaying)
                return Error("Editor must be in Play Mode for perceive.aimHit");

            // Original body (game-coupled) — see TODO(decouple) above: it read a range
            // arg, found your game's player/interaction component, resolved the play
            // camera from it (falling back to Camera.main), then asked a game perception
            // helper for the aim-hit result (Hit, HitPath, Kind, TargetId, Distance, Prompt).
            return Error("perceive.aimHit is game-specific — not wired in the generic core (see TODO(decouple) in CommandRouter)");
        }

        private static JObject HandlePerceiveLocate(JObject args)
        {
            if (!EditorApplication.isPlaying)
                return Error("Editor must be in Play Mode for perceive.locate");

            // Original body (game-coupled) — see TODO(decouple) above: it resolved the
            // target GameObject by path, found your game's player/interaction component,
            // resolved the play camera from it (falling back to Camera.main), then asked a
            // game perception helper to locate the target on screen (OnScreen, ViewportX/Y,
            // BehindCamera, Distance, FrameCount).
            return Error("perceive.locate is game-specific — not wired in the generic core (see TODO(decouple) in CommandRouter)");
        }

        private static JObject HandleEditorIsFocused()
        {
            bool focused = false;
            MainThreadDispatcher.Run(() =>
            {
                focused = InternalEditorUtility.isApplicationActive;
            });

            return Ok(new JObject { ["focused"] = focused });
        }

        private static JObject HandlePlaytestClear()
        {
            if (!EditorApplication.isPlaying)
                return Error("Editor must be in Play Mode for playtest.clear");

            // TODO(decouple): the original reset the game's structured acceptance-test
            // event log (used by the happy-path playtest harness) and returned the new
            // run id. No game-authored equivalent ships in the core. The bundled neutral
            // BridgeRunLog shim exposes Clear()/RunId if you want to wire this generically:
            //   BridgeRunLog.Clear();
            //   return Ok(new JObject { ["runId"] = BridgeRunLog.RunId });
            // Otherwise wire it to your own playtest log or drop the `playtest.clear`
            // case in Execute().
            return Error("playtest.clear is game-specific — not wired in the generic core (see TODO(decouple) in CommandRouter)");
        }

        private static JObject HandleStopBridge()
        {
            bool wasRunning = BridgeServer.IsRunning;
            if (!wasRunning)
            {
                return Ok(new JObject
                {
                    ["isRunning"] = false,
                    ["changed"] = false,
                    ["url"] = BridgeServer.DefaultPrefix,
                });
            }

            BridgeServer.StopServerDeferred();
            return Ok(new JObject
            {
                ["isRunning"] = false,
                ["changed"] = true,
                ["url"] = BridgeServer.DefaultPrefix,
            });
        }

        private static JObject HandleSceneGetHierarchy()
        {
            var scene = SceneManager.GetActiveScene();
            var roots = scene.GetRootGameObjects();
            return Ok(new JObject
            {
                ["scene"] = scene.name,
                ["roots"] = new JArray(roots.Select(go => BuildHierarchyNode(go))),
            });
        }

        private static JObject BuildHierarchyNode(GameObject go)
        {
            var children = new JArray();
            for (var i = 0; i < go.transform.childCount; i++)
            {
                children.Add(BuildHierarchyNode(go.transform.GetChild(i).gameObject));
            }

            return new JObject
            {
                ["name"] = go.name,
                ["active"] = go.activeSelf,
                ["children"] = children,
            };
        }

        // ── Editor menu handler ───────────────────────────────────────────

        private static JObject HandleExecuteMenuItem(JObject args)
        {
            var path = RequireString(args, "path");
            bool executed = EditorApplication.ExecuteMenuItem(path);
            if (!executed)
                return Error($"menu item not found or not executed: {path}");
            return Ok(new JObject { ["executed"] = true, ["path"] = path });
        }

        // ── Scene handlers ────────────────────────────────────────────────

        private static JObject HandleSceneOpen(JObject args)
        {
            var path = RequireString(args, "path");

            if (!path.StartsWith("Assets/", StringComparison.Ordinal))
                return Error("scene path must start with Assets/");

            if (!path.EndsWith(".unity", StringComparison.Ordinal))
                return Error("scene path must end with .unity");

            if (string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(path)))
                return Error($"scene asset not found: {path}");

            var scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
            return Ok(new JObject
            {
                ["path"] = path,
                ["scene"] = scene.name,
            });
        }

        private static JObject HandleSceneSave(JObject args)
        {
            var scene = SceneManager.GetActiveScene();
            if (!scene.IsValid())
            {
                return Error("no active scene");
            }

            var pathArg = args?["path"]?.Value<string>();
            var savePath = string.IsNullOrWhiteSpace(pathArg) ? scene.path : pathArg;

            if (string.IsNullOrEmpty(savePath))
            {
                return Error("active scene is untitled; call scene.open first or pass path in scene.save");
            }

            if (!savePath.StartsWith("Assets/", StringComparison.Ordinal))
            {
                return Error("scene path must start with Assets/");
            }

            if (!savePath.EndsWith(".unity", StringComparison.Ordinal))
            {
                return Error("scene path must end with .unity");
            }

            bool saved = EditorSceneManager.SaveScene(scene, savePath);

            return Ok(new JObject
            {
                ["saved"] = saved,
                ["path"] = savePath,
                ["scene"] = scene.name,
            });
        }

        // ── Component handlers ────────────────────────────────────────────

        private static JObject HandleComponentAdd(JObject args)
        {
            var path = RequireString(args, "path");
            var typeName = RequireString(args, "type");

            var go = GameObjectResolver.Find(path);
            if (go == null)
                return Error($"not found: {path}");

            var type = ComponentResolver.ResolveComponentType(typeName);
            if (type == null)
                return Error($"unknown component type: {typeName}");

            var existing = go.GetComponent(type);
            if (existing == null)
            {
                var added = go.AddComponent(type);
                if (added == null)
                {
                    return Error($"cannot add component {typeName} (type may be abstract or an editor-only script)");
                }

                Undo.RegisterCreatedObjectUndo(added, "Bridge component.add");
                EditorUtility.SetDirty(go);
                EditorSceneManager.MarkSceneDirty(go.scene);
            }

            return Ok(new JObject { ["path"] = path, ["type"] = type.Name });
        }

        private static JObject HandleComponentSetProperty(JObject args)
        {
            var path = RequireString(args, "path");
            var typeName = RequireString(args, "type");
            var member = RequireString(args, "member");
            var value = args?["value"];

            var (comp, err) = FindComponent(path, typeName);
            if (err != null)
                return err;

            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            var compType = comp.GetType();

            var field = GetFieldInHierarchy(compType, member, flags);
            if (field != null)
            {
                field.SetValue(comp, JsonCoercion.Coerce(value, field.FieldType));
                EditorUtility.SetDirty(comp);
                EditorSceneManager.MarkSceneDirty(comp.gameObject.scene);
                return Ok(new JObject());
            }

            var prop = GetPropertyInHierarchy(compType, member, flags);
            if (prop != null && prop.CanWrite)
            {
                prop.SetValue(comp, JsonCoercion.Coerce(value, prop.PropertyType));
                EditorUtility.SetDirty(comp);
                EditorSceneManager.MarkSceneDirty(comp.gameObject.scene);
                return Ok(new JObject());
            }

            return Error($"writable member '{member}' not found on {typeName}");
        }

        private static JObject HandleComponentGetProperty(JObject args)
        {
            var path = RequireString(args, "path");
            var typeName = RequireString(args, "type");
            var member = RequireString(args, "member");

            var (comp, err) = FindComponent(path, typeName);
            if (err != null)
                return err;

            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            var compType = comp.GetType();

            var field = GetFieldInHierarchy(compType, member, flags);
            if (field != null)
                return Ok(new JObject { ["value"] = ToJToken(field.GetValue(comp)) });

            var prop = GetPropertyInHierarchy(compType, member, flags);
            if (prop != null && prop.CanRead)
                return Ok(new JObject { ["value"] = ToJToken(prop.GetValue(comp)) });

            return Error($"readable member '{member}' not found on {typeName}");
        }

        private static JObject HandleComponentCallMethod(JObject args)
        {
            var path = RequireString(args, "path");
            var typeName = RequireString(args, "type");
            var methodName = RequireString(args, "method");
            var argsArray = args?["args"] as JArray ?? new JArray();

            var (comp, err) = FindComponent(path, typeName);
            if (err != null)
                return err;

            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            var methodInfo = comp.GetType().GetMethod(methodName, flags);
            if (methodInfo == null)
                return Error($"method '{methodName}' not found on {typeName}");

            var parameters = methodInfo.GetParameters();
            if (argsArray.Count != parameters.Length)
                return Error($"method '{methodName}' expects {parameters.Length} arg(s) but got {argsArray.Count}");

            var coerced = new object[parameters.Length];
            for (var i = 0; i < parameters.Length; i++)
                coerced[i] = JsonCoercion.Coerce(argsArray[i], parameters[i].ParameterType);

            var result = methodInfo.Invoke(comp, coerced);
            return Ok(new JObject { ["result"] = ToJToken(result) });
        }

        // ── Reflection helpers ────────────────────────────────────────────

        private static (Component comp, JObject err) FindComponent(string path, string typeName)
        {
            var go = GameObjectResolver.Find(path);
            if (go == null)
                return (null, Error($"not found: {path}"));

            var type = ComponentResolver.ResolveComponentType(typeName);
            if (type == null)
                return (null, Error($"unknown component type: {typeName}"));

            var comp = go.GetComponent(type);
            if (comp == null)
                return (null, Error($"component {typeName} not found on '{path}'"));

            return (comp, null);
        }

        /// <summary>Walk up the type hierarchy to find a field (needed for private fields on base types).</summary>
        private static FieldInfo GetFieldInHierarchy(Type type, string name, BindingFlags flags)
        {
            while (type != null && type != typeof(object))
            {
                var fi = type.GetField(name, flags | BindingFlags.DeclaredOnly);
                if (fi != null)
                    return fi;
                type = type.BaseType;
            }

            return null;
        }

        /// <summary>Walk up the type hierarchy to find a property.</summary>
        private static PropertyInfo GetPropertyInHierarchy(Type type, string name, BindingFlags flags)
        {
            while (type != null && type != typeof(object))
            {
                var pi = type.GetProperty(name, flags | BindingFlags.DeclaredOnly);
                if (pi != null)
                    return pi;
                type = type.BaseType;
            }

            return null;
        }

        private static JToken ToJToken(object value)
        {
            if (value == null)
                return JValue.CreateNull();
            if (value.GetType().IsEnum)
                return new JValue(value.ToString());
            return value switch
            {
                bool b => new JValue(b),
                string s => new JValue(s),
                int i => new JValue(i),
                long l => new JValue(l),
                float f => new JValue(f),
                double d => new JValue(d),
                Vector3 v => new JObject { ["x"] = v.x, ["y"] = v.y, ["z"] = v.z },
                _ => new JValue(value.ToString()),
            };
        }

        private static string RequireString(JObject args, string key)
        {
            var value = args?[key]?.Value<string>();
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException($"{key} is required");
            }

            return value;
        }

        private static JObject Ok(JObject data) => new JObject { ["ok"] = true, ["data"] = data };

        private static JObject Error(string message) => new JObject { ["ok"] = false, ["error"] = message };
    }
}
