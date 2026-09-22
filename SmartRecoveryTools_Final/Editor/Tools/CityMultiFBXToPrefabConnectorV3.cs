using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Unity.Collections;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

/// <summary>
/// Editor-only smart prefab builder. Put this file below an Assets/Editor folder.
/// It can discover repeated scene objects without requiring an FBX source.
/// </summary>
public class CityMultiFBXToPrefabConnectorV3 : EditorWindow
{
    internal sealed class AutomaticPrefabResult
    {
        internal int groups;
        internal int occurrences;
        internal readonly List<GameObject> prefabs = new List<GameObject>();
    }

    private enum ScanMode
    {
        AutomaticRepeatedObjects,
        SelectedSourcesFindAllMatches,
        SelectedSceneObjectsOnly,
        MeshDeduplicationAndParentPrefabs
    }

    private enum GroupKind
    {
        Standard,
        SharedMesh,
        ParentAssembly
    }

    [Serializable]
    private class PrefabGroup
    {
        public string signature;
        public string prefabName;
        public GameObject template;
        public List<GameObject> matches = new List<GameObject>();
        public int meshCount;
        public int rendererCount;
        public bool enabled = true;
        public bool expanded;
        public string status;
        public GroupKind kind;
        public GameObject existingPrefab;
    }

    private struct ObjectState
    {
        public string name;
        public bool active;
        public int layer;
        public string tag;
        public StaticEditorFlags staticFlags;
        public Vector3 localPosition;
        public Quaternion localRotation;
        public Vector3 localScale;
        public bool sceneHidden;
        public bool pickingDisabled;
    }

    private struct RendererState
    {
        public Material[] materials;
        public int lightmapIndex;
        public Vector4 lightmapScaleOffset;
        public int realtimeLightmapIndex;
        public Vector4 realtimeLightmapScaleOffset;
        public bool enabled;
        public ShadowCastingMode shadowCastingMode;
        public bool receiveShadows;
        public int sortingLayerID;
        public int sortingOrder;
    }

    private sealed class InstanceState
    {
        public readonly Dictionary<string, ObjectState> objects = new Dictionary<string, ObjectState>();
        public readonly Dictionary<string, RendererState> renderers = new Dictionary<string, RendererState>();
    }

    private ScanMode scanMode = ScanMode.AutomaticRepeatedObjects;
    private bool scanAllLoadedScenes = true;
    private GameObject searchRoot;
    private DefaultAsset outputPrefabFolder;
    private int minimumRepeats = 2;
    private bool includeNonRepeatedObjects = true;
    private bool scanDirectChildrenOnly;
    private bool matchDuplicatedMeshGeometry = true;
    private bool includeInactive = true;
    private bool ignoreMaterialsWhenGrouping = true;
    private bool includeComponentLayout = true;
    private bool skipPrefabInstances = true;
    private bool overwriteExistingPrefabs;
    private bool preserveNames = true;
    private bool preserveHierarchyTransforms = true;
    private bool preserveObjectSettings = true;
    private bool preserveRendererSettings = true;
    private bool preserveSceneVisibility = true;
    private bool reuseExistingPrefabs = true;
    private bool excludeRiggedObjects = true;
    private bool automaticMode;
    private readonly List<GameObject> lastAutomaticPrefabs = new List<GameObject>();
    private bool existingPrefabSearchCompleted;
    private Vector2 windowScroll;
    private Vector2 scroll;
    private readonly List<PrefabGroup> groups = new List<PrefabGroup>();
    private readonly Dictionary<Mesh, string> meshGeometryFingerprints =
        new Dictionary<Mesh, string>();

    [MenuItem("Tools/Smart Recovery Tools/3. Smart Prefab Builder", false, 22)]
    public static void Open()
    {
        GetWindow<CityMultiFBXToPrefabConnectorV3>("Smart Prefab Builder");
    }

    private Vector2 finalizedPageScroll;
    private void OnGUI()
    {
        minSize = new Vector2(560, 420);
        using (new SmartRecoveryUI.WindowScope(this, "Smart Prefab Builder"))
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
        EditorGUILayout.LabelField("Smart Repeated Objects -> Prefabs", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "Automatic mode creates one prefab asset per unique mesh and connects every scene object " +
            "using that mesh. Existing scene transforms, components, children, materials and Inspector " +
            "values remain on each prefab instance as overrides. Scan is non-destructive.", MessageType.Info);

        scanMode = (ScanMode)EditorGUILayout.EnumPopup("Discovery Mode", scanMode);
        scanAllLoadedScenes = EditorGUILayout.ToggleLeft("Scan all loaded scenes", scanAllLoadedScenes);
        GUI.enabled = !scanAllLoadedScenes;
        searchRoot = (GameObject)EditorGUILayout.ObjectField(
            new GUIContent("Optional Scene Search Root", "Objects below this root are scanned when whole-scene scanning is disabled."),
            searchRoot, typeof(GameObject), true);
        GUI.enabled = true;

        EditorGUILayout.Space(4);
        EditorGUILayout.LabelField("Detection", EditorStyles.boldLabel);
        minimumRepeats = Mathf.Max(2, EditorGUILayout.IntField("Minimum Repeat Count", minimumRepeats));
        includeNonRepeatedObjects = EditorGUILayout.ToggleLeft(
            "Also create prefabs for mesh objects used only once", includeNonRepeatedObjects);
        scanDirectChildrenOnly = EditorGUILayout.ToggleLeft(
            "Only scan direct children (OFF = smart nested assembly detection)", scanDirectChildrenOnly);
        includeInactive = EditorGUILayout.ToggleLeft("Include inactive objects", includeInactive);
        matchDuplicatedMeshGeometry = EditorGUILayout.ToggleLeft(
            "Match duplicated meshes by geometry fingerprint", matchDuplicatedMeshGeometry);
        ignoreMaterialsWhenGrouping = EditorGUILayout.ToggleLeft(
            "Ignore material differences while grouping (recommended)", ignoreMaterialsWhenGrouping);
        includeComponentLayout = EditorGUILayout.ToggleLeft(
            "Use component layout for safer matching", includeComponentLayout);
        skipPrefabInstances = EditorGUILayout.ToggleLeft("Skip objects already connected to prefabs", skipPrefabInstances);
        excludeRiggedObjects = EditorGUILayout.ToggleLeft(
            "Leave rigged objects for Smart Rig Recovery", excludeRiggedObjects);

        EditorGUILayout.Space(5);
        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Scan & Preview", GUILayout.Height(32))) Scan();
            if (GUILayout.Button("Clear", GUILayout.Height(32))) groups.Clear();
        }

        DrawSummaryAndGroups();

        reuseExistingPrefabs = EditorGUILayout.ToggleLeft(
            "Reuse previously recovered prefab when available (recommended)", reuseExistingPrefabs);
        GUI.enabled = groups.Count > 0 && reuseExistingPrefabs;
        if (GUILayout.Button("Search Existing Prefabs", GUILayout.Height(28)))
            SearchExistingPrefabs();
        GUI.enabled = true;

        EditorGUILayout.Space(7);
        EditorGUILayout.LabelField("Apply", EditorStyles.boldLabel);
        outputPrefabFolder = (DefaultAsset)EditorGUILayout.ObjectField(
            "Prefab Output Folder", outputPrefabFolder, typeof(DefaultAsset), false);
        overwriteExistingPrefabs = EditorGUILayout.ToggleLeft("Overwrite existing prefab assets", overwriteExistingPrefabs);

