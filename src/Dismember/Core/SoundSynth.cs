using System;
using System.Collections.Generic;

namespace Dismember.Core
{
    /// <summary>The cutting sounds, synthesised (no audio files to ship or load).</summary>
    public enum CutSound { SliceIn, Sever, Bone, Stuck, Saw, Scratch, Sizzle }

    /// <summary>
    /// Makes the mod's sound effects from filtered noise and decaying resonances: a wet swish as the blade goes in,
    /// a chop and splat when something comes off, cracks for bone, a dull thunk when a blade sticks, short wet
    /// scrapes for sawing. Deterministic per seed, mono, normalised.
    /// </summary>
    public static class SoundSynth
    {
        public const int Rate = 44100;

        public static float[] Make(CutSound kind, int seed)
        {
            var r = new Rng(seed * 7919 + (int)kind * 104729 + 1);
            float[] s = kind switch
            {
                CutSound.SliceIn => SliceIn(r),
                CutSound.Sever => Sever(r),
                CutSound.Bone => Bone(r),
                CutSound.Stuck => Stuck(r),
                CutSound.Saw => Saw(r),
                CutSound.Sizzle => Sizzle(r),
                _ => Scratch(r),
            };
            FadeOut(s, 0.02f);
            Normalize(s, 0.85f);
            return s;
        }

        // ------------------------------------------------------------------------------------------------
        // The sounds
        // ------------------------------------------------------------------------------------------------

        /// <summary>Blade entering flesh: a falling swish over wet tearing, with a soft squelch underneath.</summary>
        private static float[] SliceIn(Rng r)
        {
            var s = Buffer(0.34f);
            Swish(s, r, 0f, 2600f * r.Range(0.85f, 1.15f), 850f, 1.3f, 0.006f, 0.09f, 0.55f);
            Wet(s, r, 0.012f, 1300f * r.Range(0.8f, 1.2f), 0.12f, 0.75f, 0.006f, 0.016f);
            for (int i = 0; i < 2; i++)
                Squelch(s, r, 0.01f + r.Range(0f, 0.05f), r.Range(170f, 320f), 0.045f, 0.3f);
            return s;
        }

        /// <summary>Something coming off: a sharp chop, a low thump and a wet splat, then a few drips.</summary>
        private static float[] Sever(Rng r)
        {
            var s = Buffer(0.6f);
            NoiseBurst(s, r, 0f, 700f, 9000f, 0.012f, 0.9f);
            Thump(s, r, 0f, r.Range(85f, 110f), 52f, 0.075f, 0.85f);
            Wet(s, r, 0.008f, 1900f * r.Range(0.85f, 1.15f), 0.17f, 0.65f, 0.004f, 0.012f);
            Squelch(s, r, 0.02f, r.Range(140f, 220f), 0.07f, 0.35f);
            int drips = 2 + r.Next(3);
            for (int i = 0; i < drips; i++)
                Ping(s, r.Range(0.12f, 0.45f), r.Range(600f, 1300f), 0.018f, r.Range(0.06f, 0.14f), pitchDrop: 0.25f);
            return s;
        }

        /// <summary>Bone giving way: a quick run of cracks over a short crunch.</summary>
        private static float[] Bone(Rng r)
        {
            var s = Buffer(0.28f);
            int cracks = 3 + r.Next(4);
            float t = 0f, amp = 1f;
            for (int i = 0; i < cracks; i++)
            {
                NoiseBurst(s, r, t, 1500f, 12000f, 0.0025f, amp);
                Ping(s, t, r.Range(1400f, 3200f), 0.006f, amp * 0.45f, pitchDrop: 0.1f);
                t += r.Range(0.004f, 0.014f);
                amp *= r.Range(0.6f, 0.85f);
            }
            Band(s, r, 0.002f, 1200f * r.Range(0.8f, 1.25f), 2f, 0.002f, 0.04f, 0.45f, grains: true);
            return s;
        }

        /// <summary>A blade biting in and stopping: a dull thunk and a short squish.</summary>
        private static float[] Stuck(Rng r)
        {
            var s = Buffer(0.38f);
            Thump(s, r, 0f, r.Range(110f, 140f), 68f, 0.06f, 0.9f);
            NoiseBurst(s, r, 0f, 80f, 1400f, 0.025f, 0.5f);
            Wet(s, r, 0.01f, 900f, 0.09f, 0.35f, 0.008f, 0.02f);
            Squelch(s, r, 0.015f, r.Range(150f, 250f), 0.05f, 0.25f);
            return s;
        }

