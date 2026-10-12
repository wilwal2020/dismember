using System;
using System.Collections.Generic;
using System.Linq;
using Il2CppSLZ.Marrow;
using UnityEngine;
using Dismember.Core;

namespace Dismember.Game
{
    /// <summary>
    /// Stabs that leave a hole. The game does the stabbing itself (a StabPoint joints the blade's tip into the body);
    /// this watches those joints, remembers where the blade went in and how deep the tip got, and once it is pulled back
    /// out opens a hole there along the blade (<see cref="Wounds.OpenSlit"/>): a slot as wide as the blade, as deep as
    /// it went, so a stab that came out the back is a hole you can see through. Lightsabers never stab in the game
    /// (they pass through bodies), so a thrust with one is followed the same way while the beam is in the body.
    /// </summary>
    internal static class Stabs
    {
        private sealed class Stab
        {
            public NpcBody Npc;
            public int Generation;
            public int Seg;
            public Transform Body;
            public Vector3 Entry, Dir, Width;   // local to Body: where it went in, the way it went, across the blade
            public float HalfWidth, Opening;
            public float Depth;                 // furthest the tip got past the entry point (m)
            public float GameDepth;             // the game's own measure, for the log
            public float LastSeen;
            public IntPtr Weapon;
            public string Name;
            public bool Energy, Seared;
        }

        /// <summary>Steel stabs by (stab point, body collider); lightsaber thrusts by (weapon, NPC).</summary>
        private static readonly Dictionary<(IntPtr, IntPtr), Stab> Active = new Dictionary<(IntPtr, IntPtr), Stab>();

        /// <summary>Shallower than this it is a poke, not a hole.</summary>
        private const float MinDepth = 0.02f;

        public static int Count => Active.Count;

        /// <summary>Every physics step, for each steel weapon: follows the game's stab joints into NPCs.</summary>
        public static void Poll(BladeInfo info)
        {
            if (Slices.IsBusy(info.Weapon)) return;   // the blade is cutting: a slice owns it
            var points = info.Weapon.stabPoints;
            if (points == null) return;
            float now = Time.fixedTime;
            foreach (var sp in points)
            {
                if (sp == null || sp.pointCollider.IsDead() || sp.pointTran.IsDead()) continue;
                // An axe or cleaver "stabs" with its chopping edge: that is a chop, which slices handle.
                if (info.Edges.Any(e => e.Chop != null && e.Chop.Pointer == sp.Pointer)) continue;
                var joints = sp.stabJoints;
                if (joints == null) continue;
                for (int i = 0; i < joints.Length; i++)
                {
                    var sj = joints[i];
                    if (sj == null || sj.joint.IsDead() || sj.collider.IsDead()) continue;
                    var rb = sj.collider.attachedRigidbody;
                    if (rb == null) continue;
                    var npc = NpcRegistry.Find(rb, out int seg);
                    if (npc == null) continue;
                    var key = (sp.Pointer, sj.collider.Pointer);
                    if (!Active.TryGetValue(key, out var st))
                    {
                        st = BeginSteel(info, sp, sj.collider, npc, seg);
                        if (st == null) continue;
                        Active[key] = st;
                    }
                    st.LastSeen = now;
                    st.GameDepth = Mathf.Max(st.GameDepth, sj.deepestDepth);
                    Deepen(st, SteelTip(sp, st));
                }
            }
        }

