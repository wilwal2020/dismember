using System;
using System.Collections.Generic;
using System.Linq;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using Dismember.Core;
using NVector3 = System.Numerics.Vector3;

namespace Dismember.Game
{
    /// <summary>Turns a <see cref="CutResult"/> into scene changes on a live NPC.</summary>
    internal static class CutApplier
    {
        public sealed class Context
        {
            public NpcBody Npc;
            public List<RendererInfo> Solved;   // index == SkinInstance.Id
            public CutResult Result;
            public CutRegion Region;            // world space at snapshot time
            public PoseSnapshot Snapshot;       // body poses the result was computed for
            public int HitSegment;
            public Vector3 Push;                // velocity change for freshly severed parts (blade follow-through)
            public string Source = "blade";
            public bool Cauterize;              // an energy blade did it: seared cut faces, no blood
            public readonly PhaseTimer Time = new PhaseTimer();
        }

        /// <summary>Where the main-thread time of one apply went (written to the log when it is slow).</summary>
        internal sealed class PhaseTimer
        {
            private readonly System.Diagnostics.Stopwatch _sw = System.Diagnostics.Stopwatch.StartNew();
            private double _last;
            private readonly List<string> _parts = new List<string>();

            /// <summary>Ends a phase; phases under half a millisecond are left out.</summary>
            public void Mark(string what)
            {
                double now = _sw.Elapsed.TotalMilliseconds;
                if (now - _last >= 0.5) _parts.Add($"{what} {now - _last:F0}");
                _last = now;
            }

            public double Total => _sw.Elapsed.TotalMilliseconds;
            public override string ToString() => _parts.Count == 0 ? "nothing over 0.5 ms" : string.Join(", ", _parts) + " ms";
        }

