using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using Dismember.Core;

namespace Dismember.Tests
{
    public sealed class CutCase
    {
        public string Name;
        public string Description;
        public CutRegion Region;
        public Func<CutResult, NpcAsset, string> Expect;   // returns null when OK, else an error message
        /// <summary>Only sever sections touching these segments (the blade only reaches that limb).</summary>
        public string[] Only;
    }

    public static class CutTests
    {
        private static int Seg(NpcAsset n, string name) => Array.FindIndex(n.Body.Segments, s => s.Name == name);
        private static Vector3 O(NpcAsset n, string name) => n.Pose.SegmentOrigin[Seg(n, name)];

        /// <summary>Plane through a line drawn on the front view (X/Y), containing the Z axis — like the red lines in the reference picture.</summary>
        private static CutRegion FrontLine(Vector2 a, Vector2 b, float tiltZ = 0f)
        {
            var d = Vector3.Normalize(new Vector3(b - a, 0f));
            var n = Vector3.Normalize(Vector3.Cross(d, Vector3.UnitZ) + new Vector3(0, 0, tiltZ));
            var mid = new Vector3((a + b) * 0.5f, 0f);
            return CutRegion.FromBlade(mid, n, Vector3.UnitZ, -d, float.NegativeInfinity, float.PositiveInfinity, float.NegativeInfinity);
        }

        private static CutRegion Through(Vector3 p, Vector3 normal) => CutRegion.Infinite(p, normal);

        /// <summary>
        /// Bends a cut into an arc of <paramref name="radius"/> that still passes through W = <paramref name="w0"/> and
        /// <paramref name="w1"/> (a lightsaber steered along a curve between the same two points), sampled every 2 cm.
        /// </summary>
        private static CutRegion Arc(CutRegion r, float w0, float w1, float radius)
        {
            int count = (int)MathF.Ceiling((w1 - w0) / 0.02f) + 1;
            r.BendW = new float[count];
            r.BendN = new float[count];
            for (int i = 0; i < count; i++)
            {
                float w = w0 + (w1 - w0) * i / (count - 1f);
                r.BendW[i] = w;
                r.BendN[i] = (w - w0) * (w - w1) / (2f * radius);
            }
            return r;
        }

        private static string PieceWith(CutResult r, NpcAsset n, params string[] segs)
        {
            int piece = r.SegmentPiece[Seg(n, segs[0])];
            foreach (var s in segs)
                if (r.SegmentPiece[Seg(n, s)] != piece) return $"{s} is not in the same piece as {segs[0]}";
            return null;
        }

        private static string NotCore(CutResult r, NpcAsset n, params string[] segs)
        {
            foreach (var s in segs)
                if (r.SegmentPiece[Seg(n, s)] == r.CorePiece) return $"{s} should have been severed";
            return null;
        }

        private static string Core(CutResult r, NpcAsset n, params string[] segs)
        {
            foreach (var s in segs)
                if (r.SegmentPiece[Seg(n, s)] != r.CorePiece) return $"{s} should still be attached";
            return null;
        }

        private static string All(params string[] errs) => errs.FirstOrDefault(e => e != null);

