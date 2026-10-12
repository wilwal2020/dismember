using System;
using System.Collections.Generic;
using System.Linq;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using UnityEngine.Rendering;
using Dismember.Core;
using NMatrix = System.Numerics.Matrix4x4;
using NVector2 = System.Numerics.Vector2;
using NVector3 = System.Numerics.Vector3;
using NVector4 = System.Numerics.Vector4;

namespace Dismember.Game
{
    /// <summary>Conversions between Unity (column-vector) and System.Numerics (row-vector) types.</summary>
    public static class Conv
    {
        public static NVector3 N(this Vector3 v) => new NVector3(v.x, v.y, v.z);
        public static Vector3 U(this NVector3 v) => new Vector3(v.X, v.Y, v.Z);
        public static NVector4 N(this Vector4 v) => new NVector4(v.x, v.y, v.z, v.w);
        public static Vector4 U(this NVector4 v) => new Vector4(v.X, v.Y, v.Z, v.W);

        public static NMatrix N(this Matrix4x4 m) => new NMatrix(
            m.m00, m.m10, m.m20, m.m30,
            m.m01, m.m11, m.m21, m.m31,
            m.m02, m.m12, m.m22, m.m32,
            m.m03, m.m13, m.m23, m.m33);

        public static Matrix4x4 U(this NMatrix n)
        {
            var m = new Matrix4x4();
            m.m00 = n.M11; m.m10 = n.M12; m.m20 = n.M13; m.m30 = n.M14;
            m.m01 = n.M21; m.m11 = n.M22; m.m21 = n.M23; m.m31 = n.M24;
            m.m02 = n.M31; m.m12 = n.M32; m.m22 = n.M33; m.m32 = n.M34;
            m.m03 = n.M41; m.m13 = n.M42; m.m23 = n.M43; m.m33 = n.M44;
            return m;
        }
    }

