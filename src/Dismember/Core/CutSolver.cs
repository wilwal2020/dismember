using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;

namespace Dismember.Core
{
    /// <summary>
    /// Plans and performs a planar cut through a skinned ragdoll.
    ///
    /// Stage 1 (<see cref="Analyze"/>): skin every renderer on the CPU, classify vertices against the plane and trace
    /// the outlines where the plane passes through the body. Outlines are grouped into separate cross-sections
    /// (neck, an arm, the torso...) so the caller can decide, per cross-section, whether the blade gets through.
    ///
    /// Stage 2 (<see cref="Execute"/>): for the cross-sections that are severed, work out which ragdoll bodies end up
    /// in which piece, split the triangles, rebind the vertices and generate caps.
    /// </summary>
    public sealed partial class CutSolver
    {
        private readonly BodyModel _body;
        private readonly SkinInstance[] _skins;
        private readonly PoseInfo _pose;
        private readonly CutRegion _region;
        private readonly int _segCount;

        private Vector3[][] _pos;
        private Vector3[][] _nrm;
        private Vector4[][] _tan;
        private float[][] _dist;
        private int[][] _triSeg;
        private Matrix4x4[][] _skinMat;

        private int[] _segVertsA, _segVertsB;
        private Vector3[] _segCentroid;
        private Vector3[] _segCentroidA, _segCentroidB;   // centroid of each segment's flesh on each side of the plane

        private readonly List<Isect> _isect = new List<Isect>();
        private CrossSectionReport _report;

        private struct Isect
        {
            public int Skin, Tri, Segment;
            public long KeyA, KeyB;
            public Vector3 A3, B3;
            public Vector2 A2, B2;
        }

        public CutSolver(BodyModel body, SkinInstance[] skins, PoseInfo pose, CutRegion region)
        {
            _body = body;
            _skins = skins;
            _pose = pose;
            _region = region;
            _segCount = body.Segments.Length;
        }

        public CutRegion Region => _region;
        public CrossSectionReport Report => _report;

        /// <summary>Where the surface crosses each triangle (index = the ids in <see cref="SectionLoop.Edges"/>), for tests.</summary>
        public IEnumerable<(int Skin, int Tri, Vector2 A, Vector2 B)> Intersections
        {
            get { foreach (var h in _isect) yield return (h.Skin, h.Tri, h.A2, h.B2); }
        }

        // ------------------------------------------------------------------------------------------------
        // Stage 1
        // ------------------------------------------------------------------------------------------------

        public CrossSectionReport Analyze()
        {
            var sw = Stopwatch.StartNew();
            _report = new CrossSectionReport();
            SkinAll();
            _report.MillisecondsSkinning = (float)sw.Elapsed.TotalMilliseconds;
            sw.Restart();
            FindIntersections();
            BuildLoops();
            BuildGroups();
            _report.MillisecondsSections = (float)sw.Elapsed.TotalMilliseconds;
            return _report;
        }

        private int SegmentOfBone(SkinInstance s, int bone)
        {
            if (bone < 0 || s.BoneSegment == null || bone >= s.BoneSegment.Length) return _body.Root;
            int seg = s.BoneSegment[bone];
            return seg < 0 || seg >= _segCount ? _body.Root : seg;
        }

