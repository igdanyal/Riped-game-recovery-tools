#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>Preview-first cleanup that preserves the scene's 3D and visual art hierarchy.</summary>
public sealed class SceneCleanupTool : EditorWindow
{
    internal sealed class AutomaticCleanupResult
    {
        internal int removedObjects;
        internal int removedMissingScripts;
    }

    private sealed class Candidate
    {
        public GameObject gameObject;
        public string reason;
        public int missingScripts;
    }

    private bool scanAllLoadedScenes = true;
    private GameObject optionalRoot;
    private bool includeInactive = true;
    private bool removeUi = true;
    private bool removeDevelopmentObjects = true;
    private bool removeMissingScripts = true;
    private bool removeEmptyArtSafeParents;
    private bool scanned;
    private bool automaticMode;
    private Vector2 windowScroll;
    private Vector2 resultsScroll;
    private readonly List<Candidate> uiRoots = new List<Candidate>();
    private readonly List<Candidate> developmentRoots = new List<Candidate>();
    private readonly List<Candidate> missingScriptObjects = new List<Candidate>();
    private readonly List<Candidate> emptyObjects = new List<Candidate>();
    private readonly HashSet<GameObject> protectedArtHierarchy = new HashSet<GameObject>();

    [MenuItem("Tools/Smart Recovery Tools/1. Scene Art Cleanup", false, 20)]
    public static void Open()
    {
        GetWindow<SceneCleanupTool>("Smart Scene Cleanup");
    }

    private Vector2 finalizedPageScroll;
    private void OnGUI()
    {
        minSize = new Vector2(560, 420);
        using (new SmartRecoveryUI.WindowScope(this, "Scene Art Cleanup"))
        {
            finalizedPageScroll = EditorGUILayout.BeginScrollView(finalizedPageScroll);
            try { DrawToolGUI(); }
            finally { EditorGUILayout.EndScrollView(); }
        }
    }

    private int page;
    private readonly SmartPrefabMaintenance prefabMaintenance = new SmartPrefabMaintenance(true);
    private void DrawToolGUI()
    {
        page = GUILayout.Toolbar(page, new[] { "Scene Art", "Prefab Missing Scripts" });
        if (page == 1) { prefabMaintenance.Draw(); return; }
        windowScroll = EditorGUILayout.BeginScrollView(windowScroll);
        EditorGUILayout.Space(8);
        EditorGUILayout.LabelField("Smart Recovery - Scene Art Cleanup", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "Purpose: isolate and preserve 3D art. Meshes, renderers, terrain, lights, particles, " +
            "trails, lines, VFX, reflection probes and all parents required by them are protected. " +
            "Nothing changes until Apply is confirmed.", MessageType.Info);

        scanAllLoadedScenes = EditorGUILayout.ToggleLeft("Scan all loaded scenes", scanAllLoadedScenes);
        GUI.enabled = !scanAllLoadedScenes;
        optionalRoot = (GameObject)EditorGUILayout.ObjectField(
            "Optional Scene Root", optionalRoot, typeof(GameObject), true);
        GUI.enabled = true;
        includeInactive = EditorGUILayout.ToggleLeft("Include inactive objects", includeInactive);

        EditorGUILayout.Space(5);
        EditorGUILayout.LabelField("Cleanup Categories", EditorStyles.boldLabel);
        removeUi = EditorGUILayout.ToggleLeft("Remove UI and EventSystem hierarchies", removeUi);
        removeDevelopmentObjects = EditorGUILayout.ToggleLeft(
            "Remove script-related development objects with no protected visuals", removeDevelopmentObjects);
        removeMissingScripts = EditorGUILayout.ToggleLeft(
            "Remove missing script slots from preserved scene objects", removeMissingScripts);
        removeEmptyArtSafeParents = EditorGUILayout.ToggleLeft(
            "Remove truly empty transform-only objects outside the art hierarchy", removeEmptyArtSafeParents);

        EditorGUILayout.HelpBox(
            "Safety rule: an object is never a development candidate when it or any descendant contains " +
            "protected visual content. Valid scripts on protected visuals stay intact. Missing script slots " +
            "can be removed without deleting their GameObjects.", MessageType.None);

        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Scan And Preview", GUILayout.Height(34))) Scan();
            if (GUILayout.Button("Clear", GUILayout.Height(34))) Clear();
        }
        DrawResults();

