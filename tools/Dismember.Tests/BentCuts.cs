using System;
using Dismember.Core;

namespace Dismember.Tests
{
    /// <summary>Curved cuts (a lightsaber steered through a body): a random arc put on a cutting plane.</summary>
    public static class BentCuts
    {
        /// <summary>
        /// Bends <paramref name="region"/> into an arc of radius 0.3-0.8 m (either way) around a random point near its
        /// origin, sampled every 2 cm the way the game records a saber's path, at most ~50 degrees off the plane.
        /// </summary>
        public static void Bend(CutRegion region, Random rng)
        {
            float radius = 0.3f + (float)rng.NextDouble() * 0.5f;
            float curve = (rng.Next(2) == 0 ? -1f : 1f) / radius;
            float w0 = ((float)rng.NextDouble() * 2f - 1f) * 0.1f;
            const int Count = 41;
            region.BendW = new float[Count];
            region.BendN = new float[Count];
            float n = 0f;
            for (int i = 0; i < Count; i++)
            {
                float w = -0.4f + i * 0.02f;
                float slope = Math.Clamp(curve * (w - w0), -1.2f, 1.2f);
                if (i > 0) n += slope * 0.02f;
                region.BendW[i] = w;
                region.BendN[i] = n;
            }
            // Keep the surface through the origin, so the cut still goes where the plane did.
            float at = region.BendAt(0f);
            for (int i = 0; i < Count; i++) region.BendN[i] -= at;
        }
    }
}
