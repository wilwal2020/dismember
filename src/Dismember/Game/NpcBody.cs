using System;
using System.Collections.Generic;
using System.Linq;
using Il2CppSLZ.Marrow.AI;
using Il2CppSLZ.Marrow.Pool;
using Il2CppSLZ.Marrow.PuppetMasta;
using UnityEngine;
using UnityEngine.Rendering;
using Dismember.Core;
using NMatrix = System.Numerics.Matrix4x4;
using NVector3 = System.Numerics.Vector3;

namespace Dismember.Game
{
    internal sealed class MuscleInfo
    {
        public int Index;
        public Muscle Muscle;
        public Transform Body;
        public Transform Target;
        public Rigidbody Rb;
        public ConfigurableJoint Joint;
        public Collider[] Colliders = Array.Empty<Collider>();
        public int Parent = -1;
        public SegmentKind Kind;
        public int StunGroup = -1;   // SubBehaviourHealth.StunGroup: Spine, ArmLf, ArmRt, LegLf, LegRt, Head
        public string Name;
    }

    internal sealed class RendererInfo
    {
        public SkinnedMeshRenderer Renderer;
        public bool Original;            // belongs to the NPC prefab (restored), otherwise created by us (destroyed)
        public bool IsCap;
        public Mesh OriginalMesh;
        public MeshData OriginalData;
        public MeshData Data;            // current geometry
        public Transform[] Bones;
        public int[] BoneSegment;
        public int[] SegmentBone;
        public bool AllowCaps;
        public bool[] SubmeshCaps;       // per submesh: opaque flesh that gets caps (null = all)
        public bool Unavailable;         // mesh data could not be found
        public int PieceSegment;         // any segment of the piece this renderer rides on
        public SkinInstance Skin;
        public Material[] Materials;

        public bool Active => !Renderer.IsDead() && Renderer.enabled && Renderer.gameObject.activeInHierarchy;
    }

    /// <summary>
    /// Everything the mod knows and has changed about one NPC instance. Every change is recorded in
    /// <see cref="Undo"/> so a pooled NPC can be put back exactly as it was before it is reused.
    /// </summary>
    internal sealed class NpcBody
    {
        public IntPtr Key;
        public AIBrain Brain;
        public PuppetMaster Puppet;
        public SubBehaviourHealth Health;
        public GameObject Root;
        public string Name;
        public MuscleInfo[] Muscles;
        public int[] OriginalParents;
        public int[] Parents;                 // current connectivity: freed joints become -1
        public readonly List<RendererInfo> Renderers = new List<RendererInfo>();
        public readonly List<Renderer> LodOnlyRenderers = new List<Renderer>();
        public readonly List<LODGroup> LodGroups = new List<LODGroup>();
        public readonly HashSet<int> Neutralized = new HashSet<int>();
        public readonly HashSet<int> FreedJoints = new HashSet<int>();
        public readonly List<Action> Undo = new List<Action>();
        public readonly List<GameObject> FreeChunks = new List<GameObject>();
        /// <summary>Colliders we added for flesh chunks riding on other bodies.</summary>
        public readonly List<Collider> ExtraColliders = new List<Collider>();
        public CutMaterial Material = CutMaterial.Flesh;
        public GorePalette Palette = GorePalette.Flesh;
        public int Cuts;
        public bool LodsCollapsed;
        public bool LethalDone;
        public float BleedUntil;
        public float BleedPerSecond;
        /// <summary>A cut for this NPC is being computed in the background; it must not be cut again until it lands.</summary>
        public bool Busy;
        public float BusySince;
        /// <summary>How long the last wound update took on the main thread (paces the cut opening up as it is made).</summary>
        public float WoundApplyMs;
        /// <summary>Bumped on every restore so results computed for the previous life are dropped.</summary>
        public int Generation;
        public readonly Dictionary<IntPtr, int> MuscleByRigidbody = new Dictionary<IntPtr, int>();
        private readonly Dictionary<IntPtr, int> _segByTransform = new Dictionary<IntPtr, int>();

        public int Root0 => Array.IndexOf(OriginalParents, -1) is int r && r >= 0 ? r : 0;
        public bool Modified => Undo.Count > 0;

