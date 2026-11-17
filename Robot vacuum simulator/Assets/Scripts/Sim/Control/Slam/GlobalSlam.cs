using System.Collections.Generic;
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
    /// Model is redrawn from the submaps at their new poses when they move. Plain C#, no Unity objects; all of it
    /// within the physics step that triggers it.
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

        // ---- Counts (for result.json) ----
        public int Searches { get; private set; }
        public int LoopLinks { get; private set; }
        public int Optimisations { get; private set; }

        /// <summary>The submaps' global poses, by submap index.</summary>
        public IReadOnlyList<GraphPose> SubmapGlobalPoses => globals;

        /// <summary>Every link in the graph (tests and diagnostics).</summary>
        public IReadOnlyList<GraphLink> Links => links;

        public SubmapMatcher Matcher => matcher;

        /// <summary>Diagnostics only (tests): the consecutive links' heading σ instead (0 = the constant).</summary>
        internal static float ConsecutiveSigmaDegreesOverride = 0f;

        /// <summary>Diagnostics only (tests): loop links' σ × this.</summary>
        internal static float LoopSigmaScale = 1f;

        /// <summary>Diagnostics (tests): when true, every search this step is logged in <see cref="SearchLog"/> (cleared each step).</summary>
        public bool LogSearches { get; set; }
        public readonly List<(int submap, int home, RobotPose guess, RobotPose found, bool accepted, float score)> SearchLog =
            new List<(int, int, RobotPose, RobotPose, bool, float)>();

        public void Begin()
        {
            globals.Clear();
            links.Clear();
            poses.Clear();
            submapVar.Clear();
            nodeVar.Clear();
            nodeOdometry.Clear();
            submapNodes.Clear();
            rendered.Clear();
            Rebuilds = 0;
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
            SearchLog.Clear();
            AddNewSubmaps();
            if (!local.InsertedThisStep) return false;

            nodes++;
            nodesSinceSolve++;
            if (nodes % G.SearchEveryNodes == 0) Search(frame, local.InsertedPose, local.InsertedHome);

            bool redraw = false;
            if (nodesSinceSolve >= G.OptimiseEveryNodes && newLinks)
            {
                redraw = Optimise();
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
            if (NodeMode) return nodeVar.Count == 0 ? localPose : MapLocal(GraphPose.From(localPose)).ToRobotPose();
            if (globals.Count == 0) return localPose;
            int s = Mathf.Min(referenceSubmap, globals.Count - 1);
            var p = GraphPose.Compose(globals[s], GraphPose.Relative(local.Submaps[s].LocalPose, GraphPose.From(localPose)));
            return p.ToRobotPose();
        }

        /// <summary>A global point in local SLAM's frame.</summary>
        public Vector2 ToLocal(Vector2 global)
        {
            if (NodeMode && nodeVar.Count > 0)
            {
                int last = nodeVar.Count - 1;
                var q = GraphPose.Compose(GraphPose.From(local.Nodes[last].pose), GraphPose.Relative(poses[nodeVar[last]], new GraphPose(global.x, global.y, 0)));
                return new Vector2((float)q.x, (float)q.y);
            }
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

        // ---- Node mode (SLAM_Option2_Design.md, stage 1) --------------------------------------------------------
        //
        // Every scan local SLAM adds is a node with its own pose in the graph, alongside the submaps. Links: node → each
        // submap it went into (local SLAM's fit), node → node (odometry, a soft vote with Huber loss), and older submap →
        // node (loop closure). After each solve, every finished submap whose scans moved relative to it is rebuilt from
        // those scans at their corrected poses, so pose errors written into the map get rewritten.

        /// <summary>Node-level graph and submap rebuilding. Set before the first <see cref="AfterTrack(SensorFrame, RobotPose)"/>.</summary>
        public bool NodeMode { get; set; }

        /// <summary>Submaps rebuilt from their scans so far.</summary>
        public int Rebuilds { get; private set; }

        // Every graph variable (submaps and nodes, in creation order; variable 0 is the dock submap, held fixed).
        readonly List<GraphPose> poses = new List<GraphPose>();
        readonly List<int> submapVar = new List<int>();
        readonly List<int> nodeVar = new List<int>();
        readonly List<GraphPose> nodeOdometry = new List<GraphPose>();
        readonly List<List<int>> submapNodes = new List<List<int>>();
        readonly List<List<GraphPose>> rendered = new List<List<GraphPose>>(); // per submap: each of its nodes relative to it, as last drawn
        readonly List<(ScanNode, RobotPose)> rebuildScratch = new List<(ScanNode, RobotPose)>();

        // Link uncertainties, node mode.
        const double NodeSigmaMeters = 0.01, NodeSigmaDegrees = 0.3;            // a node's fit to its submap
        const double OdometrySigmaMeters = 0.01, OdometrySigmaPerMeter = 0.03;  // odometry: 1 cm + 3% of distance
        const double OdometrySigmaDegrees = 0.2, OdometrySigmaPerDegree = 0.03; // 0.2° + 3% of the turn

        /// <summary>A node's pose in the global frame (node mode).</summary>
        public GraphPose NodeGlobalPose(int node) => poses[nodeVar[node]];

        /// <summary>
        /// Node mode: as <see cref="AfterTrack(SensorFrame)"/>, with <paramref name="odometry"/> = the pure dead-reckoned
        /// pose (odometry alone, never corrected), for the odometry links between nodes.
        /// </summary>
        public bool AfterTrack(SensorFrame frame, RobotPose odometry)
        {
            if (!NodeMode) return AfterTrack(frame);
            SearchLog.Clear();
            AddNewVariables(odometry);
            if (!local.InsertedThisStep) return false;

            nodes++;
            nodesSinceSolve++;
            if (nodes % G.SearchEveryNodes == 0) SearchForNode(frame, nodeVar.Count - 1);

            bool redraw = false;
            if (nodesSinceSolve >= G.OptimiseEveryNodes)
            {
                redraw = OptimiseNodes();
                nodesSinceSolve = 0;
            }
            if (local.ActiveSubmaps.Count > 0) referenceSubmap = local.ActiveSubmaps[0].index;
            return redraw;
        }

        /// <summary>A local-frame pose in the global frame, carried by the newest node's correction.</summary>
        GraphPose MapLocal(GraphPose localPose)
        {
            int last = nodeVar.Count - 1;
            if (last < 0) return localPose;
            return GraphPose.Compose(poses[nodeVar[last]], GraphPose.Relative(GraphPose.From(local.Nodes[last].pose), localPose));
        }

        void AddNewVariables(RobotPose odometry)
        {
            var submaps = local.Submaps;
            while (submapVar.Count < submaps.Count)
            {
                int k = submapVar.Count;
                submapVar.Add(poses.Count);
                poses.Add(k == 0 ? submaps[0].LocalPose : MapLocal(submaps[k].LocalPose));
                globals.Add(poses[poses.Count - 1]);
                submapNodes.Add(new List<int>());
                rendered.Add(new List<GraphPose>());

                // A new submap has no scans linked to it until the next one is added; hold it loosely to the newest
                // node (it starts where that scan was) so a solve in between is never singular.
                int last = nodeVar.Count - 1;
                if (k > 0 && last >= 0)
                    links.Add(new GraphLink(nodeVar[last], submapVar[k],
                        GraphPose.Relative(GraphPose.From(local.Nodes[last].pose), submaps[k].LocalPose), 0.05, 1.0, revisit: false));
            }

            var all = local.Nodes;
            while (nodeVar.Count < all.Count)
            {
                int n = nodeVar.Count;
                var node = all[n];
                var nodeLocal = GraphPose.From(node.pose);
                var initial = MapLocal(nodeLocal);
                nodeVar.Add(poses.Count);
                poses.Add(initial);
                var odo = GraphPose.From(odometry);
                nodeOdometry.Add(odo);

                foreach (int s in new[] { node.submapA, node.submapB })
                {
                    if (s < 0) continue;
                    var relative = GraphPose.Relative(submaps[s].LocalPose, nodeLocal);
                    links.Add(new GraphLink(submapVar[s], nodeVar[n], relative, NodeSigmaMeters, NodeSigmaDegrees, revisit: false));
                    submapNodes[s].Add(n);
                    rendered[s].Add(relative);
                }

                if (n > 0)
                {
                    var moved = GraphPose.Relative(nodeOdometry[n - 1], odo);
                    double distance = System.Math.Sqrt(moved.x * moved.x + moved.y * moved.y);
                    links.Add(new GraphLink(nodeVar[n - 1], nodeVar[n], moved,
                        OdometrySigmaMeters + OdometrySigmaPerMeter * distance,
                        OdometrySigmaDegrees + OdometrySigmaPerDegree * System.Math.Abs(moved.heading), revisit: true));
                }
            }
        }

        void SearchForNode(SensorFrame frame, int n)
        {
            CollectPoints(frame);
            if (points.Count < S.MinHits) return;
            var node = local.Nodes[n];
            var scanGlobal = poses[nodeVar[n]];

            candidates.Clear();
            var submaps = local.Submaps;
            for (int i = 0; i < submaps.Count && i < globals.Count; i++)
            {
                if (!submaps[i].Finished || i == node.submapA || i == node.submapB) continue;
                if (DistanceSq(i, scanGlobal) <= G.MaxSearchDistanceMeters * G.MaxSearchDistanceMeters) candidates.Add(i);
            }
            candidates.Sort((a, b) =>
            {
                int byDistance = DistanceSq(a, scanGlobal).CompareTo(DistanceSq(b, scanGlobal));
                return byDistance != 0 ? byDistance : a.CompareTo(b);
            });

            int tries = Mathf.Min(G.MaxSubmapsPerSearch, candidates.Count);
            for (int t = 0; t < tries; t++)
            {
                int i = candidates[t];
                Searches++;
                var guess = GraphPose.Compose(submaps[i].LocalPose, GraphPose.Relative(globals[i], scanGlobal)).ToRobotPose();
                bool accepted = matcher.Match(submaps[i].grid, points, guess, out RobotPose found);
                if (LogSearches) SearchLog.Add((i, node.submapA, guess, found, accepted, matcher.LastScore));
                if (!accepted) continue;

                links.Add(new GraphLink(submapVar[i], nodeVar[n], GraphPose.Relative(submaps[i].LocalPose, GraphPose.From(found)),
                    G.LoopSigmaMeters * LoopSigmaScale, G.LoopSigmaDegrees * LoopSigmaScale, revisit: true));
                LoopLinks++;
            }
        }

        bool OptimiseNodes()
        {
            Optimisations++;
            var before = new List<GraphPose>(globals);
            graph.Optimise(poses, links);
            for (int s = 0; s < submapVar.Count; s++) globals[s] = poses[submapVar[s]];

            double moved = 0, turned = 0;
            for (int i = 0; i < globals.Count; i++)
            {
                double dx = globals[i].x - before[i].x, dy = globals[i].y - before[i].y;
                moved = System.Math.Max(moved, System.Math.Sqrt(dx * dx + dy * dy));
                turned = System.Math.Max(turned, System.Math.Abs(GraphPose.WrapDegrees(globals[i].heading - before[i].heading)));
            }

            // Rebuild each finished submap whose scans moved relative to it since it was last drawn.
            bool rebuilt = false;
            var submaps = local.Submaps;
            for (int s = 0; s < submaps.Count; s++)
            {
                if (!submaps[s].Finished) continue;
                var members = submapNodes[s];
                bool due = false;
                for (int k = 0; k < members.Count && !due; k++)
                {
                    var now = GraphPose.Relative(poses[submapVar[s]], poses[nodeVar[members[k]]]);
                    var was = rendered[s][k];
                    double dx = now.x - was.x, dy = now.y - was.y;
                    due = System.Math.Sqrt(dx * dx + dy * dy) > G.RedrawMoveMeters ||
                          System.Math.Abs(GraphPose.WrapDegrees(now.heading - was.heading)) > G.RedrawTurnDegrees;
                }
                if (!due) continue;

                rebuildScratch.Clear();
                for (int k = 0; k < members.Count; k++)
                {
                    var now = GraphPose.Relative(poses[submapVar[s]], poses[nodeVar[members[k]]]);
                    rendered[s][k] = now;
                    rebuildScratch.Add((local.Nodes[members[k]], GraphPose.Compose(submaps[s].LocalPose, now).ToRobotPose()));
                }
                local.Rebuild(submaps[s], rebuildScratch);
                matcher.Forget(submaps[s].grid);
                Rebuilds++;
                rebuilt = true;
            }
            return rebuilt || moved > G.RedrawMoveMeters || turned > G.RedrawTurnDegrees;
        }

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
                    G.ConsecutiveSigmaMeters, ConsecutiveSigmaDegreesOverride > 0f ? ConsecutiveSigmaDegreesOverride : G.ConsecutiveSigmaDegrees, revisit: false));
            }
        }

        void Search(SensorFrame frame, RobotPose scanLocal, int home)
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
            for (int t = 0; t < tries; t++)
            {
                int i = candidates[t];
                Searches++;

                // The guess, in submap i's grid (local SLAM's frame at the time it was built).
                var guess = GraphPose.Compose(submaps[i].LocalPose, GraphPose.Relative(globals[i], scanGlobal)).ToRobotPose();
                bool accepted = matcher.Match(submaps[i].grid, points, guess, out RobotPose found);
                if (LogSearches) SearchLog.Add((i, home, guess, found, accepted, matcher.LastScore));
                if (!accepted) continue;

                // Link submap i to the scan's own submap through the scan: (i → scan) ∘ (home → scan)⁻¹.
                var iToScan = GraphPose.Relative(submaps[i].LocalPose, GraphPose.From(found));
                var measured = GraphPose.Compose(iToScan, Inverse(scanInHome));
                links.Add(new GraphLink(i, home, measured, G.LoopSigmaMeters * LoopSigmaScale, G.LoopSigmaDegrees * LoopSigmaScale, revisit: true));
                LoopLinks++;
                newLinks = true;
            }
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

        bool Optimise()
        {
            Optimisations++;
            var before = new List<GraphPose>(globals);
            graph.Optimise(globals, links);

            double moved = 0, turned = 0;
            for (int i = 0; i < globals.Count; i++)
            {
                double dx = globals[i].x - before[i].x, dy = globals[i].y - before[i].y;
                moved = System.Math.Max(moved, System.Math.Sqrt(dx * dx + dy * dy));
                turned = System.Math.Max(turned, System.Math.Abs(GraphPose.WrapDegrees(globals[i].heading - before[i].heading)));
            }
            return moved > G.RedrawMoveMeters || turned > G.RedrawTurnDegrees;
        }
    }
}
