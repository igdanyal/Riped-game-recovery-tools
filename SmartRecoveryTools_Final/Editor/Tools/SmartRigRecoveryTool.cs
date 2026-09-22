#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

public sealed class SmartRigRecoveryTool : EditorWindow
{
    private bool includeInactive = true;
    private Vector2 scroll;
    private readonly List<GameObject> rigRoots = new List<GameObject>();
    private string status = "Scan the current scene for rigged art.";

    [MenuItem("Tools/Smart Recovery Tools/Advanced/Rig Recovery", false, 100)]
    public static void Open() => GetWindow<SmartRigRecoveryTool>("Rig Recovery");

    private Vector2 finalizedPageScroll;
    private void OnGUI()
    {
        minSize = new Vector2(560, 420);
        using (new SmartRecoveryUI.WindowScope(this, "Rig Recovery"))
        {
            finalizedPageScroll = EditorGUILayout.BeginScrollView(finalizedPageScroll);
            try { DrawToolGUI(); }
            finally { EditorGUILayout.EndScrollView(); }
        }
    }

    private void DrawToolGUI()
    {
        EditorGUILayout.Space(8);
        EditorGUILayout.LabelField("Smart Rig Recovery", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "Detects SkinnedMeshRenderers, bones, Animator/Animation and Avatar data. Rigged art is kept " +
            "out of the static prefab pipeline and saved into scene-specific Rig/Prefabs and Rig/FBX folders.",
            MessageType.Info);
        includeInactive = EditorGUILayout.ToggleLeft("Include inactive rigged objects", includeInactive);
        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Scan Current Scene", GUILayout.Height(30))) ScanCurrentScene();
            GUI.enabled = rigRoots.Count > 0;
            if (GUILayout.Button($"Recover {rigRoots.Count} Rigged Objects", GUILayout.Height(30)))
                RecoverCurrentScene(true);
            GUI.enabled = true;
        }
        EditorGUILayout.LabelField(status, EditorStyles.wordWrappedMiniLabel);
        scroll = EditorGUILayout.BeginScrollView(scroll, GUILayout.MinHeight(180));
        foreach (GameObject root in rigRoots.Where(root => root != null))
            EditorGUILayout.ObjectField(root, typeof(GameObject), true);
        EditorGUILayout.EndScrollView();
    }

    private void ScanCurrentScene()
    {
        rigRoots.Clear();
        rigRoots.AddRange(FindRigRoots(SceneManager.GetActiveScene(), includeInactive));
        status = rigRoots.Count == 0 ? "No rigged art found." : $"Rigged roots found: {rigRoots.Count}";
        Repaint();
    }

    private void RecoverCurrentScene(bool showDialog)
    {
        Scene scene = SceneManager.GetActiveScene();
        if (rigRoots.Count == 0) rigRoots.AddRange(FindRigRoots(scene, includeInactive));
        RigRecoveryResult result = RecoverScene(scene, rigRoots, showDialog, true);
        status = result.message;
        Repaint();
    }

    internal sealed class RigRecoveryResult
    {
        internal int detected;
        internal int created;
        internal int reused;
        internal int failed;
        internal readonly List<GameObject> prefabs = new List<GameObject>();
        internal string message;
    }

    internal static List<GameObject> FindRigRoots(Scene scene, bool includeInactive = true)
    {
        if (!scene.IsValid() || !scene.isLoaded) return new List<GameObject>();
        HashSet<GameObject> roots = new HashSet<GameObject>();
        foreach (GameObject sceneRoot in scene.GetRootGameObjects())
        {
            foreach (SkinnedMeshRenderer skinned in sceneRoot.GetComponentsInChildren<SkinnedMeshRenderer>(includeInactive))
            {
                if (skinned == null || skinned.sharedMesh == null) continue;
                Animator animator = skinned.GetComponentInParent<Animator>();
                Animation animation = skinned.GetComponentInParent<Animation>();
                GameObject rigRoot = animator != null ? animator.gameObject :
                    animation != null ? animation.gameObject : FindSafeRigRoot(skinned);
                roots.Add(rigRoot);
            }
            foreach (Animator animator in sceneRoot.GetComponentsInChildren<Animator>(includeInactive))
                if (animator != null && (animator.avatar != null ||
                    animator.GetComponentsInChildren<SkinnedMeshRenderer>(true).Any()))
                    roots.Add(animator.gameObject);
        }
        return roots.Where(root => root != null)
            .Where(root => !roots.Any(other => other != root && root.transform.IsChildOf(other.transform)))
            .OrderBy(root => GetHierarchyPath(root.transform), StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static GameObject FindSafeRigRoot(SkinnedMeshRenderer skinned)
    {
        Transform rootBone = skinned.rootBone;
        if (rootBone == null) return skinned.gameObject;
        Transform current = rootBone;
        while (current.parent != null && current.parent != skinned.transform.parent &&
               current.parent.GetComponent<Animator>() == null)
            current = current.parent;
        return current.parent != null && current.parent.GetComponent<Animator>() != null
            ? current.parent.gameObject : current.gameObject;
    }

    internal static RigRecoveryResult RecoverScene(Scene scene, IEnumerable<GameObject> roots,
        bool showDialog = false, bool exportFbx = true)
    {
        RigRecoveryResult result = new RigRecoveryResult();
        List<GameObject> targets = roots.Where(root => root != null).Distinct().ToList();
        result.detected = targets.Count;
        if (targets.Count == 0)
        {
            result.message = "No rigged art found.";
            return result;
        }
        string scenePath = scene.path;
        SmartRecoveryPaths.EnsureAll(scenePath);
        string prefabFolder = SmartRecoveryPaths.RigPrefabs(scenePath);
        string fbxFolder = SmartRecoveryPaths.RigFbx(scenePath);

        for (int index = 0; index < targets.Count; index++)
        {
            GameObject source = targets[index];
            if (EditorUtility.DisplayCancelableProgressBar("Smart Rig Recovery",
                $"{index + 1}/{targets.Count}: {source.name}", (float)index / targets.Count)) break;
            try
            {
                GameObject existing = SmartRecoveryPrefabRegistry.instance.Find(source);
                if (existing != null)
                {
                    ConnectExistingRigPrefab(source, existing);
                    result.reused++;
                    result.prefabs.Add(existing);
                    continue;
                }

                string safeName = Sanitize(source.name);
                string prefabPath = AssetDatabase.GenerateUniqueAssetPath(prefabFolder + "/" + safeName + ".prefab");
                bool prefabSaved;
                GameObject prefab = PrefabUtility.SaveAsPrefabAssetAndConnect(source, prefabPath,
                    InteractionMode.AutomatedAction, out prefabSaved);
                if (!prefabSaved || prefab == null) throw new Exception("Could not save rig prefab.");

                string exactIdentity = SmartRecoveryPrefabIdentity.Build(source, false);
                string geometryIdentity = SmartRecoveryPrefabIdentity.Build(source, true);
                if (exportFbx)
                {
                    string fbxPath = AssetDatabase.GenerateUniqueAssetPath(fbxFolder + "/" + safeName + ".fbx");
                    ExportFbx(source, ToAbsolutePath(fbxPath));
                    AssetDatabase.ImportAsset(fbxPath, ImportAssetOptions.ForceSynchronousImport);
                    GameObject model = AssetDatabase.LoadAssetAtPath<GameObject>(fbxPath);
                    if (model == null) throw new Exception("Unity could not import the rig FBX.");
                    RemapPrefabMeshes(prefabPath, model);
                    prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
                    SmartRecoveryData.Register("RigFBX", source, model, scenePath, geometryIdentity);
                }
                SmartRecoveryPrefabRegistry.instance.Register(exactIdentity, geometryIdentity, prefabPath);
                SmartRecoveryData.Register("RigPrefab", source, prefab, scenePath, geometryIdentity);
                result.prefabs.Add(prefab);
                result.created++;
            }
            catch (Exception exception)
            {
                result.failed++;
                Debug.LogError($"Rig Recovery failed for '{source.name}':\n{exception}");
            }
        }
        EditorUtility.ClearProgressBar();
        AssetDatabase.SaveAssets();
        result.message = $"Rigged objects: {result.detected} | Created: {result.created} | " +
                         $"Reused: {result.reused} | Failed: {result.failed}";
        if (showDialog) EditorUtility.DisplayDialog("Smart Rig Recovery", result.message, "OK");
        return result;
    }

    private static void ConnectExistingRigPrefab(GameObject sourceRoot, GameObject prefab)
    {
        if (string.Equals(PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(sourceRoot),
            AssetDatabase.GetAssetPath(prefab), StringComparison.OrdinalIgnoreCase)) return;

        Transform sourceTransform = sourceRoot.transform;
        Transform parent = sourceTransform.parent;
        int sibling = sourceTransform.GetSiblingIndex();
        GameObject instance = PrefabUtility.InstantiatePrefab(prefab, sourceRoot.scene) as GameObject;
        if (instance == null) throw new Exception("Could not instantiate the recovered rig prefab.");
        Undo.RegisterCreatedObjectUndo(instance, "Reuse Recovered Rig Prefab");
        instance.transform.SetParent(parent, false);
        instance.transform.SetSiblingIndex(sibling);

        Dictionary<string, Transform> destinations = instance.GetComponentsInChildren<Transform>(true)
            .ToDictionary(item => GetIndexPath(instance.transform, item), item => item);
        foreach (Transform source in sourceRoot.GetComponentsInChildren<Transform>(true))
        {
            string path = GetIndexPath(sourceRoot.transform, source);
            if (!destinations.TryGetValue(path, out Transform destination)) continue;
            destination.name = source.name;
            destination.localPosition = source.localPosition;
            destination.localRotation = source.localRotation;
            destination.localScale = source.localScale;
            destination.gameObject.SetActive(source.gameObject.activeSelf);
            destination.gameObject.layer = source.gameObject.layer;
            try { destination.gameObject.tag = source.gameObject.tag; } catch { }
            GameObjectUtility.SetStaticEditorFlags(destination.gameObject,
                GameObjectUtility.GetStaticEditorFlags(source.gameObject));

            Dictionary<Type, int> occurrences = new Dictionary<Type, int>();
            foreach (Component sourceComponent in source.GetComponents<Component>())
            {
                if (sourceComponent == null || sourceComponent is Transform || sourceComponent is MeshFilter) continue;
                Type type = sourceComponent.GetType();
                int occurrence = occurrences.TryGetValue(type, out int count) ? count : 0;
                occurrences[type] = occurrence + 1;
                Component[] destinationComponents = destination.GetComponents(type);
                if (occurrence >= destinationComponents.Length) continue;
                Component destinationComponent = destinationComponents[occurrence];
                Mesh protectedMesh = destinationComponent is SkinnedMeshRenderer skinned ? skinned.sharedMesh :
                    destinationComponent is MeshCollider collider ? collider.sharedMesh : null;
                EditorUtility.CopySerialized(sourceComponent, destinationComponent);
                if (destinationComponent is SkinnedMeshRenderer copiedSkinned && protectedMesh != null)
                    copiedSkinned.sharedMesh = protectedMesh;
                else if (destinationComponent is MeshCollider copiedCollider && protectedMesh != null)
                    copiedCollider.sharedMesh = protectedMesh;
            }
        }
        Undo.DestroyObjectImmediate(sourceRoot);
    }

    private static string GetIndexPath(Transform root, Transform current)
    {
        if (current == root) return ".";
        List<int> indices = new List<int>();
        while (current != null && current != root)
        {
            indices.Add(current.GetSiblingIndex());
            current = current.parent;
        }
        indices.Reverse();
        return string.Join("/", indices);
    }

    private static void RemapPrefabMeshes(string prefabPath, GameObject model)
    {
        GameObject root = PrefabUtility.LoadPrefabContents(prefabPath);
        try
        {
            List<Mesh> oldMeshes = CollectMeshes(root);
            List<Mesh> newMeshes = CollectMeshes(model);
            Dictionary<Mesh, Mesh> map = new Dictionary<Mesh, Mesh>();
            HashSet<Mesh> used = new HashSet<Mesh>();
            foreach (Mesh oldMesh in oldMeshes)
            {
                Mesh match = newMeshes.Where(mesh => !used.Contains(mesh) && Compatible(oldMesh, mesh))
                    .OrderByDescending(mesh => Normalize(mesh.name) == Normalize(oldMesh.name))
                    .FirstOrDefault();
                if (match == null) continue;
                map[oldMesh] = match;
                used.Add(match);
            }
            if (map.Count != oldMeshes.Count)
                throw new Exception($"Rig mesh alignment matched {map.Count} of {oldMeshes.Count} meshes.");
            foreach (Component component in root.GetComponentsInChildren<Component>(true))
            {
                if (component == null || component is Transform) continue;
                SerializedObject serialized = new SerializedObject(component);
                SerializedProperty property = serialized.GetIterator();
                bool changed = false;
                while (property.Next(true))
                {
                    if (property.propertyType != SerializedPropertyType.ObjectReference) continue;
                    Mesh oldMesh = property.objectReferenceValue as Mesh;
                    if (oldMesh == null || !map.TryGetValue(oldMesh, out Mesh replacement)) continue;
                    property.objectReferenceValue = replacement;
                    changed = true;
                }
                if (changed) serialized.ApplyModifiedPropertiesWithoutUndo();
            }
            PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
        }
        finally { PrefabUtility.UnloadPrefabContents(root); }
    }

    private static List<Mesh> CollectMeshes(GameObject root) => root.GetComponentsInChildren<MeshFilter>(true)
        .Where(filter => filter.sharedMesh != null).Select(filter => filter.sharedMesh)
        .Concat(root.GetComponentsInChildren<SkinnedMeshRenderer>(true)
            .Where(renderer => renderer.sharedMesh != null).Select(renderer => renderer.sharedMesh))
        .Concat(root.GetComponentsInChildren<MeshCollider>(true)
            .Where(collider => collider.sharedMesh != null).Select(collider => collider.sharedMesh))
        .Distinct().ToList();

    private static bool Compatible(Mesh left, Mesh right)
    {
        if (left == null || right == null || left.vertexCount != right.vertexCount ||
            left.subMeshCount != right.subMeshCount) return false;
        for (int i = 0; i < left.subMeshCount; i++)
            if (left.GetIndexCount(i) != right.GetIndexCount(i)) return false;
        return true;
    }

    private static void ExportFbx(GameObject source, string absolutePath)
    {
        Type type = Type.GetType("UnityEditor.Formats.Fbx.Exporter.ModelExporter, Unity.Formats.Fbx.Editor") ??
            AppDomain.CurrentDomain.GetAssemblies().Select(assembly =>
                assembly.GetType("UnityEditor.Formats.Fbx.Exporter.ModelExporter")).FirstOrDefault(value => value != null);
        if (type == null) throw new Exception("Unity FBX Exporter package is not installed.");
        MethodInfo method = type.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(candidate => candidate.Name == "ExportObject" &&
                candidate.GetParameters().Length == 2 && candidate.GetParameters()[0].ParameterType == typeof(string));
        if (method == null) throw new Exception("No compatible FBX ExportObject API was found.");
        Directory.CreateDirectory(Path.GetDirectoryName(absolutePath));
        method.Invoke(null, new object[] { absolutePath, source });
    }

    private static string ToAbsolutePath(string assetPath) =>
        Path.GetFullPath(Path.Combine(Directory.GetParent(Application.dataPath).FullName, assetPath));
    private static string Normalize(string value) => (value ?? string.Empty)
        .Replace(" Instance", string.Empty).Replace("_Mesh", string.Empty).Trim().ToLowerInvariant();
    private static string Sanitize(string value)
    {
        string result = string.IsNullOrWhiteSpace(value) ? "Rig" : value.Trim();
        foreach (char character in Path.GetInvalidFileNameChars()) result = result.Replace(character, '_');
        return result;
    }
    private static string GetHierarchyPath(Transform transform)
    {
        List<string> names = new List<string>();
        while (transform != null) { names.Add(transform.name); transform = transform.parent; }
        names.Reverse();
        return string.Join("/", names);
    }
}
#endif
