using System;
using System.Collections.Generic;
using System.Linq;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppSLZ.Marrow;
using UnityEngine;
using Dismember.Core;

namespace Dismember.Game
{
    /// <summary>
    /// One cutting edge of a weapon: a StabSlash SlashBlade (swords, knives), or a long thin StabPoint, which is how
    /// BONELAB sets up chopping edges (axes, the cleaver).
    /// </summary>
    internal sealed class Edge
    {
        public StabSlash.SlashBlade Blade;
        public StabSlash.StabPoint Chop;
        public Collider Collider;
        public Transform Tran;
        /// <summary>Edge line in weapon-local space.</summary>
        public Vector3 LocalA, LocalB;
        public float Radius;
        /// <summary>B is the end away from the grip (the tip).</summary>
        public bool TipIsB = true;
        /// <summary>For chopping edges: the way the sharp side faces, weapon-local (zero: work it out from bladeTran).</summary>
        public Vector3 LocalCutDir;
        /// <summary>No extra reach (an energy blade cuts exactly as far as its beam goes).</summary>
        public bool NoReach;

        /// <summary>
        /// The line that cuts, in world space: the edge plus the "Extra blade reach" past its tip (a little more for
        /// longer blades), so hitting with the end of the blade can still get through an arm or a torso.
        /// </summary>
        public void CutLine(Transform root, out Vector3 a, out Vector3 b)
        {
            a = root.TransformPoint(LocalA);
            b = root.TransformPoint(LocalB);
            if (NoReach) return;
            float reach = Mathf.Max(0f, Settings.BladeReachCm.Value) / 100f;
            if (reach <= 0f) return;
            var d = b - a;
            float len = d.magnitude;
            if (len < 1e-4f) return;
            float extra = reach * (1f + len);   // 8 cm: a dagger gets ~10 cm more, a long sword ~15 cm
            if (TipIsB) b += d / len * extra;
            else a -= d / len * extra;
        }
    }

    /// <summary>A weapon with at least one cutting edge, plus its motion before the current physics step.</summary>
    internal sealed class BladeInfo
    {
        public StabSlash Weapon;
        public Rigidbody Rb;
        public Transform Root;
        public Edge[] Edges;
        /// <summary>Every solid collider that makes up the blade (edges, flat, tip): ignored against flesh it cuts.</summary>
        public List<Collider> BladeColliders;
        public Vector3 V, W, Com;
        public bool HasState;
        /// <summary>A lightsaber: trigger blade, cuts anything at any speed, no drag, never sticks.</summary>
        public bool IsEnergy;
        public float LastHaptic;

        public int EdgeIndex(Collider c)
        {
            if (c == null) return -1;
            var p = c.Pointer;
            for (int i = 0; i < Edges.Length; i++)
                if (!Edges[i].Collider.IsDead() && Edges[i].Collider.Pointer == p) return i;
            return -1;
        }

        /// <summary>
        /// Distance from a contact point to an edge line (world). A contact counts for an edge when it is this close,
        /// whichever of the weapon's colliders reported it: an axe head is several colliders, and the one that touches
        /// first is not always the edge's own.
        /// </summary>
        public float DistanceToEdge(int i, Vector3 p)
        {
            var e = Edges[i];
            var a = Root.TransformPoint(e.LocalA);
            var b = Root.TransformPoint(e.LocalB);
            var ab = b - a;
            float t = Mathf.Clamp01(Vector3.Dot(p - a, ab) / Mathf.Max(1e-8f, ab.sqrMagnitude));
            return Vector3.Distance(p, a + ab * t);
        }

        /// <summary>Velocity of a point on the weapon just before this physics step.</summary>
        public Vector3 PointVelocity(Vector3 p) => HasState ? V + Vector3.Cross(W, p - Com) : Rb.GetPointVelocity(p);

        public int HandCount
        {
            get
            {
                try { var h = Weapon._host; return h != null ? h.HandCount() : 0; }
                catch { return 0; }
            }
        }

