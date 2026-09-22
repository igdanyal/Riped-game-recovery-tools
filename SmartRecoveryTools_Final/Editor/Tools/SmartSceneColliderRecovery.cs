#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>Reviews and repairs colliders on scene objects that have a MeshFilter.</summary>
public sealed class SmartSceneColliderRecovery : EditorWindow
{
    private sealed class Row
    {
        public GameObject gameObject;
        public MeshFilter filter;
        public MeshCollider meshCollider;
        public Collider[] colliders;
        public bool selected = true; public readonly HashSet<Collider> remove = new HashSet<Collider>();
    }

    private readonly List<Row> mismatched = new List<Row>();
    private readonly List<Row> multiple = new List<Row>();
    private Vector2 scroll;
    private bool includeInactive = true;
    private bool makeConvex;
    private GameObject searchRoot; private int page; private readonly SmartPrefabMaintenance prefabMaintenance = new SmartPrefabMaintenance(false);
    private string summary = "Not scanned.";

    [MenuItem("Tools/Smart Recovery Tools/4. Collider Recovery", false, 23)]
    private static void Open() => GetWindow<SmartSceneColliderRecovery>("Collider Recovery");

    private Vector2 finalizedPageScroll;
    private void OnGUI()
    {
        minSize = new Vector2(560, 420);
        using (new SmartRecoveryUI.WindowScope(this, "Collider Recovery"))
        {
            finalizedPageScroll = EditorGUILayout.BeginScrollView(finalizedPageScroll);
            try { DrawToolGUI(); }
            finally { EditorGUILayout.EndScrollView(); }
        }
    }

    private void DrawToolGUI()
    {
        page = GUILayout.Toolbar(page, new[] { "Scene Colliders", "Prefab Assets" });
        if (page == 1) { prefabMaintenance.Draw(); return; }
        EditorGUILayout.Space(6);
        EditorGUILayout.LabelField("Smart Scene Collider Recovery", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "Scans the selected root or every loaded scene. This is a cleanup tool and never adds a collider to an object that has none. " +
            "For prefab instances, the collider configuration stored in the prefab always wins. " +
            "Nothing changes until an Apply button is pressed. Scene backups and Unity Undo are created before changes.",
            MessageType.Info);
        includeInactive = EditorGUILayout.ToggleLeft("Include inactive GameObjects", includeInactive);
        searchRoot = (GameObject)EditorGUILayout.ObjectField("Optional Scene Root", searchRoot, typeof(GameObject), true);
        makeConvex = EditorGUILayout.ToggleLeft("Make created/repaired MeshColliders convex", makeConvex);
        if (makeConvex)
            EditorGUILayout.HelpBox("Unity convex MeshColliders may simplify geometry and have convex-cooking limits.", MessageType.Warning);

        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Scan All Loaded Scenes", GUILayout.Height(32))) Scan();
            if (GUILayout.Button("Clear", GUILayout.Height(32))) Clear();
        }
        EditorGUILayout.LabelField(summary, EditorStyles.wordWrappedLabel);

