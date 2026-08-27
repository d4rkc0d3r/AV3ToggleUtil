#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace d4rkpl4y3r.AV3ToggleUtil.Util
{
    public class ColumnGrid
    {
        public const float InnerIndent = 30f;

        private const float ColumnSpacing = 4f;
        private const float MinEntryWidth = 80f;

        public float entryWidth;
        private int columns;

        public void Recalculate(float availableWidth)
        {
            columns = Mathf.Clamp(Mathf.FloorToInt(availableWidth / 260f), 1, 3);
            entryWidth = Mathf.Max(MinEntryWidth, Mathf.Floor((availableWidth - InnerIndent - (columns - 1) * (ColumnSpacing + 3)) / columns));
        }

        public void DrawEntries<T>(IReadOnlyList<T> entries, Action<T> drawEntry)
        {
            for (int i = 0; i < entries.Count; i += columns)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Space(InnerIndent);
                    for (int col = 0; col < columns; col++)
                    {
                        var index = i + col;
                        if (index < entries.Count)
                        {
                            drawEntry(entries[index]);
                        }
                        else
                        {
                            GUILayout.Space(entryWidth);
                        }

                        if (col < columns - 1)
                        {
                            GUILayout.Space(ColumnSpacing);
                        }
                    }
                }
            }
        }
    }
}
#endif