        /// <summary>The layer the NPC's own meshes are drawn on (not the ragdoll bodies' collision layer).</summary>
        public int RenderLayer
        {
            get
            {
                foreach (var r in Renderers)
                    if (r.Original && !r.Renderer.IsDead()) return r.Renderer.gameObject.layer;
                return 0;
            }
        }

        public static NpcBody Create(AIBrain brain)
        {
            var pm = brain.puppetMaster;
            if (pm.IsDead() || pm.muscles == null || pm.muscles.Length == 0) return null;
            var npc = new NpcBody
            {
                Key = brain.Pointer,
                Brain = brain,
                Puppet = pm,
                Health = brain.behaviour != null ? brain.behaviour.health : null,
            };
            var poolee = brain.GetComponentInParent<Poolee>();
            npc.Root = poolee != null ? poolee.gameObject : brain.gameObject;
            npc.Name = npc.Root.name;

            int n = pm.muscles.Length;
            npc.Muscles = new MuscleInfo[n];
            for (int i = 0; i < n; i++)
            {
                var m = pm.muscles[i];
                var info = new MuscleInfo { Index = i, Muscle = m, Name = m.name, Body = m.transform, Target = m.target };
                if (info.Body.IsDead() && m.marrowBody != null) info.Body = m.marrowBody.transform;   // muscle not initiated yet
                info.Kind = (SegmentKind)(int)m.props.group;
                if (!info.Body.IsDead())
                {
                    info.Rb = info.Body.GetComponent<Rigidbody>();
                    info.Joint = info.Body.GetComponent<ConfigurableJoint>();
                    var cols = m.colliders;
                    info.Colliders = cols != null && cols.Length > 0 ? cols.Where(c => !c.IsDead()).ToArray() : OwnColliders(info.Body, info.Rb);
                }
                npc.Muscles[i] = info;
                if (!info.Rb.IsDead()) npc.MuscleByRigidbody[info.Rb.Pointer] = i;
                if (!info.Body.IsDead()) npc._segByTransform[info.Body.Pointer] = i;
            }
            for (int i = 0; i < n; i++)
            {
                if (!npc.Muscles[i].Target.IsDead() && !npc._segByTransform.ContainsKey(npc.Muscles[i].Target.Pointer))
                    npc._segByTransform[npc.Muscles[i].Target.Pointer] = i;
                var cb = npc.Muscles[i].Joint.IsDead() ? null : npc.Muscles[i].Joint.connectedBody;
                npc.Muscles[i].Parent = cb != null && npc.MuscleByRigidbody.TryGetValue(cb.Pointer, out int p) && p != i ? p : -1;
            }
            npc.OriginalParents = npc.Muscles.Select(m => m.Parent).ToArray();
            npc.Parents = (int[])npc.OriginalParents.Clone();

            try
            {
                var stun = npc.Health?.muscles;
                if (stun != null && stun.Length == n)
                    for (int i = 0; i < n; i++) npc.Muscles[i].StunGroup = (int)stun[i];
            }
            catch { /* optional */ }

            // Look and toughness.
            string lname = npc.Name.ToLowerInvariant();
            bool organic = true;
            try { organic = npc.Health == null || npc.Health.isOrganic; } catch { }
            if (lname.Contains("skeleton")) { npc.Material = CutMaterial.Skeleton; npc.Palette = GorePalette.Bone; }
            else if (!organic || lname.Contains("omni") || lname.Contains("turret")) { npc.Material = CutMaterial.Robot; npc.Palette = GorePalette.Robot; }
            else if (lname.Contains("null") || lname.Contains("void")) { npc.Palette = GorePalette.Void; }

            npc.CollectRenderers();
            return npc;
        }

        /// <summary>Colliders belonging to one ragdoll body (ragdolls are nested, so children may be other bodies).</summary>
        private static Collider[] OwnColliders(Transform body, Rigidbody rb)
        {
            var all = body.GetComponentsInChildren<Collider>(true);
            if (rb.IsDead()) return all.ToArray();
            var list = new List<Collider>();
            foreach (var c in all)
            {
                if (c.IsDead() || c.isTrigger) continue;
                var owner = c.GetComponentInParent<Rigidbody>();
                if (owner != null && owner.Pointer == rb.Pointer) list.Add(c);
            }
            return list.ToArray();
        }

