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

        // ---- Cost read-outs (the lag log; reporting only, never read by the simulation) ----

        /// <summary>Real ms in Track's scan match and in its insert into the submaps, over the run.</summary>
        public double MatchMs { get; private set; }
        public double InsertMs { get; private set; }

        /// <summary>Matcher iterations and points matched, summed over the run's matches.</summary>
        public long MatchIterations { get; private set; }
        public long MatchPoints { get; private set; }

        static double MsSince(long started) =>
            (System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

        /// <summary>Starts a run at the dock: the one pose known exactly.</summary>
        public void Begin(RobotPose start)
        {
            submaps.Clear();
            active.Clear();
            changed.Clear();
            anyInserted = false;
            lastInserted = start;
            lastInsertedTime = 0f;
            Matches = TooFewHits = ScansInserted = 0;
            MatchMs = InsertMs = 0;
            MatchIterations = MatchPoints = 0;
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
                    long matchStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                    pose = matcher.Match(active[0].grid, points, predicted, S.TranslationWeight, S.RotationWeight);
                    Matches++;
                    MatchMs += MsSince(matchStarted);
                    MatchIterations += matcher.LastIterations;
                    MatchPoints += points.Count;
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
                long insertStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                Insert(insertFrame ?? frame, mapPose, simTime);
                InsertMs += MsSince(insertStarted);
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
            const float occupied = S.OccupiedProbability;
            if (probability <= 0f || probability <= S.FreeProbability) return ease;
            if (probability >= occupied) return 0f;
            return ease * (occupied - probability) / (occupied - S.FreeProbability);
        }

        // ---- Internals ---------------------------------------------------------------------------------------

        void CollectPoints(SensorFrame frame)
        {
            points.Clear();
            var rays = frame.lidarRays;
            int stride = Mathf.Max(1, Profile.rayStride), seen = 0;
            for (int i = 0; i < rays.Count; i++)
            {
                if (!rays[i].hit) continue;
                if (seen++ % stride != 0) continue;
                points.Add(RobotPose.DirectionOf(rays[i].bearing) * rays[i].range);
            }
        }

        bool MovedEnough(RobotPose pose, float simTime) =>
            Vector2.Distance(pose.position, lastInserted.position) > S.MotionFilterMeters ||
            Mathf.Abs(Mathf.DeltaAngle(pose.heading, lastInserted.heading)) > S.MotionFilterDegrees ||
            simTime - lastInsertedTime > S.MotionFilterSeconds;

        void Insert(SensorFrame frame, RobotPose pose, float simTime)
        {
            if (active.Count == 0) AddSubmap(pose.position);

            float noReturn = Mathf.Min(S.MissingDataRayLengthMeters, frame.lidarRange);
            float margin = freeSpaceMargin;
            // The active submaps share a lattice, so the scan is traced once, into the older one, and the cells it
            // touched are replayed into the newer one: the same result as inserting into each.
            bool replay = active.Count == 2 && ReplayInserts && active[0].grid.SameLattice(active[1].grid);
            int insertedInto = active.Count;
            for (int i = 0; i < active.Count; i++)
            {
                var grid = active[i].grid;
                if (replay && i == 1) grid.Replay(scanCells, changed);
                else grid.Insert(pose, frame.lidarRays, noReturn, S.MaxInsertRangeMeters, margin, 0f, changed, replay ? scanCells : null);
                active[i].AddInsertPosition(pose.position);
            }

            anyInserted = true;
            lastInserted = pose;
            lastInsertedTime = simTime;
            ScansInserted++;

            // The newer submap is full: finish the older one and start a new one here.
            if (active[active.Count - 1].grid.ScanCount >= (ScansPerSubmapOverride > 0 ? ScansPerSubmapOverride : S.ScansPerSubmap)) AddSubmap(pose.position);

            // Each changed cell once. One grid changes a cell at most once per scan, and a replay adds only cells the
            // first grid didn't, so the list already is; two grids inserted separately can repeat cells.
            if (insertedInto > 1 && !replay)
            {
                int write = 0;
                for (int i = 0; i < changed.Count; i++)
                    if (changedSet.Add(changed[i])) changed[write++] = changed[i];
                changed.RemoveRange(write, changed.Count - write);
            }
        }

        readonly ProbabilityGrid.ScanCells scanCells = new ProbabilityGrid.ScanCells();

        /// <summary>Diagnostics (tests): false inserts each scan into each active submap separately (the old way).</summary>
        internal static bool ReplayInserts = true;

        /// <summary>Tests: scans per submap instead of the constant (> 0), so a short run crosses submap hand-overs.</summary>
        internal static int ScansPerSubmapOverride = 0;

        void AddSubmap(Vector2 origin)
        {
            if (active.Count >= 2)
            {
                active[0].Finished = true;
                active.RemoveAt(0);
            }
            var submap = new Submap(submaps.Count, new ProbabilityGrid(anchor, cellSize), origin);
            submaps.Add(submap);
            active.Add(submap);
        }
    }
}
