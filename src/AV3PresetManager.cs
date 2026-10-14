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
            public readonly string extraConditions; // null if none; "\n"-separated condition descriptions otherwise

            public PresetStateEntry(AnimatorState state, int presetId, bool isDefault, bool hasEntryTransition, string extraConditions)
            {
                this.state = state;
                this.presetId = presetId;
                this.isDefault = isDefault;
                this.hasEntryTransition = hasEntryTransition;
                this.extraConditions = extraConditions;
            }
        }

        /// <summary>One column of the preset states table: a parameter that the states' parameter drivers touch.</summary>
        private struct DriverColumn
        {
            public string name;
            public readonly Dictionary<int, List<DriverSetInfo>> perState; // state instance id -> cells

            public DriverColumn(string name)
            {
                this.name = name;
                this.perState = new Dictionary<int, List<DriverSetInfo>>();
            }
        }

        /// <summary>A single parameter driver set operation for one state within a column.</summary>
        private struct DriverSetInfo
        {
            public VRC.SDKBase.VRC_AvatarParameterDriver.ChangeType operation;
            public float value;
            public float valueMin;
            public float valueMax;
            public string source;
            public bool convertRange;
            public float sourceMin;
            public float sourceMax;
            public float destMin;
            public float destMax;
        }

        private class PresetInfo
        {
            public AnimatorStateMachine stateMachine;
            public string presetParameter;
            public readonly List<PresetStateEntry> states = new List<PresetStateEntry>();
            public List<DriverColumn> driverColumns;
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
            var extraConditionList = new Dictionary<int, List<string>>(); // destination instance id -> extra conditions
            foreach (var transition in entryTransitions)
            {
                var destination = transition.destinationState;
                if (destination == null)
                    continue;

                var conditions = transition.conditions;
                if (conditions == null)
                    continue;

                var destId = destination.GetInstanceID();
                List<string> extras = null;
                bool found = false;
                for (int c = 0; c < conditions.Length; c++)
                {
                    var cond = conditions[c];
                    if (cond.mode == AnimatorConditionMode.Equals && !string.IsNullOrEmpty(cond.parameter) && intParameters.Contains(cond.parameter))
                    {
                        if (!found)
                        {
                            var presetId = Mathf.RoundToInt(cond.threshold);
                            if (entryInfo.TryGetValue(destId, out var existing) && !string.Equals(existing.parameter, cond.parameter, StringComparison.Ordinal))
                            {
                                // The same state is targeted by entry transitions using different int parameters -> not a preset
                                return null;
                            }
                            entryInfo[destId] = (presetId, cond.parameter);
                            found = true;
                        }
                    }
                    else
                    {
                        (extras ??= new List<string>()).Add(FormatCondition(cond));
                    }
                }

                if (!found)
                    continue;

                if (extras != null)
                {
                    if (!extraConditionList.TryGetValue(destId, out var list))
                    {
                        list = new List<string>();
                        extraConditionList[destId] = list;
                    }
                    list.AddRange(extras);
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

            // Put the default state first, then sort preset states by their preset id
            var defaultState = stateMachine.defaultState;
            if (defaultState != null)
            {
                preset.states.Add(new PresetStateEntry(defaultState, int.MinValue, true, false, null));
            }

            foreach (var state in presetStates.OrderBy(s => entryInfo[s.GetInstanceID()].presetId))
            {
                extraConditionList.TryGetValue(state.GetInstanceID(), out var extras);
                preset.states.Add(new PresetStateEntry(
                    state,
                    entryInfo[state.GetInstanceID()].presetId,
                    false,
                    true,
                    extras != null ? string.Join("\n", extras) : null));
            }

            // Scan the states' VRCAvatarParameterDriver behaviours for the parameters they set (columns of the states table)
            var columns = new List<DriverColumn>();
            var columnByName = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var entry in preset.states)
            {
                var state = entry.state;
                var stateId = state.GetInstanceID();
                var behaviours = state.behaviours ?? Array.Empty<StateMachineBehaviour>();
                for (int b = 0; b < behaviours.Length; b++)
                {
                    if (!(behaviours[b] is VRCAvatarParameterDriver driver) || driver.parameters == null)
                        continue;

                    for (int p = 0; p < driver.parameters.Count; p++)
                    {
                        var prm = driver.parameters[p];
                        if (prm == null || string.IsNullOrEmpty(prm.name))
                            continue;

                        if (!columnByName.TryGetValue(prm.name, out var col))
                        {
                            col = columns.Count;
                            columnByName[prm.name] = col;
                            columns.Add(new DriverColumn(prm.name));
                        }

                        if (!columns[col].perState.TryGetValue(stateId, out var cells))
                        {
                            cells = new List<DriverSetInfo>();
                            columns[col].perState[stateId] = cells;
                        }

                        cells.Add(new DriverSetInfo
                        {
                            operation = prm.type,
                            value = prm.value,
                            valueMin = prm.valueMin,
                            valueMax = prm.valueMax,
                            source = prm.source,
                            convertRange = prm.convertRange,
                            sourceMin = prm.sourceMin,
                            sourceMax = prm.sourceMax,
                            destMin = prm.destMin,
                            destMax = prm.destMax,
                        });
                    }
                }
            }
            preset.driverColumns = columns;

            return preset;
        }

        /// <summary>Format an <see cref="AnimatorCondition"/> as a human readable string, e.g. 'Preset &gt; 1'. </summary>
        private static string FormatCondition(AnimatorCondition cond)
        {
            string op = cond.mode switch
            {
                AnimatorConditionMode.Greater => ">",
                AnimatorConditionMode.Less => "<",
                AnimatorConditionMode.Equals => "==",
                _ => "?",
            };
            return $"{cond.parameter} {op} {cond.threshold}";
        }

        /// <summary>
        /// Scan all state machines in the controller (layers and nested sub state machines) and
        /// return the path + analysis of every machine that is a preset state machine.
        /// </summary>
        private static List<(string path, AnimatorStateMachine stateMachine, PresetInfo info)> FindPresetStateMachines(AnimatorController controller)
        {
            var results = new List<(string path, AnimatorStateMachine stateMachine, PresetInfo info)>();
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

        private static readonly List<DriverColumn> EmptyDriverColumns = new List<DriverColumn>();

        // Fixed widths of the preset states table columns (kept in consts for easy tweaking)
        private const float ColumnWidth = 80f;
        private const float IdColumnWidth = 80f;
        private const float NameColumnWidth = 120f;
        private const float RowPadding = 2f; // vertical padding between rows
        private const float ColumnPadding = 4f; // horizontal padding between columns
        private static readonly float RowHeight = EditorGUIUtility.singleLineHeight + 2f;

        private VRCExpressionParameters.Parameter GetVRCExpressionParameterInfo(string parameterName)
        {
            if (lastFoundAvatarDescriptor == null
                || lastFoundAvatarDescriptor.expressionParameters == null
                || lastFoundAvatarDescriptor.expressionParameters.parameters == null
                || string.IsNullOrEmpty(parameterName))
                return null;

            foreach (var p in lastFoundAvatarDescriptor.expressionParameters.parameters)
            {
                if (p != null && string.Equals(p.name, parameterName, StringComparison.Ordinal))
                    return p;
            }
            return null;
        }

        private bool IsBoolParameter(string parameterName)
        {
            var info = GetVRCExpressionParameterInfo(parameterName);
            return info != null && info.valueType == VRCExpressionParameters.ValueType.Bool;
        }

        /// <summary>Draw the preset states as a table: preset id, state name, then one column per parameter the states' drivers set.</summary>
        private void DrawPresetStates(PresetInfo preset)
        {
            var columns = preset.driverColumns ?? EmptyDriverColumns;

            // One layout rect per row, sized for all fixed-width columns; the cells are then drawn at their calculated x & width inside it
            float rowWidth = ColumnGrid.InnerIndent + IdColumnWidth + ColumnPadding + NameColumnWidth + ColumnPadding + (ColumnWidth + ColumnPadding) * columns.Count;

            // header row
            var headerRect = GetTableRowRect(rowWidth);
            var headerCell = GetTableCellRect(headerRect);
            DrawLabel(new Rect(headerCell.x, headerCell.y, IdColumnWidth, headerCell.height), "id", EditorStyles.boldLabel);
            float headerX = headerCell.x + IdColumnWidth + ColumnPadding;
            DrawLabel(new Rect(headerX, headerCell.y, NameColumnWidth, headerCell.height), "State", EditorStyles.boldLabel);
            headerX += NameColumnWidth + ColumnPadding;
            foreach (var column in columns)
            {
                var vrcInfo = GetVRCExpressionParameterInfo(column.name);
                string headerTooltip = (vrcInfo != null ? vrcInfo.valueType.ToString() : "no VRC parameter")
                    + Environment.NewLine + "set by the state's parameter drivers";
                var headerContent = new GUIContent(column.name) { tooltip = headerTooltip };
                DrawLabel(new Rect(headerX, headerCell.y, ColumnWidth, headerCell.height), headerContent, EditorStyles.boldLabel);
                headerX += ColumnWidth + ColumnPadding;
            }

            foreach (var entry in preset.states)
            {
                var rowRect = GetTableRowRect(rowWidth);
                DrawPresetStateRow(entry, columns, rowRect);

                EditorGUIUtility.AddCursorRect(rowRect, MouseCursor.Link);
                if (Event.current.type == EventType.MouseDown && rowRect.Contains(Event.current.mousePosition) && Event.current.button == 0)
                {
                    Event.current.Use();
                    Selection.activeObject = entry.state;
                    EditorGUIUtility.PingObject(entry.state);
                }
            }
        }

        /// <summary>Get the layout rect for one row of the table (a single GetRect call for the whole row).</summary>
        private static Rect GetTableRowRect(float width)
        {
            return GUILayoutUtility.GetRect(0f, RowHeight + RowPadding, GUILayout.Width(width));
        }

        /// <summary>Shrink a row rect so cells don't fill the row padding (keep the height of the content, centered on the padding).</summary>
        private static Rect GetTableCellRect(Rect rowRect)
        {
            return new Rect(rowRect.x, rowRect.y + RowPadding * 0.5f, rowRect.width, rowRect.height - RowPadding);
        }

        /// <summary>Draw a non-layout label at an exact rect.</summary>
        private static void DrawLabel(Rect rect, string text, GUIStyle style = null)
        {
            GUI.Label(rect, text, style ?? GUI.skin.label);
        }

        /// <summary>Draw a non-layout label at an exact rect.</summary>
        private static void DrawLabel(Rect rect, GUIContent content, GUIStyle style = null)
        {
            GUI.Label(rect, content, style ?? GUI.skin.label);
        }

        /// <summary>Draw every cell of one table row at its calculated x & width inside the row rect.</summary>
        private void DrawPresetStateRow(PresetStateEntry entry, List<DriverColumn> columns, Rect rowRect)
        {
            var cellRect = GetTableCellRect(rowRect);
            string idText = entry.hasEntryTransition
                ? (entry.extraConditions != null ? $"id = {entry.presetId} *" : $"id = {entry.presetId}")
                : "default";
            var idContent = new GUIContent(idText, entry.extraConditions != null
                ? "Additional entry transition conditions:" + Environment.NewLine + entry.extraConditions
                : "");
            DrawLabel(new Rect(cellRect.x, cellRect.y, IdColumnWidth, cellRect.height), idContent);
            float x = cellRect.x + IdColumnWidth + ColumnPadding;
            DrawLabel(new Rect(x, cellRect.y, NameColumnWidth, cellRect.height), entry.state.name);
            x += NameColumnWidth + ColumnPadding;
            for (int i = 0; i < columns.Count; i++)
            {
                DrawDriverCell(entry.state, columns[i], new Rect(x, cellRect.y, ColumnWidth, cellRect.height));
                x += ColumnWidth + ColumnPadding;
            }
        }

        private void DrawDriverCell(AnimatorState state, DriverColumn column, Rect cellRect)
        {
            if (!column.perState.TryGetValue(state.GetInstanceID(), out var cells) || cells.Count == 0)
                return; // no drivers for this parameter on this state (also the default state's empty cells)

            bool isBool = IsBoolParameter(column.name);
            var lines = new List<string>();
            foreach (var cell in cells)
                lines.Add(FormatDriverCell(cell, isBool));

            string joined = string.Join(", ", lines);
            string cellTooltip = "display only" + Environment.NewLine + string.Join(Environment.NewLine, lines);

            // bool columns render as a checkbox (display only, not wired to anything)
            if (isBool)
            {
                var first = cells[0];
                bool checkedValue = first.operation == VRC.SDKBase.VRC_AvatarParameterDriver.ChangeType.Set && Mathf.Abs(first.value) > 0f;
                EditorGUI.Toggle(cellRect, new GUIContent("", cellTooltip), checkedValue);
                return;
            }

            // values render as a (disabled, display-only) input field
            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUI.TextField(cellRect, new GUIContent("", cellTooltip), joined);
            }
        }

        private static string FormatDriverCell(DriverSetInfo cell, bool isBool)
        {
            switch (cell.operation)
            {
                case VRC.SDKBase.VRC_AvatarParameterDriver.ChangeType.Set:
                    return isBool ? (Mathf.Abs(cell.value) > 0f ? "true" : "false") : FormatDriverValue(cell.value);
                case VRC.SDKBase.VRC_AvatarParameterDriver.ChangeType.Add:
                    return isBool ? "toggle" : $"+{FormatDriverValue(cell.value)}";
                case VRC.SDKBase.VRC_AvatarParameterDriver.ChangeType.Random:
                    return $"rand {FormatDriverValue(cell.valueMin)} .. {FormatDriverValue(cell.valueMax)}";
                case VRC.SDKBase.VRC_AvatarParameterDriver.ChangeType.Copy:
                    if (cell.convertRange)
                        return $"copy {cell.source} ({FormatDriverValue(cell.sourceMin)} .. {FormatDriverValue(cell.sourceMax)}) -> {FormatDriverValue(cell.destMin)} .. {FormatDriverValue(cell.destMax)}";
                    return $"copy {cell.source}";
                default:
                    return "?";
            }
        }

        private static string FormatDriverValue(float value)
        {
            return (float)Mathf.RoundToInt(value) == value ? Mathf.RoundToInt(value).ToString() : value.ToString("0.###");
        }

        private void OnGUI()
        {
            var av = GetCurrentOrLastAvatarDescriptor(ref lastFoundAvatarDescriptor);
            var controller = av == null ? null : GetLastBaseController(av);

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("Avatar", GUILayout.Width(46));
                using (new EditorGUI.DisabledScope(true))
                {
                    EditorGUILayout.ObjectField(av, typeof(VRCAvatarDescriptor), true);
                }
                GUILayout.Space(8);
                EditorGUILayout.LabelField("Controller", GUILayout.Width(66));
                using (new EditorGUI.DisabledScope(true))
                {
                    EditorGUILayout.ObjectField(controller, typeof(AnimatorController), true);
                }
            }

            if (av == null)
            {
                EditorGUILayout.HelpBox("No VRC Avatar Descriptor found.", MessageType.Warning);
                return;
            }

            if (controller == null)
            {
                EditorGUILayout.HelpBox("No Animator Controllers found in the base animation layers.", MessageType.Info);
                return;
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

                var (_, _, preset) = presets[selectedPresetIndex];

                EditorGUILayout.Space(8);
                using (new EditorGUILayout.VerticalScope("box"))
                {
                    EditorGUILayout.LabelField($"Preset States  (parameter: {preset.presetParameter}, {preset.states.Count} states)", EditorStyles.boldLabel);

                    DrawPresetStates(preset);
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
