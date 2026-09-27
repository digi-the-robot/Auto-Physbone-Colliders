using System;
using System.Collections.Generic;
using UnityEngine;

namespace Digi.AutoPhysBoneColliders
{
    public enum ColliderShape { Sphere, Capsule, Plane }

    /// <summary>A fitted shape in world space, world units. Converted to collider space only when created.</summary>
    [Serializable]
    public struct FitResult
    {
        public ColliderShape shape;
        public Vector3 center;
        public Vector3 axis;   // capsule axis, or plane normal
        public float radius;
        public float height;   // capsule end-to-end length including the caps (VRChat's convention)
    }

    [Serializable]
    public class FitSettings
    {
        [Tooltip("Which percentile of vertex distances becomes the radius. Lower sits inside the surface, higher wraps around it.")]
        public float tightness = 0.65f;
        [Tooltip("Multiplier applied to every fitted radius.")]
        public float radiusScale = 1f;
        [Tooltip("Auto mode picks a capsule when length / diameter is at least this, otherwise a sphere.")]
        public float capsuleThreshold = 1.3f;
        [Tooltip("A vertex only counts toward its strongest bone if that weight is at least this. Drops blurry joint vertices.")]
        public float minBoneWeight = 0.4f;
        [Tooltip("Fraction of vertices ignored at each end of every axis, so stray vertices don't stretch the shape.")]
        public float outlierTrim = 0.02f;
        [Tooltip("Average left/right humanoid pairs so both sides match.")]
        public bool symmetrize = true;
        [Tooltip("Parts with fewer vertices than this merge into their parent by default (non-humanoid rigs).")]
        public int minVertices = 40;
        [Tooltip("Parts smaller than this (cm) merge into their parent by default (non-humanoid rigs).")]
        public float minSizeCm = 3f;
        public int maxPointsPerFit = 30000;
    }

    public static class ShapeFitter
    {
        public const int MinPoints = 8;

        public const int VoxelResolution = 48;

        public static void Fit(List<Vector3> points, int split, ColliderShape? forced, Vector3? axisHint, FitSettings s, List<FitResult> output)
        {
            output.Clear();
            if (points.Count < MinPoints) return;

            // Even out vertex density so detailed areas (faces, mouths, eyes) don't outvote simple ones.
            points = VoxelResample(points, VoxelResolution);
            if (points.Count < MinPoints) return;

            FitResult r;
            if (split <= 1)
            {
                if (FitCluster(points, forced, axisHint, false, s, out r)) output.Add(r);
                return;
            }

            // Splitting a surface would hand each shape a curved patch of skin. Split the enclosed volume instead,
            // so every piece is a solid chunk, then size each chunk from its extents.
            var solid = SolidVoxels(points, VoxelResolution);
            foreach (var cluster in KMeans(solid, split))
            {
                if (cluster.Count >= MinPoints && FitCluster(cluster, forced, axisHint, true, s, out r)) output.Add(r);
            }
        }

