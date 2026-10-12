using System;
using System.Collections.Generic;
using System.Numerics;

namespace Dismember.Core
{
    /// <summary>A wound strip built by <see cref="GashBuilder"/>, ready to be skinned to the ragdoll bodies.</summary>
    public sealed class Gash
    {
        public MeshData Mesh;
        /// <summary>Output bone index -> segment (bones are the segments' physics bodies).</summary>
        public int[] BoneSegments;
        public Vector3 Center;
        public Vector3 Normal;
        public float Length;
    }

    /// <summary>
    /// The open wound a blade leaves where it cut into the body without going all the way through: a strip on the
    /// skin along the line the edge went in, skin and fat at its lips and raw flesh down the middle, tapering to a
    /// point at both ends. It uses the cut-face atlas (see <see cref="CapBuilder"/>).
    /// </summary>
    public static class GashBuilder
    {
        private const float Lift = 0.003f;   // above the skin, so animation squash does not swallow it

        /// <summary>
        /// Builds the wound along the part of <paramref name="loop"/> (a section outline in plane coordinates U/W)
        /// the edge has cut into: W up to <paramref name="depth"/>, U between <paramref name="uMin"/> and
        /// <paramref name="uMax"/>. <paramref name="bodyWorld"/> gives each segment's body matrix at the time the outline
        /// was taken. Returns null when there is nothing to show.
        /// </summary>
        public static Gash Build(SectionLoop loop, float depth, float uMin, float uMax, Vector3 planeNormal, float halfWidth,
                                 Func<int, Matrix4x4> bodyWorld)
        {
            int n = loop.Points2D.Count;
            if (n < 3 || loop.Normals3D.Count != n || loop.PointSegment.Count != n) return null;

            // How far inside the cut each outline point is (positive = cut).
            var m = new float[n];
            int cutCount = 0;
            for (int i = 0; i < n; i++)
            {
                var q = loop.Points2D[i];
                m[i] = Math.Min(depth - q.Y, Math.Min(q.X - uMin, uMax - q.X));
                if (m[i] > 0f) cutCount++;
            }
            if (cutCount == 0) return null;

            var arcs = new List<List<(Vector3 p, Vector3 nrm, int seg)>>();
            var closed = new List<bool>();
            if (cutCount == n)
            {
                var all = new List<(Vector3, Vector3, int)>(n);
                for (int i = 0; i < n; i++) all.Add(Point(loop, i));
                arcs.Add(all);
                closed.Add(true);
            }
            else
            {
                // Runs of cut points, each closed off where the outline leaves the cut.
                int start = 0;
                while (m[start] > 0f) start++;   // start on an uncut point so no run wraps around
                List<(Vector3, Vector3, int)> run = null;
                for (int k = 1; k <= n; k++)
                {
                    int i = (start + k) % n, prev = (start + k - 1) % n;
                    bool inCut = m[i] > 0f;
                    if (inCut && run == null)
                    {
                        run = new List<(Vector3, Vector3, int)> { Crossing(loop, prev, i, m), Point(loop, i) };
                    }
                    else if (inCut)
                    {
                        run.Add(Point(loop, i));
                    }
                    else if (run != null)
                    {
                        run.Add(Crossing(loop, prev, i, m));
                        arcs.Add(run);
                        closed.Add(false);
                        run = null;
                    }
                }
            }

            var g = new GashGeometry();
            float total = 0f;
            var centre = Vector3.Zero;
            var normalSum = Vector3.Zero;
            int centreCount = 0;
            for (int a = 0; a < arcs.Count; a++)
            {
                var arc = arcs[a];
                float len = 0f;
                for (int i = 1; i < arc.Count; i++) len += Vector3.Distance(arc[i - 1].p, arc[i].p);
                if (arc.Count < 2 || len < 0.01f) continue;
                total += len;
                foreach (var pt in arc) { centre += pt.p; normalSum += pt.nrm; centreCount++; }
                Ribbon(g, arc, closed[a], len, Vector3.Normalize(planeNormal), halfWidth);
            }
            if (g.Positions.Count == 0) return null;

            // Skin each vertex to its segment's body.
            var segs = new List<int>();
            var map = new Dictionary<int, int>();
            var md = new MeshData { Name = "DismemberGash" };
            md.Positions = g.Positions.ToArray();
            md.Normals = g.Normals.ToArray();
            md.Tangents = g.Tangents.ToArray();
            md.UV[0] = g.UV.ConvertAll(u => new Vector4(u.X, u.Y, 0, 0)).ToArray();
            md.UVDimension[0] = 2;
            md.Weights = new BoneWeight4[g.Positions.Count];
            for (int v = 0; v < g.Positions.Count; v++)
            {
                int seg = g.Segment[v];
                if (!map.TryGetValue(seg, out int b)) { b = segs.Count; segs.Add(seg); map[seg] = b; }
                md.Weights[v] = BoneWeight4.Single(b);
            }
            md.Bindposes = new Matrix4x4[segs.Count];
            for (int k = 0; k < segs.Count; k++) md.Bindposes[k] = MathX.InvertOrIdentity(bodyWorld(segs[k]));
            md.Submeshes.Add(g.Triangles.ToArray());
            return new Gash
            {
                Mesh = md,
                BoneSegments = segs.ToArray(),
                Center = centre / Math.Max(1, centreCount),
                Normal = MathX.SafeNormalize(normalSum, Vector3.UnitY),
                Length = total,
            };
        }