        public static List<CutCase> Cases(NpcAsset n)
        {
            var head = O(n, "Head_M"); var chest = O(n, "Chest_M");
            var shR = O(n, "Shoulder_R"); var elR = O(n, "Elbow_R");
            var elL = O(n, "Elbow_L"); var wrL = O(n, "Wrist_L");
            var hipL = O(n, "Hip_L"); var kneeL = O(n, "Knee_L");

            var list = new List<CutCase>
            {
                new CutCase
                {
                    Name = "decapitation", Description = "Tilted cut through the neck",
                    Region = Through(new Vector3(0, 1.53f, 0.0f), new Vector3(0.15f, 1f, 0.35f)),
                    Expect = (r, a) => All(NotCore(r, a, "Head_M", "Jaw_M"), Core(r, a, "Chest_M", "Shoulder_L", "Shoulder_R"), PieceWith(r, a, "Head_M", "Jaw_M")),
                },
                new CutCase
                {
                    Name = "upper_arm_r", Description = "Oblique cut through the right upper arm", Only = new[] { "Shoulder_R" },
                    Region = Through((shR + elR) * 0.5f, Vector3.Normalize(elR - shR) + new Vector3(0.0f, 0.0f, 0.5f)),
                    Expect = (r, a) => All(NotCore(r, a, "Elbow_R", "Wrist_R"), Core(r, a, "Chest_M", "Head_M"), PieceWith(r, a, "Elbow_R", "Wrist_R")),
                },
                new CutCase
                {
                    Name = "forearm_l", Description = "Cut through the left forearm", Only = new[] { "Elbow_L" },
                    Region = Through((elL + wrL) * 0.5f, Vector3.Normalize(wrL - elL) + new Vector3(0.3f, 0, 0)),
                    Expect = (r, a) => All(NotCore(r, a, "Wrist_L"), Core(r, a, "Elbow_L", "Shoulder_L")),
                },
                new CutCase
                {
                    Name = "thigh_l", Description = "Cut through the left thigh", Only = new[] { "Hip_L" },
                    Region = Through((hipL + kneeL) * 0.5f, new Vector3(0.2f, 1f, -0.2f)),
                    Expect = (r, a) => All(NotCore(r, a, "Knee_L", "Ankle_L"), Core(r, a, "Hip_R", "Knee_R", "Spine_M")),
                },
                new CutCase
                {
                    Name = "picture1_torso_diagonal", Description = "Reference picture #1: right waist/forearm up to the left shoulder",
                    Region = FrontLine(new Vector2(0.30f, 1.00f), new Vector2(-0.26f, 1.46f)),
                    Expect = (r, a) => All(NotCore(r, a, "Head_M", "Chest_M")),
                },
                new CutCase
                {
                    Name = "saber_arc_torso", Description = "Lightsaber steered in an arc: right waist up to the left shoulder, bowing 11 cm",
                    Region = Arc(FrontLine(new Vector2(0.30f, 1.00f), new Vector2(-0.26f, 1.46f)), -0.36f, 0.36f, 0.6f),
                    Expect = (r, a) => All(NotCore(r, a, "Head_M", "Chest_M")),
                },
                new CutCase
                {
                    Name = "picture2_armpit_to_knee", Description = "Reference picture #2: right arm, through the torso and pelvis, out the left knee",
                    Region = FrontLine(new Vector2(0.345f, 1.34f), new Vector2(-0.235f, 0.23f)),
                    Expect = (r, a) => null,
                },
                new CutCase
                {
                    Name = "picture3_head_to_shoulder", Description = "Reference picture #3: through the head and neck down to the left shoulder",
                    Region = FrontLine(new Vector2(0.197f, 1.72f), new Vector2(-0.308f, 1.21f)),
                    Expect = (r, a) => null,
                },
                new CutCase
                {
                    Name = "picture4_arm_lengthwise", Description = "Reference picture #4: near-vertical slice taking the left arm off at the shoulder",
                    Region = FrontLine(new Vector2(-0.17f, 1.556f), new Vector2(-0.324f, 0.519f)),
                    Expect = (r, a) => All(NotCore(r, a, "Shoulder_L", "Elbow_L", "Wrist_L"), Core(r, a, "Head_M", "Chest_M", "Shoulder_R")),
                },
                new CutCase
                {
                    Name = "skull_top", Description = "Slice off the top of the skull (no body to ride on: free chunk)",
                    Region = Through(new Vector3(0, 1.735f, 0.02f), new Vector3(0.25f, 1f, 0.4f)),
                    Expect = (r, a) => r.Pieces.Any(p => p.IsFreeChunk) ? Core(r, a, "Head_M") : "expected a free chunk for the skull cap",
                },
                new CutCase
                {
                    Name = "waist_short_blade", Description = "Waist cut with a blade that only spans the torso: the arms are out of reach",
                    Region = new CutRegion
                    {
                        Origin = new Vector3(0, 1.06f, 0.2f), Normal = Vector3.UnitY, AxisU = Vector3.UnitX, AxisW = -Vector3.UnitZ,
                        UMin = -0.2f, UMax = 0.2f, WMin = 0f, Margin = 0.01f,
                    },
                    Expect = (r, a) => All(NotCore(r, a, "Spine_M", "Chest_M", "Head_M", "Shoulder_L", "Shoulder_R", "Wrist_L", "Wrist_R"), Core(r, a, "Hip_L", "Hip_R")),
                },
            };
            return list;
        }

