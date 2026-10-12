using System;
using System.Collections.Generic;
using System.Numerics;

namespace Dismember.Core
{
    public static class MathX
    {
        public static float Clamp(float x, float lo, float hi) => x < lo ? lo : x > hi ? hi : x;
        public static float Clamp01(float x) => x < 0f ? 0f : x > 1f ? 1f : x;
        public static float Lerp(float a, float b, float t) => a + (b - a) * t;
        public static float InverseLerp(float a, float b, float x) => Math.Abs(b - a) < 1e-12f ? 0f : Clamp01((x - a) / (b - a));

        public static Vector3 AnyPerpendicular(Vector3 n)
        {
            var a = Math.Abs(n.X) < 0.9f ? Vector3.UnitX : Vector3.UnitY;
            return Vector3.Normalize(Vector3.Cross(n, a));
        }

        public static Vector3 SafeNormalize(Vector3 v, Vector3 fallback)
        {
            float l = v.Length();
            return l > 1e-12f ? v / l : fallback;
        }

        public static float Cross(Vector2 a, Vector2 b) => a.X * b.Y - a.Y * b.X;

        public static Vector2 SafeNormalize2(Vector2 v)
        {
            float l = v.Length();
            return l > 1e-9f ? v / l : Vector2.Zero;
        }

        /// <summary>Signed area, positive for counter-clockwise polygons.</summary>
        public static float SignedArea(IReadOnlyList<Vector2> p)
        {
            double a = 0;
            for (int i = 0, j = p.Count - 1; i < p.Count; j = i++)
                a += (double)p[j].X * p[i].Y - (double)p[i].X * p[j].Y;
            return (float)(a * 0.5);
        }

        public static Vector2 PolygonCentroid(IReadOnlyList<Vector2> p)
        {
            double a = 0, cx = 0, cy = 0;
            for (int i = 0, j = p.Count - 1; i < p.Count; j = i++)
            {
                double f = (double)p[j].X * p[i].Y - (double)p[i].X * p[j].Y;
                a += f;
                cx += (p[j].X + p[i].X) * f;
                cy += (p[j].Y + p[i].Y) * f;
            }
            if (Math.Abs(a) < 1e-14)
            {
                var s = Vector2.Zero;
                foreach (var q in p) s += q;
                return p.Count > 0 ? s / p.Count : s;
            }
            a *= 0.5;
            return new Vector2((float)(cx / (6 * a)), (float)(cy / (6 * a)));
        }

        public static bool PointInPolygon(Vector2 pt, IReadOnlyList<Vector2> poly)
        {
            bool inside = false;
            for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
            {
                var a = poly[i];
                var b = poly[j];
                if ((a.Y > pt.Y) != (b.Y > pt.Y) &&
                    pt.X < (b.X - a.X) * (pt.Y - a.Y) / (b.Y - a.Y) + a.X)
                    inside = !inside;
            }
            return inside;
        }

        /// <summary>Distance from a point to a polygon outline.</summary>
        public static float DistanceToPolygon(Vector2 pt, IReadOnlyList<Vector2> poly)
        {
            float best = float.MaxValue;
            for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
                best = Math.Min(best, DistanceToSegment(pt, poly[j], poly[i]));
            return best;
        }

        public static float DistanceToSegment(Vector2 p, Vector2 a, Vector2 b)
        {
            var ab = b - a;
            float l2 = ab.LengthSquared();
            float t = l2 > 1e-14f ? Clamp01(Vector2.Dot(p - a, ab) / l2) : 0f;
            return Vector2.Distance(p, a + ab * t);
        }

        /// <summary>Do segments ab and cd properly cross (touching at an end point does not count)?</summary>
        public static bool SegmentsCross(Vector2 a, Vector2 b, Vector2 c, Vector2 d) => SegmentIntersection(a, b, c, d, out _, out _);

        /// <summary>Where segments ab and cd cross: parameters along ab (t) and cd (s), both strictly inside (0, 1).</summary>
        public static bool SegmentIntersection(Vector2 a, Vector2 b, Vector2 c, Vector2 d, out float t, out float s)
        {
            t = s = 0f;
            var r = b - a;
            var q = d - c;
            float den = Cross(r, q);
            if (Math.Abs(den) < 1e-14f) return false;   // parallel
            var ac = c - a;
            t = Cross(ac, q) / den;
            s = Cross(ac, r) / den;
            const float eps = 1e-5f;
            return t > eps && t < 1f - eps && s > eps && s < 1f - eps;
        }

        public static Matrix4x4 InvertOrIdentity(in Matrix4x4 m)
        {
            return Matrix4x4.Invert(m, out var inv) ? inv : Matrix4x4.Identity;
        }

        public static Matrix4x4 Scale(in Matrix4x4 m, float s) => m * s;

        /// <summary>
        /// Douglas-Peucker simplification of a closed loop, keeping every point list in sync.
        /// No removed point is further than <paramref name="tolerance"/> from the simplified outline.
        /// </summary>
        public static void SimplifyLoop(List<Vector2> pts2, List<Vector3> pts3, List<int> tags, float tolerance)
        {
            int n = pts2.Count;
            if (n < 5) return;
            var keep = new bool[n];
            // Split the loop at the point furthest from point 0.
            int far = 0;
            float best = -1f;
            for (int i = 1; i < n; i++)
            {
                float d = Vector2.DistanceSquared(pts2[0], pts2[i]);
                if (d > best) { best = d; far = i; }
            }
            keep[0] = keep[far] = true;
            var stack = new Stack<(int a, int b)>();
            stack.Push((0, far));
            stack.Push((far, n));
            while (stack.Count > 0)
            {
                var (a, b) = stack.Pop();
                if (b - a < 2) continue;
                var pa = pts2[a];
                var pb = pts2[b % n];
                int worst = -1;
                float worstD = tolerance;
                for (int i = a + 1; i < b; i++)
                {
                    float d = DistanceToSegment(pts2[i], pa, pb);
                    if (d > worstD) { worstD = d; worst = i; }
                }
                if (worst < 0) continue;
                keep[worst] = true;
                stack.Push((a, worst));
                stack.Push((worst, b));
            }
            int w = 0;
            for (int i = 0; i < n; i++)
            {
                if (!keep[i]) continue;
                pts2[w] = pts2[i];
                if (pts3 != null) pts3[w] = pts3[i];
                if (tags != null) tags[w] = tags[i];
                w++;
            }
            if (w < 3) return;
            pts2.RemoveRange(w, n - w);
            pts3?.RemoveRange(w, n - w);
            tags?.RemoveRange(w, n - w);
        }
    }
}
