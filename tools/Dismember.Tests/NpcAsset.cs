using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using AssetsTools.NET;
using AssetsTools.NET.Extra;
using Dismember.Assets;
using Dismember.Core;

namespace Dismember.Tests
{
    /// <summary>
    /// Rebuilds an NPC (skeleton, PuppetMaster muscles, LOD0 skinned renderers) straight from a spawnable bundle,
    /// in its prefab rest pose. Mirrors what the mod reads from live objects in game.
    /// </summary>
    public sealed class NpcAsset
    {
        public string Name;
        public BodyModel Body;
        public PoseInfo Pose;
        public List<SkinInstance> Skins = new List<SkinInstance>();
        public List<string> RendererNames = new List<string>();
        /// <summary>True when renderers are bound straight to the ragdoll bodies (SkinnedBoneRebind), so caps should ride on bodies.</summary>
        public bool PhysicsBones;

        private sealed class Xf
        {
            public long Id;
            public long Father;
            public long GameObject;
            public string Name;
            public Vector3 Pos;
            public Quaternion Rot;
            public Vector3 Scale;
            public Matrix4x4 World;
            public bool Done;
        }

        public static NpcAsset Load(string spawnableBundle, Func<string, int, MeshData> meshResolver)
        {
            var am = new AssetsManager();
            var bun = am.LoadBundleFile(spawnableBundle, true);
            var af = am.LoadAssetsFileFromBundle(bun, 0, false);

            var goNames = new Dictionary<long, string>();
            var goTransform = new Dictionary<long, long>();
            foreach (var info in af.file.GetAssetsOfType(AssetClassID.GameObject))
            {
                var bf = am.GetBaseField(af, info);
                goNames[info.PathId] = bf["m_Name"].AsString;
            }

            var xfs = new Dictionary<long, Xf>();
            foreach (var info in af.file.GetAssetsOfType(AssetClassID.Transform))
            {
                var bf = am.GetBaseField(af, info);
                var go = bf["m_GameObject.m_PathID"].AsLong;
                var x = new Xf
                {
                    Id = info.PathId,
                    Father = bf["m_Father.m_PathID"].AsLong,
                    GameObject = go,
                    Name = goNames.TryGetValue(go, out var goName) ? goName : "?",
                    Pos = V3(bf["m_LocalPosition"]),
                    Rot = new Quaternion(bf["m_LocalRotation.x"].AsFloat, bf["m_LocalRotation.y"].AsFloat, bf["m_LocalRotation.z"].AsFloat, bf["m_LocalRotation.w"].AsFloat),
                    Scale = V3(bf["m_LocalScale"]),
                };
                xfs[x.Id] = x;
                goTransform[go] = x.Id;
            }
            Matrix4x4 World(long id)
            {
                var x = xfs[id];
                if (x.Done) return x.World;
                var local = Matrix4x4.CreateScale(x.Scale) * Matrix4x4.CreateFromQuaternion(x.Rot) * Matrix4x4.CreateTranslation(x.Pos);
                x.World = x.Father != 0 && xfs.ContainsKey(x.Father) ? local * World(x.Father) : local;
                x.Done = true;
                return x.World;
            }

            // Rigidbodies and joints.
            var rbGo = new Dictionary<long, long>();
            foreach (var info in af.file.GetAssetsOfType(AssetClassID.Rigidbody))
                rbGo[info.PathId] = am.GetBaseField(af, info)["m_GameObject.m_PathID"].AsLong;
            var jointConnected = new Dictionary<long, long>(); // ConfigurableJoint -> connected rigidbody
            foreach (var info in af.file.GetAssetsOfType(AssetClassID.ConfigurableJoint))
                jointConnected[info.PathId] = am.GetBaseField(af, info)["m_ConnectedBody.m_PathID"].AsLong;

            // MonoBehaviours: PuppetMaster, MarrowBody, MarrowJoint.
            AssetTypeValueField pm = null;
            var rebindBones = new Dictionary<long, long[]>(); // SkinnedMeshRenderer -> runtime bones (SkinnedBoneRebind)
            var bodyRb = new Dictionary<long, long>();     // MarrowBody -> Rigidbody
            var marrowJointCj = new Dictionary<long, long>(); // MarrowJoint -> ConfigurableJoint
            foreach (var info in af.file.GetAssetsOfType(AssetClassID.MonoBehaviour))
            {
                AssetTypeValueField bf;
                try { bf = am.GetBaseField(af, info); } catch { continue; }
                if (!bf["muscles"].IsDummy && !bf["targetRoot"].IsDummy) pm = bf;
                if (!bf["_rigidbody"].IsDummy) bodyRb[info.PathId] = bf["_rigidbody.m_PathID"].AsLong;
                if (!bf["_configurableJoint"].IsDummy) marrowJointCj[info.PathId] = bf["_configurableJoint.m_PathID"].AsLong;
                if (!bf["rebindBone"].IsDummy && !bf["skinnedMeshRenderer"].IsDummy)
                    rebindBones[bf["skinnedMeshRenderer.m_PathID"].AsLong] = bf["bones.Array"].Children.Select(b => b["m_PathID"].AsLong).ToArray();
            }
            if (pm == null) throw new InvalidDataException("No PuppetMaster in bundle");

            var muscles = pm["muscles.Array"].Children;
            int n = muscles.Count;
            var names = new string[n];
            var kinds = new SegmentKind[n];
            var parents = new int[n];
            var targetIds = new long[n];
            var bodyXf = new long[n];
            var rbOfMuscle = new long[n];
            for (int i = 0; i < n; i++)
            {
                var m = muscles[i];
                names[i] = m["name"].AsString;
                kinds[i] = (SegmentKind)m["props.group"].AsInt;
                targetIds[i] = m["target.m_PathID"].AsLong;
                long mb = m["marrowBody.m_PathID"].AsLong;
                rbOfMuscle[i] = bodyRb.TryGetValue(mb, out var rb) ? rb : 0;
                bodyXf[i] = rbOfMuscle[i] != 0 && rbGo.TryGetValue(rbOfMuscle[i], out var go) && goTransform.TryGetValue(go, out var t) ? t : targetIds[i];
            }
            for (int i = 0; i < n; i++)
            {
                parents[i] = -1;
                long mj = muscles[i]["marrowJoint.m_PathID"].AsLong;
                if (!marrowJointCj.TryGetValue(mj, out var cj) || !jointConnected.TryGetValue(cj, out var connected) || connected == 0) continue;
                parents[i] = Array.IndexOf(rbOfMuscle, connected);
            }

            var npc = new NpcAsset { Name = Path.GetFileNameWithoutExtension(spawnableBundle) };
            npc.Body = BodyModel.Build(names, parents, kinds);
            npc.Pose = new PoseInfo
            {
                SegmentOrigin = new Vector3[n],
                SegmentBodyWorld = new Matrix4x4[n],
                SegmentTargetWorld = new Matrix4x4[n],
            };
            var targetToSeg = new Dictionary<long, int>();
            var bodyToSeg = new Dictionary<long, int>();
            for (int i = 0; i < n; i++)
            {
                npc.Pose.SegmentBodyWorld[i] = World(bodyXf[i]);
                npc.Pose.SegmentTargetWorld[i] = World(targetIds[i]);
                npc.Pose.SegmentOrigin[i] = npc.Pose.SegmentBodyWorld[i].Translation;
                targetToSeg[targetIds[i]] = i;
                bodyToSeg[bodyXf[i]] = i;
            }

            // LOD0 renderers.
            var lod0 = new HashSet<long>();
            var lodAny = new HashSet<long>();
            foreach (var info in af.file.GetAssetsOfType(AssetClassID.LODGroup))
            {
                var bf = am.GetBaseField(af, info);
                var lods = bf["m_LODs.Array"].Children;
                for (int l = 0; l < lods.Count; l++)
                    foreach (var r in lods[l]["renderers.Array"].Children)
                    {
                        long id = r["renderer.m_PathID"].AsLong;
                        lodAny.Add(id);
                        if (l == 0) lod0.Add(id);
                    }
            }

            int skinId = 0;
            foreach (var info in af.file.GetAssetsOfType(AssetClassID.SkinnedMeshRenderer))
            {
                if (lodAny.Contains(info.PathId) && !lod0.Contains(info.PathId)) continue;
                var bf = am.GetBaseField(af, info);
                if (bf["m_Enabled"].AsByte == 0) continue;
                long meshId = bf["m_Mesh.m_PathID"].AsLong;
                var meshInfo = af.file.GetAssetInfo(meshId);
                MeshData mesh = null;
                if (meshInfo != null)
                    mesh = BundleMeshReader.Decode(am.GetBaseField(af, meshInfo));
                else
                    mesh = meshResolver?.Invoke(goNames.GetValueOrDefault(bf["m_GameObject.m_PathID"].AsLong), -1);
                if (mesh == null) continue;

                var bones = rebindBones.TryGetValue(info.PathId, out var rb2) ? rb2 : bf["m_Bones.Array"].Children.Select(b => b["m_PathID"].AsLong).ToArray();
                bool physicsBones = rebindBones.ContainsKey(info.PathId);
                var skin = new SkinInstance
                {
                    Id = skinId++,
                    Mesh = mesh,
                    BoneWorld = bones.Select(b => xfs.ContainsKey(b) ? World(b) : Matrix4x4.Identity).ToArray(),
                    BoneSegment = new int[bones.Length],
                    SegmentBone = Enumerable.Repeat(-1, n).ToArray(),
                    AllowCaps = !mesh.Name.ToLowerInvariant().Contains("hair") && bf["m_CastShadows"].AsByte != 3,
                };
                for (int b = 0; b < bones.Length; b++)
                {
                    int seg = -1;
                    long cur = bones[b];
                    while (cur != 0 && xfs.ContainsKey(cur))
                    {
                        if (bodyToSeg.TryGetValue(cur, out seg) || targetToSeg.TryGetValue(cur, out seg)) break;
                        seg = -1;
                        cur = xfs[cur].Father;
                    }
                    skin.BoneSegment[b] = seg;
                    if (seg >= 0 && (bones[b] == targetIds[seg] || bones[b] == bodyXf[seg])) skin.SegmentBone[seg] = b;
                }
                npc.Skins.Add(skin);
                npc.PhysicsBones |= physicsBones;
                npc.RendererNames.Add(goNames.GetValueOrDefault(bf["m_GameObject.m_PathID"].AsLong) + " / " + mesh.Name);
            }
            if (npc.PhysicsBones)
                for (int i = 0; i < n; i++) npc.Pose.SegmentTargetWorld[i] = npc.Pose.SegmentBodyWorld[i];
            am.UnloadAll(true);
            return npc;
        }

        private static Vector3 V3(AssetTypeValueField f) => new Vector3(f["x"].AsFloat, f["y"].AsFloat, f["z"].AsFloat);
    }
}
