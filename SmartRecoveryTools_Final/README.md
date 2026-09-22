# Smart Recovery Tools

Install the Editor folder under Assets/SmartRecoveryTools/Editor, replacing the previous tool scripts. Keep existing backup and registry data. Do not install older copies alongside this version.

Open Tools > Smart Recovery Tools > Dashboard.

## Workflow
1. Scene Art Cleanup
2. Combined Mesh Fix
3. Smart Prefab Builder
4. Collider Recovery
5. Prefab to FBX
6. Scene Final Checkup
7. Asset Mesh to FBX (optional)
8. Backup Manager

This order is shared by the dashboard, tool menus, window headers and Automatic Recovery. Windows show a short explanation and a Next button. Rig recovery is preserved inside automatic prefab preparation; its separate window is under Advanced.

## Automatic Recovery
Runs steps 1–6 on the current saved scene only. Multiple-scene queues and scene switching have been removed. Open just one scene in Edit Mode before starting. The scene is saved and backed up before step 1. Step 4 repairs references and restores prefab collider configurations; non-prefab component deletion remains a manual review operation.

Step 7 is off by default. Enable it to export the mesh assets/folders selected in the Project window before starting. Step 8 provides access to Backup Manager; it never automatically restores backups. The final scene save is optional. Cancellation or failure leaves earlier changes applied and reports the stopping point.

## Scene Final Checkup
Scans MeshFilters, MeshColliders and skinned mesh references in loaded scenes and their referenced prefab assets. For selected eligible static meshes that do not already come from FBX, it creates/reuses FBX geometry through the existing converter cache, assigns imported FBX meshes to the selected MeshFilter/MeshCollider references, saves modified prefab assets and scans again. Meshes shared by multiple eligible references are converted once per pass.

Combined/generated meshes, static-batched objects, missing meshes and rigged meshes are reported for recovery rather than flattened. Final checkup does not rerun the combined recovery or prefab-builder workflow. Remaining issues prevent Automatic Recovery from marking the scene complete.

FBX export requires Unity's FBX Exporter package.

## Verification
All Editor sources compile together against Unity 6000.3.10f1 libraries. Source checks verify ordered workflow calls, removal of the multi-scene queue, and final repair/rescan routing. Interactive Unity UI and scene conversion tests have not been run for these changes.
