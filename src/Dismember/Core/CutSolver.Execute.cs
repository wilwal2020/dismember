using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;

namespace Dismember.Core
{
    public sealed partial class CutSolver
    {
        // Graph nodes: a segment that is not cut has one node (s*2); a cut segment has one per side (s*2 + side).
        private bool[] _cut;
        private int NodeOf(int seg, int side) => _cut[seg] ? seg * 2 + side : seg * 2;

        private readonly WeightAccumulator _acc = new WeightAccumulator();
        private int[] _owner;          // cut segment -> side holding its rigid body
        private int[] _segPiece;       // segment -> piece holding its body
        private int[] _nodePiece;      // node -> piece
        private int[] _nodeBinding;    // node -> body segment its vertices ride on (-1 = free chunk)
        private bool[] _nodeChunk;
        private int _corePiece;
        private int _pieceCount;
        private readonly List<int> _freePieceSource = new List<int>();

        /// <summary>Severs the given cross-section groups (ids from <see cref="Analyze"/>).</summary>
        public CutResult Execute(IReadOnlyCollection<int> severedGroups, CutOptions opts = null)
        {
            if (_report == null) Analyze();
            opts ??= new CutOptions();
            var sw = Stopwatch.StartNew();
            var result = new CutResult();
            result.SeveredGroups.AddRange(severedGroups);

            // ---- 1. Which triangles are split ---------------------------------------------------------------
            var splitTris = new Dictionary<int, int>[_skins.Length];
            for (int i = 0; i < _skins.Length; i++) splitTris[i] = new Dictionary<int, int>();
            _cut = new bool[_segCount];
            foreach (int gid in severedGroups)
            {
                var g = _report.Groups[gid];
                foreach (int li in g.Loops)
                {
                    // Only flesh cuts a segment in two. Hair cards and straps are split along the plane but stay with
                    // their segment; otherwise trimming hair would count as cutting the head and could free the jaw.
                    bool flesh = _report.Loops[li].Capable;
                    foreach (int e in _report.Loops[li].Edges)
                    {
                        var h = _isect[e];
                        splitTris[h.Skin][h.Tri] = gid;
                        if (flesh) _cut[h.Segment] = true;
                    }
                }
                if (g.Area > 0f) result.Wounds.Add((g.Centroid3D, _region.NormalAt(_region.To2D(g.Centroid3D)), g.Area));
            }
            result.SegmentCut = (bool[])_cut.Clone();

            // ---- 2. Which side each joint lies on, and where each cut segment keeps its rigid body ---------
            var jointSide = new int[_segCount];
            for (int s = 0; s < _segCount; s++)
            {
                jointSide[s] = _region.SignedDistance(_pose.SegmentOrigin[s]) > 0 ? 0 : 1;
                // A segment the plane does not cut, whose flesh is all on the other side from its joint (the jaw when
                // the cut runs between its hinge and the chin): the plane went through its bone, so as far as the
                // pieces go its joint is on the side of its flesh.
                if (_cut[s]) continue;
                int a = _segVertsA[s], b = _segVertsB[s];
                if (a + b < 8) continue;
                int fleshSide = a >= b ? 0 : 1;
                if (Math.Max(a, b) >= (a + b) * 0.9f) jointSide[s] = fleshSide;
            }

            _owner = new int[_segCount];
            result.SegmentOwnerSide = new int[_segCount];
            for (int s = 0; s < _segCount; s++)
            {
                result.SegmentOwnerSide[s] = -1;
                if (!_cut[s]) continue;
                var seg = _body.Segments[s];
                int a = 0, b = 0;
                if (seg.Parent >= 0) { if (jointSide[s] == 0) a++; else b++; }
                foreach (int c in seg.Children) { if (jointSide[c] == 0) a++; else b++; }
                int owner;
                if (a != b) owner = a > b ? 0 : 1;
                else owner = TieBreakOwner(s, jointSide);
                _owner[s] = owner;
                result.SegmentOwnerSide[s] = owner;
            }

            // ---- 3. Connectivity graph (flesh) and physical pieces (bodies + joints that survive) ----------
            int nodeCount = _segCount * 2;
            var adj = new List<int>[nodeCount];
            for (int i = 0; i < nodeCount; i++) adj[i] = new List<int>(4);
            void Link(int a, int b) { adj[a].Add(b); adj[b].Add(a); }

            int BodyNode(int s) => _cut[s] ? s * 2 + _owner[s] : s * 2;

            var uf = new UnionFind(_segCount);
            for (int s = 0; s < _segCount; s++)
            {
                int p = _body.Segments[s].Parent;
                if (p < 0) continue;
                int x = jointSide[s];
                bool kept;
                if (!_cut[s] && !_cut[p]) { Link(s * 2, p * 2); kept = true; }
                else if (_cut[s] && !_cut[p]) { Link(s * 2 + x, p * 2); kept = BodyNode(s) == s * 2 + x; }
                else if (!_cut[s] && _cut[p]) { Link(s * 2, p * 2 + x); kept = BodyNode(p) == p * 2 + x; }
                else
                {
                    Link(s * 2 + x, p * 2 + x);
                    Link(s * 2 + 1 - x, p * 2 + 1 - x);
                    kept = _owner[s] == _owner[p];
                }
                if (kept) uf.Union(s, p);
                else result.FreedJoints.Add(s);
            }

            // ---- 4. Chunks: the non-owner part of each cut segment rides on the nearest body on its side ---
            _nodePiece = new int[nodeCount];
            _nodeBinding = new int[nodeCount];
            _nodeChunk = new bool[nodeCount];
            for (int i = 0; i < nodeCount; i++) { _nodePiece[i] = -1; _nodeBinding[i] = -1; }
            for (int s = 0; s < _segCount; s++) _nodeBinding[BodyNode(s)] = s;
            int coreSet = uf.Find(_body.Root);
            var freeGroups = new List<(int source, List<int> nodes)>();

            for (int s = 0; s < _segCount; s++)
            {
                if (!_cut[s]) continue;
                int chunk = s * 2 + 1 - _owner[s];
                _nodeChunk[chunk] = true;
                if (_nodeBinding[chunk] >= 0 || freeGroups.Exists(fg => fg.nodes.Contains(chunk))) continue;

                // BFS through chunk nodes until we reach bodies; among the closest candidates prefer the segment's
                // parent (a chunk is usually the base of the bone), then bodies staying in the core, then distance.
                var visited = new HashSet<int> { chunk };
                var frontier = new List<int> { chunk };
                var chunkGroup = new List<int> { chunk };
                var chunkCentroid = (1 - _owner[s]) == 0 ? _segCentroidA[s] : _segCentroidB[s];
                int bestBody = -1;
                while (frontier.Count > 0 && bestBody < 0)
                {
                    var next = new List<int>();
                    float bestScore = float.MaxValue;
                    foreach (int f in frontier)
                        foreach (int nb in adj[f])
                        {
                            if (!visited.Add(nb)) continue;
                            int nbSeg = nb / 2;
                            if (BodyNode(nbSeg) == nb)
                            {
                                float score = Vector3.Distance(chunkCentroid, _pose.SegmentOrigin[nbSeg]);
                                if (nbSeg == _body.Segments[s].Parent) score -= 100f;
                                if (uf.Find(nbSeg) == coreSet) score -= 10f;
                                if (score < bestScore) { bestScore = score; bestBody = nbSeg; }
                            }
                            else
                            {
                                next.Add(nb);
                                chunkGroup.Add(nb);
                            }
                        }
                    frontier = next;
                }

                if (bestBody >= 0) _nodeBinding[chunk] = bestBody;
                else freeGroups.Add((s, chunkGroup));   // nothing to hold on to (e.g. the top of a skull)
            }

            // ---- 4b. Flesh that still holds pieces together ------------------------------------------------
            // Which node every triangle belongs to, and which nodes' flesh touches in the meshes (on the same side of
            // the plane, so nothing the cut separated).
            var triNode = new int[_skins.Length][];
            for (int si = 0; si < _skins.Length; si++)
                triNode[si] = AssignTriangles(si, splitTris[si]);
            var touching = MeshNodeLinks(triNode, splitTris);
            int BodyOfNode(int n) => BodyNode(n / 2) == n ? n / 2 : _nodeBinding[n];

            // A "loose" chunk whose flesh touches flesh that rides on a body is not loose: it rides with that body.
            for (int k = freeGroups.Count - 1; k >= 0; k--)
            {
                var nodes = freeGroups[k].nodes;
                int ride = -1;
                foreach (var link in touching.Keys)
                {
                    int a = (int)(link >> 32), b = (int)(link & 0xFFFFFFFF);
                    bool ia = nodes.Contains(a), ib = nodes.Contains(b);
                    if (ia == ib) continue;
                    ride = BodyOfNode(ia ? b : a);
                    if (ride >= 0) break;
                }
                if (ride < 0) continue;
                foreach (int n in nodes)
                    if (BodyNode(n / 2) != n) _nodeBinding[n] = ride;
                freeGroups.RemoveAt(k);
            }

            // Flesh joining bodies that ended up in different pieces (their joint went to a body across the cut):
            // tearing them apart would open the skin along a line that is not the cut, with no cut face to close it.
            // Keep them together: the loose body hangs from the other where the flesh joins them.
            void Bridge(int x, int y, Vector3 at)
            {
                if (x < 0 || y < 0 || uf.Find(x) == uf.Find(y)) return;
                // The core stays put; whatever hangs is the top body of the other set (its joint is the one cut).
                int hang = TopBody(y, result.FreedJoints), hold = x;
                if (uf.Find(hang) == uf.Find(_body.Root))
                {
                    hang = TopBody(x, result.FreedJoints);
                    hold = y;
                }
                if (uf.Find(hang) == uf.Find(_body.Root)) return;
                uf.Union(hang, hold);
                result.Rejoined.Add((hang, hold, at));
            }
            for (int s = 0; s < _segCount; s++)
            {
                if (!_cut[s]) continue;
                int chunk = s * 2 + 1 - _owner[s];
                int holder = _nodeBinding[chunk];
                if (holder < 0) continue;
                foreach (int nb in adj[chunk])
                {
                    int nbSeg = nb / 2;
                    var at = _body.Segments[nbSeg].Parent == s ? _pose.SegmentOrigin[nbSeg]
                           : _body.Segments[s].Parent == nbSeg ? _pose.SegmentOrigin[s]
                           : (_pose.SegmentOrigin[s] + _pose.SegmentOrigin[nbSeg]) * 0.5f;
                    Bridge(holder, BodyOfNode(nb), at);
                }
            }
            foreach (var kv in touching)
            {
                int a = (int)(kv.Key >> 32), b = (int)(kv.Key & 0xFFFFFFFF);
                Bridge(BodyOfNode(a), BodyOfNode(b), kv.Value);
            }

            // Pieces: the core (holding the root body) first, then the other body pieces, then free chunks.
            _segPiece = new int[_segCount];
            var rootToPiece = new Dictionary<int, int>();
            rootToPiece[uf.Find(_body.Root)] = 0;
            _pieceCount = 1;
            for (int s = 0; s < _segCount; s++)
            {
                int r = uf.Find(s);
                if (!rootToPiece.TryGetValue(r, out int pi)) rootToPiece[r] = pi = _pieceCount++;
                _segPiece[s] = pi;
            }
            _corePiece = 0;
            int bodyPieceCount = _pieceCount;
            for (int n = 0; n < nodeCount; n++)
                if (_nodeBinding[n] >= 0) _nodePiece[n] = _segPiece[_nodeBinding[n]];
            foreach (var (source, nodes) in freeGroups)
            {
                int piece = _pieceCount++;
                _freePieceSource.Add(source);
                foreach (int n in nodes)
                {
                    if (!_nodeChunk[n] && BodyNode(n / 2) != n) _nodeChunk[n] = true;
                    _nodePiece[n] = piece;
                    _nodeBinding[n] = -1;
                }
            }

            var stage = Stopwatch.StartNew();
            void Mark(string name) { result.Timings += $"{name} {stage.Elapsed.TotalMilliseconds:F1}ms; "; stage.Restart(); }
            Mark("topology");


            // ---- 6. Pieces ---------------------------------------------------------------------------------
            for (int p = 0; p < _pieceCount; p++)
            {
                var piece = new Piece { Index = p, IsCore = p == _corePiece, IsFreeChunk = p >= bodyPieceCount };
                if (piece.IsFreeChunk) piece.SourceSegment = _freePieceSource[p - bodyPieceCount];
                result.Pieces.Add(piece);
            }
            for (int s = 0; s < _segCount; s++) result.Pieces[_segPiece[s]].BodySegments.Add(s);
            result.SegmentPiece = (int[])_segPiece.Clone();
            result.CorePiece = _corePiece;

            // Free chunks need an origin before their vertices are written.
            ComputeFreeOrigins(result, triNode, splitTris);

            // ---- 7. Meshes --------------------------------------------------------------------------------
            for (int si = 0; si < _skins.Length; si++)
                BuildSkinOutputs(si, triNode[si], splitTris[si], result);
            Mark("meshes");

            // ---- 8. Caps, chunk colliders ----------------------------------------------------------------
            if (opts.MakeCaps) BuildCaps(result, severedGroups, opts);
            Mark("caps");
            BuildChunkColliders(result, triNode, splitTris);
            Mark("chunks");

            result.Milliseconds = (float)sw.Elapsed.TotalMilliseconds;
            return result;
        }

