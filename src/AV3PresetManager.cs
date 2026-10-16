#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
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

        // Not-yet-applied edits of the states' parameter driver set values (kept in memory until applied or discarded)
        private readonly List<PendingChange> pendingChanges = new List<PendingChange>();
        private int pendingChangesControllerId = -1; // controller the pending changes belong to (-1 if none), used to clear stale entries

        // a pending "add a whole new preset row" (a brand new state), triggered by the + in the table's bottom row
        private bool addRowPending = false;
        private string pendingAddRowName = "";
        private string pendingAddRowCopyFrom = ""; // if set, copy the driver values of the row with this state name into the new state
        private int pendingAddRowId = -1;

        // queued deletions (cell / column / row), applied on Apply like the value edits
        private readonly List<PendingDeletion> pendingDeletions = new List<PendingDeletion>();

        // name typed into the trailing "add param" column's input (header row); the + buttons in its rows add a set operation for it
        private string newParamInput = "";

        // the preset currently displayed (set in OnGUI, used by Apply to target the right machine / preset parameter)
        private AnimatorController currentController;
        private AnimatorStateMachine currentStateMachine;
        private PresetInfo currentPreset;

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
            public StateMachineBehaviour driverBehaviour; // the VRCAvatarParameterDriver this operation lives in
            public int operationIndex; // index of the operation within that driver's own parameter list
        }

        /// <summary>An edit of a set-mode driver value, kept in memory until it gets applied to the driver (or discarded).</summary>
        private struct PendingChange
        {
            public AnimatorState state;
            public string parameterName;
            public StateMachineBehaviour driverBehaviour; // the VRCAvatarParameterDriver the set operation is in (null if a new cell is pending)
            public int operationIndex; // index of the set operation within that driver's own parameter list (-1 if a new cell)
            public float originalValue;
            public float newValue;
        }

        /// <summary>
        /// A queued deletion: a single driver operation (a row + column combination),
        /// a whole parameter column (the parameter from every state's drivers),
        /// or a whole row (the state itself + its entry transition + expression menu entry).
        /// Nothing is mutated until ApplyPendingChanges runs (Discard just throws the queue away).
        /// </summary>
        private struct PendingDeletion
        {
            public int kind; // 0: one operation of a state's driver, 1: a whole parameter column, 2: a whole row (state)
            public AnimatorState state; // the owning state (kind 0 & 2) - the state may already be destroyed for a queued row deletion
            public StateMachineBehaviour driverBehaviour; // the VRCAvatarParameterDriver (kind 0)
            public int operationIndex; // index within the driver's own parameter list (kind 0)
            public string parameterName; // the parameter (kind 0 & 1), also the row's state name for kind 2
            public VRC.SDKBase.VRC_AvatarParameterDriver.ChangeType operation; // the operation (kind 0)
            public int presetId; // the row's preset id (kind 2)
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
                            // identity of this operation within its own driver (stable across edits, unlike an index across all of the state's cells)
                            driverBehaviour = behaviours[b],
                            operationIndex = p,
                        });
                    }
                }
            }

            // the preset parameter's column comes first (e.g. before Belt/Skirt/... that the states also set)
            if (!string.IsNullOrEmpty(preset.presetParameter))
            {
                for (int c = columns.Count - 1; c >= 0; c--)
                {
                    if (string.Equals(columns[c].name, preset.presetParameter, StringComparison.Ordinal))
                    {
                        var presetColumn = columns[c];
                        columns.RemoveAt(c);
                        columns.Insert(0, presetColumn);
                        break;
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

        // Widths of the preset states table columns (kept in consts for easy tweaking)
        private const float ColumnWidthMin = 40f; // min width of a driver parameter column
        private const float ColumnWidthMax = 90f; // max width of a driver parameter column
        private const float ColumnTextPadding = 6f; // inner horizontal padding inside a driver parameter column
        private const float IdColumnWidth = 70f;
        private const float NameColumnWidth = 120f;
        private const float NewParamColumnWidth = 90f; // width of the trailing "add param" column
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
            var columnWidths = BuildDriverColumnWidths(columns);

            // One layout rect per row, sized for all columns; the cells are then drawn at their calculated x & width inside it
            float rowWidth = ColumnGrid.InnerIndent + IdColumnWidth + ColumnPadding + NameColumnWidth + ColumnPadding;
            for (int i = 0; i < columnWidths.Count; i++)
                rowWidth += columnWidths[i] + ColumnPadding;
            rowWidth += NewParamColumnWidth + ColumnPadding;

            // header row
            var headerRect = GetTableRowRect(rowWidth);
            var headerCell = GetTableCellRect(headerRect);
            GUI.Label(new Rect(headerCell.x, headerCell.y, IdColumnWidth, headerCell.height), "id", EditorStyles.boldLabel);
            float headerX = headerCell.x + IdColumnWidth + ColumnPadding;
            int headerXIndex = 0;
            GUI.Label(new Rect(headerX, headerCell.y, NameColumnWidth, headerCell.height), "State", EditorStyles.boldLabel);
            headerX += NameColumnWidth + ColumnPadding;
            foreach (var column in columns)
            {
                var vrcInfo = GetVRCExpressionParameterInfo(column.name);
                string headerTooltip = column.name
                    + Environment.NewLine + (vrcInfo != null ? vrcInfo.valueType.ToString() : "no VRC parameter");
                var headerContent = new GUIContent(GetParameterDisplayName(column.name)) { tooltip = headerTooltip };
                var columnHeaderRect = new Rect(headerX, headerCell.y, columnWidths[headerXIndex], headerCell.height);
                GUI.Label(columnHeaderRect, headerContent, EditorStyles.boldLabel);

                // right-clicking the parameter name offers removing the whole column
                if (Event.current.type == EventType.ContextClick && columnHeaderRect.Contains(Event.current.mousePosition))
                {
                    Event.current.Use();
                    ShowRemoveColumnMenu(preset, column);
                }

                headerX += columnWidths[headerXIndex] + ColumnPadding;
                headerXIndex++;
            }

            // trailing "add param" column: the header (name) row holds the parameter name input, the rows hold + buttons
            var newParamRect = new Rect(headerX, headerCell.y, NewParamColumnWidth, headerCell.height);
            var newParam = EditorGUI.TextField(newParamRect, new GUIContent("", GetNewParamInputTooltip()), newParamInput);
            if (newParam != newParamInput)
                newParamInput = newParam;

            foreach (var entry in preset.states)
            {
                var rowRect = GetTableRowRect(rowWidth);
                DrawPresetStateRow(entry, preset, columnWidths, rowRect);
            }

            // a bottom row to add a whole new preset state (id +, name input; parameter columns stay empty)
            DrawAddPresetRow(preset, rowWidth);
        }

        /// <summary>
        /// Draw the table's bottom "add row": a + button (disabled until a name is entered), the new state's name
        /// input, and a "copy from..." dropdown to its right. The new state's driver set-operations are copied
        /// from the row of the selected state. The parameter columns stay empty.
        /// </summary>
        private void DrawAddPresetRow(PresetInfo preset, float rowWidth)
        {
            var rowRect = GetTableRowRect(rowWidth);
            var cellRect = GetTableCellRect(rowRect);

            // name input + a 'copy from' dropdown to its right (width: whatever of the row is left, capped at NameColumnWidth)
            var nameRect = new Rect(cellRect.x + IdColumnWidth + ColumnPadding, cellRect.y, NameColumnWidth, cellRect.height);
            var idRect = new Rect(cellRect.x, cellRect.y, IdColumnWidth, cellRect.height);
            var copyMaxWidth = rowRect.width - ColumnGrid.InnerIndent - (nameRect.xMax - rowRect.x) - ColumnPadding;
            var copyRect = new Rect(nameRect.xMax + ColumnPadding, cellRect.y, Mathf.Min(NameColumnWidth, Mathf.Max(0f, copyMaxWidth)), cellRect.height);

            int nextId = ComputeNextPresetId(preset);
            bool hasName = !string.IsNullOrEmpty(pendingAddRowName);
            var idLabel = addRowPending ? $"id = {pendingAddRowId}" : "+";
            var idContent = new GUIContent(idLabel, $"Add a preset state with id {nextId}");

            using (new EditorGUI.DisabledScope(!hasName))
            {
                if (GUI.Button(idRect, idContent))
                {
                    pendingAddRowId = nextId;
                    addRowPending = true;
                }
            }

            var newName = EditorGUI.TextField(nameRect, new GUIContent("", "New preset state name"), pendingAddRowName ?? "");
            if (newName != pendingAddRowName)
            {
                pendingAddRowName = newName;
                if (string.IsNullOrEmpty(pendingAddRowName))
                    addRowPending = false; // clearing the name cancels the pending add
            }

            // 'copy from' dropdown: the button shows the selected state (or the 'copy from...' placeholder),
            // the menu lists the placeholder + every existing state; each item stores the exact state name (no index mapping)
            string copyShown = string.IsNullOrEmpty(pendingAddRowCopyFrom) ? "copy from..." : pendingAddRowCopyFrom;
            if (GUI.Button(copyRect, copyShown, EditorStyles.popup))
            {
                var copyMenu = new GenericMenu();
                copyMenu.AddItem(new GUIContent("copy from..."), string.IsNullOrEmpty(pendingAddRowCopyFrom), () => pendingAddRowCopyFrom = "");
                foreach (var stateEntry in preset.states)
                {
                    string stateName = stateEntry.state.name; // captured per item, the callbacks run after OnGUI finishes
                    copyMenu.AddItem(new GUIContent(stateName), string.Equals(pendingAddRowCopyFrom, stateName, StringComparison.Ordinal), () => pendingAddRowCopyFrom = stateName);
                }
                copyMenu.ShowAsContext();
            }

            // the parameter columns (including the trailing "add param" column) are left empty for a pending "add row"
        }

        private static int ComputeNextPresetId(PresetInfo presets)
        {
            var used = new HashSet<int>();
            foreach (var entry in presets.states)
            {
                if (entry.hasEntryTransition)
                    used.Add(entry.presetId);
            }

            int candidate = used.Count > 0 ? used.Min() : 1;
            if (candidate < 1)
                candidate = 1;
            while (used.Contains(candidate))
                candidate++;
            return candidate;
        }

        /// <summary>
        /// Create a new preset state (a new row in the machine): builds the AnimatorState, an entry transition
        /// pointing at it with an int == presetId condition, re-lays out every state of the machine based on the
        /// first state's position & the machine's existing spacing, and (while the preset menu has room) adds a
        /// matching menu entry.
        /// </summary>
        private void ApplyAddRow(VRCAvatarDescriptor avatar, AnimatorController controller, AnimatorStateMachine sm, PresetInfo presets, string stateName, string copyFromState, int presetId)
        {
            if (sm == null || controller == null || presets == null || string.IsNullOrEmpty(stateName))
                return;

            string presetParameter = presets.presetParameter;

            // read the machine's current child states to base the new layout on (first state's position + existing step)
            var children = sm.states ?? Array.Empty<ChildAnimatorState>();
            Vector3 firstPos = children.Length > 0 ? children[0].position : new Vector3(400f, 0f, 0f);
            float spacing = 70f; // the FXLayerVP Presets machine lays out its states 70px apart
            if (children.Length >= 2)
            {
                float step = (children[children.Length - 1].position.y - children[0].position.y) / (children.Length - 1);
                if (step > 0f)
                    spacing = step;
            }

            // the controller & state machine pre-exist - record them only (the controller's serialization covers its
            // child states/transitions/drivers, so the undo replay removes anything this action created).
            // Registering the controller as *created* would make Unity delete the whole asset on Undo.
            const string undoName = "Add Preset State";
            Undo.RecordObject(controller, undoName);
            Undo.RecordObject(sm, undoName);

            // the new state (appended at the end of the machine's state list), starting at the last row's spot
            var newState = sm.AddState(stateName, new Vector3(firstPos.x, firstPos.y + (children.Length - 1) * spacing, 0f));
            newState.writeDefaultValues = false;

            // an empty-motion state can't do anything - use the default state's motion (same as the existing Presets states)
            if (sm.defaultState != null && sm.defaultState.motion != null)
                newState.motion = sm.defaultState.motion;

            AnimatorTransition entry = sm.AddEntryTransition(newState);
            entry.AddCondition(AnimatorConditionMode.Equals, presetId, presetParameter);

            // unconditioned exit transition (matches the FXLayerVP Presets states: 0 duration, exit-time based)
            AnimatorStateTransition exit = newState.AddExitTransition();
            exit.duration = 0f;
            exit.exitTime = 0f;
            exit.hasExitTime = true;
            exit.canTransitionToSelf = false;

            // start the state with a driver that sets the preset parameter to 0 (like the existing Presets states do, e.g. FXLayerMedium)
            VRC.SDKBase.VRC_AvatarParameterDriver presetDriver = null;
            var stateBehaviours = newState.behaviours ?? Array.Empty<StateMachineBehaviour>();
            for (int i = 0; i < stateBehaviours.Length; i++)
            {
                if (stateBehaviours[i] is VRC.SDKBase.VRC_AvatarParameterDriver d)
                {
                    presetDriver = d;
                    break;
                }
            }

            if (presetDriver == null)
            {
                presetDriver = (VRC.SDKBase.VRC_AvatarParameterDriver)newState.AddStateMachineBehaviour(typeof(VRCAvatarParameterDriver));
                EditorUtility.SetDirty(presetDriver);
            }

            bool alreadySetsPreset = false;
            var driverParameters = presetDriver.parameters;
            if (driverParameters != null)
            {
                for (int i = 0; i < driverParameters.Count; i++)
                {
                    if (driverParameters[i] != null && string.Equals(driverParameters[i].name, presetParameter, StringComparison.Ordinal))
                    {
                        alreadySetsPreset = true;
                        break;
                    }
                }
            }

            if (!alreadySetsPreset)
            {
                driverParameters.Add(new VRC.SDKBase.VRC_AvatarParameterDriver.Parameter
                {
                    name = presetParameter,
                    type = VRC.SDKBase.VRC_AvatarParameterDriver.ChangeType.Set,
                    value = 0f,
                });
            }

            // if a copy-from state was given, copy set operations from its driver(s) into the new state's driver
            if (!string.IsNullOrEmpty(copyFromState))
            {
                PresetStateEntry sourceEntry = default;
                bool hasSource = false;
                for (int s = 0; s < presets.states.Count; s++)
                {
                    if (string.Equals(presets.states[s].state?.name, copyFromState, StringComparison.Ordinal))
                    {
                        sourceEntry = presets.states[s];
                        hasSource = true;
                        break;
                    }
                }

                if (hasSource && sourceEntry.state != null)
                {
                    var sourceBehaviours = sourceEntry.state.behaviours ?? Array.Empty<StateMachineBehaviour>();
                    foreach (var behaviour in sourceBehaviours)
                    {
                        if (!(behaviour is VRC.SDKBase.VRC_AvatarParameterDriver sourceDriver) || sourceDriver.parameters == null)
                            continue;

                        foreach (var sourceParam in sourceDriver.parameters)
                        {
                            if (sourceParam == null || string.IsNullOrEmpty(sourceParam.name))
                                continue;
                            if (sourceParam.type != VRC.SDKBase.VRC_AvatarParameterDriver.ChangeType.Set)
                                continue;
                            if (string.Equals(sourceParam.name, presetParameter, StringComparison.Ordinal))
                                continue; // the preset parameter stays 0 on the new state

                            presetDriver.parameters.Add(new VRC.SDKBase.VRC_AvatarParameterDriver.Parameter
                            {
                                name = sourceParam.name,
                                type = sourceParam.type,
                                value = sourceParam.value,
                                valueMin = sourceParam.valueMin,
                                valueMax = sourceParam.valueMax,
                                source = sourceParam.source,
                                convertRange = sourceParam.convertRange,
                                sourceMin = sourceParam.sourceMin,
                                sourceMax = sourceParam.sourceMax,
                                destMin = sourceParam.destMin,
                                destMax = sourceParam.destMax,
                            });
                        }
                    }

                    EditorUtility.SetDirty(presetDriver);
                }
            }

            // re-lay out every state based on the first state's position & the machine's spacing
            var laidOut = sm.states ?? Array.Empty<ChildAnimatorState>();
            var newChildren = new ChildAnimatorState[laidOut.Length];
            for (int i = 0; i < laidOut.Length; i++)
            {
                var child = laidOut[i];
                child.position = new Vector3(firstPos.x, firstPos.y + i * spacing, 0f);
                newChildren[i] = child;
            }
            sm.states = newChildren;

            EditorUtility.SetDirty(controller);
            EditorUtility.SetDirty(sm);

            // add a matching entry to the preset (sub-)menu while it has room (8 entries max)
            var presetMenuFound = FindPresetMenu(avatar?.expressionsMenu, presetParameter);
            bool menuEntryAdded = false;
            bool menuIsFull = false;
            if (presetMenuFound != null)
            {
                var menuControls = presetMenuFound.controls;
                if (menuControls.Count < 8)
                {
                    // copy the control type from an existing preset entry in this same menu
                    VRCExpressionsMenu.Control template = default;
                    bool hasTemplate = false;
                    foreach (var c in menuControls)
                    {
                        if (c.parameter != null && string.Equals(c.parameter.name, presetParameter, StringComparison.Ordinal))
                        {
                            template = c;
                            hasTemplate = true;
                            break;
                        }
                    }

                    var newControl = new VRCExpressionsMenu.Control
                    {
                        name = stateName,
                        type = hasTemplate ? template.type : VRCExpressionsMenu.Control.ControlType.Toggle,
                        parameter = new VRCExpressionsMenu.Control.Parameter { name = presetParameter },
                        value = presetId,
                    };

                    Undo.RecordObject(presetMenuFound, undoName);
                    menuControls.Add(newControl);
                    EditorUtility.SetDirty(presetMenuFound);
                    menuEntryAdded = true;
                }
                else
                {
                    menuIsFull = true;
                }
            }

            if (!menuEntryAdded)
            {
                string reason = menuIsFull
                    ? " The preset menu is already full (8 entries)."
                    : " No menu containing a control for the preset parameter was found.";
                EditorUtility.DisplayDialog("d4rk Preset Manager",
                    $"No menu entry was created for the new preset '{stateName}'.{reason}", "OK");
            }
        }

        /// <summary>Find the (sub-)menu whose controls set the given preset parameter (the int preset menu).</summary>
        private static VRCExpressionsMenu FindPresetMenu(VRCExpressionsMenu menu, string presetParameter)
        {
            if (menu == null)
                return null;

            var controls = menu.controls;
            bool hasPresetControl = false;
            foreach (var c in controls)
            {
                if (c.parameter != null && string.Equals(c.parameter.name, presetParameter, StringComparison.Ordinal))
                {
                    hasPresetControl = true;
                    break;
                }
            }
            if (hasPresetControl)
                return menu;

            foreach (var c in controls)
            {
                if (c.type == VRCExpressionsMenu.Control.ControlType.SubMenu && c.subMenu != null)
                {
                    var sub = FindPresetMenu(c.subMenu, presetParameter);
                    if (sub != null)
                        return sub;
                }
            }
            return null;
        }

        /// <summary>Right-click context menu on a table cell: one item per driver operation, each removes just that operation of this row + column combination.</summary>
        private void ShowRemoveCellMenu(AnimatorState state, DriverColumn column, List<DriverSetInfo> cells)
        {
            var menu = new GenericMenu();
            for (int i = 0; i < cells.Count; i++)
            {
                var cell = cells[i];
                string label = $"Delete '{column.name}' from '{state.name}'";
                menu.AddItem(new GUIContent(label,
                        $"Queues a deletion of just the '{column.name}' operation ({i + 1}/{cells.Count}) from '{state.name}' - press Apply in the pending changes box to confirm (Discard cancels)"),
                    false, () => RemoveDriverOperation(state, cell, column.name));
            }
            menu.ShowAsContext();
        }

        /// <summary>Queue a deletion of a single driver operation (row + column combination).</summary>
        private void RemoveDriverOperation(AnimatorState state, DriverSetInfo cell, string parameterName)
        {
            if (cell.driverBehaviour == null
                || !(cell.driverBehaviour is VRC.SDKBase.VRC_AvatarParameterDriver driver)
                || driver.parameters == null
                || cell.operationIndex < 0 || cell.operationIndex >= driver.parameters.Count)
                return;

            if (!string.Equals(driver.parameters[cell.operationIndex].name, parameterName, StringComparison.Ordinal))
                return; // the cell went stale since the table was scanned

            if (AlreadyPendingCellDeletion(state, cell.driverBehaviour, cell.operationIndex))
                return; // already queued for deletion

            // a queued deletion of an earlier operation would shift this pending value edit's stored operation index
            var driverParameters = driver.parameters;
            for (int i = 0; i < cell.operationIndex; i++)
            {
                if (HasPendingValueEdit(state, cell.driverBehaviour, i))
                {
                    EditorUtility.DisplayDialog("d4rk Preset Manager",
                        $"'" + state.name + "' has a pending value edit on '" + (driverParameters[i] != null ? driverParameters[i].name : "?") +
                        "'. Apply or discard it first, then delete '" + parameterName + "' from '" + state.name + "'.",
                        "OK");
                    return;
                }
            }

            AddPendingDeletion(new PendingDeletion
            {
                kind = 0,
                state = state,
                driverBehaviour = cell.driverBehaviour,
                operationIndex = cell.operationIndex,
                parameterName = parameterName, // already validated to match the driver's operation name above
            });
        }

        /// <summary>Right-click context menu on a header parameter name: removes that parameter's operations from every state (the whole column).</summary>
        private void ShowRemoveColumnMenu(PresetInfo preset, DriverColumn column)
        {
            var menu = new GenericMenu();
            menu.AddItem(new GUIContent($"Delete '{column.name}' from all states",
                    $"Queues a deletion of every '{column.name}' operation from the drivers of all {preset.states.Count} states - press Apply in the pending changes box to confirm (Discard cancels)"),
                false, () => RemoveColumnFromAllStates(preset, column));
            menu.ShowAsContext();
        }

        /// <summary>Queue a deletion of a whole parameter column (the parameter from every state's drivers).</summary>
        private void RemoveColumnFromAllStates(PresetInfo preset, DriverColumn column)
        {
            // queued deletions (cell first, then column) of the same parameter are dropped
            for (int i = pendingDeletions.Count - 1; i >= 0; i--)
            {
                var d = pendingDeletions[i];
                if (d.kind != 2 && string.Equals(d.parameterName, column.name, StringComparison.Ordinal))
                    pendingDeletions.RemoveAt(i);
            }

            // deleting an earlier operation in a driver would shift any pending value edit's stored operation index
            foreach (var entry in preset.states)
            {
                var state = entry.state;
                if (state == null)
                    continue;

                var behaviours = state.behaviours ?? Array.Empty<StateMachineBehaviour>();
                foreach (var behaviour in behaviours)
                {
                    if (!(behaviour is VRC.SDKBase.VRC_AvatarParameterDriver driver) || driver.parameters == null)
                        continue;

                    var parameters = driver.parameters;
                    for (int i = 0; i < parameters.Count; i++)
                    {
                        if (parameters[i] == null || !string.Equals(parameters[i].name, column.name, StringComparison.Ordinal))
                            continue;

                        for (int p = 0; p < i; p++)
                        {
                            if (HasPendingValueEdit(state, behaviour, p))
                            {
                                EditorUtility.DisplayDialog("d4rk Preset Manager",
                                    $"'" + state.name + "' has a pending value edit on '" + (parameters[p] != null ? parameters[p].name : "?") +
                                    "'. Apply or discard it first, then delete '" + column.name + "' from all states.",
                                    "OK");
                                return;
                            }
                        }
                        break; // a state's driver keeps at most one operation per parameter
                    }
                }
            }

            AddPendingDeletion(new PendingDeletion { kind = 1, parameterName = column.name });
        }

        /// <summary>Right-click context menu on a row's state name: deletes the whole row (the state itself) from the machine.</summary>
        private void ShowDeleteStateMenu(PresetStateEntry entry)
        {
            var menu = new GenericMenu();
            string warning = entry.isDefault
                ? " This state is the machine's default state - the remaining states take its place afterwards."
                : "";
            menu.AddItem(new GUIContent($"Delete state '{entry.state.name}'",
                    $"Queues a deletion of the state, its entry transition, its driver operations and its expression menu entry from the machine - press Apply in the pending changes box to confirm (Discard cancels).{warning}"),
                false, () => DeletePresetState(entry));
            menu.ShowAsContext();
        }

        /// <summary>Queue a deletion of a whole row (state + its entry transition + expression menu entry).</summary>
        private void DeletePresetState(PresetStateEntry entry)
        {
            var state = entry.state;
            if (state == null)
                return;

            if (AlreadyPendingRowDeletion(state))
                return; // already queued for deletion

            string stateName = state.name; // captured now, the state still exists

            // value edits and cell deletions of the deleted state go away with it
            for (int i = pendingChanges.Count - 1; i >= 0; i--)
            {
                if (pendingChanges[i].state == state)
                    pendingChanges.RemoveAt(i);
            }

            for (int i = pendingDeletions.Count - 1; i >= 0; i--)
            {
                var d = pendingDeletions[i];
                if (d.kind == 0 && d.state == state)
                    pendingDeletions.RemoveAt(i);
            }

            AddPendingDeletion(new PendingDeletion
            {
                kind = 2,
                state = state,
                parameterName = stateName,
                presetId = entry.isDefault ? int.MinValue : entry.presetId,
            });
        }

        /// <summary>Add a deletion to the pending queue (deduped).</summary>
        private void AddPendingDeletion(PendingDeletion deletion)
        {
            if (IsPendingDeletion(deletion.kind, deletion.state, deletion.driverBehaviour, deletion.operationIndex, deletion.parameterName))
                return;

            pendingDeletions.Add(deletion);
            Repaint();
        }

        /// <summary>True when an identical deletion is already in the queue.</summary>
        private bool IsPendingDeletion(int kind, AnimatorState state, StateMachineBehaviour driverBehaviour, int operationIndex, string parameterName)
        {
            foreach (var d in pendingDeletions)
            {
                if (d.kind != kind)
                    continue;
                if (kind == 0 && d.state == state && d.driverBehaviour == driverBehaviour && d.operationIndex == operationIndex)
                    return true;
                if (kind == 1 && string.Equals(d.parameterName, parameterName, StringComparison.Ordinal))
                    return true;
                if (kind == 2 && d.state == state)
                    return true;
            }
            return false;
        }

        /// <summary>True when the given single driver operation is already queued for deletion.</summary>
        private bool AlreadyPendingCellDeletion(AnimatorState state, StateMachineBehaviour driver, int operationIndex)
        {
            return IsPendingDeletion(0, state, driver, operationIndex, null);
        }

        /// <summary>True when a whole row (state) is already queued for deletion.</summary>
        private bool AlreadyPendingRowDeletion(AnimatorState state)
        {
            return IsPendingDeletion(2, state, null, -1, null);
        }

        /// <summary>True when the given cell (driver operation) has a pending value edit in the queue.</summary>
        private bool HasPendingValueEdit(AnimatorState state, StateMachineBehaviour driver, int operationIndex)
        {
            return pendingChanges.Exists(c => c.state == state && c.driverBehaviour == driver && c.operationIndex == operationIndex);
        }

        /// <summary>Remove the preset menu control that matches the deleted state (a control of the preset parameter with the state's name/id).</summary>
        private static void RemovePresetMenuEntry(VRCExpressionsMenu menu, string presetParameter, string stateName, int presetId)
        {
            var found = FindPresetMenu(menu, presetParameter);
            if (found == null)
                return;

            var controls = found.controls;
            for (int i = controls.Count - 1; i >= 0; i--)
            {
                var c = controls[i];
                if (c.parameter == null || !string.Equals(c.parameter.name, presetParameter, StringComparison.Ordinal))
                    continue;
                if (!string.Equals(c.name, stateName, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (presetId != int.MinValue && c.value != presetId)
                    continue;

                Undo.RecordObject(found, "Delete Preset State");
                controls.RemoveAt(i);
                EditorUtility.SetDirty(found);
            }
        }

        /// <summary>Re-position all of a machine's child states into a single column (first state's x, even spacing from their existing step).</summary>
        private static void ReLayoutPresetStates(AnimatorStateMachine sm)
        {
            var laidOut = sm.states ?? Array.Empty<ChildAnimatorState>();
            Vector3 firstPos = laidOut.Length > 0 ? laidOut[0].position : new Vector3(400f, 0f, 0f);
            float spacing = 70f;
            if (laidOut.Length >= 2)
            {
                float step = (laidOut[laidOut.Length - 1].position.y - laidOut[0].position.y) / (laidOut.Length - 1);
                if (step > 0f)
                    spacing = step;
            }

            var newChildren = new ChildAnimatorState[laidOut.Length];
            for (int i = 0; i < laidOut.Length; i++)
            {
                var child = laidOut[i];
                child.position = new Vector3(firstPos.x, firstPos.y + i * spacing, 0f);
                newChildren[i] = child;
            }
            sm.states = newChildren;
        }

        /// <summary>Compute the width of each driver parameter column based on the length of its display name, clamped to &lt;see cref="ColumnWidthMin"&gt;/&lt;see cref="ColumnWidthMax"&gt;.</summary>
        private static List<float> BuildDriverColumnWidths(List<DriverColumn> columns)
        {
            var widths = new List<float>(columns.Count);
            foreach (var column in columns)
            {
                float textWidth = EditorStyles.boldLabel.CalcSize(new GUIContent(GetParameterDisplayName(column.name))).x;
                widths.Add(Mathf.Clamp(textWidth + ColumnTextPadding, ColumnWidthMin, ColumnWidthMax));
            }
            return widths;
        }

        /// <summary>Get the layout rect for one row of the table (a single GetRect call for the whole row).</summary>
        private static Rect GetTableRowRect(float width)
        {
            return GUILayoutUtility.GetRect(0f, RowHeight + RowPadding, GUILayout.Width(width));
        }

        /// <summary>Shrink a row rect so cells don't fill the row padding (keep the height of the content, centered on the padding) and don't start at x=0 (offset by the indent).</summary>
        private static Rect GetTableCellRect(Rect rowRect)
        {
            return new Rect(rowRect.x + ColumnGrid.InnerIndent, rowRect.y + RowPadding * 0.5f, rowRect.width - ColumnGrid.InnerIndent, rowRect.height - RowPadding);
        }

        /// <summary>
        /// Get a table header's display name for a driver parameter:
        /// strips a leading "Toggle" prefix and trailing "Int"/"Float"/"Left"/"Right" suffixes,
        /// then splits camel humps with spaces ("FirstSecond" -> "First Second", "VRCParameter" -> "VRC Parameter").
        /// </summary>
        private static string GetParameterDisplayName(string parameterName)
        {
            string name = parameterName;
            if (name.StartsWith("Toggle", System.StringComparison.Ordinal))
                name = name.Substring("Toggle".Length);
            if (name.EndsWith("Int", System.StringComparison.Ordinal) && name.Length > "Int".Length)
                name = name.Substring(0, name.Length - "Int".Length);
            else if (name.EndsWith("Float", System.StringComparison.Ordinal) && name.Length > "Float".Length)
                name = name.Substring(0, name.Length - "Float".Length);
            else if (name.EndsWith("Left", System.StringComparison.Ordinal) && name.Length > "Left".Length)
                name = name.Substring(0, name.Length - "Left".Length) + "L";
            else if (name.EndsWith("Right", System.StringComparison.Ordinal) && name.Length > "Right".Length)
                name = name.Substring(0, name.Length - "Right".Length) + "R";

            // split camel humps into words: an uppercase letter starts a new word after a lowercase letter,
            // and runs of capitals end when the next letter is lowercase (so "VRCParameter" -> "VRC Parameter")
            var words = new List<string>();
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                if (i > 0 && char.IsUpper(c) && ((char.IsLower(name[i - 1]) && !char.IsLower(c)) || (i + 1 < name.Length && char.IsLower(name[i + 1]) && char.IsUpper(name[i - 1]))))
                    words.Add(string.Concat(c));
                else if (i > 0 && words.Count > 0)
                    words[words.Count - 1] = words[words.Count - 1] + c;
                else
                    words.Add(string.Concat(c));
            }
            return string.Join(" ", words);
        }

        /// <summary>Draw every cell of one table row at its calculated x & width inside the row rect.</summary>
        private void DrawPresetStateRow(PresetStateEntry entry, PresetInfo preset, List<float> columnWidths, Rect rowRect)
        {
            var cellRect = GetTableCellRect(rowRect);
            string idText = entry.hasEntryTransition
                ? (entry.extraConditions != null ? $"id = {entry.presetId} *" : $"id = {entry.presetId}")
                : "default";
            var idContent = new GUIContent(idText, entry.extraConditions != null
                ? "Additional entry transition conditions:" + Environment.NewLine + entry.extraConditions
                : "");
            GUI.Label(new Rect(cellRect.x, cellRect.y, IdColumnWidth, cellRect.height), idContent);
            float x = cellRect.x + IdColumnWidth + ColumnPadding;
            var nameRect = new Rect(x, cellRect.y, NameColumnWidth, cellRect.height);
            GUI.Label(nameRect, entry.state.name);

            // clicking the state name selects & pings the state
            EditorGUIUtility.AddCursorRect(nameRect, MouseCursor.Link);
            if (Event.current.type == EventType.MouseDown && nameRect.Contains(Event.current.mousePosition) && Event.current.button == 0)
            {
                Event.current.Use();
                Selection.activeObject = entry.state;
                EditorGUIUtility.PingObject(entry.state);
            }

            // right-clicking the state name offers deleting the whole row (state)
            if (Event.current.type == EventType.ContextClick && nameRect.Contains(Event.current.mousePosition))
            {
                Event.current.Use();
                ShowDeleteStateMenu(entry);
            }

            x += NameColumnWidth + ColumnPadding;
            for (int i = 0; i < columnWidths.Count; i++)
            {
                // shrink the field/checkbox rect so its box doesn't eat into the column's inner text padding
                var fieldRect = new Rect(x, cellRect.y, Mathf.Max(columnWidths[i] - ColumnTextPadding, 10f), cellRect.height);
                DrawDriverCell(entry.state, preset.driverColumns[i], fieldRect);
                x += columnWidths[i] + ColumnPadding;
            }

            // trailing "add param" column: + adds a pending set operation for the header input's parameter on this state.
            // the input resolves to a controller parameter by exact name, or when it fuzzy-matches exactly one parameter.
            string resolvedParam = ResolveNewParam(newParamInput);
            using (new EditorGUI.DisabledScope(resolvedParam == null))
            {
                var newParamCellRect = new Rect(x, cellRect.y, Mathf.Max(NewParamColumnWidth - ColumnTextPadding, 10f), cellRect.height);
                var newParamContent = new GUIContent("+", resolvedParam == null
                    ? GetFuzzyParamTooltip(newParamInput)
                    : $"Add a set operation for '{resolvedParam}' on '{entry.state.name}'");
                if (GUI.Button(newParamCellRect, newParamContent))
                    SetPendingValue(default(DriverSetInfo), resolvedParam, entry.state, 0f);
            }
        }

        /// <summary>Resolve the "add param" input to a controller parameter: an exact name match, or a unique fuzzy match. Null if ambiguous.</summary>
        private string ResolveNewParam(string input)
        {
            if (string.IsNullOrEmpty(input) || currentController == null)
                return null;
            if (IsControllerParameter(input))
                return input;

            var matches = FuzzyParamMatches(input);
            return matches.Count == 1 ? matches[0] : null;
        }

        /// <summary>True if the current controller declares a parameter with this exact name.</summary>
        private bool IsControllerParameter(string parameterName)
        {
            if (string.IsNullOrEmpty(parameterName) || currentController == null)
                return false;

            var parameters = currentController.parameters ?? Array.Empty<AnimatorControllerParameter>();
            for (int i = 0; i < parameters.Length; i++)
            {
                if (string.Equals(parameters[i].name, parameterName, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        /// <summary>Tooltip for the trailing "add param" input: a hint while empty, fuzzy controller parameter matches otherwise.</summary>
        private string GetNewParamInputTooltip()
        {
            if (string.IsNullOrEmpty(newParamInput))
                return "Name of a controller parameter to add as a new set operation" + Environment.NewLine
                    + "Use the + buttons in the rows below";
            return GetFuzzyParamTooltip(newParamInput);
        }

        /// <summary>All controller parameters that fuzzy-match the input (case-insensitive subsequence).</summary>
        private List<string> FuzzyParamMatches(string input)
        {
            var matches = new List<string>();
            if (currentController == null)
                return matches;

            var parameters = currentController.parameters ?? Array.Empty<AnimatorControllerParameter>();
            string needle = input.ToLowerInvariant();
            foreach (var p in parameters)
            {
                if (string.IsNullOrEmpty(p.name))
                    continue;
                if (FuzzyContains(p.name.ToLowerInvariant(), needle))
                    matches.Add(p.name);
            }
            return matches;
        }

        /// <summary>Tooltip listing the controller parameters that fuzzy-match the input, for when the name doesn't resolve exactly.</summary>
        private string GetFuzzyParamTooltip(string input)
        {
            if (string.IsNullOrEmpty(input))
                return GetNewParamInputTooltip();
            if (IsControllerParameter(input))
                return $"'{input}' — press the + buttons to add it to the states";
            if (currentController == null)
                return $"no parameter named '{input}' (no controller selected)";

            var matches = FuzzyParamMatches(input);
            if (matches.Count == 1)
                return $"no parameter named '{input}', but it uniquely matches '{matches[0]}' — the + buttons will add that";
            if (matches.Count == 0)
                return $"no parameter named '{input}' and no fuzzy matches";
            if (matches.Count > 30)
            {
                var shown = matches.Take(30).ToList();
                return $"no parameter named '{input}'. {matches.Count} fuzzy matches:" + Environment.NewLine
                    + string.Join(Environment.NewLine, shown) + Environment.NewLine + $"… and {matches.Count - 30} more";
            }
            return $"no parameter named '{input}'. {matches.Count} fuzzy matches:" + Environment.NewLine + string.Join(Environment.NewLine, matches);
        }

        /// <summary>True if every character of needle appears in haystack in order (both already lowercased).</summary>
        private static bool FuzzyContains(string haystack, string needle)
        {
            int h = 0;
            for (int n = 0; n < needle.Length; n++)
            {
                int idx = haystack.IndexOf(needle[n], h);
                if (idx < 0)
                    return false;
                h = idx + 1;
            }
            return true;
        }

        private void DrawDriverCell(AnimatorState state, DriverColumn column, Rect cellRect)
        {
            bool isBool = IsBoolParameter(column.name);
            bool hasCells = column.perState.TryGetValue(state.GetInstanceID(), out var cells) && cells.Count > 0;

            if (!hasCells)
            {
                // no driver for this parameter on this state (also all of the default state's cells):
                // a pending "add" shows an editable cell, otherwise a "+" button starts a new set operation
                var newCell = default(DriverSetInfo); // null driverBehaviour marks a pending "add"
                if (HasPendingChange(newCell, column.name, state))
                {
                    float addedValue = GetPendingValue(newCell, column.name, state);
                    var addedLine = FormatDriverCell(new DriverSetInfo
                    {
                        operation = VRC.SDKBase.VRC_AvatarParameterDriver.ChangeType.Set,
                        value = addedValue,
                    }, isBool);
                    string tooltip = "New set operation (pending)" + Environment.NewLine + addedLine;
                    DrawSetCell(cellRect, isBool, addedValue, tooltip, newCell, column.name, state);
                    return;
                }

                var addTooltip = $"Add a set operation for '{column.name}' on '{state.name}'";
                var addContent = new GUIContent("+", addTooltip);
                if (GUI.Button(cellRect, addContent))
                    SetPendingValue(newCell, column.name, state, 0f);
                return;
            }

            // right-clicking the cell offers removing its driver operations (one menu item each)
            if (Event.current.type == EventType.ContextClick && cellRect.Contains(Event.current.mousePosition))
            {
                Event.current.Use();
                ShowRemoveCellMenu(state, column, cells);
                return;
            }

            // set-mode cells are the only editable ones; other modes (add / random / copy) stay display only
            int editableCellIndex = -1;
            for (int i = 0; i < cells.Count; i++)
            {
                if (cells[i].operation == VRC.SDKBase.VRC_AvatarParameterDriver.ChangeType.Set)
                {
                    editableCellIndex = i;
                    break;
                }
            }
            bool editable = editableCellIndex >= 0;

            // build the display lines with the effective (pending-overridden) value applied
            var lines = new List<string>();
            bool hasChange = false;
            var editableCell = cells[editableCellIndex];
            bool hasPending = HasPendingChange(editableCell, column.name, state);
            for (int i = 0; i < cells.Count; i++)
            {
                var cell = cells[i];
                if (i == editableCellIndex && hasPending)
                {
                    cell.value = GetPendingValue(editableCell, column.name, state);
                    hasChange = true;
                }
                lines.Add(FormatDriverCell(cell, isBool));
            }

            if (!editable)
            {
                // no set-mode value: the whole cell is display only
                string joined = string.Join(", ", lines);
                string readOnlyTooltip = "Display only" + Environment.NewLine + string.Join(Environment.NewLine, lines);
                DrawReadOnlyCell(cellRect, joined, readOnlyTooltip);
                return;
            }

            // editable cell: the field/checkbox shows the effective value of the first set-mode driver
            float effectiveValue = hasPending
                ? GetPendingValue(editableCell, column.name, state)
                : editableCell.value;
            string cellTooltip = "Only the set-mode value is editable" + Environment.NewLine
                + (hasChange ? "value changed (pending)" + Environment.NewLine : "")
                + string.Join(Environment.NewLine, lines);

            DrawSetCell(cellRect, isBool, effectiveValue, cellTooltip, editableCell, column.name, state);
        }

        /// <summary>Draw a set-mode driver value as an editable checkbox (bool) or delayed text field (numeric); edits are recorded as pending changes.</summary>
        private void DrawSetCell(Rect cellRect, bool isBool, float effectiveValue, string tooltip, DriverSetInfo originalCell, string parameterName, AnimatorState state)
        {
            if (isBool)
            {
                bool newValue = EditorGUI.Toggle(cellRect, new GUIContent("", tooltip), effectiveValue != 0f);
                if (GUI.changed)
                    SetPendingValue(originalCell, parameterName, state, newValue ? 1f : 0f);
                return;
            }

            string displayed = FormatDriverValue(effectiveValue);
            string committed = EditorGUI.DelayedTextField(cellRect, displayed);
            if (GUI.changed)
            {
                if (float.TryParse(committed, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed))
                    SetPendingValue(originalCell, parameterName, state, parsed);
                else
                    GUI.FocusControl(null); // invalid input, cancel the edit so the field resets
            }
        }

        /// <summary>Draw a display-only (disabled) input field cell.</summary>
        private static void DrawReadOnlyCell(Rect cellRect, string text, string tooltip)
        {
            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUI.TextField(cellRect, new GUIContent("", tooltip), text);
            }
        }

        /// <summary>True if the given cell (or a matching pending "add") has a pending change; a cell with a null driverBehaviour identifies a pending add.</summary>
        private bool HasPendingChange(DriverSetInfo cell, string parameterName, AnimatorState state)
        {
            return pendingChanges.Exists(c => IsSameCell(c, cell, parameterName, state));
        }

        private float GetPendingValue(DriverSetInfo cell, string parameterName, AnimatorState state)
        {
            foreach (var change in pendingChanges)
            {
                if (IsSameCell(change, cell, parameterName, state))
                    return change.newValue;
            }
            return 0f; // unreachable: only called when HasPendingChange is true
        }

        private static bool IsSameCell(PendingChange change, DriverSetInfo cell, string parameterName, AnimatorState state)
        {
            // pending adds keep a null driverBehaviour, existing cells reference their driver
            return change.state == state
                && change.parameterName == parameterName
                && change.driverBehaviour == cell.driverBehaviour
                && (cell.driverBehaviour == null || change.operationIndex == cell.operationIndex);
        }

        /// <summary>Record (or clear, when the value matches the driver's original value) a pending edit of a set-mode driver value.</summary>
        private void SetPendingValue(DriverSetInfo cell, string parameterName, AnimatorState state, float value)
        {
            bool isNewCell = cell.driverBehaviour == null;
            bool found = false;
            for (int i = 0; i < pendingChanges.Count; i++)
            {
                var existing = pendingChanges[i];
                if (IsSameCell(existing, cell, parameterName, state))
                {
                    found = true;
                    if (value == existing.originalValue && !isNewCell)
                        pendingChanges.RemoveAt(i); // back to the original value -> nothing left to apply (new cells stay, the user still has to edit them)
                    else
                    {
                        existing.newValue = value;
                        pendingChanges[i] = existing; // the List indexer returns a copy, so write the struct back
                    }
                    break;
                }
            }

            if (!found && (isNewCell || value != cell.value))
            {
                pendingChanges.Add(new PendingChange
                {
                    state = state,
                    parameterName = parameterName,
                    driverBehaviour = cell.driverBehaviour,
                    operationIndex = isNewCell ? -1 : cell.operationIndex,
                    originalValue = cell.value,
                    newValue = value,
                });
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

        /// <summary>Format a pending change row (without the state name, the changes are grouped per state), e.g. 'param  0 -> 1' (bools shown as true/false). New cells are prefixed with 'add'.</summary>
        private string FormatPendingChangeDisplay(PendingChange change)
        {
            bool isBool = IsBoolParameter(change.parameterName);
            string Format(float v) => isBool ? (v != 0f ? "true" : "false") : FormatDriverValue(v);
            if (change.driverBehaviour == null)
                return $"add  {change.parameterName}  {Format(change.newValue)}";
            return $"{change.parameterName}  {Format(change.originalValue)} -> {Format(change.newValue)}";
        }

        /// <summary>Apply the queued deletions (single operations, whole columns, whole rows) to the machine &amp; its drivers, with undo support.</summary>
        private void ApplyPendingDeletions(AnimatorStateMachine sm, PresetInfo preset)
        {
            if (sm == null || currentController == null || preset == null)
                return;

            // bucket the queued deletions and collect the states doomed by queued row deletions
            var cellDeletions = new List<PendingDeletion>();
            var columnDeletions = new List<PendingDeletion>();
            var rowDeletions = new List<PendingDeletion>();
            var doomedStates = new List<AnimatorState>();
            foreach (var d in pendingDeletions)
            {
                switch (d.kind)
                {
                    case 0:
                        cellDeletions.Add(d);
                        break;
                    case 1:
                        columnDeletions.Add(d);
                        break;
                    case 2:
                        rowDeletions.Add(d);
                        if (d.state != null && !doomedStates.Contains(d.state))
                            doomedStates.Add(d.state);
                        break;
                }
            }

            const string undoName = "Apply Preset Deletions";

            // (1) remove the queued driver operations: whole columns from every surviving state's drivers,
            // plus the individually queued cells of that state's driver (resolved by parameter name, so index shifts don't matter)
            foreach (var entry in preset.states)
            {
                var state = entry.state;
                if (state == null || doomedStates.Contains(state))
                    continue;

                var behaviours = state.behaviours ?? Array.Empty<StateMachineBehaviour>();
                foreach (var behaviour in behaviours)
                {
                    if (!(behaviour is VRC.SDKBase.VRC_AvatarParameterDriver driver) || driver.parameters == null)
                        continue;

                    var toRemove = new List<string>();
                    foreach (var col in columnDeletions)
                    {
                        if (col.parameterName != null && !toRemove.Contains(col.parameterName))
                            toRemove.Add(col.parameterName);
                    }
                    foreach (var cell in cellDeletions)
                    {
                        if (cell.state != state || cell.driverBehaviour != behaviour || cell.parameterName == null)
                            continue;
                        if (!toRemove.Contains(cell.parameterName))
                            toRemove.Add(cell.parameterName);
                    }

                    if (toRemove.Count == 0)
                        continue;

                    Undo.RecordObject(driver, undoName);
                    var parameters = driver.parameters;
                    for (int i = parameters.Count - 1; i >= 0; i--)
                    {
                        if (parameters[i] != null && toRemove.Contains(parameters[i].name))
                            parameters.RemoveAt(i);
                    }
                    EditorUtility.SetDirty(driver);
                }
            }

            // (2) remove the doomed states: their entry transition + state node, fix the default state,
            // re-lay out the machine & drop the matching expression menu entries
            if (rowDeletions.Count > 0)
            {
                Undo.RecordObject(currentController, undoName);
                Undo.RecordObject(sm, undoName);

                foreach (var row in rowDeletions)
                {
                    var state = row.state;
                    if (state == null)
                        continue;

                    var entryTransitions = sm.entryTransitions ?? Array.Empty<AnimatorTransition>();
                    for (int i = 0; i < entryTransitions.Length; i++)
                    {
                        if (entryTransitions[i] != null && entryTransitions[i].destinationState == state)
                        {
                            sm.RemoveEntryTransition(entryTransitions[i]);
                            break;
                        }
                    }

                    sm.RemoveState(state);

                    if (sm.defaultState == state)
                    {
                        var remaining = sm.states ?? Array.Empty<ChildAnimatorState>();
                        AnimatorState newDefault = null;
                        for (int i = 0; i < remaining.Length; i++)
                        {
                            if (remaining[i].state != null)
                            {
                                newDefault = remaining[i].state;
                                break;
                            }
                        }
                        sm.defaultState = newDefault;
                    }
                }

                ReLayoutPresetStates(sm);

                string presetParameter = preset.presetParameter;
                var presetMenu = lastFoundAvatarDescriptor != null ? lastFoundAvatarDescriptor.expressionsMenu : null;
                foreach (var row in rowDeletions)
                {
                    if (row.state == null)
                        continue;
                    RemovePresetMenuEntry(presetMenu, presetParameter, row.parameterName, row.presetId);
                }

                EditorUtility.SetDirty(currentController);
                EditorUtility.SetDirty(sm);
            }
        }

        /// <summary>Format a queued deletion for the pending changes box.</summary>
        private string FormatPendingDeletionDisplay(PendingDeletion deletion)
        {
            switch (deletion.kind)
            {
                case 0:
                    return deletion.state != null
                        ? $"Delete '{deletion.parameterName}' from '{deletion.state.name}'"
                        : $"Delete '{deletion.parameterName}'";
                case 1:
                    return $"Delete '{deletion.parameterName}' from all states";
                default:
                    return deletion.state != null
                        ? $"Delete state '{deletion.state.name}'"
                        : $"Delete state '{deletion.parameterName}'";
            }
        }

        /// <summary>Write the pending changes back to the states' parameter drivers (with undo support), then clear them.</summary>
        private void ApplyPendingChanges()
        {
            // a pending "add row" is applied first (it needs the machine & preset, not a state's drivers)
            if (addRowPending && !string.IsNullOrEmpty(pendingAddRowName)
                && currentController != null && currentStateMachine != null && currentPreset != null)
            {
                ApplyAddRow(lastFoundAvatarDescriptor, currentController, currentStateMachine, currentPreset, pendingAddRowName, pendingAddRowCopyFrom, pendingAddRowId);
                addRowPending = false;
                pendingAddRowName = "";
                pendingAddRowCopyFrom = "";
            }

            // queued deletions mutate the machine & drivers - apply them first so the re-scanned table
            // reflects the final state (currentPreset is invalidated below when any deletion was applied)
            bool appliedDeletions = false;
            if (currentStateMachine != null && currentPreset != null && pendingDeletions.Count > 0)
            {
                ApplyPendingDeletions(currentStateMachine, currentPreset);
                pendingDeletions.Clear();
                appliedDeletions = true;
            }

            if (pendingChanges.Count == 0)
            {
                if (appliedDeletions)
                    currentPreset = null;
                Repaint();
                return;
            }

            // group the changes per state so each state's drivers are only walked once
            var statesWithChanges = new List<AnimatorState>();
            foreach (var change in pendingChanges)
            {
                if (!statesWithChanges.Contains(change.state))
                    statesWithChanges.Add(change.state);
            }

            foreach (var state in statesWithChanges)
            {
                var behaviours = state.behaviours ?? Array.Empty<StateMachineBehaviour>();
                // driver to append new-cell parameters to for this state: an existing driver if any,
                // otherwise the single driver created on the first new-cell change (shared by all of them)
                VRC.SDKBase.VRC_AvatarParameterDriver newCellDriver = null;
                foreach (var behaviour in behaviours)
                {
                    if (behaviour is VRC.SDKBase.VRC_AvatarParameterDriver existing)
                    {
                        newCellDriver = existing;
                        break;
                    }
                }

                foreach (var change in pendingChanges)
                {
                    if (change.state != state)
                        continue;

                    // cellIndex points at the target parameter in the exact scan order the table cells were collected
                    // (behaviours in order, each driver's parameters in order)
                    if (change.driverBehaviour == null)
                    {
                        // new cell: add a set operation to the state's driver (creating the driver behaviour if the state has none)
                        if (newCellDriver == null)
                        {
                            // VRC_AvatarParameterDriver is abstract; AddStateMachineBehaviour creates the concrete
                            // VRCAvatarParameterDriver and attaches it to the state (undo via the state itself)
                            Undo.RecordObject(state, "Apply Preset Driver Values");
                            newCellDriver = (VRC.SDKBase.VRC_AvatarParameterDriver)state.AddStateMachineBehaviour(typeof(VRCAvatarParameterDriver));
                        }

                        Undo.RecordObject(newCellDriver, "Apply Preset Driver Values");
                        newCellDriver.parameters.Add(new VRC.SDKBase.VRC_AvatarParameterDriver.Parameter
                        {
                            name = change.parameterName,
                            type = VRC.SDKBase.VRC_AvatarParameterDriver.ChangeType.Set,
                            value = change.newValue,
                        });
                        EditorUtility.SetDirty(newCellDriver);
                        continue;
                    }

                    // the pending change remembers the exact driver behaviour; the operation itself is re-resolved by name
                    // (its index may have shifted since the table was scanned because of pending deletions)
                    VRC.SDKBase.VRC_AvatarParameterDriver driver = null;
                    foreach (var behaviour in behaviours)
                    {
                        if (behaviour == change.driverBehaviour)
                            driver = behaviour as VRC.SDKBase.VRC_AvatarParameterDriver;
                    }

                    VRC.SDKBase.VRC_AvatarParameterDriver.Parameter target = null;
                    if (driver != null && driver.parameters != null)
                    {
                        for (int p = 0; p < driver.parameters.Count; p++)
                        {
                            if (driver.parameters[p] != null && string.Equals(driver.parameters[p].name, change.parameterName, StringComparison.Ordinal))
                            {
                                target = driver.parameters[p];
                                break;
                            }
                        }
                    }

                    if (target == null || !string.Equals(target.name, change.parameterName, StringComparison.Ordinal))
                        continue; // the driver changed since the table was scanned; the stale change is dropped

                    Undo.RecordObject(driver, "Apply Preset Driver Values");
                    target.value = change.newValue;
                    EditorUtility.SetDirty(driver);
                }
            }

            pendingChanges.Clear();

            if (appliedDeletions)
                currentPreset = null; // force the table to re-scan the machine (its states/columns changed)

            Repaint();
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

                // clear stale pending changes when the controller changed
                int controllerId = controller.GetInstanceID();
                if (pendingChangesControllerId != controllerId)
                {
                    pendingChanges.Clear();
                    pendingDeletions.Clear();
                    pendingChangesControllerId = controllerId;
                    addRowPending = false;
                    pendingAddRowName = "";
                    pendingAddRowCopyFrom = "";
                }

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

                // clear the cached analysis when the displayed machine changes (e.g. a pending deletion was undone in the
                // animation window) so the table re-scans the machine instead of showing its stale states/columns
                if (currentPreset != null && currentPreset.stateMachine != preset.stateMachine)
                    currentPreset = null;

                currentController = controller;
                currentStateMachine = preset.stateMachine;
                currentPreset = preset;

                EditorGUILayout.Space(8);
                using (new EditorGUILayout.VerticalScope("box"))
                {
                    EditorGUILayout.LabelField($"Preset States  (parameter: {preset.presetParameter}, {preset.states.Count} states)", EditorStyles.boldLabel);

                    DrawPresetStates(preset);
                }

                EditorGUILayout.Space(8);
                using (new EditorGUILayout.VerticalScope("box"))
                {
                    int totalPending = pendingChanges.Count + (addRowPending ? 1 : 0) + pendingDeletions.Count;
                    EditorGUILayout.LabelField($"Pending Changes  ({totalPending})", EditorStyles.boldLabel);

                    // same 30px content indent the table uses
                    void IndentedRow(float indent, Action drawContent)
                    {
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            GUILayout.Space(indent);
                            drawContent();
                        }
                    }

                    if (totalPending == 0)
                    {
                        IndentedRow(ColumnGrid.InnerIndent, () =>
                            EditorGUILayout.HelpBox("No pending changes. Edit a set-mode value in the table above.", MessageType.None));
                    }
                    else
                    {
                        if (addRowPending)
                        {
                            string basedOn = string.IsNullOrEmpty(pendingAddRowCopyFrom) ? "" : $" based on '{pendingAddRowCopyFrom}'";
                            IndentedRow(ColumnGrid.InnerIndent, () =>
                                EditorGUILayout.LabelField($"Add preset '{pendingAddRowName}' with id {pendingAddRowId}{basedOn}", EditorStyles.miniLabel));
                        }

                        // group the pending changes per state (keeping the original order of first edit)
                        var statesWithChanges = new List<AnimatorState>();
                        foreach (var change in pendingChanges)
                        {
                            if (!statesWithChanges.Contains(change.state))
                                statesWithChanges.Add(change.state);
                        }

                        for (int i = 0; i < statesWithChanges.Count; i++)
                        {
                            var state = statesWithChanges[i];
                            IndentedRow(ColumnGrid.InnerIndent, () =>
                                EditorGUILayout.LabelField(state.name, EditorStyles.boldLabel));

                            foreach (var change in pendingChanges)
                            {
                                if (change.state != state)
                                    continue;

                                IndentedRow(ColumnGrid.InnerIndent + ColumnGrid.InnerIndent / 3f, () =>
                                    EditorGUILayout.LabelField(FormatPendingChangeDisplay(change), EditorStyles.miniLabel));
                            }
                        }

                        // the queued deletions (single operations / whole columns / whole rows)
                        foreach (var deletion in pendingDeletions)
                        {
                            IndentedRow(ColumnGrid.InnerIndent, () =>
                                EditorGUILayout.LabelField(FormatPendingDeletionDisplay(deletion), EditorStyles.miniLabel));
                        }

                        EditorGUILayout.Space(2);
                        IndentedRow(ColumnGrid.InnerIndent, () =>
                        {
                            using (new EditorGUILayout.HorizontalScope())
                            {
                                if (GUILayout.Button("Apply", GUILayout.Width(80)))
                                    ApplyPendingChanges();
                                if (GUILayout.Button("Discard", GUILayout.Width(80)))
                                {
                                    pendingChanges.Clear();
                                    pendingDeletions.Clear();
                                    addRowPending = false;
                                    pendingAddRowName = "";
                                    pendingAddRowCopyFrom = "";
                                }
                            }
                        });
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
