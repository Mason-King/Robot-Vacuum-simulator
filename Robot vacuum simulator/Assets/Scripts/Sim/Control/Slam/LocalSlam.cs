using System.Collections.Generic;
using RobotVacuum.Sim.Sensors;
using UnityEngine;
using S = RobotVacuum.Sim.Control.LocalSlamConstants;

namespace RobotVacuum.Sim.Control
{
    /// <summary>
    /// The one user-facing knob for SLAM tracking (Advanced settings): how many of the lidar's returns each scan match
    /// uses. Balanced is 0 so an unset value means Balanced.
    /// </summary>
    public enum PoseCorrectionQuality
    {
        /// <summary>Every 3rd return. Least CPU.</summary>
        Performance = 1,

        /// <summary>Every 2nd return. The default.</summary>
        Balanced = 0,

        /// <summary>Every return. Most CPU.</summary>
        Reliable = 2,
    }

    /// <summary>The numbers a preset stands for.</summary>
    public readonly struct PoseCorrectionProfile
    {
        public readonly int rayStride;

        public PoseCorrectionProfile(int rayStride) => this.rayStride = rayStride;

        public static PoseCorrectionProfile For(PoseCorrectionQuality quality) => quality switch
        {
            PoseCorrectionQuality.Performance => new PoseCorrectionProfile(3),
            PoseCorrectionQuality.Reliable => new PoseCorrectionProfile(1),
            _ => new PoseCorrectionProfile(2),
        };
    }

    /// <summary>A submap: a probability grid built from a run of consecutive scans, and where it started.</summary>
    public sealed class Submap
    {
        public readonly int index;
        public readonly ProbabilityGrid grid;

        /// <summary>Where local SLAM was when the submap was started (its frame's origin; not rotated).</summary>
        public readonly Vector2 origin;

        /// <summary>True once it has all its scans: it never changes again (loop closure matches against finished submaps).</summary>
        public bool Finished { get; internal set; }

        /// <summary>Its frame's pose in local SLAM's frame: at <see cref="origin"/>, not rotated.</summary>
        public GraphPose LocalPose => new GraphPose(origin.x, origin.y, 0);

        /// <summary>Where its scans were taken, on average (local SLAM's frame).</summary>
        public Vector2 Centre => insertCount > 0 ? new Vector2((float)(sumX / insertCount), (float)(sumY / insertCount)) : origin;

        double sumX, sumY;
        int insertCount;

        internal void ResetInsertPositions()
        {
            sumX = sumY = 0;
            insertCount = 0;
        }

        internal void AddInsertPosition(Vector2 p)
        {
            sumX += p.x;
            sumY += p.y;
            insertCount++;
        }

        public Submap(int index, ProbabilityGrid grid, Vector2 origin)
        {
            this.index = index;
            this.grid = grid;
            this.origin = origin;
        }
    }

    /// <summary>
    /// One scan local SLAM added to the submaps, kept so the submaps can be rebuilt from their scans at corrected poses
    /// (design: SLAM_Option2_Design.md). <see cref="pose"/> is where it was added, in local SLAM's frame.
    /// </summary>
    public sealed class ScanNode
    {
        public readonly int index;
        public readonly RobotPose pose;
        public readonly float time;
        public readonly LidarRay[] rays;
        public readonly float lidarRange;

        /// <summary>The submaps it went into (the second is −1 if only one was active).</summary>
        public readonly int submapA, submapB;

        public ScanNode(int index, RobotPose pose, float time, LidarRay[] rays, float lidarRange, int submapA, int submapB)
        {
            this.index = index;
            this.pose = pose;
            this.time = time;
            this.rays = rays;
            this.lidarRange = lidarRange;
            this.submapA = submapA;
            this.submapB = submapB;
        }
    }

