#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEngine;

namespace F117Tools
{
    public static class F117Setup
    {
        public const string Mod = "Assets/Blueprinter/Mods/F117A";
        public const string Source = "Assets/F117Source";
        public const string ModelPath = Source + "/F117A.fbx";

        [MenuItem("Tools/FS-45/1. Configure Imports")]
        public static void ConfigureImports()
        {
            var mi = (ModelImporter)AssetImporter.GetAtPath(ModelPath);
            mi.globalScale = 1f; mi.useFileScale = true; mi.useFileUnits = true;
            mi.bakeAxisConversion = true;
            mi.importCameras = false; mi.importLights = false; mi.importVisibility = false;
            mi.preserveHierarchy = true; mi.sortHierarchyByName = false;
            mi.importBlendShapes = false; mi.importAnimation = false; mi.animationType = ModelImporterAnimationType.None;
            mi.meshCompression = ModelImporterMeshCompression.Off; mi.isReadable = true;
            mi.optimizeMeshPolygons = true; mi.optimizeMeshVertices = true; mi.weldVertices = false;
            mi.importNormals = ModelImporterNormals.Import; mi.importTangents = ModelImporterTangents.CalculateMikk;
            mi.materialImportMode = ModelImporterMaterialImportMode.ImportStandard; mi.materialLocation = ModelImporterMaterialLocation.InPrefab;
            mi.addCollider = false; mi.generateSecondaryUV = false;
            mi.SaveAndReimport();

            foreach (var guid in AssetDatabase.FindAssets("t:Texture2D", new[] { Mod + "/Textures/Exterior", Mod + "/Textures/Cockpit" }))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var ti = (TextureImporter)AssetImporter.GetAtPath(path);
                var name = Path.GetFileNameWithoutExtension(path);
                bool normal = name.EndsWith("_n"), data = name.EndsWith("_m") || name.EndsWith("_ao");
                ti.textureType = normal ? TextureImporterType.NormalMap : TextureImporterType.Default;
                ti.sRGBTexture = !(normal || data);
                ti.mipmapEnabled = true;
                ti.wrapMode = TextureWrapMode.Repeat; ti.filterMode = FilterMode.Trilinear; ti.anisoLevel = 4;
                ti.alphaSource = name.EndsWith("_m") || name.EndsWith("_dmg_b") ? TextureImporterAlphaSource.FromInput : TextureImporterAlphaSource.None;
                ti.maxTextureSize = path.Contains("/Exterior/") && name.Contains("_b") && !name.Contains("dmg") ? 4096 : 2048;
                ti.textureCompression = TextureImporterCompression.CompressedHQ;
                ti.SaveAndReimport();
            }
        }
    }
}
#endif
