using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace Dismember.Game
{
    /// <summary>
    /// Blood that lands: spots on the floor and walls where the spray from cuts and stumps comes down, which pool up
    /// where it keeps dripping and dry away after a while. A few droplets of each spray are flown ahead (gravity,
    /// raycasts) to find where they land, and a spot appears there as they arrive. Spots are shaped by their mesh (a
    /// ragged blob stretched the way the drop was flying, with a few spatters ahead of it), all in one mesh.
    /// </summary>
    internal static class Splats
    {
        private struct Spot
        {
            public Vector3 Pos, Normal;
            public Vector3 Along;        // on the surface, the way the drop was travelling
            public float Size, Stretch, Born, Shade, Lift;
            public int Seed;
            public GorePalette Palette;
        }

        private const int MaxSpots = 500;
        private const float Life = 40f;          // seconds before a spot has dried away
        private const float Fade = 6f;           // the last seconds it shrinks
        private const int RagdollLayer = 12;     // NPC ragdoll bodies
        private const int Rim = 12;              // outline points of a spot

        private static readonly List<Spot> Spots = new List<Spot>();
        private static GameObject _go;
        private static Mesh _mesh;
        private static bool _dirty;
        private static float _nextRebuild;
        private static int _lift;
        private static readonly System.Random Rng = new System.Random();

        public static int Count => Spots.Count;

        /// <summary>
        /// Flies <paramref name="droplets"/> worth of a spray from <paramref name="pos"/> along <paramref name="dir"/>
        /// (a few of them, really) and leaves a spot where each lands.
        /// </summary>
        public static void Spray(Vector3 pos, Vector3 dir, int droplets, GorePalette palette)
        {
            if (Gore.Amount <= 0f || palette == GorePalette.Bone || palette == GorePalette.Seared) return;
            int n = Math.Clamp(droplets / 12, 1, 6);
            var forward = dir.sqrMagnitude > 1e-6f ? dir.normalized : Vector3.down;
            for (int i = 0; i < n; i++)
            {
                // Like the particles: a 28 degree cone, 1.2-3.8 m/s.
                var v = Quaternion.AngleAxis((float)Rng.NextDouble() * 360f, forward) *
                        Quaternion.AngleAxis((float)Rng.NextDouble() * 28f, Perp(forward)) * forward *
                        (1.2f + (float)Rng.NextDouble() * 2.6f);
                if (!Land(pos, v, out var hit, out float t, out var vHit)) continue;
                // A drop coming in at a slant smears out along the way it was going.
                var along = vHit - hit.normal * Vector3.Dot(vHit, hit.normal);
                float slant = vHit.sqrMagnitude > 1e-6f ? along.magnitude / vHit.magnitude : 0f;
                var spot = new Spot
                {
                    Pos = hit.point,
                    Normal = hit.normal,
                    Along = along.sqrMagnitude > 1e-6f ? along.normalized : Perp(hit.normal),
                    Size = 0.02f + (float)Rng.NextDouble() * 0.035f,
                    Stretch = 1f + 1.2f * slant * slant,
                    Shade = (float)Rng.NextDouble(),
                    Seed = Rng.Next(),
                    Palette = palette,
                };
                // It appears when the droplet gets there.
                Cleanup.After(t, () => Add(spot));
            }
        }

        /// <summary>
        /// Follows a droplet under gravity until it hits something, up to 1.5 s. Only the level itself keeps a spot:
        /// anything with a rigidbody (the player, a held weapon, a ragdoll, a prop) moves away and would leave the spot
        /// hanging in the air, so a drop that hits one is gone.
        /// </summary>
        private static bool Land(Vector3 p, Vector3 v, out RaycastHit hit, out float time, out Vector3 vHit)
        {
            const float Step = 0.04f;
            var g = Physics.gravity;
            int mask = ~(1 << RagdollLayer);
            time = 0f;
            for (int i = 0; i < 38; i++)
            {
                var next = p + v * Step + 0.5f * g * Step * Step;
                var d = next - p;
                float len = d.magnitude;
                if (len > 1e-5f && Physics.Raycast(p, d / len, out hit, len, mask, QueryTriggerInteraction.Ignore))
                {
                    time += Step * hit.distance / len;
                    vHit = v;
                    return hit.collider != null && hit.collider.attachedRigidbody == null;
                }
                p = next;
                v += g * Step;
                time += Step;
            }
            hit = default;
            vHit = v;
            return false;
        }

        private static void Add(Spot s)
        {
            s.Born = Time.time;
            // Land on a spot already there and the pool grows a little instead of stacking up.
            for (int i = Spots.Count - 1; i >= 0 && i >= Spots.Count - 60; i--)
            {
                var o = Spots[i];
                if ((o.Pos - s.Pos).sqrMagnitude < o.Size * o.Size * 0.3f && Vector3.Dot(o.Normal, s.Normal) > 0.8f)
                {
                    o.Size = Mathf.Min(0.14f, o.Size + s.Size * 0.25f);
                    o.Stretch = Mathf.Max(1f, Mathf.Lerp(o.Stretch, 1f, 0.2f));   // pools round off as they fill
                    o.Born = Time.time;
                    Spots[i] = o;
                    _dirty = true;
                    return;
                }
            }
            // Each spot sits a hair above the last, so overlapping ones do not flicker through each other.
            s.Lift = 0.0015f + (_lift++ % 12) * 0.00025f;
            if (Spots.Count >= MaxSpots) Spots.RemoveAt(0);
            Spots.Add(s);
            _dirty = true;
        }

        /// <summary>Called every frame: rebuilds the mesh when spots came or are drying up.</summary>
        public static void Update()
        {
            if (Spots.Count == 0 && !_dirty) return;
            float now = Time.time;
            if (now < _nextRebuild) return;
            int before = Spots.Count;
            Spots.RemoveAll(s => now - s.Born > Life);
            // Spots that just landed spread out, old ones dry up: both need the mesh redone as they change.
            bool changing = false;
            foreach (var s in Spots)
            {
                float age = now - s.Born;
                if (age < 0.35f || age > Life - Fade) { changing = true; break; }
            }
            if (!_dirty && !changing && Spots.Count == before) return;
            _nextRebuild = now + 0.1f;
            _dirty = false;
            try { Rebuild(now); }
            catch (Exception e)
            {
                Log.Debug("splats: " + e.Message);
                Spots.Clear();
            }
        }

        private static readonly List<Vector3> Pos = new List<Vector3>();
        private static readonly List<Vector3> Nrm = new List<Vector3>();
        private static readonly List<Vector4> Tan = new List<Vector4>();
        private static readonly List<Vector2> UV = new List<Vector2>();
        private static readonly List<int> Tris = new List<int>();

        private static void Rebuild(float now)
        {
            if (_go.IsDead() || _mesh.IsDead())
            {
                var mat = Gore.SplatMaterial();
                if (mat == null) { Spots.Clear(); return; }
                _go = new GameObject("Dismember_BloodSpots");
                _mesh = new Mesh { name = "DismemberBloodSpots" };
                _mesh.MarkDynamic();
                _go.AddComponent<MeshFilter>().sharedMesh = _mesh;
                var r = _go.AddComponent<MeshRenderer>();
                r.sharedMaterial = mat;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                r.receiveShadows = true;
            }
            Pos.Clear(); Nrm.Clear(); Tan.Clear(); UV.Clear(); Tris.Clear();
            foreach (var s in Spots)
            {
                float age = now - s.Born;
                float size = s.Size * Mathf.Clamp01((Life - age) / Fade) * Mathf.Lerp(0.6f, 1f, Mathf.Clamp01(age / 0.3f));
                if (size < 0.002f) continue;
                var rng = new System.Random(s.Seed);
                var t1 = s.Along;
                var t2 = Vector3.Cross(s.Normal, t1);
                var uv = Gore.PoolUV(s.Palette, s.Shade * 0.8f + 0.1f);
                var centre = s.Pos + s.Normal * s.Lift;
                // The main blob: a ragged outline, longer the way the drop was going.
                Blob(centre, s.Normal, t1, t2, size, s.Stretch, 0.22f, rng, uv);
                // Spatters thrown ahead of it.
                int spatters = rng.Next(0, 4);
                for (int k = 0; k < spatters; k++)
                {
                    float ahead = size * s.Stretch * (1.15f + (float)rng.NextDouble() * 0.9f);
                    float aside = size * ((float)rng.NextDouble() - 0.5f) * 1.1f;
                    float r = size * (0.12f + (float)rng.NextDouble() * 0.18f);
                    Blob(centre + t1 * ahead + t2 * aside, s.Normal, t1, t2, r, 1f + 0.3f * (s.Stretch - 1f), 0.15f, rng, uv);
                }
            }
            _mesh.Clear();
            if (Pos.Count == 0) return;
            _mesh.vertices = new Il2CppStructArray<Vector3>(Pos.ToArray());
            _mesh.normals = new Il2CppStructArray<Vector3>(Nrm.ToArray());
            _mesh.tangents = new Il2CppStructArray<Vector4>(Tan.ToArray());
            _mesh.uv = new Il2CppStructArray<Vector2>(UV.ToArray());
            _mesh.triangles = new Il2CppStructArray<int>(Tris.ToArray());
            _mesh.RecalculateBounds();
        }

        /// <summary>A filled ragged outline around <paramref name="c"/>, facing along <paramref name="n"/>.</summary>
        private static void Blob(Vector3 c, Vector3 n, Vector3 t1, Vector3 t2, float r, float stretch, float ragged, System.Random rng, Vector2 uv)
        {
            int start = Pos.Count;
            var tan = new Vector4(t1.x, t1.y, t1.z, 1f);
            Pos.Add(c); Nrm.Add(n); Tan.Add(tan); UV.Add(uv);
            // Two wobbles at random phases: lumpy, but still one blob.
            float p1 = (float)rng.NextDouble() * 6.283f, p2 = (float)rng.NextDouble() * 6.283f;
            float a1 = ragged * (0.5f + (float)rng.NextDouble() * 0.5f), a2 = ragged * 0.6f * (float)rng.NextDouble();
            for (int k = 0; k < Rim; k++)
            {
                float th = k * 6.283185f / Rim;
                float rr = r * (1f + a1 * Mathf.Sin(3f * th + p1) + a2 * Mathf.Sin(5f * th + p2));
                Pos.Add(c + t1 * (Mathf.Cos(th) * rr * stretch) + t2 * (Mathf.Sin(th) * rr));
                Nrm.Add(n); Tan.Add(tan); UV.Add(uv);
            }
            // t1 x t2 = n, so the angle runs anticlockwise seen from the front: fan centre, next, this (clockwise, the
            // way Unity faces triangles towards the viewer).
            for (int k = 0; k < Rim; k++)
            {
                Tris.Add(start);
                Tris.Add(start + 1 + (k + 1) % Rim);
                Tris.Add(start + 1 + k);
            }
        }

        /// <summary>A new level: the spots belonged to the last one.</summary>
        public static void OnLevelLoaded()
        {
            Spots.Clear();
            _go = null;
            _mesh = null;
            _dirty = false;
        }

        private static Vector3 Perp(Vector3 d) => Vector3.Cross(d, Mathf.Abs(d.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
    }
}
