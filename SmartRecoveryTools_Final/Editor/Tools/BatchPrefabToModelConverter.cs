#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Batch exports prefab assets to FBX (Unity FBX Exporter package required) or OBJ.
/// Optionally keeps each original prefab asset as a wrapper and replaces its visual
/// hierarchy with an instance of the exported model.
/// Put this file anywhere below an Assets/Editor folder.
/// </summary>
public sealed class BatchPrefabToModelConverter : EditorWindow
{
    internal sealed class AutomaticFbxResult
    {
        internal int requested;
        internal int successful;
        internal int failed;
    }

    private enum ModelFormat { FBX, OBJ }
    private enum FbxEncoding { Binary, ASCII }

    [Serializable]
    private sealed class PrefabItem
    {
        public GameObject prefab;
        public bool enabled = true;
        public string status = "Ready";
    }

    private sealed class ExportResult
    {
        public PrefabItem item;
        public GameObject prefabAsset;
        public GameObject modelAsset;
        public Dictionary<string, Material[]> materials;
        public Dictionary<Mesh, Mesh> meshReplacements;
        public bool reusedCachedMeshes;
        public bool preserveExcludedMeshes;
        public string originalPrefabIdentity;
        public string originalPrefabGeometryIdentity;
    }

    [Serializable]
    private sealed class MeshCacheEntry
    {
        public int cacheVersion;
        public string sourceKey;
        public string sourceSignature;
        public string fbxAssetPath;
        public long fbxMeshLocalId;
        public string fbxMeshName;
    }

    private const int CurrentMeshCacheVersion = 2;

    [FilePath("ProjectSettings/SmartRecoveryFbxMeshCache.asset", FilePathAttribute.Location.ProjectFolder)]
    private sealed class SmartFbxMeshCache : ScriptableSingleton<SmartFbxMeshCache>
    {
        [SerializeField] private List<MeshCacheEntry> entries = new List<MeshCacheEntry>();

        public List<MeshCacheEntry> Entries => entries;

        public void SaveCache()
        {
            entries.RemoveAll(entry => entry == null || string.IsNullOrEmpty(entry.sourceKey));
            Save(true);
        }
    }

    private sealed class SceneMaterialSnapshot
    {
        public Renderer renderer;
        public Material[] materials;
    }

    private readonly List<PrefabItem> items = new List<PrefabItem>();
    private Vector2 windowScroll;
    private Vector2 scroll;
    private DefaultAsset outputFolder;
    private ModelFormat format = ModelFormat.FBX;
    private FbxEncoding fbxEncoding = FbxEncoding.Binary;
    private bool exportAnimation = true;
    private bool includeInactive = true;
    private bool applyModelImportSettings = true;
    private bool replacePrefabVisuals = true;
    private bool replaceMeshReferencesOnly = true;
    private bool fullPrefabReplacement = true;
    private bool preserveRootComponents;
    private bool preserveOriginalMaterials = true;
    private bool createBackupCopies = true;
    private bool overwriteModelFiles;
    private bool isConverting;
    private bool automaticMode;

    [MenuItem("Tools/Smart Recovery Tools/5. Prefab to FBX", false, 24)]
    private static void Open()
    {
        GetWindow<BatchPrefabToModelConverter>("Prefab To FBX-OBJ");
    }

    private Vector2 finalizedPageScroll;
    private void OnGUI()
    {
        minSize = new Vector2(560, 420);
        using (new SmartRecoveryUI.WindowScope(this, "OBJ Converter"))
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
        EditorGUILayout.LabelField("Batch Prefab To FBX / OBJ", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "Drag prefab assets into the box. The tool exports one model per prefab. Optionally, the original " +
            "prefab remains at the same path but its visual children are replaced by the exported model.",
            MessageType.Info);

        DrawDropArea();

        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Add Selected Prefabs", GUILayout.Height(27))) AddSelectedPrefabs();
            if (GUILayout.Button("Remove Disabled", GUILayout.Height(27))) items.RemoveAll(i => !i.enabled);
            if (GUILayout.Button("Clear", GUILayout.Height(27))) items.Clear();
        }

        DrawItems();
        DrawSettings();

