using System;
using System.Collections.Generic;
using System.Numerics;

namespace Dismember.Core
{
    /// <summary>Up to four bone influences, same layout as UnityEngine.BoneWeight.</summary>
    public struct BoneWeight4
    {
        public int I0, I1, I2, I3;
        public float W0, W1, W2, W3;

        public int Bone(int k) => k switch { 0 => I0, 1 => I1, 2 => I2, _ => I3 };
        public float Weight(int k) => k switch { 0 => W0, 1 => W1, 2 => W2, _ => W3 };

        public static BoneWeight4 Single(int bone) => new BoneWeight4 { I0 = bone, W0 = 1f };

        /// <summary>Index of the bone with the largest weight.</summary>
        public int Dominant
        {
            get
            {
                int b = I0; float w = W0;
                if (W1 > w) { b = I1; w = W1; }
                if (W2 > w) { b = I2; w = W2; }
                if (W3 > w) { b = I3; }
                return b;
            }
        }
    }

    /// <summary>Accumulates weighted bone influences and reduces them to the strongest four.</summary>
    public sealed class WeightAccumulator
    {
        // Small fixed buffers: 12 influences is enough for blending 3 vertices.
        private int _count;
        private readonly int[] _bones;
        private readonly float[] _weights;

        public WeightAccumulator(int capacity = 16)
        {
            _bones = new int[capacity];
            _weights = new float[capacity];
        }

        public void Clear() => _count = 0;

        public void Add(int bone, float weight)
        {
            if (weight <= 0f) return;
            for (int i = 0; i < _count; i++)
            {
                if (_bones[i] == bone) { _weights[i] += weight; return; }
            }
            if (_count < _bones.Length)
            {
                _bones[_count] = bone;
                _weights[_count] = weight;
                _count++;
            }
        }

        public void Add(in BoneWeight4 w, float scale)
        {
            Add(w.I0, w.W0 * scale);
            Add(w.I1, w.W1 * scale);
            Add(w.I2, w.W2 * scale);
            Add(w.I3, w.W3 * scale);
        }

        public BoneWeight4 ToTop4(int fallbackBone)
        {
            // Partial selection sort for the four largest.
            for (int k = 0; k < Math.Min(4, _count); k++)
            {
                int best = k;
                for (int i = k + 1; i < _count; i++)
                    if (_weights[i] > _weights[best]) best = i;
                (_bones[k], _bones[best]) = (_bones[best], _bones[k]);
                (_weights[k], _weights[best]) = (_weights[best], _weights[k]);
            }
            int n = Math.Min(4, _count);
            float sum = 0f;
            for (int i = 0; i < n; i++) sum += _weights[i];
            if (n == 0 || sum <= 1e-8f) return BoneWeight4.Single(fallbackBone);
            float inv = 1f / sum;
            var r = new BoneWeight4();
            if (n > 0) { r.I0 = _bones[0]; r.W0 = _weights[0] * inv; }
            if (n > 1) { r.I1 = _bones[1]; r.W1 = _weights[1] * inv; }
            if (n > 2) { r.I2 = _bones[2]; r.W2 = _weights[2] * inv; }
            if (n > 3) { r.I3 = _bones[3]; r.W3 = _weights[3] * inv; }
            return r;
        }
    }

    /// <summary>One frame of a blend shape, stored sparsely.</summary>
    public sealed class BlendFrame
    {
        public float Weight;
        public int[] Indices;
        public Vector3[] DeltaPositions;
        public Vector3[] DeltaNormals;   // may be null
        public Vector3[] DeltaTangents;  // may be null
    }

    public sealed class BlendShape
    {
        public string Name;
        public BlendFrame[] Frames;
    }

    /// <summary>
    /// Engine-independent copy of a (skinned) mesh in bind space.
    /// Matrices use System.Numerics conventions (row vectors: p' = p * M).
    /// </summary>
    public sealed class MeshData
    {
        public const int MaxUVChannels = 8;

