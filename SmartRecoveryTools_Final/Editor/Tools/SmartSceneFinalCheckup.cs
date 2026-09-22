#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

public sealed class SmartSceneFinalCheckup : EditorWindow
{
    private enum ReferenceKind { MeshFilter, SkinnedMeshRenderer, MeshCollider }

    private sealed class MeshReferenceIssue
    {
        public UnityEngine.Object component;
        public GameObject owner;
        public Mesh mesh;
        public ReferenceKind kind;
        public string assetPath;
        public string prefabAssetPath;
        public string reason;
        public bool enabled = true;
    }

    private readonly List<MeshReferenceIssue> issues = new List<MeshReferenceIssue>();
    private DefaultAsset fbxOutputFolder;
    private Vector2 scroll;
    private bool includeInactive = true;
    private string lastResult;
    private const string TempPrefabFolder =
        "Assets/#RecoveredMeshAssets/#recovery_tool/temp/FinalCheckTempPrefabs";

    [MenuItem("Tools/Smart Recovery Tools/6. Scene Final Checkup", false, 25)]
    private static void Open()
    {
        GetWindow<SmartSceneFinalCheckup>("Scene Final Checkup");
    }

    private Vector2 finalizedPageScroll;
    private void OnGUI()
    {
        minSize = new Vector2(560, 420);
        using (new SmartRecoveryUI.WindowScope(this, "Scene Final Checkup"))
        {
            finalizedPageScroll = EditorGUILayout.BeginScrollView(finalizedPageScroll);
            try { DrawToolGUI(); }
            finally { EditorGUILayout.EndScrollView(); }
        }
    }

    private void DrawToolGUI()
    {
        EditorGUILayout.Space(8);
        EditorGUILayout.LabelField("Scene Final Checkup", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "Scans all loaded scenes and only the prefab assets referenced by those scenes. It reports every mesh " +
            "reference that is not an FBX sub-asset. Repair creates backups, skips combined and rigged meshes, reuses the " +
            "smart FBX cache, converts eligible meshes, assigns FBX meshes to MeshFilters and MeshColliders, then scans again.",
            MessageType.Info);
        includeInactive = EditorGUILayout.ToggleLeft("Include inactive objects", includeInactive);
        fbxOutputFolder = (DefaultAsset)EditorGUILayout.ObjectField(
            "FBX Output Folder", fbxOutputFolder, typeof(DefaultAsset), false);
        if (fbxOutputFolder == null && GUILayout.Button("Use Current Scene FBX Folder"))
            fbxOutputFolder = SmartRecoveryPaths.FolderAsset(SmartRecoveryPaths.Fbx(SceneManager.GetActiveScene().path));

        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Scan Scene + Referenced Prefabs", GUILayout.Height(32))) Scan();
            if (GUILayout.Button("Clear", GUILayout.Height(32)))
            {
                issues.Clear();
                lastResult = string.Empty;
            }
        }

        int enabled = issues.Count(CanConvert);
        GUI.enabled = enabled > 0 && IsValidOutputFolder();
        if (GUILayout.Button($"Convert to FBX & Assign Selected ({enabled})", GUILayout.Height(40))) Repair();
        GUI.enabled = true;

        int sceneCount = issues.Count(issue => string.IsNullOrEmpty(issue.prefabAssetPath));
        int prefabCount = issues.Count - sceneCount;
        EditorGUILayout.LabelField(
            $"Non-FBX references: {issues.Count} | Scene: {sceneCount} | Referenced prefabs: {prefabCount}",
            EditorStyles.boldLabel);
        if (!string.IsNullOrEmpty(lastResult))
            EditorGUILayout.HelpBox(lastResult, issues.Count == 0 ? MessageType.Info : MessageType.Warning);