        public void Haptic(float amplitude, float seconds)
        {
            if (Time.time - LastHaptic < 0.05f) return;
            LastHaptic = Time.time;
            try
            {
                var host = Weapon._host;
                if (host == null) return;
                for (int i = 0; i < host.HandCount(); i++)
                {
                    var hand = host.GetHand(i);
                    var haptor = hand != null && hand.Controller != null ? hand.Controller.haptor : null;
                    haptor?.SENDHAPTIC(0f, seconds, 160f, Mathf.Clamp01(amplitude));
                }
            }
            catch { /* haptics are optional */ }
        }
    }

    /// <summary>
    /// Watches blade contacts with NPCs and decides, from the swing's energy, what gets cut.
    ///
    /// Energy = 1/2 * (blade mass + arm mass per holding hand) * edge speed^2 * strength setting. It is spent on the
    /// body's cross-sections in the order the edge reaches them (see <see cref="CutEnergy"/>): flesh costs energy per
    /// area, bones add a fixed cost. Sections the swing finishes are severed; the blade sticks in the first one it cannot.
    /// </summary>
    internal static class Blades
    {
        private static readonly Dictionary<IntPtr, BladeInfo> Infos = new Dictionary<IntPtr, BladeInfo>();
        private static readonly Dictionary<(IntPtr, IntPtr), float> Cooldown = new Dictionary<(IntPtr, IntPtr), float>();
        private static float _nextCooldownSweep;

        public static int Count => Infos.Count;

        public static BladeInfo Get(StabSlash weapon)
        {
            if (weapon.IsDead()) return null;
            if (Infos.TryGetValue(weapon.Pointer, out var info))
            {
                if (info == null || (!info.Rb.IsDead() && !info.Weapon.IsDead())) return info;
                Infos.Remove(weapon.Pointer);
            }
            info = Build(weapon);
            Infos[weapon.Pointer] = info;   // null: not a cutting weapon
            return info;
        }

        private static BladeInfo Build(StabSlash weapon)
        {
            try
            {
                var rb = weapon.rb;
                if (rb.IsDead()) return null;
                var root = rb.transform;
                var edges = new List<Edge>();
                var blades = weapon.slashBlades;
                if (blades != null)
                    foreach (var b in blades)
                    {
                        if (b == null || b.bladeCollider.IsDead()) continue;
                        var e = new Edge { Blade = b, Collider = b.bladeCollider, Tran = b.bladeTran.IsDead() ? b.bladeCollider.transform : b.bladeTran };
                        EdgeLine(e, root, b.bladeLength);
                        edges.Add(e);
                    }
                // Axes and cleavers have no slash blades: their edge is a stab point that is long and thin (a chop).
                // On swords and knives the stab points are the thrusting tip, so they only count when there is no blade.
                if (edges.Count == 0 && weapon.stabPoints != null)
                    foreach (var sp in weapon.stabPoints)
                    {
                        if (sp == null || sp.pointCollider.IsDead() || sp.pointTran.IsDead() || sp.aspectRatio < 4.5f) continue;
                        var e = new Edge { Chop = sp, Collider = sp.pointCollider, Tran = sp.pointTran };
                        if (ChopLine(e, root)) edges.Add(e);
                    }
                if (edges.Count == 0) return null;
                // The tip is the end of the edge away from the grip; the centre of mass sits near the grip on blades.
                var com = root.InverseTransformPoint(rb.worldCenterOfMass);
                foreach (var e in edges) e.TipIsB = (e.LocalB - com).sqrMagnitude >= (e.LocalA - com).sqrMagnitude;
                var info = new BladeInfo { Weapon = weapon, Rb = rb, Root = root, Edges = edges.ToArray() };
                // Lightsabers (and other energy blades) are built from blade colliders that are triggers: they pass
                // through everything, so they never collide, and they grow as the blade turns on.
                info.IsEnergy = edges.All(x => x.Blade != null && x.Collider.isTrigger);
                if (info.IsEnergy) foreach (var x in edges) x.NoReach = true;
                info.BladeColliders = FindBladeColliders(weapon, info);
                if (info.IsEnergy)
                    Log.Msg($"{weapon.name} is an energy blade: it cuts through anything it touches while it is on" +
                            (Settings.SaberSears.Value ? " and burns the wounds shut" : ""));
                else
                    Log.Msg($"{weapon.name} cuts with {edges.Count} {(edges[0].Chop != null ? "chopping" : "slashing")} edge(s), " +
                            $"{string.Join(" + ", edges.Select(x => $"{(x.LocalB - x.LocalA).magnitude * 100f:F0} cm"))} long, {rb.mass:F1} kg");
                return info;
            }
            catch (Exception e)
            {
                Log.Debug($"blade setup failed for {weapon.name}: {e.Message}");
                return null;
            }
        }

