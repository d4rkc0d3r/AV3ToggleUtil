#if UNITY_EDITOR
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEditor;
using VRC.SDK3.Avatars.Components;
using UnityEditor.Animations;

public class CreateAV3ToggleMenu : EditorWindow
{
    private Dictionary<Component, bool> componentToggles = new Dictionary<Component, bool>();
    private GameObject target;
    public GameObject Target
    {
        get { return target; }
        set
        {
            if (target == value)
                return;
            target = value;
            toggleName = "";
            componentToggles.Clear();
            if (Target == null)
                return;
            foreach (var component in Target.GetComponents<Component>())
            {
                componentToggles[component] = false;
            }
            componentToggles[Target.transform] = true;
        }
    }
    private string toggleName = "";
    public string ToggleName
    {
        get { return toggleName == "" ? GetDefaultToggleName() : toggleName; }
        set { toggleName = (value == GetDefaultToggleName()) ? "" : value; }
    }

    public string GetDefaultToggleName()
    {
        string name = "Toggle" + Target.name;
        var toggleArray = componentToggles.Where(p => p.Value).Select(p => p.Key).ToArray();
        if (toggleArray.Length == 1 && !(toggleArray[0] is Transform))
        {
            var fullType = toggleArray[0].GetType().ToString();
            if (fullType.LastIndexOf(".") != -1)
            {
                fullType = fullType.Substring(fullType.LastIndexOf(".") + 1);
            }
            name += fullType;
        }
        return name;
    }

    private static string GetAssetFolder(Object asset)
    {
        string path = AssetDatabase.GetAssetPath(asset);
        return path.Substring(0, path.LastIndexOf("/"));
    }

    private string GetAnimationsFolderPath()
    {
        var descriptor = FindAvatarDescriptor(Target);
        var path = GetAssetFolder(descriptor.expressionParameters) + "/Animations";
        if (AssetDatabase.IsValidFolder(path))
            return path;
        path = GetAssetFolder(descriptor.expressionsMenu) + "/Animations";
        if (AssetDatabase.IsValidFolder(path))
            return path;
        return GetAssetFolder(descriptor.baseAnimationLayers[4].animatorController) + "/Animations";
    }

    private string CanCreateToggle()
    {
        var descriptor = FindAvatarDescriptor(Target);
        if (descriptor == null)
            return "No Avatar Descriptor Found";
        if (AssetDatabase.GetAssetPath(descriptor.expressionParameters) == "")
            return "No Custom Parameters Found";
        if (AssetDatabase.GetAssetPath(descriptor.expressionsMenu) == "")
            return "No Custom Menu Found";
        var fxLayer = descriptor.baseAnimationLayers[4].animatorController as AnimatorController;
        if (AssetDatabase.GetAssetPath(fxLayer) == "")
            return "No Custom FxLayer Found";
        if (descriptor.expressionParameters.FindParameter(ToggleName) != null)
            return "Parameter Exists Already";
        if (fxLayer.layers.Any(l => l.name == ToggleName))
            return "Layer Exists Already";
        if (fxLayer.parameters.Any(p => p.name == ToggleName))
            return "Layer Parameter Exists Already";
        var path = GetAnimationsFolderPath();
        if (AssetDatabase.LoadAssetAtPath<AnimationClip>(path + "/" + ToggleName + "On.anim") != null)
            return "Toggle On Animation Exists Already";
        if (AssetDatabase.LoadAssetAtPath<AnimationClip>(path + "/" + ToggleName + "Off.anim") != null)
            return "Toggle Off Animation Exists Already";
        if (componentToggles.Values.Count(v => v) == 0)
            return "No Toggles Selected";
        return "";
    }

    void OnGUI()
    {
        Target = EditorGUILayout.ObjectField("Target", Target, typeof(GameObject), true) as GameObject;

        if (Target == null)
            return;

        foreach (var component in Target.GetComponents<Component>())
        {
            componentToggles.TryGetValue(component, out bool toggleValue);
            var componentName = component.GetType().ToString();
            if (componentName.LastIndexOf('.') != -1)
                componentName = componentName.Substring(componentName.LastIndexOf('.') + 1);
            if (componentName == "Transform")
                componentName = "GameObject";
            componentToggles[component] = EditorGUILayout.Toggle(componentName, toggleValue);
        }

        GUILayout.Space(8);

        ToggleName = EditorGUILayout.TextField("Toggle Name", ToggleName);

        GUILayout.Space(8);

        string errorMsg = CanCreateToggle();
        GUI.enabled = errorMsg == "";
        if (GUILayout.Button("Create" + ((errorMsg == "") ? "" : " (" + errorMsg + ")")))
        {

        }
        GUI.enabled = true;

        GUILayout.Space(8);

        var descriptor = FindAvatarDescriptor(Target);
        EditorGUILayout.LabelField("Avatar", descriptor?.name);
        if (descriptor == null)
            return;
        EditorGUILayout.LabelField("AnimationFolder", GetAnimationsFolderPath());
        EditorGUILayout.LabelField("ParamAssetPath", AssetDatabase.GetAssetPath(descriptor.expressionParameters));
        EditorGUILayout.LabelField("MenuAssetPath", AssetDatabase.GetAssetPath(descriptor.expressionsMenu));
        EditorGUILayout.LabelField("FxLayerAssetPath", AssetDatabase.GetAssetPath(descriptor.baseAnimationLayers[4].animatorController));
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

    [MenuItem("GameObject/Create AV3 Toggle", false, -1)]
    public static void CreateAV3ToggleMenuItem()
    {
        var window = GetWindow(typeof(CreateAV3ToggleMenu)) as CreateAV3ToggleMenu;
        window.Target = Selection.activeObject as GameObject;
    }

    [MenuItem("GameObject/Create AV3 Toggle", true, -1)]
    public static bool CreateAV3ToggleMenuItemValidation()
    {
        var obj = Selection.activeObject as GameObject;
        if (obj == null)
            return false;
        return FindAvatarDescriptor(obj) != null;
    }
}
#endif