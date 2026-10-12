using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;

namespace Dismember.Core
{
    public sealed partial class CutSolver
    {
        private SlitSpec _slit;

        /// <summary>
        /// How far inside the slit (reaching <paramref name="depth"/> along W) a point of the cut surface is: the distance
        /// to its nearest edge (0 or less: outside).
        /// </summary>
        private float SlitInside(Vector2 q, float depth) =>
            Math.Min(Math.Min(q.X - _region.UMin, _region.UMax - q.X), Math.Min(depth - q.Y, q.Y - _region.WMin));

        /// <summary>How far each lip (and wall) of the slit is pulled back from the cut surface at <paramref name="q"/>.</summary>
        private float SlitOpen(Vector2 q, float depth)
        {
            float m = SlitInside(q, depth);
            if (m <= 0f) return 0f;
            float t = MathX.Clamp01(m / Math.Max(1e-4f, _slit.Taper));
            return _slit.Opening * t * (2f - t);
        }

        /// <summary>
        /// Opens a wound in the given cross-sections (ids from <see cref="Analyze"/>) without separating anything: see
        /// <see cref="SlitSpec"/>. The result has one piece, the whole body, with the skin split along the slit and walls
        /// of flesh (its cap) lining the inside.
        /// </summary>
        public CutResult ExecuteSlit(IReadOnlyCollection<int> groups, SlitSpec slit, CutOptions opts = null)
        {
            if (_report == null) Analyze();
            opts ??= new CutOptions();
            _slit = slit;
            var sw = Stopwatch.StartNew();
            var result = new CutResult { CorePiece = 0 };
            _cut = new bool[_segCount];
            _segPiece = new int[_segCount];
            _pieceCount = 1;
            _corePiece = 0;
            result.SegmentCut = new bool[_segCount];
            result.SegmentOwnerSide = new int[_segCount];
            Array.Fill(result.SegmentOwnerSide, -1);
            result.SegmentPiece = new int[_segCount];
            var core = new Piece { Index = 0, IsCore = true };
            for (int s = 0; s < _segCount; s++) core.BodySegments.Add(s);
            result.Pieces.Add(core);

            // Triangles the slit runs through: those crossing the cut surface inside it (with the depth of their section).
            var split = new Dictionary<int, float>[_skins.Length];
            for (int i = 0; i < _skins.Length; i++) split[i] = new Dictionary<int, float>();
            var opened = new List<int>();
            foreach (int gid in groups)
            {
                bool any = false;
                float depth = slit.DepthOf(gid);
                foreach (int li in _report.Groups[gid].Loops)
                    foreach (int e in _report.Loops[li].Edges)
                    {
                        var h = _isect[e];
                        if (Math.Max(SlitInside(h.A2, depth), SlitInside(h.B2, depth)) <= 0f) continue;
                        split[h.Skin][h.Tri] = depth;
                        any = true;
                    }
                if (any) opened.Add(gid);
            }

            for (int si = 0; si < _skins.Length; si++)
            {
                if (split[si].Count == 0)
                    core.Renderers.Add(new RendererPiece { SkinId = _skins[si].Id, Unchanged = true, UsesOriginalBones = true });
                else
                    core.Renderers.Add(BuildSlitMesh(si, split[si]));
            }
            result.Timings = $"meshes {sw.Elapsed.TotalMilliseconds:F1}ms; ";

            if (opts.MakeCaps && opened.Count > 0)
            {
                var geo = new CapGeometry();
                foreach (int gid in opened) BuildWalls(geo, gid, opts);
                if (geo.VertexCount > 0) core.Cap = MakeCapPiece(geo, core);
            }
            result.Milliseconds = (float)sw.Elapsed.TotalMilliseconds;
            return result;
        }

