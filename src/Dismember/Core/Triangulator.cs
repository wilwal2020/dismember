using System;
using System.Collections.Generic;
using System.Numerics;

namespace Dismember.Core
{
    /// <summary>Polygon filling for cut faces.</summary>
    public static class Triangulator
    {
        /// <summary>
        /// Ear-clipping triangulation. Output triangles are counter-clockwise and index into <paramref name="poly"/>.
        /// Always clips the polygon completely: when no clean ear is left (self-touching or numerically messy outlines)
        /// it clips the least bad convex corner and carries on. Returns false when it had to do that.
        /// </summary>
        public static bool Triangulate(IReadOnlyList<Vector2> poly, List<int> output)
        {
            int n = poly.Count;
            if (n < 3) return false;

            var idx = new List<int>(n);
            bool ccw = MathX.SignedArea(poly) > 0f;
            for (int i = 0; i < n; i++) idx.Add(ccw ? i : n - 1 - i);

            bool clean = true;
            int guard = n * 4 + 16;
            while (idx.Count > 3 && guard-- > 0)
            {
                bool clipped = false;
                int forced = -1;
                float forcedArea = float.MaxValue;
                for (int i = 0; i < idx.Count; i++)
                {
                    int i0 = idx[(i - 1 + idx.Count) % idx.Count];
                    int i1 = idx[i];
                    int i2 = idx[(i + 1) % idx.Count];
                    var a = poly[i0];
                    var b = poly[i1];
                    var c = poly[i2];
                    float cross = MathX.Cross(b - a, c - b);
                    if (cross <= 1e-12f)
                    {
                        // Reflex or degenerate; drop collinear points and spikes outright.
                        if (Math.Abs(cross) <= 1e-12f)
                        {
                            idx.RemoveAt(i);
                            clipped = true;
                            break;
                        }
                        continue;
                    }
                    if (AnyPointInside(poly, idx, i0, i1, i2, a, b, c))
                    {
                        if (cross < forcedArea) { forcedArea = cross; forced = i; }
                        continue;
                    }
                    output.Add(i0); output.Add(i1); output.Add(i2);
                    idx.RemoveAt(i);
                    clipped = true;
                    break;
                }
                if (clipped) continue;
                if (forced < 0) break;   // nothing convex left: what remains has no area
                clean = false;
                output.Add(idx[(forced - 1 + idx.Count) % idx.Count]); output.Add(idx[forced]); output.Add(idx[(forced + 1) % idx.Count]);
                idx.RemoveAt(forced);
            }

            if (idx.Count == 3)
            {
                var a = poly[idx[0]]; var b = poly[idx[1]]; var c = poly[idx[2]];
                if (MathX.Cross(b - a, c - b) > 0f)
                {
                    output.Add(idx[0]); output.Add(idx[1]); output.Add(idx[2]);
                }
            }
            return clean;
        }

        private static bool AnyPointInside(IReadOnlyList<Vector2> poly, List<int> idx, int i0, int i1, int i2, Vector2 a, Vector2 b, Vector2 c)
        {
            for (int k = 0; k < idx.Count; k++)
            {
                int j = idx[k];
                if (j == i0 || j == i1 || j == i2) continue;
                var p = poly[j];
                if (p == a || p == b || p == c) continue;
                if (MathX.Cross(b - a, p - a) >= 0f && MathX.Cross(c - b, p - b) >= 0f && MathX.Cross(a - c, p - c) >= 0f)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Fills any outline, including one that crosses itself (outlines joined across mesh seams can): it is split
        /// into simple loops at its crossings and each loop is filled. <paramref name="verts"/> starts as the outline
        /// (counter-clockwise) and gets the crossing points appended; output triangles are counter-clockwise.
        /// </summary>
        public static void Fill(List<Vector2> verts, int outlineCount, List<int> output)
        {
            var first = new List<int>(outlineCount);
            for (int i = 0; i < outlineCount; i++) first.Add(i);
            var todo = new Stack<List<int>>();
            todo.Push(first);
            int splits = 0;
            var pts = new List<Vector2>();
            var local = new List<int>();
            while (todo.Count > 0)
            {
                var loop = todo.Pop();
                if (loop.Count < 3) continue;
                if (splits < 64 && FindCrossing(verts, loop, out int ei, out int ej, out var x))
                {
                    splits++;
                    int xi = verts.Count;
                    verts.Add(x);
                    // loop = ... L[ei] | L[ei+1] ... L[ej] | L[ej+1] ...: the crossing point closes both halves.
                    var a = new List<int> { xi };
                    for (int k = ei + 1; k <= ej; k++) a.Add(loop[k]);
                    var b = new List<int> { xi };
                    for (int k = ej + 1; k < loop.Count; k++) b.Add(loop[k]);
                    for (int k = 0; k <= ei; k++) b.Add(loop[k]);
                    todo.Push(a);
                    todo.Push(b);
                    continue;
                }

                pts.Clear();
                foreach (int i in loop) pts.Add(verts[i]);
                float area = MathX.SignedArea(pts);
                if (Math.Abs(area) < 1e-9f) continue;
                if (area < 0f)
                {
                    // A lobe of a figure eight runs the other way round; it is still inside the body.
                    loop.Reverse();
                    pts.Reverse();
                }
                local.Clear();
                Triangulate(pts, local);
                foreach (int k in local) output.Add(loop[k]);
            }
        }

        private static bool FindCrossing(List<Vector2> verts, List<int> loop, out int ei, out int ej, out Vector2 x)
        {
            int n = loop.Count;
            for (int i = 0; i < n; i++)
            {
                var a = verts[loop[i]]; var b = verts[loop[(i + 1) % n]];
                var lo = Vector2.Min(a, b); var hi = Vector2.Max(a, b);
                for (int j = i + 2; j < n; j++)
                {
                    if (i == 0 && j == n - 1) continue;   // neighbours through the closing edge
                    var c = verts[loop[j]]; var d = verts[loop[(j + 1) % n]];
                    if (Math.Max(c.X, d.X) < lo.X || Math.Min(c.X, d.X) > hi.X || Math.Max(c.Y, d.Y) < lo.Y || Math.Min(c.Y, d.Y) > hi.Y) continue;
                    if (!MathX.SegmentIntersection(a, b, c, d, out float t, out _)) continue;
                    ei = i; ej = j; x = a + (b - a) * t;
                    return true;
                }
            }
            ei = ej = -1; x = default;
            return false;
        }

        /// <summary>Fan triangulation around the centroid (fallback for messy outlines).</summary>
        public static void Fan(IReadOnlyList<Vector2> poly, List<int> output, int centerIndex)
        {
            bool ccw = MathX.SignedArea(poly) > 0f;
            for (int i = 0; i < poly.Count; i++)
            {
                int j = (i + 1) % poly.Count;
                if (ccw) { output.Add(centerIndex); output.Add(i); output.Add(j); }
                else { output.Add(centerIndex); output.Add(j); output.Add(i); }
            }
        }
    }
}
