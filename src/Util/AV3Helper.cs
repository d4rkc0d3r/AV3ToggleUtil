#if UNITY_EDITOR
using UnityEngine;
using UnityEditor;
using VRC.SDK3.Avatars.Components;
using System;

namespace d4rkpl4y3r.AV3ToggleUtil.Util
{
    public static class AV3Helper
    {
        public static bool ClickableLastRect()
        {
            var rect = GUILayoutUtility.GetLastRect();
            EditorGUIUtility.AddCursorRect(rect, MouseCursor.Link);
            var clicked = Event.current.type == EventType.MouseDown && rect.Contains(Event.current.mousePosition) && Event.current.button == 0;
            if (clicked)
            {
                Event.current.Use();
            }
            return clicked;
        }

        private static Transform GetTransformFromPath(Transform root, string path, Transform current = null, int index = 0)
        {
            if (string.IsNullOrEmpty(path))
                return root;
            if (current == null)
                current = root;
            foreach (Transform t in current)
            {
                if (path[index..].StartsWith(t.name, StringComparison.Ordinal))
                {
                    int nextIndex = index + t.name.Length;
                    if (path.Length == nextIndex)
                        return t;
                    if (path[nextIndex] != '/')
                        continue;
                    var result = GetTransformFromPath(root, path, t, nextIndex + 1);
                    if (result != null)
                        return result;
                }
            }
            return null;
        }

        public static Transform FindTransformByAvatarPath(Transform root, string path)
        {
            if (root == null)
                return null;
            if (string.IsNullOrEmpty(path) || path == "(Root)" || path == "(root)")
                return root;
            return GetTransformFromPath(root, path);
        }

        public static bool SelectTransformByPathFromLastRect(Transform root, string path)
        {
            var transform = FindTransformByAvatarPath(root, path);
            if (transform == null)
                return false;

            if (!ClickableLastRect())
                return false;

            Selection.activeGameObject = transform.gameObject;
            EditorGUIUtility.PingObject(transform.gameObject);
            return true;
        }

        public static VRCAvatarDescriptor FindAvatarDescriptor(GameObject obj)
        {
            if (obj == null)
                return null;
            VRCAvatarDescriptor descriptor;
            while (!obj.TryGetComponent(out descriptor))
            {
                if (obj.transform.parent == null)
                    return null;
                obj = obj.transform.parent.gameObject;
            }
            return descriptor;
        }
    }
}
#endif