        scroll = EditorGUILayout.BeginScrollView(scroll);
        DrawSection("1. MeshCollider Mesh Does Not Match", mismatched,
            "Prefab instance: restores its prefab collider data. Non-prefab or prefab without a source collider: assigns the MeshFilter mesh.", "Fix Selected Mesh References",
            ApplyMismatch);
        DrawSection("2. Extra / Collider-Only Components", multiple,
            "Prefab instance with prefab colliders: restores exactly those prefab collider components. " +
            "Otherwise removes only the components you select below. Collider-only objects are included for manual review.",
            "Apply Selected Component Cleanup", ApplyMultiple, true);
        EditorGUILayout.EndScrollView();
    }

    private static void DrawSection(string title, List<Row> rows, string explanation, string button,
        Action<List<Row>> apply, bool showRemoval = false)
    {
        EditorGUILayout.Space(8);
        EditorGUILayout.LabelField($"{title} ({rows.Count})", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(explanation, MessageType.None);
        if (rows.Count == 0) return;
        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Select All")) foreach (Row row in rows) row.selected = true;
            if (GUILayout.Button("Select None")) foreach (Row row in rows) row.selected = false;
        }
        foreach (Row row in rows)
        {
            if (row.gameObject == null) continue;
            using (new EditorGUILayout.VerticalScope("box"))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    row.selected = EditorGUILayout.Toggle(row.selected, GUILayout.Width(18));
                    EditorGUILayout.ObjectField(row.gameObject, typeof(GameObject), true);
                    if (GUILayout.Button("Ping", GUILayout.Width(45)))
                    {
                        Selection.activeGameObject = row.gameObject;
                        EditorGUIUtility.PingObject(row.gameObject);
                    }
                }
                EditorGUILayout.ObjectField("MeshFilter Mesh", row.filter == null ? null : row.filter.sharedMesh, typeof(Mesh), false);
                if (row.meshCollider != null)
                    EditorGUILayout.ObjectField("MeshCollider Mesh", row.meshCollider.sharedMesh, typeof(Mesh), false);
                if (showRemoval && row.colliders != null)
                {
                    GameObject source = PrefabUtility.GetCorrespondingObjectFromSource(row.gameObject);
                    bool restoreSource = source != null && source.GetComponents<Collider>().Length > 0;
                    if (restoreSource) EditorGUILayout.HelpBox("Apply restores this object's prefab collider configuration.", MessageType.None);
                    else foreach (Collider component in row.colliders.Where(c => c != null))
                    {
                        bool remove = EditorGUILayout.ToggleLeft("Remove " + Describe(component), row.remove.Contains(component));
                        if (remove) row.remove.Add(component); else row.remove.Remove(component);
                    }
                }
                if (row.colliders != null)
                    EditorGUILayout.LabelField("Existing Colliders", string.Join(", ",
                        row.colliders.Where(c => c != null).Select(c => c.GetType().Name)));
            }
        }
        int count = rows.Count(row => row.selected && row.gameObject != null);
        using (new EditorGUI.DisabledScope(count == 0))
            if (GUILayout.Button($"{button} ({count})", GUILayout.Height(30))) apply(rows);
    }

    private void Scan()
    {
        Clear();
        foreach (GameObject go in LoadedSceneObjects())
        {
            MeshFilter filter = go.GetComponent<MeshFilter>();
            bool hasVisualMesh = (filter != null && filter.sharedMesh != null) ||
                (go.TryGetComponent(out SkinnedMeshRenderer skinned) && skinned.sharedMesh != null);
            Collider[] colliders = go.GetComponents<Collider>().Where(c => c != null).ToArray();
            MeshCollider[] meshColliders = colliders.OfType<MeshCollider>().ToArray();
            if (filter != null && filter.sharedMesh != null && meshColliders.Any(c => c.sharedMesh != filter.sharedMesh))
            {
                MeshCollider best = meshColliders.FirstOrDefault(c => c.sharedMesh == filter.sharedMesh) ?? meshColliders[0];
                mismatched.Add(NewRow(go, filter, best, colliders));
            }
            if (colliders.Length > 1 || (!hasVisualMesh && colliders.Length > 0))
            {
                MeshCollider best = meshColliders.FirstOrDefault();
                Row row = NewRow(go, filter, best, colliders);
                Collider keeper = ChooseKeeper(colliders);
                if (colliders.Length > 1)
                    foreach (Collider c in colliders) if (c != keeper) row.remove.Add(c);
                multiple.Add(row);
            }
        }
        summary = $"Found {mismatched.Count} objects with mismatched MeshCollider meshes and " +
                  $"{multiple.Count} objects with extra or collider-only components. Objects without colliders are ignored.";
        Repaint();
    }

    private static Row NewRow(GameObject go, MeshFilter filter, MeshCollider meshCollider, Collider[] colliders) =>
        new Row { gameObject = go, filter = filter, meshCollider = meshCollider, colliders = colliders };

    private IEnumerable<GameObject> LoadedSceneObjects()
    {
        if (searchRoot != null)
        {
            foreach (Transform child in searchRoot.GetComponentsInChildren<Transform>(includeInactive))
                yield return child.gameObject;
            yield break;
        }
        for (int sceneIndex = 0; sceneIndex < SceneManager.sceneCount; sceneIndex++)
        {
            Scene scene = SceneManager.GetSceneAt(sceneIndex);
            if (!scene.IsValid() || !scene.isLoaded) continue;
            foreach (GameObject root in scene.GetRootGameObjects())
                foreach (Transform transform in root.GetComponentsInChildren<Transform>(includeInactive))
                    yield return transform.gameObject;
        }
    }

    internal static string RunAutomatic()
    {
        var tool = CreateInstance<SmartSceneColliderRecovery>();
        try
        {
            tool.Scan();
            var rows = tool.mismatched.Concat(tool.multiple).GroupBy(r => r.gameObject).Select(g => g.First()).ToList();
            BackupAffectedScenes(rows);
            int repaired = 0;
            foreach (Row row in rows)
            {
                if (RestorePrefabColliders(row.gameObject)) { repaired++; continue; }
                if (row.filter == null || row.filter.sharedMesh == null) continue;
                foreach (MeshCollider collider in row.gameObject.GetComponents<MeshCollider>())
                {
                    if (collider.sharedMesh == row.filter.sharedMesh) continue;
                    Undo.RecordObject(collider, "Recover Collider Mesh");
                    collider.sharedMesh = row.filter.sharedMesh;
                    PrefabUtility.RecordPrefabInstancePropertyModifications(collider);
                    EditorUtility.SetDirty(collider);
                    repaired++;
                }
            }
            if (rows.Count > 0) EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
            tool.Scan();
            return $"Repaired {repaired} collider references/configurations. {tool.multiple.Count} extra/collider-only objects remain for manual review; automatic recovery does not delete them.";
        }
        finally { DestroyImmediate(tool); }
    }
    private void ApplyMismatch(List<Row> rows) => Apply(rows, "Repair MeshCollider References", row =>
    {
        if (RestorePrefabColliders(row.gameObject)) return;
        if (row.filter == null || row.filter.sharedMesh == null) return;
        MeshCollider collider = row.meshCollider;
        if (collider == null) collider = row.gameObject.GetComponents<MeshCollider>().FirstOrDefault();
        if (collider == null) return;
        Undo.RecordObject(collider, "Repair MeshCollider Reference");
        collider.sharedMesh = row.filter.sharedMesh;
        collider.convex = makeConvex;
        EditorUtility.SetDirty(collider);
    });

    private void ApplyMultiple(List<Row> rows) => Apply(rows, "Clean Up Selected Colliders", row =>
    {
        if (RestorePrefabColliders(row.gameObject)) return;
        foreach (Collider collider in row.remove.Where(c => c != null).ToArray())
            Undo.DestroyObjectImmediate(collider);
    });

    private static Collider ChooseKeeper(Collider[] colliders)
    {
        Collider nonConvex = colliders.OfType<MeshCollider>().FirstOrDefault(c => !c.convex);
        return nonConvex ?? colliders.FirstOrDefault(c => !(c is MeshCollider)) ?? colliders.FirstOrDefault();
    }

    private static string Describe(Collider collider) => collider is MeshCollider mesh
        ? $"MeshCollider | convex={mesh.convex} | mesh={(mesh.sharedMesh == null ? "None" : mesh.sharedMesh.name)}"
        : collider.GetType().Name;
    /// <summary>
    /// Restores collider components only. No transform, renderer, script or other prefab override is reverted.
    /// Returns false when this object is not a prefab instance or its prefab source has no colliders.
    /// </summary>
    private static bool RestorePrefabColliders(GameObject instanceObject)
    {
        if (instanceObject == null || !PrefabUtility.IsPartOfPrefabInstance(instanceObject)) return false;
        GameObject sourceObject = PrefabUtility.GetCorrespondingObjectFromSource(instanceObject);
        if (sourceObject == null) return false;
        Collider[] sourceColliders = sourceObject.GetComponents<Collider>().Where(c => c != null).ToArray();
        if (sourceColliders.Length == 0) return false;

        // Restore collider components removed as instance overrides.
        foreach (Collider sourceCollider in sourceColliders)
        {
            bool exists = instanceObject.GetComponents<Collider>().Any(instanceCollider =>
                PrefabUtility.GetCorrespondingObjectFromSource(instanceCollider) == sourceCollider);
            if (!exists)
                PrefabUtility.RevertRemovedComponent(instanceObject, sourceCollider, InteractionMode.UserAction);
        }

        // Revert values on inherited collider components and remove only instance-added colliders.
        foreach (Collider instanceCollider in instanceObject.GetComponents<Collider>().Where(c => c != null).ToArray())
        {
            Component source = PrefabUtility.GetCorrespondingObjectFromSource(instanceCollider);
            if (source != null)
                PrefabUtility.RevertObjectOverride(instanceCollider, InteractionMode.UserAction);
            else
                Undo.DestroyObjectImmediate(instanceCollider);
        }
        return true;
    }

    private void Apply(List<Row> rows, string undoName, Action<Row> action)
    {
        List<Row> selected = rows.Where(row => row.selected && row.gameObject != null).ToList();
        if (selected.Count == 0) return;
        if (!EditorUtility.DisplayDialog(undoName,
                $"Modify collider components on {selected.Count} selected GameObjects?", "Apply", "Cancel")) return;
        BackupAffectedScenes(selected);
        Undo.IncrementCurrentGroup();
        int group = Undo.GetCurrentGroup();
        Undo.SetCurrentGroupName(undoName);
        foreach (Row row in selected) action(row);
        Undo.CollapseUndoOperations(group);
        foreach (Scene scene in selected.Select(row => row.gameObject.scene).Distinct())
            if (scene.IsValid() && scene.isLoaded) EditorSceneManager.MarkSceneDirty(scene);
        Scan();
    }

    private static void BackupAffectedScenes(IEnumerable<Row> rows)
    {
        foreach (Scene scene in rows.Select(row => row.gameObject.scene).Where(scene => scene.IsValid() && scene.isLoaded)
                     .GroupBy(scene => scene.handle).Select(group => group.First()))
            if (!string.IsNullOrEmpty(scene.path))
                CityToolBackupManager.CreateBackup(scene.path, "Smart_Collider_Recovery");
    }

    private void Clear()
    {
        mismatched.Clear();
        multiple.Clear();
        summary = "Not scanned.";
        Repaint();
    }
}
#endif
