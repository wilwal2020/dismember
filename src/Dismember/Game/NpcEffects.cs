using System;
using System.Collections.Generic;
using Il2CppSLZ.Marrow;
using Il2CppSLZ.Marrow.Combat;
using Il2CppSLZ.Marrow.Data;
using UnityEngine;
using Dismember.Core;

namespace Dismember.Game
{
    /// <summary>What losing body parts does to the NPC: death, crippled limbs, bleeding out.</summary>
    internal static class NpcEffects
    {
        // SubBehaviourHealth.StunGroup values.
        private const int Spine = 0, ArmLf = 1, ArmRt = 2, LegLf = 3, LegRt = 4, Head = 5;

        public static void OnCut(NpcBody npc, CutResult res, bool[] wasCore, CutRegion region, bool cauterized = false)
        {
            bool lostHead = false, lostTorso = false;
            var lost = new List<int>();
            for (int s = 0; s < npc.Muscles.Length; s++)
            {
                if (!wasCore[s] || npc.InCore(s)) continue;
                lost.Add(s);
                var m = npc.Muscles[s];
                if (m.Kind == SegmentKind.Head || m.StunGroup == Head) lostHead = true;
                if (m.Kind == SegmentKind.Spine) lostTorso = true;
            }

            // Cutting through the neck flesh without separating the head still kills.
            bool cutNeck = false;
            for (int s = 0; s < npc.Muscles.Length; s++)
            {
                if (!res.SegmentCut[s]) continue;
                foreach (int c in ChildrenOf(npc, s))
                    if (npc.Muscles[c].Kind == SegmentKind.Head && !npc.InCore(c)) cutNeck = true;
            }

            // A slice through the skull (top of the head off, face split) kills as surely as losing the head.
            bool cutSkull = false;
            for (int s = 0; s < npc.Muscles.Length; s++)
            {
                if (!res.SegmentCut[s] || npc.Muscles[s].Kind != SegmentKind.Head) continue;
                int parent = npc.OriginalParents[s];
                if (parent < 0 || npc.Muscles[parent].Kind != SegmentKind.Head) cutSkull = true;   // the cranium, not the jaw
            }

            if ((lostHead || lostTorso || cutNeck || cutSkull) && !npc.LethalDone)
            {
                npc.LethalDone = true;
                Kill(npc, region.Origin.U(), region.Normal.U());
                return;
            }

            var h = npc.Health;
            if (h != null)
            {
                try
                {
                    foreach (int s in lost)
                    {
                        switch (npc.Muscles[s].StunGroup)
                        {
                            case ArmLf: h.cur_arm_lf = 0f; break;
                            case ArmRt: h.cur_arm_rt = 0f; break;
                            case LegLf: h.cur_leg_lf = 0f; break;
                            case LegRt: h.cur_leg_rt = 0f; break;
                        }
                    }
                }
                catch (Exception e) { Log.Debug("limb hp: " + e.Message); }
            }

            // A lightsaber burns the stumps shut: no bleeding out.
            if (Settings.BleedDamage.Value && lost.Count > 0 && !cauterized)
            {
                npc.BleedUntil = Time.time + 25f;
                npc.BleedPerSecond += 0.6f + 0.25f * lost.Count;
            }
        }

        private static IEnumerable<int> ChildrenOf(NpcBody npc, int s)
        {
            for (int i = 0; i < npc.Muscles.Length; i++)
                if (npc.OriginalParents[i] == s) yield return i;
        }

        public static void Kill(NpcBody npc, Vector3 at, Vector3 dir)
        {
            // Lethal damage through the game's own damage path (sounds, score, death events, etc.)...
            try
            {
                var ip = FindImpactProperties(npc);
                if (ip != null)
                {
                    var attack = new Attack
                    {
                        damage = 10000f,
                        attackType = AttackType.Slicing,
                        origin = at,
                        direction = dir,
                        normal = -dir,
                        collider = ip.GetComponent<Collider>(),
                    };
                    ip.ReceiveAttack(attack);
                }
            }
            catch (Exception e) { Log.Debug("attack kill failed: " + e.Message); }

            // ...but the attack is handled asynchronously (the NPC kept going for a moment), so kill it now too,
            // and once more shortly after in case something revived it.
            void Finish()
            {
                try
                {
                    if (npc.Brain.IsDead() || npc.Brain.isDead) return;
                    npc.Health?.Kill();
                    if (!npc.Brain.isDead) npc.Brain.behaviour?.PuppetMasterKill();
                }
                catch (Exception e) { Log.Debug("kill failed: " + e.Message); }
            }
            Finish();
            Cleanup.After(0.25f, Finish);
        }

        private static ImpactProperties FindImpactProperties(NpcBody npc)
        {
            foreach (var m in npc.Muscles)
            {
                if (!npc.InCore(m.Index)) continue;
                foreach (var c in m.Colliders)
                {
                    if (c.IsDead()) continue;
                    var ip = c.GetComponent<ImpactProperties>();
                    if (ip != null) return ip;
                }
            }
            return null;
        }

        /// <summary>Bleeding out after losing limbs. Called from Update.</summary>
        public static void Bleed(NpcBody npc, float dt)
        {
            if (npc.BleedUntil <= Time.time || npc.Health == null) return;
            try
            {
                if (npc.Brain.IsDead() || npc.Brain.isDead) return;
                var h = npc.Health;
                float dmg = npc.BleedPerSecond * dt;
                h.cur_hp -= dmg;
                if (h.cur_hp <= 0f && !npc.LethalDone)
                {
                    npc.LethalDone = true;
                    Kill(npc, npc.Muscles[npc.Root0].Body.position, Vector3.down);
                }
            }
            catch (Exception e) { Log.Debug("bleed: " + e.Message); }
        }
    }
}
