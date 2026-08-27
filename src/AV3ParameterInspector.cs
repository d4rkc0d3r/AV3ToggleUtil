#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using d4rkpl4y3r.AV3ToggleUtil.Util;
using HarmonyLib;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using VRC.SDK3.Dynamics.Contact.Components;
using VRC.SDK3.Dynamics.PhysBone.Components;

using static d4rkpl4y3r.AV3ToggleUtil.Util.AV3Helper;
using Object = UnityEngine.Object;

namespace d4rkpl4y3r.AV3ToggleUtil
{
    public class AV3ParameterInspector : EditorWindow
    {
        private Vector2 leftScrollPos;
        private Vector2 rightScrollPos;
        private SplitterState splitter = new();
        private ColumnGrid columnGrid = new();
        private bool sortParameters = false;
        internal string selectedParameter = "";

        private int cachedAvatarId = 0;
        private Dictionary<string, ScanResult> scanResultCache = new(StringComparer.Ordinal);
        private HashSet<string> usedParametersCache;
        private (HashSet<string> physBoneParameters, HashSet<string> contactParameters) componentSourceCache;
        private HashSet<string> driverParametersCache;
        private VRCAvatarDescriptor lastFoundAvatarDescriptor;
        private TextFilter parameterFilter = new() { IsRegex = false, SmallButtons = true };
        private ParameterFilterMode filterMode = ParameterFilterMode.All;
        private bool renameMode = false;
        private string renameDraft = "";
        private string renameDraftSource = "";
        private bool showComponentProperties = true;

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

        private enum ParameterFilterMode
        {
            [System.ComponentModel.Description("All")]
            All,
            [System.ComponentModel.Description("PhysBone")]
            PhysBone,
            [System.ComponentModel.Description("Contact")]
            Contact,
            [System.ComponentModel.Description("Unused")]
            Unused,
            [System.ComponentModel.Description("Driver")]
            Driver,
            [System.ComponentModel.Description("Built In")]
            BuiltIn,
            [System.ComponentModel.Description("File")]
            File,
        }

        private static readonly string[] FilterModeLabels = Enumerable
            .Cast<ParameterFilterMode>(Enum.GetValues(typeof(ParameterFilterMode)))
            .Select(mode =>
                mode.GetType().GetField(mode.ToString())
                    ?.GetCustomAttribute<System.ComponentModel.DescriptionAttribute>()?.Description
                ?? mode.ToString())
            .ToArray();

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
            public int layerIndex;
            public string statePath;
            public string stateName;
            public AnimatorState state;
            public AnimatorStateMachine stateMachine;
            public string subStateMachinePath;

            public bool transitionIn;
            public bool transitionOut;
            public bool blendTree;
            public bool motionTime;
            public bool speed;
            public bool mirror;
            public bool cycleOffset;
            public bool parameterDriver;
            public bool playAudio;
        }

        [Serializable]
        private class ScanResult
        {
            public List<MenuUsage> menuUsages = new List<MenuUsage>();
            public List<StateUsage> stateUsages = new List<StateUsage>();

            public HashSet<AnimationClip> transitionClips = new HashSet<AnimationClip>();
            public HashSet<AnimationClip> blendTreeClips = new HashSet<AnimationClip>();
            public HashSet<AnimationClip> motionTimeClips = new HashSet<AnimationClip>();
            public HashSet<AnimationClip> speedClips = new HashSet<AnimationClip>();
            public HashSet<AnimationClip> mirrorClips = new HashSet<AnimationClip>();
            public HashSet<AnimationClip> cycleOffsetClips = new HashSet<AnimationClip>();
            public HashSet<AnimationClip> playAudioClips = new HashSet<AnimationClip>();
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