        /// <summary>The edge as a line segment, from its collider (capsule/box) or bladeTran +- bladeLength.</summary>
        private static void EdgeLine(Edge e, Transform root, float bladeLength)
        {
            var col = e.Collider;
            var t = col.transform;
            Vector3 a, b;
            float radius = 0.005f;
            var capsule = col.TryCast<CapsuleCollider>();
            var box = capsule == null ? col.TryCast<BoxCollider>() : null;
            if (capsule != null)
            {
                var axis = capsule.direction == 0 ? Vector3.right : capsule.direction == 1 ? Vector3.up : Vector3.forward;
                var c = t.TransformPoint(capsule.center);
                var half = t.TransformVector(axis * (capsule.height * 0.5f));
                var rv = t.TransformVector(Vector3.one * capsule.radius);
                radius = Mathf.Max(Mathf.Abs(rv.x), Mathf.Abs(rv.y), Mathf.Abs(rv.z)) / 1.7320508f;
                a = c - half;
                b = c + half;
            }
            else if (box != null)
            {
                var s = box.size;
                int k = s.x >= s.y && s.x >= s.z ? 0 : s.y >= s.z ? 1 : 2;
                var axis = k == 0 ? Vector3.right : k == 1 ? Vector3.up : Vector3.forward;
                var c = t.TransformPoint(box.center);
                var half = t.TransformVector(axis * (s[k] * 0.5f));
                a = c - half;
                b = c + half;
            }
            else
            {
                var tran = e.Tran;
                float len = bladeLength > 0f ? bladeLength * tran.lossyScale.y : 0.2f;
                a = tran.position - tran.up * len;
                b = tran.position + tran.up * len;
            }
            e.LocalA = root.InverseTransformPoint(a);
            e.LocalB = root.InverseTransformPoint(b);
            e.Radius = radius;
        }

        /// <summary>
        /// A chopping edge: SLZ stab points put pointTran's Y along the edge and Z out of it (the chop direction). The
        /// edge spans the stab collider along Y; it lies on the collider's front (the sharp side).
        /// </summary>
        private static bool ChopLine(Edge e, Transform root)
        {
            var t = e.Tran;
            var col = e.Collider;
            var fwd = t.forward;
            var c = col.bounds.center;
            // The edge runs along whichever of the stab point's other two axes the head is long in (most put it on Y,
            // the hatchet on X).
            float Extent(Vector3 axis, out float lo, out float hi)
            {
                try
                {
                    lo = Vector3.Dot(col.ClosestPoint(c - axis * 5f) - t.position, axis);
                    hi = Vector3.Dot(col.ClosestPoint(c + axis * 5f) - t.position, axis);
                }
                catch
                {
                    float r = col.bounds.extents.magnitude * 0.5f;
                    lo = Vector3.Dot(c - t.position, axis) - r;
                    hi = lo + 2f * r;
                }
                return hi - lo;
            }
            float uy0, uy1, ux0, ux1;
            bool alongY = Extent(t.up, out uy0, out uy1) >= Extent(t.right, out ux0, out ux1);
            var along = alongY ? t.up : t.right;
            float u0 = alongY ? uy0 : ux0, u1 = alongY ? uy1 : ux1;
            if (u1 - u0 < 0.03f) { u0 = -0.06f; u1 = 0.06f; }   // could not measure it: a typical axe edge
            Vector3 front;
            try { front = col.ClosestPoint(c + fwd * 5f); } catch { front = t.position; }
            // On the front of the collider, but no further out than the stab point itself.
            var basePoint = t.position + fwd * Mathf.Min(0f, Vector3.Dot(front - t.position, fwd));
            e.LocalA = root.InverseTransformPoint(basePoint + along * u0);
            e.LocalB = root.InverseTransformPoint(basePoint + along * u1);
            var cut = fwd - along * Vector3.Dot(fwd, along);
            e.LocalCutDir = cut.sqrMagnitude > 1e-6f ? root.InverseTransformDirection(cut.normalized) : Vector3.zero;
            e.Radius = 0.008f;
            return true;
        }

