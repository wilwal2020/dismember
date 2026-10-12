using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dismember.Core;

namespace Dismember.Tests
{
    /// <summary>
    /// The force model on real data: swings built the way the mod builds them from a blade contact
    /// (contact point, edge line, cutting direction, swing energy), checked for what they sever or where they stick.
    /// </summary>
    public static class SwingTests
    {
        private sealed class Weapon
        {
            public string Name;
            public float Mass;
            public float EdgeLength;   // length of the cutting edge
        }

        private static readonly Weapon Katana = new Weapon { Name = "katana", Mass = 1.5f, EdgeLength = 0.85f };
        private static readonly Weapon Dagger = new Weapon { Name = "dagger", Mass = 0.49f, EdgeLength = 0.25f };
        private static readonly Weapon Claymore = new Weapon { Name = "claymore", Mass = 2.4f, EdgeLength = 1.1f };

        private const float HandKg = 2.5f;   // default "arm strength" per hand

        private sealed class Swing
        {
            public string Name;
            public Weapon Weapon;
            public int Hands = 1;
            public float Speed;           // edge speed at the contact, m/s
            public Vector3 Contact;       // where the edge meets the body
            public Vector3 Along;         // edge direction
            public Vector3 CutDir;        // swing direction
            public float ContactAlongEdge = 0.6f;   // contact position along the edge (0 = hilt end, 1 = tip)
            public Func<CutPlan, CrossSectionReport, NpcAsset, string> Expect;
        }

        private static int Seg(NpcAsset n, string name) => Array.FindIndex(n.Body.Segments, s => s.Name == name);

        private static bool SeversSegment(CutPlan plan, CrossSectionReport r, NpcAsset n, string seg) =>
            plan.Severed.Any(g => r.Groups[g].Segments.Contains(Seg(n, seg)));

        private static bool StuckIn(CutPlan plan, CrossSectionReport r, NpcAsset n, string seg) =>
            plan.StuckGroup >= 0 && r.Groups[plan.StuckGroup].Segments.Contains(Seg(n, seg));