        public static void Apply(Context ctx)
        {
            var npc = ctx.Npc;
            var res = ctx.Result;
            npc.CollapseLods();
            ctx.Time.Mark("LODs");

            // Which segments were part of the living body before this cut.
            var wasCore = new bool[npc.Muscles.Length];
            for (int s = 0; s < wasCore.Length; s++) wasCore[s] = npc.InCore(s);

            // Caps copy the material of the biggest flesh surface (the body's skin, not the teeth or the zipper).
            var template = Gore.PickTemplate(ctx.Solved
                .Where(r => r.AllowCaps && !r.IsCap && r.Materials != null && r.Data != null)
                .OrderByDescending(r => r.Data.TriangleCount)
                .Select(r => FleshMaterials(r)));
            var capMat = Gore.CapMaterial(ctx.Cauterize ? Gore.Seared(npc.Palette) : npc.Palette, template);
            foreach (var piece in res.Pieces)
                if (piece.Cap != null) WhiteVertexColors(piece.Cap.Mesh);
            ctx.Time.Mark("cut-face material");

            // 1) Renderers that took part in the cut: keep, replace or hide.
            var coreOut = new Dictionary<int, RendererPiece>();
            foreach (var rp in res.Pieces[res.CorePiece].Renderers) coreOut[rp.SkinId] = rp;
            for (int i = 0; i < ctx.Solved.Count; i++)
            {
                var ri = ctx.Solved[i];
                if (coreOut.TryGetValue(i, out var rp))
                {
                    if (rp.Unchanged) continue;
                    if (ri.Original)
                    {
                        var mesh = MeshFactory.ToUnity(rp.Mesh, ri.OriginalMesh.name + "_cut");
                        npc.SetRendererMesh(ri, mesh, rp.Mesh);
                    }
                    else
                    {
                        // A renderer we created earlier: swap its mesh (undoable, in case this cut fails half way).
                        var r = ri.Renderer;
                        var oldMesh = r.sharedMesh;
                        var oldData = ri.Data;
                        var mesh = MeshFactory.ToUnity(rp.Mesh, rp.Mesh.Name);
                        r.sharedMesh = mesh;
                        ri.Data = rp.Mesh;
                        npc.Undo.Add(() =>
                        {
                            ri.Data = oldData;
                            if (!r.IsDead()) r.sharedMesh = oldMesh;
                            if (!mesh.IsDead()) UnityEngine.Object.Destroy(mesh);
                        });
                    }
                }
                else
                {
                    npc.HideRenderer(ri.Renderer);
                }
                ctx.Time.Mark(ri.Renderer.IsDead() ? "mesh" : ri.Renderer.name);
            }

            // 2) New pieces.
            foreach (var piece in res.Pieces)
            {
                if (piece.Index == res.CorePiece) continue;
                if (piece.IsFreeChunk)
                {
                    CreateFreeChunk(ctx, piece, capMat);
                    continue;
                }
                foreach (var rp in piece.Renderers)
                    if (!rp.Unchanged && rp.Mesh != null) CreatePieceRenderer(ctx, piece, rp);
            }
            ctx.Time.Mark("cut-off parts");

            // 3) Caps.
            foreach (var piece in res.Pieces)
                if (piece.Cap != null && !piece.IsFreeChunk && capMat != null) CreateCapRenderer(ctx, piece, capMat);
            ctx.Time.Mark("cut faces");

            // 4) Physics: separate, go limp, trim colliders, attach flesh chunks.
            foreach (int s in res.FreedJoints) npc.FreeJoint(s);
            foreach (var (body, holder, at) in res.Rejoined)
            {
                npc.Rejoin(body, holder, ctx.Snapshot.Point(npc, body, at.U()));
                // Held on by flesh only: it hangs limp (with everything below it) instead of being driven to the pose.
                for (int s = 0; s < npc.Muscles.Length; s++)
                    for (int x = s, guard = 0; x >= 0 && guard < 64; x = npc.Parents[x], guard++)
                    {
                        if (x == holder) break;
                        if (x == body) { npc.Neutralize(s); break; }
                    }
            }
            var newlySevered = new List<int>();
            for (int s = 0; s < npc.Muscles.Length; s++)
            {
                if (npc.InCore(s)) continue;
                if (!npc.Neutralized.Contains(s)) newlySevered.Add(s);
                npc.Neutralize(s);
            }
            if (Settings.SeveredCollideWithBody.Value) EnableCollisionsBetweenPieces(npc, newlySevered);
            for (int s = 0; s < npc.Muscles.Length; s++)
                if (res.SegmentCut[s]) TrimColliders(ctx, s, res.SegmentOwnerSide[s]);
            foreach (var cc in res.ChunkColliders) CreateChunkCollider(ctx, cc);

            // Fling freshly severed parts along with the blade.
            if (ctx.Push.sqrMagnitude > 0f)
                foreach (int s in newlySevered)
                {
                    var rb = npc.Muscles[s].Rb;
                    if (!rb.IsDead()) rb.velocity += ctx.Push;
                }

            ctx.Time.Mark("physics");

            // 5) Consequences and gore.
            NpcEffects.OnCut(npc, res, wasCore, ctx.Region, ctx.Cauterize);
            ctx.Time.Mark("death/crippling");
            foreach (var (center, normal, area) in res.Wounds)
            {
                var c = ctx.Snapshot.Point(npc, ctx.HitSegment, center.U());
                var n = ctx.Snapshot.Direction(npc, ctx.HitSegment, normal.U());
                float strength = Mathf.Clamp(area * 400f, 0.4f, 3f);
                if (ctx.Cauterize)
                {
                    // Burnt shut: sparks, no blood.
                    Gore.Embers(c, n, strength);
                    Gore.Embers(c, -n, strength);
                    continue;
                }
                Gore.Burst(c, n, strength, npc.Palette);
                Gore.Burst(c, -n, strength, npc.Palette);
                AttachBleeders(npc, c, n, area);
            }
            ctx.Time.Mark("blood");
            npc.Cuts++;
        }

