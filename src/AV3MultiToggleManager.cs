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

using static d4rkpl4y3r.AV3ToggleUtil.Util.AV3Helper;

namespace d4rkpl4y3r.AV3ToggleUtil
{
    /// <summary>
    /// Inspect "multi toggle" state machines inside the FX animator controller (the last base animation
    /// layer) of the currently selected avatar. Detection &amp; display only.
    ///
    /// A multi toggle state machine is a state machine where:
    ///  - every state is targeted by exactly one any-state transition with a single condition of an
    ///    int parameter == value (all the same parameter, different values per state; other
    ///    any-state transitions like exits are ignored),
    ///  - no state has any transitions going out of it,
    ///  - every state's motion is a single AnimationClip (no blend trees) whose float/int curves are
    ///    all effectively constant (multiple keys allowed only if they all hold the same value).
    /// </summary>
    public class MultiToggleManager : EditorWindow
    {
        private Vector2 scrollPos;
        private int selectedToggleIndex = -1;

        private VRCAvatarDescriptor lastFoundAvatarDescriptor;

        private readonly struct MultiToggleStateEntry
        {
            public readonly AnimatorState state;
            public readonly AnimationClip clip;
            public readonly int toggleId;

            public MultiToggleStateEntry(AnimatorState state, AnimationClip clip, int toggleId)
            {
                this.state = state;
                this.clip = clip;
                this.toggleId = toggleId;
            }
        }

        /// <summary>One row of the table: a single binding (path + property) of the states' clips.</summary>
        private class BindingRow
        {
            public string path;
            public System.Type bindType;
            public string propertyName;
            public bool isObjectReference;
            public bool isBool; // m_IsActive / m_Enabled -> rendered as a checkbox
            public readonly Dictionary<int, BindingCell> perState = new Dictionary<int, BindingCell>(); // state instance id -> cell
        }

        private struct BindingCell
        {
            public bool hasValue;
            public string display; // formatted value for non-bool rows
            public bool boolValue; // the 0/1 flag for bool rows
            public UnityEngine.Object reference; // the referenced asset for object reference rows
        }

        private class MultiToggleInfo
        {
            public AnimatorStateMachine stateMachine;
            public string toggleParameter;
            public readonly List<MultiToggleStateEntry> states = new List<MultiToggleStateEntry>();
            public readonly List<BindingRow> rows = new List<BindingRow>();
        }

        /// <summary>
        /// Analyze a state machine against the multi toggle contract (see class docs).
        /// Returns a <see cref="MultiToggleInfo"/> with the states sorted by their toggle value and the
        /// union of their clip's bindings as rows, or null if it is not a multi toggle state machine.
        /// The owning controller is used to resolve the declared int parameters.
        /// </summary>
        private static MultiToggleInfo AnalyzeMultiToggleStateMachine(AnimatorController controller, AnimatorStateMachine stateMachine)
        {
            if (stateMachine == null)
                return null;

            // The owning controller declares the parameter types
            var parameters = controller?.parameters ?? Array.Empty<AnimatorControllerParameter>();
            var intParameters = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < parameters.Length; i++)
            {
                if (parameters[i].type == AnimatorControllerParameterType.Int && !string.IsNullOrEmpty(parameters[i].name))
                    intParameters.Add(parameters[i].name);
            }

            // Every state contained directly in this machine (any-state transitions only target direct children here)
            var children = stateMachine.states ?? Array.Empty<ChildAnimatorState>();
            var states = new List<AnimatorState>(children.Length);
            var stateIds = new HashSet<int>(children.Length);
            for (int i = 0; i < children.Length; i++)
            {
                if (children[i].state != null)
                {
                    states.Add(children[i].state);
                    stateIds.Add(children[i].state.GetInstanceID());
                }
            }

            if (states.Count == 0)
                return null;

            // a multi toggle's states don't transition anywhere on their own (they're purely driven by the toggle)
            for (int i = 0; i < states.Count; i++)
            {
                var outgoing = states[i].transitions ?? Array.Empty<AnimatorStateTransition>();
                if (outgoing.Length > 0)
                    return null;
            }