        /// <summary>
        /// Pairs of nodes whose flesh touches in a mesh (triangles sharing an edge, on the same side of the plane),
        /// with a point where they touch. Key: (lower node &lt;&lt; 32) | higher node.
        /// </summary>
        private Dictionary<long, Vector3> MeshNodeLinks(int[][] triNode, Dictionary<int, int>[] splitTris)
        {
            var links = new Dictionary<long, Vector3>();
            for (int si = 0; si < _skins.Length; si++)
            {
                var topo = _skins[si].Mesh.Topology;
                var dist = _dist[si];
                var pos = _pos[si];
                var segOf = _triSeg[si];
                var split = splitTris[si];
                var nodes = triNode[si];
                int Side(int f)
                {
                    int above = 0;
                    for (int c = 0; c < 3; c++) if (dist[topo.Tri[f * 3 + c]] > 0) above++;
                    return above >= 2 ? 0 : 1;
                }
                int NodeOn(int f, int side) => split.ContainsKey(f) ? NodeOf(segOf[f], side) : Side(f) == side ? nodes[f] : -1;
                foreach (var kv in topo.EdgeTriangles)
                {
                    var tl = kv.Value;
                    if (tl.Length < 2) continue;
                    for (int i = 0; i < tl.Length; i++)
                        for (int j = i + 1; j < tl.Length; j++)
                        {
                            int f = tl[i], g = tl[j];
                            bool fs = split.ContainsKey(f), gs = split.ContainsKey(g);
                            if (!fs && !gs && nodes[f] == nodes[g]) continue;
                            for (int side = 0; side < 2; side++)
                            {
                                int a = NodeOn(f, side), b = NodeOn(g, side);
                                if (a < 0 || b < 0 || a == b) continue;
                                long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
                                if (links.ContainsKey(key)) continue;
                                links[key] = (pos[topo.Tri[f * 3]] + pos[topo.Tri[f * 3 + 1]] + pos[topo.Tri[f * 3 + 2]]) / 3f;
                            }
                        }
                }
            }
            return links;
        }

