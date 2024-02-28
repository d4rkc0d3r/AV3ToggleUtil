#if UNITY_EDITOR
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEditor;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using UnityEditor.Animations;

public class CreateAV3MaterialAnimationMenu : EditorWindow
{
    [System.Serializable]
    public class AnimatedProperty {
        public enum Type {
            Float,
            Vector,
            Color,
            ColorHDR
        }
        public string name;
        public Type type;
        public List<Vector4> values = new List<Vector4>() { Vector4.zero, Vector4.one};
    }
    public enum AnimationType {
        Toggle,
        RadialLinear,
        RadialLogLinear,
        RadialSquared,
        RadialRoot,
    }
    private AnimationType animationType = AnimationType.Toggle;
    private GameObject target = null;
    public GameObject Target
    {
        get { return target; }
    }
    private List<Renderer> renderers = new List<Renderer>();
    private bool showRenderers = true;

    private List<AnimatedProperty> animatedProperties = new List<AnimatedProperty>();
    private string propertyNameToAdd = "";

    private string parameterName = "";
    public string ParameterName
    {
        get { return parameterName; }
        set { parameterName = value; }
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

    private static string GetAssetFolder(Object asset)
    {
        string path = AssetDatabase.GetAssetPath(asset);
        return path == "" ? "" : path.Substring(0, path.LastIndexOf("/"));
    }
    
    HashSet<string> cachedAnimatableBindings = null;
    private HashSet<string> GetAnimatableBindings() {
        if (cachedAnimatableBindings != null)
            return cachedAnimatableBindings;
        cachedAnimatableBindings = new HashSet<string>();
        foreach (var renderer in renderers) {
            foreach (var animatableBinding in AnimationUtility.GetAnimatableBindings(renderer.gameObject, renderer.gameObject)) {
                var name = animatableBinding.propertyName;
                if (!name.StartsWith("material."))
                    continue;
                if (name.Length > 2 && name[name.Length - 2] == '.')
                    name = name.Substring(0, name.Length - 2);
                cachedAnimatableBindings.Add(name.Substring("material.".Length));
            }
        }
        return cachedAnimatableBindings;
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

    private string CanCreateAnimations()
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
        if (ParameterName == "")
            return "No Parameter Name Specified";
        if (descriptor.expressionParameters.FindParameter(ParameterName) != null)
            return "Parameter Exists Already";
        if (fxLayer.layers.Any(l => l.name == ParameterName))
            return "Layer Exists Already";
        if (fxLayer.parameters.Any(p => p.name == ParameterName))
            return "Layer Parameter Exists Already";
        return "";
    }
    
    private void DynamicList<T>(ref List<T> list) where T : Object
    {
        list.Add(null);
        for (int i = 0; i < list.Count; i++)
        {
            EditorGUILayout.BeginHorizontal();
            var output = EditorGUILayout.ObjectField(list[i], typeof(T), true) as T;
            if (i == list.Count - 1)
            {
                GUILayout.Space(23);
            }
            else if (GUILayout.Button("X", GUILayout.Width(20)))
            {
                output = null;
            }
            EditorGUILayout.EndHorizontal();
            list[i] = output;
        }
        list = list.Where(o => o != null).Distinct().ToList();
    }

    private float LogLerp(float a, float b, float t) => Mathf.Exp(Mathf.LerpUnclamped(Mathf.Log(a), Mathf.Log(b), t));
    private float SquareLerp(float a, float b, float t) => Mathf.LerpUnclamped(a, b, t * t);
    private float RootLerp(float a, float b, float t) => Mathf.LerpUnclamped(a, b, Mathf.Sqrt(t));

    private Keyframe CreateKeyframe(float time, float a, float b, System.Func<float, float, float, float> interpolator) {
        var tangent = (interpolator(a, b, time + 0.01f) - interpolator(a, b, time)) * 100f * (60f / 100f);
        return new Keyframe(time * 100f / 60f, interpolator(a, b, time), tangent, tangent);
    }

    Vector2 scrollPos;

    void OnGUI()
    {
        target = EditorGUILayout.ObjectField("Target", target, typeof(GameObject), true) as GameObject;

        if (Target == null)
            return;

        scrollPos = EditorGUILayout.BeginScrollView(scrollPos);

        if (showRenderers = EditorGUILayout.Foldout(showRenderers, $"Renderers ({renderers.Count})", true))
        {
            using (new EditorGUI.IndentLevelScope())
            {
                DynamicList(ref renderers);
            }
        }

        GUILayout.Space(8);

        ParameterName = EditorGUILayout.TextField("Parameter Name", ParameterName);
        animationType = (AnimationType)EditorGUILayout.EnumPopup("Animation Type", animationType);

        GUILayout.Space(8);

        var descriptor = FindAvatarDescriptor(Target);
        TargetMenu = EditorGUILayout.ObjectField("Menu", TargetMenu, typeof(VRCExpressionsMenu), false) as VRCExpressionsMenu;

        GUILayout.Space(8);

        var animatableBindings = GetAnimatableBindings();
        var filteredBindings = animatableBindings
            .Where(b => b.Contains(propertyNameToAdd))
            .OrderBy(b => b.Length)
            .Concat(animatableBindings.Where(b => b.ToLowerInvariant().Contains(propertyNameToAdd.ToLowerInvariant())).OrderBy(b => b.Length))
            .Distinct().ToList();

        EditorGUILayout.BeginVertical(GUI.skin.box);
        EditorGUILayout.LabelField($"Add Property to Animate ({filteredBindings.Count}/{animatableBindings.Count})");
        propertyNameToAdd = EditorGUILayout.TextField(propertyNameToAdd);
        for (int i = 0; i < 5; i++)
        {
            if (i >= filteredBindings.Count) {
                EditorGUILayout.LabelField("");
                continue;
            }
            var property = filteredBindings[i];
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("", GUILayout.Width(15));
            using (new EditorGUI.DisabledScope(animatedProperties.Any(ap => ap.name == property))) {
                if (GUILayout.Button("Add", GUILayout.Width(40))) {
                    animatedProperties.Add(new AnimatedProperty() { name = property });
                }
            }
            EditorGUILayout.LabelField(property);
            EditorGUILayout.EndHorizontal();
        }
        EditorGUILayout.EndVertical();
        
        GUILayout.Space(8);

        EditorGUILayout.BeginVertical(GUI.skin.box);
        EditorGUILayout.LabelField($"Animated Properties ({animatedProperties.Count})");
        AnimatedProperty toRemove = null;
        foreach (var animatedProperty in animatedProperties)
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.Space(15, false);
            EditorGUILayout.BeginVertical(GUI.skin.box);
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(animatedProperty.name);
            if (GUILayout.Button("X", GUILayout.Width(20))) {
                toRemove = animatedProperty;
            }
            EditorGUILayout.EndHorizontal();
            using (new EditorGUI.IndentLevelScope())
            {
                bool isInline = animatedProperty.type != AnimatedProperty.Type.Vector;
                if (isInline)
                    EditorGUILayout.BeginHorizontal();
                animatedProperty.type = (AnimatedProperty.Type)EditorGUILayout.EnumPopup(animatedProperty.type, GUILayout.Width(100));
                for (int i = 0; i < animatedProperty.values.Count; i++)
                {
                    switch (animatedProperty.type)
                    {
                        case AnimatedProperty.Type.Float:
                            animatedProperty.values[i] = Vector4.one * EditorGUILayout.FloatField(GUIContent.none, animatedProperty.values[i].x, GUILayout.Width(100));
                            break;
                        case AnimatedProperty.Type.Vector:
                            animatedProperty.values[i] = EditorGUILayout.Vector4Field(GUIContent.none, animatedProperty.values[i], GUILayout.Width(400));
                            break;
                        case AnimatedProperty.Type.Color:
                            animatedProperty.values[i] = EditorGUILayout.ColorField(GUIContent.none, animatedProperty.values[i], true, true, false, GUILayout.Width(100));
                            break;
                        case AnimatedProperty.Type.ColorHDR:
                            animatedProperty.values[i] = EditorGUILayout.ColorField(GUIContent.none, animatedProperty.values[i], true, true, true, GUILayout.Width(100));
                            break;
                    }
                }
                if (isInline)
                    EditorGUILayout.EndHorizontal();
                EditorGUILayout.EndHorizontal();
            }
            EditorGUILayout.EndVertical();
            GUILayout.Space(8);
        }
        if (toRemove != null)
            animatedProperties.Remove(toRemove);
        EditorGUILayout.EndVertical();

        string errorMsg = CanCreateAnimations();
        GUI.enabled = errorMsg == "";
        if (GUILayout.Button("Create" + ((errorMsg == "") ? "" : " (" + errorMsg + ")")))
        {
            string animFolder = GetAnimationsFolderPath();
            if (!AssetDatabase.IsValidFolder(animFolder))
                AssetDatabase.CreateFolder(animFolder.Substring(0, animFolder.LastIndexOf("/")), "Animations");

            var vrcParam = new VRCExpressionParameters.Parameter() {
                name = ParameterName,
                defaultValue = 0.0f,
                saved = true,
                valueType = animationType == AnimationType.Toggle ? VRCExpressionParameters.ValueType.Bool : VRCExpressionParameters.ValueType.Float
            };

            descriptor.expressionParameters.parameters = descriptor.expressionParameters.parameters
                .Union(new VRCExpressionParameters.Parameter[] { vrcParam }).ToArray();
            EditorUtility.SetDirty(descriptor.expressionParameters);

            var fxLayer = descriptor.baseAnimationLayers[4].animatorController as AnimatorController;
            fxLayer.AddParameter(new AnimatorControllerParameter() {
                name = ParameterName,
                type = AnimatorControllerParameterType.Float,
                defaultFloat = 0.0f
            });

            var root = descriptor.transform;
            var clip = new AnimationClip();
            clip.name = ParameterName + " MotionTime";
            clip.frameRate = 60;

            foreach (var renderer in renderers) {
                var pathToRoot = AnimationUtility.CalculateTransformPath(renderer.transform, root);
                foreach (var animatedProperty in animatedProperties) {
                    var vectorEnd = animatedProperty.type == AnimatedProperty.Type.Vector ? new [] { ".x", ".y", ".z", ".w" } 
                        : animatedProperty.type == AnimatedProperty.Type.Float ?  new [] { "" } : new [] { ".r", ".g", ".b", ".a" };
                    var values = animatedProperty.values;
                    for (int i = 0; i < vectorEnd.Length; i++) {
                        var property = $"material.{animatedProperty.name}{vectorEnd[i]}";
                        AnimationCurve curve = null;
                        switch (animationType) {
                            case AnimationType.Toggle:
                            case AnimationType.RadialLinear:
                                curve = AnimationCurve.Linear(0, values[0][i], 100f / 60f, values[1][i]);
                                break;
                            case AnimationType.RadialLogLinear:
                                curve = new AnimationCurve(new Keyframe[] {
                                    CreateKeyframe(0, values[0][i], values[1][i], LogLerp),
                                    CreateKeyframe(0.25f, values[0][i], values[1][i], LogLerp),
                                    CreateKeyframe(0.5f, values[0][i], values[1][i], LogLerp),
                                    CreateKeyframe(0.75f, values[0][i], values[1][i], LogLerp),
                                    CreateKeyframe(1, values[0][i], values[1][i], LogLerp)
                                });
                                break;
                            case AnimationType.RadialSquared:
                                curve = new AnimationCurve(new Keyframe[] {
                                    CreateKeyframe(0, values[0][i], values[1][i], SquareLerp),
                                    CreateKeyframe(0.25f, values[0][i], values[1][i], SquareLerp),
                                    CreateKeyframe(0.5f, values[0][i], values[1][i], SquareLerp),
                                    CreateKeyframe(0.75f, values[0][i], values[1][i], SquareLerp),
                                    CreateKeyframe(1, values[0][i], values[1][i], SquareLerp)
                                });
                                break;
                            case AnimationType.RadialRoot:
                                curve = new AnimationCurve(new Keyframe[] {
                                    CreateKeyframe(0, values[0][i], values[1][i], RootLerp),
                                    CreateKeyframe(0.25f, values[0][i], values[1][i], RootLerp),
                                    CreateKeyframe(0.5f, values[0][i], values[1][i], RootLerp),
                                    CreateKeyframe(0.75f, values[0][i], values[1][i], RootLerp),
                                    CreateKeyframe(1, values[0][i], values[1][i], RootLerp)
                                });
                                break;
                        }
                        AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(pathToRoot, renderer.GetType(), property), curve);
                    }
                }
            }

            AssetDatabase.CreateAsset(clip, $"{animFolder}/{clip.name}.anim");

            var layer = new AnimatorControllerLayer();
            layer.name = ParameterName;
            layer.stateMachine = new AnimatorStateMachine();
            layer.stateMachine.name = ParameterName;
            layer.stateMachine.hideFlags = HideFlags.HideInHierarchy;
            layer.defaultWeight = 1.0f;
            layer.avatarMask = null;

            var state = new AnimatorState();
            state.motion = clip;
            state.name = ParameterName;
            state.writeDefaultValues = false;
            state.timeParameterActive = true;
            state.timeParameter = ParameterName;
            state.hideFlags = HideFlags.HideInHierarchy;

            layer.stateMachine.AddState(state, new Vector3(300, 120, 0));

            var fxLayerPath = AssetDatabase.GetAssetPath(descriptor.baseAnimationLayers[4].animatorController);
            fxLayer.AddLayer(layer);
            AssetDatabase.AddObjectToAsset(state, fxLayerPath);
            AssetDatabase.AddObjectToAsset(layer.stateMachine, fxLayerPath);

            if (animationType == AnimationType.Toggle) {
                TargetMenu.controls.Add(new VRCExpressionsMenu.Control() {
                    name = ParameterName,
                    parameter = new VRCExpressionsMenu.Control.Parameter() { name = ParameterName },
                    type = VRCExpressionsMenu.Control.ControlType.Toggle
                });
            } else {
                TargetMenu.controls.Add(new VRCExpressionsMenu.Control() {
                    name = ParameterName,
                    subParameters = new VRCExpressionsMenu.Control.Parameter[] {
                        new VRCExpressionsMenu.Control.Parameter() { name = ParameterName }
                    },
                    type = VRCExpressionsMenu.Control.ControlType.RadialPuppet
                });
            }
            EditorUtility.SetDirty(TargetMenu);
            AssetDatabase.SaveAssets();
        }
        GUI.enabled = true;