        /// <summary>
        /// A wound opened in the body's own meshes (<see cref="CutSolver.ExecuteSlit"/>): which renderers got which mesh,
        /// and the flesh walls inside. It can be taken back out as long as nothing has cut those renderers since.
        /// </summary>
        internal sealed class SlitLayer
        {
            public NpcBody Npc;
            public int Generation;
            public readonly List<(RendererInfo Info, Mesh Mesh)> Swapped = new List<(RendererInfo, Mesh)>();
            /// <summary>What each renderer it changed had before (to work out its next state from, while it still shows).</summary>
            public readonly Dictionary<RendererInfo, MeshData> Base = new Dictionary<RendererInfo, MeshData>();
            public RendererInfo Walls;
            public Action Undo;
            public bool Reverted;

            /// <summary>True while every renderer it changed still shows its mesh (no cut went through it since).</summary>
            public bool Revertible =>
                !Reverted && Npc != null && Npc.Generation == Generation && Npc.Undo.Contains(Undo) &&
                Swapped.All(x => !x.Info.Renderer.IsDead() && x.Info.Renderer.sharedMesh != null &&
                                 x.Info.Renderer.sharedMesh.Pointer == x.Mesh.Pointer) &&
                (Walls == null || (!Walls.Renderer.IsDead() && Npc.Renderers.Contains(Walls) && Walls.Data == WallData));

            public MeshData WallData;

            /// <summary>Closes the wound again (back to the meshes from before it). False when it can no longer be.</summary>
            public bool Revert()
            {
                if (!Revertible) return false;
                Undo();
                Npc.Undo.Remove(Undo);
                if (Walls != null) Npc.Renderers.Remove(Walls);
                Reverted = true;
                return true;
            }
        }

