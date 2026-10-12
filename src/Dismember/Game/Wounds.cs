using System;
using System.Collections.Generic;
using System.Linq;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using Dismember.Core;

namespace Dismember.Game
{
    /// <summary>
    /// Open wounds where a blade cut into a body. With "Deep wounds" on, the wound is opened in the body's own mesh
    /// (<see cref="CutSolver.ExecuteSlit"/>): the skin parts along the cut and flesh walls line it, as deep as the blade
    /// has got, so you see the cut being made behind the edge and can look into it afterwards (and through a stab that
    /// went all the way). Each new shape is worked out on the cutter thread from the meshes as they were before the
    /// wound, while the old shape keeps showing, and the two swap in one frame. A part that comes off takes the
    /// opening's place with its cut face. Otherwise: a strip of raw flesh along the line the edge went in (built by
    /// <see cref="GashBuilder"/>), skinned to the ragdoll bodies so it moves with them.
    /// </summary>
    internal static class Wounds
    {
        internal sealed class Wound
        {
            public NpcBody Npc;
            public int Generation;
            public GameObject Go;
            public SkinnedMeshRenderer Renderer;
            public Mesh Mesh;
            public float Depth = float.MinValue;
            public float Length;
            public Vector3 Center, Normal;   // world, when built
            public Transform Anchor;        // a body the wound rides on (for blood)
            public float Created;
            public CutApplier.SlitLayer Slit;   // opened in the body's mesh (the strip is gone then)
            public Transform SlitBody;          // ... where, as the corners of the opening on this body
            public Vector3[] SlitCorners;
            public SlitRequest Next;            // the newest shape wanted for the opening, not started yet
            public bool InFlight;               // ... a shape being worked out on the cutter thread
            public bool Live;                   // a blade is still in it: it keeps changing
            public bool SlitPending => Next != null || InFlight;
        }

        /// <summary>A shape for a wound's opening, waiting for the NPC to be free (a cut in flight, meshes loading).</summary>
        internal sealed class SlitRequest
        {
            public NpcBody Npc;
            public int Generation;
            public int Body;                         // segment whose motion carries the cut surface
            public Vector3 Origin, Normal, AxisU, AxisW;   // the cut surface, local to that body
            public float UMin, UMax, WMin;
            public float[] BendW, BendN;
            /// <summary>How deep it goes in the sections through each set of segments (null: Spec.Depth wherever it reaches).</summary>
            public List<(HashSet<int> Segments, float Depth)> Parts;
            public SlitSpec Spec;
            public bool Seared;
            public float Since;
            public string What;
        }

        private const int MaxSlitsPerNpc = 10;

        private static readonly List<Wound> All = new List<Wound>();
        private const int MaxPerNpc = 16;
        private const float HalfWidth = 0.006f;

        public static int Count => All.Count;
        public static int SlitCount => All.Count(w => w.Slit != null && !w.Slit.Reverted);

        /// <summary>Nothing to show and nothing coming.</summary>
        private static bool Empty(Wound w) => w.Slit == null && !w.SlitPending && w.Go.IsDead();

        /// <summary>
        /// Asks for a wound to be opened in the body's mesh, or <paramref name="into"/>'s opening to be reshaped (it keeps
        /// showing until the new shape is ready, then they swap in the same frame). <paramref name="region"/> (world, now)
        /// is the surface the blade went along; its U/W limits and the depths (<paramref name="parts"/> per section, else
        /// <paramref name="spec"/>'s) say how much of it. Returns the wound (null: none could be made).
        /// </summary>
        public static Wound OpenSlit(NpcBody npc, int body, CutRegion region, SlitSpec spec, List<(HashSet<int>, float)> parts, bool seared,
                                     Wound into, string what)
        {
            if (!Settings.ShowWounds.Value || !Settings.DeepWounds.Value || npc == null || !npc.Valid) return into;
            var t = npc.Muscles[body].Body;
            if (t.IsDead()) return into;
            if (into != null && (!All.Contains(into) || into.Generation != npc.Generation)) into = null;
            if (into == null)
            {
                if (All.Count(w => w.Npc == npc && ((w.Slit != null && !w.Slit.Reverted) || w.SlitPending)) >= MaxSlitsPerNpc) return null;
                into = new Wound { Npc = npc, Generation = npc.Generation, Created = Time.time, Anchor = t };
                All.Add(into);
            }
            into.Next = new SlitRequest
            {
                Npc = npc,
                Generation = npc.Generation,
                Body = body,
                Origin = t.InverseTransformPoint(region.Origin.U()),
                Normal = t.InverseTransformDirection(region.Normal.U()),
                AxisU = t.InverseTransformDirection(region.AxisU.U()),
                AxisW = t.InverseTransformDirection(region.AxisW.U()),
                UMin = region.UMin, UMax = region.UMax, WMin = region.WMin,
                BendW = region.BendW, BendN = region.BendN,
                Parts = parts,
                Spec = spec,
                Seared = seared,
                Since = Time.time,
                What = what,
            };
            return into;
        }

