using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using Dismember.Core;

namespace Dismember.Tests
{
    /// <summary>
    /// Wounds with depth (<see cref="CutSolver.ExecuteSlit"/>): a blade stuck half way into the belly must leave an
    /// opening you can look into, a stab through the chest one you can see through, and the flesh walls inside must
    /// close every new opening in the skin.
    /// </summary>
    public static class SlitTests
    {
        private static int Seg(NpcAsset n, string name) => Array.FindIndex(n.Body.Segments, s => s.Name == name);

        public static int Run(NpcAsset npc, string outDir)
        {
            Console.WriteLine();
            Console.WriteLine("== wounds you can see into (slits)");
            var o = npc.Pose.SegmentOrigin;
            var belly = Vector3.Lerp(o[Seg(npc, "Root_M")], o[Seg(npc, "Spine_M")], 0.9f);
            var chest = o[Seg(npc, "Chest_M")];
            int fails = 0;

            // A katana chop half way into the belly from the front, edge along X.
            {
                var region = CutRegion.FromBlade(belly + new Vector3(0, 0, 0.13f), Vector3.UnitY, Vector3.UnitX, -Vector3.UnitZ, -0.45f, 0.45f, -0.15f);
                var solver = new CutSolver(npc.Body, npc.Skins.ToArray(), npc.Pose, region);
                var report = solver.Analyze();
                var torso = report.Groups.Where(g => g.InReach && g.Area > 0f && (g.Segments.Contains(Seg(npc, "Spine_M")) || g.Segments.Contains(Seg(npc, "Root_M"))))
                                         .OrderByDescending(g => g.Area).First();
                float cut = (torso.WMax - torso.WMin) * 0.5f;
                var spec = new SlitSpec { Depth = torso.WMin + cut, Opening = Math.Clamp(0.12f * cut, 0.003f, 0.008f), Taper = Math.Max(0.02f, 0.5f * cut) };
                Region = region;
                var result = solver.ExecuteSlit(new[] { torso.Id }, spec);
                var scene = Scene(npc, result);
                float before = FirstHit(Scene(npc, null), region, new Vector2(0f, region.WMin - 0.1f));
                float after = FirstHit(scene, region, new Vector2(0f, region.WMin - 0.1f));
                Console.WriteLine($"   belly chop {cut * 100:F0} cm deep (of {(torso.WMax - torso.WMin) * 100:F0}), lips {spec.Opening * 2000:F0} mm apart: " +
                                  $"looking straight in, the eye meets flesh {(before - torso.WMin) * 100:F1} cm in before, {(after - torso.WMin) * 100:F1} cm in after");
                if (!(after - torso.WMin > cut * 0.6f)) { Console.WriteLine("      FAIL you should be able to see into the wound"); fails++; }
                fails += Closed(npc, result, "belly chop");
                Export(npc, result, outDir, "slit_belly", "Blade stuck half way into the belly: the wound opens and you can see into it");
                fails += CutThrough(npc, result, belly);
            }

            fails += Growing(npc, belly);

            // A sword (4 cm wide, flat facing up) stabbed straight through the chest from the front.
            {
                var region = CutRegion.FromBlade(chest + new Vector3(0, 0, 0.3f), Vector3.UnitY, Vector3.UnitX, -Vector3.UnitZ, -0.02f, 0.02f, -0.05f);
                var solver = new CutSolver(npc.Body, npc.Skins.ToArray(), npc.Pose, region);
                var report = solver.Analyze();
                var hit = report.Groups.Where(g => g.InReach && g.Area > 0f).Select(g => g.Id).ToList();
                var spec = new SlitSpec { Depth = 0.9f, Opening = 0.008f, Taper = 0.02f };
                Region = region;
                var result = solver.ExecuteSlit(hit, spec);
                var scene = Scene(npc, result);
                float before = FirstHit(Scene(npc, null), region, new Vector2(0f, -0.1f));
                float after = FirstHit(scene, region, new Vector2(0f, -0.1f));
                float aside = FirstHit(scene, region, new Vector2(0.035f, -0.1f));
                Console.WriteLine($"   stab through the chest: straight down the hole the eye meets " +
                                  $"{(float.IsInfinity(after) ? "nothing (you see out the back)" : $"flesh {after * 100:F1} cm in")}; " +
                                  $"before the stab {before * 100:F1} cm in, 3.5 cm to the side {aside * 100:F1} cm in");
                if (!float.IsInfinity(after) || float.IsInfinity(before) || float.IsInfinity(aside)) { Console.WriteLine("      FAIL a stab through should leave a hole you can see through, and only there"); fails++; }
                fails += Closed(npc, result, "chest stab");
                Export(npc, result, outDir, "stab_chest", "A sword stabbed through the chest: a slot you can see through");
            }
            if (fails == 0) Console.WriteLine("      OK");
            return fails;
        }