        /// <summary>
        /// Applies a slit (<see cref="CutSolver.ExecuteSlit"/>: one piece, the whole body): the renderers it opened get
        /// their new meshes and the walls get a renderer. Nothing moves, comes off or dies.
        /// </summary>
        public static SlitLayer ApplySlit(Context ctx)
        {
            var npc = ctx.Npc;
            var res = ctx.Result;
            npc.CollapseLods();
            var layer = new SlitLayer { Npc = npc, Generation = npc.Generation };
            var undo = new List<Action>();
            void UndoAll()
            {
                for (int i = undo.Count - 1; i >= 0; i--)
                {
                    try { undo[i](); }
                    catch (Exception e) { Log.Debug("wound undo: " + e.Message); }
                }
            }
            try
            {
                foreach (var rp in res.Pieces[res.CorePiece].Renderers)
                {
                    if (rp.Unchanged || rp.Mesh == null || rp.SkinId >= ctx.Solved.Count) continue;
                    var ri = ctx.Solved[rp.SkinId];
                    var r = ri.Renderer;
                    if (r.IsDead()) continue;
                    var oldMesh = r.sharedMesh;
                    var oldData = ri.Data;
                    bool oldUpdate = r.updateWhenOffscreen;
                    int shapes = oldMesh != null ? oldMesh.blendShapeCount : 0;
                    var weights = new float[shapes];
                    for (int k = 0; k < shapes; k++) weights[k] = r.GetBlendShapeWeight(k);
                    var mesh = MeshFactory.ToUnity(rp.Mesh, (ri.Original ? ri.OriginalMesh.name : rp.Mesh.Name) + "_wound");
                    r.sharedMesh = mesh;
                    r.updateWhenOffscreen = true;
                    for (int k = 0; k < shapes && k < mesh.blendShapeCount; k++) r.SetBlendShapeWeight(k, weights[k]);
                    ri.Data = rp.Mesh;
                    ctx.Time.Mark(r.name);
                    layer.Swapped.Add((ri, mesh));
                    layer.Base[ri] = oldData;
                    undo.Add(() =>
                    {
                        ri.Data = oldData;
                        if (!r.IsDead())
                        {
                            r.sharedMesh = oldMesh;
                            r.updateWhenOffscreen = oldUpdate;
                            for (int k = 0; k < shapes && oldMesh != null && k < oldMesh.blendShapeCount; k++) r.SetBlendShapeWeight(k, weights[k]);
                        }
                        if (!mesh.IsDead()) UnityEngine.Object.Destroy(mesh);
                    });
                }

                var piece = res.Pieces[res.CorePiece];
                var capMat = CapMaterialFor(npc, ctx.Cauterize);
                if (piece.Cap != null && capMat != null)
                {
                    WhiteVertexColors(piece.Cap.Mesh);
                    var go = new GameObject("Dismember_WoundWalls");
                    go.transform.SetParent(npc.Root.transform, false);
                    go.layer = RenderLayer(ctx);
                    var smr = go.AddComponent<SkinnedMeshRenderer>();
                    var bones = BodyBones(npc, piece.Cap.BoneSegments);
                    var mesh = MeshFactory.ToUnity(piece.Cap.Mesh, "DismemberWoundWalls");
                    smr.sharedMesh = mesh;
                    smr.bones = new Il2CppReferenceArray<Transform>(bones);
                    smr.rootBone = bones[0];
                    smr.sharedMaterials = new Il2CppReferenceArray<Material>(new[] { capMat });
                    smr.updateWhenOffscreen = true;
                    smr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    var walls = new RendererInfo
                    {
                        Renderer = smr,
                        Original = false,
                        IsCap = true,
                        Data = piece.Cap.Mesh,
                        Bones = bones,
                        BoneSegment = (int[])piece.Cap.BoneSegments.Clone(),
                        SegmentBone = InverseMap(piece.Cap.BoneSegments, npc.Muscles.Length),
                        AllowCaps = true,
                        PieceSegment = piece.Cap.BoneSegments[0],
                        Materials = new[] { capMat },
                    };
                    npc.Renderers.Add(walls);
                    ctx.Time.Mark("walls");
                    layer.Walls = walls;
                    layer.WallData = piece.Cap.Mesh;
                    undo.Add(() =>
                    {
                        if (!go.IsDead()) UnityEngine.Object.Destroy(go);
                        if (!mesh.IsDead()) UnityEngine.Object.Destroy(mesh);
                    });
                }
            }
            catch
            {
                // All or nothing, like a cut.
                UndoAll();
                throw;
            }
            if (layer.Swapped.Count == 0 && layer.Walls == null) return null;

            // One undo step for the whole wound, so it can also be taken out on its own (Revert).
            layer.Undo = UndoAll;
            npc.Undo.Add(layer.Undo);
            return layer;
        }

        private static Transform[] BodyBones(NpcBody npc, int[] segs) => segs.Select(s => npc.Muscles[s].Body).ToArray();

        /// <summary>The cut-face material for an NPC (also used for its open wounds).</summary>
        internal static Material CapMaterialFor(NpcBody npc, bool seared = false)
        {
            var template = Gore.PickTemplate(npc.Renderers
                .Where(r => r.AllowCaps && !r.IsCap && r.Materials != null && r.Data != null)
                .OrderByDescending(r => r.Data.TriangleCount)
                .Select(r => FleshMaterials(r)));
            return Gore.CapMaterial(seared ? Gore.Seared(npc.Palette) : npc.Palette, template);
        }

        /// <summary>The layer the NPC's own meshes are drawn on.</summary>
        private static int RenderLayer(Context ctx)
        {
            foreach (var r in ctx.Solved)
                if (r.Original && !r.Renderer.IsDead()) return r.Renderer.gameObject.layer;
            foreach (var r in ctx.Solved)
                if (!r.Renderer.IsDead()) return r.Renderer.gameObject.layer;
            return 0;
        }

        /// <summary>Materials of a renderer's flesh submeshes, biggest submesh first.</summary>
        private static Material[] FleshMaterials(RendererInfo r)
        {
            var order = Enumerable.Range(0, r.Data.Submeshes.Count).OrderByDescending(s => r.Data.Submeshes[s].Length);
            var list = new List<Material>();
            foreach (int s in order)
            {
                if (r.SubmeshCaps != null && (s >= r.SubmeshCaps.Length || !r.SubmeshCaps[s])) continue;
                if (s < r.Materials.Length && !r.Materials[s].IsDead()) list.Add(r.Materials[s]);
            }
            return list.ToArray();
        }

