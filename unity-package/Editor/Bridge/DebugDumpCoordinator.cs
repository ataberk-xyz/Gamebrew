#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Gamebrew.Bridge
{
    /// <summary>
    /// EDITOR-ONLY. The <c>debug.dumpComponent</c> verb: read a LIVE runtime component's
    /// fields — INCLUDING private / non-[SerializeField] ones — over the bridge.
    ///
    /// This closes the "runtime-state-blindness" gap: before this, the only windows into a
    /// running component were Game-View captures and console logs, neither of which can read a
    /// private buffer like <c>SoilPaintVisual._cellFill</c> (a non-serialized <c>float[]</c> set
    /// by PourStation). Visual/logic-alignment QA (the minigame-visual-qa-gap) needs to assert on
    /// that interior state, so this verb reflects it out as JSON.
    ///
    /// Command route (CommandRouter.cs): "debug.dumpComponent"
    ///
    /// Arg shape (exactly one of {type, path} required):
    ///   type    string   component type SIMPLE name (e.g. "SoilPaintVisual"). Resolved by
    ///                     reflecting over loaded assemblies by Type.Name; the preferred game namespace wins (see PreferredNamespacePrefix)
    ///                     ambiguity. Live instances found via FindObjectsByType (incl. inactive).
    ///   path    string   GameObject hierarchy path (GameObjectResolver.Find); requires component.
    ///   component string  required when path given — component type name to GetComponent.
    ///   fields  string[] optional — field names to dump (e.g. ["_cellFill","_drySoilColor"]).
    ///                     Omitted ⇒ dump public + [SerializeField]-or-private instance fields,
    ///                     bounded to <see cref="MaxAutoFields"/>.
    ///   index   int      optional (default 0) — which live instance when several match.
    ///
    /// Return (ok=true):
    ///   data.type          string  — resolved Type.FullName
    ///   data.instanceCount int     — how many live instances matched (type mode) / 1 (path mode)
    ///   data.index         int     — index actually dumped
    ///   data.gameObject    string  — hierarchy path of the dumped instance's GameObject
    ///   data.fields        object  — { name: value | {"__error": "..."} }
    ///   data.truncatedFields string[] (optional) — field names whose array/list was capped
    ///
    /// Serialization (see <see cref="SerializeValue"/>):
    ///   primitives / string / bool          → JSON scalar
    ///   enum                                 → name (string)
    ///   Vector2/3/4                          → {x,y[,z[,w]]}
    ///   Color / Color32                      → {r,g,b,a}
    ///   array / IList                        → JArray, CAPPED at <see cref="MaxArrayElements"/>
    ///   null                                 → null
    ///   anything else                        → ToString()
    ///   a per-field reflection/serialize fault is captured as {"__error": "..."}, NEVER thrown.
    /// </summary>
    public static class DebugDumpCoordinator
    {
        /// <summary>Hard cap on serialized array / IList elements; excess is dropped + noted.</summary>
        public const int MaxArrayElements = 256;

        /// <summary>Cap on auto-discovered fields when <c>fields</c> is omitted (avoid dumping huge types).</summary>
        public const int MaxAutoFields = 128;

        // TODO(decouple): when a component is requested by simple name and several types
        // share it, the resolver prefers a type in YOUR game's namespace over UnityEngine.*
        // The original hardcoded the game's own root namespace. Set this to your game's
        // root namespace (e.g. "MyGame") to keep that convenience; empty string disables
        // the preference (falls straight through to UnityEngine.* → any).
        private const string PreferredNamespacePrefix = "";

        private const BindingFlags FieldFlags =
            BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;

        public static JObject DumpComponent(JObject args)
        {
            // ── arg validation (pre-flight, off the main thread) ───────────────
            string typeName = args?["type"]?.Value<string>();
            string path = args?["path"]?.Value<string>();
            string componentName = args?["component"]?.Value<string>();

            bool hasType = !string.IsNullOrWhiteSpace(typeName);
            bool hasPath = !string.IsNullOrWhiteSpace(path);

            if (!hasType && !hasPath)
                return Error("exactly one of {type, path} is required");
            if (hasType && hasPath)
                return Error("provide either type OR path, not both");
            if (hasPath && string.IsNullOrWhiteSpace(componentName))
                return Error("component is required when path is given (the component type name to read)");

            int index = args?["index"]?.Value<int>() ?? 0;
            if (index < 0)
                return Error($"index must be >= 0 (got {index})");

            List<string> requestedFields = null;
            var fieldsTok = args?["fields"];
            if (fieldsTok != null && fieldsTok.Type == JTokenType.Array)
            {
                requestedFields = new List<string>();
                foreach (var f in (JArray)fieldsTok)
                {
                    var name = f?.Value<string>();
                    if (!string.IsNullOrWhiteSpace(name))
                        requestedFields.Add(name);
                }
            }

            // ── main-thread reflection block (ONE Run; no nesting). We build the entire
            //    JObject inside the Run and return it across the boundary — JObject is a
            //    detached managed value-graph, never a live Unity reference. ───────────
            return MainThreadDispatcher.Run(() =>
            {
                Component target;
                Type resolvedType;
                int instanceCount;

                if (hasType)
                {
                    resolvedType = ResolveComponentType(typeName);
                    if (resolvedType == null)
                        return Error($"unknown component type: {typeName} (no loaded type with that simple/full name derives from Component)");

                    var instances = FindLiveInstances(resolvedType);
                    instanceCount = instances.Count;
                    if (instanceCount == 0)
                        return Error($"no live instance of {resolvedType.FullName} in the loaded scenes");
                    if (index >= instanceCount)
                        return Error($"index {index} out of range: only {instanceCount} live instance(s) of {resolvedType.FullName}");

                    target = instances[index];
                }
                else
                {
                    var go = GameObjectResolver.Find(path);
                    if (go == null)
                        return Error($"GameObject not found at path: {path}");

                    resolvedType = ResolveComponentType(componentName);
                    if (resolvedType == null)
                        return Error($"unknown component type: {componentName}");

                    target = go.GetComponent(resolvedType);
                    if (target == null)
                        return Error($"GameObject '{path}' has no component of type {resolvedType.FullName}");

                    instanceCount = 1;
                    index = 0;
                }

                // A destroyed-but-not-null managed wrapper would survive FindObjectsByType only in
                // exotic teardown windows; the Unity null-overload guards it.
                if (target == null)
                    return Error("resolved component is null/destroyed");

                var fieldsObj = new JObject();
                var truncated = new JArray();

                if (requestedFields != null)
                {
                    // Explicit field list: an unknown name is a hard error (name it).
                    foreach (var fieldName in requestedFields)
                    {
                        var fi = FindField(resolvedType, fieldName);
                        if (fi == null)
                            return Error($"unknown field '{fieldName}' on {resolvedType.FullName}");

                        fieldsObj[fieldName] = DumpField(target, fi, fieldName, truncated);
                    }
                }
                else
                {
                    // Auto-discover: public + private instance fields up the hierarchy, bounded.
                    int count = 0;
                    var seen = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var fi in EnumerateInstanceFields(resolvedType))
                    {
                        // Skip compiler-generated backing fields (e.g. <Prop>k__BackingField noise).
                        if (fi.Name.IndexOf("k__BackingField", StringComparison.Ordinal) >= 0)
                            continue;
                        if (!seen.Add(fi.Name))
                            continue; // a shadowing field lower in the hierarchy already won

                        if (count >= MaxAutoFields)
                        {
                            fieldsObj["__truncatedFieldList"] =
                                $"field list capped at {MaxAutoFields}; pass explicit 'fields' to read more";
                            break;
                        }

                        fieldsObj[fi.Name] = DumpField(target, fi, fi.Name, truncated);
                        count++;
                    }
                }

                var data = new JObject
                {
                    ["type"] = resolvedType.FullName,
                    ["instanceCount"] = instanceCount,
                    ["index"] = index,
                    ["gameObject"] = GameObjectResolver.PathOf(SafeGameObject(target)),
                    ["fields"] = fieldsObj,
                };
                if (truncated.Count > 0)
                    data["truncatedFields"] = truncated;

                BridgeRunLog.Ok(
                    "debug.dumpComponent",
                    $"type={resolvedType.Name} index={index}/{instanceCount} fields={fieldsObj.Count}");

                return Ok(data);
            });
        }

        // ── field dump (per-field fail-closed) ─────────────────────────────────

        /// <summary>
        /// Reads one field off <paramref name="target"/> and serializes it, capturing ANY fault
        /// (reflection or serialize) as a {"__error": "..."} node so one odd field never aborts
        /// the whole dump. Main-thread only (touches the live object).
        /// </summary>
        private static JToken DumpField(object target, FieldInfo fi, string reportName, JArray truncated)
        {
            try
            {
                object value = fi.GetValue(target);
                bool wasTruncated;
                var token = SerializeValue(value, out wasTruncated);
                if (wasTruncated)
                    truncated.Add(reportName);
                return token;
            }
            catch (Exception ex)
            {
                return new JObject { ["__error"] = ex.GetType().Name + ": " + ex.Message };
            }
        }

        // ── value serialization (PUBLIC for direct unit testing) ───────────────

        /// <summary>
        /// Serialize an arbitrary field value to a JToken. Pure (no Unity-scene access beyond
        /// reading the already-fetched value), so it is unit-testable in isolation.
        /// <paramref name="truncated"/> is set true when an array/IList exceeded
        /// <see cref="MaxArrayElements"/> and was capped.
        /// </summary>
        public static JToken SerializeValue(object value, out bool truncated)
        {
            truncated = false;

            if (value == null)
                return JValue.CreateNull();

            switch (value)
            {
                case string s:
                    return new JValue(s);
                case bool b:
                    return new JValue(b);
                case Enum e:
                    return new JValue(e.ToString()); // by name
            }

            var t = value.GetType();

            // Numeric primitives + decimal → JSON scalar.
            if (t.IsPrimitive)
                return JToken.FromObject(value); // covers int/float/double/byte/long/char/etc.
            if (value is decimal dec)
                return new JValue(dec);

            // Unity math/color value types → explicit object shapes.
            switch (value)
            {
                case Vector2 v2:
                    return new JObject { ["x"] = v2.x, ["y"] = v2.y };
                case Vector3 v3:
                    return new JObject { ["x"] = v3.x, ["y"] = v3.y, ["z"] = v3.z };
                case Vector4 v4:
                    return new JObject { ["x"] = v4.x, ["y"] = v4.y, ["z"] = v4.z, ["w"] = v4.w };
                case Quaternion q:
                    return new JObject { ["x"] = q.x, ["y"] = q.y, ["z"] = q.z, ["w"] = q.w };
                case Color c:
                    return new JObject { ["r"] = c.r, ["g"] = c.g, ["b"] = c.b, ["a"] = c.a };
                case Color32 c32:
                    return new JObject { ["r"] = c32.r, ["g"] = c32.g, ["b"] = c32.b, ["a"] = c32.a };
            }

            // Arrays and IList (incl. List<T>, T[]) → JArray capped at MaxArrayElements.
            // (string handled above so it never reaches this IEnumerable branch.)
            if (value is IList list)
            {
                var arr = new JArray();
                int n = list.Count;
                int take = n;
                if (take > MaxArrayElements)
                {
                    take = MaxArrayElements;
                    truncated = true;
                }
                for (int i = 0; i < take; i++)
                {
                    // Recurse so element Colors/Vectors/nested-but-shallow values serialize too.
                    // Element truncation is folded into the parent flag.
                    bool elemTrunc;
                    arr.Add(SerializeValueShallow(list[i], out elemTrunc));
                    if (elemTrunc) truncated = true;
                }
                if (truncated)
                    arr.Add(new JValue($"…truncated: {n} total, showing {take}"));
                return arr;
            }

            // Fallback: ToString(). For UnityEngine.Object this yields "name (Type)".
            return new JValue(value.ToString());
        }

        /// <summary>
        /// Element serializer for collections: same scalar/Unity-type handling as
        /// <see cref="SerializeValue"/> but does NOT recurse into nested collections (a nested
        /// IList element is rendered via ToString() to avoid unbounded fan-out). Keeps the 256
        /// cap a per-field guarantee.
        /// </summary>
        private static JToken SerializeValueShallow(object value, out bool truncated)
        {
            truncated = false;
            if (value == null) return JValue.CreateNull();

            switch (value)
            {
                case string s: return new JValue(s);
                case bool b: return new JValue(b);
                case Enum e: return new JValue(e.ToString());
            }

            var t = value.GetType();
            if (t.IsPrimitive) return JToken.FromObject(value);
            if (value is decimal dec) return new JValue(dec);

            switch (value)
            {
                case Vector2 v2: return new JObject { ["x"] = v2.x, ["y"] = v2.y };
                case Vector3 v3: return new JObject { ["x"] = v3.x, ["y"] = v3.y, ["z"] = v3.z };
                case Vector4 v4: return new JObject { ["x"] = v4.x, ["y"] = v4.y, ["z"] = v4.z, ["w"] = v4.w };
                case Quaternion q: return new JObject { ["x"] = q.x, ["y"] = q.y, ["z"] = q.z, ["w"] = q.w };
                case Color c: return new JObject { ["r"] = c.r, ["g"] = c.g, ["b"] = c.b, ["a"] = c.a };
                case Color32 c32: return new JObject { ["r"] = c32.r, ["g"] = c32.g, ["b"] = c32.b, ["a"] = c32.a };
            }

            return new JValue(value.ToString());
        }

        // ── type / instance / field resolution ─────────────────────────────────

        /// <summary>
        /// Resolve a Component subtype by simple OR full name. Ambiguity preference:
        /// exact full-name → preferred-namespace → UnityEngine.* → any. Mirrors
        /// <see cref="ComponentResolver"/> but WITHOUT the editor-assembly / abstract /
        /// AddComponent guards — introspection must reach abstract bases and editor types too.
        /// Uses <see cref="TypeCache.GetTypesDerivedFrom{Component}"/> (spans loaded assemblies).
        /// </summary>
        public static Type ResolveComponentType(string typeName)
        {
            if (string.IsNullOrWhiteSpace(typeName))
                return null;

            var exact = Type.GetType(typeName);
            if (exact != null && typeof(Component).IsAssignableFrom(exact))
                return exact;

            var candidates = TypeCache.GetTypesDerivedFrom<Component>();

            // Full-name exact wins outright (most specific).
            foreach (var t in candidates)
                if (t.FullName == typeName)
                    return t;

            // Simple-name; a type in PreferredNamespacePrefix (your game) is preferred.
            Type fb = null, unity = null, any = null;
            foreach (var t in candidates)
            {
                if (t.Name != typeName)
                    continue;
                var ns = t.Namespace ?? string.Empty;
                if (PreferredNamespacePrefix.Length > 0 &&
                    ns.StartsWith(PreferredNamespacePrefix, StringComparison.Ordinal)) { fb = t; break; }
                if (unity == null && ns.StartsWith("UnityEngine", StringComparison.Ordinal)) unity = t;
                if (any == null) any = t;
            }

            return fb ?? unity ?? any;
        }

        /// <summary>
        /// All live instances of <paramref name="type"/> across loaded scenes, INCLUDING those on
        /// inactive GameObjects. <c>FindObjectsByType&lt;T&gt;</c> requires a generic; we go via
        /// <c>Resources.FindObjectsOfTypeAll</c> filtered to scene objects to honour inactive
        /// handling, then keep deterministic order. Main-thread only.
        /// </summary>
        private static List<Component> FindLiveInstances(Type type)
        {
            var result = new List<Component>();
            // FindObjectsOfTypeAll includes inactive + DontDestroyOnLoad; filter out assets /
            // prefab-stage / hidden editor objects by requiring a valid scene with a path-or-name.
            var all = Resources.FindObjectsOfTypeAll(type);
            foreach (var o in all)
            {
                if (o is Component comp && comp != null)
                {
                    var go = comp.gameObject;
                    if (go == null) continue;
                    // Exclude prefab assets / hide-flagged editor scaffolding.
                    if ((go.hideFlags & (HideFlags.HideAndDontSave | HideFlags.NotEditable)) != 0)
                        continue;
                    if (!go.scene.IsValid())
                        continue;
                    result.Add(comp);
                }
            }
            return result;
        }

        /// <summary>Find a single declared-or-inherited instance field by exact name.</summary>
        private static FieldInfo FindField(Type type, string name)
        {
            for (var t = type; t != null && t != typeof(object); t = t.BaseType)
            {
                var fi = t.GetField(name, FieldFlags | BindingFlags.DeclaredOnly);
                if (fi != null)
                    return fi;
            }
            return null;
        }

        /// <summary>
        /// Enumerate instance fields most-derived → base (so a derived field shadows a base one of
        /// the same name; the caller dedups by name). DeclaredOnly per level avoids the BindingFlags
        /// quirk where private base fields are otherwise invisible.
        /// </summary>
        private static IEnumerable<FieldInfo> EnumerateInstanceFields(Type type)
        {
            for (var t = type; t != null && t != typeof(object); t = t.BaseType)
            {
                var fields = t.GetFields(FieldFlags | BindingFlags.DeclaredOnly);
                foreach (var fi in fields)
                    yield return fi;
            }
        }

        private static GameObject SafeGameObject(Component c)
        {
            try { return c == null ? null : c.gameObject; }
            catch { return null; }
        }

        private static JObject Ok(JObject data) => new JObject { ["ok"] = true, ["data"] = data };
        private static JObject Error(string msg) => new JObject { ["ok"] = false, ["error"] = msg };
    }
}
#endif
