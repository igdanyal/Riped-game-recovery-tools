#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>Shared, reference-safe backup history for the City Tools editor scripts.</summary>
public sealed class CityToolBackupManager : EditorWindow
{
    [Serializable]
    private sealed class BackupRecord
    {
        public string originalPath;
        public string backupPath;
        public string toolName;
        public string createdUtc;
    }

    [Serializable]
    private sealed class RestoreRollback
    {
        public string originalPath;
        public string rollbackPath;
    }

    [FilePath("ProjectSettings/CityToolBackupHistory.asset", FilePathAttribute.Location.ProjectFolder)]
    private sealed class BackupState : ScriptableSingleton<BackupState>
    {
        public List<BackupRecord> records = new List<BackupRecord>();
        public string lastRestoreOriginalPath;
        public string lastRestoreRollbackPath;
        public List<RestoreRollback> lastRestoreRollbacks = new List<RestoreRollback>();

        public void Persist() { Save(true); }
    }

    private Vector2 scroll;

    [MenuItem("Tools/Smart Recovery Tools/8. Backup Manager", false, 27)]
    public static void Open()
    {
        GetWindow<CityToolBackupManager>("City Tool Backups");
    }

    public static string CreateBackup(string originalAssetPath, string toolName)
    {
        if (string.IsNullOrEmpty(originalAssetPath) ||
            !originalAssetPath.StartsWith("Assets/", StringComparison.Ordinal))
            throw new ArgumentException("Only assets below the project's Assets folder can be backed up.");
        if (AssetDatabase.LoadMainAssetAtPath(originalAssetPath) == null)
            throw new FileNotFoundException("Asset to back up was not found.", originalAssetPath);

        string safeToolName = Sanitize(string.IsNullOrWhiteSpace(toolName) ? "Other" : toolName);
        string folder = EnsureFolder("Assets/CityTools_Backups/" + safeToolName);
        string stamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff");
        string fileName = Path.GetFileNameWithoutExtension(originalAssetPath);
        string extension = Path.GetExtension(originalAssetPath);
        string backupPath = AssetDatabase.GenerateUniqueAssetPath(
            folder + "/" + Sanitize(fileName) + "_" + stamp + extension);

        if (!AssetDatabase.CopyAsset(originalAssetPath, backupPath))
            throw new IOException("Unity could not create backup: " + backupPath);

        BackupState.instance.records.Add(new BackupRecord
        {
            originalPath = originalAssetPath,
            backupPath = backupPath,
            toolName = toolName,
            createdUtc = DateTime.UtcNow.ToString("O")
        });
        BackupState.instance.Persist();
        return backupPath;
    }

    private Vector2 finalizedPageScroll;
    private void OnGUI()
    {
        minSize = new Vector2(560, 420);
        using (new SmartRecoveryUI.WindowScope(this, "Backup Manager"))
        {
            finalizedPageScroll = EditorGUILayout.BeginScrollView(finalizedPageScroll);
            try { DrawToolGUI(); }
            finally { EditorGUILayout.EndScrollView(); }
        }
    }

    private void DrawToolGUI()
    {
        EditorGUILayout.Space(8);
        EditorGUILayout.LabelField("City Tool Backup Manager", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "Backups are stored below Assets/CityTools_Backups. Restore writes the backup content " +
            "to its original asset path, preserving the original GUID and all project references.",
            MessageType.Info);

        GUI.enabled = HasUndoableRestore();
        if (GUILayout.Button("Undo Last Restore", GUILayout.Height(30))) UndoLastRestore();
        GUI.enabled = true;

        List<BackupRecord> records = BackupState.instance.records
            .Where(record => record != null)
            .OrderByDescending(record => record.createdUtc)
            .ToList();
        List<BackupRecord> latestPerAsset = GetLatestRestorableRecords(records);
        GUI.enabled = latestPerAsset.Count > 0;
        if (GUILayout.Button($"Restore All Latest Backups ({latestPerAsset.Count} Assets)", GUILayout.Height(32)))
            RestoreAll(latestPerAsset);
        GUI.enabled = true;
        EditorGUILayout.LabelField($"Recorded backups: {records.Count}", EditorStyles.miniBoldLabel);
        scroll = EditorGUILayout.BeginScrollView(scroll);
        foreach (BackupRecord record in records)
        {
            using (new EditorGUILayout.VerticalScope("box"))
            {
                EditorGUILayout.LabelField(record.toolName + "  |  " + FormatDate(record.createdUtc),
                    EditorStyles.boldLabel);
                EditorGUILayout.LabelField("Original", record.originalPath);
                UnityEngine.Object backup = AssetDatabase.LoadMainAssetAtPath(record.backupPath);
                EditorGUILayout.ObjectField("Backup", backup, typeof(UnityEngine.Object), false);
                GUI.enabled = backup != null && File.Exists(ToAbsolutePath(record.originalPath));
                if (GUILayout.Button("Restore This Backup")) Restore(record);
                GUI.enabled = true;
            }
        }
        EditorGUILayout.EndScrollView();
    }

    private static void Restore(BackupRecord record)
    {
        if (!EditorUtility.DisplayDialog("Restore Asset Backup",
            "Restore this backup over:\n\n" + record.originalPath +
            "\n\nThe original GUID and references will remain unchanged.", "Restore", "Cancel")) return;

        string rollback = CreateBackup(record.originalPath, "Before_Restore");
        ReplaceAssetFile(record.backupPath, record.originalPath);
        BackupState.instance.lastRestoreOriginalPath = record.originalPath;
        BackupState.instance.lastRestoreRollbackPath = rollback;
        BackupState.instance.lastRestoreRollbacks = new List<RestoreRollback>
        {
            new RestoreRollback { originalPath = record.originalPath, rollbackPath = rollback }
        };
        BackupState.instance.Persist();
        EditorUtility.DisplayDialog("Backup Restored",
            "Asset restored successfully. Use 'Undo Last Restore' if you need to reverse it.", "OK");
    }

