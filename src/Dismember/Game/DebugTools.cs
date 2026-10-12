using System;
using System.Collections.Generic;
using System.Linq;
using Il2CppSLZ.Marrow.AI;
using UnityEngine;
using Dismember.Core;
using Random = System.Random;

namespace Dismember.Game
{
    /// <summary>BoneMenu helpers for trying the mod without a sword fight.</summary>
    internal static class DebugTools
    {
        public enum Preset { Neck, Arm, Leg, TorsoDiagonal, SkullTop }

        private static readonly Random Rng = new Random();

        private static Vector3 PlayerPosition()
        {
            try
            {
                var head = BoneLib.Player.Head;
                if (head != null) return head.position;
            }
            catch { }
            var cam = Camera.main;
            return cam != null ? cam.transform.position : Vector3.zero;
        }

        private static NpcBody Nearest()
        {
            var from = PlayerPosition();
            AIBrain best = null;
            float bestD = 40f;
            foreach (var brain in UnityEngine.Object.FindObjectsOfType<AIBrain>())
            {
                if (brain.IsDead() || !brain.gameObject.activeInHierarchy) continue;
                var pm = brain.puppetMaster;
                if (pm.IsDead() || pm.muscles == null || pm.muscles.Length == 0) continue;
                var t = pm.muscles[0].transform;
                if (t.IsDead()) continue;
                float d = Vector3.Distance(from, t.position);
                if (d < bestD) { bestD = d; best = brain; }
            }
            if (best == null)
            {
                Log.Msg("No NPC within 40 m.");
                return null;
            }
            return NpcRegistry.Get(best, true);
        }

        private static int Find(NpcBody npc, Func<MuscleInfo, bool> pick) => npc.Muscles.FirstOrDefault(pick)?.Index ?? -1;

        private static int FirstChild(NpcBody npc, int seg, SegmentKind kind) =>
            Array.FindIndex(npc.Muscles, m => npc.OriginalParents[m.Index] == seg && m.Kind == kind);

