using System;
using System.Collections.Generic;
using System.Linq;
using Il2CppSLZ.Marrow;
using UnityEngine;
using Dismember.Core;

namespace Dismember.Game
{
    /// <summary>
    /// A blade passing through a body.
    ///
    /// When an edge hits flesh hard enough, the blade is let into the body (collisions with the NPC are switched off)
    /// and followed physics step by physics step. Every cross-section ahead of it (an arm, the neck, the torso...) is
    /// only severed once the edge has actually come out the other side of it. On the way the flesh drags on the blade
    /// and uses up the swing's energy; a swing that runs out of energy (or a blade that stops moving) inside a section
    /// leaves the blade stuck there. A stuck blade is held in its cut by a joint: pushing it deeper or sawing along the
    /// edge finishes the cut, pulling it back out frees it. Whatever was cut into but not through is left as an open
    /// wound (<see cref="Wounds"/>).
    /// </summary>
    internal static class Slices
    {
        private sealed class Section
        {
            public int Id;
            public HashSet<int> Segments;
            public int Body;               // segment whose motion carries this section's part of the plane
            public float WMin, WMax;       // extent along the cut, measured from the entry point
            public float UMin, UMax;       // extent along the edge's length
            public float SweptMin, SweptMax;   // stretch of it (along the edge) the edge has passed over, in its own frame
            public float Rate;             // energy needed per metre the edge advances through it (J/m)
            public float LastDepth;        // where the edge physically is (along the cut)
            public float Cut;              // how far it has actually cut (can lag behind a blade that ran out of power)
            public bool Entered, Done, Severed;
            public bool SeverFailed;       // the edge came out the far side but never spanned it (blade too short)
            public int SeverTries;
            public bool HasBone;
            public SectionLoop Loop;       // its outline, for the wound
            public Wounds.Wound Wound;
            public float WoundTime;
            public string Name;
            public List<Vector2> Path;     // energy blades: (depth along the cut, offset off the entry plane) as it went
        }

        private sealed class Slice
        {
            public BladeInfo Info;
            public Edge Edge;
            public NpcBody Npc;
            public int Generation;
            public PoseSnapshot EntryPose;   // body poses when the edge went in
            public CutRegion Region;         // cutting plane at that moment (world)
            public Vector3 RefOnBlade;       // the contact point, in weapon space
            public List<Section> Sections;
            public float Energy, Budget;     // swing energy at the start / left (J)
            public string Source;
            public float Started, LastAdvance;
            public List<(IntPtr, IntPtr)> Ignored;
            // Stuck phase.
            public Section StuckIn;
            public ConfigurableJoint Joint;
            public float LastU;
            public float NextBlood;
            public bool? Ending;             // set when the slice is over; it ends once its finished cuts have landed
            public int ExtraSeen;
            public readonly List<string> Story = new List<string>();   // what happened, for the log
            public bool EnergyBlade;         // an energy blade (lightsaber): no drag, never sticks, no power limit
            public bool Sear;                // ... and burns the wounds shut (no blood)
            // The cut as it is being made: one opening in the body's mesh that grows behind the edge.
            public Wounds.Wound Opening;
            // A steel blade cutting through is held to its line by the flesh (see Guide).
            public ConfigurableJoint Guide;
            public int GuideBody = -1;
            public bool GuideTorn;
            public float NextLive;
            public float LiveKey = float.NaN;  // what the opening was last asked to show (to skip asking for the same again)
        }

        private static readonly List<Slice> Active = new List<Slice>();

        /// <summary>The edge cuts this far ahead of itself (flesh parts in front of a sharp edge).</summary>
        private const float Ahead = 0.012f;

        public static int Count => Active.Count;
        public static int StuckCount => Active.Count(s => s.StuckIn != null);

        public static bool IsBusy(StabSlash weapon)
        {
            var p = weapon.Pointer;
            foreach (var s in Active) if (s.Info.Weapon.Pointer == p) return true;
            return false;
        }

        /// <summary>Starts a slice; <paramref name="attempt"/> holds the sections ahead of the edge.</summary>
        public static void Begin(BladeInfo info, Edge edge, NpcBody npc, CutAttempt attempt, CutRegion region, Vector3 p,
                                 float energy, string source)
        {
            var report = attempt.Report;
            var sections = new List<Section>();
            foreach (var g in report.Groups.Where(g => g.InReach && g.Area > 0f).OrderBy(g => g.WMin))
            {
                float depth = Mathf.Max(0.01f, g.WMax - g.WMin);
                float coverage = g.FullyCovered ? 1f : Mathf.Max(0.05f, g.Coverage);
                var loop = report.Loops[g.OuterLoop];
                sections.Add(new Section
                {
                    Id = g.Id,
                    Segments = new HashSet<int>(g.Segments),
                    Body = NearestBody(npc, g),
                    WMin = g.WMin,
                    WMax = g.WMax,
                    UMin = loop.Min.X,
                    UMax = loop.Max.X,
                    SweptMin = region.UMin,
                    SweptMax = region.UMax,
                    Cut = g.WMin,
                    Rate = CutEnergy.RequiredEnergy(g, attempt.Model, npc.Material, coverage) / depth,
                    HasBone = g.BoneCrossings.Count > 0,
                    Loop = loop,
                    Name = string.Join("/", g.Segments.Select(x => npc.Muscles[x].Name)),
                });
            }
            if (sections.Count == 0 || sections.Any(s => s.Body < 0)) return;

            var slice = new Slice
            {
                Info = info,
                Edge = edge,
                Npc = npc,
                Generation = npc.Generation,
                EntryPose = attempt.Snapshot,
                Region = region,
                RefOnBlade = info.Root.InverseTransformPoint(p),
                Sections = sections,
                Energy = energy,
                Budget = energy,
                Source = source,
                Started = Time.time,
                LastAdvance = Time.time,
                ExtraSeen = npc.ExtraColliders.Count,
                EnergyBlade = info.IsEnergy,
                Sear = info.IsEnergy && Settings.SaberSears.Value,
            };

            Stabs.Forget(info, npc);   // a stab the game made on the way in is part of this cut, not a hole of its own
            Blades.ReleaseStockSlashes(info, npc);
            // The blade is going into the body: nothing of this NPC may push it back out while it cuts.
            slice.Ignored = Collisions.Hold(info.BladeColliders, Blades.AllNpcColliders(npc));
            // The contact itself already slowed the blade this step; give it back its swing.
            if (info.HasState)
            {
                info.Rb.velocity = info.V;
                info.Rb.angularVelocity = info.W;
            }
            Active.Add(slice);

            if (slice.Sear) Gore.Embers(p, -region.AxisW.U(), 0.6f);
            else Gore.Burst(p, -region.AxisW.U(), 0.5f, npc.Palette);
            Sounds.Play(slice.EnergyBlade ? Sounds.Kind.Sizzle : Sounds.Kind.SliceIn, p, Mathf.Clamp01(0.35f + energy / 400f));
            info.Haptic(0.6f, 0.05f);
            Log.Debug($"{source} enters {npc.Name}: {energy:F0} J for sections [{string.Join(", ", sections.Select(s => $"{s.Name} {s.Rate * (s.WMax - s.WMin):F0} J"))}]");
        }