        public static readonly List<string> Exported = new List<string>();

        public static int Run(NpcAsset npc, string outDir)
        {
            Directory.CreateDirectory(outDir);
            int fails = 0;
            var names = Exported;
            names.Clear();
            foreach (var c in Cases(npc))
            {
                var solver = new CutSolver(npc.Body, npc.Skins.ToArray(), npc.Pose, c.Region);
                var report = solver.Analyze();
                var only = c.Only?.Select(n => Seg(npc, n)).ToHashSet();
                var sever = report.Groups.Where(g => g.InReach && g.FullyCovered && (only == null || g.Segments.Overlaps(only))).Select(g => g.Id).ToList();
                var result = solver.Execute(sever, new CutOptions());

                Console.WriteLine();
                Console.WriteLine($"== {c.Name}: {c.Description}");
                Console.WriteLine($"   analysis {report.MillisecondsSkinning:F1}+{report.MillisecondsSections:F1} ms, execute {result.Milliseconds:F1} ms ({result.Timings}); {report.Loops.Count} loops, {report.Groups.Count} sections");
                foreach (var g in report.Groups)
                {
                    float energy = CutEnergy.RequiredEnergy(g, npc.Body, CutMaterial.Flesh);
                    Console.WriteLine($"   section {g.Id}: area {g.Area * 1e4f:F0} cm2, coverage {g.Coverage:P0}, reach={g.InReach}, segments [{string.Join(",", g.Segments.Select(s => npc.Body.Segments[s].Name))}], bones [{string.Join(",", g.BoneCrossings.Select(b => npc.Body.Segments[b.segment].Name))}], needs {energy:F0} J{(sever.Contains(g.Id) ? "  <- severed" : "")}");
                }
                foreach (var p in result.Pieces)
                {
                    int tris = p.Renderers.Sum(rp => rp.Unchanged ? npc.Skins[rp.SkinId].Mesh.TriangleCount : rp.Mesh.TriangleCount);
                    Console.WriteLine($"   piece {p.Index}{(p.IsCore ? " (core)" : p.IsFreeChunk ? " (free chunk)" : "")}: bodies [{string.Join(",", p.BodySegments.Select(s => npc.Body.Segments[s].Name))}] tris={tris} cap verts={p.Cap?.Mesh.VertexCount ?? 0}");
                }
                if (result.FreedJoints.Count > 0)
                    Console.WriteLine($"   freed joints: {string.Join(", ", result.FreedJoints.Select(s => npc.Body.Segments[s].Name))}; chunk colliders: {result.ChunkColliders.Count}");

                var problems = Validate(npc, solver, result);
                string expect = c.Expect?.Invoke(result, npc);
                if (expect != null) problems.Add("expectation: " + expect);
                if (problems.Count == 0) Console.WriteLine("   OK");
                foreach (var pr in problems) { Console.WriteLine("   FAIL " + pr); fails++; }

                Export(npc, c, result, outDir);
                names.Add(c.Name);
            }
            WriteViewer(outDir, names);
            return fails;
        }

        // --------------------------------------------------------------------------------------------
        // Validation
        // --------------------------------------------------------------------------------------------

        /// <summary>Positions of an output renderer piece in world space at the moment of the cut.</summary>
        public static Vector3[] WorldPositions(NpcAsset npc, Piece piece, RendererPiece rp)
        {
            var md = rp.Mesh;
            var result = new Vector3[md.VertexCount];
            if (piece.IsFreeChunk)
            {
                for (int v = 0; v < md.VertexCount; v++) result[v] = md.Positions[v] + piece.Origin;
                return result;
            }
            Matrix4x4[] mats;
            if (rp.UsesOriginalBones)
            {
                var skin = npc.Skins[rp.SkinId];
                mats = new Matrix4x4[skin.BoneWorld.Length];
                for (int b = 0; b < mats.Length; b++) mats[b] = md.Bindposes[b] * skin.BoneWorld[b];
            }
            else
            {
                mats = new Matrix4x4[rp.BoneSegments.Length];
                for (int b = 0; b < mats.Length; b++) mats[b] = md.Bindposes[b] * npc.Pose.SegmentBodyWorld[rp.BoneSegments[b]];
            }
            for (int v = 0; v < md.VertexCount; v++)
            {
                var w = md.Weights[v];
                Vector3 p = Vector3.Zero;
                for (int k = 0; k < 4; k++)
                    if (w.Weight(k) > 0) p += Vector3.Transform(md.Positions[v], mats[w.Bone(k)]) * w.Weight(k);
                result[v] = p;
            }
            return result;
        }

