#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEditor;
using VRC.SDK3.Avatars.Components;
using UnityEditor.Animations;
using System;
using System.Text.RegularExpressions;

public class d4rkAV3AnimationUtilMenu : EditorWindow
{
    private enum SelectionMode
    {
        Search,
        SelectionBindings
    }

    public class TextFilter
    {
        private string text = "";
        private bool isRegex = false;
        private bool isCaseSensitive = false;
        private bool invert = false;

        public string Text
        {
            get => text;
            set
            {
                if (text != value)
                {
                    text = value;
                    matchCache.Clear();
                }
            }
        }

        private readonly Dictionary<string, bool> matchCache = new();

        public bool Matches(string input)
        {
            if (string.IsNullOrEmpty(text))
                return true;
            input ??= "";
            if (matchCache.TryGetValue(input, out var cached))
                return cached;
            if (isRegex)
            {
                try
                {
                    var regex = new Regex(text, isCaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase);
                    bool isMatch = input != null && regex.IsMatch(input);
                    return matchCache[input] = isMatch ^ invert;
                }
                catch (Exception)
                {
                    return matchCache[input] = true;
                }
            }
            var comparison = isCaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            bool contains = input != null && input.IndexOf(text, comparison) >= 0;
            return matchCache[input] = contains ^ invert;
        }

        public void DrawGUI(string label)
        {
            using var _ = new EditorGUILayout.HorizontalScope();
            using var cc = new EditorGUI.ChangeCheckScope();

            string regexError = null;
            if (isRegex && !string.IsNullOrEmpty(text))
            {
                try { var regex = new Regex(text, isCaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase); }
                catch (Exception ex) { regexError = ex.Message; }
            }

            var prevColor = GUI.contentColor;
            if (regexError != null)
                GUI.contentColor = new Color(1f, 1f, 0.3f);

            var rect = EditorGUILayout.GetControlRect();
            text = EditorGUI.TextField(rect, new GUIContent(label), text);
            if (regexError != null)
                GUI.Label(rect, new GUIContent("", regexError));

            GUI.contentColor = prevColor;

            isRegex = GUILayout.Toggle(isRegex, "Regex", GUI.skin.button, GUILayout.ExpandWidth(false));
            isCaseSensitive = GUILayout.Toggle(isCaseSensitive, "Case", GUI.skin.button, GUILayout.ExpandWidth(false));
            invert = GUILayout.Toggle(invert, "Invert", GUI.skin.button, GUILayout.ExpandWidth(false));

            if (cc.changed)
            {
                matchCache.Clear();
            }
        }
    }

    private VRCAvatarDescriptor avatarDescriptor = null;
    private VRCAvatarDescriptor AvatarDescriptor
    {
        get
        {
            if (Selection.activeGameObject != null)
            {
                var d = FindAvatarDescriptor(Selection.activeGameObject);
                if (d != avatarDescriptor)
                {
                    ClearCaches();
                }
                avatarDescriptor = d;
            }
            if (avatarDescriptor != null)
                return avatarDescriptor;
            return null;
        }
        set { avatarDescriptor = value; }
    }

    private void ClearCaches()
    {
        animationClips = null;
        cachedAnimatableBindings.Clear();
    }

    private List<AnimationClip> animationClips = null;
    private List<AnimationClip> AnimationClips
    {
        get
        {
            if (animationClips != null)
                return animationClips;
            if (AvatarDescriptor == null)
                return null;
            var clips = AvatarDescriptor.baseAnimationLayers.SelectMany(layer =>
            {
                var controller = layer.animatorController as AnimatorController;
                if (controller == null)
                    return new AnimationClip[0];
                return controller.animationClips;
            }).Distinct().ToList();
            clips.AddRange(AvatarDescriptor.specialAnimationLayers.SelectMany(layer =>
            {
                var controller = layer.animatorController as AnimatorController;
                if (controller == null)
                    return new AnimationClip[0];
                return controller.animationClips;
            }).Distinct().ToList());
            return animationClips = clips.Distinct().ToList();
        }
    }