        /// <summary>Working a stuck blade: a short wet scrape.</summary>
        private static float[] Saw(Rng r)
        {
            var s = Buffer(0.2f);
            Band(s, r, 0f, 1600f * r.Range(0.8f, 1.2f), 1.4f, 0.02f, 0.06f, 0.5f, grains: true);
            Wet(s, r, 0.005f, 1100f, 0.05f, 0.35f, 0.005f, 0.012f);
            return s;
        }

        /// <summary>An energy blade burning through: a crackling hiss with a few sharp pops.</summary>
        private static float[] Sizzle(Rng r)
        {
            var s = Buffer(0.5f);
            // The hiss: bright noise, quick to start, dying away.
            var hp = new Biquad();
            hp.HighPass(2600f * r.Range(0.85f, 1.15f), 0.7f);
            var g = new Grains(r, 0.0015f, 0.006f);
            for (int i = 0; i < s.Length; i++)
            {
                float t = i / (float)Rate;
                s[i] += hp.Run(r.Noise()) * Env(t, 0.004f, 0.16f) * (0.35f + 0.65f * g.Next()) * 0.7f;
            }
            // The crackle: short pops of fat spitting.
            int pops = 6 + r.Next(8);
            for (int k = 0; k < pops; k++)
            {
                float at = r.Range(0f, 0.35f);
                NoiseBurst(s, r, at, 3000f, 12000f, 0.0012f, r.Range(0.3f, 0.9f));
                if (r.Next(3) == 0) Ping(s, at, r.Range(3500f, 6500f), 0.002f, 0.2f, pitchDrop: 0.2f);
            }
            // A soft low body under it.
            Band(s, r, 0f, 700f, 1.2f, 0.01f, 0.08f, 0.25f, grains: false);
            return s;
        }

        /// <summary>A shallow nick: a quick bright swish.</summary>
        private static float[] Scratch(Rng r)
        {
            var s = Buffer(0.16f);
            Swish(s, r, 0f, 4200f * r.Range(0.85f, 1.15f), 2400f, 1.6f, 0.003f, 0.03f, 0.55f);
            Wet(s, r, 0.004f, 1500f, 0.035f, 0.3f, 0.004f, 0.01f);
            return s;
        }

        // ------------------------------------------------------------------------------------------------
        // Building blocks (all add into the buffer)
        // ------------------------------------------------------------------------------------------------

        private static float[] Buffer(float seconds) => new float[(int)(seconds * Rate)];

        /// <summary>Band-passed noise whose centre slides from f0 to f1.</summary>
        private static void Swish(float[] s, Rng r, float start, float f0, float f1, float q, float attack, float decay, float amp)
        {
            var bp = new Biquad();
            int i0 = (int)(start * Rate);
            for (int i = i0; i < s.Length; i++)
            {
                float t = (i - i0) / (float)Rate;
                if ((i - i0) % 32 == 0) bp.BandPass(f1 + (f0 - f1) * MathF.Exp(-t / (decay * 0.8f)), q);
                s[i] += bp.Run(r.Noise()) * Env(t, attack, decay) * amp;
            }
        }

        /// <summary>Band-passed noise at a fixed centre, optionally chopped into grains.</summary>
        private static void Band(float[] s, Rng r, float start, float f, float q, float attack, float decay, float amp, bool grains)
        {
            var bp = new Biquad();
            bp.BandPass(f, q);
            var g = grains ? new Grains(r, 0.003f, 0.01f) : null;
            int i0 = (int)(start * Rate);
            for (int i = i0; i < s.Length; i++)
            {
                float t = (i - i0) / (float)Rate;
                s[i] += bp.Run(r.Noise()) * Env(t, attack, decay) * amp * (g?.Next() ?? 1f);
            }
        }