        private static int NearestBody(NpcBody npc, SectionGroup g)
        {
            int best = -1;
            float bestD = float.MaxValue;
            var centre = g.Centroid3D.U();
            foreach (int s in g.Segments)
            {
                var m = npc.Muscles[s];
                if (m.Body.IsDead() || m.Rb.IsDead()) continue;
                float d = (m.Body.position - centre).sqrMagnitude;
                if (d < bestD) { bestD = d; best = s; }
            }
            return best;
        }

        // The entry plane as it sits on body `seg` now.
        private static void Frame(Slice s, int seg, out Vector3 origin, out Vector3 n, out Vector3 u, out Vector3 w)
        {
            var pose = s.EntryPose;
            origin = pose.Point(s.Npc, seg, s.Region.Origin.U());
            n = pose.Direction(s.Npc, seg, s.Region.Normal.U());
            u = pose.Direction(s.Npc, seg, s.Region.AxisU.U());
            w = pose.Direction(s.Npc, seg, s.Region.AxisW.U());
        }

        public static void FixedUpdate()
        {
            for (int i = Active.Count - 1; i >= 0; i--)
            {
                if (i >= Active.Count) continue;
                var s = Active[i];
                try
                {
                    if (!Valid(s)) { End(s, false); continue; }
                    HoldNewColliders(s);
                    if (s.Ending == null)
                    {
                        if (s.StuckIn == null) Traverse(s);
                        else Stuck(s);
                    }
                    SeverFinished(s);
                    if (s.Ending != null && !s.Sections.Any(x => x.Done && !x.Severed)) End(s, s.Ending.Value);
                }
                catch (Exception e)
                {
                    Log.Debug("slice: " + e);
                    End(s, false);
                }
            }
        }

        /// <summary>The slice is over; it is removed once any finished cut has been submitted.</summary>
        private static void Finish(Slice s, bool wound, string why)
        {
            if (s.Ending == null && why != null) s.Story.Add(why);
            s.Ending ??= wound;
            DropGuide(s);
            if (!s.Joint.IsDead()) UnityEngine.Object.Destroy(s.Joint);
            s.Joint = null;
            s.StuckIn = null;
        }

        /// <summary>Chunk colliders created by cuts during this slice must not stop the blade either.</summary>
        private static void HoldNewColliders(Slice s)
        {
            var extra = s.Npc.ExtraColliders;
            if (extra.Count <= s.ExtraSeen) return;
            var fresh = extra.Skip(s.ExtraSeen).Where(c => !c.IsDead()).ToList();
            s.ExtraSeen = extra.Count;
            if (fresh.Count > 0) s.Ignored.AddRange(Collisions.Hold(s.Info.BladeColliders, fresh));
        }

        private static bool Valid(Slice s) =>
            !s.Info.Rb.IsDead() && !s.Info.Weapon.IsDead() && s.Npc.Valid && s.Npc.Generation == s.Generation;

        private static Section Current(Slice s) => s.Sections.FirstOrDefault(x => !x.Done) ?? s.Sections[s.Sections.Count - 1];

        private const int EdgeSamples = 9;

        /// <summary>Points along the cutting line (edge plus reach), in world space (the contact point first).</summary>
        private static Vector3[] EdgePoints(Slice s)
        {
            var root = s.Info.Root;
            var pts = new Vector3[EdgeSamples + 1];
            pts[0] = root.TransformPoint(s.RefOnBlade);
            s.Edge.CutLine(root, out var a, out var b);
            for (int i = 0; i < EdgeSamples; i++) pts[i + 1] = Vector3.Lerp(a, b, i / (EdgeSamples - 1f));
            return pts;
        }

        /// <summary>
        /// How far the edge has got through a section: the furthest any part of the edge over the section has gone.
        /// A swing is an arc, and the tip runs ahead of the contact point, so the contact point alone lags.
        /// </summary>
        private static float SectionDepth(Vector3[] pts, Vector3 o, Vector3 u, Vector3 w, Section sec)
        {
            float best = Vector3.Dot(pts[0] - o, w);
            for (int i = 1; i < pts.Length; i++)
            {
                float pu = Vector3.Dot(pts[i] - o, u);
                if (pu < sec.UMin - 0.03f || pu > sec.UMax + 0.03f) continue;
                best = Mathf.Max(best, Vector3.Dot(pts[i] - o, w));
            }
            return best;
        }