        private void CollectRenderers()
        {
            var lod0 = new HashSet<IntPtr>();
            var lodAny = new HashSet<IntPtr>();
            foreach (var g in Root.GetComponentsInChildren<LODGroup>(true))
            {
                LodGroups.Add(g);
                var lods = g.GetLODs();
                for (int l = 0; l < lods.Length; l++)
                {
                    var rs = lods[l].renderers;
                    if (rs == null) continue;
                    foreach (var r in rs)
                    {
                        if (r.IsDead()) continue;
                        lodAny.Add(r.Pointer);
                        if (l == 0) lod0.Add(r.Pointer);
                    }
                }
            }

            foreach (var smr in Root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (smr.IsDead() || smr.sharedMesh == null) continue;
                if (lodAny.Contains(smr.Pointer) && !lod0.Contains(smr.Pointer))
                {
                    LodOnlyRenderers.Add(smr);
                    continue;
                }
                var bones = smr.bones;
                if (bones == null || bones.Length == 0) continue;
                var ri = new RendererInfo
                {
                    Renderer = smr,
                    Original = true,
                    OriginalMesh = smr.sharedMesh,
                    Bones = bones.ToArray(),
                    Materials = smr.sharedMaterials.ToArray(),
                };
                if (!MapBones(ri)) continue;
                string n = (smr.name + " " + smr.sharedMesh.name).ToLowerInvariant();
                bool hairLike = n.Contains("hair") || n.Contains("lash") || n.Contains("brow") || n.Contains("shadow");
                // Flesh is decided per submesh: Ford's body has an alpha-cutout zipper and his face transparent
                // corneas, and those must not stop the skin around them from being capped.
                int subs = smr.sharedMesh.subMeshCount;
                ri.SubmeshCaps = new bool[subs];
                for (int s = 0; s < subs; s++)
                {
                    var mat = s < ri.Materials.Length ? ri.Materials[s] : null;
                    ri.SubmeshCaps[s] = !mat.IsDead() && mat.renderQueue < 2450;
                }
                ri.AllowCaps = !hairLike && smr.shadowCastingMode != ShadowCastingMode.ShadowsOnly && ri.SubmeshCaps.Any(c => c);
                ri.PieceSegment = Root0;
                Renderers.Add(ri);
            }
        }

        /// <summary>Maps every renderer bone to the muscle it moves with (walking up the hierarchy).</summary>
        public bool MapBones(RendererInfo ri)
        {
            int n = Muscles.Length;
            ri.BoneSegment = new int[ri.Bones.Length];
            ri.SegmentBone = Enumerable.Repeat(-1, n).ToArray();
            int mapped = 0;
            for (int b = 0; b < ri.Bones.Length; b++)
            {
                int seg = -1;
                var t = ri.Bones[b];
                if (!t.IsDead() && _segByTransform.TryGetValue(t.Pointer, out int direct))
                {
                    seg = direct;
                    // Prefer the physics body itself as the segment's bone.
                    if (ri.SegmentBone[seg] < 0 || t.Pointer == Muscles[seg].Body.Pointer) ri.SegmentBone[seg] = b;
                }
                else
                {
                    int guard = 0;
                    while (!t.IsDead() && guard++ < 64)
                    {
                        if (_segByTransform.TryGetValue(t.Pointer, out seg)) break;
                        seg = -1;
                        t = t.parent;
                    }
                }
                ri.BoneSegment[b] = seg;
                if (seg >= 0) mapped++;
            }
            return mapped > 0;
        }

        /// <summary>Requests mesh data for all renderers. Returns true when everything that can load has loaded.</summary>
        public bool EnsureMeshes()
        {
            bool allDone = true;
            foreach (var ri in Renderers)
            {
                if (ri.Data != null || ri.Unavailable) continue;
                var md = MeshLibrary.TryGet(ri.OriginalMesh, out bool pending);
                if (md != null)
                {
                    if (md.Bindposes == null || md.Bindposes.Length != ri.Bones.Length || md.Weights == null)
                    {
                        Log.Warn($"{Name}: mesh {md.Name} does not match its renderer ({md.Bindposes?.Length} bindposes, {ri.Bones.Length} bones); skipping it.");
                        ri.Unavailable = true;
                        continue;
                    }
                    if (!SkinMatchesRenderer(ri, md))
                    {
                        ri.Unavailable = true;
                        continue;
                    }
                    ri.OriginalData = md;
                    ri.Data = md;
                }
                else if (pending) allDone = false;
                else ri.Unavailable = true;
            }
            return allDone;
        }

