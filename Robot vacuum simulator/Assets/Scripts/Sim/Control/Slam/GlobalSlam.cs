using System.Collections.Generic;
using System.Threading.Tasks;
using RobotVacuum.Sim.Sensors;
using UnityEngine;
using G = RobotVacuum.Sim.Control.GlobalSlamConstants;
using S = RobotVacuum.Sim.Control.LocalSlamConstants;

namespace RobotVacuum.Sim.Control
{
    /// <summary>
    /// SLAM ONLY: global SLAM, i.e. loop closure (design doc, section 20; Cartographer's PoseGraph2D and
    /// ConstraintBuilder2D), with submaps as rigid pieces.
    ///
    ///   * Each submap has a GLOBAL pose (where it really is), starting from where local SLAM put it.
    ///   * Consecutive submaps are linked by local SLAM's own relative pose (σ = its drift over one submap).
    ///   * LOOP CLOSURE: every 3rd scan local SLAM adds is searched for, exactly (<see cref="SubmapMatcher"/>), in
    ///     the nearest finished submaps. Each match links that submap to the scan's own submap.
    ///   * Every 30 scans, if a link was added, the graph is re-solved (<see cref="PoseGraph"/>, Huber loss on loop
    ///     links; the first submap, at the dock, fixed), moving submaps as rigid pieces.
    ///
    /// The robot's believed pose is local SLAM's pose carried by the matching submap's correction; the Internal
    /// Model is redrawn from the submaps at their new poses when they move. Plain C#, no Unity objects.
    ///
    /// BACKGROUND WORK (live submap mode): the search and the solve run on a worker thread. Each is handed copies
    /// of what it needs when it is due (the scan's points and, per candidate, the finished submap's grid and the
    /// guess; or the submap poses and links), and its result is applied exactly <see cref="ApplyAfterNodes"/> scans
    /// later, waiting for the worker only if it is not done by then. A finished submap's grid never changes in live
    /// mode, so the worker reads it while local SLAM writes the active ones. Results depend only on the scans, never
    /// on timing, so runs are reproducible; and the same with <see cref="Threaded"/> off (the work then runs at once,
    /// but is still applied later). Node mode (tests) stays as it was, all within the step.
    /// </summary>
    public sealed class GlobalSlam
    {
        readonly LocalSlam local;
        readonly SubmapMatcher matcher = new SubmapMatcher();
        readonly PoseGraph graph = new PoseGraph { HuberSigmas = G.HuberSigmas };
        readonly List<GraphPose> globals = new List<GraphPose>();
        readonly List<GraphLink> links = new List<GraphLink>();
        readonly List<Vector2> points = new List<Vector2>(360);
        readonly List<int> candidates = new List<int>();

        int nodes;
        int nodesSinceSolve;
        bool newLinks;
        int referenceSubmap; // the submap whose correction carries local SLAM's pose into the global frame

        public GlobalSlam(LocalSlam localSlam) => local = localSlam;

        // ---- Background work (live submap mode) ----------------------------------------------------------------

        /// <summary>A search's or solve's result is applied this many scans (graph nodes) after it was started.</summary>
        public const int ApplyAfterNodes = 3;

        /// <summary>Run searches and solves on a worker thread (true), or at once on the calling thread (false). Results are the same either way.</summary>
        public bool Threaded { get; set; } = true;

        /// <summary>A search started at <c>dueNode - ApplyAfterNodes</c>: per candidate, the inputs and the match.</summary>
        sealed class SearchJob
        {
            public int dueNode, home;
            public GraphPose scanInHome;
            public List<Vector2> points;
            public int[] submap;
            public ProbabilityGrid[] grid;
            public RobotPose[] guess;
            public bool[] accepted;
            public RobotPose[] found;
            public Task work;
        }

        /// <summary>A solve started at <c>dueNode - ApplyAfterNodes</c>: copies of the submap poses (solved in place) and links.</summary>
        sealed class SolveJob
        {
            public int dueNode;
            public List<GraphPose> before, solved;
            public List<GraphLink> links;
            public Task work;
        }

        readonly Queue<SearchJob> pendingSearches = new Queue<SearchJob>();
        SolveJob pendingSolve;

        /// <summary>Real ms the main thread has waited for the worker in total (it waits only when a result is due and not ready).</summary>
        public double WaitMs { get; private set; }