    /// <summary>
    /// SLAM ONLY: local SLAM (design doc, section 19; Cartographer's LocalTrajectoryBuilder2D and ActiveSubmaps2D).
    /// Each step with a lidar scan:
    ///
    ///   1. MATCH: the scan is fitted (<see cref="ScanMatcher"/>) to the older of the two active submaps, starting
    ///      from the odometry guess. The result is the believed pose.
    ///   2. MOTION FILTER: the scan is added to the submaps only once the robot has moved 0.2 m, turned 1°, or 5 s
    ///      have passed since the last scan added.
    ///   3. INSERT: into both active submaps. When the newer submap has ScansPerSubmap (360) scans, the older one (by then twice that) is
    ///      finished and a new one is started where the robot is. So every scan lands in two overlapping submaps,
    ///      and matching always uses a submap that is already well built.
    ///
    /// The Internal Model is drawn from the submaps: each cell a scan changes takes its probability from the older
    /// active submap (or the newer, if the older hasn't seen it). Plain C#, no Unity objects.
    /// </summary>
    public sealed class LocalSlam
    {
        readonly Vector2 anchor;
        readonly float cellSize;
        readonly ScanMatcher matcher = new ScanMatcher();
        readonly List<Submap> submaps = new List<Submap>();
        readonly List<Submap> active = new List<Submap>(2);
        readonly List<Vector2> points = new List<Vector2>(360);
        readonly List<long> changed = new List<long>(4096);
        readonly HashSet<long> changedSet = new HashSet<long>();
        readonly List<ScanNode> nodes = new List<ScanNode>();

        /// <summary>Every scan added so far, in order (kept only when <see cref="KeepNodes"/> is on).</summary>
        public IReadOnlyList<ScanNode> Nodes => nodes;

        /// <summary>Keep each added scan as a <see cref="ScanNode"/> (the node-level graph needs them).</summary>
        public bool KeepNodes { get; set; }

        readonly float freeSpaceMargin;
        RobotPose lastInserted;
        float lastInsertedTime;
        bool anyInserted;

        /// <param name="noiseScale">The run's sensor noise setting: the free-space margin scales with it (0 = exact lidar, no margin).</param>
        public LocalSlam(Vector2 anchorWorld, float cellSizeMeters, PoseCorrectionQuality quality, float noiseScale)
        {
            anchor = anchorWorld;
            cellSize = cellSizeMeters;
            freeSpaceMargin = S.FreeSpaceMarginSigmas * Mathf.Max(0f, noiseScale);
            Profile = PoseCorrectionProfile.For(quality);
        }

        public PoseCorrectionProfile Profile { get; }

        public ScanMatcher Matcher => matcher;

        /// <summary>Every submap so far, oldest first.</summary>
        public IReadOnlyList<Submap> Submaps => submaps;

        /// <summary>The cells (packed row/col) whose probability changed this step, for the Internal Model.</summary>
        public IReadOnlyList<long> ChangedCells => changed;

        /// <summary>The active submaps: [0] is the older one, which scans are matched against.</summary>
        public IReadOnlyList<Submap> ActiveSubmaps => active;

        /// <summary>True if this step's scan was added to the submaps; then <see cref="InsertedPose"/> is where (local frame) and <see cref="InsertedHome"/> the submap it was matched against.</summary>
        public bool InsertedThisStep { get; private set; }
        public RobotPose InsertedPose { get; private set; }
        public int InsertedHome { get; private set; }

        // ---- Counts (for result.json) ----
        public int Matches { get; private set; }
        public int TooFewHits { get; private set; }
        public int ScansInserted { get; private set; }

        /// <summary>Starts a run at the dock: the one pose known exactly.</summary>
        public void Begin(RobotPose start)
        {
            submaps.Clear();
            active.Clear();
            changed.Clear();
            nodes.Clear();
            anyInserted = false;
            lastInserted = start;
            lastInsertedTime = 0f;
            Matches = TooFewHits = ScansInserted = 0;
        }

        /// <summary>
        /// One step: if this step has a lidar scan, fits it to the map (starting from <paramref name="predicted"/>,
        /// the odometry guess) and adds it to the submaps if the robot has moved enough. Returns the believed pose.
        /// </summary>
        public RobotPose Track(SensorFrame frame, RobotPose predicted, float simTime) => Track(frame, predicted, simTime, null, null);

