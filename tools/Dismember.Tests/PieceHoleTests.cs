using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dismember.Core;

namespace Dismember.Tests
{
    /// <summary>
    /// Random cuts: is the skin of every piece still closed? An edge that has a triangle on only one side, that was
    /// not open in the original mesh and does not lie on the cut (where the cut face closes it), is a hole you can see
    /// through. Also checks that cut faces sit against the flesh they close: every cap vertex skinned with the cap's
    /// bones must stay where it was built when the piece's bodies are posed differently.
    /// </summary>
    public static class PieceHoleTests
    {
        private static readonly Dictionary<string, int> MismatchKinds = new Dictionary<string, int>();

        public static int Run(NpcAsset npc, int cuts = 120, bool bent = false)
        {
            Console.WriteLine();
            Console.WriteLine($"== holes in the pieces' skin over {cuts} random {(bent ? "curved " : "")}cuts");
            var rng = new Random(bent ? 21 : 11);
            var origins = npc.Pose.SegmentOrigin;

            // Edges that are open in the uncut meshes (seams between separate meshes, cloth edges...).
            var originalOpen = new List<HashSet<(long, long)>>();
            var originalOpenSegments = new List<List<(Vector3 a, Vector3 b)>>();
            foreach (var skin in npc.Skins)
            {
                var world = SkinWorld(npc, skin);
                originalOpen.Add(OpenEdges(world, skin.Mesh.Submeshes));
                originalOpenSegments.Add(OpenEdgeList(world, skin.Mesh.Submeshes).ToList());
            }

            int holed = 0, checkedPieces = 0, capEdgeVerts = 0, capMismatch = 0;
            double worst = 0;
            string worstName = "";
            for (int c = 0; c < cuts; c++)
            {
                int seg = rng.Next(origins.Length);
                int child = npc.Body.Segments[seg].Children.Count > 0 ? npc.Body.Segments[seg].Children[0] : seg;
                var o = Vector3.Lerp(origins[seg], origins[child], (float)rng.NextDouble()) +
                        new Vector3(R(rng) * 0.05f, R(rng) * 0.05f, R(rng) * 0.05f);
                var n = Vector3.Normalize(new Vector3(R(rng), R(rng), R(rng)) + new Vector3(0, 0.0001f, 0));
                var region = CutRegion.Infinite(o, n);
                if (bent) BentCuts.Bend(region, rng);
                var solver = new CutSolver(npc.Body, npc.Skins.ToArray(), npc.Pose, region);
                var report = solver.Analyze();
                var sever = report.Groups.Where(g => g.InReach && g.Area > 0f).Select(g => g.Id).ToList();
                if (sever.Count == 0) continue;
                var result = solver.Execute(sever, new CutOptions());

                // Cut faces must ride the same bodies as the skin they close, or they slide out of place when the
                // bodies move apart.
                foreach (var piece in result.Pieces.Where(p => p.Cap != null && !p.IsFreeChunk))
                {
                    var skinVerts = new List<(Vector3 p, int seg)>();
                    foreach (var rp in piece.Renderers)
                    {
                        if (rp.Unchanged || !npc.Skins[rp.SkinId].AllowCaps) continue;
                        var world = CutTests.WorldPositions(npc, piece, rp);
                        for (int v = 0; v < world.Length; v++)
                        {
                            if (Math.Abs(region.SignedDistance(world[v])) > 0.0005f) continue;
                            int bone = rp.Mesh.Weights[v].Dominant;
                            int s = rp.UsesOriginalBones ? npc.Skins[rp.SkinId].BoneSegment[bone] : rp.BoneSegments[bone];
                            skinVerts.Add((world[v], s));
                        }
                    }
                    var cw = CutTests.CapWorld(npc, piece);
                    var md = piece.Cap.Mesh;
                    for (int v = 0; v < cw.Length; v++)
                    {
                        int capSeg = piece.Cap.BoneSegments[md.Weights[v].I0];
                        float best = 0.0015f; int skinSeg = int.MinValue; bool mixed = false;
                        foreach (var (p, s) in skinVerts)
                        {
                            float d = Vector3.Distance(p, cw[v]);
                            if (d >= 0.0015f) continue;
                            if (skinSeg != int.MinValue && s != skinSeg) mixed = true;
                            if (d < best) { best = d; skinSeg = s; }
                        }
                        // Where two body parts' skin meets at the cut, either is right.
                        if (skinSeg == int.MinValue || mixed) continue;
                        capEdgeVerts++;
                        bool neighbours = skinSeg >= 0 && (npc.Body.Segments[skinSeg].Parent == capSeg || npc.Body.Segments[capSeg].Parent == skinSeg);
                        if (skinSeg != capSeg && !neighbours)
                        {
                            capMismatch++;
                            string mk = npc.Body.Segments[capSeg].Name + " vs skin " + (skinSeg >= 0 ? npc.Body.Segments[skinSeg].Name : "?") + (piece.IsCore ? " (core)" : "");
                            MismatchKinds[mk] = MismatchKinds.TryGetValue(mk, out int mc) ? mc + 1 : 1;
                        }
                    }
                }

                foreach (var piece in result.Pieces)
                {
                    double holeLength = 0, totalLength = 0;
                    foreach (var rp in piece.Renderers)
                    {
                        var skin = npc.Skins[rp.SkinId];
                        if (!skin.AllowCaps || rp.Unchanged) continue;
                        var world = CutTests.WorldPositions(npc, piece, rp);
                        var tris = rp.Mesh.Submeshes;
                        foreach (var (a, b) in OpenEdgeList(world, tris))
                        {
                            float len = Vector3.Distance(a, b);
                            totalLength += len;
                            if (originalOpen[rp.SkinId].Contains(Key(a, b)) || OnOriginalOpen(originalOpenSegments[rp.SkinId], a, b)) continue;
                            if (Math.Abs(region.SignedDistance(a)) < 0.002f && Math.Abs(region.SignedDistance(b)) < 0.002f) continue;
                            holeLength += len;
                        }
                    }
                    if (Environment.GetEnvironmentVariable("HOLEDEBUG") == "1" && holeLength > 0.05 && (c < 6 || Environment.GetEnvironmentVariable("HOLEALL") == "1")) Explain(npc, region, piece, originalOpen, c);
                    if (totalLength <= 0) continue;
                    checkedPieces++;
                    if (holeLength > 0.05)
                    {
                        holed++;
                        string name = $"cut {c} ({npc.Body.Segments[seg].Name}) piece {piece.Index}{(piece.IsCore ? " core" : piece.IsFreeChunk ? " free" : "")} " +
                                      $"[{string.Join(",", piece.BodySegments.Select(s => npc.Body.Segments[s].Name))}]";
                        if (holed <= 12) Console.WriteLine($"   hole: {name}: {holeLength * 100:F0} cm of open edge off the cut");
                        if (holeLength > worst) { worst = holeLength; worstName = name; }
                    }
                }
            }
            Console.WriteLine($"   {checkedPieces} pieces checked, {holed} with holes; worst {worst * 100:F0} cm ({worstName})");
            if (Environment.GetEnvironmentVariable("HOLEDEBUG") == "1")
                foreach (var kv in MismatchKinds.OrderByDescending(k => k.Value).Take(8)) Console.WriteLine($"      {kv.Key}: {kv.Value}");
            Console.WriteLine($"   cut-face edge points riding a different body from the skin beside them: {capMismatch} of {capEdgeVerts}");
            if (capMismatch > capEdgeVerts / 100) { Console.WriteLine("   FAIL cut faces ride the wrong bodies"); return 1 + (holed > checkedPieces / 50 ? 1 : 0); }
            int fails = holed > checkedPieces / 50 ? 1 : 0;
            Console.WriteLine(fails == 0 ? "   OK" : "   FAIL pieces have holes in their skin");
            return fails;
        }

