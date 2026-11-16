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

        // name typed into the table's bottom "add row", and the existing row whose set operations to copy
        private string newRowName = "";
        private string newRowCopyFrom = ""; // if set, copy the driver values of the row with this state name into the new state

        // name typed into the trailing "add param" column's input (header row); the + buttons in its rows add a set operation for it
        private string newParamInput = "";

        // the preset currently displayed (set in OnGUI, used to target the table's actions at the right machine)
        private AnimatorController currentController;
        private AnimatorStateMachine currentStateMachine;

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
            var columns = preset.driverColumns;
            var rows = preset.states;
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
                    + Environment.NewLine + (vrcInfo != null ? GetValueTypeDisplayString(vrcInfo) : "no VRC parameter")
                    + Environment.NewLine + "Click to open in Parameter Inspector";
                var headerContent = new GUIContent(GetParameterDisplayName(column.name)) { tooltip = headerTooltip };
                var columnHeaderRect = new Rect(headerX, headerCell.y, columnWidths[headerXIndex], headerCell.height);
                GUI.Label(columnHeaderRect, headerContent, EditorStyles.boldLabel);

                // left-clicking the parameter name opens it in the Parameter Inspector (same pattern as AV3Helper.ClickableLastRect)
                EditorGUIUtility.AddCursorRect(columnHeaderRect, MouseCursor.Link);
                if (Event.current.type == EventType.MouseDown
                    && Event.current.button == 0
                    && columnHeaderRect.Contains(Event.current.mousePosition))
                {
                    Event.current.Use();
                    AV3ParameterInspector.OpenWithParameter(column.name);
                }
                // right-clicking the parameter name offers removing the whole column
                else if (Event.current.type == EventType.ContextClick && columnHeaderRect.Contains(Event.current.mousePosition))
                {
                    Event.current.Use();
                    ShowRemoveColumnMenu(preset, column);
                }

                headerX += columnWidths[headerXIndex] + ColumnPadding;
                headerXIndex++;
            }

            // trailing "add param" column: the header (name) row holds the parameter name input, the rows hold + buttons
            var newParamRect = new Rect(headerX, headerCell.y, NewParamColumnWidth, headerCell.height);
            string newParamInputTooltip = GetNewParamInputTooltip();
            SetRectTooltip(newParamRect, newParamInputTooltip);
            var newParam = EditorGUI.TextField(newParamRect, newParamInput);
            if (newParam != newParamInput)
                newParamInput = newParam;

            foreach (var entry in rows)
            {
                var rowRect = GetTableRowRect(rowWidth);
                DrawPresetStateRow(preset, entry, columns, columnWidths, rowRect);
            }

            // a bottom row to add a whole new preset state (id +, name input; parameter columns stay empty)
            DrawAddPresetRow(preset, rowWidth);
        }

        /// <summary>
        /// Draw the table's bottom "add row": a + button (disabled until a name is entered) that immediately
        /// creates the new state, the new state's name input, and a "copy from..." dropdown to its right. The
        /// new state's driver set-operations are copied from the row of the selected state.
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
            bool hasName = !string.IsNullOrEmpty(newRowName);
            string copyNote = string.IsNullOrEmpty(newRowCopyFrom) ? "" : $", copying set operations from '{newRowCopyFrom}'";
            var createContent = new GUIContent("+", $"Add a preset state with id {nextId}{copyNote}");

            using (new EditorGUI.DisabledScope(!hasName))
            {
                if (GUI.Button(idRect, createContent))
                {
                    ApplyAddRow(lastFoundAvatarDescriptor, currentController, preset.stateMachine, preset, newRowName, newRowCopyFrom, nextId);
                    newRowName = "";
                    newRowCopyFrom = "";
                }
            }

            SetRectTooltip(nameRect, "New preset state name");
            var newName = EditorGUI.TextField(nameRect, newRowName ?? "");
            if (newName != newRowName)
                newRowName = newName;

            // 'copy from' dropdown: the button shows the selected state (or the 'copy from...' placeholder),
            // the menu lists the placeholder + every existing state; each item stores the exact state name (no index mapping)
            string copyShown = string.IsNullOrEmpty(newRowCopyFrom) ? "copy from..." : newRowCopyFrom;
            if (GUI.Button(copyRect, copyShown, EditorStyles.popup))
            {
                var copyMenu = new GenericMenu();
                copyMenu.AddItem(new GUIContent("copy from..."), string.IsNullOrEmpty(newRowCopyFrom), () => newRowCopyFrom = "");
                foreach (var stateEntry in preset.states)
                {
                    string stateName = stateEntry.state.name; // captured per item, the callbacks run after OnGUI finishes
                    copyMenu.AddItem(new GUIContent(stateName), string.Equals(newRowCopyFrom, stateName, StringComparison.Ordinal), () => newRowCopyFrom = stateName);
                }
                copyMenu.ShowAsContext();
            }
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
                        $"Removes just the '{column.name}' operation ({i + 1}/{cells.Count}) from '{state.name}' immediately (undoable with Ctrl+Z)"),
                    false, () => RemoveDriverOperation(state, cell, column.name));
            }
            menu.ShowAsContext();
        }

        /// <summary>Remove a single driver operation (row + column combination) immediately, with undo (the operation is re-resolved by name, so a stale table can't remove the wrong one).</summary>
        private void RemoveDriverOperation(AnimatorState state, DriverSetInfo cell, string parameterName)
        {
            if (cell.driverBehaviour == null
                || !(cell.driverBehaviour is VRC.SDKBase.VRC_AvatarParameterDriver driver)
                || driver.parameters == null)
                return;

            var parameters = driver.parameters;
            int index = -1;
            for (int i = 0; i < parameters.Count; i++)
            {
                if (parameters[i] != null && string.Equals(parameters[i].name, parameterName, StringComparison.Ordinal))
                {
                    index = i;
                    break;
                }
            }
            if (index < 0)
                return; // the driver changed since the table was scanned - no such operation

            Undo.RecordObject(driver, "Preset Manager: Delete Driver Operation");
            parameters.RemoveAt(index);
            EditorUtility.SetDirty(driver);
            Repaint();
        }

        /// <summary>Right-click context menu on a header parameter name: removes that parameter's operations from every state (the whole column).</summary>
        private void ShowRemoveColumnMenu(PresetInfo preset, DriverColumn column)
        {
            var menu = new GenericMenu();
            menu.AddItem(new GUIContent($"Delete '{column.name}' from all states",
                    $"Removes every '{column.name}' operation from the drivers of all {preset.states.Count} states immediately (undoable with Ctrl+Z)"),
                false, () => RemoveColumnFromAllStates(preset, column));
            menu.ShowAsContext();
        }

        /// <summary>Remove a whole parameter column (the parameter from every state's drivers) immediately, with undo.</summary>
        private void RemoveColumnFromAllStates(PresetInfo preset, DriverColumn column)
        {
            // collect the drivers that contain the parameter, record them all up-front, then remove
            var doomed = new List<VRC.SDKBase.VRC_AvatarParameterDriver>();
            foreach (var entry in preset.states)
            {
                var state = entry.state;
                if (state == null)
                    continue;

                var behaviours = state.behaviours ?? Array.Empty<StateMachineBehaviour>();
                for (int b = 0; b < behaviours.Length; b++)
                {
                    if (!(behaviours[b] is VRC.SDKBase.VRC_AvatarParameterDriver driver) || driver.parameters == null)
                        continue;

                    bool has = false;
                    var parameters = driver.parameters;
                    for (int i = 0; i < parameters.Count; i++)
                    {
                        if (parameters[i] != null && string.Equals(parameters[i].name, column.name, StringComparison.Ordinal))
                        {
                            has = true;
                            break;
                        }
                    }
                    if (has)
                        doomed.Add(driver);
                }
            }

            if (doomed.Count == 0)
                return;

            const string undoName = "Preset Manager: Delete Parameter Column";
            foreach (var driver in doomed)
            {
                Undo.RecordObject(driver, undoName);
                var parameters = driver.parameters;
                for (int i = parameters.Count - 1; i >= 0; i--)
                {
                    if (parameters[i] != null && string.Equals(parameters[i].name, column.name, StringComparison.Ordinal))
                        parameters.RemoveAt(i);
                }
                EditorUtility.SetDirty(driver);
            }
            Repaint();
        }

        /// <summary>Right-click context menu on a row's state name: deletes the whole row (the state itself) from the machine.</summary>
        private void ShowDeleteStateMenu(PresetInfo preset, PresetStateEntry entry)
        {
            var menu = new GenericMenu();
            string warning = entry.isDefault
                ? " This state is the machine's default state - the remaining states take its place afterwards."
                : "";
            menu.AddItem(new GUIContent($"Delete state '{entry.state.name}'",
                    $"Deletes the state, its entry transition, its driver operations and its expression menu entry from the machine immediately (undoable with Ctrl+Z).{warning}"),
                false, () => DeletePresetState(preset, entry));
            menu.ShowAsContext();
        }

        /// <summary>Delete the state (row) from the machine immediately: removes its entry transition & state node,
        /// fixes the default state, re-lays out the machine & drops the matching expression menu entry. Undoable.</summary>
        private void DeletePresetState(PresetInfo preset, PresetStateEntry entry)
        {
            var state = entry.state;
            var sm = currentStateMachine;
            if (state == null || sm == null || currentController == null)
                return;

            string stateName = state.name; // captured before the state gets destroyed

            const string undoName = "Preset Manager: Delete Preset State";
            Undo.RecordObject(currentController, undoName);
            Undo.RecordObject(sm, undoName);

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
                for (int i = 0; i < remaining.Length; i++)
                {
                    if (remaining[i].state != null)
                    {
                        sm.defaultState = remaining[i].state;
                        break;
                    }
                }
            }

            ReLayoutPresetStates(sm);
            RemovePresetMenuEntry(lastFoundAvatarDescriptor?.expressionsMenu, preset.presetParameter, stateName,
                entry.isDefault ? int.MinValue : entry.presetId);

            EditorUtility.SetDirty(currentController);
            EditorUtility.SetDirty(sm);
            Repaint();
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
        private void DrawPresetStateRow(PresetInfo preset, PresetStateEntry entry, List<DriverColumn> columns, List<float> columnWidths, Rect rowRect)
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
                ShowDeleteStateMenu(preset, entry);
            }

            x += NameColumnWidth + ColumnPadding;
            for (int i = 0; i < columnWidths.Count; i++)
            {
                // shrink the field/checkbox rect so its box doesn't eat into the column's inner text padding
                var fieldRect = new Rect(x, cellRect.y, Mathf.Max(columnWidths[i] - ColumnTextPadding, 10f), cellRect.height);
                DrawDriverCell(entry.state, columns[i], fieldRect);
                x += columnWidths[i] + ColumnPadding;
            }

            // trailing "add param" column: + adds a set operation for the header input's parameter on this state.
            // the input resolves to a controller parameter by exact name, or when it fuzzy-matches exactly one parameter.
            string resolvedParam = ResolveNewParam(newParamInput);
            using (new EditorGUI.DisabledScope(resolvedParam == null))
            {
                var newParamCellRect = new Rect(x, cellRect.y, Mathf.Max(NewParamColumnWidth - ColumnTextPadding, 10f), cellRect.height);
                var newParamContent = new GUIContent("+", resolvedParam == null
                    ? GetFuzzyParamTooltip(newParamInput)
                    : $"Add a set operation for '{resolvedParam}' on '{entry.state.name}'");
                if (GUI.Button(newParamCellRect, newParamContent))
                    AddSetOperation(entry.state, resolvedParam, 0f);
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
                return $"Parameter '{input}'" + Environment.NewLine + "Press the + buttons to add it to the states";
            if (currentController == null)
                return $"No parameter named '{input}'" + Environment.NewLine + "No controller selected";

            var matches = FuzzyParamMatches(input);
            if (matches.Count == 1)
                return $"No parameter named '{input}'" + Environment.NewLine
                    + $"Uniquely matches '{matches[0]}'" + Environment.NewLine + "The + buttons will add that";
            if (matches.Count == 0)
                return $"No parameter named '{input}'" + Environment.NewLine + "No fuzzy matches";
            if (matches.Count > 30)
            {
                var shown = matches.Take(30).ToList();
                return $"No parameter named '{input}'" + Environment.NewLine + $"{matches.Count} fuzzy matches:" + Environment.NewLine
                    + string.Join(Environment.NewLine, shown) + Environment.NewLine + $"… and {matches.Count - 30} more";
            }
            return $"No parameter named '{input}'" + Environment.NewLine
                + $"{matches.Count} fuzzy matches:" + Environment.NewLine + string.Join(Environment.NewLine, matches);
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
                // the + button adds a new set operation to the state's driver
                var addTooltip = $"Add a set operation for '{column.name}' on '{state.name}'";
                var addContent = new GUIContent("+", addTooltip);
                if (GUI.Button(cellRect, addContent))
                    AddSetOperation(state, column.name, 0f);
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

            // build the display lines for every operation in the cell
            var lines = new List<string>();
            for (int i = 0; i < cells.Count; i++)
                lines.Add(FormatDriverCell(cells[i], isBool));

            if (editableCellIndex < 0)
            {
                // no set-mode value: the whole cell is display only
                string joined = string.Join(", ", lines);
                string readOnlyTooltip = "Display only" + Environment.NewLine + string.Join(Environment.NewLine, lines);
                DrawReadOnlyCell(cellRect, joined, readOnlyTooltip);
                return;
            }

            // editable cell: the field/checkbox shows the value of the first set-mode driver
            var editableCell = cells[editableCellIndex];
            string cellTooltip = "Only the set-mode value is editable" + Environment.NewLine + string.Join(Environment.NewLine, lines);

            DrawSetCell(cellRect, isBool, editableCell.value, cellTooltip, editableCell, column.name, state);
        }

        /// <summary>Draw a tooltip over the given rect: an empty label carrying the tooltip is drawn on top of the control,
        /// since a control's own GUIContent tooltip only shows on its (empty) label and stays invisible otherwise.</summary>
        private static void SetRectTooltip(Rect controlRect, string tooltip)
        {
            if (string.IsNullOrEmpty(tooltip))
                return;
            GUI.Label(controlRect, new GUIContent("", tooltip));
        }

        /// <summary>Draw a set-mode driver value as an editable checkbox (bool) or delayed text field (numeric); edits are written to the driver immediately.</summary>
        private void DrawSetCell(Rect cellRect, bool isBool, float value, string tooltip, DriverSetInfo originalCell, string parameterName, AnimatorState state)
        {
            if (isBool)
            {
                SetRectTooltip(cellRect, tooltip);
                bool newValue = EditorGUI.Toggle(cellRect, value != 0f);
                if (GUI.changed)
                    SetDriverValue(state, originalCell, parameterName, newValue ? 1f : 0f);
                return;
            }

            SetRectTooltip(cellRect, tooltip);
            string displayed = FormatDriverValue(value);
            string committed = EditorGUI.DelayedTextField(cellRect, displayed);
            if (GUI.changed)
            {
                if (float.TryParse(committed, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed))
                    SetDriverValue(state, originalCell, parameterName, parsed);
                else
                    GUI.FocusControl(null); // invalid input, cancel the edit so the field resets
            }
        }

        /// <summary>Draw a display-only (disabled) input field cell.</summary>
        private static void DrawReadOnlyCell(Rect cellRect, string text, string tooltip)
        {
            SetRectTooltip(cellRect, tooltip);
            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUI.TextField(cellRect, text);
            }
        }

        /// <summary>Write a set-mode value to the state's driver immediately, resolving the parameter (operation) by name
        /// in case the table's scan is stale, with undo support.</summary>
        private void SetDriverValue(AnimatorState state, DriverSetInfo cell, string parameterName, float value)
        {
            if (cell.driverBehaviour == null)
                return;

            var behaviours = state.behaviours ?? Array.Empty<StateMachineBehaviour>();
            VRC.SDKBase.VRC_AvatarParameterDriver driver = null;
            for (int i = 0; i < behaviours.Length; i++)
            {
                if (behaviours[i] == cell.driverBehaviour)
                {
                    driver = behaviours[i] as VRC.SDKBase.VRC_AvatarParameterDriver;
                    break;
                }
            }
            if (driver == null || driver.parameters == null)
                return; // the driver was destroyed since the table was scanned

            VRC.SDKBase.VRC_AvatarParameterDriver.Parameter target = null;
            var parameters = driver.parameters;
            for (int i = 0; i < parameters.Count; i++)
            {
                if (parameters[i] != null && string.Equals(parameters[i].name, parameterName, StringComparison.Ordinal))
                {
                    target = parameters[i];
                    break;
                }
            }
            if (target == null)
                return; // the operation was removed since the table was scanned

            Undo.RecordObject(driver, "Preset Manager: Set Driver Value");
            target.value = value;
            EditorUtility.SetDirty(driver);
            Repaint();
        }

        /// <summary>Add a new set operation to the state's parameter driver immediately (creating a driver on the state if it has none), with undo support. No-op when the state already sets the parameter.</summary>
        private void AddSetOperation(AnimatorState state, string parameterName, float value)
        {
            if (state == null || string.IsNullOrEmpty(parameterName))
                return;

            const string undoName = "Preset Manager: Add Driver Operation";
            var behaviours = state.behaviours ?? Array.Empty<StateMachineBehaviour>();
            VRC.SDKBase.VRC_AvatarParameterDriver driver = null;
            for (int i = 0; i < behaviours.Length; i++)
            {
                if (behaviours[i] is VRC.SDKBase.VRC_AvatarParameterDriver existing)
                    driver = existing;
            }

            if (driver == null)
            {
                Undo.RecordObject(state, undoName);
                // VRC_AvatarParameterDriver is abstract; AddStateMachineBehaviour creates the concrete
                // VRCAvatarParameterDriver and attaches it to the state (undo via the state itself)
                driver = (VRC.SDKBase.VRC_AvatarParameterDriver)state.AddStateMachineBehaviour(typeof(VRCAvatarParameterDriver));
            }

            if (driver.parameters == null)
                return;

            // a state's driver keeps at most one operation per parameter; don't duplicate it
            for (int i = 0; i < driver.parameters.Count; i++)
            {
                if (driver.parameters[i] != null && string.Equals(driver.parameters[i].name, parameterName, StringComparison.Ordinal))
                    return;
            }

            Undo.RecordObject(driver, undoName);
            driver.parameters.Add(new VRC.SDKBase.VRC_AvatarParameterDriver.Parameter
            {
                name = parameterName,
                type = VRC.SDKBase.VRC_AvatarParameterDriver.ChangeType.Set,
                value = value,
            });
            EditorUtility.SetDirty(driver);
            Repaint();
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
                if (av != null && controller != null)
                {
                    GUILayout.Space(8);
                    DrawGitStatusLabel(GetAvatarAnimationGitAssets(av, new[] { controller }));
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

                currentController = controller;
                currentStateMachine = preset.stateMachine;

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
            InitializeLatestAvatarDescriptor(ref window.lastFoundAvatarDescriptor);
            window.Show();
        }

        private void OnSelectionChange()
        {
            Repaint();
        }
    }
}
#endif