        /// <summary>Solid colliders lying along any edge: the blade's flat, edges and tip, but not the guard or grip.</summary>
        private static List<Collider> FindBladeColliders(StabSlash weapon, BladeInfo info)
        {
            var result = new List<Collider>();
            foreach (var e in info.Edges) if (!result.Any(c => c.Pointer == e.Collider.Pointer)) result.Add(e.Collider);
            try
            {
                var stabs = weapon.stabPoints;
                if (stabs != null)
                    foreach (var s in stabs)
                        if (s != null && !s.pointCollider.IsDead() && !result.Any(c => c.Pointer == s.pointCollider.Pointer)) result.Add(s.pointCollider);
            }
            catch { }
            foreach (var c in weapon.GetComponentsInChildren<Collider>(true))
            {
                if (c.IsDead() || c.isTrigger || result.Any(x => x.Pointer == c.Pointer)) continue;
                var p = info.Root.InverseTransformPoint(c.bounds.center);
                foreach (var e in info.Edges)
                {
                    var ab = e.LocalB - e.LocalA;
                    float len2 = ab.sqrMagnitude;
                    if (len2 < 1e-8f) continue;
                    float t = Vector3.Dot(p - e.LocalA, ab) / len2;
                    // A chopping head goes into the body whole (the edge, the cheeks behind it, the back of the head).
                    bool chop = e.Chop != null;
                    if (t < (chop ? -0.3f : -0.05f) || t > (chop ? 1.3f : 1.05f)) continue;
                    float off = Vector3.Distance(p, e.LocalA + ab * t);
                    if (off > (chop ? 0.3f : 0.06f)) continue;
                    result.Add(c);
                    break;
                }
            }
            return result;
        }

        /// <summary>Called before every physics step for each weapon: remember its motion before contacts change it.</summary>
        public static void Record(StabSlash weapon)
        {
            var info = Get(weapon);
            if (info == null) return;
            var rb = info.Rb;
            info.V = rb.velocity;
            info.W = rb.angularVelocity;
            info.Com = rb.worldCenterOfMass;
            info.HasState = true;
            if (info.IsEnergy) PollEnergy(info);
            else if (Settings.Enabled.Value) Stabs.Poll(info);
        }

        /// <summary>Slowest an energy blade's edge may sweep and still cut (m/s), whatever "Minimum swing speed" says.</summary>
        private const float EnergyMinSpeed = 0.3f;
        /// <summary>An energy blade's "swing energy": more than any body takes to cut through.</summary>
        private const float EnergyBladeJoules = 1e6f;

        private static readonly Il2CppReferenceArray<Collider> Overlaps = new Il2CppReferenceArray<Collider>(32);

        /// <summary>Makes a lightsaber stay away from an NPC for a moment (it is still inside after a slice ends).</summary>
        public static void Rest(BladeInfo info, NpcBody npc, float seconds)
        {
            if (info?.Weapon == null || npc == null) return;
            Cooldown[(info.Weapon.Pointer, npc.Key)] = Time.time + seconds;
        }