    private Vector2 scrollPos;
    private EditorCurveBinding? selectedSourceBinding = null;
    private List<EditorCurveBinding> selectedTargetBindings = new();
    private string bindingFilter = "";
    private bool showMaterialBindings = false;
    private bool showBlendShapeBindings = false;
    private bool showAllBindings = false;
    private SelectionMode selectionMode = SelectionMode.Search;
    private bool filterBySelection = true;
    private TextFilter searchBindingPathFilter = new();
    private TextFilter searchBindingPropertyFilter = new();
    private TextFilter searchBindingTypeFilter = new();
    private Dictionary<int, bool> clipShowFilteredBindings = new();

    private bool GetClipShowBindings(AnimationClip clip)
    {
        if (clip == null)
            return false;
        if (!clipShowFilteredBindings.TryGetValue(clip.GetInstanceID(), out var value))
        {
            clipShowFilteredBindings[clip.GetInstanceID()] = value = filterBySelection;
        }
        return value;
    }

    private void SetClipShowBindings(AnimationClip clip, bool value)
    {
        if (clip == null)
            return;
        clipShowFilteredBindings[clip.GetInstanceID()] = value;
    }

    private Dictionary<GameObject, Dictionary<Type, List<EditorCurveBinding>>> cachedAnimatableBindings = new();
    private Dictionary<Type, bool> typeFoldoutStates = new();

    private Dictionary<Type, List<EditorCurveBinding>> GetAnimatableBindingsOnGameObject(GameObject gameObject) {
        if (cachedAnimatableBindings.TryGetValue(gameObject, out var bindings))
            return bindings;
        bindings = new Dictionary<Type, List<EditorCurveBinding>>();
        if (AvatarDescriptor == null)
            return bindings;
        foreach (var animatableBinding in AnimationUtility.GetAnimatableBindings(gameObject, AvatarDescriptor.gameObject))
        {
            if (!showMaterialBindings && animatableBinding.propertyName.StartsWith("material."))
                continue;
            if (!showBlendShapeBindings && animatableBinding.propertyName.StartsWith("blendShape."))
                continue;
            if (string.IsNullOrWhiteSpace(bindingFilter) == false &&
                animatableBinding.propertyName.IndexOf(bindingFilter, StringComparison.OrdinalIgnoreCase) < 0)
            {
                continue;
            }
            if (!bindings.TryGetValue(animatableBinding.type, out var list))
            {
                list = new List<EditorCurveBinding>();
                bindings[animatableBinding.type] = list;
            }
            if (animatableBinding.propertyName == "m_Enabled")
                list.Insert(0, animatableBinding);
            else if (animatableBinding.propertyName == "m_IsActive")
                list.Insert(0, animatableBinding);
            else
                list.Add(animatableBinding);
        }
        return cachedAnimatableBindings[gameObject] = bindings;
    }

    private void ClickablePathLabel(string path, params GUILayoutOption[] options)
    {
        var root = AvatarDescriptor?.gameObject?.transform;
        GUILayout.Label(string.IsNullOrEmpty(path) ? "(root)" : path, options);
        var t = root != null && !string.IsNullOrEmpty(path) ? root.Find(path) : root;
        if (t != null)
        {
            EditorGUIUtility.AddCursorRect(GUILayoutUtility.GetLastRect(), MouseCursor.Link);
            if (Event.current.type == EventType.MouseDown && GUILayoutUtility.GetLastRect().Contains(Event.current.mousePosition) && Event.current.button == 0)
            {
                Selection.activeGameObject = t.gameObject;
                EditorGUIUtility.PingObject(t.gameObject);
                Event.current.Use();
            }
        }
    }