    private static void UndoLastRestore()
    {
        List<RestoreRollback> rollbacks = BackupState.instance.lastRestoreRollbacks ??
                                             new List<RestoreRollback>();
        if (rollbacks.Count == 0 && !string.IsNullOrEmpty(BackupState.instance.lastRestoreOriginalPath) &&
            !string.IsNullOrEmpty(BackupState.instance.lastRestoreRollbackPath))
        {
            rollbacks.Add(new RestoreRollback
            {
                originalPath = BackupState.instance.lastRestoreOriginalPath,
                rollbackPath = BackupState.instance.lastRestoreRollbackPath
            });
        }

        int restored = 0;
        foreach (RestoreRollback rollback in rollbacks)
        {
            if (rollback == null || !File.Exists(ToAbsolutePath(rollback.rollbackPath)) ||
                !File.Exists(ToAbsolutePath(rollback.originalPath))) continue;
            ReplaceAssetFile(rollback.rollbackPath, rollback.originalPath);
            restored++;
        }
        BackupState.instance.lastRestoreOriginalPath = null;
        BackupState.instance.lastRestoreRollbackPath = null;
        BackupState.instance.lastRestoreRollbacks = new List<RestoreRollback>();
        BackupState.instance.Persist();
        EditorUtility.DisplayDialog("Restore Undone",
            $"Restored {restored} assets to their state from before the last Restore operation.", "OK");
    }

    private static void RestoreAll(List<BackupRecord> records)
    {
        if (records == null || records.Count == 0) return;
        if (!EditorUtility.DisplayDialog("Restore All Latest Backups",
            $"Restore the latest recorded backup for {records.Count} original assets?\n\n" +
            "Original GUIDs and project references will remain unchanged.", "Restore All", "Cancel")) return;

        List<RestoreRollback> rollbacks = new List<RestoreRollback>();
        int restored = 0, failed = 0;
        foreach (BackupRecord record in records)
        {
            try
            {
                string rollbackPath = CreateBackup(record.originalPath, "Before_Restore");
                ReplaceAssetFile(record.backupPath, record.originalPath);
                rollbacks.Add(new RestoreRollback
                {
                    originalPath = record.originalPath,
                    rollbackPath = rollbackPath
                });
                restored++;
            }
            catch (Exception exception)
            {
                failed++;
                Debug.LogError($"Could not restore '{record.originalPath}':\n{exception}");
            }
        }

        BackupState.instance.lastRestoreRollbacks = rollbacks;
        BackupState.instance.lastRestoreOriginalPath = null;
        BackupState.instance.lastRestoreRollbackPath = null;
        BackupState.instance.Persist();
        EditorUtility.DisplayDialog("Restore All Complete",
            $"Assets restored: {restored}\nFailed: {failed}\n\n" +
            "Use 'Undo Last Restore' to reverse this complete restoration batch.", "OK");
    }

    private static List<BackupRecord> GetLatestRestorableRecords(IEnumerable<BackupRecord> records)
    {
        return records
            .Where(record => record != null && record.toolName != "Before_Restore")
            .Where(record => File.Exists(ToAbsolutePath(record.backupPath)) &&
                             File.Exists(ToAbsolutePath(record.originalPath)))
            .GroupBy(record => record.originalPath, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(record => record.createdUtc).First())
            .OrderBy(record => record.originalPath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool HasUndoableRestore()
    {
        if (BackupState.instance.lastRestoreRollbacks != null &&
            BackupState.instance.lastRestoreRollbacks.Any(rollback => rollback != null &&
                File.Exists(ToAbsolutePath(rollback.rollbackPath)))) return true;
        return !string.IsNullOrEmpty(BackupState.instance.lastRestoreRollbackPath) &&
               File.Exists(ToAbsolutePath(BackupState.instance.lastRestoreRollbackPath));
    }

    private static void ReplaceAssetFile(string sourceAssetPath, string destinationAssetPath)
    {
        string source = ToAbsolutePath(sourceAssetPath);
        string destination = ToAbsolutePath(destinationAssetPath);
        if (!File.Exists(source)) throw new FileNotFoundException("Backup file is missing.", source);
        if (!File.Exists(destination)) throw new FileNotFoundException("Original asset file is missing.", destination);
        FileUtil.ReplaceFile(source, destination);
        AssetDatabase.ImportAsset(destinationAssetPath, ImportAssetOptions.ForceUpdate);
        AssetDatabase.Refresh();
    }

    private static string EnsureFolder(string path)
    {
        string current = "Assets";
        foreach (string part in path.Split('/').Skip(1))
        {
            string next = current + "/" + part;
            if (!AssetDatabase.IsValidFolder(next)) AssetDatabase.CreateFolder(current, part);
            current = next;
        }
        return current;
    }

    private static string ToAbsolutePath(string assetPath)
    {
        string projectRoot = Path.GetDirectoryName(Application.dataPath);
        return Path.GetFullPath(Path.Combine(projectRoot, assetPath));
    }

    private static string Sanitize(string value)
    {
        foreach (char character in Path.GetInvalidFileNameChars()) value = value.Replace(character, '_');
        return value.Replace(' ', '_');
    }

    private static string FormatDate(string value)
    {
        return DateTime.TryParse(value, out DateTime date) ? date.ToLocalTime().ToString("g") : value;
    }
}
#endif