        public static Vector3[] CapWorld(NpcAsset npc, Piece piece)
        {
            var md = piece.Cap.Mesh;
            var r = new Vector3[md.VertexCount];
            for (int v = 0; v < md.VertexCount; v++)
            {
                if (piece.IsFreeChunk) { r[v] = md.Positions[v] + piece.Origin; continue; }
                int seg = piece.Cap.BoneSegments[md.Weights[v].I0];
                var world = piece.Cap.UseTargetBones ? npc.Pose.SegmentTargetWorld[seg] : npc.Pose.SegmentBodyWorld[seg];
                r[v] = Vector3.Transform(md.Positions[v], md.Bindposes[md.Weights[v].I0] * world);
            }
            return r;
        }

        private static double Area(Vector3[] p, int[] tris)
        {
            double a = 0;
            for (int i = 0; i + 2 < tris.Length; i += 3)
                a += Vector3.Cross(p[tris[i + 1]] - p[tris[i]], p[tris[i + 2]] - p[tris[i]]).Length() * 0.5;
            return a;
        }

        private static List<string> Validate(NpcAsset npc, CutSolver solver, CutResult result)
        {
            var problems = new List<string>();

            // 1) Surface area is conserved: every triangle ends up somewhere, split pieces add up.
            for (int si = 0; si < npc.Skins.Count; si++)
            {
                var skin = npc.Skins[si];
                var mesh = skin.Mesh;
                var mats = new Matrix4x4[skin.BoneWorld.Length];
                for (int b = 0; b < mats.Length; b++) mats[b] = mesh.Bindposes[b] * skin.BoneWorld[b];
                var world = new Vector3[mesh.VertexCount];
                for (int v = 0; v < world.Length; v++)
                {
                    var w = mesh.Weights[v];
                    for (int k = 0; k < 4; k++)
                        if (w.Weight(k) > 0) world[v] += Vector3.Transform(mesh.Positions[v], mats[w.Bone(k)]) * w.Weight(k);
                }
                double before = mesh.Submeshes.Sum(s => Area(world, s));
                double after = 0;
                foreach (var p in result.Pieces)
                    foreach (var rp in p.Renderers.Where(r => r.SkinId == skin.Id))
                    {
                        if (rp.Unchanged) { after += before; continue; }
                        var wp = WorldPositions(npc, p, rp);
                        foreach (var v in wp)
                            if (float.IsNaN(v.X) || float.IsInfinity(v.X)) { problems.Add($"NaN position in {rp.Mesh.Name}"); break; }
                        after += rp.Mesh.Submeshes.Sum(s => Area(wp, s));
                        // Weights.
                        if (rp.Mesh.Weights != null)
                        {
                            int bones = rp.UsesOriginalBones ? skin.BoneWorld.Length : rp.BoneSegments.Length;
                            foreach (var w in rp.Mesh.Weights)
                            {
                                float sum = w.W0 + w.W1 + w.W2 + w.W3;
                                if (Math.Abs(sum - 1f) > 1e-3f || w.I0 >= bones || w.I1 >= bones || w.I2 >= bones || w.I3 >= bones)
                                { problems.Add($"bad bone weight in {rp.Mesh.Name}: sum {sum}"); break; }
                            }
                        }
                        // Index range.
                        foreach (var s in rp.Mesh.Submeshes)
                            if (s.Any(i => i < 0 || i >= rp.Mesh.VertexCount)) { problems.Add($"index out of range in {rp.Mesh.Name}"); break; }
                    }
                double rel = before > 0 ? Math.Abs(after - before) / before : 0;
                if (rel > 1e-3) problems.Add($"surface area of {mesh.Name} changed by {rel:P2} ({before:F4} -> {after:F4} m2)");
            }

            // 2) Every severed cross-section has a cap on both sides (when a cap-capable renderer crosses it).
            int caps = result.Pieces.Count(p => p.Cap != null);
            if (result.SeveredGroups.Count > 0 && caps < 2) problems.Add($"expected caps on both sides, found {caps} capped pieces");

            // 3) Cap outline matches the section outline: cap vertices lie on the plane.
            foreach (var p in result.Pieces.Where(p => p.Cap != null))
            {
                var cw = CapWorld(npc, p);
                float maxOff = cw.Max(x => Math.Abs(solver.Region.SignedDistance(x)));
                if (maxOff > 0.002f) problems.Add($"cap of piece {p.Index} is {maxOff * 1000:F1} mm off the plane");
            }

            // 4) Caps carry the pose-space channel the NPC's shader reads (UV1 = model-space position), and it maps back
            //    onto where the cap is: skinning UV1 with the reference renderer's bones lands on the cap.
            var poseSkin = npc.Skins.Where(s => s.AllowCaps && s.Mesh.UV[1] != null && s.Mesh.UVDimension[1] >= 3)
                                    .OrderByDescending(s => s.Mesh.TriangleCount).FirstOrDefault();
            if (poseSkin != null)
                foreach (var p in result.Pieces.Where(p => p.Cap != null))
                {
                    var md = p.Cap.Mesh;
                    if (md.UV[1] == null || md.UVDimension[1] != 3) { problems.Add($"cap of piece {p.Index} has no pose-space UV1"); continue; }
                    if (p.IsFreeChunk) continue;
                    var cw = CapWorld(npc, p);
                    float worst = 0f;
                    for (int v = 0; v < md.VertexCount; v++)
                    {
                        int seg = p.Cap.BoneSegments[md.Weights[v].I0];
                        int bone = poseSkin.SegmentBone[seg] >= 0 ? poseSkin.SegmentBone[seg] : Array.IndexOf(poseSkin.BoneSegment, seg);
                        if (bone < 0) continue;
                        var m = poseSkin.Mesh.Bindposes[bone] * poseSkin.BoneWorld[bone];
                        var uv = md.UV[1][v];
                        worst = Math.Max(worst, Vector3.Distance(Vector3.Transform(new Vector3(uv.X, uv.Y, uv.Z), m), cw[v]));
                    }
                    if (worst > 0.001f) problems.Add($"cap of piece {p.Index}: pose-space UV1 is {worst * 1000:F1} mm off");
                }
            return problems;
        }

