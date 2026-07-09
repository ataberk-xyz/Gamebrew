using System;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Gamebrew.Bridge
{
    /// <summary>
    /// Converts a JToken to a target CLR type for reflection-based property/method calls.
    /// Handles primitives, enums by name, Vector3 from {x,y,z}, and UnityEngine.Object by path.
    /// </summary>
    public static class JsonCoercion
    {
        public static object Coerce(JToken token, Type targetType)
        {
            if (token == null || token.Type == JTokenType.Null)
                return targetType.IsValueType ? Activator.CreateInstance(targetType) : null;

            // Enum: accept name string or numeric value
            if (targetType.IsEnum)
                return Enum.Parse(targetType, token.Value<string>(), ignoreCase: true);

            // Vector3 from { x, y, z } JSON object
            if (targetType == typeof(Vector3))
            {
                if (token is JObject obj)
                {
                    return new Vector3(
                        obj["x"]?.Value<float>() ?? 0f,
                        obj["y"]?.Value<float>() ?? 0f,
                        obj["z"]?.Value<float>() ?? 0f);
                }

                throw new ArgumentException($"Expected {{x,y,z}} object for Vector3, got: {token}");
            }

            // Component subtype: resolve by hierarchy path
            if (typeof(Component).IsAssignableFrom(targetType))
            {
                var path = token.Value<string>();
                var go = GameObjectResolver.Find(path);
                if (go == null)
                    throw new ArgumentException($"No GameObject at path: {path}");
                var comp = go.GetComponent(targetType);
                if (comp == null)
                    throw new ArgumentException($"No {targetType.Name} on '{path}'");
                return comp;
            }

            // GameObject: resolve by hierarchy path
            if (targetType == typeof(GameObject))
            {
                var path = token.Value<string>();
                var go = GameObjectResolver.Find(path);
                if (go == null)
                    throw new ArgumentException($"No GameObject at path: {path}");
                return go;
            }

            // UnityEngine.Object (ScriptableObject, Texture2D, etc.) — load from AssetDatabase
            // Must come after the Component and GameObject checks above.
            if (typeof(UnityEngine.Object).IsAssignableFrom(targetType))
            {
                var assetPath = token.Value<string>();
                if (string.IsNullOrEmpty(assetPath) || !assetPath.StartsWith("Assets/", StringComparison.Ordinal))
                    throw new ArgumentException(
                        $"Expected an Assets/... path for {targetType.Name}, got: {token}");
                var asset = AssetDatabase.LoadAssetAtPath(assetPath, targetType);
                if (asset == null)
                    throw new ArgumentException(
                        $"No asset of type {targetType.Name} at path: {assetPath}");
                return asset;
            }

            // Primitives and other Newtonsoft-compatible types
            return token.ToObject(targetType);
        }
    }
}