        /// <summary>
        /// Sanity check that the decoded mesh, skinned on the CPU with the renderer's live bones, lands where Unity draws
        /// it. A wrong match (same name, different mesh) would otherwise produce nonsense cuts.
        /// </summary>
        private bool SkinMatchesRenderer(RendererInfo ri, MeshData md)
        {
            try
            {
                int bones = ri.Bones.Length;
                var mats = new NMatrix[bones];
                for (int b = 0; b < bones; b++)
                    mats[b] = md.Bindposes[b] * (ri.Bones[b].IsDead() ? NMatrix.Identity : ri.Bones[b].localToWorldMatrix.N());
                var min = new NVector3(float.MaxValue);
                var max = new NVector3(float.MinValue);
                int step = Math.Max(1, md.VertexCount / 500);
                for (int v = 0; v < md.VertexCount; v += step)
                {
                    var w = md.Weights[v];
                    var p = NVector3.Zero;
                    for (int k = 0; k < 4; k++)
                    {
                        float wk = w.Weight(k);
                        int bi = w.Bone(k);
                        if (wk > 0f && bi >= 0 && bi < bones) p += NVector3.Transform(md.Positions[v], mats[bi]) * wk;
                    }
                    min = NVector3.Min(min, p);
                    max = NVector3.Max(max, p);
                }
                var centre = ((min + max) * 0.5f).U();
                var bounds = ri.Renderer.bounds;
                bounds.Expand(Mathf.Max(0.6f, bounds.size.magnitude));
                bool ok = bounds.Contains(centre);
                string msg = $"{Name}/{ri.Renderer.name}: decoded mesh centre is {Vector3.Distance(centre, ri.Renderer.bounds.center) * 100f:F0} cm from where Unity draws it" +
                             $" (caps {(ri.AllowCaps ? "on" : "off")}, flesh submeshes {string.Join("", ri.SubmeshCaps?.Select(c => c ? '1' : '0') ?? Array.Empty<char>())})";
                if (ok) Log.Debug(msg);
                else Log.Warn(msg + "; this renderer will not be cut.");
                return ok;
            }
            catch (Exception e)
            {
                Log.Debug($"skin check for {ri.Renderer.name} failed: {e.Message}");
                return true;
            }
        }

        public bool HasAnyMesh => Renderers.Any(r => r.Data != null && r.AllowCaps);

        /// <summary>Current connectivity as a solver model whose core is the piece rooted at <paramref name="root"/>.</summary>
        public BodyModel BuildModel(int root)
        {
            var model = BodyModel.Build(Muscles.Select(m => m.Name).ToArray(), (int[])Parents.Clone(), Muscles.Select(m => m.Kind).ToArray());
            if (root >= 0 && root < Muscles.Length) model.Root = root;
            return model;
        }

        /// <summary>
        /// Marrow can rebuild rigidbodies and joints (hibernation, packing). Re-fetches anything that died so lookups
        /// and joint enforcement keep working.
        /// </summary>
        public void Refresh()
        {
            bool changed = false;
            foreach (var m in Muscles)
            {
                if (m.Body.IsDead()) continue;
                if (m.Rb.IsDead()) { m.Rb = m.Body.GetComponent<Rigidbody>(); changed = true; }
                if (m.Joint.IsDead()) { m.Joint = m.Body.GetComponent<ConfigurableJoint>(); changed = true; }
                if (m.Colliders.Length == 0 || m.Colliders.Any(c => c.IsDead()))
                {
                    var cols = m.Muscle?.colliders;
                    m.Colliders = cols != null && cols.Length > 0 ? cols.Where(c => !c.IsDead()).ToArray() : OwnColliders(m.Body, m.Rb);
                }
            }
            if (!changed) return;
            MuscleByRigidbody.Clear();
            for (int i = 0; i < Muscles.Length; i++)
                if (!Muscles[i].Rb.IsDead()) MuscleByRigidbody[Muscles[i].Rb.Pointer] = i;
        }

        public bool Alive
        {
            get
            {
                try { return !Brain.IsDead() && !Brain.isDead && !LethalDone; }
                catch { return false; }
            }
        }

        public bool Valid => !Brain.IsDead() && !Root.IsDead();