        private static CutRegion RegionOf(SlitRequest r, Transform t) => new CutRegion
        {
            Origin = t.TransformPoint(r.Origin).N(),
            Normal = t.TransformDirection(r.Normal).normalized.N(),
            AxisU = t.TransformDirection(r.AxisU).normalized.N(),
            AxisW = t.TransformDirection(r.AxisW).normalized.N(),
            UMin = r.UMin, UMax = r.UMax, WMin = r.WMin, Margin = 0.02f,
            BendW = r.BendW, BendN = r.BendN,
        };

        /// <summary>Main thread, every frame: starts the next waiting opening once its NPC is free.</summary>
        public static void Update()
        {
            // Openings a cut has taken the place of are gone; so is a finished wound with nothing left to show.
            for (int i = All.Count - 1; i >= 0; i--)
            {
                var w = All[i];
                if (w.Slit != null && w.Slit.Reverted) w.Slit = null;
                if (!w.Live && Empty(w) && w.Go == null && w.Renderer == null) All.RemoveAt(i);
            }
            for (int i = 0; i < All.Count; i++)
            {
                var w = All[i];
                var r = w.Next;
                if (r == null || w.InFlight) continue;
                var npc = r.Npc;
                if (npc == null || !npc.Valid || npc.Generation != r.Generation || npc.Muscles[r.Body].Body.IsDead() || Time.time - r.Since > 6f)
                {
                    w.Next = null;
                    if (Empty(w)) All.RemoveAt(i--);
                    continue;
                }
                if (npc.Busy) continue;
                if (w.Slit != null && w.Slit.Reverted) w.Slit = null;   // a cut took its place
                if (w.Slit != null && !w.Slit.Revertible)
                {
                    // Something else cut through these meshes since: this opening stays as it is.
                    w.Next = null;
                    continue;
                }
                var t = npc.Muscles[r.Body].Body;
                // Worked out from the meshes without this wound's current opening, which keeps showing meanwhile. The
                // analysis and the choice of sections happen on the cutter thread.
                var attempt = CutPipeline.Prepare(npc, r.Body, RegionOf(r, t), w.Slit, analyze: false);
                if (attempt == null) continue;
                w.Next = null;
                w.InFlight = true;
                float deepest = float.MinValue;
                CutPipeline.SubmitSlit(attempt, report => Pick(r, report, out deepest), r.Seared, layer => Opened(w, r, layer, t, deepest));
                return;   // one per frame
            }
        }

        /// <summary>The sections an opening goes into, each with its depth (cutter thread).</summary>
        private static (List<int>, SlitSpec) Pick(SlitRequest r, CrossSectionReport report, out float deepest)
        {
            var spec = new SlitSpec { Depth = r.Spec.Depth, Opening = r.Spec.Opening, Taper = r.Spec.Taper, GroupDepth = new Dictionary<int, float>() };
            var groups = new List<int>();
            deepest = float.MinValue;
            foreach (var g in report.Groups)
            {
                if (!g.InReach || g.Area <= 0f) continue;
                float depth = r.Parts == null ? r.Spec.Depth : float.MinValue;
                if (r.Parts != null)
                    foreach (var (segs, d) in r.Parts)
                        if (g.Segments.Overlaps(segs)) depth = Math.Max(depth, d);
                if (depth <= g.WMin) continue;
                groups.Add(g.Id);
                spec.GroupDepth[g.Id] = depth;
                deepest = Math.Max(deepest, depth);
            }
            return (groups, spec);
        }