        EditorGUILayout.LabelField("Preserve per scene instance", EditorStyles.miniBoldLabel);
        preserveNames = EditorGUILayout.ToggleLeft("Object names", preserveNames);
        preserveHierarchyTransforms = EditorGUILayout.ToggleLeft("Root + child transforms", preserveHierarchyTransforms);
        preserveObjectSettings = EditorGUILayout.ToggleLeft("Active state, layer, tag and static flags", preserveObjectSettings);
        preserveRendererSettings = EditorGUILayout.ToggleLeft(
            "Materials, lightmaps and renderer settings", preserveRendererSettings);
        preserveSceneVisibility = EditorGUILayout.ToggleLeft(
            "Hierarchy eye visibility + picking state", preserveSceneVisibility);

        int enabledGroups = groups.Count(g => g.enabled && g.template != null && g.matches.Count > 0);
        int enabledInstances = groups.Where(g => g.enabled).Sum(g => g.matches.Count);
        GUI.enabled = enabledGroups > 0 && outputPrefabFolder != null;
        if (GUILayout.Button(
            $"Create {enabledGroups} Prefabs + Connect {enabledInstances} Scene Objects", GUILayout.Height(42)))
            ApplyChanges();
        GUI.enabled = true;
        EditorGUILayout.Space(8);
        EditorGUILayout.EndScrollView();
    }

    private void DrawSummaryAndGroups()
    {
        if (groups.Count == 0) return;

        int repeated = groups.Sum(g => g.matches.Count);
        EditorGUILayout.Space(8);
        EditorGUILayout.HelpBox(
            $"Scan result: {groups.Count} prefab groups, {repeated} total scene occurrences. " +
            "Untick any group you do not want to change.", MessageType.None);

        scroll = EditorGUILayout.BeginScrollView(scroll, GUILayout.MinHeight(250));
        foreach (PrefabGroup group in groups)
        {
            using (new EditorGUILayout.VerticalScope("box"))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    group.enabled = EditorGUILayout.Toggle(group.enabled, GUILayout.Width(18));
                    group.expanded = EditorGUILayout.Foldout(group.expanded,
                        $"{group.prefabName}  -  repeats: {group.matches.Count}", true);
                }

                group.prefabName = EditorGUILayout.TextField("Prefab Name", group.prefabName);
                group.template = (GameObject)EditorGUILayout.ObjectField("Template", group.template, typeof(GameObject), true);
                EditorGUILayout.LabelField(
                    $"Meshes: {group.meshCount} | Renderers: {group.rendererCount} | {group.status}",
                    EditorStyles.miniLabel);
                if (group.existingPrefab != null)
                    EditorGUILayout.ObjectField("Will Reuse", group.existingPrefab, typeof(GameObject), false);

                if (group.expanded)
                {
                    EditorGUI.indentLevel++;
                    foreach (GameObject match in group.matches)
                        EditorGUILayout.ObjectField(match, typeof(GameObject), true);
                    EditorGUI.indentLevel--;
                }
            }
        }
        EditorGUILayout.EndScrollView();
    }

    private void Scan()
    {
        groups.Clear();
        existingPrefabSearchCompleted = false;

        if (scanMode != ScanMode.SelectedSceneObjectsOnly && !scanAllLoadedScenes && searchRoot == null)
        {
            EditorUtility.DisplayDialog("Scene Root Required",
                "Assign an Optional Scene Search Root or enable Scan all loaded scenes.", "OK");
            return;
        }

        try
        {
            EditorUtility.DisplayProgressBar("Smart Prefab Builder", "Collecting scene objects...", 0.15f);
            List<GameObject> candidates = GetCandidateRoots();
            if (candidates.Count == 0)
            {
                if (!automaticMode)
                    EditorUtility.DisplayDialog("Nothing To Scan",
                        "No eligible scene object roots were found. Check the mode, selection and filters.", "OK");
                return;
            }

            EditorUtility.DisplayProgressBar("Smart Prefab Builder",
                $"Comparing {candidates.Count} object hierarchies...", 0.55f);
            if (scanMode == ScanMode.AutomaticRepeatedObjects)
                ScanSmartMeshGroups();
            else if (scanMode == ScanMode.MeshDeduplicationAndParentPrefabs)
                ScanMeshDeduplication(candidates);
            else if (scanMode == ScanMode.SelectedSourcesFindAllMatches)
                ScanUsingSelectedSources(candidates);
            else
                GroupCandidates(candidates, false);

            groups.Sort((a, b) => b.matches.Count.CompareTo(a.matches.Count));
            if (!automaticMode && groups.Count == 0)
            {
                EditorUtility.DisplayDialog("Scan Complete - No Repeats Found",
                    $"Scanned {candidates.Count} mesh-containing object roots but found no eligible " +
                    $"{(includeNonRepeatedObjects ? "repeated or single-use" : "repeated")} groups.\n\n" +
                    "Turn OFF 'Only scan direct children' for nested objects, " +
                    "keep geometry fingerprint matching ON, or temporarily turn OFF component-layout matching.", "OK");
            }
        }
        catch (Exception ex)
        {
            Debug.LogError("Smart Prefab Builder scan failed:\n" + ex);
            EditorUtility.DisplayDialog("Scan Failed",
                "The scan hit an unexpected error. The full details were written to the Unity Console.", "OK");
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }
        Repaint();
    }

    private void SearchExistingPrefabs()
    {
        if (groups.Count == 0) return;

        foreach (PrefabGroup group in groups) group.existingPrefab = null;
        try
        {
            int registryFound = 0;
            SmartRecoveryPrefabRegistry.instance.PrepareForSearch();
            foreach (PrefabGroup group in groups)
            {
                if (group.template == null) continue;
                group.existingPrefab = SmartRecoveryPrefabRegistry.instance.Find(group.template);
                if (group.existingPrefab == null) continue;
                registryFound++;
                group.status = "Recovered FBX-backed prefab found - it will be reused";
            }

            string outputSearchFolder = outputPrefabFolder == null
                ? string.Empty : AssetDatabase.GetAssetPath(outputPrefabFolder);
            bool hasTargetedFolder = !string.IsNullOrEmpty(outputSearchFolder) &&
                                     AssetDatabase.IsValidFolder(outputSearchFolder);
            string[] prefabGuids = hasTargetedFolder
                ? AssetDatabase.FindAssets("t:Prefab", new[] { outputSearchFolder })
                : Array.Empty<string>();
            Dictionary<string, List<GameObject>> prefabsByHierarchy =
                new Dictionary<string, List<GameObject>>();
            Dictionary<string, List<GameObject>> prefabsByMesh =
                new Dictionary<string, List<GameObject>>();

            for (int i = 0; i < prefabGuids.Length; i++)
            {
                string path = AssetDatabase.GUIDToAssetPath(prefabGuids[i]);
                if (!path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)) continue;
                if (path.StartsWith("Assets/CityTools_Backups/", StringComparison.OrdinalIgnoreCase)) continue;

                EditorUtility.DisplayProgressBar("Searching Existing Prefabs",
                    $"Checking {i + 1} of {prefabGuids.Length}: {Path.GetFileName(path)}",
                    prefabGuids.Length == 0 ? 1f : (float)i / prefabGuids.Length);

                GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab == null || !HasAnyMesh(prefab)) continue;

                string hierarchySignature = BuildSignature(prefab);
                if (!prefabsByHierarchy.TryGetValue(hierarchySignature, out List<GameObject> list))
                {
                    list = new List<GameObject>();
                    prefabsByHierarchy.Add(hierarchySignature, list);
                }
                list.Add(prefab);

                if (HasDirectMesh(prefab))
                {
                    string meshSignature = BuildDirectMeshSignature(prefab);
                    if (!prefabsByMesh.TryGetValue(meshSignature, out List<GameObject> meshList))
                    {
                        meshList = new List<GameObject>();
                        prefabsByMesh.Add(meshSignature, meshList);
                    }
                    meshList.Add(prefab);
                }
            }

            int found = 0;
            foreach (PrefabGroup group in groups)
            {
                if (group.template == null) continue;
                if (group.existingPrefab != null)
                {
                    found++;
                    continue;
                }
                string signature = group.kind == GroupKind.SharedMesh
                    ? BuildDirectMeshSignature(group.template)
                    : BuildSignature(group.template);
                Dictionary<string, List<GameObject>> index = group.kind == GroupKind.SharedMesh
                    ? prefabsByMesh : prefabsByHierarchy;
                if (!index.TryGetValue(signature, out List<GameObject> matches)) continue;

                if (group.kind == GroupKind.SharedMesh)
                    matches = matches.Where(IsCleanMeshReferencePrefab).ToList();
                if (matches.Count == 0) continue;

                group.existingPrefab = matches
                    .OrderBy(prefab => prefab.GetComponentsInChildren<Transform>(true).Length)
                    .ThenBy(prefab => prefab.GetComponents<Component>().Length)
                    .ThenBy(prefab => AssetDatabase.GetAssetPath(prefab).Length)
                    .ThenBy(prefab => AssetDatabase.GetAssetPath(prefab), StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
                if (group.existingPrefab != null)
                {
                    found++;
                    group.status = "Existing prefab found - it will be reused";
                }
            }

            existingPrefabSearchCompleted = true;
            if (!automaticMode)
                EditorUtility.DisplayDialog("Existing Prefab Search Complete",
                    $"Persistent registry checked once.\n" +
                    $"Targeted folder: {(hasTargetedFolder ? outputSearchFolder : "None (registry-only)")}\n" +
                    $"Prefabs inspected in targeted folder: {prefabGuids.Length}\n\n" +
                    $"Recovered prefabs found in persistent registry: {registryFound}\n" +
                    $"Matching groups found: {found}\n" +
                    $"New prefab assets still required: {groups.Count(g => g.enabled && g.existingPrefab == null)}",
                    "OK");
        }
        catch (Exception ex)
        {
            Debug.LogError("Existing prefab search failed:\n" + ex);
            EditorUtility.DisplayDialog("Prefab Search Failed",
                "The search failed. Full details were written to the Unity Console.", "OK");
        }
        finally
        {
            EditorUtility.ClearProgressBar();
            Repaint();
        }
    }

    private List<GameObject> GetCandidateRoots()
    {
        IEnumerable<GameObject> result;

        if (scanMode == ScanMode.SelectedSceneObjectsOnly)
        {
            result = Selection.gameObjects.Where(go => go != null && go.scene.IsValid());
        }
        else if (!scanAllLoadedScenes && scanDirectChildrenOnly)
        {
            result = searchRoot.transform.Cast<Transform>().Select(t => t.gameObject);
        }
        else if (!scanAllLoadedScenes)
        {
            result = searchRoot.GetComponentsInChildren<Transform>(includeInactive)
                .Where(t => t != searchRoot.transform)
                .Select(t => t.gameObject);
        }

        else
        {
            result = GetAllLoadedSceneObjects();
        }

        return result
            .Where(go => go != null)
            .Where(go => includeInactive || go.activeInHierarchy)
            .Where(go => !skipPrefabInstances || !PrefabUtility.IsPartOfPrefabInstance(go))
            .Where(go => !excludeRiggedObjects || !IsRiggedArt(go))
            .Where(HasAnyMesh)
            .Distinct()
            .ToList();
    }

    private void GroupCandidates(List<GameObject> candidates, bool repeatsOnly)
    {
        List<IGrouping<string, GameObject>> buckets = candidates
            .GroupBy(BuildSignature)
            .OrderByDescending(bucket => bucket.Max(go => go.GetComponentsInChildren<Transform>(true).Length))
            .ToList();
        List<GameObject> acceptedAssemblyRoots = new List<GameObject>();

        foreach (IGrouping<string, GameObject> bucket in buckets)
        {
            List<GameObject> matches = bucket
                .Where(go => !repeatsOnly || !acceptedAssemblyRoots.Any(parent =>
                    go != parent && go.transform.IsChildOf(parent.transform)))
                .OrderBy(GetSceneSortKey)
                .ToList();
            if (repeatsOnly && matches.Count < minimumRepeats &&
                !(includeNonRepeatedObjects && matches.Count == 1)) continue;
            if (repeatsOnly && matches.Count == 1 && includeNonRepeatedObjects &&
                !HasDirectMesh(matches[0])) continue;

            GameObject template = matches[0];
            groups.Add(new PrefabGroup
            {
                signature = bucket.Key,
                prefabName = MakeUniqueSuggestedName(template.name),
                template = template,
                matches = matches,
                meshCount = CountMeshes(template),
                rendererCount = template.GetComponentsInChildren<Renderer>(true).Length,
                status = matches.Count >= minimumRepeats
                    ? "Ready - repeated object"
                    : "Ready - single-use object",
                kind = GroupKind.Standard
            });

            if (repeatsOnly)
                acceptedAssemblyRoots.AddRange(matches);
        }
    }

    private void ScanSmartMeshGroups()
    {
        List<GameObject> allObjects = GetCandidateRoots()
            .Where(gameObject => includeInactive || gameObject.activeInHierarchy)
            .Where(gameObject => !skipPrefabInstances || !PrefabUtility.IsPartOfPrefabInstance(gameObject))
            .ToList();

        List<GameObject> meshObjects = allObjects.Where(HasDirectMesh).ToList();

        // First promote repeated meshes to the largest parent whose complete subtree repeats.
        // Larger/deeper assemblies are accepted before their nested candidates, preventing
        // duplicate prefab targets while preserving multi-mesh assemblies as one prefab.
        List<IGrouping<string, GameObject>> repeatedAssemblies = allObjects
            .Where(go => go.transform.childCount > 0 && HasAnyMesh(go))
            .GroupBy(BuildSignature)
            .Where(bucket => bucket.Count() >= minimumRepeats)
            .OrderByDescending(bucket => bucket.Max(go => CountMeshes(go)))
            .ThenByDescending(bucket => bucket.Max(go => go.GetComponentsInChildren<Transform>(true).Length))
            .ToList();

        HashSet<GameObject> coveredMeshObjects = new HashSet<GameObject>();
        List<GameObject> acceptedAssemblyRoots = new List<GameObject>();
        foreach (IGrouping<string, GameObject> bucket in repeatedAssemblies)
        {
            List<GameObject> matches = bucket.OrderBy(GetSceneSortKey).ToList();
            if (matches.Any(match => acceptedAssemblyRoots.Any(root =>
                match == root || match.transform.IsChildOf(root.transform))))
                continue;

            // Every occurrence must have the same full hierarchy signature. BuildSignature includes
            // normalized names, relative transforms, mesh geometry and optional component layout.
            GameObject template = matches[0];
            groups.Add(new PrefabGroup
            {
                signature = bucket.Key,
                prefabName = MakeUniqueSuggestedName(NormalizeRepeatedName(template.name) + "_Assembly"),
                template = template,
                matches = matches,
                meshCount = CountMeshes(template),
                rendererCount = template.GetComponentsInChildren<Renderer>(true).Length,
                status = $"Smart parent assembly - {matches.Count} matching parents, " +
                         $"{CountMeshes(template)} mesh objects each",
                kind = GroupKind.ParentAssembly
            });
            acceptedAssemblyRoots.AddRange(matches);
            foreach (GameObject match in matches)
                foreach (Transform child in match.GetComponentsInChildren<Transform>(true))
                    if (HasDirectMesh(child.gameObject)) coveredMeshObjects.Add(child.gameObject);
        }

        // A non-repeated parent that owns a mesh and also contains child meshes must remain one
        // unique assembly. Creating separate parent/child prefab targets would overlap and be unsafe.
        if (includeNonRepeatedObjects)
        {
            List<GameObject> uniqueMultiMeshRoots = allObjects
                .Where(go => !coveredMeshObjects.Contains(go) && HasDirectMesh(go) && CountMeshes(go) > 1)
                .OrderBy(go => GetTransformDepth(go.transform))
                .ThenByDescending(CountMeshes)
                .ToList();
            foreach (GameObject candidate in uniqueMultiMeshRoots)
            {
                if (coveredMeshObjects.Contains(candidate)) continue;
                groups.Add(new PrefabGroup
                {
                    signature = BuildSignature(candidate),
                    prefabName = MakeUniqueSuggestedName(NormalizeRepeatedName(candidate.name) + "_Assembly"),
                    template = candidate,
                    matches = new List<GameObject> { candidate },
                    meshCount = CountMeshes(candidate),
                    rendererCount = candidate.GetComponentsInChildren<Renderer>(true).Length,
                    status = "Unique multi-mesh parent - individual assembly prefab",
                    kind = GroupKind.Standard
                });
                foreach (Transform child in candidate.GetComponentsInChildren<Transform>(true))
                    if (HasDirectMesh(child.gameObject)) coveredMeshObjects.Add(child.gameObject);
            }
        }

        // Repeated meshes with different parents, and unique meshes, safely fall back to their own
        // mesh-object prefab. A repeated mesh group still creates only one shared prefab asset.
        List<GameObject> fallbackMeshes = meshObjects.Where(go => !coveredMeshObjects.Contains(go)).ToList();
        foreach (IGrouping<string, GameObject> bucket in fallbackMeshes.GroupBy(BuildDirectMeshSignature))
        {
            List<GameObject> matches = bucket.OrderBy(GetSceneSortKey).ToList();
            if (matches.Count < minimumRepeats && !(includeNonRepeatedObjects && matches.Count == 1))
                continue;

            GameObject template = matches[0];
            groups.Add(new PrefabGroup
            {
                signature = bucket.Key,
                prefabName = MakeUniqueSuggestedName(NormalizeRepeatedName(template.name) + "_Mesh"),
                template = template,
                matches = matches,
                meshCount = GetDirectMeshes(template).Count(),
                rendererCount = template.GetComponents<Renderer>().Length,
                status = matches.Count > 1
                    ? $"Mesh fallback - parent assemblies differ; shared by {matches.Count} objects"
                    : "Unique mesh object - individual prefab",
                kind = GroupKind.SharedMesh
            });
        }
    }

    private void ScanMeshDeduplication(List<GameObject> candidates)
    {
        // Stage 1 preview: every mesh-owning GameObject is grouped by the mesh data it directly uses.
        List<GameObject> meshOwners = GetCandidateRoots()
            .Where(go => !skipPrefabInstances || !PrefabUtility.IsPartOfPrefabInstance(go))
            .Where(HasDirectMesh)
            .ToList();

        foreach (IGrouping<string, GameObject> bucket in meshOwners.GroupBy(BuildDirectMeshSignature))
        {
            List<GameObject> matches = bucket.OrderBy(GetSceneSortKey).ToList();
            GameObject template = matches[0];
            groups.Add(new PrefabGroup
            {
                signature = bucket.Key,
                prefabName = MakeUniqueSuggestedName(template.name + "_Mesh"),
                template = template,
                matches = matches,
                meshCount = GetDirectMeshes(template).Count(),
                rendererCount = template.GetComponents<Renderer>().Length,
                status = $"Shared mesh prefab - {matches.Count} scene uses",
                kind = GroupKind.SharedMesh
            });
        }

        // Stage 2 preview: direct child containers become parent assembly prefabs. Repeated
        // assemblies share one asset and will contain the stage-1 mesh prefabs as nested instances.
        List<GameObject> meshTargets = groups.Where(g => g.kind == GroupKind.SharedMesh)
            .SelectMany(g => g.matches).ToList();
        List<GameObject> parentRoots = GetCandidateRoots()
            .Where(go => go.transform.parent != null)
            .Where(go => HasAnyMesh(go) && !HasDirectMesh(go))
            .Where(go => !meshTargets.Contains(go))
            .Where(go => !skipPrefabInstances || !PrefabUtility.IsPartOfPrefabInstance(go))
            .ToList();

        foreach (IGrouping<string, GameObject> bucket in parentRoots.GroupBy(BuildSignature))
        {
            List<GameObject> matches = bucket.OrderBy(GetSceneSortKey).ToList();
            GameObject template = matches[0];
            groups.Add(new PrefabGroup
            {
                signature = bucket.Key,
                prefabName = MakeUniqueSuggestedName(template.name + "_Assembly"),
                template = template,
                matches = matches,
                meshCount = CountMeshes(template),
                rendererCount = template.GetComponentsInChildren<Renderer>(true).Length,
                status = $"Parent assembly prefab - {matches.Count} scene uses",
                kind = GroupKind.ParentAssembly
            });
        }
    }

    private string BuildDirectMeshSignature(GameObject go)
    {
        StringBuilder sb = new StringBuilder(128);
        sb.Append("N:").Append(NormalizeRepeatedName(go.name)).Append('|');
        MeshFilter mf = go.GetComponent<MeshFilter>();
        if (mf != null) { sb.Append("MeshFilter|"); AppendMeshIdentity(sb, mf.sharedMesh); }
        SkinnedMeshRenderer smr = go.GetComponent<SkinnedMeshRenderer>();
        if (smr != null) { sb.Append("SkinnedMeshRenderer|"); AppendMeshIdentity(sb, smr.sharedMesh); }
        AppendStaticBatchIdentity(sb, go);
        return sb.ToString();
    }

    private List<GameObject> GetAllLoadedSceneObjects()
    {
        List<GameObject> result = new List<GameObject>();
        for (int sceneIndex = 0; sceneIndex < SceneManager.sceneCount; sceneIndex++)
        {
            Scene scene = SceneManager.GetSceneAt(sceneIndex);
            if (!scene.IsValid() || !scene.isLoaded) continue;
            foreach (GameObject root in scene.GetRootGameObjects())
                result.AddRange(root.GetComponentsInChildren<Transform>(includeInactive)
                    .Where(t => includeInactive || t.gameObject.activeInHierarchy)
                    .Select(t => t.gameObject));
        }
        return result.Distinct().ToList();
    }

    private void ScanUsingSelectedSources(List<GameObject> candidates)
    {
        HashSet<Mesh> selectedMeshes = CollectSelectedMeshes();
        HashSet<string> selectedSignatures = new HashSet<string>(
            Selection.gameObjects
                .Where(go => go != null && go.scene.IsValid() && HasAnyMesh(go))
                .Select(BuildSignature));

        if (selectedMeshes.Count == 0 && selectedSignatures.Count == 0)
        {
            EditorUtility.DisplayDialog("Source Selection Required",
                "Select one or more source scene objects, Mesh assets, or FBX/model assets, then scan again.", "OK");
            return;
        }

        List<GameObject> matchingRoots = candidates
            .Where(go => selectedSignatures.Contains(BuildSignature(go)) ||
                         GetMeshes(go).Any(selectedMeshes.Contains))
            .ToList();

        GroupCandidates(matchingRoots, false);
        foreach (PrefabGroup group in groups)
            group.status = $"Ready - matched selected source ({group.matches.Count} occurrences)";
    }

    private HashSet<Mesh> CollectSelectedMeshes()
    {
        HashSet<Mesh> meshes = new HashSet<Mesh>();
        foreach (UnityEngine.Object selected in Selection.objects)
        {
            Mesh mesh = selected as Mesh;
            if (mesh != null) meshes.Add(mesh);

            GameObject model = selected as GameObject;
            if (model != null && EditorUtility.IsPersistent(model))
                foreach (Mesh childMesh in GetMeshes(model)) meshes.Add(childMesh);
        }
        return meshes;
    }

    private string BuildSignature(GameObject root)
    {
        StringBuilder sb = new StringBuilder(512);
        Transform[] transforms = root.GetComponentsInChildren<Transform>(true);
        foreach (Transform t in transforms)
        {
            string path = GetIndexPath(root.transform, t);
            sb.Append(path).Append(':').Append(NormalizeRepeatedName(t.name)).Append(':');
            AppendLocalTransform(sb, t == root.transform ? null : t);
            sb.Append('{');

            MeshFilter mf = t.GetComponent<MeshFilter>();
            if (mf != null)
            {
                AppendMeshIdentity(sb, mf.sharedMesh);
                AppendStaticBatchIdentity(sb, t.gameObject);
            }

            SkinnedMeshRenderer smr = t.GetComponent<SkinnedMeshRenderer>();
            if (smr != null) AppendMeshIdentity(sb, smr.sharedMesh);

            if (!ignoreMaterialsWhenGrouping)
            {
                Renderer renderer = t.GetComponent<Renderer>();
                if (renderer != null)
                    foreach (Material material in renderer.sharedMaterials) AppendAssetIdentity(sb, material);
            }

            if (includeComponentLayout)
            {
                foreach (Component component in t.GetComponents<Component>())
                {
                    if (component == null || component is Transform) continue;
                    sb.Append(component.GetType().AssemblyQualifiedName).Append(';');
                }
            }
            sb.Append('}');
        }
        return sb.ToString();
    }

    private static void AppendLocalTransform(StringBuilder sb, Transform transform)
    {
        if (transform == null) { sb.Append("ROOT|"); return; }
        Vector3 position = transform.localPosition;
        Quaternion rotation = transform.localRotation;
        Vector3 scale = transform.localScale;
        sb.Append(Quantize(position.x)).Append(',').Append(Quantize(position.y)).Append(',')
            .Append(Quantize(position.z)).Append('|')
            .Append(Quantize(rotation.x)).Append(',').Append(Quantize(rotation.y)).Append(',')
            .Append(Quantize(rotation.z)).Append(',').Append(Quantize(rotation.w)).Append('|')
            .Append(Quantize(scale.x)).Append(',').Append(Quantize(scale.y)).Append(',')
            .Append(Quantize(scale.z)).Append('|');
    }

    private static void AppendStaticBatchIdentity(StringBuilder sb, GameObject gameObject)
    {
        MeshRenderer renderer = gameObject.GetComponent<MeshRenderer>();
        if (renderer == null) return;
        int first = 0;
        int count = 0;
        SerializedProperty info = new SerializedObject(renderer).FindProperty("m_StaticBatchInfo");
        if (info != null)
        {
            SerializedProperty firstProperty = info.FindPropertyRelative("firstSubMesh");
            SerializedProperty countProperty = info.FindPropertyRelative("subMeshCount");
            if (firstProperty != null) first = firstProperty.intValue;
            if (countProperty != null) count = countProperty.intValue;
        }
        if (count <= 0) return;
        MeshFilter filter = gameObject.GetComponent<MeshFilter>();
        Mesh mesh = filter == null ? null : filter.sharedMesh;
        if (mesh == null || first < 0 || first + count > mesh.subMeshCount) return;

        // Do not include 'first': it is an occurrence-specific offset inside the combined mesh.
        // Topology counts plus normalized hierarchy names identify repeated static-batch parts.
        sb.Append("B:").Append(count).Append(':');
        for (int sub = first; sub < first + count; sub++)
        {
            SubMeshDescriptor descriptor = mesh.GetSubMesh(sub);
            sb.Append(descriptor.vertexCount).Append(',').Append(descriptor.indexCount).Append(',')
                .Append((int)descriptor.topology).Append(';');
        }
        sb.Append('|');
    }

    private static string NormalizeRepeatedName(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "Object";
        string result = value.Trim();
        // Exported hierarchies commonly use _001, .002, " (1)", Copy and Clone suffixes.
        string previous;
        do
        {
            previous = result;
            result = System.Text.RegularExpressions.Regex.Replace(result,
                @"\s*\(\d+\)\s*$|(?:[ _.-]*\d+|[ _.-]+(?:copy|clone))\s*$", "",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();
        } while (!string.Equals(previous, result, StringComparison.Ordinal));
        return string.IsNullOrWhiteSpace(result) ? value.Trim() : result.Trim();
    }

    private void AppendMeshIdentity(StringBuilder sb, Mesh mesh)
    {
        sb.Append("M:");
        if (mesh == null) { sb.Append("null|"); return; }

        if (!matchDuplicatedMeshGeometry)
        {
            AppendAssetIdentity(sb, mesh);
            return;
        }

        Bounds bounds = mesh.bounds;
        sb.Append(mesh.vertexCount).Append(':')
            .Append(mesh.subMeshCount).Append(':')
            .Append(Quantize(bounds.size.x)).Append(':')
            .Append(Quantize(bounds.size.y)).Append(':')
            .Append(Quantize(bounds.size.z)).Append(':');
        for (int i = 0; i < mesh.subMeshCount; i++)
            sb.Append((long)mesh.GetIndexCount(i)).Append(',');
        sb.Append(':').Append(GetExactMeshGeometryFingerprint(mesh));
        sb.Append('|');
    }

    private string GetExactMeshGeometryFingerprint(Mesh mesh)
    {
        if (meshGeometryFingerprints.TryGetValue(mesh, out string cached)) return cached;
        try
        {
            unchecked
            {
                ulong hash = 1469598103934665603UL;
                Action<int> add = value => { hash ^= (uint)value; hash *= 1099511628211UL; };
                using (Mesh.MeshDataArray array = MeshUtility.AcquireReadOnlyMeshData(mesh))
                {
                    Mesh.MeshData data = array[0];
                    string[] vertexKeys;
                    NativeArray<Vector3> positions = new NativeArray<Vector3>(data.vertexCount,
                        Allocator.Temp, NativeArrayOptions.UninitializedMemory);
                    try
                    {
                        data.GetVertices(positions);
                        vertexKeys = positions.Select(position =>
                            Quantize(position.x) + "," + Quantize(position.y) + "," + Quantize(position.z))
                            .ToArray();
                        foreach (string key in vertexKeys.OrderBy(key => key, StringComparer.Ordinal))
                            foreach (char character in key) add(character);
                    }
                    finally { positions.Dispose(); }
                    for (int subMesh = 0; subMesh < mesh.subMeshCount; subMesh++)
                    {
                        SubMeshDescriptor descriptor = data.GetSubMesh(subMesh);
                        add(descriptor.indexCount);
                        add((int)descriptor.topology);
                        NativeArray<int> indices = new NativeArray<int>(descriptor.indexCount,
                            Allocator.Temp, NativeArrayOptions.UninitializedMemory);
                        try
                        {
                            data.GetIndices(indices, subMesh, true);
                            List<string> triangles = new List<string>(indices.Length / 3);
                            for (int index = 0; index + 2 < indices.Length; index += 3)
                            {
                                string[] corners =
                                {
                                    vertexKeys[indices[index]], vertexKeys[indices[index + 1]],
                                    vertexKeys[indices[index + 2]]
                                };
                                Array.Sort(corners, StringComparer.Ordinal);
                                triangles.Add(corners[0] + "|" + corners[1] + "|" + corners[2]);
                            }
                            foreach (string triangle in triangles.OrderBy(value => value, StringComparer.Ordinal))
                                foreach (char character in triangle) add(character);
                        }
                        finally { indices.Dispose(); }
                    }
                }
                cached = hash.ToString("X16");
            }
        }
        catch
        {
            cached = AssetDatabase.TryGetGUIDAndLocalFileIdentifier(mesh, out string guid, out long localId)
                ? guid + ":" + localId : mesh.GetInstanceID().ToString();
        }
        meshGeometryFingerprints[mesh] = cached;
        return cached;
    }

    private static int Quantize(float value)
    {
        return Mathf.RoundToInt(value * 10000f);
    }

    private static void AppendAssetIdentity(StringBuilder sb, UnityEngine.Object asset)
    {
        if (asset == null) { sb.Append("null|"); return; }
        if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(asset, out string guid, out long localId))
            sb.Append(guid).Append(':').Append(localId).Append('|');
        else
            sb.Append(asset.GetInstanceID()).Append('|');
    }

    private void ApplyChanges()
    {
        if (!ValidateOutputFolder(out string folderPath)) return;
        if (reuseExistingPrefabs && !existingPrefabSearchCompleted)
            SearchExistingPrefabs();

        List<PrefabGroup> ready = groups
            .Where(g => g.enabled && g.template != null && g.matches.Count > 0)
            // Always build deepest targets first. A later parent prefab then captures the already
            // connected child prefab instances, producing a safe nested-prefab hierarchy.
            .OrderByDescending(g => GetTransformDepth(g.template.transform))
            .ThenBy(g => g.kind == GroupKind.SharedMesh ? 0 :
                         g.kind == GroupKind.ParentAssembly ? 1 : 2)
            .ToList();
        if (ready.Count == 0) return;

        List<GameObject> allTargets = ready.SelectMany(g => g.matches)
            .Where(go => go != null).Distinct().ToList();
        bool hasNestedTargets = allTargets.Any(candidate => allTargets.Any(other =>
            other != candidate && candidate.transform.IsChildOf(other.transform)));
        bool meshReferenceWorkflow = ready.All(group => group.kind == GroupKind.SharedMesh);
        bool supportsSmartNestedWorkflow = scanMode == ScanMode.AutomaticRepeatedObjects ||
                                           scanMode == ScanMode.MeshDeduplicationAndParentPrefabs;
        if (hasNestedTargets && !meshReferenceWorkflow && !supportsSmartNestedWorkflow)
        {
            EditorUtility.DisplayDialog("Overlapping Scan Results",
                "Some enabled matches are children of other enabled matches. Applying both would be unsafe. " +
                "Use direct-child scanning, change the Scene Root, or untick the unwanted nested groups.", "OK");
            return;
        }

        foreach (PrefabGroup group in ready)
            group.prefabName = SanitizeFileName(group.prefabName);

        List<string> duplicateNames = ready
            .Where(g => !reuseExistingPrefabs || g.existingPrefab == null)
            .GroupBy(g => g.prefabName, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (duplicateNames.Count > 0)
        {
            EditorUtility.DisplayDialog("Duplicate Prefab Names",
                "Use a unique name for each group:\n\n" + string.Join("\n", duplicateNames), "OK");
            return;
        }

        int occurrenceCount = ready.Sum(g => g.matches.Count);
        int reuseCount = ready.Count(g => reuseExistingPrefabs && g.existingPrefab != null);
        int createCount = ready.Count - reuseCount;
        if (!automaticMode && !EditorUtility.DisplayDialog("Create And Connect Prefabs",
            $"Existing prefab assets to reuse: {reuseCount}\n" +
            $"New prefab assets to create: {createCount}\n" +
            $"Scene objects to connect: {occurrenceCount}\nOutput for new assets: {folderPath}\n\n" +
            "This operation supports Undo, but saving a backup scene is recommended.", "Apply", "Cancel")) return;

        Undo.IncrementCurrentGroup();
        int undoGroup = Undo.GetCurrentGroup();
        Undo.SetCurrentGroupName("Smart Build And Connect Prefabs");

        int created = 0, reused = 0, connected = 0, failed = 0;
        foreach (PrefabGroup group in ready)
        {
            string prefabPath = (folderPath + "/" + group.prefabName + ".prefab").Replace("\\", "/");
            GameObject prefab = reuseExistingPrefabs ? group.existingPrefab : null;
            GameObject existingAtOutput = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (prefab == null && existingAtOutput != null && !overwriteExistingPrefabs)
            {
                group.status = "Skipped - prefab already exists";
                failed++;
                continue;
            }

            try
            {
                bool reusedThisPrefab = prefab != null;
                if (prefab == null)
                {
                    bool success;
                    prefab = group.kind == GroupKind.SharedMesh
                        ? SaveMeshReferencePrefab(group.template, prefabPath, out success)
                        : PrefabUtility.SaveAsPrefabAsset(group.template, prefabPath, out success);
                    if (!success || prefab == null) throw new Exception("Unity could not save the prefab asset.");
                    created++;
                }
                else
                {
                    reused++;
                }

                if (automaticMode && prefab != null && !lastAutomaticPrefabs.Contains(prefab))
                    lastAutomaticPrefabs.Add(prefab);

                int groupConnected = 0;
                foreach (GameObject oldObject in group.matches.Where(x => x != null).Distinct().ToList())
                {
                    try
                    {
                        ReplaceWithPrefab(oldObject, prefab,
                            reusedThisPrefab ||
                            (group.kind == GroupKind.SharedMesh && oldObject.GetComponent<MeshFilter>() != null),
                            reusedThisPrefab);
                        connected++;
                        groupConnected++;
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        Debug.LogError($"Smart Prefab Builder failed for '{oldObject.name}':\n{ex}");
                    }
                }
                group.status = reuseExistingPrefabs && group.existingPrefab != null
                    ? $"Done - reused existing prefab for {groupConnected} instances"
                    : $"Done - new prefab connected to {groupConnected} instances";
            }
            catch (Exception ex)
            {
                failed++;
                group.status = "Failed - see Console";
                Debug.LogError($"Smart Prefab Builder failed for group '{group.prefabName}':\n{ex}");
            }
        }

        Undo.CollapseUndoOperations(undoGroup);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        MarkLoadedScenesDirty();
        Repaint();
        if (!automaticMode)
            EditorUtility.DisplayDialog("Smart Prefab Builder",
                $"Finished.\n\nExisting prefabs reused: {reused}\n" +
                $"New prefabs created: {created}\nScene objects connected: {connected}\n" +
                $"Skipped/failed: {failed}", "OK");
    }

    private GameObject SaveMeshReferencePrefab(GameObject source, string prefabPath, out bool success)
    {
        MeshFilter sourceFilter = source.GetComponent<MeshFilter>();
        MeshRenderer sourceRenderer = source.GetComponent<MeshRenderer>();

        // Skinned meshes depend on bones and hierarchy, so retain their complete source hierarchy.
        if (sourceFilter == null || sourceFilter.sharedMesh == null)
            return PrefabUtility.SaveAsPrefabAsset(source, prefabPath, out success);

        GameObject temporary = new GameObject(source.name);
        try
        {
            MeshFilter filter = temporary.AddComponent<MeshFilter>();
            filter.sharedMesh = sourceFilter.sharedMesh;
            if (sourceRenderer != null)
            {
                MeshRenderer renderer = temporary.AddComponent<MeshRenderer>();
                EditorUtility.CopySerialized(sourceRenderer, renderer);
                ClearStaticBatchData(renderer);
            }

            return PrefabUtility.SaveAsPrefabAsset(temporary, prefabPath, out success);
        }
        finally
        {
            DestroyImmediate(temporary);
        }
    }

    private void ReplaceWithPrefab(GameObject oldObject, GameObject prefab,
        bool preserveCompleteInstance, bool reuseExistingHierarchy)
    {
        InstanceState state = CaptureInstanceState(oldObject);
        Transform oldTransform = oldObject.transform;
        Transform oldParent = oldTransform.parent;
        int siblingIndex = oldTransform.GetSiblingIndex();

        GameObject replacement = PrefabUtility.InstantiatePrefab(prefab, oldObject.scene) as GameObject;
        if (replacement == null) throw new Exception("Could not instantiate the new prefab.");

        Undo.RegisterCreatedObjectUndo(replacement, "Create Prefab Instance");
        replacement.transform.SetParent(oldParent, false);
        replacement.transform.SetSiblingIndex(siblingIndex);
        if (reuseExistingHierarchy)
            CopyMatchingHierarchyOverrides(oldObject, replacement);
        else if (preserveCompleteInstance)
            CopyCompleteInstanceOverrides(oldObject, replacement);
        RestoreInstanceState(replacement, state);
        Undo.DestroyObjectImmediate(oldObject);
    }

    private static void CopyMatchingHierarchyOverrides(GameObject sourceRoot, GameObject destinationRoot)
    {
        Dictionary<string, Transform> destinations = destinationRoot
            .GetComponentsInChildren<Transform>(true)
            .ToDictionary(t => GetIndexPath(destinationRoot.transform, t), t => t);

        foreach (Transform sourceTransform in sourceRoot.GetComponentsInChildren<Transform>(true))
        {
            string path = GetIndexPath(sourceRoot.transform, sourceTransform);
            if (!destinations.TryGetValue(path, out Transform destinationTransform)) continue;

            Dictionary<Type, int> occurrences = new Dictionary<Type, int>();
            foreach (Component sourceComponent in sourceTransform.GetComponents<Component>())
            {
                if (sourceComponent == null || sourceComponent is Transform || sourceComponent is MeshFilter)
                    continue;

                Type type = sourceComponent.GetType();
                int occurrence = occurrences.TryGetValue(type, out int count) ? count : 0;
                occurrences[type] = occurrence + 1;
                Component[] candidates = destinationTransform.GetComponents(type);
                if (occurrence >= candidates.Length) continue;
                Component destinationComponent = candidates[occurrence];

                Mesh protectedMesh = null;
                if (destinationComponent is SkinnedMeshRenderer destinationSkinned)
                    protectedMesh = destinationSkinned.sharedMesh;
                else if (destinationComponent is MeshCollider destinationCollider)
                    protectedMesh = destinationCollider.sharedMesh;

                Undo.RecordObject(destinationComponent, "Preserve Scene Inspector Overrides");
                EditorUtility.CopySerialized(sourceComponent, destinationComponent);

                // Keep the solid FBX mesh owned by the recovered prefab while copying every
                // other Inspector value from the scene occurrence.
                if (destinationComponent is SkinnedMeshRenderer copiedSkinned && protectedMesh != null)
                    copiedSkinned.sharedMesh = protectedMesh;
                else if (destinationComponent is MeshCollider copiedCollider && protectedMesh != null)
                    copiedCollider.sharedMesh = protectedMesh;
                if (destinationComponent is Renderer renderer)
                    ClearStaticBatchData(renderer);
            }
        }
    }

    private static void CopyCompleteInstanceOverrides(GameObject source, GameObject destination)
    {
        Dictionary<Type, int> occurrences = new Dictionary<Type, int>();
        foreach (Component sourceComponent in source.GetComponents<Component>())
        {
            if (sourceComponent == null || sourceComponent is Transform || sourceComponent is MeshFilter)
                continue;

            Type type = sourceComponent.GetType();
            int occurrence = occurrences.TryGetValue(type, out int count) ? count : 0;
            occurrences[type] = occurrence + 1;

            Component[] existing = destination.GetComponents(type);
            Component destinationComponent = occurrence < existing.Length
                ? existing[occurrence]
                : Undo.AddComponent(destination, type);
            if (destinationComponent == null) continue;

            EditorUtility.CopySerialized(sourceComponent, destinationComponent);
            if (destinationComponent is Renderer renderer)
                ClearStaticBatchData(renderer);
        }

        // The mesh prefab is deliberately clean. Original children remain unique scene-instance
        // overrides, including their components, nested prefab links and serialized values.
        foreach (Transform sourceChild in source.transform.Cast<Transform>().ToList())
        {
            GameObject childCopy = Instantiate(sourceChild.gameObject);
            childCopy.name = sourceChild.name;
            Undo.RegisterCreatedObjectUndo(childCopy, "Preserve Scene Object Children");
            childCopy.transform.SetParent(destination.transform, false);
            childCopy.transform.SetSiblingIndex(sourceChild.GetSiblingIndex());
        }
    }

    private static void ClearStaticBatchData(Renderer renderer)
    {
        if (renderer == null) return;
        SerializedObject serializedRenderer = new SerializedObject(renderer);
        SerializedProperty batchRoot = serializedRenderer.FindProperty("m_StaticBatchRoot");
        if (batchRoot != null) batchRoot.objectReferenceValue = null;
        SerializedProperty batchInfo = serializedRenderer.FindProperty("m_StaticBatchInfo");
        if (batchInfo != null)
        {
            SerializedProperty firstSubMesh = batchInfo.FindPropertyRelative("firstSubMesh");
            SerializedProperty subMeshCount = batchInfo.FindPropertyRelative("subMeshCount");
            if (firstSubMesh != null) firstSubMesh.intValue = 0;
            if (subMeshCount != null) subMeshCount.intValue = 0;
        }
        serializedRenderer.ApplyModifiedPropertiesWithoutUndo();
    }

    private InstanceState CaptureInstanceState(GameObject root)
    {
        InstanceState state = new InstanceState();
        foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
        {
            string key = GetIndexPath(root.transform, t);
            state.objects[key] = new ObjectState
            {
                name = t.name,
                active = t.gameObject.activeSelf,
                layer = t.gameObject.layer,
                tag = t.gameObject.tag,
                staticFlags = GameObjectUtility.GetStaticEditorFlags(t.gameObject),
                localPosition = t.localPosition,
                localRotation = t.localRotation,
                localScale = t.localScale,
                sceneHidden = SceneVisibilityManager.instance.IsHidden(t.gameObject),
                pickingDisabled = SceneVisibilityManager.instance.IsPickingDisabled(t.gameObject)
            };

            Renderer[] renderers = t.GetComponents<Renderer>();
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer r = renderers[i];
                state.renderers[key + "|" + r.GetType().FullName + "|" + i] = new RendererState
                {
                    materials = r.sharedMaterials,
                    lightmapIndex = r.lightmapIndex,
                    lightmapScaleOffset = r.lightmapScaleOffset,
                    realtimeLightmapIndex = r.realtimeLightmapIndex,
                    realtimeLightmapScaleOffset = r.realtimeLightmapScaleOffset,
                    enabled = r.enabled,
                    shadowCastingMode = r.shadowCastingMode,
                    receiveShadows = r.receiveShadows,
                    sortingLayerID = r.sortingLayerID,
                    sortingOrder = r.sortingOrder
                };
            }
        }
        return state;
    }

    private void RestoreInstanceState(GameObject root, InstanceState state)
    {
        foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
        {
            string key = GetIndexPath(root.transform, t);
            if (state.objects.TryGetValue(key, out ObjectState objectState))
            {
                if (preserveNames) t.name = objectState.name;
                if (preserveHierarchyTransforms)
                {
                    t.localPosition = objectState.localPosition;
                    t.localRotation = objectState.localRotation;
                    t.localScale = objectState.localScale;
                }
                if (preserveObjectSettings)
                {
                    t.gameObject.SetActive(objectState.active);
                    t.gameObject.layer = objectState.layer;
                    try { t.gameObject.tag = objectState.tag; } catch { }
                    GameObjectUtility.SetStaticEditorFlags(t.gameObject, objectState.staticFlags);
                }
                if (preserveSceneVisibility)
                {
                    if (objectState.sceneHidden)
                        SceneVisibilityManager.instance.Hide(t.gameObject, false);
                    else
                        SceneVisibilityManager.instance.Show(t.gameObject, false);

                    if (objectState.pickingDisabled)
                        SceneVisibilityManager.instance.DisablePicking(t.gameObject, false);
                    else
                        SceneVisibilityManager.instance.EnablePicking(t.gameObject, false);
                }
            }

            if (!preserveRendererSettings) continue;
            Renderer[] renderers = t.GetComponents<Renderer>();
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer r = renderers[i];
                if (!state.renderers.TryGetValue(key + "|" + r.GetType().FullName + "|" + i,
                    out RendererState rendererState)) continue;
                r.sharedMaterials = rendererState.materials;
                r.lightmapIndex = rendererState.lightmapIndex;
                r.lightmapScaleOffset = rendererState.lightmapScaleOffset;
                r.realtimeLightmapIndex = rendererState.realtimeLightmapIndex;
                r.realtimeLightmapScaleOffset = rendererState.realtimeLightmapScaleOffset;
                r.enabled = rendererState.enabled;
                r.shadowCastingMode = rendererState.shadowCastingMode;
                r.receiveShadows = rendererState.receiveShadows;
                r.sortingLayerID = rendererState.sortingLayerID;
                r.sortingOrder = rendererState.sortingOrder;
            }
        }
    }

    private static string GetIndexPath(Transform root, Transform current)
    {
        if (current == root) return ".";
        List<int> indices = new List<int>();
        Transform t = current;
        while (t != null && t != root)
        {
            indices.Add(t.GetSiblingIndex());
            t = t.parent;
        }
        indices.Reverse();
        return string.Join("/", indices.Select(i => i.ToString()));
    }

    private static bool HasAnyMesh(GameObject go)
    {
        return go.GetComponentsInChildren<MeshFilter>(true).Any(m => m.sharedMesh != null) ||
               go.GetComponentsInChildren<SkinnedMeshRenderer>(true).Any(m => m.sharedMesh != null);
    }

    private static bool IsRiggedArt(GameObject go)
    {
        return go.GetComponent<Animator>() != null || go.GetComponent<Animation>() != null ||
               go.GetComponentsInChildren<SkinnedMeshRenderer>(true).Any(renderer =>
                   renderer.sharedMesh != null);
    }

    private static bool HasDirectMesh(GameObject go)
    {
        MeshFilter mf = go.GetComponent<MeshFilter>();
        if (mf != null && mf.sharedMesh != null) return true;
        SkinnedMeshRenderer smr = go.GetComponent<SkinnedMeshRenderer>();
        return smr != null && smr.sharedMesh != null;
    }

    private static bool IsCleanMeshReferencePrefab(GameObject prefab)
    {
        if (prefab == null || prefab.transform.childCount != 0 || !HasDirectMesh(prefab)) return false;
        return prefab.GetComponents<Component>().All(component =>
            component is Transform || component is MeshFilter || component is MeshRenderer ||
            component is SkinnedMeshRenderer);
    }

    private static IEnumerable<Mesh> GetDirectMeshes(GameObject go)
    {
        MeshFilter mf = go.GetComponent<MeshFilter>();
        if (mf != null && mf.sharedMesh != null) yield return mf.sharedMesh;
        SkinnedMeshRenderer smr = go.GetComponent<SkinnedMeshRenderer>();
        if (smr != null && smr.sharedMesh != null) yield return smr.sharedMesh;
    }

    private static IEnumerable<Mesh> GetMeshes(GameObject go)
    {
        foreach (MeshFilter mf in go.GetComponentsInChildren<MeshFilter>(true))
            if (mf.sharedMesh != null) yield return mf.sharedMesh;
        foreach (SkinnedMeshRenderer smr in go.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            if (smr.sharedMesh != null) yield return smr.sharedMesh;
    }

    private static int CountMeshes(GameObject go) { return GetMeshes(go).Count(); }

    private string MakeUniqueSuggestedName(string rawName)
    {
        string baseName = SanitizeFileName(NormalizeName(rawName));
        string candidate = baseName;
        int suffix = 2;
        while (groups.Any(g => string.Equals(g.prefabName, candidate, StringComparison.OrdinalIgnoreCase)))
            candidate = baseName + "_" + suffix++;
        return candidate;
    }

    private static string NormalizeName(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "Prefab";
        string result = Regex.Replace(value.Trim(), @"\s*\(Clone\)$", "", RegexOptions.IgnoreCase);
        result = Regex.Replace(result, @"\s*\(\d+\)$", "");
        return result.Trim();
    }

    private static string SanitizeFileName(string value)
    {
        string result = string.IsNullOrWhiteSpace(value) ? "Prefab" : value.Trim();
        foreach (char c in Path.GetInvalidFileNameChars()) result = result.Replace(c, '_');
        return string.IsNullOrWhiteSpace(result) ? "Prefab" : result;
    }

    private static string GetSceneSortKey(GameObject go)
    {
        List<int> indices = new List<int>();
        Transform t = go.transform;
        while (t != null) { indices.Add(t.GetSiblingIndex()); t = t.parent; }
        indices.Reverse();
        return go.scene.handle + ":" + string.Join(".", indices.Select(i => i.ToString("D6")));
    }

    private static int GetTransformDepth(Transform transform)
    {
        int depth = 0;
        while (transform != null && transform.parent != null)
        {
            depth++;
            transform = transform.parent;
        }
        return depth;
    }

    private bool ValidateOutputFolder(out string path)
    {
        path = outputPrefabFolder == null ? "" : AssetDatabase.GetAssetPath(outputPrefabFolder);
        if (string.IsNullOrEmpty(path) || !AssetDatabase.IsValidFolder(path) ||
            !(path == "Assets" || path.StartsWith("Assets/", StringComparison.Ordinal)))
        {
            EditorUtility.DisplayDialog("Invalid Output Folder",
                "Choose a valid folder inside this Unity project's Assets folder.", "OK");
            return false;
        }
        return true;
    }

    private static void MarkLoadedScenesDirty()
    {
        for (int i = 0; i < SceneManager.sceneCount; i++)
        {
            Scene scene = SceneManager.GetSceneAt(i);
            if (scene.IsValid() && scene.isLoaded) EditorSceneManager.MarkSceneDirty(scene);
        }
    }

    internal static AutomaticPrefabResult RunAutomatic(string scenePath)
    {
        CityMultiFBXToPrefabConnectorV3 tool = CreateInstance<CityMultiFBXToPrefabConnectorV3>();
        try
        {
            tool.automaticMode = true;
            tool.scanMode = ScanMode.AutomaticRepeatedObjects;
            tool.scanAllLoadedScenes = true;
            tool.includeInactive = true;
            tool.includeNonRepeatedObjects = true;
            tool.scanDirectChildrenOnly = false;
            tool.matchDuplicatedMeshGeometry = true;
            tool.ignoreMaterialsWhenGrouping = true;
            tool.includeComponentLayout = true;
            tool.skipPrefabInstances = true;
            tool.excludeRiggedObjects = true;
            tool.reuseExistingPrefabs = true;
            tool.outputPrefabFolder = SmartRecoveryPaths.FolderAsset(
                SmartRecoveryPaths.Prefabs(scenePath));
            tool.Scan();
            AutomaticPrefabResult result = new AutomaticPrefabResult
            {
                groups = tool.groups.Count,
                occurrences = tool.groups.Sum(group => group.matches.Count)
            };
            if (tool.groups.Count > 0)
            {
                tool.SearchExistingPrefabs();
                tool.ApplyChanges();
            }
            result.prefabs.AddRange(tool.lastAutomaticPrefabs.Where(prefab => prefab != null).Distinct());
            return result;
        }
        finally { DestroyImmediate(tool); }
    }
}
