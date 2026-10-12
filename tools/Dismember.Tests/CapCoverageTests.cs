using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dismember.Core;

namespace Dismember.Tests
{
    /// <summary>
    /// Random cuts all over the body: does every severed cross-section get a cut face with no holes in it?
    /// Points are sampled inside each section's outline and checked against the cap triangles of each side that face
    /// outwards (the ones a camera outside the piece sees).
    /// </summary>
    public static class CapCoverageTests
    {
        public static int Run(NpcAsset npc, int cuts = 160, bool bent = false)
        {
            Console.WriteLine();
            Console.WriteLine($"== cap coverage over {cuts} random {(bent ? "curved " : "")}cuts");
            var rng = new Random(bent ? 17 : 7);
            int sections = 0, holed = 0, failed = 0, overflow = 0;
            double worst = 1.0;
            string worstName = "";
            var origins = npc.Pose.SegmentOrigin;
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
                CutResult result;
                try { result = solver.Execute(sever, new CutOptions()); }
                catch (Exception e) { Console.WriteLine($"   cut {c}: execute threw {e.Message}"); failed++; continue; }

                foreach (int gid in result.SeveredGroups)
                {
                    var g = report.Groups[gid];
                    var outline = report.Loops[g.OuterLoop];
                    if (!outline.Capable || outline.Area < 4e-4f) continue;   // smaller than 2x2 cm: not worth checking
                    sections++;
                    for (int side = 0; side < 2; side++)
                    {
                        float facing = side == 0 ? -1f : 1f;
                        var tris = new List<(Vector2 a, Vector2 b, Vector2 c, bool front)>();
                        foreach (var p in result.Pieces.Where(p => p.Cap != null))
                        {
                            var w = CutTests.CapWorld(npc, p);
                            var idx = p.Cap.Mesh.Submeshes[0];
                            for (int i = 0; i + 2 < idx.Length; i += 3)
                            {
                                var a = region.To2D(w[idx[i]]); var b = region.To2D(w[idx[i + 1]]); var cc = region.To2D(w[idx[i + 2]]);
                                float area = MathX.Cross(b - a, cc - a);
                                if (Math.Abs(area) < 1e-12f) continue;
                                // Only this side's caps: they sit on its side of the plane (rim/bone discs are lifted).
                                var centre3 = (w[idx[i]] + w[idx[i + 1]] + w[idx[i + 2]]) / 3f;
                                float d = region.SignedDistance(centre3);
                                if (Math.Abs(d) > 0.003f) continue;
                                tris.Add((a, b, cc, Math.Sign(area) == Math.Sign(facing)));
                            }
                        }
                        // Flesh sticking out past the outline (it would show outside the body).
                        // Each triangle is judged against the section it is nearest to (other sections' faces share the plane).
                        double outArea = 0, allArea = 0;
                        var outlines = result.SeveredGroups.Select(x => report.Loops[report.Groups[x].OuterLoop]).Where(l => l.Capable).ToList();
                        float Dist(SectionLoop l, Vector2 q) => MathX.PointInPolygon(q, l.Points2D) ? 0f : MathX.DistanceToPolygon(q, l.Points2D);
                        foreach (var t in tris.Where(t => t.front))
                        {
                            var cen = (t.a + t.b + t.c) / 3f;
                            float mine = Dist(outline, cen);
                            if (outlines.Any(l => l != outline && Dist(l, cen) < mine)) continue;
                            float ta = Math.Abs(MathX.Cross(t.b - t.a, t.c - t.a)) * 0.5f;
                            allArea += ta;
                            if (mine > 0.004f) outArea += ta;
                        }
                        if (allArea > 0 && outArea / allArea > 0.03)
                        {
                            overflow++;
                            Console.WriteLine($"   overflow: cut {c} section {string.Join("/", g.Segments.Select(s => npc.Body.Segments[s].Name))} side {side}: {outArea / allArea:P0} of the face is outside the outline");
                        }
                        int inside = 0, covered = 0, backOnly = 0;
                        var min = outline.Min; var max = outline.Max;
                        for (int k = 0; k < 600 && inside < 300; k++)
                        {
                            var q = new Vector2(min.X + (float)rng.NextDouble() * (max.X - min.X), min.Y + (float)rng.NextDouble() * (max.Y - min.Y));
                            if (!MathX.PointInPolygon(q, outline.Points2D)) continue;
                            // Stay clear of the outline itself: the rim and simplification tolerance live there.
                            if (MathX.DistanceToPolygon(q, outline.Points2D) < 0.004f) continue;
                            inside++;
                            bool front = false, back = false;
                            foreach (var t in tris)
                            {
                                if (!InTri(q, t.a, t.b, t.c)) continue;
                                if (t.front) { front = true; break; }
                                back = true;
                            }
                            if (front) covered++;
                            else if (back) backOnly++;
                        }
                        if (inside < 20) continue;
                        double frac = (double)covered / inside;
                        // Each side gets its own pass: both caps lie in the plane, so count the side's own facing only.
                        if (frac < 0.97)
                        {
                            holed++;
                            string name = $"cut {c} ({npc.Body.Segments[seg].Name}) section {string.Join("/", g.Segments.Select(s => npc.Body.Segments[s].Name))} side {side}";
                            Console.WriteLine($"   hole: {name}: {frac:P0} covered, {(double)backOnly / inside:P0} only by back faces, outline {outline.Points2D.Count} pts{(outline.WasClosed ? "" : " (was open)")}, self-intersecting={SelfIntersects(outline.Points2D)}");
                            if (frac < worst) { worst = frac; worstName = name; }
                        }
                    }
                }
            }
            Console.WriteLine($"   {sections} sections checked, {holed} cut faces with holes, {overflow} spilling outside, {failed} cuts failed; worst {worst:P0} {worstName}");
            int fails = failed;
            if (holed > sections / 50 || overflow > sections / 50) fails++;   // allow the odd sliver, not a pattern
            Console.WriteLine(fails == 0 ? "   OK" : "   FAIL too many holes in cut faces");
            return fails;
        }

        private static float R(Random r) => (float)(r.NextDouble() * 2 - 1);

        private static bool InTri(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
        {
            float d1 = MathX.Cross(b - a, p - a), d2 = MathX.Cross(c - b, p - b), d3 = MathX.Cross(a - c, p - c);
            bool neg = d1 < 0 || d2 < 0 || d3 < 0, pos = d1 > 0 || d2 > 0 || d3 > 0;
            return !(neg && pos);
        }

        public static bool SelfIntersects(IReadOnlyList<Vector2> poly)
        {
            int n = poly.Count;
            for (int i = 0; i < n; i++)
            {
                var a = poly[i]; var b = poly[(i + 1) % n];
                for (int j = i + 2; j < n; j++)
                {
                    if (i == 0 && j == n - 1) continue;
                    var c = poly[j]; var d = poly[(j + 1) % n];
                    if (MathX.SegmentsCross(a, b, c, d)) return true;
                }
            }
            return false;
        }
    }
}