        /// <summary>
        /// A cut being made (what the game shows while the blade travels): the same belly chop opened deeper and deeper,
        /// every step worked out from the uncut body (the game swaps each in for the last), and whatever else the blade
        /// is through (a hand by the hip) open all the way. Each step must be see-into, closed by its walls, and quick
        /// enough to keep up with the blade.
        /// </summary>
        private static int Growing(NpcAsset npc, Vector3 belly)
        {
            var region = CutRegion.FromBlade(belly + new Vector3(0, 0, 0.13f), Vector3.UnitY, Vector3.UnitX, -Vector3.UnitZ, -0.45f, 0.45f, -0.15f);
            var probe = new CutSolver(npc.Body, npc.Skins.ToArray(), npc.Pose, region).Analyze();
            var torso = probe.Groups.Where(g => g.InReach && g.Area > 0f && (g.Segments.Contains(Seg(npc, "Spine_M")) || g.Segments.Contains(Seg(npc, "Root_M"))))
                                    .OrderByDescending(g => g.Area).First();
            var others = probe.Groups.Where(g => g.InReach && g.Area > 0f && g.Id != torso.Id).ToList();
            int fails = 0;
            float lastSeen = float.MinValue;
            var times = new List<string>();
            Region = region;
            foreach (float frac in new[] { 0.2f, 0.4f, 0.6f, 0.8f })
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var solver = new CutSolver(npc.Body, npc.Skins.ToArray(), npc.Pose, region);
                var report = solver.Analyze();
                float analysis = (float)sw.Elapsed.TotalMilliseconds;
                // Same sections again (ids follow the outline order, which does not change for the same plane).
                var g = report.Groups.First(x => x.Id == torso.Id);
                float cut = (g.WMax - g.WMin) * frac;
                var spec = new SlitSpec
                {
                    Depth = g.WMin + cut,
                    Opening = Math.Clamp(0.12f * cut, 0.003f, 0.008f),
                    Taper = Math.Max(0.02f, 0.5f * cut),
                    GroupDepth = new Dictionary<int, float> { [g.Id] = g.WMin + cut },
                };
                foreach (var o in others) spec.GroupDepth[o.Id] = o.WMax + 0.01f;   // already through those
                var result = solver.ExecuteSlit(new[] { g.Id }.Concat(others.Select(o => o.Id)).ToList(), spec);
                times.Add($"{analysis:F0}+{result.Milliseconds:F0}");
                if (Environment.GetEnvironmentVariable("SLIT_DEBUG") == "1" && result.Pieces[0].Cap != null)
                {
                    var wall = CutTests.CapWorld(npc, result.Pieces[0]);
                    var idx = result.Pieces[0].Cap.Mesh.Submeshes[0];
                    var at = new Vector2(0f, g.WMin + cut * 0.5f);
                    int cover = 0;
                    float umin = float.MaxValue, umax = float.MinValue, wmin = float.MaxValue, wmax = float.MinValue;
                    for (int i = 0; i + 2 < idx.Length; i += 3)
                    {
                        var a = region.To2D(wall[idx[i]]); var b = region.To2D(wall[idx[i + 1]]); var c = region.To2D(wall[idx[i + 2]]);
                        if (InTri(at, a, b, c)) cover++;
                        foreach (var q in new[] { a, b, c }) { umin = Math.Min(umin, q.X); umax = Math.Max(umax, q.X); wmin = Math.Min(wmin, q.Y); wmax = Math.Max(wmax, q.Y); }
                    }
                    Console.WriteLine($"        walls: {idx.Length / 3} tris over u {umin * 100:F0}..{umax * 100:F0} w {wmin * 100:F0}..{wmax * 100:F0} cm; covering u 0 w {at.Y * 100:F1}: {cover}; torso w {g.WMin * 100:F1}..{g.WMax * 100:F1}");
                    foreach (var o in others) Console.WriteLine($"        other {string.Join("/", o.Segments.Select(x => npc.Body.Segments[x].Name))} u {probe.Loops[o.OuterLoop].Min.X * 100:F0}..{probe.Loops[o.OuterLoop].Max.X * 100:F0} w {o.WMin * 100:F0}..{o.WMax * 100:F0}");
                }
                // 1 mm off the cut surface (exactly on it, the eye slips along the seam where the two walls meet) and 3 mm
                // to the side (the walls are filled on a grid, and a sight line exactly along a grid line runs between triangles).
                float seen = FirstHit(Scene(npc, result), region, new Vector2(0.003f, region.WMin - 0.1f), 0.001f);
                Console.WriteLine($"   cut being made, {frac:P0} of the way ({cut * 100:F1} cm): the eye meets flesh {(seen - g.WMin) * 100:F1} cm in");
                if (!(seen - g.WMin > cut * 0.6f) || seen < lastSeen - 0.005f) { Console.WriteLine("      FAIL the opening should follow the edge in"); fails++; }
                lastSeen = seen;
                fails += Closed(npc, result, $"{frac:P0} of the way");
                foreach (var o in others)
                {
                    // Through those: look straight through along the middle of each.
                    var mid = new Vector2(probe.Loops[o.OuterLoop].Centroid.X, region.WMin - 0.1f);
                    float through = FirstHit(Scene(npc, result), region, mid);
                    if (!float.IsInfinity(through) && through < o.WMax - 0.01f)
                    {
                        Console.WriteLine($"      FAIL {string.Join("/", o.Segments.Select(x => npc.Body.Segments[x].Name))}: should be open all the way, eye meets flesh at {(through - o.WMin) * 100:F1} cm");
                        fails++;
                    }
                }
            }
            Console.WriteLine($"   steps took {string.Join(", ", times)} ms (analysis+opening), {others.Count} other part(s) cut through alongside");
            return fails;
        }

