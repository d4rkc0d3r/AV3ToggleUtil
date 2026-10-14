#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using d4rkpl4y3r.AV3ToggleUtil.Util;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;

using static d4rkpl4y3r.AV3ToggleUtil.Util.AV3Helper;

namespace d4rkpl4y3r.AV3ToggleUtil
{
    /// <summary>
    /// Inspect &amp; select a "preset" state machine inside the base animation layers of the
    /// currently selected avatar.
    ///
    /// A preset state machine is a state machine where:
    ///  - every state (except possibly the default state) has an entry transition (a transition
    ///    from the entry node) pointing to it with an int parameter == presetId condition
    ///    (extra conditions are fine),
    ///  - all those entry transitions use the same int parameter.
    /// </summary>
    public class PresetManager : EditorWindow
    {
        private Vector2 leftScrollPos;
        private Vector2 rightScrollPos;
        private SplitterState splitter = new();

        private AnimatorController selectedController;
        private string stateMachinePath = "";

        private VRCAvatarDescriptor lastFoundAvatarDescriptor;

        private readonly struct PresetStateEntry
        {
            public readonly AnimatorState state;
            public readonly int presetId;
            public readonly bool isDefault;
            public readonly bool hasEntryTransition;

            public PresetStateEntry(AnimatorState state, int presetId, bool isDefault, bool hasEntryTransition)
            {
                this.state = state;
                this.presetId = presetId;
                this.isDefault = isDefault;
                this.hasEntryTransition = hasEntryTransition;
            }
        }

        private class PresetInfo
        {
            public AnimatorStateMachine stateMachine;
            public string presetParameter;
            public readonly List<PresetStateEntry> states = new List<PresetStateEntry>();
        }

        private static bool IsSameController(AnimatorController a, AnimatorController b)
        {
            if (a == null || b == null)
                return ReferenceEquals(a, b);
            return a.GetInstanceID() == b.GetInstanceID();
        }

        /// <summary>Collect the unique Animator Controllers of the base animation layers.</summary>
        private static List<AnimatorController> GetBaseControllers(VRCAvatarDescriptor av)
        {
            var controllers = new List<AnimatorController>();
            if (av == null || av.baseAnimationLayers == null)
                return controllers;

            var known = new HashSet<int>();
            foreach (var layer in av.baseAnimationLayers)
            {
                var controller = layer.animatorController as AnimatorController;
                if (controller == null)
                    continue;
                var id = controller.GetInstanceID();
                if (!known.Add(id))
                    continue;
                controllers.Add(controller);
            }

            return controllers;
        }

        /// <summary>
        /// Resolve "layerName/subSMName/subSMName2/..." to a state machine inside a controller.
        /// The first segment must be a layer name; further segments are nested sub state machines.
        /// </summary>
        private static bool TryResolveStateMachinePath(AnimatorController controller, string path, out AnimatorStateMachine stateMachine, out string errorMessage)
        {
            stateMachine = null;
            errorMessage = null;

            if (controller == null)
            {
                errorMessage = "No controller selected.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(path))
            {
                errorMessage = "Enter a state machine path like 'layerName/subStateMachineName'.";
                return false;
            }

            var segments = path.Trim().Split('/');
            for (int i = 0; i < segments.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(segments[i]))
                {
                    errorMessage = $"Path segment {i + 1} is empty ('{path}').";
                    return false;
                }
            }

            var layers = controller.layers ?? Array.Empty<AnimatorControllerLayer>();
            var matchingLayer = layers.FirstOrDefault(l => string.Equals(l.name, segments[0], StringComparison.Ordinal));
            if (matchingLayer == null)
            {
                var available = layers.Length == 0 ? "none" : string.Join(", ", layers.Select(l => $"'{l.name}'"));
                errorMessage = $"Layer '{segments[0]}' not found in controller '{controller.name}'. Available layers: {available}.";
                return false;
            }

            if (segments.Length == 1)
            {
                stateMachine = matchingLayer.stateMachine;
                return true;
            }

            var currentStateMachine = matchingLayer.stateMachine;
            for (int i = 1; i < segments.Length; i++)
            {
                if (currentStateMachine == null)
                {
                    errorMessage = $"Cannot descend into '{segments[i]}': parent state machine not found.";
                    return false;
                }

                var childBindings = currentStateMachine.stateMachines ?? Array.Empty<ChildAnimatorStateMachine>();
                AnimatorStateMachine child = null;
                for (int c = 0; c < childBindings.Length; c++)
                {
                    var childMachine = childBindings[c].stateMachine;
                    if (childMachine != null && string.Equals(childMachine.name, segments[i], StringComparison.Ordinal))
                    {
                        child = childMachine;
                        break;
                    }
                }

                if (child == null)
                {
                    var available = childBindings.Length == 0
                        ? "none"
                        : string.Join(", ", childBindings.Where(c => c.stateMachine != null).Select(c => $"'{c.stateMachine.name}'"));
                    errorMessage = $"Sub state machine '{segments[i]}' not found under '{segments[i - 1]}'. Available: {available}.";
                    return false;
                }

                currentStateMachine = child;
            }

            stateMachine = currentStateMachine;
            return true;
        }

