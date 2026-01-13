#if UNITY_EDITOR
using System.Linq;
using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using System.IO;

namespace d4rkpl4y3r.Utils
{
    public class ShowShaderCompileTimesFromLog : EditorWindow
    {
        Vector2 scrollPosition;

        [MenuItem("Tools/d4rkpl4y3r/Debug/Show Shader Compile Times From Log")]
        static void InitWindow()
        {
            var window = (ShowShaderCompileTimesFromLog)GetWindow(typeof(ShowShaderCompileTimesFromLog));
            window.Show();
            window.ParseEditorLog();
        }

        string parseError = null;
        bool parsedAtLeastOnce;

        class ProgramStats
        {
            public double totalSeconds;
            public int freshVariants;
            public int cacheHits;
            public int occurrences;
        }

        class ShaderStats
        {
            public double totalSeconds;
            public int freshVariants;
            public int cacheHits;
            public int occurrences;
            public Dictionary<string, ProgramStats> programs = new();
        }

        class BuildData
        {
            public string label;
            public Dictionary<string, ShaderStats> shaderStats = new();
            public Dictionary<string, bool> foldouts = new();
            public bool foundAny;
        }

        enum SortMode { Time, Name, Variants }

        List<BuildData> builds;
        int selectedBuildIndex;
        SortMode sortMode = SortMode.Time;
        GUIStyle rightAlignLabel;

        void ParseEditorLog()
        {
            parseError = null;
            parsedAtLeastOnce = true;
            builds = new();
            selectedBuildIndex = 0;

            string logPath = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "Unity", "Editor", "Editor.log");
            if (!File.Exists(logPath))
            {
                parseError = "Editor log file not found.";
                return;
            }

            var linesList = new List<string>();
            try
            {
                using var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var sr = new StreamReader(fs);
                string line;
                while ((line = sr.ReadLine()) != null) linesList.Add(line);
            }
            catch (IOException e)
            {
                parseError = $"Could not read Editor.log (maybe locked?): {e.Message}";
                return;
            }
            var lines = linesList.ToArray();

            var buildStarts = lines
                .Select((l, i) => (l, i))
                .Where(t => t.l.StartsWith("BuildPlayer: start building target"))
                .Select(t => t.i)
                .ToList();
            if (buildStarts.Count == 0)
            {
                parseError = "No build found in the log.";
                return;
            }

            var startRegex = new Regex(@"Compiling shader ['""](?<name>[^""]+)['""](?:\s+pass\s+[""](?<pass>[^""]*)[""])?\s*(?:\((?<program>[^\)]+)\))?", RegexOptions.IgnoreCase);
            var finishRegex = new Regex(@"finished in (?<time>[\d\.]+)\s*seconds", RegexOptions.IgnoreCase);
            var freshRegex = new Regex(@"compiled\s+(\d+)\s+variants?", RegexOptions.IgnoreCase);
            var cacheRegex = new Regex(@"(Local|remote) cache hits (\d+)", RegexOptions.IgnoreCase);

            bool anyBuildHasData = false;

            for (int b = 0; b < buildStarts.Count; b++)
            {
                int start = buildStarts[b];
                int end = (b + 1 < buildStarts.Count) ? buildStarts[b + 1] : lines.Length;
                var build = new BuildData { label = $"Build {b + 1} (line {start})" };

                string currentShader = null;
                string currentProgram = null;

                foreach (var line in lines.Skip(start).Take(end - start))
                {
                    var startMatch = startRegex.Match(line);
                    if (startMatch.Success)
                    {
                        currentShader = startMatch.Groups["name"].Value;
                        currentProgram = startMatch.Groups["program"].Success ? startMatch.Groups["program"].Value : "Unknown";
                        Dictionary<string, string> fullNameTable = new() {
                            { "fp", "fragment" },
                            { "vp", "vertex" },
                            { "gp", "geometry" },
                            { "hp", "hull" },
                            { "dp", "domain" },
                        };
                        if (fullNameTable.TryGetValue(currentProgram.ToLowerInvariant(), out var fullName))
                        {
                            currentProgram = fullName;
                        }
                    }

                    var finishMatch = finishRegex.Match(line);
                    if (finishMatch.Success && !string.IsNullOrEmpty(currentShader))
                    {
                        build.foundAny = true;
                        anyBuildHasData = true;
                        var timeSeconds = double.Parse(finishMatch.Groups["time"].Value, CultureInfo.InvariantCulture);

                        if (!build.shaderStats.TryGetValue(currentShader, out var stats))
                        {
                            stats = new ShaderStats();
                            build.shaderStats[currentShader] = stats;
                        }

                        stats.totalSeconds += timeSeconds;
                        stats.occurrences++;

                        var programKey = string.IsNullOrEmpty(currentProgram) ? "Unknown" : currentProgram;
                        if (!stats.programs.TryGetValue(programKey, out var pStats))
                        {
                            pStats = new ProgramStats();
                            stats.programs[programKey] = pStats;
                        }
                        pStats.totalSeconds += timeSeconds;
                        pStats.occurrences++;

                        int fresh = 0;
                        var vMatch = freshRegex.Match(line);
                        if (vMatch.Success) int.TryParse(vMatch.Groups[1].Value, out fresh);

                        int cache = 0;
                        var cMatch = cacheRegex.Matches(line);
                        foreach (Match match in cMatch)
                        {
                            if (match.Success && int.TryParse(match.Groups[2].Value, out int c))
                            {
                                cache += c;
                            }
                        }

                        stats.freshVariants += fresh;
                        stats.cacheHits += cache;
                        pStats.freshVariants += fresh;
                        pStats.cacheHits += cache;
                    }
                }

                build.label += $" {build.shaderStats.Sum(s => s.Value.totalSeconds):F2}s";
                builds.Add(build);
            }