        Task Run(System.Action work)
        {
            if (Threaded) return Task.Run(work);
            work();
            return Task.CompletedTask;
        }

        void Await(Task work)
        {
            if (work.IsCompleted)
            {
                if (work.IsFaulted) throw work.Exception.InnerException;
                return;
            }
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            try { work.Wait(); }
            catch (System.AggregateException e) { throw e.InnerException; }
            finally { WaitMs += MsSince(started); }
        }

        /// <summary>Waits for any background work still running (before a new run reuses the matcher and graph).</summary>
        void DrainBackgroundWork()
        {
            foreach (var job in pendingSearches)
                try { job.work?.Wait(); } catch (System.AggregateException) { }
            if (pendingSolve != null)
                try { pendingSolve.work?.Wait(); } catch (System.AggregateException) { }
            pendingSearches.Clear();
            pendingSolve = null;
        }

        // ---- Counts (for result.json) ----
        public int Searches { get; private set; }
        public int LoopLinks { get; private set; }
        public int Optimisations { get; private set; }

        /// <summary>
        /// Real milliseconds the last <see cref="AfterTrack(SensorFrame)"/> spent in the loop-closure search and in the
        /// pose-graph solve (0 when it did neither). For the lag log; reporting only, never read by the simulation.
        /// </summary>
        public double LastSearchMs { get; private set; }
        public double LastSolveMs { get; private set; }