        /// <summary>
        /// Analyze a state machine against the preset state machine contract.
        /// Returns a <see cref="PresetInfo"/> with the common int parameter and per-state preset ids,
        /// or null if it is not a preset state machine. The owning controller is used to resolve
        /// the types of the condition parameters.
        /// </summary>
        private static PresetInfo AnalyzePresetStateMachine(AnimatorController controller, AnimatorStateMachine stateMachine)
        {
            if (stateMachine == null)
                return null;

            // The owning controller (the one the machine was resolved from) declares the parameter types
            var parameters = controller?.parameters ?? Array.Empty<AnimatorControllerParameter>();
            var intParameters = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < parameters.Length; i++)
            {
                if (parameters[i].type == AnimatorControllerParameterType.Int && !string.IsNullOrEmpty(parameters[i].name))
                    intParameters.Add(parameters[i].name);
            }

            // Every state contained directly in this machine (entry transitions can only target direct children)
            var children = stateMachine.states ?? Array.Empty<ChildAnimatorState>();
            var states = new List<AnimatorState>(children.Length);
            for (int i = 0; i < children.Length; i++)
            {
                if (children[i].state != null)
                    states.Add(children[i].state);
            }

            if (states.Count == 0)
                return null;

            // The entry transitions of a state machine are the transitions from its entry node
            var entryTransitions = stateMachine.entryTransitions ?? Array.Empty<AnimatorTransition>();

            // Collect int == conditions from the entry transitions and group them per destination state
            var entryInfo = new Dictionary<int, (int presetId, string parameter)>(); // destination instance id -> info
            foreach (var transition in entryTransitions)
            {
                var destination = transition.destinationState;
                if (destination == null)
                    continue;

                var conditions = transition.conditions;
                if (conditions == null)
                    continue;

                for (int c = 0; c < conditions.Length; c++)
                {
                    var cond = conditions[c];
                    if (cond.mode != AnimatorConditionMode.Equals)
                        continue;
                    if (string.IsNullOrEmpty(cond.parameter) || !intParameters.Contains(cond.parameter))
                        continue;

                    var presetId = Mathf.RoundToInt(cond.threshold);
                    var destId = destination.GetInstanceID();
                    if (entryInfo.TryGetValue(destId, out var existing) && !string.Equals(existing.parameter, cond.parameter, StringComparison.Ordinal))
                    {
                        // The same state is targeted by entry transitions using different int parameters -> not a preset
                        return null;
                    }
                    entryInfo[destId] = (presetId, cond.parameter);
                    break;
                }
            }

            // Every state must be targeted by an entry transition, except possibly the default state
            List<AnimatorState> presetStates = new List<AnimatorState>();
            foreach (var state in states)
            {
                var stateId = state.GetInstanceID();
                bool isDefault = state.GetInstanceID() == (stateMachine.defaultState?.GetInstanceID() ?? -1);
                if (entryInfo.TryGetValue(stateId, out var info))
                {
                    if (isDefault)
                        return null; // the default state is supposed to have no extra entry transition with conditions
                    presetStates.Add(state);
                }
                else if (!isDefault)
                {
                    // Non-default state without an entry transition -> not a preset
                    return null;
                }
            }

            if (presetStates.Count == 0)
                return null;

            // All entry transitions must use the same int parameter
            string commonParameter = null;
            foreach (var kv in entryInfo)
            {
                if (!presetStates.Any(s => s.GetInstanceID() == kv.Key))
                    continue;

                if (commonParameter == null)
                    commonParameter = kv.Value.parameter;
                else if (!string.Equals(commonParameter, kv.Value.parameter, StringComparison.Ordinal))
                    return null; // entry transitions use different int parameters
            }

            if (string.IsNullOrEmpty(commonParameter))
                return null;

            var preset = new PresetInfo
            {
                stateMachine = stateMachine,
                presetParameter = commonParameter,
            };

            // Sort preset states by their preset id, then append the default state (without an id) if it exists
            var defaultState = stateMachine.defaultState;
            foreach (var state in presetStates.OrderBy(s => entryInfo[s.GetInstanceID()].presetId))
            {
                preset.states.Add(new PresetStateEntry(state, entryInfo[state.GetInstanceID()].presetId, false, true));
            }