    private bool IsSelectedSource(EditorCurveBinding b) =>
        selectedSourceBinding != null && EqualBinding(selectedSourceBinding.Value, b);

    private bool IsTargetBinding(EditorCurveBinding b) =>
        selectedTargetBindings.Any(t => EqualBinding(t, b));

    private void RemoveTargetBinding(EditorCurveBinding b) =>
        selectedTargetBindings.RemoveAll(t => EqualBinding(t, b));

    private EditorCurveBinding BuildBindingForGameObject(Transform root, GameObject go, Type type, string propertyName, bool isPPtr)
    {
        var path = go.transform == root ? string.Empty : AnimationUtility.CalculateTransformPath(go.transform, root);
        return isPPtr
            ? EditorCurveBinding.PPtrCurve(path, type, propertyName)
            : EditorCurveBinding.FloatCurve(path, type, propertyName);
    }

    private void DrawBindingsList(AnimationClip clip)
    {
        var bindings = GetBindingsMatchingSearchFilters(clip);
        foreach (var item in bindings)
        {
            if (!showAllBindings && !item.matched)
                continue;

            var b = item.binding;
            var prop = $"{(b.type != null ? b.type.Name : "Component")}.{b.propertyName}";

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Space(15 * EditorGUI.indentLevel);

                var isSelected = IsSelectedSource(b);
                using (var cc = new EditorGUI.ChangeCheckScope())
                {
                    var newSelected = GUILayout.Toggle(isSelected, "S", GUI.skin.button, GUILayout.Width(20));
                    if (cc.changed)
                        selectedSourceBinding = newSelected ? b : null;
                }

                DrawCurveValues(clip, b, GUILayout.Width(90));

                var prevColor = GUI.contentColor;
                if (!item.matched)
                    GUI.contentColor = new Color(1f, 0.85f, 0.3f);

                ClickablePathLabel(b.path, GUILayout.ExpandWidth(true));
                GUILayout.Label(prop, GUILayout.ExpandWidth(true));
                GUILayout.FlexibleSpace();

                GUI.contentColor = prevColor;
            }
        }
    }

    private HashSet<string> cachedSelectionPathsUnderAvatar = null;

    // Return transform paths (relative to avatar root) of the current selection that are under the avatar
    private HashSet<string> GetSelectionPathsUnderAvatar()
    {
        if (cachedSelectionPathsUnderAvatar != null)
            return cachedSelectionPathsUnderAvatar;

        cachedSelectionPathsUnderAvatar = new HashSet<string>();
        var root = AvatarDescriptor?.gameObject?.transform;
        if (root == null) return cachedSelectionPathsUnderAvatar;

        foreach (var go in Selection.gameObjects)
        {
            if (go == null) continue;
            var t = go.transform;
            if (t == root)
            {
                cachedSelectionPathsUnderAvatar.Add(string.Empty); // root path
            }
            else if (t.IsChildOf(root))
            {
                cachedSelectionPathsUnderAvatar.Add(AnimationUtility.CalculateTransformPath(t, root));
            }
        }
        return cachedSelectionPathsUnderAvatar;
    }

    private IEnumerable<GameObject> GetSelectedGameObjectsUnderAvatar()
    {
        var root = AvatarDescriptor?.gameObject?.transform;
        if (root == null) yield break;

        foreach (var go in Selection.gameObjects)
        {
            if (go == null) continue;
            var t = go.transform;
            if (t == root || t.IsChildOf(root))
                yield return go;
        }
    }

    private void DrawCurveValues(AnimationClip clip, EditorCurveBinding binding, params GUILayoutOption[] options)
    {
        var curve = AnimationUtility.GetEditorCurve(clip, binding);
        string text = null;
        if (curve == null)
        {
            var objKeys = AnimationUtility.GetObjectReferenceCurve(clip, binding);
            if (objKeys != null && objKeys.Length != 0)
            {
                var objects = objKeys.Select(k => k.value).Distinct().ToArray();
                string tooltip = "";
                if (objects.Length > 1)
                {
                    tooltip = string.Join("\n", objects.Select(o => o == null ? "null" : o.name));
                    EditorGUI.showMixedValue = true;
                }
                var type = objects.Length == 0 || objects[0] == null ? typeof(UnityEngine.Object) : objects[0].GetType();
                var prevIndent = EditorGUI.indentLevel;
                EditorGUI.indentLevel = 0;
                var rect = EditorGUILayout.GetControlRect(options);
                EditorGUI.ObjectField(rect, objects.Length >= 1 ? objects[0] : null, type, false);
                GUI.Label(rect, new GUIContent("", tooltip));
                EditorGUI.indentLevel = prevIndent;
                EditorGUI.showMixedValue = false;
                return;
            }
            text = "empty";
        }
        if (curve != null && (curve.keys == null || curve.keys.Length == 0))
            text = "empty";
        if (text == null)
        {
            float min = curve.keys[0].value;
            float max = min;
            for (int i = 1; i < curve.keys.Length; i++)
            {
                var v = curve.keys[i].value;
                if (v < min) min = v;
                if (v > max) max = v;
            }
            text = Mathf.Approximately(min, max) ? min.ToString("0.###") : $"[{min:0.###}..{max:0.###}]";
        }
        GUILayout.Label(text, options);
    }

    private TEnum EnumMultiButton<TEnum>(TEnum currentValue, bool expand = true)
    {
        var names = Enum.GetNames(typeof(TEnum));
        var values = Enum.GetValues(typeof(TEnum)).Cast<int>().ToArray();
        int currentIndex = Array.IndexOf(values, Convert.ToInt32(currentValue));
        for (int i = 0; i < names.Length; i++)
        {
            using var cc = new EditorGUI.ChangeCheckScope();
            bool newSelected = GUILayout.Toggle(i == currentIndex, names[i], GUI.skin.button, GUILayout.ExpandWidth(expand));
            if (cc.changed && newSelected)
            {
                currentIndex = i;
            }
        }
        return (TEnum)Enum.ToObject(typeof(TEnum), values[currentIndex]);
    }

    private bool EqualBinding(EditorCurveBinding a, EditorCurveBinding b) =>
        a.path == b.path && a.propertyName == b.propertyName && a.type == b.type && a.isPPtrCurve == b.isPPtrCurve;

    private bool ClipHasBinding(AnimationClip clip, EditorCurveBinding binding)
    {
        if (binding.isPPtrCurve)
            return AnimationUtility.GetObjectReferenceCurveBindings(clip).Any(b => EqualBinding(b, binding));
        return AnimationUtility.GetCurveBindings(clip).Any(b => EqualBinding(b, binding));
    }

    private void CopySourceCurveToTargetBindings()
    {
        if (selectedSourceBinding == null || selectedTargetBindings.Count == 0 || AnimationClips == null)
            return;

        var source = selectedSourceBinding.Value;
        var clipsWithSource = AnimationClips.Where(c => c != null && ClipHasBinding(c, source)).ToList();

        if (clipsWithSource.Count == 0)
        {
            EditorUtility.DisplayDialog("Copy Binding", "No animation clips contain the source binding.", "OK");
            return;
        }

        // Build confirmation message
        var msg = $"The following {clipsWithSource.Count} animation clip(s) will be modified:\n";
        foreach (var clip in clipsWithSource)
        {
            msg += "- " + clip.name + "\n";
            if (msg.Length > 1500) { msg += "... (truncated)\n"; break; }
        }
        msg += "\nProceed with copying the source binding curve/keyframes to all target bindings in these clips?";

        if (!EditorUtility.DisplayDialog("Confirm Copy", msg, "Proceed", "Cancel"))
            return;

        int curvesCopied = 0;
        foreach (var clip in clipsWithSource)
        {
            if (clip == null) continue;
            Undo.RecordObject(clip, "Copy Binding Curves");

            if (source.isPPtrCurve)
            {
                var srcKeys = AnimationUtility.GetObjectReferenceCurve(clip, source);
                if (srcKeys == null || srcKeys.Length == 0)
                    continue;

                foreach (var target in selectedTargetBindings)
                {
                    // Skip if identical binding (already exists)
                    if (EqualBinding(source, target)) continue;
                    AnimationUtility.SetObjectReferenceCurve(clip, target, srcKeys);
                    curvesCopied++;
                }
            }
            else
            {
                var srcCurve = AnimationUtility.GetEditorCurve(clip, source);
                if (srcCurve == null || srcCurve.keys == null || srcCurve.keys.Length == 0)
                    continue;

                foreach (var target in selectedTargetBindings)
                {
                    if (EqualBinding(source, target)) continue;
                    // Duplicate curve
                    var newCurve = new AnimationCurve(srcCurve.keys)
                    {
                        preWrapMode = srcCurve.preWrapMode,
                        postWrapMode = srcCurve.postWrapMode
                    };
                    AnimationUtility.SetEditorCurve(clip, target, newCurve);
                    curvesCopied++;
                }
            }

            EditorUtility.SetDirty(clip);
        }

        AssetDatabase.SaveAssets();
    }

    void OnGUI()
    {
        using var scrollView = new EditorGUILayout.ScrollViewScope(scrollPos);
        scrollPos = scrollView.scrollPosition;

        AvatarDescriptor = EditorGUILayout.ObjectField("Avatar Descriptor", AvatarDescriptor, typeof(VRCAvatarDescriptor), true) as VRCAvatarDescriptor;

        if (AvatarDescriptor == null)
        {
            EditorGUILayout.HelpBox("No VRC Avatar Descriptor found.", MessageType.Warning);
            return;
        }

        EditorGUILayout.Space();

        using (new EditorGUILayout.HorizontalScope())
        {
            selectionMode = EnumMultiButton(selectionMode, expand:true);
        }

        GUILayout.Space(10);
        using (new EditorGUILayout.VerticalScope(GUI.skin.box))
        {
            GUILayout.Label("Source Binding:", EditorStyles.boldLabel);
            using (new EditorGUI.IndentLevelScope())
            {
                if (selectedSourceBinding != null)
                {
                    var path = selectedSourceBinding.Value.path;
                    var prop = $"{(selectedSourceBinding.Value.type != null ? selectedSourceBinding.Value.type.Name : "Component")}.{selectedSourceBinding.Value.propertyName}";

                    using (new EditorGUILayout.HorizontalScope())
                    {
                        GUILayout.Space(15 * EditorGUI.indentLevel);
                        if (GUILayout.Button("-", GUILayout.Height(18), GUILayout.Width(18)))
                        {
                            selectedSourceBinding = null;
                        }
                        ClickablePathLabel(path, GUILayout.ExpandWidth(true));
                        GUILayout.Label(prop, GUILayout.ExpandWidth(true));
                        GUILayout.FlexibleSpace();
                    }
                }
                else
                {
                    EditorGUILayout.LabelField("(none)");
                }
            }
        }

        GUILayout.Space(10);
        using (new EditorGUILayout.VerticalScope(GUI.skin.box))
        {
            GUILayout.Label($"Target Bindings({selectedTargetBindings.Count}):", EditorStyles.boldLabel);
            using (new EditorGUI.IndentLevelScope())
            {
                for (int i = 0; i < selectedTargetBindings.Count; i++)
                {
                    var b = selectedTargetBindings[i];
                    var prop = $"{(b.type != null ? b.type.Name : "Component")}.{b.propertyName}";

                    using (new EditorGUILayout.HorizontalScope())
                    {
                        GUILayout.Space(15 * EditorGUI.indentLevel);
                        if (GUILayout.Button("-", GUILayout.Height(18), GUILayout.Width(18)))
                        {
                            selectedTargetBindings.RemoveAt(i);
                            i--;
                            continue;
                        }
                        ClickablePathLabel(b.path, GUILayout.ExpandWidth(true));
                        GUILayout.Label(prop, GUILayout.ExpandWidth(true));
                        GUILayout.FlexibleSpace();
                    }
                }
            }
        }

        GUILayout.Space(10);
        using (new EditorGUI.DisabledScope(selectedSourceBinding == null || selectedTargetBindings.Count == 0))
        {
            if (GUILayout.Button("Copy Source Curve to Target Bindings"))
            {
                CopySourceCurveToTargetBindings();
            }
        }

        if (selectionMode == SelectionMode.Search)
        {
            using var box = new EditorGUILayout.VerticalScope(GUI.skin.box);

            filterBySelection = EditorGUILayout.ToggleLeft("Filter by current selection", filterBySelection);

            if (filterBySelection)
                searchBindingPathFilter.Text = GetSelectionPathsUnderAvatar().FirstOrDefault() ?? "";

            using (new EditorGUI.DisabledScope(filterBySelection))
            {
                searchBindingPathFilter.DrawGUI("Binding Path Filter");
            }
            searchBindingPropertyFilter.DrawGUI("Binding Property Filter");
            searchBindingTypeFilter.DrawGUI("Binding Type Filter");

            var allClips = AnimationClips ?? new List<AnimationClip>();
            var filteredClips = allClips
                .Where(c => GetBindingsMatchingSearchFilters(c).Any(x => x.matched))
                .ToList();

            bool allOn = filteredClips.Count > 0 && filteredClips.All(GetClipShowBindings);
            bool allOff = filteredClips.Count == 0 || filteredClips.All(c => !GetClipShowBindings(c));
            bool mixed = !(allOn || allOff);

            EditorGUI.showMixedValue = mixed;
            using (var cc = new EditorGUI.ChangeCheckScope())
            {
                bool masterValue = EditorGUILayout.ToggleLeft("Show bindings", allOn || mixed);
                if (cc.changed)
                {
                    foreach (var c in filteredClips)
                        SetClipShowBindings(c, masterValue);
                }
            }
            EditorGUI.showMixedValue = false;

            showAllBindings = EditorGUILayout.ToggleLeft("Show filtered out bindings as well", showAllBindings);
            GUILayout.Space(10);

            GUILayout.Label(
                $"Animation Clips matching filters ({filteredClips.Count}/{allClips.Count}):",
                EditorStyles.boldLabel
            );

            using (new EditorGUI.IndentLevelScope())
            {
                foreach (var clip in filteredClips)
                {
                    bool showClipBindings = GetClipShowBindings(clip);
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        GUILayout.Space(15 * EditorGUI.indentLevel);
                        var prevIndent = EditorGUI.indentLevel;
                        EditorGUI.indentLevel = 0;
                        EditorGUI.showMixedValue = showClipBindings && !showAllBindings
                            && GetBindingsMatchingSearchFilters(clip).Any(x => !x.matched);
                        using (var cc = new EditorGUI.ChangeCheckScope())
                        {
                            bool newShow = EditorGUILayout.Toggle(showClipBindings, GUILayout.Width(16));
                            if (cc.changed)
                                SetClipShowBindings(clip, newShow ^ EditorGUI.showMixedValue);
                        }
                        EditorGUI.showMixedValue = false;
                        EditorGUILayout.ObjectField(clip, typeof(AnimationClip), false);
                        EditorGUI.indentLevel = prevIndent;
                    }

                    if (GetClipShowBindings(clip))
                    {
                        using var _ = new EditorGUI.IndentLevelScope();
                        DrawBindingsList(clip);
                    }
                }
            }
        }

        if (selectionMode == SelectionMode.SelectionBindings)
        {
            using var box = new EditorGUILayout.VerticalScope(GUI.skin.box);

            // Binding filters
            using (var bindingFilters = new EditorGUI.ChangeCheckScope())
            {
                bindingFilter = EditorGUILayout.TextField("Binding Filter", bindingFilter);
                showMaterialBindings = EditorGUILayout.Toggle("Material", showMaterialBindings);
                showBlendShapeBindings = EditorGUILayout.Toggle("BlendShapes", showBlendShapeBindings);
                if (bindingFilters.changed)
                {
                    ClearCaches();
                }
            }

            // Available bindings for current selection
            GUILayout.Space(10);
            GUILayout.Label("Available Bindings for Selection:", EditorStyles.boldLabel);
            using (new EditorGUI.IndentLevelScope())
            {
                var selectedGOs = GetSelectedGameObjectsUnderAvatar().Distinct().ToArray();
                var common = GetCommonAvailableBindingSignatures(selectedGOs);
                if (selectedGOs.Length == 0)
                {
                    GUILayout.Label("(none under avatar)");
                }
                else if (common.Count == 0)
                {
                    GUILayout.Label("(no common bindings)");
                }
                else
                {
                    foreach ((var type, var sigs) in common.OrderBy(k => k.Key.Name))
                    {
                        if (!typeFoldoutStates.TryGetValue(type, out var open))
                            open = true;
                        open = EditorGUILayout.Foldout(open, $"{type.Name} ({sigs.Count})", true);
                        typeFoldoutStates[type] = open;
                        if (!open)
                            continue;
                        
                        using var indent = new EditorGUI.IndentLevelScope();
                        foreach (var sig in sigs)
                        {
                            using var horizontal = new EditorGUILayout.HorizontalScope();
                            ParseSignature(sig, out var prop, out var isPPtr);
                            GUILayout.Space(15 * EditorGUI.indentLevel);

                            using (new EditorGUI.DisabledScope(selectedGOs.Length != 1))
                            {
                                var root = AvatarDescriptor.gameObject.transform;
                                var singleBinding = selectedGOs.Length == 1
                                    ? BuildBindingForGameObject(root, selectedGOs[0], type, prop, isPPtr)
                                    : default;

                                var isSelected = selectedGOs.Length == 1 && IsSelectedSource(singleBinding);
                                using var cc = new EditorGUI.ChangeCheckScope();
                                var newSelected = GUILayout.Toggle(isSelected, "S", GUI.skin.button, GUILayout.Width(20));
                                if (cc.changed)
                                    selectedSourceBinding = newSelected ? singleBinding : null;
                            }

                            var rootT = AvatarDescriptor.gameObject.transform;
                            var bindingsForSelection = selectedGOs
                                .Select(go => BuildBindingForGameObject(rootT, go, type, prop, isPPtr))
                                .ToList();
                            var allSelectedInTargets = bindingsForSelection.Count > 0 &&
                                bindingsForSelection.All(IsTargetBinding);

                            using (var cc = new EditorGUI.ChangeCheckScope())
                            {
                                var newSelected = GUILayout.Toggle(allSelectedInTargets, "T", GUI.skin.button, GUILayout.Width(20));
                                if (cc.changed)
                                {
                                    if (newSelected)
                                    {
                                        AddTargetBindingsForSelection(type, prop, isPPtr, selectedGOs);
                                    }
                                    else
                                    {
                                        foreach (var b in bindingsForSelection)
                                            RemoveTargetBinding(b);
                                    }
                                }
                            }

                            GUILayout.Label(prop, GUILayout.ExpandWidth(true));
                            GUILayout.Label(isPPtr ? "(Object Ref)" : "(Curve)", GUILayout.Width(90));
                        }
                    }
                }
            }
        }
    }

    // Helper to create a signature excluding path (propertyName + type of curve)
    private static string MakeSignature(EditorCurveBinding b) => $"{b.propertyName}|{(b.isPPtrCurve ? "1" : "0")}";
    private static void ParseSignature(string sig, out string propertyName, out bool isPPtr)
    {
        var idx = sig.LastIndexOf('|');
        propertyName = idx >= 0 ? sig.Substring(0, idx) : sig;
        isPPtr = idx >= 0 && idx + 1 < sig.Length && sig[idx + 1] == '1';
    }

    // Compute common available bindings across all selected objects, grouped by component type.
    private Dictionary<Type, List<string>> GetCommonAvailableBindingSignatures(GameObject[] selectedGOs)
    {
        var result = new Dictionary<Type, List<string>>();
        if (selectedGOs.Length == 0) return result;

        // Initialize with first selection
        var first = GetAnimatableBindingsOnGameObject(selectedGOs[0]);
        var common = new Dictionary<Type, HashSet<string>>();
        foreach (var kv in first)
            common[kv.Key] = new HashSet<string>(kv.Value.Select(MakeSignature));

        // Intersect with the rest
        for (int i = 1; i < selectedGOs.Length; i++)
        {
            var next = GetAnimatableBindingsOnGameObject(selectedGOs[i]);
            var types = common.Keys.ToList();
            foreach (var t in types)
            {
                if (!next.TryGetValue(t, out var list))
                {
                    common.Remove(t);
                    continue;
                }
                common[t].IntersectWith(list.Select(MakeSignature));
                if (common[t].Count == 0)
                    common.Remove(t);
            }
        }

        // Convert to list and apply filter
        foreach (var kv in common)
        {
            var list = kv.Value.ToList();
            if (list.Count > 0)
                result[kv.Key] = list;
        }

        return result;
    }

    private IEnumerable<(EditorCurveBinding binding, bool matched)> GetBindingsMatchingSearchFilters(AnimationClip clip)
    {
        if (clip == null) yield break;

        bool Matches(EditorCurveBinding b)
        {
            if (filterBySelection)
            {
                var selectionPaths = GetSelectionPathsUnderAvatar();
                if (selectionPaths.Count > 0 && !selectionPaths.Contains(b.path))
                    return false;
            }
            else
            {
                if (!searchBindingPathFilter.Matches(b.path))
                    return false;
            }
            if (!searchBindingPropertyFilter.Matches(b.propertyName))
                return false;
            if (!searchBindingTypeFilter.Matches(b.type?.Name))
                return false;
            return true;
        }

        foreach (var b in AnimationUtility.GetCurveBindings(clip))
            yield return (b, Matches(b));

        foreach (var b in AnimationUtility.GetObjectReferenceCurveBindings(clip))
            yield return (b, Matches(b));
    }

    private void AddTargetBindingsForSelection(Type type, string propertyName, bool isPPtr, IEnumerable<GameObject> selectedGOs)
    {
        var root = AvatarDescriptor?.gameObject?.transform;
        if (root == null) return;

        foreach (var go in selectedGOs)
        {
            var binding = BuildBindingForGameObject(root, go, type, propertyName, isPPtr);
            if (!IsTargetBinding(binding))
                selectedTargetBindings.Add(binding);
        }
    }

    public static VRCAvatarDescriptor FindAvatarDescriptor(GameObject obj)
    {
        VRCAvatarDescriptor descriptor;
        while (!obj.TryGetComponent(out descriptor))
        {
            if (obj.transform.parent == null)
                return null;
            obj = obj.transform.parent.gameObject;
        }
        return descriptor;
    }

    [MenuItem("Tools/d4rkpl4y3r/AV3 Animation Util")]
    public static void d4rkAV3AnimationUtilMenuItem()
    {
        var window = GetWindow<d4rkAV3AnimationUtilMenu>();
        window.titleContent = new GUIContent("d4rk AV3 Animation Util");
    }

    private void OnSelectionChange()
    {
        cachedSelectionPathsUnderAvatar = null;
        Repaint();
    }
}
#endif