#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

[Serializable]
internal sealed class SmartRecoveryPrefabRecord
{
    public string exactIdentity;
    public string geometryIdentity;
    public string prefabAssetPath;
    public string prefabGuid;
    public string updatedUtc;
}

[FilePath("ProjectSettings/SmartRecoveryPrefabRegistry.asset", FilePathAttribute.Location.ProjectFolder)]
internal sealed class SmartRecoveryPrefabRegistry : ScriptableSingleton<SmartRecoveryPrefabRegistry>
{
    [SerializeField] private List<SmartRecoveryPrefabRecord> records = new List<SmartRecoveryPrefabRecord>();

    internal void Register(GameObject sourceBeforeConversion, string prefabAssetPath)
    {
        if (sourceBeforeConversion == null || string.IsNullOrEmpty(prefabAssetPath)) return;
        Register(SmartRecoveryPrefabIdentity.Build(sourceBeforeConversion, false),
            SmartRecoveryPrefabIdentity.Build(sourceBeforeConversion, true), prefabAssetPath);
    }

    internal void Register(string exact, string geometry, string prefabAssetPath)
    {
        if (string.IsNullOrEmpty(exact) || string.IsNullOrEmpty(prefabAssetPath)) return;
        string guid = AssetDatabase.AssetPathToGUID(prefabAssetPath);
        records.RemoveAll(record => record == null ||
            (!string.IsNullOrEmpty(guid) && record.prefabGuid == guid) ||
            record.exactIdentity == exact);
        records.Add(new SmartRecoveryPrefabRecord
        {
            exactIdentity = exact,
            geometryIdentity = geometry,
            prefabAssetPath = prefabAssetPath,
            prefabGuid = guid,
            updatedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)
        });
        Cleanup();
        Save(true);
    }

    internal GameObject Find(GameObject sceneTemplate)
    {
        if (sceneTemplate == null) return null;
        string exact = SmartRecoveryPrefabIdentity.Build(sceneTemplate, false);
        SmartRecoveryPrefabRecord match = records.LastOrDefault(record => record.exactIdentity == exact);
        if (match == null)
        {
            string geometry = SmartRecoveryPrefabIdentity.Build(sceneTemplate, true);
            match = records.LastOrDefault(record => record.geometryIdentity == geometry);
        }
        return match == null ? null : AssetDatabase.LoadAssetAtPath<GameObject>(match.prefabAssetPath);
    }

    internal void PrepareForSearch()
    {
        Cleanup();
        Save(true);
    }

    private void Cleanup()
    {
        records.RemoveAll(record => record == null || string.IsNullOrEmpty(record.prefabAssetPath) ||
            AssetDatabase.LoadAssetAtPath<GameObject>(record.prefabAssetPath) == null);
    }
}

internal static class SmartRecoveryPrefabIdentity
{
    internal static string Build(GameObject root, bool geometryOnly)
    {
        if (root == null) return string.Empty;
        StringBuilder value = new StringBuilder(2048);
        Transform[] hierarchy = root.GetComponentsInChildren<Transform>(true);
        foreach (Transform current in hierarchy)
        {
            value.Append(GetIndexPath(root.transform, current)).Append('|')
                .Append(NormalizeName(current.name)).Append('|')
                .Append(Quantize(current.localPosition.x)).Append(',')
                .Append(Quantize(current.localPosition.y)).Append(',')
                .Append(Quantize(current.localPosition.z)).Append('|')
                .Append(Quantize(current.localRotation.x)).Append(',')
                .Append(Quantize(current.localRotation.y)).Append(',')
                .Append(Quantize(current.localRotation.z)).Append(',')
                .Append(Quantize(current.localRotation.w)).Append('|')
                .Append(Quantize(current.localScale.x)).Append(',')
                .Append(Quantize(current.localScale.y)).Append(',')
                .Append(Quantize(current.localScale.z)).Append('|');

            foreach (Component component in current.GetComponents<Component>().Where(c => c != null))
                value.Append(component.GetType().FullName).Append(',');
            value.Append('|');

            foreach (Mesh mesh in GetDirectMeshes(current.gameObject))
                value.Append(GetMeshIdentity(mesh, geometryOnly)).Append(';');
            value.AppendLine();
        }
        return Hash128.Compute(value.ToString()).ToString();
    }

    private static IEnumerable<Mesh> GetDirectMeshes(GameObject gameObject)
    {
        MeshFilter filter = gameObject.GetComponent<MeshFilter>();
        if (filter != null && filter.sharedMesh != null) yield return filter.sharedMesh;
        SkinnedMeshRenderer skinned = gameObject.GetComponent<SkinnedMeshRenderer>();
        if (skinned != null && skinned.sharedMesh != null) yield return skinned.sharedMesh;
        foreach (MeshCollider collider in gameObject.GetComponents<MeshCollider>())
            if (collider.sharedMesh != null) yield return collider.sharedMesh;
    }

    private static string GetMeshIdentity(Mesh mesh, bool geometryOnly)
    {
        StringBuilder value = new StringBuilder();
        if (!geometryOnly && AssetDatabase.TryGetGUIDAndLocalFileIdentifier(mesh,
            out string guid, out long localId))
            value.Append(guid).Append(':').Append(localId).Append(':');
        value.Append(mesh.vertexCount).Append(':').Append(mesh.subMeshCount).Append(':');
        for (int i = 0; i < mesh.subMeshCount; i++)
            value.Append((int)mesh.GetTopology(i)).Append(',').Append(mesh.GetIndexCount(i)).Append(';');
        Vector3 size = mesh.bounds.size;
        value.Append(Quantize(size.x)).Append(',').Append(Quantize(size.y)).Append(',').Append(Quantize(size.z));
        return value.ToString();
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

    private static int Quantize(float value) => Mathf.RoundToInt(value * 10000f);

    private static string NormalizeName(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        string result = value.Trim();
        while (result.EndsWith("(Clone)", StringComparison.OrdinalIgnoreCase))
            result = result.Substring(0, result.Length - 7).TrimEnd();
        int underscore = result.LastIndexOf('_');
        if (underscore >= 0 && result.Substring(underscore + 1).All(char.IsDigit))
            result = result.Substring(0, underscore);
        return result.ToLowerInvariant();
    }
}
#endif
