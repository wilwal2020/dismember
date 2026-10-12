using System;
using System.Collections.Generic;
using System.Numerics;

namespace Dismember.Core
{
    /// <summary>Body region of a physics segment (mirrors PuppetMaster's Muscle.Group).</summary>
    public enum SegmentKind { Hips = 0, Spine = 1, Head = 2, Arm = 3, Hand = 4, Leg = 5, Foot = 6, Tail = 7, Prop = 8, Other = 9 }

    /// <summary>One rigid body of the ragdoll (a PuppetMaster muscle).</summary>
    public sealed class Segment
    {
        public int Index;
        public int Parent = -1;
        public readonly List<int> Children = new List<int>();
        public SegmentKind Kind;
        public string Name;
    }

    public sealed class BodyModel
    {
        public Segment[] Segments;
        public int Root;

        public static BodyModel Build(string[] names, int[] parents, SegmentKind[] kinds)
        {
            int n = names.Length;
            var m = new BodyModel { Segments = new Segment[n], Root = -1 };
            for (int i = 0; i < n; i++)
                m.Segments[i] = new Segment { Index = i, Parent = parents[i], Kind = kinds[i], Name = names[i] };
            for (int i = 0; i < n; i++)
            {
                int p = parents[i];
                if (p >= 0 && p < n && p != i) m.Segments[p].Children.Add(i);
                else
                {
                    m.Segments[i].Parent = -1;
                    if (m.Root < 0) m.Root = i;
                }
            }
            if (m.Root < 0 && n > 0) m.Root = 0;
            return m;
        }
    }

    /// <summary>One skinned renderer to be cut, with its pose at the moment of the cut.</summary>
    public sealed class SkinInstance
    {
        public int Id;
        public MeshData Mesh;
        /// <summary>World matrix of every renderer bone at cut time (System.Numerics row-vector convention).</summary>
        public Matrix4x4[] BoneWorld;
        /// <summary>Renderer bone -> segment index (-1 = not under any segment; treated as the root segment).</summary>
        public int[] BoneSegment;
        /// <summary>Segment -> index of the renderer bone that moves rigidly with it (the muscle's target bone), or -1.</summary>
        public int[] SegmentBone;
        /// <summary>Current blend shape weights (0..100), or null.</summary>
        public float[] BlendShapeWeights;
        /// <summary>Whether this renderer's cross-sections may produce cap geometry (false for hair cards, shadow proxies...).</summary>
        public bool AllowCaps = true;
        /// <summary>
        /// Per submesh: does it count as flesh? Null means all of them (when <see cref="AllowCaps"/>). Lets a body mesh
        /// with an alpha-cutout zipper or a face with transparent corneas still be cut, without capping the zipper.
        /// </summary>
        public bool[] SubmeshCaps;

        internal bool CapsTriangle(int tri)
        {
            if (!AllowCaps) return false;
            if (SubmeshCaps == null) return true;
            int sub = Mesh.Topology.TriSubmesh[tri];
            return sub < SubmeshCaps.Length && SubmeshCaps[sub];
        }

        // Static per-renderer data, computed on first use and reused across cuts (mesh and bone mapping never change).
        internal int[] CachedTriSegment;
        internal int[] CachedVertSegment;
        internal object CacheKey;
    }

    /// <summary>World-space pose data per segment at cut time.</summary>
    public sealed class PoseInfo
    {
        /// <summary>Joint position of each segment (where it connects to its parent).</summary>
        public Vector3[] SegmentOrigin;
        /// <summary>World matrix of each segment's physics body.</summary>
        public Matrix4x4[] SegmentBodyWorld;
        /// <summary>World matrix of each segment's visual target bone.</summary>
        public Matrix4x4[] SegmentTargetWorld;
    }

