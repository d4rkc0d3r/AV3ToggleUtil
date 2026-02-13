#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

using static d4rkpl4y3r.AV3ToggleUtil.Util.AV3Helper;

namespace d4rkpl4y3r.AV3ToggleUtil
{
    public class AV3ParameterInspector : EditorWindow
    {
        private Vector2 scrollPos;
        private float leftPanelWidth = 200f;
        private string selectedParameter;

        void OnGUI()
        {
            using var scrollView = new EditorGUILayout.ScrollViewScope(scrollPos);
            scrollPos = scrollView.scrollPosition;

            var av = FindAvatarDescriptor(Selection.activeGameObject);
            if (av == null)
            {
                EditorGUILayout.HelpBox("No VRC Avatar Descriptor found.", MessageType.Warning);
                return;
            }

            using var outerHorizontal = new EditorGUILayout.HorizontalScope();
            using (new EditorGUILayout.VerticalScope(GUILayout.Width(leftPanelWidth)))
            {
                // TODO: List parameters as toggle buttons
            }
            // TODO: Add a vertical separator that can be dragged to resize the left panel width
            using (new EditorGUILayout.VerticalScope())
            {
                // TODO: List all sub-menus that change this parameter

                // TODO:
                // For each animator controller in the avatar descriptor:
                //   For each layer in the animator controller if any:
                //     List all states that use this parameter as a condition on an in/out transition
                //     List all states that use this parameter in a blend tree
                //     List all states that use this parameter as motion time
                //     List all affected animation clips of the prior three cases above
                //     List all states that use this parameter in a parameter driver
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
            Repaint();
        }
    }
}
#endif