        public static int Run(NpcAsset npc)
        {
            var o = npc.Pose.SegmentOrigin;
            var neck = Vector3.Lerp(o[Seg(npc, "Chest_M")], o[Seg(npc, "Head_M")], 0.8f);
            var belly = Vector3.Lerp(o[Seg(npc, "Root_M")], o[Seg(npc, "Spine_M")], 0.9f);
            var head = o[Seg(npc, "Head_M")];
            var elbowR = o[Seg(npc, "Elbow_R")];
            var wristR = o[Seg(npc, "Wrist_R")];

            // Ford faces +Z; his right is -X... his left arm is at -X in the bundle, so "right" here is +X.
            var swings = new List<Swing>
            {
                new Swing
                {
                    Name = "neck, hard one-handed katana swing (7 m/s)", Weapon = Katana, Speed = 7f,
                    Contact = neck + new Vector3(0.07f, 0, 0), Along = Vector3.Normalize(new Vector3(0, 0.15f, 1)), CutDir = -Vector3.UnitX,
                    Expect = (p, r, n) => SeversSegment(p, r, n, "Head_M") || p.Severed.Any(g => r.Groups[g].Segments.Contains(Seg(n, "Chest_M"))) ? null : "should decapitate",
                },
                new Swing
                {
                    Name = "neck, lazy katana swing (3 m/s)", Weapon = Katana, Speed = 3f,
                    Contact = neck + new Vector3(0.07f, 0, 0), Along = Vector3.Normalize(new Vector3(0, 0.15f, 1)), CutDir = -Vector3.UnitX,
                    Expect = (p, r, n) => !p.AnySevered && p.StuckGroup >= 0 ? null : "should get stuck in the neck",
                },
                new Swing
                {
                    Name = "forearm, quick dagger slash (6 m/s)", Weapon = Dagger, Speed = 6f,
                    Contact = Vector3.Lerp(elbowR, wristR, 0.5f) + new Vector3(0, 0.04f, 0), Along = Vector3.UnitZ, CutDir = -Vector3.UnitY,
                    Expect = (p, r, n) => SeversSegment(p, r, n, "Elbow_R") ? null : "should take the forearm off",
                },
                new Swing
                {
                    Name = "belly, one-handed katana (8 m/s)", Weapon = Katana, Speed = 8f,
                    Contact = belly + new Vector3(0, 0, 0.13f), Along = Vector3.UnitX, CutDir = -Vector3.UnitZ, ContactAlongEdge = 0.5f,
                    // The forearms hang in the way at belly height: they may come off, the torso must not.
                    Expect = (p, r, n) => !SeversSegment(p, r, n, "Spine_M") && !SeversSegment(p, r, n, "Root_M") &&
                                          (StuckIn(p, r, n, "Spine_M") || StuckIn(p, r, n, "Root_M")) ? null : "one hand should not cut a torso in half, but stick in it",
                },
                new Swing
                {
                    Name = "belly, dagger (8 m/s): blade too short", Weapon = Dagger, Speed = 8f,
                    Contact = belly + new Vector3(0, 0, 0.13f), Along = Vector3.UnitX, CutDir = -Vector3.UnitZ, ContactAlongEdge = 0.5f,
                    Expect = (p, r, n) => !p.AnySevered ? null : "a dagger cannot span a torso",
                },
                new Swing
                {
                    Name = "waist, two-handed claymore (9 m/s)", Weapon = Claymore, Hands = 2, Speed = 9f,
                    Contact = belly + new Vector3(0, 0, 0.13f), Along = Vector3.UnitX, CutDir = -Vector3.UnitZ, ContactAlongEdge = 0.5f,
                    Expect = (p, r, n) => SeversSegment(p, r, n, "Root_M") || SeversSegment(p, r, n, "Spine_M") ? null : "should cut him in half",
                },
                new Swing
                {
                    Name = "overhead katana chop on the head (8 m/s)", Weapon = Katana, Speed = 8f, Hands = 2,
                    Contact = head + new Vector3(0, 0.16f, 0.02f), Along = Vector3.UnitZ, CutDir = -Vector3.UnitY, ContactAlongEdge = 0.5f,
                    Expect = (p, r, n) => p.StuckGroup >= 0 || p.AnySevered ? null : "should at least bite into the skull",
                },
            };

            Console.WriteLine();
            Console.WriteLine("== swings (energy model, defaults: arm 2.5 kg/hand, strength 1)");
            int fails = 0;
            foreach (var s in swings)
            {
                // Same construction as Blades.Swing in the mod.
                var along = Vector3.Normalize(s.Along);
                var cut = Vector3.Normalize(s.CutDir - along * Vector3.Dot(s.CutDir, along));
                var flat = Vector3.Normalize(Vector3.Cross(along, cut));
                float uMin = -s.Weapon.EdgeLength * s.ContactAlongEdge, uMax = s.Weapon.EdgeLength * (1f - s.ContactAlongEdge);
                var region = CutRegion.FromBlade(s.Contact, flat, along, cut, uMin, uMax, -0.03f);
                float mass = s.Weapon.Mass + s.Hands * HandKg;
                float energy = 0.5f * mass * s.Speed * s.Speed;

                var solver = new CutSolver(npc.Body, npc.Skins.ToArray(), npc.Pose, region);
                var report = solver.Analyze();
                var plan = CutEnergy.Decide(report, npc.Body, energy, CutMaterial.Flesh);

                string Sections(IEnumerable<int> ids) => string.Join(" + ", ids.Select(g => string.Join("/", report.Groups[g].Segments.Select(x => npc.Body.Segments[x].Name))));
                Console.WriteLine($"   {s.Name}: {energy:F0} J ({s.Weapon.Name}, {s.Hands} hand(s))");
                Console.WriteLine($"      in reach: {string.Join(", ", report.Groups.Where(g => g.InReach).OrderBy(g => g.WMin).Select(g => $"{string.Join("/", g.Segments.Select(x => npc.Body.Segments[x].Name))} {CutEnergy.RequiredEnergy(g, npc.Body, CutMaterial.Flesh):F0} J cover {g.Coverage:P0}"))}");
                Console.WriteLine($"      -> severed [{Sections(plan.Severed)}] gashed [{Sections(plan.Gashed)}]" +
                                  (plan.StuckGroup >= 0 ? $" stuck in {Sections(new[] { plan.StuckGroup })} at {plan.StuckFraction:P0} ({plan.StuckDepth * 100:F1} cm)" : "") +
                                  $", {plan.EnergyLeft:F0} J left");
                string err = s.Expect?.Invoke(plan, report, npc);
                if (err == null) Console.WriteLine("      OK");
                else { Console.WriteLine("      FAIL " + err); fails++; }
            }
            fails += Penetration(npc);
            return fails;
        }

        /// <summary>
        /// A fast blade is already well inside the body when the physics engine reports the hit (15 m/s is 17 cm a
        /// step). The flesh between the skin and that point was cut in the step before: it has to count as swept, or
        /// the cut never "reaches across" the section.
        /// </summary>
        private static int Penetration(NpcAsset npc)
        {
            var o = npc.Pose.SegmentOrigin;
            var belly = Vector3.Lerp(o[Seg(npc, "Root_M")], o[Seg(npc, "Spine_M")], 0.9f);
            var contact = belly + new Vector3(0, 0, 0.13f - 0.12f);   // 12 cm in from the front
            float Coverage(float wMin)
            {
                var region = CutRegion.FromBlade(contact, Vector3.UnitY, Vector3.UnitX, -Vector3.UnitZ, -0.5f, 0.5f, wMin);
                var report = new CutSolver(npc.Body, npc.Skins.ToArray(), npc.Pose, region).Analyze();
                return report.Groups.Where(g => g.Area > 0f && g.Segments.Contains(Seg(npc, "Spine_M")) || g.Segments.Contains(Seg(npc, "Root_M")))
                             .Select(g => g.Coverage).DefaultIfEmpty(0f).Max();
            }
            float before = Coverage(-0.05f), after = Coverage(-(15f / 90f * 1.5f + 0.03f));
            Console.WriteLine($"   belly hit registered 12 cm in: torso counted as swept {before:P0} without the allowance, {after:P0} with it");
            if (after < 0.85f || after <= before) { Console.WriteLine("      FAIL the allowance should let the torso be severed"); return 1; }
            Console.WriteLine("      OK");
            return 0;
        }
    }
}
