#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEditor;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using UnityEditor.Animations;
using d4rkpl4y3r.AV3ToggleUtil.Util;
using System.Text.RegularExpressions;
using BitConverter = System.BitConverter;

public class CreateAV3ToggleMenu : EditorWindow
{
    private Dictionary<EditorCurveBinding, (Vector4 offValue, Vector4 onValue)> bindingsToToggle = new();
    private List<EditorCurveBinding> filteredBindingCache = null;
    private bool updateTargetWithCurrentSelection = true;
    private bool defaultToggleState = false;
    private bool savedParameter = true;
    private bool syncedParameter = true;
    private TextFilter bindingFilter = new() { Text = "^(?!material\\.)" };
    private Component componentToSelectBindingFrom = null;
    private Vector2 scrollPos;
    private const int bindingsPerPage = 20;
    private int tabbedBindingPage = 0;
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
            filteredBindingCache = null;
            componentToSelectBindingFrom = null;
            cache_GetExistingAnimationsForBinding = null;
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

    public string GetDefaultToggleName()
    {
        var text = Target.name;
        if (bindingsToToggle.Count == 1)
        {
            var extraText = bindingsToToggle.First().Key.propertyName;
            if (extraText.StartsWith("material."))
                extraText = extraText["material.".Length..].TrimStart('_');
            if (extraText.EndsWith(".x") || extraText.EndsWith(".r"))
                extraText = extraText[..^2];
            text += " " + extraText switch
            {
                var s when s == "m_IsActive" => "",
                var s when s == "m_Enabled" => bindingsToToggle.First().Key.type.Name,
                var s when s.StartsWith("m_") => s[2..],
                var s when s.Contains('.') => s[(s.LastIndexOf('.') + 1)..].TrimStart('_'),
                var s => s
            };
        }
        return text;
    }

    public string GetDefaultParameterName()
    {
        var text = ToggleName.Replace(" ", "");
        text = string.IsNullOrEmpty(text) ? GetDefaultToggleName() : text;
        return text;
    }

    private bool ClickableLastRect()
    {
        var rect = GUILayoutUtility.GetLastRect();
        EditorGUIUtility.AddCursorRect(rect, MouseCursor.Link);
        var clicked = Event.current.type == EventType.MouseDown && rect.Contains(Event.current.mousePosition) && Event.current.button == 0;
        if (clicked)
        {
            Event.current.Use();
        }
        return clicked;
    }

    private static string TrimAfterLastSlash(string path)
    {
        int lastSlash = path.LastIndexOf("/");
        return lastSlash >= 0 ? path[..lastSlash] : path;
    }

    private static string GetAssetFolder(Object asset)
    {
        return TrimAfterLastSlash(AssetDatabase.GetAssetPath(asset));
    }

    private string GetAnimationsFolderPath()
    {
        const string animationsFolderName = "Animations";
        var av = FindAvatarDescriptor(Target);
        var path = $"{GetAssetFolder(av.baseAnimationLayers[4].animatorController)}/{animationsFolderName}";
        if (AssetDatabase.IsValidFolder(path) && path != $"/{animationsFolderName}")
            return path;
        path = $"{GetAssetFolder(av.expressionParameters)}/{animationsFolderName}";
        if (AssetDatabase.IsValidFolder(path))
            return path;
        path = $"{GetAssetFolder(av.expressionsMenu)}/{animationsFolderName}";
        if (AssetDatabase.IsValidFolder(path))
            return path;
        return $"{GetAssetFolder(av.baseAnimationLayers[4].animatorController)}/{animationsFolderName}";
    }

    private string CanCreateToggle()
    {
        var av = FindAvatarDescriptor(Target);
        if (av == null)
            return "No Avatar Descriptor Found";
        if (AssetDatabase.GetAssetPath(av.expressionParameters) == "")
            return "No Custom Parameters Found";
        if (AssetDatabase.GetAssetPath(av.expressionsMenu) == "")
            return "No Custom Menu Found";
        if (TargetMenu.controls.Count >= 8)
            return "Target Menu Is Full Already";
        var fxLayer = av.baseAnimationLayers[4].animatorController as AnimatorController;
        if (AssetDatabase.GetAssetPath(fxLayer) == "")
            return "No Custom FxLayer Found";
        if (av.expressionParameters.FindParameter(ParameterName) != null)
            return "Parameter Exists Already";
        if (fxLayer.layers.Any(l => l.name == ToggleName))
            return "Layer Exists Already";
        if (fxLayer.parameters.Any(p => p.name == ToggleName))
            return "Layer Parameter Exists Already";
        var path = GetAnimationsFolderPath();
        if (AssetDatabase.LoadAssetAtPath<AnimationClip>($"{path}/{ToggleName} On.anim") != null)
            return "Toggle On Animation Exists Already";
        if (AssetDatabase.LoadAssetAtPath<AnimationClip>($"{path}/{ToggleName} Off.anim") != null)
            return "Toggle Off Animation Exists Already";
        if (bindingsToToggle.Count == 0)
            return "No Bindings Selected";
        return "";
    }

