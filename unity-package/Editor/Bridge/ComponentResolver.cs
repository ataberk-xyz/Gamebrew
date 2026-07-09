using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace Gamebrew.Bridge
{
    public static class ComponentResolver
    {
        private static readonly Dictionary<string, bool> EditorOnlyAssemblyCache = new Dictionary<string, bool>();

        // TODO(decouple): simple-name resolution prefers a type in YOUR game's namespace
        // over UnityEngine.* The original hardcoded the game's own root namespace. Set this
        // to your game's root namespace to keep that convenience; empty string disables the preference.
        private const string PreferredNamespacePrefix = "";

        /// <summary>
        /// Resolves a Component subtype by short name or full name.
        /// Priority: exact assembly-qualified → preferred-namespace → UnityEngine.* → any.
        /// Skips types in Editor-only assemblies (cannot be added via AddComponent).
        /// </summary>
        public static Type ResolveComponentType(string typeName)
        {
            if (string.IsNullOrWhiteSpace(typeName))
            {
                return null;
            }

            var exact = Type.GetType(typeName);
            if (exact != null && typeof(Component).IsAssignableFrom(exact) && CanAddAsComponent(exact))
            {
                return exact;
            }

            var candidates = TypeCache.GetTypesDerivedFrom<Component>();

            var fbMatch = PreferredNamespacePrefix.Length == 0 ? null : FirstAddable(candidates, t =>
                t.FullName == typeName ||
                (t.Name == typeName && t.Namespace != null &&
                 t.Namespace.StartsWith(PreferredNamespacePrefix, StringComparison.Ordinal)));
            if (fbMatch != null)
            {
                return fbMatch;
            }

            var unityMatch = FirstAddable(candidates, t =>
                t.FullName == typeName ||
                (t.Name == typeName && t.Namespace != null &&
                 t.Namespace.StartsWith("UnityEngine", StringComparison.Ordinal)));
            if (unityMatch != null)
            {
                return unityMatch;
            }

            return FirstAddable(candidates, t => t.Name == typeName || t.FullName == typeName);
        }

        private static Type FirstAddable(IEnumerable<Type> candidates, Func<Type, bool> predicate)
        {
            foreach (var type in candidates)
            {
                if (predicate(type) && CanAddAsComponent(type))
                {
                    return type;
                }
            }

            return null;
        }

        private static bool CanAddAsComponent(Type type)
        {
            if (type == null || type.IsAbstract || type.IsGenericType)
            {
                return false;
            }

            var asmName = type.Assembly.GetName().Name;
            if (EditorOnlyAssemblyCache.TryGetValue(asmName, out var editorOnly))
            {
                return !editorOnly;
            }

            editorOnly = false;
            foreach (var asm in CompilationPipeline.GetAssemblies())
            {
                if (asm.name != asmName)
                {
                    continue;
                }

                editorOnly = (asm.flags & AssemblyFlags.EditorAssembly) != 0;
                break;
            }

            EditorOnlyAssemblyCache[asmName] = editorOnly;
            return !editorOnly;
        }
    }
}
