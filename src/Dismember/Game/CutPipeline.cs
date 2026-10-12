using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using UnityEngine;
using Dismember.Core;

namespace Dismember.Game
{
    /// <summary>Where every ragdoll body was when a cut was planned, so results can be applied a few frames later.</summary>
    internal sealed class PoseSnapshot
    {
        public Vector3[] Pos;
        public Quaternion[] Rot;

        public static PoseSnapshot Take(NpcBody npc)
        {
            int n = npc.Muscles.Length;
            var s = new PoseSnapshot { Pos = new Vector3[n], Rot = new Quaternion[n] };
            for (int i = 0; i < n; i++)
            {
                var b = npc.Muscles[i].Body;
                if (b.IsDead()) { s.Rot[i] = Quaternion.identity; continue; }
                s.Pos[i] = b.position;
                s.Rot[i] = b.rotation;
            }
            return s;
        }

        /// <summary>Rigid motion of body <paramref name="seg"/> since the snapshot.</summary>
        public Quaternion DeltaRotation(NpcBody npc, int seg)
        {
            var b = npc.Muscles[seg].Body;
            return b.IsDead() ? Quaternion.identity : b.rotation * Quaternion.Inverse(Rot[seg]);
        }

        /// <summary>Maps a world point recorded at snapshot time to where it is now, riding on body <paramref name="seg"/>.</summary>
        public Vector3 Point(NpcBody npc, int seg, Vector3 then)
        {
            var b = npc.Muscles[seg].Body;
            if (b.IsDead()) return then;
            return b.position + DeltaRotation(npc, seg) * (then - Pos[seg]);
        }

        public Vector3 Direction(NpcBody npc, int seg, Vector3 then) => DeltaRotation(npc, seg) * then;
    }

    /// <summary>A planned cut: pose snapshot and cross-section analysis, done on the main thread.</summary>
    internal sealed class CutAttempt
    {
        public NpcBody Npc;
        public int Segment;
        public List<RendererInfo> Solved;
        public BodyModel Model;
        public CutSolver Solver;
        public CrossSectionReport Report;
        public CutRegion Region;
        public PoseSnapshot Snapshot;
        public int Generation;
        /// <summary>
        /// A wound still showing that this was worked out underneath (from the meshes it replaced): it is taken out the
        /// moment the result goes in, in the same frame, so the body never shows neither.
        /// </summary>
        public CutApplier.SlitLayer Under;
    }

    /// <summary>
    /// Runs the expensive part of a cut (splitting meshes, building caps: 10-60 ms) on a worker thread so VR frames
    /// are not dropped, then applies the result on the main thread.
    /// </summary>
    internal static class CutPipeline
    {
        private sealed class Job
        {
            public CutAttempt Attempt;
            public List<int> Sever;
            public Vector3 Push;
            public string Source;
            public bool Cauterize;
            public SlitSpec Slit;              // set: open a wound (ExecuteSlit) in sections Sever instead of cutting
            public Action<CutApplier.SlitLayer> OnSlit;
            /// <summary>A wound whose sections are picked on the cutter thread, after the analysis (null: none to open).</summary>
            public Func<CrossSectionReport, (List<int> Groups, SlitSpec Slit)> PickSlit;
            public CutResult Result;
            public Exception Error;
            public Action<CutResult> OnApplied;
        }

        private static readonly BlockingCollection<Job> Queue = new BlockingCollection<Job>();
        private static readonly ConcurrentQueue<Job> Finished = new ConcurrentQueue<Job>();
        private static Thread _worker;

        public static int Pending => Queue.Count;
        public static string LastTimings = "";
        private static int _slowReports = 12;

        /// <summary>Says in the log (a few times a session) where the time went when applying took long on the main thread.</summary>
        private static void ReportIfSlow(NpcBody npc, string what, CutApplier.Context ctx)
        {
            if (ctx.Time.Total < 12.0 || _slowReports <= 0) return;
            _slowReports--;
            Log.Msg($"Slow {what} on {npc.Name}: {ctx.Time.Total:F0} ms on the main thread ({ctx.Time})");
        }

        public static void Start()
        {
            if (_worker != null) return;
            _worker = new Thread(Work) { IsBackground = true, Name = "Dismember cutter" };
            _worker.Start();
        }