        /// <summary>
        /// One renderer with the slit opened in it: every triangle the slit runs through is split along it, each side
        /// keeping its own copy of the new points, pulled back to its own side. Skin right beside the slit goes back with
        /// it, so the lips never fold over the skin next to them.
        /// </summary>
        private RendererPiece BuildSlitMesh(int si, Dictionary<int, float> split)
        {
            var skin = _skins[si];
            var mesh = skin.Mesh;
            var topo = mesh.Topology;
            var dist = _dist[si];
            var pos = _pos[si];
            int vc = mesh.VertexCount;
            EnsureFrames(si);

            var o = new OutMesh { Piece = 0, Core = true, Sub = new List<int>[mesh.Submeshes.Count] };
            for (int k = 0; k < o.Sub.Length; k++) o.Sub[k] = new List<int>();
            for (int k = 0; k < MeshData.MaxUVChannels; k++) if (mesh.UV[k] != null) o.UV[k] = new List<Vector4>();

            BoneWeight4 WeightsOf(int v) => mesh.Weights != null ? mesh.Weights[v] : BoneWeight4.Single(0);

            const float Clear = 0.0015f;
            var moved = new Dictionary<int, Vector3>();
            foreach (var kv in split)
            {
                for (int c = 0; c < 3; c++)
                {
                    int v = topo.Tri[kv.Key * 3 + c];
                    if (moved.ContainsKey(v)) continue;
                    var q = _region.To2D(pos[v]);
                    float open = SlitOpen(q, kv.Value);
                    float d = dist[v];
                    if (open <= 0f || Math.Abs(d) >= open + Clear) continue;
                    float want = (d > 0f ? 1f : -1f) * (open + Clear);
                    moved[v] = _region.NormalAt(q) * (want - d);
                }
            }

            int Vert(int v)
            {
                if (o.FromOrig.TryGetValue(v, out int idx)) return idx;
                idx = o.Pos.Count;
                o.FromOrig[v] = idx;
                if (moved.TryGetValue(v, out var shift))
                {
                    var w = WeightsOf(v);
                    var n = _nrm[si] != null ? _nrm[si][v] : Vector3.UnitY;
                    var t = _tan[si] != null ? _tan[si][v] : new Vector4(1, 0, 0, 1);
                    Rebind(si, w, pos[v] + shift, n, t, out var pb, out var nb, out var tb);
                    PushVertex(o, mesh, v, pb, nb, tb, w, v);
                }
                else
                {
                    PushVertex(o, mesh, v, mesh.Positions[v],
                        mesh.Normals != null ? mesh.Normals[v] : Vector3.UnitY,
                        mesh.Tangents != null ? mesh.Tangents[v] : new Vector4(1, 0, 0, 1),
                        WeightsOf(v), v);
                }
                return idx;
            }

            // The slit's points on edge a-b: one copy per side, skinned like that side's corner.
            var edges = new Dictionary<(long, int), int>();
            int EdgeVert(int a, int b, int side, int from, float depth)
            {
                int lo = Math.Min(a, b), hi = Math.Max(a, b);
                var key = (((long)lo << 32) | (uint)hi, side);
                if (edges.TryGetValue(key, out int idx)) return idx;
                idx = o.Pos.Count;
                edges[key] = idx;
                float dl = dist[lo], dh = dist[hi];
                float t = dl / (dl - dh);
                var p = Vector3.Lerp(pos[lo], pos[hi], t);
                var q = _region.To2D(p);
                float open = SlitOpen(q, depth);
                p += _region.NormalAt(q) * (side == 0 ? open : -open);
                var n = _nrm[si] != null ? MathX.SafeNormalize(Vector3.Lerp(_nrm[si][lo], _nrm[si][hi], t), Vector3.UnitY) : Vector3.UnitY;
                var tw = _tan[si] != null ? Vector4.Lerp(_tan[si][lo], _tan[si][hi], t) : new Vector4(1, 0, 0, 1);
                tw = new Vector4(MathX.SafeNormalize(new Vector3(tw.X, tw.Y, tw.Z), Vector3.UnitX), _tan[si] != null ? _tan[si][lo].W : 1f);
                var w = WeightsOf(from);
                Rebind(si, w, p, n, tw, out var pb, out var nb, out var tb);
                o.Pos.Add(pb);
                o.Nrm.Add(nb);
                o.Tan.Add(tb);
                if (mesh.Colors != null) o.Col.Add(Vector4.Lerp(mesh.Colors[lo], mesh.Colors[hi], t));
                for (int k = 0; k < MeshData.MaxUVChannels; k++)
                    if (o.UV[k] != null) o.UV[k].Add(Vector4.Lerp(mesh.UV[k][lo], mesh.UV[k][hi], t));
                o.W.Add(w);
                o.SourceVertex.Add(-1);
                return idx;
            }

            for (int f = 0; f < topo.TriangleCount; f++)
            {
                int sub = topo.TriSubmesh[f];
                int i0 = topo.Tri[f * 3], i1 = topo.Tri[f * 3 + 1], i2 = topo.Tri[f * 3 + 2];
                var list = o.Sub[sub];
                if (!split.TryGetValue(f, out float depth))
                {
                    list.Add(Vert(i0)); list.Add(Vert(i1)); list.Add(Vert(i2));
                    continue;
                }
                // Rotate so 'a' is the corner alone on its side.
                int a, b, c;
                bool p0 = dist[i0] > 0, p1 = dist[i1] > 0, p2 = dist[i2] > 0;
                if (p0 != p1 && p0 != p2) { a = i0; b = i1; c = i2; }
                else if (p1 != p0 && p1 != p2) { a = i1; b = i2; c = i0; }
                else { a = i2; b = i0; c = i1; }
                int sideA = dist[a] > 0 ? 0 : 1;
                int ab = EdgeVert(a, b, sideA, a, depth), ac = EdgeVert(a, c, sideA, a, depth);
                list.Add(Vert(a)); list.Add(ab); list.Add(ac);
                int ab2 = EdgeVert(a, b, 1 - sideA, b, depth), ac2 = EdgeVert(a, c, 1 - sideA, c, depth);
                int vb = Vert(b), vcIdx = Vert(c);
                list.Add(ab2); list.Add(vb); list.Add(vcIdx);
                list.Add(ab2); list.Add(vcIdx); list.Add(ac2);
            }

            var md = new MeshData { Name = mesh.Name.EndsWith("_cut") ? mesh.Name : mesh.Name + "_cut" };
            md.Positions = o.Pos.ToArray();
            if (mesh.Normals != null) md.Normals = o.Nrm.ToArray();
            if (mesh.Tangents != null) md.Tangents = o.Tan.ToArray();
            if (mesh.Colors != null) md.Colors = o.Col.ToArray();
            for (int k = 0; k < MeshData.MaxUVChannels; k++)
            {
                if (o.UV[k] == null) continue;
                md.UV[k] = o.UV[k].ToArray();
                md.UVDimension[k] = mesh.UVDimension[k];
            }
            foreach (var s in o.Sub) md.Submeshes.Add(s.ToArray());
            md.Weights = o.W.ToArray();
            md.Bindposes = mesh.Bindposes;
            CopyBlendShapes(mesh, o, md);
            return new RendererPiece { SkinId = skin.Id, Mesh = md, UsesOriginalBones = true };
        }

