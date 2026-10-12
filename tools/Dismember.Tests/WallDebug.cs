using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dismember.Core;

namespace Dismember.Tests
{
    /// <summary>Diagnostics (DISMEMBER_ONLY=walldebug): the belly wall polygon at several depths, filled on its own.</summary>
    public static class WallDebug
    {
        public static void Run(NpcAsset npc)
        {
            int Seg(string name) => Array.FindIndex(npc.Body.Segments, s => s.Name == name);
            var o = npc.Pose.SegmentOrigin;
            var belly = Vector3.Lerp(o[Seg("Root_M")], o[Seg("Spine_M")], 0.9f);
            var region = CutRegion.FromBlade(belly + new Vector3(0, 0, 0.13f), Vector3.UnitY, Vector3.UnitX, -Vector3.UnitZ, -0.45f, 0.45f, -0.15f);
            var report = new CutSolver(npc.Body, npc.Skins.ToArray(), npc.Pose, region).Analyze();
            var torso = report.Groups.Where(g => g.InReach && g.Area > 0f && (g.Segments.Contains(Seg("Spine_M")) || g.Segments.Contains(Seg("Root_M"))))
                                     .OrderByDescending(g => g.Area).First();
            var loop = report.Loops[torso.OuterLoop];
            Console.WriteLine($"   torso outline {loop.Points2D.Count} pts, self-intersecting {CapCoverageTests.SelfIntersects(loop.Points2D)}, loops in group {torso.Loops.Count}");
            foreach (float frac in new[] { 0.2f, 0.4f, 0.6f, 0.8f })
            {
                float depth = torso.WMin + (torso.WMax - torso.WMin) * frac;
                var pts = new List<Vector2>(loop.Points2D);
                Clip(pts, q => depth - q.Y);
                var geo = new CapGeometry();
                CapBuilder.BuildShared(_ => geo, pts, new int[pts.Count], null, region, 1f, null, _ => 0, new CutOptions(), null, null, 0.015f);
                var geo2 = new CapGeometry();
                CapBuilder.BuildShared(_ => geo2, pts, new int[pts.Count], null, region, 1f, null, _ => 0, new CutOptions());
                {
                    var solver = new CutSolver(npc.Body, npc.Skins.ToArray(), npc.Pose, region);
                    solver.Analyze();
                    float cut = depth - torso.WMin;
                    var spec = new SlitSpec { Depth = depth, Opening = Math.Clamp(0.12f * cut, 0.003f, 0.008f), Taper = Math.Max(0.02f, 0.5f * cut) };
                    if (Environment.GetEnvironmentVariable("WALL_GROUPDEPTH") == "1") { spec.GroupDepth = new Dictionary<int, float> { [torso.Id] = depth }; spec.Depth = 0f; }
                    var res = solver.ExecuteSlit(new[] { torso.Id }, spec);
                    var cap = res.Pieces[0].Cap;
                    var wall = CutTests.CapWorld(npc, res.Pieces[0]);
                    var idx = cap.Mesh.Submeshes[0];
                    var holes = new List<string>();
                    int n = 0, miss = 0;
                    for (float u = -0.14f; u <= 0.14f; u += 0.01f)
                        for (float w = torso.WMin + 0.01f; w < depth - 0.005f; w += 0.01f)
                        {
                            var q = new Vector2(u, w);
                            if (!MathX.PointInPolygon(q, pts) || MathX.DistanceToPolygon(q, pts) < 0.004f) continue;
                            n++;
                            bool hit = false;
                            for (int i = 0; i + 2 < idx.Length && !hit; i += 3)
                            {
                                var a = region.To2D(wall[idx[i]]); var b = region.To2D(wall[idx[i + 1]]); var c = region.To2D(wall[idx[i + 2]]);
                                float d1 = MathX.Cross(b - a, q - a), d2 = MathX.Cross(c - b, q - b), d3 = MathX.Cross(a - c, q - c);
                                hit = !((d1 < 0 || d2 < 0 || d3 < 0) && (d1 > 0 || d2 > 0 || d3 > 0));
                            }
                            if (!hit) { miss++; if (holes.Count < 12) holes.Add($"({u * 100:F0},{w * 100:F0})"); }
                        }
                    Console.WriteLine($"   {frac:P0}: walls from the solver miss {miss} of {n} points (u,w cm): {string.Join(" ", holes)}");
                }
                Console.WriteLine($"   {frac:P0}: clipped {pts.Count} pts, area {MathX.SignedArea(pts) * 1e4f:F0} cm2, self-intersecting {CapCoverageTests.SelfIntersects(pts)}; " +
                                  $"grid fill {Covered(geo, region, pts):P0} covered, plain fill {Covered(geo2, region, pts):P0}");
            }
        }

        private static double Covered(CapGeometry geo, CutRegion region, List<Vector2> poly)
        {
            var rng = new Random(1);
            var min = new Vector2(float.MaxValue); var max = new Vector2(float.MinValue);
            foreach (var q in poly) { min = Vector2.Min(min, q); max = Vector2.Max(max, q); }
            int inside = 0, covered = 0;
            for (int k = 0; k < 3000 && inside < 400; k++)
            {
                var q = new Vector2(min.X + (float)rng.NextDouble() * (max.X - min.X), min.Y + (float)rng.NextDouble() * (max.Y - min.Y));
                if (!MathX.PointInPolygon(q, poly) || MathX.DistanceToPolygon(q, poly) < 0.004f) continue;
                inside++;
                for (int i = 0; i + 2 < geo.Triangles.Count; i += 3)
                {
                    var a = region.To2D(geo.Positions[geo.Triangles[i]]); var b = region.To2D(geo.Positions[geo.Triangles[i + 1]]); var c = region.To2D(geo.Positions[geo.Triangles[i + 2]]);
                    float d1 = MathX.Cross(b - a, q - a), d2 = MathX.Cross(c - b, q - b), d3 = MathX.Cross(a - c, q - c);
                    if (!((d1 < 0 || d2 < 0 || d3 < 0) && (d1 > 0 || d2 > 0 || d3 > 0))) { covered++; break; }
                }
            }
            return inside == 0 ? 1.0 : (double)covered / inside;
        }

        private static void Clip(List<Vector2> pts, Func<Vector2, float> inside)
        {
            var outP = new List<Vector2>();
            for (int i = 0; i < pts.Count; i++)
            {
                var a = pts[i]; var b = pts[(i + 1) % pts.Count];
                float fa = inside(a), fb = inside(b);
                if (fa >= 0f) outP.Add(a);
                if ((fa >= 0f) != (fb >= 0f)) outP.Add(Vector2.Lerp(a, b, fa / (fa - fb)));
            }
            pts.Clear(); pts.AddRange(outP);
        }
    }
}