        private static void Traverse(Slice s)
        {
            var info = s.Info;
            var pts = EdgePoints(s);
            var refWorld = pts[0];
            float dt = Time.fixedDeltaTime;

            // Where the edge is relative to the plane it went in on.
            var cur = Current(s);
            Frame(s, cur.Body, out var o, out var n, out var u, out var w);
            float depth = float.MinValue;
            foreach (var pt in pts) depth = Mathf.Max(depth, Vector3.Dot(pt - o, w));
            // An energy blade may be steered along a curve: how far it strays is measured from where its path has got to.
            if (s.EnergyBlade && cur.Path != null && cur.Path.Count > 0) o += n * cur.Path[cur.Path.Count - 1].y;
            float off = OffPlane(pts, o, n);
            bool allDone = s.Sections.All(x => x.Done);
            if (!allDone && depth < -0.06f)
            {
                CompleteNearlyThrough(s, pulledOut: true);
                Finish(s, true, "pulled back out");
                return;
            }
            if (!allDone && off > 0.22f)
            {
                CompleteNearlyThrough(s);
                Finish(s, true, $"blade went {off * 100f:F0} cm off its line");
                return;
            }

            // Advance through each section in turn, paying for the flesh the edge goes through. A fast blade can cover a
            // whole section in one step; it only cuts as far as its energy lasts.
            float dragRate = 0f;
            bool advanced = false, outOfEnergy = false;
            Section deepest = null;
            foreach (var sec in s.Sections)
            {
                if (sec.Done) continue;
                Frame(s, sec.Body, out var so, out var sn, out var su, out var sw);
                float d = SectionDepth(pts, so, su, sw, sec);
                sec.LastDepth = d;
                if (d + Ahead > sec.WMin - 0.02f && d < sec.WMax + 0.05f)
                {
                    TrackSweep(s, sec, so, su);
                    if (s.EnergyBlade) TrackPath(sec, pts, so, sn, su, d);
                }
                if (d + Ahead <= sec.WMin) continue;
                if (!sec.Entered && sec != s.Sections[0])
                    Sounds.Play(Sounds.Kind.SliceIn, refWorld, Mathf.Clamp01(0.25f + s.Budget / 500f));
                sec.Entered = true;
                deepest ??= sec;
                if (d < sec.WMax) dragRate += sec.Rate;
                float target = Mathf.Min(d + Ahead, sec.WMax);
                if (target > sec.Cut && !outOfEnergy)
                {
                    float need = (target - sec.Cut) * sec.Rate;
                    if (s.Budget >= need)
                    {
                        s.Budget -= need;
                        sec.Cut = target;
                    }
                    else
                    {
                        sec.Cut += Mathf.Max(0f, s.Budget) / sec.Rate;
                        s.Budget = 0f;
                        outOfEnergy = true;
                        deepest = sec;
                    }
                    advanced = true;
                }
                if (sec.Cut >= sec.WMax - 0.001f) sec.Done = true;   // the cut has reached the far side
                if (outOfEnergy) break;
            }
            if (advanced) s.LastAdvance = Time.time;

            // The flesh drags on the blade (and the blade on the body).
            var bodyRb = s.Npc.Muscles[cur.Body].Rb;
            var vRel = info.Rb.GetPointVelocity(refWorld) - (bodyRb.IsDead() ? Vector3.zero : bodyRb.GetPointVelocity(refWorld));
            float vw = Vector3.Dot(vRel, w);
            if (s.EnergyBlade)
            {
                // An energy blade meets no resistance; the wound smokes where it passes.
                if (advanced && Time.time > s.NextBlood)
                {
                    s.NextBlood = Time.time + 0.1f;
                    var at = o + u * Vector3.Dot(refWorld - o, u);
                    if (s.Sear) Gore.Embers(at, -w, 0.3f); else Gore.Burst(at, -w, 0.2f, s.Npc.Palette);
                }
            }
            else if (dragRate > 0f && vw > 0f)
            {
                float force = dragRate * 0.35f * Settings.SliceDrag.Value / Mathf.Max(0.1f, Settings.CutStrength.Value);
                force = Mathf.Min(force, info.Rb.mass * vw / dt * 0.5f);   // slow it, never throw it back
                var f = -w * force;
                info.Rb.AddForceAtPosition(f, refWorld, ForceMode.Force);
                // The body takes part of the push back, through its centre: pushed at the cut it spins away and the cut
                // loses its line.
                if (!bodyRb.IsDead()) bodyRb.AddForce(-f * 0.4f, ForceMode.Force);
                info.Haptic(Mathf.Clamp01(0.25f + force / 600f), 0.03f);
                if (Time.time > s.NextBlood)
                {
                    s.NextBlood = Time.time + 0.12f;
                    Gore.Burst(o + u * Vector3.Dot(refWorld - o, u), -w, 0.2f, s.Npc.Palette);
                }
            }

            if (advanced) ShowWounds(s, force: false);   // the cut opens up behind the edge as it goes
            if (!s.EnergyBlade) Guide(s, deepest != null && !deepest.Done ? deepest : null, refWorld);

            bool inside = deepest != null && !deepest.Done;
            if (inside && !s.EnergyBlade && (outOfEnergy || Time.time - s.LastAdvance > 0.3f))
            {
                // Out of energy, or the blade stopped: it stays in the cut (or, with stuck blades off, stops cutting).
                string why = outOfEnergy ? $"ran out of power ({s.Energy:F0} J swing)" : "blade stopped moving";
                if (Settings.StuckBlades.Value) BecomeStuck(s, deepest, refWorld, why);
                else Finish(s, true, why);
                return;
            }

            // Done once every section is either cut through or behind the edge already (it went past without
            // getting into it: out of the edge's reach).
            allDone = s.Sections.All(x => x.Done);
            bool pastAll = s.Sections.All(x => x.Done || x.LastDepth > x.WMax + 0.05f);
            bool idle = !inside && Time.time - s.LastAdvance > 0.4f;
            if (allDone) Finish(s, false, null);
            else if (pastAll) { CompleteNearlyThrough(s); Finish(s, true, "edge went past without reaching across"); }
            else if (idle) { CompleteNearlyThrough(s); Finish(s, true, "blade stopped moving"); }
            else if (Time.time - s.Started > (s.EnergyBlade ? 15f : 4f)) { CompleteNearlyThrough(s); Finish(s, true, "took too long"); }
        }

