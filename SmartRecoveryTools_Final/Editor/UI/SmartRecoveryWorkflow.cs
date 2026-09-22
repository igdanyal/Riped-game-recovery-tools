#if UNITY_EDITOR
using System;

internal static class SmartRecoveryWorkflow
{
    internal static readonly string[] Names = {
        "1. Scene Art Cleanup", "2. Combined Mesh Fix", "3. Smart Prefab Builder",
        "4. Collider Recovery", "5. Prefab to FBX", "6. Scene Final Checkup",
        "7. Asset Mesh to FBX (Optional)", "8. Backup Manager"
    };
    internal static readonly string[] Descriptions = {
        "Remove unwanted scene objects and missing scripts while protecting art.",
        "Recover combined geometry and reuse matching mesh assets.",
        "Create reusable prefabs and reconnect scene objects.",
        "Repair collider meshes; review extra collider components.",
        "Export prefab models and retain their materials and references.",
        "Convert remaining eligible meshes to FBX, assign them, then scan again.",
        "Export selected mesh assets separately when you need standalone FBX files.",
        "Review or restore backups created before changes."
    };
    internal static readonly Type[] Types = {
        typeof(SceneCleanupTool), typeof(SceneCombinedMeshAssetFixer), typeof(CityMultiFBXToPrefabConnectorV3),
        typeof(SmartSceneColliderRecovery), typeof(BatchPrefabToModelConverter), typeof(SmartSceneFinalCheckup),
        typeof(AssetMeshToFbxConverter), typeof(CityToolBackupManager)
    };
}
#endif