        static bool FitCluster(List<Vector3> pts, ColliderShape? forced, Vector3? axisHint, bool solid, FitSettings s, out FitResult result)
        {
            result = default(FitResult);
            int n = pts.Count;
            float trim = Mathf.Clamp(s.outlierTrim, 0f, 0.2f);

            Vector3 mean, e1, e2, e3;
            Pca(pts, out mean, out e1, out e2, out e3);

            if (forced.HasValue && forced.Value == ColliderShape.Plane)
            {
                // Plane normal is the direction of least variance; sit the plane on the "top" surface.
                var normal = e3;
                if (Vector3.Dot(normal, Vector3.up) < 0f) normal = -normal;
                var d = new float[n];
                for (int i = 0; i < n; i++) d[i] = Vector3.Dot(pts[i] - mean, normal);
                Array.Sort(d);
                result.shape = ColliderShape.Plane;
                result.axis = normal;
                result.center = mean + normal * SortedPercentile(d, 1f - trim);
                return true;
            }

            Vector3 u = e1;
            if (axisHint.HasValue && axisHint.Value.sqrMagnitude > 1e-10f) u = axisHint.Value.normalized;
            Vector3 v = e2 - u * Vector3.Dot(e2, u);
            if (v.sqrMagnitude < 1e-6f) v = e3 - u * Vector3.Dot(e3, u);
            if (v.sqrMagnitude < 1e-6f) v = Vector3.Cross(u, Mathf.Abs(u.y) < 0.9f ? Vector3.up : Vector3.right);
            v.Normalize();
            Vector3 w = Vector3.Cross(u, v);

            var t = new float[n];
            var a = new float[n];
            var b = new float[n];
            for (int i = 0; i < n; i++)
            {
                var p = pts[i];
                t[i] = Vector3.Dot(p, u);
                a[i] = Vector3.Dot(p, v);
                b[i] = Vector3.Dot(p, w);
            }

            float tLo, tHi, aLo, aHi, bLo, bHi;
            TrimmedRange(t, trim, out tLo, out tHi);
            TrimmedRange(a, trim, out aLo, out aHi);
            TrimmedRange(b, trim, out bLo, out bHi);
            float tm = (tLo + tHi) * 0.5f, am = (aLo + aHi) * 0.5f, bm = (bLo + bHi) * 0.5f;

            // Radial distance from the fitted axis (capsule) and from the center (sphere).
            var radial = new float[n];
            var dist = new float[n];
            for (int i = 0; i < n; i++)
            {
                float da = a[i] - am, db = b[i] - bm, dt = t[i] - tm;
                radial[i] = Mathf.Sqrt(da * da + db * db);
                dist[i] = Mathf.Sqrt(da * da + db * db + dt * dt);
            }
            float tightness = Mathf.Clamp01(s.tightness);
            float capsuleRadius, sphereRadius;
            if (solid)
            {
                // Filled volumes: distances are spread from 0 to the edge, so use the extents instead.
                float ha = (aHi - aLo) * 0.5f, hb = (bHi - bLo) * 0.5f, ht = (tHi - tLo) * 0.5f;
                capsuleRadius = Mathf.Lerp(Mathf.Min(ha, hb), Mathf.Max(ha, hb), tightness) * s.radiusScale;
                sphereRadius = Mathf.Lerp(Mathf.Min(ht, Mathf.Min(ha, hb)), Mathf.Max(ht, Mathf.Max(ha, hb)), tightness) * s.radiusScale;
            }
            else
            {
                Array.Sort(radial);
                Array.Sort(dist);
                capsuleRadius = SortedPercentile(radial, tightness) * s.radiusScale;
                sphereRadius = SortedPercentile(dist, tightness) * s.radiusScale;
            }
            float length = tHi - tLo;

            ColliderShape shape;
            if (forced.HasValue) shape = forced.Value;
            else shape = capsuleRadius > 1e-6f && length / (2f * capsuleRadius) >= s.capsuleThreshold ? ColliderShape.Capsule : ColliderShape.Sphere;

            result.shape = shape;
            result.center = u * tm + v * am + w * bm;
            result.axis = u;
            if (shape == ColliderShape.Capsule)
            {
                result.radius = capsuleRadius;
                result.height = Mathf.Max(length, 2f * capsuleRadius);
            }
            else
            {
                result.radius = sphereRadius;
                result.height = 0f;
            }
            return result.radius > 1e-5f;
        }

        /// <summary>
        /// Snaps points to a grid (resolution cells along the longest side) and keeps one averaged point per cell,
        /// so every region counts by the space it covers rather than by how many vertices the artist used there.
        /// </summary>
        public static List<Vector3> VoxelResample(List<Vector3> points, int resolution)
        {
            var min = points[0];
            var max = points[0];
            for (int i = 1; i < points.Count; i++)
            {
                min = Vector3.Min(min, points[i]);
                max = Vector3.Max(max, points[i]);
            }
            var size = max - min;
            float cell = Mathf.Max(size.x, Mathf.Max(size.y, size.z)) / Mathf.Max(4, resolution);
            if (cell <= 1e-7f) return points;
            float inv = 1f / cell;

            var sums = new Dictionary<long, Vector4>();
            for (int i = 0; i < points.Count; i++)
            {
                var p = points[i];
                long x = (long)((p.x - min.x) * inv), y = (long)((p.y - min.y) * inv), z = (long)((p.z - min.z) * inv);
                long key = (x << 42) | (y << 21) | z;
                Vector4 acc;
                sums.TryGetValue(key, out acc);
                sums[key] = new Vector4(acc.x + p.x, acc.y + p.y, acc.z + p.z, acc.w + 1f);
            }

            var result = new List<Vector3>(sums.Count);
            foreach (var acc in sums.Values) result.Add(new Vector3(acc.x, acc.y, acc.z) / acc.w);
            return result;
        }