        int ready = items.Count(i => i.enabled && i.prefab != null);
        GUI.enabled = ready > 0 && outputFolder != null;
        if (GUILayout.Button($"Batch Convert {ready} Prefabs To {format}", GUILayout.Height(42)))
            ConvertAll();
        GUI.enabled = true;
        EditorGUILayout.Space(8);
        EditorGUILayout.EndScrollView();
    }

    private void DrawDropArea()
    {
        Rect rect = GUILayoutUtility.GetRect(0, 62, GUILayout.ExpandWidth(true));
        GUI.Box(rect, "Drag & Drop Prefab Assets Here\n(Project window prefabs only)", EditorStyles.helpBox);

        Event evt = Event.current;
        if (!rect.Contains(evt.mousePosition)) return;
        if (evt.type == EventType.DragUpdated)
        {
            DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
            evt.Use();
        }
        else if (evt.type == EventType.DragPerform)
        {
            DragAndDrop.AcceptDrag();
            AddObjects(DragAndDrop.objectReferences);
            evt.Use();
        }
    }

    private void DrawItems()
    {
        if (items.Count == 0) return;
        EditorGUILayout.Space(5);
        EditorGUILayout.LabelField($"Prefabs ({items.Count})", EditorStyles.boldLabel);
        scroll = EditorGUILayout.BeginScrollView(scroll, GUILayout.MinHeight(130), GUILayout.MaxHeight(280));
        foreach (PrefabItem item in items)
        {
            using (new EditorGUILayout.HorizontalScope("box"))
            {
                item.enabled = EditorGUILayout.Toggle(item.enabled, GUILayout.Width(18));
                item.prefab = (GameObject)EditorGUILayout.ObjectField(item.prefab, typeof(GameObject), false);
                EditorGUILayout.LabelField(item.status, EditorStyles.miniLabel, GUILayout.Width(190));
            }
        }
        EditorGUILayout.EndScrollView();
    }

    private void DrawSettings()
    {
        EditorGUILayout.Space(7);
        EditorGUILayout.LabelField("Common Export Settings", EditorStyles.boldLabel);
        format = (ModelFormat)EditorGUILayout.EnumPopup("Format", format);
        outputFolder = (DefaultAsset)EditorGUILayout.ObjectField("Model Output Folder", outputFolder, typeof(DefaultAsset), false);

        if (format == ModelFormat.FBX)
        {
            fbxEncoding = (FbxEncoding)EditorGUILayout.EnumPopup("FBX Encoding", fbxEncoding);
            exportAnimation = EditorGUILayout.Toggle("Export Animation", exportAnimation);
            EditorGUILayout.HelpBox(
                "FBX export requires Unity's official com.unity.formats.fbx package. Encoding/animation options " +
                "are applied when supported by the installed package version.", MessageType.None);
        }
        else
        {
            EditorGUILayout.HelpBox(
                "OBJ contains static geometry, UVs, normals and material assignments. OBJ does not support animation.",
                MessageType.None);
        }

        includeInactive = EditorGUILayout.Toggle("Include Inactive Objects", includeInactive);
        applyModelImportSettings = EditorGUILayout.Toggle("Configure Imported Model", applyModelImportSettings);
        overwriteModelFiles = EditorGUILayout.Toggle("Overwrite Existing Models", overwriteModelFiles);

        EditorGUILayout.Space(5);
        EditorGUILayout.LabelField("Prefab Variant / Wrapper", EditorStyles.boldLabel);
        replacePrefabVisuals = EditorGUILayout.ToggleLeft(
            "Update every selected prefab with its exported model (recommended)", replacePrefabVisuals);
        GUI.enabled = replacePrefabVisuals;
        replaceMeshReferencesOnly = EditorGUILayout.ToggleLeft(
            "Mesh references only: keep prefab structure, materials and components unchanged (recommended)",
            replaceMeshReferencesOnly);
        GUI.enabled = replacePrefabVisuals && !replaceMeshReferencesOnly;
        fullPrefabReplacement = EditorGUILayout.ToggleLeft(
            "Clean rebuild: keep only an empty wrapper + the new model (recommended)",
            fullPrefabReplacement);
        GUI.enabled = replacePrefabVisuals && !replaceMeshReferencesOnly && !fullPrefabReplacement;
        preserveRootComponents = EditorGUILayout.ToggleLeft("Preserve old root components", preserveRootComponents);
        GUI.enabled = replacePrefabVisuals && !replaceMeshReferencesOnly;
        preserveOriginalMaterials = EditorGUILayout.ToggleLeft(
            "Apply original prefab materials to converted model (recommended)", preserveOriginalMaterials);
        GUI.enabled = replacePrefabVisuals;
        createBackupCopies = EditorGUILayout.ToggleLeft("Create prefab backup before replacement (recommended)", createBackupCopies);
        GUI.enabled = true;
        if (GUILayout.Button("Open Backup Manager")) CityToolBackupManager.Open();
    }

    private void AddSelectedPrefabs() { AddObjects(Selection.objects); }

    private void AddObjects(IEnumerable<UnityEngine.Object> objects)
    {
        foreach (UnityEngine.Object obj in objects)
        {
            GameObject go = obj as GameObject;
            if (go == null) continue;
            string path = AssetDatabase.GetAssetPath(go);
            if (!path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)) continue;
            if (items.Any(i => i.prefab == go)) continue;
            items.Add(new PrefabItem { prefab = go });
        }
        Repaint();
    }

    private void ConvertAll()
    {
        if (isConverting)
        {
            Debug.LogWarning("A prefab conversion is already running.");
            return;
        }

        string outputAssetPath = AssetDatabase.GetAssetPath(outputFolder);
        if (!AssetDatabase.IsValidFolder(outputAssetPath) ||
            !(outputAssetPath == "Assets" || outputAssetPath.StartsWith("Assets/", StringComparison.Ordinal)))
        {
            EditorUtility.DisplayDialog("Invalid Output Folder", "Choose a folder inside Assets.", "OK");
            return;
        }

        if (format == ModelFormat.FBX && FindFbxExporterType() == null)
        {
            EditorUtility.DisplayDialog("Unity FBX Exporter Required",
                "Install 'FBX Exporter' (com.unity.formats.fbx) from Unity Package Manager, then try again. " +
                "OBJ export does not require that package.", "OK");
            return;
        }

        List<PrefabItem> ready = items.Where(i => i.enabled && i.prefab != null).ToList();
        if (ready.Count == 0) return;
        if (!automaticMode && !EditorUtility.DisplayDialog("Batch Convert Prefabs",
            $"Prefabs: {ready.Count}\nFormat: {format}\nOutput: {outputAssetPath}\n" +
            $"Update selected prefabs: {replacePrefabVisuals}\n" +
            $"Mesh references only: {replacePrefabVisuals && replaceMeshReferencesOnly}\n" +
            $"Full prefab replacement: {replacePrefabVisuals && !replaceMeshReferencesOnly && fullPrefabReplacement}\n\nContinue?",
            "Convert", "Cancel")) return;

        int exported = 0, wrapped = 0, failed = 0;
        bool cancelled = false;
        List<ExportResult> successfulExports = new List<ExportResult>();
        isConverting = true;
        try
        {
            // Stage 1: export every selected prefab before modifying any prefab asset. This is
            // essential for duplicated prefabs, shared meshes, base prefabs and prefab variants.
            for (int index = 0; index < ready.Count; index++)
            {
                PrefabItem item = ready[index];
                if (EditorUtility.DisplayCancelableProgressBar("Batch Prefab Export",
                    $"{index + 1}/{ready.Count}: {item.prefab.name}", (float)index / ready.Count))
                {
                    cancelled = true;
                    break;
                }
                try
                {
                    string extension = format == ModelFormat.FBX ? ".fbx" : ".obj";
                    string modelPath = AssetDatabase.GenerateUniqueAssetPath(
                        (outputAssetPath + "/" + SanitizeFileName(item.prefab.name) + extension).Replace("\\", "/"));
                    string requestedPath = (outputAssetPath + "/" + SanitizeFileName(item.prefab.name) + extension).Replace("\\", "/");
                    if (overwriteModelFiles) modelPath = requestedPath;
                    else if (File.Exists(ToAbsolutePath(requestedPath))) modelPath = AssetDatabase.GenerateUniqueAssetPath(requestedPath);

                    GameObject source = null;
                    Dictionary<string, Material[]> materials = null;
                    List<Material> temporarySubMeshMarkers = null;
                    List<Mesh> sourceMeshes = null;
                    Dictionary<Mesh, Mesh> cachedReplacements = null;
                    Dictionary<Mesh, string> uniqueExportNames = null;
                    Dictionary<Mesh, Mesh> sourceCanonicalMeshes = null;
                    List<Mesh> uniqueSourceMeshes = null;
                    GameObject uniqueExportRoot = null;
                    bool reusedCache = false;
                    bool preserveExcludedMeshes = false;
                    string originalPrefabIdentity = null;
                    string originalPrefabGeometryIdentity = null;
                    try
                    {
                        source = PrefabUtility.LoadPrefabContents(AssetDatabase.GetAssetPath(item.prefab));
                        if (source == null) throw new Exception("Unity could not load the prefab contents.");
                        materials = CaptureMaterials(source);
                        sourceMeshes = CollectReferencedMeshes(source);
                        originalPrefabIdentity = SmartRecoveryPrefabIdentity.Build(source, false);
                        originalPrefabGeometryIdentity = SmartRecoveryPrefabIdentity.Build(source, true);

                        if (format == ModelFormat.FBX)
                        {
                            List<Mesh> assetMeshes = sourceMeshes.Where(IsStandaloneAssetMesh).ToList();
                            preserveExcludedMeshes = assetMeshes.Count != sourceMeshes.Count;
                            sourceMeshes = assetMeshes;
                            if (sourceMeshes.Count == 0)
                            {
                                item.status = "Skipped - no .asset meshes (existing model references unchanged)";
                                continue;
                            }

                            // This is an unsaved, isolated prefab-content copy. Keep the rig and
                            // hierarchy, but never export geometry already supplied by a model.
                            ExcludeNonAssetMeshesFromExport(source);
                        }

                        // A complete cache hit means this prefab can be pointed at FBX mesh
                        // sub-assets that already exist. No new FBX file or duplicate mesh data
                        // is created for this prefab.
                        reusedCache = format == ModelFormat.FBX && replacePrefabVisuals &&
                            replaceMeshReferencesOnly && sourceMeshes.Count > 0 &&
                            TryBuildCachedReplacementMap(sourceMeshes, out cachedReplacements);

                        bool containsSkinnedMeshes = source.GetComponentsInChildren<SkinnedMeshRenderer>(true)
                            .Any(renderer => renderer.sharedMesh != null);
                        if (!reusedCache && format == ModelFormat.FBX && (replaceMeshReferencesOnly || preserveExcludedMeshes) &&
                            !containsSkinnedMeshes)
                        {
                            uniqueSourceMeshes = BuildCanonicalSourceMeshes(sourceMeshes,
                                out sourceCanonicalMeshes);
                            uniqueExportRoot = CreateUniqueStaticMeshExportRoot(source, uniqueSourceMeshes,
                                out uniqueExportNames, out temporarySubMeshMarkers);
                        }
                        else if (!reusedCache && replacePrefabVisuals && (replaceMeshReferencesOnly || preserveExcludedMeshes))
                        {
                            temporarySubMeshMarkers = ApplyUniqueSubMeshMarkers(source);
                        }
                        if (!reusedCache)
                        {
                            if (format == ModelFormat.FBX)
                                ExportFbx(ToAbsolutePath(modelPath), uniqueExportRoot != null ? uniqueExportRoot : source);
                            else ExportObj(ToAbsolutePath(modelPath), source);
                        }
                    }
                    finally
                    {
                        // Never destroy temporary materials while renderers still reference them.
                        // Newer FBX Exporter/Unity versions can retain native renderer state briefly,
                        // making dangling material references an editor-crash risk.
                        if (uniqueExportRoot != null) UnityEngine.Object.DestroyImmediate(uniqueExportRoot);
                        if (source != null && materials != null)
                            RestoreMaterials(source, materials);
                        if (temporarySubMeshMarkers != null)
                            foreach (Material marker in temporarySubMeshMarkers)
                                if (marker != null) UnityEngine.Object.DestroyImmediate(marker);
                        if (source != null) PrefabUtility.UnloadPrefabContents(source);
                    }

                    if (reusedCache)
                    {
                        successfulExports.Add(new ExportResult
                        {
                            item = item,
                            prefabAsset = item.prefab,
                            materials = materials,
                            meshReplacements = cachedReplacements,
                            reusedCachedMeshes = true,
                            preserveExcludedMeshes = preserveExcludedMeshes,
                            originalPrefabIdentity = originalPrefabIdentity,
                            originalPrefabGeometryIdentity = originalPrefabGeometryIdentity
                        });
                        item.status = "Cache hit - waiting for prefab update";
                        continue;
                    }

                    AssetDatabase.ImportAsset(modelPath, ImportAssetOptions.ForceSynchronousImport);
                    ConfigureModelImporter(modelPath);
                    exported++;

                    GameObject model = AssetDatabase.LoadAssetAtPath<GameObject>(modelPath);
                    if (model == null) throw new Exception("Unity did not import the exported model.");
                    List<Mesh> importedMeshes = CollectReferencedMeshes(model);
                    Dictionary<Mesh, Mesh> replacements = sourceMeshes != null
                        ? BuildMeshReplacementMap(uniqueSourceMeshes ?? sourceMeshes,
                            importedMeshes, uniqueExportNames)
                        : new Dictionary<Mesh, Mesh>();
                    if (sourceCanonicalMeshes != null)
                    {
                        Dictionary<Mesh, Mesh> canonicalReplacements = replacements;
                        replacements = new Dictionary<Mesh, Mesh>();
                        foreach (Mesh original in sourceMeshes)
                            if (sourceCanonicalMeshes.TryGetValue(original, out Mesh canonical) &&
                                canonicalReplacements.TryGetValue(canonical, out Mesh imported))
                                replacements[original] = imported;
                    }
                    if (format == ModelFormat.FBX && replacePrefabVisuals && (replaceMeshReferencesOnly || preserveExcludedMeshes))
                    {
                        if (replacements.Count != sourceMeshes.Count)
                            throw new Exception($"Safe mesh alignment stopped: matched {replacements.Count} of {sourceMeshes.Count} meshes.");
                        UpdatePersistentMeshCache(replacements, modelPath);
                    }
                    successfulExports.Add(new ExportResult
                    {
                        item = item,
                        prefabAsset = item.prefab,
                        modelAsset = model,
                        materials = materials,
                        meshReplacements = replacements,
                        preserveExcludedMeshes = preserveExcludedMeshes,
                        originalPrefabIdentity = originalPrefabIdentity,
                        originalPrefabGeometryIdentity = originalPrefabGeometryIdentity
                    });
                    item.status = replacePrefabVisuals ? "Exported - waiting for prefab update" : "Exported";
                }
                catch (Exception ex)
                {
                    failed++;
                    item.status = "Failed - see Console";
                    Debug.LogError($"Prefab conversion failed for '{item.prefab?.name}':\n{ex}");
                }
            }

            // Stage 2: update selected prefab variants before their base prefabs. The models and
            // material data above were captured while every selected prefab was still unchanged.
            if (replacePrefabVisuals)
            {
                List<ExportResult> updateOrder = successfulExports
                    .OrderByDescending(r => GetPrefabInheritanceDepth(r.prefabAsset))
                    .ToList();

                for (int index = 0; index < updateOrder.Count; index++)
                {
                    ExportResult result = updateOrder[index];
                    if (EditorUtility.DisplayCancelableProgressBar("Updating Selected Prefabs",
                        $"{index + 1}/{updateOrder.Count}: {result.prefabAsset.name}",
                        updateOrder.Count == 0 ? 1f : (float)index / updateOrder.Count))
                    {
                        cancelled = true;
                        break;
                    }
                    try
                    {
                        // Replacing the entire hierarchy of a mixed prefab would discard its
                        // excluded FBX/OBJ visuals. Update only eligible references in that case.
                        if (replaceMeshReferencesOnly || result.preserveExcludedMeshes)
                            ReplacePrefabMeshReferences(result.prefabAsset, result.modelAsset,
                                result.materials, result.meshReplacements);
                        else
                            RewritePrefabAsModelWrapper(result.prefabAsset, result.modelAsset, result.materials);
                        SmartRecoveryPrefabRegistry.instance.Register(
                            result.originalPrefabIdentity, result.originalPrefabGeometryIdentity,
                            AssetDatabase.GetAssetPath(result.prefabAsset));
                        wrapped++;
                        result.item.status = result.reusedCachedMeshes
                            ? "Reused cached FBX meshes + prefab updated"
                            : "Exported + selected prefab updated";
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        result.item.status = "Exported, prefab update failed";
                        Debug.LogError($"Prefab update failed for '{result.prefabAsset?.name}':\n{ex}");
                    }
                }
            }
        }
        finally
        {
            isConverting = false;
            EditorUtility.ClearProgressBar();
            AssetDatabase.SaveAssets();
            Repaint();
        }

        if (!automaticMode)
            EditorUtility.DisplayDialog("Batch Prefab Export Complete",
                $"Models exported: {exported}\nPrefabs updated: {wrapped}\nFailed: {failed}" +
                (cancelled ? "\n\nOperation cancelled safely." : string.Empty), "OK");
    }

    private static int GetPrefabInheritanceDepth(GameObject prefabAsset)
    {
        int depth = 0;
        UnityEngine.Object current = prefabAsset;
        HashSet<UnityEngine.Object> visited = new HashSet<UnityEngine.Object>();
        while (current != null && visited.Add(current))
        {
            UnityEngine.Object source = PrefabUtility.GetCorrespondingObjectFromSource(current);
            if (source == null || source == current) break;
            depth++;
            current = source;
        }
        return depth;
    }

    private void ReplacePrefabMeshReferences(GameObject prefabAsset, GameObject modelAsset,
        Dictionary<string, Material[]> originalMaterials, Dictionary<Mesh, Mesh> preparedReplacements)
    {
        string prefabPath = AssetDatabase.GetAssetPath(prefabAsset);
        List<SceneMaterialSnapshot> sceneMaterials = CaptureLoadedSceneMaterials(prefabPath);
        if (createBackupCopies)
            CityToolBackupManager.CreateBackup(prefabPath, "Prefab_To_FBX_OBJ");

        GameObject root = PrefabUtility.LoadPrefabContents(prefabPath);
        try
        {
            List<Mesh> originalMeshes = CollectReferencedMeshes(root);
            if (format == ModelFormat.FBX)
                originalMeshes = originalMeshes.Where(IsStandaloneAssetMesh).ToList();
            Dictionary<Mesh, Mesh> replacements = preparedReplacements;
            if (replacements == null && modelAsset != null)
                replacements = BuildMeshReplacementMap(originalMeshes, CollectReferencedMeshes(modelAsset));
            if (replacements == null)
                replacements = new Dictionary<Mesh, Mesh>();
            if (replacements.Count != originalMeshes.Count)
                throw new Exception(
                    $"Safe mesh alignment stopped: matched {replacements.Count} of {originalMeshes.Count} meshes. " +
                    "At least one FBX mesh did not preserve the original submesh/material-face layout.");

            int changed = ReplaceAllSerializedMeshReferences(root, replacements, false);

            // Assigning a mesh can make Unity resize renderer material slots when an exporter has
            // changed submesh layout. Compatible meshes are enforced below, and this restores the
            // exact existing Project Material references on the prefab as an additional safeguard.
            RestoreMaterials(root, originalMaterials);

            if (changed == 0)
                throw new Exception("Converted meshes were matched, but the prefab had no replaceable mesh references.");

            // No renderer, material, transform, child, script or collider setting is modified here.
            PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
            ReplaceLoadedSceneMeshReferences(prefabPath, replacements);
            RestoreLoadedSceneMaterials(sceneMaterials);
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }
    }

    private static int ReplaceAllSerializedMeshReferences(GameObject root,
        Dictionary<Mesh, Mesh> replacements, bool recordUndo)
    {
        int changed = 0;
        foreach (Component component in root.GetComponentsInChildren<Component>(true))
        {
            if (component == null || component is Transform) continue;
            if (recordUndo) Undo.RecordObject(component, "Replace Scene Mesh Reference");
            SerializedObject serialized = new SerializedObject(component);
            SerializedProperty property = serialized.GetIterator();
            bool componentChanged = false;
            while (property.Next(true))
            {
                if (property.propertyType != SerializedPropertyType.ObjectReference) continue;
                Mesh oldMesh = property.objectReferenceValue as Mesh;
                if (oldMesh == null || !replacements.TryGetValue(oldMesh, out Mesh newMesh)) continue;
                property.objectReferenceValue = newMesh;
                componentChanged = true;
                changed++;
            }
            if (componentChanged) serialized.ApplyModifiedPropertiesWithoutUndo();
        }
        return changed;
    }

    private static void ReplaceLoadedSceneMeshReferences(string prefabPath,
        Dictionary<Mesh, Mesh> replacements)
    {
        HashSet<GameObject> instanceRoots = new HashSet<GameObject>();
        foreach (GameObject gameObject in Resources.FindObjectsOfTypeAll<GameObject>())
        {
            if (gameObject == null || !gameObject.scene.IsValid() || !gameObject.scene.isLoaded) continue;
            if (!string.Equals(PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(gameObject),
                prefabPath, StringComparison.OrdinalIgnoreCase)) continue;
            GameObject root = PrefabUtility.GetNearestPrefabInstanceRoot(gameObject);
            if (root != null) instanceRoots.Add(root);
        }

        foreach (GameObject instanceRoot in instanceRoots)
        {
            int changed = ReplaceAllSerializedMeshReferences(instanceRoot, replacements, true);
            if (changed > 0) EditorSceneManager.MarkSceneDirty(instanceRoot.scene);
        }
    }

    private static List<SceneMaterialSnapshot> CaptureLoadedSceneMaterials(string prefabPath)
    {
        return Resources.FindObjectsOfTypeAll<Renderer>()
            .Where(renderer => renderer != null && renderer.gameObject.scene.IsValid() &&
                renderer.gameObject.scene.isLoaded)
            .Where(renderer => string.Equals(
                PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(renderer.gameObject),
                prefabPath, StringComparison.OrdinalIgnoreCase))
            .Select(renderer => new SceneMaterialSnapshot
            {
                renderer = renderer,
                materials = renderer.sharedMaterials.ToArray()
            }).ToList();
    }

    private static void RestoreLoadedSceneMaterials(IEnumerable<SceneMaterialSnapshot> snapshots)
    {
        foreach (SceneMaterialSnapshot snapshot in snapshots)
        {
            if (snapshot == null || snapshot.renderer == null) continue;
            Material[] current = snapshot.renderer.sharedMaterials;
            if (current.SequenceEqual(snapshot.materials)) continue;
            Undo.RecordObject(snapshot.renderer, "Preserve Scene Instance Materials");
            snapshot.renderer.sharedMaterials = snapshot.materials;
            EditorSceneManager.MarkSceneDirty(snapshot.renderer.gameObject.scene);
        }
    }

    private static bool IsStandaloneAssetMesh(Mesh mesh)
    {
        return mesh != null && string.Equals(
            Path.GetExtension(AssetDatabase.GetAssetPath(mesh)), ".asset",
            StringComparison.OrdinalIgnoreCase);
    }

    private static void ExcludeNonAssetMeshesFromExport(GameObject root)
    {
        foreach (MeshFilter filter in root.GetComponentsInChildren<MeshFilter>(true))
            if (filter.sharedMesh != null && !IsStandaloneAssetMesh(filter.sharedMesh))
                filter.sharedMesh = null;
        foreach (SkinnedMeshRenderer renderer in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            if (renderer.sharedMesh != null && !IsStandaloneAssetMesh(renderer.sharedMesh))
                renderer.sharedMesh = null;
        foreach (MeshCollider collider in root.GetComponentsInChildren<MeshCollider>(true))
            if (collider.sharedMesh != null && !IsStandaloneAssetMesh(collider.sharedMesh))
                collider.sharedMesh = null;
    }

    private static List<Mesh> CollectReferencedMeshes(GameObject root)
    {
        if (root == null) return new List<Mesh>();
        return root.GetComponentsInChildren<MeshFilter>(true)
            .Where(filter => filter.sharedMesh != null).Select(filter => filter.sharedMesh)
            .Concat(root.GetComponentsInChildren<SkinnedMeshRenderer>(true)
                .Where(renderer => renderer.sharedMesh != null).Select(renderer => renderer.sharedMesh))
            .Concat(root.GetComponentsInChildren<MeshCollider>(true)
                .Where(collider => collider.sharedMesh != null).Select(collider => collider.sharedMesh))
            .Distinct().ToList();
    }

    private static bool TryBuildCachedReplacementMap(IEnumerable<Mesh> sourceMeshes,
        out Dictionary<Mesh, Mesh> replacements)
    {
        replacements = new Dictionary<Mesh, Mesh>();
        SmartFbxMeshCache cache = SmartFbxMeshCache.instance;
        bool cacheChanged = false;
        foreach (Mesh source in sourceMeshes.Distinct())
        {
            string sourceKey = GetStableMeshKey(source);
            string sourceSignature = GetMeshSignature(source);
            MeshCacheEntry entry = cache.Entries.LastOrDefault(candidate =>
                candidate != null && candidate.sourceKey == sourceKey &&
                candidate.cacheVersion == CurrentMeshCacheVersion &&
                candidate.sourceSignature == sourceSignature);
            Mesh cachedMesh = entry == null ? null : ResolveCachedMesh(entry);
            if (cachedMesh == null || !IsTopologyCompatible(source, cachedMesh))
            {
                if (entry != null)
                {
                    cache.Entries.Remove(entry);
                    cacheChanged = true;
                }
                replacements.Clear();
                if (cacheChanged) cache.SaveCache();
                return false;
            }
            replacements[source] = cachedMesh;
        }
        if (cacheChanged) cache.SaveCache();
        return replacements.Count > 0;
    }

    private static void UpdatePersistentMeshCache(Dictionary<Mesh, Mesh> replacements,
        string fbxAssetPath)
    {
        if (replacements == null || replacements.Count == 0) return;
        SmartFbxMeshCache cache = SmartFbxMeshCache.instance;
        foreach (KeyValuePair<Mesh, Mesh> pair in replacements)
        {
            if (pair.Key == null || pair.Value == null) continue;
            string sourceKey = GetStableMeshKey(pair.Key);
            string signature = GetMeshSignature(pair.Key);
            if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(pair.Value,
                out string ignoredGuid, out long importedLocalId)) continue;

            cache.Entries.RemoveAll(entry => entry != null && entry.sourceKey == sourceKey);
            cache.Entries.Add(new MeshCacheEntry
            {
                cacheVersion = CurrentMeshCacheVersion,
                sourceKey = sourceKey,
                sourceSignature = signature,
                fbxAssetPath = fbxAssetPath,
                fbxMeshLocalId = importedLocalId,
                fbxMeshName = pair.Value.name
            });

            string importedKey = GetStableMeshKey(pair.Value);
            cache.Entries.RemoveAll(entry => entry != null && entry.sourceKey == importedKey);
            cache.Entries.Add(new MeshCacheEntry
            {
                cacheVersion = CurrentMeshCacheVersion,
                sourceKey = importedKey,
                sourceSignature = GetMeshSignature(pair.Value),
                fbxAssetPath = fbxAssetPath,
                fbxMeshLocalId = importedLocalId,
                fbxMeshName = pair.Value.name
            });
        }
        cache.SaveCache();
    }

    private static Mesh ResolveCachedMesh(MeshCacheEntry entry)
    {
        if (entry == null || string.IsNullOrEmpty(entry.fbxAssetPath) ||
            AssetDatabase.LoadMainAssetAtPath(entry.fbxAssetPath) == null) return null;
        foreach (Mesh mesh in AssetDatabase.LoadAllAssetsAtPath(entry.fbxAssetPath).OfType<Mesh>())
        {
            if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(mesh,
                out string ignoredGuid, out long localId)) continue;
            if (localId == entry.fbxMeshLocalId) return mesh;
        }
        return AssetDatabase.LoadAllAssetsAtPath(entry.fbxAssetPath).OfType<Mesh>()
            .FirstOrDefault(mesh => mesh.name == entry.fbxMeshName &&
                GetMeshSignature(mesh) == entry.sourceSignature);
    }

    private static string GetStableMeshKey(Mesh mesh)
    {
        if (mesh == null) return string.Empty;
        if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(mesh, out string guid, out long localId) &&
            !string.IsNullOrEmpty(guid))
            return guid + ":" + localId.ToString(CultureInfo.InvariantCulture);
        return "runtime:" + NormalizeMeshName(mesh.name) + ":" + GetMeshSignature(mesh);
    }

    private static string GetMeshSignature(Mesh mesh)
    {
        if (mesh == null) return string.Empty;
        StringBuilder signature = new StringBuilder(96);
        signature.Append(mesh.vertexCount).Append('|').Append(mesh.subMeshCount).Append('|');
        for (int index = 0; index < mesh.subMeshCount; index++)
        {
            signature.Append((int)mesh.GetTopology(index)).Append(':')
                .Append(mesh.GetIndexCount(index)).Append(';');
        }
        Vector3 size = mesh.bounds.size;
        signature.Append(Mathf.RoundToInt(size.x * 10000f)).Append(',')
            .Append(Mathf.RoundToInt(size.y * 10000f)).Append(',')
            .Append(Mathf.RoundToInt(size.z * 10000f));
        return signature.ToString();
    }

    private static Dictionary<Mesh, Mesh> BuildMeshReplacementMap(List<Mesh> originals,
        List<Mesh> imported, Dictionary<Mesh, string> expectedExportNames = null)
    {
        Dictionary<Mesh, Mesh> result = new Dictionary<Mesh, Mesh>();
        HashSet<Mesh> usedImported = new HashSet<Mesh>();
        foreach (Mesh original in originals)
        {
            // Match each source mesh to one imported mesh only. This prevents two
            // different source assets with identical topology from being silently
            // collapsed onto the same FBX sub-asset.
            Mesh best = imported.Where(mesh => mesh != null && !usedImported.Contains(mesh))
                .OrderByDescending(mesh => MeshMatchScore(original, mesh) +
                    (expectedExportNames != null && expectedExportNames.TryGetValue(original, out string expected) &&
                     NormalizeMeshName(mesh.name) == NormalizeMeshName(expected) ? 10000 : 0))
                .FirstOrDefault();
            if (best == null || !IsTopologyCompatible(original, best)) continue;
            result[original] = best;
            usedImported.Add(best);
        }
        return result;
    }

    private static int MeshMatchScore(Mesh original, Mesh imported)
    {
        if (original == null || imported == null) return int.MinValue;
        int score = 0;
        if (NormalizeMeshName(original.name) == NormalizeMeshName(imported.name)) score += 1000;
        if (original.vertexCount == imported.vertexCount) score += 200;
        if (original.subMeshCount == imported.subMeshCount) score += 100;
        if (Approximately(original.bounds.size, imported.bounds.size)) score += 100;
        if (original.subMeshCount == imported.subMeshCount)
        {
            bool indicesMatch = true;
            for (int i = 0; i < original.subMeshCount; i++)
                if (original.GetIndexCount(i) != imported.GetIndexCount(i)) { indicesMatch = false; break; }
            if (indicesMatch) score += 200;
        }
        return score;
    }

    private static bool IsTopologyCompatible(Mesh original, Mesh imported)
    {
        if (original == null || imported == null ||
            original.subMeshCount != imported.subMeshCount) return false;
        for (int i = 0; i < original.subMeshCount; i++)
            if (original.GetIndexCount(i) != imported.GetIndexCount(i)) return false;
        return true;
    }

    private static string NormalizeMeshName(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value.Replace(" Instance", "").Replace("_Mesh", "").Trim().ToLowerInvariant();
    }

    private static bool Approximately(Vector3 left, Vector3 right)
    {
        float scale = Mathf.Max(1f, left.magnitude, right.magnitude);
        return (left - right).magnitude <= scale * 0.0001f;
    }

    private void RewritePrefabAsModelWrapper(GameObject prefabAsset, GameObject modelAsset,
        Dictionary<string, Material[]> originalMaterials)
    {
        string prefabPath = AssetDatabase.GetAssetPath(prefabAsset);
        if (createBackupCopies)
        {
            CityToolBackupManager.CreateBackup(prefabPath, "Prefab_To_FBX_OBJ");
        }

        // Rebuilding is intentional. Editing a prefab variant can leave inherited MeshFilters,
        // renderers or mesh colliders on its root even after DestroyImmediate. A fresh wrapper
        // makes the converted prefab independent and guarantees exactly one visual model source.
        if (fullPrefabReplacement)
        {
            RewriteAsCleanModelWrapper(prefabAsset.name, prefabPath, modelAsset,
                preserveOriginalMaterials ? originalMaterials : null);
            return;
        }

        GameObject root = PrefabUtility.LoadPrefabContents(prefabPath);
        try
        {
            List<GameObject> oldChildren = root.transform.Cast<Transform>().Select(t => t.gameObject).ToList();
            foreach (GameObject child in oldChildren) UnityEngine.Object.DestroyImmediate(child);

            if (!preserveRootComponents)
            {
                foreach (Component component in root.GetComponents<Component>())
                    if (component != null && !(component is Transform)) UnityEngine.Object.DestroyImmediate(component);
            }
            else
            {
                // Root scripts, colliders and gameplay data may remain, but the old visual mesh
                // must not survive beside the newly exported model.
                Component[] rootVisuals = root.GetComponents<Component>()
                    .Where(component => component is Renderer || component is MeshFilter ||
                                        component is MeshCollider || component is LODGroup)
                    .ToArray();
                foreach (Component component in rootVisuals)
                    if (component != null) UnityEngine.Object.DestroyImmediate(component);
            }

            GameObject modelInstance = PrefabUtility.InstantiatePrefab(modelAsset, root.scene) as GameObject;
            if (modelInstance == null) throw new Exception("Could not instantiate exported model in prefab.");

            modelInstance.name = modelAsset.name;
            modelInstance.transform.SetParent(root.transform, false);
            modelInstance.transform.localPosition = Vector3.zero;
            modelInstance.transform.localRotation = Quaternion.identity;
            modelInstance.transform.localScale = Vector3.one;

            if (preserveOriginalMaterials)
                RestoreMaterials(modelInstance, originalMaterials);
            PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }
    }

    private static void RewriteAsCleanModelWrapper(string prefabName, string prefabPath,
        GameObject modelAsset, Dictionary<string, Material[]> originalMaterials)
    {
        GameObject wrapper = new GameObject(prefabName);
        try
        {
            wrapper.transform.localPosition = Vector3.zero;
            wrapper.transform.localRotation = Quaternion.identity;
            wrapper.transform.localScale = Vector3.one;
            wrapper.SetActive(true);
            wrapper.layer = modelAsset.layer;
            try { wrapper.tag = "Untagged"; } catch { }
            wrapper.hideFlags = HideFlags.None;

            GameObject modelInstance = PrefabUtility.InstantiatePrefab(modelAsset) as GameObject;
            if (modelInstance == null)
                throw new Exception("Could not instantiate exported model for the clean prefab wrapper.");

            modelInstance.name = modelAsset.name;
            modelInstance.transform.SetParent(wrapper.transform, false);
            modelInstance.transform.localPosition = Vector3.zero;
            modelInstance.transform.localRotation = Quaternion.identity;
            modelInstance.transform.localScale = Vector3.one;

            if (originalMaterials != null)
                RestoreMaterials(modelInstance, originalMaterials);

            bool success;
            GameObject saved = PrefabUtility.SaveAsPrefabAsset(wrapper, prefabPath, out success);
            if (!success || saved == null)
                throw new Exception("Unity could not rebuild the prefab as a clean model wrapper.");
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(wrapper);
        }
    }

    private static Dictionary<string, Material[]> CaptureMaterials(GameObject root)
    {
        Dictionary<string, Material[]> result = new Dictionary<string, Material[]>();
        foreach (Renderer renderer in root.GetComponentsInChildren<Renderer>(true))
            result[GetNamePath(root.transform, renderer.transform)] = renderer.sharedMaterials;
        return result;
    }

    private static List<Mesh> BuildCanonicalSourceMeshes(IEnumerable<Mesh> sourceMeshes,
        out Dictionary<Mesh, Mesh> canonicalBySource)
    {
        canonicalBySource = new Dictionary<Mesh, Mesh>();
        Dictionary<string, Mesh> canonicalByGeometry = new Dictionary<string, Mesh>(StringComparer.Ordinal);
        foreach (Mesh source in sourceMeshes.Where(mesh => mesh != null).Distinct())
        {
            string fingerprint = GetExactGeometryFingerprint(source);
            if (!canonicalByGeometry.TryGetValue(fingerprint, out Mesh canonical))
            {
                canonical = source;
                canonicalByGeometry.Add(fingerprint, canonical);
            }
            canonicalBySource[source] = canonical;
        }
        return canonicalByGeometry.Values.ToList();
    }

    private static string GetExactGeometryFingerprint(Mesh mesh)
    {
        try
        {
            unchecked
            {
                ulong hash = 1469598103934665603UL;
                Action<int> add = value => { hash ^= (uint)value; hash *= 1099511628211UL; };
                using (Mesh.MeshDataArray array = MeshUtility.AcquireReadOnlyMeshData(mesh))
                {
                    Mesh.MeshData data = array[0];
                    NativeArray<Vector3> positions = new NativeArray<Vector3>(data.vertexCount,
                        Allocator.Temp, NativeArrayOptions.UninitializedMemory);
                    string[] vertexKeys;
                    try
                    {
                        data.GetVertices(positions);
                        vertexKeys = positions.Select(position =>
                            Mathf.RoundToInt(position.x * 10000f) + "," +
                            Mathf.RoundToInt(position.y * 10000f) + "," +
                            Mathf.RoundToInt(position.z * 10000f)).ToArray();
                        foreach (string key in vertexKeys.OrderBy(value => value, StringComparer.Ordinal))
                            foreach (char character in key) add(character);
                    }
                    finally { positions.Dispose(); }
                    for (int subMesh = 0; subMesh < mesh.subMeshCount; subMesh++)
                    {
                        SubMeshDescriptor descriptor = data.GetSubMesh(subMesh);
                        add((int)descriptor.topology);
                        add(descriptor.indexCount);
                        NativeArray<int> indices = new NativeArray<int>(descriptor.indexCount,
                            Allocator.Temp, NativeArrayOptions.UninitializedMemory);
                        try
                        {
                            data.GetIndices(indices, subMesh, true);
                            List<string> primitives = new List<string>(indices.Length / 3);
                            for (int index = 0; index + 2 < indices.Length; index += 3)
                            {
                                string[] corners =
                                {
                                    vertexKeys[indices[index]], vertexKeys[indices[index + 1]],
                                    vertexKeys[indices[index + 2]]
                                };
                                Array.Sort(corners, StringComparer.Ordinal);
                                primitives.Add(corners[0] + "|" + corners[1] + "|" + corners[2]);
                            }
                            foreach (string primitive in primitives.OrderBy(value => value, StringComparer.Ordinal))
                                foreach (char character in primitive) add(character);
                        }
                        finally { indices.Dispose(); }
                    }
                }
                return hash.ToString("X16") + ":" + mesh.vertexCount + ":" + mesh.subMeshCount;
            }
        }
        catch
        {
            return GetStableMeshKey(mesh) + ":" + GetMeshSignature(mesh);
        }
    }

    private static GameObject CreateUniqueStaticMeshExportRoot(GameObject prefabContentsRoot,
        IEnumerable<Mesh> meshes,
        out Dictionary<Mesh, string> exportNames, out List<Material> markers)
    {
        exportNames = new Dictionary<Mesh, string>();
        markers = new List<Material>();
        Shader shader = Shader.Find("Standard") ?? Shader.Find("Hidden/InternalErrorShader");
        if (shader == null)
            throw new Exception("Unity could not find a temporary shader required to preserve FBX submeshes.");

        GameObject root = new GameObject("__SmartRecovery_UniqueMeshLibrary");
        root.hideFlags = HideFlags.None;
        root.transform.SetParent(prefabContentsRoot.transform, false);
        int meshIndex = 0;
        foreach (Mesh mesh in meshes.Where(mesh => mesh != null).Distinct())
        {
            string exportName = $"Mesh_{meshIndex:D4}_{SanitizeFileName(mesh.name)}";
            GameObject holder = new GameObject(exportName);
            holder.hideFlags = HideFlags.None;
            holder.transform.SetParent(root.transform, false);
            holder.AddComponent<MeshFilter>().sharedMesh = mesh;
            MeshRenderer renderer = holder.AddComponent<MeshRenderer>();
            Material[] slots = new Material[Mathf.Max(1, mesh.subMeshCount)];
            for (int subMesh = 0; subMesh < slots.Length; subMesh++)
            {
                Material marker = new Material(shader)
                {
                    name = $"__SmartRecovery_Mesh_{meshIndex:D4}_SubMesh_{subMesh:D4}",
                    hideFlags = HideFlags.HideAndDontSave
                };
                markers.Add(marker);
                slots[subMesh] = marker;
            }
            renderer.sharedMaterials = slots;
            exportNames[mesh] = exportName;
            meshIndex++;
        }
        if (exportNames.Count == 0)
        {
            UnityEngine.Object.DestroyImmediate(root);
            throw new Exception("The prefab contains no unique static mesh data to export.");
        }
        return root;
    }

    private static List<Material> ApplyUniqueSubMeshMarkers(GameObject temporaryExportRoot)
    {
        List<Material> markers = new List<Material>();
        Shader shader = Shader.Find("Standard") ?? Shader.Find("Hidden/InternalErrorShader");
        if (shader == null)
            throw new Exception("Unity could not find a temporary shader required to preserve FBX submeshes.");

        int rendererNumber = 0;
        foreach (Renderer renderer in temporaryExportRoot.GetComponentsInChildren<Renderer>(true))
        {
            Mesh mesh = null;
            if (renderer is SkinnedMeshRenderer skinned)
                mesh = skinned.sharedMesh;
            else
            {
                MeshFilter filter = renderer.GetComponent<MeshFilter>();
                if (filter != null) mesh = filter.sharedMesh;
            }
            if (mesh == null || mesh.subMeshCount == 0) continue;

            Material[] slots = new Material[mesh.subMeshCount];
            for (int subMesh = 0; subMesh < mesh.subMeshCount; subMesh++)
            {
                Material marker = new Material(shader)
                {
                    name = $"__SmartRecovery_Renderer_{rendererNumber:D4}_SubMesh_{subMesh:D4}",
                    hideFlags = HideFlags.HideAndDontSave
                };
                markers.Add(marker);
                slots[subMesh] = marker;
            }
            renderer.sharedMaterials = slots;
            rendererNumber++;
        }
        return markers;
    }

    private static void RestoreMaterials(GameObject root, Dictionary<string, Material[]> materials)
    {
        Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
        foreach (Renderer renderer in renderers)
        {
            string path = GetNamePath(root.transform, renderer.transform);
            if (materials.TryGetValue(path, out Material[] exact)) renderer.sharedMaterials = exact;
            else
            {
                string leaf = renderer.transform.name;
                KeyValuePair<string, Material[]> match = materials.FirstOrDefault(kv =>
                    kv.Key.EndsWith("/" + leaf, StringComparison.Ordinal) || kv.Key == leaf);
                if (match.Value != null) renderer.sharedMaterials = match.Value;
                else if (materials.Count == 1) renderer.sharedMaterials = materials.First().Value;
            }
        }
    }

    private static string GetNamePath(Transform root, Transform current)
    {
        List<string> parts = new List<string>();
        while (current != null && current != root)
        {
            parts.Add(current.name);
            current = current.parent;
        }
        parts.Reverse();
        return string.Join("/", parts);
    }

    private void ConfigureModelImporter(string modelPath)
    {
        ModelImporter importer = AssetImporter.GetAtPath(modelPath) as ModelImporter;
        if (importer == null) return;

        // Converted models are mesh sources only. Never create/extract new materials; the prefab's
        // existing project Material references and every scene-instance material override stay intact.
        PropertyInfo importMaterialsProperty = typeof(ModelImporter).GetProperty("importMaterials");
        if (importMaterialsProperty != null && importMaterialsProperty.CanWrite)
            importMaterialsProperty.SetValue(importer, false, null);
        PropertyInfo materialModeProperty = typeof(ModelImporter).GetProperty("materialImportMode");
        if (materialModeProperty != null && materialModeProperty.CanWrite &&
            materialModeProperty.PropertyType.IsEnum)
        {
            try
            {
                object none = Enum.Parse(materialModeProperty.PropertyType, "None", true);
                materialModeProperty.SetValue(importer, none, null);
            }
            catch { }
        }

        if (applyModelImportSettings)
            importer.importAnimation = format == ModelFormat.FBX && exportAnimation;
        importer.SaveAndReimport();
    }

    private static Type FindFbxExporterType()
    {
        return Type.GetType("UnityEditor.Formats.Fbx.Exporter.ModelExporter, Unity.Formats.Fbx.Editor") ??
               AppDomain.CurrentDomain.GetAssemblies()
                   .Select(a => a.GetType("UnityEditor.Formats.Fbx.Exporter.ModelExporter"))
                   .FirstOrDefault(t => t != null);
    }

    private void ExportFbx(string absolutePath, GameObject source)
    {
        Type exporterType = FindFbxExporterType();
        if (exporterType == null) throw new Exception("Unity FBX Exporter package is not installed.");

        MethodInfo advanced = exporterType.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(m => m.Name == "ExportObject" && m.GetParameters().Length == 3 &&
                                 m.GetParameters()[0].ParameterType == typeof(string));
        MethodInfo simple = exporterType.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(m => m.Name == "ExportObject" && m.GetParameters().Length == 2 &&
                                 m.GetParameters()[0].ParameterType == typeof(string));
        if (advanced == null && simple == null)
            throw new Exception("The installed FBX Exporter has no compatible ExportObject API.");

        Directory.CreateDirectory(Path.GetDirectoryName(absolutePath));
        object result;
        if (advanced != null)
        {
            Type optionsType = advanced.GetParameters()[2].ParameterType;
            object options = Activator.CreateInstance(optionsType);
            TrySetEnumProperty(options, "ExportFormat", fbxEncoding == FbxEncoding.ASCII ? "ASCII" : "Binary");
            TrySetBoolProperty(options, "ExportUnrendered", includeInactive);
            TrySetBoolProperty(options, "AnimateSkinnedMesh", exportAnimation);
            TrySetBoolProperty(options, "ExportAnimation", exportAnimation);
            result = advanced.Invoke(null, new[] { (object)absolutePath, source, options });
        }
        else
        {
            result = simple.Invoke(null, new object[] { absolutePath, source });
        }
        if (result is string exported && string.IsNullOrEmpty(exported))
            throw new Exception("FBX Exporter returned an empty result.");

        if (advanced == null && fbxEncoding == FbxEncoding.ASCII)
            Debug.LogWarning("ASCII FBX was requested. This FBX Exporter API version exposes only the default " +
                             "ExportObject call, so the package may still write Binary FBX.");
    }

    private static void TrySetBoolProperty(object target, string propertyName, bool value)
    {
        PropertyInfo property = target.GetType().GetProperty(propertyName,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (property != null && property.CanWrite && property.PropertyType == typeof(bool))
            property.SetValue(target, value, null);
    }

    private static void TrySetEnumProperty(object target, string propertyName, string value)
    {
        PropertyInfo property = target.GetType().GetProperty(propertyName,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (property == null || !property.CanWrite || !property.PropertyType.IsEnum) return;
        try { property.SetValue(target, Enum.Parse(property.PropertyType, value, true), null); }
        catch { }
    }

    private void ExportObj(string absolutePath, GameObject source)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(absolutePath));
        StringBuilder obj = new StringBuilder(1024 * 64);
        StringBuilder mtl = new StringBuilder(4096);
        string mtlName = Path.GetFileNameWithoutExtension(absolutePath) + ".mtl";
        obj.Append("mtllib ").Append(mtlName).AppendLine();

        Dictionary<Material, string> materialNames = new Dictionary<Material, string>();
        int vertexOffset = 0, uvOffset = 0, normalOffset = 0;
        IEnumerable<MeshFilter> filters = source.GetComponentsInChildren<MeshFilter>(includeInactive);
        foreach (MeshFilter filter in filters)
        {
            Mesh mesh = filter.sharedMesh;
            MeshRenderer renderer = filter.GetComponent<MeshRenderer>();
            if (mesh == null || renderer == null) continue;
            Matrix4x4 matrix = source.transform.worldToLocalMatrix * filter.transform.localToWorldMatrix;
            Matrix4x4 normalMatrix = matrix.inverse.transpose;
            obj.Append("o ").Append(SafeObjName(filter.name)).AppendLine();

            foreach (Vector3 v in mesh.vertices)
            {
                Vector3 p = matrix.MultiplyPoint3x4(v);
                obj.AppendFormat(CultureInfo.InvariantCulture, "v {0} {1} {2}\n", -p.x, p.y, p.z);
            }
            foreach (Vector2 uv in mesh.uv)
                obj.AppendFormat(CultureInfo.InvariantCulture, "vt {0} {1}\n", uv.x, uv.y);
            foreach (Vector3 n in mesh.normals)
            {
                Vector3 normal = normalMatrix.MultiplyVector(n).normalized;
                obj.AppendFormat(CultureInfo.InvariantCulture, "vn {0} {1} {2}\n", -normal.x, normal.y, normal.z);
            }

            for (int sub = 0; sub < mesh.subMeshCount; sub++)
            {
                Material material = sub < renderer.sharedMaterials.Length ? renderer.sharedMaterials[sub] : null;
                string materialName = GetObjMaterialName(material, materialNames, mtl);
                obj.Append("usemtl ").Append(materialName).AppendLine();
                int[] triangles = mesh.GetTriangles(sub);
                bool hasUv = mesh.uv != null && mesh.uv.Length == mesh.vertexCount;
                bool hasNormals = mesh.normals != null && mesh.normals.Length == mesh.vertexCount;
                for (int t = 0; t + 2 < triangles.Length; t += 3)
                {
                    obj.Append("f ");
                    AppendObjIndex(obj, triangles[t + 2], vertexOffset, uvOffset, normalOffset, hasUv, hasNormals);
                    obj.Append(' ');
                    AppendObjIndex(obj, triangles[t + 1], vertexOffset, uvOffset, normalOffset, hasUv, hasNormals);
                    obj.Append(' ');
                    AppendObjIndex(obj, triangles[t], vertexOffset, uvOffset, normalOffset, hasUv, hasNormals);
                    obj.AppendLine();
                }
            }
            vertexOffset += mesh.vertexCount;
            uvOffset += mesh.uv != null ? mesh.uv.Length : 0;
            normalOffset += mesh.normals != null ? mesh.normals.Length : 0;
        }

        File.WriteAllText(absolutePath, obj.ToString(), new UTF8Encoding(false));
        File.WriteAllText(Path.ChangeExtension(absolutePath, ".mtl"), mtl.ToString(), new UTF8Encoding(false));
    }

    private static void AppendObjIndex(StringBuilder sb, int index, int vOffset, int uvOffset,
        int nOffset, bool hasUv, bool hasNormal)
    {
        sb.Append(vOffset + index + 1);
        if (!hasUv && !hasNormal) return;
        sb.Append('/');
        if (hasUv) sb.Append(uvOffset + index + 1);
        if (hasNormal) sb.Append('/').Append(nOffset + index + 1);
    }

    private static string GetObjMaterialName(Material material, Dictionary<Material, string> names, StringBuilder mtl)
    {
        if (material != null && names.TryGetValue(material, out string existing)) return existing;
        string name = SafeObjName(material == null ? "DefaultMaterial" : material.name);
        if (material != null) names[material] = name;
        Color color = material != null && material.HasProperty("_Color") ? material.color : Color.white;
        mtl.Append("newmtl ").Append(name).AppendLine();
        mtl.AppendFormat(CultureInfo.InvariantCulture, "Kd {0} {1} {2}\n", color.r, color.g, color.b);
        mtl.AppendFormat(CultureInfo.InvariantCulture, "d {0}\n\n", color.a);
        return name;
    }

    private static string SafeObjName(string value)
    {
        return RegexReplaceWhitespace(string.IsNullOrWhiteSpace(value) ? "Object" : value.Trim());
    }

    private static string RegexReplaceWhitespace(string value)
    {
        StringBuilder result = new StringBuilder(value.Length);
        foreach (char c in value) result.Append(char.IsWhiteSpace(c) ? '_' : c);
        return result.ToString();
    }

    private static string SanitizeFileName(string value)
    {
        string result = string.IsNullOrWhiteSpace(value) ? "Model" : value.Trim();
        foreach (char c in Path.GetInvalidFileNameChars()) result = result.Replace(c, '_');
        return result;
    }

    private static string ToAbsolutePath(string assetPath)
    {
        string projectRoot = Path.GetDirectoryName(Application.dataPath);
        return Path.GetFullPath(Path.Combine(projectRoot, assetPath));
    }

    internal static AutomaticFbxResult RunAutomatic(IEnumerable<GameObject> prefabs,
        string outputFolderPath)
    {
        BatchPrefabToModelConverter tool = CreateInstance<BatchPrefabToModelConverter>();
        try
        {
            tool.automaticMode = true;
            tool.format = ModelFormat.FBX;
            tool.outputFolder = SmartRecoveryPaths.FolderAsset(outputFolderPath);
            tool.includeInactive = true;
            tool.applyModelImportSettings = true;
            tool.replacePrefabVisuals = true;
            tool.replaceMeshReferencesOnly = true;
            tool.fullPrefabReplacement = false;
            tool.preserveOriginalMaterials = true;
            tool.createBackupCopies = true;
            tool.overwriteModelFiles = false;
            tool.AddObjects(prefabs.Where(prefab => prefab != null).Cast<UnityEngine.Object>());
            AutomaticFbxResult result = new AutomaticFbxResult { requested = tool.items.Count };
            if (tool.items.Count > 0) tool.ConvertAll();
            result.successful = tool.items.Count(item =>
                item.status == "Reused cached FBX meshes + prefab updated" ||
                item.status == "Exported + selected prefab updated" ||
                item.status.StartsWith("Skipped - no .asset meshes", StringComparison.Ordinal));
            result.failed = result.requested - result.successful;
            return result;
        }
        finally { DestroyImmediate(tool); }
    }
}
#endif
