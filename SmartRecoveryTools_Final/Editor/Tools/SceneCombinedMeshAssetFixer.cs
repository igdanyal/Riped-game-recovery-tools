#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Unity.Collections;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.Rendering;

/// <summary>
/// Recovers broken/temporary scene MeshFilter references by finding the original
/// persistent Mesh sub-asset in the Unity Project. Reuses matching mesh assets and saves recovered geometry when required.
/// Put below Assets/Editor.
/// </summary>
public sealed class SceneCombinedMeshAssetFixer : EditorWindow
{
    internal sealed class AutomaticCombinedResult
    {
        internal int colliderRecovered;
        internal int existingMeshesReused;
        internal int newMeshesRequired;
        internal int occurrences;
    }

    private enum MatchQuality { ExactCollider, StrongGeometry, NameGuess, Missing }

    private sealed class RecoveryRecord
    {
        public MeshFilter filter;
        public Mesh currentMesh;
        public Mesh proposedMesh;
        public MatchQuality quality;
        public string reason;
        public bool enabled = true;
        public readonly List<MeshFilter> occurrences = new List<MeshFilter>();
    }

    private sealed class SeparationGroup
    {
        public string displayName;
        public string fingerprint;
        public Mesh previewMesh;
        public Mesh persistentColliderMesh;
        public readonly List<MeshFilter> filters = new List<MeshFilter>();
        public string sourceDescription;
        public bool enabled = true;
        public string status;
    }

    private sealed class UnresolvedCombinedRecord
    {
        public MeshFilter filter;
        public string reason;
        public bool selectedForRemoval;
    }

    private sealed class DuplicateMeshFamily
    {
        public string normalizedName;
        public readonly List<MeshFilter> filters = new List<MeshFilter>();
        public readonly List<Mesh> candidates = new List<Mesh>();
        public Mesh selectedMesh;
        public bool enabled = true;
        public string status;
    }

    [Serializable]
    private sealed class FastMeshIndexData
    {
        public List<string> assetPaths = new List<string>();
    }

    private bool scanAllLoadedScenes = true;
    private GameObject searchRoot;
    private bool includeInactive = true;
    private bool useGeometryConfirmation = true;
    private Vector2 windowScroll;
    private Vector2 scroll;
    private readonly List<RecoveryRecord> records = new List<RecoveryRecord>();
    private static readonly List<Mesh> projectMeshes = new List<Mesh>();
    private static readonly Dictionary<string, List<Mesh>> projectMeshesByTopology =
        new Dictionary<string, List<Mesh>>(StringComparer.Ordinal);
    private static readonly Dictionary<Mesh, string> projectMeshFingerprintCache =
        new Dictionary<Mesh, string>();
    private readonly List<SeparationGroup> separationGroups = new List<SeparationGroup>();
    private readonly List<UnresolvedCombinedRecord> unresolvedCombined = new List<UnresolvedCombinedRecord>();
    private readonly List<DuplicateMeshFamily> duplicateMeshFamilies = new List<DuplicateMeshFamily>();
    private static readonly Dictionary<MeshFilter, Bounds> extractedWorldBounds =
        new Dictionary<MeshFilter, Bounds>();
    private DefaultAsset separatedMeshFolder;
    private Vector2 separationScroll;
    private Vector2 unresolvedScroll;
    private Vector2 duplicateScroll;
    private const string DefaultSeparatedMeshFolder = "Assets/RecoveredMeshAssets/SeparatedMeshes";
    private const string FastIndexPath =
        "Assets/#RecoveredMeshAssets/#recovery_tool/temp/CombinedMeshIndex_v2.json";
    private static readonly HashSet<string> fastRegisteredAssetPaths =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private bool automaticMode;

    private static readonly Color ExactGreen = new Color(0.35f, 0.82f, 0.38f, 0.24f);
    private static readonly Color StrongGreen = new Color(0.62f, 0.86f, 0.42f, 0.22f);
    private static readonly Color GuessOrange = new Color(1f, 0.58f, 0.16f, 0.24f);
    private static readonly Color MissingRed = new Color(1f, 0.25f, 0.25f, 0.23f);

    [MenuItem("Tools/Smart Recovery Tools/2. Combined Mesh Fix", false, 21)]
    private static void Open()
    {
        GetWindow<SceneCombinedMeshAssetFixer>("Combined Mesh Recovery");
    }

    private Vector2 finalizedPageScroll;
    private void OnGUI()
    {
        minSize = new Vector2(560, 420);
        using (new SmartRecoveryUI.WindowScope(this, "Combined Mesh Recovery"))
        {
            finalizedPageScroll = EditorGUILayout.BeginScrollView(finalizedPageScroll);
            try { DrawToolGUI(); }
            finally { EditorGUILayout.EndScrollView(); }
        }
    }

