using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text;
using AssetsTools.NET;
using AssetsTools.NET.Extra;
using Dismember.Core;

namespace Dismember.Assets
{
    /// <summary>
    /// Reads mesh data straight out of Unity asset bundles.
    ///
    /// BONELAB ships its NPC meshes with Read/Write disabled, so the CPU copy is gone at runtime
    /// and Mesh.vertices etc. throw. The bundle files on disk still contain the full vertex/index
    /// data, so we decode it from there instead (AssetsTools.NET ships with MelonLoader).
    /// </summary>
    public static class BundleMeshReader
    {
        // Unity 2019+ VertexAttribute order.
        private const int ChPosition = 0, ChNormal = 1, ChTangent = 2, ChColor = 3, ChUV0 = 4, ChBlendWeight = 12, ChBlendIndices = 13;

        /// <summary>Opens a bundle and decodes the mesh with the given path id. Not thread-safe per manager.</summary>
        public static MeshData ReadMesh(string bundlePath, long pathId)
        {
            var am = new AssetsManager();
            try
            {
                var bun = am.LoadBundleFile(bundlePath, true);
                int n = bun.file.BlockAndDirInfo.DirectoryInfos.Count;
                for (int i = 0; i < n; i++)
                {
                    if (!bun.file.IsAssetsFile(i)) continue;
                    var af = am.LoadAssetsFileFromBundle(bun, i, false);
                    var info = af.file.GetAssetInfo(pathId);
                    if (info == null || info.TypeId != (int)AssetClassID.Mesh) continue;
                    var bf = am.GetBaseField(af, info);
                    return Decode(bf, name => ReadResource(bun, name));
                }
                return null;
            }
            finally
            {
                am.UnloadAll(true);
            }
        }

        /// <summary>Reads (offset, size) bytes from another entry of the bundle, e.g. "archive:/CAB-x/CAB-x.resS".</summary>
        private static Func<ulong, uint, byte[]> ReadResource(BundleFileInstance bun, string path)
        {
            string file = path;
            int slash = file.LastIndexOf('/');
            if (slash >= 0) file = file.Substring(slash + 1);
            foreach (var di in bun.file.BlockAndDirInfo.DirectoryInfos)
            {
                if (!string.Equals(di.Name, file, StringComparison.OrdinalIgnoreCase)) continue;
                var entry = di;
                return (offset, size) =>
                {
                    var r = bun.file.DataReader;
                    r.Position = entry.Offset + (long)offset;
                    return r.ReadBytes((int)size);
                };
            }
            return null;
        }

