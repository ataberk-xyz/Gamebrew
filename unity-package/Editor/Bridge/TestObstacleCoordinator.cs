using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Gamebrew.Bridge
{
    /// <summary>
    /// EDITOR-ONLY. AI-test-only infra for NavMesh detour verification.
    ///
    /// Provides two bridge verbs (NOT registered here — see CommandRouter wiring note below):
    ///
    ///   test.spawnWall  {x, z, width?, height?, length?, name?}
    ///   test.despawnWall {name?}
    ///
    /// PURPOSE: the existing play.navTo tests used clear 2-corner straight lines where
    /// NavMesh.CalculatePath would trivially succeed without real routing. This coordinator
    /// spawns a blocking cube primitive mid-path, triggers BridgeNavMeshBaker.Rebake() so the
    /// surface reflects the new obstacle, then the orchestrator calls play.navTo and asserts
    /// cornerCount >= 3 (a detour adds at least one intermediate corner). despawnWall cleans up
    /// and rebakes so subsequent tests start clean.
    ///
    /// WHY CreatePrimitive: PrimitiveType.Cube gives a MeshRenderer + BoxCollider automatically.
    /// A typical BridgeNavMeshBaker bakes render meshes / colliders over the whole scene, so the
    /// cube is seen as obstacle geometry without any manual collider setup.
    ///
    /// HARD CONSTRAINT: this file is Editor-only (lives in the Bridge assembly which is
    /// includePlatforms:["Editor"]). It must never be shipped in a build.
    ///
    /// ── CommandRouter wiring (do NOT edit CommandRouter.cs yourself) ──────────────────────
    ///
    ///   Verb             Handler                               Args shape
    ///   ───────────────  ────────────────────────────────────  ──────────────────────────────
    ///   test.spawnWall   TestObstacleCoordinator.SpawnWall     {x:float, z:float, width?=0.4,
    ///                                                           height?=2.5, length?=6,
    ///                                                           name?="TestWall"}
    ///   test.despawnWall TestObstacleCoordinator.DespawnWall   {name?="TestWall"}
    ///
    /// Add these two cases to CommandRouter.Execute's switch block, mirroring e.g. play.navTo:
    ///
    ///   case "test.spawnWall":
    ///       return TestObstacleCoordinator.SpawnWall(args);
    ///
    ///   case "test.despawnWall":
    ///       return TestObstacleCoordinator.DespawnWall(args);
    ///
    /// ─────────────────────────────────────────────────────────────────────────────────────
    /// </summary>
    public static class TestObstacleCoordinator
    {
        private const string DefaultWallName   = "TestWall";
        private const float  DefaultWallWidth  = 0.4f;   // X-extent of the slab (thin dimension)
        private const float  DefaultWallHeight = 2.5f;   // Y-extent (tall enough no one can jump it)
        private const float  DefaultWallLength = 6f;     // Z-extent (long enough to span the path)

        /// <summary>
        /// test.spawnWall — create a blocking cube primitive, position it, then rebake NavMesh.
        ///
        /// Args:
        ///   x      float  (required) world X of the wall's centre
        ///   z      float  (required) world Z of the wall's centre
        ///   width  float? (default 0.4) localScale.x — the thin blocking dimension
        ///   height float? (default 2.5) localScale.y — tall enough to be a real obstacle
        ///   length float? (default 6.0) localScale.z — long enough to span the whole path
        ///   name   string?(default "TestWall") GameObject name; used as the despawn key
        ///
        /// Returns {ok, data:{name, pos:{x,y,z}, hasNavMesh, bakeFound}}
        ///   hasNavMesh  — BridgeNavMeshBaker.Rebake() result after rebake (baker found + baked)
        ///   bakeFound   — whether a BridgeNavMeshBaker was found at all; false = no rebake happened
        /// </summary>
        public static JObject SpawnWall(JObject args)
        {
            if (args?["x"] == null) return Error("x is required");
            if (args?["z"] == null) return Error("z is required");

            float x      = args["x"].Value<float>();
            float z      = args["z"].Value<float>();
            float width  = args?["width"]?.Value<float>()  ?? DefaultWallWidth;
            float height = args?["height"]?.Value<float>() ?? DefaultWallHeight;
            float length = args?["length"]?.Value<float>() ?? DefaultWallLength;
            string name  = args?["name"]?.Value<string>()  ?? DefaultWallName;

            if (width  <= 0f) return Error("width must be > 0");
            if (height <= 0f) return Error("height must be > 0");
            if (length <= 0f) return Error("length must be > 0");

            string error = null;
            JObject result = null;

            MainThreadDispatcher.Run(() =>
            {
                if (!EditorApplication.isPlaying)
                {
                    error = "Editor must be in Play Mode for test.spawnWall";
                    return;
                }

                // Remove any stale wall with the same name so SpawnWall is idempotent.
                var stale = GameObject.Find(name);
                if (stale != null)
                    Object.Destroy(stale);

                // PrimitiveType.Cube auto-attaches MeshRenderer + BoxCollider.
                // A whole-scene BridgeNavMeshBaker picks this cube up as an obstacle without
                // any extra setup.
                var wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
                wall.name = name;

                // Centre the wall at (x, height/2, z) so its base sits on the ground plane.
                wall.transform.position   = new Vector3(x, height * 0.5f, z);
                wall.transform.localScale = new Vector3(width, height, length);

                // Rebake the NavMesh so the new obstacle is reflected in the surface.
                var bootstrap = Object.FindAnyObjectByType<BridgeNavMeshBaker>();
                bool bakeFound  = bootstrap != null;
                bool hasNavMesh = bakeFound && bootstrap.Rebake();

                Vector3 pos = wall.transform.position;
                result = new JObject
                {
                    ["name"]       = wall.name,
                    ["pos"]        = new JObject { ["x"] = pos.x, ["y"] = pos.y, ["z"] = pos.z },
                    ["hasNavMesh"] = hasNavMesh,
                    ["bakeFound"]  = bakeFound,
                };
            });

            return error != null ? Error(error) : Ok(result);
        }

        /// <summary>
        /// test.despawnWall — destroy the named wall and rebake the NavMesh (cleanup).
        ///
        /// Args:
        ///   name string? (default "TestWall") name of the wall GameObject to destroy
        ///
        /// Returns {ok, data:{name, destroyed, hasNavMesh, bakeFound}}
        ///   destroyed   — whether the GameObject was found and destroyed
        ///   hasNavMesh  — BridgeNavMeshBaker.Rebake() result after rebake
        ///   bakeFound   — whether a BridgeNavMeshBaker was found
        /// </summary>
        public static JObject DespawnWall(JObject args)
        {
            string name = args?["name"]?.Value<string>() ?? DefaultWallName;

            string error = null;
            JObject result = null;

            MainThreadDispatcher.Run(() =>
            {
                if (!EditorApplication.isPlaying)
                {
                    error = "Editor must be in Play Mode for test.despawnWall";
                    return;
                }

                var wall = GameObject.Find(name);
                bool destroyed = wall != null;
                if (destroyed)
                    Object.Destroy(wall);

                // Rebake regardless of whether the wall was found — caller may have destroyed it
                // via other means, and we still want a clean NavMesh after cleanup.
                var bootstrap  = Object.FindAnyObjectByType<BridgeNavMeshBaker>();
                bool bakeFound  = bootstrap != null;
                bool hasNavMesh = bakeFound && bootstrap.Rebake();

                result = new JObject
                {
                    ["name"]       = name,
                    ["destroyed"]  = destroyed,
                    ["hasNavMesh"] = hasNavMesh,
                    ["bakeFound"]  = bakeFound,
                };
            });

            return error != null ? Error(error) : Ok(result);
        }

        private static JObject Ok(JObject data) =>
            new JObject { ["ok"] = true, ["data"] = data };

        private static JObject Error(string message) =>
            new JObject { ["ok"] = false, ["error"] = message };
    }
}
