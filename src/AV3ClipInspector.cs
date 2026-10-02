#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using d4rkpl4y3r.AV3ToggleUtil.Util;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace d4rkpl4y3r.AV3ToggleUtil
{
    public class AV3ClipInspector : EditorWindow
    {
        private Vector2 leftScrollPos;
        private Vector2 rightScrollPos;
        private SplitterState splitter = new();

        private bool showUnusedClips = true;
        private HashSet<string> selectedControllerPaths = new(StringComparer.Ordinal);
        private TextFilter controllerFilter = new() { IsRegex = false, SmallButtons = true };
        private TextFilter clipFilter = new() { IsRegex = false, SmallButtons = true };

        private List<AnimationClip> cachedClips = new();
        private List<AnimatorController> cachedControllers = new();
        private Dictionary<AnimationClip, string> clipPaths = new();
        private Dictionary<AnimatorController, string> controllerPaths = new();
        private FolderNode clipTree = new() { name = "", path = "" };
        private ControllerNode controllerTree = new() { name = "", path = "" };
        private HashSet<string> expandedFolders = new(StringComparer.Ordinal);
        private HashSet<string> expandedControllerFolders = new(StringComparer.Ordinal);
        private bool splitterInitialized = false;

        private HashSet<AnimationClip> usedClipsCache;
        private int usedClipsCacheSelectionCount = -1;

        private static Texture FolderIcon;
        private static Texture FolderOpenedIcon;
        private static Texture ClipIcon;

        private class FolderNode
        {
            public string name;
            public string path;
            public readonly List<AnimationClip> clips = new();
            public readonly Dictionary<string, FolderNode> children = new(StringComparer.Ordinal);
            public int count;
        }

        private class ControllerNode
        {
            public string name;
            public string path;
            public readonly List<AnimatorController> controllers = new();
            public readonly Dictionary<string, ControllerNode> children = new(StringComparer.Ordinal);
            public int count;
        }

        [MenuItem("Tools/d4rkpl4y3r/AV3 Toggle Util/Clip Inspector")]
        public static void AV3ClipInspectorMenuItem()
        {
            var window = GetWindow<AV3ClipInspector>();
            window.titleContent = new GUIContent("d4rk Clip Inspector");
            window.Show();
        }

        private void OnEnable()
        {
            if (FolderIcon == null)
            {
                FolderIcon = EditorGUIUtility.IconContent("d_Folder Icon").image;
                FolderOpenedIcon = EditorGUIUtility.IconContent("d_FolderOpened Icon").image;
                ClipIcon = EditorGUIUtility.IconContent("d_AnimationClip Icon").image;
            }

            ScanProject();
        }

        private void OnGUI()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label($"Clips ({cachedClips.Count})  Controllers ({cachedControllers.Count})", EditorStyles.boldLabel);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Refresh", GUILayout.Width(80f)))
                    ScanProject();
            }

            if (!splitterInitialized)
            {
                splitter.leftPanelWidth = Mathf.Clamp(position.width / 2f, 160f, Mathf.Max(160f, position.width - 200f));
                splitterInitialized = true;
            }

            using var horizontal = new EditorGUILayout.HorizontalScope();

            using (new EditorGUILayout.VerticalScope(GUILayout.Width(splitter.leftPanelWidth)))
            {
                var filteredControllerCount = 0;
                for (int i = 0; i < cachedControllers.Count; i++)
                {
                    if (controllerFilter.Matches(cachedControllers[i].name))
                        filteredControllerCount++;
                }

                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label($"Controllers ({filteredControllerCount}/{cachedControllers.Count})", EditorStyles.boldLabel);
                }
                controllerFilter.DrawGUI();

                using var leftScroll = new EditorGUILayout.ScrollViewScope(leftScrollPos);
                leftScrollPos = leftScroll.scrollPosition;

                DrawAllControllersEntry();

                DrawControllerTree(controllerTree, 0);
            }

            splitter.DrawSplitter(this, 160f, Mathf.Max(160f, position.width - 200f));

            using (new EditorGUILayout.VerticalScope())
            {
                var usedClips = GetUsedClips();

                var visibleClipCount = 0;
                for (int i = 0; i < cachedClips.Count; i++)
                {
                    var clip = cachedClips[i];
                    var isUsed = usedClips.Contains(clip);
                    if (showUnusedClips ? !isUsed : isUsed)
                        visibleClipCount++;
                }

                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label(
                        showUnusedClips
                            ? $"Unused Clips ({visibleClipCount}/{cachedClips.Count})"
                            : $"Used Clips ({visibleClipCount}/{cachedClips.Count})",
                        EditorStyles.boldLabel);
                    showUnusedClips = GUILayout.Toggle(showUnusedClips, "Unused", GUI.skin.button, GUILayout.ExpandWidth(false));
                }

                clipFilter.DrawGUI();

                using var rightScroll = new EditorGUILayout.ScrollViewScope(rightScrollPos);
                rightScrollPos = rightScroll.scrollPosition;

                if (clipTree.children.Count == 0 && clipTree.clips.Count == 0)
                {
                    EditorGUILayout.HelpBox("No clips found.", MessageType.Info);
                }
                else
                {
                    DrawFolderTree(clipTree, 0, usedClips);
                }
            }
        }

        private void DrawAllControllersEntry()
        {
            var allSelected = cachedControllers.Count > 0 && selectedControllerPaths.Count == cachedControllers.Count;
            using var cc = new EditorGUI.ChangeCheckScope();
            var selected = GUILayout.Toggle(allSelected, $"All Controllers ({cachedControllers.Count})", GUI.skin.button, GUILayout.ExpandWidth(true));
            if (cc.changed)
            {
                selectedControllerPaths.Clear();
                if (selected)
                {
                    foreach (var controller in cachedControllers)
                        selectedControllerPaths.Add(controllerPaths[controller]);
                }
                InvalidateUsedClipsCache();
            }
        }

        private void DrawControllerEntry(string path, string label)
        {
            using var cc = new EditorGUI.ChangeCheckScope();
            var selected = GUILayout.Toggle(selectedControllerPaths.Contains(path), label, GUI.skin.button, GUILayout.ExpandWidth(true));
            if (cc.changed)
            {
                if (selected)
                    selectedControllerPaths.Add(path);
                else
                    selectedControllerPaths.Remove(path);
                InvalidateUsedClipsCache();
            }
        }

        private void BuildControllerTree()
        {
            controllerTree = new ControllerNode { name = "", path = "" };

            foreach (var controller in cachedControllers)
            {
                var path = controllerPaths[controller];
                var folder = System.IO.Path.GetDirectoryName(path)?.Replace('\\', '/');

                var current = controllerTree;
                if (!string.IsNullOrEmpty(folder))
                {
                    foreach (var part in folder.Split('/'))
                    {
                        if (string.IsNullOrEmpty(part))
                            continue;
                        if (!current.children.TryGetValue(part, out var child))
                        {
                            child = new ControllerNode { name = part, path = string.IsNullOrEmpty(current.path) ? part : current.path + "/" + part };
                            current.children[part] = child;
                        }
                        current = child;
                    }
                }

                current.controllers.Add(controller);
            }

            PruneControllerTree(controllerTree);
        }

        private static void PruneControllerTree(ControllerNode node)
        {
            foreach (var child in node.children.Values.ToList())
            {
                PruneControllerTree(child);
                if (child.controllers.Count == 0 && child.children.Count == 0)
                    node.children.Remove(child.name);
            }

            node.count = node.controllers.Count + node.children.Values.Sum(c => c.count);
        }

        private void DrawControllerTree(ControllerNode node, int depth)
        {
            foreach (var child in node.children.Values)
            {
                if (!controllerFilter.Matches(child.name))
                    continue;

                var wasExpanded = expandedControllerFolders.Contains(child.path);
                bool expanded;
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Space(depth * 15f);
                    expanded = EditorGUILayout.Foldout(wasExpanded,
                        new GUIContent($"{child.name} ({CountVisibleControllers(child)})", wasExpanded ? FolderOpenedIcon : FolderIcon), true);
                }
                if (expanded != wasExpanded)
                {
                    if (expanded)
                    {
                        expandedControllerFolders.Add(child.path);
                        ExpandSingleChildChain(child);
                    }
                    else
                        expandedControllerFolders.Remove(child.path);
                }

                if (expanded)
                    DrawControllerTree(child, depth + 1);
            }

            foreach (var controller in node.controllers)
            {
                if (!controllerFilter.Matches(controller.name))
                    continue;

                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Space(depth * 15f);
                    DrawControllerEntry(controllerPaths[controller], controller.name);
                }
            }
        }

        private int CountVisibleControllers(ControllerNode node)
        {
            var count = 0;
            foreach (var controller in node.controllers)
            {
                if (controllerFilter.Matches(controller.name))
                    count++;
            }
            foreach (var child in node.children.Values)
            {
                if (controllerFilter.Matches(child.name))
                    count += CountVisibleControllers(child);
            }
            return count;
        }

        private HashSet<AnimationClip> GetUsedClips()
        {
            if (usedClipsCache != null && usedClipsCacheSelectionCount == selectedControllerPaths.Count)
                return usedClipsCache;

            var usedClips = new HashSet<AnimationClip>();

            foreach (var controller in cachedControllers)
            {
                if (!selectedControllerPaths.Contains(controllerPaths[controller]))
                    continue;

                foreach (var clip in controller.animationClips)
                {
                    if (clip != null)
                        usedClips.Add(clip);
                }
            }

            usedClipsCache = usedClips;
            usedClipsCacheSelectionCount = selectedControllerPaths.Count;
            return usedClips;
        }

        private void InvalidateUsedClipsCache()
        {
            usedClipsCache = null;
            usedClipsCacheSelectionCount = -1;
        }

        private void ScanProject()
        {
            cachedClips.Clear();
            cachedControllers.Clear();

            try
            {
                var clipGuids = AssetDatabase.FindAssets("t:AnimationClip");
                var controllerGuids = AssetDatabase.FindAssets("t:AnimatorController");

                var clipCount = clipGuids.Length;
                for (int i = 0; i < clipCount; i++)
                {
                    if (clipCount > 3)
                        EditorUtility.DisplayProgressBar("Scanning Animation Clips", $"{i + 1}/{clipCount}", (i + 1f) / clipCount);
                    var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(AssetDatabase.GUIDToAssetPath(clipGuids[i]));
                    if (clip != null)
                        cachedClips.Add(clip);
                }

                foreach (var guid in controllerGuids)
                {
                    var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(AssetDatabase.GUIDToAssetPath(guid));
                    if (controller != null)
                        cachedControllers.Add(controller);
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            clipPaths.Clear();
            foreach (var clip in cachedClips)
                clipPaths[clip] = AssetDatabase.GetAssetPath(clip);
            cachedClips = cachedClips
                .OrderBy(c => clipPaths[c], StringComparer.OrdinalIgnoreCase)
                .ToList();

            controllerPaths.Clear();
            foreach (var controller in cachedControllers)
                controllerPaths[controller] = AssetDatabase.GetAssetPath(controller);
            cachedControllers = cachedControllers
                .OrderBy(c => controllerPaths[c], StringComparer.OrdinalIgnoreCase)
                .ToList();

            selectedControllerPaths.Clear();
            foreach (var controller in cachedControllers)
                selectedControllerPaths.Add(controllerPaths[controller]);

            BuildControllerTree();
            RebuildClipTree();
            InvalidateUsedClipsCache();

            Repaint();
        }

        private void RebuildClipTree()
        {
            clipTree = new FolderNode { name = "", path = "" };

            foreach (var clip in cachedClips)
            {
                var path = clipPaths[clip];
                var folder = System.IO.Path.GetDirectoryName(path)?.Replace('\\', '/');

                var current = clipTree;
                if (!string.IsNullOrEmpty(folder))
                {
                    foreach (var part in folder.Split('/'))
                    {
                        if (string.IsNullOrEmpty(part))
                            continue;
                        if (!current.children.TryGetValue(part, out var child))
                        {
                            child = new FolderNode { name = part, path = string.IsNullOrEmpty(current.path) ? part : current.path + "/" + part };
                            current.children[part] = child;
                        }
                        current = child;
                    }
                }

                current.clips.Add(clip);
            }

            PruneFolderTree(clipTree);
        }

        private static void PruneFolderTree(FolderNode node)
        {
            foreach (var child in node.children.Values.ToList())
            {
                PruneFolderTree(child);
                if (child.clips.Count == 0 && child.children.Count == 0)
                    node.children.Remove(child.name);
            }

            node.count = node.clips.Count + node.children.Values.Sum(c => c.count);
        }

        private void ExpandSingleChildChain(FolderNode node)
        {
            if (node.children.Count != 1)
                return;

            var child = node.children.Values.First();
            expandedFolders.Add(child.path);
            ExpandSingleChildChain(child);
        }

        private void ExpandSingleChildChain(ControllerNode node)
        {
            if (node.children.Count != 1)
                return;

            var child = node.children.Values.First();
            expandedControllerFolders.Add(child.path);
            ExpandSingleChildChain(child);
        }

        private void DrawFolderTree(FolderNode node, int depth, HashSet<AnimationClip> usedClips)
        {
            foreach (var child in node.children.Values)
            {
                if (!clipFilter.Matches(child.name))
                    continue;

                var wasExpanded = expandedFolders.Contains(child.path);
                bool expanded;
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Space(depth * 15f);
                    expanded = EditorGUILayout.Foldout(wasExpanded,
                        new GUIContent($"{child.name} ({CountVisibleClips(child, usedClips)})", wasExpanded ? FolderOpenedIcon : FolderIcon), true);
                }
                if (expanded != wasExpanded)
                {
                    if (expanded)
                    {
                        expandedFolders.Add(child.path);
                        ExpandSingleChildChain(child);
                    }
                    else
                        expandedFolders.Remove(child.path);
                }

                if (expanded)
                    DrawFolderTree(child, depth + 1, usedClips);
            }

            foreach (var clip in node.clips)
            {
                if (!clipFilter.Matches(clip.name))
                    continue;

                var isUsed = usedClips.Contains(clip);
                if (showUnusedClips && isUsed)
                    continue;
                if (!showUnusedClips && !isUsed)
                    continue;

                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Space((1 + depth) * 15f);
                    GUILayout.Label(new GUIContent(clip.name, ClipIcon, isUsed ? "Used by selected controllers" : "Not used by any selected controller"),
                        GUILayout.Height(20), GUILayout.ExpandWidth(true));
                }
            }
        }

        private int CountVisibleClips(FolderNode node, HashSet<AnimationClip> usedClips)
        {
            var count = 0;
            foreach (var child in node.children.Values)
            {
                if (clipFilter.Matches(child.name))
                    count += CountVisibleClips(child, usedClips);
            }

            foreach (var clip in node.clips)
            {
                if (!clipFilter.Matches(clip.name))
                    continue;
                var isUsed = usedClips.Contains(clip);
                if (showUnusedClips && isUsed)
                    continue;
                if (!showUnusedClips && !isUsed)
                    continue;
                count++;
            }

            return count;
        }
    }
}
#endif
