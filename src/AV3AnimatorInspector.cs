#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using d4rkpl4y3r.AV3ToggleUtil.Util;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;

using static d4rkpl4y3r.AV3ToggleUtil.Util.AV3Helper;
using Object = UnityEngine.Object;

namespace d4rkpl4y3r.AV3ToggleUtil
{
    public class AV3AnimatorInspector : EditorWindow
    {
        private Vector2 leftScrollPos;
        private Vector2 rightScrollPos;
        private SplitterState splitter = new();
        private ColumnGrid columnGrid = new();
        private bool showComponentProperties = true;

        private AnimatorController selectedController;
        private int selectedLayerIndex = 0;
        private bool sortLayers = false;

        private VRCAvatarDescriptor lastFoundAvatarDescriptor;
        private TextFilter layerFilter = new() { IsRegex = true, SmallButtons = true };

        private class ControllerEntry
        {
            public AnimatorController controller;
            public string displayName;
        }

        private static bool IsSameController(AnimatorController a, AnimatorController b)
        {
            if (a == null || b == null)
                return ReferenceEquals(a, b);
            return a.GetInstanceID() == b.GetInstanceID();
        }

        private static List<ControllerEntry> GetControllers(VRCAvatarDescriptor av)
        {
            var entries = new List<ControllerEntry>();
            var known = new HashSet<int>();

            void AddController(AnimatorController controller, bool isSpecial)
            {
                if (controller == null) return;
                var id = controller.GetInstanceID();
                if (!known.Add(id)) return;

                var name = string.IsNullOrEmpty(controller.name) ? "(Unnamed Controller)" : controller.name;
                if (isSpecial)
                    name += "  (Special)";

                entries.Add(new ControllerEntry
                {
                    controller = controller,
                    displayName = name,
                });
            }

            if (av == null)
                return entries;

            if (av.baseAnimationLayers != null)
            {
                foreach (var layer in av.baseAnimationLayers)
                {
                    AddController(layer.animatorController as AnimatorController, false);
                }
            }

            if (av.specialAnimationLayers != null)
            {
                foreach (var layer in av.specialAnimationLayers)
                {
                    AddController(layer.animatorController as AnimatorController, true);
                }
            }

            return entries;
        }