        /// <summary>The top body of the chain <paramref name="x"/> hangs in: up through parents whose joints survive.</summary>
        private int TopBody(int x, List<int> freed)
        {
            for (int guard = 0; guard < 64; guard++)
            {
                int p = _body.Segments[x].Parent;
                if (p < 0 || freed.Contains(x)) return x;
                x = p;
            }
            return x;
        }

        /// <summary>
        /// When a cut segment has as many joints on each side, its rigid body stays on the parent's side unless the
        /// cut is close to the parent joint (then most of the bone leaves with the far side).
        /// </summary>
        private int TieBreakOwner(int s, int[] jointSide)
        {
            var seg = _body.Segments[s];
            var o = _pose.SegmentOrigin[s];
            Vector3 end;
            if (seg.Children.Count > 0)
            {
                end = Vector3.Zero;
                foreach (int c in seg.Children) end += _pose.SegmentOrigin[c];
                end /= seg.Children.Count;
            }
            else end = o + (_segCentroid[s] - o) * 2f;
            float d0 = _region.SignedDistance(o), d1 = _region.SignedDistance(end);
            int originSide = d0 > 0 ? 0 : 1;
            if ((d0 > 0) != (d1 > 0))
            {
                float t = d0 / (d0 - d1);
                return t < 0.3f ? 1 - originSide : originSide;
            }
            // Lengthwise cut: keep the body where most of the segment is.
            return _segVertsA[s] >= _segVertsB[s] ? 0 : 1;
        }

        // ------------------------------------------------------------------------------------------------

        private int[] AssignTriangles(int si, Dictionary<int, int> split)
        {
            var mesh = _skins[si].Mesh;
            var topo = mesh.Topology;
            var dist = _dist[si];
            var segOf = _triSeg[si];
            var node = new int[topo.TriangleCount];
            for (int f = 0; f < node.Length; f++)
            {
                int s = segOf[f];
                node[f] = _cut[s] ? -1 : s * 2;
            }
            bool anyCut = false;
            for (int f = 0; f < node.Length && !anyCut; f++) anyCut = node[f] < 0;
            if (!anyCut) return node;

            var edgeTris = topo.EdgeTriangles;

            // a) Flood out from the split triangles on each side, staying on that side of the plane.
            for (int side = 0; side < 2 && split.Count > 0; side++)
            {
                bool positive = side == 0;
                var queue = new Queue<int>();
                var seen = new HashSet<int>();
                foreach (var kv in split) { queue.Enqueue(kv.Key); seen.Add(kv.Key); }
                while (queue.Count > 0)
                {
                    int f = queue.Dequeue();
                    int seg = segOf[f];
                    for (int e = 0; e < 3; e++)
                    {
                        int a = topo.Tri[f * 3 + e], b = topo.Tri[f * 3 + (e + 1) % 3];
                        if ((dist[a] > 0) != positive || (dist[b] > 0) != positive) continue;
                        if (!edgeTris.TryGetValue(MeshTopology.EdgeKey(topo.Weld[a], topo.Weld[b]), out var nbs)) continue;
                        foreach (int g in nbs)
                        {
                            if (g == f || segOf[g] != seg || split.ContainsKey(g) || !seen.Add(g)) continue;
                            if (node[g] < 0) node[g] = seg * 2 + side;
                            queue.Enqueue(g);
                        }
                    }
                }
            }

            // b) Triangles connected to an assigned region (but not across the cut) join that region.
            {
                var queue = new Queue<int>();
                for (int f = 0; f < node.Length; f++)
                    if (node[f] >= 0 && _cut[segOf[f]] && !split.ContainsKey(f)) queue.Enqueue(f);
                while (queue.Count > 0)
                {
                    int f = queue.Dequeue();
                    for (int e = 0; e < 3; e++)
                    {
                        int a = topo.Tri[f * 3 + e], b = topo.Tri[f * 3 + (e + 1) % 3];
                        if (!edgeTris.TryGetValue(MeshTopology.EdgeKey(topo.Weld[a], topo.Weld[b]), out var nbs)) continue;
                        foreach (int g in nbs)
                        {
                            if (node[g] >= 0 || segOf[g] != segOf[f] || split.ContainsKey(g)) continue;
                            node[g] = node[f];
                            queue.Enqueue(g);
                        }
                    }
                }
            }

            // c) Isolated parts (eyes, teeth, meshes the cut never touched) go to whichever side they are on.
            var comp = new List<int>();
            var cq = new Queue<int>();
            for (int f = 0; f < node.Length; f++)
            {
                if (node[f] >= 0) continue;
                if (split.ContainsKey(f)) { node[f] = segOf[f] * 2 + _owner[segOf[f]]; continue; }
                comp.Clear();
                var queue = cq;
                queue.Clear();
                queue.Enqueue(f);
                node[f] = int.MinValue;
                int pos = 0, neg = 0;
                while (queue.Count > 0)
                {
                    int t = queue.Dequeue();
                    comp.Add(t);
                    for (int c = 0; c < 3; c++) { if (dist[topo.Tri[t * 3 + c]] > 0) pos++; else neg++; }
                    for (int e = 0; e < 3; e++)
                    {
                        int a = topo.Tri[t * 3 + e], b = topo.Tri[t * 3 + (e + 1) % 3];
                        if (!edgeTris.TryGetValue(MeshTopology.EdgeKey(topo.Weld[a], topo.Weld[b]), out var nbs)) continue;
                        foreach (int g in nbs)
                        {
                            if (node[g] != -1 || segOf[g] != segOf[t] || split.ContainsKey(g)) continue;
                            node[g] = int.MinValue;
                            queue.Enqueue(g);
                        }
                    }
                }
                int side = pos >= neg ? 0 : 1;
                foreach (int t in comp) node[t] = segOf[t] * 2 + side;
            }
            return node;
        }

