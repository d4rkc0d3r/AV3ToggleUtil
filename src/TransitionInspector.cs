#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using d4rkpl4y3r.AV3ToggleUtil.Util;

namespace d4rkpl4y3r.AV3ToggleUtil
{
    public class TransitionInspector : EditorWindow
    {
        private Vector2 scrollPos;
        private readonly Dictionary<string, bool> foldoutStates = new Dictionary<string, bool>();
        private readonly Dictionary<string, ConditionFieldState> conditionFieldStates = new Dictionary<string, ConditionFieldState>();
        private readonly HashSet<string> activeGroupHeaders = new HashSet<string>();

        private class ConditionFieldState
        {
            public string Text;
            public bool HasUserInput;
        }

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

            var visibleHeaders = new List<string>();
            foreach (var group in groups)
            {
                using (new EditorGUILayout.VerticalScope("box"))
                {
                    var transitions = group.Value;
                    var stateKey = group.Key.Id;
                    var headerText = $"{group.Key.SourceName} -> {group.Key.DestinationName}";
                    visibleHeaders.Add(stateKey);
                    GUILayout.Label(headerText, EditorStyles.boldLabel);

                    // Multiple transitions from the same source to the same target act as OR; each condition inside a single transition acts as AND
                    var settingsMixed = AnyMixedValue(transitions);
                    var hasExit = AnyMatches(transitions, t => t.isExit);
                    var canonical = GetCanonicalExpression(transitions, out _);
                    var knownParams = GetKnownParameterNames(transitions);
                    DrawConditionEditor(stateKey, group, transitions, canonical, settingsMixed, hasExit, knownParams);

                    // Apply (inside DrawConditionEditor) may remove transitions mid-frame;
                    // drop any destroyed ones before the Settings rows iterate this list.
                    transitions.RemoveAll(t => t == null);

                    if (!foldoutStates.TryGetValue(stateKey, out var showSettings))
                    {
                        showSettings = AnyMixedValue(transitions) || AnyMatches(transitions, t => t.duration > 0f) || AnyMatches(transitions, t => t.hasExitTime);
                    }
                    showSettings = EditorGUILayout.Foldout(showSettings, "Settings", true);
                    foldoutStates[stateKey] = showSettings;
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
            PruneState(visibleHeaders);
        }

        private void PruneState(List<string> visibleHeaders)
        {
            activeGroupHeaders.Clear();
            for (var i = 0; i < visibleHeaders.Count; i++)
                activeGroupHeaders.Add(visibleHeaders[i]);

            var conditionKeys = new List<string>(conditionFieldStates.Keys);
            for (var i = 0; i < conditionKeys.Count; i++)
            {
                if (!activeGroupHeaders.Contains(conditionKeys[i]))
                    conditionFieldStates.Remove(conditionKeys[i]);
            }
            var foldoutKeys = new List<string>(foldoutStates.Keys);
            for (var i = 0; i < foldoutKeys.Count; i++)
            {
                if (!activeGroupHeaders.Contains(foldoutKeys[i]))
                    foldoutStates.Remove(foldoutKeys[i]);
            }
        }

        private void DrawConditionEditor(string headerText, KeyValuePair<TransitionGroupKey, List<AnimatorStateTransition>> group, List<AnimatorStateTransition> transitions, BoolExpression canonical, bool settingsMixed, bool hasExitTransition, IEnumerable<string> knownParameterNames)
        {
            var formattedText = FormatConditionText(canonical);
            if (!conditionFieldStates.TryGetValue(headerText, out var state))
            {
                state = new ConditionFieldState { Text = formattedText };
                conditionFieldStates[headerText] = state;
            }

            // Snapshot before any button can mutate the text this frame.
            var preButtonText = state.Text;
            var parseOk = BoolExpression.TryParse(state.Text, knownParameterNames, out var parsed, out var parseError);
            var editable = !settingsMixed && !hasExitTransition;
            var differs = parseOk && canonical.Differs(parsed);

            string tooltip = null;
            if (!parseOk)
                tooltip = parseError;
            else if (differs)
                tooltip = "Currently: " + state.Text.Replace("\n", " ");
            else if (hasExitTransition)
                tooltip = "Disabled: this group contains an exit transition, which always fires - conditions cannot be edited for it.";
            else if (!editable)
                tooltip = "Disabled: transitions in this group have different settings. Set the settings to identical values first.";

            var wasEnabled = GUI.enabled;
            GUI.enabled = wasEnabled && editable;
            var prevContentColor = GUI.contentColor;
            if (!parseOk)
                GUI.contentColor = new Color(1f, 0.36f, 0.36f);
            var fieldRect = EditorGUILayout.GetControlRect(true, 80f);
            var newText = EditorGUI.TextArea(fieldRect, state.Text);
            if (tooltip != null)
                GUI.Label(fieldRect, new GUIContent("", tooltip));
            GUI.contentColor = prevContentColor;
            GUI.enabled = wasEnabled;

            // Buttons are only drawn when the field is editable; each is individually
            // disabled (via DisabledScope) when there's nothing for it to do.
            if (editable)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    var outerEnabled = GUI.enabled;

                    var canApply = parseOk && differs && !string.IsNullOrWhiteSpace(state.Text);
                    var applyTooltip = canApply
                        ? null
                        : !parseOk ? parseError
                        : string.IsNullOrWhiteSpace(state.Text) ? "Nothing to apply - an empty expression removes all conditions from the current states"
                        : "Nothing to apply - the expression matches the current states";
                    using (new EditorGUI.DisabledScope(!outerEnabled || !canApply))
                    {
                        if (GUILayout.Button(new GUIContent("Apply", applyTooltip)))
                            ApplyExpression(group, parsed);
                    }

                    var canRevert = differs;
                    using (new EditorGUI.DisabledScope(!outerEnabled || !canRevert))
                    {
                        if (GUILayout.Button(new GUIContent("Revert",
                            canRevert ? null : "Nothing to revert - the expression matches the current states")))
                        {
                            state.Text = formattedText;
                            state.HasUserInput = false;
                            GUI.FocusControl(null);
                        }
                    }

                    var canNegate = parseOk && parsed != null && !parsed.IsEmpty;
                    var negateTooltip = !parseOk ? parseError
                        : canNegate ? "Flip the expression (De Morgan)"
                        : "Nothing to negate - give the expression at least one condition first";
                    using (new EditorGUI.DisabledScope(!outerEnabled || !canNegate))
                    {
                        if (GUILayout.Button(new GUIContent("Negate", negateTooltip)))
                        {
                            // Raw text wrap; the parser's '!' handles flattening.
                            var negatedText = "!(" + state.Text.Replace("\n", " ") + ")";
                            state.HasUserInput = true;
                            if (BoolExpression.TryParse(negatedText, knownParameterNames, out var negated, out _))
                                state.Text = FormatConditionText(negated);
                            else
                                state.Text = negatedText;
                            GUI.FocusControl(null);
                        }
                    }

                    // parseOk implies parsed is non-null here.
                    var canNormalize = parseOk && !parsed.IsEmpty;
                    var normalizeTooltip = !parseOk ? parseError
                        : canNormalize ? "Show the simplified canonical form (deduped, simplified, contradictions removed)"
                        : "Nothing to normalize - the expression is empty";
                    using (new EditorGUI.DisabledScope(!outerEnabled || !canNormalize))
                    {
                        if (GUILayout.Button(new GUIContent("Normalize", normalizeTooltip)))
                        {
                            state.Text = FormatConditionText(parsed);
                            GUI.FocusControl(null);
                            state.HasUserInput = true;
                        }
                    }
                }
            }