        public int PieceRoot(int seg)
        {
            int guard = 0;
            while (seg >= 0 && Parents[seg] >= 0 && guard++ < 64) seg = Parents[seg];
            return seg;
        }

        public bool InCore(int seg) => PieceRoot(seg) == PieceRoot(Root0);

        /// <summary>Active renderers on the piece that contains <paramref name="seg"/>, with mesh data.</summary>
        public List<RendererInfo> RenderersOfPiece(int seg)
        {
            int root = PieceRoot(seg);
            var list = new List<RendererInfo>();
            foreach (var ri in Renderers)
                if (ri.Data != null && ri.Active && PieceRoot(ri.PieceSegment) == root) list.Add(ri);
            return list;
        }

        /// <summary>Current pose for the solver.</summary>
        public PoseInfo SnapshotPose()
        {
            int n = Muscles.Length;
            var pose = new PoseInfo { SegmentOrigin = new NVector3[n], SegmentBodyWorld = new NMatrix[n], SegmentTargetWorld = new NMatrix[n] };
            for (int i = 0; i < n; i++)
            {
                var t = Muscles[i].Body;
                if (t.IsDead()) { pose.SegmentBodyWorld[i] = NMatrix.Identity; continue; }
                var m = t.localToWorldMatrix.N();
                pose.SegmentBodyWorld[i] = m;
                pose.SegmentTargetWorld[i] = m;   // caps and pieces ride on the physics bodies
                pose.SegmentOrigin[i] = t.position.N();
            }
            return pose;
        }

        public SkinInstance SnapshotSkin(RendererInfo ri, int id)
        {
            ri.Skin ??= new SkinInstance();
            var s = ri.Skin;
            s.Id = id;
            s.Mesh = ri.Data;
            s.BoneSegment = ri.BoneSegment;
            s.SegmentBone = ri.SegmentBone;
            s.AllowCaps = ri.AllowCaps;
            s.SubmeshCaps = ri.SubmeshCaps != null && ri.SubmeshCaps.Length == ri.Data.Submeshes.Count ? ri.SubmeshCaps : null;
            if (s.BoneWorld == null || s.BoneWorld.Length != ri.Bones.Length) s.BoneWorld = new NMatrix[ri.Bones.Length];
            for (int b = 0; b < ri.Bones.Length; b++)
                s.BoneWorld[b] = ri.Bones[b].IsDead() ? NMatrix.Identity : ri.Bones[b].localToWorldMatrix.N();
            int shapes = ri.Data.BlendShapes.Count;
            if (shapes > 0 && ri.Original)
            {
                s.BlendShapeWeights ??= new float[shapes];
                for (int k = 0; k < shapes; k++) s.BlendShapeWeights[k] = ri.Renderer.GetBlendShapeWeight(k);
            }
            else s.BlendShapeWeights = null;
            return s;
        }

        // ---------------------------------------------------------------------------------------------
        // Undoable modifications
        // ---------------------------------------------------------------------------------------------

        /// <summary>Separates a segment from its parent (the joint stays but no longer constrains anything).</summary>
        public void FreeJoint(int seg)
        {
            // Hung on by flesh after an earlier cut (Rejoin): that hinge is what holds it now, and it goes.
            if (RejoinJoints.ContainsKey(seg)) ReleaseRejoin(seg);
            if (!FreedJoints.Add(seg))
            {
                // Its own joint is already free: it just stops being attached to anything.
                int was = Parents[seg];
                if (was >= 0)
                {
                    Parents[seg] = -1;
                    Undo.Add(() => Parents[seg] = was);
                }
                return;
            }
            int prevParent = Parents[seg];
            Parents[seg] = -1;
            var j = Muscles[seg].Joint;
            if (j.IsDead())
            {
                Undo.Add(() => { FreedJoints.Remove(seg); Parents[seg] = prevParent; });
                return;
            }
            var saved = (j.xMotion, j.yMotion, j.zMotion, j.angularXMotion, j.angularYMotion, j.angularZMotion,
                         j.slerpDrive, j.angularXDrive, j.angularYZDrive, j.xDrive, j.yDrive, j.zDrive);
            ApplyFreeJoint(j);
            Undo.Add(() =>
            {
                FreedJoints.Remove(seg);
                Parents[seg] = prevParent;
                if (j.IsDead()) return;
                j.xMotion = saved.Item1; j.yMotion = saved.Item2; j.zMotion = saved.Item3;
                j.angularXMotion = saved.Item4; j.angularYMotion = saved.Item5; j.angularZMotion = saved.Item6;
                j.slerpDrive = saved.Item7; j.angularXDrive = saved.Item8; j.angularYZDrive = saved.Item9;
                j.xDrive = saved.Item10; j.yDrive = saved.Item11; j.zDrive = saved.Item12;
            });
        }

