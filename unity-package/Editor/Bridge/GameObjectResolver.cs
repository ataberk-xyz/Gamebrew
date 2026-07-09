using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Gamebrew.Bridge
{
    public static class GameObjectResolver
    {
        public static GameObject Find(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            var segments = SplitPath(path);
            if (segments.Length == 0)
            {
                return null;
            }

            var current = FindRoot(segments[0]);
            if (current == null)
            {
                return null;
            }

            for (var i = 1; i < segments.Length; i++)
            {
                var child = current.transform.Find(segments[i]);
                if (child == null)
                {
                    return null;
                }

                current = child.gameObject;
            }

            return current;
        }

        public static GameObject CreateAtPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("path is required", nameof(path));
            }

            var segments = SplitPath(path);
            if (segments.Length == 0)
            {
                throw new ArgumentException("path is required", nameof(path));
            }

            var scene = SceneManager.GetActiveScene();
            var current = FindRoot(segments[0]);
            if (current == null)
            {
                current = CreateObject(segments[0], null, scene);
            }

            for (var i = 1; i < segments.Length; i++)
            {
                var childTransform = current.transform.Find(segments[i]);
                if (childTransform == null)
                {
                    current = CreateObject(segments[i], current.transform, scene);
                }
                else
                {
                    current = childTransform.gameObject;
                }
            }

            return current;
        }

        public static string PathOf(GameObject go)
        {
            if (go == null)
            {
                return null;
            }

            var parts = new List<string>();
            var t = go.transform;
            while (t != null)
            {
                parts.Add(t.name);
                t = t.parent;
            }

            parts.Reverse();
            return string.Join("/", parts);
        }

        private static GameObject FindRoot(string name)
        {
            foreach (var root in SceneManager.GetActiveScene().GetRootGameObjects())
            {
                if (root.name == name)
                {
                    return root;
                }
            }

            return null;
        }

        private static GameObject CreateObject(string name, Transform parent, Scene scene)
        {
            var go = new GameObject(name);
            if (parent != null)
            {
                go.transform.SetParent(parent, false);
            }

            Undo.RegisterCreatedObjectUndo(go, "Bridge create GameObject");
            EditorSceneManager.MarkSceneDirty(scene);
            return go;
        }

        private static string[] SplitPath(string path) =>
            path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
    }
}