    /// <summary>Builds Unity meshes from <see cref="MeshData"/> and reads readable Unity meshes back.</summary>
    public static class MeshFactory
    {
        public static Mesh ToUnity(MeshData md, string name)
        {
            var mesh = new Mesh { name = name };
            int vc = md.VertexCount;
            if (vc > 65000) mesh.indexFormat = IndexFormat.UInt32;

            var pos = new Vector3[vc];
            for (int i = 0; i < vc; i++) pos[i] = md.Positions[i].U();
            mesh.vertices = pos;

            if (md.Normals != null)
            {
                var n = new Vector3[vc];
                for (int i = 0; i < vc; i++) n[i] = md.Normals[i].U();
                mesh.normals = n;
            }
            if (md.Tangents != null)
            {
                var t = new Vector4[vc];
                for (int i = 0; i < vc; i++) t[i] = md.Tangents[i].U();
                mesh.tangents = t;
            }
            if (md.Colors != null)
            {
                var c = new Color[vc];
                for (int i = 0; i < vc; i++) c[i] = new Color(md.Colors[i].X, md.Colors[i].Y, md.Colors[i].Z, md.Colors[i].W);
                mesh.colors = c;
            }
            for (int ch = 0; ch < MeshData.MaxUVChannels; ch++)
            {
                var uv = md.UV[ch];
                if (uv == null) continue;
                int dim = md.UVDimension[ch];
                if (dim <= 2)
                {
                    var a = new Vector2[vc];
                    for (int i = 0; i < vc; i++) a[i] = new Vector2(uv[i].X, uv[i].Y);
                    mesh.SetUVs(ch, new Il2CppStructArray<Vector2>(a));
                }
                else if (dim == 3)
                {
                    var a = new Vector3[vc];
                    for (int i = 0; i < vc; i++) a[i] = new Vector3(uv[i].X, uv[i].Y, uv[i].Z);
                    mesh.SetUVs(ch, new Il2CppStructArray<Vector3>(a));
                }
                else
                {
                    var a = new Vector4[vc];
                    for (int i = 0; i < vc; i++) a[i] = uv[i].U();
                    mesh.SetUVs(ch, new Il2CppStructArray<Vector4>(a));
                }
            }
            if (md.Weights != null)
            {
                var bw = new BoneWeight[vc];
                for (int i = 0; i < vc; i++)
                {
                    var w = md.Weights[i];
                    bw[i] = new BoneWeight
                    {
                        boneIndex0 = w.I0, weight0 = w.W0,
                        boneIndex1 = w.I1, weight1 = w.W1,
                        boneIndex2 = w.I2, weight2 = w.W2,
                        boneIndex3 = w.I3, weight3 = w.W3,
                    };
                }
                mesh.boneWeights = bw;
            }
            if (md.Bindposes != null)
            {
                var bp = new Matrix4x4[md.Bindposes.Length];
                for (int i = 0; i < bp.Length; i++) bp[i] = md.Bindposes[i].U();
                mesh.bindposes = bp;
            }

            mesh.subMeshCount = Math.Max(1, md.Submeshes.Count);
            for (int s = 0; s < md.Submeshes.Count; s++)
                mesh.SetTriangles(new Il2CppStructArray<int>(md.Submeshes[s]), s, false);

            // Shapes must keep their order (renderers address them by index), and names must be unique: Unity appends
            // a frame to an existing shape of the same name (Ford's hands have seven shapes all called "Morpher").
            var names = new HashSet<string>();
            // Every frame needs full-length arrays, though it moves only some vertices: three game-side arrays are filled,
            // handed over and cleared again for each frame (a face has 40-odd shapes; fresh arrays for each were ~15 MB
            // of garbage per mesh).
            Il2CppStructArray<Vector3> dp = null, dn = null, dt = null;
            foreach (var shape in md.BlendShapes)
            {
                string shapeName = string.IsNullOrEmpty(shape.Name) ? "shape" : shape.Name;
                for (int i = 1; !names.Add(shapeName); i++) shapeName = (string.IsNullOrEmpty(shape.Name) ? "shape" : shape.Name) + "_" + i;
                int added = 0;
                float lastWeight = float.NegativeInfinity;
                foreach (var frame in shape.Frames.OrderBy(f => f.Weight))
                {
                    if (frame.Weight <= lastWeight) continue;   // weights must strictly increase
                    dp ??= new Il2CppStructArray<Vector3>(vc);
                    if (frame.DeltaNormals != null) dn ??= new Il2CppStructArray<Vector3>(vc);
                    if (frame.DeltaTangents != null) dt ??= new Il2CppStructArray<Vector3>(vc);
                    for (int k = 0; k < frame.Indices.Length; k++)
                    {
                        int v = frame.Indices[k];
                        if (v < 0 || v >= vc) continue;
                        dp[v] = frame.DeltaPositions[k].U();
                        if (frame.DeltaNormals != null) dn[v] = frame.DeltaNormals[k].U();
                        if (frame.DeltaTangents != null) dt[v] = frame.DeltaTangents[k].U();
                    }
                    try
                    {
                        mesh.AddBlendShapeFrame(shapeName, frame.Weight, dp, frame.DeltaNormals != null ? dn : null, frame.DeltaTangents != null ? dt : null);
                        lastWeight = frame.Weight;
                        added++;
                    }
                    catch (Exception e) { Log.Debug($"blend shape {shapeName} on {md.Name}: {e.Message}"); }
                    for (int k = 0; k < frame.Indices.Length; k++)
                    {
                        int v = frame.Indices[k];
                        if (v < 0 || v >= vc) continue;
                        dp[v] = default;
                        if (frame.DeltaNormals != null) dn[v] = default;
                        if (frame.DeltaTangents != null) dt[v] = default;
                    }
                }
                if (added == 0)
                {
                    // Keep the index slot so later shapes stay where the renderer expects them.
                    dp ??= new Il2CppStructArray<Vector3>(vc);
                    try { mesh.AddBlendShapeFrame(shapeName, 100f, dp, null, null); }
                    catch (Exception e) { Log.Debug($"placeholder blend shape {shapeName}: {e.Message}"); }
                }
            }
            mesh.RecalculateBounds();
            // We keep our own copy (MeshData); drop Unity's CPU copy to halve the memory per cut.
            mesh.UploadMeshData(true);
            return mesh;
        }

