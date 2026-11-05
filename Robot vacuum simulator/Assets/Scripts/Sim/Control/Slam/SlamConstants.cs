namespace RobotVacuum.Sim.Control
{
    /// <summary>
    /// SLAM local mapping and tracking (design doc, section 19), following Google Cartographer's 2D local SLAM.
    /// Where Cartographer has a default, it is used and named here. SLAM runs only; a Normal run never reads these.
    /// </summary>
    public static class LocalSlamConstants
    {
        // ---- Submaps (probability grids) ----

        /// <summary>A cell first seen as a hit starts at this probability of being occupied; later hits multiply its odds by this value's odds (Cartographer hit_probability).</summary>
        public const float HitProbability = 0.55f;

        /// <summary>The same for a ray passing through the cell (Cartographer miss_probability).</summary>
        public const float MissProbability = 0.49f;

        /// <summary>Probabilities are kept within these limits, so nothing becomes impossible to change (Cartographer's kMinProbability / kMaxProbability).</summary>
        public const float MinProbability = 0.1f;
        public const float MaxProbability = 0.9f;

        /// <summary>
        /// Scans per submap half (Cartographer submaps.num_range_data; its default is 90): a submap takes twice this many,
        /// the first half while it is being started. 360 here: tracking against young submaps let each submap generation
        /// carry its own offset (Experiment_SubmapLifetime / Confirm, 8 seeds × 300 s, loop closure on: p99 8.0 → 6.7 cm
        /// in the test apartment, 6.6 → 6.3 in the angled one).
        /// </summary>
        public const int ScansPerSubmap = 360;

        /// <summary>A ray with no return is entered as free space this far (Cartographer missing_data_ray_length), never past the lidar's range or <see cref="MaxInsertRangeMeters"/>.</summary>
        public const float MissingDataRayLengthMeters = 5f;

        /// <summary>
        /// Only this much of each ray goes into the submaps: returns further away aren't mapped, and free space stops
        /// here (Cartographer's range filter, set for this lidar). 3 m originally (slice 1: far, noisy returns smeared
        /// walls). 5 m since 2026-11-05: with only nearby returns mapped, walls were written from up close only and the
        /// map's scale was barely observable, so the tracking/mapping loop shrank the map ~1.1% (Diagnose_Scale,
        /// Diagnose_ScaleSource). Experiment_InsertRange (8 seeds × 300 s, live settings), 3 → 5 m: test apartment p99
        /// 6.7 → 5.1 cm, worst 7.9 → 6.1; angled apartment p99 6.3 → 4.4, worst 7.4 → 5.5; wall thickness 1.33 → 1.45×
        /// and 1.21 → 1.23×; walls missed 258 → 121 and 354 → 292.
        /// </summary>
        public const float MaxInsertRangeMeters = 5f;

        /// <summary>Loop closure searches with returns within this range (as tested: 3 m, the insert range it was tuned with).</summary>
        public const float LoopClosurePointRangeMeters = 3f;

        /// <summary>
        /// Free space along a ray stops this many range-noise σ short of its return (σ = the lidar's range noise at
        /// that range × the run's noise setting). Otherwise rays that read long clear the near edge of a wall that
        /// rays reading short hit, drawing walls too deep. With <see cref="MaxInsertRangeMeters"/>, 2σ took worst drift
        /// over 300 s from 15.5 to 8.9 cm (6 seeds, no loop closure); 1σ with a 0.75 wall threshold was as good for
        /// drift (9.2 cm) and drew the thinnest walls (Diagnose_WallThickness, 2026-11-03).
        /// </summary>
        public const float FreeSpaceMarginSigmas = 1f;

        // ---- What the Internal Model sees ----

        /// <summary>
        /// At or above this probability a cell is a wall: traversability 0. 0.75 = six scans' hits on an unknown cell
        /// (0.65, four hits, drew walls thicker: Diagnose_WallThickness, 2026-11-03).
        /// </summary>
        public const float OccupiedProbability = 0.75f;

        /// <summary>At or below this probability (or unknown) a cell is passable at its floor ease; in between it is shaded.</summary>
        public const float FreeProbability = 0.5f;

        // ---- Motion filter (Cartographer motion_filter): a scan is added to the submaps only after this much motion ----

        public const float MotionFilterMeters = 0.2f;
        public const float MotionFilterDegrees = 1f;
        public const float MotionFilterSeconds = 5f;

        // ---- Scan matching (Cartographer ceres_scan_matcher) ----

        /// <summary>
        /// Cost weights: fit to the submap (per point, divided by √points), and how strongly the result is held to the
        /// odometry guess. Translation and rotation are Cartographer's defaults (these weights are meant to be tuned
        /// per robot). The fit weight is raised from Cartographer's default 1 to 10 for this robot, which has no IMU: at
        /// 1 the odometry guess dominates and the heading barely corrects (checked: a 0.61° guess error became 0.59°;
        /// at 10 it becomes 0.33°, and 30 or 100 are no better).
        /// </summary>
        public const float OccupiedSpaceWeight = 10f;
        public const float TranslationWeight = 10f;
        public const float RotationWeight = 40f;

        /// <summary>Solver iterations at most, stopping early once the step is smaller than this (metres / radians).</summary>
        public const int MaxIterations = 20;
        public const double ConvergedStep = 1e-7;

        /// <summary>A scan with fewer hits than this isn't matched (the odometry guess is kept).</summary>
        public const int MinHits = 10;

        // ---- Hit-field matching (fix 1; kept, switched OFF by Eric, 2026-11-05) ----
        // Experiment_HitField (8 seeds × 300 s, 360-scan submaps, loop closure on), σ 3 cm vs cell centres:
        //   matcher bias at fixed true poses: exact map 1.71 → 0.21 cm, as-built map 0.96 → 0.55 cm;
        //   live: test apartment worst 7.9 → 6.7 cm, p99 6.7 → 6.0; angled apartment worst 7.4 → 6.6, p99 6.3 → 5.7.
        //   FAILED its stop rule: the room-driven rotation it was aimed at did not move (+0.26° → +0.27°).
        //   σ 5 cm was worse on every measure.

        /// <summary>
        /// Score scan points against where hits actually landed in each cell (a likelihood field over the hits), not
        /// against probabilities interpolated between cell centres. Occupancy still decides which cells count.
        /// </summary>
        public const bool MatchAgainstHits = false;

        /// <summary>Width (σ) of each cell's hit kernel, metres.</summary>
        public const float HitFieldSigmaMeters = 0.03f;

        /// <summary>A cell's weight grows with its hit count up to this many hits.</summary>
        public const int HitFieldCountCap = 10;

        // ---- Start of a SLAM run ----

        /// <summary>A SLAM run starts with a full spin in place at this rate, mapping from the one pose known exactly (the dock).</summary>
        public const float StartSpinDegreesPerSecond = 120f;
        public const float StartSpinDegrees = 360f;
    }

    /// <summary>The pose graph solver's fixed values (design doc, section 18).</summary>
    public static class PoseGraphConstants
    {
        /// <summary>Gauss-Newton iterations at most, stopping early once no pose changes by more than this (metres / radians).</summary>
        public const int MaxIterations = 10;
        public const double ConvergedStep = 1e-6;

        /// <summary>The first pose is the dock, known exactly: held in place by this weight.</summary>
        public const double FixedPoseWeight = 1e9;
    }

    /// <summary>
    /// SLAM global mapping (loop closure; design doc, section 20), following Google Cartographer's 2D global SLAM, with
    /// submaps treated as rigid pieces: the pose graph's variables are the submaps' poses.
    /// </summary>
    public static class GlobalSlamConstants
    {
        // ---- Looking for loop closures ----

        /// <summary>Every this-many-th scan added to the submaps is matched against nearby finished submaps (Cartographer samples 0.3).</summary>
        public const int SearchEveryNodes = 3;

        /// <summary>... the nearest few finished submaps ...</summary>
        public const int MaxSubmapsPerSearch = 3;

        /// <summary>... whose scans were taken (on average) within this distance of the robot.</summary>
        public const float MaxSearchDistanceMeters = 2.5f;

        /// <summary>
        /// The search window around where the graph currently places the robot in that submap (Cartographer's default
        /// is ±7 m / ±30° for relocalising; this robot's drift stays well inside these).
        /// </summary>
        public const float SearchWindowMeters = 0.5f;
        public const float SearchWindowDegrees = 8f;

        /// <summary>Depth of the branch-and-bound search: the coarsest level looks at 2^(depth − 1) cells at a time.</summary>
        public const int BranchAndBoundDepth = 5;

        /// <summary>A match is accepted only if the points' mean occupancy probability there is at least this (Cartographer min_score).</summary>
        public const float MinScore = 0.55f;

        /// <summary>The accepted match is refined by least squares with these weights (Cartographer's constraint builder: 20, 10, 1).</summary>
        public const float RefineOccupiedWeight = 20f;
        public const float RefineTranslationWeight = 10f;
        public const float RefineRotationWeight = 1f;

        // ---- The pose graph ----

        /// <summary>How sure the link between consecutive submaps is (σ): local SLAM's drift over one submap's worth of scans.</summary>
        public const float ConsecutiveSigmaMeters = 0.02f;
        public const float ConsecutiveSigmaDegrees = 0.4f;

        /// <summary>How sure a loop-closure link is (σ).</summary>
        public const float LoopSigmaMeters = 0.01f;
        public const float LoopSigmaDegrees = 0.3f;

        /// <summary>Loop-closure links further than this many σ from agreeing with the rest are progressively discounted (Huber loss).</summary>
        public const double HuberSigmas = 2.0;

        /// <summary>The graph is re-solved after this many new scans, if a loop-closure link was added since the last solve.</summary>
        public const int OptimiseEveryNodes = 30;

        /// <summary>The Internal Model is redrawn from the submaps after a solve that moved any submap more than this (metres / degrees).</summary>
        public const float RedrawMoveMeters = 0.01f;
        public const float RedrawTurnDegrees = 0.1f;
    }
}