        private void ComputeFreeOrigins(CutResult result, int[][] triNode, Dictionary<int, int>[] splitTris)
        {
            var sum = new Vector3[_pieceCount];
            var count = new int[_pieceCount];
            for (int si = 0; si < _skins.Length; si++)
            {
                var topo = _skins[si].Mesh.Topology;
                for (int f = 0; f < topo.TriangleCount; f++)
                {
                    for (int c = 0; c < 3; c++)
                    {
                        int v = topo.Tri[f * 3 + c];
                        int n = splitTris[si].ContainsKey(f) ? NodeOf(_triSeg[si][f], _dist[si][v] > 0 ? 0 : 1) : triNode[si][f];
                        int p = _nodePiece[n];
                        if (p < 0 || !result.Pieces[p].IsFreeChunk) continue;
                        sum[p] += _pos[si][v];
                        count[p]++;
                    }
                }
            }
            foreach (var piece in result.Pieces)
                if (piece.IsFreeChunk)
                    piece.Origin = count[piece.Index] > 0 ? sum[piece.Index] / count[piece.Index] : _segCentroid[piece.SourceSegment];
        }

        // ------------------------------------------------------------------------------------------------
        // Mesh output
        // ------------------------------------------------------------------------------------------------

        private enum VMode : byte { None = 0, Original = 1, FixForeign = 2, Force = 3 }

        private sealed class OutMesh
        {
            public int Piece;
            public bool Core, Free;
            public Vector3 Origin;
            public readonly List<Vector3> Pos = new List<Vector3>();
            public readonly List<Vector3> Nrm = new List<Vector3>();
            public readonly List<Vector4> Tan = new List<Vector4>();
            public readonly List<Vector4> Col = new List<Vector4>();
            public readonly List<Vector4>[] UV = new List<Vector4>[MeshData.MaxUVChannels];
            public readonly List<BoneWeight4> W = new List<BoneWeight4>();
            public readonly List<int> SourceVertex = new List<int>();   // original vertex whose blend shape deltas apply, or -1
            public List<int>[] Sub;
            public readonly Dictionary<int, int> FromOrig = new Dictionary<int, int>();
            public readonly Dictionary<long, int> FromEdge = new Dictionary<long, int>();
            public readonly Dictionary<int, int> SegBone = new Dictionary<int, int>();
            public readonly List<int> BoneSegs = new List<int>();
            public VMode[] Mode;
            public int[] Bind;
            public int TriCount;
        }