        /// <summary>Caps borrow the NPC's shader, which may tint by vertex colour; make that a no-op.</summary>
        private static void WhiteVertexColors(MeshData md)
        {
            if (md.Colors != null) return;
            md.Colors = new System.Numerics.Vector4[md.VertexCount];
            Array.Fill(md.Colors, System.Numerics.Vector4.One);
        }

        private static int[] InverseMap(int[] boneSegments, int muscleCount)
        {
            var map = Enumerable.Repeat(-1, muscleCount).ToArray();
            for (int i = 0; i < boneSegments.Length; i++) map[boneSegments[i]] = i;
            return map;
        }

        private static void CreatePieceRenderer(Context ctx, Piece piece, RendererPiece rp)
        {
            var npc = ctx.Npc;
            var src = ctx.Solved[rp.SkinId];
            var go = new GameObject($"Dismember_{src.Renderer.name}_{piece.Index}");
            go.transform.SetParent(npc.Root.transform, false);
            go.layer = src.Renderer.gameObject.layer;
            var smr = go.AddComponent<SkinnedMeshRenderer>();
            var bones = BodyBones(npc, rp.BoneSegments);
            var mesh = MeshFactory.ToUnity(rp.Mesh, rp.Mesh.Name);
            smr.sharedMesh = mesh;
            smr.bones = new Il2CppReferenceArray<Transform>(bones);
            smr.rootBone = bones[0];
            smr.sharedMaterials = new Il2CppReferenceArray<Material>(src.Materials);
            smr.shadowCastingMode = src.Renderer.shadowCastingMode;
            smr.receiveShadows = src.Renderer.receiveShadows;
            smr.updateWhenOffscreen = true;
            npc.Track(go);
            npc.Track(mesh);
            npc.Renderers.Add(new RendererInfo
            {
                Renderer = smr,
                Original = false,
                Data = rp.Mesh,
                Bones = bones,
                BoneSegment = (int[])rp.BoneSegments.Clone(),
                SegmentBone = InverseMap(rp.BoneSegments, npc.Muscles.Length),
                AllowCaps = src.AllowCaps,
                SubmeshCaps = src.SubmeshCaps,
                PieceSegment = rp.BoneSegments[0],
                Materials = src.Materials,
            });
        }

        private static void CreateCapRenderer(Context ctx, Piece piece, Material capMat)
        {
            var npc = ctx.Npc;
            var cap = piece.Cap;
            var go = new GameObject($"Dismember_Cap_{piece.Index}");
            go.transform.SetParent(npc.Root.transform, false);
            // The layer the NPC is drawn on, not the ragdoll bodies' collision layer (which the camera may not draw).
            go.layer = RenderLayer(ctx);
            var smr = go.AddComponent<SkinnedMeshRenderer>();
            var bones = BodyBones(npc, cap.BoneSegments);
            var mesh = MeshFactory.ToUnity(cap.Mesh, "DismemberCap");
            smr.sharedMesh = mesh;
            smr.bones = new Il2CppReferenceArray<Transform>(bones);
            smr.rootBone = bones[0];
            smr.sharedMaterials = new Il2CppReferenceArray<Material>(new[] { capMat });
            smr.updateWhenOffscreen = true;
            npc.Track(go);
            npc.Track(mesh);
            npc.Renderers.Add(new RendererInfo
            {
                Renderer = smr,
                Original = false,
                IsCap = true,
                Data = cap.Mesh,
                Bones = bones,
                BoneSegment = (int[])cap.BoneSegments.Clone(),
                SegmentBone = InverseMap(cap.BoneSegments, npc.Muscles.Length),
                AllowCaps = true,
                PieceSegment = cap.BoneSegments[0],
                Materials = new[] { capMat },
            });
            CheckDrawn(ctx, smr, capMat, piece);
        }