        /// <summary>Widens the stretch of a section the edge has passed over (measured in that section's own frame).</summary>
        private static void TrackSweep(Slice s, Section sec, Vector3 o, Vector3 u)
        {
            s.Edge.CutLine(s.Info.Root, out var a, out var b);
            float ua = Vector3.Dot(a - o, u);
            float ub = Vector3.Dot(b - o, u);
            sec.SweptMin = Mathf.Min(sec.SweptMin, Mathf.Min(ua, ub) - s.Edge.Radius);
            sec.SweptMax = Mathf.Max(sec.SweptMax, Mathf.Max(ua, ub) + s.Edge.Radius);
        }

        /// <summary>
        /// An energy blade meets no resistance, so it can be steered while it is in the body: this remembers how far
        /// its edge (over this section) is off the entry plane as it goes deeper, so the cut can follow that curve.
        /// </summary>
        private static void TrackPath(Section sec, Vector3[] pts, Vector3 o, Vector3 n, Vector3 u, float depth)
        {
            float sum = 0f;
            int count = 0;
            for (int i = 1; i < pts.Length; i++)
            {
                float pu = Vector3.Dot(pts[i] - o, u);
                if (pu < sec.UMin - 0.03f || pu > sec.UMax + 0.03f) continue;
                sum += Vector3.Dot(pts[i] - o, n);
                count++;
            }
            if (count == 0) return;
            float off = sum / count;
            var path = sec.Path ??= new List<Vector2>();
            if (path.Count == 0)
            {
                // Straight up to where the edge reached this section.
                float start = Mathf.Min(depth, sec.WMin);
                path.Add(new Vector2(start - 0.08f, off));
                path.Add(new Vector2(start - 0.03f, off));
            }
            var last = path[path.Count - 1];
            if (depth < last.x + 0.004f) return;
            // The cut stays one sheet across the body (each point of it reached once going in), so it can only turn so
            // far between two steps: about 50 degrees off the way it went in.
            float turn = (depth - last.x) * 1.2f;
            path.Add(new Vector2(depth, Mathf.Clamp(off, last.y - turn, last.y + turn)));
        }

        /// <summary>
        /// The curve an energy blade's edge took through a section, smoothed (hands shake), as a bend for its cut
        /// (<see cref="CutRegion.BendW"/>); nulls when it went straight.
        /// </summary>
        private static void Bend(Section sec, CutRegion region)
        {
            var p = sec.Path;
            if (p == null || p.Count < 4) return;
            var smooth = new List<Vector2>(p.Count);
            for (int i = 0; i < p.Count; i++)
            {
                float sum = 0f;
                int c = 0;
                for (int k = Mathf.Max(0, i - 2); k <= Mathf.Min(p.Count - 1, i + 2); k++) { sum += p[k].y; c++; }
                smooth.Add(new Vector2(p[i].x, sum / c));
            }
            var bw = new List<float>();
            var bn = new List<float>();
            foreach (var q in smooth)
                if (bw.Count == 0 || q.x - bw[bw.Count - 1] >= 0.015f) { bw.Add(q.x); bn.Add(q.y); }
            var end = smooth[smooth.Count - 1];
            if (end.x - bw[bw.Count - 1] > 0.003f) { bw.Add(end.x); bn.Add(end.y); }
            float most = 0f;
            foreach (float x in bn) most = Mathf.Max(most, Mathf.Abs(x));
            if (bw.Count < 2 || most < 0.008f) return;
            region.BendW = bw.ToArray();
            region.BendN = bn.ToArray();
        }

        /// <summary>
        /// How far the blade is from the plane it went in on: the nearest part of the edge, 0 while the edge still
        /// crosses the plane. (The contact point alone wanders off as the blade turns during a swing.)
        /// </summary>
        private static float OffPlane(Vector3[] pts, Vector3 o, Vector3 n)
        {
            float min = float.MaxValue, lo = float.MaxValue, hi = float.MinValue;
            foreach (var p in pts)
            {
                float d = Vector3.Dot(p - o, n);
                min = Mathf.Min(min, Mathf.Abs(d));
                lo = Mathf.Min(lo, d);
                hi = Mathf.Max(hi, d);
            }
            return lo <= 0f && hi >= 0f ? 0f : min;
        }