        /// <summary>
        /// Hangs body <paramref name="seg"/> from <paramref name="holder"/> with a flesh hinge at <paramref name="atWorld"/>:
        /// its own joint was cut, but flesh on its side of the cut still holds it on (an arm below a cut that took
        /// the chest away stays on the lower torso). A CharacterJoint, so it is never mistaken for the muscle's own.
        /// </summary>
        public void Rejoin(int seg, int holder, Vector3 atWorld)
        {
            var a = Muscles[seg];
            var h = Muscles[holder];
            if (a.Rb.IsDead() || h.Rb.IsDead() || seg == holder) return;
            if (RejoinJoints.ContainsKey(seg)) ReleaseRejoin(seg);
            int prev = Parents[seg];
            Parents[seg] = holder;
            var local = a.Rb.transform.InverseTransformPoint(atWorld);
            RejoinJoints[seg] = (MakeHinge(a.Rb, h.Rb, local), holder, local);
            Undo.Add(() =>
            {
                Parents[seg] = prev;
                if (RejoinJoints.TryGetValue(seg, out var cur))
                {
                    if (!cur.Joint.IsDead()) UnityEngine.Object.Destroy(cur.Joint);
                    RejoinJoints.Remove(seg);
                }
            });
        }

        /// <summary>Flesh hinges from <see cref="Rejoin"/>, by the segment that hangs.</summary>
        private readonly Dictionary<int, (CharacterJoint Joint, int Holder, Vector3 LocalAnchor)> RejoinJoints =
            new Dictionary<int, (CharacterJoint, int, Vector3)>();

        /// <summary>A later cut took a hung-on part away from its holder: the hinge goes (and comes back on undo).</summary>
        private void ReleaseRejoin(int seg)
        {
            var (joint, holder, local) = RejoinJoints[seg];
            RejoinJoints.Remove(seg);
            if (!joint.IsDead()) UnityEngine.Object.Destroy(joint);
            Undo.Add(() =>
            {
                var a = Muscles[seg];
                var h = Muscles[holder];
                if (a.Rb.IsDead() || h.Rb.IsDead()) return;
                RejoinJoints[seg] = (MakeHinge(a.Rb, h.Rb, local), holder, local);
            });
        }

        private static CharacterJoint MakeHinge(Rigidbody body, Rigidbody holder, Vector3 localAnchor)
        {
            var j = body.gameObject.AddComponent<CharacterJoint>();
            j.autoConfigureConnectedAnchor = true;
            j.anchor = localAnchor;
            j.connectedBody = holder;
            var twistLo = new SoftJointLimit(); twistLo.limit = -25f;
            var twistHi = new SoftJointLimit(); twistHi.limit = 25f;
            var swing = new SoftJointLimit(); swing.limit = 35f;
            j.lowTwistLimit = twistLo;
            j.highTwistLimit = twistHi;
            j.swing1Limit = swing;
            j.swing2Limit = swing;
            j.enableCollision = false;
            return j;
        }

        public static void ApplyFreeJoint(ConfigurableJoint j)
        {
            if (j.xMotion != ConfigurableJointMotion.Free) j.xMotion = ConfigurableJointMotion.Free;
            if (j.yMotion != ConfigurableJointMotion.Free) j.yMotion = ConfigurableJointMotion.Free;
            if (j.zMotion != ConfigurableJointMotion.Free) j.zMotion = ConfigurableJointMotion.Free;
            if (j.angularXMotion != ConfigurableJointMotion.Free) j.angularXMotion = ConfigurableJointMotion.Free;
            if (j.angularYMotion != ConfigurableJointMotion.Free) j.angularYMotion = ConfigurableJointMotion.Free;
            if (j.angularZMotion != ConfigurableJointMotion.Free) j.angularZMotion = ConfigurableJointMotion.Free;
            var zero = new JointDrive();
            if (j.slerpDrive.positionSpring != 0f || j.slerpDrive.positionDamper != 0f) j.slerpDrive = zero;
            if (j.angularXDrive.positionSpring != 0f || j.angularXDrive.positionDamper != 0f) j.angularXDrive = zero;
            if (j.angularYZDrive.positionSpring != 0f || j.angularYZDrive.positionDamper != 0f) j.angularYZDrive = zero;
            if (j.xDrive.positionSpring != 0f || j.xDrive.positionDamper != 0f) j.xDrive = zero;
            if (j.yDrive.positionSpring != 0f || j.yDrive.positionDamper != 0f) j.yDrive = zero;
            if (j.zDrive.positionSpring != 0f || j.zDrive.positionDamper != 0f) j.zDrive = zero;
        }

