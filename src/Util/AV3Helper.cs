#if UNITY_EDITOR
using UnityEngine;
using UnityEditor;
using UnityEditor.Animations;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
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

        /// <summary>
        /// Formats a parameter's declared value type, appending the signedness-prefixed bit width
        /// in parentheses for Int and Float parameters, e.g. "Int (u8)", "Float (s4)", "Bool".
        /// </summary>
        public static string GetValueTypeDisplayString(VRCExpressionParameters.Parameter parameter)
        {
            if (parameter == null)
                return "Unknown";

            var valueType = parameter.valueType;
            switch (valueType)
            {
                case VRCExpressionParameters.ValueType.Bool:
                    return "Bool";
                case VRCExpressionParameters.ValueType.Int:
                case VRCExpressionParameters.ValueType.Float:
                    bool isSigned = GetParameterRepresentation(valueType, parameter.flippedRepresentation)
                        == VRCExpressionParameters.Parameter.ValueRepresentation.Signed;
                    var bitWidth = Mathf.Clamp(parameter.bitWidth, (int)VRCExpressionParameters.Parameter.MinBitWidth, (int)VRCExpressionParameters.Parameter.MaxBitWidth);
                    return $"{valueType} ({(isSigned ? "s" : "u")}{bitWidth})";
                default:
                    return valueType.ToString();
            }
        }

        /// <summary>
        /// Resolves a parameter's value representation to signed/unsigned.
        /// Float defaults to Signed, Int defaults to Unsigned; flippedRepresentation inverts the default.
        /// </summary>
        public static VRCExpressionParameters.Parameter.ValueRepresentation GetParameterRepresentation(
            VRCExpressionParameters.ValueType valueType, bool flipped)
        {
            bool isSigned = valueType switch
            {
                VRCExpressionParameters.ValueType.Float => !flipped,
                VRCExpressionParameters.ValueType.Int => flipped,
                _ => false // Bool is always unsigned
            };
            return isSigned
                ? VRCExpressionParameters.Parameter.ValueRepresentation.Signed
                : VRCExpressionParameters.Parameter.ValueRepresentation.Unsigned;
        }

        public static void ForEachClipInMotion(Motion motion, Action<AnimationClip> onClip, HashSet<BlendTree> visitedTrees = null)
        {
            if (motion == null) return;

            if (motion is AnimationClip clip)
            {
                onClip(clip);
                return;
            }

            if (motion is BlendTree tree)
            {
                if (visitedTrees == null) visitedTrees = new HashSet<BlendTree>();
                if (!visitedTrees.Add(tree)) return;

                var children = tree.children;
                for (int i = 0; i < children.Length; i++)
                {
                    ForEachClipInMotion(children[i].motion, onClip, visitedTrees);
                }
            }
        }

        public static AnimatorController GetFxAnimatorController(VRCAvatarDescriptor av)
        {
            if (av == null)
                return null;
            var baseLayerCount = GetBaseLayerCount(av);
            if (av.baseAnimationLayers == null || av.baseAnimationLayers.Length != baseLayerCount)
                return null;
            return av.baseAnimationLayers[baseLayerCount - 1].animatorController as AnimatorController;
        }

        private static int GetBaseLayerCount(VRCAvatarDescriptor av)
        {
            if (!av.TryGetComponent<Animator>(out var animator) || animator == null)
                return 5;
            if (animator.avatar == null || !animator.avatar.isHuman)
                return 3;
            return 5;
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
            Transform avatarRoot,
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
                        var path = avatarRoot != null && comp.transform != avatarRoot
                            ? AnimationUtility.CalculateTransformPath(comp.transform, avatarRoot)
                            : string.Empty;
                        var compTypeName = comp.GetType().Name;
                        foreach (var prop in merged.OrderBy(p => p))
                        {
                            var typeName = compTypeName;
                            if (typeName == nameof(Transform) && prop == "m_IsActive")
                                typeName = nameof(GameObject);
                            var filterProp = prop;
                            var lastDot = prop.LastIndexOf('.');
                            if (lastDot > 0)
                            {
                                var suffix = prop[(lastDot + 1)..];
                                if (suffix.Length > 1 && suffix.All(c => "xyzwrgba".Contains(c)))
                                    filterProp = prop[..lastDot];
                            }
                            using (new EditorGUILayout.HorizontalScope())
                            {
                                GUILayout.Space(ColumnGrid.InnerIndent + 15);
                                EditorGUILayout.LabelField(new GUIContent(prop, "Click to open in Animation Utility"), EditorStyles.label);
                                if (ClickableLastRect())
                                    d4rkAV3AnimationUtilMenu.OpenWithFilters(path, filterProp, typeName);
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

        public static VRCAvatarDescriptor GetCurrentOrLastAvatarDescriptor(ref VRCAvatarDescriptor lastFound)
        {
            var selected = FindAvatarDescriptor(Selection.activeGameObject);
            if (selected != null)
                lastFound = selected;
            return lastFound;
        }
    }
}
#endif