        /// <summary>
        /// When a slice ends because the blade strays, stops, has passed or is pulled out, a part it got nearly all the
        /// way through counts as cut through: the "Cut through at" setting (90% by default) of the way, or 20 points
        /// less with the power left to finish it (not when pulled back out, and not at 100%, which means only a cut
        /// that comes out the other side). A real blade would not have stopped there; what is left tears.
        /// </summary>
        private static void CompleteNearlyThrough(Slice s, bool pulledOut = false)
        {
            foreach (var sec in s.Sections)
            {
                if (sec.Done || !sec.Entered) continue;
                float depth = Mathf.Max(0.01f, sec.WMax - sec.WMin);
                float done = (sec.Cut - sec.WMin) / depth;
                float rest = (sec.WMax - sec.Cut) * sec.Rate;
                float through = Mathf.Clamp(Settings.ThroughAtPercent.Value, 50f, 100f) / 100f;
                bool enough = done >= through ||
                              (!pulledOut && through < 0.999f && done >= through - 0.2f && s.Budget >= rest);
                if (!enough) continue;
                s.Budget = Mathf.Max(0f, s.Budget - rest);
                sec.Cut = sec.WMax;
                sec.Done = true;
                s.Story.Add($"{sec.Name} counted as through ({done:P0} cut)");
            }
        }

        /// <summary>
        /// The flesh holds a steel blade to the line it is cutting along. While the edge is in a body part, a soft joint
        /// between blade and that part keeps the blade on the plane it went in on (as that plane sits on the part now)
        /// and stops it twisting out of it, but leaves it free to go deeper, to saw along its edge and to swing round
        /// within that plane, as a swing does. Within a few millimetres / degrees it does nothing; beyond that it pulls
        /// back harder the further it is pushed, and a hard enough wrench tears the blade free of its line.
        /// "Blade guidance" scales it (0: off). Energy blades are not held: they can be steered along a curve.
        /// </summary>
        private static void Guide(Slice s, Section inSec, Vector3 refWorld)
        {
            float k = Settings.BladeGuide.Value;
            if (s.Guide != null && s.Guide.IsDead())
            {
                // Broken by the physics engine: the blade was wrenched off its line.
                s.Guide = null;
                s.GuideTorn = true;
                s.Story.Add("blade wrenched off its line");
            }
            bool want = k > 0f && inSec != null && !s.GuideTorn && !s.Npc.Muscles[inSec.Body].Rb.IsDead();
            if (s.Guide != null && (!want || s.GuideBody != inSec.Body)) DropGuide(s);
            if (!want || s.Guide != null) return;
            try
            {
                s.Guide = CreateGuide(s, inSec, refWorld, k);
                s.GuideBody = inSec.Body;
            }
            catch (Exception e)
            {
                Log.Debug("guide: " + e.Message);
                s.GuideTorn = true;
            }
        }

        private static ConfigurableJoint CreateGuide(Slice s, Section sec, Vector3 refWorld, float k)
        {
            var info = s.Info;
            var root = info.Root;
            var bodyRb = s.Npc.Muscles[sec.Body].Rb;
            Frame(s, sec.Body, out var o, out var n, out var u, out var w);
            var j = info.Rb.gameObject.AddComponent<ConfigurableJoint>();
            j.autoConfigureConnectedAnchor = false;
            j.anchor = root.InverseTransformPoint(refWorld);
            j.axis = root.InverseTransformDirection(w);            // joint X: deeper into / back out of the cut
            j.secondaryAxis = root.InverseTransformDirection(u);   // joint Y: along the edge; Z: off the plane
            j.connectedBody = bodyRb;
            // On the plane itself (the edge may have drifted a little off it before this part was reached).
            j.connectedAnchor = bodyRb.transform.InverseTransformPoint(refWorld - n * Vector3.Dot(refWorld - o, n));
            j.xMotion = ConfigurableJointMotion.Free;
            j.yMotion = ConfigurableJointMotion.Free;
            j.zMotion = ConfigurableJointMotion.Limited;
            var lin = new SoftJointLimit();
            lin.limit = 0.004f;
            j.linearLimit = lin;
            var linSpring = new SoftJointLimitSpring();
            linSpring.spring = 20000f * k;
            linSpring.damper = 300f * k;
            j.linearLimitSpring = linSpring;
            // Twisting about the cut direction (the edge tilting out of the plane) and about the edge (the cut turning off
            // its line) are held; swinging round within the plane is free.
            j.angularXMotion = ConfigurableJointMotion.Limited;
            j.angularYMotion = ConfigurableJointMotion.Limited;
            j.angularZMotion = ConfigurableJointMotion.Free;
            var low = new SoftJointLimit();
            low.limit = -4f;
            j.lowAngularXLimit = low;
            var high = new SoftJointLimit();
            high.limit = 4f;
            j.highAngularXLimit = high;
            var yLimit = new SoftJointLimit();
            yLimit.limit = 4f;
            j.angularYLimit = yLimit;
            var turn = new SoftJointLimitSpring();
            turn.spring = 300f * k;
            turn.damper = 20f * k;
            j.angularXLimitSpring = turn;
            j.angularYZLimitSpring = turn;
            j.enableCollision = false;
            j.breakForce = 4000f * Mathf.Max(0.25f, k);
            j.breakTorque = 600f * Mathf.Max(0.25f, k);
            return j;
        }

        private static void DropGuide(Slice s)
        {
            if (!s.Guide.IsDead()) UnityEngine.Object.Destroy(s.Guide);
            s.Guide = null;
            s.GuideBody = -1;
        }