        public string Name;
        public Vector3[] Positions;
        public Vector3[] Normals;        // may be null
        public Vector4[] Tangents;       // may be null
        public Vector4[] Colors;         // may be null
        public Vector4[][] UV = new Vector4[MaxUVChannels][];
        public int[] UVDimension = new int[MaxUVChannels];
        public BoneWeight4[] Weights;    // may be null (unskinned)
        public List<int[]> Submeshes = new List<int[]>();
        public Matrix4x4[] Bindposes;    // may be null
        public List<BlendShape> BlendShapes = new List<BlendShape>();

        public int VertexCount => Positions?.Length ?? 0;

        public int TriangleCount
        {
            get
            {
                int n = 0;
                foreach (var s in Submeshes) n += s.Length / 3;
                return n;
            }
        }

        private MeshTopology _topology;

        /// <summary>Welded triangle adjacency, built lazily and cached (bind data never changes).</summary>
        public MeshTopology Topology => _topology ??= MeshTopology.Build(this);
    }

    /// <summary>
    /// Hash for packed (a,b) edge keys. long.GetHashCode() is lo^hi, which collides for almost every edge
    /// of a mesh with small vertex indices and turns dictionary lookups quadratic.
    /// </summary>
    public sealed class EdgeKeyComparer : IEqualityComparer<long>
    {
        public static readonly EdgeKeyComparer Instance = new EdgeKeyComparer();
        public bool Equals(long x, long y) => x == y;
        public int GetHashCode(long k)
        {
            ulong z = (ulong)k + 0x9E3779B97F4A7C15UL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return (int)(z ^ (z >> 31));
        }
    }

    /// <summary>
    /// Flattened triangle list plus edge adjacency computed on welded bind positions,
    /// so UV seams and hard edges do not break connectivity.
    /// </summary>
    public sealed class MeshTopology
    {
        public int[] Tri;          // 3 vertex indices per triangle, all submeshes concatenated
        public int[] TriSubmesh;   // submesh of each triangle
        public int[] Weld;         // vertex -> weld id
        public int TriangleCount;
        private Dictionary<long, int[]> _edgeTris;

        public static MeshTopology Build(MeshData mesh)
        {
            var t = new MeshTopology();
            int triCount = mesh.TriangleCount;
            t.TriangleCount = triCount;
            t.Tri = new int[triCount * 3];
            t.TriSubmesh = new int[triCount];
            int o = 0;
            for (int s = 0; s < mesh.Submeshes.Count; s++)
            {
                var idx = mesh.Submeshes[s];
                for (int i = 0; i + 2 < idx.Length; i += 3)
                {
                    t.Tri[o * 3] = idx[i];
                    t.Tri[o * 3 + 1] = idx[i + 1];
                    t.Tri[o * 3 + 2] = idx[i + 2];
                    t.TriSubmesh[o] = s;
                    o++;
                }
            }

            // Weld identical positions.
            var map = new Dictionary<Vector3, int>(mesh.VertexCount);
            t.Weld = new int[mesh.VertexCount];
            for (int v = 0; v < mesh.VertexCount; v++)
            {
                if (!map.TryGetValue(mesh.Positions[v], out int id))
                {
                    id = map.Count;
                    map.Add(mesh.Positions[v], id);
                }
                t.Weld[v] = id;
            }
            return t;
        }

        public static long EdgeKey(int a, int b) => a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;

        /// <summary>Triangles sharing each welded edge.</summary>
        public Dictionary<long, int[]> EdgeTriangles
        {
            get
            {
                if (_edgeTris != null) return _edgeTris;
                var lists = new Dictionary<long, List<int>>(TriangleCount * 2, EdgeKeyComparer.Instance);
                for (int f = 0; f < TriangleCount; f++)
                {
                    for (int e = 0; e < 3; e++)
                    {
                        int a = Weld[Tri[f * 3 + e]], b = Weld[Tri[f * 3 + (e + 1) % 3]];
                        if (a == b) continue;
                        long key = EdgeKey(a, b);
                        if (!lists.TryGetValue(key, out var l)) lists[key] = l = new List<int>(2);
                        l.Add(f);
                    }
                }
                var result = new Dictionary<long, int[]>(lists.Count, EdgeKeyComparer.Instance);
                foreach (var kv in lists) result[kv.Key] = kv.Value.ToArray();
                _edgeTris = result;
                return result;
            }
        }
    }
}