        /// <summary>Wet tearing: low-passed noise broken into random little bursts.</summary>
        private static void Wet(float[] s, Rng r, float start, float cutoff, float decay, float amp, float grainMin, float grainMax)
        {
            var lp = new Biquad();
            lp.LowPass(cutoff, 0.9f);
            var g = new Grains(r, grainMin, grainMax);
            int i0 = (int)(start * Rate);
            for (int i = i0; i < s.Length; i++)
            {
                float t = (i - i0) / (float)Rate;
                s[i] += lp.Run(r.Noise()) * Env(t, 0.004f, decay) * amp * g.Next() * 1.6f;
            }
        }

        /// <summary>A short broadband hit (high-passed then low-passed noise).</summary>
        private static void NoiseBurst(float[] s, Rng r, float start, float hp, float lp, float decay, float amp)
        {
            var h = new Biquad(); h.HighPass(hp, 0.7f);
            var l = new Biquad(); l.LowPass(lp, 0.7f);
            int i0 = (int)(start * Rate);
            int len = Math.Min(s.Length - i0, (int)(decay * 8f * Rate));
            for (int k = 0; k < len; k++)
            {
                float t = k / (float)Rate;
                s[i0 + k] += l.Run(h.Run(r.Noise())) * MathF.Exp(-t / decay) * amp;
            }
        }

        /// <summary>A low sine thump whose pitch falls from f0 towards f1.</summary>
        private static void Thump(float[] s, Rng r, float start, float f0, float f1, float decay, float amp)
        {
            int i0 = (int)(start * Rate);
            float phase = 0f;
            int len = Math.Min(s.Length - i0, (int)(decay * 7f * Rate));
            for (int k = 0; k < len; k++)
            {
                float t = k / (float)Rate;
                float f = f1 + (f0 - f1) * MathF.Exp(-t / (decay * 0.6f));
                phase += 2f * MathF.PI * f / Rate;
                s[i0 + k] += MathF.Sin(phase) * Env(t, 0.002f, decay) * amp;
            }
        }

        /// <summary>A damped resonance with a little noise on it: the squelchy body of flesh sounds.</summary>
        private static void Squelch(float[] s, Rng r, float start, float f, float decay, float amp)
        {
            var bp = new Biquad();
            bp.BandPass(f * 2.5f, 1.5f);
            int i0 = (int)(start * Rate);
            float phase = 0f;
            int len = Math.Min(s.Length - i0, (int)(decay * 7f * Rate));
            for (int k = 0; k < len; k++)
            {
                float t = k / (float)Rate;
                float fr = f * (1f - 0.35f * (1f - MathF.Exp(-t / decay)));
                phase += 2f * MathF.PI * fr / Rate;
                float e = Env(t, 0.003f, decay);
                s[i0 + k] += (MathF.Sin(phase) * 0.7f + bp.Run(r.Noise()) * 0.8f) * e * amp;
            }
        }

        /// <summary>A short pure ping (drips, the ring of a crack).</summary>
        private static void Ping(float[] s, float start, float f, float decay, float amp, float pitchDrop)
        {
            int i0 = (int)(start * Rate);
            if (i0 >= s.Length) return;
            float phase = 0f;
            int len = Math.Min(s.Length - i0, (int)(decay * 7f * Rate));
            for (int k = 0; k < len; k++)
            {
                float t = k / (float)Rate;
                phase += 2f * MathF.PI * f * (1f - pitchDrop * (1f - MathF.Exp(-t / decay))) / Rate;
                s[i0 + k] += MathF.Sin(phase) * Env(t, 0.0008f, decay) * amp;
            }
        }

        /// <summary>Fast attack, exponential decay.</summary>
        private static float Env(float t, float attack, float decay) =>
            (t < attack ? t / attack : 1f) * MathF.Exp(-MathF.Max(0f, t - attack) / decay);

        private static void FadeOut(float[] s, float seconds)
        {
            int n = Math.Min(s.Length, (int)(seconds * Rate));
            for (int k = 0; k < n; k++) s[s.Length - 1 - k] *= k / (float)n;
        }

        private static void Normalize(float[] s, float peak)
        {
            float max = 0f;
            foreach (float x in s) max = MathF.Max(max, MathF.Abs(x));
            if (max < 1e-6f) return;
            float g = peak / max;
            for (int i = 0; i < s.Length; i++)
            {
                float x = s[i] * g;
                s[i] = MathF.Abs(x) > 0.7f ? MathF.Sign(x) * (0.7f + 0.3f * MathF.Tanh((MathF.Abs(x) - 0.7f) / 0.3f)) : x;   // soft knee
            }
        }

