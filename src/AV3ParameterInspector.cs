#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using VRC.SDK3.Dynamics.Contact.Components;
using VRC.SDK3.Dynamics.PhysBone.Components;

using static d4rkpl4y3r.AV3ToggleUtil.Util.AV3Helper;

namespace d4rkpl4y3r.AV3ToggleUtil
{
    public class AV3ParameterInspector : EditorWindow
    {
        private const float MinLeftPanelWidth = 160f;
        private const float MaxLeftPanelWidth = 520f;
        private const float SplitterWidth = 4f;

        private Vector2 leftScrollPos;
        private Vector2 rightScrollPos;
        private float leftPanelWidth = 220f;
        private bool isDraggingSplitter;
        private bool sortParameters = true;
        private string selectedParameter = "";

        private int cachedAvatarId = 0;
        private string cachedParameter = "";
        private ScanResult cachedScanResult;
        private bool forceRescan = true;

        // https://creators.vrchat.com/avatars/animator-parameters/
        private static readonly HashSet<string> VRChatBuiltInParameters = new(StringComparer.Ordinal)
        {
            "IsLocal",
            "PreviewMode",
            "Viseme",
            "Voice",
            "GestureLeft",
            "GestureRight",
            "GestureLeftWeight",
            "GestureRightWeight",
            "AngularY",
            "VelocityX",
            "VelocityY",
            "VelocityZ",
            "VelocityMagnitude",
            "Upright",
            "Grounded",
            "Seated",
            "AFK",
            "TrackingType",
            "VRMode",
            "MuteSelf",
            "InStation",
            "Earmuffs",
            "IsOnFriendsList",
            "AvatarVersion",
            "IsAnimatorEnabled",

            "ScaleModified",
            "ScaleFactor",
            "ScaleFactorInverse",
            "EyeHeightAsMeters",
            "EyeHeightAsPercent",
        };

        [Serializable]
        private class MenuUsage
        {
            public string menuPath;
            public string controlName;
            public VRCExpressionsMenu.Control.ControlType controlType;
            public VRCExpressionsMenu menu;
        }

        [Serializable]
        private class StateUsage
        {
            public AnimatorController controller;
            public string controllerName;
            public string layerName;
            public string statePath;
            public string stateName;
            public AnimatorState state;

            public bool transitionIn;
            public bool transitionOut;
            public bool blendTree;
            public bool motionTime;
            public bool parameterDriver;
        }

        [Serializable]
        private class ScanResult
        {
            public List<MenuUsage> menuUsages = new List<MenuUsage>();
            public List<StateUsage> stateUsages = new List<StateUsage>();

            public HashSet<AnimationClip> transitionClips = new HashSet<AnimationClip>();
            public HashSet<AnimationClip> blendTreeClips = new HashSet<AnimationClip>();
            public HashSet<AnimationClip> motionTimeClips = new HashSet<AnimationClip>();
        }

        private readonly struct ComponentParameterWriter
        {
            public readonly string sourceType;
            public readonly string path;
            public readonly string configuredParameter;

            public ComponentParameterWriter(string sourceType, string path, string configuredParameter)
            {
                this.sourceType = sourceType;
                this.path = path;
                this.configuredParameter = configuredParameter;
            }
        }

        private static bool IsSameParameter(string a, string b)
        {
            return string.Equals(a, b, StringComparison.Ordinal);
        }

        private static IEnumerable<AnimatorController> GetAllControllers(VRCAvatarDescriptor av)
        {
            if (av == null) yield break;

            foreach (var layer in av.baseAnimationLayers)
            {
                var controller = layer.animatorController as AnimatorController;
                if (controller != null) yield return controller;
            }

            foreach (var layer in av.specialAnimationLayers)
            {
                var controller = layer.animatorController as AnimatorController;
                if (controller != null) yield return controller;
            }
        }