        private sealed class GashGeometry
        {
            public readonly List<Vector3> Positions = new List<Vector3>();
            public readonly List<Vector3> Normals = new List<Vector3>();
            public readonly List<Vector4> Tangents = new List<Vector4>();
            public readonly List<Vector2> UV = new List<Vector2>();
            public readonly List<int> Segment = new List<int>();
            public readonly List<int> Triangles = new List<int>();
        }

        private static (Vector3 p, Vector3 nrm, int seg) Point(SectionLoop loop, int i) =>
            (loop.Points3D[i], loop.Normals3D[i], loop.PointSegment[i]);

        /// <summary>Where the outline crosses out of the cut between points a (one side) and b (the other).</summary>
        private static (Vector3 p, Vector3 nrm, int seg) Crossing(SectionLoop loop, int a, int b, float[] m)
        {
            float t = Math.Abs(m[a] - m[b]) < 1e-9f ? 0.5f : MathX.Clamp01(m[a] / (m[a] - m[b]));
            var p = Vector3.Lerp(loop.Points3D[a], loop.Points3D[b], t);
            var nrm = MathX.SafeNormalize(Vector3.Lerp(loop.Normals3D[a], loop.Normals3D[b], t), loop.Normals3D[m[a] > 0f ? a : b]);
            return (p, nrm, loop.PointSegment[m[a] > 0f ? a : b]);
        }