        /// <summary>
        /// Energy blades are triggers, so nothing ever collides with them: every physics step the blade (as long as
        /// it is right now: it grows as it turns on) is checked for NPCs it passes through, and a touch starts a cut
        /// exactly as a steel blade's contact does.
        /// </summary>
        private static void PollEnergy(BladeInfo info)
        {
            if (!Settings.Enabled.Value) return;
            try
            {
                Edge blade = null;
                float longest = 0f;
                var com = info.Root.InverseTransformPoint(info.Rb.worldCenterOfMass);
                foreach (var e in info.Edges)
                {
                    if (e.Collider.IsDead() || !e.Collider.enabled || !e.Collider.gameObject.activeInHierarchy) continue;
                    EdgeLine(e, info.Root, e.Blade != null ? e.Blade.bladeLength : 0f);
                    e.TipIsB = (e.LocalB - com).sqrMagnitude >= (e.LocalA - com).sqrMagnitude;
                    float len = (e.LocalB - e.LocalA).magnitude;
                    if (len > longest) { longest = len; blade = e; }
                }
                if (blade == null || longest < 0.12f) return;   // off, or still coming out of the hilt
                var a = info.Root.TransformPoint(blade.LocalA);
                var b = info.Root.TransformPoint(blade.LocalB);

                // Bodies the beam is about to reach (as far as its tip moves in a couple of steps): nothing solid on the
                // blade may knock into them. Sabers carry a small solid collider at the tip for the game's stabbing,
                // which would otherwise catch in flesh and stop the blade like a stick.
                float tipSpeed = info.V.magnitude + info.W.magnitude * longest;
                float margin = Mathf.Clamp(tipSpeed * Time.fixedDeltaTime * 2f + 0.1f, 0.15f, 0.7f);
                int near = Physics.OverlapCapsuleNonAlloc(a, b, blade.Radius + margin, Overlaps, ~0, QueryTriggerInteraction.Ignore);
                for (int i = 0; i < near && i < Overlaps.Length; i++)
                {
                    var rbNear = Overlaps[i]?.attachedRigidbody;
                    if (rbNear == null) continue;
                    var npcNear = NpcRegistry.Find(rbNear, out _);
                    if (npcNear != null) LetThrough(info, npcNear);
                }
                if (Slices.IsBusy(info.Weapon)) return;

                int n = Physics.OverlapCapsuleNonAlloc(a, b, blade.Radius + 0.01f, Overlaps, ~0, QueryTriggerInteraction.Ignore);
                float now = Time.time;
                for (int i = 0; i < n && i < Overlaps.Length; i++)
                {
                    var col = Overlaps[i];
                    if (col == null) continue;
                    var rb = col.attachedRigidbody;
                    if (rb == null) continue;
                    var npc = NpcRegistry.Find(rb, out int seg);
                    if (npc == null) continue;
                    var key = (info.Weapon.Pointer, npc.Key);
                    if (Cooldown.TryGetValue(key, out float until) && now < until) continue;
                    // A thrust in along the beam makes a hole rather than a cut.
                    if (Stabs.Energy(info, blade, npc, seg, col, blade.TipIsB ? a : b, blade.TipIsB ? b : a)) return;
                    // Where it touches: the point of that body part nearest the blade.
                    var mid = ClosestOnSegment(a, b, col.bounds.center);
                    Vector3 p;
                    try { p = col.ClosestPoint(mid); }
                    catch { p = mid; }
                    try
                    {
                        Swing(info, blade, npc, seg, rb, p, key);
                    }
                    catch (Exception e)
                    {
                        Log.Warn($"Energy blade contact failed: {e}");
                        Cooldown[key] = now + 0.5f;
                    }
                    return;   // one cut at a time
                }
            }
            catch (Exception e)
            {
                Log.Debug("energy blade poll: " + e.Message);
            }
        }

        /// <summary>When each energy blade last let each NPC through, and how many colliders the NPC had then.</summary>
        private static readonly Dictionary<(IntPtr, IntPtr), (float Time, int Colliders)> PassThrough = new Dictionary<(IntPtr, IntPtr), (float, int)>();

        /// <summary>
        /// An energy blade goes through bodies: its solid parts along the beam stop colliding with this NPC (for
        /// good; refreshed now and then, and when the NPC gains chunk colliders), and any stab the game made with it
        /// into this NPC lets go.
        /// </summary>
        private static void LetThrough(BladeInfo info, NpcBody npc)
        {
            var key = (info.Weapon.Pointer, npc.Key);
            int count = npc.ExtraColliders.Count;
            float now = Time.time;
            if (PassThrough.TryGetValue(key, out var seen) && seen.Colliders == count && now - seen.Time < 5f) return;
            PassThrough[key] = (now, count);
            var npcCols = AllNpcColliders(npc);
            foreach (var c in info.BladeColliders)
            {
                if (c.IsDead() || c.isTrigger) continue;
                foreach (var o in npcCols) Physics.IgnoreCollision(c, o, true);
            }
            ReleaseStockSlashes(info, npc);
        }

        private static Vector3 ClosestOnSegment(Vector3 a, Vector3 b, Vector3 p)
        {
            var ab = b - a;
            float t = Mathf.Clamp01(Vector3.Dot(p - a, ab) / Mathf.Max(1e-8f, ab.sqrMagnitude));
            return a + ab * t;
        }

