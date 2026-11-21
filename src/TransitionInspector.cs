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
            public bool UseTextMode; // false = structured layout (default); true = raw text box
            public List<List<string>> VisualClauses; // draft terms per OR group for the structured layout; null until initialized
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
                state = new ConditionFieldState
                {
                    Text = formattedText,
                    VisualClauses = BuildVisualClauses(canonical)
                };
                conditionFieldStates[headerText] = state;
            }

            // Snapshot before any button can mutate the text this frame.
            var preButtonText = state.Text;
            var parseOk = BoolExpression.TryParse(state.Text, knownParameterNames, out var parsed, out var parseError);
            var editable = !settingsMixed && !hasExitTransition;
            var differs = parseOk && canonical.Differs(parsed);
            // The structured layout is the default; the raw text box is a manual fallback
            // for debugging. A broken draft does NOT force the text box in - the term
            // fields stay editable and show the error - so half-typed terms don't yank
            // the user away from the structured layout.
            var useText = state.UseTextMode;

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

            List<List<string>> newClauses = null;
            var newText = state.Text;
            if (useText)
            {
                var fieldRect = EditorGUILayout.GetControlRect(true, 80f);
                newText = EditorGUI.TextArea(fieldRect, state.Text);
                if (tooltip != null)
                    GUI.Label(fieldRect, new GUIContent("", tooltip));
            }
            else
            {
                // If the draft drifted from the stored clause drafts (e.g. the user edited
                // it in text mode before toggling back), rebuild them from the parsed draft.
                if (state.Text != BuildDraftText(state.VisualClauses))
                    state.VisualClauses = SyncVisualClauses(state.VisualClauses, parsed, knownParameterNames);
                newClauses = DrawVisualConditions(state.VisualClauses);
                if (!parseOk)
                    EditorGUILayout.HelpBox(parseError, MessageType.Error);
                else if (!editable)
                    GUILayout.Label(tooltip, EditorStyles.miniLabel);
                else if (differs)
                    GUILayout.Label("Currently: " + canonical.ToDisplayString(), EditorStyles.miniLabel);
            }
            GUI.contentColor = prevContentColor;
            GUI.enabled = wasEnabled;

            // Buttons are only drawn when the field is editable; each is individually
            // disabled (via DisabledScope) when there's nothing for it to do.
            if (editable)
                DrawConditionButtons(group, state, parsed, parseOk, parseError, differs, formattedText, canonical, knownParameterNames);

            // IMGUI draws the fields before the buttons in the same frame/event, so on the
            // frame a button is clicked the field values still hold the PRE-click data.
            // Only write the fields' value back if a button didn't already change it this frame.
            if (useText)
            {
                if (state.Text == preButtonText && newText != state.Text)
                {
                    state.Text = newText;
                    state.HasUserInput = true;
                }
            }
            else if (state.Text == preButtonText)
            {
                state.VisualClauses = newClauses;
                var builtText = BuildDraftText(newClauses);
                if (builtText != state.Text)
                {
                    state.Text = builtText;
                    state.HasUserInput = true;
                }
            }
        }

        private void DrawConditionButtons(KeyValuePair<TransitionGroupKey, List<AnimatorStateTransition>> group, ConditionFieldState state, BoolExpression parsed, bool parseOk, string parseError, bool differs, string formattedText, BoolExpression canonical, IEnumerable<string> knownParameterNames)
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
                        state.VisualClauses = SyncVisualClauses(state.VisualClauses, canonical, knownParameterNames);
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
                        {
                            state.Text = FormatConditionText(negated);
                            state.VisualClauses = SyncVisualClauses(state.VisualClauses, negated, knownParameterNames);
                        }
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
                        state.VisualClauses = SyncVisualClauses(state.VisualClauses, parsed, knownParameterNames);
                    }
                }

                var toggleLabel = state.UseTextMode ? "Structured" : "Raw text";
                var toggleTooltip = state.UseTextMode
                    ? "Switch to the structured condition layout"
                    : "Switch to the raw text box - one field holding the full expression, mainly useful for debugging";
                if (GUILayout.Button(new GUIContent(toggleLabel, toggleTooltip)))
                    state.UseTextMode = !state.UseTextMode;
            }
        }

        /// <summary>
        /// Draws the structured condition layout: OR-joined boxes, each an AND group of
        /// raw-text term fields, with per-term and per-group remove buttons. Returns the
        /// edited term strings; blank terms and empty groups are excluded from the
        /// resulting expression by <see cref="BuildDraftText"/> at commit time.
        /// </summary>
        private static List<List<string>> DrawVisualConditions(List<List<string>> clauses)
        {
            clauses = clauses ?? new List<List<string>>();
            var newClauses = new List<List<string>>(clauses.Count);

            for (var i = 0; i < clauses.Count; i++)
            {
                var terms = clauses[i];
                var removeClause = false;
                var newTerms = new List<string>(terms.Count);

                using (new EditorGUILayout.HorizontalScope())
                {
                    // An empty label (not a Space) so the first row's indent matches the
                    // OR labels' control geometry exactly, keeping the boxes aligned.
                    GUILayout.Label(
                        i > 0 ? new GUIContent("OR", "Any of these groups can trigger the transition") : new GUIContent(string.Empty),
                        EditorStyles.miniLabel,
                        GUILayout.Width(22f));

                    using (new EditorGUILayout.VerticalScope("box"))
                    {
                        // The AND header row is only worth drawing with 2+ conditions; a
                        // single-condition group has no group-level remove button either,
                        // since removing that one condition already drops the whole group.
                        if (terms.Count > 1)
                        {
                            using (new EditorGUILayout.HorizontalScope())
                            {
                                GUILayout.Label("AND", EditorStyles.miniLabel, GUILayout.Width(26f));
                                GUILayout.FlexibleSpace();
                                if (GUILayout.Button(new GUIContent("\u2715", "Remove this OR group"), GUILayout.Width(26f)))
                                    removeClause = true;
                            }
                        }

                        for (var j = 0; j < terms.Count; j++)
                        {
                            using (new EditorGUILayout.HorizontalScope())
                            {
                                // The add button lives in the row of the last condition,
                                // in the indent column the AND label would occupy. The indent
                                // is reserved with the button's own style + width constraints
                                // in every row, so the fields line up - GUILayout.Space would
                                // not account for the button's margin/padding.
                                var indentRect = GUILayoutUtility.GetRect(GUIContent.none, EditorStyles.miniButton, GUILayout.Width(30f));
                                if (j == terms.Count - 1)
                                {
                                    if (GUI.Button(indentRect, new GUIContent("+", "Add condition"), EditorStyles.miniButton))
                                        newTerms.Add(string.Empty);
                                }

                                var term = EditorGUILayout.TextField(terms[j]);
                                if (GUILayout.Button(new GUIContent("\u2715", "Remove this condition"), GUILayout.Width(26f)))
                                    continue;
                                newTerms.Add(term);
                            }
                        }
                    }
                }

                // A group whose conditions were all removed is dropped along with it,
                // so the layout matches the draft text (empty groups are not part of it).
                if (!removeClause && newTerms.Count > 0)
                    newClauses.Add(newTerms);
            }

            if (clauses.Count == 0)
            {
                using (new EditorGUILayout.VerticalScope("box"))
                    GUILayout.Label("(no conditions - fires unconditionally)", EditorStyles.miniLabel);
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Space(22f);
                if (GUILayout.Button(new GUIContent("+ Condition group"), EditorStyles.miniButton))
                    newClauses.Add(new List<string> { string.Empty });
                GUILayout.FlexibleSpace();
            }

            return newClauses;
        }

        /// <summary>Converts a parsed expression into structured draft term strings.</summary>
        private static List<List<string>> BuildVisualClauses(BoolExpression expr)
        {
            var result = new List<List<string>>();
            if (expr == null)
                return result;
            for (var i = 0; i < expr.Clauses.Count; i++)
            {
                var row = new List<string>(expr.Clauses[i].Count);
                for (var j = 0; j < expr.Clauses[i].Count; j++)
                    row.Add(expr.Clauses[i][j].ToString());
                result.Add(row);
            }
            return result;
        }

        /// <summary>
        /// Rebuilds the raw draft text from the structured term strings. Whitespace terms
        /// and fully empty groups are skipped, so removing every condition of a group
        /// drops the group out of the expression (it can still be filled in later).
        /// </summary>
        private static string BuildDraftText(List<List<string>> clauses)
        {
            if (clauses == null)
                return string.Empty;
            var lines = new List<string>();
            for (var i = 0; i < clauses.Count; i++)
            {
                var line = string.Empty;
                for (var j = 0; j < clauses[i].Count; j++)
                {
                    var s = clauses[i][j];
                    if (string.IsNullOrWhiteSpace(s))
                        continue;
                    line += line.Length == 0 ? s : " & " + s;
                }
                if (line.Length > 0)
                    lines.Add(line);
            }
            return lines.Count == 0 ? string.Empty : string.Join("\n| ", lines);
        }

        /// <summary>
        /// Rebuilds the structured draft from a parsed expression, keeping an old user
        /// string at the same position when it still parses to the same term. When the
        /// expression is empty (e.g. a term broke parsing while the user was typing), the
        /// old row structure is kept and any term that is missing from the parse result is
        /// shown as raw text, so a half-typed condition is never wiped from the fields.
        /// </summary>
        private static List<List<string>> SyncVisualClauses(List<List<string>> old, BoolExpression expr, IEnumerable<string> knownParameterNames)
        {
            if (expr == null || expr.IsEmpty)
            {
                // Keep the rows the user had; blank terms stay blank.
                if (old != null)
                    return old;
                return new List<List<string>>();
            }
            var result = new List<List<string>>(expr.Clauses.Count);
            for (var i = 0; i < expr.Clauses.Count; i++)
            {
                var row = new List<string>(expr.Clauses[i].Count);
                for (var j = 0; j < expr.Clauses[i].Count; j++)
                {
                    var term = expr.Clauses[i][j];
                    var text = term.ToString();
                    if (old != null && i < old.Count && j < old[i].Count
                        && BoolExpression.TryParse(old[i][j], knownParameterNames, out var single, out _)
                        && single.Clauses.Count == 1 && single.Clauses[0].Count == 1
                        && single.Clauses[0][0].Equals(term))
                        text = old[i][j];
                    row.Add(text);
                }
                result.Add(row);
            }
            return result;
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