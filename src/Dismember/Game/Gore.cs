using System;
using System.Collections.Generic;
using UnityEngine;
using Random = System.Random;

namespace Dismember.Game
{
    internal enum GorePalette { Flesh, Void, Robot, Bone, Seared }

    /// <summary>Procedural cap textures/materials and blood particles. Nothing here needs asset bundles.</summary>
    internal static class Gore
    {
        private struct Colors
        {
            public Color32 Meat, MeatDark, Fat, Skin, Bone, Marrow, Blood1, Blood2;
            public float Smoothness, Metallic;
        }

        private static Colors PaletteColors(GorePalette p) => p switch
        {
            GorePalette.Void => new Colors
            {
                Meat = new Color32(46, 12, 64, 255), MeatDark = new Color32(22, 6, 32, 255), Fat = new Color32(96, 40, 128, 255),
                Skin = new Color32(24, 22, 28, 255), Bone = new Color32(185, 175, 205, 255), Marrow = new Color32(70, 0, 90, 255),
                Blood1 = new Color32(40, 0, 60, 255), Blood2 = new Color32(15, 0, 25, 255), Smoothness = 0.75f, Metallic = 0f,
            },
            GorePalette.Robot => new Colors
            {
                Meat = new Color32(70, 72, 78, 255), MeatDark = new Color32(40, 42, 46, 255), Fat = new Color32(35, 35, 38, 255),
                Skin = new Color32(120, 122, 128, 255), Bone = new Color32(155, 158, 165, 255), Marrow = new Color32(255, 150, 20, 255),
                Blood1 = new Color32(255, 190, 60, 255), Blood2 = new Color32(255, 110, 10, 255), Smoothness = 0.55f, Metallic = 0.6f,
            },
            GorePalette.Bone => new Colors
            {
                Meat = new Color32(214, 204, 178, 255), MeatDark = new Color32(170, 158, 130, 255), Fat = new Color32(190, 180, 150, 255),
                Skin = new Color32(230, 222, 200, 255), Bone = new Color32(242, 236, 222, 255), Marrow = new Color32(150, 118, 90, 255),
                Blood1 = new Color32(220, 214, 200, 255), Blood2 = new Color32(180, 170, 150, 255), Smoothness = 0.2f, Metallic = 0f,
            },
            GorePalette.Seared => new Colors
            {
                // Burnt shut by an energy blade: charred flesh, a rim still glowing at the edge, embers for blood.
                Meat = new Color32(70, 30, 20, 255), MeatDark = new Color32(26, 11, 8, 255), Fat = new Color32(46, 20, 12, 255),
                Skin = new Color32(255, 150, 50, 255), Bone = new Color32(72, 60, 48, 255), Marrow = new Color32(38, 16, 10, 255),
                Blood1 = new Color32(255, 205, 100, 255), Blood2 = new Color32(255, 110, 20, 255), Smoothness = 0.25f, Metallic = 0f,
            },
            _ => new Colors
            {
                // Fresh meat reads lighter than one expects; dark reds look like a hole in VR.
                Meat = new Color32(200, 58, 60, 255), MeatDark = new Color32(150, 32, 38, 255), Fat = new Color32(246, 224, 160, 255),
                Skin = new Color32(224, 172, 148, 255), Bone = new Color32(242, 236, 218, 255), Marrow = new Color32(160, 46, 46, 255),
                Blood1 = new Color32(120, 4, 8, 255), Blood2 = new Color32(70, 0, 4, 255), Smoothness = 0.62f, Metallic = 0f,
            },
        };

        private static readonly Dictionary<GorePalette, Texture2D> Atlases = new Dictionary<GorePalette, Texture2D>();
        private static readonly Dictionary<string, Material> CapMaterials = new Dictionary<string, Material>();
        private static readonly Dictionary<GorePalette, Material> BloodMaterials = new Dictionary<GorePalette, Material>();
        private static Texture2D _drop;

        public static float Amount = 1f;