        /// <summary>
        /// Five rows of vertices across the wound: lip, inner edge of the skin/fat band, raw centre, and back out.
        /// The two halves get normals tilted towards each other so the strip shades like a groove.
        /// </summary>
        private static void Ribbon(GashGeometry g, List<(Vector3 p, Vector3 nrm, int seg)> arc, bool closed, float length, Vector3 planeNormal, float halfWidth)
        {
            int count = arc.Count;
            float taper = Math.Min(0.03f, length / 3f);
            const int Row = 7;
            var pos = new Vector3[count * Row];
            var nrms = new Vector3[count * Row];
            var uvs = new Vector2[count * Row];
            var tans = new Vector4[count];
            float s = 0f;
            for (int i = 0; i < count; i++)
            {
                if (i > 0) s += Vector3.Distance(arc[i - 1].p, arc[i].p);
                var (p, nrm, _) = arc[i];
                if (nrm.LengthSquared() < 0.5f) nrm = planeNormal;   // no surface normal: any will do
                // Across the wound: the plane normal laid onto the skin.
                var across = planeNormal - nrm * Vector3.Dot(planeNormal, nrm);
                across = MathX.SafeNormalize(across, MathX.AnyPerpendicular(nrm));
                // Along the wound, for the tangent frame.
                var along = arc[Math.Min(count - 1, i + 1)].p - arc[Math.Max(0, i - 1)].p;
                along = MathX.SafeNormalize(along - nrm * Vector3.Dot(along, nrm), Vector3.Cross(nrm, across));
                float f = closed ? 1f : MathX.Clamp01(Math.Min(s, length - s) / Math.Max(1e-4f, taper));
                f = MathF.Sqrt(f);   // rounder ends
                float w = halfWidth * f;
                var lift = nrm * Lift;
                var wallL = MathX.SafeNormalize(nrm - across * 0.7f, nrm);   // the +across half faces back towards the middle
                var wallR = MathX.SafeNormalize(nrm + across * 0.7f, nrm);
                tans[i] = new Vector4(along, 1f);
                float v = s % 0.6f - 0.3f;   // flesh texture runs along the wound
                int r = i * Row;
                // 0 lip+, 1 band+, 2 flesh+, 3 centre, 4 flesh-, 5 band-, 6 lip-
                (pos[r], nrms[r], uvs[r]) = (p + across * w + lift, wallL, new Vector2(0.505f, 0.5f));
                (pos[r + 1], nrms[r + 1], uvs[r + 1]) = (p + across * (w * 0.55f) + lift, wallL, new Vector2(0.745f, 0.5f));
                (pos[r + 2], nrms[r + 2], uvs[r + 2]) = (p + across * (w * 0.55f) + lift, wallL, CapBuilder.FleshUV(new Vector2(-0.01f, v)));
                (pos[r + 3], nrms[r + 3], uvs[r + 3]) = (p + lift * 1.1f, nrm, CapBuilder.FleshUV(new Vector2(0f, v)));
                (pos[r + 4], nrms[r + 4], uvs[r + 4]) = (p - across * (w * 0.55f) + lift, wallR, CapBuilder.FleshUV(new Vector2(0.01f, v)));
                (pos[r + 5], nrms[r + 5], uvs[r + 5]) = (p - across * (w * 0.55f) + lift, wallR, new Vector2(0.745f, 0.5f));
                (pos[r + 6], nrms[r + 6], uvs[r + 6]) = (p - across * w + lift, wallR, new Vector2(0.505f, 0.5f));
            }

            // Each stretch between two points rides one body part: a wound across the chest and an arm then breaks
            // apart when they move (or are cut apart) instead of stretching between them. Rows are shared while the
            // body part stays the same.
            int Emit(int i, int seg)
            {
                int start = g.Positions.Count;
                for (int k = 0; k < Row; k++)
                {
                    g.Positions.Add(pos[i * Row + k]);
                    g.Normals.Add(nrms[i * Row + k]);
                    g.Tangents.Add(tans[i]);
                    g.UV.Add(uvs[i * Row + k]);
                    g.Segment.Add(seg);
                }
                return start;
            }
            int segments = closed ? count : count - 1;
            int prevRow = -1, prevSeg = int.MinValue;
            for (int i = 0; i < segments; i++)
            {
                int j = (i + 1) % count;
                int seg = arc[i].seg;
                int a = prevSeg == seg && prevRow >= 0 ? prevRow : Emit(i, seg);
                int b = Emit(j, seg);
                prevRow = b;
                prevSeg = seg;
                // Strips: 0-1 (skin band), 2-3 and 3-4 (flesh), 5-6 (skin band).
                foreach (var (x, y) in new[] { (0, 1), (2, 3), (3, 4), (5, 6) })
                    Quad(g, a + x, a + y, b + y, b + x, arc[i].nrm);
            }
        }

        /// <summary>Two triangles, wound so they face along <paramref name="outward"/> (Unity: clockwise from outside).</summary>
        private static void Quad(GashGeometry g, int a, int b, int c, int d, Vector3 outward)
        {
            // The diagonals give the quad's facing even when one side has shrunk to a point (the tapered ends).
            var facing = Vector3.Cross(g.Positions[c] - g.Positions[a], g.Positions[d] - g.Positions[b]);
            bool flip = Vector3.Dot(facing, outward) < 0f;
            if (!flip)
            {
                g.Triangles.Add(a); g.Triangles.Add(b); g.Triangles.Add(c);
                g.Triangles.Add(a); g.Triangles.Add(c); g.Triangles.Add(d);
            }
            else
            {
                g.Triangles.Add(a); g.Triangles.Add(c); g.Triangles.Add(b);
                g.Triangles.Add(a); g.Triangles.Add(d); g.Triangles.Add(c);
            }
        }
    }
}