        scroll = EditorGUILayout.BeginScrollView(scroll);
        foreach (MeshReferenceIssue issue in issues)
        {
            using (new EditorGUILayout.VerticalScope("box"))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    issue.enabled = EditorGUILayout.Toggle(issue.enabled, GUILayout.Width(18));
                    EditorGUILayout.ObjectField(issue.component, issue.component == null
                        ? typeof(UnityEngine.Object) : issue.component.GetType(), true);
                }
                EditorGUILayout.ObjectField("Mesh", issue.mesh, typeof(Mesh), false);
                EditorGUILayout.LabelField("Source: " +
                    (string.IsNullOrEmpty(issue.assetPath) ? "Temporary / combined scene mesh" : issue.assetPath),
                    EditorStyles.wordWrappedMiniLabel);
                if (!string.IsNullOrEmpty(issue.prefabAssetPath))
                    EditorGUILayout.LabelField("Prefab: " + issue.prefabAssetPath, EditorStyles.wordWrappedMiniLabel);
                EditorGUILayout.LabelField(issue.reason, EditorStyles.wordWrappedMiniLabel);
            }
        }
        EditorGUILayout.EndScrollView();
    }

    private void Scan()
    {
        issues.Clear();
        HashSet<string> referencedPrefabPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (GameObject gameObject in GetLoadedSceneObjects())
        {
            AddIssues(gameObject, string.Empty);
            string prefabPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(gameObject);
            if (!string.IsNullOrEmpty(prefabPath) && prefabPath.EndsWith(".prefab",
                StringComparison.OrdinalIgnoreCase)) referencedPrefabPaths.Add(prefabPath);
        }

        foreach (string prefabPath in referencedPrefabPaths.OrderBy(path => path,
            StringComparer.OrdinalIgnoreCase))
        {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (prefab == null) continue;
            foreach (Transform transform in prefab.GetComponentsInChildren<Transform>(true))
                AddIssues(transform.gameObject, prefabPath);
        }

        issues.Sort((a, b) => string.Compare(GetIssueSortKey(a), GetIssueSortKey(b),
            StringComparison.OrdinalIgnoreCase));
        lastResult = issues.Count == 0
            ? "PASS: every scanned scene and referenced-prefab mesh comes from an FBX file."
            : "Review the remaining non-FBX references below. Nothing has been changed.";
        Repaint();
    }

    private void AddIssues(GameObject owner, string prefabAssetPath)
    {
        if (owner == null) return;
        MeshFilter filter = owner.GetComponent<MeshFilter>();
        if (filter != null) AddIssue(filter, owner, filter.sharedMesh, ReferenceKind.MeshFilter, prefabAssetPath);
        SkinnedMeshRenderer skinned = owner.GetComponent<SkinnedMeshRenderer>();
        if (skinned != null)
            AddIssue(skinned, owner, skinned.sharedMesh, ReferenceKind.SkinnedMeshRenderer, prefabAssetPath);
        foreach (MeshCollider collider in owner.GetComponents<MeshCollider>())
            AddIssue(collider, owner, collider.sharedMesh, ReferenceKind.MeshCollider, prefabAssetPath);
    }

    private void AddIssue(UnityEngine.Object component, GameObject owner, Mesh mesh,
        ReferenceKind kind, string prefabAssetPath)
    {
        if (mesh == null)
        {
            issues.Add(new MeshReferenceIssue { component = component, owner = owner, kind = kind, prefabAssetPath = prefabAssetPath, reason = "Missing mesh: restore a source mesh before conversion.", enabled = false });
            return;
        }
        string path = AssetDatabase.GetAssetPath(mesh);
        if (path.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase)) return;
        bool combined = IsCombinedMesh(mesh);
        issues.Add(new MeshReferenceIssue
        {
            component = component,
            owner = owner,
            mesh = mesh,
            kind = kind,
            assetPath = path,
            prefabAssetPath = prefabAssetPath,
            reason = combined
                ? "Combined/generated mesh: recover visual geometry and pivot before FBX conversion."
                : (kind == ReferenceKind.SkinnedMeshRenderer && string.IsNullOrEmpty(prefabAssetPath)
                    ? "Scene-only rigged mesh requires a prefab-based rig conversion; it will not be flattened."
                    : "Persistent mesh is not sourced from FBX and can be converted/reconnected.")
        });
    }

    internal static int RunAutomaticCheck(string outputPath)
    {
        var tool = CreateInstance<SmartSceneFinalCheckup>();
        try
        {
            tool.fbxOutputFolder = SmartRecoveryPaths.FolderAsset(outputPath);
            tool.Scan();
            tool.Repair();
            return tool.issues.Count;
        }
        finally { DestroyImmediate(tool); }
    }

    private static bool CanConvert(MeshReferenceIssue issue) => issue.enabled && issue.mesh != null &&
        issue.kind != ReferenceKind.SkinnedMeshRenderer && !IsCombinedMesh(issue.mesh) && issue.mesh.bindposes.Length == 0 &&
        !(issue.owner != null && issue.owner.TryGetComponent(out MeshRenderer renderer) && renderer.isPartOfStaticBatch);

    private void Repair()
    {
        if (!IsValidOutputFolder()) return;
        string outputPath = AssetDatabase.GetAssetPath(fbxOutputFolder);
        List<MeshReferenceIssue> selected = issues.Where(CanConvert).ToList();
        if (selected.Count == 0) { Scan(); lastResult += " No eligible static mesh references to convert."; return; }
        BackupLoadedScenes();
        foreach (string path in selected.Where(i => !string.IsNullOrEmpty(i.prefabAssetPath)).Select(i => i.prefabAssetPath).Distinct())
            CityToolBackupManager.CreateBackup(path, "Scene_Final_Checkup");
        Dictionary<Mesh, Mesh> replacements = ConvertLooseSceneMeshes(outputPath);
        int changed = 0;
        foreach (MeshReferenceIssue issue in selected.Where(i => string.IsNullOrEmpty(i.prefabAssetPath)))
        {
            if (issue.component == null || !replacements.TryGetValue(issue.mesh, out Mesh mesh)) continue;
            Undo.RecordObject(issue.component, "Assign Final FBX Mesh");
            if (issue.component is MeshFilter filter) filter.sharedMesh = mesh;
            else if (issue.component is MeshCollider collider) collider.sharedMesh = mesh;
            PrefabUtility.RecordPrefabInstancePropertyModifications(issue.component);
            EditorUtility.SetDirty(issue.component);
            changed++;
        }
        foreach (var group in selected.Where(i => !string.IsNullOrEmpty(i.prefabAssetPath)).GroupBy(i => i.prefabAssetPath))
        {
            GameObject root = null;
            try
            {
                root = PrefabUtility.LoadPrefabContents(group.Key);
                foreach (MeshReferenceIssue issue in group)
                {
                    if (!replacements.TryGetValue(issue.mesh, out Mesh mesh)) continue;
                    Transform target = ResolveTransform(root.transform, TransformKey(issue.owner.transform));
                    if (target == null) continue;
                    if (issue.kind == ReferenceKind.MeshFilter)
                    {
                        MeshFilter filter = target.GetComponent<MeshFilter>();
                        if (filter != null && filter.sharedMesh == issue.mesh) { filter.sharedMesh = mesh; changed++; }
                    }
                    else
                    {
                        int index = Array.IndexOf(issue.owner.GetComponents<MeshCollider>(), issue.component as MeshCollider);
                        MeshCollider[] colliders = target.GetComponents<MeshCollider>();
                        if (index >= 0 && index < colliders.Length && colliders[index].sharedMesh == issue.mesh)
                        { colliders[index].sharedMesh = mesh; changed++; }
                    }
                }
                PrefabUtility.SaveAsPrefabAsset(root, group.Key, out bool success);
                if (!success) throw new IOException("Could not save prefab " + group.Key);
            }
            finally { if (root != null) PrefabUtility.UnloadPrefabContents(root); }
        }
        AssetDatabase.SaveAssets();
        MarkLoadedScenesDirty();
        Scan();
        lastResult = $"Assigned {changed} FBX mesh references. Rescan: {issues.Count} remaining. Combined, missing and rigged meshes require their recovery tools.";
    }

    private static int[] TransformKey(Transform transform)
    {
        var indices = new List<int>();
        while (transform.parent != null) { indices.Add(transform.GetSiblingIndex()); transform = transform.parent; }
        indices.Reverse();
        return indices.ToArray();
    }

    private static Transform ResolveTransform(Transform root, int[] indices)
    {
        foreach (int index in indices) { if (index >= root.childCount) return null; root = root.GetChild(index); }
        return root;
    }
    private Dictionary<Mesh, Mesh> ConvertLooseSceneMeshes(string outputPath)
    {
        Dictionary<Mesh, Mesh> replacements = new Dictionary<Mesh, Mesh>();
        List<Mesh> sources = issues
            .Where(CanConvert)
            .Select(issue => issue.mesh).Distinct().ToList();
        if (sources.Count == 0) return replacements;

        EnsureAssetFolder(TempPrefabFolder);
        List<GameObject> temporaryPrefabs = new List<GameObject>();
        Dictionary<string, Mesh> sourceByPrefabPath = new Dictionary<string, Mesh>(StringComparer.OrdinalIgnoreCase);
        Material defaultMaterial = AssetDatabase.GetBuiltinExtraResource<Material>("Default-Material.mat");
        foreach (Mesh source in sources)
        {
            string token = Hash128.Compute(GetStableMeshKey(source)).ToString().Substring(0, 12);
            string prefabPath = $"{TempPrefabFolder}/{Sanitize(source.name)}_{token}.prefab";
            GameObject holder = new GameObject(Sanitize(source.name));
            try
            {
                holder.AddComponent<MeshFilter>().sharedMesh = source;
                MeshRenderer renderer = holder.AddComponent<MeshRenderer>();
                renderer.sharedMaterials = Enumerable.Repeat(defaultMaterial,
                    Mathf.Max(1, source.subMeshCount)).ToArray();
                GameObject prefab = PrefabUtility.SaveAsPrefabAsset(holder, prefabPath);
                if (prefab != null)
                {
                    temporaryPrefabs.Add(prefab);
                    sourceByPrefabPath[prefabPath] = source;
                }
            }
            finally { DestroyImmediate(holder); }
        }

        if (temporaryPrefabs.Count > 0)
            BatchPrefabToModelConverter.RunAutomatic(temporaryPrefabs, outputPath);
        foreach (KeyValuePair<string, Mesh> pair in sourceByPrefabPath)
        {
            GameObject convertedPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(pair.Key);
            Mesh converted = convertedPrefab == null ? null :
                convertedPrefab.GetComponentInChildren<MeshFilter>(true)?.sharedMesh;
            if (converted != null && AssetDatabase.GetAssetPath(converted)
                .EndsWith(".fbx", StringComparison.OrdinalIgnoreCase))
                replacements[pair.Value] = converted;
        }
        return replacements;
    }

    private IEnumerable<GameObject> GetLoadedSceneObjects()
    {
        List<GameObject> result = new List<GameObject>();
        for (int index = 0; index < SceneManager.sceneCount; index++)
        {
            Scene scene = SceneManager.GetSceneAt(index);
            if (!scene.IsValid() || !scene.isLoaded) continue;
            foreach (GameObject root in scene.GetRootGameObjects())
                result.AddRange(root.GetComponentsInChildren<Transform>(includeInactive)
                    .Select(transform => transform.gameObject));
        }
        return result.Distinct();
    }

    private bool IsValidOutputFolder()
    {
        if (fbxOutputFolder == null) return false;
        string path = AssetDatabase.GetAssetPath(fbxOutputFolder);
        return !string.IsNullOrEmpty(path) && AssetDatabase.IsValidFolder(path) &&
               (path == "Assets" || path.StartsWith("Assets/", StringComparison.Ordinal));
    }

    private static bool IsCombinedMesh(Mesh mesh)
    {
        if (mesh == null) return false;
        if (string.IsNullOrEmpty(AssetDatabase.GetAssetPath(mesh))) return true;
        string name = mesh.name.ToLowerInvariant();
        return name.Contains("combined") || name.Contains("static batch") || name.Contains("baked") ||
               name.Contains("meshbaker");
    }

    private static string GetStableMeshKey(Mesh mesh)
    {
        if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(mesh, out string guid, out long localId))
            return guid + ":" + localId;
        return mesh.name + ":" + mesh.vertexCount + ":" + mesh.subMeshCount;
    }

    private static string GetIssueSortKey(MeshReferenceIssue issue) =>
        (issue.prefabAssetPath ?? string.Empty) + "|" +
        (issue.owner == null ? string.Empty : issue.owner.name) + "|" + issue.kind;

    private static string Sanitize(string value)
    {
        string result = string.IsNullOrWhiteSpace(value) ? "Mesh" : value.Trim();
        foreach (char invalid in Path.GetInvalidFileNameChars()) result = result.Replace(invalid, '_');
        return result;
    }

    private static void EnsureAssetFolder(string path)
    {
        string[] parts = path.Split('/');
        string current = parts[0];
        for (int index = 1; index < parts.Length; index++)
        {
            string next = current + "/" + parts[index];
            if (!AssetDatabase.IsValidFolder(next)) AssetDatabase.CreateFolder(current, parts[index]);
            current = next;
        }
    }

    private static void BackupLoadedScenes()
    {
        for (int index = 0; index < SceneManager.sceneCount; index++)
        {
            Scene scene = SceneManager.GetSceneAt(index);
            if (scene.IsValid() && scene.isLoaded && !string.IsNullOrEmpty(scene.path))
                CityToolBackupManager.CreateBackup(scene.path, "Smart_Scene_Final_Checkup");
        }
    }

    private static void MarkLoadedScenesDirty()
    {
        for (int index = 0; index < SceneManager.sceneCount; index++)
        {
            Scene scene = SceneManager.GetSceneAt(index);
            if (scene.IsValid() && scene.isLoaded) EditorSceneManager.MarkSceneDirty(scene);
        }
    }
}
#endif