        /// <summary>Debug: which original vertices (segment, side of the plane) the hole edges run along.</summary>
        private static void Explain(NpcAsset npc, CutRegion region, Piece piece, List<HashSet<(long, long)>> originalOpen, int c)
        {
            foreach (var rp in piece.Renderers)
            {
                var skin = npc.Skins[rp.SkinId];
                if (!skin.AllowCaps || rp.Unchanged) continue;
                var orig = SkinWorld(npc, skin);
                var index = new Dictionary<long, int>();
                for (int v = 0; v < orig.Length; v++) index[PointKey(orig[v])] = v;
                var world = CutTests.WorldPositions(npc, piece, rp);
                var hist = new Dictionary<string, int>();
                int unknown = 0;
                foreach (var (a, b) in OpenEdgeList(world, rp.Mesh.Submeshes))
                {
                    if (originalOpen[rp.SkinId].Contains(Key(a, b))) continue;
                    if (Math.Abs(region.SignedDistance(a)) < 0.002f && Math.Abs(region.SignedDistance(b)) < 0.002f) continue;
                    foreach (var p in new[] { a, b })
                    {
                        if (!index.TryGetValue(PointKey(p), out int v)) { unknown++; continue; }
                        int seg = skin.BoneSegment[skin.Mesh.Weights[v].Dominant];
                        string k = (seg >= 0 ? npc.Body.Segments[seg].Name : "?") + (region.SignedDistance(p) > 0 ? " +" : " -");
                        hist[k] = hist.TryGetValue(k, out int x) ? x + 1 : 1;
                    }
                }
                Console.WriteLine($"      cut {c} piece {piece.Index} {skin.Mesh.Name}: hole vertices by segment/side: " +
                                  string.Join(", ", hist.OrderByDescending(h => h.Value).Select(h => h.Key + " x" + h.Value)) + $"; not original: {unknown}");
            }
        }

