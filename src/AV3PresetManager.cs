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
        private Vector2 scrollPos;
        private int selectedPresetIndex = -1;

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

        /// <summary>Get the controller of the last base animation layer of the avatar.</summary>
        private static AnimatorController GetLastBaseController(VRCAvatarDescriptor av)
        {
            if (av == null || av.baseAnimationLayers == null)
                return null;

            AnimatorController controller = null;
            foreach (var layer in av.baseAnimationLayers)
            {
                if (layer.animatorController is AnimatorController c)
                    controller = c;
            }

            return controller;
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

        /// <summary>
        /// Scan all state machines in the controller (layers and nested sub state machines) and
        /// return the path + analysis of every machine that is a preset state machine.
        /// </summary>
        private static List<(string path, AnimatorStateMachine stateMachine, PresetInfo info)> FindPresetStateMachines(AnimatorController controller)
        {
            var results = new List<(string, AnimatorStateMachine, PresetInfo)>();
            if (controller == null)
                return results;

            void Scan(AnimatorStateMachine stateMachine, string path)
            {
                if (stateMachine == null)
                    return;

                var presetInfo = AnalyzePresetStateMachine(controller, stateMachine);
                if (presetInfo != null)
                    results.Add((path, stateMachine, presetInfo));

                var childBindings = stateMachine.stateMachines ?? Array.Empty<ChildAnimatorStateMachine>();
                for (int i = 0; i < childBindings.Length; i++)
                {
                    var child = childBindings[i].stateMachine;
                    if (child == null)
                        continue;
                    Scan(child, string.IsNullOrEmpty(path) ? child.name : $"{path}/{child.name}");
                }
            }

            var layers = controller.layers ?? Array.Empty<AnimatorControllerLayer>();
            for (int i = 0; i < layers.Length; i++)
            {
                Scan(layers[i].stateMachine, layers[i].name);
            }

            return results;
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

            var controller = GetLastBaseController(av);
            if (controller == null)
            {
                EditorGUILayout.HelpBox("No Animator Controllers found in the base animation layers.", MessageType.Info);
                return;
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("Controller", GUILayout.Width(66));
                using (new EditorGUI.DisabledScope(true))
                {
                    EditorGUILayout.ObjectField(controller, typeof(AnimatorController), true);
                }
            }

            using (new EditorGUILayout.VerticalScope())
            {
                using var scroll = new EditorGUILayout.ScrollViewScope(scrollPos);
                scrollPos = scroll.scrollPosition;

                var presets = FindPresetStateMachines(controller);

                if (presets.Count == 0)
                {
                    EditorGUILayout.HelpBox($"No preset state machines found in controller '{controller.name}'.\n" +
                        "A preset state machine needs an entry transition (a transition from the entry node with an int parameter == condition) " +
                        "pointing to each of its states, all using the same int parameter. " +
                        "The default state may omit the entry transition.", MessageType.Info);
                    return;
                }

                if (selectedPresetIndex < 0 || selectedPresetIndex >= presets.Count)
                    selectedPresetIndex = 0;

                using (new EditorGUILayout.VerticalScope("box"))
                {
                    EditorGUILayout.LabelField("Preset State Machine", EditorStyles.boldLabel);
                    var options = presets.Select(p => $"{p.path}  [param: {p.info.presetParameter}, {p.info.states.Count} states]").ToArray();
                    selectedPresetIndex = EditorGUILayout.Popup("State Machine", selectedPresetIndex, options);
                }

                var (path, stateMachine, preset) = presets[selectedPresetIndex];

                EditorGUILayout.Space(8);
                using (new EditorGUILayout.VerticalScope("box"))
                {
                    GUILayout.Label(new GUIContent(stateMachine.name ?? "(unnamed)"), EditorStyles.boldLabel);
                    GUILayout.Label($"Path: {path}");
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
