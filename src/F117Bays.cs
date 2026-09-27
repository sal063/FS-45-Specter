#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace F117Tools
{
    public static class F117Bays
    {
        const string Stock = "Assets/Blueprinter/_donotship/MonoBehaviour/";
        const string MountDir = F117Setup.Mod + "/Weapons";

        static Type Native(string name) => AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(name)).First(t => t != null);
        static Component ComponentNamed(GameObject go, string type) => go.GetComponentsInChildren<Component>(true).First(c => c != null && c.GetType().Name == type);
        static Transform Find(GameObject go, string name) => go.GetComponentsInChildren<Transform>(true).Single(t => t.name == name);

        static void Number(Component c, string field, float value)
        {
            var so = new SerializedObject(c);
            so.FindProperty(field).floatValue = value;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        static Bounds BoundsOf(GameObject go)
        {
            var rs = go.GetComponentsInChildren<MeshRenderer>(true);
            var b = rs[0].bounds;
            foreach (var r in rs.Skip(1)) b.Encapsulate(r.bounds);
            return b;
        }

        public static Object GetMount(string key)
        {
            if (key.StartsWith("F117_")) key = key.Substring(5);
            if (!AssetDatabase.IsValidFolder(MountDir)) AssetDatabase.CreateFolder(F117Setup.Mod, "Weapons");
            var path = MountDir + "/F117_" + key + ".asset";
            var existing = AssetDatabase.LoadAssetAtPath<Object>(path);
            if (existing != null) return existing;

            var stock = AssetDatabase.LoadAssetAtPath<Object>(Stock + key + "_PLACEHOLDER.asset");
            var data = Object.Instantiate(stock);
            data.name = key;
            var so = new SerializedObject(data);
            var go = Object.Instantiate((GameObject)so.FindProperty("prefab").objectReferenceValue);
            go.name = "F117_" + key;
            try
            {
                go.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                var b = BoundsOf(go);
                var offset = new Vector3(-b.center.x, -b.max.y - .04f, -b.center.z);
                foreach (Transform child in go.transform) child.localPosition += offset;
                foreach (var c in go.GetComponentsInChildren<Component>(true).Where(c => c != null && c.GetType().Name == "MountedMissile"))
                    Number(c, "railDelay", .8f);
                var prefab = PrefabUtility.SaveAsPrefabAsset(go, MountDir + "/F117_" + key + ".prefab");
                so.FindProperty("prefab").objectReferenceValue = prefab;
                so.FindProperty("jsonKey").stringValue = "F117_" + key;
                so.ApplyModifiedPropertiesWithoutUndo();
                AssetDatabase.CreateAsset(data, path);
                data.name = key;
                return data;
            }
            finally { Object.DestroyImmediate(go); }
        }

        static Component Actuator(Transform t, float angle)
        {
            var c = t.GetComponent(Native("BayDoor")) ?? t.gameObject.AddComponent(Native("BayDoor"));
            Number(c, "hingeAngle", angle);
            Number(c, "openSpeed", 1.5f);
            Number(c, "closeSpeed", 4f);
            return c;
        }

        static Transform Axis(Transform parent, string name, Vector3 worldPosition)
        {
            var t = new GameObject(name).transform;
            t.SetParent(parent, false);
            t.SetPositionAndRotation(worldPosition, Quaternion.Euler(0, 90, 0));
            return t;
        }

        public static void Configure(GameObject root)
        {
            var wm = ComponentNamed(root, "WeaponManager");
            var so = new SerializedObject(wm);
            var set = so.FindProperty("hardpointSets").GetArrayElementAtIndex(0);
            var points = set.FindPropertyRelative("hardpoints");
            for (int i = 0; i < 2; i++)
            {
                var side = i == 0 ? "L" : "R";
                var beam = Find(root, "F117_trapeze_" + side + "_beam");
                var hp = Find(root, "HP_bay_" + side);
                hp.localPosition = new Vector3(0, -.07f, 0);
                hp.rotation = Quaternion.identity;
                var door = Find(root, "HINGE_bay_door_" + side).GetComponent(Native("BayDoor"));
                Number(door, "openSpeed", 6);
                Number(door, "closeSpeed", 1);

                var drive = Axis(beam.parent, "F117_carrier_drive_" + side, beam.position + Vector3.forward * .85f);
                var counter = Axis(drive, "F117_carrier_level_" + side, beam.position);
                beam.SetParent(counter, true);
                var list = new List<Component> { door, Actuator(drive, -70), Actuator(counter, 70) };
                foreach (var end in new[] { "fwd", "aft" })
                {
                    var hinge = Find(root, "HINGE_trapeze_" + side + "_" + end);
                    var axis = Axis(hinge.parent, "F117_carrier_arm_" + side + "_" + end, hinge.position);
                    hinge.SetParent(axis, true);
                    list.Add(Actuator(axis, -70));
                }

                var point = points.GetArrayElementAtIndex(i);
                var doors = point.FindPropertyRelative("bayDoors");
                doors.arraySize = list.Count;
                for (int j = 0; j < list.Count; j++) doors.GetArrayElementAtIndex(j).objectReferenceValue = list[j];
                point.FindPropertyRelative("doorOpenDuration").floatValue = 2.6f;
            }
            so.ApplyModifiedPropertiesWithoutUndo();
        }
    }
}
#endif