        private static void Work()
        {
            foreach (var job in Queue.GetConsumingEnumerable())
            {
                try
                {
                    if (job.PickSlit != null)
                    {
                        // The analysis was left for this thread (it is most of the work a wound takes on the main thread).
                        job.Attempt.Report = job.Attempt.Solver.Analyze();
                        var (groups, slit) = job.PickSlit(job.Attempt.Report);
                        job.Sever = groups;
                        job.Slit = slit;
                        if (groups == null || groups.Count == 0)
                        {
                            Finished.Enqueue(job);   // nothing to open: Result stays null
                            continue;
                        }
                    }
                    job.Result = job.Slit != null
                        ? job.Attempt.Solver.ExecuteSlit(job.Sever, job.Slit, new CutOptions())
                        : job.Attempt.Solver.Execute(job.Sever, new CutOptions());
                    // The next cut through these meshes needs their adjacency: build it here, not on the main thread.
                    foreach (var piece in job.Result.Pieces)
                    {
                        foreach (var rp in piece.Renderers)
                            if (rp.Mesh != null) _ = rp.Mesh.Topology.EdgeTriangles;
                        if (piece.Cap != null) _ = piece.Cap.Mesh.Topology;
                    }
                }
                catch (Exception e)
                {
                    job.Error = e;
                }
                Finished.Enqueue(job);
            }
        }

        /// <summary>
        /// Snapshots the piece of <paramref name="npc"/> that contains <paramref name="seg"/> and finds the cross-sections
        /// the plane passes through. Returns null when the NPC cannot be cut right now (meshes still loading, busy...).
        /// With <paramref name="under"/> (a wound opened in the mesh that is still showing), the meshes from before that
        /// wound are used, and the wound is swapped out when the result is applied.
        /// </summary>
        public static CutAttempt Prepare(NpcBody npc, int seg, CutRegion region, CutApplier.SlitLayer under = null, bool analyze = true)
        {
            if (npc.Busy || !npc.Valid) return null;
            if (!npc.EnsureMeshes()) return null;
            if (under != null && !under.Revertible) under = null;
            var solved = npc.RenderersOfPiece(seg);
            if (under?.Walls != null) solved.Remove(under.Walls);
            if (solved.Count == 0 || !solved.Any(r => r.AllowCaps)) return null;

            var sw = Stopwatch.StartNew();
            var model = npc.BuildModel(npc.PieceRoot(seg));
            var pose = npc.SnapshotPose();
            var skins = new SkinInstance[solved.Count];
            for (int i = 0; i < solved.Count; i++)
            {
                skins[i] = npc.SnapshotSkin(solved[i], i);
                if (under != null && under.Base.TryGetValue(solved[i], out var before)) skins[i].Mesh = before;
            }
            var solver = new CutSolver(model, skins, pose, region);
            var report = analyze ? solver.Analyze() : null;
            if (analyze) Log.Debug($"{npc.Name}: analysis {sw.Elapsed.TotalMilliseconds:F1} ms, {report.Groups.Count} sections");
            return new CutAttempt
            {
                Npc = npc,
                Segment = seg,
                Solved = solved,
                Model = model,
                Solver = solver,
                Report = report,
                Region = region,
                Snapshot = PoseSnapshot.Take(npc),
                Generation = npc.Generation,
                Under = under,
            };
        }

        /// <summary>Queues the severing of <paramref name="sever"/> (section ids from the attempt's report).</summary>
        public static void Submit(CutAttempt attempt, List<int> sever, Vector3 push, string source, bool cauterize = false, Action<CutResult> onApplied = null)
        {
            if (sever == null || sever.Count == 0) return;
            attempt.Npc.Busy = true;
            attempt.Npc.BusySince = Time.time;
            Queue.Add(new Job { Attempt = attempt, Sever = sever, Push = push, Source = source, Cauterize = cauterize, OnApplied = onApplied });
        }

        /// <summary>
        /// Queues a wound opened in sections <paramref name="groups"/> (ids from the attempt's report): nothing comes off,
        /// the skin opens along the cut and flesh walls line it (<see cref="CutSolver.ExecuteSlit"/>).
        /// </summary>
        public static void SubmitSlit(CutAttempt attempt, List<int> groups, SlitSpec slit, bool seared, Action<CutApplier.SlitLayer> onApplied)
        {
            if (groups == null || groups.Count == 0) return;
            attempt.Npc.Busy = true;
            attempt.Npc.BusySince = Time.time;
            Queue.Add(new Job { Attempt = attempt, Sever = groups, Slit = slit, Source = "wound", Cauterize = seared, OnSlit = onApplied });
        }

        /// <summary>
        /// Queues a wound whose sections are only picked after the analysis, which runs on the cutter thread too
        /// (<paramref name="attempt"/> from <see cref="Prepare"/> with analyze: false). <paramref name="pick"/> runs on
        /// that thread; returning no sections means nothing to open (<paramref name="onApplied"/> gets null).
        /// </summary>
        public static void SubmitSlit(CutAttempt attempt, Func<CrossSectionReport, (List<int>, SlitSpec)> pick, bool seared,
                                      Action<CutApplier.SlitLayer> onApplied)
        {
            attempt.Npc.Busy = true;
            attempt.Npc.BusySince = Time.time;
            Queue.Add(new Job { Attempt = attempt, PickSlit = pick, Source = "wound", Cauterize = seared, OnSlit = onApplied });
        }