        /// <summary>Decodes a serialized Mesh (Unity 2019.3+ layout).</summary>
        public static MeshData Decode(AssetTypeValueField bf, Func<string, Func<ulong, uint, byte[]>> resourceResolver = null)
        {
            var cm = bf["m_CompressedMesh"];
            if (bf["m_MeshCompression"].AsByte != 0 && !cm.IsDummy && cm["m_Vertices.m_NumItems"].AsUInt > 0)
                return DecodeCompressed(bf, cm);

            var mesh = new MeshData { Name = bf["m_Name"].AsString };
            var vd = bf["m_VertexData"];
            int vertexCount = (int)vd["m_VertexCount"].AsUInt;

            byte[] data = vd["m_DataSize"].AsByteArray;
            if ((data == null || data.Length == 0) && !bf["m_StreamData"].IsDummy && bf["m_StreamData.size"].AsUInt > 0)
            {
                var sd = bf["m_StreamData"];
                var read = resourceResolver?.Invoke(sd["path"].AsString);
                if (read == null) throw new InvalidDataException("Mesh data is streamed from a resource that could not be found");
                data = read(sd["offset"].AsULong, sd["size"].AsUInt);
            }
            if (data == null) data = Array.Empty<byte>();

            // Channels and stream layout.
            var channels = new List<(int stream, int offset, int format, int dim)>();
            foreach (var c in vd["m_Channels.Array"].Children)
                channels.Add((c["stream"].AsByte, c["offset"].AsByte, c["format"].AsByte, c["dimension"].AsByte & 0xF));

            int streamCount = 0;
            foreach (var c in channels) if (c.dim > 0) streamCount = Math.Max(streamCount, c.stream + 1);
            var stride = new int[streamCount];
            foreach (var c in channels)
                if (c.dim > 0) stride[c.stream] = Math.Max(stride[c.stream], c.offset + FormatSize(c.format) * c.dim);
            var streamOffset = new int[streamCount];
            int pos = 0;
            for (int s = 0; s < streamCount; s++)
            {
                streamOffset[s] = pos;
                pos += stride[s] * vertexCount;
                pos = (pos + 15) & ~15;
            }

            float[] ReadChannel(int ch, out int dim)
            {
                dim = 0;
                if (ch >= channels.Count) return null;
                var c = channels[ch];
                if (c.dim == 0) return null;
                dim = c.dim;
                var result = new float[vertexCount * c.dim];
                int size = FormatSize(c.format);
                for (int v = 0; v < vertexCount; v++)
                {
                    int o = streamOffset[c.stream] + v * stride[c.stream] + c.offset;
                    for (int k = 0; k < c.dim; k++)
                        result[v * c.dim + k] = ReadComponent(data, o + k * size, c.format);
                }
                return result;
            }

            var p = ReadChannel(ChPosition, out int pd);
            mesh.Positions = new Vector3[vertexCount];
            for (int v = 0; v < vertexCount; v++)
                mesh.Positions[v] = new Vector3(p[v * pd], p[v * pd + 1], pd > 2 ? p[v * pd + 2] : 0f);

            var nrm = ReadChannel(ChNormal, out int nd);
            if (nrm != null && nd >= 3)
            {
                mesh.Normals = new Vector3[vertexCount];
                for (int v = 0; v < vertexCount; v++)
                    mesh.Normals[v] = new Vector3(nrm[v * nd], nrm[v * nd + 1], nrm[v * nd + 2]);
            }

            var tan = ReadChannel(ChTangent, out int td);
            if (tan != null && td >= 4)
            {
                mesh.Tangents = new Vector4[vertexCount];
                for (int v = 0; v < vertexCount; v++)
                    mesh.Tangents[v] = new Vector4(tan[v * td], tan[v * td + 1], tan[v * td + 2], tan[v * td + 3]);
            }

            var col = ReadChannel(ChColor, out int cd);
            if (col != null)
            {
                mesh.Colors = new Vector4[vertexCount];
                for (int v = 0; v < vertexCount; v++)
                    mesh.Colors[v] = new Vector4(col[v * cd], cd > 1 ? col[v * cd + 1] : 0, cd > 2 ? col[v * cd + 2] : 0, cd > 3 ? col[v * cd + 3] : 1);
            }

            for (int uv = 0; uv < MeshData.MaxUVChannels; uv++)
            {
                var t = ReadChannel(ChUV0 + uv, out int ud);
                if (t == null) continue;
                var arr = new Vector4[vertexCount];
                for (int v = 0; v < vertexCount; v++)
                    arr[v] = new Vector4(t[v * ud], ud > 1 ? t[v * ud + 1] : 0, ud > 2 ? t[v * ud + 2] : 0, ud > 3 ? t[v * ud + 3] : 0);
                mesh.UV[uv] = arr;
                mesh.UVDimension[uv] = ud;
            }

            var bw = ReadChannel(ChBlendWeight, out int bwd);
            var bi = ReadChannel(ChBlendIndices, out int bid);
            if (bi != null)
            {
                mesh.Weights = new BoneWeight4[vertexCount];
                for (int v = 0; v < vertexCount; v++)
                {
                    var w = new BoneWeight4();
                    for (int k = 0; k < Math.Min(4, bid); k++)
                    {
                        int bone = (int)bi[v * bid + k];
                        float weight = bw != null ? (k < bwd ? bw[v * bwd + k] : 0f) : (k == 0 ? 1f : 0f);
                        // Unity stores N-1 weights for some layouts: the last is implied.
                        switch (k)
                        {
                            case 0: w.I0 = bone; w.W0 = weight; break;
                            case 1: w.I1 = bone; w.W1 = weight; break;
                            case 2: w.I2 = bone; w.W2 = weight; break;
                            case 3: w.I3 = bone; w.W3 = weight; break;
                        }
                    }
                    if (bw != null && bwd < bid)
                    {
                        float rest = 1f - (w.W0 + w.W1 + w.W2 + w.W3);
                        if (bwd == bid - 1 && rest > 0f)
                        {
                            switch (bwd)
                            {
                                case 1: w.W1 = rest; break;
                                case 2: w.W2 = rest; break;
                                case 3: w.W3 = rest; break;
                            }
                        }
                    }
                    mesh.Weights[v] = w;
                }
            }
            else if (!bf["m_Skin"].IsDummy && bf["m_Skin.Array"].Children.Count == vertexCount)
            {
                // Pre-2019 layout.
                mesh.Weights = new BoneWeight4[vertexCount];
                int v = 0;
                foreach (var s in bf["m_Skin.Array"].Children)
                {
                    mesh.Weights[v++] = new BoneWeight4
                    {
                        W0 = s["weight[0]"].AsFloat, W1 = s["weight[1]"].AsFloat, W2 = s["weight[2]"].AsFloat, W3 = s["weight[3]"].AsFloat,
                        I0 = s["boneIndex[0]"].AsInt, I1 = s["boneIndex[1]"].AsInt, I2 = s["boneIndex[2]"].AsInt, I3 = s["boneIndex[3]"].AsInt,
                    };
                }
            }

            // Index buffer and submeshes.
            byte[] ib = bf["m_IndexBuffer.Array"].AsByteArray ?? Array.Empty<byte>();
            bool use32 = bf["m_IndexFormat"].AsInt == 1;
            ReadSubmeshes(bf, mesh, k => use32 ? BitConverter.ToInt32(ib, k * 4) : BitConverter.ToUInt16(ib, k * 2));
            ReadBindPoses(bf, mesh);
            ReadBlendShapes(bf["m_Shapes"], mesh);
            return mesh;
        }