        /// <summary>
        /// The body after a slit, as skins to cut again: the opened meshes in place of the old ones, the walls as one more
        /// skin riding on the bodies like a cut face does.
        /// </summary>
        private static SkinInstance[] SkinsAfter(NpcAsset npc, CutResult result)
        {
            var piece = result.Pieces[0];
            var list = new List<SkinInstance>();
            foreach (var skin in npc.Skins)
            {
                var rp = piece.Renderers.FirstOrDefault(r => r.SkinId == skin.Id);
                list.Add(rp == null || rp.Unchanged ? skin : new SkinInstance
                {
                    Id = skin.Id, Mesh = rp.Mesh, BoneWorld = skin.BoneWorld, BoneSegment = skin.BoneSegment,
                    SegmentBone = skin.SegmentBone, AllowCaps = skin.AllowCaps, SubmeshCaps = skin.SubmeshCaps,
                });
            }
            if (piece.Cap != null)
            {
                var cap = piece.Cap;
                var segBone = Enumerable.Repeat(-1, npc.Body.Segments.Length).ToArray();
                for (int b = 0; b < cap.BoneSegments.Length; b++) segBone[cap.BoneSegments[b]] = b;
                list.Add(new SkinInstance
                {
                    Id = list.Count,
                    Mesh = cap.Mesh,
                    BoneWorld = cap.BoneSegments.Select(sg => cap.UseTargetBones ? npc.Pose.SegmentTargetWorld[sg] : npc.Pose.SegmentBodyWorld[sg]).ToArray(),
                    BoneSegment = (int[])cap.BoneSegments.Clone(),
                    SegmentBone = segBone,
                    AllowCaps = true,
                });
            }
            return list.ToArray();
        }