        private void BuildSkinOutputs(int si, int[] triNode, Dictionary<int, int> split, CutResult result)
        {
            var skin = _skins[si];
            var mesh = skin.Mesh;
            var topo = mesh.Topology;
            var dist = _dist[si];
            var segOf = _triSeg[si];
            int vc = mesh.VertexCount;

            // Renderers the cut does not touch keep their mesh: no split triangles, everything stays in the core,
            // and no vertex leans on a body that left the core.
            if (split.Count == 0)
            {
                bool touched = false;
                for (int f = 0; f < triNode.Length && !touched; f++) touched = _nodePiece[triNode[f]] != _corePiece || _nodeChunk[triNode[f]];
                if (!touched && mesh.Weights != null)
                    for (int v = 0; v < vc && !touched; v++) touched = HasForeign(si, v, _corePiece);
                if (!touched)
                {
                    result.Pieces[_corePiece].Renderers.Add(new RendererPiece { SkinId = skin.Id, Unchanged = true, UsesOriginalBones = true });
                    return;
                }
            }
            EnsureFrames(si);

            var outs = new OutMesh[_pieceCount];
            OutMesh Get(int piece)
            {
                if (outs[piece] != null) return outs[piece];
                var o = new OutMesh
                {
                    Piece = piece,
                    Core = piece == _corePiece,
                    Free = result.Pieces[piece].IsFreeChunk,
                    Origin = result.Pieces[piece].Origin,
                    Mode = new VMode[vc],
                    Bind = new int[vc],
                    Sub = new List<int>[mesh.Submeshes.Count],
                };
                for (int k = 0; k < o.Sub.Length; k++) o.Sub[k] = new List<int>();
                for (int k = 0; k < MeshData.MaxUVChannels; k++) if (mesh.UV[k] != null) o.UV[k] = new List<Vector4>();
                if (!o.Core && !o.Free)
                    foreach (int s in result.Pieces[piece].BodySegments) AddBone(o, s);
                outs[piece] = o;
                return o;
            }

            // Pass 1: decide how each vertex is bound in each piece that uses it.
            void Mark(int piece, int v, int nodeId)
            {
                var o = Get(piece);
                int bind = _nodeBinding[nodeId];
                VMode want = _nodeChunk[nodeId] ? VMode.Force : HasForeign(si, v, piece) ? VMode.FixForeign : VMode.Original;
                if (want > o.Mode[v]) { o.Mode[v] = want; o.Bind[v] = bind; }
            }
            for (int f = 0; f < topo.TriangleCount; f++)
            {
                int seg = segOf[f];
                if (split.ContainsKey(f))
                {
                    for (int c = 0; c < 3; c++)
                    {
                        int v = topo.Tri[f * 3 + c];
                        int n = NodeOf(seg, dist[v] > 0 ? 0 : 1);
                        Mark(_nodePiece[n], v, n);
                    }
                }
                else
                {
                    int n = triNode[f];
                    for (int c = 0; c < 3; c++) Mark(_nodePiece[n], topo.Tri[f * 3 + c], n);
                }
            }

            // Pass 2: emit triangles.
            for (int f = 0; f < topo.TriangleCount; f++)
            {
                int sub = topo.TriSubmesh[f];
                int i0 = topo.Tri[f * 3], i1 = topo.Tri[f * 3 + 1], i2 = topo.Tri[f * 3 + 2];
                int seg = segOf[f];
                if (!split.ContainsKey(f))
                {
                    var o = Get(_nodePiece[triNode[f]]);
                    o.Sub[sub].Add(Orig(si, o, i0));
                    o.Sub[sub].Add(Orig(si, o, i1));
                    o.Sub[sub].Add(Orig(si, o, i2));
                    o.TriCount++;
                    continue;
                }

                // Rotate so 'a' is the corner alone on its side.
                int a, b, c;
                bool p0 = dist[i0] > 0, p1 = dist[i1] > 0, p2 = dist[i2] > 0;
                if (p0 != p1 && p0 != p2) { a = i0; b = i1; c = i2; }
                else if (p1 != p0 && p1 != p2) { a = i1; b = i2; c = i0; }
                else { a = i2; b = i0; c = i1; }
                int sideA = dist[a] > 0 ? 0 : 1;
                int nodeA = NodeOf(seg, sideA), nodeBC = NodeOf(seg, 1 - sideA);

                var oa = Get(_nodePiece[nodeA]);
                int ab = Edge(si, oa, a, b, _nodeBinding[nodeA]);
                int ac = Edge(si, oa, a, c, _nodeBinding[nodeA]);
                oa.Sub[sub].Add(Orig(si, oa, a)); oa.Sub[sub].Add(ab); oa.Sub[sub].Add(ac);
                oa.TriCount++;

                var ob = Get(_nodePiece[nodeBC]);
                int ab2 = Edge(si, ob, a, b, _nodeBinding[nodeBC]);
                int ac2 = Edge(si, ob, a, c, _nodeBinding[nodeBC]);
                int vb = Orig(si, ob, b), vcIdx = Orig(si, ob, c);
                ob.Sub[sub].Add(ab2); ob.Sub[sub].Add(vb); ob.Sub[sub].Add(vcIdx);
                ob.Sub[sub].Add(ab2); ob.Sub[sub].Add(vcIdx); ob.Sub[sub].Add(ac2);
                ob.TriCount += 2;
            }

            // Pass 3: finalize.
            foreach (var o in outs)
            {
                if (o == null || o.TriCount == 0) continue;
                var md = new MeshData { Name = mesh.Name + (o.Core ? "_cut" : "_piece" + o.Piece) };
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

                var rp = new RendererPiece { SkinId = skin.Id, Mesh = md, UsesOriginalBones = o.Core };
                if (o.Core)
                {
                    md.Weights = o.W.ToArray();
                    md.Bindposes = mesh.Bindposes;
                    CopyBlendShapes(mesh, o, md);
                }
                else if (!o.Free)
                {
                    md.Weights = o.W.ToArray();
                    md.Bindposes = new Matrix4x4[o.BoneSegs.Count];
                    for (int k = 0; k < o.BoneSegs.Count; k++)
                        md.Bindposes[k] = MathX.InvertOrIdentity(_pose.SegmentBodyWorld[o.BoneSegs[k]]);
                    rp.BoneSegments = o.BoneSegs.ToArray();
                }
                result.Pieces[o.Piece].Renderers.Add(rp);
            }
        }

        private static int AddBone(OutMesh o, int seg)
        {
            if (o.SegBone.TryGetValue(seg, out int b)) return b;
            b = o.BoneSegs.Count;
            o.BoneSegs.Add(seg);
            o.SegBone[seg] = b;
            return b;
        }

        private bool HasForeign(int si, int v, int piece)
        {
            var w = _skins[si].Mesh.Weights;
            if (w == null) return false;
            var bw = w[v];
            for (int k = 0; k < 4; k++)
            {
                if (bw.Weight(k) <= 0f) continue;
                if (_segPiece[SegmentOfBone(_skins[si], bw.Bone(k))] != piece) return true;
            }
            return false;
        }

        /// <summary>Renderer bone that carries segment <paramref name="seg"/> in skin <paramref name="si"/>.</summary>
        private int BoneForSegment(int si, int seg)
        {
            var skin = _skins[si];
            if (skin.SegmentBone != null && seg >= 0 && seg < skin.SegmentBone.Length && skin.SegmentBone[seg] >= 0)
                return skin.SegmentBone[seg];
            if (skin.BoneSegment != null)
                for (int i = 0; i < skin.BoneSegment.Length; i++)
                    if (skin.BoneSegment[i] == seg) return i;
            return -1;
        }

        private int Orig(int si, OutMesh o, int v)
        {
            if (o.FromOrig.TryGetValue(v, out int idx)) return idx;
            var mesh = _skins[si].Mesh;
            idx = o.Pos.Count;
            o.FromOrig[v] = idx;

            var mode = o.Mode[v] == VMode.None ? VMode.Original : o.Mode[v];
            int bind = o.Bind[v];
            Vector3 pWorld = _pos[si][v];
            Vector3 nWorld = _nrm[si] != null ? _nrm[si][v] : Vector3.UnitY;
            Vector4 tWorld = _tan[si] != null ? _tan[si][v] : new Vector4(1, 0, 0, 1);

            if (o.Free)
            {
                PushVertex(o, mesh, v, pWorld - o.Origin, nWorld, tWorld, default, -1);
            }
            else if (o.Core)
            {
                if (mode == VMode.Original || mesh.Weights == null)
                {
                    PushVertex(o, mesh, v, mesh.Positions[v],
                        mesh.Normals != null ? mesh.Normals[v] : Vector3.UnitY,
                        mesh.Tangents != null ? mesh.Tangents[v] : new Vector4(1, 0, 0, 1),
                        mesh.Weights != null ? mesh.Weights[v] : BoneWeight4.Single(0), v);
                }
                else
                {
                    BoneWeight4 w;
                    int bone = BoneForSegment(si, bind);
                    if (mode == VMode.Force || bone < 0)
                    {
                        w = BoneWeight4.Single(bone >= 0 ? bone : mesh.Weights[v].Dominant);
                    }
                    else
                    {
                        var acc = _acc;
                        acc.Clear();
                        var bw = mesh.Weights[v];
                        for (int k = 0; k < 4; k++)
                        {
                            float wk = bw.Weight(k);
                            if (wk <= 0f) continue;
                            int bi = bw.Bone(k);
                            acc.Add(_segPiece[SegmentOfBone(_skins[si], bi)] == o.Piece ? bi : bone, wk);
                        }
                        w = acc.ToTop4(bone);
                    }
                    Rebind(si, w, pWorld, nWorld, tWorld, out var pb, out var nb, out var tb);
                    PushVertex(o, mesh, v, pb, nb, tb, w, -1);
                }
            }
            else
            {
                BoneWeight4 w;
                int bindBone = AddBone(o, bind >= 0 ? bind : o.BoneSegs.Count > 0 ? o.BoneSegs[0] : 0);
                if (mode == VMode.Force || mesh.Weights == null)
                {
                    w = BoneWeight4.Single(bindBone);
                }
                else
                {
                    var acc = _acc;
                    acc.Clear();
                    var bw = mesh.Weights[v];
                    for (int k = 0; k < 4; k++)
                    {
                        float wk = bw.Weight(k);
                        if (wk <= 0f) continue;
                        int seg = SegmentOfBone(_skins[si], bw.Bone(k));
                        acc.Add(_segPiece[seg] == o.Piece ? AddBone(o, seg) : bindBone, wk);
                    }
                    w = acc.ToTop4(bindBone);
                }
                PushVertex(o, mesh, v, pWorld, nWorld, tWorld, w, -1);
            }
            return idx;
        }