    /// <summary>
    /// The cutting plane plus the strip of it the blade actually sweeps.
    /// U runs along the blade, W is the cutting direction; points with W &lt; WMin are behind the edge.
    /// </summary>
    public sealed class CutRegion
    {
        public Vector3 Origin;
        public Vector3 Normal;
        public Vector3 AxisU;
        public Vector3 AxisW;
        public float UMin = float.NegativeInfinity, UMax = float.PositiveInfinity;
        public float WMin = float.NegativeInfinity;
        public float Margin = 0.02f;

        public static CutRegion Infinite(Vector3 origin, Vector3 normal)
        {
            normal = Vector3.Normalize(normal);
            var u = MathX.AnyPerpendicular(normal);
            return new CutRegion { Origin = origin, Normal = normal, AxisU = u, AxisW = Vector3.Cross(normal, u) };
        }

        /// <summary>
        /// Plane spanned by the blade's length axis and its cutting direction. U/W/Normal form a right-handed frame
        /// (U x W = Normal); the normal is flipped as needed so W matches <paramref name="cutDirection"/>.
        /// uMin/uMax are the blade extents along U, measured from <paramref name="origin"/>.
        /// </summary>
        public static CutRegion FromBlade(Vector3 origin, Vector3 planeNormal, Vector3 bladeAxis, Vector3 cutDirection, float uMin, float uMax, float wMin)
        {
            var n = Vector3.Normalize(planeNormal);
            var u = bladeAxis - n * Vector3.Dot(bladeAxis, n);
            u = MathX.SafeNormalize(u, MathX.AnyPerpendicular(n));
            var w = Vector3.Cross(n, u);
            if (Vector3.Dot(w, cutDirection) < 0f)
            {
                n = -n;
                w = -w;
            }
            return new CutRegion { Origin = origin, Normal = n, AxisU = u, AxisW = w, UMin = uMin, UMax = uMax, WMin = wMin };
        }

        /// <summary>
        /// Optional bend (a blade that curved as it went, like a lightsaber swept in an arc): the cut surface lies
        /// <c>BendN[k]</c> off the plane along the normal at <c>BendW[k]</c> along W, straight in between, and carries on
        /// in the direction of its last stretch past the ends. Null: flat. The surface is then a plane bent along lines
        /// parallel to U, so plane coordinates (U, W) still describe every point of it once.
        /// </summary>
        public float[] BendW, BendN;

        public bool Bent => BendW != null && BendW.Length >= 2;

        /// <summary>How far the surface is off the plane (along the normal) at <paramref name="w"/>.</summary>
        public float BendAt(float w)
        {
            if (!Bent) return 0f;
            int n = BendW.Length;
            if (w <= BendW[0]) return BendN[0] + (w - BendW[0]) * Slope(0);
            if (w >= BendW[n - 1]) return BendN[n - 1] + (w - BendW[n - 1]) * Slope(n - 2);
            int k = Segment(w);
            return BendN[k] + (w - BendW[k]) * Slope(k);
        }

        /// <summary>d(bend)/dW at <paramref name="w"/>.</summary>
        public float SlopeAt(float w)
        {
            if (!Bent) return 0f;
            int n = BendW.Length;
            if (w <= BendW[0]) return Slope(0);
            if (w >= BendW[n - 1]) return Slope(n - 2);
            return Slope(Segment(w));
        }

        private float Slope(int k) => (BendN[k + 1] - BendN[k]) / Math.Max(1e-5f, BendW[k + 1] - BendW[k]);

        private int Segment(float w)
        {
            int lo = 0, hi = BendW.Length - 2;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (BendW[mid] <= w) lo = mid; else hi = mid - 1;
            }
            return lo;
        }

        /// <summary>Signed offset from the cut surface along the normal (for a flat cut: the distance to the plane).</summary>
        public float SignedDistance(Vector3 p)
        {
            var d = p - Origin;
            float n = Vector3.Dot(d, Normal);
            return Bent ? n - BendAt(Vector3.Dot(d, AxisW)) : n;
        }

