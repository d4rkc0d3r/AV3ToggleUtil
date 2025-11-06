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

public class d4rkAV3AnimationUtilMenu : EditorWindow
{
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
                    animationClips = null;
                    cachedAnimatableBindings.Clear();
                }
                avatarDescriptor = d;
            }
            if (avatarDescriptor != null)
                return avatarDescriptor;
            return null;
        }
        set { avatarDescriptor = value; }
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

    private Dictionary<GameObject, Dictionary<Type, List<EditorCurveBinding>>> cachedAnimatableBindings = new();
    private Dictionary<Type, List<EditorCurveBinding>> GetAnimatableBindingsOnGameObject(GameObject gameObject) {
        if (cachedAnimatableBindings.TryGetValue(gameObject, out var bindings))
            return bindings;
        bindings = new Dictionary<Type, List<EditorCurveBinding>>();
        if (AvatarDescriptor == null)
            return bindings;
        foreach (var animatableBinding in AnimationUtility.GetAnimatableBindings(gameObject, AvatarDescriptor.gameObject))
        {
            if (!bindings.TryGetValue(animatableBinding.type, out var list))
            {
                list = new List<EditorCurveBinding>();
                bindings[animatableBinding.type] = list;
            }
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

        GUILayout.Space(10);
        GUILayout.Label("Selected Source Binding:", EditorStyles.boldLabel);
        using (new EditorGUI.IndentLevelScope())
        {
            if (selectedSourceBinding != null)
            {
                var path = string.IsNullOrEmpty(selectedSourceBinding.Value.path) ? "(root)" : selectedSourceBinding.Value.path;
                var prop = $"{(selectedSourceBinding.Value.type != null ? selectedSourceBinding.Value.type.Name : "Component")}.{selectedSourceBinding.Value.propertyName}";

                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Space(15 * EditorGUI.indentLevel);
                    if (GUILayout.Button("C", GUILayout.Width(20)))
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

        GUILayout.Space(10);
        GUILayout.Label($"Selected Target Bindings({selectedTargetBindings.Count}):", EditorStyles.boldLabel);
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
                    if (GUILayout.Button("C", GUILayout.Width(20)))
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

        GUILayout.Space(10);
        bindingFilter = EditorGUILayout.TextField("Binding Filter", bindingFilter);
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