        static double MsSince(long started) =>
            (System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

        /// <summary>The submaps' global poses, by submap index.</summary>
        public IReadOnlyList<GraphPose> SubmapGlobalPoses => globals;

        /// <summary>Every link in the graph (tests and diagnostics).</summary>
        public IReadOnlyList<GraphLink> Links => links;

        public SubmapMatcher Matcher => matcher;

        public void Begin()
        {
            DrainBackgroundWork();
            WaitMs = 0;
            globals.Clear();
            links.Clear();
            nodes = nodesSinceSolve = 0;
            newLinks = false;
            referenceSubmap = 0;
            Searches = LoopLinks = Optimisations = 0;
        }

        /// <summary>
        /// After <see cref="LocalSlam.Track"/>: registers new submaps, searches for loop closures for a newly added scan,
        /// and re-solves when due. Returns true if submaps moved enough that the Internal Model should be redrawn.
        /// </summary>
        public bool AfterTrack(SensorFrame frame)
        {
            LastSearchMs = LastSolveMs = 0;
            AddNewSubmaps();
            if (!local.InsertedThisStep) return false;

            nodes++;
            nodesSinceSolve++;

            // Results due now, in the order their work was started (a search started with a solve applies first).
            long searchStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            while (pendingSearches.Count > 0 && pendingSearches.Peek().dueNode <= nodes) ApplySearch(pendingSearches.Dequeue());
            bool redraw = false;
            long solveStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            if (pendingSolve != null && pendingSolve.dueNode <= nodes)
            {
                redraw = ApplySolve(pendingSolve);
                pendingSolve = null;
            }
            LastSolveMs = MsSince(solveStarted);
            LastSearchMs = (solveStarted - searchStarted) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

            if (nodes % G.SearchEveryNodes == 0)
            {
                searchStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                StartSearch(frame, local.InsertedPose, local.InsertedHome);
                LastSearchMs += MsSince(searchStarted);
            }

            if (nodesSinceSolve >= G.OptimiseEveryNodes && newLinks && pendingSolve == null)
            {
                solveStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                StartSolve();
                LastSolveMs += MsSince(solveStarted);
                nodesSinceSolve = 0;
                newLinks = false;
            }
            if (local.ActiveSubmaps.Count > 0) referenceSubmap = local.ActiveSubmaps[0].index;
            return redraw;
        }

        // ---- Frames -----------------------------------------------------------------------------------------

        /// <summary>A pose in local SLAM's frame, in the global frame (carried by the matching submap's correction).</summary>
        public RobotPose ToGlobal(RobotPose localPose)
        {
            if (globals.Count == 0) return localPose;
            int s = Mathf.Min(referenceSubmap, globals.Count - 1);
            var p = GraphPose.Compose(globals[s], GraphPose.Relative(local.Submaps[s].LocalPose, GraphPose.From(localPose)));
            return p.ToRobotPose();
        }

        /// <summary>A global point in local SLAM's frame.</summary>
        public Vector2 ToLocal(Vector2 global)
        {
            if (globals.Count == 0) return global;
            int s = Mathf.Min(referenceSubmap, globals.Count - 1);
            var p = GraphPose.Compose(local.Submaps[s].LocalPose, GraphPose.Relative(globals[s], new GraphPose(global.x, global.y, 0)));
            return new Vector2((float)p.x, (float)p.y);
        }

        /// <summary>A point in submap <paramref name="submap"/>'s grid (local SLAM's frame) in the global frame.</summary>
        public Vector2 SubmapToGlobal(int submap, Vector2 point)
        {
            if (submap >= globals.Count) return point;
            var p = GraphPose.Compose(globals[submap], GraphPose.Relative(local.Submaps[submap].LocalPose, new GraphPose(point.x, point.y, 0)));
            return new Vector2((float)p.x, (float)p.y);
        }

        /// <summary>
        /// Submap <paramref name="submap"/>'s correction as a plain 2D transform: global = R(angle) · (point − localOrigin)
        /// + globalOrigin (its local frame isn't rotated, so this is exact). For drawing many cells cheaply.
        /// </summary>
        public void Transform(int submap, out float cos, out float sin, out Vector2 localOrigin, out Vector2 globalOrigin)
        {
            var g = globals[submap];
            float rad = (float)(g.heading * System.Math.PI / 180.0);
            cos = Mathf.Cos(rad);
            sin = Mathf.Sin(rad);
            localOrigin = local.Submaps[submap].origin;
            globalOrigin = new Vector2((float)g.x, (float)g.y);
        }

        /// <summary>The submap whose correction the current pose and live map updates use.</summary>
        public int ReferenceSubmap => Mathf.Min(referenceSubmap, Mathf.Max(0, globals.Count - 1));

        // ---- Internals --------------------------------------------------------------------------------------

        void AddNewSubmaps()
        {
            var submaps = local.Submaps;
            while (globals.Count < submaps.Count)
            {
                int k = globals.Count;
                if (k == 0)
                {
                    globals.Add(submaps[0].LocalPose); // the dock: local and global agree, and it is held fixed
                    continue;
                }

                // Placed by the reference submap's current correction, and linked to the previous submap by local SLAM.
                int r = Mathf.Min(referenceSubmap, k - 1);
                globals.Add(GraphPose.Compose(globals[r], GraphPose.Relative(submaps[r].LocalPose, submaps[k].LocalPose)));
                links.Add(new GraphLink(k - 1, k, GraphPose.Relative(submaps[k - 1].LocalPose, submaps[k].LocalPose),
                    G.ConsecutiveSigmaMeters, G.ConsecutiveSigmaDegrees, revisit: false));
            }
        }

        /// <summary>
        /// Starts a loop-closure search for the scan just added (in <paramref name="home"/>'s submap at
        /// <paramref name="scanLocal"/>): picks the nearest finished submaps and each one's guess now, and hands the
        /// matching to the worker. Applied by <see cref="ApplySearch"/> <see cref="ApplyAfterNodes"/> scans later.
        /// </summary>
        void StartSearch(SensorFrame frame, RobotPose scanLocal, int home)
        {
            CollectPoints(frame);
            if (points.Count < S.MinHits) return;

            // Where the graph currently puts this scan.
            var scanGlobal = GraphPose.Compose(globals[home], GraphPose.Relative(local.Submaps[home].LocalPose, GraphPose.From(scanLocal)));
            var scanInHome = GraphPose.Relative(local.Submaps[home].LocalPose, GraphPose.From(scanLocal));

            // The nearest finished submaps (by where their scans were taken), nearest first; ties: the older.
            candidates.Clear();
            var submaps = local.Submaps;
            for (int i = 0; i < submaps.Count && i < globals.Count; i++)
            {
                if (!submaps[i].Finished || i == home) continue;
                if (DistanceSq(i, scanGlobal) <= G.MaxSearchDistanceMeters * G.MaxSearchDistanceMeters) candidates.Add(i);
            }
            candidates.Sort((a, b) =>
            {
                int byDistance = DistanceSq(a, scanGlobal).CompareTo(DistanceSq(b, scanGlobal));
                return byDistance != 0 ? byDistance : a.CompareTo(b);
            });

            int tries = Mathf.Min(G.MaxSubmapsPerSearch, candidates.Count);
            if (tries == 0) return;
            var job = new SearchJob
            {
                dueNode = nodes + ApplyAfterNodes,
                home = home,
                scanInHome = scanInHome,
                points = new List<Vector2>(points),
                submap = new int[tries],
                grid = new ProbabilityGrid[tries],
                guess = new RobotPose[tries],
                accepted = new bool[tries],
                found = new RobotPose[tries],
            };
            for (int t = 0; t < tries; t++)
            {
                int i = candidates[t];
                Searches++;
                job.submap[t] = i;
                job.grid[t] = submaps[i].grid;
                // The guess, in submap i's grid (local SLAM's frame at the time it was built).
                job.guess[t] = GraphPose.Compose(submaps[i].LocalPose, GraphPose.Relative(globals[i], scanGlobal)).ToRobotPose();
            }

            // The worker: only the matcher (its own caches) and the finished grids, which never change.
            job.work = Run(() =>
            {
                for (int t = 0; t < job.submap.Length; t++)
                    job.accepted[t] = matcher.Match(job.grid[t], job.points, job.guess[t], out job.found[t]);
            });
            pendingSearches.Enqueue(job);
        }

        void ApplySearch(SearchJob job)
        {
            Await(job.work);
            var submaps = local.Submaps;
            for (int t = 0; t < job.submap.Length; t++)
            {
                int i = job.submap[t];
                if (!job.accepted[t]) continue;

                // Link submap i to the scan's own submap through the scan: (i → scan) ∘ (home → scan)⁻¹.
                var iToScan = GraphPose.Relative(submaps[i].LocalPose, GraphPose.From(job.found[t]));
                var measured = GraphPose.Compose(iToScan, Inverse(job.scanInHome));
                links.Add(new GraphLink(i, job.home, measured, G.LoopSigmaMeters, G.LoopSigmaDegrees, revisit: true));
                LoopLinks++;
                newLinks = true;
            }
        }

        /// <summary>Starts a re-solve of the submap graph on copies of the poses and links; applied by <see cref="ApplySolve"/>.</summary>
        void StartSolve()
        {
            Optimisations++;
            var job = new SolveJob
            {
                dueNode = nodes + ApplyAfterNodes,
                before = new List<GraphPose>(globals),
                solved = new List<GraphPose>(globals),
                links = new List<GraphLink>(links),
            };
            job.work = Run(() => graph.Optimise(job.solved, job.links));
            pendingSolve = job;
        }

        /// <summary>
        /// Applies a finished solve. Submaps added since it started weren't in it: each is re-placed after its
        /// predecessor by local SLAM's relative pose, as when it was added. True if the submaps moved enough to redraw.
        /// </summary>
        bool ApplySolve(SolveJob job)
        {
            Await(job.work);
            double moved = 0, turned = 0;
            for (int i = 0; i < job.solved.Count && i < globals.Count; i++)
            {
                double dx = job.solved[i].x - job.before[i].x, dy = job.solved[i].y - job.before[i].y;
                moved = System.Math.Max(moved, System.Math.Sqrt(dx * dx + dy * dy));
                turned = System.Math.Max(turned, System.Math.Abs(GraphPose.WrapDegrees(job.solved[i].heading - job.before[i].heading)));
                globals[i] = job.solved[i];
            }
            var submaps = local.Submaps;
            for (int k = job.solved.Count; k < globals.Count; k++)
                globals[k] = GraphPose.Compose(globals[k - 1], GraphPose.Relative(submaps[k - 1].LocalPose, submaps[k].LocalPose));
            return moved > G.RedrawMoveMeters || turned > G.RedrawTurnDegrees;
        }

        double DistanceSq(int submap, GraphPose p)
        {
            Vector2 c = SubmapToGlobal(submap, local.Submaps[submap].Centre);
            double dx = c.x - p.x, dy = c.y - p.y;
            return dx * dx + dy * dy;
        }

        static GraphPose Inverse(GraphPose p) => GraphPose.Relative(p, new GraphPose(0, 0, 0));

        void CollectPoints(SensorFrame frame)
        {
            points.Clear();
            var rays = frame.lidarRays;
            int stride = Mathf.Max(1, local.Profile.rayStride), seen = 0;
            for (int i = 0; i < rays.Count; i++)
            {
                if (!rays[i].hit || rays[i].range > S.LoopClosurePointRangeMeters) continue;
                if (seen++ % stride != 0) continue;
                points.Add(RobotPose.DirectionOf(rays[i].bearing) * rays[i].range);
            }
        }
    }
}