        private static Texture2D Atlas(GorePalette p)
        {
            if (Atlases.TryGetValue(p, out var t) && !t.IsDead()) return t;
            var c = PaletteColors(p);
            const int S = 256;
            var px = new Color32[S * S];
            var rng = new Random(1234 + (int)p);
            for (int y = 0; y < S; y++)
            {
                for (int x = 0; x < S; x++)
                {
                    Color32 col;
                    if (x < 128)
                    {
                        // Flesh: muscle fibres plus noise.
                        float fib = 0.5f + 0.5f * MathF.Sin(y * 0.55f + MathF.Sin(x * 0.08f) * 3f);
                        float n = (float)rng.NextDouble();
                        float k = 0.55f * fib + 0.45f * n;
                        col = Lerp(c.MeatDark, c.Meat, 0.35f + 0.65f * k);
                        if (p == GorePalette.Robot && n > 0.985f) col = n > 0.993f ? new Color32(220, 40, 40, 255) : new Color32(40, 120, 230, 255);
                        if (p == GorePalette.Seared && n > 0.975f) col = Lerp(new Color32(255, 120, 30, 255), c.Meat, (n - 0.975f) * 20f);   // embers
                    }
                    else if (x < 192)
                    {
                        // Rim: skin -> fat -> flesh across the strip.
                        float u = (x - 128) / 63f;
                        if (u < 0.12f) col = c.Skin;
                        else if (u < 0.75f) col = Lerp(c.Fat, Lerp(c.Fat, c.Skin, 0.25f), (float)rng.NextDouble() * 0.3f);
                        else col = Lerp(c.Fat, c.Meat, (u - 0.75f) / 0.25f);
                    }
                    else
                    {
                        // Bone cross-section: cortical ring around marrow, flesh around it.
                        float dx = x - 224f, dy = y - 128f;
                        float d = MathF.Sqrt(dx * dx + dy * dy);
                        if (d < 13f) col = Lerp(c.Marrow, c.MeatDark, (float)rng.NextDouble() * 0.3f);
                        else if (d < 27f) col = Lerp(c.Bone, c.Fat, (float)rng.NextDouble() * 0.15f);
                        else col = c.Meat;
                    }
                    px[y * S + x] = col;
                }
            }
            var tex = new Texture2D(S, S, TextureFormat.RGBA32, true) { name = "DismemberAtlas_" + p, wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            tex.SetPixels32(px);
            tex.Apply(true);
            tex.hideFlags = HideFlags.DontUnloadUnusedAsset;
            Atlases[p] = tex;
            return tex;
        }

        private static Color32 Lerp(Color32 a, Color32 b, float t) => Color32.Lerp(a, b, Math.Clamp(t, 0f, 1f));

        private static readonly Dictionary<string, Texture2D> Solids = new Dictionary<string, Texture2D>();

        /// <summary>A tiny single-colour texture (flat normals, MAS maps...), cached.</summary>
        private static Texture2D Solid(string name, Color32 c, bool linear)
        {
            string key = name + c.r + "," + c.g + "," + c.b + "," + c.a;
            if (Solids.TryGetValue(key, out var t) && !t.IsDead()) return t;
            t = new Texture2D(4, 4, TextureFormat.RGBA32, false, linear) { name = "Dismember_" + name, wrapMode = TextureWrapMode.Repeat };
            var px = new Color32[16];
            for (int i = 0; i < px.Length; i++) px[i] = c;
            t.SetPixels32(px);
            t.Apply(false);
            t.hideFlags = HideFlags.DontUnloadUnusedAsset;
            Solids[key] = t;
            return t;
        }

        /// <summary>How cut faces are drawn (the "Cut face style" setting).</summary>
        public enum CapStyle { Auto = 0, NpcShader = 1, PlainLit = 2, Unlit = 3 }

        private const string LitMasName = "SLZ/LitMAS/LitMAS Standard";
        private static Material _litMasStandard;
        private static float _nextLitMasSearch;

        /// <summary>
        /// An opaque material drawn with SLZ's plain LitMAS shader. Copying one the game itself uses keeps a shader
        /// variant known to be in the build, without the pose-space extras the NPC skin shader expects from its meshes.
        /// Our own copy is kept, so it survives the level it came from being unloaded.
        /// </summary>
        private static Material LitMasStandard()
        {
            if (!_litMasStandard.IsDead() && _litMasStandard.shader != null) return _litMasStandard;
            _litMasStandard = null;
            if (Time.realtimeSinceStartup < _nextLitMasSearch) return null;
            _nextLitMasSearch = Time.realtimeSinceStartup + 5f;
            Material found = null;
            int fewest = int.MaxValue;
            try
            {
                foreach (var mat in Resources.FindObjectsOfTypeAll<Material>())
                {
                    if (mat.IsDead() || mat.shader == null || mat.shader.name != LitMasName) continue;
                    if (mat.renderQueue >= 2450 || mat.IsKeywordEnabled("_EMISSION")) continue;
                    int keywords = mat.shaderKeywords?.Length ?? 0;
                    if (keywords < fewest) { fewest = keywords; found = mat; }
                }
                if (found != null)
                {
                    _litMasStandard = new Material(found) { name = "DismemberLitMAS (" + found.name + ")", hideFlags = HideFlags.DontUnloadUnusedAsset };
                }
                else
                {
                    // No material to copy in this level: a fresh one from the shader itself.
                    foreach (var sh in Resources.FindObjectsOfTypeAll<Shader>())
                        if (!sh.IsDead() && sh.name == LitMasName && sh.isSupported)
                        {
                            _litMasStandard = new Material(sh) { name = "DismemberLitMAS (new)", hideFlags = HideFlags.DontUnloadUnusedAsset };
                            break;
                        }
                }
            }
            catch (Exception e) { Log.Debug("LitMAS search: " + e.Message); }
            if (_litMasStandard != null) Log.Debug("Cut faces can use " + _litMasStandard.name);
            return _litMasStandard;
        }

        /// <summary>A new level: look for templates again (the old ones may have been unloaded with the last level).</summary>
        public static void OnLevelLoaded()
        {
            _nextLitMasSearch = 0f;
            if (_particleTemplate.IsDead()) { _particleTemplate = null; _searchedParticleTemplate = false; }
        }

        /// <summary>
        /// Material for cut faces, per the "Cut face style" setting. Auto copies a material the game draws with SLZ's
        /// plain LitMAS shader, else the NPC's own skin material; either way with its textures swapped for ours and
        /// its keywords kept, so the shader variant is one the build has. Falls back to URP/Lit.
        /// </summary>
        public static Material CapMaterial(GorePalette p, Material npcTemplate) =>
            SurfaceMaterial("cap|" + p, "DismemberCap_" + p, Atlas(p), PaletteColors(p), npcTemplate, true);

        /// <summary>
        /// An opaque lit material showing <paramref name="albedo"/>, drawn the way the "Cut face style" setting says
        /// (see <see cref="CapMaterial"/>). Cached by <paramref name="what"/>.
        /// </summary>
        private static Material SurfaceMaterial(string what, string name, Texture2D albedo, Colors c, Material npcTemplate, bool log)
        {
            var style = (CapStyle)Math.Clamp(Settings.CapStyle.Value, 0, 3);
            // Auto only borrows the NPC's own material when it is one of SLZ's shaders (some NPCs use third-party skin
            // shaders that read data our meshes do not have).
            bool slzNpc = npcTemplate != null && npcTemplate.shader != null && npcTemplate.shader.name.StartsWith("SLZ/");
            Material template = style switch
            {
                CapStyle.Auto => LitMasStandard() ?? (slzNpc ? npcTemplate : null),
                CapStyle.NpcShader => npcTemplate,
                CapStyle.Unlit => ParticleTemplate(),
                _ => null,
            };
            string key = style + "|" + what + "|" + (template != null && template.shader != null ? template.shader.name : "-");
            if (CapMaterials.TryGetValue(key, out var m) && !m.IsDead() && m.shader != null) return m;
            if (template != null) m = new Material(template);
            else
            {
                var shader = style == CapStyle.Unlit
                    ? Shader.Find("Universal Render Pipeline/Particles/Unlit")
                    : Shader.Find("Universal Render Pipeline/Lit");
                if (shader == null)
                {
                    if (npcTemplate == null) return null;
                    shader = npcTemplate.shader;
                }
                m = new Material(shader);
                foreach (var kw in new[] { "_NORMALMAP", "_EMISSION", "_METALLICSPECGLOSSMAP", "_DETAIL_MULX2", "_OCCLUSIONMAP" })
                    m.DisableKeyword(kw);
            }
            m.name = name + "_" + style;

            byte smooth = (byte)(c.Smoothness * 255f), metal = (byte)(c.Metallic * 255f);
            var flatNormal = Solid("FlatNormal", new Color32(128, 128, 255, 255), true);
            var mas = Solid("MAS", new Color32(metal, 255, smooth, smooth), true);   // metallic, occlusion, smoothness (+ smoothness in alpha for URP)
            var white = Solid("White", new Color32(255, 255, 255, 255), false);
            var black = Solid("Black", new Color32(0, 0, 0, 255), false);
            var grey = Solid("Grey", new Color32(128, 128, 128, 128), true);

            void Tex(Texture2D t, params string[] props)
            {
                foreach (var prop in props)
                {
                    if (!m.HasProperty(prop)) continue;
                    m.SetTexture(prop, t);
                    // The template's tiling would show only a corner of ours.
                    m.SetTextureScale(prop, Vector2.one);
                    m.SetTextureOffset(prop, Vector2.zero);
                }
            }
            Tex(albedo, "_BaseMap", "_MainTex", "_Albedo");
            Tex(flatNormal, "_BumpMap", "_NormalMap", "_DetailNormalMap");
            Tex(mas, "_MetallicGlossMap");
            Tex(white, "_OcclusionMap");
            Tex(black, "_EmissionMap", "_HitRamp");
            Tex(grey, "_DetailMap", "_DetailAlbedoMap", "_DetailMask");
            foreach (var prop in new[] { "_BaseColor", "_Color" })
                if (m.HasProperty(prop)) m.SetColor(prop, Color.white);
            if (m.HasProperty("_EmissionColor")) m.SetColor("_EmissionColor", Color.black);
            if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", c.Smoothness);
            if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", c.Smoothness);
            if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", c.Metallic);
            if (m.HasProperty("_BumpScale")) m.SetFloat("_BumpScale", 0f);
            if (m.HasProperty("_NumberOfHits")) m.SetFloat("_NumberOfHits", 0f);
            // Draw both sides: a cut face seen from behind (through a gap in the clothes, from inside a hollow part)
            // should still look like flesh, not like a hole.
            if (m.HasProperty("_Cull")) m.SetFloat("_Cull", 0f);
            if (m.HasProperty("_CullMode")) m.SetFloat("_CullMode", 0f);
            m.hideFlags = HideFlags.DontUnloadUnusedAsset;
            CapMaterials[key] = m;
            if (log) Log.Msg($"Cut faces drawn with {(template != null ? "a copy of " + template.name : "a new material")} ({m.shader?.name}), style {style}");
            return m;
        }

        private static Texture2D _pool;

        /// <summary>
        /// Shades of spilt blood, one row per palette (flesh, void, robot): spots pick a shade along their row, so a
        /// floor of them is not all one flat colour.
        /// </summary>
        private static Texture2D PoolTexture()
        {
            if (!_pool.IsDead()) return _pool;
            const int W = 32, H = 4;
            var px = new Color32[W * H];
            for (int row = 0; row < H; row++)
            {
                var c = PaletteColors((GorePalette)row);
                var light = Lerp(c.Blood1, new Color32(255, 255, 255, 255), row == (int)GorePalette.Robot ? 0f : 0.08f);
                for (int x = 0; x < W; x++) px[row * W + x] = Lerp(c.Blood2, light, x / (W - 1f));
            }
            _pool = new Texture2D(W, H, TextureFormat.RGBA32, false) { name = "DismemberBloodPool", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            _pool.SetPixels32(px);
            _pool.Apply(false);
            _pool.hideFlags = HideFlags.DontUnloadUnusedAsset;
            return _pool;
        }

        /// <summary>Texture coordinate of a shade (0 darkest .. 1 lightest) of a palette's spilt blood.</summary>
        public static Vector2 PoolUV(GorePalette p, float shade) =>
            new Vector2(Mathf.Lerp(0.05f, 0.95f, Mathf.Clamp01(shade)), (Mathf.Clamp((int)p, 0, 3) + 0.5f) / 4f);

        /// <summary>Material for blood spots on floors and walls: opaque, lit and wet (the spots are shaped by their mesh).</summary>
        public static Material SplatMaterial()
        {
            var c = PaletteColors(GorePalette.Flesh);
            c.Smoothness = 0.88f;
            c.Metallic = 0f;
            return SurfaceMaterial("pool", "DismemberBloodPool", PoolTexture(), c, null, false);
        }

        /// <summary>The NPC material best suited as a cap template: opaque, from a flesh renderer.</summary>
        public static Material PickTemplate(IEnumerable<Material[]> flesh)
        {
            foreach (var mats in flesh)
                foreach (var mat in mats)
                {
                    if (mat.IsDead() || mat.shader == null) continue;
                    int q = mat.renderQueue;
                    if (q >= 2450) continue;   // transparent / alpha-tested (hair, eyes, lashes)
                    return mat;
                }
            return null;
        }

        private static Texture2D Drop()
        {
            if (!_drop.IsDead()) return _drop;
            const int S = 32;
            var px = new Color32[S * S];
            for (int y = 0; y < S; y++)
                for (int x = 0; x < S; x++)
                {
                    float dx = x - 15.5f, dy = y - 15.5f;
                    byte a = (byte)(dx * dx + dy * dy < 14f * 14f ? 255 : 0);
                    px[y * S + x] = new Color32(255, 255, 255, a);
                }
            _drop = new Texture2D(S, S, TextureFormat.RGBA32, false) { name = "DismemberDrop", wrapMode = TextureWrapMode.Clamp };
            _drop.SetPixels32(px);
            _drop.Apply();
            _drop.hideFlags = HideFlags.DontUnloadUnusedAsset;
            return _drop;
        }

        private static Material _particleTemplate;
        private static bool _searchedParticleTemplate;

        /// <summary>
        /// A particle material the game itself uses with alpha (clipped or blended). Copying it keeps a shader variant
        /// that is known to be in the build; a fresh material with our own keywords might have been stripped.
        /// </summary>
        private static Material ParticleTemplate()
        {
            if (_searchedParticleTemplate) return _particleTemplate.IsDead() ? null : _particleTemplate;
            _searchedParticleTemplate = true;
            try
            {
                foreach (var mat in Resources.FindObjectsOfTypeAll<Material>())
                {
                    if (mat.IsDead() || mat.shader == null || mat.shader.name != "Universal Render Pipeline/Particles/Unlit") continue;
                    // Flip-book materials draw one frame of a sheet (a corner of our round drop: a square with a cut
                    // corner); soft/fading ones need the depth texture.
                    if (mat.IsKeywordEnabled("_FLIPBOOKBLENDING_ON") || mat.IsKeywordEnabled("_SOFTPARTICLES_ON") ||
                        mat.IsKeywordEnabled("_FADING_ON") || mat.IsKeywordEnabled("_DISTORTION_ON")) continue;
                    if (mat.IsKeywordEnabled("_ALPHATEST_ON")) { _particleTemplate = mat; break; }
                    if (_particleTemplate == null && mat.IsKeywordEnabled("_SURFACE_TYPE_TRANSPARENT")) _particleTemplate = mat;
                }
            }
            catch (Exception e) { Log.Debug("particle template: " + e.Message); }
            if (_particleTemplate != null) Log.Msg("Blood droplets use a copy of " + _particleTemplate.name);
            return _particleTemplate;
        }

        private static Material BloodMaterial(GorePalette p)
        {
            if (BloodMaterials.TryGetValue(p, out var m) && !m.IsDead()) return m;
            var template = ParticleTemplate();
            if (template != null)
            {
                m = new Material(template) { name = "DismemberBlood_" + p };
            }
            else
            {
                var shader = Shader.Find("Universal Render Pipeline/Particles/Unlit") ?? Shader.Find("Universal Render Pipeline/Unlit");
                if (shader == null) return CapMaterial(p, null);
                m = new Material(shader) { name = "DismemberBlood_" + p };
                // Opaque with alpha clipping: round droplets, no sorting problems.
                if (m.HasProperty("_AlphaClip")) m.SetFloat("_AlphaClip", 1f);
                if (m.HasProperty("_Cutoff")) m.SetFloat("_Cutoff", 0.5f);
                m.EnableKeyword("_ALPHATEST_ON");
            }
            foreach (var prop in new[] { "_BaseMap", "_MainTex" })
            {
                if (!m.HasProperty(prop)) continue;
                m.SetTexture(prop, Drop());
                m.SetTextureScale(prop, Vector2.one);
                m.SetTextureOffset(prop, Vector2.zero);
            }
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", Color.white);
            if (m.HasProperty("_EmissionColor")) m.SetColor("_EmissionColor", Color.black);
            m.hideFlags = HideFlags.DontUnloadUnusedAsset;
            BloodMaterials[p] = m;
            return m;
        }

        /// <summary>A one-shot spray where a cut happens.</summary>
        public static void Burst(Vector3 pos, Vector3 dir, float strength, GorePalette p)
        {
            if (Amount <= 0f) return;
            try
            {
                var ps = Create("DismemberBurst", null, pos, dir, p, false, out var go);
                var em = ps.emission;
                em.rateOverTime = new ParticleSystem.MinMaxCurve(0f);
                int count = (int)Math.Clamp(40f * strength * Amount, 8f, 220f);
                em.SetBursts(new[] { new ParticleSystem.Burst(0f, (short)count) });
                ps.Play();
                UnityEngine.Object.Destroy(go, 4f);
                Splats.Spray(pos, dir, count, p);
            }
            catch (Exception e) { Log.Debug("burst failed: " + e.Message); }
        }

        /// <summary>A pulsing stream from a stump, parented to the body part it comes out of.</summary>
        public static ParticleSystem Bleeder(Transform parent, Vector3 worldPos, Vector3 worldDir, float seconds, float rate, GorePalette p)
        {
            if (Amount <= 0f || parent.IsDead()) return null;
            try
            {
                var ps = Create("DismemberBleeder", parent, worldPos, worldDir, p, true, out var go);
                var em = ps.emission;
                em.rateOverTime = new ParticleSystem.MinMaxCurve(rate * Amount);
                var main = ps.main;
                main.startSpeed = new ParticleSystem.MinMaxCurve(0.4f, 1.6f);
                ps.Play();
                UnityEngine.Object.Destroy(go, seconds + 2f);
                Bleeders.Add((ps, Time.time, seconds, rate * Amount, p));
                return ps;
            }
            catch (Exception e) { Log.Debug("bleeder failed: " + e.Message); return null; }
        }

        private static readonly List<(ParticleSystem ps, float start, float seconds, float rate, GorePalette palette)> Bleeders =
            new List<(ParticleSystem, float, float, float, GorePalette)>();

        /// <summary>Arterial pulses that fade out over the bleeder's life.</summary>
        public static void Update()
        {
            float now = Time.time;
            for (int i = Bleeders.Count - 1; i >= 0; i--)
            {
                var (ps, start, seconds, rate, palette) = Bleeders[i];
                if (ps.IsDead()) { Bleeders.RemoveAt(i); continue; }
                float t = (now - start) / seconds;
                var em = ps.emission;
                if (t >= 1f)
                {
                    em.rateOverTime = new ParticleSystem.MinMaxCurve(0f);
                    Bleeders.RemoveAt(i);
                    continue;
                }
                float pulse = 0.35f + 0.65f * MathF.Max(0f, MathF.Sin((now - start) * 7.5f));
                em.rateOverTime = new ParticleSystem.MinMaxCurve(rate * (1f - t) * pulse);
                // The stream lands somewhere: now and then a drop of it becomes a splat there.
                if (UnityEngine.Random.value < rate * (1f - t) * pulse * Time.deltaTime * 0.15f)
                    Splats.Spray(ps.transform.position, ps.transform.forward, 1, palette);
            }
        }

        private static ParticleSystem Create(string name, Transform parent, Vector3 pos, Vector3 dir, GorePalette p, bool loop, out GameObject go)
        {
            var c = PaletteColors(p);
            go = new GameObject(name);
            if (parent != null) go.transform.SetParent(parent, true);
            go.transform.position = pos;
            go.transform.rotation = Quaternion.LookRotation(dir.sqrMagnitude > 1e-6f ? dir : Vector3.up);
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

            var main = ps.main;
            main.duration = 1f;
            main.loop = loop;
            main.playOnAwake = false;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.6f, 1.4f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(1.2f, 3.8f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.006f, p == GorePalette.Robot ? 0.012f : 0.022f);
            main.startColor = new ParticleSystem.MinMaxGradient(c.Blood1, c.Blood2);
            main.gravityModifier = new ParticleSystem.MinMaxCurve(p == GorePalette.Robot ? 0.6f : 1f);
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = 400;

            var shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = 28f;
            shape.radius = 0.02f;

            // No collisions: levels are full of invisible trigger volumes (and the player carries some), which
            // particles collide with and stop dead in, hanging in the air. Droplets fall through the floor out of sight
            // instead, and blood spots (Splats) appear where they land.
            var col = ps.collision;
            col.enabled = false;

            var r = go.GetComponent<ParticleSystemRenderer>();
            r.material = BloodMaterial(p);
            r.renderMode = ParticleSystemRenderMode.Billboard;
            return ps;
        }

        /// <summary>What a cauterised cut (an energy blade) looks like in a palette: burnt, for anything made of flesh.</summary>
        public static GorePalette Seared(GorePalette p) => p == GorePalette.Flesh || p == GorePalette.Void ? GorePalette.Seared : p;

        /// <summary>Sparks off a cut burnt by an energy blade: tiny glowing bits, no blood.</summary>
        public static void Embers(Vector3 pos, Vector3 dir, float strength)
        {
            try
            {
                var ps = Create("DismemberEmbers", null, pos, dir, GorePalette.Seared, false, out var go);
                var main = ps.main;
                main.startLifetime = new ParticleSystem.MinMaxCurve(0.2f, 0.7f);
                main.startSpeed = new ParticleSystem.MinMaxCurve(0.4f, 2.4f);
                main.startSize = new ParticleSystem.MinMaxCurve(0.003f, 0.008f);
                main.gravityModifier = new ParticleSystem.MinMaxCurve(0.25f);
                var col = ps.collision;
                col.enabled = false;
                var shape = ps.shape;
                shape.angle = 50f;
                var em = ps.emission;
                em.rateOverTime = new ParticleSystem.MinMaxCurve(0f);
                int count = (int)Math.Clamp(25f * strength, 6f, 120f);
                em.SetBursts(new[] { new ParticleSystem.Burst(0f, (short)count) });
                ps.Play();
                UnityEngine.Object.Destroy(go, 2f);
            }
            catch (Exception e) { Log.Debug("embers failed: " + e.Message); }
        }
    }
}
