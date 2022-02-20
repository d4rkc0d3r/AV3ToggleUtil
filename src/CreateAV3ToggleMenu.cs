#if UNITY_EDITOR
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEditor;
using VRC.SDK3.Avatars.Components;

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

    void OnGUI()
    {
        Target = EditorGUILayout.ObjectField("Target GameObject", Target, typeof(GameObject), true) as GameObject;

        if (Target == null)
            return;

        foreach (var component in Target.GetComponents<Component>())
        {
            componentToggles[component] = EditorGUILayout.Toggle("" + component.GetType(), componentToggles[component]);
        }

        GUILayout.Space(8);

        ToggleName = EditorGUILayout.TextField("Toggle Name", ToggleName);

        GUILayout.Space(8);

        var descriptor = FindAvatarDescriptor(Target);
        EditorGUILayout.LabelField("Avatar", descriptor?.name);
        if (descriptor == null)
            return;
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