        private static void BecomeStuck(Slice s, Section sec, Vector3 refWorld, string why)
        {
            DropGuide(s);   // the stuck joint holds it now
            var bodyRb = s.Npc.Muscles[sec.Body].Rb;
            if (bodyRb.IsDead()) { Finish(s, true, why); return; }
            Frame(s, sec.Body, out var o, out _, out var u, out var w);
            try
            {
                s.Joint = CreateJoint(s.Info, bodyRb, refWorld, u, w);
            }
            catch (Exception e)
            {
                Log.Warn("Could not stick the blade: " + e.Message);
                Finish(s, true, why);
                return;
            }
            s.StuckIn = sec;
            s.LastU = Vector3.Dot(refWorld - o, u);
            s.Story.Add($"stuck in {sec.Name} {(sec.Cut - sec.WMin) * 100f:F0} of {(sec.WMax - sec.WMin) * 100f:F0} cm deep ({why})");
            Gore.Bleeder(s.Npc.Muscles[sec.Body].Body, refWorld, -w, 8f, 35f, s.Npc.Palette);
            Sounds.Play(Sounds.Kind.Stuck, refWorld, Mathf.Clamp01(0.4f + s.Energy / 400f));
            s.Info.Haptic(0.8f, 0.12f);
            ShowWounds(s, force: true);
        }

        private static ConfigurableJoint CreateJoint(BladeInfo info, Rigidbody bodyRb, Vector3 p, Vector3 u, Vector3 w)
        {
            var root = info.Root;
            var j = info.Rb.gameObject.AddComponent<ConfigurableJoint>();
            j.autoConfigureConnectedAnchor = false;
            j.anchor = root.InverseTransformPoint(p);
            j.axis = root.InverseTransformDirection(w);            // joint X: deeper into / back out of the cut
            j.secondaryAxis = root.InverseTransformDirection(u);   // joint Y: along the edge (sawing)
            j.connectedBody = bodyRb;
            j.connectedAnchor = bodyRb.transform.InverseTransformPoint(p);
            j.xMotion = ConfigurableJointMotion.Free;
            j.yMotion = ConfigurableJointMotion.Free;
            j.zMotion = ConfigurableJointMotion.Locked;            // the slit holds the flat of the blade
            j.angularXMotion = ConfigurableJointMotion.Locked;
            j.angularYMotion = ConfigurableJointMotion.Locked;
            j.angularZMotion = ConfigurableJointMotion.Limited;    // some levering within the cut
            var lim = new SoftJointLimit();
            lim.limit = 25f;
            j.angularZLimit = lim;
            float resist = Mathf.Max(0.05f, Settings.PushResistance.Value);
            var push = new JointDrive();
            push.positionDamper = 250f * resist;
            push.maximumForce = 1e6f;
            j.xDrive = push;
            var saw = new JointDrive();
            saw.positionDamper = 80f * resist;
            saw.maximumForce = 1e6f;
            j.yDrive = saw;
            j.enableCollision = false;
            j.breakForce = 3000f;
            j.breakTorque = 500f;
            return j;
        }

        private static void Stuck(Slice s)
        {
            // Joint broken (yanked out sideways), weapon or NPC gone: let go. Worked nearly all the way through, the
            // rest tears as it comes out (see CompleteNearlyThrough), as it does for a moving blade.
            if (s.Joint.IsDead()) { CompleteNearlyThrough(s); Finish(s, true, "yanked out"); return; }
            var sec = s.StuckIn;
            var refWorld = s.Info.Root.TransformPoint(s.RefOnBlade);
            Frame(s, sec.Body, out var o, out _, out var u, out var w);
            float d = Vector3.Dot(refWorld - o, w);
            float along = Vector3.Dot(refWorld - o, u);
            float dDepth = d - sec.LastDepth, dAlong = along - s.LastU;
            sec.LastDepth = d;
            s.LastU = along;
            TrackSweep(s, sec, o, u);

            if (d < sec.WMin - 0.03f) { CompleteNearlyThrough(s, pulledOut: true); Finish(s, true, "pulled back out"); return; }   // the way it came in

            // Pushing deeper cuts as far as the blade goes; sawing along the edge cuts a fraction of the stroke length.
            float resist = Mathf.Max(0.05f, Settings.PushResistance.Value);
            if (dDepth > 0f && d > sec.Cut) sec.Cut = Mathf.Min(d, sec.Cut + dDepth);
            sec.Cut += Mathf.Abs(dAlong) * (0.15f / resist);
            float moved = Mathf.Abs(dDepth) + Mathf.Abs(dAlong);
            if (moved > 0.0015f)
            {
                s.Info.Haptic(Mathf.Clamp01(moved * 60f) * 0.5f, 0.03f);
                if (Time.time > s.NextBlood)
                {
                    s.NextBlood = Time.time + 0.25f;
                    Gore.Burst(o + u * along, -w, 0.25f, s.Npc.Palette);
                    Sounds.Play(Sounds.Kind.Saw, refWorld, Mathf.Clamp01(moved * 80f));
                }
            }
            if (sec.Cut >= sec.WMax)
            {
                sec.Done = true;
                if (!s.Joint.IsDead()) UnityEngine.Object.Destroy(s.Joint);
                s.Joint = null;
                s.StuckIn = null;
                s.Source += ", pushed through";
                s.Info.Haptic(1f, 0.1f);
                s.LastAdvance = Time.time;
                // Carry on as a slice with no energy left: it ends once the cut has landed.
                s.Budget = 0f;
                return;
            }
            ShowWounds(s, force: false);
        }

        /// <summary>Open wounds for every section cut into but not (yet) through. Rebuilt as the cut deepens.</summary>
        private static void ShowWounds(Slice s, bool force)
        {
            if (!Settings.ShowWounds.Value) return;
            if (Settings.DeepWounds.Value)
            {
                LiveOpening(s, force);
                return;
            }
            foreach (var sec in s.Sections)
            {
                if (!sec.Entered || (sec.Severed && !sec.SeverFailed)) continue;
                if (!force && Time.time - sec.WoundTime < 0.15f) continue;
                float cut = sec.SeverFailed ? sec.WMax + 0.01f : sec.Cut;
                if (cut - sec.WMin < 0.01f) continue;
                sec.Wound = Wounds.Show(sec.Wound, s.Npc, sec.Loop, s.Region, cut, sec.SweptMin, sec.SweptMax, s.EntryPose, s.Sear);
                sec.WoundTime = Time.time;
            }
        }