            // The any-state transitions that target the machine's own states are its toggle transitions:
            // each must carry exactly one condition, an int parameter == value
            var targetInfo = new Dictionary<int, (int toggleId, string parameter)>(); // destination instance id -> info
            var anyTransitions = stateMachine.anyStateTransitions ?? Array.Empty<AnimatorStateTransition>();
            for (int i = 0; i < anyTransitions.Length; i++)
            {
                var transition = anyTransitions[i];
                if (transition == null || transition.destinationState == null)
                    continue; // exit transitions and other non-state targets aren't toggle transitions

                var destId = transition.destinationState.GetInstanceID();
                if (!stateIds.Contains(destId))
                    continue;

                var conditions = transition.conditions;
                if (conditions == null || conditions.Length != 1)
                    return null; // multiple (or zero) conditions disqualify

                var cond = conditions[0];
                if (cond.mode != AnimatorConditionMode.Equals
                    || string.IsNullOrEmpty(cond.parameter)
                    || !intParameters.Contains(cond.parameter))
                    return null; // the toggle condition must be an int parameter == check

                if (targetInfo.TryGetValue(destId, out var existing))
                {
                    if (!string.Equals(existing.parameter, cond.parameter, StringComparison.Ordinal))
                        return null;
                    continue; // same destination targeted twice -> rejected below (not every state is targeted)
                }

                targetInfo[destId] = (Mathf.RoundToInt(cond.threshold), cond.parameter);
            }

            // every state targeted exactly once, all with differently-valued conditions on the same int parameter
            if (targetInfo.Count != states.Count)
                return null;

            string toggleParameter = null;
            var usedIds = new HashSet<int>();
            foreach (var entry in targetInfo)
            {
                if (toggleParameter == null)
                    toggleParameter = entry.Value.parameter;
                else if (!string.Equals(toggleParameter, entry.Value.parameter, StringComparison.Ordinal))
                    return null; // transitions use different int parameters
                if (!usedIds.Add(entry.Value.toggleId))
                    return null; // two states use the same value
            }

            // every state's motion must be a single (no blend tree) AnimationClip with effectively constant curves
            var info = new MultiToggleInfo
            {
                stateMachine = stateMachine,
                toggleParameter = toggleParameter,
            };
            var rowIndexOf = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < states.Count; i++)
            {
                var state = states[i];
                if (!(state.motion is AnimationClip clip))
                    return null; // no motion, a blend tree, etc.

                if (!IsEffectivelyConstant(clip))
                    return null; // an animated (non-constant) curve disqualifies

                info.states.Add(new MultiToggleStateEntry(state, clip, targetInfo[state.GetInstanceID()].toggleId));
                CollectStateBindings(clip, state.GetInstanceID(), info.rows, rowIndexOf);
            }

