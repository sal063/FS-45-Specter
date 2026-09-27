#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace F117Tools
{
    public static class F117Builder
    {
        const string Mod = F117Setup.Mod;
        const string DS = "Assets/Blueprinter/_donotship/";
        const string DR = DS + "GameObject/Darkreach_PLACEHOLDER.prefab";
        public const string PrefabPath = Mod + "/Prefabs/F117A.prefab";
        public const string DefPath = Mod + "/Data/F117A.asset";
        public const string ParPath = Mod + "/Data/F117A_Parameters.asset";
        public const string JsonKey = "F117A", UnitName = "FS-45 Specter", Code = "FS-45", ModTitle = "FS-45 Specter", Version = "0.2.7";
        const string Description = "A single-seat stealth strike aircraft. Its faceted airframe and radar-absorbent skin give it one of the smallest radar returns of any aircraft, so it can slip through dense air defences at night. It has no radar of its own and flies subsonic on two non-afterburning turbofans. Each of its two main weapon bays carries one heavy store, aimed by an infrared targeting system, and two small bays each hold an infrared missile for self-defence.";
        const float EngineThrust = 80000f, WingLiftOffset = 3.7f, MassScale = 0.85f, RadarSize = 0.0002f, ShelterHalfLength = 9.5f;
        const int SolverIterations = 24;
        static readonly Quaternion Rfix = Quaternion.LookRotation(new Vector3(0, -1, 0), new Vector3(0, 0, -1));

        static StringBuilder log;
        static void L(string s) => log.AppendLine(s);

        static Type GT(string name) => AppDomain.CurrentDomain.GetAssemblies().Select(a => { try { return a.GetType(name); } catch { return null; } }).FirstOrDefault(t => t != null);
        static T Load<T>(string path) where T : Object { var o = AssetDatabase.LoadAssetAtPath<T>(path); if (o == null) L("missing asset " + path); return o; }
        static Object LoadAny(string path) { var o = AssetDatabase.LoadMainAssetAtPath(path); if (o == null) L("missing asset " + path); return o; }

        static SerializedProperty P(SerializedObject so, string path)
        {
            var p = so.FindProperty(path);
            if (p == null) L($"no field {path} on {so.targetObject.GetType().Name} ({so.targetObject.name})");
            return p;
        }

        static void Num(SerializedProperty p, double v)
        {
            if (p == null) return;
            if (p.propertyType == SerializedPropertyType.Integer) p.intValue = (int)Math.Round(v);
            else p.floatValue = (float)v;
        }

        static void SetColor32(SerializedProperty p, Color32 c)
        {
            if (p.propertyType == SerializedPropertyType.Color) { p.colorValue = c; return; }
            var r = p.FindPropertyRelative("r");
            if (r != null)
            {
                r.intValue = c.r; p.FindPropertyRelative("g").intValue = c.g; p.FindPropertyRelative("b").intValue = c.b; p.FindPropertyRelative("a").intValue = c.a;
            }
            else
            {
                var rgba = p.FindPropertyRelative("rgba");
                if (rgba != null) rgba.longValue = (uint)(c.r | (c.g << 8) | (c.b << 16) | (c.a << 24));
            }
        }

        static void Set(Object o, string path, Action<SerializedProperty> f)
        {
            var so = new SerializedObject(o);
            var p = P(so, path);
            if (p == null) return;
            f(p);
            so.ApplyModifiedPropertiesWithoutUndo();
        }
        static void SetRef(Object o, string path, Object v) => Set(o, path, p => p.objectReferenceValue = v);
        static void SetF(Object o, string path, float v) => Set(o, path, p => Num(p, v));
        static void SetI(Object o, string path, int v) => Set(o, path, p => { if (p.propertyType == SerializedPropertyType.Enum) p.enumValueIndex = v; else Num(p, v); });
        static void SetB(Object o, string path, bool v) => Set(o, path, p => p.boolValue = v);
        static void SetS(Object o, string path, string v) => Set(o, path, p => p.stringValue = v);
        static void SetCurve(Object o, string path, AnimationCurve v) => Set(o, path, p => p.animationCurveValue = v);
        static void SetRefs(Object o, string path, IList<Object> vs) => Set(o, path, p =>
        {
            p.arraySize = vs.Count;
            for (int i = 0; i < vs.Count; i++) p.GetArrayElementAtIndex(i).objectReferenceValue = vs[i];
        });

        static GameObject tmpl;
        static Transform TT(string path)
        {
            var t = path == "" ? tmpl.transform : tmpl.transform.Find(path);
            if (t == null) L("template path not found: " + path);
            return t;
        }
        static Component TC(string path, string type) => TT(path)?.GetComponent(GT(type));

        static Component CopyComp(Component src, GameObject dst, bool reuse = true)
        {
            if (src == null) return null;
            var c = reuse ? dst.GetComponent(src.GetType()) : null;
            if (c == null) c = dst.AddComponent(src.GetType());
            EditorUtility.CopySerialized(src, c);
            ClearTemplateRefs(c);
            return c;
        }

        static void ClearTemplateRefs(Object c)
        {
            var so = new SerializedObject(c);
            var p = so.GetIterator();
            bool changed = false;
            while (p.Next(true))
            {
                if (p.propertyType != SerializedPropertyType.ObjectReference || p.objectReferenceValue == null) continue;
                var o = p.objectReferenceValue;
                var tr = o is Component cc ? cc.transform : o is GameObject g ? g.transform : null;
                if (tr != null && tr.IsChildOf(tmpl.transform)) { p.objectReferenceValue = null; changed = true; }
            }
            var ms = so.FindProperty("maxSplit");
            if (changed && ms != null && so.FindProperty("splitUpper")?.objectReferenceValue == null) ms.floatValue = 0f;
            if (changed) so.ApplyModifiedPropertiesWithoutUndo();
        }

        static GameObject CloneTmpl(string path, Transform parent, string name = null)
        {
            var t = TT(path);
            if (t == null) return null;
            var go = Object.Instantiate(t.gameObject, parent, false);
            go.name = name ?? t.name;
            foreach (var c in go.GetComponentsInChildren<Component>(true)) if (c != null && !(c is Transform)) ClearTemplateRefs(c);
            return go;
        }

        static void StripTo(GameObject go, params Type[] keep)
        {
            foreach (var c in go.GetComponents<Component>().Reverse()) if (!(c is Transform) && !keep.Any(k => k.IsInstanceOfType(c))) Object.DestroyImmediate(c);
            for (int i = go.transform.childCount - 1; i >= 0; i--) Object.DestroyImmediate(go.transform.GetChild(i).gameObject);
        }

        static Dictionary<string, Transform> N;
        static Dictionary<string, Material> mats;
        static Dictionary<string, Mesh> colMesh;

        static Mesh Bake(Mesh src, Matrix4x4 m, string name, int[] order = null)
        {
            order ??= Enumerable.Range(0, src.subMeshCount).ToArray();
            var R = Matrix4x4.Rotate(Rfix) * m;
            var mesh = new Mesh { name = name, indexFormat = src.indexFormat };
            mesh.vertices = src.vertices.Select(v => R.MultiplyPoint3x4(v)).ToArray();
            var n = src.normals;
            if (n.Length == src.vertexCount) mesh.normals = n.Select(v => R.MultiplyVector(v).normalized).ToArray();
            var t = src.tangents;
            if (t.Length == src.vertexCount) mesh.tangents = t.Select(v => { var x = R.MultiplyVector(new Vector3(v.x, v.y, v.z)).normalized; return new Vector4(x.x, x.y, x.z, v.w); }).ToArray();
            for (int ch = 0; ch < 4; ch++)
            {
                var uv = new List<Vector2>();
                src.GetUVs(ch, uv);
                if (uv.Count == src.vertexCount) mesh.SetUVs(ch, uv);
            }
            if (src.colors32.Length == src.vertexCount) mesh.colors32 = src.colors32;
            mesh.subMeshCount = order.Length;
            for (int s = 0; s < order.Length; s++) mesh.SetTriangles(src.GetTriangles(order[s]), s);
            mesh.RecalculateBounds();
            AssetDatabase.CreateAsset(mesh, $"{Mod}/Meshes/{name}.asset");
            return mesh;
        }

        static Mesh FitShelter(Mesh src, Transform t, Transform root)
        {
            var v = src.vertices.Select(p => root.InverseTransformPoint(t.TransformPoint(p))).ToArray();
            if (v.All(p => Mathf.Abs(p.z) <= ShelterHalfLength)) return src;
            var mesh = new Mesh { name = src.name + "_col" };
            mesh.vertices = v.Select(p => t.InverseTransformPoint(root.TransformPoint(new Vector3(p.x, p.y, Mathf.Clamp(p.z, -ShelterHalfLength, ShelterHalfLength))))).ToArray();
            mesh.triangles = src.triangles;
            mesh.RecalculateBounds();
            AssetDatabase.CreateAsset(mesh, $"{Mod}/Meshes/{mesh.name}.asset");
            return mesh;
        }

        static Material MapMat(Material m)
        {
            if (m == null) return null;
            var key = m.name.Replace(" (Instance)", "");
            if (mats.TryGetValue(key, out var r)) return r;
            var mat = new Material(Load<Material>(DS + "Material/SFB_gear_PLACEHOLDER.mat")) { name = key };
            foreach (var tex in new[] { "_BaseMap", "_MainTex", "_BumpMap", "_MetallicGlossMap", "_OcclusionMap" }) if (mat.HasProperty(tex)) mat.SetTexture(tex, null);
            mat.DisableKeyword("_METALLICSPECGLOSSMAP"); mat.DisableKeyword("_NORMALMAP"); mat.DisableKeyword("_OCCLUSIONMAP");
            var col = m.HasProperty("_BaseColor") ? m.GetColor("_BaseColor") : m.color;
            mat.SetColor("_BaseColor", col);
            if (mat.HasProperty("_Color")) mat.SetColor("_Color", col);
            float met = 0f, sm = 0.35f;
            var k = key.ToLowerInvariant();
            if (k.Contains("chrome")) { met = 1f; sm = 0.85f; }
            else if (k.Contains("fan_ti") || k.Contains("pins")) { met = 0.9f; sm = 0.55f; }
            else if (k.Contains("hub") || k.Contains("cockpit_metal") || k.Contains("mount") || k.Contains("trapeze") || k.Contains("ladder")) { met = 0.6f; sm = 0.45f; }
            else if (k.Contains("tire") || k.Contains("rubber") || k.Contains("rope")) { met = 0f; sm = 0.15f; }
            mat.SetFloat("_Metallic", met);
            mat.SetFloat("_Smoothness", sm);
            mats[key] = SaveMat(mat);
            return mat;
        }

        static Material SaveMat(Material mat)
        {
            AssetDatabase.CreateAsset(mat, $"{Mod}/Materials/{mat.name}.mat");
            return mat;
        }

        static void Rebuild(Transform s, Transform dstParent, GameObject root)
        {
            Transform d;
            if (dstParent == null) d = root.transform;
            else
            {
                d = new GameObject(s.name).transform;
                d.SetParent(dstParent, false);
                d.localPosition = Rfix * s.localPosition;
                d.localRotation = Rfix * s.localRotation * Quaternion.Inverse(Rfix);
                d.localScale = s.localScale;
            }
            N[s.name] = d;
            var mf = s.GetComponent<MeshFilter>();
            var mr = s.GetComponent<MeshRenderer>();
            if (mf != null && mr != null && mf.sharedMesh != null)
            {
                var src = mr.sharedMaterials;
                var order = Enumerable.Range(0, src.Length).OrderBy(i => src[i] != null && src[i].name == "F117_Exterior_Livery" ? 0 : 1).ToArray();
                d.gameObject.AddComponent<MeshFilter>().sharedMesh = Bake(mf.sharedMesh, Matrix4x4.identity, s.name, order);
                d.gameObject.AddComponent<MeshRenderer>().sharedMaterials = order.Select(i => MapMat(src[i])).ToArray();
            }
            foreach (Transform c in s)
                if (!c.name.StartsWith("COL_") && c.name != "F117_Flight_Controls") Rebuild(c, d, root);
        }

        static readonly Dictionary<string, string> ColOwner = new Dictionary<string, string>
        {
            {"COL_Fuselage_Centre","F117_Fuselage_Centre"}, {"COL_Nose","F117_Nose"}, {"COL_Fuselage_Aft","F117_Fuselage_Aft"},
            {"COL_Wing_L","F117_Wing_L"}, {"COL_Wing_R","F117_Wing_R"}, {"COL_Tail_L","F117_Tail_L"}, {"COL_Tail_R","F117_Tail_R"},
            {"COL_Engine_L","F117_Engine_L"}, {"COL_Engine_R","F117_Engine_R"}, {"COL_Canopy","F117_Canopy"},
            {"COL_Nose_Tip","F117_Nose_Tip"}, {"COL_WingRoot_L","F117_WingRoot_L"}, {"COL_WingRoot_R","F117_WingRoot_R"},
            {"COL_Wingtip_L","F117_Wingtip_L"}, {"COL_Wingtip_R","F117_Wingtip_R"}, {"COL_Exhaust_L","F117_Exhaust_L"}, {"COL_Exhaust_R","F117_Exhaust_R"},
        };

        static void Clean()
        {
            foreach (var f in new[] { "Prefabs", "Data", "Meshes", "Materials", "Weapons", "Textures/Status", "Textures/TacScreen" })
                if (AssetDatabase.IsValidFolder(Mod + "/" + f)) AssetDatabase.DeleteAsset(Mod + "/" + f);
            foreach (var f in new[] { "Prefabs", "Data", "Meshes", "Materials" }) AssetDatabase.CreateFolder(Mod, f);
        }

        [MenuItem("Tools/FS-45/2. Build Aircraft")]
        public static void BuildMenu() => Debug.Log(Build());

        public static string Build()
        {
            log = new StringBuilder();
            N = new Dictionary<string, Transform>();
            mats = new Dictionary<string, Material>();
            colMesh = new Dictionary<string, Mesh>();
            Clean();
            GameObject src = null, root = null;
            try
            {
                AssetDatabase.StartAssetEditing();
                try
                {
                    tmpl = (GameObject)PrefabUtility.InstantiatePrefab(Load<GameObject>(DR));
                    PrefabUtility.UnpackPrefabInstance(tmpl, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
                    src = (GameObject)PrefabUtility.InstantiatePrefab(Load<GameObject>(F117Setup.ModelPath));
                    PrefabUtility.UnpackPrefabInstance(src, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
                    MakeMaterials();
                    root = new GameObject("F117A");
                    Rebuild(src.transform.Find("F117_Fuselage_Centre"), null, root);
                    var all = src.GetComponentsInChildren<Transform>(true);
                    foreach (var col in all.Where(t => t.name.StartsWith("COL_")))
                    {
                        if (!ColOwner.TryGetValue(col.name, out var owner)) continue;
                        var m = all.First(t => t.name == owner).worldToLocalMatrix * col.localToWorldMatrix;
                        colMesh[owner] = Bake(col.GetComponent<MeshFilter>().sharedMesh, m, col.name);
                    }
                }
                finally { AssetDatabase.StopAssetEditing(); }
                AddEverything(root);
                var prefab = PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
                MakeData(prefab);
                MakeOps();
                AssetDatabase.SaveAssets();
                L("built " + PrefabPath);
            }
            catch (Exception e) { L(e.ToString()); }
            finally
            {
                if (tmpl != null) Object.DestroyImmediate(tmpl);
                if (src != null) Object.DestroyImmediate(src);
                if (root != null) Object.DestroyImmediate(root);
                tmpl = null;
            }
            return log.ToString();
        }

        static void MakeMaterials()
        {
            var T = Mod + "/Textures/Exterior/";
            var skin = new Material(Load<Material>(DS + "Material/SFB_skin_PLACEHOLDER.mat")) { name = "F117_skin" };
            skin.SetTexture("_Basecolor", Load<Texture2D>(T + "F117_BDF_MatteGray_b.png"));
            skin.SetTexture("_Livery", Load<Texture2D>(T + "F117_BDF_MatteGray_b.png"));
            skin.SetTexture("_Metallic", Load<Texture2D>(T + "F117_skin_m.png"));
            skin.SetTexture("_Normal", Load<Texture2D>(T + "F117_skin_n.png"));
            skin.SetTexture("_AO", Load<Texture2D>(T + "F117_skin_ao.png"));
            skin.SetTexture("_BasecolorDmg", Load<Texture2D>(T + "F117_skin_dmg_b.png"));
            skin.SetTexture("_NormalDmg", Load<Texture2D>(T + "F117_skin_dmg_n.png"));
            mats["F117_Exterior_Livery"] = SaveMat(skin);

            foreach (var (blender, game) in new[] { ("NO_Seats", "Seats"), ("NO_hotas", "hotas"), ("NO_Matte_black", "Matte_black"),
                                                    ("NO_aircraft_structure", "aircraft_structure"), ("F117_Break_Damage", "aircraft_structure") })
                mats[blender] = Load<Material>(DS + "Material/" + game + "_PLACEHOLDER.mat");

            var C = Mod + "/Textures/Cockpit/";
            var ck = new Material(Load<Material>(DS + "Material/SFB_cockpit_PLACEHOLDER.mat")) { name = "F117_cockpit" };
            ck.SetTexture("_BaseMap", Load<Texture2D>(C + "F117_Cockpit_b.png"));
            ck.SetTexture("_MainTex", Load<Texture2D>(C + "F117_Cockpit_b.png"));
            ck.SetTexture("_MetallicGlossMap", Load<Texture2D>(C + "F117_Cockpit_m.png"));
            ck.SetTexture("_BumpMap", Load<Texture2D>(C + "F117_Cockpit_n.png"));
            ck.SetTexture("_OcclusionMap", Load<Texture2D>(C + "F117_Cockpit_ao.png"));
            ck.SetFloat("_Smoothness", 1f);
            ck.SetFloat("_Metallic", 1f);
            mats["F117_Cockpit"] = SaveMat(ck);

            mats["F117_Canopy_Glass"] = Load<Material>(DS + "Material/SFB_glass_PLACEHOLDER.mat");
            mats["F117_HUD_Glass"] = Load<Material>(DS + "Material/SFB_glass_int_PLACEHOLDER.mat");
            mats["F117_TacScreen"] = Load<Material>(DS + "Material/tacScreenDisplay_PLACEHOLDER.mat");
            mats["F117_WarningLight"] = Load<Material>(DS + "Material/CockpitWarningLight_PLACEHOLDER.mat");
        }

        class PartCfg
        {
            public string node, parent, tmplPath, group;
            public float mass, wing, drag, breakF, fuel;
            public PartCfg(string n, string p, float m, float w, float d, float b, float f, string t, string g = null)
            {
                node = n; parent = p; mass = m * MassScale; wing = w; drag = d; breakF = b; fuel = f; tmplPath = t; group = g;
            }
        }

        static readonly PartCfg[] Parts =
        {
            new PartCfg("F117_Fuselage_Centre", null,                   4600, 20f,  0.55f, 0,      5700, ""),
            new PartCfg("F117_WingRoot_L",      "F117_Fuselage_Centre", 0,    0f,   0f,    9.0e5f, 0,    "engine_L/gearbay_L/wing1b_L/wing1a_L", "F117_Fuselage_Centre"),
            new PartCfg("F117_WingRoot_R",      "F117_Fuselage_Centre", 0,    0f,   0f,    9.0e5f, 0,    "engine_R/gearbay_R/wing1b_R/wing1a_R", "F117_Fuselage_Centre"),
            new PartCfg("F117_Nose",            "F117_Fuselage_Centre", 3500, 3f,   0.30f, 1.0e6f, 0,    "fuselage_F/cockpit"),
            new PartCfg("F117_Nose_Tip",        "F117_Nose",            0,    0f,   0f,    3.0e5f, 0,    "fuselage_F/cockpit/nose", "F117_Nose"),
            new PartCfg("F117_Fuselage_Aft",    "F117_Fuselage_Centre", 700,  6f,   0.35f, 6.0e5f, 0,    "fuselage_R"),
            new PartCfg("F117_Exhaust_L",       "F117_Fuselage_Aft",    0,    0f,   0f,    3.0e5f, 0,    "fuselage_R/nozzle_L", "F117_Fuselage_Aft"),
            new PartCfg("F117_Exhaust_R",       "F117_Fuselage_Aft",    0,    0f,   0f,    3.0e5f, 0,    "fuselage_R/nozzle_R", "F117_Fuselage_Aft"),
            new PartCfg("F117_Wing_L",          "F117_WingRoot_L",      750,  27f,  0.12f, 7.0e5f, 1300, "engine_L/gearbay_L/wing1b_L/wing2_L"),
            new PartCfg("F117_Wing_R",          "F117_WingRoot_R",      750,  27f,  0.12f, 7.0e5f, 1300, "engine_R/gearbay_R/wing1b_R/wing2_R"),
            new PartCfg("F117_Wingtip_L",       "F117_Wing_L",          0,    0f,   0f,    2.5e5f, 0,    "engine_L/gearbay_L/wing1b_L/wing2_L/wingtip_L", "F117_Wing_L"),
            new PartCfg("F117_Wingtip_R",       "F117_Wing_R",          0,    0f,   0f,    2.5e5f, 0,    "engine_R/gearbay_R/wing1b_R/wing2_R/wingtip_R", "F117_Wing_R"),
            new PartCfg("F117_Engine_L",        "F117_Fuselage_Centre", 1036, 0f,   0f,    2.0e6f, 0,    "engine_L"),
            new PartCfg("F117_Engine_R",        "F117_Fuselage_Centre", 1036, 0f,   0f,    2.0e6f, 0,    "engine_R"),
            new PartCfg("F117_Tail_L",          "F117_Fuselage_Aft",    180,  3.6f, 0.04f, 3.0e5f, 0,    "fuselage_R/nozzle_L/vstab1_L"),
            new PartCfg("F117_Tail_R",          "F117_Fuselage_Aft",    180,  3.6f, 0.04f, 3.0e5f, 0,    "fuselage_R/nozzle_R/vstab1_R"),
            new PartCfg("HINGE_rudder_L",       "F117_Tail_L",          30,   2.1f, 0f,    1.0e5f, 0,    "fuselage_R/nozzle_L/vstab1_L/rudder_L"),
            new PartCfg("HINGE_rudder_R",       "F117_Tail_R",          30,   2.1f, 0f,    1.0e5f, 0,    "fuselage_R/nozzle_R/vstab1_R/rudder_R"),
            new PartCfg("HINGE_elevon_L_in",    "F117_Wing_L",          60,   2.6f, 0f,    1.5e5f, 0,    "engine_L/gearbay_L/wing1b_L/wing2_L/aileron_L"),
            new PartCfg("HINGE_elevon_L_out",   "F117_Wingtip_L",       60,   2.4f, 0f,    1.5e5f, 0,    "engine_L/gearbay_L/wing1b_L/wing2_L/aileron_L"),
            new PartCfg("HINGE_elevon_R_in",    "F117_Wing_R",          60,   2.6f, 0f,    1.5e5f, 0,    "engine_R/gearbay_R/wing1b_R/wing2_R/aileron_R"),
            new PartCfg("HINGE_elevon_R_out",   "F117_Wingtip_R",       60,   2.4f, 0f,    1.5e5f, 0,    "engine_R/gearbay_R/wing1b_R/wing2_R/aileron_R"),
        };

        static readonly Dictionary<string, float> BaseLiftZ = new Dictionary<string, float>
        {
            { "F117_Fuselage_Centre", 0.8188f }, { "F117_Nose", 5.6317f }, { "F117_Fuselage_Aft", -4.0443f }, { "F117_Wing_L", -2.6243f }, { "F117_Wing_R", -2.6178f }
        };

        static readonly Dictionary<string, Component> part = new Dictionary<string, Component>();
        static readonly Dictionary<string, (float mass, float wing, float drag, Vector3 lift, float fuel)> eff = new Dictionary<string, (float, float, float, Vector3, float)>();

        static (float vol, Vector3 centroid, float foot) HullStats(Mesh m, Transform t)
        {
            var v = m.vertices.Select(t.TransformPoint).ToArray();
            var tri = m.triangles;
            double vol = 0, foot = 0;
            var c = Vector3.zero;
            for (int i = 0; i < tri.Length; i += 3)
            {
                Vector3 a = v[tri[i]], b = v[tri[i + 1]], d = v[tri[i + 2]];
                float tv = Vector3.Dot(a, Vector3.Cross(b, d)) / 6f;
                vol += tv;
                c += tv * (a + b + d) / 4f;
                foot += Mathf.Abs(Vector3.Cross(b - a, d - a).y) * 0.25;
            }
            return ((float)Math.Abs(vol), c / (float)vol, (float)foot);
        }

        static void SplitGroups()
        {
            eff.Clear();
            foreach (var pc in Parts)
                eff[pc.node] = (pc.mass, pc.wing, pc.drag, pc.node.StartsWith("F117_Wing_") ? new Vector3(0f, 0f, WingLiftOffset) : Vector3.zero, pc.fuel);
            foreach (var g in Parts.Where(p => Parts.Any(q => q.group == p.node)).ToList())
            {
                var members = new[] { g }.Concat(Parts.Where(q => q.group == g.node)).ToList();
                var st = members.Select(m => HullStats(colMesh[m.node], N[m.node])).ToList();
                float V = st.Sum(s => s.vol), A = st.Sum(s => s.foot);
                float off = eff[g.node].lift.z, liftZ = BaseLiftZ[g.node] + off;
                float rest = 0f;
                for (int k = 1; k < members.Count; k++) rest += st[k].foot * (st[k].centroid.z + off);
                float mainOff = (A * liftZ - rest) / st[0].foot - st[0].centroid.z;
                for (int k = 0; k < members.Count; k++)
                {
                    var s = st[k];
                    eff[members[k].node] = (g.mass * s.vol / V, g.wing * s.foot / A, g.drag * s.foot / A, new Vector3(0f, 0f, k == 0 ? mainOff : off), g.fuel * s.vol / V);
                }
            }
        }

        static List<Renderer> SkinRenderers(Transform t, HashSet<Transform> partNodes)
        {
            var skin = mats["F117_Exterior_Livery"];
            var list = new List<Renderer>();
            void Rec(Transform x)
            {
                var r = x.GetComponent<MeshRenderer>();
                if (r != null && r.sharedMaterials.Contains(skin)) list.Add(r);
                foreach (Transform c in x) if (!partNodes.Contains(c)) Rec(c);
            }
            Rec(t);
            return list;
        }

        static void SanitizeDamage(Component ap)
        {
            var so = new SerializedObject(ap);
            var de = so.FindProperty("damageEffects");
            for (int i = de.arraySize - 1; i >= 0; i--)
                if (de.GetArrayElementAtIndex(i).FindPropertyRelative("prefab").objectReferenceValue == null) de.DeleteArrayElementAtIndex(i);
            foreach (var path in new[] { "disintegrationEffects", "disintegrationObjects" })
            {
                var a = so.FindProperty(path);
                if (a == null) continue;
                for (int i = a.arraySize - 1; i >= 0; i--)
                    if (a.GetArrayElementAtIndex(i).objectReferenceValue == null) { a.GetArrayElementAtIndex(i).objectReferenceValue = null; a.DeleteArrayElementAtIndex(i); }
            }
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        static void SetEngineColliderClearance(Transform engine, string side)
        {
            var source = Load<Mesh>(Mod + "/Meshes/COL_Engine_" + side + ".asset");
            var mesh = Object.Instantiate(source);
            mesh.name = "COL_Engine_" + side + "_Clearance";
            float rear = source.bounds.min.z, front = source.bounds.max.z;
            mesh.vertices = source.vertices.Select(v => new Vector3(v.x, v.y, Mathf.Lerp(rear + 0.06f, front, Mathf.InverseLerp(rear, front, v.z)))).ToArray();
            mesh.RecalculateBounds();
            mesh.RecalculateNormals();
            AssetDatabase.CreateAsset(mesh, Mod + "/Meshes/" + mesh.name + ".asset");
            engine.GetComponent<MeshCollider>().sharedMesh = mesh;
        }

        static void SetElevonLiftFrame(Component aero, Transform visual, Transform root)
        {
            var frame = new GameObject(visual.name + "_liftNormal").transform;
            frame.SetParent(visual, false);
            frame.rotation = root.rotation;
            frame.gameObject.layer = visual.gameObject.layer;
            SetRef(aero, "liftNormal", frame);
            SetF(aero, "airflowChanneling", 0f);
        }

        static void ConfigureInternalECM(GameObject root, Component aircraft)
        {
            var jammer = CopyComp(Load<GameObject>(DR).GetComponent(GT("RadarJammer")), root);
            SetRef(jammer, "aircraft", aircraft);
            SetS(jammer, "displayName", "Radar ECM");
            SetB(jammer, "chargeable", true);
            SetI(jammer, "ammo", 1);
            SetF(jammer, "jammingIntensity", 16f);
            SetF(jammer, "powerUsage", 40f);
            SetF(jammer, "capacitance", 200f);
        }

        static void AddEverything(GameObject root)
        {
            part.Clear();
            Component aircraft = CopyComp(TC("", "Aircraft"), root);
            CopyComp(TC("", "Mirage.NetworkIdentity"), root);
            var rb = (Rigidbody)CopyComp(TC("", "UnityEngine.Rigidbody"), root);
            rb.mass = Parts.Sum(p => p.mass);
            Transform Nd(string n) { if (N.TryGetValue(n, out var t)) return t; L("node not found: " + n); return null; }
            var layerExt = LayerMask.NameToLayer("CockpitAndExternal");
            var layerCk = LayerMask.NameToLayer("Cockpit");
            foreach (var t in root.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = layerExt;

            foreach (var (node, mesh) in new[] { ("F117_EjectionSeat", "ejectionSeat"), ("F117_joystick", "fighter1_joystick"), ("F117_throttle_L", "fighter1_throttle") })
            {
                var mf = Nd(node).GetComponent<MeshFilter>();
                AssetDatabase.DeleteAsset(AssetDatabase.GetAssetPath(mf.sharedMesh));
                mf.sharedMesh = Load<Mesh>(DS + "Mesh/" + mesh + "_PLACEHOLDER.asset");
            }

            var lodMat = Load<Material>(DS + "Material/__dedupe/LOD_PLACEHOLDER.mat");
            var physMat = Load<PhysicMaterial>(DS + "PhysicMaterial/Metal_PLACEHOLDER.physicMaterial");
            var lod1 = new List<Renderer>();
            foreach (var pc in Parts)
            {
                var t = Nd(pc.node);
                var ap = CopyComp(TC(pc.tmplPath, "AeroPart"), t.gameObject);
                part[pc.node] = ap;
                var hinge = pc.node.StartsWith("HINGE_");
                Mesh cm = null;
                if (hinge) cm = Nd(pc.node.Replace("HINGE_", "F117_")).GetComponent<MeshFilter>().sharedMesh;
                else colMesh.TryGetValue(pc.node, out cm);
                if (cm == null) continue;
                var mc = t.gameObject.AddComponent<MeshCollider>();
                mc.sharedMesh = FitShelter(cm, t, root.transform); mc.convex = true; mc.sharedMaterial = physMat;
                var l = new GameObject(pc.node + "_lod1") { layer = layerExt };
                l.transform.SetParent(hinge ? Nd(pc.node.Replace("HINGE_", "F117_")) : t, false);
                l.AddComponent<MeshFilter>().sharedMesh = cm;
                var lr = l.AddComponent<MeshRenderer>();
                lr.sharedMaterial = lodMat;
                lod1.Add(lr);
            }

            SplitGroups();
            var partNodes = new HashSet<Transform>(part.Values.Select(c => c.transform));
            var breakSound = new SerializedObject(TC("engine_L", "AeroPart")).FindProperty("joints").GetArrayElementAtIndex(0).FindPropertyRelative("breakSound").objectReferenceValue;
            foreach (var pc in Parts)
            {
                var ap = part[pc.node];
                SanitizeDamage(ap);
                var ev = eff[pc.node];
                var so = new SerializedObject(ap);
                P(so, "parentUnit").objectReferenceValue = aircraft;
                P(so, "rb").objectReferenceValue = pc.parent == null ? rb : null;
                Num(P(so, "mass"), ev.mass);
                Num(P(so, "wingArea"), ev.wing);
                Num(P(so, "dragArea"), ev.drag);
                Num(P(so, "airfoil"), 0);
                P(so, "centerOfLift").vector3Value = ev.lift;
                P(so, "liftNormal").objectReferenceValue = null;
                var skinR = SkinRenderers(ap.transform, partNodes);
                var dr = P(so, "damageMaterial.renderers");
                dr.arraySize = skinR.Count;
                for (int i = 0; i < skinR.Count; i++) dr.GetArrayElementAtIndex(i).objectReferenceValue = skinR[i];

                var links = new List<(string, float)>();
                if (pc.parent != null) links.Add((pc.parent, pc.breakF));
                if (pc.node.EndsWith("_out")) links.Add((pc.node.Replace("_out", "_in"), 5.0e4f));
                var j = P(so, "joints");
                j.arraySize = links.Count;
                for (int i = 0; i < links.Count; i++)
                {
                    var e = j.GetArrayElementAtIndex(i);
                    e.FindPropertyRelative("connectedPart").objectReferenceValue = part[links[i].Item1];
                    e.FindPropertyRelative("tensor").objectReferenceValue = null;
                    Num(e.FindPropertyRelative("solverIterations"), SolverIterations);
                    Num(e.FindPropertyRelative("breakForce"), links[i].Item2);
                    Num(e.FindPropertyRelative("breakTorque"), links[i].Item2);
                    e.FindPropertyRelative("anchor").objectReferenceValue = null;
                    e.FindPropertyRelative("breakSound").objectReferenceValue = breakSound;
                }
                so.ApplyModifiedPropertiesWithoutUndo();
                if (ev.fuel > 0)
                {
                    var ft = CopyComp(TC("", "FuelTank"), ap.gameObject, false);
                    SetF(ft, "fuelCapacity", ev.fuel);
                    SetRef(ft, "part", ap);
                    SetRefs(ft, "connectedTanks", new Object[0]);
                }
            }
            var centre = part["F117_Fuselage_Centre"];
            var nose = part["F117_Nose"];
            var aft = part["F117_Fuselage_Aft"];
            foreach (var side in new[] { "L", "R" }) SetEngineColliderClearance(Nd("F117_Engine_" + side), side);

            foreach (var side in new[] { "L", "R" })
            {
                var tail = Nd("F117_Tail_" + side);
                var rud = Nd("F117_rudder_" + side);
                var nrm = Vector3.Cross(Nd("HINGE_rudder_" + side).right, Vector3.forward).normalized;
                if (Vector3.Dot(nrm, side == "L" ? Vector3.left : Vector3.right) < 0) nrm = -nrm;
                var tln = new GameObject("F117_Tail_" + side + "_liftNormal") { layer = layerExt }.transform;
                tln.SetParent(tail, false);
                tln.position = tail.GetComponent<MeshRenderer>().bounds.center;
                tln.rotation = Quaternion.LookRotation(Vector3.Cross(nrm, Vector3.Cross(Vector3.forward, nrm)).normalized, nrm);
                SetRef(part["F117_Tail_" + side], "liftNormal", tln);
                var rln = new GameObject("F117_rudder_" + side + "_liftNormal") { layer = layerExt }.transform;
                rln.SetParent(rud, false);
                rln.position = rud.GetComponent<MeshRenderer>().bounds.center;
                rln.rotation = tln.rotation;
                SetRef(part["HINGE_rudder_" + side], "liftNormal", rln);
                SetF(part["HINGE_rudder_" + side], "airflowChanneling", 0f);
                foreach (var io in new[] { "in", "out" }) SetElevonLiftFrame(part[$"HINGE_elevon_{side}_{io}"], Nd($"F117_elevon_{side}_{io}"), root.transform);
            }

            var csT = TC("engine_L/gearbay_L/wing1b_L/wing2_L/aileron_L", "ControlSurface");
            foreach (var side in new[] { "L", "R" })
            {
                float s = side == "L" ? 1f : -1f;
                foreach (var io in new[] { "in", "out" })
                {
                    var cs = CopyComp(csT, Nd($"HINGE_elevon_{side}_{io}").gameObject);
                    ConfigureCS(cs, part[$"HINGE_elevon_{side}_{io}"], Nd($"F117_elevon_{side}_{io}").gameObject, 38f, 35f * s, 0f, 110f);
                }
                var rcs = CopyComp(csT, Nd("HINGE_rudder_" + side).gameObject);
                ConfigureCS(rcs, part["HINGE_rudder_" + side], Nd("F117_rudder_" + side).gameObject, 0f, 0f, 35f, 100f);
                var ped = Nd("F117_pedal_" + side);
                var pcs = CopyComp(csT, ped.gameObject);
                ConfigureCS(pcs, nose, ped.gameObject, 0f, 0f, -2.2924f * s, 200f);
            }

            var vanes = Nd("F117_exhaust_vanes_L").GetComponent<MeshRenderer>().bounds;
            vanes.Encapsulate(Nd("F117_exhaust_vanes_R").GetComponent<MeshRenderer>().bounds);
            var nozzles = new Dictionary<string, Component>();
            var engines = new Dictionary<string, GameObject>();
            var engAudio = new Dictionary<string, AudioSource>();
            var nozAudio = new Dictionary<string, AudioSource>();
            foreach (var side in new[] { "L", "R" })
            {
                float sx = side == "L" ? -1f : 1f;
                var nz = CloneTmpl($"fuselage_R/nozzle_{side}/nozzle", Nd("F117_Fuselage_Aft"), "nozzle_" + side);
                nz.transform.position = new Vector3(sx * vanes.extents.x * 0.5f, vanes.center.y, vanes.min.z);
                nz.transform.rotation = Quaternion.identity;
                foreach (var t in nz.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = layerExt;
                var jn = nz.GetComponent(GT("JetNozzle"));
                SetRef(jn, "part", aft);
                SetRef(jn, "thrustTransform", nz.transform);
                SetF(jn, "IRMin", 0.4f);
                SetF(jn, "IRMax", 1.6f);
                nozzles[side] = jn;
                nozAudio[side] = nz.GetComponent<AudioSource>();

                var eng = Nd("F117_Engine_" + side).gameObject;
                engines[side] = eng;
                var tEng = TT("engine_" + side);
                var auds = tEng.GetComponents<AudioSource>().Select(a => (AudioSource)CopyComp(a, eng, false)).ToArray();
                engAudio[side] = auds[0];
                var tf = CopyComp(tEng.GetComponent(GT("Turbofan")), eng);
                SetF(tf, "staticThrust", EngineThrust);
                SetCurve(tf, "speedThrust", new AnimationCurve(new Keyframe(0, 1f), new Keyframe(150, 1.02f), new Keyframe(260, 0.92f), new Keyframe(320, 0f)));
                SetRef(tf, "turbineAudio", auds[0]);
                SetRef(tf, "part", part["F117_Engine_" + side]);
                SetRefs(tf, "nozzles", new Object[] { jn });
                var so = new SerializedObject(tf);
                var cp = P(so, "criticalParts");
                cp.arraySize = 3;
                var crit = new[] { (part["F117_Engine_" + side], 0.5f), (centre, 0.25f), (part["F117_Exhaust_" + side], 0.25f) };
                for (int i = 0; i < 3; i++)
                {
                    var e = cp.GetArrayElementAtIndex(i);
                    e.FindPropertyRelative("part").objectReferenceValue = crit[i].Item1;
                    Num(e.FindPropertyRelative("threshold"), 80f);
                    Num(e.FindPropertyRelative("weight"), crit[i].Item2);
                }
                so.ApplyModifiedPropertiesWithoutUndo();
            }

            var ps = CopyComp(TC("", "PowerSupply"), root);
            var capAudio = (AudioSource)CopyComp(TT("").GetComponent<AudioSource>(), root, false);
            SetRefs(ps, "powerSources", new Object[] { engines["L"], engines["R"] });
            SetRef(ps, "source", capAudio);
            SetRef(ps, "aircraft", aircraft);
            ConfigureInternalECM(root, aircraft);
            SetRef(CopyComp(TC("", "NuclearOption.NetworkTransforms.AircraftNetworkTransform"), root), "Aircraft", aircraft);
            var eots = CopyComp(TC("", "TargetDetector"), root);
            SetRef(eots, "attachedUnit", aircraft);
            SetRef(eots, "scanner", root.transform);
            var flare = CopyComp(TC("", "FlareEjector"), root);
            SetRef(flare, "aircraft", aircraft);
            SetI(flare, "ammo", 32);
            {
                var so = new SerializedObject(flare);
                var ep = P(so, "ejectionPoints");
                ep.arraySize = 2;
                var ab = Nd("F117_Fuselage_Aft").GetComponent<MeshRenderer>().bounds;
                for (int i = 0; i < 2; i++)
                {
                    float sx = i == 0 ? -1f : 1f;
                    var fe = new GameObject(i == 0 ? "flareEjector_L" : "flareEjector_R") { layer = layerExt }.transform;
                    fe.SetParent(Nd("F117_Fuselage_Aft"), false);
                    fe.position = new Vector3(sx * ab.extents.x * 0.45f, ab.min.y + 0.15f, ab.center.z);
                    fe.rotation = Quaternion.LookRotation(new Vector3(sx * 0.3f, -1f, -0.2f));
                    var e = ep.GetArrayElementAtIndex(i);
                    e.FindPropertyRelative("part").objectReferenceValue = aft;
                    e.FindPropertyRelative("transform").objectReferenceValue = fe;
                }
                so.ApplyModifiedPropertiesWithoutUndo();
            }

            var sparks = CloneTmpl("contactSparks", root.transform);
            sparks.transform.localPosition = new Vector3(0, 2.2f, 0);
            var dw = CloneTmpl("downwash", root.transform);
            dw.transform.localPosition = new Vector3(0, -1.8f, -1.5f);
            var dwc = dw.GetComponent(GT("Downwash"));
            SetRef(dwc, "aircraft", aircraft);
            {
                var so = new SerializedObject(dwc);
                var s = P(so, "downwashSources");
                s.arraySize = 2;
                for (int i = 0; i < 2; i++)
                {
                    var e = s.GetArrayElementAtIndex(i);
                    var sd = i == 0 ? "L" : "R";
                    e.FindPropertyRelative("thrustSourceObject").objectReferenceValue = engines[sd];
                    e.FindPropertyRelative("castTransform").objectReferenceValue = nozzles[sd].transform;
                    Num(e.FindPropertyRelative("maxThrust"), 55000f);
                }
                so.ApplyModifiedPropertiesWithoutUndo();
            }
            foreach (var t in sparks.GetComponentsInChildren<Transform>(true).Concat(dw.GetComponentsInChildren<Transform>(true))) t.gameObject.layer = layerExt;

            var vortex = new List<ParticleSystem>();
            var navEntries = new List<(Renderer r, GameObject go, Material mat, Component part)>();
            foreach (var side in new[] { "L", "R" })
            {
                var w = Nd("F117_Wingtip_" + side);
                var wb = Nd("F117_Wing_" + side).GetComponent<MeshRenderer>().bounds;
                wb.Encapsulate(w.GetComponent<MeshRenderer>().bounds);
                var tipPos = new Vector3(side == "L" ? wb.min.x + 0.05f : wb.max.x - 0.05f, wb.center.y, wb.min.z + 0.4f);
                var tip = $"engine_{side}/gearbay_{side}/wing1b_{side}/wing2_{side}/wingtip_{side}";
                var vx = CloneTmpl($"{tip}/wingtipvortex_{side}", w, "wingtipvortex_" + side);
                vx.transform.position = tipPos;
                vx.transform.rotation = Quaternion.identity;
                vortex.Add(vx.GetComponent<ParticleSystem>());
                var nl = CloneTmpl($"{tip}/navLight_{side}/navlight_{side}_effects", w, "navlight_" + side);
                nl.transform.position = tipPos + new Vector3(0, 0, 0.3f);
                nl.transform.rotation = Quaternion.identity;
                var nlr = nl.GetComponent<ParticleSystemRenderer>();
                navEntries.Add((nlr, nl, nlr.sharedMaterial, part["F117_Wingtip_" + side]));
                foreach (var t in vx.GetComponentsInChildren<Transform>(true).Concat(nl.GetComponentsInChildren<Transform>(true))) t.gameObject.layer = layerExt;
            }
            var vap = CopyComp(TC("fuselage_F", "VaporEffect"), Nd("F117_Nose").gameObject);
            SetRef(vap, "aircraft", aircraft);
            {
                var so = new SerializedObject(vap);
                var em = P(so, "emitters");
                for (int i = 0; i < Mathf.Min(em.arraySize, 2); i++)
                {
                    var e = em.GetArrayElementAtIndex(i);
                    e.FindPropertyRelative("particles").objectReferenceValue = vortex[i];
                    var et = e.FindPropertyRelative("emitTransforms");
                    et.arraySize = 1;
                    et.GetArrayElementAtIndex(0).objectReferenceValue = vortex[i].transform;
                }
                so.ApplyModifiedPropertiesWithoutUndo();
            }

            var gearComp = new Dictionary<string, Component>();
            var dust = new List<ParticleSystem>();
            foreach (var g in new[] { "N", "L", "R" })
            {
                var strut = Nd($"F117_gear_{g}_strut");
                var tg = g == "N" ? "fuselage_F/cockpit/gearhinge_F/gear_F" : $"engine_{g}/gearbay_{g}/gearhinge_{g}/gear_{g}";
                var lg = CopyComp(TT(tg).GetComponent(GT("LandingGear")), strut.gameObject);
                gearComp[g] = lg;
                var sb = strut.GetComponent<MeshFilter>().sharedMesh.bounds;
                var bottom = Mathf.Max(sb.min.y, strut.InverseTransformPoint(Nd("gear_unsprung_" + g).position).y + 0.05f);
                var mc = strut.gameObject.AddComponent<BoxCollider>();
                mc.center = new Vector3(sb.center.x, (sb.max.y + bottom) * 0.5f, sb.center.z);
                mc.size = new Vector3(sb.size.x, sb.max.y - bottom, sb.size.z);
                mc.enabled = false;
                var wheel = Nd($"F117_wheel_{g}");
                var tw = g == "N" ? "fuselage_F/cockpit/gearhinge_F/gear_F/gear_unsprung_F/wheel_F" : $"engine_{g}/gearbay_{g}/gearhinge_{g}/gear_{g}/gear_unsprung_{g}/bogey/wheel_{g}R";
                var twd = g == "N" ? tw : $"engine_{g}/gearbay_{g}/gearhinge_{g}/gear_{g}/gear_unsprung_{g}/bogey/wheel_{g}F";
                var tires = TT(tw).GetComponents<AudioSource>().Select(a => (AudioSource)CopyComp(a, wheel.gameObject, false)).ToArray();
                var d = CloneTmpl(twd, wheel, "dust");
                StripTo(d, typeof(ParticleSystem), typeof(ParticleSystemRenderer));
                d.transform.localPosition = Vector3.zero;
                d.transform.localRotation = Quaternion.identity;
                d.layer = layerExt;
                var dps = d.GetComponent<ParticleSystem>();
                dust.Add(dps);

                var so = new SerializedObject(lg);
                P(so, "attachedPart").objectReferenceValue = g == "N" ? nose : centre;
                P(so, "aircraft").objectReferenceValue = aircraft;
                P(so, "bumpStop").objectReferenceValue = Nd("bumpstop_" + g).gameObject;
                P(so, "unsprung").objectReferenceValue = Nd("gear_unsprung_" + g).gameObject;
                P(so, "castPoint").objectReferenceValue = null;
                Num(P(so, "wheelRadius"), g == "N" ? 0.25f : 0.41f);
                Num(P(so, "suspensionTravel"), g == "N" ? 0.45f : 0.66f);
                Num(P(so, "springRate"), g == "N" ? 4.0e5f : 1.2e6f);
                Num(P(so, "dampingRate"), g == "N" ? 3.5e4f : 9.0e4f);
                Num(P(so, "mass"), g == "N" ? 120f : 300f);
                var wl = P(so, "wheels");
                wl.arraySize = 1;
                wl.GetArrayElementAtIndex(0).objectReferenceValue = wheel;
                P(so, "axle").objectReferenceValue = Nd($"F117_gear_{g}_fork");
                P(so, "joints").arraySize = 0;
                P(so, "tireNoiseSound").objectReferenceValue = tires.Length > 0 ? tires[0] : null;
                P(so, "tireSkidSound").objectReferenceValue = tires.Length > 1 ? tires[1] : (tires.Length > 0 ? tires[0] : null);
                P(so, "gearCollider").objectReferenceValue = mc;
                P(so, "gearHinge").objectReferenceValue = Nd("HINGE_gear_" + g);
                P(so, "hingeFoldMotion").vector3Value = g == "N" ? new Vector3(0, 0, -0.455f) : Vector3.zero;
                Num(P(so, "foldDegrees"), -90f);
                P(so, "strutRotationTransform").objectReferenceValue = Nd("gear_unsprung_" + g);
                Num(P(so, "strutRotation"), g == "N" ? 0f : (g == "R" ? -90f : 90f));
                P(so, "movingParts").arraySize = 0;
                var door = Nd("F117_geardoor_" + g);
                var openAngle = DoorAngle(door);
                door.localRotation = Quaternion.identity;
                var gd = P(so, "gearDoors");
                gd.arraySize = 1;
                var ge = gd.GetArrayElementAtIndex(0);
                ge.FindPropertyRelative("transform").objectReferenceValue = door;
                ge.FindPropertyRelative("closedAngle").vector3Value = Vector3.zero;
                ge.FindPropertyRelative("openAngle").vector3Value = openAngle;
                P(so, "steering").boolValue = g == "N";
                Num(P(so, "steeringLock"), g == "N" ? 60f : 0f);
                P(so, "braked").boolValue = g != "N";
                P(so, "dust").objectReferenceValue = dps;
                so.ApplyModifiedPropertiesWithoutUndo();
            }
            SetRefs(CopyComp(TC("", "SetGlobalParticles"), root), "systems", dust.Cast<Object>().ToList());
            var gl = CloneTmpl("fuselage_F/cockpit/gearhinge_F/gear_F/gearlight_F", Nd("F117_gear_N_strut"), "gearlight_N");
            gl.transform.position = Nd("bumpstop_N").position + new Vector3(0, -0.25f, 0.35f);
            gl.transform.localRotation = Quaternion.identity;
            foreach (var t in gl.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = layerExt;
            var nav = CopyComp(TC("", "NavLights"), root);
            SetRef(nav, "aircraft", aircraft);
            {
                var tn = new SerializedObject(TC("", "NavLights")).FindProperty("navLights");
                var so = new SerializedObject(nav);
                var nl = P(so, "navLights");
                nl.arraySize = 3;
                var e0 = nl.GetArrayElementAtIndex(0);
                e0.FindPropertyRelative("renderer").objectReferenceValue = gl.GetComponent<ParticleSystemRenderer>();
                var o0 = e0.FindPropertyRelative("objects");
                o0.arraySize = 1;
                o0.GetArrayElementAtIndex(0).objectReferenceValue = gl;
                e0.FindPropertyRelative("litMaterial").objectReferenceValue = tn.GetArrayElementAtIndex(0).FindPropertyRelative("litMaterial").objectReferenceValue;
                e0.FindPropertyRelative("part").objectReferenceValue = nose;
                e0.FindPropertyRelative("gear").objectReferenceValue = gearComp["N"];
                e0.FindPropertyRelative("controlState").enumValueIndex = 0;
                for (int i = 0; i < 2; i++)
                {
                    var e = nl.GetArrayElementAtIndex(i + 1);
                    var ne = navEntries[i];
                    e.FindPropertyRelative("renderer").objectReferenceValue = ne.r;
                    var oo = e.FindPropertyRelative("objects");
                    oo.arraySize = 1;
                    oo.GetArrayElementAtIndex(0).objectReferenceValue = ne.go;
                    e.FindPropertyRelative("litMaterial").objectReferenceValue = ne.mat;
                    e.FindPropertyRelative("part").objectReferenceValue = ne.part;
                    e.FindPropertyRelative("gear").objectReferenceValue = null;
                    e.FindPropertyRelative("controlState").enumValueIndex = 1;
                }
                so.ApplyModifiedPropertiesWithoutUndo();
            }

            var bays = new Dictionary<string, Component>();
            foreach (var side in new[] { "L", "R" })
            {
                var h = Nd("HINGE_bay_door_" + side);
                var bd = CopyComp(TC("baydoor_LL", "BayDoor"), h.gameObject);
                var aud = (AudioSource)CopyComp(TT("baydoor_LL").GetComponent<AudioSource>(), h.gameObject, false);
                SetF(bd, "hingeAngle", side == "R" ? 105f : -105f);
                SetRef(bd, "doorAudioSource", aud);
                bays[side] = bd;
                Nd("HP_bay_" + side).rotation = Quaternion.identity;
            }
            var wm = CopyComp(TC("fuselage_F/cockpit", "WeaponManager"), Nd("F117_Nose").gameObject);
            SetRef(wm, "aircraft", aircraft);
            {
                var so = new SerializedObject(wm);
                var sets = P(so, "hardpointSets");
                sets.arraySize = 1;
                var s0 = sets.GetArrayElementAtIndex(0);
                s0.FindPropertyRelative("name").stringValue = "Internal Weapon Bays";
                s0.FindPropertyRelative("precludingHardpointSets").arraySize = 0;
                s0.FindPropertyRelative("SymmetryWithPrev").boolValue = false;
                s0.FindPropertyRelative("SymmetryName").stringValue = "";
                var opts = s0.FindPropertyRelative("weaponOptions");
                var list = BayWeapons();
                opts.arraySize = list.Count + 1;
                opts.GetArrayElementAtIndex(0).objectReferenceValue = null;
                for (int i = 0; i < list.Count; i++) opts.GetArrayElementAtIndex(i + 1).objectReferenceValue = list[i];
                var hps = s0.FindPropertyRelative("hardpoints");
                hps.arraySize = 2;
                for (int i = 0; i < 2; i++)
                {
                    var side = i == 0 ? "L" : "R";
                    var e = hps.GetArrayElementAtIndex(i);
                    e.FindPropertyRelative("transform").objectReferenceValue = Nd("HP_bay_" + side);
                    e.FindPropertyRelative("part").objectReferenceValue = centre;
                    var bdl = e.FindPropertyRelative("bayDoors");
                    bdl.arraySize = 1;
                    bdl.GetArrayElementAtIndex(0).objectReferenceValue = bays[side];
                    Num(e.FindPropertyRelative("doorOpenDuration"), 1f);
                    e.FindPropertyRelative("pylonOptions").arraySize = 0;
                    e.FindPropertyRelative("Pylon").objectReferenceValue = null;
                    e.FindPropertyRelative("Plug").objectReferenceValue = null;
                    e.FindPropertyRelative("BuiltInWeapons").arraySize = 0;
                    e.FindPropertyRelative("BuiltInTurrets").arraySize = 0;
                    Num(e.FindPropertyRelative("HardpointIndex"), side == "L" ? 3 : 0);
                }
                so.ApplyModifiedPropertiesWithoutUndo();
            }

            SetRef(CopyComp(TC("fuselage_F/cockpit", "AutopilotPlane"), Nd("F117_Nose").gameObject), "aircraft", aircraft);
            var cf = CopyComp(TC("fuselage_F/cockpit", "ControlsFilter"), Nd("F117_Nose").gameObject);
            SetRef(cf, "flyByWire.noseGear", gearComp["N"]);
            SetF(cf, "flyByWire.gLimitPositive", 8.5f);
            SetF(cf, "flyByWire.maxRollSpeed", 220f);
            SetF(cf, "flyByWire.maxPitchAngularVel", 0.95f);
            SetF(cf, "flyByWire.maxRollAngularVel", 3.8f);
            SetF(cf, "flyByWire.rollTightness", 0.5f);
            SetF(cf, "flyByWire.yawTightness", 1.5f);
            SetF(cf, "flyByWire.pFactorFast", 10f);
            SetF(cf, "flyByWire.dFactorFast", 1.2f);
            SetF(cf, "flyByWire.alphaLimiter", 18f);

            var noseT = Nd("F117_Nose");
            var nb = noseT.GetComponent<MeshRenderer>().bounds;
            nb.Encapsulate(Nd("F117_Nose_Tip").GetComponent<MeshRenderer>().bounds);
            var camF = new GameObject("targetCamForward") { layer = layerExt }.transform;
            camF.SetParent(noseT, false);
            camF.position = new Vector3(0, nb.min.y + 0.12f, nb.max.z - 1.2f);
            var camR = new GameObject("targetCamRear") { layer = layerExt }.transform;
            camR.SetParent(noseT, false);
            camR.localPosition = new Vector3(0, 0.6f, -19f);
            camR.localRotation = Quaternion.Euler(0, 180, 0);
            var camL = new GameObject("landingCamPoint") { layer = layerExt }.transform;
            camL.SetParent(noseT, false);
            camL.localPosition = new Vector3(0, -1.4f, -15f);
            camL.localRotation = Quaternion.Euler(10, 0, 0);
            var tc = CopyComp(TC("fuselage_F/cockpit/nose", "TargetCam"), noseT.gameObject);
            SetRef(tc, "currentMount", camF);
            SetRef(tc, "camMountForward", camF);
            SetRef(tc, "camMountRear", camR);
            SetRef(tc, "camMountLanding", camL);
            SetRef(tc, "attachedPart", nose);
            SetRef(tc, "targetScreenRenderer", Nd("F117_tacScreen").GetComponent<MeshRenderer>());
            var radar = CopyComp(TC("fuselage_F/cockpit/nose", "Radar"), noseT.gameObject);
            SetRef(radar, "attachedUnit", aircraft);
            SetRef(radar, "scanner", noseT);
            SetF(radar, "RadarParameters.maxRange", 20000f);
            var rl = CopyComp(TC("fuselage_F/cockpit/nose", "RadarLocator"), noseT.gameObject);
            SetRef(rl, "aircraft", aircraft);
            SetRefs(rl, "essentialParts", new Object[] { nose });

            var ckInt = Nd("F117_cockpit_int").gameObject;
            var ckc = CopyComp(TC("fuselage_F/cockpit/cockpit_int", "Cockpit"), ckInt);
            SetRef(ckc, "tacScreenRender", Nd("F117_tacScreen").GetComponent<MeshRenderer>());
            SetRef(ckc, "aircraft", aircraft);
            SetRef(ckc, "tacScreenUIPrefab", F117TacScreen.Build());
            SetRefs(ckc, "engineSources", new Object[] { engines["L"], engines["R"] });
            {
                var so = new SerializedObject(ckc);
                var js = P(so, "joysticks");
                js.arraySize = 1;
                js.GetArrayElementAtIndex(0).FindPropertyRelative("transform").objectReferenceValue = Nd("F117_joystick");
                Num(js.GetArrayElementAtIndex(0).FindPropertyRelative("range"), 9f);
                var th = P(so, "throttles");
                th.arraySize = 1;
                var e = th.GetArrayElementAtIndex(0);
                e.FindPropertyRelative("rotation").boolValue = true;
                e.FindPropertyRelative("motion").boolValue = false;
                e.FindPropertyRelative("transform").objectReferenceValue = Nd("F117_throttle_L");
                Num(e.FindPropertyRelative("range"), 6.5f);
                so.ApplyModifiedPropertiesWithoutUndo();
            }
            var cwl = CopyComp(TC("fuselage_F/cockpit/cockpit_int", "CockpitWarningLights"), ckInt);
            SetRefs(cwl, "lightRenderers", new Object[] { Nd("F117_warningLights").GetComponent<MeshRenderer>() });
            SetRef(cwl, "aircraft", aircraft);

            var pilotT = Nd("Pilot");
            var cap = CopyComp(TC("fuselage_F/cockpit/Pilot", "UnityEngine.CapsuleCollider"), pilotT.gameObject);
            var fig = CloneTmpl("fuselage_F/cockpit/Pilot/pilot", pilotT, "pilot");
            fig.transform.localPosition = Vector3.zero;
            fig.transform.localRotation = Quaternion.identity;
            foreach (var t in fig.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = layerExt;
            var pil = CopyComp(TC("fuselage_F/cockpit/Pilot", "Pilot"), pilotT.gameObject);
            SetRef(pil, "aircraft", aircraft);
            SetRef(pil, "pilotCollider", cap);
            SetRef(pil, "skinnedMeshRenderer", fig.GetComponentInChildren<SkinnedMeshRenderer>(true));
            SetRef(pil, "animator", fig.GetComponent<Animator>());
            SetRef(pil, "unitPart", nose);
            SetB(pil, "ejectionSeat", true);
            SetI(pil, "exitDirection", 0);
            SetRef(pil, "autoTrimmer", cf);
            var helmetCam = fig.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name.StartsWith("helmetCamPoint"));

            var canopy = Nd("F117_Canopy").gameObject;
            var glassExt = Nd("F117_Canopy_Glass");
            glassExt.gameObject.layer = 0;
            var glassInt = Object.Instantiate(glassExt.gameObject, glassExt.parent, false).transform;
            glassInt.name = "F117_Canopy_Glass_int";
            glassInt.gameObject.layer = layerCk;
            glassInt.GetComponent<MeshRenderer>().sharedMaterial = Load<Material>(DS + "Material/SFB_glass_int_PLACEHOLDER.mat");
            Nd("F117_HUD_Glass").gameObject.layer = layerCk;
            var cmc = canopy.AddComponent<MeshCollider>();
            cmc.sharedMesh = colMesh["F117_Canopy"];
            cmc.convex = true;
            cmc.enabled = false;
            var can = CopyComp(TC("fuselage_F/cockpit/canopyHingea/canopyHingeb/canopyFrame", "Canopy"), canopy);
            SetRef(can, "ejectionTransform", canopy.transform);
            SetRef(can, "attachedPart", nose);
            SetF(can, "mass", 150f);
            SetRef(can, "ejectionCollider", cmc);
            SetRefs(can, "glassRenderers", new Object[] { glassInt.GetComponent<MeshRenderer>() });
            {
                var so = new SerializedObject(can);
                var ch = P(so, "canopyHinges");
                ch.arraySize = 1;
                ch.GetArrayElementAtIndex(0).FindPropertyRelative("transform").objectReferenceValue = Nd("HINGE_canopy_a");
                Num(ch.GetArrayElementAtIndex(0).FindPropertyRelative("hingeAngle"), -40f);
                so.ApplyModifiedPropertiesWithoutUndo();
            }

            var ground = new[] { "F117_ladder", "F117_chocks_N", "F117_chocks_L", "F117_chocks_R" }.Select(n => Nd(n).gameObject).ToList();
            foreach (var g in ground) g.SetActive(false);

            {
                var so = new SerializedObject(aircraft);
                P(so, "cockpitViewPoint").objectReferenceValue = helmetCam;
                P(so, "radar").objectReferenceValue = radar;
                var ds = P(so, "dopplerSounds");
                var dss = new Object[] { engAudio["L"], engAudio["R"], nozAudio["L"], nozAudio["R"] };
                ds.arraySize = dss.Length;
                for (int i = 0; i < dss.Length; i++) ds.GetArrayElementAtIndex(i).objectReferenceValue = dss[i];
                P(so, "EOTS").objectReferenceValue = eots;
                var pl = P(so, "pilots");
                pl.arraySize = 1;
                pl.GetArrayElementAtIndex(0).objectReferenceValue = pil;
                P(so, "targetCam").objectReferenceValue = tc;
                P(so, "weaponManager").objectReferenceValue = wm;
                P(so, "countermeasureManager.aircraft").objectReferenceValue = aircraft;
                P(so, "cockpit").objectReferenceValue = nose;
                P(so, "controlsFilter").objectReferenceValue = cf;
                P(so, "powerSupply").objectReferenceValue = ps;
                var cn = P(so, "canopies");
                cn.arraySize = 1;
                cn.GetArrayElementAtIndex(0).objectReferenceValue = can;
                var ckr = new Object[] { Nd("F117_tacScreen").GetComponent<MeshRenderer>(), Nd("F117_HUD_Glass").GetComponent<MeshRenderer>(), glassInt.GetComponent<MeshRenderer>() };
                var cr = P(so, "cockpitRenderers");
                cr.arraySize = ckr.Length;
                for (int i = 0; i < ckr.Length; i++) cr.GetArrayElementAtIndex(i).objectReferenceValue = ckr[i];
                var er = P(so, "exteriorRenderers");
                er.arraySize = 1;
                er.GetArrayElementAtIndex(0).objectReferenceValue = glassExt.GetComponent<MeshRenderer>();
                P(so, "sparksEmitter").objectReferenceValue = sparks.GetComponent<ParticleSystem>();
                var gre = P(so, "groundEquipment");
                gre.arraySize = ground.Count;
                for (int i = 0; i < ground.Count; i++) gre.GetArrayElementAtIndex(i).objectReferenceValue = ground[i];
                P(so, "weaponStations").arraySize = 0;
                so.ApplyModifiedPropertiesWithoutUndo();
            }

            Nd("F117_Engine_L").name = "engine_L";
            Nd("F117_Engine_R").name = "engine_R";

            F117Bays.Configure(root);
            F117IrmBays.Configure(root, centre);
            var lodg = root.AddComponent<LODGroup>();
            var lod0 = root.GetComponentsInChildren<Renderer>(true).Where(r => !lod1.Contains(r)).ToArray();
            lodg.SetLODs(new[] { new LOD(0.05f, lod0), new LOD(0.001f, lod1.ToArray()) });
            lodg.RecalculateBounds();
        }

        static void ConfigureCS(Component cs, Component attached, GameObject visible, float pitch, float roll, float yaw, float servo)
        {
            var so = new SerializedObject(cs);
            Num(P(so, "pitchRange"), pitch);
            Num(P(so, "rollRange"), roll);
            Num(P(so, "yawRange"), yaw);
            Num(P(so, "brakeRange"), 0f);
            Num(P(so, "servoSpeed"), servo);
            P(so, "attachedSurface").objectReferenceValue = attached;
            P(so, "visibleMesh").objectReferenceValue = visible;
            P(so, "flap").boolValue = false;
            P(so, "splitUpper").objectReferenceValue = null;
            P(so, "splitLower").objectReferenceValue = null;
            Num(P(so, "maxSplit"), 0f);
            Num(P(so, "splitDrag"), 0f);
            Num(P(so, "yawSplitFactor"), 0f);
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        static Vector3 DoorAngle(Transform door)
        {
            door.localRotation.ToAngleAxis(out float ang, out Vector3 axis);
            if (ang > 180f) { ang = 360f - ang; axis = -axis; }
            var main = Mathf.Abs(axis.x) > Mathf.Abs(axis.y) ? (Mathf.Abs(axis.x) > Mathf.Abs(axis.z) ? 0 : 2) : (Mathf.Abs(axis.y) > Mathf.Abs(axis.z) ? 1 : 2);
            var v = Vector3.zero;
            v[main] = ang * Mathf.Sign(axis[main]);
            return v;
        }

        static readonly string[] BayKeys =
        {
            "bomb_500_glide_internalx1", "bomb_500_internal", "bomb_250_glide_internal", "bomb_250_internal", "bomb_cluster1_single_internal",
            "AGM_heavy_internal", "ARM2_single_internal", "AShM2_internal_single", "nuclearBomb1_internal", "nuclearBomb1_strategic_internal"
        };
        static List<Object> BayWeapons() => BayKeys.Select(F117Bays.GetMount).ToList();

        static void MakeData(GameObject prefab)
        {
            var liv = new List<(string name, string faction, ScriptableObject data)>();
            var livType = GT("LiveryData");
            foreach (var (file, name, faction, gloss) in new[] { ("BDF_MatteGray", "BDF Matte Gray", "Boscali", 0.28f), ("BDF_GrayCamo", "BDF Gray Camo", "Boscali", 0.28f), ("PALA_MatteBlack", "PALA Matte Black", "Primeva", 0.25f), ("PALA_DesertCamo", "PALA Desert Camo", "Primeva", 0.28f) })
            {
                var ld = ScriptableObject.CreateInstance(livType);
                ld.name = "F117_Livery_" + file;
                var tex = Load<Texture2D>($"{Mod}/Textures/Exterior/F117_{file}_b.png");
                var so = new SerializedObject(ld);
                P(so, "Texture").objectReferenceValue = tex;
                Num(P(so, "Glossiness"), gloss);
                var cols = Swatches(tex);
                var ca = P(so, "Colors");
                ca.arraySize = cols.Count;
                for (int i = 0; i < cols.Count; i++)
                {
                    SetColor32(ca.GetArrayElementAtIndex(i).FindPropertyRelative("Color"), cols[i].Item1);
                    Num(ca.GetArrayElementAtIndex(i).FindPropertyRelative("Count"), cols[i].Item2);
                }
                so.ApplyModifiedPropertiesWithoutUndo();
                AssetDatabase.CreateAsset(ld, $"{Mod}/Data/F117_Livery_{file}.asset");
                liv.Add((name, faction, ld));
            }

            var statusDisplay = F117StatusDisplay.Build();
            var mapIcon = F117TacScreen.MapIcon();
            var irm = F117IrmBays.Mount();
            var par = Object.Instantiate(Load<ScriptableObject>(DS + "MonoBehaviour/SFBParameters_PLACEHOLDER.asset"));
            par.name = "F117A_Parameters";
            {
                var so = new SerializedObject(par);
                P(so, "aircraftName").stringValue = UnitName;
                P(so, "StatusDisplay").objectReferenceValue = statusDisplay;
                Num(P(so, "rankRequired"), 3);
                var af = P(so, "airfoils");
                af.arraySize = 1;
                var a0 = af.GetArrayElementAtIndex(0);
                a0.FindPropertyRelative("name").stringValue = "F117_faceted_wing";
                a0.FindPropertyRelative("liftCoef").animationCurveValue = new AnimationCurve(new Keyframe(-3.14159f, 0f), new Keyframe(-1.57f, 0f), new Keyframe(-0.785f, -1.0f), new Keyframe(-0.42f, -0.95f), new Keyframe(-0.3f, -1.1f), new Keyframe(0f, 0f), new Keyframe(0.3f, 1.1f), new Keyframe(0.42f, 0.95f), new Keyframe(0.785f, 1.0f), new Keyframe(1.57f, 0f), new Keyframe(3.14159f, 0f));
                a0.FindPropertyRelative("dragCoef").animationCurveValue = new AnimationCurve(new Keyframe(-3f, 0f), new Keyframe(-1.49f, 0.62f), new Keyframe(-0.2f, 0.17f), new Keyframe(0f, 0.016f), new Keyframe(0.2f, 0.17f), new Keyframe(1.49f, 0.62f), new Keyframe(3f, 0f));

                var bw = BayWeapons().ToDictionary(o => o.name, o => o);
                void Weapons(SerializedProperty w, Object bay, Object irmMount)
                {
                    w.arraySize = 2;
                    w.GetArrayElementAtIndex(0).objectReferenceValue = bay;
                    w.GetArrayElementAtIndex(1).objectReferenceValue = irmMount;
                }
                var lo = P(so, "loadouts");
                lo.arraySize = 2;
                Weapons(lo.GetArrayElementAtIndex(0).FindPropertyRelative("weapons"), null, null);
                Weapons(lo.GetArrayElementAtIndex(1).FindPropertyRelative("weapons"), bw["bomb_500_glide_internalx1"], irm);
                var std = new (string, string)[] { ("Precision Strike", "bomb_500_glide_internalx1"), ("Unguided", "bomb_500_internal"), ("Small Glide Bombs", "bomb_250_glide_internal"), ("Cluster", "bomb_cluster1_single_internal"),
                                                  ("Air-to-Ground Missiles", "AGM_heavy_internal"), ("Radar Suppression", "ARM2_single_internal"), ("Anti-Ship", "AShM2_internal_single"),
                                                  ("Tactical Nuclear", "nuclearBomb1_internal"), ("Strategic Nuclear", "nuclearBomb1_strategic_internal") };
                var sl = P(so, "StandardLoadouts");
                sl.arraySize = std.Length;
                for (int i = 0; i < std.Length; i++)
                {
                    var e = sl.GetArrayElementAtIndex(i);
                    e.FindPropertyRelative("disabled").boolValue = false;
                    e.FindPropertyRelative("Name").stringValue = std[i].Item1;
                    Weapons(e.FindPropertyRelative("loadout.weapons"), bw[std[i].Item2], irm);
                    Num(e.FindPropertyRelative("FuelRatio"), std[i].Item2.StartsWith("nuclearBomb1_strategic") ? 1f : 0.6f);
                }

                var lv = P(so, "liveries");
                lv.arraySize = liv.Count;
                var factions = new Dictionary<string, Object> { { "Boscali", LoadAny(DS + "MonoBehaviour/Boscali_PLACEHOLDER.asset") }, { "Primeva", LoadAny(DS + "MonoBehaviour/Primeva_PLACEHOLDER.asset") } };
                for (int i = 0; i < liv.Count; i++)
                {
                    var e = lv.GetArrayElementAtIndex(i);
                    e.FindPropertyRelative("name").stringValue = liv[i].name;
                    e.FindPropertyRelative("faction").objectReferenceValue = factions[liv[i].faction];
                    e.FindPropertyRelative("assetReference.m_AssetGUID").stringValue = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(liv[i].data));
                    e.FindPropertyRelative("assetReference.m_SubObjectName").stringValue = "";
                }
                Num(P(so, "aircraftGLimit"), 8.5f); Num(P(so, "PIDReferenceAirspeed"), 180f); Num(P(so, "maxSpeed"), 270f);
                Num(P(so, "takeoffSpeed"), 85f); Num(P(so, "takeoffDistance"), 1400f); Num(P(so, "turningRadius"), 1300f); Num(P(so, "cornerSpeed"), 170f);
                Num(P(so, "approachSpeed"), 80f); Num(P(so, "landingSpeed"), 88f); Num(P(so, "shortLandingSpeed"), 82f); Num(P(so, "minimumRadarAlt"), 80f);
                so.ApplyModifiedPropertiesWithoutUndo();
            }
            AssetDatabase.CreateAsset(par, ParPath);

            var def = Object.Instantiate(Load<ScriptableObject>(DS + "MonoBehaviour/SFB_PLACEHOLDER.asset"));
            def.name = "F117A";
            {
                var so = new SerializedObject(def);
                P(so, "jsonKey").stringValue = JsonKey;
                P(so, "unitName").stringValue = UnitName;
                P(so, "code").stringValue = Code;
                P(so, "mapIcon").objectReferenceValue = mapIcon;
                P(so, "description").stringValue = Description;
                Num(P(so, "length"), 20.08f); Num(P(so, "width"), 13.2f); Num(P(so, "height"), 3.78f);
                Num(P(so, "value"), 140f); Num(P(so, "manpower"), 1); Num(P(so, "radarSize"), RadarSize);
                P(so, "unitPrefab").objectReferenceValue = prefab;
                P(so, "spawnOffset").vector3Value = new Vector3(0f, 1.8f, 0f);
                P(so, "aircraftParameters").objectReferenceValue = par;
                Num(P(so, "mass"), Parts.Sum(p => p.mass));
                Num(P(so, "aircraftInfo.emptyWeight"), Parts.Sum(p => p.mass));
                Num(P(so, "aircraftInfo.maxWeight"), 23800f);
                Num(P(so, "aircraftInfo.maxSpeed"), 993f);
                Num(P(so, "aircraftInfo.stallSpeed"), 260f);
                Num(P(so, "aircraftInfo.maneuverability"), 7f);
                P(so, "restRotation").vector3Value = Vector3.zero;
                P(so, "disabled").boolValue = false;
                P(so, "dontAutomaticallyAddToEncyclopedia").boolValue = false;
                so.ApplyModifiedPropertiesWithoutUndo();
            }
            AssetDatabase.CreateAsset(def, DefPath);
            SetRef(prefab.GetComponent(GT("Aircraft")), "definition", def);
            PrefabUtility.SavePrefabAsset(prefab);
        }

        static List<(Color32, int)> Swatches(Texture2D tex)
        {
            var rt = RenderTexture.GetTemporary(64, 64, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            Graphics.Blit(tex, rt);
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var small = new Texture2D(64, 64, TextureFormat.RGBA32, false);
            small.ReadPixels(new Rect(0, 0, 64, 64), 0, 0);
            small.Apply();
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);
            var bins = new Dictionary<int, (Vector3 sum, int n)>();
            foreach (var c in small.GetPixels32())
            {
                int k = (c.r / 32) * 64 + (c.g / 32) * 8 + (c.b / 32);
                bins.TryGetValue(k, out var b);
                bins[k] = (b.sum + new Vector3(c.r, c.g, c.b), b.n + 1);
            }
            Object.DestroyImmediate(small);
            return bins.Values.OrderByDescending(v => v.n).Take(3).Select(b => { var m = b.sum / b.n; return (new Color32((byte)m.x, (byte)m.y, (byte)m.z, 255), b.n); }).ToList();
        }

        static void MakeOps()
        {
            var op = ScriptableObject.CreateInstance(GT("Blueprinter.OpAddAircraftToHangars"));
            var so = new SerializedObject(op);
            P(so, "aircraftJsonKey").stringValue = JsonKey;
            var targets = new[] { "hangar_med", "shelter1", "revetment1" };
            var hs = P(so, "hangars");
            hs.arraySize = targets.Length;
            for (int i = 0; i < targets.Length; i++)
            {
                var e = hs.GetArrayElementAtIndex(i);
                e.FindPropertyRelative("hangarUnitJsonKey").stringValue = targets[i];
                var n = e.FindPropertyRelative("hangarNames");
                n.arraySize = 1;
                n.GetArrayElementAtIndex(0).stringValue = targets[i];
            }
            so.ApplyModifiedPropertiesWithoutUndo();
            AssetDatabase.CreateAsset(op, Mod + "/Data/OpAddAircraftToHangars_F117A.asset");
        }

        [MenuItem("Tools/FS-45/3. Build Mod")]
        public static void BuildModMenu() => Debug.Log(BuildMod(Path.Combine(Directory.GetParent(Application.dataPath).FullName, "Builds"), Version));

        public static string BuildMod(string outputFolder, string version)
        {
            AssetDatabase.SaveAssets();
            var m = GT("Blueprinter.ModBuilder").GetMethod("Build", new[] { typeof(string), typeof(string), typeof(string), typeof(string) });
            m.Invoke(null, new object[] { JsonKey, ModTitle, version, outputFolder });
            var f = Path.Combine(outputFolder, ModTitle + "_" + version + ".nobp");
            return File.Exists(f) ? "built " + f : "build failed, see console";
        }
    }
}
#endif