        public Vector2 To2D(Vector3 p) { var d = p - Origin; return new Vector2(Vector3.Dot(d, AxisU), Vector3.Dot(d, AxisW)); }

        /// <summary>The point of the cut surface at plane coordinates <paramref name="q"/>, moved <paramref name="offset"/> along the normal.</summary>
        public Vector3 To3D(Vector2 q, float offset = 0f) => Origin + AxisU * q.X + AxisW * q.Y + Normal * (BendAt(q.Y) + offset);

        /// <summary>The surface normal at <paramref name="q"/> (the plane's, tilted where the cut bends).</summary>
        public Vector3 NormalAt(Vector2 q) => Bent ? Vector3.Normalize(Normal - AxisW * SlopeAt(q.Y)) : Normal;

        public bool InStrip(Vector2 q) =>
            q.X >= UMin - Margin && q.X <= UMax + Margin && q.Y >= WMin - Margin;

        /// <summary>A copy of this region with its plane moved by a rigid motion (body moved since).</summary>
        public CutRegion With(Vector3 origin, Vector3 normal, Vector3 axisU, Vector3 axisW) => new CutRegion
        {
            Origin = origin, Normal = normal, AxisU = axisU, AxisW = axisW,
            UMin = UMin, UMax = UMax, WMin = WMin, Margin = Margin, BendW = BendW, BendN = BendN,
        };
    }

    /// <summary>
    /// A cut that opens the body without separating anything: a wound. Over the part of the cut surface the blade went
    /// through (U between the region's UMin and UMax, W from its WMin to <see cref="Depth"/>) the skin is split and its
    /// lips pulled apart, and the inside is lined with flesh: two walls that meet where the slit ends inside the body.
    /// A stab is the same thing along the blade: U across the blade's width, W the way it went in.
    /// </summary>
    public sealed class SlitSpec
    {
        /// <summary>The slit reaches this far along W (plane coordinates).</summary>
        public float Depth;
        /// <summary>
        /// Optional: a depth of its own for some cross-sections (group id -> depth along W), for a blade that is through
        /// one part and half way into the next. Others use <see cref="Depth"/>.
        /// </summary>
        public Dictionary<int, float> GroupDepth;

        public float DepthOf(int group) => GroupDepth != null && GroupDepth.TryGetValue(group, out float d) ? d : Depth;
        /// <summary>How far each lip is pulled back from the cut surface at most (m).</summary>
        public float Opening = 0.006f;
        /// <summary>Distance from the slit's closed edges over which it opens up to <see cref="Opening"/> (m).</summary>
        public float Taper = 0.025f;
    }

    /// <summary>A closed outline of the body where the plane passes through it.</summary>
    public sealed class SectionLoop
    {
        public List<Vector2> Points2D = new List<Vector2>();
        public List<Vector3> Points3D = new List<Vector3>();
        public List<int> Edges = new List<int>();   // indices into the solver's intersection list
        /// <summary>For each point, the intersection (triangle) it came from — tells caps which piece owns that stretch of outline.</summary>
        public List<int> PointEdge = new List<int>();
        /// <summary>For each point, the outward surface normal there and the segment that flesh moves with.</summary>
        public List<Vector3> Normals3D = new List<Vector3>();
        public List<int> PointSegment = new List<int>();
        /// <summary>True when the loop comes from a renderer that may produce caps (not hair cards / shadow proxies).</summary>
        public bool Capable;
        public bool WasClosed;
        public float Area;
        public Vector2 Centroid;
        public Vector2 Min, Max;
        public int Parent = -1;                     // containing loop, if nested (clothing over skin)
    }

    /// <summary>A separate cross-section (an arm, the neck, the torso...) the blade has to get through.</summary>
    public sealed class SectionGroup
    {
        public int Id;
        public int OuterLoop;
        public List<int> Loops = new List<int>();
        public float Area;              // m^2
        public float Coverage;          // fraction of the outline inside the blade strip
        public bool InReach;
        public float WMin, WMax;        // extent along the cutting direction
        public HashSet<int> Segments = new HashSet<int>();
        public List<(int segment, Vector2 point)> BoneCrossings = new List<(int, Vector2)>();
        public Vector3 Centroid3D;
        public bool FullyCovered => Coverage >= 0.999f;
    }

    public sealed class CrossSectionReport
    {
        public List<SectionLoop> Loops = new List<SectionLoop>();
        public List<SectionGroup> Groups = new List<SectionGroup>();
        public float MillisecondsSkinning, MillisecondsSections;
    }

    public sealed class CutOptions
    {
        public float RimWidth = 0.006f;
        public float CapOffset = 0.0004f;
        public bool MakeCaps = true;
    }

    /// <summary>Output mesh for one renderer within one piece.</summary>
    public sealed class RendererPiece
    {
        public int SkinId;
        public MeshData Mesh;
        /// <summary>True: bones are the original renderer bones (core piece). False: see BoneSegments.</summary>
        public bool UsesOriginalBones;
        /// <summary>The cut does not touch this renderer: keep its current mesh (Mesh is null).</summary>
        public bool Unchanged;
        /// <summary>When not using original bones: output bone index -> segment (bone = new transform on that segment's body).</summary>
        public int[] BoneSegments;
    }

    public sealed class CapPiece
    {
        public MeshData Mesh;
        /// <summary>Output bone index -> segment.</summary>
        public int[] BoneSegments;
        /// <summary>True: bones are the segments' visual target bones (core). False: new transforms on the bodies.</summary>
        public bool UseTargetBones;
    }

    /// <summary>A rigid convex chunk of a cut segment that rides on another body.</summary>
    public sealed class ChunkCollider
    {
        public int Segment;        // the cut segment the flesh came from
        public int BodySegment;    // body it is attached to
        public Vector3[] PointsWorld;
    }

    public sealed class Piece
    {
        public int Index;
        public bool IsCore;
        public bool IsFreeChunk;
        public List<int> BodySegments = new List<int>();
        public List<RendererPiece> Renderers = new List<RendererPiece>();
        public CapPiece Cap;
        /// <summary>For free chunks: mesh vertices are relative to this world position (identity rotation).</summary>
        public Vector3 Origin;
        public Vector3[] HullPointsLocal;
        public float Volume;
        public int SourceSegment = -1;
    }

    public sealed class CutResult
    {
        public List<Piece> Pieces = new List<Piece>();
        public bool[] SegmentCut;
        public int[] SegmentOwnerSide;   // 0 = positive side of the plane, 1 = negative; -1 if not cut
        public int[] SegmentPiece;       // piece holding each segment's body
        public List<int> FreedJoints = new List<int>();          // segment whose joint to its parent must be released
        /// <summary>
        /// Bodies that lost their joint but are still held on by flesh on their side of the cut (the arm below a cut
        /// that took the chest away): hang <c>Body</c> from <c>Holder</c> at <c>At</c> (world, at cut time).
        /// </summary>
        public List<(int Body, int Holder, Vector3 At)> Rejoined = new List<(int, int, Vector3)>();
        public List<ChunkCollider> ChunkColliders = new List<ChunkCollider>();
        public List<int> SeveredGroups = new List<int>();
        public List<(Vector3 center, Vector3 normal, float area)> Wounds = new List<(Vector3, Vector3, float)>();
        public int CorePiece = -1;
        public float Milliseconds;
        public string Timings = "";
        public List<string> Warnings = new List<string>();

        /// <summary>Segments whose bodies are no longer part of the core piece.</summary>
        public IEnumerable<int> SegmentsLostFromCore()
        {
            for (int s = 0; s < SegmentPiece.Length; s++)
                if (SegmentPiece[s] != CorePiece) yield return s;
        }
    }
}
