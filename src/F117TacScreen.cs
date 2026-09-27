#if UNITY_EDITOR
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace F117Tools
{
    public static class F117TacScreen
    {
        const string Src = F117Setup.Source + "/TacScreen";
        const string TexDir = F117Setup.Mod + "/Textures/TacScreen";
        const string Template = "Assets/Blueprinter/_donotship/GameObject/tacScreen_fighter1_PLACEHOLDER.prefab";
        public const string PrefabPath = F117Setup.Mod + "/Prefabs/tacScreen_FS45.prefab";

        static readonly (string title, string box, string marker, string hardpoint)[] Stations =
        {
            ("R. BAY", "Box_TipR", "hardpoint_TipR", "HP_bay_R"),
            ("R. IRM", "Box_PylonR", "hardpoint_PylonR", "HP_irm_R"),
            ("L. IRM", "Box_PylonL", "hardpoint_PylonL", "HP_irm_L"),
            ("L. BAY", "Box_TipL", "hardpoint_TipL", "HP_bay_L"),
        };
        static readonly string[] Unused = { "Box_Bay", "hardpoint_BayL", "Box_Gun", "hardpoint_Gun" };
        static readonly float[] BoxX = { -162.5f, -54f, 54f, 162.5f };
        const float BoxW = 95f, MarkerSize = 11f;

        static Sprite ImportSprite(string file, bool mips)
        {
            if (!AssetDatabase.IsValidFolder(TexDir)) AssetDatabase.CreateFolder(F117Setup.Mod + "/Textures", "TacScreen");
            var path = TexDir + "/" + file;
            File.Copy(Path.Combine(Src, file), path, true);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            var ti = (TextureImporter)AssetImporter.GetAtPath(path);
            ti.textureType = TextureImporterType.Sprite;
            ti.spriteImportMode = SpriteImportMode.Single;
            ti.alphaIsTransparency = true;
            ti.alphaSource = TextureImporterAlphaSource.FromInput;
            ti.mipmapEnabled = mips;
            ti.wrapMode = TextureWrapMode.Clamp;
            ti.textureCompression = TextureImporterCompression.CompressedHQ;
            ti.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Sprite>(path);
        }

        public static Sprite MapIcon() => ImportSprite("FS45_mapIcon.png", true);

        static Transform Find(Transform root, string name) => root.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == name);

        static Dictionary<string, Vector2> Markers()
        {
            var json = File.ReadAllText(Path.Combine(Src, "tacscreen.json"));
            var d = new Dictionary<string, Vector2>();
            foreach (Match m in Regex.Matches(json, "\"(HP_\\w+)\": \\[\\s*([-\\d.]+),\\s*([-\\d.]+)\\s*\\]"))
                d[m.Groups[1].Value] = new Vector2(float.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture), float.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture));
            return d;
        }

        public static GameObject Build()
        {
            var profile = ImportSprite("FS45_front.png", false);
            var markers = Markers();
            var tmpl = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(Template));
            PrefabUtility.UnpackPrefabInstance(tmpl, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
            try
            {
                tmpl.name = "tacScreen_FS45";
                var root = tmpl.transform;
                Find(root, "frontProfile").GetComponent<Image>().sprite = profile;
                var pi = tmpl.GetComponentsInChildren<Component>(true).First(c => c != null && c.GetType().Name == "PylonIndicator");
                foreach (var n in Unused)
                {
                    var t = Find(root, n);
                    if (t != null) Object.DestroyImmediate(t.gameObject);
                }

                var so = new SerializedObject(pi);
                var sets = so.FindProperty("pylonElementSets");
                sets.arraySize = Stations.Length;
                for (int i = 0; i < Stations.Length; i++)
                {
                    var st = Stations[i];
                    var box = (RectTransform)Find(root, st.box);
                    var marker = (RectTransform)Find(root, st.marker);
                    box.anchoredPosition = new Vector2(BoxX[i], box.anchoredPosition.y);
                    box.sizeDelta = new Vector2(BoxW, box.sizeDelta.y);
                    marker.anchoredPosition = markers[st.hardpoint];
                    marker.sizeDelta = new Vector2(MarkerSize, MarkerSize);

                    var texts = box.GetComponentsInChildren<Component>(true).Where(c => c != null && c.GetType().Name == "TextMeshProUGUI").ToList();
                    var title = texts.FirstOrDefault(c => c.name == "Title");
                    if (title != null)
                    {
                        var ts = new SerializedObject(title);
                        ts.FindProperty("m_text").stringValue = st.title;
                        ts.ApplyModifiedPropertiesWithoutUndo();
                    }
                    var images = new List<Object> { marker.GetComponent<Image>() }.Concat(box.GetComponentsInChildren<Image>(true)).Distinct().ToList();

                    var e = sets.GetArrayElementAtIndex(i);
                    var pim = e.FindPropertyRelative("pylonImages");
                    pim.arraySize = images.Count;
                    for (int k = 0; k < images.Count; k++) pim.GetArrayElementAtIndex(k).objectReferenceValue = images[k];
                    var ptx = e.FindPropertyRelative("pylonTexts");
                    ptx.arraySize = texts.Count;
                    for (int k = 0; k < texts.Count; k++) ptx.GetArrayElementAtIndex(k).objectReferenceValue = texts[k];
                }
                so.ApplyModifiedPropertiesWithoutUndo();
                return PrefabUtility.SaveAsPrefabAsset(tmpl, PrefabPath);
            }
            finally { Object.DestroyImmediate(tmpl); }
        }
    }
}
#endif