        // --------------------------------------------------------------------------------------------
        // Export for the local viewer
        // --------------------------------------------------------------------------------------------

        private static void Export(NpcAsset npc, CutCase c, CutResult result, string outDir)
        {
            var sb = new StringBuilder();
            var inv = CultureInfo.InvariantCulture;
            sb.Append("window.CUTS=window.CUTS||{};window.CUTS[").Append(Q(c.Name)).Append("]={desc:").Append(Q(c.Description)).Append(",normal:[")
              .Append(string.Join(",", new[] { c.Region.Normal.X, c.Region.Normal.Y, c.Region.Normal.Z }.Select(f => f.ToString("F4", inv))))
              .Append("],pieces:[");
            bool firstPiece = true;
            foreach (var p in result.Pieces)
            {
                // Push pieces apart along the plane normal so the caps are visible.
                var side = Vector3.Zero;
                if (!p.IsCore)
                {
                    float sd = 0; int cnt = 0;
                    foreach (int s in p.BodySegments) { sd += c.Region.SignedDistance(npc.Pose.SegmentOrigin[s]); cnt++; }
                    if (p.IsFreeChunk) { sd = c.Region.SignedDistance(p.Origin); cnt = 1; }
                    side = c.Region.Normal * (sd >= 0 ? 0.12f : -0.12f);
                }
                if (!firstPiece) sb.Append(',');
                firstPiece = false;
                sb.Append("{core:").Append(p.IsCore ? "true" : "false").Append(",free:").Append(p.IsFreeChunk ? "true" : "false").Append(",meshes:[");
                bool firstMesh = true;
                foreach (var rp0 in p.Renderers)
                {
                    var rp = rp0.Unchanged ? new RendererPiece { SkinId = rp0.SkinId, Mesh = npc.Skins[rp0.SkinId].Mesh, UsesOriginalBones = true } : rp0;
                    var wp = WorldPositions(npc, p, rp);
                    var tris = rp.Mesh.Submeshes.SelectMany(s => s).ToArray();
                    bool hair = !npc.Skins[rp.SkinId].AllowCaps;
                    AppendMesh(sb, ref firstMesh, wp, side, tris, hair ? "hair" : "skin");
                }
                if (p.Cap != null)
                    AppendMesh(sb, ref firstMesh, CapWorld(npc, p), side, p.Cap.Mesh.Submeshes[0], "cap", p.Cap.Mesh.UV[0]);
                sb.Append("]}");
            }
            sb.Append("]};");
            File.WriteAllText(Path.Combine(outDir, "cut_" + c.Name + ".js"), sb.ToString());
        }