        public static void OnContact(StabSlash weapon, Collision c)
        {
            if (!Settings.Enabled.Value || c == null) return;
            var otherRb = c.rigidbody;
            if (otherRb == null) return;   // world geometry
            var npc = NpcRegistry.Find(otherRb, out int seg);
            if (npc == null) return;
            var info = Get(weapon);
            if (info == null || Slices.IsBusy(weapon)) return;

            var key = (weapon.Pointer, npc.Key);
            float now = Time.time;
            if (Cooldown.TryGetValue(key, out float until) && now < until) return;

            // Where the contact is, on which edge: contacts on an edge's own collider, or close enough to its line.
            // With several candidates (both edges of a sword), the one facing the way the blade moves.
            int edgeCount = info.Edges.Length;
            var sums = new Vector3[edgeCount];
            var counts = new int[edgeCount];
            int contacts = c.contactCount;
            for (int k = 0; k < contacts; k++)
            {
                var cp = c.GetContact(k);
                int own = info.EdgeIndex(cp.thisCollider);
                for (int i = 0; i < edgeCount; i++)
                {
                    var ed = info.Edges[i];
                    float tol = ed.Radius + (ed.Chop != null ? 0.06f : 0.03f);
                    if (i != own && info.DistanceToEdge(i, cp.point) > tol) continue;
                    sums[i] += cp.point;
                    counts[i]++;
                }
            }
            int edge = -1;
            float best = float.MinValue;
            for (int i = 0; i < edgeCount; i++)
            {
                if (counts[i] == 0) continue;
                var at = sums[i] / counts[i];
                var rel = info.PointVelocity(at) - otherRb.GetPointVelocity(at);
                float score = Vector3.Dot(CutDirection(info, info.Edges[i], rel), rel.normalized);
                if (score > best) { best = score; edge = i; }
            }
            if (edge < 0) return;
            var p = sums[edge] / counts[edge];

            try
            {
                Swing(info, info.Edges[edge], npc, seg, otherRb, p, key);
            }
            catch (Exception e)
            {
                Log.Warn($"Blade contact failed: {e}");
                Cooldown[key] = now + 0.5f;
            }
        }

        /// <summary>
        /// The way an edge faces (out of its sharp side), across the blade. SLZ blades put bladeTran / pointTran's Y
        /// along the edge, Z out of the edge and X across the flat. If a weapon does not follow that, the edge is taken
        /// to face the way it moves (<paramref name="v"/>).
        /// </summary>
        internal static Vector3 CutDirection(BladeInfo info, Edge e, Vector3 v)
        {
            if (e.LocalCutDir.sqrMagnitude > 0.5f) return info.Root.TransformDirection(e.LocalCutDir);
            var tran = e.Tran;
            var along = (info.Root.TransformPoint(e.LocalB) - info.Root.TransformPoint(e.LocalA)).normalized;
            if (!tran.IsDead() && Mathf.Abs(Vector3.Dot(tran.up, along)) >= 0.7f)
            {
                var flat = tran.right - along * Vector3.Dot(tran.right, along);
                if (flat.sqrMagnitude > 1e-6f)
                {
                    var cutDir = Vector3.Cross(flat.normalized, along);
                    return Vector3.Dot(cutDir, tran.forward) < 0f ? -cutDir : cutDir;
                }
            }
            var d = v - along * Vector3.Dot(v, along);
            return d.sqrMagnitude < 1e-6f ? Vector3.zero : d.normalized;
        }

