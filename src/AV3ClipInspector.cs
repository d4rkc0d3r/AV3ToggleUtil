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
        private const string ShowAllPath = "<all>";

        private Vector2 leftScrollPos;
        private Vector2 rightScrollPos;
        private SplitterState splitter = new();

        private bool showUnusedClips = true;
        private string selectedControllerPath = ShowAllPath;
        private TextFilter controllerFilter = new() { IsRegex = false, SmallButtons = true };
        private TextFilter clipFilter = new() { IsRegex = false, SmallButtons = true };

        private List<AnimationClip> cachedClips = new();
        private List<AnimatorController> cachedControllers = new();
        private HashSet<string> expandedFolders = new(StringComparer.Ordinal);

        private class FolderNode
        {
            public string name;
            public string path;
            public readonly List<AnimationClip> clips = new();
            public readonly Dictionary<string, FolderNode> children = new(StringComparer.Ordinal);
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

            using var horizontal = new EditorGUILayout.HorizontalScope();

            using (new EditorGUILayout.VerticalScope(GUILayout.Width(splitter.leftPanelWidth)))
            {
                var filteredControllers = cachedControllers
                    .Where(c => controllerFilter.Matches(c.name))
                    .OrderBy(c => c.name, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label($"Controllers ({filteredControllers.Count}/{cachedControllers.Count})", EditorStyles.boldLabel);
                }
                controllerFilter.DrawGUI();

                using var leftScroll = new EditorGUILayout.ScrollViewScope(leftScrollPos);
                leftScrollPos = leftScroll.scrollPosition;

                DrawControllerEntry(ShowAllPath, "All Controllers", cachedControllers.Count);

                for (int i = 0; i < filteredControllers.Count; i++)
                {
                    var controller = filteredControllers[i];
                    DrawControllerEntry(AssetDatabase.GetAssetPath(controller), controller.name, 1);
                }
            }

            splitter.DrawSplitter(this, 160f, 520f);

            using (new EditorGUILayout.VerticalScope())
            {
                var usedClips = GetUsedClips();
                var visibleClips = showUnusedClips
                    ? cachedClips.Where(c => !usedClips.Contains(c)).ToList()
                    : cachedClips.Where(c => usedClips.Contains(c)).ToList();

                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label(
                        showUnusedClips
                            ? $"Unused Clips ({visibleClips.Count}/{cachedClips.Count})"
                            : $"Used Clips ({visibleClips.Count}/{cachedClips.Count})",
                        EditorStyles.boldLabel);
                    showUnusedClips = GUILayout.Toggle(showUnusedClips, "Unused", GUI.skin.button, GUILayout.ExpandWidth(false));
                }
                clipFilter.DrawGUI();

                using var rightScroll = new EditorGUILayout.ScrollViewScope(rightScrollPos);
                rightScrollPos = rightScroll.scrollPosition;

                var tree = BuildFolderTree(visibleClips);
                if (tree.children.Count == 0 && tree.clips.Count == 0)
                {
                    EditorGUILayout.HelpBox("No clips found.", MessageType.Info);
                }
                else
                {
                    DrawFolderTree(tree, 0);
                }
            }
        }

        private void DrawControllerEntry(string path, string label, int count)
        {
            using var cc = new EditorGUI.ChangeCheckScope();
            var selected = GUILayout.Toggle(string.Equals(selectedControllerPath, path, StringComparison.Ordinal),
                $"{label} ({count})", GUI.skin.button, GUILayout.ExpandWidth(true));
            if (cc.changed && selected)
                selectedControllerPath = path;
        }

        private HashSet<AnimationClip> GetUsedClips()
        {
            var usedClips = new HashSet<AnimationClip>();

            IEnumerable<AnimatorController> controllers;
            if (string.Equals(selectedControllerPath, ShowAllPath, StringComparison.Ordinal))
            {
                controllers = cachedControllers;
            }
            else
            {
                var controller = cachedControllers.FirstOrDefault(c =>
                    string.Equals(AssetDatabase.GetAssetPath(c), selectedControllerPath, StringComparison.Ordinal));
                controllers = controller != null ? new[] { controller } : Array.Empty<AnimatorController>();
            }

            foreach (var controller in controllers)
            {
                foreach (var clip in controller.animationClips)
                {
                    if (clip != null)
                        usedClips.Add(clip);
                }
            }

            return usedClips;
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

            cachedClips = cachedClips
                .OrderBy(c => AssetDatabase.GetAssetPath(c), StringComparer.OrdinalIgnoreCase)
                .ToList();
            cachedControllers = cachedControllers
                .OrderBy(c => AssetDatabase.GetAssetPath(c), StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (!string.Equals(selectedControllerPath, ShowAllPath, StringComparison.Ordinal)
                && !cachedControllers.Any(c => string.Equals(AssetDatabase.GetAssetPath(c), selectedControllerPath, StringComparison.Ordinal)))
            {
                selectedControllerPath = ShowAllPath;
            }

            Repaint();
        }

        private FolderNode BuildFolderTree(List<AnimationClip> clips)
        {
            var root = new FolderNode { name = "", path = "" };

            foreach (var clip in clips)
            {
                if (!clipFilter.Matches(clip.name))
                    continue;

                var path = AssetDatabase.GetAssetPath(clip);
                var folder = System.IO.Path.GetDirectoryName(path)?.Replace('\\', '/');

                var current = root;
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

            PruneFolderTree(root);
            return root;
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

        private void DrawFolderTree(FolderNode node, int depth)
        {
            foreach (var child in node.children.Values.OrderBy(c => c.name, StringComparer.OrdinalIgnoreCase))
            {
                var wasExpanded = expandedFolders.Contains(child.path);
                bool expanded;
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Space(depth * 15f);
                    var folderIcon = wasExpanded ? "d_FolderOpened Icon" : "d_Folder Icon";
                    expanded = EditorGUILayout.Foldout(wasExpanded,
                        new GUIContent($"{child.name} ({child.count})", EditorGUIUtility.IconContent(folderIcon).image), true);
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
                    DrawFolderTree(child, depth + 1);
            }

            foreach (var clip in node.clips)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Space((1 + depth) * 15f);
                    GUILayout.Label(new GUIContent(clip.name, EditorGUIUtility.IconContent("d_AnimationClip Icon").image),
                        GUILayout.Height(20), GUILayout.ExpandWidth(true));
                }
            }
        }
    }
}
#endif
