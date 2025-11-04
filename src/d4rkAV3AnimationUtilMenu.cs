#if UNITY_EDITOR
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEditor;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using UnityEditor.Animations;

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
                }
                return avatarDescriptor = (d == null ? null : d);
            }
            if (avatarDescriptor != null)
                return avatarDescriptor;
            return null;
        }
        set { avatarDescriptor = value; }
    }

    private VRCExpressionsMenu targetMenu = null;
    public VRCExpressionsMenu TargetMenu
    {
        get { return targetMenu == null ? GetMainMenu() : targetMenu; }
        set { targetMenu = (value == GetMainMenu()) ? null : value; }
    }

    public VRCExpressionsMenu GetMainMenu()
    {
        return AvatarDescriptor?.expressionsMenu;
    }

    private static string GetAssetFolder(Object asset)
    {
        string path = AssetDatabase.GetAssetPath(asset);
        return path == "" ? "" : path.Substring(0, path.LastIndexOf("/"));
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

    Vector2 scrollPos;
    private bool showAnimationClips = true;
    private bool showSelectionFilteredClips = true;
    private bool showSelectionBindings = true;

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