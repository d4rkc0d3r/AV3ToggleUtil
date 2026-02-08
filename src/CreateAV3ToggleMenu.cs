#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEditor;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using UnityEditor.Animations;
using Type = System.Type;
using d4rkpl4y3r.AV3ToggleUtil.Util;

public class CreateAV3ToggleMenu : EditorWindow
{
    private Dictionary<EditorCurveBinding, (float offValue, float onValue)> bindingsToToggle = new();
    private bool defaultToggleState = false;
    private TextFilter bindingFilter = new();
    private Component componentToSelectBindingFrom = null;
    private Vector2 scrollPos;
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
            bindingsToToggle.Clear();
            cachedAnimatableBindings.Clear();
            componentToSelectBindingFrom = null;
            if (Target == null)
                return;
            defaultToggleState = Target.activeSelf;
        }
    }
    private string toggleName = "";
    public string ToggleName
    {
        get { return toggleName == "" ? GetDefaultToggleName() : toggleName; }
    }
    private VRCExpressionsMenu targetMenu = null;
    public VRCExpressionsMenu TargetMenu
    {
        get { return targetMenu == null ? GetMainMenu() : targetMenu; }
        set { targetMenu = (value == GetMainMenu()) ? null : value; }
    }

    private string parameterName = "";
    public string ParameterName
    {
        get { return parameterName == "" ? GetDefaultParameterName() : parameterName; }
    }

    public VRCExpressionsMenu GetMainMenu()
    {
        return FindAvatarDescriptor(Target)?.expressionsMenu;
    }

    public string GetDefaultToggleName() => Target.name;

    public string GetDefaultParameterName()
    {
        var text = ToggleName.Replace(" ", "");
        text = string.IsNullOrEmpty(text) ? GetDefaultToggleName() : text;
        var bindingTypes = bindingsToToggle.Select(b => b.Key.type.Name).Distinct().ToArray();
        if (bindingTypes.Length == 1 && bindingTypes[0] != "GameObject")
        {
            text += bindingTypes[0];
        }
        return text;
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
        if (AssetDatabase.LoadAssetAtPath<AnimationClip>($"{path}/{ToggleName}On.anim") != null)
            return "Toggle On Animation Exists Already";
        if (AssetDatabase.LoadAssetAtPath<AnimationClip>($"{path}/{ToggleName}Off.anim") != null)
            return "Toggle Off Animation Exists Already";
        if (bindingsToToggle.Count == 0)
            return "No Bindings Selected";
        return "";
    }

    void DrawBindingsToToggle()
    {
        var bindingLayout = GUILayout.ExpandWidth(true);
        var valueLayout = GUILayout.Width(60);
        var invertLayout = GUILayout.Width(40);
        float spacing = 10;
        float removeButtonWidth = 20;

        using var verticalScope = new EditorGUILayout.VerticalScope("box");
        using (new EditorGUILayout.HorizontalScope())
        {
            GUILayout.Label("Binding", bindingLayout);
            GUILayout.Space(spacing);
            GUILayout.Label("Off Value", valueLayout);
            GUILayout.Space(spacing);
            GUILayout.Label("On Value", valueLayout);
            GUILayout.Space(spacing);
            GUILayout.Label("", invertLayout);
            GUILayout.Space(removeButtonWidth);
        }
        foreach (var pair in bindingsToToggle.OrderBy(pair => pair.Key.type.Name).ThenBy(pair => pair.Key.propertyName).ToArray())
        {
            using var horizontalScope = new EditorGUILayout.HorizontalScope();
            bool isToggle = pair.Key.propertyName == "m_IsActive" || pair.Key.propertyName == "m_Enabled";
            float FloatOrToggleField(float value)
            {
                using var disabledScope = new EditorGUI.DisabledScope(isToggle);
                if (isToggle)
                {
                    return EditorGUILayout.Toggle(value > 0.5f, valueLayout) ? 1.0f : 0.0f;
                }
                else
                {
                    return EditorGUILayout.FloatField(value, valueLayout);
                }
            }
            GUILayout.Label($"{pair.Key.type.Name}:{pair.Key.propertyName}", bindingLayout);
            GUILayout.Space(spacing);
            float newOffValue = FloatOrToggleField(pair.Value.offValue);
            GUILayout.Space(spacing);
            float newOnValue = FloatOrToggleField(pair.Value.onValue);
            GUILayout.Space(spacing);
            if (GUILayout.Button("Flip", invertLayout))
            {
                newOffValue = pair.Value.onValue;
                newOnValue = pair.Value.offValue;
            }
            if (newOffValue != pair.Value.offValue || newOnValue != pair.Value.onValue)
            {
                bindingsToToggle[pair.Key] = (newOffValue, newOnValue);
            }
            if (GUILayout.Button("X", GUILayout.Width(removeButtonWidth)))
            {
                bindingsToToggle.Remove(pair.Key);
            }
        }
    }

    EditorCurveBinding GetComponentToggleBinding(Component component)
    {
        return new EditorCurveBinding()
        {
            path = AnimationUtility.CalculateTransformPath(component.transform, FindAvatarDescriptor(Target).transform),
            propertyName = component is Transform ? "m_IsActive" : "m_Enabled",
            type = component is Transform ? typeof(GameObject) : component.GetType()
        };
    }

    string TextFieldWithDefault(string label, string text, string defaultText)
    {
        text = EditorGUILayout.TextField(label, text);
        if (string.IsNullOrEmpty(text))
        {
            var rect = GUILayoutUtility.GetLastRect();
            rect.x += EditorGUIUtility.labelWidth + 3;
            var prevColor = GUI.contentColor;
            GUI.contentColor = Color.gray;
            GUI.Label(rect, defaultText);
            GUI.contentColor = prevColor;
        }
        return text;
    }

    void OnGUI()
    {
        using var scrollView = new EditorGUILayout.ScrollViewScope(scrollPos);
        scrollPos = scrollView.scrollPosition;
        Target = EditorGUILayout.ObjectField("Target", Target, typeof(GameObject), true) as GameObject;

        if (Target == null)
            return;

        foreach (var component in Target.GetComponents<Component>())
        {
            using var _ = new EditorGUILayout.HorizontalScope();
            var componentName = component.GetType().Name;
            if (componentName == "Transform")
                componentName = "GameObject";
            else if (GetAnimatableBindings(component).Count == 0)
                continue;
            GUILayout.Label(componentName, GUILayout.Width(EditorGUIUtility.labelWidth - 3));
            var toggleBinding = GetComponentToggleBinding(component);
            bool isAlreadyIncluded = bindingsToToggle.ContainsKey(toggleBinding);
            bool shouldBeIncluded = GUILayout.Toggle(isAlreadyIncluded, "Toggle", GUI.skin.button, GUILayout.ExpandWidth(false));
            if (shouldBeIncluded && !isAlreadyIncluded)
            {
                bindingsToToggle.Add(toggleBinding, (0, 1));
            }
            else if (!shouldBeIncluded && isAlreadyIncluded)
            {
                bindingsToToggle.Remove(toggleBinding);
            }
            GUILayout.Space(10);
            if (GetAnimatableBindings(component).Any(b => b.propertyName != "m_Enabled"))
            {
                bool isCurrentlySelectedComponent = component == componentToSelectBindingFrom;
                bool isSelectedComponent = GUILayout.Toggle(isCurrentlySelectedComponent, "Search Bindings", GUI.skin.button, GUILayout.ExpandWidth(false));
                if (isSelectedComponent && !isCurrentlySelectedComponent)
                {
                    componentToSelectBindingFrom = component;
                }
                else if (!isSelectedComponent && isCurrentlySelectedComponent)
                {
                    componentToSelectBindingFrom = null;
                }
            }
        }

        if (componentToSelectBindingFrom != null)
        {
            GUILayout.Space(8);
            using var box = new EditorGUILayout.VerticalScope("box");
            GUILayout.Label($"Search bindings for {componentToSelectBindingFrom.GetType().Name}:", EditorStyles.boldLabel);
            using var indentScope = new EditorGUI.IndentLevelScope();
            using var cc = new EditorGUI.ChangeCheckScope();
            bindingFilter.DrawGUI("Binding Filter");
            if (cc.changed)
            {
                cachedAnimatableBindings.Clear();
            }
            GUILayout.Space(8);
            var bindings = GetAnimatableBindings(componentToSelectBindingFrom).Where(b => bindingFilter.Matches(b.propertyName)).ToArray();
            foreach (var binding in bindings)
            {
                using var horizontalScope = new EditorGUILayout.HorizontalScope();
                GUILayout.Space(15);
                bool isAlreadyIncluded = bindingsToToggle.ContainsKey(binding);
                bool shouldBeIncluded = GUILayout.Toggle(isAlreadyIncluded, "Select", GUI.skin.button, GUILayout.ExpandWidth(false));
                GUILayout.Space(10);
                GUILayout.Label($"{binding.propertyName}");
                if (shouldBeIncluded && !isAlreadyIncluded)
                {
                    bindingsToToggle.Add(binding, (0, binding.propertyName.StartsWith("blendShape") ? 100 : 1));
                }
                else if (!shouldBeIncluded && isAlreadyIncluded)
                {
                    bindingsToToggle.Remove(binding);
                }
            }
        }

        GUILayout.Space(8);

        toggleName = TextFieldWithDefault("Toggle Name", toggleName, GetDefaultToggleName());
        parameterName = TextFieldWithDefault("Parameter Name", parameterName, GetDefaultParameterName());

        var descriptor = FindAvatarDescriptor(Target);
        TargetMenu = EditorGUILayout.ObjectField("Menu", TargetMenu, typeof(VRCExpressionsMenu), false) as VRCExpressionsMenu;

        defaultToggleState = EditorGUILayout.Toggle("Default Toggle State", defaultToggleState);

        GUILayout.Space(8);

        string errorMsg = CanCreateToggle();
        GUI.enabled = errorMsg == "";
        if (GUILayout.Button("Create" + ((errorMsg == "") ? "" : " (" + errorMsg + ")")))
        {
            string animFolder = GetAnimationsFolderPath();
            if (!AssetDatabase.IsValidFolder(animFolder))
                AssetDatabase.CreateFolder(animFolder[..animFolder.LastIndexOf("/")], "Animations");
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
            foreach (var pair in bindingsToToggle)
            {
                EditorCurveBinding binding = pair.Key;
                var curveOn = new AnimationCurve();
                curveOn.AddKey(0, pair.Value.onValue);
                curveOn.AddKey(1 / 60f, pair.Value.onValue);
                AnimationUtility.SetEditorCurve(clipOn, binding, curveOn);
                var curveOff = new AnimationCurve();
                curveOff.AddKey(0, pair.Value.offValue);
                curveOff.AddKey(1 / 60f, pair.Value.offValue);
                AnimationUtility.SetEditorCurve(clipOff, binding, curveOff);
            }
            AssetDatabase.CreateAsset(clipOn, $"{animFolder}/{clipOn.name}.anim");
            AssetDatabase.CreateAsset(clipOff, $"{animFolder}/{clipOff.name}.anim");

            var param = new VRCExpressionParameters.Parameter()
            {
                name = ToggleName,
                defaultValue = defaultToggleState ? 1.0f : 0.0f,
                saved = true,
                valueType = VRCExpressionParameters.ValueType.Bool
            };

            descriptor.expressionParameters.parameters = descriptor.expressionParameters.parameters
                .Union(new VRCExpressionParameters.Parameter[] { param }).ToArray();
            EditorUtility.SetDirty(descriptor.expressionParameters);
            AssetDatabase.SaveAssets();

            var fxLayer = descriptor.baseAnimationLayers[4].animatorController as AnimatorController;
            fxLayer.AddParameter(new AnimatorControllerParameter()
            {
                name = ToggleName,
                type = AnimatorControllerParameterType.Bool,
                defaultBool = defaultToggleState
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
            transitionToOn.duration = 0.0f;
            transitionToOn.AddCondition(AnimatorConditionMode.If, 0, ToggleName);
            transitionToOn.hideFlags = HideFlags.HideInHierarchy;
            toggleOff.AddTransition(transitionToOn);

            var transitionToOff = new AnimatorStateTransition();
            transitionToOff.canTransitionToSelf = false;
            transitionToOff.destinationState = toggleOff;
            transitionToOff.hasFixedDuration = true;
            transitionToOff.hasExitTime = false;
            transitionToOff.duration = 0.0f;
            transitionToOff.AddCondition(AnimatorConditionMode.IfNot, 0, ToggleName);
            transitionToOff.hideFlags = HideFlags.HideInHierarchy;
            toggleOn.AddTransition(transitionToOff);

            if (defaultToggleState)
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
                name = ParameterName,
                parameter = new VRCExpressionsMenu.Control.Parameter() { name = ToggleName },
                type = VRCExpressionsMenu.Control.ControlType.Toggle
            });
            EditorUtility.SetDirty(TargetMenu);
            AssetDatabase.SaveAssets();
        }
        GUI.enabled = true;

        GUILayout.Space(8);

        DrawBindingsToToggle();

        GUILayout.Space(8);

        if (GetMainMenu() != null)
        {
            void DrawMenuWithSubMenus(VRCExpressionsMenu menu, int indent, HashSet<VRCExpressionsMenu> drawnMenus = null)
            {
                drawnMenus ??= new HashSet<VRCExpressionsMenu>();
                if (!drawnMenus.Add(menu))
                    return;
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Space(indent * 15);
                    EditorGUILayout.ObjectField(menu, typeof(VRCExpressionsMenu), false);
                    if (GUILayout.Toggle(TargetMenu == menu, "S", GUI.skin.button, GUILayout.Width(20)))
                    {
                        TargetMenu = menu;
                    }
                    var prevColor = GUI.contentColor;
                    if (menu.controls.Count >= 8)
                        GUI.contentColor = Color.yellow;
                    GUILayout.Label($"({menu.controls.Count}/8)", GUILayout.Width(30));
                    GUI.contentColor = prevColor;
                }
                foreach (var control in menu.controls)
                {
                    if (control.type == VRCExpressionsMenu.Control.ControlType.SubMenu && control.subMenu != null)
                    {
                        DrawMenuWithSubMenus(control.subMenu, indent + 1, drawnMenus);
                    }
                }
            }
            using (new EditorGUILayout.VerticalScope("box"))
            {
                DrawMenuWithSubMenus(GetMainMenu(), 0);
            }
            GUILayout.Space(8);
        }

        EditorGUILayout.LabelField("Avatar", descriptor?.name);
        if (descriptor == null)
            return;
        EditorGUILayout.LabelField("AnimationFolder", GetAnimationsFolderPath());
        EditorGUILayout.LabelField("ParamAssetPath", AssetDatabase.GetAssetPath(descriptor.expressionParameters));
        EditorGUILayout.LabelField("MenuAssetPath", AssetDatabase.GetAssetPath(descriptor.expressionsMenu));
        EditorGUILayout.LabelField("FxLayerAssetPath", AssetDatabase.GetAssetPath(descriptor.baseAnimationLayers[4].animatorController));
    }

    private static readonly HashSet<char> vectorChars = new() { 'x', 'y', 'z', 'w', 'r', 'g', 'b', 'a' };
    
    private Dictionary<Component, List<EditorCurveBinding>> cachedAnimatableBindings = new();
    private List<EditorCurveBinding> GetAnimatableBindings(Component component) {
        if (cachedAnimatableBindings.TryGetValue(component, out var bindings))
            return bindings;
        bindings = new List<EditorCurveBinding>();
        foreach (var animatableBinding in AnimationUtility.GetAnimatableBindings(component.gameObject, FindAvatarDescriptor(component.gameObject).gameObject))
        {
            var propName = animatableBinding.propertyName;
            if (propName.StartsWith("material."))
                continue;
            if (animatableBinding.type != component.GetType())
                continue;
            if (animatableBinding.isPPtrCurve)
                continue;
            if (propName.Length > 2 && propName[^2] == '.' && vectorChars.Contains(propName[^1]))
                continue;
            bindings.Add(animatableBinding);
        }
        return cachedAnimatableBindings[component] = bindings;
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
        var window = GetWindow<CreateAV3ToggleMenu>();
        window.Target = Selection.activeObject as GameObject;
        window.titleContent = new GUIContent("Create AV3 Toggle");
        window.Show();
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