        /// <summary>
        /// Voxelizes the points, flood-fills the outside, and returns the centers of every voxel that isn't outside
        /// (the shell plus whatever it encloses). Open meshes that leak just come back as their shell.
        /// </summary>
        public static List<Vector3> SolidVoxels(List<Vector3> points, int resolution)
        {
            var min = points[0];
            var max = points[0];
            for (int i = 1; i < points.Count; i++)
            {
                min = Vector3.Min(min, points[i]);
                max = Vector3.Max(max, points[i]);
            }
            var size = max - min;
            float cell = Mathf.Max(size.x, Mathf.Max(size.y, size.z)) / Mathf.Max(4, resolution);
            if (cell <= 1e-7f) return points;

            // One empty voxel of padding on every side so the flood fill can wrap all the way around.
            int nx = Mathf.CeilToInt(size.x / cell) + 3, ny = Mathf.CeilToInt(size.y / cell) + 3, nz = Mathf.CeilToInt(size.z / cell) + 3;
            int count = nx * ny * nz;
            var shell = new bool[count];
            foreach (var p in points)
            {
                int x = Mathf.Clamp((int)((p.x - min.x) / cell) + 1, 1, nx - 2);
                int y = Mathf.Clamp((int)((p.y - min.y) / cell) + 1, 1, ny - 2);
                int z = Mathf.Clamp((int)((p.z - min.z) / cell) + 1, 1, nz - 2);
                shell[(z * ny + y) * nx + x] = true;
            }

            // Thicken the shell by one voxel so small gaps (eye holes, seams) don't let the fill leak inside.
            var wall = (bool[])shell.Clone();
            for (int z = 1; z < nz - 1; z++)
                for (int y = 1; y < ny - 1; y++)
                    for (int x = 1; x < nx - 1; x++)
                    {
                        int i = (z * ny + y) * nx + x;
                        if (!shell[i]) continue;
                        wall[i - 1] = wall[i + 1] = true;
                        wall[i - nx] = wall[i + nx] = true;
                        wall[i - nx * ny] = wall[i + nx * ny] = true;
                    }

            var outside = new bool[count];
            var queue = new Queue<int>();
            outside[0] = true;
            queue.Enqueue(0);
            while (queue.Count > 0)
            {
                int i = queue.Dequeue();
                int x = i % nx, y = (i / nx) % ny, z = i / (nx * ny);
                if (x > 0) Visit(i - 1, wall, outside, queue);
                if (x < nx - 1) Visit(i + 1, wall, outside, queue);
                if (y > 0) Visit(i - nx, wall, outside, queue);
                if (y < ny - 1) Visit(i + nx, wall, outside, queue);
                if (z > 0) Visit(i - nx * ny, wall, outside, queue);
                if (z < nz - 1) Visit(i + nx * ny, wall, outside, queue);
            }

            var result = new List<Vector3>();
            for (int z = 0; z < nz; z++)
                for (int y = 0; y < ny; y++)
                    for (int x = 0; x < nx; x++)
                    {
                        int i = (z * ny + y) * nx + x;
                        // Keep the real shell and the enclosed interior, but not the thickening itself.
                        if (shell[i] || (!outside[i] && !wall[i]))
                            result.Add(min + new Vector3(x - 0.5f, y - 0.5f, z - 0.5f) * cell);
                    }
            return result;
        }

        static void Visit(int i, bool[] wall, bool[] outside, Queue<int> queue)
        {
            if (wall[i] || outside[i]) return;
            outside[i] = true;
            queue.Enqueue(i);
        }

        // ---------------------------------------------------------------- statistics

        static void TrimmedRange(float[] values, float trim, out float lo, out float hi)
        {
            var sorted = (float[])values.Clone();
            Array.Sort(sorted);
            lo = SortedPercentile(sorted, trim);
            hi = SortedPercentile(sorted, 1f - trim);
        }

        static float SortedPercentile(float[] sorted, float p)
        {
            if (sorted.Length == 0) return 0f;
            float f = Mathf.Clamp01(p) * (sorted.Length - 1);
            int i = Mathf.FloorToInt(f);
            if (i >= sorted.Length - 1) return sorted[sorted.Length - 1];
            return Mathf.Lerp(sorted[i], sorted[i + 1], f - i);
        }

        /// <summary>Principal axes of a point cloud, sorted by decreasing variance.</summary>
        public static void Pca(List<Vector3> pts, out Vector3 mean, out Vector3 e1, out Vector3 e2, out Vector3 e3)
        {
            int n = pts.Count;
            double mx = 0, my = 0, mz = 0;
            for (int i = 0; i < n; i++) { mx += pts[i].x; my += pts[i].y; mz += pts[i].z; }
            mx /= n; my /= n; mz /= n;
            mean = new Vector3((float)mx, (float)my, (float)mz);

            double xx = 0, xy = 0, xz = 0, yy = 0, yz = 0, zz = 0;
            for (int i = 0; i < n; i++)
            {
                double dx = pts[i].x - mx, dy = pts[i].y - my, dz = pts[i].z - mz;
                xx += dx * dx; xy += dx * dy; xz += dx * dz;
                yy += dy * dy; yz += dy * dz; zz += dz * dz;
            }

            var m = new double[3, 3] { { xx, xy, xz }, { xy, yy, yz }, { xz, yz, zz } };
            double[] values;
            double[,] vectors;
            JacobiEigen(m, out values, out vectors);

            var order = new[] { 0, 1, 2 };
            Array.Sort(order, (i, j) => values[j].CompareTo(values[i]));
            e1 = Column(vectors, order[0]);
            e2 = Column(vectors, order[1]);
            e3 = Column(vectors, order[2]);
        }