        public static void SliceNearest(Preset preset)
        {
            try
            {
                var npc = Nearest();
                if (npc == null) return;
                if (npc.Busy) { Log.Msg($"{npc.Name} is still being cut."); return; }
                if (!npc.EnsureMeshes()) { Log.Msg($"{npc.Name}: mesh data still loading ({MeshLibrary.Status}); try again in a moment."); return; }

                int root = npc.Root0;
                CutRegion region = null;
                HashSet<int> only = null;
                int hitSeg = root;

                // Bone axes differ between rigs, so build directions from body positions and where the player is.
                int head = Find(npc, m => m.Kind == SegmentKind.Head && (npc.OriginalParents[m.Index] < 0 || npc.Muscles[npc.OriginalParents[m.Index]].Kind != SegmentKind.Head));
                var hips = npc.Muscles[root].Body.position;
                var up = head >= 0 ? (npc.Muscles[head].Body.position - hips).normalized : Vector3.up;
                var toPlayer = PlayerPosition() - hips;
                toPlayer -= up * Vector3.Dot(toPlayer, up);
                toPlayer = toPlayer.sqrMagnitude > 1e-4f ? toPlayer.normalized : Vector3.Cross(up, Vector3.right).normalized;
                var side = Vector3.Cross(up, toPlayer).normalized;
                switch (preset)
                {
                    case Preset.Neck:
                    {
                        if (head < 0) { Log.Msg("This NPC has no head segment."); return; }
                        int parent = npc.OriginalParents[head];
                        var h = npc.Muscles[head].Body.position;
                        var b = parent >= 0 ? npc.Muscles[parent].Body.position : h - up * 0.3f;
                        var dir = (h - b).normalized;
                        region = CutRegion.Infinite(Vector3.Lerp(b, h, 0.75f).N(), (dir + toPlayer * 0.25f).normalized.N());
                        only = new HashSet<int> { head };
                        if (parent >= 0) only.Add(parent);
                        hitSeg = head;
                        break;
                    }
                    case Preset.Arm:
                    case Preset.Leg:
                    {
                        var kind = preset == Preset.Arm ? SegmentKind.Arm : SegmentKind.Leg;
                        var upper = npc.Muscles.Where(m => m.Kind == kind && npc.InCore(m.Index) &&
                                                           (npc.OriginalParents[m.Index] < 0 || npc.Muscles[npc.OriginalParents[m.Index]].Kind != kind)).ToList();
                        if (upper.Count == 0) { Log.Msg($"No attached {kind.ToString().ToLowerInvariant()} left."); return; }
                        var limb = upper[Rng.Next(upper.Count)];
                        int child = FirstChild(npc, limb.Index, kind);
                        var a = limb.Body.position;
                        var c = child >= 0 ? npc.Muscles[child].Body.position : a - up * 0.3f;
                        var dir = (c - a).normalized;
                        region = CutRegion.Infinite(Vector3.Lerp(a, c, 0.5f).N(), (dir + toPlayer * 0.3f).normalized.N());
                        only = new HashSet<int> { limb.Index };
                        hitSeg = limb.Index;
                        break;
                    }
                    case Preset.TorsoDiagonal:
                    {
                        int spine = Find(npc, m => m.Kind == SegmentKind.Spine && npc.OriginalParents[m.Index] == root);
                        int chest = spine >= 0 ? FirstChild(npc, spine, SegmentKind.Spine) : -1;
                        if (spine < 0) { Log.Msg("This NPC has no spine segment."); return; }
                        var a = npc.Muscles[spine].Body.position;
                        var c = chest >= 0 ? npc.Muscles[chest].Body.position : a + up * 0.2f;
                        region = CutRegion.Infinite(Vector3.Lerp(a, c, 0.5f).N(), (up + side * 0.8f).normalized.N());
                        hitSeg = spine;
                        break;
                    }
                    case Preset.SkullTop:
                    {
                        if (head < 0) { Log.Msg("This NPC has no head segment."); return; }
                        var ht = npc.Muscles[head].Body;
                        int parent = npc.OriginalParents[head];
                        var headUp = parent >= 0 ? (ht.position - npc.Muscles[parent].Body.position).normalized : up;
                        region = CutRegion.Infinite((ht.position + headUp * 0.13f).N(), (headUp + toPlayer * 0.3f).normalized.N());
                        only = new HashSet<int> { head };
                        hitSeg = head;
                        break;
                    }
                }

                if (!npc.InCore(hitSeg)) hitSeg = root;
                var attempt = CutPipeline.Prepare(npc, hitSeg, region);
                if (attempt == null) { Log.Msg($"{npc.Name} cannot be cut right now."); return; }
                var sever = attempt.Report.Groups
                    .Where(g => g.InReach && g.Area > 0f && g.FullyCovered && (only == null || g.Segments.Overlaps(only)))
                    .Select(g => g.Id).ToList();
                if (sever.Count == 0)
                {
                    // Say why, so a log from a test session shows what the cut saw.
                    var r = attempt.Report;
                    string Seg(int s) => npc.Muscles[s].Name;
                    Log.Msg($"Nothing to cut for {preset} on {npc.Name}: {r.Loops.Count} outline(s), {r.Groups.Count} section(s) " +
                            $"[{string.Join("; ", r.Groups.Select(g => $"{string.Join("/", g.Segments.Select(Seg))} {g.Area * 1e4f:F0} cm2"))}]; " +
                            $"wanted [{(only == null ? "any" : string.Join("/", only.Select(Seg)))}]; renderers: " +
                            string.Join(", ", attempt.Solved.Select(x => $"{x.Renderer.name} caps={(x.AllowCaps ? 1 : 0)}")));
                    return;
                }
                CutPipeline.Submit(attempt, sever, Vector3.zero, "menu: " + preset);
                Log.Msg($"Slicing {npc.Name}: {preset} ({sever.Count} section(s))");
            }
            catch (Exception e)
            {
                Log.Error("Debug slice failed: " + e);
            }
        }

        public static void RestoreNearest()
        {
            try
            {
                var npc = Nearest();
                if (npc == null) return;
                Slices.ReleaseAll(npc);
                npc.Restore();
                Log.Msg($"Restored {npc.Name}.");
            }
            catch (Exception e)
            {
                Log.Error("Restore failed: " + e);
            }
        }

        public static void PrintStatus()
        {
            var npcs = NpcRegistry.All;
            Log.Msg($"Dismember: enabled={Settings.Enabled.Value}; mesh index: {MeshLibrary.Status}; " +
                    $"NPCs tracked {npcs.Count}, cut {npcs.Count(n => n.Modified)}; blades seen {Blades.Count}; slicing {Slices.Count} (stuck {Slices.StuckCount}); open wounds {Wounds.Count} ({Wounds.SlitCount} opened in the mesh); stabs in {Stabs.Count}; blood spots {Splats.Count}; " +
                    $"cuts queued {CutPipeline.Pending}; last cut: {CutPipeline.LastTimings}");
            foreach (var n in npcs)
            {
                int ready = n.Renderers.Count(r => r.Data != null), missing = n.Renderers.Count(r => r.Unavailable);
                Log.Msg($"  {n.Name}: cuts {n.Cuts}, renderers {n.Renderers.Count} (ready {ready}, unavailable {missing}), palette {n.Palette}");
            }
        }
    }
}