        private static bool MotionUsesBlendParameter(Motion motion, string parameterName, HashSet<BlendTree> visitedTrees = null)
        {
            if (motion == null) return false;
            if (!(motion is BlendTree tree)) return false;

            if (visitedTrees == null) visitedTrees = new HashSet<BlendTree>();
            if (!visitedTrees.Add(tree)) return false;

            var isSecondParameterUsed = tree.blendType != BlendTreeType.Simple1D && tree.blendType != BlendTreeType.Direct;
            if (IsSameParameter(tree.blendParameter, parameterName) || (isSecondParameterUsed && IsSameParameter(tree.blendParameterY, parameterName)))
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

        private static bool StateUsesSpeedParameter(AnimatorState state, string parameterName)
        {
            if (state == null) return false;
            return state.speedParameterActive && IsSameParameter(state.speedParameter, parameterName);
        }

        private static bool StateUsesMirrorParameter(AnimatorState state, string parameterName)
        {
            if (state == null) return false;
            return state.mirrorParameterActive && IsSameParameter(state.mirrorParameter, parameterName);
        }

        private static bool StateUsesCycleOffsetParameter(AnimatorState state, string parameterName)
        {
            if (state == null) return false;
            return state.cycleOffsetParameterActive && IsSameParameter(state.cycleOffsetParameter, parameterName);
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
                            var parameter = typedDriver.parameters[p];
                            if (IsSameParameter(parameter.name, parameterName))
                                return true;

                            if (parameter.type == VRC.SDKBase.VRC_AvatarParameterDriver.ChangeType.Copy
                                && IsSameParameter(parameter.source, parameterName))
                                return true;
                        }
                    }
                    continue;
                }
            }

            return false;
        }

        private static bool StateUsesPlayAudioParameter(AnimatorState state, string parameterName)
        {
            if (state == null || state.behaviours == null) return false;

            for (int i = 0; i < state.behaviours.Length; i++)
            {
                if (state.behaviours[i] is VRCAnimatorPlayAudio playAudio
                    && playAudio.PlaybackOrder == VRCAnimatorPlayAudio.Order.Parameter
                    && IsSameParameter(playAudio.ParameterName, parameterName))
                    return true;
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

        private static bool RenameTransitionConditions(AnimatorTransitionBase transition, string oldParameter, string newParameter)
        {
            if (transition == null || transition.conditions == null)
                return false;

            var conditions = transition.conditions;
            var changed = false;
            for (int i = 0; i < conditions.Length; i++)
            {
                if (!IsSameParameter(conditions[i].parameter, oldParameter))
                    continue;

                conditions[i].parameter = newParameter;
                changed = true;
            }

            if (changed)
            {
                transition.conditions = conditions;
                EditorUtility.SetDirty(transition);
            }

            return changed;
        }

        private static bool RenameBlendTreeParameters(Motion motion, string oldParameter, string newParameter, HashSet<BlendTree> visitedTrees = null)
        {
            if (!(motion is BlendTree tree))
                return false;

            if (visitedTrees == null)
                visitedTrees = new HashSet<BlendTree>();
            if (!visitedTrees.Add(tree))
                return false;

            var changed = false;
            if (IsSameParameter(tree.blendParameter, oldParameter))
            {
                tree.blendParameter = newParameter;
                changed = true;
            }

            if (IsSameParameter(tree.blendParameterY, oldParameter))
            {
                tree.blendParameterY = newParameter;
                changed = true;
            }

            var children = tree.children;
            var childrenChanged = false;
            for (int i = 0; i < children.Length; i++)
            {
                if (IsSameParameter(children[i].directBlendParameter, oldParameter))
                {
                    children[i].directBlendParameter = newParameter;
                    childrenChanged = true;
                    changed = true;
                }

                if (RenameBlendTreeParameters(children[i].motion, oldParameter, newParameter, visitedTrees))
                    changed = true;
            }

            if (childrenChanged)
                tree.children = children;

            if (changed)
                EditorUtility.SetDirty(tree);

            return changed;
        }

        private static bool RenameInStateMachine(AnimatorStateMachine stateMachine, string oldParameter, string newParameter)
        {
            if (stateMachine == null)
                return false;

            var changed = false;

            var anyStateTransitions = stateMachine.anyStateTransitions;
            if (anyStateTransitions != null)
            {
                for (int i = 0; i < anyStateTransitions.Length; i++)
                {
                    if (RenameTransitionConditions(anyStateTransitions[i], oldParameter, newParameter))
                        changed = true;
                }
            }

            var entryTransitions = stateMachine.entryTransitions;
            if (entryTransitions != null)
            {
                for (int i = 0; i < entryTransitions.Length; i++)
                {
                    if (RenameTransitionConditions(entryTransitions[i], oldParameter, newParameter))
                        changed = true;
                }
            }

            var states = stateMachine.states;
            for (int i = 0; i < states.Length; i++)
            {
                var state = states[i].state;
                if (state == null)
                    continue;

                if (state.transitions != null)
                {
                    for (int t = 0; t < state.transitions.Length; t++)
                    {
                        if (RenameTransitionConditions(state.transitions[t], oldParameter, newParameter))
                            changed = true;
                    }
                }

                if (state.timeParameterActive && IsSameParameter(state.timeParameter, oldParameter))
                {
                    state.timeParameter = newParameter;
                    EditorUtility.SetDirty(state);
                    changed = true;
                }

                if (state.speedParameterActive && IsSameParameter(state.speedParameter, oldParameter))
                {
                    state.speedParameter = newParameter;
                    EditorUtility.SetDirty(state);
                    changed = true;
                }

                if (state.mirrorParameterActive && IsSameParameter(state.mirrorParameter, oldParameter))
                {
                    state.mirrorParameter = newParameter;
                    EditorUtility.SetDirty(state);
                    changed = true;
                }

                if (state.cycleOffsetParameterActive && IsSameParameter(state.cycleOffsetParameter, oldParameter))
                {
                    state.cycleOffsetParameter = newParameter;
                    EditorUtility.SetDirty(state);
                    changed = true;
                }

                if (RenameBlendTreeParameters(state.motion, oldParameter, newParameter))
                    changed = true;

                var stateBehaviours = state.behaviours;
                if (stateBehaviours == null)
                    continue;

                for (int b = 0; b < stateBehaviours.Length; b++)
                {
                    var behaviour = stateBehaviours[b];
                    if (!(behaviour is VRCAvatarParameterDriver driver) || driver.parameters == null)
                        continue;

                    var driverChanged = false;
                    for (int p = 0; p < driver.parameters.Count; p++)
                    {
                        var parameter = driver.parameters[p];

                        if (IsSameParameter(parameter.name, oldParameter))
                        {
                            parameter.name = newParameter;
                            driverChanged = true;
                        }

                        if (parameter.type == VRC.SDKBase.VRC_AvatarParameterDriver.ChangeType.Copy
                            && IsSameParameter(parameter.source, oldParameter))
                        {
                            parameter.source = newParameter;
                            driverChanged = true;
                        }
                    }

                    if (driverChanged)
                    {
                        EditorUtility.SetDirty(driver);
                        changed = true;
                    }
                }

                for (int b = 0; b < stateBehaviours.Length; b++)
                {
                    if (stateBehaviours[b] is VRCAnimatorPlayAudio playAudio
                        && playAudio.PlaybackOrder == VRCAnimatorPlayAudio.Order.Parameter
                        && IsSameParameter(playAudio.ParameterName, oldParameter))
                    {
                        playAudio.ParameterName = newParameter;
                        EditorUtility.SetDirty(playAudio);
                        changed = true;
                    }
                }
            }

            var childStateMachines = stateMachine.stateMachines;
            for (int i = 0; i < childStateMachines.Length; i++)
            {
                if (RenameInStateMachine(childStateMachines[i].stateMachine, oldParameter, newParameter))
                    changed = true;
            }

            return changed;
        }

        private static bool RenameInAnimatorController(AnimatorController controller, string oldParameter, string newParameter)
        {
            if (controller == null)
                return false;

            var changed = false;

            using var controllerSerialized = new SerializedObject(controller);
            var controllerParameters = controllerSerialized.FindProperty("m_AnimatorParameters");
            var parametersChanged = false;
            for (int i = 0; i < controllerParameters.arraySize; i++)
            {
                var parameterName = controllerParameters.GetArrayElementAtIndex(i).FindPropertyRelative("m_Name");
                if (!IsSameParameter(parameterName.stringValue, oldParameter))
                    continue;

                parameterName.stringValue = newParameter;
                parametersChanged = true;
            }

            if (parametersChanged)
            {
                controllerSerialized.ApplyModifiedProperties();
                changed = true;
            }

            if (controller.layers != null)
            {
                for (int i = 0; i < controller.layers.Length; i++)
                {
                    if (RenameInStateMachine(controller.layers[i].stateMachine, oldParameter, newParameter))
                        changed = true;
                }
            }

            if (changed)
                EditorUtility.SetDirty(controller);

            return changed;
        }

        private static bool RenameInExpressionParameters(VRCAvatarDescriptor av, string oldParameter, string newParameter)
        {
            var expressionParameters = av != null ? av.expressionParameters : null;
            if (expressionParameters == null || expressionParameters.parameters == null)
                return false;

            var changed = false;
            for (int i = 0; i < expressionParameters.parameters.Length; i++)
            {
                var parameter = expressionParameters.parameters[i];
                if (parameter == null || !IsSameParameter(parameter.name, oldParameter))
                    continue;

                parameter.name = newParameter;
                changed = true;
            }

            if (changed)
                EditorUtility.SetDirty(expressionParameters);

            return changed;
        }

        private static bool RenameInExpressionMenus(VRCAvatarDescriptor av, string oldParameter, string newParameter)
        {
            var rootMenu = av != null ? av.expressionsMenu : null;
            if (rootMenu == null)
                return false;

            var changed = false;
            var visitedMenus = new HashSet<VRCExpressionsMenu>();

            void Traverse(VRCExpressionsMenu menu)
            {
                if (menu == null || !visitedMenus.Add(menu))
                    return;

                var menuChanged = false;
                var controls = menu.controls;
                if (controls != null)
                {
                    for (int i = 0; i < controls.Count; i++)
                    {
                        var control = controls[i];
                        if (control == null)
                            continue;

                        if (control.parameter != null && IsSameParameter(control.parameter.name, oldParameter))
                        {
                            control.parameter.name = newParameter;
                            menuChanged = true;
                        }

                        if (control.subParameters != null)
                        {
                            for (int s = 0; s < control.subParameters.Length; s++)
                            {
                                var subParameter = control.subParameters[s];
                                if (subParameter == null || !IsSameParameter(subParameter.name, oldParameter))
                                    continue;

                                subParameter.name = newParameter;
                                menuChanged = true;
                            }
                        }

                        if (control.type == VRCExpressionsMenu.Control.ControlType.SubMenu && control.subMenu != null)
                            Traverse(control.subMenu);
                    }
                }

                if (menuChanged)
                {
                    EditorUtility.SetDirty(menu);
                    changed = true;
                }
            }

            Traverse(rootMenu);
            return changed;
        }

        private static bool RenameInComponentParameters(VRCAvatarDescriptor av, string oldParameter, string newParameter)
        {
            if (av == null || av.transform == null)
                return false;

            var changed = false;
            var root = av.transform;

            foreach (var physBone in root.GetComponentsInChildren<VRCPhysBone>(true))
            {
                if (physBone == null || string.IsNullOrEmpty(physBone.parameter))
                    continue;

                if (IsSameParameter(physBone.parameter, oldParameter))
                {
                    physBone.parameter = newParameter;
                    EditorUtility.SetDirty(physBone);
                    changed = true;
                    continue;
                }

                if (!IsParameterWrittenByPhysBone(oldParameter, physBone.parameter))
                    continue;

                var oldSuffix = oldParameter.Substring(physBone.parameter.Length);
                var nextBaseParameter = newParameter;
                if (!string.IsNullOrEmpty(oldSuffix)
                    && newParameter.Length > oldSuffix.Length
                    && newParameter.EndsWith(oldSuffix, StringComparison.Ordinal))
                {
                    nextBaseParameter = newParameter.Substring(0, newParameter.Length - oldSuffix.Length);
                }

                if (IsSameParameter(physBone.parameter, nextBaseParameter))
                    continue;

                physBone.parameter = nextBaseParameter;
                EditorUtility.SetDirty(physBone);
                changed = true;
            }

            foreach (var contactReceiver in root.GetComponentsInChildren<VRCContactReceiver>(true))
            {
                if (contactReceiver == null || string.IsNullOrEmpty(contactReceiver.parameter))
                    continue;

                if (!IsSameParameter(contactReceiver.parameter, oldParameter))
                    continue;

                contactReceiver.parameter = newParameter;
                EditorUtility.SetDirty(contactReceiver);
                changed = true;
            }

            return changed;
        }

        private bool ApplyParameterRename(VRCAvatarDescriptor av, string oldParameter, string newParameter)
        {
            if (av == null || string.IsNullOrEmpty(oldParameter) || string.IsNullOrEmpty(newParameter) || IsSameParameter(oldParameter, newParameter))
                return false;

            var changed = false;
            changed |= RenameInExpressionParameters(av, oldParameter, newParameter);
            changed |= RenameInExpressionMenus(av, oldParameter, newParameter);
            changed |= RenameInComponentParameters(av, oldParameter, newParameter);

            foreach (var controller in GetAllControllers(av).Distinct())
            {
                if (RenameInAnimatorController(controller, oldParameter, newParameter))
                    changed = true;
            }

            if (!changed)
                return false;

            EditorUtility.SetDirty(av);
            ResetCaches();
            return true;
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
                                    layerIndex = layerIndex,
                                    statePath = statePath,
                                    stateName = stateName,
                                    state = state,
                                    stateMachine = sm,
                                    subStateMachinePath = smPath,
                                };

                                usage.transitionOut = state.transitions != null && state.transitions.Any(t => TransitionUsesParameter(t, parameterName));
                                usage.transitionIn = HasIncomingTransitionUsingParameter(sm, state, parameterName);

                                if (usage.transitionIn || usage.transitionOut)
                                    ForEachClipInMotion(state.motion, c => result.transitionClips.Add(c));

                                usage.blendTree = MotionUsesBlendParameter(state.motion, parameterName);
                                if (usage.blendTree)
                                    ForEachClipInMotion(state.motion, c => result.blendTreeClips.Add(c));

                                usage.motionTime = StateUsesMotionTimeParameter(state, parameterName);
                                if (usage.motionTime)
                                    ForEachClipInMotion(state.motion, c => result.motionTimeClips.Add(c));

                                usage.speed = StateUsesSpeedParameter(state, parameterName);
                                if (usage.speed)
                                    ForEachClipInMotion(state.motion, c => result.speedClips.Add(c));

                                usage.mirror = StateUsesMirrorParameter(state, parameterName);
                                if (usage.mirror)
                                    ForEachClipInMotion(state.motion, c => result.mirrorClips.Add(c));

                                usage.cycleOffset = StateUsesCycleOffsetParameter(state, parameterName);
                                if (usage.cycleOffset)
                                    ForEachClipInMotion(state.motion, c => result.cycleOffsetClips.Add(c));

                                usage.parameterDriver = StateUsesParameterDriver(state, parameterName);

                                usage.playAudio = StateUsesPlayAudioParameter(state, parameterName);
                                if (usage.playAudio)
                                    ForEachClipInMotion(state.motion, c => result.playAudioClips.Add(c));

                                if (usage.transitionIn || usage.transitionOut || usage.blendTree || usage.motionTime || usage.speed || usage.mirror || usage.cycleOffset || usage.parameterDriver || usage.playAudio)
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

        private void ResetCaches()
        {
            scanResultCache.Clear();
            usedParametersCache = null;
            driverParametersCache = null;
            componentSourceCache = default;
        }

        private void EnsureParametersScanned(VRCAvatarDescriptor av, List<string> allParameters)
        {
            var missingParameters = allParameters.Where(p => !scanResultCache.ContainsKey(p)).ToList();
            if (missingParameters.Count == 0)
                return;

            if (missingParameters.Count > 3)
            {
                try
                {
                    for (int i = 0; i < missingParameters.Count; i++)
                    {
                        var parameter = missingParameters[i];
                        EditorUtility.DisplayProgressBar("Scanning Parameters", $"{i + 1}/{missingParameters.Count} {parameter}", (i + 1f) / missingParameters.Count);
                        scanResultCache[parameter] = BuildScanResult(av, parameter);
                    }
                }
                finally
                {
                    EditorUtility.ClearProgressBar();
                }
            }
            else
            {
                foreach (var parameter in missingParameters)
                    scanResultCache[parameter] = BuildScanResult(av, parameter);
            }
        }

        private HashSet<string> GetParametersMatching(VRCAvatarDescriptor av, List<string> allParameters, Func<ScanResult, bool> predicate, ref HashSet<string> cache)
        {
            if (cache == null)
            {
                EnsureParametersScanned(av, allParameters);

                var result = new HashSet<string>(StringComparer.Ordinal);
                foreach (var parameter in allParameters)
                {
                    if (predicate(scanResultCache[parameter]))
                        result.Add(parameter);
                }

                cache = result;
            }

            return cache;
        }

        private HashSet<string> GetUsedParameters(VRCAvatarDescriptor av, List<string> allParameters)
        {
            return GetParametersMatching(av, allParameters, r => r.stateUsages.Count > 0, ref usedParametersCache);
        }

        private (HashSet<string> physBoneParameters, HashSet<string> contactParameters) GetComponentSourceParameters(VRCAvatarDescriptor av, List<string> allParameters)
        {
            if (componentSourceCache.physBoneParameters == null)
            {
                var physBoneParameters = new HashSet<string>(StringComparer.Ordinal);
                var contactParameters = new HashSet<string>(StringComparer.Ordinal);

                if (av.transform != null)
                {
                    var root = av.transform;
                    var physBoneConfiguredParameters = new List<string>();
                    var contactConfiguredParameters = new List<string>();

                    foreach (var physBone in root.GetComponentsInChildren<VRCPhysBone>(true))
                    {
                        if (physBone != null && !string.IsNullOrEmpty(physBone.parameter))
                            physBoneConfiguredParameters.Add(physBone.parameter);
                    }

                    foreach (var contactReceiver in root.GetComponentsInChildren<VRCContactReceiver>(true))
                    {
                        if (contactReceiver != null && !string.IsNullOrEmpty(contactReceiver.parameter))
                            contactConfiguredParameters.Add(contactReceiver.parameter);
                    }

                    foreach (var parameter in allParameters)
                    {
                        foreach (var configuredParameter in physBoneConfiguredParameters)
                        {
                            if (IsParameterWrittenByPhysBone(parameter, configuredParameter))
                            {
                                physBoneParameters.Add(parameter);
                                break;
                            }
                        }

                        foreach (var configuredParameter in contactConfiguredParameters)
                        {
                            if (IsSameParameter(parameter, configuredParameter))
                            {
                                contactParameters.Add(parameter);
                                break;
                            }
                        }
                    }
                }

                componentSourceCache = (physBoneParameters, contactParameters);
            }

            return componentSourceCache;
        }

        private HashSet<string> GetDriverParameters(VRCAvatarDescriptor av, List<string> allParameters)
        {
            return GetParametersMatching(av, allParameters, r => r.stateUsages.Any(s => s.parameterDriver), ref driverParametersCache);
        }

        private static HashSet<string> GetExpressionFileParameters(VRCAvatarDescriptor av)
        {
            var expressionParams = av.expressionParameters?.parameters;
            if (expressionParams == null)
                return null;

            return new HashSet<string>(
                expressionParams.Where(p => p != null && !string.IsNullOrEmpty(p.name)).Select(p => p.name),
                StringComparer.Ordinal);
        }

        private (HashSet<string> included, HashSet<string> excluded) GetFilterParameterSets(VRCAvatarDescriptor av, List<string> allParameters)
        {
            switch (filterMode)
            {
                case ParameterFilterMode.Unused:
                    return (null, GetUsedParameters(av, allParameters));
                case ParameterFilterMode.PhysBone:
                    return (GetComponentSourceParameters(av, allParameters).physBoneParameters, null);
                case ParameterFilterMode.Contact:
                    return (GetComponentSourceParameters(av, allParameters).contactParameters, null);
                case ParameterFilterMode.Driver:
                    return (GetDriverParameters(av, allParameters), null);
                case ParameterFilterMode.BuiltIn:
                    return (VRChatBuiltInParameters, null);
                case ParameterFilterMode.File:
                    return (GetExpressionFileParameters(av), null);
                default:
                    return (null, null);
            }
        }

        private VRCAvatarDescriptor GetCurrentOrLastAvatarDescriptor()
        {
            var selectedAvatarDescriptor = FindAvatarDescriptor(Selection.activeGameObject);
            if (selectedAvatarDescriptor != null)
                lastFoundAvatarDescriptor = selectedAvatarDescriptor;
            return lastFoundAvatarDescriptor;
        }

        private static void FocusAnimatorState(StateUsage usage)
        {
            if (usage == null || usage.controller == null || usage.state == null)
                return;

            FocusAnimatorLayer(usage);
            SelectAndPingObject(usage.state);
        }

        private static void FocusAnimatorLayer(StateUsage usage)
        {
            if (usage == null || usage.controller == null)
                return;

            var animatorWindowType = Type.GetType("UnityEditor.Graphs.AnimatorControllerTool, UnityEditor.Graphs");
            if (animatorWindowType != null)
            {
                var animatorWindow = GetWindow(animatorWindowType);
                if (animatorWindow != null)
                {
                    animatorWindow.Show();
                    animatorWindow.Focus();
                    Traverse.Create(animatorWindow).Property("animatorController").SetValue(usage.controller);
                    Traverse.Create(animatorWindow).Property("selectedLayerIndex").SetValue(usage.layerIndex);
                    animatorWindow.Repaint();
                }
            }
        }

        private void OnGUI()
        {
            var av = GetCurrentOrLastAvatarDescriptor();

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
            }

            if (!IsSameParameter(renameDraftSource, selectedParameter))
            {
                renameDraftSource = selectedParameter;
                renameDraft = selectedParameter;
            }

            var avatarId = av.GetInstanceID();
            if (avatarId != cachedAvatarId)
            {
                cachedAvatarId = avatarId;
                ResetCaches();
            }

            if (!scanResultCache.TryGetValue(selectedParameter, out var cachedScanResult))
            {
                cachedScanResult = BuildScanResult(av, selectedParameter);
                scanResultCache[selectedParameter] = cachedScanResult;
            }

            using var horizontal = new EditorGUILayout.HorizontalScope();

            using (new EditorGUILayout.VerticalScope(GUILayout.Width(splitter.leftPanelWidth)))
            {
                var (includedParameters, excludedParameters) = GetFilterParameterSets(av, allParameters);

                var filteredParameters = allParameters
                    .Where(p => parameterFilter.Matches(p))
                    .Where(p => (includedParameters == null || includedParameters.Contains(p))
                              && (excludedParameters == null || !excludedParameters.Contains(p)))
                    .ToList();
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label($"Parameters ({filteredParameters.Count}/{allParameters.Count})", EditorStyles.boldLabel);
                    sortParameters = GUILayout.Toggle(sortParameters, new GUIContent("A→Z", "Sort alphabetically"), GUI.skin.button, GUILayout.ExpandWidth(false));
                }
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label("Filter");
                    GUILayout.FlexibleSpace();
                    filterMode = (ParameterFilterMode)EditorGUILayout.Popup((int)filterMode, FilterModeLabels, GUILayout.Width(80));
                }
                parameterFilter.DrawGUI();
                using var leftScroll = new EditorGUILayout.ScrollViewScope(leftScrollPos);
                leftScrollPos = leftScroll.scrollPosition;

                for (int i = 0; i < filteredParameters.Count; i++)
                {
                    var parameter = filteredParameters[i];
                    using var cc = new EditorGUI.ChangeCheckScope();
                    var selected = GUILayout.Toggle(IsSameParameter(selectedParameter, parameter), parameter, GUI.skin.button, GUILayout.ExpandWidth(true));
                    if (cc.changed && selected && !IsSameParameter(selectedParameter, parameter))
                    {
                        selectedParameter = parameter;
                        renameMode = false;
                        GUI.FocusControl(null);
                    }
                }
            }

            splitter.DrawSplitter(this, 160f, 520f);

            using (new EditorGUILayout.VerticalScope())
            {
                using var rightScroll = new EditorGUILayout.ScrollViewScope(rightScrollPos);
                rightScrollPos = rightScroll.scrollPosition;

                var rightPanelWidth = Mathf.Max(200f, position.width - splitter.leftPanelWidth - SplitterState.SplitterWidth - 30f);
                columnGrid.Recalculate(rightPanelWidth);
                const float innerIndent = ColumnGrid.InnerIndent;
                var entryWidth = columnGrid.entryWidth;

                var isBuiltInParameter = VRChatBuiltInParameters.Contains(selectedParameter);
                if (isBuiltInParameter)
                    renameMode = false;

                using (new EditorGUILayout.VerticalScope("box"))
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        using (new EditorGUI.DisabledScope(isBuiltInParameter))
                        {
                            using var cc = new EditorGUI.ChangeCheckScope();
                            renameMode = GUILayout.Toggle(renameMode, "Rename", GUI.skin.button, GUILayout.Width(100f));
                            if (cc.changed && renameMode)
                            {
                                renameDraftSource = selectedParameter;
                                renameDraft = selectedParameter;
                                GUI.FocusControl(null);
                            }
                        }
                    }

                    if (renameMode)
                    {
                        using var renameHorizontal = new EditorGUILayout.HorizontalScope();
                        GUILayout.Space(innerIndent);
                        renameDraft = EditorGUILayout.TextField(renameDraft);
                        using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(renameDraft) || IsSameParameter(renameDraft, selectedParameter)))
                        {
                            if (GUILayout.Button("Apply", GUILayout.Width(100f)))
                            {
                                if (ApplyParameterRename(av, selectedParameter, renameDraft))
                                {
                                    selectedParameter = renameDraft;
                                    renameDraftSource = selectedParameter;
                                    renameDraft = selectedParameter;
                                    renameMode = false;
                                    GUI.FocusControl(null);
                                    Repaint();
                                    return;
                                }
                            }
                        }
                    }
                }
                
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
                            SelectTransformByPathFromLastRect(av.transform, componentWriters[0].path);
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
                            GUILayout.Label("Set by PhysBones & Contact Receivers");
                        }

                        foreach (var writer in componentWriters)
                        {
                            using var writerRow = new EditorGUILayout.HorizontalScope();
                            GUILayout.Space(innerIndent + 15);
                            GUILayout.Label($"{writer.sourceType}: '{writer.path}'");
                            SelectTransformByPathFromLastRect(av.transform, writer.path);
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
                        EditorGUILayout.LabelField("Sub-Menus & Controls Using Parameter", EditorStyles.boldLabel);

                        var grouped = usages
                            .GroupBy(x => x.menuPath)
                            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                            .ToList();

                        foreach (var group in grouped)
                        {
                            var representative = group.FirstOrDefault();
                            var groupedMenu = representative?.menu;
                            using (new EditorGUILayout.HorizontalScope())
                            {
                                GUILayout.Space(15);
                                GUILayout.Label(group.Key, EditorStyles.boldLabel, GUILayout.ExpandWidth(true));
                                if (ClickableLastRect())
                                    SelectAndPingObject(groupedMenu);
                            }

                            var controls = group
                                .Select(x => new { x.controlName, x.controlType })
                                .Distinct()
                                .OrderBy(x => x.controlName, StringComparer.OrdinalIgnoreCase)
                                .ToList();

                            columnGrid.DrawEntries(controls, control =>
                            {
                                GUILayout.Label($"{control.controlName}   ({control.controlType})", GUILayout.Width(entryWidth));
                                if (ClickableLastRect())
                                    SelectAndPingObject(groupedMenu);
                            });
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
                            .GroupBy(x => new { x.controller, x.layerIndex, x.subStateMachinePath })
                            .OrderBy(g => g.First().statePath, StringComparer.OrdinalIgnoreCase)
                            .ToList();

                        foreach (var group in grouped)
                        {
                            var representative = group.First();
                            using (new EditorGUILayout.HorizontalScope())
                            {
                                GUILayout.Space(15);
                                GUILayout.Label(representative.statePath, EditorStyles.boldLabel, GUILayout.ExpandWidth(true));
                                if (ClickableLastRect())
                                    FocusAnimatorLayer(representative);
                            }

                            var states = group
                                .Where(x => x.state != null)
                                .GroupBy(x => x.state)
                                .Select(g => g.First())
                                .OrderBy(x => x.stateName, StringComparer.OrdinalIgnoreCase)
                                .ToList();

                            columnGrid.DrawEntries(states, stateUsage =>
                            {
                                GUILayout.Label(stateUsage.stateName, GUILayout.Width(entryWidth));
                                if (ClickableLastRect())
                                    FocusAnimatorState(stateUsage);
                            });
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
                        columnGrid.DrawEntries(ordered, clip =>
                        {
                            EditorGUILayout.ObjectField(clip, typeof(AnimationClip), false, GUILayout.Width(entryWidth));
                        });
                    }
                    return true;
                }

                DrawMenuUsageSection();
                DrawStateUsageSection("States Using Parameter in Transition Conditions (In/Out)", s => s.transitionIn || s.transitionOut);
                DrawStateUsageSection("States Using Parameter in Blend Trees", s => s.blendTree);
                DrawStateUsageSection("States Using Parameter as Motion Time", s => s.motionTime);
                DrawStateUsageSection("States Using Parameter as Speed", s => s.speed);
                DrawStateUsageSection("States Using Parameter as Mirror", s => s.mirror);
                DrawStateUsageSection("States Using Parameter as Cycle Offset", s => s.cycleOffset);
                DrawStateUsageSection("States Using Parameter in Parameter Drivers", s => s.parameterDriver);
                DrawStateUsageSection("States Using Parameter in Play Audio Scripts", s => s.playAudio);

                var allAffected = cachedScanResult.transitionClips
                    .Concat(cachedScanResult.blendTreeClips)
                    .Concat(cachedScanResult.motionTimeClips)
                    .Concat(cachedScanResult.speedClips)
                    .Concat(cachedScanResult.mirrorClips)
                    .Concat(cachedScanResult.cycleOffsetClips)
                    .Concat(cachedScanResult.playAudioClips)
                    .Where(c => c != null)
                    .Distinct()
                    .OrderBy(c => c.name, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                DrawClipSection("Affected Animation Clips", allAffected);

                var componentPropertyMap = new Dictionary<Component, HashSet<string>>();
                CollectComponentBindings(allAffected, av, componentPropertyMap);
                DrawComponentBindingsSection(componentPropertyMap, ref showComponentProperties, columnGrid, entryWidth);
            }
        }

        [MenuItem("Tools/d4rkpl4y3r/AV3 Toggle Util/Parameter Inspector")]
        public static void AV3ParameterInspectorMenuItem()
        {
            var window = GetWindow<AV3ParameterInspector>();
            window.titleContent = new GUIContent("d4rk Parameter Inspector");
            window.Show();
        }

        public static AV3ParameterInspector OpenWithParameter(string parameterName)
        {
            var window = GetWindow<AV3ParameterInspector>();
            window.titleContent = new GUIContent("d4rk Parameter Inspector");
            if (!string.IsNullOrEmpty(parameterName))
                window.selectedParameter = parameterName;
            window.Show();
            return window;
        }

        private void OnSelectionChange()
        {
            Repaint();
        }
    }
}
#endif