        /// <summary>Submeshes; <paramref name="index"/> gives the k-th entry of the index buffer.</summary>
        private static void ReadSubmeshes(AssetTypeValueField bf, MeshData mesh, Func<int, int> index)
        {
            bool use32 = bf["m_IndexFormat"].AsInt == 1;
            foreach (var sm in bf["m_SubMeshes.Array"].Children)
            {
                int first = (int)sm["firstByte"].AsUInt / (use32 ? 4 : 2);
                int count = (int)sm["indexCount"].AsUInt;
                int topology = sm["topology"].AsInt;
                int baseVertex = (int)sm["baseVertex"].AsUInt;
                if (topology != 0)
                {
                    mesh.Submeshes.Add(Array.Empty<int>());
                    continue;
                }
                var tris = new int[count - count % 3];
                for (int k = 0; k < tris.Length; k++) tris[k] = index(first + k) + baseVertex;
                mesh.Submeshes.Add(tris);
            }
        }

        /// <summary>
        /// Meshes saved with Unity's mesh compression keep their vertex data bit-packed in m_CompressedMesh
        /// (quantised floats, normals as x/y plus a sign, weights in 31ths). Same layout AssetStudio decodes.
        /// </summary>
        private static MeshData DecodeCompressed(AssetTypeValueField bf, AssetTypeValueField cm)
        {
            var mesh = new MeshData { Name = bf["m_Name"].AsString };
            var pv = cm["m_Vertices"];
            int vc = (int)(pv["m_NumItems"].AsUInt / 3);
            var p = UnpackFloats(pv, 3, 3 * 4);
            mesh.Positions = new Vector3[vc];
            for (int v = 0; v < vc; v++) mesh.Positions[v] = new Vector3(p[v * 3], p[v * 3 + 1], p[v * 3 + 2]);

            var uvField = cm["m_UV"];
            if (uvField["m_NumItems"].AsUInt > 0)
            {
                uint info = cm["m_UVInfo"].AsUInt;
                if (info != 0)
                {
                    int src = 0;
                    for (int uv = 0; uv < 8; uv++)
                    {
                        uint bits = (info >> (uv * 4)) & 0xF;
                        if ((bits & 4) == 0) continue;
                        int dim = 1 + (int)(bits & 3);
                        var t = UnpackFloats(uvField, dim, dim * 4, src, vc);
                        src += dim * vc;
                        if (uv >= MeshData.MaxUVChannels) continue;
                        var arr = new Vector4[vc];
                        for (int v = 0; v < vc; v++)
                            arr[v] = new Vector4(t[v * dim], dim > 1 ? t[v * dim + 1] : 0, dim > 2 ? t[v * dim + 2] : 0, dim > 3 ? t[v * dim + 3] : 0);
                        mesh.UV[uv] = arr;
                        mesh.UVDimension[uv] = dim;
                    }
                }
                else
                {
                    var t = UnpackFloats(uvField, 2, 2 * 4, 0, vc);
                    mesh.UV[0] = new Vector4[vc];
                    for (int v = 0; v < vc; v++) mesh.UV[0][v] = new Vector4(t[v * 2], t[v * 2 + 1], 0, 0);
                    mesh.UVDimension[0] = 2;
                    if (uvField["m_NumItems"].AsUInt >= vc * 4)
                    {
                        var t1 = UnpackFloats(uvField, 2, 2 * 4, vc * 2, vc);
                        mesh.UV[1] = new Vector4[vc];
                        for (int v = 0; v < vc; v++) mesh.UV[1][v] = new Vector4(t1[v * 2], t1[v * 2 + 1], 0, 0);
                        mesh.UVDimension[1] = 2;
                    }
                }
            }

            // Normals and tangents: x and y, z rebuilt from unit length, its sign stored separately.
            static Vector3 FromXY(float x, float y, bool positive)
            {
                float zz = 1f - x * x - y * y;
                var n = zz >= 0f ? new Vector3(x, y, MathF.Sqrt(zz)) : Vector3.Normalize(new Vector3(x, y, 0f));
                return positive ? n : new Vector3(n.X, n.Y, -n.Z);
            }
            var nf = cm["m_Normals"];
            if (nf["m_NumItems"].AsUInt > 0)
            {
                var d = UnpackFloats(nf, 2, 4 * 2);
                var signs = UnpackInts(cm["m_NormalSigns"]);
                int n = (int)(nf["m_NumItems"].AsUInt / 2);
                if (n == vc)
                {
                    mesh.Normals = new Vector3[vc];
                    for (int i = 0; i < n; i++) mesh.Normals[i] = FromXY(d[i * 2], d[i * 2 + 1], signs[i] != 0);
                }
            }
            var tf = cm["m_Tangents"];
            if (tf["m_NumItems"].AsUInt > 0)
            {
                var d = UnpackFloats(tf, 2, 4 * 2);
                var signs = UnpackInts(cm["m_TangentSigns"]);
                int n = (int)(tf["m_NumItems"].AsUInt / 2);
                if (n == vc)
                {
                    mesh.Tangents = new Vector4[vc];
                    for (int i = 0; i < n; i++)
                    {
                        var t = FromXY(d[i * 2], d[i * 2 + 1], signs[i * 2] != 0);
                        mesh.Tangents[i] = new Vector4(t, signs[i * 2 + 1] > 0 ? 1f : -1f);
                    }
                }
            }
            var cf = cm["m_FloatColors"];
            if (!cf.IsDummy && cf["m_NumItems"].AsUInt >= vc * 4)
            {
                var c = UnpackFloats(cf, 1, 4);
                mesh.Colors = new Vector4[vc];
                for (int v = 0; v < vc; v++) mesh.Colors[v] = new Vector4(c[v * 4], c[v * 4 + 1], c[v * 4 + 2], c[v * 4 + 3]);
            }

            // Skin: weights in 31ths, a vertex's weights end once they add up to 31 (or after the fourth).
            var wf = cm["m_Weights"];
            if (wf["m_NumItems"].AsUInt > 0)
            {
                var weights = UnpackInts(wf);
                var bones = UnpackInts(cm["m_BoneIndices"]);
                mesh.Weights = new BoneWeight4[vc];
                int vert = 0, j = 0, sum = 0, bi = 0;
                var w = new BoneWeight4();
                void Set(int k, int bone, float weight)
                {
                    switch (k)
                    {
                        case 0: w.I0 = bone; w.W0 = weight; break;
                        case 1: w.I1 = bone; w.W1 = weight; break;
                        case 2: w.I2 = bone; w.W2 = weight; break;
                        default: w.I3 = bone; w.W3 = weight; break;
                    }
                }
                for (int i = 0; i < weights.Length && vert < vc; i++)
                {
                    Set(j, bones[bi++], weights[i] / 31f);
                    j++;
                    sum += weights[i];
                    if (sum >= 31)
                    {
                        mesh.Weights[vert++] = w;
                        w = new BoneWeight4();
                        j = 0; sum = 0;
                    }
                    else if (j == 3)
                    {
                        Set(3, bones[bi++], (31 - sum) / 31f);
                        mesh.Weights[vert++] = w;
                        w = new BoneWeight4();
                        j = 0; sum = 0;
                    }
                }
            }

            var tris = UnpackInts(cm["m_Triangles"]);
            ReadSubmeshes(bf, mesh, k => tris[k]);
            ReadBindPoses(bf, mesh);
            ReadBlendShapes(bf["m_Shapes"], mesh);
            return mesh;
        }

