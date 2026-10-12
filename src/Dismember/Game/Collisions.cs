using System;
using System.Collections.Generic;
using UnityEngine;

namespace Dismember.Game
{
    /// <summary>
    /// Temporary collision ignores between a blade and the flesh it is passing through. Several systems may want the
    /// same pair ignored (a follow-through and then a stuck blade), so pairs are reference counted, and collision is
    /// only switched back on once the two colliders have separated (or a deadline passes), so PhysX does not shove
    /// a blade out of a body.
    /// </summary>
    internal static class Collisions
    {
        private sealed class Entry
        {
            public Collider A, B;
            public int Holds;
            public float NotBefore, Deadline;
        }

        private static readonly Dictionary<(IntPtr, IntPtr), Entry> Pairs = new Dictionary<(IntPtr, IntPtr), Entry>();
        private static readonly List<(IntPtr, IntPtr)> Done = new List<(IntPtr, IntPtr)>();

        private static (IntPtr, IntPtr) Key(Collider a, Collider b)
        {
            IntPtr x = a.Pointer, y = b.Pointer;
            return x.ToInt64() < y.ToInt64() ? (x, y) : (y, x);
        }

        /// <summary>Ignores every a/b pair now and keeps them ignored until released.</summary>
        public static List<(IntPtr, IntPtr)> Hold(List<Collider> a, List<Collider> b)
        {
            var keys = new List<(IntPtr, IntPtr)>(a.Count * b.Count);
            foreach (var ca in a)
            {
                if (ca.IsDead()) continue;
                foreach (var cb in b)
                {
                    if (cb.IsDead()) continue;
                    var k = Key(ca, cb);
                    if (!Pairs.TryGetValue(k, out var e))
                    {
                        // Already ignored by the game (grips, its own slash joints): not ours to switch back on.
                        if (Physics.GetIgnoreCollision(ca, cb)) continue;
                        e = new Entry { A = ca, B = cb };
                        Pairs[k] = e;
                        Physics.IgnoreCollision(ca, cb, true);
                    }
                    e.Holds++;
                    keys.Add(k);
                }
            }
            return keys;
        }

        /// <summary>Ignores the pairs for at least <paramref name="minSeconds"/>, re-enabling them once clear (at most <paramref name="maxSeconds"/>).</summary>
        public static void Hold(List<Collider> a, List<Collider> b, float minSeconds, float maxSeconds) =>
            Release(Hold(a, b), minSeconds, maxSeconds);

        public static void Release(List<(IntPtr, IntPtr)> keys, float minSeconds, float maxSeconds)
        {
            if (keys == null) return;
            float now = Time.time;
            foreach (var k in keys)
            {
                if (!Pairs.TryGetValue(k, out var e) || e.Holds <= 0) continue;
                e.Holds--;
                e.NotBefore = Mathf.Max(e.NotBefore, now + minSeconds);
                e.Deadline = Mathf.Max(e.Deadline, now + maxSeconds);
            }
        }

        public static void FixedUpdate()
        {
            if (Pairs.Count == 0) return;
            float now = Time.time;
            Done.Clear();
            foreach (var kv in Pairs)
            {
                var e = kv.Value;
                if (e.A.IsDead() || e.B.IsDead()) { Done.Add(kv.Key); continue; }
                if (e.Holds > 0 || now < e.NotBefore) continue;
                if (Overlapping(e.A, e.B) && now < e.Deadline) continue;
                Physics.IgnoreCollision(e.A, e.B, false);
                Done.Add(kv.Key);
            }
            foreach (var k in Done) Pairs.Remove(k);
        }

        /// <summary>True while two colliders interpenetrate (switching collision back on then would launch them apart).</summary>
        public static bool Overlapping(Collider a, Collider b)
        {
            try
            {
                if (!a.enabled || !b.enabled || !a.gameObject.activeInHierarchy || !b.gameObject.activeInHierarchy) return false;
                if (!a.bounds.Intersects(b.bounds)) return false;
                var ta = a.transform;
                var tb = b.transform;
                return Physics.ComputePenetration(a, ta.position, ta.rotation, b, tb.position, tb.rotation, out _, out float d) && d > 0.002f;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Re-enables everything right away (mod disabled).</summary>
        public static void ReleaseAll()
        {
            foreach (var e in Pairs.Values)
                if (!e.A.IsDead() && !e.B.IsDead()) Physics.IgnoreCollision(e.A, e.B, false);
            Pairs.Clear();
        }

        public static void Clear() => Pairs.Clear();
    }
}
