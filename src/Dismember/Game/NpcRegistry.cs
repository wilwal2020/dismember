using System;
using System.Collections.Generic;
using Il2CppSLZ.Marrow.AI;
using Il2CppSLZ.Marrow.Pool;
using UnityEngine;

namespace Dismember.Game
{
    /// <summary>Finds the NPC (and ragdoll segment) behind a rigidbody, and keeps pooled NPCs intact between lives.</summary>
    internal static class NpcRegistry
    {
        private static readonly Dictionary<IntPtr, NpcBody> ByBrain = new Dictionary<IntPtr, NpcBody>();
        // Rigidbody -> NPC; null marks a rigidbody already checked that does not belong to an NPC.
        private static readonly Dictionary<IntPtr, NpcBody> ByRigidbody = new Dictionary<IntPtr, NpcBody>();
        private static readonly List<NpcBody> List = new List<NpcBody>();
        private static float _nextNegativeFlush;

        public static IReadOnlyList<NpcBody> All => List;

        /// <summary>The NPC owning <paramref name="rb"/>, or null. <paramref name="seg"/> is its ragdoll segment.</summary>
        public static NpcBody Find(Rigidbody rb, out int seg)
        {
            seg = -1;
            if (rb.IsDead()) return null;
            var ptr = rb.Pointer;
            if (ByRigidbody.TryGetValue(ptr, out var npc))
            {
                if (npc == null) return null;
                if (npc.Valid && npc.MuscleByRigidbody.TryGetValue(ptr, out seg)) return npc;
                ByRigidbody.Remove(ptr);
            }

            AIBrain brain = null;
            try
            {
                brain = rb.GetComponentInParent<AIBrain>();
                if (brain == null)
                {
                    var poolee = rb.GetComponentInParent<Poolee>();
                    if (poolee != null) brain = poolee.GetComponentInChildren<AIBrain>(true);
                }
            }
            catch (Exception e) { Log.Debug("brain lookup: " + e.Message); }

            if (brain == null)
            {
                ByRigidbody[ptr] = null;
                return null;
            }
            npc = Get(brain, true);
            if (npc == null)
            {
                ByRigidbody[ptr] = null;
                return null;
            }
            if (!npc.MuscleByRigidbody.ContainsKey(ptr)) npc.Refresh();
            foreach (var kv in npc.MuscleByRigidbody) ByRigidbody[kv.Key] = npc;
            if (npc.MuscleByRigidbody.TryGetValue(ptr, out seg)) return npc;
            ByRigidbody[ptr] = null;   // part of the NPC but not a muscle (held prop, trigger...)
            return null;
        }

        public static NpcBody Get(AIBrain brain, bool create)
        {
            if (brain.IsDead()) return null;
            if (ByBrain.TryGetValue(brain.Pointer, out var npc))
            {
                if (npc.Valid) return npc;
                Forget(npc);
            }
            if (!create) return null;
            try
            {
                npc = NpcBody.Create(brain);
            }
            catch (Exception e)
            {
                Log.Warn($"Could not read NPC {brain.name}: {e.Message}");
                return null;
            }
            if (npc == null) return null;
            ByBrain[brain.Pointer] = npc;
            List.Add(npc);
            foreach (var kv in npc.MuscleByRigidbody) ByRigidbody[kv.Key] = npc;
            Log.Debug($"Tracking {npc.Name}: {npc.Muscles.Length} segments, {npc.Renderers.Count} renderers");
            npc.EnsureMeshes();   // start decoding its meshes in the background
            return npc;
        }

        private static void Forget(NpcBody npc)
        {
            ByBrain.Remove(npc.Key);
            List.Remove(npc);
            var dead = new List<IntPtr>();
            foreach (var kv in ByRigidbody) if (kv.Value == npc) dead.Add(kv.Key);
            foreach (var k in dead) ByRigidbody.Remove(k);
        }

        /// <summary>Pool spawn / resurrection: put the NPC back together and warm up its mesh data.</summary>
        public static void OnSpawn(AIBrain brain)
        {
            if (brain.IsDead()) return;
            if (ByBrain.TryGetValue(brain.Pointer, out var npc))
            {
                Slices.ReleaseAll(npc);
                if (npc.Modified)
                {
                    npc.Restore();
                    Log.Debug($"Restored {npc.Name} on spawn");
                }
            }
            else if (Settings.Enabled.Value)
            {
                // The NPC itself is read on its first hit (its ragdoll may not be set up yet); its meshes can start
                // decoding now so that first hit already cuts.
                Prefetch(brain);
            }
        }

        private static readonly System.Text.RegularExpressions.Regex LowerLod =
            new System.Text.RegularExpressions.Regex(@"LOD[1-9]", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        private static void Prefetch(AIBrain brain)
        {
            var poolee = brain.GetComponentInParent<Poolee>();
            var root = poolee != null ? poolee.gameObject : brain.gameObject;
            foreach (var smr in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                var mesh = smr.sharedMesh;
                if (mesh == null || LowerLod.IsMatch(mesh.name) || LowerLod.IsMatch(smr.name)) continue;
                MeshLibrary.TryGet(mesh, out _);
            }
        }

        /// <summary>Drops NPCs that no longer exist (level changes) and stale "not an NPC" answers.</summary>
        public static void Sweep()
        {
            foreach (var npc in List.ToArray())
                if (!npc.Valid) Forget(npc);
            var neg = new List<IntPtr>();
            foreach (var kv in ByRigidbody) if (kv.Value == null) neg.Add(kv.Key);
            foreach (var k in neg) ByRigidbody.Remove(k);
        }

        /// <summary>Despawn: restore before the NPC goes back into the pool.</summary>
        public static void OnDespawn(AIBrain brain)
        {
            if (brain.IsDead() || !ByBrain.TryGetValue(brain.Pointer, out var npc)) return;
            Slices.ReleaseAll(npc);
            if (npc.Modified)
            {
                npc.Restore();
                Log.Debug($"Restored {npc.Name} on despawn");
            }
        }

        public static void FixedUpdate()
        {
            for (int i = List.Count - 1; i >= 0; i--)
            {
                var npc = List[i];
                if (!npc.Modified) continue;
                if (!npc.Valid) { Forget(npc); continue; }
                try { npc.Enforce(); }
                catch (Exception e) { Log.Debug("enforce: " + e.Message); }
            }
        }

        public static void Update(float dt)
        {
            for (int i = 0; i < List.Count; i++)
            {
                var npc = List[i];
                if (npc.BleedUntil > Time.time) NpcEffects.Bleed(npc, dt);
            }
            if (Time.time > _nextNegativeFlush)
            {
                // Pointers of destroyed objects get reused; do not trust "not an NPC" answers forever.
                _nextNegativeFlush = Time.time + 20f;
                var neg = new List<IntPtr>();
                foreach (var kv in ByRigidbody) if (kv.Value == null) neg.Add(kv.Key);
                foreach (var k in neg) ByRigidbody.Remove(k);
            }
        }

        public static void RestoreAll()
        {
            foreach (var npc in List)
            {
                Slices.ReleaseAll(npc);
                if (npc.Modified && npc.Valid) npc.Restore();
            }
        }

        public static void Clear()
        {
            ByBrain.Clear();
            ByRigidbody.Clear();
            List.Clear();
        }
    }
}