        private void SkinAll()
        {
            int n = _skins.Length;
            _pos = new Vector3[n][];
            _nrm = new Vector3[n][];
            _tan = new Vector4[n][];
            _dist = new float[n][];
            _triSeg = new int[n][];
            _skinMat = new Matrix4x4[n][];
            _segVertsA = new int[_segCount];
            _segVertsB = new int[_segCount];
            _segCentroid = new Vector3[_segCount];
            _segCentroidA = new Vector3[_segCount];
            _segCentroidB = new Vector3[_segCount];
            var segVertCount = new int[_segCount];

            for (int si = 0; si < n; si++)
            {
                var skin = _skins[si];
                var mesh = skin.Mesh;
                int vc = mesh.VertexCount;
                int boneCount = skin.BoneWorld?.Length ?? 0;
                EnsureSegmentCache(skin);

                var mats = new Matrix4x4[Math.Max(1, boneCount)];
                for (int b = 0; b < boneCount; b++)
                {
                    var bind = mesh.Bindposes != null && b < mesh.Bindposes.Length ? mesh.Bindposes[b] : Matrix4x4.Identity;
                    mats[b] = bind * skin.BoneWorld[b];
                }
                if (boneCount == 0) mats[0] = Matrix4x4.Identity;
                _skinMat[si] = mats;

                var basePos = mesh.Positions;
                if (skin.BlendShapeWeights != null && mesh.BlendShapes.Count > 0 && AnyWeight(skin.BlendShapeWeights))
                {
                    basePos = (Vector3[])mesh.Positions.Clone();
                    for (int k = 0; k < mesh.BlendShapes.Count && k < skin.BlendShapeWeights.Length; k++)
                        ApplyBlendShape(mesh.BlendShapes[k], skin.BlendShapeWeights[k], basePos, null);
                }

                var pos = new Vector3[vc];
                var dist = new float[vc];
                var weights = mesh.Weights;
                var vseg = skin.CachedVertSegment;
                var normal = _region.Normal;
                var origin = _region.Origin;
                bool bent = _region.Bent;
                for (int v = 0; v < vc; v++)
                {
                    Vector3 p;
                    if (weights == null || boneCount == 0)
                    {
                        p = Vector3.Transform(basePos[v], mats[0]);
                    }
                    else
                    {
                        var w = weights[v];
                        var bp = basePos[v];
                        p = Vector3.Transform(bp, mats[w.I0]) * w.W0;
                        if (w.W1 > 0f) p += Vector3.Transform(bp, mats[w.I1]) * w.W1;
                        if (w.W2 > 0f) p += Vector3.Transform(bp, mats[w.I2]) * w.W2;
                        if (w.W3 > 0f) p += Vector3.Transform(bp, mats[w.I3]) * w.W3;
                    }
                    pos[v] = p;
                    float d = bent ? _region.SignedDistance(p) : Vector3.Dot(p - origin, normal);
                    if (d < 1e-7f && d > -1e-7f) d = 1e-7f;
                    dist[v] = d;

                    int seg = vseg[v];
                    if (d > 0) { _segVertsA[seg]++; _segCentroidA[seg] += p; }
                    else { _segVertsB[seg]++; _segCentroidB[seg] += p; }
                    _segCentroid[seg] += p;
                    segVertCount[seg]++;
                }
                _pos[si] = pos;
                _dist[si] = dist;
                _triSeg[si] = skin.CachedTriSegment;
            }

            for (int s = 0; s < _segCount; s++)
            {
                _segCentroid[s] = segVertCount[s] > 0 ? _segCentroid[s] / segVertCount[s] : _pose.SegmentOrigin[s];
                _segCentroidA[s] = _segVertsA[s] > 0 ? _segCentroidA[s] / _segVertsA[s] : _segCentroid[s];
                _segCentroidB[s] = _segVertsB[s] > 0 ? _segCentroidB[s] / _segVertsB[s] : _segCentroid[s];
            }
        }

        private static bool AnyWeight(float[] w)
        {
            foreach (var x in w) if (x > 1e-4f || x < -1e-4f) return true;
            return false;
        }

