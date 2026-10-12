using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Dismember.Core;

namespace Dismember.Tests
{
    public static class Program
    {
        public static int Main(string[] args)
        {
            if (args.Length > 1 && args[0] == "dump")
            {
                for (int i = 1; i < args.Length; i++) WeaponDump.Run(args[i]);
                return 0;
            }
            string game = args.Length > 0 ? args[0] : @"D:\SteamLibrary\steamapps\common\BONELAB";
            string npcName = args.Length > 1 ? args[1] : "ford";
            string outDir = args.Length > 2 ? args[2] : Path.Combine(AppContext.BaseDirectory, "out");
            string bundle = Path.Combine(game, @"BONELAB_Steam_Windows64_Data\StreamingAssets\aa\StandaloneWindows64\bonelabcontent_spawnables_assets_spawnable", npcName + ".bundle");

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var npc = NpcAsset.Load(bundle, null);
            if (npcName == "ford") ApplyFordMaterials(npc);
            Console.WriteLine($"Loaded {npc.Name} in {sw.ElapsedMilliseconds} ms: {npc.Body.Segments.Length} segments, {npc.Skins.Count} LOD0 renderers");
            foreach (var s in npc.Body.Segments)
                Console.WriteLine($"  seg {s.Index,2} {s.Name,-24} {s.Kind,-6} parent={s.Parent,2} origin={Fmt(npc.Pose.SegmentOrigin[s.Index])}");
            for (int i = 0; i < npc.Skins.Count; i++)
            {
                var m = npc.Skins[i].Mesh;
                Console.WriteLine($"  skin {i}: {npc.RendererNames[i]} verts={m.VertexCount} tris={m.TriangleCount} submeshes={m.Submeshes.Count} bones={npc.Skins[i].BoneWorld.Length} shapes={m.BlendShapes.Count} caps={npc.Skins[i].AllowCaps}");
            }

            // DISMEMBER_ONLY=slits (comma-separated) runs just those groups of checks.
            var only = (Environment.GetEnvironmentVariable("DISMEMBER_ONLY") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries);
            bool Run(string name) => only.Length == 0 || only.Contains(name);
            int failures = 0;
            if (Run("rest")) failures += Checks.RestPose(npc);
            if (Run("cuts")) failures += CutTests.Run(npc, outDir);
            if (Run("swings")) failures += SwingTests.Run(npc);
            if (Run("caps")) failures += CapCoverageTests.Run(npc);
            if (Run("holes")) failures += PieceHoleTests.Run(npc);
            if (Run("wounds")) failures += GashTests.Run(npc, outDir);
            if (only.Contains("slitdebug")) SlitDebug.Run(npc);   // diagnostics, only when asked for
            if (only.Contains("walldebug")) WallDebug.Run(npc);
            if (Run("slits")) failures += SlitTests.Run(npc, outDir);
            if (Run("bent"))
            {
                failures += CapCoverageTests.Run(npc, 120, bent: true);
                failures += PieceHoleTests.Run(npc, 80, bent: true);
            }
            if (Run("sounds")) failures += SoundTests.Run(outDir);
            Console.WriteLine();
            Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : $"{failures} CHECK(S) FAILED");
            Console.WriteLine($"Preview: {Path.Combine(outDir, "index.html")}");
            return failures == 0 ? 0 : 1;
        }

        public static string Fmt(Vector3 v) => $"({v.X:F3}, {v.Y:F3}, {v.Z:F3})";

        /// <summary>
        /// Which of Ford's submeshes are opaque flesh in game (material render queue below 2450), as the mod decides
        /// it at runtime: the body's zipper is alpha-cutout, the face has a transparent hair cap, eye shadow and cornea.
        /// </summary>
        private static void ApplyFordMaterials(NpcAsset npc)
        {
            var flesh = new Dictionary<string, bool[]>
            {
                ["brett_body_Converted"] = new[] { false, true },                          // zipper, body
                ["brett_face_Converted"] = new[] { true, false, false, false, true, true },  // teeth, hairCap, eyeshadow, cornea, eye, face
                ["brett_r_hand_Converted"] = new[] { true },
                ["brett_l_hand_Converted"] = new[] { true },
            };
            foreach (var s in npc.Skins)
            {
                if (!flesh.TryGetValue(s.Mesh.Name, out var caps)) continue;
                s.AllowCaps = true;
                s.SubmeshCaps = caps.Length == s.Mesh.Submeshes.Count ? caps : null;
            }
        }
    }

    public static class Checks
    {
        /// <summary>In the prefab's rest pose, CPU skinning should land (almost) on the bind-space vertices.</summary>
        public static int RestPose(NpcAsset npc)
        {
            int fails = 0;
            foreach (var skin in npc.Skins)
            {
                var mesh = skin.Mesh;
                float maxDev = 0f;
                double sumDev = 0;
                for (int v = 0; v < mesh.VertexCount; v++)
                {
                    var w = mesh.Weights[v];
                    Vector3 p = Vector3.Zero;
                    for (int k = 0; k < 4; k++)
                    {
                        if (w.Weight(k) <= 0) continue;
                        var m = mesh.Bindposes[w.Bone(k)] * skin.BoneWorld[w.Bone(k)];
                        p += Vector3.Transform(mesh.Positions[v], m) * w.Weight(k);
                    }
                    float d = Vector3.Distance(p, mesh.Positions[v]);
                    maxDev = Math.Max(maxDev, d);
                    sumDev += d;
                }
                Console.WriteLine($"  rest-pose check {mesh.Name}: mean deviation {sumDev / mesh.VertexCount * 1000:F2} mm, max {maxDev * 1000:F2} mm");
            }
            return fails;
        }
    }
}
