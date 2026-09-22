#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

[Serializable]
internal sealed class SmartRecoveryAssetRecord
{
    public string kind;
    public string sceneGuid;
    public string sourceGuid;
    public long sourceLocalId;
    public string sourcePath;
    public string recoveredGuid;
    public long recoveredLocalId;
    public string recoveredPath;
    public string geometrySignature;
    public string updatedUtc;
}

internal sealed class SmartRecoveryDataAsset : ScriptableObject
{
    public List<SmartRecoveryAssetRecord> records = new List<SmartRecoveryAssetRecord>();
    public List<string> completedSceneGuids = new List<string>();
}

internal static class SmartRecoveryPaths
{
    internal const string Root = "Assets/RecoveredMeshAssets";
    internal const string Tools = "Assets/#RecoveredMeshAssets/#recovery_tool";
    internal const string Temp = Tools + "/temp";
    internal const string DataPath = Temp + "/SmartRecoveryData.asset";

    internal static string SceneName(string scenePath)
    {
        string value = Path.GetFileNameWithoutExtension(scenePath);
        value = (value ?? string.Empty).TrimStart('#').Trim();
        return Sanitize(string.IsNullOrWhiteSpace(value) ? "UnsavedScene" : value);
    }

    internal static string Separated(string scenePath) =>
        EnsureFolder(Root + "/SeparatedMeshes/" + SceneName(scenePath));
    internal static string Prefabs(string scenePath) =>
        EnsureFolder(Root + "/Prefabs/" + SceneName(scenePath));
    internal static string Fbx(string scenePath) =>
        EnsureFolder(Root + "/FBX/" + SceneName(scenePath));
    internal static string RigPrefabs(string scenePath) =>
        EnsureFolder(Root + "/Rig/Prefabs/" + SceneName(scenePath));
    internal static string RigFbx(string scenePath) =>
        EnsureFolder(Root + "/Rig/FBX/" + SceneName(scenePath));
    internal static string SceneBackups() => EnsureFolder(Temp + "/SceneBackups");
    internal static string MeshGuidCache() => EnsureFolder(Temp + "/MeshGUIDCache");
    internal static string PrefabRegistry() => EnsureFolder(Temp + "/PrefabRegistry");
    internal static string FbxRegistry() => EnsureFolder(Temp + "/FBXRegistry");
    internal static string Sessions() => EnsureFolder(Temp + "/RecoverySessions");

    internal static void EnsureAll(string scenePath)
    {
        Separated(scenePath);
        Prefabs(scenePath);
        Fbx(scenePath);
        RigPrefabs(scenePath);
        RigFbx(scenePath);
        SceneBackups();
        MeshGuidCache();
        PrefabRegistry();
        FbxRegistry();
        Sessions();
    }

    internal static string EnsureFolder(string assetPath)
    {
        assetPath = assetPath.Replace('\\', '/').TrimEnd('/');
        string[] parts = assetPath.Split('/');
        string current = parts[0];
        for (int i = 1; i < parts.Length; i++)
        {
            string next = current + "/" + parts[i];
            if (!AssetDatabase.IsValidFolder(next))
                AssetDatabase.CreateFolder(current, parts[i]);
            current = next;
        }
        return assetPath;
    }

    internal static DefaultAsset FolderAsset(string assetPath) =>
        AssetDatabase.LoadAssetAtPath<DefaultAsset>(EnsureFolder(assetPath));

    internal static string CreateSceneBackup(string scenePath)
    {
        if (string.IsNullOrEmpty(scenePath) || !scenePath.StartsWith("Assets/", StringComparison.Ordinal))
            throw new InvalidOperationException("Automatic Recovery requires a saved scene asset.");
        string folder = SceneBackups();
        string file = SceneName(scenePath) + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".unity";
        string destination = AssetDatabase.GenerateUniqueAssetPath(folder + "/" + file);
        if (!AssetDatabase.CopyAsset(scenePath, destination))
            throw new IOException("Unity could not create the scene backup: " + destination);
        AssetDatabase.ImportAsset(destination, ImportAssetOptions.ForceSynchronousImport);
        return destination;
    }

    private static string Sanitize(string value)
    {
        foreach (char character in Path.GetInvalidFileNameChars()) value = value.Replace(character, '_');
        return string.IsNullOrWhiteSpace(value) ? "Recovered" : value.Trim();
    }
}

internal static class SmartRecoveryData
{
    private static SmartRecoveryDataAsset cached;

    internal static SmartRecoveryDataAsset Load()
    {
        if (cached != null) return cached;
        SmartRecoveryPaths.EnsureFolder(SmartRecoveryPaths.Temp);
        cached = AssetDatabase.LoadAssetAtPath<SmartRecoveryDataAsset>(SmartRecoveryPaths.DataPath);
        if (cached == null)
        {
            cached = ScriptableObject.CreateInstance<SmartRecoveryDataAsset>();
            AssetDatabase.CreateAsset(cached, SmartRecoveryPaths.DataPath);
            AssetDatabase.SaveAssets();
        }
        return cached;
    }

    internal static void Register(string kind, UnityEngine.Object source,
        UnityEngine.Object recovered, string scenePath, string geometrySignature = "")
    {
        SmartRecoveryDataAsset data = Load();
        GetIdentity(source, out string sourceGuid, out long sourceLocalId, out string sourcePath);
        GetIdentity(recovered, out string recoveredGuid, out long recoveredLocalId, out string recoveredPath);
        string sceneGuid = AssetDatabase.AssetPathToGUID(scenePath);
        SmartRecoveryAssetRecord record = data.records.LastOrDefault(item => item != null &&
            item.kind == kind && item.sourceGuid == sourceGuid && item.sourceLocalId == sourceLocalId);
        if (record == null)
        {
            record = new SmartRecoveryAssetRecord();
            data.records.Add(record);
        }
        record.kind = kind;
        record.sceneGuid = sceneGuid;
        record.sourceGuid = sourceGuid;
        record.sourceLocalId = sourceLocalId;
        record.sourcePath = sourcePath;
        record.recoveredGuid = recoveredGuid;
        record.recoveredLocalId = recoveredLocalId;
        record.recoveredPath = recoveredPath;
        record.geometrySignature = geometrySignature;
        record.updatedUtc = DateTime.UtcNow.ToString("o");
        EditorUtility.SetDirty(data);
        AssetDatabase.SaveAssets();
    }

    internal static void MarkSceneComplete(string scenePath)
    {
        string guid = AssetDatabase.AssetPathToGUID(scenePath);
        SmartRecoveryDataAsset data = Load();
        if (!string.IsNullOrEmpty(guid) && !data.completedSceneGuids.Contains(guid))
            data.completedSceneGuids.Add(guid);
        EditorUtility.SetDirty(data);
        AssetDatabase.SaveAssets();
    }

    private static void GetIdentity(UnityEngine.Object asset, out string guid,
        out long localId, out string path)
    {
        guid = string.Empty;
        localId = 0;
        path = asset == null ? string.Empty : AssetDatabase.GetAssetPath(asset);
        if (asset != null)
            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(asset, out guid, out localId);
    }
}
#endif
