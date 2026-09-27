#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace F117Tools
{
    public static class F117StatusDisplay
    {
        const string Src = F117Setup.Source + "/Status";
        const string TexDir = F117Setup.Mod + "/Textures/Status";
        const string Template = "Assets/Blueprinter/_donotship/GameObject/StatusDisplay_SFB_PLACEHOLDER.prefab";
        public const string PrefabPath = F117Setup.Mod + "/Prefabs/StatusDisplay_F117A.prefab";

        [Serializable] class Part { public string node, image, sprite; public int[] rect; }
        [Serializable] class Layout { public int frame; public string background; public Part[] parts; }

        static Type GT(string name) => AppDomain.CurrentDomain.GetAssemblies().Select(a => { try { return a.GetType(name); } catch { return null; } }).FirstOrDefault(t => t != null);

        static Sprite ImportSprite(string file)
        {
            var path = TexDir + "/" + file;
            File.Copy(Path.Combine(Src, file), path, true);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            var ti = (TextureImporter)AssetImporter.GetAtPath(path);
            ti.textureType = TextureImporterType.Sprite;
            ti.spriteImportMode = SpriteImportMode.Single;
            ti.alphaIsTransparency = true;
            ti.alphaSource = TextureImporterAlphaSource.FromInput;
            ti.mipmapEnabled = false;
            ti.wrapMode = TextureWrapMode.Clamp;
            ti.maxTextureSize = 1024;
            ti.textureCompression = TextureImporterCompression.CompressedHQ;
            ti.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Sprite>(path);
        }

        public static GameObject Build()
        {
            var lay = JsonUtility.FromJson<Layout>(File.ReadAllText(Path.Combine(Src, "status_layout.json")));
            if (!AssetDatabase.IsValidFolder(TexDir)) AssetDatabase.CreateFolder(F117Setup.Mod + "/Textures", "Status");
            var bgSprite = ImportSprite(lay.background);
            var sprites = lay.parts.ToDictionary(p => p.image, p => ImportSprite(p.sprite));

            var tmpl = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(Template));
            PrefabUtility.UnpackPrefabInstance(tmpl, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
            try
            {
                tmpl.name = "StatusDisplay_F117A";
                var so = new SerializedObject(tmpl.GetComponent(GT("StatusDisplay")));
                var list = so.FindProperty("statusDisplays");
                var first = (Image)list.GetArrayElementAtIndex(0).FindPropertyRelative("partImage").objectReferenceValue;
                var parent = first.transform.parent;
                int layer = first.gameObject.layer;
                var mat = first.material;
                var color = first.color;
                float threshold = list.GetArrayElementAtIndex(0).FindPropertyRelative("redStatusThreshold").floatValue;

                var old = Enumerable.Range(0, list.arraySize)
                    .Select(i => list.GetArrayElementAtIndex(i).FindPropertyRelative("partImage").objectReferenceValue as Image)
                    .Where(im => im != null).Select(im => im.gameObject).Distinct().ToList();
                foreach (var go in old) Object.DestroyImmediate(go);

                ((Image)so.FindProperty("aircraftBackground").objectReferenceValue).sprite = bgSprite;
                var frame = ((RectTransform)parent).rect.size;
                float scale = Mathf.Min(frame.x, frame.y) / lay.frame;
                list.arraySize = lay.parts.Length;
                for (int i = 0; i < lay.parts.Length; i++)
                {
                    var p = lay.parts[i];
                    var go = new GameObject(p.image, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image)) { layer = layer };
                    var rt = (RectTransform)go.transform;
                    rt.SetParent(parent, false);
                    rt.SetSiblingIndex(i);
                    rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
                    float x = p.rect[0], y = p.rect[1], w = p.rect[2], h = p.rect[3];
                    rt.sizeDelta = new Vector2(w, h) * scale;
                    rt.anchoredPosition = new Vector2(x + w / 2 - lay.frame / 2f, lay.frame / 2f - (y + h / 2)) * scale;
                    var img = go.GetComponent<Image>();
                    img.sprite = sprites[p.image];
                    img.material = mat;
                    img.color = color;
                    img.raycastTarget = false;
                    var e = list.GetArrayElementAtIndex(i);
                    e.FindPropertyRelative("partImage").objectReferenceValue = img;
                    e.FindPropertyRelative("redStatusThreshold").floatValue = threshold;
                }
                so.ApplyModifiedPropertiesWithoutUndo();
                return PrefabUtility.SaveAsPrefabAsset(tmpl, PrefabPath);
            }
            finally { Object.DestroyImmediate(tmpl); }
        }
    }
}
#endif