        private static float[] UnpackFloats(AssetTypeValueField f, int itemsPerChunk, int chunkStride, int start = 0, int numChunks = -1)
        {
            int bitSize = f["m_BitSize"].AsByte;
            float range = f["m_Range"].AsFloat, origin = f["m_Start"].AsFloat;
            byte[] data = f["m_Data.Array"].AsByteArray ?? Array.Empty<byte>();
            uint numItems = f["m_NumItems"].AsUInt;
            if (numChunks < 0) numChunks = (int)(numItems / (uint)itemsPerChunk);
            var result = new float[numChunks * itemsPerChunk];
            if (bitSize == 0)
            {
                Array.Fill(result, origin);
                return result;
            }
            long bitPos = (long)bitSize * start;
            int indexPos = (int)(bitPos / 8);
            int shift = (int)(bitPos % 8);
            float scale = range / ((1L << bitSize) - 1);
            for (int i = 0; i < result.Length; i++)
            {
                uint x = 0;
                int bits = 0;
                while (bits < bitSize)
                {
                    x |= (uint)((data[indexPos] >> shift) << bits);
                    int num = Math.Min(bitSize - bits, 8 - shift);
                    shift += num;
                    bits += num;
                    if (shift == 8) { indexPos++; shift = 0; }
                }
                x &= (uint)((1L << bitSize) - 1);
                result[i] = x * scale + origin;
            }
            return result;
        }