        static Vector3 Column(double[,] v, int c)
        {
            return new Vector3((float)v[0, c], (float)v[1, c], (float)v[2, c]).normalized;
        }

        /// <summary>Cyclic Jacobi eigen-decomposition of a symmetric 3x3 matrix. Eigenvectors are the columns of vectors.</summary>
        static void JacobiEigen(double[,] a, out double[] values, out double[,] vectors)
        {
            var v = new double[3, 3] { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } };
            for (int sweep = 0; sweep < 50; sweep++)
            {
                double off = a[0, 1] * a[0, 1] + a[0, 2] * a[0, 2] + a[1, 2] * a[1, 2];
                double diag = a[0, 0] * a[0, 0] + a[1, 1] * a[1, 1] + a[2, 2] * a[2, 2];
                if (off <= 1e-24 * Math.Max(diag, 1e-300)) break;

                for (int p = 0; p < 2; p++)
                {
                    for (int q = p + 1; q < 3; q++)
                    {
                        if (Math.Abs(a[p, q]) < 1e-300) continue;
                        double theta = (a[q, q] - a[p, p]) / (2.0 * a[p, q]);
                        double t = Math.Sign(theta) / (Math.Abs(theta) + Math.Sqrt(theta * theta + 1.0));
                        if (theta == 0) t = 1.0;
                        double c = 1.0 / Math.Sqrt(t * t + 1.0);
                        double s = t * c;

                        for (int k = 0; k < 3; k++)
                        {
                            double akp = a[k, p], akq = a[k, q];
                            a[k, p] = c * akp - s * akq;
                            a[k, q] = s * akp + c * akq;
                        }
                        for (int k = 0; k < 3; k++)
                        {
                            double apk = a[p, k], aqk = a[q, k];
                            a[p, k] = c * apk - s * aqk;
                            a[q, k] = s * apk + c * aqk;
                        }
                        for (int k = 0; k < 3; k++)
                        {
                            double vkp = v[k, p], vkq = v[k, q];
                            v[k, p] = c * vkp - s * vkq;
                            v[k, q] = s * vkp + c * vkq;
                        }
                    }
                }
            }
            values = new[] { a[0, 0], a[1, 1], a[2, 2] };
            vectors = v;
        }

        // ---------------------------------------------------------------- clustering

        /// <summary>Deterministic k-means, seeded by slicing the cloud along its longest axis.</summary>
        public static List<List<Vector3>> KMeans(List<Vector3> pts, int k)
        {
            int n = pts.Count;
            k = Mathf.Clamp(k, 1, Mathf.Max(1, n / MinPoints));

            Vector3 mean, e1, e2, e3;
            Pca(pts, out mean, out e1, out e2, out e3);

            var keys = new float[n];
            var index = new int[n];
            for (int i = 0; i < n; i++) { keys[i] = Vector3.Dot(pts[i] - mean, e1); index[i] = i; }
            Array.Sort(keys, index);

            var centers = new Vector3[k];
            for (int c = 0; c < k; c++)
            {
                int from = c * n / k, to = (c + 1) * n / k;
                var sum = Vector3.zero;
                for (int i = from; i < to; i++) sum += pts[index[i]];
                centers[c] = sum / Mathf.Max(1, to - from);
            }

            var assign = new int[n];
            var sums = new Vector3[k];
            var counts = new int[k];
            for (int iter = 0; iter < 20; iter++)
            {
                bool changed = false;
                for (int i = 0; i < n; i++)
                {
                    int best = 0;
                    float bestD = float.MaxValue;
                    for (int c = 0; c < k; c++)
                    {
                        float d = (pts[i] - centers[c]).sqrMagnitude;
                        if (d < bestD) { bestD = d; best = c; }
                    }
                    if (iter == 0 || assign[i] != best) { assign[i] = best; changed = true; }
                }
                if (!changed) break;

                Array.Clear(sums, 0, k);
                Array.Clear(counts, 0, k);
                for (int i = 0; i < n; i++) { sums[assign[i]] += pts[i]; counts[assign[i]]++; }
                for (int c = 0; c < k; c++) if (counts[c] > 0) centers[c] = sums[c] / counts[c];
            }

            var clusters = new List<List<Vector3>>(k);
            for (int c = 0; c < k; c++) clusters.Add(new List<Vector3>());
            for (int i = 0; i < n; i++) clusters[assign[i]].Add(pts[i]);
            return clusters;
        }
    }
}