        /// <summary>
        /// A cut through a wound that could not be closed first (something else was cut since): the skin has a gap there
        /// and the walls go down into it. The cut face must still cover the whole cross-section.
        /// </summary>
        private static int CutThrough(NpcAsset npc, CutResult slit, Vector3 belly)
        {
            var skins = SkinsAfter(npc, slit);
            int fails = 0;
            // Both planes pass through the wound (it runs across the belly from the front, 13 cm in).
            foreach (var (name, at, normal) in new[]
            {
                ("tilted 30 degrees", new Vector3(0.02f, 0f, 0.07f), new Vector3(0f, 1f, 0.58f)),
                ("square across it", new Vector3(0.02f, 0.01f, 0f), new Vector3(1f, 0.25f, 0f)),
            })
            {
                var region = CutRegion.Infinite(belly + at, Vector3.Normalize(normal));
                var solver = new CutSolver(npc.Body, skins, npc.Pose, region);
                var report = solver.Analyze();
                var sever = report.Groups.Where(g => g.InReach && g.Area > 0f).Select(g => g.Id).ToList();
                var plain = new CutSolver(npc.Body, npc.Skins.ToArray(), npc.Pose, region).Analyze();
                float before = plain.Groups.Where(g => g.InReach).Sum(g => g.Area), after = report.Groups.Where(g => g.InReach).Sum(g => g.Area);
                CutResult result;
                try { result = solver.Execute(sever, new CutOptions()); }
                catch (Exception e) { Console.WriteLine($"      FAIL cut through the wound, {name}: {e.Message}"); fails++; continue; }
                double worst = 1.0;
                foreach (int gid in result.SeveredGroups)
                {
                    var outline = report.Loops[report.Groups[gid].OuterLoop];
                    if (!outline.Capable || outline.Area < 4e-4f) continue;
                    worst = Math.Min(worst, Coverage(npc, result, region, outline));
                }
                Console.WriteLine($"   cut through the wound, {name}: sections {after * 1e4f:F0} cm2 (without the wound {before * 1e4f:F0}), " +
                                  $"{result.Pieces.Count} pieces, cut faces at least {worst:P0} covered");
                if (worst < 0.95 || Math.Abs(after - before) > 0.15f * before) { Console.WriteLine($"      FAIL cut through the wound, {name}"); fails++; }
            }
            return fails;
        }

        /// <summary>The share of a section's inside that a cut face covers, from both sides (worst side).</summary>
        private static double Coverage(NpcAsset npc, CutResult result, CutRegion region, SectionLoop outline)
        {
            var rng = new Random(3);
            double worst = 1.0;
            for (int side = 0; side < 2; side++)
            {
                float facing = side == 0 ? -1f : 1f;
                var tris = new List<(Vector2 a, Vector2 b, Vector2 c)>();
                foreach (var p in result.Pieces.Where(p => p.Cap != null))
                {
                    var w = CutTests.CapWorld(npc, p);
                    var idx = p.Cap.Mesh.Submeshes[0];
                    for (int i = 0; i + 2 < idx.Length; i += 3)
                    {
                        if (Math.Abs(region.SignedDistance((w[idx[i]] + w[idx[i + 1]] + w[idx[i + 2]]) / 3f)) > 0.003f) continue;
                        var a = region.To2D(w[idx[i]]); var b = region.To2D(w[idx[i + 1]]); var c = region.To2D(w[idx[i + 2]]);
                        float area = MathX.Cross(b - a, c - a);
                        if (Math.Abs(area) > 1e-12f && Math.Sign(area) == Math.Sign(facing)) tris.Add((a, b, c));
                    }
                }
                int inside = 0, covered = 0;
                for (int k = 0; k < 2000 && inside < 300; k++)
                {
                    var q = new Vector2(outline.Min.X + (float)rng.NextDouble() * (outline.Max.X - outline.Min.X),
                                        outline.Min.Y + (float)rng.NextDouble() * (outline.Max.Y - outline.Min.Y));
                    if (!MathX.PointInPolygon(q, outline.Points2D) || MathX.DistanceToPolygon(q, outline.Points2D) < 0.004f) continue;
                    inside++;
                    if (tris.Any(t => InTri(q, t.a, t.b, t.c))) covered++;
                }
                if (inside >= 20) worst = Math.Min(worst, (double)covered / inside);
            }
            return worst;
        }

