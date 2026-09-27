using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace Digi.AutoPhysBoneColliders
{
    /// <summary>
    /// Talks to VRChat's PhysBone components purely through reflection and SerializedObject.
    /// There is no compile-time reference to the VRChat SDK, so this tool compiles in any project
    /// and works with every SDK version that ships PhysBones (3.0.x through current).
    /// </summary>
    public static class PhysBoneApi
    {
        const string ColliderTypeName = "VRC.SDK3.Dynamics.PhysBone.Components.VRCPhysBoneCollider";
        const string PhysBoneTypeName = "VRC.SDK3.Dynamics.PhysBone.Components.VRCPhysBone";

        static bool searched;
        static Type colliderType;
        static Type physBoneType;
        static string[] shapeNames;

        public static Type ColliderType { get { Search(); return colliderType; } }
        public static Type PhysBoneType { get { Search(); return physBoneType; } }
        public static bool Available { get { return ColliderType != null; } }

        static void Search()
        {
            if (searched) return;
            searched = true;

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (colliderType == null) colliderType = asm.GetType(ColliderTypeName, false);
                if (physBoneType == null) physBoneType = asm.GetType(PhysBoneTypeName, false);
            }

            // Fallback in case a future SDK moves the namespace but keeps the class names.
            if (colliderType == null || physBoneType == null)
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type[] types;
                    try { types = asm.GetTypes(); }
                    catch (ReflectionTypeLoadException e) { types = e.Types; }
                    catch { continue; }

                    foreach (var t in types)
                    {
                        if (t == null || t.IsAbstract || !typeof(Component).IsAssignableFrom(t)) continue;
                        if (colliderType == null && t.Name == "VRCPhysBoneCollider") colliderType = t;
                        if (physBoneType == null && t.Name == "VRCPhysBone") physBoneType = t;
                    }
                }
            }

            if (colliderType != null)
            {
                var field = colliderType.GetField("shapeType", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (field != null && field.FieldType.IsEnum) shapeNames = Enum.GetNames(field.FieldType);
            }
        }

        public static bool SupportsShape(ColliderShape shape)
        {
            Search();
            if (shapeNames == null) return shape != ColliderShape.Plane;
            return Array.IndexOf(shapeNames, shape.ToString()) >= 0;
        }

        public static string SdkDescription
        {
            get
            {
                if (!Available) return "not found";
                return ColliderType.FullName + " (" + ColliderType.Assembly.GetName().Name + ")";
            }
        }

        // ---------------------------------------------------------------- colliders

        /// <summary>
        /// VRChat scales a collider's radius, height and position offset by the largest axis of the
        /// transform's lossy scale, and rotates it by the transform's world rotation.
        /// </summary>
        public static float VrcScale(Transform t)
        {
            var ls = t.lossyScale;
            float s = Mathf.Max(ls.x, Mathf.Max(ls.y, ls.z));
            if (s <= 1e-6f) s = Mathf.Max(Mathf.Abs(ls.x), Mathf.Max(Mathf.Abs(ls.y), Mathf.Abs(ls.z)));
            return s <= 1e-6f ? 1f : s;
        }

        public static void ToColliderSpace(FitResult r, Transform attachTo, out Vector3 position, out Quaternion rotation, out float radius, out float height)
        {
            float s = VrcScale(attachTo);
            var inv = Quaternion.Inverse(attachTo.rotation);
            position = inv * (r.center - attachTo.position) / s;
            rotation = Quaternion.FromToRotation(Vector3.up, inv * r.axis);
            radius = r.radius / s;
            height = r.height / s;
        }

        public static Component CreateCollider(GameObject host, Transform attachTo, FitResult r)
        {
            Vector3 position;
            Quaternion rotation;
            float radius, height;
            ToColliderSpace(r, attachTo, out position, out rotation, out radius, out height);

            var collider = Undo.AddComponent(host, ColliderType);
            var so = new SerializedObject(collider);
            SetObject(so, "rootTransform", attachTo);
            if (!SetEnum(so, "shapeType", r.shape.ToString())) SetEnum(so, "shapeType", ColliderShape.Sphere.ToString());
            SetFloat(so, "radius", radius);
            SetFloat(so, "height", height);
            SetVector3(so, "position", position);
            SetQuaternion(so, "rotation", rotation);
            SetBool(so, "insideBounds", false);
            so.ApplyModifiedPropertiesWithoutUndo();
            return collider;
        }

        public static Transform GetColliderRoot(Component collider)
        {
            var so = new SerializedObject(collider);
            var p = so.FindProperty("rootTransform");
            var t = p != null ? p.objectReferenceValue as Transform : null;
            return t != null ? t : collider.transform;
        }

        // ---------------------------------------------------------------- physbones

        public static Transform GetPhysBoneRoot(Component physBone)
        {
            var so = new SerializedObject(physBone);
            var p = so.FindProperty("rootTransform");
            var t = p != null ? p.objectReferenceValue as Transform : null;
            return t != null ? t : physBone.transform;
        }

        public static List<Transform> GetIgnoreTransforms(Component physBone)
        {
            var list = new List<Transform>();
            var so = new SerializedObject(physBone);
            var p = so.FindProperty("ignoreTransforms");
            if (p == null || !p.isArray) return list;
            for (int i = 0; i < p.arraySize; i++)
            {
                var t = p.GetArrayElementAtIndex(i).objectReferenceValue as Transform;
                if (t != null) list.Add(t);
            }
            return list;
        }

        public static List<Component> GetColliders(Component physBone)
        {
            var list = new List<Component>();
            var so = new SerializedObject(physBone);
            var p = so.FindProperty("colliders");
            if (p == null || !p.isArray) return list;
            for (int i = 0; i < p.arraySize; i++)
                list.Add(p.GetArrayElementAtIndex(i).objectReferenceValue as Component);
            return list;
        }

        /// <summary>Removes collider entries matching the predicate (and null entries if requested). Undoable.</summary>
        public static int RemoveColliders(Component physBone, Predicate<Component> match, bool alsoRemoveMissing)
        {
            var so = new SerializedObject(physBone);
            var p = so.FindProperty("colliders");
            if (p == null || !p.isArray) return 0;
            int removed = 0;
            for (int i = p.arraySize - 1; i >= 0; i--)
            {
                var element = p.GetArrayElementAtIndex(i);
                var c = element.objectReferenceValue as Component;
                if ((c == null && alsoRemoveMissing) || (c != null && match(c)))
                {
                    element.objectReferenceValue = null;
                    p.DeleteArrayElementAtIndex(i);
                    removed++;
                }
            }
            if (removed > 0) so.ApplyModifiedProperties();
            return removed;
        }

        /// <summary>Appends colliders to a PhysBone's collider list, skipping ones already present. Undoable.</summary>
        public static int AddColliders(Component physBone, IList<Component> colliders)
        {
            var so = new SerializedObject(physBone);
            var p = so.FindProperty("colliders");
            if (p == null || !p.isArray) return 0;

            var existing = new HashSet<Component>();
            for (int i = 0; i < p.arraySize; i++)
            {
                var c = p.GetArrayElementAtIndex(i).objectReferenceValue as Component;
                if (c != null) existing.Add(c);
            }

            int added = 0;
            foreach (var c in colliders)
            {
                if (c == null || existing.Contains(c)) continue;
                p.arraySize++;
                p.GetArrayElementAtIndex(p.arraySize - 1).objectReferenceValue = c;
                existing.Add(c);
                added++;
            }
            if (added > 0) so.ApplyModifiedProperties();
            return added;
        }

        // ---------------------------------------------------------------- serialized helpers

        static void SetObject(SerializedObject so, string name, UnityEngine.Object value)
        {
            var p = so.FindProperty(name);
            if (p != null && p.propertyType == SerializedPropertyType.ObjectReference) p.objectReferenceValue = value;
        }

        static bool SetEnum(SerializedObject so, string name, string value)
        {
            var p = so.FindProperty(name);
            if (p == null || p.propertyType != SerializedPropertyType.Enum) return false;
            int index = Array.IndexOf(p.enumNames, value);
            if (index < 0) return false;
            p.enumValueIndex = index;
            return true;
        }

        static void SetFloat(SerializedObject so, string name, float value)
        {
            var p = so.FindProperty(name);
            if (p != null && p.propertyType == SerializedPropertyType.Float) p.floatValue = value;
        }

        static void SetBool(SerializedObject so, string name, bool value)
        {
            var p = so.FindProperty(name);
            if (p != null && p.propertyType == SerializedPropertyType.Boolean) p.boolValue = value;
        }

        static void SetVector3(SerializedObject so, string name, Vector3 value)
        {
            var p = so.FindProperty(name);
            if (p != null && p.propertyType == SerializedPropertyType.Vector3) p.vector3Value = value;
        }

        static void SetQuaternion(SerializedObject so, string name, Quaternion value)
        {
            var p = so.FindProperty(name);
            if (p != null && p.propertyType == SerializedPropertyType.Quaternion) p.quaternionValue = value;
        }
    }
}