        /// <summary>Copies a readable Unity mesh (modded NPCs often ship readable meshes).</summary>
        public static MeshData FromUnity(Mesh mesh)
        {
            var md = new MeshData { Name = mesh.name };
            var v = (Vector3[])mesh.vertices;
            int vc = v.Length;
            md.Positions = new NVector3[vc];
            for (int i = 0; i < vc; i++) md.Positions[i] = v[i].N();

            var n = (Vector3[])mesh.normals;
            if (n != null && n.Length == vc)
            {
                md.Normals = new NVector3[vc];
                for (int i = 0; i < vc; i++) md.Normals[i] = n[i].N();
            }
            var t = (Vector4[])mesh.tangents;
            if (t != null && t.Length == vc)
            {
                md.Tangents = new NVector4[vc];
                for (int i = 0; i < vc; i++) md.Tangents[i] = t[i].N();
            }
            var c = (Color[])mesh.colors;
            if (c != null && c.Length == vc)
            {
                md.Colors = new NVector4[vc];
                for (int i = 0; i < vc; i++) md.Colors[i] = new NVector4(c[i].r, c[i].g, c[i].b, c[i].a);
            }
            var uv0 = (Vector2[])mesh.uv;
            if (uv0 != null && uv0.Length == vc)
            {
                md.UV[0] = new NVector4[vc];
                md.UVDimension[0] = 2;
                for (int i = 0; i < vc; i++) md.UV[0][i] = new NVector4(uv0[i].x, uv0[i].y, 0, 0);
            }
            var uv1 = (Vector2[])mesh.uv2;
            if (uv1 != null && uv1.Length == vc)
            {
                md.UV[1] = new NVector4[vc];
                md.UVDimension[1] = 2;
                for (int i = 0; i < vc; i++) md.UV[1][i] = new NVector4(uv1[i].x, uv1[i].y, 0, 0);
            }
            var bw = (BoneWeight[])mesh.boneWeights;
            if (bw != null && bw.Length == vc)
            {
                md.Weights = new BoneWeight4[vc];
                for (int i = 0; i < vc; i++)
                    md.Weights[i] = new BoneWeight4
                    {
                        I0 = bw[i].boneIndex0, W0 = bw[i].weight0,
                        I1 = bw[i].boneIndex1, W1 = bw[i].weight1,
                        I2 = bw[i].boneIndex2, W2 = bw[i].weight2,
                        I3 = bw[i].boneIndex3, W3 = bw[i].weight3,
                    };
            }
            var bp = (Matrix4x4[])mesh.bindposes;
            if (bp != null && bp.Length > 0)
            {
                md.Bindposes = new NMatrix[bp.Length];
                for (int i = 0; i < bp.Length; i++) md.Bindposes[i] = bp[i].N();
            }
            for (int s = 0; s < mesh.subMeshCount; s++)
                md.Submeshes.Add((int[])mesh.GetTriangles(s));

            for (int k = 0; k < mesh.blendShapeCount; k++)
            {
                var shape = new BlendShape { Name = mesh.GetBlendShapeName(k) };
                int frames = mesh.GetBlendShapeFrameCount(k);
                shape.Frames = new BlendFrame[frames];
                for (int f = 0; f < frames; f++)
                {
                    var dv = new Il2CppStructArray<Vector3>(vc);
                    var dn = new Il2CppStructArray<Vector3>(vc);
                    var dt = new Il2CppStructArray<Vector3>(vc);
                    mesh.GetBlendShapeFrameVertices(k, f, dv, dn, dt);
                    var idx = new List<int>();
                    var p = new List<NVector3>(); var nn = new List<NVector3>(); var tt = new List<NVector3>();
                    for (int i = 0; i < vc; i++)
                    {
                        var a = dv[i]; var b = dn[i]; var d = dt[i];
                        if (a.sqrMagnitude == 0f && b.sqrMagnitude == 0f && d.sqrMagnitude == 0f) continue;
                        idx.Add(i); p.Add(a.N()); nn.Add(b.N()); tt.Add(d.N());
                    }
                    shape.Frames[f] = new BlendFrame
                    {
                        Weight = mesh.GetBlendShapeFrameWeight(k, f),
                        Indices = idx.ToArray(),
                        DeltaPositions = p.ToArray(),
                        DeltaNormals = nn.ToArray(),
                        DeltaTangents = tt.ToArray(),
                    };
                }
                md.BlendShapes.Add(shape);
            }
            return md;
        }
    }

    internal static class UnityExt
    {
        /// <summary>True when the wrapper is null, collected or the Unity object was destroyed.</summary>
        public static bool IsDead(this UnityEngine.Object o) => o == null || o.WasCollected;
    }
}
