#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

public sealed class SmartAutomaticRecoveryTool : EditorWindow
{
    private bool running, exportSelectedMeshes;
    private bool saveScene = true;
    private Vector2 scroll;
    private readonly string[] status = new string[8];
    private string result = "Ready. Recovery runs on the current saved scene.";

    [MenuItem("Tools/Smart Recovery Tools/Automatic Recovery", false, 1)]
    public static void Open() => GetWindow<SmartAutomaticRecoveryTool>("Automatic Recovery");

    private void OnGUI()
    {
        minSize = new Vector2(600, 520);
        using (new SmartRecoveryUI.WindowScope(this, "Automatic Recovery"))
        {
            scroll = EditorGUILayout.BeginScrollView(scroll);
            EditorGUILayout.HelpBox("Run steps 1–6 in order on the current scene. Step 7 exports selected mesh assets only when enabled. Step 8 opens your backups. A scene backup is created before step 1.", MessageType.Info);
            Scene scene = SceneManager.GetActiveScene();
            bool ready = scene.IsValid() && !string.IsNullOrEmpty(scene.path) && SceneManager.sceneCount == 1 && !EditorApplication.isPlayingOrWillChangePlaymode;
            if (!ready) EditorGUILayout.HelpBox("Open one saved scene in Edit Mode before starting. Close other loaded scenes first.", MessageType.Warning);
            using (new EditorGUI.DisabledScope(running))
            {
                saveScene = EditorGUILayout.ToggleLeft("Save the scene after recovery", saveScene);
                exportSelectedMeshes = EditorGUILayout.ToggleLeft("7. Also export selected .asset meshes to FBX (optional)", exportSelectedMeshes);
            }
            for (int i = 0; i < SmartRecoveryWorkflow.Names.Length; i++)
            {
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    EditorGUILayout.LabelField(SmartRecoveryWorkflow.Names[i], EditorStyles.boldLabel);
                    EditorGUILayout.LabelField(status[i] ?? (i == 6 ? "Optional" : i == 7 ? "Available after recovery" : "Waiting"), EditorStyles.wordWrappedMiniLabel);
                }
            }
            using (new EditorGUI.DisabledScope(running || !ready))
                if (GUILayout.Button("Run Recovery on Current Scene", GUILayout.Height(42))) RunRecovery();
            EditorGUILayout.HelpBox(result, MessageType.None);
            using (new EditorGUI.DisabledScope(running))
            {
                if (GUILayout.Button("Open Scene Final Checkup")) GetWindow<SmartSceneFinalCheckup>("Scene Final Checkup");
                if (GUILayout.Button("8. Open Backup Manager")) CityToolBackupManager.Open();
            }
            EditorGUILayout.EndScrollView();
        }
    }

    private void Step(int index, Action action)
    {
        if (EditorUtility.DisplayCancelableProgressBar("Current Scene Recovery", SmartRecoveryWorkflow.Names[index], index / 7f))
            throw new OperationCanceledException("Cancelled before " + SmartRecoveryWorkflow.Names[index]);
        status[index] = "Running…";
        Repaint();
        try { action(); }
        catch { status[index] = "Stopped — see result below"; throw; }
    }

    private void RunRecovery()
    {
        Scene scene = SceneManager.GetActiveScene();
        if (running || SceneManager.sceneCount != 1 || string.IsNullOrEmpty(scene.path) || EditorApplication.isPlayingOrWillChangePlaymode) return;
        if (!AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetType("UnityEditor.Formats.Fbx.Exporter.ModelExporter", false) != null))
        { result = "Install Unity's FBX Exporter package before running recovery."; return; }
        if (!EditorUtility.DisplayDialog("Recover Current Scene", "Save and back up " + scene.name + ", then run the listed recovery steps?", "Save & Run", "Cancel")) return;
        if (!EditorSceneManager.SaveScene(scene)) { result = "Scene could not be saved. Recovery did not start."; return; }
        UnityEngine.Object[] meshSelection = Selection.objects.ToArray();
        running = true;
        Array.Clear(status, 0, status.Length);
        int remaining = 0;
        try
        {
            string path = scene.path;
            SmartRecoveryPaths.EnsureAll(path);
            string backup = CityToolBackupManager.CreateBackup(path, "Automatic_Recovery");
            status[7] = "Scene backup ready: " + backup;
            var prefabs = new List<GameObject>();
            Step(0, () => {
                var cleanup = SceneCleanupTool.RunAutomatic();
                status[0] = $"Removed {cleanup.removedObjects} objects and {cleanup.removedMissingScripts} missing scripts.";
            });
            Step(1, () => {
                var meshes = SceneCombinedMeshAssetFixer.RunAutomatic(path);
                status[1] = $"Recovered {meshes.colliderRecovered} references; reused {meshes.existingMeshesReused} meshes; {meshes.newMeshesRequired} new meshes required.";
            });
            Step(2, () => {
                var built = CityMultiFBXToPrefabConnectorV3.RunAutomatic(path);
                prefabs.AddRange(built.prefabs);
                // Rig preservation is part of building art assets, not an extra workflow step.
                var roots = SmartRigRecoveryTool.FindRigRoots(scene, true);
                if (roots.Count > 0) prefabs.AddRange(SmartRigRecoveryTool.RecoverScene(scene, roots, false, false).prefabs);
                status[2] = $"Prepared {prefabs.Distinct().Count()} prefabs, including rigged art.";
            });
            Step(3, () => status[3] = SmartSceneColliderRecovery.RunAutomatic());
            Step(4, () => {
                var exported = BatchPrefabToModelConverter.RunAutomatic(prefabs.Distinct(), SmartRecoveryPaths.Fbx(path));
                status[4] = $"Exported {exported.successful}/{exported.requested} prefabs.";
                if (exported.failed > 0) throw new InvalidOperationException($"{exported.failed} prefab exports failed. Review the Console.");
            });
            Step(5, () => {
                remaining = SmartSceneFinalCheckup.RunAutomaticCheck(SmartRecoveryPaths.Fbx(path));
                status[5] = remaining == 0 ? "Passed: no non-FBX mesh references found." : $"Review required: {remaining} non-FBX mesh references. Open Scene Final Checkup.";
            });
            if (exportSelectedMeshes)
                Step(6, () => status[6] = AssetMeshToFbxConverter.RunAutomatic(meshSelection, SmartRecoveryPaths.Fbx(path) + "/OptionalMeshExports"));
            else status[6] = "Skipped — optional export disabled.";
            AssetDatabase.SaveAssets();
            if (saveScene && !EditorSceneManager.SaveScene(scene)) throw new InvalidOperationException("Recovered scene could not be saved.");
            if (remaining == 0) SmartRecoveryData.MarkSceneComplete(path);
            result = remaining == 0 ? "Recovery finished." : "Recovery finished with references requiring review in step 6.";
            if (!saveScene) result += " Scene changes are unsaved.";
        }
        catch (OperationCanceledException ex) { result = ex.Message + ". Earlier steps remain applied; backups are available."; }
        catch (Exception ex) { result = "Recovery stopped: " + ex.Message; Debug.LogException(ex); }
        finally { running = false; EditorUtility.ClearProgressBar(); Repaint(); }
    }
}
#endif