        private static List<AnimationClip> GetClipsInLayer(AnimatorControllerLayer layer)
        {
            var clips = new HashSet<AnimationClip>();

            if (layer != null && layer.stateMachine != null)
            {
                void TraverseStateMachine(AnimatorStateMachine stateMachine)
                {
                    if (stateMachine == null)
                        return;

                    var states = stateMachine.states;
                    for (int i = 0; i < states.Length; i++)
                    {
                        var state = states[i].state;
                        if (state == null)
                            continue;

                        CollectClipsFromMotion(state.motion, clips);
                    }

                    var childStateMachines = stateMachine.stateMachines;
                    for (int i = 0; i < childStateMachines.Length; i++)
                    {
                        TraverseStateMachine(childStateMachines[i].stateMachine);
                    }
                }

                TraverseStateMachine(layer.stateMachine);
            }

            return clips
                .Where(c => c != null)
                .OrderBy(c => c.name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private class LayerParameterScan
        {
            public readonly HashSet<string> transitionParameters = new(StringComparer.Ordinal);
            public readonly HashSet<string> stateParameters = new(StringComparer.Ordinal);
            public readonly HashSet<string> driverParameters = new(StringComparer.Ordinal);
            public readonly HashSet<string> playAudioParameters = new(StringComparer.Ordinal);
        }

        private static void CollectTransitionConditionParameters(AnimatorTransitionBase transition, HashSet<string> parameters)
        {
            if (transition == null || transition.conditions == null) return;
            for (int i = 0; i < transition.conditions.Length; i++)
            {
                if (!string.IsNullOrEmpty(transition.conditions[i].parameter))
                    parameters.Add(transition.conditions[i].parameter);
            }
        }

        private static void CollectBlendTreeParameters(Motion motion, HashSet<string> parameters, HashSet<BlendTree> visitedTrees = null)
        {
            if (motion == null) return;
            if (!(motion is BlendTree tree)) return;

            if (visitedTrees == null) visitedTrees = new HashSet<BlendTree>();
            if (!visitedTrees.Add(tree)) return;

            var isSecondParameterUsed = tree.blendType != BlendTreeType.Simple1D && tree.blendType != BlendTreeType.Direct;
            if (!string.IsNullOrEmpty(tree.blendParameter))
                parameters.Add(tree.blendParameter);
            if (isSecondParameterUsed && !string.IsNullOrEmpty(tree.blendParameterY))
                parameters.Add(tree.blendParameterY);

            var children = tree.children;
            if (tree.blendType == BlendTreeType.Direct)
            {
                for (int i = 0; i < children.Length; i++)
                {
                    if (!string.IsNullOrEmpty(children[i].directBlendParameter))
                        parameters.Add(children[i].directBlendParameter);
                }
            }

            for (int i = 0; i < children.Length; i++)
            {
                CollectBlendTreeParameters(children[i].motion, parameters, visitedTrees);
            }
        }

        private static void ScanLayerParameters(AnimatorControllerLayer layer, LayerParameterScan scan)
        {
            if (layer == null || layer.stateMachine == null)
                return;

            void TraverseStateMachine(AnimatorStateMachine stateMachine)
            {
                if (stateMachine == null)
                    return;

                var anyStateTransitions = stateMachine.anyStateTransitions;
                if (anyStateTransitions != null)
                {
                    for (int i = 0; i < anyStateTransitions.Length; i++)
                    {
                        CollectTransitionConditionParameters(anyStateTransitions[i], scan.transitionParameters);
                    }
                }

                var states = stateMachine.states;
                for (int i = 0; i < states.Length; i++)
                {
                    var state = states[i].state;
                    if (state == null)
                        continue;

                    var transitions = state.transitions;
                    if (transitions != null)
                    {
                        for (int t = 0; t < transitions.Length; t++)
                        {
                            CollectTransitionConditionParameters(transitions[t], scan.transitionParameters);
                        }
                    }

                    if (state.timeParameterActive && !string.IsNullOrEmpty(state.timeParameter))
                        scan.stateParameters.Add(state.timeParameter);
                    if (state.speedParameterActive && !string.IsNullOrEmpty(state.speedParameter))
                        scan.stateParameters.Add(state.speedParameter);
                    if (state.mirrorParameterActive && !string.IsNullOrEmpty(state.mirrorParameter))
                        scan.stateParameters.Add(state.mirrorParameter);
                    if (state.cycleOffsetParameterActive && !string.IsNullOrEmpty(state.cycleOffsetParameter))
                        scan.stateParameters.Add(state.cycleOffsetParameter);

                    CollectBlendTreeParameters(state.motion, scan.stateParameters);

                    var behaviours = state.behaviours;
                    if (behaviours != null)
                    {
                        for (int b = 0; b < behaviours.Length; b++)
                        {
                            if (behaviours[b] is VRCAvatarParameterDriver driver && driver.parameters != null)
                            {
                                for (int p = 0; p < driver.parameters.Count; p++)
                                {
                                    var parameter = driver.parameters[p];
                                    if (parameter == null)
                                        continue;

                                    if (!string.IsNullOrEmpty(parameter.name))
                                        scan.driverParameters.Add(parameter.name);

                                    if (parameter.type == VRC.SDKBase.VRC_AvatarParameterDriver.ChangeType.Copy
                                        && !string.IsNullOrEmpty(parameter.source))
                                    {
                                        scan.driverParameters.Add(parameter.source);
                                    }
                                }
                            }

                            if (behaviours[b] is VRCAnimatorPlayAudio playAudio
                                && playAudio.PlaybackOrder == VRCAnimatorPlayAudio.Order.Parameter
                                && !string.IsNullOrEmpty(playAudio.ParameterName))
                            {
                                scan.playAudioParameters.Add(playAudio.ParameterName);
                            }
                        }
                    }
                }

                var childStateMachines = stateMachine.stateMachines;
                for (int i = 0; i < childStateMachines.Length; i++)
                {
                    TraverseStateMachine(childStateMachines[i].stateMachine);
                }
            }

            TraverseStateMachine(layer.stateMachine);
        }

        private static void CollectClipsFromMotion(Motion motion, HashSet<AnimationClip> clips, HashSet<BlendTree> visitedTrees = null)
        {
            if (motion == null || clips == null)
                return;

            if (motion is AnimationClip clip)
            {
                clips.Add(clip);
                return;
            }

            if (motion is BlendTree tree)
            {
                if (visitedTrees == null) visitedTrees = new HashSet<BlendTree>();
                if (!visitedTrees.Add(tree)) return;

                var children = tree.children;
                for (int i = 0; i < children.Length; i++)
                {
                    CollectClipsFromMotion(children[i].motion, clips, visitedTrees);
                }
            }
        }

        private static void CollectComponentBindings(AnimationClip[] clips, VRCAvatarDescriptor av, Dictionary<Component, HashSet<string>> componentPropertyMap)
        {
            if (av == null || av.transform == null)
                return;

            foreach (var clip in clips)
            {
                if (clip == null) continue;
                foreach (var binding in AnimationUtility.GetCurveBindings(clip).Concat(AnimationUtility.GetObjectReferenceCurveBindings(clip)))
                {
                    Transform t = FindTransformByAvatarPath(av.transform, binding.path);
                    if (t == null) continue;
                    Component comp = null;
                    if (binding.type == typeof(Transform) || binding.type == typeof(GameObject))
                        comp = t;
                    else if (typeof(Component).IsAssignableFrom(binding.type))
                        comp = t.GetComponent(binding.type);
                    if (comp != null)
                    {
                        if (!componentPropertyMap.TryGetValue(comp, out var props))
                        {
                            props = new HashSet<string>();
                            componentPropertyMap[comp] = props;
                        }
                        props.Add(binding.propertyName);
                    }
                }
            }
        }

        private static HashSet<string> MergePropertyNames(HashSet<string> propertyNames)
        {
            var merged = new HashSet<string>();
            var groups = propertyNames
                .Where(p => !string.IsNullOrEmpty(p))
                .GroupBy(p =>
                {
                    int lastDot = p.LastIndexOf('.');
                    if (lastDot > 0 && lastDot < p.Length - 1)
                    {
                        string suffix = p[(lastDot + 1)..];
                        if (suffix.Length == 1)
                            return p[..lastDot];
                    }
                    return p;
                })
                .ToList();

            foreach (var group in groups)
            {
                var first = group.First();
                int lastDot = first.LastIndexOf('.');
                if (lastDot > 0 && lastDot < first.Length - 1)
                {
                    string suffix = first[(lastDot + 1)..];
                    if (suffix.Length == 1 && group.Count() > 1)
                    {
                        string prefix = first[..(lastDot + 1)];
                        var componentLetters = group.Select(p => p[(lastDot + 1)..]).ToList();
                        var letterSet = new HashSet<string>(componentLetters);
                        string orderKey = null;
                        if (letterSet.SetEquals(new[] { "r", "g", "b", "a" }))
                            orderKey = "rgba";
                        else if (letterSet.SetEquals(new[] { "x", "y", "z", "w" }))
                            orderKey = "xyzw";
                        var letters = orderKey != null
                            ? componentLetters.OrderBy(s => orderKey.IndexOf(s[0])).ToList()
                            : componentLetters.OrderBy(s => s).ToList();
                        merged.Add(prefix + string.Concat(letters));
                        continue;
                    }
                }
                foreach (var name in group)
                    merged.Add(name);
            }
            return merged;
        }

        private static void SelectAndPingObject(Object obj)
        {
            Selection.activeObject = obj;
            EditorGUIUtility.PingObject(obj);
        }

        private VRCAvatarDescriptor GetCurrentOrLastAvatarDescriptor()
        {
            var selectedAvatarDescriptor = FindAvatarDescriptor(Selection.activeGameObject);
            if (selectedAvatarDescriptor != null)
                lastFoundAvatarDescriptor = selectedAvatarDescriptor;
            return lastFoundAvatarDescriptor;
        }

        private void OnGUI()
        {
            var av = GetCurrentOrLastAvatarDescriptor();

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("Avatar", GUILayout.Width(46));
                using (new EditorGUI.DisabledScope(true))
                {
                    EditorGUILayout.ObjectField(av, typeof(VRCAvatarDescriptor), true);
                }
            }

            if (av == null)
            {
                EditorGUILayout.HelpBox("No VRC Avatar Descriptor found.", MessageType.Warning);
                return;
            }

            var controllers = GetControllers(av);
            if (controllers.Count == 0)
            {
                EditorGUILayout.HelpBox("No Animator Controllers found on the Avatar Descriptor.", MessageType.Info);
                return;
            }

            var selectedEntry = controllers.FirstOrDefault(c => IsSameController(c.controller, selectedController));
            if (selectedEntry == null)
                selectedEntry = controllers[0];

            var layers = selectedEntry.controller.layers ?? Array.Empty<AnimatorControllerLayer>();
            if (selectedLayerIndex >= layers.Length)
                selectedLayerIndex = 0;
            if (layers.Length == 0)
            {
                EditorGUILayout.HelpBox($"'{selectedEntry.displayName}' has no layers.", MessageType.Info);
                return;
            }

            var selectedLayer = layers[selectedLayerIndex];
            var layerClips = GetClipsInLayer(selectedLayer);
            var layerScan = new LayerParameterScan();
            ScanLayerParameters(selectedLayer, layerScan);

            using var horizontal = new EditorGUILayout.HorizontalScope();

            using (new EditorGUILayout.VerticalScope(GUILayout.Width(splitter.leftPanelWidth)))
            {
                using var leftScroll = new EditorGUILayout.ScrollViewScope(leftScrollPos);
                leftScrollPos = leftScroll.scrollPosition;

                // Section 1: Animator Controllers (exclusive selection)
                using (new EditorGUILayout.VerticalScope("box"))
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        GUILayout.Label($"Controllers ({controllers.Count})", EditorStyles.boldLabel);
                    }

                    for (int i = 0; i < controllers.Count; i++)
                    {
                        var entry = controllers[i];
                        using var cc = new EditorGUI.ChangeCheckScope();
                        var selected = GUILayout.Toggle(IsSameController(entry.controller, selectedEntry.controller), entry.displayName, GUI.skin.button, GUILayout.ExpandWidth(true));
                        if (cc.changed && selected && !IsSameController(entry.controller, selectedEntry.controller))
                        {
                            selectedController = entry.controller;
                            selectedLayerIndex = 0;
                            GUI.FocusControl(null);
                        }
                    }
                }

                // Section 2: Regex filter + layers of the selected controller (exclusive selection)
                using (new EditorGUILayout.VerticalScope("box"))
                {
                    var layerEntries = new List<(int index, string name)>(layers.Length);
                    for (int i = 0; i < layers.Length; i++)
                    {
                        var layerName = string.IsNullOrEmpty(layers[i].name) ? "(Unnamed Layer)" : layers[i].name;
                        if (layerFilter.Matches(layerName))
                            layerEntries.Add((i, layerName));
                    }

                    if (sortLayers)
                        layerEntries.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.name, b.name));

