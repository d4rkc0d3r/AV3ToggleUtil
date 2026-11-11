#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace d4rkpl4y3r.AV3ToggleUtil
{
    public class TransitionInspector : EditorWindow
    {
        private Vector2 scrollPos;
        private readonly Dictionary<string, bool> foldoutStates = new Dictionary<string, bool>();

        [MenuItem("Tools/d4rkpl4y3r/AV3 Toggle Util/Transition Inspector")]
        public static void TransitionInspectorMenuItem()
        {
            var window = GetWindow<TransitionInspector>();
            window.titleContent = new GUIContent("Transition Inspector");
            window.Show();
        }

        private void OnGUI()
        {
            var groups = CollectTransitions(Selection.objects);
            if (groups.Count == 0)
                return;

            using var scroll = new EditorGUILayout.ScrollViewScope(scrollPos);
            scrollPos = scroll.scrollPosition;

            foreach (var group in groups)
            {
                using (new EditorGUILayout.VerticalScope("box"))
                {
                    var transitions = group.Value;
                    var headerText = $"{group.Key.SourceName} -> {group.Key.DestinationName}";
                    GUILayout.Label(headerText, EditorStyles.boldLabel);

                    // Multiple transitions from the same source to the same target act as OR; each condition inside a single transition acts as AND
                    var clauses = new List<string>();
                    for (var i = 0; i < transitions.Count; i++)
                    {
                        var parts = BuildConditionParts(transitions[i]);
                        if (parts.Count == 0)
                            continue;

                        var clause = string.Join(" & ", parts);
                        clauses.Add(parts.Count > 1 ? "(" + clause + ")" : clause);
                    }
                    GUILayout.Label(clauses.Count > 0 ? string.Join(" | ", clauses) : "no conditions");

                    if (!foldoutStates.TryGetValue(headerText, out var showSettings))
                    {
                        showSettings = AnyMixedValue(transitions) || AnyMatches(transitions, t => t.duration > 0f) || AnyMatches(transitions, t => t.hasExitTime);
                    }
                    showSettings = EditorGUILayout.Foldout(showSettings, "Settings", true);
                    foldoutStates[headerText] = showSettings;
                    if (showSettings)
                    {
                        using var _ = new EditorGUI.IndentLevelScope();
                        DrawBoolSetting("Has Exit Time", transitions, t => t.hasExitTime, (t, v) => t.hasExitTime = v);
                        DrawFloatSetting("Exit Time", transitions, t => t.exitTime, (t, v) => t.exitTime = v, AnyMatches(transitions, t => t.hasExitTime));
                        DrawBoolSetting("Fixed Duration", transitions, t => t.hasFixedDuration, (t, v) => t.hasFixedDuration = v);
                        DrawFloatSetting("Duration", transitions, t => t.duration, (t, v) => t.duration = v, AnyMatches(transitions, t => t.hasFixedDuration));
                        DrawFloatSetting("Offset", transitions, t => t.offset, (t, v) => t.offset = v);
                        DrawEnumSetting("Interruption Source", transitions, t => t.interruptionSource, (t, v) => t.interruptionSource = v);
                        DrawBoolSetting("Ordered Interruption", transitions, t => t.orderedInterruption, (t, v) => t.orderedInterruption = v, AnyMatches(transitions, t => t.interruptionSource != TransitionInterruptionSource.None));
                        if (group.Key.IsAnyState)
                            DrawBoolSetting("Can Transition To Self", transitions, t => t.canTransitionToSelf, (t, v) => t.canTransitionToSelf = v);
                    }
                }
            }
        }

        private static Dictionary<TransitionGroupKey, List<AnimatorStateTransition>> CollectTransitions(object[] selectedObjects)
        {
            var groups = new Dictionary<TransitionGroupKey, List<AnimatorStateTransition>>(TransitionGroupKeyComparer.Instance);

            var transitions = new List<AnimatorStateTransition>();
            var anyTransitionSelected = false;
            var states = new List<AnimatorState>();

            foreach (var selected in selectedObjects)
            {
                if (selected is AnimatorStateTransition transition)
                {
                    anyTransitionSelected = true;
                    transitions.Add(transition);
                }
                else if (selected is AnimatorState state)
                {
                    states.Add(state);
                }
            }

            // If no transitions are selected, treat all outgoing transitions of any selected state as selected
            if (!anyTransitionSelected && states.Count > 0)
            {
                foreach (var state in states)
                {
                    var outgoing = state.transitions;
                    foreach (var t in outgoing)
                        transitions.Add(t);
                }
            }

            foreach (var transition in transitions)
            {
                var controller = ResolveController(transition);
                var (sourceName, isAnyState) = ResolveSourceState(transition, controller);
                var key = new TransitionGroupKey(sourceName, isAnyState, transition);

                if (!groups.TryGetValue(key, out var list))
                {
                    list = new List<AnimatorStateTransition>();
                    groups[key] = list;
                }
                list.Add(transition);
            }

            return groups;
        }

        private static List<string> BuildConditionParts(AnimatorStateTransition transition)
        {
            var parts = new List<string>();
            var conditions = transition.conditions;
            for (var i = 0; i < conditions.Length; i++)
            {
                var c = conditions[i];

                switch (c.mode)
                {
                    case AnimatorConditionMode.Equals:
                        parts.Add($"{c.parameter} = {c.threshold:0.####}");
                        break;
                    case AnimatorConditionMode.NotEqual:
                        parts.Add($"{c.parameter} != {c.threshold:0.####}");
                        break;
                    case AnimatorConditionMode.Greater:
                        parts.Add($"{c.parameter} > {c.threshold:0.####}");
                        break;
                    case AnimatorConditionMode.Less:
                        parts.Add($"{c.parameter} < {c.threshold:0.####}");
                        break;
                    case AnimatorConditionMode.If:
                        parts.Add(c.parameter);
                        break;
                    case AnimatorConditionMode.IfNot:
                        parts.Add("!" + c.parameter);
                        break;
                }
            }
            return parts;
        }

        private static void DrawBoolSetting(string label, List<AnimatorStateTransition> transitions, Func<AnimatorStateTransition, bool> get, Action<AnimatorStateTransition, bool> set, bool enabled = true)
        {
            var value = get(transitions[0]);
            var mixed = transitions.Count > 1;
            for (var i = 1; i < transitions.Count && mixed; i++)
                mixed = get(transitions[i]) != value;

            EditorGUI.showMixedValue = mixed;
            using (new EditorGUI.DisabledScope(!enabled))
            {
                var newValue = EditorGUILayout.Toggle(label, value);
                if (newValue != value)
                {
                    for (var i = 0; i < transitions.Count; i++)
                    {
                        set(transitions[i], newValue);
                        EditorUtility.SetDirty(transitions[i]);
                    }
                }
            }
            EditorGUI.showMixedValue = false;
        }

        private static void DrawFloatSetting(string label, List<AnimatorStateTransition> transitions, Func<AnimatorStateTransition, float> get, Action<AnimatorStateTransition, float> set, bool enabled = true)
        {
            var value = get(transitions[0]);
            var mixed = transitions.Count > 1;
            for (var i = 1; i < transitions.Count && mixed; i++)
                mixed = get(transitions[i]) != value;

            EditorGUI.showMixedValue = mixed;
            using (new EditorGUI.DisabledScope(!enabled))
            {
                var newValue = EditorGUILayout.FloatField(label, value);
                if (newValue != value)
                {
                    for (var i = 0; i < transitions.Count; i++)
                    {
                        set(transitions[i], newValue);
                        EditorUtility.SetDirty(transitions[i]);
                    }
                }
            }
            EditorGUI.showMixedValue = false;
        }

        private static void DrawEnumSetting<T>(string label, List<AnimatorStateTransition> transitions, Func<AnimatorStateTransition, T> get, Action<AnimatorStateTransition, T> set, bool enabled = true) where T : struct, System.Enum
        {
            var value = get(transitions[0]);
            var mixed = transitions.Count > 1;
            var equality = EqualityComparer<T>.Default;
            for (var i = 1; i < transitions.Count && mixed; i++)
                mixed = !equality.Equals(get(transitions[i]), value);

            EditorGUI.showMixedValue = mixed;
            using (new EditorGUI.DisabledScope(!enabled))
            {
                Enum newValue = EditorGUILayout.EnumPopup(label, (Enum)value);
                if (!equality.Equals((T)newValue, value))
                {
                    for (var i = 0; i < transitions.Count; i++)
                    {
                        set(transitions[i], (T)newValue);
                        EditorUtility.SetDirty(transitions[i]);
                    }
                }
            }
            EditorGUI.showMixedValue = false;
        }

        private static bool AnyMatches(List<AnimatorStateTransition> transitions, Func<AnimatorStateTransition, bool> predicate)
        {
            for (var i = 0; i < transitions.Count; i++)
            {
                if (predicate(transitions[i]))
                    return true;
            }
            return false;
        }

        private static bool AnyMixedValue(List<AnimatorStateTransition> transitions)
        {
            if (transitions.Count < 2)
                return false;

            var any = false;
            for (var i = 1; i < transitions.Count && !any; i++)
            {
                var a = transitions[0];
                var b = transitions[i];
                any = a.hasExitTime != b.hasExitTime
                    || a.exitTime != b.exitTime
                    || a.hasFixedDuration != b.hasFixedDuration
                    || a.duration != b.duration
                    || a.offset != b.offset
                    || a.interruptionSource != b.interruptionSource
                    || a.orderedInterruption != b.orderedInterruption
                    || (a.canTransitionToSelf != b.canTransitionToSelf);
            }
            return any;
        }

        private struct TransitionGroupKey
        {
            public string SourceName;
            public bool IsAnyState;
            public AnimatorState Destination;
            public AnimatorStateMachine DestinationMachine;
            public string DestinationName;

            public TransitionGroupKey(string sourceName, bool isAnyState, AnimatorStateTransition transition)
            {
                SourceName = sourceName;
                IsAnyState = isAnyState;
                var isExit = transition.isExit;
                var destinationState = isExit ? null : transition.destinationState;
                var destinationStateMachine = isExit ? null : transition.destinationStateMachine;
                Destination = destinationState;
                DestinationMachine = destinationStateMachine;
                DestinationName = isExit
                    ? "(Exit)"
                    : (destinationState != null
                        ? destinationState.name
                        : (destinationStateMachine != null ? destinationStateMachine.name : "?"));
            }
        }

        private sealed class TransitionGroupKeyComparer : IEqualityComparer<TransitionGroupKey>
        {
            public static readonly TransitionGroupKeyComparer Instance = new TransitionGroupKeyComparer();

            public bool Equals(TransitionGroupKey a, TransitionGroupKey b)
            {
                return string.Equals(a.SourceName, b.SourceName, StringComparison.Ordinal)
                    && a.Destination == b.Destination
                    && a.DestinationMachine == b.DestinationMachine;
            }

            public int GetHashCode(TransitionGroupKey obj)
            {
                unchecked
                {
                    var hash = obj.SourceName != null ? obj.SourceName.GetHashCode() : 0;
                    hash = (hash * 397) ^ (obj.Destination != null ? obj.Destination.GetInstanceID() : 0);
                    hash = (hash * 397) ^ (obj.DestinationMachine != null ? obj.DestinationMachine.GetInstanceID() : 0);
                    return hash;
                }
            }
        }

        private static AnimatorController ResolveController(AnimatorStateTransition transition)
        {
            var path = AssetDatabase.GetAssetPath(transition);
            if (string.IsNullOrEmpty(path))
                return null;
            return AssetDatabase.LoadMainAssetAtPath(path) as AnimatorController;
        }

        private static (string name, bool isAnyState) ResolveSourceState(AnimatorStateTransition transition, AnimatorController controller)
        {
            if (controller == null)
                return ("Unknown (could not resolve parent AnimatorController)", false);

            var layers = controller.layers;
            for (var li = 0; li < layers.Length; li++)
            {
                var result = FindSourceInStateMachine(layers[li].stateMachine, transition);
                if (result.name != null)
                    return result;
            }
            return ("Orphaned (not in any state of selected controller)", false);
        }

        private static (string name, bool isAnyState) FindSourceInStateMachine(AnimatorStateMachine stateMachine, AnimatorStateTransition transition)
        {
            if (stateMachine == null)
                return (null, false);

            var anyTransitions = stateMachine.anyStateTransitions;
            for (var i = 0; i < anyTransitions.Length; i++)
            {
                if (anyTransitions[i] == transition)
                    return ($"({stateMachine.name} [Any])", true);
            }

            var states = stateMachine.states;
            for (var i = 0; i < states.Length; i++)
            {
                var stateData = states[i];
                if (stateData.state == null)
                    continue;

                var transitions = stateData.state.transitions;
                for (var j = 0; j < transitions.Length; j++)
                {
                    if (transitions[j] == transition)
                        return (stateData.state.name, false);
                }
            }

            var subStateMachines = stateMachine.stateMachines;
            for (var i = 0; i < subStateMachines.Length; i++)
            {
                var result = FindSourceInStateMachine(subStateMachines[i].stateMachine, transition);
                if (result.name != null)
                    return result;
            }

            return (null, false);
        }

        private void OnSelectionChange()
        {
            Repaint();
        }
    }
}
#endif