        private static void Opened(Wound w, SlitRequest r, CutApplier.SlitLayer layer, Transform body, float depth)
        {
            w.InFlight = false;
            if (w.Slit != null && w.Slit.Reverted) w.Slit = null;   // swapped out for this one (or a cut took it)
            if (layer == null)
            {
                if (Empty(w)) All.Remove(w);
                return;
            }
            if (!All.Contains(w) || w.Generation != r.Npc.Generation)
            {
                layer.Revert();   // the wound was closed while this was being worked out
                return;
            }
            // Where the opening is (for later cuts that pass through it), on the body it rides on.
            var local = new CutRegion
            {
                Origin = r.Origin.N(), Normal = r.Normal.normalized.N(), AxisU = r.AxisU.normalized.N(), AxisW = r.AxisW.normalized.N(),
                BendW = r.BendW, BendN = r.BendN,
            };
            var corners = new[]
            {
                new System.Numerics.Vector2(r.UMin, r.WMin), new System.Numerics.Vector2(r.UMax, r.WMin),
                new System.Numerics.Vector2(r.UMin, depth), new System.Numerics.Vector2(r.UMax, depth),
                new System.Numerics.Vector2((r.UMin + r.UMax) * 0.5f, (r.WMin + depth) * 0.5f),
            }.Select(q => local.To3D(q).U()).ToArray();
            bool first = w.Slit == null;
            w.Slit = layer;
            w.SlitBody = body;
            w.SlitCorners = corners;
            w.Anchor = body;
            w.Center = body.TransformPoint(corners[4]);
            // The opening replaces the strip.
            if (!w.Go.IsDead()) UnityEngine.Object.Destroy(w.Go);
            if (!w.Mesh.IsDead()) UnityEngine.Object.Destroy(w.Mesh);
            w.Go = null;
            w.Renderer = null;
            w.Mesh = null;
            if (first || !w.Live) Log.Debug($"wound in {r.Npc.Name} opened ({r.What}), {depth * 100f:F0} cm along the cut");
        }

        /// <summary>
        /// A new cut is about to go through <paramref name="region"/> (world, now): wounds opened in the mesh that it
        /// passes through are closed first, so it cuts whole skin (the cut face takes their place). <paramref name="except"/>
        /// is the cut's own wound, which the cut is worked out underneath instead (see <see cref="CutPipeline.Prepare"/>).
        /// </summary>
        public static void CloseSlitsAlong(NpcBody npc, CutRegion region, Wound except = null)
        {
            for (int i = All.Count - 1; i >= 0; i--)
            {
                var w = All[i];
                if (w == except || w.Npc != npc || w.Slit == null || w.SlitBody.IsDead()) continue;
                float lo = float.MaxValue, hi = float.MinValue;
                bool inStrip = false;
                foreach (var c in w.SlitCorners)
                {
                    var p = w.SlitBody.TransformPoint(c).N();
                    float d = region.SignedDistance(p);
                    lo = Mathf.Min(lo, d);
                    hi = Mathf.Max(hi, d);
                    inStrip |= region.InStrip(region.To2D(p));
                }
                if (!inStrip || lo > 0.02f || hi < -0.02f) continue;
                if (w.Slit.Revert())
                {
                    All.RemoveAt(i);
                    w.Next = null;
                    Log.Debug($"wound in {npc.Name} closed for a cut through it");
                }
            }
        }

