#if UNITY_EDITOR
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEditor;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using UnityEditor.Animations;

public class CreateAV3MultiToggleMenu : EditorWindow
{
    private AnimationClip offClip = null;
    private List<AnimationClip> animationClips = new List<AnimationClip>();
    private int defaultState = 1;
    private bool offStateInMenu = false;
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
            offClip = null;
            animationClips.Clear();
            if (Target == null)
                return;
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
        string name = "";
        var allClips = animationClips.ToList();
        if (offClip != null)
            allClips.Add(offClip);
        if (allClips.Count > 0)
        {
            // Find longest prefix of all clip names
            int maxPrefixLength = allClips[0].name.Length;
            for (int i = 1; i < allClips.Count; i++)
            {
                int prefixLength = 0;
                for (int j = 0; j < allClips[i].name.Length && j < allClips[0].name.Length; j++)
                {
                    if (allClips[i].name[j] == allClips[0].name[j])
                        prefixLength++;
                    else
                        break;
                }
                if (prefixLength < maxPrefixLength)
                    maxPrefixLength = prefixLength;
            }
            if (maxPrefixLength > 0)
            {
                // Prefix should end when the next character is uppercase
                for (int i = maxPrefixLength; i > 0; i--)
                {
                    if (char.IsUpper(i == allClips[0].name.Length ? 'l' : allClips[0].name[i]))
                    {
                        maxPrefixLength = i;
                        break;
                    }
                }
                name = allClips[0].name.Substring(0, maxPrefixLength);
            }
        }
        if (name == "")
            name = "MultiToggle";
        return name;
    }

    private static string GetAssetFolder(Object asset)
    {
        string path = AssetDatabase.GetAssetPath(asset);
        return path == "" ? "" : path.Substring(0, path.LastIndexOf("/"));
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
        if (TargetMenu.controls.Any(c => c.name == ToggleName))
            return "Menu Item Exists Already";
        return "";
    }

    private string CutToggleNamePrefix(string name)
    {
        if (name.Length <= ToggleName.Length || name.Substring(0, ToggleName.Length) != ToggleName)
            return name;
        return name.Substring(ToggleName.Length);
    }

    private void AddLeafState(AnimatorState baseState, AnimatorStateMachine stateMachine, AnimationClip clip, int index, List<Object> toSave)
    {
        var state = new AnimatorState();
        state.motion = clip;
        state.name = clip.name;
        state.writeDefaultValues = false;
        state.hideFlags = HideFlags.HideInHierarchy;

        var transitionToState = new AnimatorStateTransition();
        transitionToState.canTransitionToSelf = false;
        transitionToState.destinationState = state;
        transitionToState.hasFixedDuration = true;
        transitionToState.hasExitTime = false;
        transitionToState.duration = 0.0f;
        transitionToState.AddCondition(AnimatorConditionMode.Equals, index, ToggleName);
        transitionToState.hideFlags = HideFlags.HideInHierarchy;
        baseState.AddTransition(transitionToState);
        toSave.Add(transitionToState);

        var transitionToBase = new AnimatorStateTransition();
        transitionToBase.canTransitionToSelf = false;
        transitionToBase.destinationState = baseState;
        transitionToBase.hasFixedDuration = true;
        transitionToBase.hasExitTime = false;
        transitionToBase.duration = 0.0f;
        transitionToBase.AddCondition(AnimatorConditionMode.NotEqual, index, ToggleName);
        transitionToBase.hideFlags = HideFlags.HideInHierarchy;
        state.AddTransition(transitionToBase);
        toSave.Add(transitionToBase);

        stateMachine.AddState(state, new Vector3(500, 0 + 70 * index, 0));
        toSave.Add(state);
    }

    void OnGUI()
    {
        Target = EditorGUILayout.ObjectField("Target", Target, typeof(GameObject), true) as GameObject;

        if (Target == null)
            return;

        GUILayout.Space(8);

        defaultState = EditorGUILayout.IntField("Default State", defaultState);
        offStateInMenu = EditorGUILayout.Toggle("Off State in Menu", offStateInMenu);
        offClip = EditorGUILayout.ObjectField("Clip Off", offClip, typeof(AnimationClip), false) as AnimationClip;

        animationClips.Add(null);
        for (int i = 0; i < animationClips.Count; i++)
        {
            animationClips[i] = EditorGUILayout.ObjectField("Clip " + (i + 1), animationClips[i], typeof(AnimationClip), false) as AnimationClip;
            if (animationClips[i] == null)
            {
                animationClips.RemoveAt(i);
                i--;
            }
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
            var param = new VRCExpressionParameters.Parameter()
            {
                name = ToggleName,
                defaultValue = defaultState,
                saved = true,
                valueType = VRCExpressionParameters.ValueType.Int
            };

            descriptor.expressionParameters.parameters = descriptor.expressionParameters.parameters
                .Union(new VRCExpressionParameters.Parameter[] { param }).ToArray();
            EditorUtility.SetDirty(descriptor.expressionParameters);
            AssetDatabase.SaveAssets();

            var fxLayer = descriptor.baseAnimationLayers[4].animatorController as AnimatorController;
            fxLayer.AddParameter(new AnimatorControllerParameter()
            {
                name = ToggleName,
                type = AnimatorControllerParameterType.Int,
                defaultInt = defaultState
            });
            EditorUtility.SetDirty(fxLayer);
            AssetDatabase.SaveAssets();

            var toSave = new List<Object>();

            var layer = new AnimatorControllerLayer();
            layer.name = ToggleName;
            layer.stateMachine = new AnimatorStateMachine();
            layer.stateMachine.name = ToggleName;
            layer.stateMachine.hideFlags = HideFlags.HideInHierarchy;
            layer.defaultWeight = 1.0f;
            layer.avatarMask = null;

            var baseState = new AnimatorState();
            baseState.name = "Base";
            baseState.writeDefaultValues = false;
            baseState.hideFlags = HideFlags.HideInHierarchy;
            
            layer.stateMachine.AddState(baseState, new Vector3(250, 120, 0));
            toSave.Add(baseState);

            if (offClip != null)
            {
                AddLeafState(baseState, layer.stateMachine, offClip, 0, toSave);
            }

            for (int i = 0; i < animationClips.Count; i++)
            {
                AddLeafState(baseState, layer.stateMachine, animationClips[i], i + 1, toSave);
            }

            var fxLayerPath = AssetDatabase.GetAssetPath(descriptor.baseAnimationLayers[4].animatorController);
            fxLayer.AddLayer(layer);
            AssetDatabase.SaveAssets();
            foreach (var obj in toSave)
            {
                AssetDatabase.AddObjectToAsset(obj, fxLayerPath);
            }
            AssetDatabase.AddObjectToAsset(layer.stateMachine, fxLayerPath);
            AssetDatabase.SaveAssets();

            var menu = ScriptableObject.CreateInstance("VRCExpressionsMenu") as VRCExpressionsMenu;
            menu.controls = new List<VRCExpressionsMenu.Control>();
            if (offStateInMenu && offClip != null)
            {
                menu.controls.Add(new VRCExpressionsMenu.Control()
                {
                    name = CutToggleNamePrefix(offClip.name),
                    parameter = new VRCExpressionsMenu.Control.Parameter() { name = ToggleName },
                    type = VRCExpressionsMenu.Control.ControlType.Toggle,
                    value = 0,
                });
            }
            for (int i = 0; i < animationClips.Count; i++)
            {
                menu.controls.Add(new VRCExpressionsMenu.Control()
                {
                    name = CutToggleNamePrefix(animationClips[i].name),
                    parameter = new VRCExpressionsMenu.Control.Parameter() { name = ToggleName },
                    type = VRCExpressionsMenu.Control.ControlType.Toggle,
                    value = i + 1,
                });
            }
            AssetDatabase.CreateAsset(menu, AssetDatabase.GenerateUniqueAssetPath(GetAssetFolder(TargetMenu) + "/" + ToggleName + ".asset"));

            TargetMenu.controls.Add(new VRCExpressionsMenu.Control()
            {
                name = ToggleName,
                type = VRCExpressionsMenu.Control.ControlType.SubMenu,
                subMenu = menu,
            });
            EditorUtility.SetDirty(TargetMenu);
            AssetDatabase.SaveAssets();
        }
        GUI.enabled = true;

        GUILayout.Space(8);

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

    [MenuItem("GameObject/Create AV3 Multi Toggle", false, -1)]
    public static void CreateAV3ToggleMenuItem()
    {
        var window = GetWindow(typeof(CreateAV3MultiToggleMenu)) as CreateAV3MultiToggleMenu;
        window.Target = Selection.activeObject as GameObject;
    }

    [MenuItem("GameObject/Create AV3 Multi Toggle", true, -1)]
    public static bool CreateAV3ToggleMenuItemValidation()
    {
        var obj = Selection.activeObject as GameObject;
        if (obj == null)
            return false;
        return FindAvatarDescriptor(obj) != null;
    }
}
#endif