        private static Stab BeginSteel(BladeInfo info, StabSlash.StabPoint sp, Collider bodyCol, NpcBody npc, int seg)
        {
            var body = npc.Muscles[seg].Body;
            if (body.IsDead()) return null;
            // The way the blade goes in: along a sword's blade towards its tip, else out of the stab point.
            var dir = sp.pointTran.forward;
            var slash = info.Edges.Where(e => e.Blade != null).ToList();
            if (slash.Count > 0)
            {
                var e = slash[0];
                var along = info.Root.TransformPoint(e.LocalB) - info.Root.TransformPoint(e.LocalA);
                if (!e.TipIsB) along = -along;
                if (along.sqrMagnitude > 1e-6f && Vector3.Dot(along, dir) > -0.2f) dir = along.normalized;
            }
            // Across the blade: the way its edge faces (a stab with a sword opens a slot as wide as the blade).
            var width = slash.Count > 0 ? Blades.CutDirection(info, slash[0], Vector3.zero) : sp.pointTran.up;
            width -= dir * Vector3.Dot(width, dir);
            if (width.sqrMagnitude < 1e-4f) width = Vector3.Cross(dir, Mathf.Abs(dir.y) < 0.9f ? Vector3.up : Vector3.right);
            width.Normalize();
            float half = 0.015f;
            if (slash.Count >= 2)
            {
                // A double-edged blade: as wide as the gap between its edges.
                var a0 = info.Root.TransformPoint((slash[0].LocalA + slash[0].LocalB) * 0.5f);
                var a1 = info.Root.TransformPoint((slash[1].LocalA + slash[1].LocalB) * 0.5f);
                half = Mathf.Abs(Vector3.Dot(a1 - a0, width)) * 0.5f;
            }
            half = Mathf.Clamp(half, 0.008f, 0.035f);
            var tip = SteelTip(sp, dir);
            var entry = EntryPoint(bodyCol, tip, dir);
            return new Stab
            {
                Npc = npc,
                Generation = npc.Generation,
                Seg = seg,
                Body = body,
                Entry = body.InverseTransformPoint(entry),
                Dir = body.InverseTransformDirection(dir),
                Width = body.InverseTransformDirection(width),
                HalfWidth = half,
                Opening = 0.005f,
                Weapon = info.Weapon.Pointer,
                Name = info.Weapon.name,
            };
        }

        /// <summary>The furthest point of the stab point's collider along the way it goes in (its tip).</summary>
        private static Vector3 SteelTip(StabSlash.StabPoint sp, Vector3 dir)
        {
            try { return sp.pointCollider.ClosestPoint(sp.pointCollider.bounds.center + dir * 2f); }
            catch { return sp.pointTran.position; }
        }

        private static Vector3 SteelTip(StabSlash.StabPoint sp, Stab st) => SteelTip(sp, st.Body.TransformDirection(st.Dir));

        /// <summary>Where a blade going <paramref name="dir"/> to <paramref name="tip"/> entered a body collider.</summary>
        private static Vector3 EntryPoint(Collider bodyCol, Vector3 tip, Vector3 dir)
        {
            try
            {
                var from = tip - dir * 0.6f;
                if (bodyCol.Raycast(new Ray(from, dir), out var hit, 0.65f)) return hit.point;
            }
            catch { /* mesh colliders can refuse; fall back below */ }
            return tip - dir * MinDepth;
        }

        private static void Deepen(Stab st, Vector3 tip)
        {
            if (st.Body.IsDead()) return;
            var entry = st.Body.TransformPoint(st.Entry);
            var dir = st.Body.TransformDirection(st.Dir);
            st.Depth = Mathf.Max(st.Depth, Vector3.Dot(tip - entry, dir));
        }

