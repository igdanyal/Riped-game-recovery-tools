Unity Smart Recovery Tools
Unity Editor tools for fixing exported scenes and turning recovered art into reusable meshes, prefabs, and FBX models.
What It Does
- Cleans unwanted scene objects and missing scripts.
- Recovers combined meshes and reconnects duplicates.
- Fixes collider references.
- Creates prefabs and exports FBX/OBJ models.
- Checks remaining mesh issues and manages backups.
Installation
Copy SmartRecoveryTools_Final/Editor into your project’s Assets/SmartRecoveryTools/Editor folder.
Open Tools → Smart Recovery Tools → Dashboard.
FBX export requires Unity’s FBX Exporter package.
How to Use
Follow this order:
1. Scene Art Cleanup
2. Combined Mesh Fix
3. Collider Recovery
4. Smart Prefab Builder
5. Prefab to FBX
6. Scene Final Checkup
7. Asset Mesh to FBX — optional
8. Backup Manager
Use Automatic Recovery to run the main workflow on one saved scene.
Experimental Version
The separate experimental package adds mesh diagnosis, candidate previews, manual duplicate-family assignment, and pause/resume processing.
Open Tools → Smart Recovery Experimental → Recovery Lab.
Status
Under development. Compilation checked with Unity 6000.3.10f1. Test on a scene copy and review results—some missing or ambiguous meshes still require manual recovery.
Contributions and bug reports are welcome
