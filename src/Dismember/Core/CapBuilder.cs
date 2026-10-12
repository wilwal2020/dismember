using System;
using System.Collections.Generic;
using System.Numerics;

namespace Dismember.Core
{
    /// <summary>
    /// Builds the geometry that closes a cut: a flesh polygon, a thin skin/fat rim along the outline and a bone
    /// disc wherever a bone passes through. All three sample one procedurally generated atlas texture:
    ///   u 0.00-0.50 flesh (planar mapped), u 0.50-0.75 rim gradient (skin -> fat), u 0.75-1.00 bone cross-section.
    /// </summary>
    public sealed class CapGeometry
    {
        public readonly List<Vector3> Positions = new List<Vector3>();
        public readonly List<Vector3> Normals = new List<Vector3>();
        public readonly List<Vector4> Tangents = new List<Vector4>();
        public readonly List<Vector2> UV = new List<Vector2>();
        public readonly List<int> Triangles = new List<int>();
        /// <summary>Segment each vertex should be skinned to.</summary>
        public readonly List<int> VertexSegment = new List<int>();

        public int VertexCount => Positions.Count;
    }

    public static class CapBuilder
    {
        public const float FleshUVScale = 0.8f;   // metres covered by the flesh region of the atlas

        /// <summary>Longest triangle edge where the face is divided between pieces.</summary>
        private const float DivisionDetail = 0.02f;

        /// <summary>Appends a cap for <paramref name="outline"/> (plane coordinates) that belongs to one piece.</summary>
        public static void Build(
            CapGeometry geo,
            IReadOnlyList<Vector2> outline,
            CutRegion region,
            float facing,
            IReadOnlyList<(Vector2 center, float radius)> bones,
            int segment,
            CutOptions opts)
        {
            var owner = new int[outline.Count];
            BuildShared(_ => geo, outline, owner, null, region, facing, bones, _ => segment, opts);
        }