        /// <summary>
        /// A lightsaber beam overlapping an NPC this physics step (<paramref name="a"/>-<paramref name="b"/>, tip at
        /// <paramref name="b"/>). Returns true when it is a thrust (in along the beam) that this follows, so no slice
        /// should start from it.
        /// </summary>
        public static bool Energy(BladeInfo info, Edge blade, NpcBody npc, int seg, Collider col, Vector3 a, Vector3 b)
        {
            var key = (info.Weapon.Pointer, npc.Key);
            var dir = (b - a).normalized;
            Active.TryGetValue(key, out var st);
            var v = info.PointVelocity(b);
            float vIn = Vector3.Dot(v, dir);
            float vSide = (v - dir * vIn).magnitude;
            if (st == null)
            {
                // Only a push in along the beam starts one; a sideways sweep is a slice.
                if (vIn < 0.3f || vSide > vIn) return false;
                var body = npc.Muscles[seg].Body;
                if (body.IsDead()) return false;
                var width = Vector3.Cross(dir, Mathf.Abs(Vector3.Dot(dir, Vector3.up)) < 0.9f ? Vector3.up : Vector3.right).normalized;
                var entry = EntryPoint(col, b, dir);
                st = new Stab
                {
                    Npc = npc,
                    Generation = npc.Generation,
                    Seg = seg,
                    Body = body,
                    Entry = body.InverseTransformPoint(entry),
                    Dir = body.InverseTransformDirection(dir),
                    Width = body.InverseTransformDirection(width),
                    HalfWidth = Mathf.Clamp(blade.Radius * 1.3f, 0.01f, 0.03f),
                    Opening = Mathf.Clamp(blade.Radius, 0.006f, 0.014f),
                    Weapon = info.Weapon.Pointer,
                    Name = info.Weapon.name,
                    Energy = true,
                    Seared = Settings.SaberSears.Value,
                };
                Active[key] = st;
                Gore.Embers(entry, -dir, 0.5f);
                Sounds.Play(Sounds.Kind.Sizzle, entry, 0.8f);
            }
            else if (vSide > 0.6f && vSide > Mathf.Abs(vIn) * 1.5f)
            {
                // Swept sideways while in: from here on it is a cut, and the slice takes over.
                Active.Remove(key);
                return false;
            }
            st.LastSeen = Time.fixedTime;
            Deepen(st, b);
            info.Haptic(0.3f, 0.03f);
            return true;
        }

        /// <summary>A slice is taking over from the game's stab joints into this NPC: those stabs leave no hole of their own.</summary>
        public static void Forget(BladeInfo info, NpcBody npc)
        {
            foreach (var kv in Active.Where(kv => kv.Value.Weapon == info.Weapon.Pointer && kv.Value.Npc == npc).ToList())
                Active.Remove(kv.Key);
        }

        /// <summary>Every physics step: stabs whose blade has come back out leave their hole.</summary>
        public static void FixedUpdate()
        {
            if (Active.Count == 0) return;
            float now = Time.fixedTime;
            foreach (var kv in Active.ToList())
            {
                var st = kv.Value;
                if (now - st.LastSeen <= Time.fixedDeltaTime * 2.5f) continue;
                Active.Remove(kv.Key);
                try { Finish(st); }
                catch (Exception e) { Log.Debug("stab: " + e.Message); }
            }
        }

        private static void Finish(Stab st)
        {
            var npc = st.Npc;
            if (npc == null || !npc.Valid || npc.Generation != st.Generation || st.Body.IsDead()) return;
            if (st.Depth < MinDepth) return;
            var entry = st.Body.TransformPoint(st.Entry);
            var dir = st.Body.TransformDirection(st.Dir).normalized;
            var width = st.Body.TransformDirection(st.Width).normalized;
            var flat = Vector3.Cross(width, dir).normalized;
            var region = CutRegion.FromBlade(entry.N(), flat.N(), width.N(), dir.N(), -st.HalfWidth, st.HalfWidth, -0.04f);
            var spec = new SlitSpec
            {
                Depth = st.Depth + 0.005f,
                Opening = st.Opening,
                Taper = Mathf.Clamp(st.HalfWidth, 0.008f, 0.02f),
            };
            Log.Debug($"{st.Name} stab into {npc.Name}/{npc.Muscles[st.Seg].Name}: {st.Depth * 100f:F0} cm deep" +
                      (st.Energy ? " (energy blade)" : $" (game says {st.GameDepth:F2})"));
            Wounds.OpenSlit(npc, st.Seg, region, spec, null, st.Seared, null, $"{st.Name} stab, {st.Depth * 100f:F0} cm");
            if (!st.Seared)
            {
                Gore.Bleeder(st.Body, entry, -dir, 6f, 18f, npc.Palette);
                Gore.Burst(entry, -dir, 0.5f, npc.Palette);
            }
        }

        public static void Clear() => Active.Clear();
    }
}