            if (defaultState != null)
            {
                preset.states.Add(new PresetStateEntry(defaultState, int.MinValue, true, false));
            }

            return preset;
        }

        private void OnGUI()
        {
            var av = GetCurrentOrLastAvatarDescriptor(ref lastFoundAvatarDescriptor);

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

            var controllers = GetBaseControllers(av);
            if (controllers.Count == 0)
            {
                EditorGUILayout.HelpBox("No Animator Controllers found in the base animation layers.", MessageType.Info);
                return;
            }

            // Default to the last controller of the base animation layers
            var selectedEntry = controllers.FirstOrDefault(c => IsSameController(c, selectedController));
            if (selectedEntry == null)
                selectedController = controllers[controllers.Count - 1];

            using var horizontal = new EditorGUILayout.HorizontalScope();

            using (new EditorGUILayout.VerticalScope(GUILayout.Width(splitter.leftPanelWidth)))
            {
                using var leftScroll = new EditorGUILayout.ScrollViewScope(leftScrollPos);
                leftScrollPos = leftScroll.scrollPosition;

                using (new EditorGUILayout.VerticalScope("box"))
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        GUILayout.Label($"Controllers ({controllers.Count})", EditorStyles.boldLabel);
                    }

                    for (int i = 0; i < controllers.Count; i++)
                    {
                        var controller = controllers[i];
                        using var cc = new EditorGUI.ChangeCheckScope();
                        var selected = GUILayout.Toggle(IsSameController(controller, selectedController),
                            $"{controller.name} ({i})", GUI.skin.button, GUILayout.ExpandWidth(true));
                        if (cc.changed && selected && !IsSameController(controller, selectedController))
                        {
                            selectedController = controller;
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

                using (new EditorGUILayout.VerticalScope("box"))
                {
                    EditorGUILayout.LabelField("State Machine", EditorStyles.boldLabel);
                    stateMachinePath = EditorGUILayout.TextField("Path", stateMachinePath);
                    EditorGUILayout.HelpBox("Path format: 'layerName/subStateMachineName'. Slashes descend into (nested) sub state machines; the first segment is the layer name in the selected controller.", MessageType.None);
                }

                if (!TryResolveStateMachinePath(selectedController, stateMachinePath, out var stateMachine, out var errorMessage))
                {
                    EditorGUILayout.Space(8);
                    EditorGUILayout.HelpBox($"State machine not found:\n{errorMessage}", MessageType.Info);
                    return;
                }

                EditorGUILayout.Space(8);
                using (new EditorGUILayout.VerticalScope("box"))
                {
                    GUILayout.Label(new GUIContent(stateMachine.name ?? "(unnamed)"), EditorStyles.boldLabel);
                }

                var preset = AnalyzePresetStateMachine(selectedController, stateMachine);
                if (preset == null)
                {
                    EditorGUILayout.Space(8);
                    EditorGUILayout.HelpBox($"The state machine '{stateMachine.name}' is not a preset state machine.\n" +
                        "A preset state machine needs an entry transition (a transition from the entry node with an int parameter == condition) " +
                        "pointing to each of its states, all using the same int parameter. " +
                        "The default state may omit the entry transition.", MessageType.Info);
                    return;
                }

                EditorGUILayout.Space(8);
                using (new EditorGUILayout.VerticalScope("box"))
                {
                    EditorGUILayout.LabelField($"Preset States  (parameter: {preset.presetParameter}, {preset.states.Count} states)", EditorStyles.boldLabel);

                    foreach (var entry in preset.states)
                    {
                        using var row = new EditorGUILayout.HorizontalScope();
                        GUILayout.Space(ColumnGrid.InnerIndent);
                        GUILayout.Label($"{entry.state.name}", GUILayout.Width(240f));
                        if (entry.hasEntryTransition)
                            GUILayout.Label($"id = {entry.presetId}");
                        else if (entry.isDefault)
                            GUILayout.Label("default state (no entry condition)");
                        if (ClickableLastRect())
                        {
                            Selection.activeObject = entry.state;
                            EditorGUIUtility.PingObject(entry.state);
                        }
                    }
                }
            }
        }

        [MenuItem("Tools/d4rkpl4y3r/AV3 Toggle Util/Preset Manager")]
        public static void AV3PresetManagerMenuItem()
        {
            var window = GetWindow<PresetManager>();
            window.titleContent = new GUIContent("d4rk Preset Manager");
            window.Show();
        }

        private void OnSelectionChange()
        {
            Repaint();
        }
    }
}
#endif
