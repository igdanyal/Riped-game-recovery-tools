#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

// Shared prefab-only maintenance. Scene operations belong to the parent tool.
internal sealed class SmartPrefabMaintenance
{
    private sealed class Issue
    {
        internal string path;
        internal int count;
        internal bool selected = true;
    }
    private readonly bool scripts;
    private readonly List<Issue> issues = new List<Issue>();
    private string status = "Scan prefab assets to preview changes.";
    private DefaultAsset folder;
    internal SmartPrefabMaintenance(bool scripts) { this.scripts = scripts; }

    internal void Draw()
    {
        EditorGUILayout.HelpBox(scripts
            ? "Remove missing script slots from prefab assets. Valid scripts and components are preserved."
            : "Repair empty MeshCollider references from a valid MeshFilter on the same prefab object.", MessageType.Info);
        folder = (DefaultAsset)EditorGUILayout.ObjectField("Optional Assets Folder", folder, typeof(DefaultAsset), false);
        if (GUILayout.Button("Scan Prefab Assets", GUILayout.Height(32))) Scan();
        EditorGUILayout.LabelField(status, EditorStyles.wordWrappedLabel);
        foreach (Issue issue in issues)
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
            {
                issue.selected = EditorGUILayout.Toggle(issue.selected, GUILayout.Width(20));
                EditorGUILayout.ObjectField(AssetDatabase.LoadAssetAtPath<GameObject>(issue.path), typeof(GameObject), false);
                GUILayout.Label(issue.count + " fixes", GUILayout.Width(65));
            }
        }
        int count = issues.Where(i => i.selected).Sum(i => i.count);
        using (new EditorGUI.DisabledScope(count == 0))
            if (GUILayout.Button($"Back Up & Apply {count} Fixes", GUILayout.Height(36))) Apply();
    }

    private void Scan()
    {
        issues.Clear();
        string path = folder == null ? "Assets" : AssetDatabase.GetAssetPath(folder);
        if (!(path == "Assets" || path.StartsWith("Assets/", StringComparison.Ordinal)) || !AssetDatabase.IsValidFolder(path))
        { status = "Choose a folder under Assets."; return; }
        int failed = 0;
        try
        {
            string[] paths = AssetDatabase.FindAssets("t:Prefab", new[] { path })
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(p => p.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase) &&
                    !p.StartsWith("Assets/CityTools_Backups/", StringComparison.OrdinalIgnoreCase) &&
                    !p.Contains("/#recovery_tool/temp/"))
                .Distinct().OrderBy(p => p).ToArray();
            for (int i = 0; i < paths.Length; i++)
            {
                EditorUtility.DisplayProgressBar("Scan Prefabs", paths[i], (float)i / paths.Length);
                GameObject root = null;
                try
                {
                    root = PrefabUtility.LoadPrefabContents(paths[i]);
                    int count = Process(root, false);
                    if (count > 0) issues.Add(new Issue { path = paths[i], count = count });
                }
                catch (Exception ex) { failed++; Debug.LogWarning($"Could not scan {paths[i]}: {ex.Message}"); }
                finally { if (root != null) PrefabUtility.UnloadPrefabContents(root); }
            }
            status = $"{issues.Count} affected prefabs • {issues.Sum(i => i.count)} fixes • {failed} failed scans";
        }
        finally { EditorUtility.ClearProgressBar(); }
    }

    private int Process(GameObject root, bool apply)
    {
        int count = 0;
        foreach (Transform transform in root.GetComponentsInChildren<Transform>(true))
        {
            GameObject go = transform.gameObject;
            if (scripts)
            {
                count += GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(go);
                if (apply) GameObjectUtility.RemoveMonoBehavioursWithMissingScript(go);
            }
            else
            {
                MeshFilter filter = go.GetComponent<MeshFilter>();
                if (filter == null || filter.sharedMesh == null) continue;
                foreach (MeshCollider collider in go.GetComponents<MeshCollider>())
                    if (collider.sharedMesh == null)
                    {
                        count++;
                        if (apply) collider.sharedMesh = filter.sharedMesh;
                    }
            }
        }
        return count;
    }

    private void Apply()
    {
        Issue[] selected = issues.Where(i => i.selected).ToArray();
        if (!EditorUtility.DisplayDialog("Apply Prefab Maintenance",
            $"Back up and modify {selected.Length} prefab assets?", "Back Up & Apply", "Cancel")) return;
        int fixedCount = 0, failed = 0;
        try
        {
            foreach (Issue issue in selected)
            {
                GameObject root = null;
                try
                {
                    CityToolBackupManager.CreateBackup(issue.path, scripts ? "Missing_Script_Cleanup" : "Collider_Recovery");
                    root = PrefabUtility.LoadPrefabContents(issue.path);
                    int changed = Process(root, true);
                    PrefabUtility.SaveAsPrefabAsset(root, issue.path, out bool success);
                    if (!success) throw new InvalidOperationException("Prefab save failed.");
                    fixedCount += changed;
                }
                catch (Exception ex) { failed++; Debug.LogError($"Could not repair {issue.path}: {ex.Message}"); }
                finally { if (root != null) PrefabUtility.UnloadPrefabContents(root); }
            }
            AssetDatabase.SaveAssets();
        }
        finally { EditorUtility.ClearProgressBar(); }
        Scan();
        status = $"Applied {fixedCount} fixes • {failed} failed prefabs. " + status;
    }
}
#endif
