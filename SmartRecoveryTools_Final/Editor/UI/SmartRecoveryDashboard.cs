#if UNITY_EDITOR
using System;
using UnityEditor;
using UnityEngine;

public sealed class SmartRecoveryDashboard : EditorWindow
{
    private string search = "";
    private Vector2 scroll;
    [MenuItem("Tools/Smart Recovery Tools/Dashboard", false, 0)]
    public static void Open() => GetWindow<SmartRecoveryDashboard>("Recovery Workflow");
    private void OnGUI()
    {
        minSize = new Vector2(580, 520);
        GUILayout.Space(12);
        GUILayout.Label("Scene Recovery", new GUIStyle(EditorStyles.boldLabel) { fontSize = 23 });
        GUILayout.Label("Follow the steps below in order, or run them with Automatic Recovery.", EditorStyles.wordWrappedLabel);
        if (GUILayout.Button("Automatic Recovery — Current Scene", GUILayout.Height(38))) SmartAutomaticRecoveryTool.Open();
        search = EditorGUILayout.TextField("Find a tool", search);
        scroll = EditorGUILayout.BeginScrollView(scroll);
        for (int i = 0; i < SmartRecoveryWorkflow.Names.Length; i++)
        {
            string name = SmartRecoveryWorkflow.Names[i];
            if (!string.IsNullOrWhiteSpace(search) && (name + SmartRecoveryWorkflow.Descriptions[i]).IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0) continue;
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label(name, EditorStyles.boldLabel);
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button("Open", GUILayout.Width(70), GUILayout.Height(28)))
                        GetWindow(SmartRecoveryWorkflow.Types[i], false, name).Show();
                }
                GUILayout.Label(SmartRecoveryWorkflow.Descriptions[i], EditorStyles.wordWrappedLabel);
                GUILayout.Space(5);
            }
        }
        EditorGUILayout.EndScrollView();
        GUILayout.Label("FBX export requires Unity's FBX Exporter package.", EditorStyles.wordWrappedMiniLabel);
    }
}
#endif