        /// <summary>
        /// Appends the cap for one cross-section that is shared between several pieces on the same side of the plane
        /// (the torso and an arm when the cut runs through the armpit, both halves of an elbow...).
        /// <paramref name="owner"/> says which piece owns each outline point (-1: whoever is nearest). The whole outline
        /// is filled once and every bit of it goes to the piece whose stretch of outline is closest, so the pieces'
        /// faces fit together with no gap between them.
        /// <paramref name="pointSegment"/> (optional) is the body each outline point's flesh rides on: each part of the
        /// face then rides on the body of the flesh next to it, so a face spanning the torso and an arm stump moves with
        /// both instead of staying with one. -1 or null: <paramref name="segmentFor"/> of its piece.
        /// <paramref name="facing"/> is +1 to face along the plane normal, -1 to face against it.
        /// </summary>
        /// <param name="lift">Optional: how far the face is moved off the cut surface at each point, along the side it
        /// faces away from (a slit's walls, pulled apart). Normals follow the slope that gives.</param>
        /// <param name="rimAlong">Optional: whether the outline edge between two points gets the skin/fat rim (not where a
        /// slit's walls meet inside the body).</param>
        /// <param name="fillDetail">Optional: fill the face on a grid this fine, so a lift that varies across it is followed
        /// everywhere (a plain fill has long triangles that only see the lift at the outline).</param>
        public static void BuildShared(
            Func<int, CapGeometry> geoFor,
            IReadOnlyList<Vector2> outline,
            IReadOnlyList<int> owner,
            IReadOnlyList<int> pointSegment,
            CutRegion region,
            float facing,
            IReadOnlyList<(Vector2 center, float radius)> bones,
            Func<int, int> segmentFor,
            CutOptions opts,
            Func<Vector2, float> lift = null,
            Func<Vector2, Vector2, bool> rimAlong = null,
            float fillDetail = 0f)
        {
            // Clean outline (no repeated points), counter-clockwise, every point owned.
            var poly = new List<Vector2>(outline.Count);
            var own = new List<int>(outline.Count);
            var pseg = new List<int>(outline.Count);
            for (int i = 0; i < outline.Count; i++)
            {
                if (poly.Count > 0 && Vector2.DistanceSquared(poly[poly.Count - 1], outline[i]) < 1e-12f) continue;
                poly.Add(outline[i]);
                own.Add(owner[i]);
                pseg.Add(pointSegment != null ? pointSegment[i] : -1);
            }
            while (poly.Count > 1 && Vector2.DistanceSquared(poly[0], poly[poly.Count - 1]) < 1e-12f)
            {
                poly.RemoveAt(poly.Count - 1);
                own.RemoveAt(own.Count - 1);
                pseg.RemoveAt(pseg.Count - 1);
            }
            int n = poly.Count;
            if (n < 3) return;
            float area = MathX.SignedArea(poly);
            if (Math.Abs(area) < 1e-7f) return;
            if (area < 0f)
            {
                poly.Reverse();
                own.Reverse();
                pseg.Reverse();
            }
            if (!FillUnowned(own)) return;
            var centroid = MathX.PolygonCentroid(poly);

            var tangent = new Vector4(region.AxisU, 1f);
            float off = opts.CapOffset;

            // Where a point of the face goes: on the cut surface, moved back into its own side by the lift (if any).
            float Away(Vector2 q) => lift == null ? 0f : -facing * lift(q);
            Vector3 At(Vector2 q, float extra) => region.To3D(q, Away(q) + extra);
            Vector3 NormalAt(Vector2 q)
            {
                var nrm = region.NormalAt(q);
                if (lift != null)
                {
                    const float h = 0.002f;
                    float du = (Away(q + new Vector2(h, 0f)) - Away(q - new Vector2(h, 0f))) / (2f * h);
                    float dw = (Away(q + new Vector2(0f, h)) - Away(q - new Vector2(0f, h))) / (2f * h);
                    nrm = MathX.SafeNormalize(nrm - region.AxisU * du - region.AxisW * dw, nrm);
                }
                return nrm * facing;
            }

            // Who owns a point inside: the owner of the nearest stretch of outline.
            int NearestOwner(Vector2 q)
            {
                int best = own[0];
                float bestD = float.MaxValue;
                for (int i = 0; i < n; i++)
                {
                    int j = (i + 1) % n;
                    float d = MathX.DistanceToSegment(q, poly[i], poly[j]);
                    if (d >= bestD) continue;
                    bestD = d;
                    best = Vector2.DistanceSquared(q, poly[i]) <= Vector2.DistanceSquared(q, poly[j]) ? own[i] : own[j];
                }
                return best;
            }

            // The body a point of the face rides on: that of the nearest outline point of the same piece.
            int SegAt(int o, Vector2 q)
            {
                int best = -1;
                float bestD = float.MaxValue;
                for (int i = 0; i < n; i++)
                {
                    if (own[i] != o || pseg[i] < 0) continue;
                    float d = Vector2.DistanceSquared(q, poly[i]);
                    if (d < bestD) { bestD = d; best = pseg[i]; }
                }
                return best >= 0 ? best : segmentFor(o);
            }

            bool shared = false;
            for (int i = 1; i < n; i++) if (own[i] != own[0]) { shared = true; break; }

            // 1) Flesh: fill the whole outline once, then hand out the triangles.
            var verts = new List<Vector2>(poly);
            var tris = new List<int>();
            if (fillDetail > 0f) GridFill(poly, fillDetail, verts, tris);
            else Triangulator.Fill(verts, n, tris);
            var flesh = new Dictionary<int, (List<Vector2> pts, Dictionary<Vector2, int> index, List<int> tris)>();
            void Emit(int o, Vector2 a, Vector2 b, Vector2 c)
            {
                if (!flesh.TryGetValue(o, out var f)) flesh[o] = f = (new List<Vector2>(), new Dictionary<Vector2, int>(), new List<int>());
                foreach (var q in new[] { a, b, c })
                {
                    if (!f.index.TryGetValue(q, out int k)) { k = f.pts.Count; f.pts.Add(q); f.index[q] = k; }
                    f.tris.Add(k);
                }
            }
            void Assign(Vector2 a, Vector2 b, Vector2 c, int depth)
            {
                if (!shared) { Emit(own[0], a, b, c); return; }
                int oc = NearestOwner((a + b + c) / 3f);
                float longest = MathF.Max(Vector2.DistanceSquared(a, b), MathF.Max(Vector2.DistanceSquared(b, c), Vector2.DistanceSquared(c, a)));
                bool uniform = NearestOwner(a) == oc && NearestOwner(b) == oc && NearestOwner(c) == oc;
                if (uniform || depth >= 6 || longest <= DivisionDetail * DivisionDetail)
                {
                    Emit(oc, a, b, c);
                    return;
                }
                var ab = (a + b) * 0.5f; var bc = (b + c) * 0.5f; var ca = (c + a) * 0.5f;
                Assign(a, ab, ca, depth + 1);
                Assign(ab, b, bc, depth + 1);
                Assign(ca, bc, c, depth + 1);
                Assign(ab, bc, ca, depth + 1);
            }
            for (int i = 0; i + 2 < tris.Count; i += 3)
            {
                var a = verts[tris[i]]; var b = verts[tris[i + 1]]; var c = verts[tris[i + 2]];
                if (MathX.Cross(b - a, c - a) <= 0f) continue;
                Assign(a, b, c, 0);
            }
            foreach (var kv in flesh)
            {
                var geo = geoFor(kv.Key);
                if (geo == null) continue;
                int start = geo.VertexCount;
                foreach (var q in kv.Value.pts) AddVertex(geo, At(q, 0f), NormalAt(q), tangent, FleshUV(q), SegAt(kv.Key, q));
                AddTriangles(geo, kv.Value.tris, start, facing);
            }

            // 2) Skin/fat rim along each piece's own stretch of outline, slightly in front of the flesh.
            if (opts.RimWidth > 0f)
            {
                var inner = new Vector2[n];
                for (int i = 0; i < n; i++)
                {
                    var q = poly[i];
                    var d0 = q - poly[(i - 1 + n) % n];
                    var d1 = poly[(i + 1) % n] - q;
                    var inward = Perp(MathX.SafeNormalize2(d0)) + Perp(MathX.SafeNormalize2(d1));
                    if (inward.LengthSquared() < 1e-8f) inward = centroid - q;
                    inward = MathX.SafeNormalize2(inward);
                    float w = Math.Min(opts.RimWidth, Vector2.Distance(centroid, q) * 0.35f);
                    inner[i] = q + inward * w;
                }
                float rimLift = off * facing;
                for (int i = 0; i < n; i++)
                {
                    int j = (i + 1) % n;
                    if (rimAlong != null && !rimAlong(poly[i], poly[j])) continue;
                    int o = own[i];
                    var geo = geoFor(o);
                    if (geo == null) continue;
                    int si = SegAt(o, poly[i]), sj = SegAt(o, poly[j]);
                    int s = geo.VertexCount;
                    AddVertex(geo, At(poly[i], rimLift), NormalAt(poly[i]), tangent, new Vector2(0.505f, 0.5f), si);
                    AddVertex(geo, At(poly[j], rimLift), NormalAt(poly[j]), tangent, new Vector2(0.505f, 0.5f), sj);
                    AddVertex(geo, At(inner[j], rimLift), NormalAt(inner[j]), tangent, new Vector2(0.745f, 0.5f), sj);
                    AddVertex(geo, At(inner[i], rimLift), NormalAt(inner[i]), tangent, new Vector2(0.745f, 0.5f), si);
                    AddTriangles(geo, new List<int> { 0, 1, 2, 0, 2, 3 }, s, facing);
                }
            }

            // 3) Bone discs, on whichever piece holds that part of the face.
            if (bones != null)
            {
                const int Ring = 16;
                foreach (var (c, r0) in bones)
                {
                    if (!MathX.PointInPolygon(c, poly)) continue;
                    float edge = MathX.DistanceToPolygon(c, poly);
                    float r = Math.Min(r0, edge * 0.8f);
                    if (r < 0.003f) continue;
                    int o = shared ? NearestOwner(c) : own[0];
                    var geo = geoFor(o);
                    if (geo == null) continue;
                    int seg = SegAt(o, c);
                    int start = geo.VertexCount;
                    float discLift = off * 2f * facing;
                    var discNormal = NormalAt(c);
                    AddVertex(geo, At(c, discLift), discNormal, tangent, new Vector2(0.875f, 0.5f), seg);
                    for (int k = 0; k < Ring; k++)
                    {
                        float a = k * MathF.PI * 2f / Ring;
                        var d = new Vector2(MathF.Cos(a), MathF.Sin(a));
                        AddVertex(geo, At(c + d * r, discLift), discNormal, tangent, new Vector2(0.875f, 0.5f) + d * 0.105f, seg);
                    }
                    var disc = new List<int>(Ring * 3);
                    for (int k = 0; k < Ring; k++)
                    {
                        disc.Add(0); disc.Add(1 + k); disc.Add(1 + (k + 1) % Ring);
                    }
                    AddTriangles(geo, disc, start, facing);
                }
            }
        }

