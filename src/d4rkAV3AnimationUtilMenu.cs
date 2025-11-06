#if UNITY_EDITOR
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEditor;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using UnityEditor.Animations;
using System;
using UnityEngine.UIElements;

public class d4rkAV3AnimationUtilMenu : EditorWindow
{
    private enum SelectionMode
    {
        AllClips,
        SelectionClips,
        SelectionBindings
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
    private bool showAnimationClips = false;
    private bool showSelectionFilteredClips = true;
    private bool showSelectionBindings = true;
    private EditorCurveBinding? selectedSourceBinding = null;
    private List<EditorCurveBinding> selectedTargetBindings = new();
    private string bindingFilter = "";
    private bool showMaterialBindings = false;
    private bool showBlendShapeBindings = false;
    private SelectionMode selectionMode = SelectionMode.SelectionClips;

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

    // Return transform paths (relative to avatar root) of the current selection that are under the avatar
    private IEnumerable<string> GetSelectionPathsUnderAvatar()
    {
        var root = AvatarDescriptor?.gameObject?.transform;
        if (root == null) yield break;

        foreach (var go in Selection.gameObjects)
        {
            if (go == null) continue;
            var t = go.transform;
            if (t == root)
            {
                yield return string.Empty; // root path
            }
            else if (t.IsChildOf(root))
            {
                yield return AnimationUtility.CalculateTransformPath(t, root);
            }
        }
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

    // Filter clips to those that have any binding referencing the selection paths
    private List<AnimationClip> GetSelectionFilteredClips()
    {
        var paths = new HashSet<string>(GetSelectionPathsUnderAvatar());
        if (paths.Count == 0) return new List<AnimationClip>();

        return AnimationClips.Where(clip =>
        {
            // curve bindings (floats)
            foreach (var b in AnimationUtility.GetCurveBindings(clip))
                if (paths.Contains(b.path)) return true;

            // object reference bindings (textures, sprites, etc.)
            foreach (var b in AnimationUtility.GetObjectReferenceCurveBindings(clip))
                if (paths.Contains(b.path)) return true;

            return false;
        }).ToList();
    }

    // Returns all bindings in the given clip that affect any of the selected objects (by path)
    private IEnumerable<EditorCurveBinding> GetBindingsAffectingSelection(AnimationClip clip, HashSet<string> selectionPaths)
    {
        foreach (var b in AnimationUtility.GetCurveBindings(clip))
            if (selectionPaths.Contains(b.path)) yield return b;
        foreach (var b in AnimationUtility.GetObjectReferenceCurveBindings(clip))
            if (selectionPaths.Contains(b.path)) yield return b;
    }

    // For curve bindings, returns bracketed min..max or a single number if all keyframes share the same value.
    private string GetCurveRangeText(AnimationClip clip, EditorCurveBinding binding)
    {
        var curve = AnimationUtility.GetEditorCurve(clip, binding);
        if (curve == null || curve.keys == null || curve.keys.Length == 0) return string.Empty;

        float min = curve.keys[0].value;
        float max = min;
        for (int i = 1; i < curve.keys.Length; i++)
        {
            var v = curve.keys[i].value;
            if (v < min) min = v;
            if (v > max) max = v;
        }

        if (Mathf.Approximately(min, max))
            return min.ToString("0.###");

        return $"[{min:0.###}..{max:0.###}]";
    }

    private TEnum EnumMultiButton<TEnum>(TEnum current)
    {
        var names = Enum.GetNames(typeof(TEnum));
        var values = Enum.GetValues(typeof(TEnum)).Cast<int>().ToArray();
        int currentValue = Array.IndexOf(values, Convert.ToInt32(current));
        using (new EditorGUILayout.HorizontalScope())
        {
            GUILayout.Space(15 * EditorGUI.indentLevel);
            for (int i = 0; i < names.Length; i++)
            {
                using (new EditorGUI.DisabledScope(i == currentValue))
                {
                    if (GUILayout.Button(names[i]))
                    {
                        currentValue = i;
                    }
                }
            }
        }
        return (TEnum)Enum.ToObject(typeof(TEnum), values[currentValue]);
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

        selectionMode = EnumMultiButton(selectionMode);

        GUILayout.Space(10);
        using (new EditorGUILayout.VerticalScope(GUI.skin.box))
        {
            GUILayout.Label("Source Binding:", EditorStyles.boldLabel);
            using (new EditorGUI.IndentLevelScope())
            {
                if (selectedSourceBinding != null)
                {
                    var path = string.IsNullOrEmpty(selectedSourceBinding.Value.path) ? "(root)" : selectedSourceBinding.Value.path;
                    var prop = $"{(selectedSourceBinding.Value.type != null ? selectedSourceBinding.Value.type.Name : "Component")}.{selectedSourceBinding.Value.propertyName}";

                    using (new EditorGUILayout.HorizontalScope())
                    {
                        GUILayout.Space(15 * EditorGUI.indentLevel);
                        if (GUILayout.Button("-", GUILayout.Width(20)))
                        {
                            selectedSourceBinding = null;
                        }
                        GUILayout.Label(path, GUILayout.ExpandWidth(true));
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
                    var path = string.IsNullOrEmpty(b.path) ? "(root)" : b.path;
                    var prop = $"{(b.type != null ? b.type.Name : "Component")}.{b.propertyName}";

                    using (new EditorGUILayout.HorizontalScope())
                    {
                        GUILayout.Space(15 * EditorGUI.indentLevel);
                        if (GUILayout.Button("-", GUILayout.Width(20)))
                        {
                            selectedTargetBindings.RemoveAt(i);
                            i--;
                            continue;
                        }
                        GUILayout.Label(path, GUILayout.ExpandWidth(true));
                        GUILayout.Label(prop, GUILayout.ExpandWidth(true));
                        GUILayout.FlexibleSpace();
                    }
                }
            }
        }

        GUILayout.Space(10);

        if (selectionMode == SelectionMode.AllClips)
        {
            using var box = new EditorGUILayout.VerticalScope(GUI.skin.box);
            // All clips foldout
            showAnimationClips = EditorGUILayout.Foldout(showAnimationClips, $"All Animation Clips in Avatar ({AnimationClips.Count}):", true);
            if (showAnimationClips)
            {
                using (new EditorGUI.IndentLevelScope())
                {
                    foreach (var clip in AnimationClips)
                    {
                        EditorGUILayout.ObjectField(clip, typeof(AnimationClip), false);
                    }
                }
            }
        }

        if (selectionMode == SelectionMode.SelectionClips)
        {
            using var box = new EditorGUILayout.VerticalScope(GUI.skin.box);
            // Selection-filtered clips foldout
            var selectionPaths = new HashSet<string>(GetSelectionPathsUnderAvatar());
            var selectionClips = GetSelectionFilteredClips();
            showSelectionFilteredClips = EditorGUILayout.Foldout(showSelectionFilteredClips, $"Animation Clips affecting Selection ({selectionClips.Count}):", true);
            if (showSelectionFilteredClips)
            {
                using (new EditorGUI.IndentLevelScope())
                {
                    showSelectionBindings = EditorGUILayout.ToggleLeft("Show affected bindings", showSelectionBindings);
                    foreach (var clip in selectionClips)
                    {
                        EditorGUILayout.ObjectField(clip, typeof(AnimationClip), false);
                        if (showSelectionBindings)
                        {
                            using (new EditorGUI.IndentLevelScope())
                            {
                                foreach (var b in GetBindingsAffectingSelection(clip, selectionPaths))
                                {
                                    var path = string.IsNullOrEmpty(b.path) ? "(root)" : b.path;
                                    var prop = $"{(b.type != null ? b.type.Name : "Component")}.{b.propertyName}";
                                    var range = GetCurveRangeText(clip, b); // empty for non-curve bindings

                                    using (new EditorGUILayout.HorizontalScope())
                                    {
                                        GUILayout.Space(15 * EditorGUI.indentLevel);
                                        if (GUILayout.Button("S", GUILayout.Width(20)))
                                        {
                                            selectedSourceBinding = b;
                                        }
                                        GUILayout.Label(range, GUILayout.Width(90));
                                        GUILayout.Label(path, GUILayout.ExpandWidth(true));
                                        GUILayout.Label(prop, GUILayout.ExpandWidth(true));
                                        GUILayout.FlexibleSpace();
                                    }
                                }
                            }
                        }
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
                if (selectedGOs.Length == 0)
                {
                    GUILayout.Label("(none under avatar)");
                }
                else
                {
                    var common = GetCommonAvailableBindingSignatures(selectedGOs);
                    if (common.Count == 0)
                    {
                        GUILayout.Label("(no common bindings)");
                    }
                    else
                    {
                        foreach (var kv in common.OrderBy(k => k.Key.Name))
                        {
                            var type = kv.Key;
                            var sigs = kv.Value;
                            if (!typeFoldoutStates.TryGetValue(type, out var open)) open = true;
                            open = EditorGUILayout.Foldout(open, $"{type.Name} ({sigs.Count})", true);
                            typeFoldoutStates[type] = open;

                            if (open)
                            {
                                using (new EditorGUI.IndentLevelScope())
                                {
                                    foreach (var sig in sigs)
                                    {
                                        ParseSignature(sig, out var prop, out var isPPtr);
                                        using (new EditorGUILayout.HorizontalScope())
                                        {
                                            GUILayout.Space(15 * EditorGUI.indentLevel);
                                            using (new EditorGUI.DisabledScope(selectedGOs.Length != 1))
                                            {
                                                if (GUILayout.Button("S", GUILayout.Width(20)))
                                                {
                                                    var pathToSelected = selectedGOs.Length == 1
                                                        ? (selectedGOs[0].transform == AvatarDescriptor.gameObject.transform ? string.Empty : AnimationUtility.CalculateTransformPath(selectedGOs[0].transform, AvatarDescriptor.gameObject.transform))
                                                        : string.Empty;
                                                    selectedSourceBinding = isPPtr
                                                        ? EditorCurveBinding.PPtrCurve(pathToSelected, type, prop)
                                                        : EditorCurveBinding.FloatCurve(pathToSelected, type, prop);
                                                }
                                            }
                                            if (GUILayout.Button("T", GUILayout.Width(20)))
                                            {
                                                AddTargetBindingsForSelection(type, prop, isPPtr, selectedGOs);
                                            }
                                            GUILayout.Label(prop, GUILayout.ExpandWidth(true));
                                            GUILayout.Label(isPPtr ? "(Object Ref)" : "(Curve)", GUILayout.Width(90));
                                        }
                                    }
                                }
                            }
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

    private void AddTargetBindingsForSelection(Type type, string propertyName, bool isPPtr, IEnumerable<GameObject> selectedGOs)
    {
        var root = AvatarDescriptor?.gameObject?.transform;
        if (root == null) return;

        foreach (var go in selectedGOs)
        {
            var path = go.transform == root ? string.Empty : AnimationUtility.CalculateTransformPath(go.transform, root);
            var binding = isPPtr
                ? EditorCurveBinding.PPtrCurve(path, type, propertyName)
                : EditorCurveBinding.FloatCurve(path, type, propertyName);

            bool exists = selectedTargetBindings.Any(b =>
                b.path == binding.path &&
                b.type == binding.type &&
                b.propertyName == binding.propertyName &&
                b.isPPtrCurve == binding.isPPtrCurve);

            if (!exists)
                selectedTargetBindings.Add(binding);
        }
    }

    public static VRCAvatarDescriptor FindAvatarDescriptor(GameObject obj)
    {
        VRCAvatarDescriptor descriptor = null;
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
        var window = GetWindow(typeof(d4rkAV3AnimationUtilMenu)) as d4rkAV3AnimationUtilMenu;
    }

    private void OnEnable()
    {
        Selection.selectionChanged += Repaint;
    }

    private void OnDisable()
    {
        Selection.selectionChanged -= Repaint;
    }

    private void OnSelectionChange()
    {
        Repaint();
    }
}
#endif