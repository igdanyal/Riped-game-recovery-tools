#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// Standalone geometry export. Does not replace references or modify source assets.
public sealed class AssetMeshToFbxConverter : EditorWindow
{
    private readonly List<Mesh> meshes = new List<Mesh>();
    private DefaultAsset outputFolder;
    private Vector2 scroll;
    private string status = "Select mesh .asset files or folders in the Project window, then add them.";
    private Mesh[] pending;
    private string outputPath;
    private MethodInfo exportMethod;
    private int next, converted, failed, skipped;

    [MenuItem("Tools/Smart Recovery Tools/7. Asset Mesh to FBX (Optional)", false, 26)]
    private static void Open() => GetWindow<AssetMeshToFbxConverter>("Asset Mesh to FBX");

    private static bool IsAssetMesh(Mesh mesh) => mesh != null &&
        string.Equals(Path.GetExtension(AssetDatabase.GetAssetPath(mesh)), ".asset", StringComparison.OrdinalIgnoreCase);

    private Vector2 finalizedPageScroll;
    private void OnGUI()
    {
        minSize = new Vector2(560, 420);
        using (new SmartRecoveryUI.WindowScope(this, "Asset Mesh to FBX Converter"))
        {
            finalizedPageScroll = EditorGUILayout.BeginScrollView(finalizedPageScroll);
            try { DrawToolGUI(); }
            finally { EditorGUILayout.EndScrollView(); }
        }
    }

