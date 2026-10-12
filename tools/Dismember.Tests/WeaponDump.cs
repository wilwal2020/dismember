using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AssetsTools.NET;
using AssetsTools.NET.Extra;

namespace Dismember.Tests
{
    /// <summary>
    /// Prints a weapon prefab from its spawnable bundle: transforms, colliders, rigidbody and the StabSlash blade
    /// setup. Used to see why a weapon's edges are not found or not hit. `dotnet run -- dump path\to\weapon.bundle`
    /// </summary>
    public static class WeaponDump
    {
        public static void Run(string bundle)
        {
            var am = new AssetsManager();
            var bun = am.LoadBundleFile(bundle, true);
            var af = am.LoadAssetsFileFromBundle(bun, 0, false);
            var names = new Dictionary<long, string>();
            foreach (var info in af.file.GetAssetsOfType(AssetClassID.GameObject))
                names[info.PathId] = am.GetBaseField(af, info)["m_Name"].AsString;

            var xf = new Dictionary<long, (long go, long father, Vector3 pos, Quaternion rot, Vector3 scale)>();
            foreach (var info in af.file.GetAssetsOfType(AssetClassID.Transform))
            {
                var bf = am.GetBaseField(af, info);
                xf[info.PathId] = (bf["m_GameObject.m_PathID"].AsLong, bf["m_Father.m_PathID"].AsLong, V3(bf["m_LocalPosition"]),
                    new Quaternion(bf["m_LocalRotation.x"].AsFloat, bf["m_LocalRotation.y"].AsFloat, bf["m_LocalRotation.z"].AsFloat, bf["m_LocalRotation.w"].AsFloat),
                    V3(bf["m_LocalScale"]));
            }
            var goXf = xf.ToDictionary(kv => kv.Value.go, kv => kv.Key);
            Matrix4x4 World(long id)
            {
                var x = xf[id];
                var local = Matrix4x4.CreateScale(x.scale) * Matrix4x4.CreateFromQuaternion(x.rot) * Matrix4x4.CreateTranslation(x.pos);
                return x.father != 0 && xf.ContainsKey(x.father) ? local * World(x.father) : local;
            }
            string Path(long xfId)
            {
                var parts = new List<string>();
                for (long id = xfId; id != 0 && xf.ContainsKey(id); id = xf[id].father) parts.Add(names.GetValueOrDefault(xf[id].go, "?"));
                parts.Reverse();
                return string.Join("/", parts);
            }
            string GoPath(long go) => goXf.TryGetValue(go, out var t) ? Path(t) : "go:" + go;
            string Fmt(Vector3 v) => $"({v.X:F3}, {v.Y:F3}, {v.Z:F3})";

            Console.WriteLine($"== {bundle}");
            var typeCounts = new Dictionary<string, int>();
            foreach (var info in af.file.AssetInfos)
            {
                string tn = ((AssetClassID)info.TypeId).ToString();
                typeCounts[tn] = typeCounts.TryGetValue(tn, out int tc) ? tc + 1 : 1;
            }
            Console.WriteLine("  asset types: " + string.Join(", ", typeCounts.OrderByDescending(k => k.Value).Select(k => k.Key + " x" + k.Value)));
            foreach (var info in af.file.GetAssetsOfType(AssetClassID.AnimationClip))
            {
                var bf = am.GetBaseField(af, info);
                var bindings = bf["m_ClipBindingConstant.genericBindings.Array"];
                Console.WriteLine($"  [clip] {bf["m_Name"].AsString} length? bindings {(bindings.IsDummy ? 0 : bindings.Children.Count)}");
                if (!bindings.IsDummy)
                    Console.WriteLine("     bindings (typeID:attribute hash): " + string.Join(" ", bindings.Children.Select(b => b["typeID"].AsInt + ":" + b["attribute"].AsUInt.ToString("x"))));
                foreach (var curve in new[] { "m_FloatCurves.Array", "m_PositionCurves.Array", "m_ScaleCurves.Array", "m_EditorCurves.Array" })
                {
                    var arr = bf[curve];
                    if (arr.IsDummy) continue;
                    foreach (var c in arr.Children)
                    {
                        string attr = c["attribute"].IsDummy ? "" : c["attribute"].AsString;
                        string path = c["path"].IsDummy ? "" : c["path"].AsString;
                        var keys = c["curve.m_Curve.Array"];
                        string vals = keys.IsDummy ? "" : string.Join(" ", keys.Children.Take(4).Select(k => k["time"].AsFloat.ToString("F2") + ":" + (k["value"].Children.Count > 0 ? string.Join("/", k["value"].Children.Select(x => x.AsFloat.ToString("F3"))) : k["value"].AsFloat.ToString("F3"))));
                        Console.WriteLine($"     {curve.Split('.')[0]} {path} {attr} {vals}");
                    }
                }
            }
            foreach (var kv in xf.OrderBy(k => Path(k.Key)))
            {
                var w = World(kv.Key);
                var right = Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitX, w));
                var up = Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitY, w));
                var fwd = Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitZ, w));
                var sc = new Vector3(Vector3.TransformNormal(Vector3.UnitX, w).Length(), Vector3.TransformNormal(Vector3.UnitY, w).Length(), Vector3.TransformNormal(Vector3.UnitZ, w).Length());
                Console.WriteLine($"  {Path(kv.Key)}  pos {Fmt(w.Translation)} right {Fmt(right)} up {Fmt(up)} fwd {Fmt(fwd)} local scale {Fmt(kv.Value.scale)} world scale {Fmt(sc)}");
            }
            void Colliders(AssetClassID id, string kind, Func<AssetTypeValueField, string> desc)
            {
                foreach (var info in af.file.GetAssetsOfType(id))
                {
                    var bf = am.GetBaseField(af, info);
                    Console.WriteLine($"  [{kind} #{info.PathId}] on {GoPath(bf["m_GameObject.m_PathID"].AsLong)} trigger={bf["m_IsTrigger"].AsBool} enabled={bf["m_Enabled"].AsBool} {desc(bf)}");
                }
            }
            Colliders(AssetClassID.BoxCollider, "box", bf => $"center {Fmt(V3(bf["m_Center"]))} size {Fmt(V3(bf["m_Size"]))}");
            Colliders(AssetClassID.CapsuleCollider, "capsule", bf => $"center {Fmt(V3(bf["m_Center"]))} r {bf["m_Radius"].AsFloat:F3} h {bf["m_Height"].AsFloat:F3} dir {bf["m_Direction"].AsInt}");
            Colliders(AssetClassID.MeshCollider, "mesh", bf => $"convex={bf["m_Convex"].AsBool}");
            Colliders(AssetClassID.SphereCollider, "sphere", bf => $"center {Fmt(V3(bf["m_Center"]))} r {bf["m_Radius"].AsFloat:F3}");
            foreach (var info in af.file.GetAssetsOfType(AssetClassID.Rigidbody))
            {
                var bf = am.GetBaseField(af, info);
                Console.WriteLine($"  [rigidbody] on {GoPath(bf["m_GameObject.m_PathID"].AsLong)} mass {bf["m_Mass"].AsFloat:F2}");
            }
            foreach (var info in af.file.GetAssetsOfType(AssetClassID.MonoBehaviour))
            {
                AssetTypeValueField bf;
                try { bf = am.GetBaseField(af, info); } catch { continue; }
                if (bf == null) continue;
                var blades = bf["slashBlades"];
                if (blades == null || blades.IsDummy) continue;
                Console.WriteLine($"  [StabSlash] on {GoPath(bf["m_GameObject.m_PathID"].AsLong)}");
                Dump(bf, "    ", 0, id => names.ContainsKey(id) ? "go " + GoPath(id) : xf.ContainsKey(id) ? "xf " + Path(id) : "#" + id);
            }
        }

        private static void Dump(AssetTypeValueField f, string indent, int depth, Func<long, string> resolve)
        {
            if (depth > 6) return;
            foreach (var c in f.Children)
            {
                if (c.FieldName.StartsWith("m_") && depth == 0 && c.FieldName != "m_Enabled") continue;
                if (c.TypeName == "PPtr`1" || c.TypeName.StartsWith("PPtr<"))
                {
                    long id = c["m_PathID"].AsLong;
                    Console.WriteLine($"{indent}{c.FieldName}: {(id == 0 ? "null" : resolve(id))}");
                    continue;
                }
                if (c.Value != null && c.Children.Count == 0)
                {
                    Console.WriteLine($"{indent}{c.FieldName}: {c.Value.AsString ?? c.AsString}");
                    continue;
                }
                if (c.TypeName == "Array" || c.TypeName == "vector")
                {
                    Console.WriteLine($"{indent}{c.FieldName}[{c.Children.Count}]");
                    for (int i = 0; i < Math.Min(8, c.Children.Count); i++)
                    {
                        Console.WriteLine($"{indent}  [{i}]");
                        Dump(c.Children[i], indent + "    ", depth + 1, resolve);
                    }
                    continue;
                }
                Console.WriteLine($"{indent}{c.FieldName} ({c.TypeName})");
                Dump(c, indent + "  ", depth + 1, resolve);
            }
        }

        private static Vector3 V3(AssetTypeValueField f) => new Vector3(f["x"].AsFloat, f["y"].AsFloat, f["z"].AsFloat);
    }
}
