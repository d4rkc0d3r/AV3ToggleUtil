#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace d4rkpl4y3r.AV3ToggleUtil.Util
{
    public class SplitterState
    {
        public const float SplitterWidth = 4f;

        public float leftPanelWidth = 220f;
        private bool isDraggingSplitter;

        public void DrawSplitter(EditorWindow window, float minWidth, float maxWidth)
        {
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
                leftPanelWidth = Mathf.Clamp(evt.mousePosition.x, minWidth, Mathf.Min(maxWidth, window.position.width - 220f));
                window.Repaint();
            }
            else if (evt.type == EventType.MouseUp && isDraggingSplitter)
            {
                isDraggingSplitter = false;
                evt.Use();
            }
            if (isDraggingSplitter)
            {
                var currentCursorRect = new Rect(evt.mousePosition.x - 11, evt.mousePosition.y - 11, 21f, 21f);
                EditorGUIUtility.AddCursorRect(currentCursorRect, MouseCursor.ResizeHorizontal);
            }
        }
    }
}
#endif