        private int Edge(int si, OutMesh o, int a, int b, int bind)
        {
            int lo = Math.Min(a, b), hi = Math.Max(a, b);
            long key = ((long)lo << 32) | (uint)hi;
            if (o.FromEdge.TryGetValue(key, out int idx)) return idx;
            idx = o.Pos.Count;
            o.FromEdge[key] = idx;

            var mesh = _skins[si].Mesh;
            float dl = _dist[si][lo], dh = _dist[si][hi];
            float t = dl / (dl - dh);
            Vector3 pWorld = Vector3.Lerp(_pos[si][lo], _pos[si][hi], t);
            Vector3 nWorld = _nrm[si] != null ? MathX.SafeNormalize(Vector3.Lerp(_nrm[si][lo], _nrm[si][hi], t), Vector3.UnitY) : Vector3.UnitY;
            Vector4 tWorld = _tan[si] != null ? Vector4.Lerp(_tan[si][lo], _tan[si][hi], t) : new Vector4(1, 0, 0, 1);
            tWorld = new Vector4(MathX.SafeNormalize(new Vector3(tWorld.X, tWorld.Y, tWorld.Z), Vector3.UnitX), _tan[si] != null ? _tan[si][lo].W : 1f);

            Vector3 p, n; Vector4 tt; BoneWeight4 w;
            if (o.Free)
            {
                p = pWorld - o.Origin; n = nWorld; tt = tWorld; w = default;
            }
            else if (o.Core)
            {
                int bone = BoneForSegment(si, bind);
                if (bone < 0) bone = mesh.Weights != null ? mesh.Weights[lo].Dominant : 0;
                w = BoneWeight4.Single(bone);
                Rebind(si, w, pWorld, nWorld, tWorld, out p, out n, out tt);
            }
            else
            {
                w = BoneWeight4.Single(AddBone(o, bind >= 0 ? bind : o.BoneSegs.Count > 0 ? o.BoneSegs[0] : 0));
                p = pWorld; n = nWorld; tt = tWorld;
            }

            o.Pos.Add(p);
            o.Nrm.Add(n);
            o.Tan.Add(tt);
            if (mesh.Colors != null) o.Col.Add(Vector4.Lerp(mesh.Colors[lo], mesh.Colors[hi], t));
            for (int k = 0; k < MeshData.MaxUVChannels; k++)
                if (o.UV[k] != null) o.UV[k].Add(Vector4.Lerp(mesh.UV[k][lo], mesh.UV[k][hi], t));
            o.W.Add(w);
            o.SourceVertex.Add(-1);
            return idx;
        }

        private static void PushVertex(OutMesh o, MeshData mesh, int v, Vector3 p, Vector3 n, Vector4 t, BoneWeight4 w, int source)
        {
            o.Pos.Add(p);
            o.Nrm.Add(n);
            o.Tan.Add(t);
            if (mesh.Colors != null) o.Col.Add(mesh.Colors[v]);
            for (int k = 0; k < MeshData.MaxUVChannels; k++)
                if (o.UV[k] != null) o.UV[k].Add(mesh.UV[k][v]);
            o.W.Add(w);
            o.SourceVertex.Add(source);
        }

        /// <summary>Finds bind-space data that skins to the given world position/normal with weights <paramref name="w"/>.</summary>
        private void Rebind(int si, BoneWeight4 w, Vector3 pWorld, Vector3 nWorld, Vector4 tWorld, out Vector3 p, out Vector3 n, out Vector4 t)
        {
            var mats = _skinMat[si];
            var m = new Matrix4x4();
            float total = 0f;
            for (int k = 0; k < 4; k++)
            {
                float wk = w.Weight(k);
                int b = w.Bone(k);
                if (wk <= 0f || b < 0 || b >= mats.Length) continue;
                m += mats[b] * wk;
                total += wk;
            }
            if (total <= 0f) m = Matrix4x4.Identity;
            var inv = MathX.InvertOrIdentity(m);
            p = Vector3.Transform(pWorld, inv);
            n = MathX.SafeNormalize(Vector3.TransformNormal(nWorld, inv), Vector3.UnitY);
            var t3 = MathX.SafeNormalize(Vector3.TransformNormal(new Vector3(tWorld.X, tWorld.Y, tWorld.Z), inv), Vector3.UnitX);
            t = new Vector4(t3, tWorld.W);
        }

        private static void CopyBlendShapes(MeshData src, OutMesh o, MeshData dst)
        {
            if (src.BlendShapes.Count == 0) return;
            int vc = src.VertexCount;
            var lookup = new int[vc];
            foreach (var shape in src.BlendShapes)
            {
                var copy = new BlendShape { Name = shape.Name, Frames = new BlendFrame[shape.Frames.Length] };
                for (int fi = 0; fi < shape.Frames.Length; fi++)
                {
                    var fr = shape.Frames[fi];
                    Array.Fill(lookup, -1);
                    for (int k = 0; k < fr.Indices.Length; k++)
                        if (fr.Indices[k] >= 0 && fr.Indices[k] < vc) lookup[fr.Indices[k]] = k;
                    var idx = new List<int>();
                    var dp = new List<Vector3>();
                    var dn = fr.DeltaNormals != null ? new List<Vector3>() : null;
                    var dt = fr.DeltaTangents != null ? new List<Vector3>() : null;
                    for (int ov = 0; ov < o.SourceVertex.Count; ov++)
                    {
                        int sv = o.SourceVertex[ov];
                        if (sv < 0) continue;
                        int k = lookup[sv];
                        if (k < 0) continue;
                        idx.Add(ov);
                        dp.Add(fr.DeltaPositions[k]);
                        dn?.Add(fr.DeltaNormals[k]);
                        dt?.Add(fr.DeltaTangents[k]);
                    }
                    copy.Frames[fi] = new BlendFrame
                    {
                        Weight = fr.Weight,
                        Indices = idx.ToArray(),
                        DeltaPositions = dp.ToArray(),
                        DeltaNormals = dn?.ToArray(),
                        DeltaTangents = dt?.ToArray(),
                    };
                }
                dst.BlendShapes.Add(copy);
            }
        }