    private readonly HashSet<string> knownToggleProperties = new() {
        "m_IsActive", "m_Enabled",
        // SkinnedMeshRenderer
        "m_UpdateWhenOffscreen",
        "m_ReceiveShadows",
        "m_SkinnedMotionVectors",
        // VRCParentConstraint
        "IsActive",
        "SolveInLocalSpace",
        "FreezeToWorld",
        "RebakeOffsetsWhenUnfrozen",
        "Locked",
        "AffectsPositionX", "AffectsPositionY", "AffectsPositionZ",
        "AffectsRotationX", "AffectsRotationY", "AffectsRotationZ",
    };

    void DrawBindingsToToggle()
    {
        var bindingLayout = GUILayout.ExpandWidth(true);
        var valueLayout = GUILayout.Width(60);
        var invertLayout = GUILayout.Width(40);
        var removeButtonLayout = GUILayout.Width(20);
        float spacing = 10;

        using var verticalScope = new EditorGUILayout.VerticalScope("box");
        using (new EditorGUILayout.HorizontalScope())
        {
            GUILayout.Label("Binding", bindingLayout);
            GUILayout.Label("Off Value", valueLayout);
            GUILayout.Space(spacing);
            GUILayout.Label("On Value", valueLayout);
            GUILayout.Space(spacing);
            using var _ = new EditorGUI.DisabledScope(bindingsToToggle.Count == 0);
            if (GUILayout.Button("Flip", invertLayout))
            {
                foreach (var pair in bindingsToToggle.ToArray())
                {
                    bindingsToToggle[pair.Key] = (pair.Value.onValue, pair.Value.offValue);
                }
            }
            if (GUILayout.Button("X", removeButtonLayout))
            {
                bindingsToToggle.Clear();
            }
        }
        foreach ((var binding, var values) in bindingsToToggle.OrderBy(pair => pair.Key.type.Name).ThenBy(pair => pair.Key.propertyName).ToArray())
        {
            using var horizontalScope = new EditorGUILayout.HorizontalScope();
            bool isToggle = knownToggleProperties.Contains(binding.propertyName);
            bool isColor = binding.propertyName.EndsWith(".r");
            Vector4 ValueField(Vector4 value)
            {
                if (isColor)
                {
                    return ColorField(value, valueLayout);
                }
                float newValue = isToggle
                    ? EditorGUILayout.Toggle(value.x > 0.5f, valueLayout) ? 1 : 0
                    : EditorGUILayout.FloatField(value.x, valueLayout);
                return new Vector4(newValue, 0, 0, 0);
            }
            var displayName = isColor ? binding.propertyName[..^2] : binding.propertyName;
            GUILayout.Label($"{binding.type.Name}:{displayName}", bindingLayout);
            ShowWarningIfBindingExists(binding, GUILayout.Width(20));
            using var cc = new EditorGUI.ChangeCheckScope();
            var newOffValue = ValueField(values.offValue);
            GUILayout.Space(spacing);
            var newOnValue = ValueField(values.onValue);
            GUILayout.Space(spacing);
            if (GUILayout.Button("Flip", invertLayout) || (isToggle && cc.changed))
            {
                newOffValue = values.onValue;
                newOnValue = values.offValue;
            }
            if (newOffValue != values.offValue || newOnValue != values.onValue)
            {
                bindingsToToggle[binding] = (newOffValue, newOnValue);
            }
            if (GUILayout.Button("X", removeButtonLayout))
            {
                bindingsToToggle.Remove(binding);
            }
        }
    }

    Dictionary<EditorCurveBinding, List<(AnimatorController controller, AnimationClip clip, string layerName, int layerID)>>
    cache_GetExistingAnimationsForBinding = null;