        /// <summary>Stops PuppetMaster from driving a muscle that is no longer attached to the body.</summary>
        public void Neutralize(int seg)
        {
            if (!Neutralized.Add(seg)) return;
            var m = Muscles[seg].Muscle;
            if (m == null)
            {
                Undo.Add(() => Neutralized.Remove(seg));
                return;
            }
            var props = m.props;
            float mw = props.muscleWeight, md = props.muscleDamper;
            var state = m.state;
            var savedState = state;
            ApplyNeutral(m);
            Undo.Add(() =>
            {
                Neutralized.Remove(seg);
                try
                {
                    props.muscleWeight = mw;
                    props.muscleDamper = md;
                    m.state = savedState;
                }
                catch { }
            });
        }

        public static void ApplyNeutral(Muscle m)
        {
            var props = m.props;
            if (props.muscleWeight != 0f) props.muscleWeight = 0f;
            if (props.muscleDamper != 0f) props.muscleDamper = 0f;
            var st = m.state;
            if (st.muscleWeightMlp != 0f || st.muscleDamperMlp != 0f || st.muscleDamperAdd != 0f || st.maxForceMlp != 0f)
            {
                st.muscleWeightMlp = 0f;
                st.muscleDamperMlp = 0f;
                st.muscleDamperAdd = 0f;
                st.maxForceMlp = 0f;
                m.state = st;
            }
        }

        /// <summary>Re-applies separation every physics step: PuppetMaster and Marrow may restore limits or drives.</summary>
        public void Enforce()
        {
            if (_collideWhenClear.Count > 0) UpdateCollideWhenClear();
            foreach (int s in FreedJoints)
            {
                var j = Muscles[s].Joint;
                if (!j.IsDead()) ApplyFreeJoint(j);
            }
            foreach (int s in Neutralized)
            {
                var m = Muscles[s].Muscle;
                if (m != null) ApplyNeutral(m);
                var j = Muscles[s].Joint;
                if (!j.IsDead() && !FreedJoints.Contains(s))
                {
                    // Internal joints of a severed limb stay connected but limp.
                    var zero = new JointDrive();
                    if (j.slerpDrive.positionSpring != 0f || j.slerpDrive.positionDamper != 0f) j.slerpDrive = zero;
                    if (j.angularXDrive.positionSpring != 0f) j.angularXDrive = zero;
                    if (j.angularYZDrive.positionSpring != 0f) j.angularYZDrive = zero;
                }
            }
        }

        public void SetRendererMesh(RendererInfo ri, Mesh mesh, MeshData data)
        {
            var r = ri.Renderer;
            var oldMesh = r.sharedMesh;
            bool oldUpdate = r.updateWhenOffscreen;
            // Keep the face's current expression across the mesh swap.
            int shapes = oldMesh != null ? oldMesh.blendShapeCount : 0;
            var weights = new float[shapes];
            for (int k = 0; k < shapes; k++) weights[k] = r.GetBlendShapeWeight(k);
            r.sharedMesh = mesh;
            r.updateWhenOffscreen = true;
            for (int k = 0; k < shapes && k < mesh.blendShapeCount; k++) r.SetBlendShapeWeight(k, weights[k]);
            var oldData = ri.Data;
            ri.Data = data;
            Undo.Add(() =>
            {
                ri.Data = oldData;
                if (!r.IsDead())
                {
                    r.sharedMesh = oldMesh;
                    r.updateWhenOffscreen = oldUpdate;
                }
                if (!mesh.IsDead()) UnityEngine.Object.Destroy(mesh);
            });
        }