            // IMGUI draws the text area before the buttons in the same frame/event, so on the
            // frame a button is clicked `newText` still holds the PRE-click text. Only write
            // the field's value back if a button didn't already change it this frame.
            if (state.Text == preButtonText && newText != state.Text)
            {
                state.Text = newText;
                state.HasUserInput = true;
            }
        }

        /// <summary>
        /// Formats an expression as one |-clause per line so the multi-line field
        /// never overflows the window width. Single clauses stay on a single line;
        /// the parser treats newlines as whitespace, so any layout round-trips.
        /// </summary>
        private static string FormatConditionText(BoolExpression expr)
        {
            // Empty (not a label): the parser treats blank input as unconditional,
            // which is the correct value for a group with no conditions.
            if (expr == null) return string.Empty;
            var clauses = expr.Clauses;
            var count = clauses.Count;
            if (count == 0) return string.Empty;

            var lines = new string[count];
            for (var i = 0; i < count; i++)
            {
                var terms = clauses[i];
                var first = true;
                var line = string.Empty;
                for (var j = 0; j < terms.Count; j++)
                {
                    line += first ? terms[j].ToString() : " & " + terms[j];
                    first = false;
                }
                lines[i] = line;
            }
            return count == 1 ? lines[0] : string.Join("\n| ", lines);
        }

