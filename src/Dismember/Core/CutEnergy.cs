using System;
using System.Collections.Generic;

namespace Dismember.Core
{
    /// <summary>How hard an NPC's body is to cut. Values are energies in joules.</summary>
    public sealed class CutMaterial
    {
        /// <summary>Energy needed per square metre of soft tissue cross-section.</summary>
        public float FleshToughness = 2200f;
        /// <summary>Scale on the per-bone energies from <see cref="CutEnergy.BoneEnergy"/>.</summary>
        public float BoneScale = 1f;
        /// <summary>Overall multiplier (armour, robots...).</summary>
        public float Multiplier = 1f;

        public static CutMaterial Flesh => new CutMaterial();
        public static CutMaterial Robot => new CutMaterial { FleshToughness = 9000f, BoneScale = 3f, Multiplier = 1f };
        public static CutMaterial Skeleton => new CutMaterial { FleshToughness = 300f, BoneScale = 1.2f, Multiplier = 1f };
    }

    /// <summary>What a swing does to the body, cross-section by cross-section.</summary>
    public sealed class CutPlan
    {
        public readonly List<int> Severed = new List<int>();
        /// <summary>Cross-sections the blade passes through without separating them (blade shorter than the section).</summary>
        public readonly List<int> Gashed = new List<int>();
        public int StuckGroup = -1;
        /// <summary>How deep the blade gets into the stuck group, measured from where it entered (metres).</summary>
        public float StuckDepth;
        public float StuckFraction;
        public float EnergyIn, EnergyLeft, EnergyRequiredTotal;
        public bool AnySevered => Severed.Count > 0;
    }

    public static class CutEnergy
    {
        /// <summary>Extra energy to get through a bone, by body region.</summary>
        public static float BoneEnergy(SegmentKind kind) => kind switch
        {
            SegmentKind.Head => 25f,
            SegmentKind.Hips => 25f,
            SegmentKind.Spine => 15f,
            SegmentKind.Leg => 16f,
            SegmentKind.Arm => 9f,
            SegmentKind.Foot => 5f,
            SegmentKind.Hand => 3f,
            SegmentKind.Tail => 3f,
            _ => 8f,
        };

        public static float RequiredEnergy(SectionGroup g, BodyModel body, CutMaterial m, float coverage = 1f)
        {
            float e = m.FleshToughness * g.Area * coverage;
            foreach (var (seg, _) in g.BoneCrossings)
                e += BoneEnergy(body.Segments[seg].Kind) * m.BoneScale * coverage;
            return e * m.Multiplier;
        }

        /// <summary>
        /// Spends the swing's energy on the cross-sections in the order the blade meets them.
        /// A section is severed only if the blade spans it completely and the energy lasts; the blade
        /// stops (gets stuck) in the first section it cannot finish.
        /// </summary>
        public static CutPlan Decide(CrossSectionReport report, BodyModel body, float energy, CutMaterial material)
        {
            var plan = new CutPlan { EnergyIn = energy };
            var groups = new List<SectionGroup>();
            // Sections with no flesh (stray hair cards, straps) cost nothing and sever nothing: leave them out.
            foreach (var g in report.Groups) if (g.InReach && g.Area > 0f) groups.Add(g);
            groups.Sort((a, b) => a.WMin.CompareTo(b.WMin));

            foreach (var g in groups)
            {
                float coverage = g.FullyCovered ? 1f : Math.Max(0.05f, g.Coverage);
                float cost = RequiredEnergy(g, body, material, coverage);
                plan.EnergyRequiredTotal += cost;
                if (energy >= cost)
                {
                    energy -= cost;
                    if (g.FullyCovered) plan.Severed.Add(g.Id);
                    else plan.Gashed.Add(g.Id);
                    continue;
                }
                plan.StuckGroup = g.Id;
                plan.StuckFraction = cost > 0f ? energy / cost : 0f;
                plan.StuckDepth = plan.StuckFraction * (g.WMax - g.WMin) * coverage;
                energy = 0f;
                break;
            }
            plan.EnergyLeft = energy;
            return plan;
        }
    }
}