        /// <summary>
        /// Fills a polygon cell by cell on a square grid of side <paramref name="h"/>: every cell is the polygon clipped to that
        /// square, filled on its own. Cells meet along the grid lines at the same points, so the fill has no cracks.
        /// Appends vertices to <paramref name="verts"/> and counter-clockwise triangles to <paramref name="tris"/>.
        /// </summary>
        private static void GridFill(List<Vector2> poly, float h, List<Vector2> verts, List<int> tris)
        {
            verts.Clear();
            var min = new Vector2(float.MaxValue);
            var max = new Vector2(float.MinValue);
            foreach (var q in poly) { min = Vector2.Min(min, q); max = Vector2.Max(max, q); }
            int cu = (int)MathF.Ceiling((max.X - min.X) / h), cw = (int)MathF.Ceiling((max.Y - min.Y) / h);
            if ((long)cu * cw > 4000) h = MathF.Sqrt((max.X - min.X) * (max.Y - min.Y) / 4000f) + 1e-4f;   // keep it sane
            float u0 = MathF.Floor(min.X / h) * h, w0 = MathF.Floor(min.Y / h) * h;
            var cell = new List<Vector2>();
            var scratch = new List<Vector2>();
            var local = new List<int>();
            for (int i = 0; u0 + i * h < max.X; i++)
            {
                float ua = u0 + i * h, ub = u0 + (i + 1) * h;
                for (int j = 0; w0 + j * h < max.Y; j++)
                {
                    float wa = w0 + j * h, wb = w0 + (j + 1) * h;
                    cell.Clear();
                    cell.AddRange(poly);
                    Clip(cell, scratch, q => q.X - ua);
                    Clip(cell, scratch, q => ub - q.X);
                    Clip(cell, scratch, q => q.Y - wa);
                    Clip(cell, scratch, q => wb - q.Y);
                    if (cell.Count < 3 || MathF.Abs(MathX.SignedArea(cell)) < 1e-9f) continue;
                    local.Clear();
                    Triangulator.Triangulate(cell, local);
                    int start = verts.Count;
                    verts.AddRange(cell);
                    foreach (int k in local) tris.Add(start + k);
                }
            }
        }