        /// <summary>
        /// A section the edge has come all the way through gets severed, one at a time, each in the plane as it sits on
        /// that section's own body now (they move apart while the blade is in them) and over the stretch the edge
        /// actually passed over there.
        /// </summary>
        private static void SeverFinished(Slice s)
        {
            var sec = s.Sections.FirstOrDefault(x => x.Done && !x.Severed);
            if (sec == null || s.Npc.Busy) return;
            Frame(s, sec.Body, out var o, out var n, out var u, out var w);
            var region = new CutRegion
            {
                Origin = o.N(), Normal = n.N(), AxisU = u.N(), AxisW = w.N(),
                // Behind the entry point counts too, as far as the blade had already gone in when the hit registered.
                UMin = sec.SweptMin, UMax = sec.SweptMax, WMin = Mathf.Min(-0.05f, s.Region.WMin), Margin = 0.02f,
            };
            if (s.EnergyBlade) Bend(sec, region);
            // An earlier wound opened in the mesh where this cut goes would leave a gap in its outline: close it first.
            Wounds.CloseSlitsAlong(s.Npc, region, s.Opening);
            // Worked out under the cut's own opening (which shows until the part comes off, then makes way for the cut face).
            var attempt = CutPipeline.Prepare(s.Npc, sec.Body, region, s.Opening?.Slit);
            if (attempt == null)
            {
                // Meshes busy or still loading: try again next step, for a little while.
                if (++sec.SeverTries < 150) return;
                sec.Severed = true;
                return;
            }
            sec.Severed = true;
            // This section's own flesh: where it was when the edge went in (the plane rides on its body, so the same
            // plane coordinates). Sections sharing a body with it (the pelvis in both thighs) would otherwise drag in
            // thin slices beside a face this swing already cut, which come off as loose chunks.
            var report = attempt.Report;
            var near = report.Groups.Where(g => g.InReach && g.Area > 0f && g.Segments.Overlaps(sec.Segments)).ToList();
            var groups = near.Where(g => Within(report.Loops[g.OuterLoop].Centroid, sec.Loop, 0.03f)).ToList();
            if (groups.Count == 0) groups = near;   // it moved too far to tell: as before
            // The edge has to have swept across enough of it ("Blade must span", 80% by default).
            float span = Mathf.Clamp(Settings.SpanPercent.Value, 10f, 100f) / 100f;
            var sever = groups.Where(g => g.Coverage >= span - 1e-4f).Select(g => g.Id).ToList();
            if (sever.Count == 0)
            {
                // Through, but the edge never spanned it: what it went through stays as a wound.
                sec.SeverFailed = true;
                s.Story.Add($"went through {sec.Name} but only reached across " +
                            $"{(groups.Count > 0 ? groups.Max(g => g.Coverage) : 0f):P0} of it (blade too short there)");
                ShowWounds(s, force: true);
                return;
            }
            if (region.Bent) s.Story.Add($"{sec.Name} cut along a curve ({region.BendW.Length} points)");
            CutPipeline.Submit(attempt, sever, w * (s.EnergyBlade ? 0.4f : 1.5f), s.Source + (region.Bent ? ", curved" : ""), s.Sear);
            Wounds.Remove(sec.Wound);   // the cut face takes its place
            sec.Wound = null;
            s.LiveKey = float.NaN;      // what is left open is shown again once the part is off
            // Other finished sections this cut already went through are done too (cutting them again in their own,
            // slightly different plane would only shave off slivers).
            var cutSegs = new HashSet<int>(groups.Where(g => sever.Contains(g.Id)).SelectMany(g => g.Segments));
            foreach (var x in s.Sections)
            {
                if (x == sec || !x.Done || x.Severed || !x.Segments.Overlaps(cutSegs)) continue;
                x.Severed = true;
                Wounds.Remove(x.Wound);
                x.Wound = null;
            }
            var at = s.Info.Root.TransformPoint(s.RefOnBlade);
            if (s.EnergyBlade) Sounds.Play(Sounds.Kind.Sizzle, at, 1f);
            else
            {
                Sounds.Play(Sounds.Kind.Sever, at, Mathf.Clamp01(0.5f + s.Energy / 400f));
                if (sec.HasBone) Sounds.Play(Sounds.Kind.Bone, at, 0.8f);
            }
            s.Info.Haptic(1f, 0.08f);
        }

        /// <summary>Whether a plane point lies over a section's outline (its bounding box, grown by <paramref name="margin"/>).</summary>
        private static bool Within(System.Numerics.Vector2 q, SectionLoop loop, float margin) =>
            q.X >= loop.Min.X - margin && q.X <= loop.Max.X + margin && q.Y >= loop.Min.Y - margin && q.Y <= loop.Max.Y + margin;