        internal static void AppendMesh(StringBuilder sb, ref bool first, Vector3[] pos, Vector3 offset, int[] tris, string kind, Vector4[] uv = null)
        {
            var inv = CultureInfo.InvariantCulture;
            if (!first) sb.Append(',');
            first = false;
            sb.Append("{kind:").Append(Q(kind)).Append(",p:[");
            for (int i = 0; i < pos.Length; i++)
            {
                var v = pos[i] + offset;
                if (i > 0) sb.Append(',');
                sb.Append(v.X.ToString("0.####", inv)).Append(',').Append(v.Y.ToString("0.####", inv)).Append(',').Append(v.Z.ToString("0.####", inv));
            }
            sb.Append("],i:[").Append(string.Join(",", tris)).Append(']');
            if (uv != null)
            {
                sb.Append(",uv:[");
                for (int i = 0; i < uv.Length; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(uv[i].X.ToString("0.###", inv)).Append(',').Append(uv[i].Y.ToString("0.###", inv));
                }
                sb.Append(']');
            }
            sb.Append('}');
        }

        internal static string Q(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

        internal static void WriteViewer(string outDir, List<string> names)
        {
            long stamp = DateTime.UtcNow.Ticks;
            var scripts = string.Join("\n", names.Select(n => $"<script src=\"cut_{n}.js?v={stamp}\"></script>"));
            File.WriteAllText(Path.Combine(outDir, "index.html"), ViewerHtml.Replace("%SCRIPTS%", scripts));
        }

        private const string ViewerHtml = @"<!doctype html>
<html><head><meta charset='utf-8'><title>Dismember cut preview</title>
<meta name='viewport' content='width=device-width,initial-scale=1'>
<style>
html,body{margin:0;height:100%;background:#15171a;color:#ddd;font:13px system-ui,sans-serif;overflow:hidden}
#ui{position:absolute;top:8px;left:8px;z-index:2;background:#000a;padding:8px 10px;border-radius:6px;max-width:420px}
select{font:inherit;background:#222;color:#eee;border:1px solid #444;border-radius:4px;padding:3px}
#desc{margin-top:6px;color:#aaa}
canvas{display:block}
</style></head><body>
<div id='ui'><select id='pick'></select> <select id='only'><option value='-1'>all pieces</option></select> <label><input type='checkbox' id='split' checked> explode</label><div id='desc'></div></div>
%SCRIPTS%
<script src='https://cdnjs.cloudflare.com/ajax/libs/three.js/0.160.0/three.min.js'></script>
<script>
const r=new THREE.WebGLRenderer({antialias:true});r.setSize(innerWidth,innerHeight);r.setPixelRatio(devicePixelRatio);document.body.appendChild(r.domElement);
const scene=new THREE.Scene();scene.background=new THREE.Color(0x15171a);
const cam=new THREE.PerspectiveCamera(35,innerWidth/innerHeight,0.01,50);
scene.add(new THREE.HemisphereLight(0xffffff,0x333344,1.1));const dl=new THREE.DirectionalLight(0xffffff,1.4);dl.position.set(1.5,3,2);scene.add(dl);
const dl2=new THREE.DirectionalLight(0xffffff,0.6);dl2.position.set(-2,1,-2);scene.add(dl2);
function capTex(){const c=document.createElement('canvas');c.width=c.height=256;const g=c.getContext('2d');
 for(let y=0;y<256;y++)for(let x=0;x<128;x++){const n=Math.random()*30+Math.sin(y*0.4+x*0.05)*12;g.fillStyle=`rgb(${140+n|0},${22+n*0.3|0},${28+n*0.2|0})`;g.fillRect(x,y,1,1);}
 const gr=g.createLinearGradient(128,0,192,0);gr.addColorStop(0,'#d9a48c');gr.addColorStop(0.15,'#c98a73');gr.addColorStop(0.25,'#f0d27a');gr.addColorStop(0.9,'#e8c060');gr.addColorStop(1,'#a8323a');g.fillStyle=gr;g.fillRect(128,0,64,256);
 g.fillStyle='#8c1d24';g.fillRect(192,0,64,256);g.beginPath();g.arc(224,128,27,0,7);g.fillStyle='#efe6cf';g.fill();g.beginPath();g.arc(224,128,14,0,7);g.fillStyle='#7a1e22';g.fill();
 const t=new THREE.CanvasTexture(c);t.colorSpace=THREE.SRGBColorSpace;return t;}
const tex=capTex();
const mats={skin:new THREE.MeshStandardMaterial({color:0xb8a99a,roughness:0.75,side:THREE.DoubleSide}),
 core:new THREE.MeshStandardMaterial({color:0x9aa7b8,roughness:0.75,side:THREE.DoubleSide}),
 hair:new THREE.MeshStandardMaterial({color:0x3a2c22,roughness:0.9,side:THREE.DoubleSide}),
 cap:new THREE.MeshStandardMaterial({map:tex,roughness:0.45})};
let group=null;
function show(name){if(group)scene.remove(group);group=new THREE.Group();const c=window.CUTS[name];document.getElementById('desc').textContent=c.desc+'  ('+c.pieces.length+' pieces)';
 const explode=document.getElementById('split').checked;const only=+document.getElementById('only').value;
 const sel=document.getElementById('only');while(sel.options.length>1)sel.remove(1);c.pieces.forEach((p,pi)=>{const o=document.createElement('option');o.value=pi;o.textContent='piece '+pi+(p.core?' (core)':p.free?' (free)':'');sel.appendChild(o);});sel.value=only<c.pieces.length?only:-1;
 c.pieces.forEach((p,pi)=>{if(only>=0&&pi!==only)return;p.meshes.forEach(m=>{const g=new THREE.BufferGeometry();let pos=new Float32Array(m.p);
  if(!explode&&!p.core){const n=c.normal;}
  g.setAttribute('position',new THREE.BufferAttribute(pos,3));if(m.uv)g.setAttribute('uv',new THREE.BufferAttribute(new Float32Array(m.uv),2));
  g.setIndex(m.i);g.computeVertexNormals();
  const mat=m.kind==='cap'?mats.cap:m.kind==='hair'?mats.hair:(p.core?mats.core:mats.skin);
  group.add(new THREE.Mesh(g,mat));});});
 scene.add(group);}
const pick=document.getElementById('pick');Object.keys(window.CUTS).forEach(k=>{const o=document.createElement('option');o.value=o.textContent=k;pick.appendChild(o);});
pick.onchange=()=>{document.getElementById('only').value=-1;show(pick.value);};document.getElementById('split').onchange=()=>show(pick.value);document.getElementById('only').onchange=()=>show(pick.value);
let yaw=0.5,pitch=0.1,dist=2.6,target=new THREE.Vector3(0,1.05,0),drag=null;
function place(){cam.position.set(target.x+dist*Math.sin(yaw)*Math.cos(pitch),target.y+dist*Math.sin(pitch),target.z+dist*Math.cos(yaw)*Math.cos(pitch));cam.lookAt(target);}
r.domElement.onpointerdown=e=>{drag=[e.clientX,e.clientY];};onpointerup=()=>drag=null;
onpointermove=e=>{if(!drag)return;yaw-=(e.clientX-drag[0])*0.01;pitch=Math.max(-1.4,Math.min(1.4,pitch+(e.clientY-drag[1])*0.01));drag=[e.clientX,e.clientY];};
r.domElement.onwheel=e=>{dist=Math.max(0.4,Math.min(8,dist*(1+e.deltaY*0.001)));e.preventDefault();};
onresize=()=>{r.setSize(innerWidth,innerHeight);cam.aspect=innerWidth/innerHeight;cam.updateProjectionMatrix();};
const hash=decodeURIComponent(location.hash.slice(1));if(hash&&window.CUTS[hash])pick.value=hash;show(pick.value);
(function loop(){place();r.render(scene,cam);requestAnimationFrame(loop);})();
</script></body></html>";
    }
}