        private static bool TryGetSourceLocation(AnimatorStateTransition transition, out AnimatorState sourceState, out AnimatorStateMachine sourceMachine)
        {
            var path = AssetDatabase.GetAssetPath(transition);
            if (string.IsNullOrEmpty(path))
            {
                sourceState = null;
                sourceMachine = null;
                return false;
            }
            var controller = AssetDatabase.LoadMainAssetAtPath(path) as AnimatorController;
            if (controller == null)
            {
                sourceState = null;
                sourceMachine = null;
                return false;
            }

            var layers = controller.layers;
            for (var li = 0; li < layers.Length; li++)
            {
                var stateMachine = layers[li].stateMachine;
                sourceState = null;
                sourceMachine = null;
                if (FindSourceLocationInStateMachine(stateMachine, transition, out sourceState, out sourceMachine))
                    return true;
            }

            sourceState = null;
            sourceMachine = null;
            return false;
        }

        private static bool FindSourceLocationInStateMachine(AnimatorStateMachine stateMachine, AnimatorStateTransition transition, out AnimatorState sourceState, out AnimatorStateMachine sourceMachine)
        {
            sourceState = null;
            sourceMachine = null;
            if (stateMachine == null)
                return false;

            var anyTransitions = stateMachine.anyStateTransitions;
            for (var i = 0; i < anyTransitions.Length; i++)
            {
                if (anyTransitions[i] == transition)
                {
                    sourceMachine = stateMachine;
                    return true;
                }
            }

            var states = stateMachine.states;
            for (var i = 0; i < states.Length; i++)
            {
                var stateData = states[i];
                if (stateData.state == null)
                    continue;

                var stateTransitions = stateData.state.transitions;
                for (var j = 0; j < stateTransitions.Length; j++)
                {
                    if (stateTransitions[j] == transition)
                    {
                        sourceState = stateData.state;
                        return true;
                    }
                }
            }

            var subStateMachines = stateMachine.stateMachines;
            for (var i = 0; i < subStateMachines.Length; i++)
            {
                if (FindSourceLocationInStateMachine(subStateMachines[i].stateMachine, transition, out sourceState, out sourceMachine))
                    return true;
            }

            return false;
        }

        private static Dictionary<TransitionGroupKey, List<AnimatorStateTransition>> CollectTransitions(object[] selectedObjects)
        {
            var groups = new Dictionary<TransitionGroupKey, List<AnimatorStateTransition>>();

            var transitions = new List<AnimatorStateTransition>();
            var anyTransitionSelected = false;
            var selectedStates = new List<AnimatorState>();

            foreach (var selected in selectedObjects)
            {
                if (selected == null) // destroyed
                    continue;
                if (selected is AnimatorStateTransition transition)
                {
                    anyTransitionSelected = true;
                    transitions.Add(transition);
                }
                else if (selected is AnimatorState state)
                {
                    selectedStates.Add(state);
                }
            }

            // If no transitions are selected, treat all outgoing transitions of any selected state as selected
            if (!anyTransitionSelected && selectedStates.Count > 0)
            {
                foreach (var state in selectedStates)
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
                var hasSourceLocation = TryGetSourceLocation(transition, out var sourceState, out var anySourceMachine);
                var key = new TransitionGroupKey(sourceName, isAnyState, sourceState, anySourceMachine, hasSourceLocation, transition);

                if (!groups.TryGetValue(key, out var list))
                {
                    list = new List<AnimatorStateTransition>();
                    groups[key] = list;
                }
                list.Add(transition);
            }

            return groups;
        }

        private static BoolExpression GetCanonicalExpression(List<AnimatorStateTransition> transitions, out string display)
        {
            var expressions = new List<BoolExpression>(transitions.Count);
            for (var i = 0; i < transitions.Count; i++)
                expressions.Add(ExpressionFromTransition(transitions[i]));

            var displayParts = new List<string>();
            for (var i = 0; i < transitions.Count; i++)
            {
                var clause = expressions[i].ToDisplayString();
                if (clause.Length == 0)
                    continue;
                displayParts.Add(clause);
            }
            display = AnyMatches(transitions, t => t.isExit) ? "(exit transition)" : (displayParts.Count > 0 ? string.Join(" || ", displayParts) : "(no conditions)");

            var canonical = new BoolExpression();
            for (var i = 0; i < expressions.Count; i++)
                canonical.Clauses.AddRange(expressions[i].Clauses);
            canonical.Normalize();
            return canonical;
        }