            if (!anyBuildHasData)
            {
                parseError = "No shader compile entries found in the log.";
                return;
            }

            var lastWithData = builds.FindLastIndex(b => b.foundAny);
            if (lastWithData >= 0) selectedBuildIndex = lastWithData;
        }

        void OnGUI()
        {
            rightAlignLabel ??= new GUIStyle(EditorStyles.label) { alignment = TextAnchor.MiddleRight };

            using (new EditorGUILayout.HorizontalScope())
            {
                if (!parsedAtLeastOnce || GUILayout.Button("Parse Editor Log for Shader Compile Times"))
                {
                    ParseEditorLog();
                }

                using (new EditorGUI.DisabledGroupScope(builds == null || builds.Count == 0))
                {
                    var sortLabels = new[] { "Time", "Name", "Variants" };
                    sortMode = (SortMode)EditorGUILayout.Popup((int)sortMode, sortLabels, GUILayout.Width(70));

                    var labels = builds?.Select(b => b.label).ToArray() ?? System.Array.Empty<string>();
                    selectedBuildIndex = EditorGUILayout.Popup(selectedBuildIndex, labels);
                    using (new EditorGUI.DisabledScope(selectedBuildIndex <= 0))
                    {
                        if (GUILayout.Button("-", GUILayout.Width(20)))
                        {
                            selectedBuildIndex--;
                        }
                    }
                    using (new EditorGUI.DisabledScope(builds == null || selectedBuildIndex >= builds.Count - 1))
                    {
                        if (GUILayout.Button("+", GUILayout.Width(20)))
                        {
                            selectedBuildIndex++;
                        }
                    }
                }
            }

            if (!string.IsNullOrEmpty(parseError))
            {
                EditorGUILayout.HelpBox(parseError, MessageType.Error);
                return;
            }

            if (builds == null || builds.Count == 0)
            {
                EditorGUILayout.HelpBox("No data parsed yet. Click the button above.", MessageType.Info);
                return;
            }

            var activeIndex = Mathf.Clamp(selectedBuildIndex, 0, builds.Count - 1);
            var active = builds[activeIndex];

            if (!active.foundAny || active.shaderStats.Count == 0)
            {
                EditorGUILayout.HelpBox("No shader compile entries found for this build.", MessageType.Info);
                return;
            }

            bool TableEntry(string label, string time, string compiled, string cached, string occurrences, bool isFoldout, bool foldoutState = false)
            {
                using var _ = new EditorGUILayout.HorizontalScope();
                if (isFoldout)
                {
                    foldoutState = EditorGUILayout.Foldout(foldoutState, label, true);
                }
                else
                {
                    EditorGUILayout.LabelField(label, GUILayout.ExpandWidth(true));
                }
                GUILayout.Label(time, rightAlignLabel, GUILayout.Width(70));
                GUILayout.Label(compiled, rightAlignLabel, GUILayout.Width(60));
                GUILayout.Label(cached, rightAlignLabel, GUILayout.Width(60));
                GUILayout.Label(occurrences, rightAlignLabel, GUILayout.Width(40));
                return foldoutState;
            }

            using var scrollView = new EditorGUILayout.ScrollViewScope(scrollPosition);
            scrollPosition = scrollView.scrollPosition;

            TableEntry("Shader", "Time (s)", "Compiled", "Cached", "Occ", false);

            var totalTime = active.shaderStats.Values.Sum(s => s.totalSeconds);
            var totalFresh = active.shaderStats.Values.Sum(s => s.freshVariants);
            var totalCache = active.shaderStats.Values.Sum(s => s.cacheHits);
            var totalOcc = active.shaderStats.Values.Sum(s => s.occurrences);
            TableEntry("All Shaders", $"{totalTime:F2}", $"{totalFresh}", $"{totalCache}", $"{totalOcc}", false);

            EditorGUILayout.Separator();

            IEnumerable<KeyValuePair<string, ShaderStats>> orderedShaders = active.shaderStats.OrderByDescending(s => s.Value.totalSeconds);
            if (sortMode == SortMode.Name) orderedShaders = active.shaderStats.OrderBy(s => s.Key);
            else if (sortMode == SortMode.Variants) orderedShaders = active.shaderStats.OrderByDescending(s => s.Value.freshVariants + s.Value.cacheHits);

            foreach ((var shaderName, var stats) in orderedShaders)
            {
                var open = active.foldouts.TryGetValue(shaderName, out var o) && o;

                active.foldouts[shaderName] = TableEntry(shaderName, $"{stats.totalSeconds:F2}", $"{stats.freshVariants}", $"{stats.cacheHits}", $"{stats.occurrences}", true, open);

                if (active.foldouts[shaderName])
                {
                    EditorGUI.indentLevel++;
                    foreach ((var programName, var ps) in stats.programs.OrderByDescending(p => p.Value.totalSeconds))
                    {
                        TableEntry(programName, $"{ps.totalSeconds:F2}", $"{ps.freshVariants}", $"{ps.cacheHits}", $"{ps.occurrences}", false);
                    }
                    EditorGUI.indentLevel--;
                }
            }
        }
    }
}
#endif