            info.states.Sort(CompareStateEntries);
            info.rows.Sort(CompareBindingRows);
            return info;
        }

        private static int CompareStateEntries(MultiToggleStateEntry a, MultiToggleStateEntry b)
        {
            if (a.toggleId != b.toggleId)
                return a.toggleId.CompareTo(b.toggleId);
            return string.Compare(a.state.name, b.state.name, StringComparison.Ordinal);
        }

        private static int CompareBindingRows(BindingRow a, BindingRow b)
        {
            int c = string.Compare(a.path, b.path, StringComparison.Ordinal);
            if (c != 0)
                return c;
            if (a.isObjectReference != b.isObjectReference)
                return a.isObjectReference ? 1 : -1;
            return string.Compare(a.propertyName, b.propertyName, StringComparison.Ordinal);
        }

        /// <summary>True when every float/int curve of the clip holds one single value across all of its keys.</summary>
        private static bool IsEffectivelyConstant(AnimationClip clip)
        {
            // GetCurveBindings covers every non-PPtr curve (float, int, bool, quaternion) of the clip
            var bindings = AnimationUtility.GetCurveBindings(clip);
            for (int i = 0; i < bindings.Length; i++)
            {
                var binding = bindings[i];
                if (binding.isPPtrCurve)
                    continue;

                var curve = AnimationUtility.GetEditorCurve(clip, binding);
                if (curve == null)
                    continue;

                var keys = curve.keys;
                for (int k = 1; k < keys.Length; k++)
                {
                    if (keys[k].value != keys[0].value)
                        return false;
                }
            }

            return true;
        }

        /// <summary>Add the clip's curve &amp; object reference bindings to the row set, filling this state's cell for each row.</summary>
        private static void CollectStateBindings(AnimationClip clip, int stateId, List<BindingRow> rows, Dictionary<string, int> rowIndexOf)
        {
            var bindings = AnimationUtility.GetCurveBindings(clip);
            for (int i = 0; i < bindings.Length; i++)
            {
                var binding = bindings[i];
                if (binding.isPPtrCurve)
                    continue; // texture / PPtr swaps aren't tracked yet

                var key = $"{binding.path}|{binding.propertyName}|{binding.type.FullName}";
                if (!rowIndexOf.TryGetValue(key, out var rowIndex))
                {
                    rowIndex = rows.Count;
                    rowIndexOf[key] = rowIndex;
                    rows.Add(new BindingRow
                    {
                        path = binding.path,
                        bindType = binding.type,
                        propertyName = binding.propertyName,
                        isBool = IsBoolBinding(binding.propertyName),
                    });
                }

                // the curve is effectively constant (verified at detection), any key does
                var curve = AnimationUtility.GetEditorCurve(clip, binding);
                if (curve != null && curve.keys.Length > 0)
                {
                    float value = curve.keys[curve.keys.Length - 1].value;
                    rows[rowIndex].perState[stateId] = new BindingCell
                    {
                        hasValue = true,
                        // bools are rendered as checkboxes from boolValue, everything else as formatted text
                        boolValue = value != 0f,
                        display = rows[rowIndex].isBool ? null : FormatValue(value),
                    };
                    continue;
                }

                rows[rowIndex].perState[stateId] = default; // unresolvable curve value
            }

            var objectBindings = AnimationUtility.GetObjectReferenceCurveBindings(clip);
            for (int i = 0; i < objectBindings.Length; i++)
            {
                var binding = objectBindings[i];
                var key = $"OBJ:{binding.path}|{binding.propertyName}|{binding.type.FullName}";
                if (!rowIndexOf.TryGetValue(key, out var rowIndex))
                {
                    rowIndex = rows.Count;
                    rowIndexOf[key] = rowIndex;
                    rows.Add(new BindingRow
                    {
                        path = binding.path,
                        bindType = binding.type,
                        propertyName = binding.propertyName,
                        isObjectReference = true,
                    });
                }

                var keyframes = AnimationUtility.GetObjectReferenceCurve(clip, binding);
                rows[rowIndex].perState[stateId] = new BindingCell
                {
                    hasValue = keyframes != null && keyframes.Length > 0,
                    reference = keyframes != null && keyframes.Length > 0 ? keyframes[keyframes.Length - 1].value : null,
                };
            }
        }

        /// <summary>
        /// Scan all state machines in the controller (layers and nested sub state machines) and
        /// return the path + analysis of every machine that is a multi toggle state machine.
        /// </summary>
        private static List<(string path, AnimatorStateMachine stateMachine, MultiToggleInfo info)> FindMultiToggleStateMachines(AnimatorController controller)
        {
            var results = new List<(string path, AnimatorStateMachine stateMachine, MultiToggleInfo info)>();
            if (controller == null)
                return results;

            void Scan(AnimatorStateMachine stateMachine, string path)
            {
                if (stateMachine == null)
                    return;

                var info = AnalyzeMultiToggleStateMachine(controller, stateMachine);
                if (info != null)
                    results.Add((path, stateMachine, info));

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

        // Widths of the table columns (kept in consts for easy tweaking)
        private const float ColumnWidthMin = 60f; // min width of a state column
        private const float ColumnWidthMax = 120f; // max width of a state column
        private const float ColumnTextPadding = 6f; // inner horizontal padding inside a state column
        private const float PathColumnWidthMin = 200f; // min width of the path column
        private const float PathColumnWidthMax = 300f; // max width of the path column
        private const float PropColumnWidthMin = 100f; // min width of the property column
        private const float PropColumnWidthMax = 220f; // max width of the property column
        private const float RowPadding = 1f; // vertical padding between rows
        private const float ColumnPadding = 4f; // horizontal padding between columns
        private static readonly float RowHeight = EditorGUIUtility.singleLineHeight + 2f;

        /// <summary>Compute the width of each state column based on the length of its (possibly shortened) header, clamped to &lt;see cref="ColumnWidthMin"/&gt;/&lt;see cref="ColumnWidthMax"/&gt;.</summary>
        private static List<float> BuildColumnWidths(List<MultiToggleStateEntry> states)
        {
            var widths = new List<float>(states.Count);
            foreach (var entry in states)
            {
                var size = GUI.skin.label.CalcSize(new GUIContent($"{GetShortStateName(states, entry.state.name)} ({entry.toggleId})"));
                widths.Add(Mathf.Clamp(size.x + ColumnTextPadding, ColumnWidthMin, ColumnWidthMax));
            }
            return widths;
        }

        /// <summary>
        /// The state name with the longest common whole-word prefix of all the states' names removed.
        /// Only word boundaries are considered: the stripped part always ends on a space inside the shared
        /// prefix (i.e. it's a complete word), so no partial words are ever cut.
        /// </summary>
        private static string GetShortStateName(List<MultiToggleStateEntry> states, string name)
        {
            // find the longest common prefix of all the names
            int commonLength = name.Length;
            for (int i = 1; i < states.Count; i++)
            {
                var other = states[i].state.name;
                int n = Mathf.Min(commonLength, other.Length);
                int j = 0;
                while (j < n && name[j] == other[j])
                    j++;
                commonLength = j;
                if (commonLength == 0)
                    break;
            }

            // the longest whole-word prefix is everything up to (and including) the last space inside the shared part
            int stripLength = -1;
            for (int i = commonLength - 1; i >= 0; i--)
            {
                if (name[i] == ' ')
                {
                    stripLength = i + 1;
                    break;
                }
            }

            if (stripLength < 0)
                return name; // shared part contains no word boundary

            // never strip a name down to an empty header
            for (int i = 0; i < states.Count; i++)
            {
                if (states[i].state.name.Length <= stripLength)
                    return name;
            }

            return name.Substring(stripLength);
        }

        /// <summary>Compute the widths of the leading path &amp; property columns based on the width of their widest content.</summary>
        private static (float pathWidth, float propertyWidth) BuildBindingColumnWidths(List<BindingRow> rows)
        {
            float pathWidth = PathColumnWidthMin;
            float propertyWidth = PropColumnWidthMin;
            foreach (var row in rows)
            {
                var pathSize = GUI.skin.label.CalcSize(GetPathContent(row.path));
                pathWidth = Mathf.Max(pathWidth, Mathf.Clamp(pathSize.x + ColumnTextPadding, PathColumnWidthMin, PathColumnWidthMax));

                var propertySize = GUI.skin.label.CalcSize(new GUIContent(row.propertyName));
                propertyWidth = Mathf.Max(propertyWidth, Mathf.Clamp(propertySize.x + ColumnTextPadding, PropColumnWidthMin, PropColumnWidthMax));
            }
            return (pathWidth, propertyWidth);
        }

        /// <summary>Display an empty (root) path as '(root)' so the column stays readable.</summary>
        private static GUIContent GetPathContent(string path)
        {
            return new GUIContent(path.Length > 0 ? path : "(root)");
        }

        /// <summary>
        /// Resolve the object a binding's path &amp; type point at: Transform / GameObject bindings are the
        /// transform itself, component bindings are that component on the transform. Null when the path or
        /// the component on it can't be found.
        /// </summary>
        private static UnityEngine.Object ResolveBindingTarget(Transform avatarRoot, BindingRow row)
        {
            if (avatarRoot == null || row.bindType == null)
                return null;

            var transform = FindTransformByAvatarPath(avatarRoot, row.path);
            if (transform == null)
                return null;

            if (row.bindType == typeof(Transform) || row.bindType == typeof(GameObject))
                return transform;

            if (typeof(Component).IsAssignableFrom(row.bindType))
                return transform.GetComponent(row.bindType);

            return null; // not a component-typed binding (rare) -> fall back to the path text
        }

        /// <summary>Get the layout rect for one row of the table (a single GetRect call for the whole row).</summary>
        private static Rect GetTableRowRect(float width)
        {
            return EditorGUILayout.GetControlRect(false, RowHeight, GUILayout.Width(width));
        }

        /// <summary>Shrink a row rect so cells don't fill the row padding (keep the height of the content, centered on the padding) and don't start at x=0 (offset by the indent).</summary>
        private static Rect GetTableCellRect(Rect rowRect)
        {
            return new Rect(rowRect.x + ColumnGrid.InnerIndent, rowRect.y + RowPadding, rowRect.width - ColumnGrid.InnerIndent, rowRect.height - RowPadding * 2f);
        }

        /// <summary>
        /// Draw the multi toggle as a table transposed compared to the preset manager's:
        /// the columns are the state's (their name + toggle value), the rows are the bindings of the states' clips.
        /// </summary>
        private void DrawMultiToggleTable(MultiToggleInfo info, Transform avatarRoot)
        {
            var columnWidths = BuildColumnWidths(info.states);
            var (pathWidth, propertyWidth) = BuildBindingColumnWidths(info.rows);

            float rowWidth = ColumnGrid.InnerIndent + pathWidth + propertyWidth + ColumnPadding * 2f;
            for (int i = 0; i < columnWidths.Count; i++)
                rowWidth += columnWidths[i] + ColumnPadding;

            // header row: the path & property column labels, then one column per state
            var headerRect = GetTableRowRect(rowWidth);
            var headerCell = GetTableCellRect(headerRect);
            GUI.Label(new Rect(headerCell.x, headerCell.y, pathWidth, headerCell.height), "Path", EditorStyles.boldLabel);
            float headerX = headerCell.x + pathWidth + ColumnPadding;
            GUI.Label(new Rect(headerX, headerCell.y, propertyWidth, headerCell.height), "Property", EditorStyles.boldLabel);
            headerX += propertyWidth + ColumnPadding;
            for (int i = 0; i < info.states.Count; i++)
            {
                var entry = info.states[i];
                var headerContent = new GUIContent($"{GetShortStateName(info.states, entry.state.name)} ({entry.toggleId})",
                    $"State: {entry.state.name}{Environment.NewLine}Value: {entry.toggleId}{Environment.NewLine}Clip: {entry.clip.name}");
                GUI.Label(new Rect(headerX, headerCell.y, columnWidths[i], headerCell.height), headerContent, EditorStyles.boldLabel);
                headerX += columnWidths[i] + ColumnPadding;
            }

            for (int i = 0; i < info.rows.Count; i++)
            {
                var rowRect = GetTableRowRect(rowWidth);
                DrawBindingRow(info.rows[i], info.states, columnWidths, pathWidth, propertyWidth, avatarRoot, rowRect);
            }
        }

        /// <summary>Draw every cell of one binding row at its calculated x &amp; width inside the row rect.</summary>
        private void DrawBindingRow(BindingRow row, List<MultiToggleStateEntry> states, List<float> columnWidths,
            float pathWidth, float propertyWidth, Transform avatarRoot, Rect rowRect)
        {
            var cellRect = GetTableCellRect(rowRect);

            // the path column is the object the binding binds to (transform itself, or the bound component
            // on that transform, when it resolves), else the raw path text
            var pathContent = GetPathContent(row.path);
            var typeName = row.bindType == null ? "Unknown"
                : row.bindType == typeof(Transform) || row.bindType == typeof(GameObject) ? row.bindType.Name
                : typeof(Component).IsAssignableFrom(row.bindType) ? $"{row.bindType.Name} (Component)"
                : row.bindType.Name;
            var pathTooltip = new GUIContent(pathContent.text,
                $"{pathContent.text}{Environment.NewLine}{typeName}");
            var pathCell = new Rect(cellRect.x, cellRect.y, pathWidth, cellRect.height);
            UnityEngine.Object boundObject = ResolveBindingTarget(avatarRoot, row);
            if (boundObject != null)
            {
                // display only: disabled so the reference can't be swapped
                using (new EditorGUI.DisabledScope(true))
                {
                    EditorGUI.ObjectField(pathCell, boundObject, boundObject.GetType(), false);
                }
                EditorGUI.LabelField(pathCell, new GUIContent("", pathTooltip.tooltip));
            }
            else
            {
                GUI.Label(pathCell, pathTooltip);
            }

            float x = cellRect.x + pathWidth + ColumnPadding;
            GUI.Label(new Rect(x, cellRect.y, propertyWidth, cellRect.height), row.propertyName);

            x += propertyWidth + ColumnPadding;
            for (int i = 0; i < states.Count; i++)
            {
                var columnRect = new Rect(x, cellRect.y, columnWidths[i], cellRect.height);
                row.perState.TryGetValue(states[i].state.GetInstanceID(), out var cell);

                if (row.isObjectReference)
                {
                    // display only: the field shows the referenced asset, disabled so it can't write into the clip
                    using (new EditorGUI.DisabledScope(true))
                    {
                        EditorGUI.ObjectField(columnRect, cell.reference, typeof(UnityEngine.Object), false);
                    }
                }
                else if (row.isBool)
                {
                    // display only: the 0/1 flag as a disabled checkbox
                    using (new EditorGUI.DisabledScope(true))
                    {
                        EditorGUI.Toggle(columnRect, cell.hasValue ? cell.boolValue : false);
                    }
                }
                else
                {
                    GUI.Label(columnRect, cell.hasValue && cell.display != null ? cell.display : "—");
                }

                x += columnWidths[i] + ColumnPadding;
            }
        }

        private static string FormatValue(float value)
        {
            return (float)Mathf.RoundToInt(value) == value
                ? Mathf.RoundToInt(value).ToString(CultureInfo.InvariantCulture)
                : value.ToString("0.###", CultureInfo.InvariantCulture);
        }

        /// <summary>Bindings that hold a boolean as a 0/1 float and should be shown as checkboxes.</summary>
        private static bool IsBoolBinding(string propertyName)
        {
            return propertyName == "m_IsActive" || propertyName == "m_Enabled";
        }

        private void OnGUI()
        {
            var av = GetCurrentOrLastAvatarDescriptor(ref lastFoundAvatarDescriptor);
            var controller = av == null ? null : GetFxAnimatorController(av);

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
                EditorGUILayout.HelpBox("No Animator Controller found in the FX base animation layer.", MessageType.Info);
                return;
            }

            using (new EditorGUILayout.VerticalScope())
            {
                using var scroll = new EditorGUILayout.ScrollViewScope(scrollPos);
                scrollPos = scroll.scrollPosition;

                var toggles = FindMultiToggleStateMachines(controller);

                if (toggles.Count == 0)
                {
                    EditorGUILayout.HelpBox($"No multi toggle state machines found in controller '{controller.name}'.\n" +
                        "A multi toggle state machine has a transition for each of its states pointing at it (an any-state transition) " +
                        "with a single int parameter == condition (all on the same parameter, different values per state). " +
                        "The states must have no transitions going out of them, and each state's motion must be a single animation clip " +
                        "(no blend tree) whose curves are all effectively constant.", MessageType.Info);
                    return;
                }

                if (selectedToggleIndex < 0 || selectedToggleIndex >= toggles.Count)
                    selectedToggleIndex = 0;

                using (new EditorGUILayout.VerticalScope("box"))
                {
                    EditorGUILayout.LabelField("Multi Toggle State Machine", EditorStyles.boldLabel);
                    var options = toggles.Select(t => $"{t.path}  [param: {t.info.toggleParameter}, {t.info.states.Count} states]").ToArray();
                    selectedToggleIndex = EditorGUILayout.Popup("State Machine", selectedToggleIndex, options);
                }

                var (_, _, info) = toggles[selectedToggleIndex];

                EditorGUILayout.Space(8);
                using (new EditorGUILayout.VerticalScope("box"))
                {
                    EditorGUILayout.LabelField($"Multi Toggle Bindings  (parameter: {info.toggleParameter}, {info.states.Count} states, {info.rows.Count} bindings)", EditorStyles.boldLabel);

                    DrawMultiToggleTable(info, av.transform);
                }
            }
        }

        [MenuItem("Tools/d4rkpl4y3r/AV3 Toggle Util/Multi Toggle Manager")]
        public static void AV3MultiToggleManagerMenuItem()
        {
            var window = GetWindow<MultiToggleManager>();
            window.titleContent = new GUIContent("d4rk Multi Toggle Manager");
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