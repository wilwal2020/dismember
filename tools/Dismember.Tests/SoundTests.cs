using System;
using System.IO;
using System.Linq;
using Dismember.Core;

namespace Dismember.Tests
{
    /// <summary>The synthesised cutting sounds: well formed, audible, not clipping. Also written out as WAV files.</summary>
    public static class SoundTests
    {
        public static int Run(string outDir)
        {
            Console.WriteLine();
            Console.WriteLine("== sounds");
            string dir = Path.Combine(outDir, "sounds");
            Directory.CreateDirectory(dir);
            int fails = 0;
            foreach (CutSound kind in Enum.GetValues(typeof(CutSound)))
            {
                for (int v = 1; v <= 3; v++)
                {
                    var s = SoundSynth.Make(kind, v);
                    bool finite = s.All(x => !float.IsNaN(x) && !float.IsInfinity(x));
                    float peak = s.Max(x => Math.Abs(x));
                    double rms = Math.Sqrt(s.Average(x => (double)x * x));
                    float tail = s.Skip(s.Length - 64).Max(x => Math.Abs(x));
                    File.WriteAllBytes(Path.Combine(dir, $"{kind}_{v}.wav"), SoundSynth.ToWav(s));
                    if (v == 1) Console.WriteLine($"   {kind}: {s.Length * 1000 / SoundSynth.Rate} ms, peak {peak:F2}, rms {rms:F3}");
                    if (!finite || peak > 1f || peak < 0.5f || rms < 0.02 || tail > 0.05f)
                    {
                        Console.WriteLine($"   FAIL {kind} #{v}: finite={finite} peak={peak:F2} rms={rms:F3} tail={tail:F3}");
                        fails++;
                    }
                }
            }
            Console.WriteLine($"   WAV files: {dir}");
            if (fails == 0) Console.WriteLine("   OK");
            return fails;
        }
    }
}