        /// <summary>Vertex and triangle -> segment maps. They only depend on the mesh and bone mapping, so they are cached.</summary>
        private void EnsureSegmentCache(SkinInstance skin)
        {
            var key = (skin.Mesh, skin.BoneSegment);
            if (skin.CachedTriSegment != null && Equals(skin.CacheKey, key)) return;
            var mesh = skin.Mesh;
            var topo = mesh.Topology;
            var vseg = new int[mesh.VertexCount];
            var tseg = new int[topo.TriangleCount];
            if (mesh.Weights == null)
            {
                Array.Fill(vseg, _body.Root);
                Array.Fill(tseg, _body.Root);
            }
            else
            {
                for (int v = 0; v < vseg.Length; v++) vseg[v] = SegmentOfBone(skin, mesh.Weights[v].Dominant);
                var segs = new int[12];
                var sums = new float[12];
                for (int f = 0; f < tseg.Length; f++)
                {
                    int used = 0;
                    for (int c = 0; c < 3; c++)
                    {
                        var w = mesh.Weights[topo.Tri[f * 3 + c]];
                        for (int k = 0; k < 4; k++)
                        {
                            float wk = w.Weight(k);
                            if (wk <= 0f) continue;
                            int seg = SegmentOfBone(skin, w.Bone(k));
                            int slot = 0;
                            while (slot < used && segs[slot] != seg) slot++;
                            if (slot == used) { segs[used] = seg; sums[used] = 0f; used++; }
                            sums[slot] += wk;
                        }
                    }
                    int best = _body.Root; float bestW = -1f;
                    for (int i = 0; i < used; i++)
                        if (sums[i] > bestW || (sums[i] == bestW && segs[i] < best)) { best = segs[i]; bestW = sums[i]; }
                    tseg[f] = best;
                }
            }
            skin.CachedVertSegment = vseg;
            skin.CachedTriSegment = tseg;
            skin.CacheKey = key;
        }

        /// <summary>World normals/tangents at cut time; only needed for renderers that get new meshes.</summary>
        private void EnsureFrames(int si)
        {
            if (_nrm[si] != null || _tan[si] != null) return;
            var skin = _skins[si];
            var mesh = skin.Mesh;
            if (mesh.Normals == null && mesh.Tangents == null) return;
            int vc = mesh.VertexCount;
            var mats = _skinMat[si];
            var baseNrm = mesh.Normals;
            if (baseNrm != null && skin.BlendShapeWeights != null && mesh.BlendShapes.Count > 0 && AnyWeight(skin.BlendShapeWeights))
            {
                baseNrm = (Vector3[])mesh.Normals.Clone();
                var dummy = (Vector3[])mesh.Positions.Clone();
                for (int k = 0; k < mesh.BlendShapes.Count && k < skin.BlendShapeWeights.Length; k++)
                    ApplyBlendShape(mesh.BlendShapes[k], skin.BlendShapeWeights[k], dummy, baseNrm);
            }
            var nrm = baseNrm != null ? new Vector3[vc] : null;
            var tan = mesh.Tangents != null ? new Vector4[vc] : null;
            for (int v = 0; v < vc; v++)
            {
                Vector3 nn = Vector3.Zero, tt = Vector3.Zero;
                var t0 = tan != null ? new Vector3(mesh.Tangents[v].X, mesh.Tangents[v].Y, mesh.Tangents[v].Z) : Vector3.Zero;
                if (mesh.Weights == null)
                {
                    if (nrm != null) nn = Vector3.TransformNormal(baseNrm[v], mats[0]);
                    if (tan != null) tt = Vector3.TransformNormal(t0, mats[0]);
                }
                else
                {
                    var w = mesh.Weights[v];
                    for (int k = 0; k < 4; k++)
                    {
                        float wk = w.Weight(k);
                        if (wk <= 0f) continue;
                        ref var m = ref mats[w.Bone(k)];
                        if (nrm != null) nn += Vector3.TransformNormal(baseNrm[v], m) * wk;
                        if (tan != null) tt += Vector3.TransformNormal(t0, m) * wk;
                    }
                }
                if (nrm != null) nrm[v] = MathX.SafeNormalize(nn, Vector3.UnitY);
                if (tan != null) tan[v] = new Vector4(MathX.SafeNormalize(tt, Vector3.UnitX), mesh.Tangents[v].W);
            }
            _nrm[si] = nrm;
            _tan[si] = tan;
        }

