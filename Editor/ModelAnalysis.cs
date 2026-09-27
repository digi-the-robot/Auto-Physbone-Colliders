using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace Digi.AutoPhysBoneColliders
{
    public enum PartMode { Auto, Capsule, Sphere, Plane, Merge, Skip }

    public enum HumanoidPreset { Light, Standard, Detailed }

    public class RendererEntry
    {
        public Renderer renderer;
        public bool include;
        public bool skinned;
        public int vertexCount;
        public string error;
    }

    /// <summary>Vertices stored in a transform's local space, so they stay valid if the model is moved or posed.</summary>
    public class PointSource
    {
        public Transform transform;
        public readonly List<Vector3> localPoints = new List<Vector3>();
    }

    /// <summary>A bone or mesh object that owns vertices and can become one or more colliders.</summary>
    public class Part
    {
        public Transform transform;
        public string label;
        public bool isHumanBone;
        public HumanBodyBones humanBone;
        public bool isStaticMesh;
        public bool physBoneDriven;
        public Part parent;
        public readonly List<Part> children = new List<Part>();
        public int depth;
        // Capsule axis follows axisFrom -> axisTo when set (humanoid anatomy), otherwise the mesh's own principal axis.
        public Transform axisFrom;
        public Transform axisTo;
        public readonly List<PointSource> sources = new List<PointSource>();
        public int ownPointCount;
        public float ownSizeMeters;

        public PartMode mode = PartMode.Auto;
        public int split = 1;

        // Results of the most recent fit.
        public Part target;
        public int fittedPointCount;
        public readonly List<FitResult> results = new List<FitResult>();

        public bool IsCollider
        {
            get { return mode == PartMode.Auto || mode == PartMode.Capsule || mode == PartMode.Sphere || mode == PartMode.Plane; }
        }
    }

    public class ModelAnalysis
    {
        public GameObject root;
        public Animator animator;
        public bool isHumanoid;
        public readonly Dictionary<Transform, HumanBodyBones> humanBones = new Dictionary<Transform, HumanBodyBones>();
        public readonly List<RendererEntry> renderers = new List<RendererEntry>();
        public readonly List<Part> parts = new List<Part>();
        public readonly Dictionary<Transform, Part> partByTransform = new Dictionary<Transform, Part>();
        // Also includes outfit bones that were routed to an avatar bone of the same name.
        readonly Dictionary<Transform, Part> partLookup = new Dictionary<Transform, Part>();
        public readonly List<Component> physBones = new List<Component>();
        public readonly HashSet<Transform> physBoneDriven = new HashSet<Transform>();
        public readonly List<string> warnings = new List<string>();

        Transform mirrorSpace;
        float mirrorOriginX;

        // ================================================================ building

        public static ModelAnalysis Build(GameObject root, Func<Renderer, bool?> includeOverride, FitSettings settings)
        {
            var a = new ModelAnalysis();
            a.root = root;
            a.FindAnimator();
            a.FindPhysBones();
            a.FindRenderers(includeOverride);
            a.CollectPoints(settings.minBoneWeight);
            a.LinkParts();
            return a;
        }

        void FindAnimator()
        {
            animator = root.GetComponent<Animator>();
            if (animator == null || !animator.isHuman)
            {
                foreach (var anim in root.GetComponentsInChildren<Animator>(true))
                {
                    if (anim.isHuman) { animator = anim; break; }
                }
            }
            isHumanoid = animator != null && animator.isHuman && animator.avatar != null;
            if (!isHumanoid) return;

            for (int i = 0; i < (int)HumanBodyBones.LastBone; i++)
            {
                var hb = (HumanBodyBones)i;
                Transform t = null;
                try { t = animator.GetBoneTransform(hb); } catch { }
                if (t != null && !humanBones.ContainsKey(t)) humanBones.Add(t, hb);
            }

            mirrorSpace = animator.transform;
            var hips = animator.GetBoneTransform(HumanBodyBones.Hips);
            mirrorOriginX = hips != null ? mirrorSpace.InverseTransformPoint(hips.position).x : 0f;
        }

        void FindPhysBones()
        {
            var type = PhysBoneApi.PhysBoneType;
            if (type == null) return;

            foreach (var pb in root.GetComponentsInChildren(type, true))
            {
                physBones.Add(pb);
                var pbRoot = PhysBoneApi.GetPhysBoneRoot(pb);
                var ignored = new HashSet<Transform>(PhysBoneApi.GetIgnoreTransforms(pb));
                MarkDriven(pbRoot, ignored);
            }
        }

        void MarkDriven(Transform t, HashSet<Transform> ignored)
        {
            if (t == null || ignored.Contains(t)) return;
            physBoneDriven.Add(t);
            foreach (Transform child in t) MarkDriven(child, ignored);
        }

        void FindRenderers(Func<Renderer, bool?> includeOverride)
        {
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                var smr = r as SkinnedMeshRenderer;
                Mesh mesh = null;
                if (smr != null) mesh = smr.sharedMesh;
                else if (r is MeshRenderer)
                {
                    var mf = r.GetComponent<MeshFilter>();
                    if (mf != null) mesh = mf.sharedMesh;
                }
                if (mesh == null) continue;

                var entry = new RendererEntry();
                entry.renderer = r;
                entry.skinned = smr != null;
                entry.vertexCount = mesh.vertexCount;

                bool visible = r.gameObject.activeInHierarchy && r.enabled;
                // On avatars, loose mesh props (particles quads, accessories) are opt-in; skinned meshes are the body.
                entry.include = visible && (entry.skinned || !isHumanoid);

                var over = includeOverride != null ? includeOverride(r) : null;
                if (over.HasValue) entry.include = over.Value;
                renderers.Add(entry);
            }
        }

        void CollectPoints(float minBoneWeight)
        {
            var buckets = new Dictionary<Transform, PointSource>();
            var staticOwners = new HashSet<Transform>();

            foreach (var entry in renderers)
            {
                if (!entry.include) continue;
                try
                {
                    var smr = entry.renderer as SkinnedMeshRenderer;
                    if (smr != null) CollectSkinned(smr, minBoneWeight, buckets, staticOwners);
                    else
                    {
                        var mf = entry.renderer.GetComponent<MeshFilter>();
                        var verts = mf.sharedMesh.vertices;
                        AddPoints(buckets, mf.transform, verts);
                        staticOwners.Add(mf.transform);
                    }
                }
                catch (Exception e)
                {
                    entry.error = e.Message;
                    warnings.Add("Couldn't read mesh on '" + entry.renderer.name + "': " + e.Message +
                                 " (try enabling Read/Write on the model's import settings)");
                }
            }

            // Outfit armatures that aren't merged yet (Modular Avatar / VRCFury) carry their own copies of
            // the humanoid bones. Route those vertices to the matching avatar bone instead of making duplicates.
            var humanByName = new Dictionary<string, Transform>();
            Transform hips = null;
            if (isHumanoid)
            {
                hips = animator.GetBoneTransform(HumanBodyBones.Hips);
                foreach (var kv in humanBones)
                {
                    var key = NormalizeBoneName(kv.Key.name);
                    if (!humanByName.ContainsKey(key)) humanByName.Add(key, kv.Key);
                }
            }

            foreach (var kv in buckets)
            {
                var t = kv.Key;
                Transform owner = t;
                if (isHumanoid && hips != null && !humanBones.ContainsKey(t) && !t.IsChildOf(hips))
                {
                    Transform alias;
                    if (humanByName.TryGetValue(NormalizeBoneName(t.name), out alias)) owner = alias;
                }

                Part part;
                if (!partByTransform.TryGetValue(owner, out part))
                {
                    part = new Part();
                    part.transform = owner;
                    partByTransform.Add(owner, part);
                }
                part.sources.Add(kv.Value);
                partLookup[t] = part;
                partLookup[owner] = part;
                if (staticOwners.Contains(t) && owner == t && !humanBones.ContainsKey(t)) part.isStaticMesh = true;
            }
        }

        static string NormalizeBoneName(string name)
        {
            name = Regex.Replace(name, @"\.\d{3}$", "");
            return Regex.Replace(name, @"[\s_\.\-]", "").ToLowerInvariant();
        }

        static void AddPoints(Dictionary<Transform, PointSource> buckets, Transform t, Vector3[] localPoints)
        {
            var src = GetBucket(buckets, t);
            src.localPoints.AddRange(localPoints);
        }

        static PointSource GetBucket(Dictionary<Transform, PointSource> buckets, Transform t)
        {
            PointSource src;
            if (!buckets.TryGetValue(t, out src))
            {
                src = new PointSource();
                src.transform = t;
                buckets.Add(t, src);
            }
            return src;
        }

        void CollectSkinned(SkinnedMeshRenderer smr, float minBoneWeight, Dictionary<Transform, PointSource> buckets, HashSet<Transform> staticOwners)
        {
            var mesh = smr.sharedMesh;
            var verts = mesh.vertices;
            ApplyBlendShapes(smr, mesh, verts);

            var bones = smr.bones;
            var bindposes = mesh.bindposes;
            var weights = mesh.boneWeights;
            if (bones == null || bones.Length == 0 || bindposes.Length == 0 || weights.Length != verts.Length)
            {
                // No skinning: renders relative to its own transform like a static mesh.
                AddPoints(buckets, smr.transform, verts);
                staticOwners.Add(smr.transform);
                return;
            }

            for (int i = 0; i < verts.Length; i++)
            {
                var bw = weights[i];
                int index = bw.boneIndex0;
                float w = bw.weight0;
                if (bw.weight1 > w) { w = bw.weight1; index = bw.boneIndex1; }
                if (bw.weight2 > w) { w = bw.weight2; index = bw.boneIndex2; }
                if (bw.weight3 > w) { w = bw.weight3; index = bw.boneIndex3; }
                if (w < minBoneWeight || index < 0 || index >= bones.Length || index >= bindposes.Length) continue;

                var bone = bones[index];
                if (bone == null) continue;
                // bindpose maps mesh space into the bone's local space, exactly how the skinning sees it.
                GetBucket(buckets, bone).localPoints.Add(bindposes[index].MultiplyPoint3x4(verts[i]));
            }
        }

        /// <summary>Applies the renderer's current blendshape weights (shrink keys, body sliders) to the base vertices.</summary>
        static void ApplyBlendShapes(SkinnedMeshRenderer smr, Mesh mesh, Vector3[] verts)
        {
            int count = mesh.blendShapeCount;
            if (count == 0) return;

            Vector3[] hiDelta = null, loDelta = null, normals = null, tangents = null;
            for (int s = 0; s < count; s++)
            {
                float w = smr.GetBlendShapeWeight(s);
                if (Mathf.Abs(w) < 0.01f) continue;
                int frames = mesh.GetBlendShapeFrameCount(s);
                if (frames == 0) continue;

                if (hiDelta == null)
                {
                    hiDelta = new Vector3[verts.Length];
                    loDelta = new Vector3[verts.Length];
                    normals = new Vector3[verts.Length];
                    tangents = new Vector3[verts.Length];
                }

                int hi = 0;
                while (hi < frames - 1 && mesh.GetBlendShapeFrameWeight(s, hi) < w) hi++;
                float hiWeight = mesh.GetBlendShapeFrameWeight(s, hi);
                mesh.GetBlendShapeFrameVertices(s, hi, hiDelta, normals, tangents);

                if (hi == 0 || w >= hiWeight)
                {
                    float f = Mathf.Abs(hiWeight) > 1e-6f ? w / hiWeight : 0f;
                    for (int i = 0; i < verts.Length; i++) verts[i] += hiDelta[i] * f;
                }
                else
                {
                    float loWeight = mesh.GetBlendShapeFrameWeight(s, hi - 1);
                    mesh.GetBlendShapeFrameVertices(s, hi - 1, loDelta, normals, tangents);
                    float f = (w - loWeight) / Mathf.Max(1e-6f, hiWeight - loWeight);
                    for (int i = 0; i < verts.Length; i++) verts[i] += Vector3.LerpUnclamped(loDelta[i], hiDelta[i], f);
                }
            }
        }

        void LinkParts()
        {
            foreach (var part in partByTransform.Values)
            {
                HumanBodyBones hb;
                if (humanBones.TryGetValue(part.transform, out hb))
                {
                    part.isHumanBone = true;
                    part.humanBone = hb;
                    part.label = ObjectNames.NicifyVariableName(hb.ToString());
                    AssignAxisHint(part, hb);
                }
                else part.label = part.transform.name;

                part.physBoneDriven = physBoneDriven.Contains(part.transform);

                int count = 0;
                var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
                var max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
                foreach (var src in part.sources)
                {
                    count += src.localPoints.Count;
                    var m = src.transform.localToWorldMatrix;
                    int step = Mathf.Max(1, src.localPoints.Count / 2000);
                    for (int i = 0; i < src.localPoints.Count; i += step)
                    {
                        var p = m.MultiplyPoint3x4(src.localPoints[i]);
                        min = Vector3.Min(min, p);
                        max = Vector3.Max(max, p);
                    }
                }
                part.ownPointCount = count;
                var size = max - min;
                part.ownSizeMeters = count > 0 ? Mathf.Max(size.x, Mathf.Max(size.y, size.z)) : 0f;

                var t = part.transform.parent;
                while (t != null && part.parent == null)
                {
                    Part p;
                    if (partLookup.TryGetValue(t, out p) && p != part) part.parent = p;
                    if (t == root.transform) break;
                    t = t.parent;
                }
                if (part.parent != null) part.parent.children.Add(part);
            }

            // Hierarchy order for display.
            AddInHierarchyOrder(root.transform, 0);
        }

        void AddInHierarchyOrder(Transform t, int depth)
        {
            Part part;
            if (partByTransform.TryGetValue(t, out part))
            {
                part.depth = depth;
                parts.Add(part);
                depth++;
            }
            foreach (Transform child in t) AddInHierarchyOrder(child, depth);
        }

        Transform Bone(HumanBodyBones hb)
        {
            try { return animator.GetBoneTransform(hb); } catch { return null; }
        }

        Transform FirstBone(params HumanBodyBones[] candidates)
        {
            foreach (var hb in candidates)
            {
                var t = Bone(hb);
                if (t != null) return t;
            }
            return null;
        }

        void AssignAxisHint(Part part, HumanBodyBones hb)
        {
            Transform from = part.transform, to = null;
            switch (hb)
            {
                // Torsos are wider than they are deep, but not always by enough for PCA to notice (big jackets,
                // chests, long hair weighted to the spine). Run them left-right along the hip or shoulder line.
                case HumanBodyBones.Hips:
                case HumanBodyBones.Spine:
                    from = Bone(HumanBodyBones.LeftUpperLeg); to = Bone(HumanBodyBones.RightUpperLeg); break;
                case HumanBodyBones.Chest:
                case HumanBodyBones.UpperChest:
                    from = Bone(HumanBodyBones.LeftUpperArm); to = Bone(HumanBodyBones.RightUpperArm); break;
                // Heads run along the neck so hair hanging down the back can't tip them over.
                case HumanBodyBones.Head:
                    from = FirstBone(HumanBodyBones.Neck, HumanBodyBones.UpperChest, HumanBodyBones.Chest); to = part.transform; break;
                case HumanBodyBones.Neck: to = Bone(HumanBodyBones.Head); break;
                case HumanBodyBones.LeftShoulder: to = Bone(HumanBodyBones.LeftUpperArm); break;
                case HumanBodyBones.RightShoulder: to = Bone(HumanBodyBones.RightUpperArm); break;
                case HumanBodyBones.LeftUpperArm: to = Bone(HumanBodyBones.LeftLowerArm); break;
                case HumanBodyBones.RightUpperArm: to = Bone(HumanBodyBones.RightLowerArm); break;
                case HumanBodyBones.LeftLowerArm: to = Bone(HumanBodyBones.LeftHand); break;
                case HumanBodyBones.RightLowerArm: to = Bone(HumanBodyBones.RightHand); break;
                case HumanBodyBones.LeftHand: to = Bone(HumanBodyBones.LeftMiddleProximal); break;
                case HumanBodyBones.RightHand: to = Bone(HumanBodyBones.RightMiddleProximal); break;
                case HumanBodyBones.LeftUpperLeg: to = Bone(HumanBodyBones.LeftLowerLeg); break;
                case HumanBodyBones.RightUpperLeg: to = Bone(HumanBodyBones.RightLowerLeg); break;
                case HumanBodyBones.LeftLowerLeg: to = Bone(HumanBodyBones.LeftFoot); break;
                case HumanBodyBones.RightLowerLeg: to = Bone(HumanBodyBones.RightFoot); break;
                case HumanBodyBones.LeftFoot: to = Bone(HumanBodyBones.LeftToes); break;
                case HumanBodyBones.RightFoot: to = Bone(HumanBodyBones.RightToes); break;
                default:
                    // Finger segments point at the next segment.
                    var name = hb.ToString();
                    if (name.EndsWith("Proximal") || name.EndsWith("Intermediate")) to = Bone((HumanBodyBones)((int)hb + 1));
                    break;
            }
            if (from != null && to != null && from != to)
            {
                part.axisFrom = from;
                part.axisTo = to;
            }
        }

        // ================================================================ defaults

        public void ApplyDefaults(HumanoidPreset preset, FitSettings s)
        {
            bool hasChest = HasPart(HumanBodyBones.Chest);
            bool hasHead = HasPart(HumanBodyBones.Head);

            foreach (var part in parts)
            {
                part.split = 1;

                if (part.physBoneDriven && !part.isHumanBone)
                {
                    part.mode = PartMode.Skip;
                    continue;
                }

                if (isHumanoid)
                {
                    if (part.isHumanBone) part.mode = HumanDefault(part.humanBone, preset, hasChest, hasHead);
                    else if (part.parent == null) part.mode = PartMode.Skip;
                    else if (part.isStaticMesh) part.mode = PartMode.Merge;
                    // Unmapped chains (tails, ears, hair without PhysBones) would bloat whatever they merge into.
                    else part.mode = ChainDepth(part) >= 2 ? PartMode.Skip : PartMode.Merge;
                }
                else
                {
                    bool small = part.ownPointCount < s.minVertices || part.ownSizeMeters * 100f < s.minSizeCm;
                    part.mode = part.parent != null && small ? PartMode.Merge : PartMode.Auto;
                }
            }
        }

        bool HasPart(HumanBodyBones hb)
        {
            foreach (var kv in humanBones)
                if (kv.Value == hb) return partByTransform.ContainsKey(kv.Key);
            return false;
        }

        static int ChainDepth(Part part)
        {
            int best = 0;
            foreach (var c in part.children) best = Math.Max(best, 1 + ChainDepth(c));
            return best;
        }

        static PartMode HumanDefault(HumanBodyBones hb, HumanoidPreset preset, bool hasChest, bool hasHead)
        {
            bool light = preset == HumanoidPreset.Light;
            bool detailed = preset == HumanoidPreset.Detailed;
            switch (hb)
            {
                case HumanBodyBones.Hips:
                case HumanBodyBones.Chest:
                case HumanBodyBones.Head:
                case HumanBodyBones.LeftUpperArm:
                case HumanBodyBones.RightUpperArm:
                case HumanBodyBones.LeftUpperLeg:
                case HumanBodyBones.RightUpperLeg:
                    return PartMode.Auto;

                case HumanBodyBones.Spine:
                    return light && hasChest ? PartMode.Merge : PartMode.Auto;
                case HumanBodyBones.UpperChest:
                    return detailed || !hasChest ? PartMode.Auto : PartMode.Merge;
                case HumanBodyBones.Neck:
                    return light && hasHead ? PartMode.Merge : PartMode.Auto;

                case HumanBodyBones.LeftLowerArm:
                case HumanBodyBones.RightLowerArm:
                case HumanBodyBones.LeftLowerLeg:
                case HumanBodyBones.RightLowerLeg:
                    return light ? PartMode.Merge : PartMode.Auto;

                case HumanBodyBones.LeftHand:
                case HumanBodyBones.RightHand:
                    return light ? PartMode.Skip : detailed ? PartMode.Auto : PartMode.Merge;

                case HumanBodyBones.LeftFoot:
                case HumanBodyBones.RightFoot:
                    return detailed ? PartMode.Auto : PartMode.Skip;

                default:
                    // Shoulders, fingers, toes, eyes, jaw fold into their parent.
                    return PartMode.Merge;
            }
        }

        // ================================================================ fitting

        public void Refit(FitSettings s)
        {
            foreach (var part in parts)
            {
                part.target = Resolve(part);
                part.results.Clear();
                part.fittedPointCount = 0;
            }

            // Count first so big meshes can be evenly thinned to maxPointsPerFit.
            var totals = new Dictionary<Part, int>();
            foreach (var part in parts)
            {
                if (part.target == null) continue;
                int c;
                totals.TryGetValue(part.target, out c);
                totals[part.target] = c + part.ownPointCount;
            }

            var gathered = new Dictionary<Part, List<Vector3>>();
            var counters = new Dictionary<Part, int>();
            foreach (var part in parts)
            {
                var target = part.target;
                if (target == null) continue;
                int total = totals[target];
                int stride = Mathf.Max(1, Mathf.CeilToInt(total / (float)Mathf.Max(1000, s.maxPointsPerFit)));

                List<Vector3> list;
                if (!gathered.TryGetValue(target, out list))
                {
                    list = new List<Vector3>(Mathf.Min(total, s.maxPointsPerFit + 16));
                    gathered.Add(target, list);
                    counters.Add(target, 0);
                }
                int counter = counters[target];
                foreach (var src in part.sources)
                {
                    var m = src.transform.localToWorldMatrix;
                    var pts = src.localPoints;
                    for (int i = 0; i < pts.Count; i++, counter++)
                    {
                        if (counter % stride == 0) list.Add(m.MultiplyPoint3x4(pts[i]));
                    }
                }
                counters[target] = counter;
            }

            foreach (var kv in gathered)
            {
                var target = kv.Key;
                ColliderShape? forced = null;
                if (target.mode == PartMode.Capsule) forced = ColliderShape.Capsule;
                else if (target.mode == PartMode.Sphere) forced = ColliderShape.Sphere;
                else if (target.mode == PartMode.Plane) forced = ColliderShape.Plane;

                Vector3? hint = null;
                if (target.axisFrom != null && target.axisTo != null)
                {
                    var d = target.axisTo.position - target.axisFrom.position;
                    if (d.sqrMagnitude > 1e-8f) hint = d;
                }

                ShapeFitter.Fit(kv.Value, Mathf.Clamp(target.split, 1, 16), forced, hint, s, target.results);
                target.fittedPointCount = totals[target];
            }

            if (s.symmetrize && isHumanoid) Symmetrize();
        }

        static Part Resolve(Part part)
        {
            var cur = part;
            while (cur != null)
            {
                if (cur.IsCollider) return cur;
                if (cur.mode == PartMode.Skip) return null;
                cur = cur.parent;
            }
            return null;
        }

        void Symmetrize()
        {
            var byHuman = new Dictionary<HumanBodyBones, Part>();
            foreach (var p in parts) if (p.isHumanBone) byHuman[p.humanBone] = p;

            foreach (var left in parts)
            {
                if (!left.isHumanBone) continue;
                var name = left.humanBone.ToString();
                if (!name.StartsWith("Left")) continue;

                HumanBodyBones rightBone;
                try { rightBone = (HumanBodyBones)Enum.Parse(typeof(HumanBodyBones), "Right" + name.Substring(4)); }
                catch { continue; }

                Part right;
                if (!byHuman.TryGetValue(rightBone, out right)) continue;
                if (!left.IsCollider || !right.IsCollider) continue;
                if (left.results.Count != 1 || right.results.Count != 1) continue;

                var a = left.results[0];
                var b = right.results[0];
                if (a.shape != b.shape || a.shape == ColliderShape.Plane) continue;

                var bCenter = MirrorPoint(b.center);
                var bAxis = MirrorDirection(b.axis);
                if (Vector3.Dot(bAxis, a.axis) < 0f) bAxis = -bAxis;

                var avg = a;
                avg.center = (a.center + bCenter) * 0.5f;
                avg.axis = (a.axis + bAxis).normalized;
                if (avg.axis.sqrMagnitude < 0.5f) avg.axis = a.axis;
                avg.radius = (a.radius + b.radius) * 0.5f;
                avg.height = (a.height + b.height) * 0.5f;

                var mirrored = avg;
                mirrored.center = MirrorPoint(avg.center);
                mirrored.axis = MirrorDirection(avg.axis);

                left.results[0] = avg;
                right.results[0] = mirrored;
            }
        }

        Vector3 MirrorPoint(Vector3 world)
        {
            var l = mirrorSpace.InverseTransformPoint(world);
            l.x = 2f * mirrorOriginX - l.x;
            return mirrorSpace.TransformPoint(l);
        }

        Vector3 MirrorDirection(Vector3 world)
        {
            var l = mirrorSpace.InverseTransformDirection(world);
            l.x = -l.x;
            return mirrorSpace.TransformDirection(l).normalized;
        }

        public int ColliderCount
        {
            get
            {
                int n = 0;
                foreach (var p in parts) if (p.IsCollider) n += p.results.Count;
                return n;
            }
        }
    }
}