                    using (new EditorGUILayout.HorizontalScope())
                    {
                        GUILayout.Label($"Layers ({layerEntries.Count}/{layers.Length})", EditorStyles.boldLabel);
                        sortLayers = GUILayout.Toggle(sortLayers, new GUIContent("A→Z", "Sort alphabetically"), GUI.skin.button, GUILayout.ExpandWidth(false));
                    }
                    layerFilter.DrawGUI();

                    foreach (var layerEntry in layerEntries)
                    {
                        using var cc = new EditorGUI.ChangeCheckScope();
                        var selected = GUILayout.Toggle(selectedLayerIndex == layerEntry.index, $"{layerEntry.name} ({layerEntry.index})", GUI.skin.button, GUILayout.ExpandWidth(true));
                        if (cc.changed && selected && selectedLayerIndex != layerEntry.index)
                        {
                            selectedLayerIndex = layerEntry.index;
                            GUI.FocusControl(null);
                        }
                    }
                }
            }

            splitter.DrawSplitter(this, 160f, 520f);

            using (new EditorGUILayout.VerticalScope())
            {
                using var rightScroll = new EditorGUILayout.ScrollViewScope(rightScrollPos);
                rightScrollPos = rightScroll.scrollPosition;

                var rightPanelWidth = Mathf.Max(200f, position.width - splitter.leftPanelWidth - SplitterState.SplitterWidth - 30f);
                columnGrid.Recalculate(rightPanelWidth);
                const float innerIndent = ColumnGrid.InnerIndent;
                var entryWidth = columnGrid.entryWidth;

                bool DrawParameterSection(string title, IEnumerable<string> parameters)
                {
                    var list = parameters
                        .Where(p => !string.IsNullOrEmpty(p))
                        .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    if (list.Count == 0)
                        return false;

                    EditorGUILayout.Space(8);
                    using (new EditorGUILayout.VerticalScope("box"))
                    {
                        EditorGUILayout.LabelField($"{title} ({list.Count})", EditorStyles.boldLabel);
                        columnGrid.DrawEntries(list, parameter =>
                        {
                            GUILayout.Label(new GUIContent(parameter, "Click to open in Parameter Inspector"), GUILayout.Width(entryWidth));
                            if (ClickableLastRect())
                                AV3ParameterInspector.OpenWithParameter(parameter);
                        });
                    }
                    return true;
                }

                DrawParameterSection("Parameters in Transition Conditions", layerScan.transitionParameters);
                DrawParameterSection("Parameters Used in States (Motion Time / Speed / Mirror / Cycle Offset / Blend Trees)", layerScan.stateParameters);
                DrawParameterSection("Parameters Used in Parameter Drivers", layerScan.driverParameters);
                DrawParameterSection("Parameters Used in Play Audio", layerScan.playAudioParameters);

                // Section: Animation Clips used in the selected layer
                EditorGUILayout.Space(8);
                using (new EditorGUILayout.VerticalScope("box"))
                {
                    EditorGUILayout.LabelField($"Animation Clips in Layer '{(string.IsNullOrEmpty(selectedLayer.name) ? "(Unnamed Layer)" : selectedLayer.name)}' ({layerClips.Count})", EditorStyles.boldLabel);
                    if (layerClips.Count == 0)
                    {
                        EditorGUILayout.HelpBox("No animation clips found in this layer.", MessageType.Info);
                    }
                    else
                    {
                        columnGrid.DrawEntries(layerClips, clip =>
                        {
                            EditorGUILayout.ObjectField(clip, typeof(AnimationClip), false, GUILayout.Width(entryWidth));
                        });
                    }
                }

                // Section 2: Components & bindings affected by the clips (same as Parameter Inspector)
                var componentPropertyMap = new Dictionary<Component, HashSet<string>>();
                CollectComponentBindings(layerClips.ToArray(), av, componentPropertyMap);

                if (componentPropertyMap.Count > 0)
                {
                    EditorGUILayout.Space(8);
                    using (new EditorGUILayout.VerticalScope("box"))
                    {
                        EditorGUILayout.LabelField($"Components Bound in Animation Clips ({componentPropertyMap.Count})", EditorStyles.boldLabel);
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            GUILayout.Space(innerIndent);
                            showComponentProperties = GUILayout.Toggle(showComponentProperties, "Show Properties", GUILayout.Width(120f));
                        }
                        var components = componentPropertyMap.Keys.ToList();
                        components.Sort((a, b) => string.Compare(a.name, b.name, StringComparison.OrdinalIgnoreCase));
                        if (showComponentProperties)
                        {
                            foreach (var comp in components)
                            {
                                using (new EditorGUILayout.HorizontalScope())
                                {
                                    GUILayout.Space(innerIndent);
                                    EditorGUILayout.ObjectField("", comp, comp.GetType(), true, GUILayout.Width(entryWidth));
                                }
                                var props = componentPropertyMap[comp];
                                var merged = MergePropertyNames(props);
                                foreach (var prop in merged.OrderBy(p => p))
                                {
                                    using (new EditorGUILayout.HorizontalScope())
                                    {
                                        GUILayout.Space(innerIndent + 15);
                                        EditorGUILayout.LabelField(prop, EditorStyles.label);
                                    }
                                }
                            }
                        }
                        else
                        {
                            columnGrid.DrawEntries(components, comp =>
                            {
                                var props = componentPropertyMap[comp];
                                var merged = MergePropertyNames(props);
                                var tooltip = string.Join(", ", merged.OrderBy(p => p));
                                EditorGUILayout.ObjectField("", comp, comp.GetType(), true, GUILayout.Width(entryWidth));
                                EditorGUI.LabelField(GUILayoutUtility.GetLastRect(), new GUIContent("", tooltip));
                            });
                        }
                    }
                }
            }
        }

        [MenuItem("Tools/d4rkpl4y3r/AV3 Toggle Util/Animator Inspector")]
        public static void AV3AnimatorInspectorMenuItem()
        {
            var window = GetWindow<AV3AnimatorInspector>();
            window.titleContent = new GUIContent("d4rk Animator Inspector");
            window.Show();
        }

        private void OnSelectionChange()
        {
            Repaint();
        }
    }
}
#endif