        // ------------------------------------------------------------------------------------------------
        // Caps and chunk colliders
        // ------------------------------------------------------------------------------------------------

        private void BuildCaps(CutResult result, IReadOnlyCollection<int> severedGroups, CutOptions opts)
        {
            var caps = new CapGeometry[_pieceCount];
            foreach (int gid in severedGroups)
            {
                var g = _report.Groups[gid];

                // The outline to cap: the outermost loop that comes from a renderer allowed to make caps.
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
                if (capLoop < 0) continue;
                var outline = _report.Loops[capLoop];

                var bones = new List<(Vector2, float)>();
                foreach (var (seg, q) in g.BoneCrossings)
                    bones.Add((q, BoneRadius(_body.Segments[seg].Kind, g.Area)));

                for (int side = 0; side < 2; side++)
                {
                    float facing = side == 0 ? -1f : 1f;
                    int n = outline.Points2D.Count;

                    // Which piece (and body) owns each stretch of outline on this side.
                    var owner = new int[n];
                    var bind = new int[n];
                    for (int i = 0; i < n; i++)
                    {
                        int e = i < outline.PointEdge.Count ? outline.PointEdge[i] : outline.Edges[0];
                        int node = NodeOf(_isect[e].Segment, side);
                        owner[i] = _nodePiece[node];
                        bind[i] = _nodeBinding[node];
                    }

                    // Tiny runs (a point or two owned by another piece) are noise; absorb them.
                    var smoothed = new int[n];
                    foreach (var (start, length, p) in OwnerRuns(owner, 3))
                        for (int k = 0; k < length; k++) smoothed[(start + k) % n] = p;

                    // One face for the whole section; where it spans several separating pieces (torso and arm at the
                    // armpit, both halves of an elbow) each piece gets the part nearest its own stretch of outline.
                    // Each part of the face rides on the body its neighbouring flesh rides on (a face across the torso
                    // and an arm stump follows both, like the skin around it).
                    var binding = new Dictionary<int, int>();
                    var pointSeg = new int[n];
                    for (int i = 0; i < n; i++) pointSeg[i] = owner[i] == smoothed[i] ? bind[i] : -1;
                    CapBuilder.BuildShared(
                        p => p < 0 ? null : caps[p] ??= new CapGeometry(),
                        outline.Points2D, smoothed, pointSeg, _region, facing, bones,
                        p =>
                        {
                            if (!binding.TryGetValue(p, out int b)) binding[p] = b = MostCommonBinding(bind, owner, p, 0, n);
                            return b;
                        },
                        opts,
                        // A curved cut: fill on a fine grid so the face follows the curve (long triangles would cut across it).
                        fillDetail: _region.Bent ? 0.012f : 0f);
                }
            }

            for (int p = 0; p < _pieceCount; p++)
            {
                var geo = caps[p];
                if (geo == null || geo.VertexCount == 0) continue;
                var piece = result.Pieces[p];
                piece.Cap = MakeCapPiece(geo, piece);
            }
        }

        /// <summary>Turns cap geometry for a piece into a mesh skinned to that piece's bodies.</summary>
        private CapPiece MakeCapPiece(CapGeometry geo, Piece piece)
        {
            int poseSkin = PoseSpaceSkin();
            {
                var md = new MeshData { Name = "DismemberCap" };
                md.Normals = geo.Normals.ToArray();
                md.Tangents = geo.Tangents.ToArray();
                md.UV[0] = geo.UV.ConvertAll(u => new Vector4(u.X, u.Y, 0, 0)).ToArray();
                md.UVDimension[0] = 2;
                if (poseSkin >= 0) AddPoseSpace(md, geo, poseSkin);
                md.Submeshes.Add(geo.Triangles.ToArray());
                var cap = new CapPiece { Mesh = md, UseTargetBones = piece.IsCore };
                if (piece.IsFreeChunk)
                {
                    md.Positions = geo.Positions.ConvertAll(x => x - piece.Origin).ToArray();
                }
                else
                {
                    md.Positions = geo.Positions.ToArray();
                    var segs = new List<int>();
                    var map = new Dictionary<int, int>();
                    md.Weights = new BoneWeight4[geo.VertexCount];
                    for (int v = 0; v < geo.VertexCount; v++)
                    {
                        int seg = geo.VertexSegment[v];
                        if (seg < 0) seg = piece.BodySegments.Count > 0 ? piece.BodySegments[0] : _body.Root;
                        if (!map.TryGetValue(seg, out int b)) { b = segs.Count; segs.Add(seg); map[seg] = b; }
                        md.Weights[v] = BoneWeight4.Single(b);
                    }
                    md.Bindposes = new Matrix4x4[segs.Count];
                    for (int k = 0; k < segs.Count; k++)
                        md.Bindposes[k] = MathX.InvertOrIdentity(piece.IsCore ? _pose.SegmentTargetWorld[segs[k]] : _pose.SegmentBodyWorld[segs[k]]);
                    cap.BoneSegments = segs.ToArray();
                }
                return cap;
            }
        }

        /// <summary>
        /// The flesh renderer whose model space the caps' pose-space channel is written in: the capable skin with the
        /// most triangles that carries a 3D UV1 (the pose-space position some shaders, like SLZ's LitMAS Posespace, read).
        /// </summary>
        private int PoseSpaceSkin()
        {
            int best = -1, bestTris = -1;
            for (int si = 0; si < _skins.Length; si++)
            {
                var s = _skins[si];
                if (!s.AllowCaps || s.Mesh.UV[1] == null || s.Mesh.UVDimension[1] < 3) continue;
                int tris = s.Mesh.TriangleCount;
                if (tris > bestTris) { bestTris = tris; best = si; }
            }
            return best;
        }