        private static void ApplyBlendShape(BlendShape shape, float weight, Vector3[] pos, Vector3[] nrm)
        {
            if (Math.Abs(weight) < 1e-4f || shape.Frames.Length == 0) return;
            var frames = shape.Frames;
            // Unity interpolates from the rest pose to the first frame, then between consecutive frames.
            BlendFrame a = null, b = frames[0];
            float wa = 0f, wb = frames[0].Weight;
            for (int i = 1; i < frames.Length && weight > wb; i++)
            {
                a = frames[i - 1]; wa = frames[i - 1].Weight;
                b = frames[i]; wb = frames[i].Weight;
            }
            float t = Math.Abs(wb - wa) < 1e-6f ? 1f : (weight - wa) / (wb - wa);
            void Add(BlendFrame f, float s)
            {
                if (f == null || s == 0f) return;
                for (int k = 0; k < f.Indices.Length; k++)
                {
                    int v = f.Indices[k];
                    if (v < 0 || v >= pos.Length) continue;
                    pos[v] += f.DeltaPositions[k] * s;
                    if (nrm != null && f.DeltaNormals != null) nrm[v] += f.DeltaNormals[k] * s;
                }
            }
            Add(a, 1f - t);
            Add(b, t);
        }

        private void FindIntersections()
        {
            for (int si = 0; si < _skins.Length; si++)
            {
                var topo = _skins[si].Mesh.Topology;
                var dist = _dist[si];
                var pos = _pos[si];
                var weld = topo.Weld;
                for (int f = 0; f < topo.TriangleCount; f++)
                {
                    int i0 = topo.Tri[f * 3], i1 = topo.Tri[f * 3 + 1], i2 = topo.Tri[f * 3 + 2];
                    bool s0 = dist[i0] > 0, s1 = dist[i1] > 0, s2 = dist[i2] > 0;
                    if (s0 == s1 && s1 == s2) continue;
                    if (weld[i0] == weld[i1] || weld[i1] == weld[i2] || weld[i0] == weld[i2]) continue;

                    var hit = new Isect { Skin = si, Tri = f, Segment = _triSeg[si][f] };
                    int found = 0;
                    for (int e = 0; e < 3; e++)
                    {
                        int a = topo.Tri[f * 3 + e], b = topo.Tri[f * 3 + (e + 1) % 3];
                        if ((dist[a] > 0) == (dist[b] > 0)) continue;
                        // Canonical direction (by weld id) so neighbouring triangles produce identical points.
                        int lo = a, hi = b;
                        if (weld[lo] > weld[hi]) { lo = b; hi = a; }
                        float t = dist[lo] / (dist[lo] - dist[hi]);
                        var p = pos[lo] + (pos[hi] - pos[lo]) * t;
                        long key = MeshTopology.EdgeKey(weld[a], weld[b]);
                        if (found == 0) { hit.KeyA = key; hit.A3 = p; hit.A2 = _region.To2D(p); }
                        else { hit.KeyB = key; hit.B3 = p; hit.B2 = _region.To2D(p); }
                        found++;
                    }
                    if (found == 2) _isect.Add(hit);
                }
            }
        }

        private sealed class Chain
        {
            public List<int> Isects = new List<int>();
            public List<Vector3> P3 = new List<Vector3>();
            public List<Vector2> P2 = new List<Vector2>();
            public List<int> PointEdge = new List<int>();
            public bool Closed;
        }

