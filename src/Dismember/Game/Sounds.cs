using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using UnityEngine.Audio;
using Dismember.Core;

namespace Dismember.Game
{
    /// <summary>
    /// Plays the cutting sounds (<see cref="SoundSynth"/>) in 3D where things happen. The clips are made once, a few
    /// variants of each, and played through a small pool of sources routed into the game's sound-effects mixer when
    /// one can be found (so the game's volume settings apply).
    /// </summary>
    internal static class Sounds
    {
        public enum Kind { SliceIn, Sever, Bone, Stuck, Saw, Scratch, Sizzle }

        private const int Variants = 3;
        private const int PoolSize = 10;

        private static readonly Dictionary<Kind, AudioClip[]> Clips = new Dictionary<Kind, AudioClip[]>();
        private static AudioSource[] _pool;
        private static int _next;
        private static AudioMixerGroup _mixer;
        private static bool _mixerSearched;
        private static bool _failed;
        private static readonly Dictionary<Kind, float> LastPlayed = new Dictionary<Kind, float>();
        private static readonly System.Random Rng = new System.Random();

        private static float BaseVolume(Kind k) => k switch
        {
            Kind.Sever => 1f,
            Kind.Bone => 0.8f,
            Kind.Stuck => 0.85f,
            Kind.SliceIn => 0.75f,
            Kind.Saw => 0.45f,
            Kind.Sizzle => 0.8f,
            _ => 0.5f,
        };

        /// <summary>Plays <paramref name="kind"/> at <paramref name="at"/>; <paramref name="intensity"/> 0..1 scales loudness.</summary>
        public static void Play(Kind kind, Vector3 at, float intensity)
        {
            float vol = Settings.SoundVolume.Value * BaseVolume(kind) * Mathf.Lerp(0.35f, 1f, Mathf.Clamp01(intensity));
            if (vol <= 0.001f || _failed) return;
            // The same sound twice in a few milliseconds just gets louder and phasey.
            if (LastPlayed.TryGetValue(kind, out float last) && Time.time - last < 0.04f) return;
            LastPlayed[kind] = Time.time;
            try
            {
                var clip = Clip(kind);
                var src = Source();
                if (clip == null || src == null) return;
                src.transform.position = at;
                src.clip = clip;
                src.volume = Mathf.Clamp01(vol);
                src.pitch = 0.9f + (float)Rng.NextDouble() * 0.2f;
                src.Play();
            }
            catch (Exception e)
            {
                _failed = true;   // do not spam the log every swing
                Log.Warn("Cut sounds could not be played and are off for this session: " + e.Message);
            }
        }

        private static AudioClip Clip(Kind kind)
        {
            if (Clips.TryGetValue(kind, out var set) && set[0] != null && !set[0].IsDead())
                return set[Rng.Next(set.Length)];
            set = new AudioClip[Variants];
            for (int v = 0; v < Variants; v++)
            {
                var data = SoundSynth.Make((CutSound)(int)kind, v + 1);
                var clip = AudioClip.Create("Dismember_" + kind + "_" + v, data.Length, 1, SoundSynth.Rate, false);
                clip.SetData(new Il2CppStructArray<float>(data), 0);
                clip.hideFlags = HideFlags.DontUnloadUnusedAsset;
                set[v] = clip;
            }
            Clips[kind] = set;
            return set[Rng.Next(set.Length)];
        }

        private static AudioSource Source()
        {
            if (_pool == null || _pool[0].IsDead())
            {
                var root = new GameObject("Dismember_Audio");
                UnityEngine.Object.DontDestroyOnLoad(root);
                root.hideFlags = HideFlags.DontUnloadUnusedAsset;
                _pool = new AudioSource[PoolSize];
                for (int i = 0; i < PoolSize; i++)
                {
                    var go = new GameObject("Source" + i);
                    go.transform.SetParent(root.transform, false);
                    var a = go.AddComponent<AudioSource>();
                    a.playOnAwake = false;
                    a.spatialBlend = 1f;
                    a.minDistance = 0.7f;
                    a.maxDistance = 30f;
                    a.rolloffMode = AudioRolloffMode.Logarithmic;
                    a.dopplerLevel = 0f;
                    a.priority = 60;
                    _pool[i] = a;
                }
                _mixerSearched = false;
            }
            if (!_mixerSearched) RouteToMixer();
            var src = _pool[_next];
            _next = (_next + 1) % PoolSize;
            return src;
        }

        /// <summary>Sends our sources into the mixer group the game's own 3D sound effects use.</summary>
        private static void RouteToMixer()
        {
            _mixerSearched = true;
            try
            {
                if (_mixer.IsDead())
                {
                    _mixer = null;
                    var votes = new Dictionary<IntPtr, (AudioMixerGroup g, int n)>();
                    foreach (var a in Resources.FindObjectsOfTypeAll<AudioSource>())
                    {
                        if (a.IsDead() || a.spatialBlend < 0.5f) continue;
                        var g = a.outputAudioMixerGroup;
                        if (g == null) continue;
                        string name = g.name.ToLowerInvariant();
                        if (name.Contains("music") || name.Contains("voice") || name.Contains("ui") || name.Contains("ambien")) continue;
                        votes.TryGetValue(g.Pointer, out var v);
                        votes[g.Pointer] = (g, v.n + (name.Contains("sfx") || name.Contains("effect") ? 5 : 1));
                    }
                    int best = 0;
                    foreach (var v in votes.Values) if (v.n > best) { best = v.n; _mixer = v.g; }
                }
                foreach (var a in _pool) if (!a.IsDead()) a.outputAudioMixerGroup = _mixer;
                Log.Debug(_mixer != null ? "Cut sounds go through mixer group " + _mixer.name : "Cut sounds: no mixer group found, playing direct");
            }
            catch (Exception e) { Log.Debug("mixer: " + e.Message); }
        }

        /// <summary>A new level may come with its own mixer setup.</summary>
        public static void OnLevelLoaded()
        {
            _mixerSearched = false;
            if (_mixer.IsDead()) _mixer = null;
        }
    }
}
