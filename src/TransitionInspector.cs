#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
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
            public bool UseTextMode;
            public List<List<string>> VisualClauses;
            public List<AliasRow> AliasRows;
            public HashSet<string> RemovedAliasParams;
            public bool AliasInitDone;
        }

        private class AliasRow
        {
            public string Alias;
            public string Param;
            /// <summary>The alias token that the text field currently contains. Updated whenever a valid rename takes effect.</summary>
            public string CommittedAlias;
        }

        private enum AliasSweepMode
        {
            Expand,
            Compress,
            Swap
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

                    DrawConditionEditor(stateKey, group);

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

            PruneKeys(conditionFieldStates);
            PruneKeys(foldoutStates);
        }

        private void PruneKeys<T>(Dictionary<string, T> dict)
        {
            var keys = new List<string>(dict.Keys);
            for (var i = 0; i < keys.Count; i++)
            {
                if (!activeGroupHeaders.Contains(keys[i]))
                    dict.Remove(keys[i]);
            }
        }

        private void DrawConditionEditor(string stateKey, KeyValuePair<TransitionGroupKey, List<AnimatorStateTransition>> group)
        {
            var transitions = group.Value;
            var canonical = GetCanonicalExpression(transitions, out _);
            // Show the cheapest known equivalent from the very first frame (Minify is
            // idempotent, so this is frame-stable) and keep Apply/Revert in sync by
            // diffing against the same minified form. Also guarantees a freshly
            // selected group is displayed minified.
            canonical.Minify();
            var settingsMixed = AnyMixedValue(transitions);
            var hasExitTransition = AnyMatches(transitions, t => t.isExit);
            var parameterInfo = GetKnownParameterInfo(transitions);
            var formattedText = FormatConditionText(canonical);
            if (!conditionFieldStates.TryGetValue(stateKey, out var state))
            {
                state = new ConditionFieldState
                {
                    Text = formattedText,
                    UseTextMode = true,
                    VisualClauses = BuildVisualClauses(canonical)
                };
                conditionFieldStates[stateKey] = state;
            }

            var preButtonText = state.Text; // snapshot before any button can mutate it this frame
            var knownNames = parameterInfo?.Names;
            var knownTypes = parameterInfo?.Types;
            var useText = state.UseTextMode;
            // Text mode accepts short aliases ("a & !b"); expand them to the real parameter
            // names before parsing so everything downstream works on conventional text.
            var aliasRows = useText ? GetValidAliasRows(state.AliasRows, knownNames) : null;
            var parseText = aliasRows != null ? SweepAliases(state.Text, aliasRows, AliasSweepMode.Expand) : state.Text;
            var parseOk = BoolExpression.TryParse(parseText, knownNames, knownTypes, out var parsed, out var parseError);
            if (useText && parseOk)
            {
                SeedAliasRows(state, parsed, knownNames);
                // A group starts in text mode with the fully spelled-out expression -
                // compress it to aliases once, right after the rows have been seeded.
                if (!state.AliasInitDone)
                {
                    state.AliasInitDone = true;
                    state.Text = SweepAliases(state.Text, GetValidAliasRows(state.AliasRows, knownNames), AliasSweepMode.Compress);
                }
            }
            var editable = !settingsMixed && !hasExitTransition;
            // Compare logical content (canonical DNF), not tree shape - that is what
            // the transitions actually store, so structurally different but equivalent
            // text (e.g. right after Minify) does not light up Apply/Revert.
            var differs = parseOk && canonical.LogicallyDiffers(parsed);

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
                DrawAliasTable(state, knownNames, out newText, tooltip);
            }
            else
            {
                if (state.Text != BuildDraftText(state.VisualClauses))
                    state.VisualClauses = SyncVisualClauses(state.VisualClauses, parsed, knownNames, knownTypes);
                newClauses = DrawVisualConditions(state.VisualClauses, parameterInfo);
                if (!parseOk)
                    EditorGUILayout.HelpBox(parseError, MessageType.Error);
                else if (!editable)
                    GUILayout.Label(tooltip, EditorStyles.miniLabel);
            }
            GUI.contentColor = prevContentColor;
            GUI.enabled = wasEnabled;

            if (editable)
                DrawConditionButtons(group, state, parsed, parseOk, parseError, differs, formattedText, canonical, knownNames, knownTypes, aliasRows);

            if (useText)
            {
                if (state.Text == preButtonText && newText != state.Text)
                    state.Text = newText;
            }
            else if (state.Text == preButtonText)
            {
                state.VisualClauses = newClauses;
                var builtText = BuildDraftText(newClauses);
                if (builtText != state.Text)
                    state.Text = builtText;
            }
        }

        private void DrawConditionButtons(KeyValuePair<TransitionGroupKey, List<AnimatorStateTransition>> group, ConditionFieldState state, BoolExpression parsed, bool parseOk, string parseError, bool differs, string formattedText, BoolExpression canonical, IEnumerable<string> knownParameterNames, Dictionary<string, AnimatorControllerParameterType> parameterTypes, List<AliasRow> aliasRows)
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
                        state.Text = state.UseTextMode ? SweepAliases(formattedText, aliasRows, AliasSweepMode.Compress) : formattedText;
                        state.VisualClauses = SyncVisualClauses(state.VisualClauses, canonical, knownParameterNames, parameterTypes);
                        GUI.FocusControl(null);
                    }
                }

                // Negation is total — every term op has a complement — so no preview
                // is needed; the button is simply enabled whenever there's something to flip.
                var canNegate = parseOk && !parsed.IsEmpty;
                var negateTooltip = !parseOk ? parseError
                    : !canNegate
                        ? "Nothing to negate - give the expression at least one condition first"
                        : "Flip the expression (De Morgan)";
                using (new EditorGUI.DisabledScope(!outerEnabled || !canNegate))
                {
                    if (GUILayout.Button(new GUIContent("Negate", negateTooltip)))
                    {
                        if (BoolExpression.TryNegate(parsed, out var negated, out _))
                        {
                            state.Text = state.UseTextMode ? SweepAliases(FormatConditionText(negated), aliasRows, AliasSweepMode.Compress) : FormatConditionText(negated);
                            state.VisualClauses = SyncVisualClauses(state.VisualClauses, negated, knownParameterNames, parameterTypes);
                        }
                        GUI.FocusControl(null);
                    }
                }

                var canDnf = parseOk && !parsed.IsEmpty;
                var dnfTooltip = !parseOk ? parseError
                    : canDnf ? "Rewrite the expression to disjunctive normal form (AND terms joined with OR)"
                    : "Nothing to rewrite - the expression is empty";
                using (new EditorGUI.DisabledScope(!outerEnabled || !canDnf))
                {
                    if (GUILayout.Button(new GUIContent("DNF", dnfTooltip)))
                    {
                        var dnfText = parsed.ToDnfString();
                        state.Text = state.UseTextMode ? SweepAliases(dnfText, aliasRows, AliasSweepMode.Compress) : dnfText;
                        GUI.FocusControl(null);
                        state.VisualClauses = SyncVisualClauses(state.VisualClauses, parsed, knownParameterNames, parameterTypes);
                    }
                }

                var canMinify = canDnf;
                var minifyTooltip = !parseOk ? parseError
                    : canMinify ? "Rewrite the expression with (heuristically) fewer operators - may stay nested, unlike DNF"
                    : "Nothing to rewrite - the expression is empty";
                using (new EditorGUI.DisabledScope(!outerEnabled || !canMinify))
                {
                    if (GUILayout.Button(new GUIContent("Minify", minifyTooltip)))
                    {
                        parsed.Minify();
                        var minText = parsed.ToDisplayString();
                        state.Text = state.UseTextMode ? SweepAliases(minText, aliasRows, AliasSweepMode.Compress) : minText;
                        GUI.FocusControl(null);
                        state.VisualClauses = SyncVisualClauses(state.VisualClauses, parsed, knownParameterNames, parameterTypes);
                    }
                }

                var toggleLabel = state.UseTextMode ? "Structured" : "Raw text";
                var toggleTooltip = state.UseTextMode
                    ? "Switch to the structured condition layout"
                    : "Switch to the raw text box - one field holding the full expression, mainly useful for debugging";
                if (GUILayout.Button(new GUIContent(toggleLabel, toggleTooltip)))
                {
                    state.UseTextMode = !state.UseTextMode;
                    GUI.FocusControl(null);
                }
            }
        }

        // ---- Text-mode aliases -----------------------------------------------------
        //
        // The text field accepts both real parameter names and short aliases ("a & !b").
        // Aliases are stored per transition group (in ConditionFieldState), seeded from the
        // parameters actually used in the expression, and applied to the raw text just
        // before it is parsed, so all parsing / diffing logic works on the expanded form.

        private void DrawAliasTable(ConditionFieldState state, string[] knownNames, out string newText, string tooltip)
        {
            DrawAliasRowsAndButtons(state, knownNames);

            var fieldRect = EditorGUILayout.GetControlRect(true, 80f);
            newText = EditorGUI.TextArea(fieldRect, state.Text);
            if (tooltip != null)
                GUI.Label(fieldRect, new GUIContent("", tooltip));
        }

        private void DrawAliasRowsAndButtons(ConditionFieldState state, string[] knownNames)
        {
            if (knownNames == null || knownNames.Length == 0)
                return;

            state.AliasRows ??= new List<AliasRow>();
            state.RemovedAliasParams ??= new HashSet<string>();
            var rows = state.AliasRows;

            var removedIndices = new List<int>();
            for (var i = 0; i < rows.Count; i++)
            {
                var row = rows[i] ?? new AliasRow { Alias = string.Empty };
                rows[i] = row;
                using (new EditorGUILayout.HorizontalScope())
                {
                    var aliasValid = IsValidAlias(row.Alias, knownNames, rows, i);
                    var wasColor = GUI.contentColor;
                    if (!aliasValid)
                        GUI.contentColor = new Color(1f, 0.36f, 0.36f);
                    var newAlias = EditorGUILayout.TextField(
                        new GUIContent(string.Empty,
                            aliasValid
                                ? null
                                : "Invalid alias - use letters/digits/underscore, start with a letter, and don't clash with a parameter name or another alias"),
                        row.Alias,
                        GUILayout.Width(120f));
                    GUI.contentColor = wasColor;
                    row.Alias = newAlias;
                    // Live-rename: once the freshly typed alias is valid, rewrite every
                    // occurrence of the previous alias in the text field. Invalid interim
                    // values (while a word is half-typed, collisions, ...) simply wait.
                    if (IsValidAlias(newAlias, knownNames, rows, i)
                        && row.CommittedAlias != null
                        && row.CommittedAlias != newAlias)
                    {
                        state.Text = ReplaceTokens(state.Text, row.CommittedAlias, newAlias);
                        row.CommittedAlias = newAlias;
                    }

                    var paramIndex = row.Param == null ? -1 : Array.IndexOf(knownNames, row.Param);
                    var newParamIndex = EditorGUILayout.Popup(paramIndex < 0 ? 0 : paramIndex, knownNames, GUILayout.Width(220f));
                    var newParam = knownNames[newParamIndex];
                    if (newParam != row.Param)
                    {
                        // Parameter names are unique across rows - hand this row's old
                        // parameter to whichever row had the newly chosen one.
                        for (var j = 0; j < rows.Count; j++)
                        {
                            if (j != i && rows[j] != null && string.Equals(rows[j].Param, newParam, StringComparison.Ordinal))
                            {
                                rows[j].Param = row.Param;
                                break;
                            }
                        }
                        // The freed parameter is no longer explicitly aliased - let it re-seed.
                        state.RemovedAliasParams.Remove(row.Param);
                        row.Param = newParam;
                        state.RemovedAliasParams.Remove(newParam);
                    }

                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button(new GUIContent("\u2715", "Remove this alias"), GUILayout.Width(26f)))
                        removedIndices.Add(i);
                }
            }
            for (var k = removedIndices.Count - 1; k >= 0; k--)
            {
                // Remember the removal so the seed loop doesn't immediately re-add the row.
                var index = removedIndices[k];
                if (rows[index] != null && !string.IsNullOrEmpty(rows[index].Param))
                    state.RemovedAliasParams.Add(rows[index].Param);
                rows.RemoveAt(index);
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(new GUIContent("+ Alias", "Add a new alias row"), GUILayout.Width(70f)))
                {
                    var usedParams = new HashSet<string>(StringComparer.Ordinal);
                    for (var r = 0; r < rows.Count; r++)
                        if (rows[r] != null && rows[r].Param != null)
                            usedParams.Add(rows[r].Param);

                    string param = null;
                    for (var r = 0; r < knownNames.Length; r++)
                    {
                        if (knownNames[r] != null && !usedParams.Contains(knownNames[r]))
                        {
                            param = knownNames[r];
                            break;
                        }
                    }
                    if (param == null)
                        param = knownNames[0];

                    var seededAlias = NextFreeAlias(rows);
                    rows.Add(new AliasRow { Alias = seededAlias, Param = param, CommittedAlias = seededAlias });
                    state.RemovedAliasParams.Remove(param);
                }

                var validRows = GetValidAliasRows(rows, knownNames);
                using (new EditorGUI.DisabledScope(validRows.Count == 0))
                {
                    if (GUILayout.Button(new GUIContent("\u21C4 Swap", "Rewrite the text field, turning aliases into real parameter names and real parameter names into aliases"), GUILayout.Width(80f)))
                    {
                        state.Text = SweepAliases(state.Text, validRows, AliasSweepMode.Swap);
                        GUI.FocusControl(null);
                    }
                }
                GUILayout.FlexibleSpace();
            }
        }

        private static bool IsValidAlias(string alias, string[] knownNames, List<AliasRow> rows, int selfIndex)
        {
            if (string.IsNullOrEmpty(alias) || !char.IsLetter(alias[0]))
                return false;
            for (var i = 1; i < alias.Length; i++)
            {
                var c = alias[i];
                if (!char.IsLetterOrDigit(c) && c != '_')
                    return false;
            }
            for (var i = 0; i < knownNames.Length; i++)
            {
                if (knownNames[i] != null && string.Equals(alias, knownNames[i], StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            for (var r = 0; r < rows.Count; r++)
            {
                if (r == selfIndex || rows[r] == null)
                    continue;
                if (string.Equals(alias, rows[r].Alias, StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            return true;
        }

        private static List<AliasRow> GetValidAliasRows(List<AliasRow> rows, string[] knownNames)
        {
            var valid = new List<AliasRow>();
            if (rows == null || knownNames == null)
                return valid;
            for (var i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                if (row == null || string.IsNullOrEmpty(row.Param))
                    continue;
                if (IsValidAlias(row.Alias, knownNames, rows, i))
                    valid.Add(row);
            }
            return valid;
        }

        /// <summary>
        /// Makes sure every parameter used in the expression has an alias row (seeded with the
        /// next free letter), unless the user explicitly removed that row via the X button.
        /// </summary>
        private static void SeedAliasRows(ConditionFieldState state, BoolExpression parsed, string[] knownNames)
        {
            if (knownNames == null || knownNames.Length == 0 || parsed == null)
                return;

            state.AliasRows ??= new List<AliasRow>();
            state.RemovedAliasParams ??= new HashSet<string>();
            var rows = state.AliasRows;

            var used = new HashSet<string>(StringComparer.Ordinal);
            parsed.CollectParameters(used);

            // Forget removals for parameters that are no longer referenced at all.
            var stale = new List<string>(state.RemovedAliasParams.Count);
            foreach (var param in state.RemovedAliasParams)
            {
                if (!used.Contains(param))
                    stale.Add(param);
            }
            for (var i = 0; i < stale.Count; i++)
                state.RemovedAliasParams.Remove(stale[i]);

            for (var i = 0; i < knownNames.Length; i++)
            {
                var param = knownNames[i];
                if (string.IsNullOrEmpty(param) || !used.Contains(param) || state.RemovedAliasParams.Contains(param))
                    continue;

                var hasRow = false;
                for (var r = 0; r < rows.Count; r++)
                {
                    if (rows[r] != null && string.Equals(rows[r].Param, param, StringComparison.Ordinal))
                    {
                        hasRow = true;
                        break;
                    }
                }
                if (!hasRow)
                {
                    var seededAlias = NextFreeAlias(rows);
                    rows.Add(new AliasRow { Alias = seededAlias, Param = param, CommittedAlias = seededAlias });
                }
            }
        }

        private static string NextFreeAlias(List<AliasRow> rows)
        {
            for (var i = 0; i < 26; i++)
            {
                var candidate = ((char)('a' + i)).ToString();
                if (!AliasTaken(rows, candidate))
                    return candidate;
            }
            for (var n = 2; n < 100; n++)
            {
                for (var i = 0; i < 26; i++)
                {
                    var candidate = ((char)('a' + i)).ToString() + n;
                    if (!AliasTaken(rows, candidate))
                        return candidate;
                }
            }
            return "alias" + rows.Count;
        }

        private static bool AliasTaken(List<AliasRow> rows, string candidate)
        {
            for (var i = 0; i < rows.Count; i++)
            {
                if (rows[i] != null && string.Equals(rows[i].Alias, candidate, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Sweeps the text token by token and substitutes alias tokens / real parameter names
        /// per mode. A token is a run of identifier characters (plus internal whitespace, like the
        /// parser's candidate scan), so alias "a" never rewrites the parameter "a hat". Real
        /// parameter names are never aliased by validation, so matching is exact and unambiguous.
        /// </summary>
        private static string SweepAliases(string text, List<AliasRow> rows, AliasSweepMode mode)
        {
            if (text == null || rows == null || rows.Count == 0)
                return text ?? string.Empty;

            var sb = new StringBuilder(text.Length);
            var i = 0;
            while (i < text.Length)
            {
                var c = text[i];
                if (char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.' || c == '$')
                {
                    var end = ScanCandidateRunEnd(text, i);
                    var candidate = text.Substring(i, end - i);
                    string replacement = null;
                    for (var r = 0; r < rows.Count && replacement == null; r++)
                    {
                        var row = rows[r];
                        if (row == null || string.IsNullOrEmpty(row.Alias) || string.IsNullOrEmpty(row.Param))
                            continue;
                        if (mode == AliasSweepMode.Expand)
                        {
                            if (string.Equals(candidate, row.Alias, StringComparison.Ordinal))
                                replacement = row.Param;
                        }
                        else if (mode == AliasSweepMode.Compress)
                        {
                            if (string.Equals(candidate, row.Param, StringComparison.Ordinal))
                                replacement = row.Alias;
                        }
                        else
                        {
                            if (string.Equals(candidate, row.Alias, StringComparison.Ordinal))
                                replacement = row.Param;
                            else if (string.Equals(candidate, row.Param, StringComparison.Ordinal))
                                replacement = row.Alias;
                        }
                    }
                    sb.Append(replacement ?? candidate);
                    i = end;
                }
                else
                {
                    sb.Append(c);
                    i++;
                }
            }
            return sb.ToString();
        }

        /// <summary>
        /// Replaces whole-token occurrences of <paramref name="from"/> in the text with
        /// <paramref name="to"/> using the same tokenization as <see cref="SweepAliases"/>.
        /// </summary>
        private static string ReplaceTokens(string text, string from, string to)
        {
            if (string.IsNullOrEmpty(from) || from == to)
                return text ?? string.Empty;

            var sb = new StringBuilder(text.Length);
            var i = 0;
            while (i < text.Length)
            {
                var c = text[i];
                if (char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.' || c == '$')
                {
                    var end = ScanCandidateRunEnd(text, i);
                    var candidate = text.Substring(i, end - i);
                    sb.Append(string.Equals(candidate, from, StringComparison.Ordinal) ? to : candidate);
                    i = end;
                }
                else
                {
                    sb.Append(c);
                    i++;
                }
            }
            return sb.ToString();
        }

        /// <summary>
        /// Returns the end index of the identifier-ish run starting at <paramref name="start"/>
        /// (which must begin with an identifier character), consuming internal whitespace runs
        /// exactly like BoolExpression's candidate scan.
        /// </summary>
        private static int ScanCandidateRunEnd(string text, int start)
        {
            var pos = start;
            var lastNonWs = start;
            while (pos < text.Length)
            {
                var c = text[pos];
                if (char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.' || c == '$')
                {
                    lastNonWs = pos + 1;
                    pos++;
                }
                else if (char.IsWhiteSpace(c))
                {
                    var t = pos;
                    while (t < text.Length && char.IsWhiteSpace(text[t]))
                        t++;
                    if (t < text.Length)
                    {
                        var nc = text[t];
                        if (char.IsLetterOrDigit(nc) || nc == '_' || nc == '-' || nc == '.' || nc == '$')
                        {
                            pos = t;
                            continue;
                        }
                    }
                    break;
                }
                else
                    break;
            }
            return lastNonWs;
        }

        private static readonly string[] BoolOpLabels = { "true", "false" };
        private static readonly string[] CompareOpLabels =
        {
            "GreaterOrEqual", "Greater", "LessOrEqual", "Less", "Equals", "NotEqual"
        };
        private static readonly BoolTermOp[] CompareOpValues =
        {
            BoolTermOp.GreaterEqual, BoolTermOp.Greater, BoolTermOp.LessEqual, BoolTermOp.Less,
            BoolTermOp.Equals, BoolTermOp.NotEquals
        };
        private static readonly System.Globalization.CultureInfo Invariant = System.Globalization.CultureInfo.InvariantCulture;

        private class TermEdit
        {
            public bool Structured;
            public int ParamIndex;
            public BoolTermOp Op;
            public string Threshold;
            public bool ThresholdValid;
        }

        /// <summary>Breaks a draft term string into display parts; falls back to raw text when it can't be read.</summary>
        private static TermEdit ParseTermForEdit(string term, string[] names, Dictionary<string, AnimatorControllerParameterType> types)
        {
            if (names == null || names.Length == 0)
                return new TermEdit { Structured = false };

            var trimmed = term == null ? string.Empty : term.Trim();
            bool isEmpty = trimmed.Length == 0;

            if (!isEmpty && BoolExpression.TryParse(trimmed, names, types, out var expr, out _)
                && expr.TryGetSingleTerm(out var t))
            {
                var idx = Array.IndexOf(names, t.Parameter);
                if (idx >= 0)
                    return new TermEdit { Structured = true, ParamIndex = idx, Op = t.Op, Threshold = BoolTerm.FormatThreshold(t.Threshold), ThresholdValid = true };
            }

            // Longest-prefix parameter match: the typed text starting with a full
            // parameter name (e.g. "Pool Spawn > 0.5") wins; otherwise a unique
            // start-of-name prefix ("Pool " -> "Pool Spawn") is auto-completed so
            // partially typed conditions keep their structured display.
            var not = !isEmpty && trimmed.StartsWith("!");
            var scan = not ? trimmed[1..].Trim() : trimmed;
            string best = null;
            string completion = null;
            for (var i = 0; i < names.Length; i++)
            {
                if (names[i].Length == 0)
                    continue;
                if (scan.StartsWith(names[i], StringComparison.Ordinal))
                {
                    if (best == null || names[i].Length > best.Length)
                        best = names[i];
                }
                else if (names[i].StartsWith(scan, StringComparison.Ordinal))
                    completion = completion == null ? names[i] : null; // null = ambiguous
            }
            var paramToUse = best ?? completion;
            if (paramToUse == null)
                return new TermEdit { Structured = false };
            var paramLength = best != null ? best.Length : scan.Length;

            var result = new TermEdit { Structured = true, ParamIndex = Array.IndexOf(names, paramToUse) };
            var rest = scan[paramLength..].Trim();
            if (rest.Length == 0)
            {
                result.Op = not ? BoolTermOp.IfNot : BoolTermOp.If;
                result.Threshold = BoolTerm.FormatThreshold(0f);
                result.ThresholdValid = true;
            }
            else if (!not)
            {
                string token;
                // Two-char operators must be tested before their single-char prefixes.
                if (rest.StartsWith("!=")) { token = "!="; result.Op = BoolTermOp.NotEquals; }
                else if (rest.StartsWith(">=")) { token = ">="; result.Op = BoolTermOp.GreaterEqual; }
                else if (rest.StartsWith(">")) { token = ">"; result.Op = BoolTermOp.Greater; }
                else if (rest.StartsWith("<=")) { token = "<="; result.Op = BoolTermOp.LessEqual; }
                else if (rest.StartsWith("<")) { token = "<"; result.Op = BoolTermOp.Less; }
                else if (rest.StartsWith("=")) { token = "="; result.Op = BoolTermOp.Equals; }
                else return new TermEdit { Structured = false };
                result.Threshold = rest[token.Length..].Trim();
                result.ThresholdValid = TryParseThreshold(result.Threshold, out _);
            }
            else
                return new TermEdit { Structured = false };
            return result;
        }

        private static string BuildTermDraft(string param, BoolTermOp op, string threshold)
        {
            switch (op)
            {
                case BoolTermOp.If: return param;
                case BoolTermOp.IfNot: return "!" + param;
                case BoolTermOp.Equals: return param + " = " + threshold;
                case BoolTermOp.NotEquals: return param + " != " + threshold;
                case BoolTermOp.GreaterEqual: return param + " >= " + threshold;
                case BoolTermOp.Greater: return param + " > " + threshold;
                case BoolTermOp.LessEqual: return param + " <= " + threshold;
                case BoolTermOp.Less: return param + " < " + threshold;
                default: return string.Empty;
            }
        }

        private static bool TryParseThreshold(string s, out float value)
            => float.TryParse(s, System.Globalization.NumberStyles.Float, Invariant, out value);

        private static bool IsBoolOrTrigger(string name, Dictionary<string, AnimatorControllerParameterType> types)
        {
            return name != null && types.TryGetValue(name, out AnimatorControllerParameterType t)
                && (t == AnimatorControllerParameterType.Bool || t == AnimatorControllerParameterType.Trigger);
        }

        /// <summary>
        /// Produces a default valid condition draft for a freshly added (empty) term,
        /// using the first available parameter and a valid default op ("true" for bools,
        /// "Equals 0" for int/float/trigger).
        /// </summary>
        private static string BuildNewTermDraft(string[] names, Dictionary<string, AnimatorControllerParameterType> types)
        {
            if (names == null || names.Length == 0)
                return string.Empty;

            for (var i = 0; i < names.Length; i++)
            {
                if (string.IsNullOrEmpty(names[i]))
                    continue;

                var isBool = IsBoolOrTrigger(names[i], types);
                return BuildTermDraft(names[i], isBool ? BoolTermOp.If : BoolTermOp.Equals, isBool ? string.Empty : BoolTerm.FormatThreshold(0f));
            }
            return string.Empty;
        }

        /// <summary>Draws one structured term and returns the draft term string it produces.</summary>
        private static string DrawStructuredTerm(TermEdit edit, string[] names, Dictionary<string, AnimatorControllerParameterType> types)
        {
            var boolish = IsBoolOrTrigger(names[edit.ParamIndex], types);

            var newParamIndex = EditorGUILayout.Popup(edit.ParamIndex, names);
            var newParam = names[newParamIndex];
            var newBoolish = IsBoolOrTrigger(newParam, types);

            // The current op's label is carried over to the new parameter's option set if it appears there; otherwise the first option is shown.
            var currentLabel = boolish ? BoolOpLabels[GetOpIndex(edit.Op, boolish)] : CompareOpLabels[GetOpIndex(edit.Op, boolish)];
            var labels = newBoolish ? BoolOpLabels : CompareOpLabels;
            var idx = Array.IndexOf(labels, currentLabel);
            var newOpIndex = EditorGUILayout.Popup(idx >= 0 ? idx : 0, labels, GUILayout.Width(75f));
            var chosenOp = newBoolish ? (newOpIndex == 1 ? BoolTermOp.IfNot : BoolTermOp.If) : CompareOpValues[newOpIndex];

            // Kept at the same width for bool ops (disabled, greyed) so rows line up.
            if (newBoolish || chosenOp == BoolTermOp.If || chosenOp == BoolTermOp.IfNot)
            {
                using (new EditorGUI.DisabledScope(true))
                {
                    EditorGUILayout.TextField(string.Empty, GUILayout.Width(70f));
                }
            }
            else
            {
                var wasColor = GUI.contentColor;
                if (!edit.ThresholdValid)
                    GUI.contentColor = new Color(1f, 0.36f, 0.36f);
                var newThreshold = EditorGUILayout.TextField(edit.Threshold, GUILayout.Width(70f));
                GUI.contentColor = wasColor;
                return BuildTermDraft(newParam, chosenOp, newThreshold);
            }

            return BuildTermDraft(newParam, chosenOp, string.Empty);
        }

        private static int GetOpIndex(BoolTermOp op, bool boolish)
        {
            if (boolish)
                return op == BoolTermOp.IfNot ? 1 : 0;
            for (var i = 0; i < CompareOpValues.Length; i++)
            {
                if (CompareOpValues[i] == op)
                    return i;
            }
            return 0;
        }

        private static List<List<string>> DrawVisualConditions(List<List<string>> clauses, ParameterInfo parameterInfo)
        {
            clauses ??= new List<List<string>>();
            var newClauses = new List<List<string>>(clauses.Count);

            for (var i = 0; i < clauses.Count; i++)
            {
                var terms = clauses[i];
                var removeClause = false;
                var addTermRequested = false;
                var newTerms = new List<string>(terms.Count);

                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label(
                        i > 0 ? new GUIContent("OR", "Any of these groups can trigger the transition") : new GUIContent(string.Empty),
                        EditorStyles.miniLabel,
                        GUILayout.Width(22f));

                    using (new EditorGUILayout.VerticalScope("box"))
                    {
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
                                var indentRect = GUILayoutUtility.GetRect(GUIContent.none, EditorStyles.miniButton, GUILayout.Width(30f));
                                if (j == terms.Count - 1)
                                {
                                    if (GUI.Button(indentRect, new GUIContent("+", "Add condition"), EditorStyles.miniButton))
                                        addTermRequested = true;
                                }

                                var term = terms[j] ?? string.Empty;
                                if (j == terms.Count - 1 && string.IsNullOrEmpty(term))
                                    term = BuildNewTermDraft(parameterInfo?.Names, parameterInfo?.Types);
                                var edit = ParseTermForEdit(term, parameterInfo?.Names, parameterInfo?.Types);
                                if (!edit.Structured)
                                {
                                    EditorGUILayout.HelpBox(
                                        "Could not read this condition as \"parameter operator threshold\".\n"
                                        + (string.IsNullOrWhiteSpace(term)
                                            ? "Type a parameter name, e.g. \"MyToggle\" or \"MyFloat > 0.5\"."
                                            : "It is kept as-is in the expression; try re-typing it in raw text mode."),
                                        MessageType.None);
                                }
                                else
                                {
                                    term = DrawStructuredTerm(edit, parameterInfo.Names, parameterInfo.Types);
                                    GUILayout.FlexibleSpace();
                                }

                                if (GUILayout.Button(new GUIContent("\u2715", "Remove this condition"), GUILayout.Width(26f)))
                                    continue;
                                newTerms.Add(term);
                            }
                        }

                        if (addTermRequested)
                            newTerms.Add(BuildNewTermDraft(parameterInfo?.Names, parameterInfo?.Types));
                    }
                }

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

        private static List<List<string>> BuildVisualClauses(BoolExpression expr)
        {
            var result = new List<List<string>>();
            if (expr == null || expr.IsEmpty)
                return result;
            var clauses = expr.ToDisjunctiveClauses();
            for (var i = 0; i < clauses.Count; i++)
            {
                var row = new List<string>(clauses[i].Count);
                for (var j = 0; j < clauses[i].Count; j++)
                    row.Add(clauses[i][j].ToString());
                result.Add(row);
            }
            return result;
        }

        private static string BuildDraftText(List<List<string>> clauses)
        {
            if (clauses == null)
                return string.Empty;
            var lines = new List<string>(clauses.Count);
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
            return JoinClauseLines(lines);
        }

        private static string JoinClauseLines(IEnumerable<string> lines)
        {
            var kept = new List<string>();
            foreach (var line in lines)
            {
                if (line.Length > 0)
                    kept.Add(line);
            }
            return kept.Count > 0 ? string.Join("\n| ", kept) : string.Empty;
        }

        /// <summary>
        /// Rebuilds the structured draft from a parsed expression, keeping an old user
        /// string at the same position when it still parses to the same term. When the
        /// expression is empty (e.g. a term broke parsing while the user was typing), the
        /// old row structure is kept and any term that is missing from the parse result is
        /// shown as raw text, so a half-typed condition is never wiped from the fields.
        /// </summary>
        private static List<List<string>> SyncVisualClauses(List<List<string>> old, BoolExpression expr, IEnumerable<string> knownParameterNames, Dictionary<string, AnimatorControllerParameterType> parameterTypes)
        {
            if (expr == null || expr.IsEmpty)
            {
                if (old != null)
                    return old;
                return new List<List<string>>();
            }
            var clauses = expr.ToDisjunctiveClauses();
            var result = new List<List<string>>(clauses.Count);
            for (var i = 0; i < clauses.Count; i++)
            {
                var row = new List<string>(clauses[i].Count);
                for (var j = 0; j < clauses[i].Count; j++)
                {
                    var term = clauses[i][j];
                    var text = term.ToString();
                    if (old != null && i < old.Count && j < old[i].Count
                        && BoolExpression.TryParse(old[i][j], knownParameterNames, parameterTypes, out var single, out _)
                        && single.TryGetSingleTerm(out var oldTerm)
                        && oldTerm.Equals(term))
                        text = old[i][j];
                    row.Add(text);
                }
                result.Add(row);
            }
            return result;
        }

        /// <summary>
        /// Formats the expression in tree form - parens only where precedence needs them
        /// (e.g. "a & (b | c)"). Newlines are whitespace and "&"/"|" accept the "&&"/"||"
        /// spelling, so the result always round-trips through the parser.
        /// </summary>
        private static string FormatConditionText(BoolExpression expr)
            => expr == null ? string.Empty : expr.ToDisplayString();

        private static bool TryGetSourceLocation(AnimatorStateTransition transition, out AnimatorState sourceState, out AnimatorStateMachine sourceMachine)
        {
            sourceState = null;
            sourceMachine = null;
            var path = AssetDatabase.GetAssetPath(transition);
            if (string.IsNullOrEmpty(path))
                return false;
            var controller = AssetDatabase.LoadMainAssetAtPath(path) as AnimatorController;
            if (controller == null)
                return false;

            return FindSourceLocationInStateMachine(controller, transition, out sourceState, out sourceMachine);
        }

        private static bool FindSourceLocationInStateMachine(AnimatorController controller, AnimatorStateTransition transition, out AnimatorState sourceState, out AnimatorStateMachine sourceMachine)
        {
            sourceState = null;
            sourceMachine = null;
            var layers = controller.layers;
            for (var li = 0; li < layers.Length; li++)
            {
                if (FindSourceLocationInStateMachine(layers[li].stateMachine, transition, out sourceState, out sourceMachine))
                    return true;
            }
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
                if (selected == null)
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

            var roots = new List<BoolNode>();
            for (var i = 0; i < expressions.Count; i++)
                if (expressions[i].Root != null)
                    roots.Add(expressions[i].Root);

            var canonical = new BoolExpression();
            if (roots.Count == 1)
                canonical.Root = roots[0];
            else if (roots.Count > 1)
                canonical.Root = new BoolOr(roots);
            canonical.Normalize();
            return canonical;
        }

        private static BoolExpression ExpressionFromTransition(AnimatorStateTransition transition)
        {
            var expr = new BoolExpression();
            var conditions = transition.conditions;
            var terms = new List<BoolNode>(conditions.Length);
            for (var i = 0; i < conditions.Length; i++)
            {
                var c = conditions[i];
                terms.Add(new BoolTermLeaf(new BoolTerm(c.parameter, MapModeToOp(c.mode), c.threshold)));
            }
            if (terms.Count == 0)
                return expr; // no conditions: unconditional (null root)
            expr.Root = terms.Count == 1 ? terms[0] : new BoolAnd(terms);
            expr.Normalize();
            if (expr.IsContradiction)
                expr.Root = null; // impossible conditions: drop, like the old DNF normalizer did
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

        private static void ApplyExpression(KeyValuePair<TransitionGroupKey, List<AnimatorStateTransition>> group, BoolExpression parsed)
        {
            var key = group.Key;
            var transitions = group.Value;
            var clauses = parsed.ToDisjunctiveClauses();

            if (!key.HasSourceLocation)
                return;

            var controller = ResolveController(transitions[0]);
            var sourceState = key.SourceState;
            var anyMachine = key.AnySourceMachine;
            // Needed to project >= / <= onto Unity's > / < with the correct type-based
            // offset (Int: ±1, Float: one ulp) at store time.
            var parameterTypes = GetKnownParameterInfo(transitions)?.Types;

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
                    use = key.Destination != null
                        ? (sourceState != null
                            ? sourceState.AddTransition(key.Destination)
                            : anyMachine.AddAnyStateTransition(key.Destination))
                        : (sourceState != null
                            ? sourceState.AddTransition(key.DestinationMachine)
                            : anyMachine.AddAnyStateTransition(key.DestinationMachine));
                    if (use == null)
                        return;
                    // Clone settings from the first existing transition, then restore the destination
                    EditorUtility.CopySerialized(template, use);
                    use.isExit = false;
                    use.destinationState = key.Destination;
                    use.destinationStateMachine = key.DestinationMachine;
                    created.Add(use);
                }

                SetConditions(use, clauses[i], parameterTypes);
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

        private static void SetConditions(AnimatorStateTransition transition, List<BoolTerm> clause, Dictionary<string, AnimatorControllerParameterType> parameterTypes)
        {
            var conditions = new AnimatorCondition[clause.Count];
            for (var i = 0; i < clause.Count; i++)
            {
                var term = clause[i];
                // Project onto Unity's supported modes here: >= / <= become > / < with the
                // type-based offset (Int ±1, Float one ulp), exactly mirroring the
                // fingerprint so Apply never shows a spurious diff after a round trip.
                var type = parameterTypes != null && parameterTypes.TryGetValue(term.Parameter, out var t)
                    ? t
                    : AnimatorControllerParameterType.Float;
                var (mode, threshold) = BoolExpression.ProjectForStorage(term.Op, term.Threshold, type);
                conditions[i] = new AnimatorCondition
                {
                    parameter = term.Parameter,
                    mode = mode,
                    threshold = threshold
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

        private static void DrawEnumSetting<T>(string label, List<AnimatorStateTransition> transitions, Func<AnimatorStateTransition, T> get, Action<AnimatorStateTransition, T> set, bool enabled = true) where T : struct, Enum
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
                // The exit time value is irrelevant while exit time is disabled - only
                // compare it when at least one transition uses it.
                any = a.hasExitTime != b.hasExitTime
                    || a.hasExitTime && a.exitTime != b.exitTime
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

        private class ParameterInfo
        {
            public string[] Names;
            public Dictionary<string, AnimatorControllerParameterType> Types;
        }

        private static ParameterInfo GetKnownParameterInfo(List<AnimatorStateTransition> transitions)
        {
            var first = transitions.Find(t => t != null);
            if (first == null)
                return null;

            var controller = ResolveController(first);
            if (controller == null)
                return null;

            var parameters = controller.parameters;
            var names = new string[parameters.Length];
            var types = new Dictionary<string, AnimatorControllerParameterType>();
            for (var i = 0; i < parameters.Length; i++)
            {
                if (string.IsNullOrEmpty(parameters[i].name))
                    continue;
                names[i] = parameters[i].name;
                types[parameters[i].name] = parameters[i].type;
            }
            names = names.Where(n => !string.IsNullOrEmpty(n)).ToArray();
            return new ParameterInfo { Names = names, Types = types };
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