        private static BoolExpression ExpressionFromTransition(AnimatorStateTransition transition)
        {
            var expr = new BoolExpression();
            var clause = new List<BoolTerm>();
            var conditions = transition.conditions;
            for (var i = 0; i < conditions.Length; i++)
            {
                var c = conditions[i];
                clause.Add(new BoolTerm(c.parameter, MapModeToOp(c.mode), c.threshold));
            }
            expr.Clauses.Add(clause);
            expr.Normalize();
            return expr;
        }

        private static BoolTermOp MapModeToOp(AnimatorConditionMode mode)
        {
            switch (mode)
            {
                case AnimatorConditionMode.Equals:
                    return BoolTermOp.Equals;
                case AnimatorConditionMode.NotEqual:
                    return BoolTermOp.NotEquals;
                case AnimatorConditionMode.Greater:
                    return BoolTermOp.Greater;
                case AnimatorConditionMode.Less:
                    return BoolTermOp.Less;
                case AnimatorConditionMode.If:
                    return BoolTermOp.If;
                case AnimatorConditionMode.IfNot:
                    return BoolTermOp.IfNot;
                default:
                    return BoolTermOp.If;
            }
        }

        private static AnimatorConditionMode MapOpToMode(BoolTermOp op)
        {
            switch (op)
            {
                case BoolTermOp.Equals:
                    return AnimatorConditionMode.Equals;
                case BoolTermOp.NotEquals:
                    return AnimatorConditionMode.NotEqual;
                case BoolTermOp.Greater:
                    return AnimatorConditionMode.Greater;
                case BoolTermOp.Less:
                    return AnimatorConditionMode.Less;
                case BoolTermOp.If:
                    return AnimatorConditionMode.If;
                case BoolTermOp.IfNot:
                    return AnimatorConditionMode.IfNot;
                default:
                    return AnimatorConditionMode.If;
            }
        }

        private static void ApplyExpression(KeyValuePair<TransitionGroupKey, List<AnimatorStateTransition>> group, BoolExpression parsed)
        {
            var key = group.Key;
            var transitions = group.Value;
            var clauses = parsed.Clauses;

            if (!key.HasSourceLocation)
                return;

            var controller = ResolveController(transitions[0]);
            var sourceState = key.SourceState;
            var anyMachine = key.AnySourceMachine;

            // Reuse existing transitions for the first clauses, add new ones, remove surplus
            var remainingPool = new List<AnimatorStateTransition>();
            foreach (var t in transitions)
                if (!remainingPool.Contains(t))
                    remainingPool.Add(t);

            var created = new List<AnimatorStateTransition>();

            for (var i = 0; i < clauses.Count; i++)
            {
                AnimatorStateTransition use = null;
                if (sourceState != null && remainingPool.Count > 0)
                {
                    // Prefer a transition still attached at the source location
                    var attached = sourceState.transitions;
                    for (var a = 0; a < attached.Length; a++)
                    {
                        if (remainingPool.Remove(attached[a]))
                        {
                            use = attached[a];
                            break;
                        }
                    }
                    if (use == null && i < remainingPool.Count)
                        use = remainingPool[i];
                }

                if (use == null)
                {
                    var template = transitions[0];
                    if (sourceState != null)
                    {
                        use = key.Destination != null
                            ? sourceState.AddTransition(key.Destination)
                            : sourceState.AddTransition(key.DestinationMachine);
                    }
                    else
                    {
                        use = key.Destination != null
                            ? anyMachine.AddAnyStateTransition(key.Destination)
                            : anyMachine.AddAnyStateTransition(key.DestinationMachine);
                    }
                    if (use == null)
                        return;
                    // Clone settings from the first existing transition, then restore the destination
                    EditorUtility.CopySerialized(template, use);
                    use.isExit = false;
                    use.destinationState = key.Destination;
                    use.destinationStateMachine = key.DestinationMachine;
                    created.Add(use);
                }

                SetConditions(use, clauses[i]);
            }

            // Remove transitions that were not reused
            for (var i = 0; i < remainingPool.Count; i++)
                RemoveTransition(remainingPool[i]);

            if (controller != null)
                EditorUtility.SetDirty(controller);

            // Keep the newly created transitions inspectable without re-selecting them
            if (created.Count > 0)
                AddToSelection(created);
        }

