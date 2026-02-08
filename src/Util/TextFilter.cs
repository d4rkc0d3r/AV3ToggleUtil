#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using System;
using System.Text.RegularExpressions;

namespace d4rkpl4y3r.AV3ToggleUtil.Util
{
    public class TextFilter
    {
        private string text = "";
        private bool isRegex = true;
        private bool isCaseSensitive = false;
        private bool invert = false;

        public string Text
        {
            get => text;
            set
            {
                if (text != value)
                {
                    text = value;
                    matchCache.Clear();
                }
            }
        }

        public bool IsRegex
        {
            get => isRegex;
            set
            {
                if (isRegex != value)
                {
                    isRegex = value;
                    matchCache.Clear();
                }
            }
        }

        public bool IsCaseSensitive
        {
            get => isCaseSensitive;
            set
            {
                if (isCaseSensitive != value)
                {
                    isCaseSensitive = value;
                    matchCache.Clear();
                }
            }
        }

        public bool Invert
        {
            get => invert;
            set
            {
                if (invert != value)
                {
                    invert = value;
                    matchCache.Clear();
                }
            }
        }

        private readonly Dictionary<string, bool> matchCache = new();

        public bool Matches(string input)
        {
            if (string.IsNullOrEmpty(text))
                return true;
            input ??= "";
            if (matchCache.TryGetValue(input, out var cached))
                return cached;
            if (isRegex)
            {
                try
                {
                    var regex = new Regex(text, isCaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase);
                    bool isMatch = input != null && regex.IsMatch(input);
                    return matchCache[input] = isMatch ^ invert;
                }
                catch (Exception)
                {
                    return matchCache[input] = true;
                }
            }
            var comparison = isCaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            bool contains = input != null && input.IndexOf(text, comparison) >= 0;
            return matchCache[input] = contains ^ invert;
        }

        public void DrawGUI(string label)
        {
            using var _ = new EditorGUILayout.HorizontalScope();
            using var cc = new EditorGUI.ChangeCheckScope();

            string regexError = null;
            if (isRegex && !string.IsNullOrEmpty(text))
            {
                try { var regex = new Regex(text, isCaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase); }
                catch (Exception ex) { regexError = ex.Message; }
            }

            var prevColor = GUI.contentColor;
            if (regexError != null)
                GUI.contentColor = new Color(1f, 1f, 0.3f);

            var rect = EditorGUILayout.GetControlRect();
            text = EditorGUI.TextField(rect, new GUIContent(label), text);
            if (regexError != null)
                GUI.Label(rect, new GUIContent("", regexError));

            GUI.contentColor = prevColor;

            isRegex = GUILayout.Toggle(isRegex, "Regex", GUI.skin.button, GUILayout.ExpandWidth(false));
            isCaseSensitive = GUILayout.Toggle(isCaseSensitive, "Case", GUI.skin.button, GUILayout.ExpandWidth(false));
            invert = GUILayout.Toggle(invert, "Invert", GUI.skin.button, GUILayout.ExpandWidth(false));

            if (cc.changed)
            {
                matchCache.Clear();
            }
        }
    }
}
#endif