    private void DrawToolGUI()
    {
        windowScroll = EditorGUILayout.BeginScrollView(windowScroll);
        EditorGUILayout.Space(8);
        EditorGUILayout.LabelField("Combined Mesh Recovery", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "Two-phase workflow. Phase 1 scans every combined MeshFilter, recovers safe collider references, then " +
            "separates and assigns everything still combined. Phase 2 manually consolidates numbered duplicate names.",
            MessageType.Info);

        scanAllLoadedScenes = EditorGUILayout.ToggleLeft("Scan all loaded scenes", scanAllLoadedScenes);
        GUI.enabled = !scanAllLoadedScenes;
        searchRoot = (GameObject)EditorGUILayout.ObjectField("Optional Scene Search Root", searchRoot,
            typeof(GameObject), true);
        GUI.enabled = true;
        includeInactive = EditorGUILayout.ToggleLeft("Include inactive objects", includeInactive);

        EditorGUILayout.Space(8);
        EditorGUILayout.LabelField("Phase 1 - Recover Combined Meshes", EditorStyles.boldLabel);
        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("1. Scan & Preview", GUILayout.Height(32))) Scan();
            if (GUILayout.Button("Clear", GUILayout.Height(32))) ClearAllResults();
        }

        DrawLegend();
        DrawRecords();
        DrawPhaseOneUnresolvedRemoval();

        int applicable = records.Count(r => r.enabled && r.filter != null && r.proposedMesh != null);
        GUI.enabled = applicable > 0;
        if (GUILayout.Button($"2. Apply Collider Mesh Recovery ({applicable})", GUILayout.Height(42)))
            ApplyRecoveredMeshes();
        GUI.enabled = true;

        DrawCombinedMeshSeparation();
        DrawDuplicateMeshConsolidation();
        DrawUnresolvedCombinedAudit();
        EditorGUILayout.Space(8);
        EditorGUILayout.EndScrollView();
    }

    private void OnDisable()
    {
        ClearSeparationGroups();
    }

    private void DrawCombinedMeshSeparation()
    {
        EditorGUILayout.Space(10);
        EditorGUILayout.LabelField("Phase 1 - Action 3: Separate Remaining Combined Meshes", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "After Action 2, this finds every MeshFilter that is still combined, separates its visual geometry with " +
            "the correct pivot, reuses compatible recovered meshes, and assigns the result. " +
            "It never scans every mesh asset. If no exact fingerprint exists, it creates one recovered mesh. " +
            "No MeshCollider components are added or changed.",
            MessageType.Info);

        separatedMeshFolder = (DefaultAsset)EditorGUILayout.ObjectField(
            "Separated Mesh Folder", separatedMeshFolder, typeof(DefaultAsset), false);
        if (separatedMeshFolder == null && GUILayout.Button("Use / Create Default #RecoveredMeshAssets Folder"))
            separatedMeshFolder = EnsureDefaultSeparatedMeshFolder();

        if (GUILayout.Button("3. Find, Separate & Assign Every Remaining Combined Mesh", GUILayout.Height(36)))
            RecoverEveryRemainingCombinedMesh();

        if (separationGroups.Count == 0) return;

        int occurrences = separationGroups.Sum(group => group.filters.Count);
        int reusable = separationGroups.Count(group => group.persistentColliderMesh != null);
        EditorGUILayout.HelpBox(
            $"Unique mesh parts: {separationGroups.Count} | Scene occurrences: {occurrences} | " +
            $"Existing Project meshes reusable: {reusable} | " +
            $"New assets required: {separationGroups.Count - reusable}", MessageType.None);

        separationScroll = EditorGUILayout.BeginScrollView(
            separationScroll, GUILayout.MinHeight(180), GUILayout.MaxHeight(360));
        foreach (SeparationGroup group in separationGroups)
        {
            using (new EditorGUILayout.VerticalScope("box"))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    group.enabled = EditorGUILayout.Toggle(group.enabled, GUILayout.Width(18));
                    EditorGUILayout.LabelField(group.displayName, EditorStyles.boldLabel);
                    EditorGUILayout.LabelField($"Uses: {group.filters.Count}", GUILayout.Width(70));
                }
                EditorGUILayout.ObjectField("Preview / Source Mesh",
                    group.persistentColliderMesh != null ? group.persistentColliderMesh : group.previewMesh,
                    typeof(Mesh), false);
                EditorGUILayout.LabelField(group.sourceDescription, EditorStyles.wordWrappedMiniLabel);
                if (!string.IsNullOrEmpty(group.status))
                    EditorGUILayout.LabelField(group.status, EditorStyles.miniLabel);
                if (group.filters.Count > 0)
                    EditorGUILayout.ObjectField("First Scene Object", group.filters[0], typeof(MeshFilter), true);
            }
        }
        EditorGUILayout.EndScrollView();
    }

    private void RecoverEveryRemainingCombinedMesh()
    {
        if (separatedMeshFolder == null) separatedMeshFolder = EnsureDefaultSeparatedMeshFolder();
        FindUniqueCombinedMeshParts();
        if (separationGroups.Any(group => group.enabled && group.filters.Count > 0))
            SeparateSaveAndAssign();
    }

    private void DrawDuplicateMeshConsolidation()
    {
        EditorGUILayout.Space(12);
        EditorGUILayout.LabelField("Phase 2 - Smart Duplicate Mesh Checker", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "Finds scene objects whose names differ only by numeric instance suffixes but reference different mesh " +
            "files. Nothing is selected automatically for Apply: choose the canonical mesh for each enabled family.",
            MessageType.Info);
        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Scan Duplicate Name Families", GUILayout.Height(30))) ScanDuplicateMeshFamilies();
            if (GUILayout.Button("Select First Mesh For All", GUILayout.Height(30))) SelectFirstMeshForAllFamilies();
            GUI.enabled = duplicateMeshFamilies.Any(family => family.enabled && family.selectedMesh != null);
            if (GUILayout.Button("Apply Selected Canonical Meshes", GUILayout.Height(30)))
                ApplyDuplicateMeshFamilies();
            GUI.enabled = true;
        }

        if (duplicateMeshFamilies.Count == 0) return;
        duplicateScroll = EditorGUILayout.BeginScrollView(duplicateScroll,
            GUILayout.MinHeight(160), GUILayout.MaxHeight(340));
        foreach (DuplicateMeshFamily family in duplicateMeshFamilies)
        {
            using (new EditorGUILayout.VerticalScope("box"))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    family.enabled = EditorGUILayout.Toggle(family.enabled, GUILayout.Width(18));
                    EditorGUILayout.LabelField(family.normalizedName, EditorStyles.boldLabel);
                    EditorGUILayout.LabelField($"Objects: {family.filters.Count} | Mesh files: {family.candidates.Count}",
                        GUILayout.Width(190));
                }
                family.selectedMesh = (Mesh)EditorGUILayout.ObjectField(
                    "Selected / Drag Mesh", family.selectedMesh, typeof(Mesh), false);
                EditorGUILayout.LabelField("Scene objects (click to view in Hierarchy):", EditorStyles.miniBoldLabel);
                foreach (MeshFilter filter in family.filters)
                    EditorGUILayout.ObjectField(filter, typeof(MeshFilter), true);
                EditorGUILayout.LabelField("Available mesh assets (click to view, or press Use):",
                    EditorStyles.miniBoldLabel);
                foreach (Mesh candidate in family.candidates)
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUILayout.ObjectField(candidate, typeof(Mesh), false);
                        if (GUILayout.Button("Use", GUILayout.Width(55))) family.selectedMesh = candidate;
                    }
                    if (candidate != null)
                        EditorGUILayout.LabelField(AssetDatabase.GetAssetPath(candidate),
                            EditorStyles.wordWrappedMiniLabel);
                }
                if (!string.IsNullOrEmpty(family.status))
                    EditorGUILayout.LabelField(family.status, EditorStyles.wordWrappedMiniLabel);
            }
        }
        EditorGUILayout.EndScrollView();
    }

    private void DrawUnresolvedCombinedAudit()
    {
        EditorGUILayout.Space(12);
        EditorGUILayout.LabelField("Final Combined-Mesh Audit", EditorStyles.boldLabel);
        if (unresolvedCombined.Count == 0)
        {
            EditorGUILayout.HelpBox("No unresolved combined MeshFilters are currently recorded.", MessageType.None);
            return;
        }
        EditorGUILayout.HelpBox($"Still combined: {unresolvedCombined.Count}. Nothing is silently discarded.",
            MessageType.Warning);
        EditorGUILayout.HelpBox("Optional deletion removes selected GameObjects AND their children, not mesh assets. " +
            "Select only objects you intentionally want to discard. This never runs automatically.", MessageType.Warning);
        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Select All For Removal"))
                foreach (UnresolvedCombinedRecord record in unresolvedCombined) record.selectedForRemoval = true;
            if (GUILayout.Button("Deselect All"))
                foreach (UnresolvedCombinedRecord record in unresolvedCombined) record.selectedForRemoval = false;
        }
        using (new EditorGUI.DisabledScope(!unresolvedCombined.Any(record => record.selectedForRemoval && record.filter != null)))
        {
            if (GUILayout.Button("Remove Selected Unresolved GameObjects...", GUILayout.Height(30)))
                RemoveSelectedUnresolvedObjects();
        }
        unresolvedScroll = EditorGUILayout.BeginScrollView(unresolvedScroll,
            GUILayout.MinHeight(120), GUILayout.MaxHeight(260));
        foreach (UnresolvedCombinedRecord record in unresolvedCombined)
        {
            using (new EditorGUILayout.VerticalScope("box"))
            {
                record.selectedForRemoval = EditorGUILayout.ToggleLeft("Select for removal", record.selectedForRemoval);
                EditorGUILayout.ObjectField("Scene Object", record.filter, typeof(MeshFilter), true);
                EditorGUILayout.LabelField(record.reason, EditorStyles.wordWrappedMiniLabel);
            }
        }
        EditorGUILayout.EndScrollView();
    }

    private void DrawPhaseOneUnresolvedRemoval()
    {
        List<RecoveryRecord> unresolved = records.Where(record => record.proposedMesh == null &&
            record.filter != null).ToList();
        if (unresolved.Count == 0) return;
        EditorGUILayout.HelpBox("Optional removal uses the checked unresolved rows above. " +
            "It deletes their GameObjects and children—not mesh assets. Backup and confirmation are required.",
            MessageType.Warning);
        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Select All Unresolved"))
                foreach (RecoveryRecord record in unresolved) record.enabled = true;
            if (GUILayout.Button("Deselect Unresolved"))
                foreach (RecoveryRecord record in unresolved) record.enabled = false;
        }
        List<MeshFilter> selected = unresolved.Where(record => record.enabled)
            .SelectMany(record => record.occurrences.Count > 0
                ? record.occurrences : new List<MeshFilter> { record.filter })
            .Where(filter => filter != null && IsCombinedOrGeneratedMesh(filter.sharedMesh)).Distinct().ToList();
        using (new EditorGUI.DisabledScope(selected.Count == 0))
        {
            if (GUILayout.Button($"Remove Selected Unresolved GameObjects ({selected.Count})...", GUILayout.Height(32)))
                RemoveSelectedUnresolvedObjects(selected);
        }
    }

    private void RemoveSelectedUnresolvedObjects(IEnumerable<MeshFilter> requestedFilters = null)
    {
        // Revalidate live references: stale preview rows must never delete an already-recovered object or asset.
        IEnumerable<MeshFilter> candidates = requestedFilters ?? unresolvedCombined
            .Where(record => record.selectedForRemoval).Select(record => record.filter);
        List<GameObject> selected = candidates
            .Where(filter => filter != null && !EditorUtility.IsPersistent(filter) &&
                filter.gameObject.scene.IsValid() && filter.gameObject.scene.isLoaded &&
                IsCombinedOrGeneratedMesh(filter.sharedMesh))
            .Select(filter => filter.gameObject).Distinct().ToList();
        List<GameObject> roots = selected.Where(candidate => !selected.Any(other => other != candidate &&
            candidate.transform.IsChildOf(other.transform))).ToList();
        if (roots.Count == 0)
        {
            EditorUtility.DisplayDialog("Nothing To Remove", "The selected objects are no longer unresolved.", "OK");
            return;
        }
        int totalObjects = roots.Sum(root => root.GetComponentsInChildren<Transform>(true).Length);
        string names = string.Join("\n", roots.Take(12).Select(root => GetHierarchyPath(root.transform)));
        if (!EditorUtility.DisplayDialog("Delete Unresolved GameObjects?",
            $"Delete {roots.Count} selected hierarchy roots and their children ({totalObjects} GameObjects total)?\n\n" +
            names + (roots.Count > 12 ? "\n..." : "") +
            "\n\nChildren may contain recovered art. Current scene copies will be backed up first. " +
            "Mesh/prefab asset files are not deleted. Ctrl+Z can undo the scene deletion.", "Back Up And Delete", "Cancel")) return;

        List<Scene> affectedScenes = roots.Select(root => root.scene).Distinct().ToList();
        try
        {
            string folder = SmartRecoveryPaths.EnsureFolder(
                "Assets/#RecoveredMeshAssets/#recovery_tool/temp/UnresolvedRemovalBackups");
            foreach (Scene scene in affectedScenes)
            {
                string path = AssetDatabase.GenerateUniqueAssetPath(folder + "/" +
                    SanitizeFileName(scene.name) + "_" + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff") + ".unity");
                // Save a copy of the current in-memory scene, including unsaved edits, without changing its path.
                if (!EditorSceneManager.SaveScene(scene, path, true))
                    throw new IOException("Unable to back up scene: " + scene.name);
            }
        }
        catch (Exception exception)
        {
            EditorUtility.DisplayDialog("Deletion Cancelled", "Scene backup failed. No objects were removed.\n" +
                exception.Message, "OK");
            return;
        }

        Undo.IncrementCurrentGroup();
        int undoGroup = Undo.GetCurrentGroup();
        Undo.SetCurrentGroupName("Remove Selected Unresolved GameObjects");
        int deleted = 0;
        try
        {
            foreach (GameObject root in roots)
            {
                try { Undo.DestroyObjectImmediate(root); deleted++; }
                catch (Exception exception)
                {
                    // Do not unpack prefab instances or expand the deletion scope to bypass Unity restrictions.
                    Debug.LogWarning("Unresolved object was not removed: " + exception.Message);
                }
            }
        }
        finally
        {
            Undo.CollapseUndoOperations(undoGroup);
            foreach (Scene scene in affectedScenes) EditorSceneManager.MarkSceneDirty(scene);
            ClearAllResults();
            AuditRemainingCombinedMeshes("Still combined after optional unresolved-object removal.");
        }
        EditorUtility.DisplayDialog("Removal Complete", $"Removed {deleted}/{roots.Count} selected hierarchy roots. " +
            "Use Ctrl+Z to undo. Current-scene backups are in the tool's temp/UnresolvedRemovalBackups folder.", "OK");
    }

    private void FindUniqueCombinedMeshParts()
    {
        ClearSeparationGroups();
        unresolvedCombined.Clear();
        extractedWorldBounds.Clear();
        if (!scanAllLoadedScenes && searchRoot == null)
        {
            EditorUtility.DisplayDialog("Scene Root Required",
                "Assign an Optional Scene Search Root or enable Scan all loaded scenes.", "OK");
            return;
        }

        Dictionary<string, SeparationGroup> groupsByFingerprint =
            new Dictionary<string, SeparationGroup>(StringComparer.Ordinal);
        Dictionary<string, Mesh> existingMeshByExtractedFingerprint =
            new Dictionary<string, Mesh>(StringComparer.Ordinal);
        MeshFilter[] filters = CollectMeshFilters();
        LoadFastCandidateMeshes(filters);
        BuildProjectMeshTopologyIndex();

        try
        {
            for (int i = 0; i < filters.Length; i++)
            {
                MeshFilter filter = filters[i];
                if (EditorUtility.DisplayCancelableProgressBar("Finding Unique Combined Mesh Parts",
                    $"{i + 1}/{filters.Length}: {filter.name}",
                    filters.Length == 0 ? 1f : (float)i / filters.Length))
                    break;

                try
                {
                    Mesh combined = filter.sharedMesh;
                    if (!IsCombinedOrGeneratedMesh(combined)) continue;

                if (!TryGetStaticBatchRange(filter.GetComponent<MeshRenderer>(), combined,
                    out int firstSubMesh, out int subMeshCount))
                {
                    AddUnresolved(filter, "No valid static-batch submesh range was available.");
                    Debug.LogWarning($"Combined Mesh Separation skipped '{GetHierarchyPath(filter.transform)}': " +
                                     "no valid serialized/public static-batch submesh range.");
                    continue;
                }

                Mesh workingMesh = ExtractStaticBatchPart(filter, combined, firstSubMesh, subMeshCount);
                string source = $"Batch-extracted with the attached V2 process from submeshes " +
                                $"{firstSubMesh}..{firstSubMesh + subMeshCount - 1}.";

                MeshRenderer sourceRenderer = filter.GetComponent<MeshRenderer>();
                float boundsError = float.PositiveInfinity;
                if (sourceRenderer == null || !RecoveredPlacementMatches(filter, workingMesh, out boundsError))
                {
                    DestroyImmediate(workingMesh);
                    AddUnresolved(filter, $"Extracted mesh did not preserve source submesh placement ({boundsError:F6}).");
                    Debug.LogWarning($"Combined Mesh Separation skipped '{GetHierarchyPath(filter.transform)}': " +
                                     $"source submesh placement validation failed (normalized error {boundsError:F6}).");
                    continue;
                }

                Mesh fingerprintMesh = workingMesh;
                if (fingerprintMesh == null || fingerprintMesh.vertexCount == 0)
                {
                    if (workingMesh != null) DestroyImmediate(workingMesh);
                    AddUnresolved(filter, "Extracted submesh contained no usable vertices.");
                    continue;
                }

                // Do not skip objects merely because Phase 1 could recover them. If their MeshFilter is still
                // combined, Phase 1 Action 3 must process them so no repeated family leaves one occurrence behind.
                string normalizedName = NormalizeRepeatedObjectName(filter.gameObject.name);
                string shapeFingerprint = BuildTransformIndependentShapeFingerprint(workingMesh);
                string familyKey = normalizedName.ToLowerInvariant() + "|SHAPE:" + shapeFingerprint;
                string groupKey = familyKey;
                string fingerprint = null;
                groupsByFingerprint.TryGetValue(groupKey, out SeparationGroup group);

                // Shape matching may ignore pivot/rotation/scale, but sharing a mesh must not change placement.
                // If the canonical mesh would not preserve this occurrence's world bounds, keep an exact subgroup.
                if (group != null)
                {
                    Mesh canonical = group.persistentColliderMesh != null
                        ? group.persistentColliderMesh : group.previewMesh;
                    float canonicalBoundsError;
                    if (canonical == null || !RecoveredPlacementMatches(filter, canonical, out canonicalBoundsError))
                    {
                        fingerprint = BuildGeometryFingerprint(fingerprintMesh);
                        groupKey = familyKey + "|EXACT:" + fingerprint;
                        groupsByFingerprint.TryGetValue(groupKey, out group);
                    }
                }

                if (group == null)
                {
                    if (string.IsNullOrEmpty(fingerprint))
                        fingerprint = BuildGeometryFingerprint(fingerprintMesh);
                    Mesh existingProjectMesh;
                    if (!existingMeshByExtractedFingerprint.TryGetValue(fingerprint, out existingProjectMesh))
                    {
                        existingProjectMesh = FindExistingSeparatedMesh(
                            workingMesh, fingerprint, normalizedName, combined);
                        existingMeshByExtractedFingerprint[fingerprint] = existingProjectMesh;
                    }
                    group = new SeparationGroup
                    {
                        displayName = SanitizeFileName(normalizedName),
                        fingerprint = fingerprint,
                        previewMesh = existingProjectMesh == null ? workingMesh : null,
                        persistentColliderMesh = existingProjectMesh,
                        sourceDescription = existingProjectMesh == null
                            ? source + " No compatible Project mesh was found; a new asset is required. " +
                                $"Repeat family: {normalizedName}. Transform-independent shape confirmed."
                            : "Existing Project mesh matched the extracted geometry and will be reused: " +
                                AssetDatabase.GetAssetPath(existingProjectMesh),
                        status = existingProjectMesh == null ? "Ready - new mesh required" : "Ready - reuse existing mesh"
                    };
                    groupsByFingerprint.Add(groupKey, group);
                    separationGroups.Add(group);
                    if (existingProjectMesh != null && workingMesh != null)
                        DestroyImmediate(workingMesh);
                }
                else if (workingMesh != null)
                {
                    DestroyImmediate(workingMesh);
                }

                    group.filters.Add(filter);
                }
                catch (Exception ex)
                {
                    AddUnresolved(filter, ex.Message);
                    Debug.LogWarning($"Combined Mesh Separation skipped '{GetHierarchyPath(filter.transform)}': " +
                                     ex.Message);
                }
            }
        }
        catch (Exception ex)
        {
            Debug.LogError("Combined Mesh Separation scan failed:\n" + ex);
            EditorUtility.DisplayDialog("Separation Scan Failed", "See the Unity Console for details.", "OK");
        }
        finally
        {
            EditorUtility.ClearProgressBar();
            AuditRemainingCombinedMeshes("Awaiting Phase 1 Action 3 Apply or unresolved during scan.");
            Repaint();
        }

        if (!automaticMode && separationGroups.Count == 0)
            EditorUtility.DisplayDialog("Scan Complete",
                "No safely separable combined meshes were found. Objects require either a different MeshCollider mesh " +
                "or valid static-batch submesh metadata.", "OK");
    }

    private void ScanDuplicateMeshFamilies()
    {
        duplicateMeshFamilies.Clear();
        IEnumerable<IGrouping<string, MeshFilter>> families = CollectMeshFilters()
            .Where(filter => filter != null && filter.sharedMesh != null &&
                             !IsCombinedOrGeneratedMesh(filter.sharedMesh))
            .GroupBy(filter => NormalizeRepeatedObjectName(filter.gameObject.name),
                StringComparer.OrdinalIgnoreCase);

        foreach (IGrouping<string, MeshFilter> grouping in families)
        {
            List<MeshFilter> filters = grouping.ToList();
            if (!filters.Any(filter => HasNumberedRepeatSuffix(filter.gameObject.name))) continue;
            List<Mesh> meshes = filters.Select(filter => filter.sharedMesh).Where(mesh => mesh != null)
                .Distinct()
                .OrderBy(mesh => AssetDatabase.GetAssetPath(mesh), StringComparer.OrdinalIgnoreCase)
                .ThenBy(mesh => mesh.name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (filters.Count < 2 || meshes.Count < 2) continue;
            DuplicateMeshFamily family = new DuplicateMeshFamily { normalizedName = grouping.Key };
            family.filters.AddRange(filters);
            family.candidates.AddRange(meshes);
            duplicateMeshFamilies.Add(family);
        }
        duplicateMeshFamilies.Sort((a, b) => string.Compare(a.normalizedName, b.normalizedName,
            StringComparison.OrdinalIgnoreCase));
        Repaint();
    }

    private void SelectFirstMeshForAllFamilies()
    {
        foreach (DuplicateMeshFamily family in duplicateMeshFamilies)
            family.selectedMesh = family.candidates.FirstOrDefault(mesh => mesh != null);
        Repaint();
    }

    private void ApplyDuplicateMeshFamilies()
    {
        List<DuplicateMeshFamily> ready = duplicateMeshFamilies
            .Where(family => family.enabled && family.selectedMesh != null).ToList();
        if (ready.Count == 0) return;
        CreateLoadedSceneBackups("Smart_Phase2_Duplicate_Mesh_Consolidation");

        Undo.IncrementCurrentGroup();
        int undoGroup = Undo.GetCurrentGroup();
        Undo.SetCurrentGroupName("Consolidate Same Name Mesh Files");
        foreach (DuplicateMeshFamily family in ready)
        {
            int assigned = 0, skipped = 0;
            foreach (MeshFilter filter in family.filters.Where(filter => filter != null))
            {
                MeshRenderer renderer = filter.GetComponent<MeshRenderer>();
                float error = float.PositiveInfinity;
                if (renderer == null || !WorldBoundsMatch(filter.transform, family.selectedMesh.bounds,
                    renderer.bounds, out error))
                {
                    skipped++;
                    continue;
                }
                Undo.RecordObject(filter, "Assign Canonical Duplicate Mesh");
                filter.sharedMesh = family.selectedMesh;
                EditorUtility.SetDirty(filter);
                assigned++;
            }
            family.status = $"Assigned {assigned}/{family.filters.Count}; skipped unsafe placements: {skipped}.";
        }
        Undo.CollapseUndoOperations(undoGroup);
        MarkLoadedScenesDirty();
        Repaint();
    }

    private void SeparateSaveAndAssign()
    {
        List<SeparationGroup> ready = separationGroups
            .Where(group => group.enabled && group.filters.Any(filter => filter != null)).ToList();
        if (ready.Count == 0) return;
        if (!automaticMode) CreateLoadedSceneBackups("Smart_Phase2_Combined_Mesh_Recovery");

        bool requiresOutputFolder = ready.Any(group => group.persistentColliderMesh == null);
        string folderPath = separatedMeshFolder == null ? "" : AssetDatabase.GetAssetPath(separatedMeshFolder);
        if (requiresOutputFolder && (string.IsNullOrEmpty(folderPath) ||
            !AssetDatabase.IsValidFolder(folderPath) ||
            !(folderPath == "Assets" || folderPath.StartsWith("Assets/", StringComparison.Ordinal))))
        {
            EditorUtility.DisplayDialog("Invalid Output Folder",
                "Choose a folder inside Assets for the mesh parts that do not already exist.", "OK");
            return;
        }

        int occurrences = ready.Sum(group => group.filters.Count(filter => filter != null));
        int existingCount = ready.Count(group => group.persistentColliderMesh != null);
        int createRequired = ready.Count - existingCount;
        if (!automaticMode && !EditorUtility.DisplayDialog("Separate Combined Meshes",
            $"Existing Project meshes to reuse: {existingCount}\n" +
            $"New unique mesh assets to create: {createRequired}\n" +
            $"MeshFilter occurrences to assign: {occurrences}\n" +
            $"Output for new assets: {(requiresOutputFolder ? folderPath : "Not required")}\n\n" +
            "Only MeshFilter.sharedMesh will change. Materials, MeshColliders, " +
            "transforms, renderers, children and other components remain unchanged.",
            "Save And Assign", "Cancel")) return;

        Undo.IncrementCurrentGroup();
        int undoGroup = Undo.GetCurrentGroup();
        Undo.SetCurrentGroupName("Separate And Assign Combined Mesh Parts");
        int created = 0, assigned = 0, failed = 0;

        try
        {
            for (int i = 0; i < ready.Count; i++)
            {
                SeparationGroup group = ready[i];
                EditorUtility.DisplayProgressBar("Saving Separated Meshes",
                    $"{i + 1}/{ready.Count}: {group.displayName}",
                    ready.Count == 0 ? 1f : (float)i / ready.Count);
                try
                {
                    Mesh assetMesh = group.persistentColliderMesh;
                    Mesh validationMesh = assetMesh != null ? assetMesh : group.previewMesh;
                    List<MeshFilter> safeTargets = new List<MeshFilter>();
                    foreach (MeshFilter candidate in group.filters.Where(filter => filter != null))
                    {
                        MeshRenderer candidateRenderer = candidate.GetComponent<MeshRenderer>();
                        float candidateError = float.PositiveInfinity;
                        if (validationMesh != null && candidateRenderer != null &&
                            RecoveredPlacementMatches(candidate, validationMesh, out candidateError))
                            safeTargets.Add(candidate);
                        else
                        {
                            failed++;
                            Debug.LogWarning($"Recovery Phase 1 Action 3 left '{GetHierarchyPath(candidate.transform)}' " +
                                             $"unchanged because canonical mesh placement was unsafe " +
                                             $"(error {candidateError:F6}).");
                        }
                    }

                    if (safeTargets.Count == 0)
                    {
                        group.status = "Skipped - no safe scene assignments; no asset created";
                        continue;
                    }

                    if (assetMesh == null)
                    {
                        if (group.previewMesh == null) throw new Exception("Extracted preview mesh is missing.");
                        Mesh copy = CloneMesh(group.previewMesh, group.displayName);
                        copy.hideFlags = HideFlags.None;
                        string assetPath = AssetDatabase.GenerateUniqueAssetPath(
                            (folderPath + "/" + SanitizeFileName(group.displayName) + ".asset").Replace("\\", "/"));
                        AssetDatabase.CreateAsset(copy, assetPath);
                        assetMesh = copy;
                        if (!projectMeshes.Contains(assetMesh)) projectMeshes.Add(assetMesh);
                        fastRegisteredAssetPaths.Add(assetPath);
                        created++;
                    }

                    int groupAssigned = 0;
                    foreach (MeshFilter filter in safeTargets)
                    {
                        try
                        {
                            MeshRenderer renderer = filter.GetComponent<MeshRenderer>();
                            Undo.RecordObject(filter, "Assign Separated Mesh");
                            Material[] originalMaterials = renderer.sharedMaterials;
                            bool rendererEnabled = renderer.enabled;
                            Undo.RecordObject(renderer, "Clear Old Static Batch Metadata");
                            ClearRendererStaticBatchMetadata(renderer);
                            filter.sharedMesh = assetMesh;
                            renderer.sharedMaterials = originalMaterials;
                            renderer.enabled = rendererEnabled;
                            EditorUtility.SetDirty(renderer);
                            EditorUtility.SetDirty(filter);
                            assigned++;
                            groupAssigned++;
                        }
                        catch (Exception occurrenceException)
                        {
                            failed++;
                            Debug.LogError($"Recovery Phase 1 Action 3 could not assign " +
                                           $"'{GetHierarchyPath(filter.transform)}':\n{occurrenceException}");
                        }
                    }
                    group.status = $"{(group.persistentColliderMesh != null ? "Reused" : "Created")}: " +
                                   $"{AssetDatabase.GetAssetPath(assetMesh)} | Assigned {groupAssigned}/{group.filters.Count}";
                }
                catch (Exception ex)
                {
                    failed++;
                    group.status = "Failed - see Console";
                    Debug.LogError($"Failed separating '{group.displayName}':\n{ex}");
                }
            }
        }
        finally
        {
            Undo.CollapseUndoOperations(undoGroup);
            EditorUtility.ClearProgressBar();
            AssetDatabase.SaveAssets();
            SaveFastMeshIndex();
            MarkLoadedScenesDirty();
            AuditRemainingCombinedMeshes("Still references a combined/generated mesh after Phase 1 Action 3 Apply.");
            foreach (RecoveryRecord record in records)
            {
                record.occurrences.RemoveAll(filter => filter == null || !IsCombinedOrGeneratedMesh(filter.sharedMesh));
                if (record.occurrences.Count > 0)
                {
                    record.filter = record.occurrences[0];
                    record.currentMesh = record.filter.sharedMesh;
                }
            }
            records.RemoveAll(record => record.occurrences.Count == 0);
            Repaint();
        }

        if (!automaticMode)
            EditorUtility.DisplayDialog("Combined Mesh Separation Complete",
                $"Existing Project meshes reused: {existingCount}\n" +
                $"New unique mesh assets: {created}\nMeshFilters assigned: {assigned}\n" +
                $"Failed: {failed}\n\nSave the scene now.", "OK");
    }

    private static bool IsCombinedOrGeneratedMesh(Mesh mesh)
    {
        if (mesh == null) return false;
        if (string.IsNullOrEmpty(AssetDatabase.GetAssetPath(mesh))) return true;
        string name = mesh.name.ToLowerInvariant();
        return name.Contains("combined") || name.Contains("static batch") ||
               name.Contains("baked") || name.Contains("meshbaker");
    }

    private static void ClearRendererStaticBatchMetadata(MeshRenderer renderer)
    {
        SerializedObject serialized = new SerializedObject(renderer);
        bool changed = false;

        SerializedProperty root = serialized.FindProperty("m_StaticBatchRoot");
        if (root != null)
        {
            root.objectReferenceValue = null;
            changed = true;
        }

        SerializedProperty info = serialized.FindProperty("m_StaticBatchInfo");
        if (info != null)
        {
            SerializedProperty first = info.FindPropertyRelative("firstSubMesh");
            SerializedProperty count = info.FindPropertyRelative("subMeshCount");
            if (first != null) first.intValue = 0;
            if (count != null) count.intValue = 0;
            changed = true;
        }

        if (changed) serialized.ApplyModifiedProperties();
    }

    private static bool TryGetStaticBatchRange(MeshRenderer renderer, Mesh mesh,
        out int firstSubMesh, out int subMeshCount)
    {
        firstSubMesh = 0;
        subMeshCount = renderer == null || mesh == null ? 0 :
            Mathf.Clamp(renderer.sharedMaterials.Length, 1, Mathf.Max(1, mesh.subMeshCount));
        if (renderer == null || mesh == null) return false;

        SerializedObject serialized = new SerializedObject(renderer);
        SerializedProperty info = serialized.FindProperty("m_StaticBatchInfo");
        bool foundSerializedRange = false;
        if (info != null)
        {
            SerializedProperty first = info.FindPropertyRelative("firstSubMesh");
            SerializedProperty count = info.FindPropertyRelative("subMeshCount");
            if (first != null && count != null && count.intValue > 0)
            {
                firstSubMesh = first.intValue;
                subMeshCount = count.intValue;
                foundSerializedRange = true;
            }
        }

        if (foundSerializedRange && subMeshCount > 0 && firstSubMesh >= 0 &&
            firstSubMesh + subMeshCount <= mesh.subMeshCount)
            return true;

        // Without serialized or live batching metadata, guessing submesh zero can recover the wrong object.
        if (!renderer.isPartOfStaticBatch) return false;

        firstSubMesh = 0;
        try
        {
            int publicStart = renderer.subMeshStartIndex;
            if (publicStart >= 0 && publicStart < mesh.subMeshCount) firstSubMesh = publicStart;
        }
        catch { }

        int available = mesh.subMeshCount - firstSubMesh;
        subMeshCount = Mathf.Clamp(renderer.sharedMaterials.Length, 1, Mathf.Max(1, available));
        return firstSubMesh >= 0 && subMeshCount > 0 && firstSubMesh + subMeshCount <= mesh.subMeshCount;
    }

    private static Mesh ExtractStaticBatchPart(MeshFilter filter, Mesh source,
        int firstSubMesh, int subMeshCount)
    {
        if (source == null) throw new Exception("Combined source mesh is missing.");
        if (firstSubMesh < 0 || subMeshCount <= 0 || firstSubMesh + subMeshCount > source.subMeshCount)
            throw new Exception("Static batch submesh range is invalid for the combined mesh.");

        Mesh.MeshDataArray array = MeshUtility.AcquireReadOnlyMeshData(source);
        try
        {
            if (array.Length == 0) throw new Exception("Unity returned no readable mesh data.");
            Mesh.MeshData data = array[0];
            int vertexCount = data.vertexCount;

            Vector3[] sourceVertices = ReadVertices(data, vertexCount);
            Vector3[] sourceNormals = ReadNormals(data, vertexCount);
            Vector4[] sourceTangents = ReadTangents(data, vertexCount);
            Color32[] sourceColors = ReadColors(data, vertexCount);
            Vector4[][] sourceUvs = new Vector4[8][];
            for (int channel = 0; channel < 8; channel++)
                sourceUvs[channel] = ReadUv(data, vertexCount, channel);

            List<int[]> trianglesBySubMesh = new List<int[]>();
            SortedSet<int> used = new SortedSet<int>();
            for (int localSub = 0; localSub < subMeshCount; localSub++)
            {
                int sourceSub = firstSubMesh + localSub;
                SubMeshDescriptor descriptor = data.GetSubMesh(sourceSub);
                if (descriptor.topology != MeshTopology.Triangles)
                {
                    throw new Exception("Selected batch range includes unsupported non-triangle topology.");
                }

                NativeArray<int> indices = new NativeArray<int>(
                    descriptor.indexCount, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
                try
                {
                    data.GetIndices(indices, sourceSub, true);
                    int[] triangles = indices.ToArray();
                    trianglesBySubMesh.Add(triangles);
                    foreach (int index in triangles) used.Add(index);
                }
                finally { indices.Dispose(); }
            }

            if (used.Count == 0) throw new Exception("Detected submesh range contains no triangles.");
            Dictionary<int, int> remap = new Dictionary<int, int>();
            int next = 0; foreach (int oldIndex in used) remap[oldIndex] = next++;

            // Static batches with a root use root-local coordinates; rootless batches use world coordinates.
            Matrix4x4 sourceToWorld = Matrix4x4.identity;
            MeshRenderer batchRenderer = filter.GetComponent<MeshRenderer>();
            if (batchRenderer != null)
            {
                SerializedProperty rootProperty = new SerializedObject(batchRenderer).FindProperty("m_StaticBatchRoot");
                UnityEngine.Object rootObject = rootProperty == null ? null : rootProperty.objectReferenceValue;
                Transform batchRoot = rootObject as Transform;
                if (batchRoot == null && rootObject is GameObject rootGameObject) batchRoot = rootGameObject.transform;
                if (batchRoot != null) sourceToWorld = batchRoot.localToWorldMatrix;
            }
            if (Mathf.Abs(filter.transform.localToWorldMatrix.determinant) < 0.000000000001f)
                throw new Exception("Cannot preserve the pivot of an object with a singular transform (zero scale).");
            Matrix4x4 combinedToLocal = filter.transform.worldToLocalMatrix * sourceToWorld;
            Matrix4x4 normalMatrix = combinedToLocal.inverse.transpose;
            Bounds sourceWorldBounds = new Bounds(sourceToWorld.MultiplyPoint3x4(sourceVertices[used.Min]), Vector3.zero);
            foreach (int sourceIndex in used)
                sourceWorldBounds.Encapsulate(sourceToWorld.MultiplyPoint3x4(sourceVertices[sourceIndex]));
            float positionTolerance = Mathf.Max(0.0001f, sourceWorldBounds.size.magnitude * 0.00001f);

            Vector3[] vertices = new Vector3[used.Count];
            Vector3[] normals = sourceNormals == null ? null : new Vector3[used.Count];
            Vector4[] tangents = sourceTangents == null ? null : new Vector4[used.Count];
            Color32[] colors = sourceColors == null ? null : new Color32[used.Count];
            foreach (KeyValuePair<int, int> pair in remap)
            {
                int oldIndex = pair.Key, newIndex = pair.Value;
                vertices[newIndex] = combinedToLocal.MultiplyPoint3x4(sourceVertices[oldIndex]);
                Vector3 expectedWorld = sourceToWorld.MultiplyPoint3x4(sourceVertices[oldIndex]);
                Vector3 recoveredWorld = filter.transform.localToWorldMatrix.MultiplyPoint3x4(vertices[newIndex]);
                if ((expectedWorld - recoveredWorld).magnitude > positionTolerance)
                    throw new Exception("Extraction failed the per-vertex world-position round-trip check.");
                if (normals != null) normals[newIndex] = normalMatrix.MultiplyVector(sourceNormals[oldIndex]).normalized;
                if (tangents != null)
                {
                    Vector4 old = sourceTangents[oldIndex];
                    Vector3 direction = combinedToLocal.MultiplyVector(new Vector3(old.x, old.y, old.z)).normalized;
                    tangents[newIndex] = new Vector4(direction.x, direction.y, direction.z,
                        old.w * (combinedToLocal.determinant < 0f ? -1f : 1f));
                }
                if (colors != null) colors[newIndex] = sourceColors[oldIndex];
            }

            Mesh result = new Mesh
            {
                name = SanitizeFileName(filter.gameObject.name) + "_Recovered",
                hideFlags = HideFlags.HideAndDontSave,
                indexFormat = used.Count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16
            };
            result.vertices = vertices;
            if (normals != null) result.normals = normals;
            if (tangents != null) result.tangents = tangents;
            if (colors != null) result.colors32 = colors;

            for (int channel = 0; channel < sourceUvs.Length; channel++)
            {
                if (sourceUvs[channel] == null) continue;
                List<Vector4> uv = Enumerable.Repeat(Vector4.zero, used.Count).ToList();
                foreach (KeyValuePair<int, int> pair in remap) uv[pair.Value] = sourceUvs[channel][pair.Key];
                result.SetUVs(channel, uv);
            }

            result.subMeshCount = subMeshCount;
            for (int sub = 0; sub < trianglesBySubMesh.Count; sub++)
            {
                int[] sourceTriangles = trianglesBySubMesh[sub];
                int[] triangles = new int[sourceTriangles.Length];
                for (int i = 0; i < sourceTriangles.Length; i++) triangles[i] = remap[sourceTriangles[i]];
                result.SetTriangles(triangles, sub, false);
            }
            if (normals == null) result.RecalculateNormals();
            result.RecalculateBounds();
            extractedWorldBounds[filter] = sourceWorldBounds;
            return result;
        }
        finally { array.Dispose(); }
    }

    private static Vector3[] ReadVertices(Mesh.MeshData data, int count)
    {
        NativeArray<Vector3> values = new NativeArray<Vector3>(count, Allocator.Temp,
            NativeArrayOptions.UninitializedMemory);
        try { data.GetVertices(values); return values.ToArray(); }
        finally { values.Dispose(); }
    }

    private static Vector3[] ReadNormals(Mesh.MeshData data, int count)
    {
        if (!data.HasVertexAttribute(VertexAttribute.Normal)) return null;
        NativeArray<Vector3> values = new NativeArray<Vector3>(count, Allocator.Temp,
            NativeArrayOptions.UninitializedMemory);
        try { data.GetNormals(values); return values.ToArray(); }
        finally { values.Dispose(); }
    }

    private static Vector4[] ReadTangents(Mesh.MeshData data, int count)
    {
        if (!data.HasVertexAttribute(VertexAttribute.Tangent)) return null;
        NativeArray<Vector4> values = new NativeArray<Vector4>(count, Allocator.Temp,
            NativeArrayOptions.UninitializedMemory);
        try { data.GetTangents(values); return values.ToArray(); }
        finally { values.Dispose(); }
    }

    private static Color32[] ReadColors(Mesh.MeshData data, int count)
    {
        if (!data.HasVertexAttribute(VertexAttribute.Color)) return null;
        NativeArray<Color32> values = new NativeArray<Color32>(count, Allocator.Temp,
            NativeArrayOptions.UninitializedMemory);
        try { data.GetColors(values); return values.ToArray(); }
        finally { values.Dispose(); }
    }

    private static Vector4[] ReadUv(Mesh.MeshData data, int count, int channel)
    {
        VertexAttribute attribute = (VertexAttribute)((int)VertexAttribute.TexCoord0 + channel);
        if (!data.HasVertexAttribute(attribute)) return null;
        NativeArray<Vector4> values = new NativeArray<Vector4>(count, Allocator.Temp,
            NativeArrayOptions.UninitializedMemory);
        try { data.GetUVs(channel, values); return values.ToArray(); }
        finally { values.Dispose(); }
    }

    private static Mesh CloneMesh(Mesh source, string name)
    {
        if (source == null) return null;
        Mesh copy = Instantiate(source);
        copy.name = SanitizeFileName(name);
        copy.hideFlags = HideFlags.HideAndDontSave;
        return copy;
    }

    private static string BuildGeometryFingerprint(Mesh mesh)
    {
        try
        {
            unchecked
            {
                ulong hash = 1469598103934665603UL;
                Action<int> add = value =>
                {
                    hash ^= (uint)value;
                    hash *= 1099511628211UL;
                };

                add(mesh.vertexCount);
                add(mesh.subMeshCount);

                // Vertex/index order frequently changes between Unity static batches. Hash sorted,
                // quantized local-space positions so identical repeated geometry remains detectable.
                using (Mesh.MeshDataArray meshDataArray = MeshUtility.AcquireReadOnlyMeshData(mesh))
                {
                if (meshDataArray.Length == 0) throw new Exception("Unity returned no mesh data.");
                Mesh.MeshData meshData = meshDataArray[0];
                Vector3[] vertices;
                NativeArray<Vector3> vertexData = new NativeArray<Vector3>(meshData.vertexCount,
                    Allocator.Temp, NativeArrayOptions.UninitializedMemory);
                try
                {
                    meshData.GetVertices(vertexData);
                    vertices = vertexData.ToArray();
                }
                finally { vertexData.Dispose(); }
                string[] vertexKeys = vertices.Select(QuantizedVertexKey)
                    .OrderBy(key => key, StringComparer.Ordinal).ToArray();
                foreach (string key in vertexKeys)
                {
                    foreach (char character in key) add(character);
                }

                for (int sub = 0; sub < mesh.subMeshCount; sub++)
                {
                    SubMeshDescriptor descriptor = meshData.GetSubMesh(sub);
                    NativeArray<int> indexData = new NativeArray<int>(descriptor.indexCount,
                        Allocator.Temp, NativeArrayOptions.UninitializedMemory);
                    int[] triangles;
                    try
                    {
                        meshData.GetIndices(indexData, sub, true);
                        triangles = indexData.ToArray();
                    }
                    finally { indexData.Dispose(); }
                    add(triangles.Length);
                    List<string> triangleKeys = new List<string>(triangles.Length / 3);
                    for (int index = 0; index + 2 < triangles.Length; index += 3)
                    {
                        string[] corners =
                        {
                            QuantizedVertexKey(vertices[triangles[index]]),
                            QuantizedVertexKey(vertices[triangles[index + 1]]),
                            QuantizedVertexKey(vertices[triangles[index + 2]])
                        };
                        Array.Sort(corners, StringComparer.Ordinal);
                        triangleKeys.Add(corners[0] + "|" + corners[1] + "|" + corners[2]);
                    }
                    triangleKeys.Sort(StringComparer.Ordinal);
                    foreach (string triangle in triangleKeys)
                        foreach (char character in triangle) add(character);
                }
                long indexCount = 0;
                for (int sub = 0; sub < mesh.subMeshCount; sub++)
                    indexCount += (long)mesh.GetIndexCount(sub);
                string result = hash.ToString("X16") + ":V" + mesh.vertexCount + ":I" + indexCount +
                                ":S" + mesh.subMeshCount;
                return result;
                }
            }
        }
        catch
        {
            string guid = "";
            long localId = 0;
            if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(mesh, out guid, out localId))
                return "ASSET:" + guid + ":" + localId;
            return "INSTANCE:" + mesh.GetInstanceID();
        }
    }

    private static string QuantizedVertexKey(Vector3 vertex)
    {
        return Mathf.RoundToInt(vertex.x * 10000f) + "," +
               Mathf.RoundToInt(vertex.y * 10000f) + "," +
               Mathf.RoundToInt(vertex.z * 10000f);
    }

    private static string BuildTransformIndependentShapeFingerprint(Mesh mesh)
    {
        try
        {
            using (Mesh.MeshDataArray array = MeshUtility.AcquireReadOnlyMeshData(mesh))
            {
                if (array.Length == 0) return BuildGeometryFingerprint(mesh);
                Mesh.MeshData data = array[0];
                NativeArray<Vector3> nativeVertices = new NativeArray<Vector3>(data.vertexCount,
                    Allocator.Temp, NativeArrayOptions.UninitializedMemory);
                Vector3[] vertices;
                try
                {
                    data.GetVertices(nativeVertices);
                    vertices = nativeVertices.ToArray();
                }
                finally { nativeVertices.Dispose(); }

                List<Vector3> triangleEdges = new List<Vector3>();
                float largestEdge = 0f;
                long indexCount = 0;
                for (int sub = 0; sub < mesh.subMeshCount; sub++)
                {
                    SubMeshDescriptor descriptor = data.GetSubMesh(sub);
                    if (descriptor.topology != MeshTopology.Triangles) continue;
                    NativeArray<int> indices = new NativeArray<int>(descriptor.indexCount,
                        Allocator.Temp, NativeArrayOptions.UninitializedMemory);
                    try
                    {
                        data.GetIndices(indices, sub, true);
                        indexCount += indices.Length;
                        for (int index = 0; index + 2 < indices.Length; index += 3)
                        {
                            float a = Vector3.Distance(vertices[indices[index]], vertices[indices[index + 1]]);
                            float b = Vector3.Distance(vertices[indices[index + 1]], vertices[indices[index + 2]]);
                            float c = Vector3.Distance(vertices[indices[index + 2]], vertices[indices[index]]);
                            float[] sorted = { a, b, c };
                            Array.Sort(sorted);
                            triangleEdges.Add(new Vector3(sorted[0], sorted[1], sorted[2]));
                            largestEdge = Mathf.Max(largestEdge, sorted[2]);
                        }
                    }
                    finally { indices.Dispose(); }
                }

                if (triangleEdges.Count == 0 || largestEdge <= 0.0000001f)
                    return BuildGeometryFingerprint(mesh);

                // Static batching may duplicate vertices and introduce small matrix round-off differences.
                // A 0.1% normalized edge tolerance is strict enough to separate different art meshes while
                // allowing repeated instances of the same source mesh to share a family.
                const float shapePrecision = 1000f;
                string[] normalizedTriangles = triangleEdges.Select(edges =>
                    Mathf.RoundToInt(edges.x / largestEdge * shapePrecision) + "," +
                    Mathf.RoundToInt(edges.y / largestEdge * shapePrecision) + "," +
                    Mathf.RoundToInt(edges.z / largestEdge * shapePrecision))
                    .OrderBy(value => value, StringComparer.Ordinal).ToArray();

                unchecked
                {
                    ulong hash = 1469598103934665603UL;
                    foreach (string triangle in normalizedTriangles)
                    {
                        foreach (char character in triangle)
                        {
                            hash ^= character;
                            hash *= 1099511628211UL;
                        }
                    }
                    // Deliberately omit vertexCount: identical triangles can be represented with split/duplicated
                    // vertices after static batching. Index/triangle counts and the edge-pattern hash remain required.
                    return hash.ToString("X16") + ":I" + indexCount + ":T" + triangleEdges.Count;
                }
            }
        }
        catch
        {
            return BuildGeometryFingerprint(mesh);
        }
    }

    private static string NormalizeRepeatedObjectName(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "SeparatedMesh";
        string result = value.Trim();
        result = System.Text.RegularExpressions.Regex.Replace(result,
            @"(?:\s*\(\s*\d+\s*\)|[ _.-]*\d+|[ _.-]+(?:copy|clone))+\s*$", "",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return string.IsNullOrWhiteSpace(result) ? value.Trim() : result.Trim();
    }

    private static bool HasNumberedRepeatSuffix(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        return System.Text.RegularExpressions.Regex.IsMatch(value.Trim(),
            @"(?:\(\s*\d+\s*\)|[ _.-]\d+|\d+)\s*$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    private static bool RecoveredPlacementMatches(MeshFilter filter, Mesh mesh, out float normalizedError)
    {
        normalizedError = float.PositiveInfinity;
        if (filter == null || mesh == null || !extractedWorldBounds.TryGetValue(filter,
            out Bounds expectedWorldBounds)) return false;
        // Transform actual vertices, not a local AABB: rotating an AABB inflates its extents.
        Bounds predicted;
        using (Mesh.MeshDataArray array = MeshUtility.AcquireReadOnlyMeshData(mesh))
        {
            Vector3[] vertices = ReadVertices(array[0], array[0].vertexCount);
            if (vertices.Length == 0) return false;
            Matrix4x4 localToWorld = filter.transform.localToWorldMatrix;
            predicted = new Bounds(localToWorld.MultiplyPoint3x4(vertices[0]), Vector3.zero);
            for (int index = 1; index < vertices.Length; index++)
                predicted.Encapsulate(localToWorld.MultiplyPoint3x4(vertices[index]));
        }
        float scale = Mathf.Max(0.001f, expectedWorldBounds.size.magnitude);
        normalizedError = ((predicted.center - expectedWorldBounds.center).magnitude +
                           (predicted.size - expectedWorldBounds.size).magnitude) / scale;
        return normalizedError <= 0.005f;
    }

    private static bool WorldBoundsMatch(Transform transform, Bounds localMeshBounds,
        Bounds expectedWorldBounds, out float normalizedError)
    {
        Bounds predicted = TransformBounds(transform.localToWorldMatrix, localMeshBounds);
        float scale = Mathf.Max(0.001f, expectedWorldBounds.size.magnitude);
        normalizedError = ((predicted.center - expectedWorldBounds.center).magnitude +
                           (predicted.size - expectedWorldBounds.size).magnitude) / scale;
        return normalizedError <= 0.005f;
    }

    private static Bounds TransformBounds(Matrix4x4 matrix, Bounds bounds)
    {
        Vector3 center = matrix.MultiplyPoint3x4(bounds.center);
        Vector3 extents = bounds.extents;
        Vector3 x = matrix.MultiplyVector(new Vector3(extents.x, 0f, 0f));
        Vector3 y = matrix.MultiplyVector(new Vector3(0f, extents.y, 0f));
        Vector3 z = matrix.MultiplyVector(new Vector3(0f, 0f, extents.z));
        Vector3 worldExtents = new Vector3(
            Mathf.Abs(x.x) + Mathf.Abs(y.x) + Mathf.Abs(z.x),
            Mathf.Abs(x.y) + Mathf.Abs(y.y) + Mathf.Abs(z.y),
            Mathf.Abs(x.z) + Mathf.Abs(y.z) + Mathf.Abs(z.z));
        return new Bounds(center, worldExtents * 2f);
    }

    private static DefaultAsset EnsureDefaultSeparatedMeshFolder() =>
        SmartRecoveryPaths.FolderAsset(DefaultSeparatedMeshFolder);

    private static string SanitizeFileName(string value)
    {
        string result = string.IsNullOrWhiteSpace(value) ? "SeparatedMesh" : value.Trim();
        foreach (char c in Path.GetInvalidFileNameChars()) result = result.Replace(c, '_');
        return result;
    }

    private void ClearAllResults()
    {
        records.Clear();
        ClearSeparationGroups();
        duplicateMeshFamilies.Clear();
        unresolvedCombined.Clear();
        Repaint();
    }

    private void ClearSeparationGroups()
    {
        foreach (SeparationGroup group in separationGroups)
        {
            if (group.previewMesh != null && string.IsNullOrEmpty(AssetDatabase.GetAssetPath(group.previewMesh)))
                DestroyImmediate(group.previewMesh);
        }
        separationGroups.Clear();
    }

    private static void DrawLegend()
    {
        EditorGUILayout.Space(5);
        using (new EditorGUILayout.HorizontalScope())
        {
            DrawLegendBox(ExactGreen, "Green: MeshCollider exact");
            DrawLegendBox(StrongGreen, "Light green: geometry confirmed");
            DrawLegendBox(GuessOrange, "Orange: name guess");
            DrawLegendBox(MissingRed, "Red: unresolved");
        }
    }

    private static void DrawLegendBox(Color color, string label)
    {
        Color old = GUI.backgroundColor;
        GUI.backgroundColor = color;
        GUILayout.Box(label, GUILayout.ExpandWidth(true));
        GUI.backgroundColor = old;
    }

    private void DrawRecords()
    {
        if (records.Count == 0) return;
        EditorGUILayout.Space(5);
        int found = records.Count(r => r.proposedMesh != null);
        EditorGUILayout.LabelField($"Results: {found} found, {records.Count - found} unresolved", EditorStyles.boldLabel);
        scroll = EditorGUILayout.BeginScrollView(scroll, GUILayout.MinHeight(230), GUILayout.MaxHeight(430));
        foreach (RecoveryRecord record in records)
        {
            Color old = GUI.backgroundColor;
            GUI.backgroundColor = GetQualityColor(record.quality);
            using (new EditorGUILayout.VerticalScope("box"))
            {
                GUI.backgroundColor = old;
                using (new EditorGUILayout.HorizontalScope())
                {
                    record.enabled = EditorGUILayout.Toggle(record.enabled, GUILayout.Width(18));
                    EditorGUILayout.ObjectField("Scene Object", record.filter, typeof(MeshFilter), true);
                }
                EditorGUILayout.ObjectField("Current Mesh", record.currentMesh, typeof(Mesh), false);
                record.proposedMesh = (Mesh)EditorGUILayout.ObjectField(
                    "Proposed Project Mesh", record.proposedMesh, typeof(Mesh), false);
                EditorGUILayout.LabelField(record.reason, EditorStyles.wordWrappedMiniLabel);
                int uses = record.occurrences.Count > 0 ? record.occurrences.Count : 1;
                EditorGUILayout.LabelField($"Repeated scene uses assigned together: {uses}", EditorStyles.miniLabel);
                if (record.proposedMesh != null)
                    EditorGUILayout.LabelField(AssetDatabase.GetAssetPath(record.proposedMesh), EditorStyles.miniLabel);
            }
            GUI.backgroundColor = old;
        }
        EditorGUILayout.EndScrollView();
    }

    private void Scan()
    {
        records.Clear();
        projectMeshes.Clear();
        if (!scanAllLoadedScenes && searchRoot == null)
        {
            EditorUtility.DisplayDialog("Scene Root Required",
                "Assign an Optional Scene Search Root or enable Scan all loaded scenes.", "OK");
            return;
        }

        try
        {
            MeshFilter[] filters = CollectMeshFilters();
            Dictionary<Mesh, RecoveryRecord> colliderGroups = new Dictionary<Mesh, RecoveryRecord>();
            for (int i = 0; i < filters.Length; i++)
            {
                MeshFilter filter = filters[i];
                EditorUtility.DisplayProgressBar("Recovering Mesh References",
                    $"Checking {i + 1}/{filters.Length}: {filter.name}", filters.Length == 0 ? 1 : (float)i / filters.Length);

                Mesh current = filter.sharedMesh;
                if (!IsCombinedOrGeneratedMesh(current)) continue;

                Mesh colliderMesh = SelectBestPhaseOneColliderMesh(filter, current, out string colliderReason);
                if (colliderMesh == null)
                {
                    RecoveryRecord unresolved = new RecoveryRecord
                    {
                        filter = filter,
                        currentMesh = current,
                        quality = MatchQuality.Missing,
                        enabled = false,
                        reason = "PHASE 1 UNRESOLVED: " + colliderReason
                    };
                    unresolved.occurrences.Add(filter);
                    records.Add(unresolved);
                    continue;
                }

                if (!colliderGroups.TryGetValue(colliderMesh, out RecoveryRecord recovery))
                {
                    recovery = new RecoveryRecord
                    {
                        filter = filter,
                        currentMesh = current,
                        proposedMesh = colliderMesh,
                        quality = MatchQuality.ExactCollider,
                        reason = "CONFIRMED: " + colliderReason
                    };
                    colliderGroups.Add(colliderMesh, recovery);
                    records.Add(recovery);
                }
                recovery.occurrences.Add(filter);
            }
            records.Sort((a, b) => a.quality != b.quality
                ? a.quality.CompareTo(b.quality)
                : string.Compare(GetHierarchyPath(a.filter.transform), GetHierarchyPath(b.filter.transform),
                    StringComparison.OrdinalIgnoreCase));

            if (!automaticMode && records.Count == 0)
                EditorUtility.DisplayDialog("Scan Complete",
                    "No combined MeshFilters with an exact persistent MeshCollider mesh were found. " +
                    "Use Phase 1 Action 3 for the remaining combined objects.", "OK");
        }
        catch (Exception ex)
        {
            Debug.LogError("Mesh Reference Recovery scan failed:\n" + ex);
            EditorUtility.DisplayDialog("Scan Failed", "See the Unity Console for full details.", "OK");
        }
        finally
        {
            EditorUtility.ClearProgressBar();
            Repaint();
        }
    }

    private void LoadFastCandidateMeshes(IEnumerable<MeshFilter> sceneFilters)
    {
        projectMeshes.Clear();
        fastRegisteredAssetPaths.Clear();
        HashSet<Mesh> unique = new HashSet<Mesh>();

        Action<Mesh> add = mesh =>
        {
            if (mesh != null && !string.IsNullOrEmpty(AssetDatabase.GetAssetPath(mesh))) unique.Add(mesh);
        };

        foreach (MeshFilter filter in sceneFilters ?? Enumerable.Empty<MeshFilter>())
        {
            if (filter == null) continue;
            add(filter.sharedMesh);
            MeshCollider collider = filter.GetComponent<MeshCollider>();
            if (collider != null) add(collider.sharedMesh);
        }

        string diskPath = ToDiskPath(FastIndexPath);
        if (File.Exists(diskPath))
        {
            try
            {
                FastMeshIndexData cache = JsonUtility.FromJson<FastMeshIndexData>(File.ReadAllText(diskPath));
                foreach (string path in cache?.assetPaths ?? new List<string>())
                {
                    if (!IsRecoveredMeshPath(path)) continue;
                    fastRegisteredAssetPaths.Add(path);
                    foreach (Mesh mesh in AssetDatabase.LoadAllAssetsAtPath(path).OfType<Mesh>()) add(mesh);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("Recovery mesh index could not be read and will be rebuilt: " + ex.Message);
            }
        }

        projectMeshes.AddRange(unique);
    }

    private static bool IsRecoveredMeshPath(string path)
    {
        return !string.IsNullOrEmpty(path) &&
               path.StartsWith(DefaultSeparatedMeshFolder + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static void SaveFastMeshIndex()
    {
        FastMeshIndexData data = new FastMeshIndexData();
        data.assetPaths = fastRegisteredAssetPaths
            .Where(IsRecoveredMeshPath)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        string diskPath = ToDiskPath(FastIndexPath);
        Directory.CreateDirectory(Path.GetDirectoryName(diskPath));
        File.WriteAllText(diskPath, JsonUtility.ToJson(data, true));
    }

    private static string ToDiskPath(string assetPath)
    {
        string projectRoot = Directory.GetParent(Application.dataPath).FullName;
        return Path.Combine(projectRoot, assetPath.Replace('/', Path.DirectorySeparatorChar));
    }

    private void BuildProjectMeshTopologyIndex()
    {
        projectMeshesByTopology.Clear();
        projectMeshFingerprintCache.Clear();
        foreach (Mesh mesh in projectMeshes)
        {
            string key = BuildTopologyKey(mesh);
            if (string.IsNullOrEmpty(key)) continue;
            if (!projectMeshesByTopology.TryGetValue(key, out List<Mesh> meshes))
            {
                meshes = new List<Mesh>();
                projectMeshesByTopology.Add(key, meshes);
            }
            meshes.Add(mesh);
        }
    }

    private static string BuildTopologyKey(Mesh mesh)
    {
        if (mesh == null) return string.Empty;
        try
        {
            StringBuilder key = new StringBuilder(64);
            key.Append(mesh.vertexCount).Append(':').Append(mesh.subMeshCount).Append(':');
            for (int subMesh = 0; subMesh < mesh.subMeshCount; subMesh++)
                key.Append((int)mesh.GetTopology(subMesh)).Append(',')
                    .Append(mesh.GetIndexCount(subMesh)).Append(';');
            return key.ToString();
        }
        catch { return string.Empty; }
    }

    private MeshFilter[] CollectMeshFilters()
    {
        if (!scanAllLoadedScenes)
            return searchRoot == null
                ? new MeshFilter[0]
                : searchRoot.GetComponentsInChildren<MeshFilter>(includeInactive);

        List<MeshFilter> result = new List<MeshFilter>();
        for (int sceneIndex = 0; sceneIndex < SceneManager.sceneCount; sceneIndex++)
        {
            Scene scene = SceneManager.GetSceneAt(sceneIndex);
            if (!scene.IsValid() || !scene.isLoaded) continue;
            foreach (GameObject root in scene.GetRootGameObjects())
                result.AddRange(root.GetComponentsInChildren<MeshFilter>(includeInactive));
        }
        return result.Where(filter => filter != null)
            .Distinct()
            .OrderBy(filter => GetHierarchyPath(filter.transform), StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private void AddUnresolved(MeshFilter filter, string reason)
    {
        if (filter == null) return;
        UnresolvedCombinedRecord existing = unresolvedCombined.FirstOrDefault(record => record.filter == filter);
        if (existing == null)
            unresolvedCombined.Add(new UnresolvedCombinedRecord { filter = filter, reason = reason });
        else if (!string.IsNullOrEmpty(reason))
            existing.reason = reason;
    }

    private void AuditRemainingCombinedMeshes(string fallbackReason)
    {
        Dictionary<MeshFilter, string> previous = unresolvedCombined
            .Where(record => record != null && record.filter != null)
            .GroupBy(record => record.filter)
            .ToDictionary(group => group.Key, group => group.Last().reason);
        unresolvedCombined.Clear();
        foreach (MeshFilter filter in CollectMeshFilters())
        {
            if (filter == null || !IsCombinedOrGeneratedMesh(filter.sharedMesh)) continue;
            unresolvedCombined.Add(new UnresolvedCombinedRecord
            {
                filter = filter,
                reason = previous.TryGetValue(filter, out string reason) && !string.IsNullOrEmpty(reason)
                    ? reason : fallbackReason
            });
        }
    }

    private RecoveryRecord FindRecovery(MeshFilter filter)
    {
        RecoveryRecord result = new RecoveryRecord { filter = filter, currentMesh = filter.sharedMesh };

        Mesh colliderMesh = FindPersistentColliderMesh(filter);
        if (colliderMesh != null)
        {
            result.proposedMesh = colliderMesh;
            result.quality = MatchQuality.ExactCollider;
            result.reason = "CONFIRMED: persistent MeshCollider reference found on this object or a closely related object.";
            return result;
        }

        List<Mesh> geometryMatches = useGeometryConfirmation && filter.sharedMesh != null
            ? projectMeshes.Where(mesh => GeometryMatches(filter.sharedMesh, mesh)).ToList()
            : new List<Mesh>();

        string[] names = GetSearchNames(filter).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct().ToArray();
        List<Mesh> namedGeometryMatches = geometryMatches
            .Where(mesh => names.Any(name => NameScore(mesh.name, name) >= 70)).ToList();

        if (namedGeometryMatches.Count == 1)
        {
            result.proposedMesh = namedGeometryMatches[0];
            result.quality = MatchQuality.StrongGeometry;
            result.reason = "HIGH CONFIDENCE: geometry matches and renderer/object naming points to one Project mesh.";
            return result;
        }
        if (geometryMatches.Count == 1)
        {
            result.proposedMesh = geometryMatches[0];
            result.quality = MatchQuality.StrongGeometry;
            result.reason = "HIGH CONFIDENCE: exactly one Project mesh has matching vertex, submesh and bounds data.";
            return result;
        }

        Mesh bestName = projectMeshes
            .Select(mesh => new { mesh, score = names.Max(name => NameScore(mesh.name, name)) })
            .Where(x => x.score >= 55)
            .OrderByDescending(x => x.score)
            .ThenBy(x => x.mesh.name.Length)
            .Select(x => x.mesh)
            .FirstOrDefault();

        if (bestName != null)
        {
            result.proposedMesh = bestName;
            result.quality = MatchQuality.NameGuess;
            result.reason = "GUESSED: selected by MeshFilter, renderer, GameObject or parent name. Verify visually before Apply.";
            return result;
        }

        result.quality = MatchQuality.Missing;
        result.enabled = false;
        result.reason = filter.sharedMesh == null
            ? "UNRESOLVED: MeshFilter is empty and no credible Project mesh name match was found."
            : "UNRESOLVED: no persistent MeshCollider, geometry match or credible name match was found.";
        return result;
    }

    private static Mesh FindPersistentColliderMesh(MeshFilter filter)
    {
        MeshCollider sameObject = filter.GetComponent<MeshCollider>();
        if (IsPersistentMesh(sameObject == null ? null : sameObject.sharedMesh)) return sameObject.sharedMesh;

        MeshCollider[] siblings = filter.transform.parent == null
            ? new MeshCollider[0]
            : filter.transform.parent.GetComponentsInChildren<MeshCollider>(true);
        string filterName = NormalizeName(filter.gameObject.name);
        List<MeshCollider> matching = siblings.Where(c => IsPersistentMesh(c.sharedMesh) &&
            (c.gameObject == filter.gameObject || NormalizeName(c.gameObject.name) == filterName ||
             NormalizeName(c.sharedMesh.name) == filterName)).ToList();
        return matching.Count == 1 ? matching[0].sharedMesh : null;
    }

    private static bool IsPersistentMesh(Mesh mesh)
    {
        return mesh != null && !string.IsNullOrEmpty(AssetDatabase.GetAssetPath(mesh));
    }

    private static Mesh SelectBestPhaseOneColliderMesh(MeshFilter filter, Mesh currentCombinedMesh,
        out string reason)
    {
        reason = "no persistent non-convex MeshCollider matched this object.";
        if (filter == null) return null;
        List<MeshCollider> candidates = filter.GetComponents<MeshCollider>()
            .Where(collider => IsSafePhaseOneColliderReference(
                filter, collider, currentCombinedMesh, out string ignoredReason))
            .ToList();
        if (candidates.Count == 0) return null;
        if (candidates.Count == 1)
        {
            reason = "one persistent non-convex MeshCollider matched the normalized object name.";
            return candidates[0].sharedMesh;
        }

        Mesh visible = null;
        try
        {
            MeshRenderer renderer = filter.GetComponent<MeshRenderer>();
            if (renderer != null && TryGetStaticBatchRange(renderer, currentCombinedMesh,
                out int firstSubMesh, out int subMeshCount))
            {
                visible = ExtractStaticBatchPart(filter, currentCombinedMesh, firstSubMesh, subMeshCount);
            }

            MeshCollider best = candidates
                .Select(collider => new
                {
                    collider,
                    score = ScoreColliderGeometry(visible, collider.sharedMesh, filter)
                })
                .OrderByDescending(item => item.score)
                .ThenBy(item => AssetDatabase.GetAssetPath(item.collider.sharedMesh),
                    StringComparer.OrdinalIgnoreCase)
                .Select(item => item.collider)
                .First();
            reason = $"selected the closest geometry from {candidates.Count} persistent non-convex MeshColliders.";
            return best.sharedMesh;
        }
        finally
        {
            if (visible != null) DestroyImmediate(visible);
        }
    }

    private static float ScoreColliderGeometry(Mesh visible, Mesh candidate, MeshFilter filter)
    {
        if (candidate == null) return float.MinValue;
        float score = 0f;
        if (visible != null)
        {
            if (string.Equals(BuildTransformIndependentShapeFingerprint(visible),
                BuildTransformIndependentShapeFingerprint(candidate), StringComparison.Ordinal)) score += 10000f;
            long visibleIndices = 0, candidateIndices = 0;
            for (int sub = 0; sub < visible.subMeshCount; sub++) visibleIndices += (long)visible.GetIndexCount(sub);
            for (int sub = 0; sub < candidate.subMeshCount; sub++) candidateIndices += (long)candidate.GetIndexCount(sub);
            score += 1000f * Mathf.Min(visibleIndices, candidateIndices) /
                     Mathf.Max(1f, Mathf.Max(visibleIndices, candidateIndices));
            score += 500f * Mathf.Min(visible.vertexCount, candidate.vertexCount) /
                     Mathf.Max(1f, Mathf.Max(visible.vertexCount, candidate.vertexCount));
        }
        score += NameScore(candidate.name, filter.gameObject.name) * 10f;
        return score;
    }

    private static bool IsSafePhaseOneColliderReference(MeshFilter filter, MeshCollider collider,
        Mesh currentCombinedMesh, out string reason)
    {
        reason = string.Empty;
        if (filter == null || collider == null || collider.sharedMesh == null)
            return false;

        Mesh candidate = collider.sharedMesh;
        string assetPath = AssetDatabase.GetAssetPath(candidate);
        if (candidate == currentCombinedMesh || string.IsNullOrEmpty(assetPath))
            return false;

        if (collider.convex)
        {
            reason = "rejected generated/convex collider";
            return false;
        }

        string lowerName = candidate.name.ToLowerInvariant();
        string lowerPath = assetPath.ToLowerInvariant();
        string[] generatedMarkers =
        {
            "convex", "convexhull", "convex_hull", "collisionmesh", "collision_mesh",
            "collidermesh", "collider_mesh", "generated collider", "generated_collider", "ucx_"
        };
        if (generatedMarkers.Any(marker => lowerName.Contains(marker) || lowerPath.Contains(marker)))
        {
            reason = "rejected generated collider asset marker";
            return false;
        }

        string objectName = NormalizeName(filter.gameObject.name);
        string meshName = NormalizeName(candidate.name);
        if (string.IsNullOrEmpty(objectName) || string.IsNullOrEmpty(meshName) ||
            !(objectName == meshName || objectName.Contains(meshName) || meshName.Contains(objectName)))
        {
            reason = "rejected because collider mesh name does not match the scene object";
            return false;
        }

        reason = "persistent non-convex MeshCollider asset matches the normalized scene-object name.";
        return true;
    }

    private static bool GeometryMatches(Mesh a, Mesh b)
    {
        if (a == null || b == null || a.vertexCount != b.vertexCount || a.subMeshCount != b.subMeshCount) return false;
        Vector3 sa = a.bounds.size;
        Vector3 sb = b.bounds.size;
        float tolerance = Mathf.Max(0.0001f, Mathf.Max(sa.magnitude, sb.magnitude) * 0.0005f);
        if ((sa - sb).magnitude > tolerance) return false;
        try
        {
            for (int i = 0; i < a.subMeshCount; i++)
                if (a.GetIndexCount(i) != b.GetIndexCount(i)) return false;
        }
        catch { }
        return true;
    }

    private static bool ExactGeometryMatches(Mesh visiblePart, Mesh candidate)
    {
        if (!GeometryMatches(visiblePart, candidate)) return false;
        return string.Equals(BuildGeometryFingerprint(visiblePart), BuildGeometryFingerprint(candidate),
            StringComparison.Ordinal);
    }

    private Mesh FindExistingSeparatedMesh(Mesh extractedMesh, string extractedFingerprint,
        string normalizedObjectName, Mesh combinedSource)
    {
        if (extractedMesh == null || string.IsNullOrEmpty(extractedFingerprint)) return null;

        if (!projectMeshesByTopology.TryGetValue(BuildTopologyKey(extractedMesh),
            out List<Mesh> topologyCandidates)) return null;

        return topologyCandidates
            .Where(mesh => mesh != null && mesh != combinedSource)
            .Where(mesh =>
            {
                string path = AssetDatabase.GetAssetPath(mesh);
                return !string.IsNullOrEmpty(path) &&
                    !path.StartsWith("Assets/CityTools_Backups/", StringComparison.OrdinalIgnoreCase);
            })
            .Where(mesh => GeometryMatches(extractedMesh, mesh))
            .Where(mesh => string.Equals(GetCachedProjectMeshFingerprint(mesh), extractedFingerprint,
                StringComparison.Ordinal))
            .OrderByDescending(mesh =>
                string.Equals(NormalizeRepeatedObjectName(mesh.name), normalizedObjectName,
                    StringComparison.OrdinalIgnoreCase))
            .ThenBy(mesh => AssetDatabase.GetAssetPath(mesh).Length)
            .ThenBy(mesh => AssetDatabase.GetAssetPath(mesh), StringComparer.OrdinalIgnoreCase)
            .ThenBy(mesh => mesh.name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private string GetCachedProjectMeshFingerprint(Mesh mesh)
    {
        if (mesh == null) return string.Empty;
        if (projectMeshFingerprintCache.TryGetValue(mesh, out string fingerprint))
            return fingerprint;
        fingerprint = BuildGeometryFingerprint(mesh);
        projectMeshFingerprintCache[mesh] = fingerprint;
        return fingerprint;
    }

    private static IEnumerable<string> GetSearchNames(MeshFilter filter)
    {
        if (filter.sharedMesh != null) yield return filter.sharedMesh.name;
        yield return filter.gameObject.name;
        MeshRenderer renderer = filter.GetComponent<MeshRenderer>();
        if (renderer != null) yield return renderer.gameObject.name;
        if (filter.transform.parent != null) yield return filter.transform.parent.name;
    }

    private static int NameScore(string meshName, string sourceName)
    {
        string a = NormalizeName(meshName);
        string b = NormalizeName(sourceName);
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return 0;
        if (a == b) return 100;
        if (a.Contains(b) || b.Contains(a)) return 80;
        string[] at = a.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        string[] bt = b.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        int common = at.Intersect(bt).Count();
        return common == 0 ? 0 : Mathf.RoundToInt(70f * common / Mathf.Max(at.Length, bt.Length));
    }

    private static string NormalizeName(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        string lower = NormalizeRepeatedObjectName(value).ToLowerInvariant();
        string[] noise = { "combined", "mesh", "root", "scene", "clone", "renderer", "filter" };
        foreach (string word in noise) lower = lower.Replace(word, " ");
        char[] chars = lower.Select(c => char.IsLetterOrDigit(c) ? c : ' ').ToArray();
        return string.Join(" ", new string(chars).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
    }

    private void ApplyRecoveredMeshes()
    {
        List<RecoveryRecord> ready = records
            .Where(r => r.enabled && r.quality == MatchQuality.ExactCollider &&
                        r.filter != null && r.proposedMesh != null &&
                        !string.IsNullOrEmpty(AssetDatabase.GetAssetPath(r.proposedMesh))).ToList();
        if (ready.Count == 0) return;
        if (!automaticMode) CreateLoadedSceneBackups("Smart_Phase1_Collider_Mesh_Recovery");
        int exactOccurrences = ready.Sum(record => record.occurrences.Count > 0 ? record.occurrences.Count : 1);
        if (!automaticMode && !EditorUtility.DisplayDialog("Apply Recovered Mesh References",
            $"Unique Project meshes: {ready.Count}\nScene occurrences to reconnect: {exactOccurrences}\n\n" +
            "Only MeshFilter.sharedMesh will change. No combined mesh asset will be modified or deleted.",
            "Apply", "Cancel")) return;

        Undo.IncrementCurrentGroup();
        int group = Undo.GetCurrentGroup();
        Undo.SetCurrentGroupName("Recover Project Mesh References");
        int applied = 0;
        foreach (RecoveryRecord record in ready)
        {
            IEnumerable<MeshFilter> targets = record.occurrences.Count > 0
                ? record.occurrences.Where(filter => filter != null)
                : new[] { record.filter };
            foreach (MeshFilter target in targets)
            {
                Undo.RecordObject(target, "Recover MeshFilter Reference");
                MeshRenderer renderer = target.GetComponent<MeshRenderer>();
                Material[] materials = renderer == null ? null : renderer.sharedMaterials;
                bool rendererEnabled = renderer != null && renderer.enabled;
                if (renderer != null)
                {
                    Undo.RecordObject(renderer, "Clear Old Static Batch Metadata");
                    ClearRendererStaticBatchMetadata(renderer);
                }
                target.sharedMesh = record.proposedMesh;
                if (renderer != null)
                {
                    renderer.sharedMaterials = materials;
                    renderer.enabled = rendererEnabled;
                    EditorUtility.SetDirty(renderer);
                }
                EditorUtility.SetDirty(target);
                applied++;
            }
            record.reason = "APPLIED: " + AssetDatabase.GetAssetPath(record.proposedMesh);
        }
        Undo.CollapseUndoOperations(group);
        MarkLoadedScenesDirty();
        if (!automaticMode)
            EditorUtility.DisplayDialog("Mesh Recovery Complete",
                $"Recovered exact MeshCollider references: {applied}\n\n" +
                "All remaining combined objects are unchanged and available in Phase 1 Action 3. Save the scene now.", "OK");
        Repaint();
    }

    private static Color GetQualityColor(MatchQuality quality)
    {
        switch (quality)
        {
            case MatchQuality.ExactCollider: return ExactGreen;
            case MatchQuality.StrongGeometry: return StrongGreen;
            case MatchQuality.NameGuess: return GuessOrange;
            default: return MissingRed;
        }
    }

    private static string GetHierarchyPath(Transform transform)
    {
        List<string> parts = new List<string>();
        while (transform != null) { parts.Add(transform.name); transform = transform.parent; }
        parts.Reverse();
        return string.Join("/", parts);
    }

    private static void MarkLoadedScenesDirty()
    {
        for (int i = 0; i < SceneManager.sceneCount; i++)
        {
            Scene scene = SceneManager.GetSceneAt(i);
            if (scene.IsValid() && scene.isLoaded) EditorSceneManager.MarkSceneDirty(scene);
        }
    }

    private static void CreateLoadedSceneBackups(string operationName)
    {
        for (int index = 0; index < SceneManager.sceneCount; index++)
        {
            Scene scene = SceneManager.GetSceneAt(index);
            if (!scene.IsValid() || !scene.isLoaded || string.IsNullOrEmpty(scene.path)) continue;
            CityToolBackupManager.CreateBackup(scene.path, operationName);
        }
    }

    internal static AutomaticCombinedResult RunAutomatic(string scenePath)
    {
        SceneCombinedMeshAssetFixer tool = CreateInstance<SceneCombinedMeshAssetFixer>();
        try
        {
            tool.automaticMode = true;
            tool.scanAllLoadedScenes = true;
            tool.includeInactive = true;
            tool.separatedMeshFolder = SmartRecoveryPaths.FolderAsset(
                SmartRecoveryPaths.Separated(scenePath));
            tool.Scan();
            AutomaticCombinedResult result = new AutomaticCombinedResult
            {
                colliderRecovered = tool.records.Sum(record =>
                    record.occurrences.Count > 0 ? record.occurrences.Count : 1)
            };
            tool.ApplyRecoveredMeshes();
            tool.FindUniqueCombinedMeshParts();
            result.existingMeshesReused = tool.separationGroups.Count(group => group.persistentColliderMesh != null);
            result.newMeshesRequired = tool.separationGroups.Count(group => group.persistentColliderMesh == null);
            result.occurrences = tool.separationGroups.Sum(group => group.filters.Count);
            tool.SeparateSaveAndAssign();
            return result;
        }
        finally
        {
            tool.ClearSeparationGroups();
            DestroyImmediate(tool);
        }
    }
}
#endif
