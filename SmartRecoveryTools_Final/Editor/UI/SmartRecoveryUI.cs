#if UNITY_EDITOR
using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

internal static class SmartRecoveryUI
{
    internal sealed class WindowScope : IDisposable
    {
        private readonly bool enabled = GUI.enabled;
        private readonly float labelWidth = EditorGUIUtility.labelWidth;
        private readonly Color background = GUI.backgroundColor;
        private readonly Color content = GUI.contentColor;
        private readonly Color color = GUI.color;

        internal WindowScope(EditorWindow window, string title)
        {
            int step = Array.IndexOf(SmartRecoveryWorkflow.Types, window.GetType());
            if (step >= 0) title = SmartRecoveryWorkflow.Names[step];
            EditorGUIUtility.labelWidth = Mathf.Clamp(window.position.width * 0.38f, 170, 280);
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                GUILayout.Label("SMART RECOVERY", EditorStyles.miniBoldLabel);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Tool Dashboard", EditorStyles.toolbarButton)) SmartRecoveryDashboard.Open();
            }
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                GUILayout.Space(8);
                GUILayout.Label(title, new GUIStyle(EditorStyles.boldLabel) { fontSize = 19, wordWrap = true });
                var scene = SceneManager.GetActiveScene();
                GUILayout.Label("Active scene: " + (string.IsNullOrEmpty(scene.name) ? "Unsaved scene" : scene.name) +
                    (scene.isDirty ? "  •  Unsaved changes" : ""), EditorStyles.wordWrappedMiniLabel);
                if (step >= 0)
                {
                    GUILayout.Label(SmartRecoveryWorkflow.Descriptions[step], EditorStyles.wordWrappedLabel);
                    if (step + 1 < SmartRecoveryWorkflow.Types.Length && GUILayout.Button("Next: " + SmartRecoveryWorkflow.Names[step + 1]))
                        EditorWindow.GetWindow(SmartRecoveryWorkflow.Types[step + 1], false, SmartRecoveryWorkflow.Names[step + 1]).Show();
                }
                GUILayout.Space(6);
            }
        }

        public void Dispose()
        {
            GUI.enabled = enabled;
            GUI.backgroundColor = background;
            GUI.contentColor = content;
            GUI.color = color;
            EditorGUIUtility.labelWidth = labelWidth;
        }
    }
}
#endif
