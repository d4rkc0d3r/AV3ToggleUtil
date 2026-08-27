#if UNITY_EDITOR
using UnityEngine;
using UnityEditor;
using UnityEditor.Animations;
using VRC.SDK3.Avatars.Components;
using System;
using System.Collections.Generic;
using System.Linq;

namespace d4rkpl4y3r.AV3ToggleUtil.Util
{
    public static class AV3Helper
    {
        public static void SelectAndPingObject(UnityEngine.Object obj)
        {
            Selection.activeObject = obj;
            EditorGUIUtility.PingObject(obj);
        }

        public static void CollectClipsFromMotion(Motion motion, HashSet<AnimationClip> clips, HashSet<BlendTree> visitedTrees = null)
        {
            if (motion == null || clips == null)
                return;

            if (motion is AnimationClip clip)
            {
                clips.Add(clip);
                return;
            }

            if (motion is BlendTree tree)
            {
                if (visitedTrees == null) visitedTrees = new HashSet<BlendTree>();
                if (!visitedTrees.Add(tree)) return;

                var children = tree.children;
                for (int i = 0; i < children.Length; i++)
                {
                    CollectClipsFromMotion(children[i].motion, clips, visitedTrees);
                }
            }
        }

        public static void CollectComponentBindings(IEnumerable<AnimationClip> clips, VRCAvatarDescriptor av, Dictionary<Component, HashSet<string>> componentPropertyMap)
        {
            if (av == null || av.transform == null)
                return;

            foreach (var clip in clips)
            {
                if (clip == null) continue;
                foreach (var binding in AnimationUtility.GetCurveBindings(clip).Concat(AnimationUtility.GetObjectReferenceCurveBindings(clip)))
                {
                    Transform t = FindTransformByAvatarPath(av.transform, binding.path);
                    if (t == null) continue;
                    Component comp = null;
                    if (binding.type == typeof(Transform) || binding.type == typeof(GameObject))
                        comp = t;
                    else if (typeof(Component).IsAssignableFrom(binding.type))
                        comp = t.GetComponent(binding.type);
                    if (comp != null)
                    {
                        if (!componentPropertyMap.TryGetValue(comp, out var props))
                        {
                            props = new HashSet<string>();
                            componentPropertyMap[comp] = props;
                        }
                        props.Add(binding.propertyName);
                    }
                }
            }
        }

        public static HashSet<string> MergePropertyNames(HashSet<string> propertyNames)
        {
            var merged = new HashSet<string>();
            var groups = propertyNames
                .Where(p => !string.IsNullOrEmpty(p))
                .GroupBy(p =>
                {
                    int lastDot = p.LastIndexOf('.');
                    if (lastDot > 0 && lastDot < p.Length - 1)
                    {
                        string suffix = p[(lastDot + 1)..];
                        if (suffix.Length == 1)
                            return p[..lastDot];
                    }
                    return p;
                })
                .ToList();

            foreach (var group in groups)
            {
                var first = group.First();
                int lastDot = first.LastIndexOf('.');
                if (lastDot > 0 && lastDot < first.Length - 1)
                {
                    string suffix = first[(lastDot + 1)..];
                    if (suffix.Length == 1 && group.Count() > 1)
                    {
                        string prefix = first[..(lastDot + 1)];
                        var componentLetters = group.Select(p => p[(lastDot + 1)..]).ToList();
                        var letterSet = new HashSet<string>(componentLetters);
                        string orderKey = null;
                        if (letterSet.SetEquals(new[] { "r", "g", "b", "a" }))
                            orderKey = "rgba";
                        else if (letterSet.SetEquals(new[] { "x", "y", "z", "w" }))
                            orderKey = "xyzw";
                        var letters = orderKey != null
                            ? componentLetters.OrderBy(s => orderKey.IndexOf(s[0])).ToList()
                            : componentLetters.OrderBy(s => s).ToList();
                        merged.Add(prefix + string.Concat(letters));
                        continue;
                    }
                }
                foreach (var name in group)
                    merged.Add(name);
            }
            return merged;
        }

        public static bool DrawComponentBindingsSection(
            Dictionary<Component, HashSet<string>> componentPropertyMap,
            ref bool showComponentProperties,
            ColumnGrid columnGrid,
            float entryWidth,
            int componentCountLabel = -1)
        {
            if (componentPropertyMap == null || componentPropertyMap.Count == 0)
                return false;

            EditorGUILayout.Space(8);
            using (new EditorGUILayout.VerticalScope("box"))
            {
                var title = "Components Bound in Animation Clips"
                    + (componentCountLabel >= 0 ? $" ({componentCountLabel})" : "");
                EditorGUILayout.LabelField(title, EditorStyles.boldLabel);
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Space(ColumnGrid.InnerIndent);
                    showComponentProperties = GUILayout.Toggle(showComponentProperties, "Show Properties", GUILayout.Width(120f));
                }
                var components = componentPropertyMap.Keys.ToList();
                components.Sort((a, b) => string.Compare(a.name, b.name, StringComparison.OrdinalIgnoreCase));
                if (showComponentProperties)
                {
                    foreach (var comp in components)
                    {
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            GUILayout.Space(ColumnGrid.InnerIndent);
                            EditorGUILayout.ObjectField("", comp, comp.GetType(), true, GUILayout.Width(entryWidth));
                        }
                        var props = componentPropertyMap[comp];
                        var merged = MergePropertyNames(props);
                        foreach (var prop in merged.OrderBy(p => p))
                        {
                            using (new EditorGUILayout.HorizontalScope())
                            {
                                GUILayout.Space(ColumnGrid.InnerIndent + 15);
                                EditorGUILayout.LabelField(prop, EditorStyles.label);
                            }
                        }
                    }
                }
                else
                {
                    columnGrid.DrawEntries(components, comp =>
                    {
                        var props = componentPropertyMap[comp];
                        var merged = MergePropertyNames(props);
                        var tooltip = string.Join(", ", merged.OrderBy(p => p));
                        EditorGUILayout.ObjectField("", comp, comp.GetType(), true, GUILayout.Width(entryWidth));
                        EditorGUI.LabelField(GUILayoutUtility.GetLastRect(), new GUIContent("", tooltip));
                    });
                }
            }
            return true;
        }

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