    List<(AnimatorController controller, AnimationClip clip, string layerName, int layerID)>
    GetExistingAnimationsForBinding(EditorCurveBinding binding)
    {
        if (cache_GetExistingAnimationsForBinding == null)
        {
            cache_GetExistingAnimationsForBinding = new();

            var av = FindAvatarDescriptor(Target);
            if (av == null)
                return new();

            void AddBinding(EditorCurveBinding b, AnimatorController controller, AnimationClip clip, string layerName, int layerID)
            {
                if (!cache_GetExistingAnimationsForBinding.TryGetValue(b, out var list))
                {
                    list = new List<(AnimatorController controller, AnimationClip clip, string layerName, int layerID)>();
                    cache_GetExistingAnimationsForBinding[b] = list;
                }
                if (!list.Any(x => x.controller == controller && x.layerID == layerID && x.clip == clip))
                    list.Add((controller, clip, layerName, layerID));
            }

            void ProcessClip(AnimationClip clip, AnimatorController controller, string layerName, int layerID)
            {
                if (clip == null)
                    return;
                foreach (var b in AnimationUtility.GetCurveBindings(clip))
                    AddBinding(b, controller, clip, layerName, layerID);
            }

            void CollectFromMotion(Motion motion, AnimatorController controller, string layerName, int layerID)
            {
                if (motion == null)
                    return;
                if (motion is AnimationClip clip)
                {
                    ProcessClip(clip, controller, layerName, layerID);
                    return;
                }
                if (motion is BlendTree tree)
                {
                    foreach (var child in tree.children)
                        CollectFromMotion(child.motion, controller, layerName, layerID);
                }
            }

            void CollectFromStateMachine(AnimatorStateMachine sm, AnimatorController controller, string layerName, int layerID)
            {
                if (sm == null)
                    return;
                foreach (var state in sm.states)
                    CollectFromMotion(state.state.motion, controller, layerName, layerID);
                foreach (var child in sm.stateMachines)
                    CollectFromStateMachine(child.stateMachine, controller, layerName, layerID);
            }

            foreach (var controller in av.baseAnimationLayers.Concat(av.specialAnimationLayers)
                         .Select(l => l.animatorController as AnimatorController)
                         .Where(c => c != null)
                         .Distinct())
            {
                var layers = controller.layers;
                for (int i = 0; i < layers.Length; i++)
                {
                    var sm = layers[i].stateMachine;
                    if (sm == null)
                        continue;
                    CollectFromStateMachine(sm, controller, layers[i].name, i);
                }
            }
        }

        return cache_GetExistingAnimationsForBinding.TryGetValue(binding, out var result) ? result : new();
    }

    void ShowWarningIfBindingExists(EditorCurveBinding binding, params GUILayoutOption[] layoutOptions)
    {
        var prevIndent = EditorGUI.indentLevel;
        EditorGUI.indentLevel = 0;
        var rect = EditorGUILayout.GetControlRect(layoutOptions);
        EditorGUI.indentLevel = prevIndent;
        var existing = GetExistingAnimationsForBinding(binding);
        if (existing.Count > 0)
        {
            var groupedByLayer = existing.GroupBy(x => (x.controller, x.layerID))
                .Select(g => (g.Key.controller, g.Key.layerID, clips: g.Select(x => x.clip)
                .ToArray(), g.First().layerName));
            var icon = EditorGUIUtility.IconContent("console.warnicon.sml");
            icon.tooltip = "This binding already exists in:\n" +
            string.Join("\n", groupedByLayer.Select(g => $"{g.controller.name}/{g.layerName}:\n  {string.Join("\n  ", g.clips.Select(c => c.name))}"));
            GUI.Label(rect, icon);
        }
    }