        private static int[] UnpackInts(AssetTypeValueField f)
        {
            int bitSize = f["m_BitSize"].AsByte;
            byte[] data = f["m_Data.Array"].AsByteArray ?? Array.Empty<byte>();
            var result = new int[f["m_NumItems"].AsUInt];
            int indexPos = 0, shift = 0;
            for (int i = 0; i < result.Length && bitSize > 0; i++)
            {
                int x = 0, bits = 0;
                while (bits < bitSize)
                {
                    x |= (data[indexPos] >> shift) << bits;
                    int num = Math.Min(bitSize - bits, 8 - shift);
                    shift += num;
                    bits += num;
                    if (shift == 8) { indexPos++; shift = 0; }
                }
                result[i] = x & (int)((1L << bitSize) - 1);
            }
            return result;
        }

        private static void ReadBindPoses(AssetTypeValueField bf, MeshData mesh)
        {
            // Bind poses (stored as Unity column-vector matrices; convert to row-vector System.Numerics).
            var bp = bf["m_BindPose.Array"].Children;
            if (bp.Count > 0)
            {
                mesh.Bindposes = new Matrix4x4[bp.Count];
                for (int i = 0; i < bp.Count; i++)
                {
                    var m = bp[i];
                    mesh.Bindposes[i] = FromUnity(
                        m["e00"].AsFloat, m["e01"].AsFloat, m["e02"].AsFloat, m["e03"].AsFloat,
                        m["e10"].AsFloat, m["e11"].AsFloat, m["e12"].AsFloat, m["e13"].AsFloat,
                        m["e20"].AsFloat, m["e21"].AsFloat, m["e22"].AsFloat, m["e23"].AsFloat,
                        m["e30"].AsFloat, m["e31"].AsFloat, m["e32"].AsFloat, m["e33"].AsFloat);
                }
            }
        }