        public void HideRenderer(Renderer r)
        {
            if (r.IsDead() || !r.enabled) return;
            r.enabled = false;
            Undo.Add(() => { if (!r.IsDead()) r.enabled = true; });
        }

        public void Track(GameObject go)
        {
            Undo.Add(() => { if (!go.IsDead()) UnityEngine.Object.Destroy(go); });
        }

        public void Track(UnityEngine.Object o)
        {
            Undo.Add(() => { if (!o.IsDead()) UnityEngine.Object.Destroy(o); });
        }

        public void CollapseLods()
        {
            if (LodsCollapsed) return;
            LodsCollapsed = true;
            foreach (var g in LodGroups)
            {
                if (g.IsDead() || !g.enabled) continue;
                var gg = g;
                gg.enabled = false;
                Undo.Add(() => { if (!gg.IsDead()) gg.enabled = true; });
            }
            foreach (var r in LodOnlyRenderers) HideRenderer(r);
            Undo.Add(() => LodsCollapsed = false);
        }

        public void SetIgnore(Collider a, Collider b, bool ignore)
        {
            if (a.IsDead() || b.IsDead()) return;
            bool was = Physics.GetIgnoreCollision(a, b);
            if (was == ignore) return;
            Physics.IgnoreCollision(a, b, ignore);
            Undo.Add(() => { if (!a.IsDead() && !b.IsDead()) Physics.IgnoreCollision(a, b, was); });
        }

        private readonly List<(Collider a, Collider b, float deadline)> _collideWhenClear = new List<(Collider, Collider, float)>();

        /// <summary>
        /// Lets two body parts collide again, but only once they have come apart: parts that were joined overlap at
        /// the joint, and colliding while overlapping would fling them away.
        /// </summary>
        public void CollideWhenClear(Collider a, Collider b, float maxWait = 2f)
        {
            if (a.IsDead() || b.IsDead() || !Physics.GetIgnoreCollision(a, b)) return;
            if (!Collisions.Overlapping(a, b)) SetIgnore(a, b, false);
            else _collideWhenClear.Add((a, b, Time.time + maxWait));
        }

        private void UpdateCollideWhenClear()
        {
            float now = Time.time;
            for (int i = _collideWhenClear.Count - 1; i >= 0; i--)
            {
                var (a, b, deadline) = _collideWhenClear[i];
                if (a.IsDead() || b.IsDead()) { _collideWhenClear.RemoveAt(i); continue; }
                if (now < deadline && Collisions.Overlapping(a, b)) continue;
                SetIgnore(a, b, false);
                _collideWhenClear.RemoveAt(i);
            }
        }

        /// <summary>Undoes everything recorded after the given marks (a cut that failed half way).</summary>
        public void Rollback(int undoMark, int rendererMark)
        {
            for (int i = Undo.Count - 1; i >= undoMark && i >= 0; i--)
            {
                try { Undo[i](); }
                catch (Exception e) { Log.Debug("rollback step failed: " + e.Message); }
            }
            if (undoMark < Undo.Count) Undo.RemoveRange(undoMark, Undo.Count - undoMark);
            // Renderers created by the failed cut were appended; their objects are already destroyed above.
            if (rendererMark < Renderers.Count) Renderers.RemoveRange(rendererMark, Renderers.Count - rendererMark);
        }

        /// <summary>Puts the NPC back exactly as it was. Safe to call more than once.</summary>
        public void Restore()
        {
            for (int i = Undo.Count - 1; i >= 0; i--)
            {
                try { Undo[i](); }
                catch (Exception e) { Log.Debug("restore step failed: " + e.Message); }
            }
            Undo.Clear();
            Renderers.RemoveAll(r => !r.Original);
            foreach (var ri in Renderers)
            {
                ri.Data = ri.OriginalData;
                ri.PieceSegment = Root0;
            }
            Neutralized.Clear();
            FreedJoints.Clear();
            foreach (var r in RejoinJoints.Values)
                if (!r.Joint.IsDead()) UnityEngine.Object.Destroy(r.Joint);
            RejoinJoints.Clear();
            FreeChunks.Clear();
            ExtraColliders.Clear();
            _collideWhenClear.Clear();
            Parents = (int[])OriginalParents.Clone();
            Cuts = 0;
            LethalDone = false;
            BleedUntil = 0f;
            BleedPerSecond = 0f;
            Busy = false;
            Generation++;
        }
    }
}