        private static int _capChecks = 4;

        /// <summary>
        /// Says in the log, for the first few cut faces of a session, whether Unity actually draws them (next to the
        /// body part they sit on), so a cut face that does not show up can be told apart from one that draws wrong.
        /// </summary>
        private static void CheckDrawn(Context ctx, SkinnedMeshRenderer cap, Material mat, Piece piece)
        {
            if (_capChecks <= 0) return;
            _capChecks--;
            var npc = ctx.Npc;
            Cleanup.After(1f, () =>
            {
                if (cap.IsDead()) return;
                Renderer beside = null;
                foreach (var r in npc.Renderers)
                    if (!r.IsCap && !r.Renderer.IsDead() && r.Renderer.enabled && (piece.IsCore ? r.Original : !r.Original)) { beside = r.Renderer; break; }
                var b = cap.bounds;
                Log.Msg($"Cut face check on {npc.Name}: drawn={cap.isVisible}, body part beside it drawn={(beside != null ? beside.isVisible.ToString() : "?")}, " +
                        $"layer {cap.gameObject.layer}, active={cap.gameObject.activeInHierarchy}, shader '{mat.shader?.name}', " +
                        $"{cap.sharedMesh?.vertexCount} verts, bounds centre {b.center} size {b.size}");
            });
        }

        private static void CreateFreeChunk(Context ctx, Piece piece, Material capMat)
        {
            var npc = ctx.Npc;
            var src = piece.SourceSegment >= 0 ? npc.Muscles[piece.SourceSegment] : null;
            var go = new GameObject("Dismember_Chunk");
            // Vertices are relative to Origin with the world axes of the snapshot; follow the source body since then.
            if (src != null)
            {
                go.transform.position = ctx.Snapshot.Point(npc, src.Index, piece.Origin.U());
                go.transform.rotation = ctx.Snapshot.DeltaRotation(npc, src.Index);
            }
            else go.transform.position = piece.Origin.U();
            if (src != null && !src.Body.IsDead()) go.layer = src.Body.gameObject.layer;

            var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            var max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            foreach (var rp in piece.Renderers)
            {
                if (rp.Mesh == null) continue;
                var child = new GameObject("mesh");
                child.transform.SetParent(go.transform, false);
                child.layer = RenderLayer(ctx);
                var mf = child.AddComponent<MeshFilter>();
                var mr = child.AddComponent<MeshRenderer>();
                var mesh = MeshFactory.ToUnity(rp.Mesh, rp.Mesh.Name);
                mf.sharedMesh = mesh;
                mr.sharedMaterials = new Il2CppReferenceArray<Material>(ctx.Solved[rp.SkinId].Materials);
                npc.Track(mesh);
                foreach (var p in rp.Mesh.Positions)
                {
                    min = Vector3.Min(min, p.U());
                    max = Vector3.Max(max, p.U());
                }
            }
            if (piece.Cap != null && capMat != null)
            {
                var child = new GameObject("cap");
                child.transform.SetParent(go.transform, false);
                child.layer = RenderLayer(ctx);
                var mesh = MeshFactory.ToUnity(piece.Cap.Mesh, "DismemberCap");
                child.AddComponent<MeshFilter>().sharedMesh = mesh;
                child.AddComponent<MeshRenderer>().sharedMaterial = capMat;
                npc.Track(mesh);
            }
            if (min.x > max.x) { min = -Vector3.one * 0.03f; max = Vector3.one * 0.03f; }

            var box = go.AddComponent<BoxCollider>();
            box.center = (min + max) * 0.5f;
            box.size = Vector3.Max(max - min, Vector3.one * 0.01f);

            var rb = go.AddComponent<Rigidbody>();
            var size = max - min;
            rb.mass = Mathf.Clamp(size.x * size.y * size.z * 600f, 0.05f, 6f);
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            if (src != null && !src.Rb.IsDead()) rb.velocity = src.Rb.GetPointVelocity(go.transform.position);
            rb.velocity += ctx.Push;

            npc.Track(go);
            npc.FreeChunks.Add(go);
            Cleanup.Expire(go, Settings.ChunkLifetime.Value);
        }