        private static void ReadBlendShapes(AssetTypeValueField shapes, MeshData mesh)
        {
            if (shapes.IsDummy) return;
            var verts = shapes["vertices.Array"].Children;
            var frames = shapes["shapes.Array"].Children;
            var chans = shapes["channels.Array"].Children;
            var weights = shapes["fullWeights.Array"].Children;
            if (chans.Count == 0) return;

            foreach (var ch in chans)
            {
                var shape = new BlendShape { Name = ch["name"].AsString };
                int fi = ch["frameIndex"].AsInt, fc = ch["frameCount"].AsInt;
                shape.Frames = new BlendFrame[fc];
                for (int f = 0; f < fc; f++)
                {
                    var fr = frames[fi + f];
                    int first = (int)fr["firstVertex"].AsUInt, count = (int)fr["vertexCount"].AsUInt;
                    bool hasN = fr["hasNormals"].AsBool, hasT = fr["hasTangents"].AsBool;
                    var frame = new BlendFrame
                    {
                        Weight = fi + f < weights.Count ? weights[fi + f].AsFloat : 100f,
                        Indices = new int[count],
                        DeltaPositions = new Vector3[count],
                        DeltaNormals = hasN ? new Vector3[count] : null,
                        DeltaTangents = hasT ? new Vector3[count] : null,
                    };
                    for (int k = 0; k < count; k++)
                    {
                        var bv = verts[first + k];
                        frame.Indices[k] = (int)bv["index"].AsUInt;
                        frame.DeltaPositions[k] = V3(bv["vertex"]);
                        if (hasN) frame.DeltaNormals[k] = V3(bv["normal"]);
                        if (hasT) frame.DeltaTangents[k] = V3(bv["tangent"]);
                    }
                    shape.Frames[f] = frame;
                }
                mesh.BlendShapes.Add(shape);
            }
        }

        private static Vector3 V3(AssetTypeValueField f) => new Vector3(f["x"].AsFloat, f["y"].AsFloat, f["z"].AsFloat);

        /// <summary>
        /// Unity serializes Matrix4x4f as eRC (row R, column C) with column vectors (translation in e03,e13,e23).
        /// System.Numerics uses row vectors, i.e. the transpose.
        /// </summary>
        public static Matrix4x4 FromUnity(
            float e00, float e01, float e02, float e03,
            float e10, float e11, float e12, float e13,
            float e20, float e21, float e22, float e23,
            float e30, float e31, float e32, float e33)
        {
            return new Matrix4x4(
                e00, e10, e20, e30,
                e01, e11, e21, e31,
                e02, e12, e22, e32,
                e03, e13, e23, e33);
        }

        private static int FormatSize(int format) => format switch
        {
            0 => 4,   // Float32
            1 => 2,   // Float16
            2 => 1,   // UNorm8
            3 => 1,   // SNorm8
            4 => 2,   // UNorm16
            5 => 2,   // SNorm16
            6 => 1,   // UInt8
            7 => 1,   // SInt8
            8 => 2,   // UInt16
            9 => 2,   // SInt16
            10 => 4,  // UInt32
            11 => 4,  // SInt32
            _ => 4,
        };

        private static float ReadComponent(byte[] d, int o, int format) => format switch
        {
            0 => BitConverter.ToSingle(d, o),
            1 => (float)BitConverter.ToHalf(d, o),
            2 => d[o] / 255f,
            3 => Math.Max((sbyte)d[o] / 127f, -1f),
            4 => BitConverter.ToUInt16(d, o) / 65535f,
            5 => Math.Max(BitConverter.ToInt16(d, o) / 32767f, -1f),
            6 => d[o],
            7 => (sbyte)d[o],
            8 => BitConverter.ToUInt16(d, o),
            9 => BitConverter.ToInt16(d, o),
            10 => BitConverter.ToUInt32(d, o),
            11 => BitConverter.ToInt32(d, o),
            _ => 0f,
        };

        /// <summary>Lists (pathId, name, vertexCount, isSkinned) for every mesh in a bundle without decoding vertex data.</summary>
        public static List<(long pathId, string name, int vertexCount, bool skinned)> ListMeshes(string bundlePath)
        {
            var result = new List<(long, string, int, bool)>();
            var am = new AssetsManager();
            try
            {
                var bun = am.LoadBundleFile(bundlePath, true);
                int n = bun.file.BlockAndDirInfo.DirectoryInfos.Count;
                for (int i = 0; i < n; i++)
                {
                    if (!bun.file.IsAssetsFile(i)) continue;
                    var af = am.LoadAssetsFileFromBundle(bun, i, false);
                    foreach (var info in af.file.GetAssetsOfType(AssetClassID.Mesh))
                    {
                        var r = af.file.Reader;
                        r.Position = info.GetAbsoluteByteOffset(af.file);
                        int len = r.ReadInt32();
                        if (len < 0 || len > 1024) continue;
                        string name = Encoding.UTF8.GetString(r.ReadBytes(len));
                        // Vertex count and skinning need the full parse; keep it cheap and let the decoder verify.
                        result.Add((info.PathId, name, -1, true));
                    }
                }
            }
            finally
            {
                am.UnloadAll(true);
            }
            return result;
        }
    }
}