        private sealed class Rng
        {
            private uint _s;
            public Rng(int seed) { _s = (uint)seed * 2654435761u + 12345u; if (_s == 0) _s = 1; }
            public uint NextU() { _s ^= _s << 13; _s ^= _s >> 17; _s ^= _s << 5; return _s; }
            public float Next01() => (NextU() & 0xFFFFFF) / 16777216f;
            public float Noise() => Next01() * 2f - 1f;
            public float Range(float a, float b) => a + (b - a) * Next01();
            public int Next(int n) => (int)(NextU() % (uint)n);
        }

        /// <summary>Random short bursts: 0..1 gain that jumps to a random level and decays, again and again.</summary>
        private sealed class Grains
        {
            private readonly Rng _r;
            private readonly float _min, _max;
            private int _left;
            private float _level, _decay;
            public Grains(Rng r, float minSeconds, float maxSeconds) { _r = r; _min = minSeconds; _max = maxSeconds; }
            public float Next()
            {
                if (_left-- <= 0)
                {
                    float len = _r.Range(_min, _max);
                    _left = (int)(len * Rate);
                    _level = 0.25f + 0.75f * _r.Next01() * _r.Next01() * 1.6f;
                    _decay = MathF.Exp(-1f / (len * Rate * 0.5f));
                }
                _level *= _decay;
                return MathF.Min(1.2f, _level);
            }
        }

        /// <summary>RBJ biquad filter.</summary>
        private sealed class Biquad
        {
            private float _b0, _b1, _b2, _a1, _a2, _x1, _x2, _y1, _y2;

            public void LowPass(float f, float q) => Set(f, q, 0);
            public void HighPass(float f, float q) => Set(f, q, 1);
            public void BandPass(float f, float q) => Set(f, q, 2);

            private void Set(float f, float q, int type)
            {
                f = Math.Clamp(f, 20f, Rate * 0.45f);
                float w = 2f * MathF.PI * f / Rate;
                float cos = MathF.Cos(w), alpha = MathF.Sin(w) / (2f * q);
                float a0 = 1f + alpha;
                switch (type)
                {
                    case 0: _b0 = (1f - cos) / 2f; _b1 = 1f - cos; _b2 = (1f - cos) / 2f; break;
                    case 1: _b0 = (1f + cos) / 2f; _b1 = -(1f + cos); _b2 = (1f + cos) / 2f; break;
                    default: _b0 = alpha; _b1 = 0f; _b2 = -alpha; break;
                }
                _a1 = -2f * cos;
                _a2 = 1f - alpha;
                _b0 /= a0; _b1 /= a0; _b2 /= a0; _a1 /= a0; _a2 /= a0;
            }

            public float Run(float x)
            {
                float y = _b0 * x + _b1 * _x1 + _b2 * _x2 - _a1 * _y1 - _a2 * _y2;
                _x2 = _x1; _x1 = x; _y2 = _y1; _y1 = y;
                return y;
            }
        }

        /// <summary>16-bit mono WAV, for listening to the sounds outside the game.</summary>
        public static byte[] ToWav(float[] s)
        {
            var bytes = new List<byte>(44 + s.Length * 2);
            void U32(uint v) { bytes.Add((byte)v); bytes.Add((byte)(v >> 8)); bytes.Add((byte)(v >> 16)); bytes.Add((byte)(v >> 24)); }
            void U16(ushort v) { bytes.Add((byte)v); bytes.Add((byte)(v >> 8)); }
            void Tag(string t) { foreach (char c in t) bytes.Add((byte)c); }
            Tag("RIFF"); U32((uint)(36 + s.Length * 2)); Tag("WAVE");
            Tag("fmt "); U32(16); U16(1); U16(1); U32(Rate); U32(Rate * 2); U16(2); U16(16);
            Tag("data"); U32((uint)(s.Length * 2));
            foreach (float x in s) U16((ushort)(short)Math.Clamp(x * 32767f, -32768f, 32767f));
            return bytes.ToArray();
        }
    }
}