    private void DrawToolGUI()
    {
        EditorGUILayout.HelpBox("Exports only meshes stored in .asset files. FBX/OBJ sources are ignored. " +
            "Source files, scenes and prefab references remain unchanged. Materials and a rig cannot be recovered from a mesh asset alone.", MessageType.Info);
        using (new EditorGUI.DisabledScope(pending != null))
        {
            if (GUILayout.Button("Add Selected Mesh Assets / Folders")) AddSelection();
            Rect drop = GUILayoutUtility.GetRect(0, 42, GUILayout.ExpandWidth(true));
            GUI.Box(drop, "Drop mesh .asset files or folders here");
            Event evt = Event.current;
            if (drop.Contains(evt.mousePosition) && (evt.type == EventType.DragUpdated || evt.type == EventType.DragPerform))
            {
                DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
                if (evt.type == EventType.DragPerform)
                {
                    DragAndDrop.AcceptDrag();
                    AddObjects(DragAndDrop.objectReferences);
                }
                evt.Use();
            }
            EditorGUILayout.LabelField("Queued meshes", meshes.Count.ToString());
            scroll = EditorGUILayout.BeginScrollView(scroll, GUILayout.MaxHeight(250));
            for (int i = 0; i < meshes.Count; i++)
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.ObjectField(meshes[i], typeof(Mesh), false);
                if (GUILayout.Button("Remove", GUILayout.Width(65))) { meshes.RemoveAt(i--); }
                EditorGUILayout.EndHorizontal();
            }
            EditorGUILayout.EndScrollView();
            if (GUILayout.Button("Clear List")) meshes.Clear();
            outputFolder = (DefaultAsset)EditorGUILayout.ObjectField("FBX Output Folder", outputFolder, typeof(DefaultAsset), false);
            if (GUILayout.Button("Convert .asset Meshes to FBX")) Begin();
        }
        if (pending != null && GUILayout.Button("Cancel After Current Mesh")) Finish(true);
        EditorGUILayout.HelpBox(status, MessageType.None);
    }

    private void AddSelection() => AddObjects(Selection.objects);

    private void AddObjects(IEnumerable<UnityEngine.Object> objects)
    {
        HashSet<string> paths = new HashSet<string>();
        foreach (UnityEngine.Object obj in objects)
        {
            string path = AssetDatabase.GetAssetPath(obj);
            if (AssetDatabase.IsValidFolder(path))
            {
                foreach (string guid in AssetDatabase.FindAssets("t:Mesh", new[] { path }))
                    paths.Add(AssetDatabase.GUIDToAssetPath(guid));
            }
            else if (obj is Mesh mesh)
            {
                if (IsAssetMesh(mesh) && !meshes.Contains(mesh)) meshes.Add(mesh);
            }
            else paths.Add(path);
        }
        foreach (string path in paths.Where(p => string.Equals(Path.GetExtension(p), ".asset", StringComparison.OrdinalIgnoreCase)))
            foreach (Mesh mesh in AssetDatabase.LoadAllAssetsAtPath(path).OfType<Mesh>())
                if (IsAssetMesh(mesh) && !meshes.Contains(mesh)) meshes.Add(mesh);
        status = meshes.Count + " unique mesh assets queued. FBX/OBJ sources ignored.";
    }

    internal static string RunAutomatic(IEnumerable<UnityEngine.Object> selected, string folder)
    {
        var tool = CreateInstance<AssetMeshToFbxConverter>();
        try
        {
            tool.outputFolder = SmartRecoveryPaths.FolderAsset(folder);
            tool.AddObjects(selected);
            if (tool.meshes.Count == 0) return "Skipped — no selected .asset meshes.";
            tool.Begin();
            if (tool.pending == null) throw new InvalidOperationException("Mesh export could not start.");
            EditorApplication.update -= tool.Tick;
            while (tool.pending != null) tool.Tick();
            if (tool.failed > 0) throw new InvalidOperationException(tool.status);
            return tool.status;
        }
        finally { EditorApplication.update -= tool.Tick; DestroyImmediate(tool); }
    }
    private void Begin()
    {
        outputPath = AssetDatabase.GetAssetPath(outputFolder);
        if (!outputPath.StartsWith("Assets/", StringComparison.Ordinal) || !AssetDatabase.IsValidFolder(outputPath))
        {
            EditorUtility.DisplayDialog("Output Folder", "Select an output folder under Assets.", "OK");
            return;
        }
        exportMethod = AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.GetType("UnityEditor.Formats.Fbx.Exporter.ModelExporter", false))
            .Where(t => t != null)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static))
            .FirstOrDefault(m => m.Name == "ExportObject" && m.GetParameters().Length == 2 &&
                m.GetParameters()[0].ParameterType == typeof(string) &&
                m.GetParameters()[1].ParameterType == typeof(UnityEngine.Object));
        if (exportMethod == null)
        {
            EditorUtility.DisplayDialog("FBX Exporter Required", "Install Unity's FBX Exporter package (com.unity.formats.fbx).", "OK");
            return;
        }
        pending = meshes.Where(IsAssetMesh).Distinct().ToArray();
        next = converted = failed = skipped = 0;
        EditorApplication.update += Tick;
    }

    private void Tick()
    {
        if (pending == null) return;
        if (next >= pending.Length) { Finish(false); return; }
        Mesh mesh = pending[next++];
        try
        {
            if (!IsAssetMesh(mesh)) { skipped++; return; }
            // An isolated mesh cannot provide the bone hierarchy required for a valid rig export.
            if (mesh.bindposes.Length > 0)
            {
                skipped++;
                Debug.LogWarning("Skipped rigged mesh '" + mesh.name + "': export its rigged prefab with the rig tool instead.", mesh);
                return;
            }
            Export(mesh);
            converted++;
        }
        catch (Exception ex)
        {
            failed++;
            Debug.LogError("Asset Mesh to FBX failed for '" + (mesh != null ? mesh.name : "missing mesh") + "': " + ex);
        }
        finally
        {
            status = $"Processed {next}/{pending.Length}: {converted} exported, {skipped} skipped, {failed} failed.";
            Repaint();
        }
    }

    private void Export(Mesh mesh)
    {
        string name = string.Concat(mesh.name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        if (string.IsNullOrWhiteSpace(name)) name = "Mesh";
        string path = AssetDatabase.GenerateUniqueAssetPath(outputPath + "/" + name + ".fbx");
        var preview = EditorSceneManager.NewPreviewScene();
        GameObject root = null;
        var materials = new List<Material>();
        try
        {
            root = new GameObject(name);
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(root, preview);
            root.AddComponent<MeshFilter>().sharedMesh = mesh;
            Shader shader = Shader.Find("Standard") ?? Shader.Find("Hidden/InternalErrorShader");
            if (shader == null) throw new InvalidOperationException("No placeholder shader available.");
            for (int i = 0; i < Math.Max(1, mesh.subMeshCount); i++)
                materials.Add(new Material(shader) { name = "Submesh_" + i, hideFlags = HideFlags.HideAndDontSave });
            root.AddComponent<MeshRenderer>().sharedMaterials = materials.ToArray();
            string absolute = Path.GetFullPath(Path.Combine(Application.dataPath, "..", path));
            exportMethod.Invoke(null, new object[] { absolute, root });
            if (!File.Exists(absolute) || new FileInfo(absolute).Length == 0)
                throw new IOException("Exporter did not produce an FBX file.");
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            if (!AssetDatabase.LoadAllAssetsAtPath(path).OfType<Mesh>().Any())
                throw new IOException("Exported FBX contains no imported mesh: " + path);
            Debug.Log("Created FBX: " + path, AssetDatabase.LoadMainAssetAtPath(path));
        }
        finally
        {
            if (root != null) DestroyImmediate(root);
            foreach (Material material in materials) if (material != null) DestroyImmediate(material);
            EditorSceneManager.ClosePreviewScene(preview);
        }
    }

    private void Finish(bool cancelled)
    {
        EditorApplication.update -= Tick;
        pending = null;
        status = $"{(cancelled ? "Cancelled" : "Finished")}: {converted} exported, {skipped} skipped, {failed} failed. Existing files were not overwritten.";
        Repaint();
    }

    private void OnDisable() { EditorApplication.update -= Tick; pending = null; }
}
#endif