        /// <summary>
        /// Writes UV1 as the cap's position in the reference renderer's model (bind) space, which is what that channel
        /// holds on the NPC's own meshes. A cap drawn with the NPC's material then gets the data its shader expects.
        /// </summary>
        private void AddPoseSpace(MeshData md, CapGeometry geo, int si)
        {
            var mats = _skinMat[si];
            var inverse = new Dictionary<int, Matrix4x4>();
            var uv = new Vector4[geo.VertexCount];
            for (int v = 0; v < geo.VertexCount; v++)
            {
                int seg = geo.VertexSegment[v];
                int bone = seg >= 0 ? BoneForSegment(si, seg) : -1;
                if (bone < 0 || bone >= mats.Length) bone = BoneForSegment(si, _body.Root);
                Matrix4x4 m = Matrix4x4.Identity;
                if (bone >= 0 && bone < mats.Length && !inverse.TryGetValue(bone, out m))
                    inverse[bone] = m = MathX.InvertOrIdentity(mats[bone]);
                var p = Vector3.Transform(geo.Positions[v], m);
                uv[v] = new Vector4(p, 0f);
            }
            md.UV[1] = uv;
            md.UVDimension[1] = 3;
        }

        /// <summary>
        /// Splits a closed outline into runs of consecutive points with the same owner. Isolated points and runs
        /// shorter than <paramref name="minLength"/> are absorbed by their neighbours.
        /// </summary>
        private static List<(int start, int length, int piece)> OwnerRuns(int[] owner, int minLength)
        {
            int n = owner.Length;
            var o = (int[])owner.Clone();
            for (int pass = 0; pass < 2; pass++)
                for (int i = 0; i < n; i++)
                {
                    int a = o[(i - 1 + n) % n], b = o[(i + 1) % n];
                    if (o[i] != a && a == b) o[i] = a;
                }

            int first = -1;
            for (int i = 0; i < n; i++)
                if (o[i] != o[(i - 1 + n) % n]) { first = i; break; }
            var runs = new List<(int start, int length, int piece)>();
            if (first < 0)
            {
                runs.Add((0, n, o[0]));
                return runs;
            }
            int startIdx = first, len = 0;
            for (int k = 0; k < n; k++)
            {
                int i = (first + k) % n;
                if (len > 0 && o[i] != o[startIdx]) { runs.Add((startIdx, len, o[startIdx])); startIdx = i; len = 0; }
                len++;
            }
            runs.Add((startIdx, len, o[startIdx]));

            // Absorb tiny runs into the previous one.
            for (int i = 0; i < runs.Count && runs.Count > 1; i++)
            {
                if (runs[i].length >= minLength) continue;
                int prev = (i - 1 + runs.Count) % runs.Count;
                runs[prev] = (runs[prev].start, runs[prev].length + runs[i].length, runs[prev].piece);
                runs.RemoveAt(i);
                i--;
            }
            // Merge neighbours that ended up with the same owner.
            for (int i = 0; i < runs.Count && runs.Count > 1; i++)
            {
                int next = (i + 1) % runs.Count;
                if (runs[next].piece != runs[i].piece) continue;
                runs[i] = (runs[i].start, runs[i].length + runs[next].length, runs[i].piece);
                runs.RemoveAt(next);
                i--;
            }
            return runs;
        }

        private static int MostCommonBinding(int[] bind, int[] owner, int piece, int start, int length)
        {
            var counts = new Dictionary<int, int>();
            int n = bind.Length;
            for (int k = 0; k < length; k++)
            {
                int i = (start + k) % n;
                if (owner[i] != piece) continue;
                counts.TryGetValue(bind[i], out int c);
                counts[bind[i]] = c + 1;
            }
            int best = -1, bestC = -1;
            foreach (var kv in counts) if (kv.Value > bestC) { bestC = kv.Value; best = kv.Key; }
            return best;
        }

        private void BuildChunkColliders(CutResult result, int[][] triNode, Dictionary<int, int>[] splitTris)
        {
            // Gather chunk vertices per chunk node, and free-chunk vertices per piece.
            var pts = new Dictionary<int, List<Vector3>>();
            for (int si = 0; si < _skins.Length; si++)
            {
                var topo = _skins[si].Mesh.Topology;
                for (int f = 0; f < topo.TriangleCount; f++)
                {
                    bool isSplit = splitTris[si].ContainsKey(f);
                    for (int c = 0; c < 3; c++)
                    {
                        int v = topo.Tri[f * 3 + c];
                        int n = isSplit ? NodeOf(_triSeg[si][f], _dist[si][v] > 0 ? 0 : 1) : triNode[si][f];
                        if (!_nodeChunk[n]) continue;
                        if (!pts.TryGetValue(n, out var l)) pts[n] = l = new List<Vector3>();
                        l.Add(_pos[si][v]);
                    }
                }
            }

            foreach (var kv in pts)
            {
                int n = kv.Key;
                var list = Subsample(kv.Value, 96);
                int piece = _nodePiece[n];
                if (piece < 0) continue;
                if (result.Pieces[piece].IsFreeChunk)
                {
                    var p = result.Pieces[piece];
                    var local = new List<Vector3>(p.HullPointsLocal ?? Array.Empty<Vector3>());
                    foreach (var x in list) local.Add(x - p.Origin);
                    p.HullPointsLocal = Subsample(local, 96).ToArray();
                    var min = new Vector3(float.MaxValue); var max = new Vector3(float.MinValue);
                    foreach (var x in p.HullPointsLocal) { min = Vector3.Min(min, x); max = Vector3.Max(max, x); }
                    var e = max - min;
                    p.Volume = Math.Max(1e-6f, e.X * e.Y * e.Z * 0.5f);
                    continue;
                }
                var bmin = new Vector3(float.MaxValue); var bmax = new Vector3(float.MinValue);
                foreach (var x in list) { bmin = Vector3.Min(bmin, x); bmax = Vector3.Max(bmax, x); }
                var ext = bmax - bmin;
                if (Math.Max(ext.X, Math.Max(ext.Y, ext.Z)) < 0.03f) continue;
                result.ChunkColliders.Add(new ChunkCollider { Segment = n / 2, BodySegment = _nodeBinding[n], PointsWorld = list.ToArray() });
            }
        }

        private static List<Vector3> Subsample(List<Vector3> src, int max)
        {
            if (src.Count <= max) return new List<Vector3>(src);
            var r = new List<Vector3>(max);
            float step = (float)src.Count / max;
            for (int i = 0; i < max; i++) r.Add(src[(int)(i * step)]);
            return r;
        }

        private sealed class UnionFind
        {
            private readonly int[] _p;
            public UnionFind(int n) { _p = new int[n]; for (int i = 0; i < n; i++) _p[i] = i; }
            public int Find(int x) { while (_p[x] != x) { _p[x] = _p[_p[x]]; x = _p[x]; } return x; }
            public void Union(int a, int b) { a = Find(a); b = Find(b); if (a != b) _p[b] = a; }
        }
    }
}