        /// <summary>
        /// Diagnostics (tests): as <see cref="Track"/>, but the scan added to the map is <paramref name="insertFrame"/>
        /// at <paramref name="insertAt"/> (e.g. the true pose, or an exact scan) instead of this scan at its fitted pose.
        /// </summary>
        internal RobotPose Track(SensorFrame frame, RobotPose predicted, float simTime, RobotPose? insertAt, SensorFrame insertFrame)
        {
            changed.Clear();
            changedSet.Clear();
            InsertedThisStep = false;
            if (!frame.hasLidarScan) return predicted;

            RobotPose pose = predicted;
            if (active.Count > 0)
            {
                CollectPoints(frame);
                if (points.Count >= S.MinHits)
                {
                    matcher.Weights = PointWeightPower > 0 ? weights : null;
                    pose = matcher.Match(active[0].grid, points, predicted, TranslationWeightOverride > 0f ? TranslationWeightOverride : S.TranslationWeight,
                        RotationWeightOverride > 0f ? RotationWeightOverride : S.RotationWeight);
                    Matches++;
                }
                else
                {
                    TooFewHits++;
                }
            }

            RobotPose mapPose = insertAt ?? pose;
            if (!anyInserted || MovedEnough(mapPose, simTime))
            {
                int home = active.Count > 0 ? active[0].index : 0;
                Insert(insertFrame ?? frame, mapPose, simTime);
                InsertedThisStep = true;
                InsertedPose = mapPose;
                InsertedHome = home;
            }
            return pose;
        }

        /// <summary>
        /// SLAM start, standing still at a known pose (the dock): the scan is added at <paramref name="knownPose"/>
        /// directly, with no fit and no motion filter, so the first submap is built from many scans taken at an exact
        /// pose. Returns <paramref name="knownPose"/>.
        /// </summary>
        public RobotPose TrackStationary(SensorFrame frame, RobotPose knownPose, float simTime)
        {
            changed.Clear();
            changedSet.Clear();
            InsertedThisStep = false;
            if (!frame.hasLidarScan) return knownPose;

            int home = active.Count > 0 ? active[0].index : 0;
            Insert(frame, knownPose, simTime);
            InsertedThisStep = true;
            InsertedPose = knownPose;
            InsertedHome = home;
            return knownPose;
        }

        /// <summary>A certain obstacle (a bump) at <paramref name="contactWorld"/>: entered into the active submaps.</summary>
        public void MarkCertain(Vector2 contactWorld)
        {
            foreach (var submap in active) submap.grid.SetCertain(contactWorld, changed);
        }

        /// <summary>The probability the Internal Model shows for a cell: the older active submap's, else the newer's, else 0 (unknown).</summary>
        public float Probability(int row, int col)
        {
            for (int i = 0; i < active.Count; i++)
            {
                float p = active[i].grid.Probability(row, col);
                if (p > 0f) return p;
            }
            return 0f;
        }

        /// <summary>The traversability an algorithm sees for a probability: wall = 0, unknown or free = the floor's ease, in between = shaded.</summary>
        public static float Traversability(float probability, float ease)
        {
            float occupied = OccupiedOverride > 0f ? OccupiedOverride : S.OccupiedProbability;
            if (probability <= 0f || probability <= S.FreeProbability) return ease;
            if (probability >= occupied) return 0f;
            return ease * (occupied - probability) / (occupied - S.FreeProbability);
        }

        // ---- Diagnostics only (tests): wall-thickness experiments. Defaults = the normal rule. ----
        internal static float MarginSigmasOverride = -1f;  // ≥ 0: use this free-space margin (σ) instead
        internal static float MarginFromRangeMeters = 0f;  // the margin applies only to returns further than this
        internal static float OccupiedOverride = 0f;       // > 0: wall threshold for the Internal Model instead
        internal static float RotationWeightOverride = 0f; // > 0: the scan matcher's rotation weight instead
        internal static float TranslationWeightOverride = 0f; // > 0: the scan matcher's translation weight (trust in odometry's step) instead
        internal static float InsertRangeOverride = 0f;       // > 0: returns within this range are written into the map instead
        static float InsertRange => InsertRangeOverride > 0f ? InsertRangeOverride : S.MaxInsertRangeMeters;
        internal static float SubmapCellOverride = 0f;
        internal static float TrackRangeOverride = 0f;     // > 0: tracking uses only returns within this range (m)
        internal static int PointWeightPower = 0;          // 1 or 2: tracking weights each point by (σ(1 m) / σ(range))^power
        internal static int ScansPerSubmapOverride = 0;    // > 0: scans per submap instead     // > 0: submap cell size instead (the Internal Model can't be drawn then)

        // ---- Internals ---------------------------------------------------------------------------------------

