#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;

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
        private string selectedParameter = "";

        private int cachedAvatarId = 0;
        private string cachedParameter = "";
        private ScanResult cachedScanResult;
        private bool forceRescan = true;

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

        private static List<string> GetAllParameterNames(VRCAvatarDescriptor av)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);

            if (av != null && av.expressionParameters != null && av.expressionParameters.parameters != null)
            {
                foreach (var p in av.expressionParameters.parameters)
                {
                    if (p == null || string.IsNullOrEmpty(p.name)) continue;
                    names.Add(p.name);
                }
            }

            foreach (var controller in GetAllControllers(av).Distinct())
            {
                if (controller == null || controller.parameters == null) continue;
                foreach (var p in controller.parameters)
                {
                    if (p == null || string.IsNullOrEmpty(p.name)) continue;
                    names.Add(p.name);
                }
            }

            return names.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
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

        private static IEnumerable<string> GetDriverParameterNamesReflective(StateMachineBehaviour behaviour)
        {
            if (behaviour == null) yield break;

            var behaviourType = behaviour.GetType();
            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            object parametersObj = null;

            var field = behaviourType.GetField("parameters", flags);
            if (field != null)
                parametersObj = field.GetValue(behaviour);

            if (parametersObj == null)
            {
                var property = behaviourType.GetProperty("parameters", flags);
                if (property != null && property.CanRead)
                    parametersObj = property.GetValue(behaviour, null);
            }

            if (!(parametersObj is IEnumerable parameterEnumerable))
                yield break;

            foreach (var p in parameterEnumerable)
            {
                if (p == null) continue;

                var pType = p.GetType();
                string name = null;

                var nameField = pType.GetField("name", flags);
                if (nameField != null)
                    name = nameField.GetValue(p) as string;

                if (string.IsNullOrEmpty(name))
                {
                    var nameProperty = pType.GetProperty("name", flags);
                    if (nameProperty != null && nameProperty.CanRead)
                        name = nameProperty.GetValue(p, null) as string;
                }

                if (!string.IsNullOrEmpty(name))
                    yield return name;
            }
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

                var typeName = behaviour.GetType().Name;
                if (typeName.IndexOf("ParameterDriver", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                foreach (var name in GetDriverParameterNamesReflective(behaviour))
                {
                    if (IsSameParameter(name, parameterName))
                        return true;
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
                                var statePath = string.IsNullOrEmpty(smPath) ? stateName : smPath + "/" + stateName;

                                var usage = new StateUsage
                                {
                                    controller = controller,
                                    controllerName = controller.name,
                                    layerName = layer.name,
                                    statePath = statePath,
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

                                var childPath = string.IsNullOrEmpty(smPath) ? childName : smPath + "/" + childName;
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
                .OrderBy(x => x.controllerName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.layerName, StringComparer.OrdinalIgnoreCase)
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
                }
            }

            if (av == null)
            {
                EditorGUILayout.HelpBox("No VRC Avatar Descriptor found.", MessageType.Warning);
                return;
            }

            var allParameters = GetAllParameterNames(av);
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
                EditorGUILayout.LabelField("Parameters", EditorStyles.boldLabel);

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

                EditorGUILayout.LabelField("Selected Parameter", EditorStyles.boldLabel);
                EditorGUILayout.LabelField(selectedParameter);
                EditorGUILayout.Space(6);

                void DrawMenuUsageSection()
                {
                    EditorGUILayout.LabelField("Sub-Menus / Controls Using Parameter", EditorStyles.boldLabel);
                    var usages = cachedScanResult.menuUsages;

                    if (usages.Count == 0)
                    {
                        EditorGUILayout.HelpBox("No Expression Menu controls reference this parameter.", MessageType.Info);
                        return;
                    }

                    foreach (var usage in usages)
                    {
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            GUILayout.Label("•", GUILayout.Width(12));
                            GUILayout.Label(usage.menuPath + " -> " + usage.controlName + " (" + usage.controlType + ")", GUILayout.ExpandWidth(true));
                        }
                    }
                }

                void DrawStateUsageSection(string title, Func<StateUsage, bool> predicate)
                {
                    EditorGUILayout.Space(8);
                    EditorGUILayout.LabelField(title, EditorStyles.boldLabel);

                    var matches = cachedScanResult.stateUsages.Where(predicate).ToList();
                    if (matches.Count == 0)
                    {
                        EditorGUILayout.LabelField("None");
                        return;
                    }

                    foreach (var usage in matches)
                    {
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            GUILayout.Label("•", GUILayout.Width(12));
                            GUILayout.Label(usage.controllerName + " / " + usage.layerName + " / " + usage.statePath, GUILayout.ExpandWidth(true));
                        }
                    }
                }

                void DrawClipSection(string title, IEnumerable<AnimationClip> clips)
                {
                    EditorGUILayout.Space(8);
                    EditorGUILayout.LabelField(title, EditorStyles.boldLabel);

                    var ordered = clips.Where(c => c != null).Distinct().OrderBy(c => c.name, StringComparer.OrdinalIgnoreCase).ToList();
                    if (ordered.Count == 0)
                    {
                        EditorGUILayout.LabelField("None");
                        return;
                    }

                    for (int i = 0; i < ordered.Count; i++)
                    {
                        EditorGUILayout.ObjectField(ordered[i], typeof(AnimationClip), false);
                    }
                }

                DrawMenuUsageSection();
                DrawStateUsageSection("States Using Parameter in Transition Conditions (In/Out)", s => s.transitionIn || s.transitionOut);
                DrawStateUsageSection("States Using Parameter in Blend Trees", s => s.blendTree);
                DrawStateUsageSection("States Using Parameter as Motion Time", s => s.motionTime);
                DrawStateUsageSection("States Using Parameter in Parameter Drivers", s => s.parameterDriver);

                DrawClipSection("Affected Animation Clips (Transition Matches)", cachedScanResult.transitionClips);
                DrawClipSection("Affected Animation Clips (BlendTree Matches)", cachedScanResult.blendTreeClips);
                DrawClipSection("Affected Animation Clips (Motion Time Matches)", cachedScanResult.motionTimeClips);

                var allAffected = cachedScanResult.transitionClips
                    .Concat(cachedScanResult.blendTreeClips)
                    .Concat(cachedScanResult.motionTimeClips)
                    .Where(c => c != null)
                    .Distinct()
                    .OrderBy(c => c.name, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                DrawClipSection("Affected Animation Clips (All)", allAffected);
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