        private static bool InTri(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
        {
            float d1 = MathX.Cross(b - a, p - a), d2 = MathX.Cross(c - b, p - b), d3 = MathX.Cross(a - c, p - c);
            return !((d1 < 0 || d2 < 0 || d3 < 0) && (d1 > 0 || d2 > 0 || d3 > 0));
        }

        /// <summary>World triangles of every renderer and the walls after a slit (or the uncut body for null).</summary>
        private static List<(Vector3 a, Vector3 b, Vector3 c)> Scene(NpcAsset npc, CutResult result)
        {
            var tris = new List<(Vector3, Vector3, Vector3)>();
            void Add(Vector3[] p, IEnumerable<int[]> subs)
            {
                foreach (var s in subs)
                    for (int i = 0; i + 2 < s.Length; i += 3) tris.Add((p[s[i]], p[s[i + 1]], p[s[i + 2]]));
            }
            for (int si = 0; si < npc.Skins.Count; si++)
            {
                if (!npc.Skins[si].AllowCaps) continue;
                var rp = result?.Pieces[0].Renderers.FirstOrDefault(r => r.SkinId == si);
                if (rp == null || rp.Unchanged) Add(PieceHoleTests.SkinWorld(npc, npc.Skins[si]), npc.Skins[si].Mesh.Submeshes);
                else Add(CutTests.WorldPositions(npc, result.Pieces[0], rp), rp.Mesh.Submeshes);
            }
            if (result?.Pieces[0].Cap != null) Add(CutTests.CapWorld(npc, result.Pieces[0]), result.Pieces[0].Cap.Mesh.Submeshes);
            return tris;
        }

        /// <summary>How far along W a line of sight from <paramref name="from"/> (plane coordinates) first meets a triangle.</summary>
        private static float FirstHit(List<(Vector3 a, Vector3 b, Vector3 c)> tris, CutRegion region, Vector2 from, float off = 0f)
        {
            var origin = region.To3D(from, off);
            var dir = region.AxisW;
            float best = float.PositiveInfinity;
            foreach (var (a, b, c) in tris)
                if (Ray(origin, dir, a, b, c, out float t) && t < best) best = t;
            if (Environment.GetEnvironmentVariable("SLIT_DEBUG") == "1")
            {
                int hits = 0;
                foreach (var (a, b, c) in tris)
                    if (Ray(origin, dir, a, b, c, out float t2) && hits++ < 6)
                    {
                        string P(Vector3 p) { var d = p - region.Origin; return $"(u {Vector3.Dot(d, region.AxisU) * 100:F1} w {Vector3.Dot(d, region.AxisW) * 100:F1} n {Vector3.Dot(d, region.Normal) * 1000:F1}mm)"; }
                        Console.WriteLine($"        hit at w {(t2 + from.Y) * 100:F1} cm: {P(a)} {P(b)} {P(c)}");
                    }
            }
            return best + from.Y;
        }

        private static bool Ray(Vector3 o, Vector3 d, Vector3 a, Vector3 b, Vector3 c, out float t)
        {
            t = 0f;
            var e1 = b - a;
            var e2 = c - a;
            var p = Vector3.Cross(d, e2);
            float det = Vector3.Dot(e1, p);
            if (Math.Abs(det) < 1e-12f) return false;
            float inv = 1f / det;
            var s = o - a;
            float u = Vector3.Dot(s, p) * inv;
            if (u < 0f || u > 1f) return false;
            var q = Vector3.Cross(s, e1);
            float v = Vector3.Dot(d, q) * inv;
            if (v < 0f || u + v > 1f) return false;
            t = Vector3.Dot(e2, q) * inv;
            return t > 0f;
        }

        /// <summary>Every edge the slit opened in the skin must have the flesh wall right against it (no gap to see through).</summary>
        private static CutRegion Region;

        private static int Closed(NpcAsset npc, CutResult result, string name)
        {
            var piece = result.Pieces[0];
            if (piece.Cap == null) { Console.WriteLine($"      FAIL {name}: no walls"); return 1; }
            var wall = CutTests.CapWorld(npc, piece);
            var wallTris = piece.Cap.Mesh.Submeshes[0];
            int open = 0, far = 0;
            float worst = 0f;
            foreach (var rp in piece.Renderers)
            {
                if (rp.Unchanged || !npc.Skins[rp.SkinId].AllowCaps) continue;
                var skin = npc.Skins[rp.SkinId];
                // Flesh only: see-through cloth (Ford's zipper) is split too, but nothing has to close it.
                List<int[]> Flesh(List<int[]> subs) => subs.Where((s, k) => skin.SubmeshCaps == null || (k < skin.SubmeshCaps.Length && skin.SubmeshCaps[k])).ToList();
                var before = PieceHoleTests.OpenEdges(PieceHoleTests.SkinWorld(npc, skin), Flesh(skin.Mesh.Submeshes));
                var world = CutTests.WorldPositions(npc, piece, rp);
                // Every edge of the mesh: where the slit ends (no opening there) a split point lies on its neighbour's edge, which
                // closes it as well as a shared edge would.
                var all = new List<(Vector3, Vector3)>();
                foreach (var s in Flesh(rp.Mesh.Submeshes))
                    for (int i = 0; i + 2 < s.Length; i += 3)
                        for (int e = 0; e < 3; e++) all.Add((world[s[i + e]], world[s[i + (e + 1) % 3]]));
                // Covered when every bit of it lies along other edges (one longer edge, or the two halves of a split one).
                bool OnAnotherEdge(Vector3 a, Vector3 b)
                {
                    for (int k = 0; k <= 4; k++)
                    {
                        var p = Vector3.Lerp(a, b, 0.1f + 0.2f * k);
                        bool covered = false;
                        foreach (var (c, d) in all)
                        {
                            if ((Vector3.DistanceSquared(a, c) < 1e-10f && Vector3.DistanceSquared(b, d) < 1e-10f) ||
                                (Vector3.DistanceSquared(a, d) < 1e-10f && Vector3.DistanceSquared(b, c) < 1e-10f)) continue;
                            if (SegmentDistance(p, c, d) < 0.0003f) { covered = true; break; }
                        }
                        if (!covered) return false;
                    }
                    return true;
                }
                foreach (var (a, b) in PieceHoleTests.OpenEdgeList(world, Flesh(rp.Mesh.Submeshes)))
                {
                    if (before.Contains(PieceHoleTests.Key(a, b)) || OnAnotherEdge(a, b)) continue;
                    open++;
                    var m = (a + b) * 0.5f;
                    float d = float.MaxValue;
                    for (int i = 0; i + 2 < wallTris.Length; i += 3)
                        d = Math.Min(d, PointTriangle(m, wall[wallTris[i]], wall[wallTris[i + 1]], wall[wallTris[i + 2]]));
                    worst = Math.Max(worst, d);
                    if (d > 0.003f) far++;
                    if (d > 0.003f && Environment.GetEnvironmentVariable("SLIT_DEBUG") == "1")
                    {
                        var r = Region;
                        string P(Vector3 p) { var dd = p - r.Origin; return $"(u {Vector3.Dot(dd, r.AxisU) * 100:F1} w {Vector3.Dot(dd, r.AxisW) * 100:F1} n {Vector3.Dot(dd, r.Normal) * 1000:F1}mm)"; }
                        Console.WriteLine($"        far edge {d * 1000:F1} mm: {P(a)} {P(b)}");
                    }
                }
            }
            Console.WriteLine($"      {name}: {open} edges opened in the skin, {far} of them more than 3 mm from a wall (worst {worst * 1000:F1} mm)");
            if (open == 0 || far > open / 50) { Console.WriteLine($"      FAIL {name}: the walls do not close the slit"); return 1; }
            return 0;
        }

        private static float SegmentDistance(Vector3 p, Vector3 a, Vector3 b)
        {
            var ab = b - a;
            float l2 = ab.LengthSquared();
            float t = l2 > 1e-14f ? Math.Clamp(Vector3.Dot(p - a, ab) / l2, 0f, 1f) : 0f;
            return Vector3.Distance(p, a + ab * t);
        }

        private static float PointTriangle(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
        {
            // Closest point on a triangle (Ericson, Real-Time Collision Detection 5.1.5).
            var ab = b - a; var ac = c - a; var ap = p - a;
            float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
            if (d1 <= 0f && d2 <= 0f) return Vector3.Distance(p, a);
            var bp = p - b;
            float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
            if (d3 >= 0f && d4 <= d3) return Vector3.Distance(p, b);
            float vc = d1 * d4 - d3 * d2;
            if (vc <= 0f && d1 >= 0f && d3 <= 0f) return Vector3.Distance(p, a + ab * (d1 / (d1 - d3)));
            var cp = p - c;
            float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
            if (d6 >= 0f && d5 <= d6) return Vector3.Distance(p, c);
            float vb = d5 * d2 - d1 * d6;
            if (vb <= 0f && d2 >= 0f && d6 <= 0f) return Vector3.Distance(p, a + ac * (d2 / (d2 - d6)));
            float va = d3 * d6 - d5 * d4;
            if (va <= 0f && d4 - d3 >= 0f && d5 - d6 >= 0f) return Vector3.Distance(p, b + (c - b) * ((d4 - d3) / ((d4 - d3) + (d5 - d6))));
            float den = 1f / (va + vb + vc);
            return Vector3.Distance(p, a + ab * (vb * den) + ac * (vc * den));
        }

        private static void Export(NpcAsset npc, CutResult result, string outDir, string name, string desc)
        {
            var sb = new StringBuilder();
            sb.Append("window.CUTS=window.CUTS||{};window.CUTS[").Append(CutTests.Q(name)).Append("]={desc:").Append(CutTests.Q(desc))
              .Append(",normal:[0,1,0],pieces:[{core:true,free:false,meshes:[");
            bool first = true;
            var piece = result.Pieces[0];
            for (int si = 0; si < npc.Skins.Count; si++)
            {
                var skin = npc.Skins[si];
                var rp = piece.Renderers.FirstOrDefault(r => r.SkinId == si);
                var mesh = rp == null || rp.Unchanged ? skin.Mesh : rp.Mesh;
                var wp = rp == null || rp.Unchanged ? PieceHoleTests.SkinWorld(npc, skin) : CutTests.WorldPositions(npc, piece, rp);
                CutTests.AppendMesh(sb, ref first, wp, Vector3.Zero, mesh.Submeshes.SelectMany(s => s).ToArray(), skin.AllowCaps ? "skin" : "hair");
            }
            if (piece.Cap != null)
                CutTests.AppendMesh(sb, ref first, CutTests.CapWorld(npc, piece), Vector3.Zero, piece.Cap.Mesh.Submeshes[0], "cap", piece.Cap.Mesh.UV[0]);
            sb.Append("]}]};");
            System.IO.File.WriteAllText(System.IO.Path.Combine(outDir, "cut_" + name + ".js"), sb.ToString());
            if (!CutTests.Exported.Contains(name)) CutTests.Exported.Add(name);
            CutTests.WriteViewer(outDir, CutTests.Exported);
        }
    }
}
