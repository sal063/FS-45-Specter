#if UNITY_EDITOR
using System;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace F117Tools
{
    public static class F117IrmBays
    {
        const string StockMount = "Assets/Blueprinter/_donotship/MonoBehaviour/IRMS1_single_PLACEHOLDER.asset";
        const string MountPath = F117Setup.Mod + "/Weapons/F117_IRMS1_internal.asset";
        const string MountPrefabPath = F117Setup.Mod + "/Weapons/F117_IRMS1_internal.prefab";
        const string SetName = "IRM Bays";
        const float HingeAngle = 95f, DoorOpenSpeed = 4f, DoorCloseSpeed = 2f, DoorOpenDuration = 1.2f;
        const float RailLength = 1f, RailSpeed = 4f, RailDelay = 0.3f;
        const int RailDown = 1;

        static Type GT(string name) => AppDomain.CurrentDomain.GetAssemblies().Select(a => { try { return a.GetType(name); } catch { return null; } }).FirstOrDefault(t => t != null);
        static Component Comp(GameObject go, string type) => go.GetComponentsInChildren<Component>(true).FirstOrDefault(c => c != null && c.GetType().Name == type);
        static Transform Find(GameObject root, string name) => root.GetComponentsInChildren<Transform>(true).First(t => t.name == name);

        public static Object Mount()
        {
            var existing = AssetDatabase.LoadAssetAtPath<Object>(MountPath);
            if (existing != null) return existing;

            var stock = AssetDatabase.LoadAssetAtPath<Object>(StockMount);
            var source = (GameObject)new SerializedObject(stock).FindProperty("prefab").objectReferenceValue;
            var go = Object.Instantiate(source);
            go.name = "F117_IRMS1_internal";
            try
            {
                go.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                var mm = go.GetComponentsInChildren<Component>(true).Single(c => c != null && c.GetType().Name == "MountedMissile");
                var missile = mm.transform;
                missile.SetParent(go.transform, true);
                foreach (var c in go.transform.Cast<Transform>().ToList()) if (c != missile) Object.DestroyImmediate(c.gameObject);
                foreach (var c in go.GetComponents<Component>().Where(c => c is Renderer || c is Collider)) Object.DestroyImmediate(c);
                foreach (var c in go.GetComponents<MeshFilter>()) Object.DestroyImmediate(c);
                missile.localRotation = Quaternion.identity;
                missile.localPosition = Vector3.zero;
                var b = missile.GetComponent<Renderer>().bounds;
                missile.localPosition = new Vector3(-b.center.x, -b.max.y - 0.002f, -b.center.z);
                var so = new SerializedObject(mm);
                so.FindProperty("railDirection").intValue = RailDown;
                so.FindProperty("railLength").floatValue = RailLength;
                so.FindProperty("railSpeed").floatValue = RailSpeed;
                so.FindProperty("railDelay").floatValue = RailDelay;
                so.ApplyModifiedPropertiesWithoutUndo();
                var prefab = PrefabUtility.SaveAsPrefabAsset(go, MountPrefabPath);

                var data = Object.Instantiate(stock);
                data.name = "IRMS1_internal";
                var ds = new SerializedObject(data);
                ds.FindProperty("prefab").objectReferenceValue = prefab;
                ds.FindProperty("jsonKey").stringValue = "F117_IRMS1_internal";
                ds.FindProperty("mountName").stringValue = "IRM-S1 (IRM bay)";
                ds.FindProperty("missileBay").boolValue = true;
                foreach (var f in new[] { "emptyMass", "drag", "emptyDrag", "RCS", "emptyRCS" })
                {
                    var p = ds.FindProperty(f);
                    if (p.propertyType == SerializedPropertyType.Integer) p.intValue = 0; else p.floatValue = 0f;
                }
                ds.ApplyModifiedPropertiesWithoutUndo();
                AssetDatabase.CreateAsset(data, MountPath);
                data.name = "IRMS1_internal";
                return data;
            }
            finally { Object.DestroyImmediate(go); }
        }

        public static void Configure(GameObject root, Component centrePart)
        {
            var mount = Mount();
            var bayDoorT = GT("BayDoor");
            var tmplHinge = Find(root, "HINGE_bay_door_L");
            var tmplBD = tmplHinge.GetComponent(bayDoorT);
            var tmplAudio = tmplHinge.GetComponent<AudioSource>();

            var wso = new SerializedObject(Comp(root, "WeaponManager"));
            var sets = wso.FindProperty("hardpointSets");
            sets.arraySize = 2;
            var s = sets.GetArrayElementAtIndex(1);
            s.FindPropertyRelative("name").stringValue = SetName;
            s.FindPropertyRelative("precludingHardpointSets").arraySize = 0;
            s.FindPropertyRelative("SymmetryWithPrev").boolValue = false;
            s.FindPropertyRelative("SymmetryName").stringValue = "";
            var opts = s.FindPropertyRelative("weaponOptions");
            opts.arraySize = 2;
            opts.GetArrayElementAtIndex(0).objectReferenceValue = null;
            opts.GetArrayElementAtIndex(1).objectReferenceValue = mount;
            var runtime = s.FindPropertyRelative("weaponMount");
            if (runtime != null) runtime.objectReferenceValue = null;
            var hps = s.FindPropertyRelative("hardpoints");
            hps.arraySize = 2;

            for (int i = 0; i < 2; i++)
            {
                var side = i == 0 ? "L" : "R";
                var h = Find(root, "HINGE_irm_door_" + side).gameObject;
                var audio = h.AddComponent<AudioSource>();
                EditorUtility.CopySerialized(tmplAudio, audio);
                var bd = h.AddComponent(bayDoorT);
                EditorUtility.CopySerialized(tmplBD, bd);
                var so = new SerializedObject(bd);
                so.FindProperty("hingeAngle").floatValue = side == "L" ? HingeAngle : -HingeAngle;
                so.FindProperty("openSpeed").floatValue = DoorOpenSpeed;
                so.FindProperty("closeSpeed").floatValue = DoorCloseSpeed;
                so.FindProperty("doorAudioSource").objectReferenceValue = audio;
                so.ApplyModifiedPropertiesWithoutUndo();

                var hp = Find(root, "HP_irm_" + side);
                hp.rotation = Quaternion.identity;
                var e = hps.GetArrayElementAtIndex(i);
                e.FindPropertyRelative("transform").objectReferenceValue = hp;
                e.FindPropertyRelative("part").objectReferenceValue = centrePart;
                var bdl = e.FindPropertyRelative("bayDoors");
                bdl.arraySize = 1;
                bdl.GetArrayElementAtIndex(0).objectReferenceValue = bd;
                e.FindPropertyRelative("doorOpenDuration").floatValue = DoorOpenDuration;
                e.FindPropertyRelative("pylonOptions").arraySize = 0;
                e.FindPropertyRelative("Pylon").objectReferenceValue = null;
                e.FindPropertyRelative("Plug").objectReferenceValue = null;
                e.FindPropertyRelative("BuiltInWeapons").arraySize = 0;
                e.FindPropertyRelative("BuiltInTurrets").arraySize = 0;
                e.FindPropertyRelative("HardpointIndex").intValue = side == "L" ? 2 : 1;
            }
            wso.ApplyModifiedPropertiesWithoutUndo();
        }
    }
}
#endif
