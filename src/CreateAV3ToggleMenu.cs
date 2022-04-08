#if UNITY_EDITOR
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEditor;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
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
    private VRCExpressionsMenu targetMenu = null;
    public VRCExpressionsMenu TargetMenu
    {
        get { return targetMenu == null ? GetMainMenu() : targetMenu; }
        set { targetMenu = (value == GetMainMenu()) ? null : value; }
    }

    public VRCExpressionsMenu GetMainMenu()
    {
        return FindAvatarDescriptor(Target)?.expressionsMenu;
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
        return path == "" ? "" : path.Substring(0, path.LastIndexOf("/"));
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
        if (TargetMenu.controls.Count >= 8)
            return "Target Menu Is Full Already";
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

        var descriptor = FindAvatarDescriptor(Target);
        TargetMenu = EditorGUILayout.ObjectField("Menu", TargetMenu, typeof(VRCExpressionsMenu), false) as VRCExpressionsMenu;

        GUILayout.Space(8);

        string errorMsg = CanCreateToggle();
        GUI.enabled = errorMsg == "";
        if (GUILayout.Button("Create" + ((errorMsg == "") ? "" : " (" + errorMsg + ")")))
        {
            string animFolder = GetAnimationsFolderPath();
            if (!AssetDatabase.IsValidFolder(animFolder))
                AssetDatabase.CreateFolder(animFolder.Substring(0, animFolder.LastIndexOf("/")), "Animations");
            string pathToAvatarRoot = "";
            var t = Target.transform;
            var root = descriptor.transform;
            if (t != root)
            {
                pathToAvatarRoot = t.name;
                while ((t = t.parent) != root)
                {
                    pathToAvatarRoot = t.name + "/" + pathToAvatarRoot;
                }
            }
            var clipOn = new AnimationClip();
            clipOn.name = ToggleName + "On";
            var clipOff = new AnimationClip();
            clipOff.name = ToggleName + "Off";
            foreach (var pair in componentToggles.Where(p => p.Value))
            {
                bool isGameObjectToggle = pair.Key is Transform;
                EditorCurveBinding binding = new EditorCurveBinding();
                binding.path = pathToAvatarRoot;
                binding.propertyName = isGameObjectToggle ? "m_IsActive" : "m_Enabled";
                binding.type = isGameObjectToggle ? typeof(GameObject) : pair.Key.GetType();
                var curveOn = new AnimationCurve();
                curveOn.AddKey(0, 1);
                curveOn.AddKey(1 / 60f, 1);
                AnimationUtility.SetEditorCurve(clipOn, binding, curveOn);
                var curveOff = new AnimationCurve();
                curveOff.AddKey(0, 0);
                curveOff.AddKey(1 / 60f, 0);
                AnimationUtility.SetEditorCurve(clipOff, binding, curveOff);
            }
            AssetDatabase.CreateAsset(clipOn, animFolder + "/" + clipOn.name + ".anim");
            AssetDatabase.CreateAsset(clipOff, animFolder + "/" + clipOff.name + ".anim");

            var param = new VRCExpressionParameters.Parameter()
            {
                name = ToggleName,
                defaultValue = Target.activeSelf ? 1.0f : 0.0f,
                saved = true,
                valueType = VRCExpressionParameters.ValueType.Bool
            };

            descriptor.expressionParameters.parameters = descriptor.expressionParameters.parameters
                .Union(new VRCExpressionParameters.Parameter[] { param }).ToArray();
            
            AssetDatabase.SaveAssets();

            var fxLayer = descriptor.baseAnimationLayers[4].animatorController as AnimatorController;
            fxLayer.AddParameter(new AnimatorControllerParameter()
            {
                name = ToggleName,
                type = AnimatorControllerParameterType.Bool,
                defaultBool = Target.activeSelf
            });

            var layer = new AnimatorControllerLayer();
            layer.name = ToggleName;
            layer.stateMachine = new AnimatorStateMachine();
            layer.stateMachine.name = ToggleName;
            layer.stateMachine.hideFlags = HideFlags.HideInHierarchy;
            layer.defaultWeight = 1.0f;
            layer.avatarMask = null;

            var toggleOff = new AnimatorState();
            toggleOff.motion = clipOff;
            toggleOff.name = clipOff.name;
            toggleOff.writeDefaultValues = false;
            toggleOff.hideFlags = HideFlags.HideInHierarchy;

            var toggleOn = new AnimatorState();
            toggleOn.motion = clipOn;
            toggleOn.name = clipOn.name;
            toggleOn.writeDefaultValues = false;
            toggleOn.hideFlags = HideFlags.HideInHierarchy;

            var transitionToOn = new AnimatorStateTransition();
            transitionToOn.canTransitionToSelf = false;
            transitionToOn.destinationState = toggleOn;
            transitionToOn.hasFixedDuration = true;
            transitionToOn.hasExitTime = false;
            transitionToOn.duration = 0.1f;
            transitionToOn.AddCondition(AnimatorConditionMode.If, 0, ToggleName);
            transitionToOn.hideFlags = HideFlags.HideInHierarchy;
            toggleOff.AddTransition(transitionToOn);

            var transitionToOff = new AnimatorStateTransition();
            transitionToOff.canTransitionToSelf = false;
            transitionToOff.destinationState = toggleOff;
            transitionToOff.hasFixedDuration = true;
            transitionToOff.hasExitTime = false;
            transitionToOff.duration = 0.1f;
            transitionToOff.AddCondition(AnimatorConditionMode.IfNot, 0, ToggleName);
            transitionToOff.hideFlags = HideFlags.HideInHierarchy;
            toggleOn.AddTransition(transitionToOff);

            if (Target.activeSelf)
            {
                layer.stateMachine.AddState(toggleOn, new Vector3(300, 200, 0));
                layer.stateMachine.AddState(toggleOff, new Vector3(300, 120, 0));
            }
            else
            {
                layer.stateMachine.AddState(toggleOff, new Vector3(300, 120, 0));
                layer.stateMachine.AddState(toggleOn, new Vector3(300, 200, 0));
            }

            var fxLayerPath = AssetDatabase.GetAssetPath(descriptor.baseAnimationLayers[4].animatorController);
            fxLayer.AddLayer(layer);
            AssetDatabase.SaveAssets();
            AssetDatabase.AddObjectToAsset(toggleOff, fxLayerPath);
            AssetDatabase.AddObjectToAsset(toggleOn, fxLayerPath);
            AssetDatabase.AddObjectToAsset(transitionToOn, fxLayerPath);
            AssetDatabase.AddObjectToAsset(transitionToOff, fxLayerPath);
            AssetDatabase.AddObjectToAsset(layer.stateMachine, fxLayerPath);
            AssetDatabase.SaveAssets();

            TargetMenu.controls.Add(new VRCExpressionsMenu.Control()
            {
                name = ToggleName.StartsWith("Toggle") ? ToggleName.Substring(6) : ToggleName,
                parameter = new VRCExpressionsMenu.Control.Parameter() { name = ToggleName },
                type = VRCExpressionsMenu.Control.ControlType.Toggle
            });

            AssetDatabase.SaveAssets();
        }
        GUI.enabled = true;

        GUILayout.Space(8);

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