        private static List<string> GetAllParameterNames(VRCAvatarDescriptor av, bool sortAlphabetically)
        {
            var names = new List<string>();
            var knownNames = new HashSet<string>(StringComparer.Ordinal);

            void AddParameterName(string name)
            {
                if (string.IsNullOrEmpty(name)) return;
                if (!knownNames.Add(name)) return;
                names.Add(name);
            }

            if (av != null && av.expressionParameters != null && av.expressionParameters.parameters != null)
            {
                foreach (var p in av.expressionParameters.parameters)
                {
                    if (p == null) continue;
                    AddParameterName(p.name);
                }
            }

            foreach (var controller in GetAllControllers(av).Distinct())
            {
                if (controller == null || controller.parameters == null) continue;
                foreach (var p in controller.parameters)
                {
                    if (p == null) continue;
                    AddParameterName(p.name);
                }
            }

            if (sortAlphabetically)
                return names.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();

            return names;
        }

        private static bool TransitionUsesParameter(AnimatorTransitionBase transition, string parameterName)
        {
            if (transition == null || transition.conditions == null) return false;
            for (int i = 0; i < transition.conditions.Length; i++)
            {
                var condition = transition.conditions[i];
                if (IsSameParameter(condition.parameter, parameterName))
                    return true;
            }
            return false;
        }

        private static void CollectClipsFromMotion(Motion motion, HashSet<AnimationClip> clips, HashSet<BlendTree> visitedTrees = null)
        {
            if (motion == null || clips == null) return;

            if (motion is AnimationClip clip)
            {
                clips.Add(clip);
                return;
            }

            if (motion is BlendTree tree)
            {
                if (visitedTrees == null) visitedTrees = new HashSet<BlendTree>();
                if (!visitedTrees.Add(tree)) return;

                var children = tree.children;
                for (int i = 0; i < children.Length; i++)
                {
                    CollectClipsFromMotion(children[i].motion, clips, visitedTrees);
                }
            }
        }

        private static bool MotionUsesBlendParameter(Motion motion, string parameterName, HashSet<BlendTree> visitedTrees = null)
        {
            if (motion == null) return false;
            if (!(motion is BlendTree tree)) return false;

            if (visitedTrees == null) visitedTrees = new HashSet<BlendTree>();
            if (!visitedTrees.Add(tree)) return false;

            if (IsSameParameter(tree.blendParameter, parameterName) || IsSameParameter(tree.blendParameterY, parameterName))
                return true;

            var children = tree.children;
            if (tree.blendType == BlendTreeType.Direct)
            {
                for (int i = 0; i < children.Length; i++)
                {
                    if (IsSameParameter(children[i].directBlendParameter, parameterName))
                        return true;
                }
            }

            for (int i = 0; i < children.Length; i++)
            {
                if (MotionUsesBlendParameter(children[i].motion, parameterName, visitedTrees))
                    return true;
            }

            return false;
        }

        private static bool StateUsesMotionTimeParameter(AnimatorState state, string parameterName)
        {
            if (state == null) return false;
            return state.timeParameterActive && IsSameParameter(state.timeParameter, parameterName);
        }

        private static bool StateUsesParameterDriver(AnimatorState state, string parameterName)
        {
            if (state == null || state.behaviours == null) return false;

            for (int i = 0; i < state.behaviours.Length; i++)
            {
                var behaviour = state.behaviours[i];
                if (behaviour == null) continue;

                if (behaviour is VRCAvatarParameterDriver typedDriver)
                {
                    if (typedDriver.parameters != null)
                    {
                        for (int p = 0; p < typedDriver.parameters.Count; p++)
                        {
                            if (IsSameParameter(typedDriver.parameters[p].name, parameterName))
                                return true;
                        }
                    }
                    continue;
                }
            }

            return false;
        }

        private static bool HasIncomingTransitionUsingParameter(AnimatorStateMachine ownerStateMachine, AnimatorState state, string parameterName)
        {
            if (ownerStateMachine == null || state == null) return false;

            var anyStateTransitions = ownerStateMachine.anyStateTransitions;
            if (anyStateTransitions != null)
            {
                for (int i = 0; i < anyStateTransitions.Length; i++)
                {
                    var transition = anyStateTransitions[i];
                    if (transition == null) continue;
                    if (transition.destinationState != state) continue;
                    if (TransitionUsesParameter(transition, parameterName)) return true;
                }
            }

            var ownerStates = ownerStateMachine.states;
            for (int i = 0; i < ownerStates.Length; i++)
            {
                var sourceState = ownerStates[i].state;
                if (sourceState == null || sourceState.transitions == null) continue;

                for (int t = 0; t < sourceState.transitions.Length; t++)
                {
                    var transition = sourceState.transitions[t];
                    if (transition == null) continue;
                    if (transition.destinationState != state) continue;
                    if (TransitionUsesParameter(transition, parameterName)) return true;
                }
            }

            return false;
        }