        /// <summary>Merges objects into the current selection, preserving order and skipping duplicates.</summary>
        private static void AddToSelection(List<AnimatorStateTransition> add)
        {
            var current = Selection.objects;
            var merged = current.Where(o => o != null)
                .Concat(add)
                .Distinct()
                .ToArray();
            Selection.objects = merged; // OnSelectionChange repaints the window
        }

        private static void SetConditions(AnimatorStateTransition transition, List<BoolTerm> clause)
        {
            var conditions = new AnimatorCondition[clause.Count];
            for (var i = 0; i < clause.Count; i++)
            {
                var term = clause[i];
                var op = term.Op;
                var isBare = op == BoolTermOp.If || op == BoolTermOp.IfNot;
                conditions[i] = new AnimatorCondition
                {
                    parameter = term.Parameter,
                    mode = MapOpToMode(op),
                    threshold = isBare ? 0f : term.Threshold
                };
            }
            transition.conditions = conditions;
        }

        private static void RemoveTransition(AnimatorStateTransition transition)
        {
            var controller = ResolveController(transition);
            if (controller == null)
                return;

            var layers = controller.layers;
            for (var li = 0; li < layers.Length; li++)
            {
                if (RemoveTransitionInStateMachine(layers[li].stateMachine, transition))
                    return;
            }
        }

        private static bool RemoveTransitionInStateMachine(AnimatorStateMachine stateMachine, AnimatorStateTransition transition)
        {
            if (stateMachine == null)
                return false;

            var anyTransitions = stateMachine.anyStateTransitions;
            for (var i = 0; i < anyTransitions.Length; i++)
            {
                if (anyTransitions[i] == transition)
                {
                    stateMachine.RemoveAnyStateTransition(transition);
                    return true;
                }
            }

            var states = stateMachine.states;
            for (var i = 0; i < states.Length; i++)
            {
                var state = states[i].state;
                if (state == null)
                    continue;

                var transitions = state.transitions;
                for (var j = 0; j < transitions.Length; j++)
                {
                    if (transitions[j] == transition)
                    {
                        state.RemoveTransition(transition);
                        return true;
                    }
                }
            }

            var subStateMachines = stateMachine.stateMachines;
            for (var i = 0; i < subStateMachines.Length; i++)
            {
                if (RemoveTransitionInStateMachine(subStateMachines[i].stateMachine, transition))
                    return true;
            }

            return false;
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
            public string Id;
            public string SourceName;
            public bool IsAnyState;
            public AnimatorState SourceState;
            public AnimatorStateMachine AnySourceMachine;
            public bool HasSourceLocation;
            public AnimatorState Destination;
            public AnimatorStateMachine DestinationMachine;
            public string DestinationName;

            public TransitionGroupKey(string sourceName, bool isAnyState, AnimatorState sourceState, AnimatorStateMachine anySourceMachine, bool hasSourceLocation, AnimatorStateTransition transition)
            {
                SourceName = sourceName;
                IsAnyState = isAnyState;
                SourceState = sourceState;
                AnySourceMachine = anySourceMachine;
                HasSourceLocation = hasSourceLocation;

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

                unchecked
                {
                    var hash = string.IsNullOrEmpty(sourceName) ? 0 : sourceName.GetHashCode();
                    hash = (hash * 397) ^ (sourceState != null ? sourceState.GetInstanceID() : 0);
                    hash = (hash * 397) ^ (anySourceMachine != null ? anySourceMachine.GetInstanceID() : 0);
                    hash = (hash * 397) ^ (destinationState != null ? destinationState.GetInstanceID() : 0);
                    hash = (hash * 397) ^ (destinationStateMachine != null ? destinationStateMachine.GetInstanceID() : 0);
                    Id = sourceName + "#" + hash;
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

        private static IEnumerable<string> GetKnownParameterNames(List<AnimatorStateTransition> transitions)
        {
            var first = transitions.Find(t => t != null);
            if (first == null)
                return null;

            var controller = ResolveController(first);
            if (controller == null)
                return null;

            var parameters = controller.parameters;
            var names = new string[parameters.Length];
            for (var i = 0; i < parameters.Length; i++)
                names[i] = parameters[i].name;
            return names;
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