namespace RobotVacuum.Sim.Control
{
    /// <summary>
    /// The SLAM map's evidence rule (design doc, section 17). SLAM runs only; a Normal run never reads these.
    /// Values come from the Python prototype that met the accuracy target; tune here, not elsewhere.
    ///
    /// Each cell keeps a score of the evidence seen for it: 0 = unknown, positive = seen blocked, negative = seen clear.
    /// </summary>
    public static class EvidenceConstants
    {
        /// <summary>Added when a lidar hit lands in the cell.</summary>
        public const float HitStep = 0.9f;

        /// <summary>Subtracted when a clear ray passes through the cell within <see cref="NearRangeMeters"/> of the robot (fast: a moved cat vanishes in about 3 scans).</summary>
        public const float ClearNear = 1.5f;

        /// <summary>Subtracted when a clear ray passes through the cell further away -- but never from a confirmed wall.</summary>
        public const float ClearFar = 0.4f;

        /// <summary>The range that counts as "near" for clearing.</summary>
        public const float NearRangeMeters = 1f;

        /// <summary>The score's limits, so nothing becomes impossible to change.</summary>
        public const float Min = -2f;
        public const float Max = 3.5f;

        /// <summary>At or above this score a cell is a confirmed wall (two hits): traversability 0, and far clear rays can't erase it.</summary>
        public const float Confirmed = 1f;
    }

    /// <summary>
    /// SLAM pose correction's fixed values (design doc, section 17). Not user settings: the user picks a preset
    /// (<see cref="PoseCorrectionQuality"/>). Values come from the Python prototype.
    /// </summary>
    public static class PoseCorrectionConstants
    {
        // ---- Scoring ----

        /// <summary>A hit point scores exp(-d² / 2σ²), d = its distance to where hits in a nearby confirmed wall cell actually landed.</summary>
        public const float ScoreSigmaMeters = 0.05f;

        /// <summary>Below this mean score per hit the map isn't built enough to match against: no correction (WeakMatch).</summary>
        public const float MinMeanScore = 0.2f;

        /// <summary>A match needs at least this share of the rays used to have hit something, and never fewer than <see cref="MinHitRaysFloor"/>.</summary>
        public const float MinHitFraction = 0.3f;
        public const int MinHitRaysFloor = 10;

        // ---- Soft limit (the odometry prior) ----

        /// <summary>
        /// Each candidate's score is reduced by this × ((distance / σpos)² + (turn / σhead)²), where σ is how far odometry
        /// could plausibly have drifted since the last correction. A strong match still wins; a marginal one far from
        /// where odometry says the robot is, doesn't.
        /// </summary>
        public const float PriorWeight = 0.01f;

        /// <summary>The smallest σ the soft limit uses, so a robot that has barely moved can still be corrected a little.</summary>
        public const float PriorFloorMeters = 0.01f;
        public const float PriorFloorDegrees = 0.3f;

        // ---- Search grids ----

        /// <summary>The fine search around the odometry guess, run first on every attempt.</summary>
        public const float FineRangeMeters = 0.02f;
        public const float FineStepMeters = 0.005f;
        public const float FineRangeDegrees = 1f;
        public const float FineStepDegrees = 0.25f;

        /// <summary>The wide search's steps (its window comes from the preset). Run only when the fine search's best is on its edge.</summary>
        public const float CoarseStepMeters = 0.03f;
        public const float CoarseStepDegrees = 1f;

        // ---- Estimate between grid steps, and partial correction ----

        /// <summary>Candidates are weighted by exp((score − best) / this) to estimate the pose between grid steps, and how sure that is.</summary>
        public const float SoftTemperature = 0.05f;

        /// <summary>
        /// A direction whose estimated spread exceeds this is not corrected. NOTE: the spread is measured over the fine
        /// grid (±2 cm, ±1°), so it can't exceed about 1.3 cm / 0.65°; at these values this check never fires. The
        /// prototype met the target with exactly these values, so they are kept as tested. In a corridor the along-axis
        /// estimate stays at the guess anyway, because every shift along it scores about the same and the weighted mean
        /// of an even spread is its centre. Revisit if partial correction is ever needed beyond that.
        /// </summary>
        public const float LooseMeters = 0.04f;
        public const float LooseDegrees = 1.5f;

        // ---- When to try ----

        /// <summary>A correction is also made on any scan after the believed heading has turned this far since the last one.</summary>
        public const float TurnTriggerDegrees = 5f;

        /// <summary>Odometry says the robot moved less than this since the last attempt: skip ...</summary>
        public const float StationaryMeters = 0.01f;
        public const float StationaryDegrees = 0.5f;

        /// <summary>... but try anyway after this long, since the wheels can't feel the robot being pushed.</summary>
        public const float StationaryRecheckSeconds = 1f;

        // ---- Start of a SLAM run ----

        /// <summary>A SLAM run starts with a full spin in place at this rate, mapping from the one pose known exactly (the dock).</summary>
        public const float StartSpinDegreesPerSecond = 120f;
        public const float StartSpinDegrees = 360f;
    }
}