        private void BuildLoops()
        {
            var chains = new List<Chain>();
            var bySkin = new Dictionary<int, List<int>>();
            for (int i = 0; i < _isect.Count; i++)
            {
                if (!bySkin.TryGetValue(_isect[i].Skin, out var l)) bySkin[_isect[i].Skin] = l = new List<int>();
                l.Add(i);
            }

            foreach (var kv in bySkin)
            {
                var at = new Dictionary<long, List<int>>(EdgeKeyComparer.Instance);
                foreach (int i in kv.Value)
                {
                    AddAt(at, _isect[i].KeyA, i);
                    AddAt(at, _isect[i].KeyB, i);
                }
                var used = new HashSet<int>();
                foreach (var node in at)
                    if (node.Value.Count == 1) Walk(node.Key, at, used, chains);
                foreach (int i in kv.Value)
                    if (!used.Contains(i)) Walk(_isect[i].KeyA, at, used, chains);
            }

            JoinOpenChains(chains, 0.015f);

            foreach (var c in chains)
            {
                if (c.P2.Count == 0) continue;
                var loop = new SectionLoop { WasClosed = c.Closed };
                loop.Points2D.AddRange(c.P2);
                loop.Points3D.AddRange(c.P3);
                loop.PointEdge.AddRange(c.PointEdge);
                loop.Edges.AddRange(c.Isects);
                foreach (int e in c.Isects) if (_skins[_isect[e].Skin].CapsTriangle(_isect[e].Tri)) { loop.Capable = true; break; }
                MathX.SimplifyLoop(loop.Points2D, loop.Points3D, loop.PointEdge, 0.0006f);
                foreach (int e in loop.PointEdge)
                {
                    loop.Normals3D.Add(FaceNormal(_isect[e].Skin, _isect[e].Tri));
                    loop.PointSegment.Add(_isect[e].Segment);
                }
                loop.Area = loop.Points2D.Count >= 3 ? Math.Abs(MathX.SignedArea(loop.Points2D)) : 0f;
                // Single-sided geometry (straps, cloth edges) gives outlines with no area. They still have to be
                // split, but they get no cap and cost nothing to cut.
                if (loop.Area < 1e-6f) loop.Capable = false;
                loop.Centroid = loop.Points2D.Count >= 3 && loop.Area > 0f ? MathX.PolygonCentroid(loop.Points2D) : Average(loop.Points2D);
                loop.Min = new Vector2(float.MaxValue);
                loop.Max = new Vector2(float.MinValue);
                foreach (var q in loop.Points2D)
                {
                    loop.Min = Vector2.Min(loop.Min, q);
                    loop.Max = Vector2.Max(loop.Max, q);
                }
                _report.Loops.Add(loop);
            }
        }

        /// <summary>Outward normal of a skinned triangle at cut time (Unity winding: clockwise seen from outside).</summary>
        private Vector3 FaceNormal(int si, int tri)
        {
            var topo = _skins[si].Mesh.Topology;
            var pos = _pos[si];
            var a = pos[topo.Tri[tri * 3]];
            var b = pos[topo.Tri[tri * 3 + 1]];
            var c = pos[topo.Tri[tri * 3 + 2]];
            return MathX.SafeNormalize(Vector3.Cross(b - a, c - a), Vector3.Zero);
        }

        private static Vector2 Average(List<Vector2> pts)
        {
            var sum = Vector2.Zero;
            foreach (var p in pts) sum += p;
            return pts.Count > 0 ? sum / pts.Count : sum;
        }

        private static void AddAt(Dictionary<long, List<int>> at, long key, int i)
        {
            if (!at.TryGetValue(key, out var l)) at[key] = l = new List<int>(2);
            l.Add(i);
        }

        private void Walk(long startKey, Dictionary<long, List<int>> at, HashSet<int> used, List<Chain> chains)
        {
            int e = FirstUnused(at, startKey, used);
            if (e < 0) return;
            var chain = new Chain();
            long node = startKey;
            AddPoint(chain, e, node);
            while (true)
            {
                used.Add(e);
                chain.Isects.Add(e);
                var hit = _isect[e];
                long other = hit.KeyA == node ? hit.KeyB : hit.KeyA;
                node = other;
                if (node == startKey) { chain.Closed = true; break; }
                AddPoint(chain, e, node);
                int next = FirstUnused(at, node, used);
                if (next < 0) break;
                e = next;
            }
            chains.Add(chain);
        }

        private static int FirstUnused(Dictionary<long, List<int>> at, long key, HashSet<int> used)
        {
            if (!at.TryGetValue(key, out var l)) return -1;
            foreach (int i in l) if (!used.Contains(i)) return i;
            return -1;
        }

        private void AddPoint(Chain c, int isect, long key)
        {
            var h = _isect[isect];
            if (h.KeyA == key) { c.P3.Add(h.A3); c.P2.Add(h.A2); }
            else { c.P3.Add(h.B3); c.P2.Add(h.B2); }
            c.PointEdge.Add(isect);
        }