        /// <summary>Main thread: applies finished cuts.</summary>
        public static void Update()
        {
            int applied = 0;
            while (applied < 2 && Finished.TryDequeue(out var job))
            {
                var a = job.Attempt;
                var npc = a.Npc;
                if (npc.Generation != a.Generation || !npc.Valid)
                {
                    // The NPC was restored or destroyed while we were cutting; the result belongs to its previous life.
                    if (npc.Generation == a.Generation) npc.Busy = false;
                    job.OnSlit?.Invoke(null);
                    continue;
                }
                npc.Busy = false;
                if (job.Error == null && job.Result == null)
                {
                    job.OnSlit?.Invoke(null);   // a wound that found nothing to open
                    continue;
                }
                if (job.Error != null)
                {
                    Log.Warn($"{(job.Slit != null ? "Wound" : "Cut")} failed on {npc.Name}: {job.Error}");
                    job.OnSlit?.Invoke(null);
                    continue;
                }
                var sw = Stopwatch.StartNew();
                // Worked out underneath a wound that is still showing: take it out first. If something changed those
                // meshes since (it cannot be taken out), the result no longer fits them.
                if (a.Under != null && !a.Under.Revert())
                {
                    Log.Debug($"{(job.Slit != null ? "wound" : "cut")} on {npc.Name} dropped: the meshes changed under it");
                    job.OnSlit?.Invoke(null);
                    continue;
                }
                int mark = npc.Undo.Count, rendererMark = npc.Renderers.Count;
                if (job.Slit != null)
                {
                    try
                    {
                        var wctx = new CutApplier.Context
                        {
                            Npc = npc, Solved = a.Solved, Result = job.Result, Region = a.Region, Snapshot = a.Snapshot,
                            HitSegment = a.Segment, Source = job.Source, Cauterize = job.Cauterize,
                        };
                        var layer = CutApplier.ApplySlit(wctx);
                        npc.WoundApplyMs = (float)sw.Elapsed.TotalMilliseconds;
                        Log.Debug($"Wound opened in {npc.Name} [{job.Result.Milliseconds:F0}+{sw.Elapsed.TotalMilliseconds:F0} ms]");
                        ReportIfSlow(npc, "wound update", wctx);
                        job.OnSlit?.Invoke(layer);
                    }
                    catch (Exception e)
                    {
                        npc.Rollback(mark, rendererMark);
                        Log.Warn($"Opening a wound in {npc.Name} failed (undone): {e.Message}");
                        job.OnSlit?.Invoke(null);
                    }
                    applied++;
                    continue;
                }
                var ctx = new CutApplier.Context
                {
                    Npc = npc,
                    Solved = a.Solved,
                    Result = job.Result,
                    Region = a.Region,
                    Snapshot = a.Snapshot,
                    HitSegment = a.Segment,
                    Push = job.Push,
                    Source = job.Source,
                    Cauterize = job.Cauterize,
                };
                try
                {
                    CutApplier.Apply(ctx);
                    job.OnApplied?.Invoke(job.Result);
                    ReportIfSlow(npc, "cut", ctx);
                }
                catch (Exception e)
                {
                    // All or nothing: a half-applied cut would leave holes in the body.
                    npc.Rollback(mark, rendererMark);
                    Log.Error($"Applying cut to {npc.Name} failed (undone): {e}");
                    applied++;
                    continue;
                }
                LastTimings = $"solve {job.Result.Milliseconds:F0} ms ({job.Result.Timings}), apply {sw.Elapsed.TotalMilliseconds:F0} ms";
                var lost = job.Result.Pieces.Where(p => !p.IsCore).SelectMany(p => p.BodySegments).Select(s => npc.Muscles[s].Name).ToList();
                Log.Msg($"Cut {npc.Name} ({job.Source}): {(lost.Count > 0 ? "severed " + string.Join(", ", lost) : "sliced")}" +
                        $"{(job.Result.Pieces.Any(p => p.IsFreeChunk) ? " + loose chunk" : "")} [{job.Result.Milliseconds:F0}+{sw.Elapsed.TotalMilliseconds:F0} ms]");
                Log.Debug($"  timings: {LastTimings}");
                applied++;
            }

            // A job that never came back must not lock its NPC forever.
            foreach (var npc in NpcRegistry.All)
                if (npc.Busy && Time.time - npc.BusySince > 5f && Queue.Count == 0 && Finished.IsEmpty) npc.Busy = false;
        }
    }
}