        private static void CreateChunkCollider(Context ctx, ChunkCollider cc)
        {
            var npc = ctx.Npc;
            if (cc.BodySegment < 0) return;
            var body = npc.Muscles[cc.BodySegment].Body;
            if (body.IsDead()) return;
            var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            var max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            foreach (var p in cc.PointsWorld)
            {
                var l = body.InverseTransformPoint(ctx.Snapshot.Point(npc, cc.BodySegment, p.U()));
                min = Vector3.Min(min, l);
                max = Vector3.Max(max, l);
            }
            var go = new GameObject("Dismember_ChunkCollider");
            go.layer = body.gameObject.layer;
            go.transform.SetParent(body, false);
            var box = go.AddComponent<BoxCollider>();
            box.center = (min + max) * 0.5f;
            box.size = Vector3.Max(max - min, Vector3.one * 0.01f) * 0.85f;
            // It must not fight the body it rides on (or the other bodies it used to be joined to).
            foreach (var m in npc.Muscles)
                foreach (var c in m.Colliders)
                    if (!c.IsDead()) Physics.IgnoreCollision(box, c, true);
            npc.ExtraColliders.Add(box);
            npc.Track(go);
        }

        private static void EnableCollisionsBetweenPieces(NpcBody npc, List<int> newlySevered)
        {
            if (newlySevered.Count == 0) return;
            var severed = new HashSet<int>(newlySevered);
            foreach (int a in newlySevered)
            {
                int pieceA = npc.PieceRoot(a);
                for (int b = 0; b < npc.Muscles.Length; b++)
                {
                    if (severed.Contains(b) && b < a) continue;
                    if (npc.PieceRoot(b) == pieceA) continue;
                    foreach (var ca in npc.Muscles[a].Colliders)
                        foreach (var cb in npc.Muscles[b].Colliders)
                            npc.CollideWhenClear(ca, cb);
                }
            }
        }

        private static void AttachBleeders(NpcBody npc, Vector3 center, Vector3 normal, float area)
        {
            // One stream on each side, from the body nearest to the wound on that side.
            for (int side = 0; side < 2; side++)
            {
                float sign = side == 0 ? 1f : -1f;
                int best = -1;
                float bestD = 0.35f;
                for (int s = 0; s < npc.Muscles.Length; s++)
                {
                    var body = npc.Muscles[s].Body;
                    if (body.IsDead()) continue;
                    var d = body.position - center;
                    if (Vector3.Dot(d, normal) * sign < 0f) continue;
                    float dist = d.magnitude;
                    if (dist < bestD) { bestD = dist; best = s; }
                }
                if (best < 0) continue;
                bool core = npc.InCore(best);
                float seconds = core ? 14f : 6f;
                float rate = Mathf.Clamp(area * 6000f, 25f, 160f);
                Gore.Bleeder(npc.Muscles[best].Body, center, -normal * sign, seconds, rate, npc.Palette);
            }
        }

        // ---------------------------------------------------------------------------------------------
        // Collider trimming: the part of a cut bone's collider on the far side of the plane goes away.
        // ---------------------------------------------------------------------------------------------

        private static void TrimColliders(Context ctx, int seg, int ownerSide)
        {
            if (ownerSide < 0) return;
            var npc = ctx.Npc;
            var region = ctx.Region;
            // The cut surface near this body's joint (a curved cut: its tangent plane there), as it sits on the body now.
            var q = region.To2D(ctx.Snapshot.Pos[seg].N());
            var nrm = region.NormalAt(q);
            var planePoint = ctx.Snapshot.Point(npc, seg, region.To3D(q).U());
            var keepNormal = ctx.Snapshot.Direction(npc, seg, (ownerSide == 0 ? nrm : -nrm).U());
            foreach (var col in npc.Muscles[seg].Colliders)
            {
                if (col.IsDead() || !col.enabled) continue;
                try { TrimCollider(npc, col, planePoint, keepNormal); }
                catch (Exception e) { Log.Debug("trim failed: " + e.Message); }
            }
        }