        /// <summary>
        /// Open chains happen where separate meshes meet (body/head, sleeve/hand) or at mesh holes.
        /// Greedily connect the closest chain ends, then treat whatever remains as closed.
        /// </summary>
        private static void JoinOpenChains(List<Chain> chains, float maxGap)
        {
            var open = chains.FindAll(c => !c.Closed && c.P3.Count > 0);
            while (open.Count > 0)
            {
                float best = maxGap;
                int bi = -1, bj = -1; bool biEnd = false, bjEnd = false;
                for (int i = 0; i < open.Count; i++)
                {
                    var ci = open[i];
                    // Closing a chain on itself.
                    float self = Vector3.Distance(ci.P3[0], ci.P3[ci.P3.Count - 1]);
                    if (self < best && ci.P3.Count >= 3) { best = self; bi = i; bj = i; }
                    for (int j = i + 1; j < open.Count; j++)
                    {
                        var cj = open[j];
                        for (int ei = 0; ei < 2; ei++)
                            for (int ej = 0; ej < 2; ej++)
                            {
                                var pi = ei == 0 ? ci.P3[0] : ci.P3[ci.P3.Count - 1];
                                var pj = ej == 0 ? cj.P3[0] : cj.P3[cj.P3.Count - 1];
                                float d = Vector3.Distance(pi, pj);
                                if (d < best) { best = d; bi = i; bj = j; biEnd = ei == 1; bjEnd = ej == 1; }
                            }
                    }
                }
                if (bi < 0) break;
                if (bi == bj)
                {
                    open[bi].Closed = true;
                    open.RemoveAt(bi);
                    continue;
                }
                var a = open[bi];
                var b = open[bj];
                // Arrange so a's end meets b's start.
                if (!biEnd) Reverse(a);
                if (bjEnd) Reverse(b);
                a.P3.AddRange(b.P3);
                a.P2.AddRange(b.P2);
                a.PointEdge.AddRange(b.PointEdge);
                a.Isects.AddRange(b.Isects);
                chains.Remove(b);
                open.RemoveAt(bj);
            }
        }

        private static void Reverse(Chain c)
        {
            c.P3.Reverse();
            c.P2.Reverse();
            c.PointEdge.Reverse();
            c.Isects.Reverse();
        }

