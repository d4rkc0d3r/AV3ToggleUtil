#if UNITY_EDITOR
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEditor;
using UnityEditor.Animations;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;

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

        private static readonly HashSet<string> knownToggleProperties = new() {
            "m_IsActive", "m_Enabled",
            // SkinnedMeshRenderer
            "m_UpdateWhenOffscreen",
            "m_ReceiveShadows",
            "m_SkinnedMotionVectors",
        };

        /// <summary>Whether a binding animates a boolean-ish property (0/1 float, bool field, or known toggle property).</summary>
        public static bool IsBoolBinding(EditorCurveBinding binding)
        {
            if (knownToggleProperties.Contains(binding.propertyName))
                return true;
            return binding.type.GetField(binding.propertyName,
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                ?.FieldType == typeof(bool);
        }

        private const string materialSlotPropertyPrefix = "m_Materials.Array.data[";

        /// <summary>Whether a binding swaps a material in one of a (skinned mesh) renderer's material slots
        /// (the property name is of the form <c>m_Materials.Array.data[i]</c> where i is the slot index).</summary>
        public static bool IsMaterialSwapBinding(EditorCurveBinding binding)
        {
            if (binding.type == null || !typeof(Renderer).IsAssignableFrom(binding.type))
                return false;

            // property format: m_Materials.Array.data[<int>]
            var propertyName = binding.propertyName;
            if (!propertyName.StartsWith(materialSlotPropertyPrefix, StringComparison.Ordinal))
                return false;
            int closeIndex = propertyName.IndexOf(']');
            if (closeIndex < materialSlotPropertyPrefix.Length || closeIndex < propertyName.Length - 1)
                return false;
            return int.TryParse(propertyName[materialSlotPropertyPrefix.Length..closeIndex], out _);
        }

        private const string constraintSourcePropertyPrefix = "Sources.source";

        /// <summary>Whether a binding's property targets a slot of a VRC constraint's source list (a
        /// property of the form <c>Sources.source[i].<something></c>), returning the slot index when it does.</summary>
        public static bool TryGetConstraintSourceSlot(System.Type bindType, string propertyName, out int sourceIndex)
        {
            sourceIndex = -1;
            if (bindType == null || !typeof(VRC.Dynamics.VRCConstraintBase).IsAssignableFrom(bindType))
                return false;
            if (!propertyName.StartsWith(constraintSourcePropertyPrefix, StringComparison.Ordinal))
                return false;
            // the first '.' in the name is the one inside the prefix itself ("Sources.source<N>"),
            // so search for the separator after "source<N>" (i.e. before the sub-property)
            int dotIndex = propertyName.IndexOf('.', constraintSourcePropertyPrefix.Length);
            if (dotIndex < 0)
                return false;
            if (!int.TryParse(propertyName[constraintSourcePropertyPrefix.Length..dotIndex], out sourceIndex))
                return false;
            return sourceIndex >= 0;
        }

        /// <summary>The root-relative path of a transform (<c>(root)</c> for the root itself), or null when
        /// either transform is null or the transform is not under the root.
        /// (For binding paths - where empty string means root - use AnimationUtility.CalculateTransformPath directly.)</summary>
        public static string GetTransformPath(Transform root, Transform transform)
        {
            if (root == null || transform == null)
                return null;
            var path = AnimationUtility.CalculateTransformPath(transform, root);
            return string.IsNullOrEmpty(path) ? "(root)" : path;
        }

        /// <summary>The transform sitting in the given slot of the constraint's source list, or null when
        /// the slot has no transform (including stale slot indices that exceed the current source count).</summary>
        public static Transform GetConstraintSourceTransform(VRC.Dynamics.VRCConstraintBase constraint, int sourceIndex)
        {
            if (constraint == null || sourceIndex >= constraint.SourceCount)
                return null;
            return constraint.GetSource(sourceIndex).SourceTransform;
        }

        /// <summary>
        /// The root of the git repo containing the given file or folder (the nearest folder with a .git entry,
        /// walking up the parents), or null when it is not inside one. When given, the scan never goes above
        /// <paramref name="stopRoot"/>.
        /// </summary>
        public static string GetGitRepoRoot(string path, string stopRoot = null)
        {
            if (string.IsNullOrEmpty(path))
                return null;
            // start at the folder containing the file (the path may be a file itself)
            var directory = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(path)) ?? Path.GetPathRoot(Path.GetFullPath(path)));
            var stop = string.IsNullOrEmpty(stopRoot) ? null : Path.GetFullPath(stopRoot);
            while (directory != null)
            {
                if (directory.EnumerateFileSystemInfos(".git").Any())
                    return directory.FullName;
                if (stop != null && string.Equals(directory.FullName, stop, StringComparison.OrdinalIgnoreCase))
                    return null;
                directory = directory.Parent;
            }
            return null;
        }

        public static string ProjectRootPath()
        {
            return Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath;
        }

        /// <summary>
        /// The project-root-relative path (Assets/... or Packages/...) of an asset path plus the root of
        /// the git repo it lives in (never above the project root), null when it isn't in one.
        /// </summary>
        public static (string assetPath, string repoRoot) ResolveAssetRepoInfo(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath))
                return (string.Empty, null);
            if (!assetPath.StartsWith("Assets/", StringComparison.Ordinal) && !assetPath.StartsWith("Packages/", StringComparison.Ordinal))
                assetPath = "Assets/" + assetPath;
            var projectRoot = ProjectRootPath();
            return (assetPath, GetGitRepoRoot(Path.Combine(projectRoot, assetPath), projectRoot));
        }

        /// <summary>The animation assets relevant for git tracking of an avatar: controllers, expression parameters &amp; expressions menu.</summary>
        public static List<(string displayName, string assetPath, string repoRoot)> GetAvatarAnimationGitAssets(VRCAvatarDescriptor av, IEnumerable<AnimatorController> controllers)
        {
            var results = new List<(string displayName, string assetPath, string repoRoot)>();
            if (av == null)
                return results;

            void Add(string displayName, string assetPath)
            {
                if (string.IsNullOrEmpty(assetPath) || results.Any(r => r.assetPath == assetPath))
                    return;
                var (path, repoRoot) = ResolveAssetRepoInfo(assetPath);
                results.Add((displayName, path, repoRoot));
            }

            if (controllers != null)
                foreach (var controller in controllers)
                    if (controller != null)
                        Add(controller.name, AssetDatabase.GetAssetPath(controller));
            Add(av.expressionParameters?.name, AssetDatabase.GetAssetPath(av.expressionParameters));
            Add(av.expressionsMenu?.name, AssetDatabase.GetAssetPath(av.expressionsMenu));
            return results;
        }

        /// <summary>Tooltip message & severity for the git repo (if any) the given assets live in. Empty message when there are none.</summary>
        public static (string message, bool isWarning) GetGitRepoStatusMessage(IReadOnlyList<(string displayName, string assetPath, string repoRoot)> assets)
        {
            if (assets == null || assets.Count == 0)
                return (string.Empty, false);
            var projectRoot = ProjectRootPath();
            // repos rooted at the project root keep their full path, anything else is shown relative to it
            string RepoLabel(string repoRoot)
            {
                if (string.Equals(repoRoot, projectRoot, StringComparison.OrdinalIgnoreCase))
                    return repoRoot;
                return repoRoot.Length > projectRoot.Length && repoRoot.StartsWith(projectRoot, StringComparison.OrdinalIgnoreCase)
                    ? repoRoot.Substring(projectRoot.Length).TrimStart('\\', '/')
                    : repoRoot;
            }

            var tracked = assets.Where(a => a.repoRoot != null).ToList();
            if (tracked.Count == assets.Count)
            {
                if (tracked.Select(a => a.repoRoot).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1)
                    return ("In git repo: " + RepoLabel(tracked[0].repoRoot), false);
                return ("Assets are spread across multiple git repos:\n" +
                    string.Join("\n", assets.Select(a => $"- {a.displayName}: {RepoLabel(a.repoRoot)}")), true);
            }

            var untracked = assets.Where(a => a.repoRoot == null).ToList();
            return ("Assets not in a git repo:\n" +
                string.Join("\n", untracked.Select(a => $"- {a.displayName}")), true);
        }

        /// <summary>An inline "Git Status" label with an info/warning icon; the repo details go into its tooltip.</summary>
        public static void DrawGitStatusLabel(IReadOnlyList<(string displayName, string assetPath, string repoRoot)> assets)
        {
            var (message, isWarning) = GetGitRepoStatusMessage(assets);
            if (string.IsNullOrEmpty(message))
                return;
            var icon = EditorGUIUtility.IconContent(isWarning ? "console.warnicon.sml" : "console.infoicon.sml");
            GUILayout.Label(new GUIContent("Git Status", icon.image, message), GUILayout.ExpandWidth(false));
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
                                    d4rkAV3AnimationUtilMenu.OpenWithFilters(path, filterProp, typeName, avatarRoot);
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

        /// <summary>
        /// Initializes a cached "last found" descriptor: first checks the current selection (without
        /// clearing a previously found descriptor), and only falls back to scanning enabled top-level
        /// scene objects when nothing is selected and no descriptor was found before.
        /// Designed for window menu items; OnGUI should keep using <see cref="GetCurrentOrLastAvatarDescriptor"/>.
        /// </summary>
        public static VRCAvatarDescriptor InitializeLatestAvatarDescriptor(ref VRCAvatarDescriptor lastFound)
        {
            var selected = FindAvatarDescriptor(Selection.activeGameObject);
            if (selected != null)
            {
                lastFound = selected;
                return lastFound;
            }

            if (lastFound == null)
                lastFound = SearchAvatarDescriptorInScene();

            return lastFound;
        }

        public static VRCAvatarDescriptor SearchAvatarDescriptorInScene()
        {
            var scene = SceneManager.GetActiveScene();
            if (!scene.IsValid())
                return null;

            var rootGameObjects = scene.GetRootGameObjects();
            for (int i = 0; i < rootGameObjects.Length; i++)
            {
                var root = rootGameObjects[i];
                if (root == null || !root.activeSelf)
                    continue;
                if (root.TryGetComponent(out VRCAvatarDescriptor descriptor))
                    return descriptor;
            }
            return null;
        }
    }
}
#endif