        /// <summary>
        /// Shows the wound for a section cut into up to <paramref name="depth"/> (plane coordinates of
        /// <paramref name="region"/>, which is where the section outline was traced, with the bodies as in
        /// <paramref name="pose"/>), or grows <paramref name="w"/> to that depth. Returns the wound (or null).
        /// </summary>
        public static Wound Show(Wound w, NpcBody npc, SectionLoop loop, CutRegion region, float depth, float uMin, float uMax, PoseSnapshot pose,
                                 bool seared = false)
        {
            if (!Settings.ShowWounds.Value || npc == null || !npc.Valid || loop == null) return w;
            if (w != null && (w.Slit != null || w.SlitPending)) return w;   // being opened in the mesh
            if (w != null && (w.Go.IsDead() || w.Generation != npc.Generation)) { Remove(w); w = null; }
            if (w != null && depth < w.Depth + 0.01f) return w;   // has not grown enough to bother
            try
            {
                var gash = GashBuilder.Build(loop, depth, uMin, uMax, region.Normal, HalfWidth, seg => BodyWorld(npc, pose, seg));
                if (gash == null || gash.Length < 0.015f) return w;
                if (gash.BoneSegments.Any(s => npc.Muscles[s].Body.IsDead())) return w;
                var mat = CutApplier.CapMaterialFor(npc, seared);
                if (mat == null) return w;
                gash.Mesh.Colors = new System.Numerics.Vector4[gash.Mesh.VertexCount];
                Array.Fill(gash.Mesh.Colors, System.Numerics.Vector4.One);
                var mesh = MeshFactory.ToUnity(gash.Mesh, "DismemberGash");
                var bones = gash.BoneSegments.Select(s => npc.Muscles[s].Body).ToArray();

                if (w == null)
                {
                    var go = new GameObject("Dismember_Wound");
                    go.transform.SetParent(npc.Root.transform, false);
                    go.layer = npc.RenderLayer;
                    var smr = go.AddComponent<SkinnedMeshRenderer>();
                    smr.updateWhenOffscreen = true;
                    smr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    smr.sharedMaterials = new Il2CppReferenceArray<Material>(new[] { mat });
                    npc.Track(go);
                    w = new Wound { Npc = npc, Generation = npc.Generation, Go = go, Renderer = smr, Created = Time.time };
                    All.Add(w);
                    Trim(npc);
                }
                var old = w.Mesh;
                w.Renderer.bones = new Il2CppReferenceArray<Transform>(bones);
                w.Renderer.rootBone = bones[0];
                w.Renderer.sharedMesh = mesh;
                w.Mesh = mesh;
                npc.Track(mesh);
                if (!old.IsDead()) UnityEngine.Object.Destroy(old);
                w.Depth = depth;
                w.Length = gash.Length;
                int anchorSeg = gash.BoneSegments[0];
                w.Anchor = npc.Muscles[anchorSeg].Body;
                w.Center = pose.Point(npc, anchorSeg, gash.Center.U());
                w.Normal = pose.Direction(npc, anchorSeg, gash.Normal.U());
                return w;
            }
            catch (Exception e)
            {
                Log.Debug("wound: " + e.Message);
                return w;
            }
        }

        /// <summary>World matrix of a segment's body when the outline was traced.</summary>
        private static System.Numerics.Matrix4x4 BodyWorld(NpcBody npc, PoseSnapshot pose, int seg)
        {
            var b = npc.Muscles[seg].Body;
            var scale = b.IsDead() ? Vector3.one : b.lossyScale;
            return Matrix4x4.TRS(pose.Pos[seg], pose.Rot[seg], scale).N();
        }

        /// <summary>Bleeds from a fresh wound, more for a longer and deeper one.</summary>
        public static void Bleed(Wound w, float strength)
        {
            if (w == null || w.Anchor.IsDead()) return;
            Gore.Bleeder(w.Anchor, w.Center, w.Normal, 4f + 6f * strength, 12f + 30f * strength, w.Npc.Palette);
        }

        public static void Remove(Wound w)
        {
            if (w == null) return;
            All.Remove(w);
            w.Next = null;
            w.Slit?.Revert();
            if (!w.Go.IsDead()) UnityEngine.Object.Destroy(w.Go);
            if (!w.Mesh.IsDead()) UnityEngine.Object.Destroy(w.Mesh);
        }

        /// <summary>Oldest wounds go first once an NPC has too many.</summary>
        private static void Trim(NpcBody npc)
        {
            // Only strips: wounds opened in the mesh stay (there are few of them, see MaxSlitsPerNpc).
            var mine = All.Where(x => x.Npc == npc && x.Slit == null && !x.SlitPending).ToList();
            for (int i = 0; i < mine.Count - MaxPerNpc; i++) Remove(mine[i]);
        }

        /// <summary>Forget wounds whose NPC was put back together, despawned or unloaded.</summary>
        public static void Sweep()
        {
            for (int i = All.Count - 1; i >= 0; i--)
            {
                var w = All[i];
                if ((w.Slit == null && !w.SlitPending && w.Go.IsDead()) || w.Npc == null || w.Generation != w.Npc.Generation) All.RemoveAt(i);
            }
        }
    }
}
