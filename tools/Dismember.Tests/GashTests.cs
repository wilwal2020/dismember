using System;
using System.Linq;
using System.Numerics;
using Dismember.Core;

namespace Dismember.Tests
{
    /// <summary>The open wound left by a blade stuck half way: on the skin, facing out, only where the edge has been.</summary>
    public static class GashTests
    {
        private static int Seg(NpcAsset n, string name) => Array.FindIndex(n.Body.Segments, s => s.Name == name);

        public static int Run(NpcAsset npc, string outDir)
        {
            Console.WriteLine();
            Console.WriteLine("== wounds from part-way cuts");
            var o = npc.Pose.SegmentOrigin;
            var belly = Vector3.Lerp(o[Seg(npc, "Root_M")], o[Seg(npc, "Spine_M")], 0.9f);
            // A katana swung into the belly from the front, edge along X.
            var region = CutRegion.FromBlade(belly + new Vector3(0, 0, 0.13f), Vector3.UnitY, Vector3.UnitX, -Vector3.UnitZ, -0.45f, 0.45f, -0.03f);
            var solver = new CutSolver(npc.Body, npc.Skins.ToArray(), npc.Pose, region);
            var report = solver.Analyze();
            var torso = report.Groups.Where(g => g.InReach && g.Area > 0f && g.Segments.Contains(Seg(npc, "Spine_M")) || g.Segments.Contains(Seg(npc, "Root_M")))
                                     .OrderByDescending(g => g.Area).FirstOrDefault();
            if (torso == null) { Console.WriteLine("   FAIL no torso section"); return 1; }
            var loop = report.Loops[torso.OuterLoop];
            int fails = 0;
            foreach (float fraction in new[] { 0.15f, 0.5f, 0.9f })
            {
                float depth = torso.WMin + (torso.WMax - torso.WMin) * fraction;
                var gash = GashBuilder.Build(loop, depth, float.NegativeInfinity, float.PositiveInfinity, region.Normal, 0.006f,
                                             seg => npc.Pose.SegmentBodyWorld[seg]);
                if (gash == null) { Console.WriteLine($"   FAIL {fraction:P0} deep: no wound"); fails++; continue; }
                var md = gash.Mesh;
                // Skinned back with the pose it was built in, the wound must be where it was built.
                float worstSkin = 0f;
                var world = new Vector3[md.VertexCount];
                for (int v = 0; v < md.VertexCount; v++)
                {
                    int seg = gash.BoneSegments[md.Weights[v].I0];
                    world[v] = Vector3.Transform(md.Positions[v], md.Bindposes[md.Weights[v].I0] * npc.Pose.SegmentBodyWorld[seg]);
                    worstSkin = Math.Max(worstSkin, Vector3.Distance(world[v], md.Positions[v]));
                }
                // On the skin: every vertex within a centimetre of the outline, and only on the cut side.
                float worstOff = 0f, deepest = float.MinValue;
                foreach (var p in world)
                {
                    float d = loop.Points3D.Min(q => Vector3.Distance(p, q));
                    worstOff = Math.Max(worstOff, d);
                    deepest = Math.Max(deepest, region.To2D(p).Y);
                }
                // Facing out: triangles agree with the surface normal of the outline point nearest to them.
                int tris = md.Submeshes[0].Length / 3, inward = 0;
                for (int t = 0; t < tris; t++)
                {
                    int a = md.Submeshes[0][t * 3], b = md.Submeshes[0][t * 3 + 1], c = md.Submeshes[0][t * 3 + 2];
                    var fn = Vector3.Cross(world[b] - world[a], world[c] - world[a]);
                    if (fn.LengthSquared() < 1e-14f) continue;
                    var centre = (world[a] + world[b] + world[c]) / 3f;
                    int k = 0; float best = float.MaxValue;
                    for (int i = 0; i < loop.Points3D.Count; i++)
                    {
                        float d = Vector3.DistanceSquared(centre, loop.Points3D[i]);
                        if (d < best) { best = d; k = i; }
                    }
                    // Edge-on slivers where the tapered ends pinch together do not count.
                    if (Vector3.Dot(Vector3.Normalize(fn), loop.Normals3D[k]) < -0.2f) inward++;
                }
                if (fraction == 0.5f) Export(npc, region, gash, world, outDir);
                Console.WriteLine($"   {fraction:P0} deep: {gash.Length * 100:F0} cm long, {md.VertexCount} verts, {tris} tris, " +
                                  $"off the skin {worstOff * 1000:F1} mm, reaches {(deepest - torso.WMin) * 100:F1} of {(depth - torso.WMin) * 100:F1} cm, " +
                                  $"{inward} facing inwards, skinning error {worstSkin * 1000:F2} mm");
                // No triangle may hang between two body parts (it would stretch when they move apart).
                int spanning = 0;
                for (int t = 0; t < tris; t++)
                {
                    int a = md.Submeshes[0][t * 3], b = md.Submeshes[0][t * 3 + 1], c = md.Submeshes[0][t * 3 + 2];
                    if (md.Weights[a].I0 != md.Weights[b].I0 || md.Weights[a].I0 != md.Weights[c].I0) spanning++;
                }
                if (spanning > 0) Console.WriteLine($"   {spanning} triangles span two body parts");
                if (spanning > 0 || worstOff > 0.012f || deepest > depth + 0.012f || inward > tris / 50 || worstSkin > 0.0005f) { Console.WriteLine("   FAIL"); fails++; }
            }
            // Facing check on the outline normals themselves: they should point away from the section's middle.
            int outward = 0;
            for (int i = 0; i < loop.Points3D.Count; i++)
                if (Vector3.Dot(loop.Normals3D[i], loop.Points3D[i] - torso.Centroid3D) > 0f) outward++;
            Console.WriteLine($"   outline normals pointing out of the body: {outward} of {loop.Points3D.Count}");
            if (outward < loop.Points3D.Count * 0.9f) { Console.WriteLine("   FAIL surface normals"); fails++; }
            if (fails == 0) Console.WriteLine("   OK");
            return fails;
        }

        /// <summary>The body with a half-way wound across the belly, for the local viewer.</summary>
        private static void Export(NpcAsset npc, CutRegion region, Gash gash, Vector3[] world, string outDir)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("window.CUTS=window.CUTS||{};window.CUTS[\"wound_belly\"]={desc:\"Blade stuck half way into the belly: the open wound it leaves\",normal:[0,1,0],pieces:[{core:true,free:false,meshes:[");
            bool first = true;
            for (int si = 0; si < npc.Skins.Count; si++)
            {
                var skin = npc.Skins[si];
                var rp = new RendererPiece { SkinId = si, Mesh = skin.Mesh, UsesOriginalBones = true };
                var wp = CutTests.WorldPositions(npc, new Piece(), rp);
                CutTests.AppendMesh(sb, ref first, wp, Vector3.Zero, skin.Mesh.Submeshes.SelectMany(s => s).ToArray(), skin.AllowCaps ? "skin" : "hair");
            }
            CutTests.AppendMesh(sb, ref first, world, Vector3.Zero, gash.Mesh.Submeshes[0], "cap", gash.Mesh.UV[0]);
            sb.Append("]}]};");
            System.IO.File.WriteAllText(System.IO.Path.Combine(outDir, "cut_wound_belly.js"), sb.ToString());
            if (!CutTests.Exported.Contains("wound_belly")) CutTests.Exported.Add("wound_belly");
            CutTests.WriteViewer(outDir, CutTests.Exported);
        }
    }
}