        private static void Swing(BladeInfo info, Edge e, NpcBody npc, int seg, Rigidbody otherRb, Vector3 p, (IntPtr, IntPtr) key)
        {
            float now = Time.time;
            if (npc.Cuts >= Settings.MaxCutsPerNpc.Value) return;

            int hands = info.HandCount;
            var bladeV = info.PointVelocity(p);
            // An energy blade cuts at any speed: it burns through rather than chops.
            float minSpeed = info.IsEnergy ? EnergyMinSpeed : Settings.MinCutSpeed.Value;
            // A blade lying still does not cut whoever walks into it; a thrown one does.
            if (hands == 0 && bladeV.magnitude < minSpeed) return;
            var v = bladeV - otherRb.GetPointVelocity(p);

            // Blade frame. SLZ blades put bladeTran's Y along the edge, Z out of the edge (the cutting direction) and
            // X across the flat. The edge line itself comes from the edge collider (plus the extra reach).
            e.CutLine(info.Root, out var a, out var b);
            var along = (b - a).normalized;
            if (along.sqrMagnitude < 0.5f) return;

            // How fast the edge line sweeps through (whatever way the blade is turned), and how well the edge leads.
            // Swings are rarely in line with the blade: a horizontal swing usually has the wrist turned part way. Up
            // to ~35 degrees off counts fully, then the swing loses power until ~75 degrees, where the flat leads and
            // it is a slap. The back of a blade does not cut. An energy blade is all edge.
            var sweep = v - along * Vector3.Dot(v, along);
            float vSweep = sweep.magnitude;
            if (vSweep < minSpeed) return;
            sweep /= vSweep;
            float cosTilt = 1f;
            if (!info.IsEnergy)
            {
                var cutDir = CutDirection(info, e, v);
                if (cutDir.sqrMagnitude < 0.5f) return;
                cosTilt = Vector3.Dot(sweep, cutDir);
            }
            const float FullUpTo = 0.819f, NoneFrom = 0.259f;   // cos 35, cos 75
            if (cosTilt <= NoneFrom) return;
            float edgeFactor = Mathf.Clamp01((cosTilt - NoneFrom) / (FullUpTo - NoneFrom));
            float vDraw = Vector3.Dot(v, along);
            float speed = Mathf.Sqrt(vSweep * vSweep + 0.12f * vDraw * vDraw);   // drawing the edge along helps a little

            float mass = info.Rb.mass + hands * Settings.HandStrengthKg.Value;
            float energy = info.IsEnergy ? EnergyBladeJoules : 0.5f * mass * speed * speed * Settings.CutStrength.Value * edgeFactor;
            float vEdge = vSweep;

            // The cut follows the blade's real path: the plane the edge sweeps (the edge line and the way it moves),
            // not the blade's flat, which a real swing is rarely exactly in line with. Along the edge the strip spans
            // the edge's extent from the contact; forward it is open. Behind the contact it reaches back as far as a
            // fast blade gets into the body within one physics step: the hit only registers once it is already in.
            var sweepNormal = Vector3.Cross(along, sweep).normalized;
            float ua = Vector3.Dot(a - p, along), ub = Vector3.Dot(b - p, along);
            float back = Mathf.Clamp(vSweep * Time.fixedDeltaTime * 1.5f + 0.03f, 0.05f, 0.3f);
            var region = CutRegion.FromBlade(p.N(), sweepNormal.N(), along.N(), sweep.N(),
                Mathf.Min(ua, ub) - e.Radius, Mathf.Max(ua, ub) + e.Radius, -back);

            var attempt = CutPipeline.Prepare(npc, seg, region);
            if (attempt == null)
            {
                Cooldown[key] = now + 0.1f;   // meshes still loading or a cut in flight
                return;
            }
            var plan = CutEnergy.Decide(attempt.Report, attempt.Model, energy, npc.Material);
            string source = info.IsEnergy ? $"{info.Weapon.name}, energy blade, {vEdge:F1} m/s" : $"{info.Weapon.name}, {vEdge:F1} m/s, {energy:F0} J";
            Log.Debug($"{info.Weapon.name} -> {npc.Name}/{npc.Muscles[seg].Name}: edge {vEdge:F1} m/s ({Mathf.Acos(Mathf.Clamp(cosTilt, -1f, 1f)) * Mathf.Rad2Deg:F0} deg off the edge), " +
                      $"{hands} hand(s), {energy:F0} J; needs {plan.EnergyRequiredTotal:F0} J; could sever [{string.Join(",", plan.Severed)}], " +
                      $"part-way through [{string.Join(",", plan.Gashed)}], stops in {plan.StuckGroup} ({plan.StuckFraction:P0})");

            // Nothing comes off on contact: the blade is let into the body and has to make it out the other side
            // (Slices). That includes sections the edge does not reach all the way across yet: a swing is an arc and
            // the edge sweeps more of the body as it goes. A swing too weak to even sink the edge in just scratches.
            var w = region.AxisW.U();
            bool bites = plan.AnySevered || plan.Gashed.Count > 0 || (plan.StuckGroup >= 0 && plan.StuckDepth >= 0.015f && energy >= 6f);
            if (bites)
            {
                Slices.Begin(info, e, npc, attempt, region, p, energy, source);
            }
            else if (plan.StuckGroup >= 0)
            {
                // Too weak to sink in: a nick in the skin.
                Gore.Burst(p, -w, 0.4f, npc.Palette);
                Sounds.Play(Sounds.Kind.Scratch, p, Mathf.Clamp01(energy / 20f));
                info.Haptic(0.35f, 0.04f);
                if (energy >= 3f)
                {
                    var g = attempt.Report.Groups[plan.StuckGroup];
                    Wounds.Show(null, npc, attempt.Report.Loops[g.OuterLoop], region, g.WMin + Mathf.Max(plan.StuckDepth, 0.008f),
                                region.UMin, region.UMax, attempt.Snapshot);
                }
            }
            bool severs = bites;

            Cooldown[key] = now + (severs ? 0.15f : 0.25f);
            if (now > _nextCooldownSweep)
            {
                _nextCooldownSweep = now + 10f;
                var stale = Cooldown.Where(kv => kv.Value < now).Select(kv => kv.Key).ToList();
                foreach (var k in stale) Cooldown.Remove(k);
            }
        }