        /// <summary>Part of an edge that was already open before the cut (the cut split it in two).</summary>
        private static bool OnOriginalOpen(List<(Vector3 a, Vector3 b)> open, Vector3 p, Vector3 q)
        {
            foreach (var (a, b) in open)
            {
                var ab = b - a;
                float l2 = ab.LengthSquared();
                if (l2 < 1e-12f) continue;
                float Off(Vector3 x) { float t = Math.Clamp(Vector3.Dot(x - a, ab) / l2, 0f, 1f); return Vector3.Distance(x, a + ab * t); }
                if (Off(p) < 0.0005f && Off(q) < 0.0005f) return true;
            }
            return false;
        }

        private static float R(Random r) => (float)(r.NextDouble() * 2 - 1);

        internal static Vector3[] SkinWorld(NpcAsset npc, SkinInstance skin)
        {
            var mesh = skin.Mesh;
            var mats = new Matrix4x4[skin.BoneWorld.Length];
            for (int b = 0; b < mats.Length; b++) mats[b] = mesh.Bindposes[b] * skin.BoneWorld[b];
            var world = new Vector3[mesh.VertexCount];
            for (int v = 0; v < world.Length; v++)
            {
                var w = mesh.Weights[v];
                for (int k = 0; k < 4; k++)
                    if (w.Weight(k) > 0) world[v] += Vector3.Transform(mesh.Positions[v], mats[w.Bone(k)]) * w.Weight(k);
            }
            return world;
        }

        private static long Q(float x) => (long)MathF.Round(x * 5000f);   // 0.2 mm grid
        private static long PointKey(Vector3 p) => (Q(p.X) * 73856093L) ^ (Q(p.Y) * 19349663L) ^ (Q(p.Z) * 83492791L);
        internal static (long, long) Key(Vector3 a, Vector3 b)
        {
            long ka = PointKey(a), kb = PointKey(b);
            return ka < kb ? (ka, kb) : (kb, ka);
        }

        internal static IEnumerable<(Vector3 a, Vector3 b)> OpenEdgeList(Vector3[] world, List<int[]> submeshes)
        {
            var count = new Dictionary<(long, long), (int n, Vector3 a, Vector3 b)>();
            foreach (var tris in submeshes)
                for (int i = 0; i + 2 < tris.Length; i += 3)
                    for (int e = 0; e < 3; e++)
                    {
                        var a = world[tris[i + e]]; var b = world[tris[i + (e + 1) % 3]];
                        var k = Key(a, b);
                        if (k.Item1 == k.Item2) continue;
                        count[k] = count.TryGetValue(k, out var x) ? (x.n + 1, x.a, x.b) : (1, a, b);
                    }
            foreach (var kv in count) if (kv.Value.n == 1) yield return (kv.Value.a, kv.Value.b);
        }

        internal static HashSet<(long, long)> OpenEdges(Vector3[] world, List<int[]> submeshes) =>
            new HashSet<(long, long)>(OpenEdgeList(world, submeshes).Select(e => Key(e.a, e.b)));
    }
}