        int selected = (removeUi ? uiRoots.Count : 0) +
                       (removeDevelopmentObjects ? developmentRoots.Count : 0) +
                       (removeMissingScripts ? missingScriptObjects.Sum(c => c.missingScripts) : 0) +
                       (removeEmptyArtSafeParents ? emptyObjects.Count : 0);
        GUI.enabled = scanned && selected > 0;
        if (GUILayout.Button($"Apply Selected Cleanup ({selected})", GUILayout.Height(44))) ApplyCleanup();
        GUI.enabled = true;
        EditorGUILayout.Space(8);
        EditorGUILayout.EndScrollView();
    }

    private void DrawResults()
    {
        if (!scanned) return;
        EditorGUILayout.Space(7);
        EditorGUILayout.HelpBox(
            $"Protected art hierarchy: {protectedArtHierarchy.Count} objects\n" +
            $"UI roots: {uiRoots.Count} | Development roots: {developmentRoots.Count} | " +
            $"Missing scripts: {missingScriptObjects.Sum(c => c.missingScripts)} | Empty: {emptyObjects.Count}",
            MessageType.Info);
        resultsScroll = EditorGUILayout.BeginScrollView(resultsScroll, GUILayout.MinHeight(220));
        DrawSection("UI / EventSystem Roots", uiRoots);
        DrawSection("Development-Only Roots", developmentRoots);
        DrawSection("Objects With Missing Scripts", missingScriptObjects);
        DrawSection("Empty Objects Outside Art Hierarchy", emptyObjects);
        EditorGUILayout.EndScrollView();
    }

    private static void DrawSection(string title, IEnumerable<Candidate> candidates)
    {
        List<Candidate> valid = candidates.Where(c => c.gameObject != null).ToList();
        if (valid.Count == 0) return;
        EditorGUILayout.LabelField(title, EditorStyles.miniBoldLabel);
        foreach (Candidate candidate in valid)
        {
            using (new EditorGUILayout.HorizontalScope("box"))
            {
                EditorGUILayout.ObjectField(candidate.gameObject, typeof(GameObject), true);
                EditorGUILayout.LabelField(candidate.reason, EditorStyles.miniLabel, GUILayout.Width(250));
            }
        }
    }

    private void Scan()
    {
        Clear();
        if (!scanAllLoadedScenes && optionalRoot == null)
        {
            EditorUtility.DisplayDialog("Smart Scene Cleanup",
                "Assign a scene root or enable Scan all loaded scenes.", "OK");
            return;
        }

        try
        {
            List<GameObject> objects = CollectObjects();
            HashSet<GameObject> scope = new HashSet<GameObject>(objects);
            BuildProtectedArtHierarchy(objects);

            // If a UI ancestor contains protected particles, meshes, lights or VFX, do not delete
            // that ancestor. Non-visual UI descendants can still be offered independently.
            List<GameObject> uiObjects = objects
                .Where(IsUiObject)
                .Where(go => !protectedArtHierarchy.Contains(go))
                .ToList();
            HashSet<GameObject> uiSet = new HashSet<GameObject>(uiObjects);
            foreach (GameObject root in FindRoots(uiObjects, uiSet, scope))
                uiRoots.Add(new Candidate { gameObject = root, reason = "UI or EventSystem hierarchy" });
            HashSet<GameObject> uiHierarchy = DescendantSet(uiRoots.Select(c => c.gameObject));

            foreach (GameObject gameObject in objects)
            {
                int missing = GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(gameObject);
                if (missing > 0 && !uiHierarchy.Contains(gameObject))
                    missingScriptObjects.Add(new Candidate
                    {
                        gameObject = gameObject,
                        missingScripts = missing,
                        reason = $"{missing} missing component slot(s)"
                    });
            }

            List<GameObject> development = objects
                .Where(go => !uiHierarchy.Contains(go) && !protectedArtHierarchy.Contains(go))
                .Where(HasScriptOrDevelopmentComponent).ToList();
            HashSet<GameObject> developmentSet = new HashSet<GameObject>(development);
            foreach (GameObject root in FindRoots(development, developmentSet, scope))
                developmentRoots.Add(new Candidate
                {
                    gameObject = root,
                    reason = "Script/development hierarchy; no protected visuals"
                });

            HashSet<GameObject> developmentHierarchy = DescendantSet(developmentRoots.Select(c => c.gameObject));
            emptyObjects.AddRange(objects
                .Where(go => go != optionalRoot && !uiHierarchy.Contains(go) && !developmentHierarchy.Contains(go))
                .Where(go => !protectedArtHierarchy.Contains(go) && IsTransformOnly(go))
                .OrderByDescending(go => GetDepth(go.transform))
                .Select(go => new Candidate { gameObject = go, reason = "Transform only; outside art hierarchy" }));
            scanned = true;
            Repaint();
        }
        catch (Exception ex)
        {
            Debug.LogError("Smart Scene Cleanup scan failed:\n" + ex);
            EditorUtility.DisplayDialog("Scan Failed", "See the Unity Console for details.", "OK");
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }
    }

    private List<GameObject> CollectObjects()
    {
        if (!scanAllLoadedScenes)
            return optionalRoot.GetComponentsInChildren<Transform>(includeInactive)
                .Select(t => t.gameObject).Where(IsEligibleSceneObject).Distinct().ToList();
        List<GameObject> result = new List<GameObject>();
        for (int i = 0; i < SceneManager.sceneCount; i++)
        {
            Scene scene = SceneManager.GetSceneAt(i);
            if (!scene.IsValid() || !scene.isLoaded) continue;
            foreach (GameObject root in scene.GetRootGameObjects())
                result.AddRange(root.GetComponentsInChildren<Transform>(includeInactive).Select(t => t.gameObject));
        }
        return result.Where(IsEligibleSceneObject).Distinct().ToList();
    }

    private bool IsEligibleSceneObject(GameObject gameObject)
    {
        return gameObject != null && gameObject.scene.IsValid() &&
               (includeInactive || gameObject.activeInHierarchy);
    }

    private void BuildProtectedArtHierarchy(IEnumerable<GameObject> objects)
    {
        foreach (GameObject visual in objects.Where(HasProtectedVisualComponent))
        {
            Transform current = visual.transform;
            while (current != null)
            {
                protectedArtHierarchy.Add(current.gameObject);
                current = current.parent;
            }
        }
    }

    private static bool HasProtectedVisualComponent(GameObject gameObject)
    {
        if (gameObject.GetComponent<MeshFilter>() != null || gameObject.GetComponent<Renderer>() != null ||
            gameObject.GetComponent<Terrain>() != null || gameObject.GetComponent<TerrainCollider>() != null ||
            gameObject.GetComponent<Light>() != null || gameObject.GetComponent<ParticleSystem>() != null ||
            gameObject.GetComponent<LODGroup>() != null || gameObject.GetComponent<ReflectionProbe>() != null)
            return true;
        return gameObject.GetComponents<Component>().Any(component =>
        {
            if (component == null) return false;
            string name = component.GetType().FullName ?? string.Empty;
            return name.StartsWith("UnityEngine.VFX.", StringComparison.Ordinal) ||
                   name.IndexOf("VisualEffect", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   name.IndexOf("DecalProjector", StringComparison.OrdinalIgnoreCase) >= 0;
        });
    }

    private static bool HasScriptOrDevelopmentComponent(GameObject gameObject)
    {
        foreach (Component component in gameObject.GetComponents<Component>())
        {
            if (component == null) return true;
            if (component is Transform) continue;
            if (component is MonoBehaviour || component is Animator || component is Animation ||
                component is Rigidbody || component is Rigidbody2D || component is Collider ||
                component is Collider2D || component is Camera || component is AudioSource ||
                component is AudioListener) return true;
        }
        return false;
    }

    private static bool IsUiObject(GameObject gameObject)
    {
        if (gameObject.GetComponent<RectTransform>() != null || gameObject.GetComponent<Canvas>() != null)
            return true;
        return gameObject.GetComponents<Component>().Any(component =>
        {
            if (component == null) return false;
            string name = component.GetType().FullName ?? string.Empty;
            return name.StartsWith("UnityEngine.UI.", StringComparison.Ordinal) ||
                   name.StartsWith("TMPro.", StringComparison.Ordinal) ||
                   name.StartsWith("UnityEngine.EventSystems.", StringComparison.Ordinal);
        });
    }

    private static IEnumerable<GameObject> FindRoots(IEnumerable<GameObject> candidates,
        HashSet<GameObject> candidateSet, HashSet<GameObject> scope)
    {
        foreach (GameObject gameObject in candidates)
        {
            Transform parent = gameObject.transform.parent;
            if (parent == null || !scope.Contains(parent.gameObject) || !candidateSet.Contains(parent.gameObject))
                yield return gameObject;
        }
    }

    private static HashSet<GameObject> DescendantSet(IEnumerable<GameObject> roots)
    {
        HashSet<GameObject> result = new HashSet<GameObject>();
        foreach (GameObject root in roots.Where(root => root != null))
            foreach (Transform transform in root.GetComponentsInChildren<Transform>(true))
                result.Add(transform.gameObject);
        return result;
    }

    private void ApplyCleanup()
    {
        int objects = (removeUi ? uiRoots.Count : 0) +
                      (removeDevelopmentObjects ? developmentRoots.Count : 0) +
                      (removeEmptyArtSafeParents ? emptyObjects.Count : 0);
        int scripts = removeMissingScripts ? missingScriptObjects.Sum(c => c.missingScripts) : 0;
        if (!automaticMode && !EditorUtility.DisplayDialog("Apply Smart Scene Cleanup",
            $"Delete candidate roots/objects: {objects}\nRemove missing scripts: {scripts}\n\n" +
            "This supports Unity Undo. Save only after visual inspection.", "Apply Cleanup", "Cancel")) return;

        Undo.IncrementCurrentGroup();
        int undoGroup = Undo.GetCurrentGroup();
        Undo.SetCurrentGroupName("Smart Recovery Scene Cleanup");
        int removedObjects = 0;
        int removedScripts = 0;
        try
        {
            HashSet<GameObject> roots = new HashSet<GameObject>();
            if (removeUi) roots.UnionWith(uiRoots.Select(c => c.gameObject).Where(go => go != null));
            if (removeDevelopmentObjects)
                roots.UnionWith(developmentRoots.Select(c => c.gameObject).Where(go => go != null));
            foreach (GameObject root in roots.OrderByDescending(go => GetDepth(go.transform)).ToList())
            {
                if (root == null) continue;
                Undo.DestroyObjectImmediate(root);
                removedObjects++;
            }

            if (removeMissingScripts)
                foreach (Candidate candidate in missingScriptObjects.Where(c => c.gameObject != null))
                {
                    Undo.RegisterCompleteObjectUndo(candidate.gameObject, "Remove Missing Script Slots");
                    removedScripts += GameObjectUtility.RemoveMonoBehavioursWithMissingScript(candidate.gameObject);
                }

            if (removeEmptyArtSafeParents)
                foreach (GameObject gameObject in emptyObjects.Select(c => c.gameObject)
                             .Where(go => go != null).OrderByDescending(go => GetDepth(go.transform)).ToList())
                    if (gameObject != null && gameObject.transform.childCount == 0 && IsTransformOnly(gameObject))
                    {
                        Undo.DestroyObjectImmediate(gameObject);
                        removedObjects++;
                    }
        }
        finally
        {
            Undo.CollapseUndoOperations(undoGroup);
        }
        MarkLoadedScenesDirty();
        Clear();
        if (!automaticMode)
            EditorUtility.DisplayDialog("Smart Scene Cleanup Complete",
                $"GameObjects removed: {removedObjects}\nMissing script slots removed: {removedScripts}\n\n" +
                "Inspect the scene visually before saving it.", "OK");
    }

    internal static AutomaticCleanupResult RunAutomatic()
    {
        SceneCleanupTool tool = CreateInstance<SceneCleanupTool>();
        try
        {
            tool.automaticMode = true;
            tool.scanAllLoadedScenes = true;
            tool.includeInactive = true;
            tool.removeUi = true;
            tool.removeDevelopmentObjects = true;
            tool.removeMissingScripts = true;
            tool.removeEmptyArtSafeParents = false;
            tool.Scan();
            AutomaticCleanupResult result = new AutomaticCleanupResult
            {
                removedObjects = tool.uiRoots.Count + tool.developmentRoots.Count,
                removedMissingScripts = tool.missingScriptObjects.Sum(candidate => candidate.missingScripts)
            };
            tool.ApplyCleanup();
            return result;
        }
        finally { DestroyImmediate(tool); }
    }

    private void Clear()
    {
        uiRoots.Clear();
        developmentRoots.Clear();
        missingScriptObjects.Clear();
        emptyObjects.Clear();
        protectedArtHierarchy.Clear();
        scanned = false;
        Repaint();
    }

    private static bool IsTransformOnly(GameObject gameObject)
    {
        Component[] components = gameObject.GetComponents<Component>();
        return components.Length == 1 && components[0] is Transform;
    }

    private static int GetDepth(Transform transform)
    {
        int depth = 0;
        while (transform != null && transform.parent != null)
        {
            depth++;
            transform = transform.parent;
        }
        return depth;
    }

    private static void MarkLoadedScenesDirty()
    {
        for (int i = 0; i < SceneManager.sceneCount; i++)
        {
            Scene scene = SceneManager.GetSceneAt(i);
            if (scene.IsValid() && scene.isLoaded) EditorSceneManager.MarkSceneDirty(scene);
        }
    }
}
#endif