        /// <summary>Segments plus their direct parents and children (flesh near a cut overlaps neighbouring colliders).</summary>
        public static HashSet<int> WithNeighbours(NpcBody npc, IEnumerable<int> segs)
        {
            var result = new HashSet<int>(segs);
            foreach (int s in result.ToList())
            {
                int p = npc.OriginalParents[s];
                if (p >= 0) result.Add(p);
                for (int i = 0; i < npc.OriginalParents.Length; i++)
                    if (npc.OriginalParents[i] == s) result.Add(i);
            }
            return result;
        }

        public static List<Collider> NpcColliders(NpcBody npc, IEnumerable<int> segs)
        {
            var list = new List<Collider>();
            foreach (int s in segs)
                foreach (var c in npc.Muscles[s].Colliders)
                    if (!c.IsDead()) list.Add(c);
            foreach (var c in npc.ExtraColliders)
                if (!c.IsDead()) list.Add(c);
            return list;
        }

        public static List<Collider> AllNpcColliders(NpcBody npc) => NpcColliders(npc, Enumerable.Range(0, npc.Muscles.Length));

        /// <summary>
        /// The game's own slash system may have pinned the blade to this NPC on contact; those joints would drag
        /// severed parts around (or fight our stuck joint), so let go of them.
        /// </summary>
        public static void ReleaseStockSlashes(BladeInfo info, NpcBody npc)
        {
            foreach (var e in info.Edges)
            {
                try
                {
                    var joints = e.Blade.bladeJoints;
                    if (joints == null) continue;
                    for (int i = joints.Length - 1; i >= 0; i--)
                    {
                        var bj = joints[i];
                        if (bj == null || bj.joint.IsDead() || bj.collider.IsDead()) continue;
                        var rb = bj.collider.attachedRigidbody;
                        if (rb.IsDead() || !npc.MuscleByRigidbody.ContainsKey(rb.Pointer)) continue;
                        e.Blade.UnSlash(i, false);
                    }
                }
                catch (Exception ex) { Log.Debug("unslash: " + ex.Message); }
            }
            // Axes and cleavers "stab" on a chop: the game joints them into the flesh.
            try
            {
                var stabs = info.Weapon.stabPoints;
                if (stabs == null) return;
                foreach (var sp in stabs)
                {
                    var joints = sp?.stabJoints;
                    if (joints == null) continue;
                    for (int i = joints.Length - 1; i >= 0; i--)
                    {
                        var sj = joints[i];
                        if (sj == null || sj.joint.IsDead() || sj.collider.IsDead()) continue;
                        var rb = sj.collider.attachedRigidbody;
                        if (rb.IsDead() || !npc.MuscleByRigidbody.ContainsKey(rb.Pointer)) continue;
                        sp.UnStab(i);
                    }
                }
            }
            catch (Exception ex) { Log.Debug("unstab: " + ex.Message); }
        }

        public static void Clear()
        {
            Infos.Clear();
            Cooldown.Clear();
            PassThrough.Clear();
        }
    }
}