        void CollectPoints(SensorFrame frame)
        {
            points.Clear();
            weights.Clear();
            var rays = frame.lidarRays;
            int stride = Mathf.Max(1, Profile.rayStride), seen = 0;
            for (int i = 0; i < rays.Count; i++)
            {
                if (!rays[i].hit) continue;
                if (TrackRangeOverride > 0f && rays[i].range > TrackRangeOverride) continue;
                if (seen++ % stride != 0) continue;
                points.Add(RobotPose.DirectionOf(rays[i].bearing) * rays[i].range);
                if (PointWeightPower > 0)
                {
                    const float sigmaAtOneMeter = Sensors.SensorNoiseProfile.LidarRangeSigmaMeters + Sensors.SensorNoiseProfile.LidarRangeSigmaFraction;
                    float sigma = Sensors.SensorNoiseProfile.LidarRangeSigmaMeters + Sensors.SensorNoiseProfile.LidarRangeSigmaFraction * rays[i].range;
                    weights.Add(Mathf.Pow(sigmaAtOneMeter / sigma, PointWeightPower));
                }
            }

            // Scale the weights so their mean square is 1: the fit's total weight stays as without weights.
            if (weights.Count > 0)
            {
                double meanSquare = 0;
                foreach (float w in weights) meanSquare += w * w;
                float k = (float)(1.0 / System.Math.Sqrt(meanSquare / weights.Count));
                for (int i = 0; i < weights.Count; i++) weights[i] *= k;
            }
        }

        readonly List<float> weights = new List<float>(360);

        bool MovedEnough(RobotPose pose, float simTime) =>
            Vector2.Distance(pose.position, lastInserted.position) > S.MotionFilterMeters ||
            Mathf.Abs(Mathf.DeltaAngle(pose.heading, lastInserted.heading)) > S.MotionFilterDegrees ||
            simTime - lastInsertedTime > S.MotionFilterSeconds;

        void Insert(SensorFrame frame, RobotPose pose, float simTime)
        {
            if (active.Count == 0) AddSubmap(pose.position);

            float noReturn = Mathf.Min(S.MissingDataRayLengthMeters, frame.lidarRange);
            float margin = MarginSigmasOverride >= 0f ? MarginSigmasOverride : freeSpaceMargin;
            foreach (var submap in active)
            {
                submap.grid.Insert(pose, frame.lidarRays, noReturn, InsertRange, margin, MarginFromRangeMeters, changed);
                submap.AddInsertPosition(pose.position);
            }
            if (KeepNodes)
                nodes.Add(new ScanNode(nodes.Count, pose, simTime, frame.lidarRays.ToArray(), frame.lidarRange,
                    active[0].index, active.Count > 1 ? active[1].index : -1));

            anyInserted = true;
            lastInserted = pose;
            lastInsertedTime = simTime;
            ScansInserted++;

            // The newer submap is full: finish the older one and start a new one here.
            if (active[active.Count - 1].grid.ScanCount >= (ScansPerSubmapOverride > 0 ? ScansPerSubmapOverride : S.ScansPerSubmap)) AddSubmap(pose.position);

            // Each changed cell once.
            int write = 0;
            for (int i = 0; i < changed.Count; i++)
                if (changedSet.Add(changed[i])) changed[write++] = changed[i];
            changed.RemoveRange(write, changed.Count - write);
        }

        /// <summary>
        /// Rebuilds a FINISHED submap from scans at the given poses (in local SLAM's frame, i.e. the submap's grid frame),
        /// inserted exactly as they were the first time. Active submaps are never rebuilt (tracking uses them).
        /// </summary>
        public void Rebuild(Submap submap, List<(ScanNode node, RobotPose pose)> scans)
        {
            if (!submap.Finished) return;
            float margin = MarginSigmasOverride >= 0f ? MarginSigmasOverride : freeSpaceMargin;
            submap.grid.Clear();
            submap.ResetInsertPositions();
            foreach (var (node, pose) in scans)
            {
                float noReturn = Mathf.Min(S.MissingDataRayLengthMeters, node.lidarRange);
                submap.grid.Insert(pose, node.rays, noReturn, InsertRange, margin, MarginFromRangeMeters, null);
                submap.AddInsertPosition(pose.position);
            }
        }

        void AddSubmap(Vector2 origin)
        {
            if (active.Count >= 2)
            {
                active[0].Finished = true;
                active.RemoveAt(0);
            }
            var submap = new Submap(submaps.Count, new ProbabilityGrid(anchor, SubmapCellOverride > 0f ? SubmapCellOverride : cellSize), origin);
            submaps.Add(submap);
            active.Add(submap);
        }
    }
}