        private static void End(Slice s, bool wound)
        {
            if (!Active.Remove(s)) return;
            if (!s.Joint.IsDead()) UnityEngine.Object.Destroy(s.Joint);
            DropGuide(s);
            if (s.Opening != null) s.Opening.Live = false;
            // Let the blade finish leaving the body before it can collide with it again.
            Collisions.Release(s.Ignored, 0.15f, 2.5f);
            // An energy blade still in the body would start a new slice at once: give it a moment.
            if (s.EnergyBlade) Blades.Rest(s.Info, s.Npc, 0.25f);
            if (wound && s.Npc.Valid && s.Npc.Generation == s.Generation)
            {
                ShowWounds(s, force: true);   // the opening's final shape, as deep as the blade got
                var open = s.Sections.Where(x => x.Entered && (!x.Severed || x.SeverFailed) && x.Cut - x.WMin >= 0.01f).ToList();
                if (!s.Sear)
                    foreach (var x in open) BleedFrom(s, x);
                if (open.Count == 0 && !s.Sear && s.Sections.Any(x => x.Entered))
                {
                    var sec = s.Sections.First(x => x.Entered);
                    var body = s.Npc.Muscles[sec.Body].Body;
                    if (!body.IsDead())
                    {
                        Frame(s, sec.Body, out var o, out _, out _, out var w);
                        Gore.Burst(o, -w, 0.6f, s.Npc.Palette);
                        Gore.Bleeder(body, o, -w, 5f, 25f, s.Npc.Palette);
                    }
                }
            }
            // Say why a swing that got into the body did not take anything off.
            if (!s.Sections.Any(x => x.Severed && !x.SeverFailed) && s.Sections.Any(x => x.Entered) && s.Story.Count > 0)
            {
                var deepest = s.Sections.Where(x => x.Entered).OrderByDescending(x => (x.Cut - x.WMin) / Mathf.Max(0.01f, x.WMax - x.WMin)).First();
                Log.Msg($"Slice into {s.Npc.Name} ({s.Source}) did not cut anything off: {string.Join(", then ", s.Story)}. " +
                        $"Deepest: {deepest.Name}, {(deepest.Cut - deepest.WMin) * 100f:F0} of {(deepest.WMax - deepest.WMin) * 100f:F0} cm.");
            }
        }

        /// <summary>
        /// The cut as far as it has got: every section the edge is in (or went through without taking it off) opened in
        /// the body's mesh as deep as it has cut there, so you see the cut being made behind the edge. Asked for again as
        /// the edge gets deeper; the new shape is worked out from the uncut meshes and swapped in when ready (see
        /// <see cref="Wounds.OpenSlit"/>). Not while a part is about to come off: that cut goes first.
        /// </summary>
        private static void LiveOpening(Slice s, bool force)
        {
            if (!force && Time.time < s.NextLive) return;
            if (s.Sections.Any(x => x.Done && !x.Severed)) return;
            var parts = new List<(HashSet<int>, float)>();
            Section frame = null;
            float deepest = 0f, key = 0f, uMin = float.MaxValue, uMax = float.MinValue;
            foreach (var x in s.Sections)
            {
                if (!x.Entered || (x.Severed && !x.SeverFailed)) continue;
                float cut = x.SeverFailed ? x.WMax + 0.01f : x.Cut;
                if (cut - x.WMin < 0.01f) continue;
                parts.Add((x.Segments, cut));
                deepest = Mathf.Max(deepest, cut - x.WMin);
                key += cut + (x.SweptMax - x.SweptMin);
                uMin = Mathf.Min(uMin, x.SweptMin);
                uMax = Mathf.Max(uMax, x.SweptMax);
                if (frame == null || (!x.Done && frame.Done)) frame = x;   // the section the edge is in carries it
            }
            if (parts.Count == 0) return;
            // Only once it has got noticeably deeper or longer since the last one.
            if (!force && !float.IsNaN(s.LiveKey) && Mathf.Abs(key - s.LiveKey) < 0.008f) return;
            s.LiveKey = key;
            // At most ~12% of the main thread: a cheap body updates every 50 ms, Ford's face (many expressions) less often.
            s.NextLive = Time.time + Mathf.Clamp(s.Npc.WoundApplyMs * 0.008f, 0.05f, 0.4f);
            Frame(s, frame.Body, out var o, out var n, out var u, out var w);
            var region = new CutRegion
            {
                Origin = o.N(), Normal = n.N(), AxisU = u.N(), AxisW = w.N(),
                UMin = uMin, UMax = uMax, WMin = Mathf.Min(-0.05f, s.Region.WMin), Margin = 0.02f,
            };
            if (s.EnergyBlade) Bend(frame, region);
            var spec = new SlitSpec
            {
                Depth = parts.Max(x => x.Item2),
                // A deeper cut gapes wider; an energy blade burns a wide channel.
                Opening = s.EnergyBlade ? 0.008f : Mathf.Clamp(0.12f * deepest, 0.003f, 0.008f),
                Taper = Mathf.Max(0.02f, 0.5f * deepest),
            };
            s.Opening = Wounds.OpenSlit(s.Npc, frame.Body, region, spec, parts, s.Sear, s.Opening, $"{frame.Name}, {deepest * 100f:F0} cm deep");
            if (s.Opening != null) s.Opening.Live = true;
        }

        /// <summary>Blood from a section cut into but not off, more for a deeper cut.</summary>
        private static void BleedFrom(Slice s, Section sec)
        {
            float strength = Mathf.Clamp01((sec.Cut - sec.WMin) / Mathf.Max(0.01f, sec.WMax - sec.WMin));
            if (sec.Wound != null) { Wounds.Bleed(sec.Wound, strength); return; }
            var body = s.Npc.Muscles[sec.Body].Body;
            if (body.IsDead()) return;
            Frame(s, sec.Body, out var o, out _, out var u, out var w);
            float mid = (Mathf.Max(sec.UMin, sec.SweptMin) + Mathf.Min(sec.UMax, sec.SweptMax)) * 0.5f;
            Gore.Bleeder(body, o + u * mid + w * sec.WMin, -w, 4f + 6f * strength, 12f + 30f * strength, s.Npc.Palette);
        }

        public static void ReleaseAll(NpcBody npc)
        {
            foreach (var s in Active.Where(x => x.Npc == npc).ToList()) End(s, false);
        }

        public static void ReleaseAll()
        {
            foreach (var s in Active.ToList()) End(s, false);
        }

        public static void Clear() => Active.Clear();
    }
}