        private static bool ControlReferencesParameter(VRCExpressionsMenu.Control control, string parameterName)
        {
            if (control == null) return false;

            if (control.parameter != null && IsSameParameter(control.parameter.name, parameterName))
                return true;

            if (control.subParameters != null)
            {
                for (int i = 0; i < control.subParameters.Length; i++)
                {
                    var subParameter = control.subParameters[i];
                    if (subParameter == null) continue;
                    if (IsSameParameter(subParameter.name, parameterName))
                        return true;
                }
            }

            return false;
        }

        private static VRCExpressionParameters.Parameter GetVRCExpressionParameterInfo(VRCAvatarDescriptor av, string parameterName)
        {
            if (av == null || av.expressionParameters == null || av.expressionParameters.parameters == null || string.IsNullOrEmpty(parameterName))
                return null;
            return av.expressionParameters.parameters.FirstOrDefault(p => p != null && IsSameParameter(p.name, parameterName));
        }

        private static List<(string controllerName, AnimatorControllerParameterType parameterType)> GetAnimatorControllerParameterInfos(
            VRCAvatarDescriptor av,
            string parameterName)
        {
            var results = new List<(string controllerName, AnimatorControllerParameterType parameterType)>();
            if (av == null || string.IsNullOrEmpty(parameterName))
                return results;

            foreach (var controller in GetAllControllers(av).Distinct())
            {
                if (controller == null || controller.parameters == null)
                    continue;

                var parameter = controller.parameters.FirstOrDefault(p => p != null && IsSameParameter(p.name, parameterName));
                if (parameter == null)
                    continue;

                results.Add((controller.name, parameter.type));
            }

            return results
                .OrderBy(x => x.controllerName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static bool IsParameterWrittenByPhysBone(string selectedParameter, string configuredParameter)
        {
            if (string.IsNullOrEmpty(selectedParameter) || string.IsNullOrEmpty(configuredParameter))
                return false;

            if (IsSameParameter(selectedParameter, configuredParameter))
                return true;

            return selectedParameter.StartsWith(configuredParameter + "_", StringComparison.Ordinal);
        }

        private static List<ComponentParameterWriter> GetComponentParameterWriters(VRCAvatarDescriptor av, string selectedParameter)
        {
            var writers = new List<ComponentParameterWriter>();
            if (av == null || av.transform == null || string.IsNullOrEmpty(selectedParameter))
                return writers;

            var root = av.transform;
            foreach (var physBone in root.GetComponentsInChildren<VRCPhysBone>(true))
            {
                if (physBone == null || string.IsNullOrEmpty(physBone.parameter))
                    continue;

                if (!IsParameterWrittenByPhysBone(selectedParameter, physBone.parameter))
                    continue;

                var path = AnimationUtility.CalculateTransformPath(physBone.transform, root);
                if (string.IsNullOrEmpty(path))
                    path = "(Root)";

                writers.Add(new ComponentParameterWriter("PhysBone", path, physBone.parameter));
            }

            foreach (var contactReceiver in root.GetComponentsInChildren<VRCContactReceiver>(true))
            {
                if (contactReceiver == null || string.IsNullOrEmpty(contactReceiver.parameter))
                    continue;

                if (!IsSameParameter(selectedParameter, contactReceiver.parameter))
                    continue;

                var path = AnimationUtility.CalculateTransformPath(contactReceiver.transform, root);
                if (string.IsNullOrEmpty(path))
                    path = "(Root)";

                writers.Add(new ComponentParameterWriter("Contact", path, contactReceiver.parameter));
            }

            return writers
                .OrderBy(x => x.sourceType, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.path, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.configuredParameter, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private ScanResult BuildScanResult(VRCAvatarDescriptor av, string parameterName)
        {
            var result = new ScanResult();
            if (av == null || string.IsNullOrEmpty(parameterName))
                return result;

            void ScanMenus()
            {
                var rootMenu = av.expressionsMenu;
                if (rootMenu == null) return;

                var visitedMenus = new HashSet<VRCExpressionsMenu>();
                void TraverseMenu(VRCExpressionsMenu menu, string menuPath)
                {
                    if (menu == null || !visitedMenus.Add(menu)) return;

                    if (menu.controls != null)
                    {
                        for (int i = 0; i < menu.controls.Count; i++)
                        {
                            var control = menu.controls[i];
                            if (control == null) continue;

                            if (ControlReferencesParameter(control, parameterName))
                            {
                                result.menuUsages.Add(new MenuUsage
                                {
                                    menu = menu,
                                    menuPath = menuPath,
                                    controlName = string.IsNullOrEmpty(control.name) ? "(Unnamed Control)" : control.name,
                                    controlType = control.type,
                                });
                            }

                            if (control.type == VRCExpressionsMenu.Control.ControlType.SubMenu && control.subMenu != null)
                            {
                                var childName = string.IsNullOrEmpty(control.name) ? control.subMenu.name : control.name;
                                var nextPath = string.IsNullOrEmpty(menuPath) ? childName : menuPath + " / " + childName;
                                TraverseMenu(control.subMenu, nextPath);
                            }
                        }
                    }
                }

                TraverseMenu(rootMenu, rootMenu.name);
            }

            void ScanAnimators()
            {
                foreach (var controller in GetAllControllers(av).Distinct())
                {
                    if (controller == null || controller.layers == null) continue;

                    for (int layerIndex = 0; layerIndex < controller.layers.Length; layerIndex++)
                    {
                        var layer = controller.layers[layerIndex];
                        var layerStateMachine = layer.stateMachine;
                        if (layerStateMachine == null) continue;

                        void TraverseStateMachine(AnimatorStateMachine sm, string smPath)
                        {
                            if (sm == null) return;

                            var states = sm.states;
                            for (int i = 0; i < states.Length; i++)
                            {
                                var state = states[i].state;
                                if (state == null) continue;

                                var stateName = string.IsNullOrEmpty(states[i].state.name) ? "(Unnamed State)" : states[i].state.name;
                                var subStateMachinePath = string.IsNullOrEmpty(smPath) ? "" : $" / {smPath}";
                                var statePath = $"{controller.name} / {layer.name}{subStateMachinePath}";

                                var usage = new StateUsage
                                {
                                    controller = controller,
                                    controllerName = controller.name,
                                    layerName = layer.name,
                                    statePath = statePath,
                                    stateName = stateName,
                                    state = state,
                                };

                                usage.transitionOut = state.transitions != null && state.transitions.Any(t => TransitionUsesParameter(t, parameterName));
                                usage.transitionIn = HasIncomingTransitionUsingParameter(sm, state, parameterName);

                                if (usage.transitionIn || usage.transitionOut)
                                    CollectClipsFromMotion(state.motion, result.transitionClips);

                                usage.blendTree = MotionUsesBlendParameter(state.motion, parameterName);
                                if (usage.blendTree)
                                    CollectClipsFromMotion(state.motion, result.blendTreeClips);

                                usage.motionTime = StateUsesMotionTimeParameter(state, parameterName);
                                if (usage.motionTime)
                                    CollectClipsFromMotion(state.motion, result.motionTimeClips);

                                usage.parameterDriver = StateUsesParameterDriver(state, parameterName);

                                if (usage.transitionIn || usage.transitionOut || usage.blendTree || usage.motionTime || usage.parameterDriver)
                                    result.stateUsages.Add(usage);
                            }

                            var childStateMachines = sm.stateMachines;
                            for (int i = 0; i < childStateMachines.Length; i++)
                            {
                                var child = childStateMachines[i].stateMachine;
                                if (child == null) continue;

                                var childName = string.IsNullOrEmpty(childStateMachines[i].stateMachine.name)
                                    ? "(Unnamed StateMachine)"
                                    : childStateMachines[i].stateMachine.name;

                                var childPath = string.IsNullOrEmpty(smPath) ? childName : $"{smPath} / {childName}";
                                TraverseStateMachine(child, childPath);
                            }
                        }

                        TraverseStateMachine(layerStateMachine, "");
                    }
                }
            }

            ScanMenus();
            ScanAnimators();

            result.menuUsages = result.menuUsages
                .OrderBy(x => x.menuPath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.controlName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            result.stateUsages = result.stateUsages
                .OrderBy(x => x.statePath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.stateName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.statePath, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return result;
        }

        private void OnGUI()
        {
            var av = FindAvatarDescriptor(Selection.activeGameObject);

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("Avatar", GUILayout.Width(46));
                using (new EditorGUI.DisabledScope(true))
                {
                    EditorGUILayout.ObjectField(av, typeof(VRCAvatarDescriptor), true);
                    EditorGUILayout.ObjectField(av != null ? av.expressionParameters : null, typeof(VRCExpressionParameters), false);
                }
            }

            if (av == null)
            {
                EditorGUILayout.HelpBox("No VRC Avatar Descriptor found.", MessageType.Warning);
                return;
            }

            var allParameters = GetAllParameterNames(av, sortParameters);
            if (allParameters.Count == 0)
            {
                EditorGUILayout.HelpBox("No parameters found in Expression Parameters or linked Animator Controllers.", MessageType.Info);
                return;
            }

            if (string.IsNullOrEmpty(selectedParameter) || !allParameters.Contains(selectedParameter))
            {
                selectedParameter = allParameters[0];
                forceRescan = true;
            }

            var avatarId = av.GetInstanceID();
            if (forceRescan || cachedScanResult == null || cachedAvatarId != avatarId || !IsSameParameter(cachedParameter, selectedParameter))
            {
                cachedScanResult = BuildScanResult(av, selectedParameter);
                cachedAvatarId = avatarId;
                cachedParameter = selectedParameter;
                forceRescan = false;
            }

            using var horizontal = new EditorGUILayout.HorizontalScope();

            using (new EditorGUILayout.VerticalScope(GUILayout.Width(leftPanelWidth)))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label($"Parameters ({allParameters.Count})", EditorStyles.boldLabel);
                    sortParameters = GUILayout.Toggle(sortParameters, "A→Z", GUI.skin.button, GUILayout.ExpandWidth(false));
                }

                using var leftScroll = new EditorGUILayout.ScrollViewScope(leftScrollPos);
                leftScrollPos = leftScroll.scrollPosition;

                for (int i = 0; i < allParameters.Count; i++)
                {
                    var parameter = allParameters[i];
                    using var cc = new EditorGUI.ChangeCheckScope();
                    var selected = GUILayout.Toggle(IsSameParameter(selectedParameter, parameter), parameter, GUI.skin.button, GUILayout.ExpandWidth(true));
                    if (cc.changed && selected && !IsSameParameter(selectedParameter, parameter))
                    {
                        selectedParameter = parameter;
                        forceRescan = true;
                        GUI.FocusControl(null);
                    }
                }
            }

            var splitterRect = GUILayoutUtility.GetRect(SplitterWidth, 1f, GUILayout.Width(SplitterWidth), GUILayout.ExpandHeight(true));
            EditorGUIUtility.AddCursorRect(splitterRect, MouseCursor.ResizeHorizontal);
            EditorGUI.DrawRect(splitterRect, EditorGUIUtility.isProSkin ? new Color(0.2f, 0.2f, 0.2f, 1f) : new Color(0.65f, 0.65f, 0.65f, 1f));

            var evt = Event.current;
            if (evt.type == EventType.MouseDown && evt.button == 0 && splitterRect.Contains(evt.mousePosition))
            {
                isDraggingSplitter = true;
                evt.Use();
            }
            else if (evt.type == EventType.MouseDrag && isDraggingSplitter)
            {
                leftPanelWidth = Mathf.Clamp(evt.mousePosition.x, MinLeftPanelWidth, Mathf.Min(MaxLeftPanelWidth, position.width - 220f));
                Repaint();
            }
            else if (evt.type == EventType.MouseUp && isDraggingSplitter)
            {
                isDraggingSplitter = false;
                evt.Use();
            }

            using (new EditorGUILayout.VerticalScope())
            {
                using var rightScroll = new EditorGUILayout.ScrollViewScope(rightScrollPos);
                rightScrollPos = rightScroll.scrollPosition;

                var rightPanelWidth = Mathf.Max(200f, position.width - leftPanelWidth - SplitterWidth - 30f);
                var columns = Mathf.Clamp(Mathf.FloorToInt(rightPanelWidth / 260f), 1, 3);
                const float innerIndent = 30f;
                const float columnSpacing = 4f;
                var entryWidth = Mathf.Max(80f, Mathf.Floor((rightPanelWidth - innerIndent - (columns - 1) * (columnSpacing + 3)) / columns));
                
                using (new EditorGUILayout.VerticalScope("box"))
                {
                    var componentWriters = GetComponentParameterWriters(av, selectedParameter);
                    const float width = 200f;

                    GUILayout.Label("Selected Parameter", EditorStyles.boldLabel);
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        GUILayout.Space(innerIndent);
                        GUILayout.Label($"{selectedParameter}", GUILayout.Width(width));
                        var vrcParameter = GetVRCExpressionParameterInfo(av, selectedParameter);
                        if (vrcParameter != null)
                        {
                            GUILayout.Label($"{vrcParameter.valueType}"
                                + (vrcParameter.saved ? ", Saved" : "")
                                + (vrcParameter.networkSynced ? ", Synced" : ""));
                        }
                        else if (VRChatBuiltInParameters.Contains(selectedParameter))
                        {
                            GUILayout.Label($"VRChat Built-in Parameter");
                        }
                        else if (componentWriters.Count == 1)
                        {
                            GUILayout.Label($"{componentWriters[0].sourceType}: '{componentWriters[0].path}'");
                        }
                    }

                    var controllerParameterInfos = GetAnimatorControllerParameterInfos(av, selectedParameter);
                    foreach (var info in controllerParameterInfos)
                    {
                        using var controllerInfoRow = new EditorGUILayout.HorizontalScope();
                        GUILayout.Space(innerIndent);
                        GUILayout.Label($"{info.controllerName}", GUILayout.Width(width));
                        GUILayout.Label($"{info.parameterType}");
                    }

                    if (componentWriters.Count > 1)
                    {
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            GUILayout.Space(innerIndent);
                            GUILayout.Label("Set by PhysBones / Contact Receivers");
                        }

                        foreach (var writer in componentWriters)
                        {
                            using var writerRow = new EditorGUILayout.HorizontalScope();
                            GUILayout.Space(innerIndent + 15);
                            GUILayout.Label($"{writer.sourceType}: '{writer.path}'");
                        }
                    }
                }

                bool DrawMenuUsageSection()
                {
                    var usages = cachedScanResult.menuUsages;
                    if (usages.Count == 0)
                        return false;

                    EditorGUILayout.Space(8);
                    using (new EditorGUILayout.VerticalScope("box"))
                    {
                        EditorGUILayout.LabelField("Sub-Menus / Controls Using Parameter", EditorStyles.boldLabel);

                        var grouped = usages
                            .GroupBy(x => x.menuPath)
                            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                            .ToList();

                        foreach (var group in grouped)
                        {
                            using (new EditorGUILayout.HorizontalScope())
                            {
                                GUILayout.Space(15);
                                GUILayout.Label(group.Key, EditorStyles.boldLabel, GUILayout.ExpandWidth(true));
                            }

                            var controlNames = group
                                .Select(x => $"{x.controlName}   ({x.controlType})")
                                .Distinct()
                                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                                .ToList();

                            for (int i = 0; i < controlNames.Count; i += columns)
                            {
                                using (new EditorGUILayout.HorizontalScope())
                                {
                                    GUILayout.Space(innerIndent);
                                    for (int col = 0; col < columns; col++)
                                    {
                                        var index = i + col;
                                        if (index < controlNames.Count)
                                        {
                                            GUILayout.Label(controlNames[index], GUILayout.Width(entryWidth));
                                        }
                                        else
                                        {
                                            GUILayout.Space(entryWidth);
                                        }

                                        if (col < columns - 1)
                                        {
                                            GUILayout.Space(columnSpacing);
                                        }
                                    }
                                }
                            }
                        }
                    }
                    return true;
                }

                bool DrawStateUsageSection(string title, Func<StateUsage, bool> predicate)
                {
                    var matches = cachedScanResult.stateUsages.Where(predicate).ToList();
                    if (matches.Count == 0)
                        return false;

                    EditorGUILayout.Space(8);
                    using (new EditorGUILayout.VerticalScope("box"))
                    {
                        EditorGUILayout.LabelField(title, EditorStyles.boldLabel);
                        var grouped = matches
                            .GroupBy(x => x.statePath)
                            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                            .ToList();

                        foreach (var group in grouped)
                        {
                            using (new EditorGUILayout.HorizontalScope())
                            {
                                GUILayout.Space(15);
                                GUILayout.Label(group.Key, EditorStyles.boldLabel, GUILayout.ExpandWidth(true));
                            }

                            var stateNames = group.Select(x => x.stateName).Distinct().OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
                            for (int i = 0; i < stateNames.Count; i += columns)
                            {
                                using (new EditorGUILayout.HorizontalScope())
                                {
                                    GUILayout.Space(innerIndent);
                                    for (int col = 0; col < columns; col++)
                                    {
                                        var index = i + col;
                                        if (index < stateNames.Count)
                                        {
                                            GUILayout.Label(stateNames[index], GUILayout.Width(entryWidth));
                                        }
                                        else
                                        {
                                            GUILayout.Space(entryWidth);
                                        }

                                        if (col < columns - 1)
                                        {
                                            GUILayout.Space(columnSpacing);
                                        }
                                    }
                                }
                            }
                        }
                    }
                    return true;
                }

                bool DrawClipSection(string title, IEnumerable<AnimationClip> clips)
                {
                    var ordered = clips.Where(c => c != null).Distinct().OrderBy(c => c.name, StringComparer.OrdinalIgnoreCase).ToList();
                    if (ordered.Count == 0)
                        return false;

                    EditorGUILayout.Space(8);
                    using (new EditorGUILayout.VerticalScope("box"))
                    {
                        EditorGUILayout.LabelField(title, EditorStyles.boldLabel);
                        for (int i = 0; i < ordered.Count; i += columns)
                        {
                            using var h = new EditorGUILayout.HorizontalScope();
                            GUILayout.Space(innerIndent);
                            for (int col = 0; col < columns; col++)
                            {
                                var index = i + col;
                                if (index < ordered.Count)
                                {
                                    EditorGUILayout.ObjectField(ordered[index], typeof(AnimationClip), false, GUILayout.Width(entryWidth));
                                }
                                else
                                {
                                    GUILayout.Space(entryWidth);
                                }

                                if (col < columns - 1)
                                {
                                    GUILayout.Space(columnSpacing);
                                }
                            }
                        }
                    }
                    return true;
                }

                DrawMenuUsageSection();
                DrawStateUsageSection("States Using Parameter in Transition Conditions (In/Out)", s => s.transitionIn || s.transitionOut);
                DrawStateUsageSection("States Using Parameter in Blend Trees", s => s.blendTree);
                DrawStateUsageSection("States Using Parameter as Motion Time", s => s.motionTime);
                DrawStateUsageSection("States Using Parameter in Parameter Drivers", s => s.parameterDriver);

                var allAffected = cachedScanResult.transitionClips
                    .Concat(cachedScanResult.blendTreeClips)
                    .Concat(cachedScanResult.motionTimeClips)
                    .Where(c => c != null)
                    .Distinct()
                    .OrderBy(c => c.name, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                DrawClipSection("Affected Animation Clips", allAffected);
            }
        }

        [MenuItem("Tools/d4rkpl4y3r/AV3 Toggle Util/Parameter Inspector")]
        public static void AV3ParameterInspectorMenuItem()
        {
            var window = GetWindow<AV3ParameterInspector>();
            window.titleContent = new GUIContent("d4rk AV3 Parameter Inspector");
            window.Show();
        }

        private void OnSelectionChange()
        {
            forceRescan = true;
            Repaint();
        }
    }
}
#endif