        /// <summary>Keeps the part of a polygon where <paramref name="inside"/> (a linear function) is not negative.</summary>
        private static void Clip(List<Vector2> pts, List<Vector2> scratch, Func<Vector2, float> inside)
        {
            int n = pts.Count;
            scratch.Clear();
            for (int i = 0; i < n; i++)
            {
                var a = pts[i];
                var b = pts[(i + 1) % n];
                float fa = inside(a), fb = inside(b);
                if (fa >= 0f) scratch.Add(a);
                if ((fa >= 0f) != (fb >= 0f)) scratch.Add(Vector2.Lerp(a, b, fa / (fa - fb)));
            }
            pts.Clear();
            pts.AddRange(scratch);
        }

        private static Vector2 Perp(Vector2 d) => new Vector2(-d.Y, d.X);   // left of the direction: inside a CCW outline

        /// <summary>Gives unowned outline points to their nearest owned neighbour. False if nobody owns any of it.</summary>
        private static bool FillUnowned(List<int> own)
        {
            int n = own.Count;
            int any = own.FindIndex(o => o >= 0);
            if (any < 0) return false;
            for (int pass = 0; pass < 2; pass++)
                for (int k = 0; k < n; k++)
                {
                    int i = (any + k) % n;
                    if (own[i] < 0) own[i] = own[(i - 1 + n) % n];
                }
            return true;
        }

        public static Vector2 FleshUV(Vector2 q) =>
            new Vector2(
                MathX.Clamp(0.25f + q.X / FleshUVScale * 0.5f, 0.01f, 0.49f),
                MathX.Clamp(0.5f + q.Y / FleshUVScale, 0.01f, 0.99f));

        private static void AddVertex(CapGeometry g, Vector3 p, Vector3 n, Vector4 t, Vector2 uv, int segment)
        {
            g.Positions.Add(p);
            g.Normals.Add(n);
            g.Tangents.Add(t);
            g.UV.Add(uv);
            g.VertexSegment.Add(segment);
        }

        /// <summary>Adds counter-clockwise (in plane space) triangles, flipping them when the cap faces -Normal.</summary>
        private static void AddTriangles(CapGeometry g, List<int> tris, int offset, float facing)
        {
            for (int i = 0; i + 2 < tris.Count; i += 3)
            {
                g.Triangles.Add(offset + tris[i]);
                if (facing > 0f)
                {
                    g.Triangles.Add(offset + tris[i + 1]);
                    g.Triangles.Add(offset + tris[i + 2]);
                }
                else
                {
                    g.Triangles.Add(offset + tris[i + 2]);
                    g.Triangles.Add(offset + tris[i + 1]);
                }
            }
        }
    }
}