        GUILayout.Space(8);

        EditorGUILayout.LabelField("Avatar", descriptor?.name);
        if (descriptor != null) {
            EditorGUILayout.LabelField("AnimationFolder", GetAnimationsFolderPath());
            EditorGUILayout.LabelField("ParamAssetPath", AssetDatabase.GetAssetPath(descriptor.expressionParameters));
            EditorGUILayout.LabelField("MenuAssetPath", AssetDatabase.GetAssetPath(descriptor.expressionsMenu));
            EditorGUILayout.LabelField("FxLayerAssetPath", AssetDatabase.GetAssetPath(descriptor.baseAnimationLayers[4].animatorController));
        }
        EditorGUILayout.EndScrollView();
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

    [MenuItem("GameObject/Create AV3 Material Animation", false, -1)]
    public static void CreateAV3MaterialAnimationMenuItem()
    {
        var window = GetWindow(typeof(CreateAV3MaterialAnimationMenu)) as CreateAV3MaterialAnimationMenu;
        window.target = FindAvatarDescriptor(Selection.gameObjects[0])?.gameObject;
        window.renderers = Selection.gameObjects.Select(go => go.GetComponent<Renderer>()).Where(r => r != null).ToList();
    }

    [MenuItem("GameObject/Create AV3 Material Animation", true, -1)]
    public static bool CreateAV3MaterialAnimationMenuItemValidation()
    {
        if (Selection.gameObjects.Length == 0)
            return false;
        return FindAvatarDescriptor(Selection.gameObjects[0]) != null;
    }
}
#endif