    EditorCurveBinding GetComponentToggleBinding(Component component)
    {
        return EditorCurveBinding.FloatCurve(
            AnimationUtility.CalculateTransformPath(component.transform, FindAvatarDescriptor(Target).transform),
            component is Transform ? typeof(GameObject) : component.GetType(),
            component is Transform ? "m_IsActive" : "m_Enabled");
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

    (Vector4 offValue, Vector4 onValue) GetDefaultValuesForBinding(EditorCurveBinding binding)
    {
        Vector4 currentValue = Vector4.zero;
        var avGO = FindAvatarDescriptor(Target).gameObject;
        if (AnimationUtility.GetFloatValue(avGO, binding, out var sceneValue))
        {
            currentValue = new Vector4(sceneValue, sceneValue, sceneValue, sceneValue);
            var vectorBindings = GetRemainingVectorBindings(binding).ToArray();
            for (int i = 0; i < vectorBindings.Length; i++)
            {
                if (AnimationUtility.GetFloatValue(avGO, vectorBindings[i], out var v))
                {
                    currentValue[i + 1] = v;
                }
            }
        }
        if (!currentValue.Equals(Vector4.zero))
        {
            if (binding.propertyName.EndsWith(".r"))
                return (Color.black, currentValue);
            return (Vector4.zero, currentValue);
        }
        if (binding.propertyName.StartsWith("blendShape"))
        {
            return (Vector4.zero, Vector4.one * 100);
        }
        return (Vector4.zero, Vector4.one);
    }

    IEnumerable<EditorCurveBinding> GetRemainingVectorBindings(EditorCurveBinding binding)
    {
        var name = binding.propertyName;
        if (!name.EndsWith(".x") && !name.EndsWith(".r"))
            yield break;
        var prefix = name[..^2];
        var suffixes = name.EndsWith(".x") ? new[] { ".y", ".z", ".w" } : new[] { ".g", ".b", ".a" };
        foreach (var suffix in suffixes)
        {
            binding.propertyName = prefix + suffix;
            if (GetAnimatableBindings(Target.GetComponent(binding.type)).Contains(binding))
                yield return binding;
        }
    }

    Color ColorField(Color color, params GUILayoutOption[] layoutOptions)
    {
        var prevIndent = EditorGUI.indentLevel;
        EditorGUI.indentLevel = 0;
        var rect = EditorGUILayout.GetControlRect(layoutOptions);
        EditorGUI.indentLevel = prevIndent;
        return EditorGUI.ColorField(rect, GUIContent.none, color, showEyedropper:false, showAlpha:true, hdr:true);
    }

    void OnGUI()
    {
        using var scrollView = new EditorGUILayout.ScrollViewScope(scrollPos);
        scrollPos = scrollView.scrollPosition;
        using (new EditorGUILayout.HorizontalScope())
        {
            Target = EditorGUILayout.ObjectField("Target", Target, typeof(GameObject), true) as GameObject;
            updateTargetWithCurrentSelection =
                GUILayout.Toggle(updateTargetWithCurrentSelection, "Auto Update With Selection", GUI.skin.button, GUILayout.ExpandWidth(false));
        }

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
                bindingsToToggle.Add(toggleBinding, (Vector4.zero, Vector4.one));
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
                    filteredBindingCache = null;
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
            using (new EditorGUI.IndentLevelScope())
            {
                using var cc = new EditorGUI.ChangeCheckScope();
                bindingFilter.DrawGUI("Binding Filter");
                if (cc.changed)
                {
                    filteredBindingCache = null;
                }
                filteredBindingCache ??= GetAnimatableBindings(componentToSelectBindingFrom)
                    .Where(b => bindingFilter.Matches(b.propertyName))
                    .Where(b => !new HashSet<char> {'y', 'z', 'w', 'g', 'b', 'a'}.Contains(b.propertyName.Last()))
                    .ToList();
                GUILayout.Space(8);
                if (filteredBindingCache.Count > bindingsPerPage)
                {
                    using var _ = new EditorGUILayout.HorizontalScope();
                    var totalPages = Mathf.CeilToInt(filteredBindingCache.Count / (float)bindingsPerPage);
                    GUILayout.Label($"Page ({tabbedBindingPage + 1}/{totalPages})", GUILayout.Width(100));
                    using (new EditorGUI.DisabledScope(totalPages <= 1 || tabbedBindingPage <= 0))
                    {
                        if (GUILayout.Button("<", GUILayout.Width(20)))
                        {
                            tabbedBindingPage = Mathf.Max(tabbedBindingPage - 1, 0);
                        }
                    }
                    using (new EditorGUI.DisabledScope(totalPages <= 1 || tabbedBindingPage >= totalPages - 1))
                    {
                        if (GUILayout.Button(">", GUILayout.Width(20)))
                        {
                            tabbedBindingPage = Mathf.Min(tabbedBindingPage + 1, totalPages - 1);
                        }
                    }
                    GUILayout.Space(10);
                    tabbedBindingPage = EditorGUILayout.IntField(tabbedBindingPage + 1, GUILayout.Width(50)) - 1;
                    tabbedBindingPage = Mathf.Clamp(tabbedBindingPage, 0, totalPages - 1);
                }
                else
                {
                    tabbedBindingPage = 0;
                }
            }
            foreach (var binding in filteredBindingCache.Skip(tabbedBindingPage * bindingsPerPage).Take(bindingsPerPage))
            {
                using var horizontalScope = new EditorGUILayout.HorizontalScope();
                GUILayout.Space(15);
                bool isAlreadyIncluded = bindingsToToggle.ContainsKey(binding);
                bool shouldBeIncluded = isAlreadyIncluded;
                using (new EditorGUI.DisabledScope(binding.isDiscreteCurve))
                {
                    shouldBeIncluded = GUILayout.Toggle(isAlreadyIncluded, "Select", GUI.skin.button, GUILayout.ExpandWidth(false));
                    if (binding.isDiscreteCurve)
                    {
                        GUI.Label(GUILayoutUtility.GetLastRect(),
                            new GUIContent("", "Discrete curves are currently not supported"));
                    }
                }
                ShowWarningIfBindingExists(binding, GUILayout.Width(20));
                GUILayout.Label($"{(binding.propertyName.EndsWith(".r") ? binding.propertyName[..^2] : binding.propertyName)}", GUILayout.ExpandWidth(true));
                var avGO = FindAvatarDescriptor(Target).gameObject;
                if (AnimationUtility.GetFloatValue(avGO, binding, out var sceneValue))
                {
                    var width = GUILayout.Width(70);
                    if (binding.isDiscreteCurve)
                    {
                        sceneValue = BitConverter.SingleToInt32Bits(sceneValue);
                    }
                    var isToggle = knownToggleProperties.Contains(binding.propertyName);
                    if (binding.propertyName.EndsWith(".r"))
                    {
                        var vectorBindings = GetRemainingVectorBindings(binding).ToArray();
                        var vectorValues = new float[4];
                        for (int i = 0; i < 4; i++)
                        {
                            if (i == 0)
                                vectorValues[i] = sceneValue;
                            else if (i < vectorBindings.Length + 1)
                                AnimationUtility.GetFloatValue(avGO, vectorBindings[i - 1], out vectorValues[i]);
                        }
                        ColorField(new Color(vectorValues[0], vectorValues[1], vectorValues[2], vectorValues[3]), width);
                    }
                    else if (isToggle)
                    {
                        GUILayout.Toggle(sceneValue > 0.5f, "", width);
                    }
                    else
                    {
                        GUILayout.Label($"{sceneValue}", width);
                    }
                }
                if (shouldBeIncluded && !isAlreadyIncluded)
                {
                    bindingsToToggle.Add(binding, GetDefaultValuesForBinding(binding));
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
        savedParameter = EditorGUILayout.Toggle("Saved Parameter", savedParameter);
        syncedParameter = EditorGUILayout.Toggle("Synced Parameter", syncedParameter);

        GUILayout.Space(8);

        string errorMsg = CanCreateToggle();
        GUI.enabled = errorMsg == "";
        if (GUILayout.Button("Create" + ((errorMsg == "") ? "" : " (" + errorMsg + ")")))
        {
            string animFolder = GetAnimationsFolderPath();
            if (!AssetDatabase.IsValidFolder(animFolder))
                AssetDatabase.CreateFolder(animFolder[..animFolder.LastIndexOf("/")], "Animations");
            var t = Target.transform;
            var root = descriptor.transform;
            string pathToAvatarRoot = AnimationUtility.CalculateTransformPath(t, root);
            var clipOn = new AnimationClip();
            clipOn.name = ToggleName + " On";
            var clipOff = new AnimationClip();
            clipOff.name = ToggleName + " Off";
            foreach ((var binding, var value) in bindingsToToggle)
            {
                void AddCurve(AnimationClip clip, EditorCurveBinding binding, float value)
                {
                    var curve = AnimationCurve.Linear(0, value, 0, value);
                    AnimationUtility.SetEditorCurve(clip, binding, curve);
                }
                var extraBindings = GetRemainingVectorBindings(binding).ToArray();
                AddCurve(clipOn, binding, value.onValue.x);
                for (int i = 0; i < extraBindings.Length; i++)
                {
                    AddCurve(clipOn, extraBindings[i], value.onValue[i + 1]);
                }
                AddCurve(clipOff, binding, value.offValue.x);
                for (int i = 0; i < extraBindings.Length; i++)
                {
                    AddCurve(clipOff, extraBindings[i], value.offValue[i + 1]);
                }
            }
            AssetDatabase.CreateAsset(clipOn, $"{animFolder}/{clipOn.name}.anim");
            AssetDatabase.CreateAsset(clipOff, $"{animFolder}/{clipOff.name}.anim");

            var param = new VRCExpressionParameters.Parameter()
            {
                name = ParameterName,
                defaultValue = defaultToggleState ? 1.0f : 0.0f,
                saved = savedParameter,
                networkSynced = syncedParameter,
                valueType = VRCExpressionParameters.ValueType.Bool
            };

            descriptor.expressionParameters.parameters = descriptor.expressionParameters.parameters
                .Union(new VRCExpressionParameters.Parameter[] { param }).ToArray();
            EditorUtility.SetDirty(descriptor.expressionParameters);
            AssetDatabase.SaveAssets();

            var fxLayer = descriptor.baseAnimationLayers[4].animatorController as AnimatorController;
            fxLayer.AddParameter(new AnimatorControllerParameter()
            {
                name = ParameterName,
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
            transitionToOn.AddCondition(AnimatorConditionMode.If, 0, ParameterName);
            transitionToOn.hideFlags = HideFlags.HideInHierarchy;
            toggleOff.AddTransition(transitionToOn);

            var transitionToOff = new AnimatorStateTransition();
            transitionToOff.canTransitionToSelf = false;
            transitionToOff.destinationState = toggleOff;
            transitionToOff.hasFixedDuration = true;
            transitionToOff.hasExitTime = false;
            transitionToOff.duration = 0.0f;
            transitionToOff.AddCondition(AnimatorConditionMode.IfNot, 0, ParameterName);
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
                name = ToggleName,
                parameter = new VRCExpressionsMenu.Control.Parameter() { name = ParameterName },
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
        if (ClickableLastRect())
            EditorGUIUtility.PingObject(descriptor);
        EditorGUILayout.LabelField("AnimationFolder", GetAnimationsFolderPath());
        if (ClickableLastRect())
            EditorGUIUtility.PingObject(AssetDatabase.LoadAssetAtPath<DefaultAsset>(GetAnimationsFolderPath()));
        EditorGUILayout.LabelField("ParamAssetPath", AssetDatabase.GetAssetPath(descriptor.expressionParameters));
        if (ClickableLastRect())
            EditorGUIUtility.PingObject(descriptor.expressionParameters);
        EditorGUILayout.LabelField("MenuAssetPath", AssetDatabase.GetAssetPath(descriptor.expressionsMenu));
        if (ClickableLastRect())
            EditorGUIUtility.PingObject(descriptor.expressionsMenu);
        EditorGUILayout.LabelField("FxLayerAssetPath", AssetDatabase.GetAssetPath(descriptor.baseAnimationLayers[4].animatorController));
        if (ClickableLastRect())
            EditorGUIUtility.PingObject(descriptor.baseAnimationLayers[4].animatorController);
    }

    private static readonly HashSet<char> vectorChars = new() { 'x', 'y', 'z', 'w' };
    
    private Dictionary<Component, List<EditorCurveBinding>> cachedAnimatableBindings = new();
    private List<EditorCurveBinding> GetAnimatableBindings(Component component) {
        if (cachedAnimatableBindings.TryGetValue(component, out var bindings))
            return bindings;
        bindings = new List<EditorCurveBinding>();
        foreach (var animatableBinding in AnimationUtility.GetAnimatableBindings(component.gameObject, FindAvatarDescriptor(component.gameObject).gameObject))
        {
            var propName = animatableBinding.propertyName;
            if (animatableBinding.type != component.GetType())
                continue;
            // these are ui properties from thry editor so we never want to animate them
            if (Regex.IsMatch(propName, @"^material\.[smg]_(start|end)"))
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
        VRCAvatarDescriptor descriptor;
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

    private void OnSelectionChange()
    {
        if (updateTargetWithCurrentSelection)
        {
            var obj = Selection.activeObject as GameObject;
            if (obj != null && FindAvatarDescriptor(obj) != null)
            {
                Target = obj;
                Repaint();
            }
        }
    }
}
#endif