        private static void TrimCollider(NpcBody npc, Collider col, Vector3 planePoint, Vector3 keepNormal)
        {
            var t = col.transform;
            var o = t.InverseTransformPoint(planePoint);
            var n = t.InverseTransformDirection(keepNormal).normalized;

            var capsule = col.TryCast<CapsuleCollider>();
            if (capsule != null)
            {
                Vector3 axis = capsule.direction == 0 ? Vector3.right : capsule.direction == 1 ? Vector3.up : Vector3.forward;
                float r = capsule.radius, h = Mathf.Max(capsule.height, 2f * r);
                var c = capsule.center;
                var p0 = c - axis * (h * 0.5f - r);
                var p1 = c + axis * (h * 0.5f - r);
                float d0 = Vector3.Dot(p0 - o, n), d1 = Vector3.Dot(p1 - o, n);
                if (d0 >= 0f && d1 >= 0f) return;
                var (oc, oh, or) = (capsule.center, capsule.height, capsule.radius);
                if (d0 < 0f && d1 < 0f) { DisableCollider(npc, col); return; }
                float tt = d0 / (d0 - d1);
                var cross = p0 + (p1 - p0) * tt;
                var keep = d0 >= 0f ? p0 : p1;
                float len = Vector3.Distance(keep, cross);
                capsule.center = (keep + cross) * 0.5f;
                capsule.height = len + 2f * r;
                npc.Undo.Add(() => { if (!capsule.IsDead()) { capsule.center = oc; capsule.height = oh; capsule.radius = or; } });
                return;
            }

            var box = col.TryCast<BoxCollider>();
            if (box != null)
            {
                int k = 0;
                float best = 0f;
                for (int i = 0; i < 3; i++)
                {
                    float a = Mathf.Abs(n[i]);
                    if (a > best) { best = a; k = i; }
                }
                if (best < 0.3f) return; // plane runs along the box: leave it
                var (oc, os) = (box.center, box.size);
                float lo = oc[k] - os[k] * 0.5f, hi = oc[k] + os[k] * 0.5f;
                // Where the plane crosses the box's centre line along axis k.
                var cc = oc;
                float tcross = cc[k] + Vector3.Dot(o - cc, n) / n[k];
                if (n[k] > 0f) lo = Mathf.Max(lo, tcross); else hi = Mathf.Min(hi, tcross);
                if (hi - lo < 0.01f) { DisableCollider(npc, col); return; }
                if (Mathf.Approximately(lo, oc[k] - os[k] * 0.5f) && Mathf.Approximately(hi, oc[k] + os[k] * 0.5f)) return;
                var center = oc; center[k] = (lo + hi) * 0.5f;
                var size = os; size[k] = hi - lo;
                box.center = center;
                box.size = size;
                npc.Undo.Add(() => { if (!box.IsDead()) { box.center = oc; box.size = os; } });
                return;
            }

            var sphere = col.TryCast<SphereCollider>();
            if (sphere != null)
            {
                var (oc, or) = (sphere.center, sphere.radius);
                float d = Vector3.Dot(oc - o, n);
                if (d >= or) return;
                if (d <= -or) { DisableCollider(npc, col); return; }
                float r2 = (or + d) * 0.5f;
                sphere.center = oc + n * ((or - d) * 0.5f);
                sphere.radius = Mathf.Max(0.01f, r2);
                npc.Undo.Add(() => { if (!sphere.IsDead()) { sphere.center = oc; sphere.radius = or; } });
            }
        }

        private static void DisableCollider(NpcBody npc, Collider col)
        {
            col.enabled = false;
            npc.Undo.Add(() => { if (!col.IsDead()) col.enabled = true; });
        }
    }
}