        /// <summary>
        /// The flesh lining a slit through one cross-section: its outline cut down to the part the slit covers, filled
        /// on both sides, each wall pulled back to its side like the lips beside it. The walls meet (no opening) where
        /// the slit ends inside the body, and get the skin/fat rim only along the skin.
        /// </summary>
        private void BuildWalls(CapGeometry geo, int gid, CutOptions opts)
        {
            var g = _report.Groups[gid];
            int capLoop = -1;
            float bestArea = 0f;
            foreach (int li in g.Loops)
            {
                var loop = _report.Loops[li];
                bool allowed = false;
                foreach (int e in loop.Edges) if (_skins[_isect[e].Skin].CapsTriangle(_isect[e].Tri)) { allowed = true; break; }
                if (!allowed) continue;
                if (li == g.OuterLoop) { capLoop = li; break; }
                if (loop.Area > bestArea) { bestArea = loop.Area; capLoop = li; }
            }
            if (capLoop < 0) return;
            var outline = _report.Loops[capLoop];
            var pts = new List<Vector2>(outline.Points2D);
            int fallback = -1;
            foreach (int s in g.Segments) { fallback = s; break; }
            var segs = new List<int>(pts.Count);
            for (int i = 0; i < pts.Count; i++) segs.Add(i < outline.PointSegment.Count ? outline.PointSegment[i] : fallback);

            float depth = _slit.DepthOf(gid);
            var edges = new Func<Vector2, float>[]
            {
                q => q.X - _region.UMin,
                q => _region.UMax - q.X,
                q => depth - q.Y,
                q => q.Y - _region.WMin,
            };
            foreach (var inside in edges) ClipPolygon(pts, segs, inside);
            if (pts.Count < 3 || Math.Abs(MathX.SignedArea(pts)) < 1e-7f) return;

            // Inside the body, along the slit's own edges, the walls just meet: no skin there.
            bool AlongSkin(Vector2 a, Vector2 b)
            {
                foreach (var f in edges)
                    if (Math.Abs(f(a)) < 1e-5f && Math.Abs(f(b)) < 1e-5f) return false;
                return true;
            }

            var bones = new List<(Vector2, float)>();
            foreach (var (seg, q) in g.BoneCrossings)
                if (SlitInside(q, depth) > 0f) bones.Add((q, BoneRadius(_body.Segments[seg].Kind, g.Area)));
            var owner = new int[pts.Count];
            for (int side = 0; side < 2; side++)
            {
                float facing = side == 0 ? -1f : 1f;
                CapBuilder.BuildShared(_ => geo, pts, owner, segs, _region, facing, bones, _ => fallback, opts, q => SlitOpen(q, depth), AlongSkin,
                    MathX.Clamp(_slit.Taper * 0.4f, 0.005f, 0.015f));
            }
        }

        /// <summary>Keeps the part of a polygon where <paramref name="inside"/> (a linear function) is not negative.</summary>
        private static void ClipPolygon(List<Vector2> pts, List<int> tags, Func<Vector2, float> inside)
        {
            int n = pts.Count;
            if (n == 0) return;
            var outP = new List<Vector2>(n + 4);
            var outT = new List<int>(n + 4);
            for (int i = 0; i < n; i++)
            {
                int j = (i + 1) % n;
                var a = pts[i];
                var b = pts[j];
                float fa = inside(a), fb = inside(b);
                if (fa >= 0f) { outP.Add(a); outT.Add(tags[i]); }
                if ((fa >= 0f) != (fb >= 0f))
                {
                    float t = fa / (fa - fb);
                    outP.Add(Vector2.Lerp(a, b, t));
                    outT.Add(fa >= 0f ? tags[i] : tags[j]);
                }
            }
            pts.Clear();
            pts.AddRange(outP);
            tags.Clear();
            tags.AddRange(outT);
        }
    }
}
