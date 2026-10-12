using System;
using System.Linq;
using System.Numerics;
using Dismember.Core;

namespace Dismember.Tests
{
    public static class SlitDebug
    {
        public static void Run(NpcAsset npc)
        {
            var chest = npc.Pose.SegmentOrigin[Array.FindIndex(npc.Body.Segments, s => s.Name == "Chest_M")];
            var region = CutRegion.FromBlade(chest + new Vector3(0, 0, 0.3f), Vector3.UnitY, Vector3.UnitX, -Vector3.UnitZ, -0.02f, 0.02f, -0.05f);
            var solver = new CutSolver(npc.Body, npc.Skins.ToArray(), npc.Pose, region);
            var report = solver.Analyze();
            var isects = solver.Intersections.ToList();
            var loopOf = new int[isects.Count];
            Array.Fill(loopOf, -1);
            for (int li = 0; li < report.Loops.Count; li++) foreach (int e in report.Loops[li].Edges) loopOf[e] = li;
            int GroupOf(int li) => report.Groups.FindIndex(g => g.Loops.Contains(li));
            for (int i = 0; i < isects.Count; i++)
            {
                var h = isects[i];
                var m = (h.A + h.B) * 0.5f;
                if (Math.Abs(m.X) > 0.012f || m.Y > 0.2f) continue;
                int li = loopOf[i];
                Console.WriteLine($"   isect skin {h.Skin} tri {h.Tri} sub {npc.Skins[h.Skin].Mesh.Topology.TriSubmesh[h.Tri]} at u {m.X * 100:F1} w {m.Y * 100:F1}: loop {li} " +
                                  (li >= 0 ? $"(capable {report.Loops[li].Capable}, area {report.Loops[li].Area * 1e4:F1} cm2, {report.Loops[li].Points2D.Count} pts) group {GroupOf(li)}" : ""));
            }
            foreach (var g in report.Groups)
                Console.WriteLine($"   group {g.Id}: loops [{string.Join(",", g.Loops)}] area {g.Area * 1e4:F0} cm2 inReach {g.InReach} segs {string.Join("/", g.Segments.Select(s => npc.Body.Segments[s].Name))}");
        }
    }
}