        private void BuildGroups()
        {
            var loops = _report.Loops;

            // Flesh outlines (from renderers that make caps) define the cross-sections; biggest first.
            var order = new List<int>();
            for (int i = 0; i < loops.Count; i++) if (loops[i].Capable) order.Add(i);
            order.Sort((x, y) => loops[y].Area.CompareTo(loops[x].Area));

            // A loop whose centre (or most of whose outline) lies inside a bigger one is the same cross-section
            // seen through another layer (skin under clothing, the face mesh inside the collar...).
            for (int oi = 0; oi < order.Count; oi++)
            {
                int i = order[oi];
                var li = loops[i];
                int bestParent = -1;
                float bestArea = float.MaxValue;
                for (int oj = 0; oj < oi; oj++)
                {
                    int j = order[oj];
                    var lj = loops[j];
                    if (li.Max.X < lj.Min.X || li.Min.X > lj.Max.X || li.Max.Y < lj.Min.Y || li.Min.Y > lj.Max.Y) continue;
                    bool inside = MathX.PointInPolygon(li.Centroid, lj.Points2D);
                    if (!inside)
                    {
                        int hits = 0, tested = 0;
                        int step = Math.Max(1, li.Points2D.Count / 12);
                        for (int k = 0; k < li.Points2D.Count; k += step)
                        {
                            tested++;
                            if (MathX.PointInPolygon(li.Points2D[k], lj.Points2D)) hits++;
                        }
                        inside = hits * 4 >= tested * 3;
                    }
                    if (inside && lj.Area < bestArea)
                    {
                        bestArea = lj.Area;
                        bestParent = j;
                    }
                }
                li.Parent = bestParent;
            }

            int Root(int i) { while (loops[i].Parent >= 0) i = loops[i].Parent; return i; }

            var groupOf = new Dictionary<int, SectionGroup>();
            SectionGroup NewGroup(int outer)
            {
                var g = new SectionGroup { Id = _report.Groups.Count, OuterLoop = outer };
                groupOf[outer] = g;
                _report.Groups.Add(g);
                return g;
            }
            foreach (int i in order)
            {
                int r = Root(i);
                if (!groupOf.TryGetValue(r, out var g)) g = NewGroup(r);
                g.Loops.Add(i);
            }

            // Hair cards, shadow proxies etc. ride along with whatever flesh section they sit in or next to.
            for (int i = 0; i < loops.Count; i++)
            {
                if (loops[i].Capable) continue;
                var li = loops[i];
                SectionGroup best = null;
                float bestD = 0.03f;
                foreach (var g in _report.Groups)
                {
                    var outer = loops[g.OuterLoop];
                    if (!outer.Capable) continue;
                    float d = MathX.PointInPolygon(li.Centroid, outer.Points2D) ? 0f : MathX.DistanceToPolygon(li.Centroid, outer.Points2D);
                    if (d < bestD) { bestD = d; best = g; }
                }
                if (best == null) best = NewGroup(i);
                best.Loops.Add(i);
            }

            foreach (var g in _report.Groups)
            {
                foreach (int li in g.Loops)
                    foreach (int e in loops[li].Edges) g.Segments.Add(_isect[e].Segment);

                var outer = loops[g.OuterLoop];
                g.Area = outer.Capable ? outer.Area : 0f;
                int inStrip = 0;
                g.WMin = float.MaxValue; g.WMax = float.MinValue;
                foreach (var q in outer.Points2D)
                {
                    if (_region.InStrip(q)) inStrip++;
                    g.WMin = Math.Min(g.WMin, q.Y);
                    g.WMax = Math.Max(g.WMax, q.Y);
                }
                g.Coverage = (float)inStrip / outer.Points2D.Count;
                g.InReach = inStrip > 0;
                g.Centroid3D = _region.To3D(outer.Centroid);

                if (!outer.Capable) continue;
                foreach (int seg in g.Segments)
                    foreach (var q in BoneCrossings(seg))
                        if (MathX.PointInPolygon(q, outer.Points2D)) g.BoneCrossings.Add((seg, q));
            }
        }

        /// <summary>Where the segment's bone (towards each child, or through its middle for end segments) crosses the plane.</summary>
        private IEnumerable<Vector2> BoneCrossings(int seg)
        {
            var s = _body.Segments[seg];
            var o = _pose.SegmentOrigin[seg];
            if (s.Children.Count == 0)
            {
                if (TryCross(o, o + (_segCentroid[seg] - o) * 2f, out var q)) yield return q;
                yield break;
            }
            foreach (int c in s.Children)
                if (TryCross(o, _pose.SegmentOrigin[c], out var q)) yield return q;
        }

        private bool TryCross(Vector3 a, Vector3 b, out Vector2 q)
        {
            q = default;
            float d0 = _region.SignedDistance(a), d1 = _region.SignedDistance(b);
            if ((d0 > 0) == (d1 > 0)) return false;
            float t = d0 / (d0 - d1);
            q = _region.To2D(a + (b - a) * t);
            return true;
        }

        /// <summary>Approximate bone radius for the bone disc on a cap.</summary>
        public static float BoneRadius(SegmentKind kind, float sectionArea)
        {
            float r = MathF.Sqrt(sectionArea / MathF.PI) * 0.2f;
            float max = kind switch
            {
                SegmentKind.Hand => 0.008f,
                SegmentKind.Foot => 0.01f,
                SegmentKind.Arm => 0.013f,
                SegmentKind.Leg => 0.018f,
                SegmentKind.Head => 0.012f,
                _ => 0.02f,
            };
            return MathX.Clamp(r, 0.004f, max);
        }
    }
}
