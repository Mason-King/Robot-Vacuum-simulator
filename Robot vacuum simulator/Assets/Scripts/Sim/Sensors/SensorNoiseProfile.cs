namespace RobotVacuum.Sim.Sensors
{
    /// <summary>
    /// EVERY SENSOR ERROR SIZE LIVES IN THIS ONE FILE. These are the errors of a realistic budget robot at a
    /// noise scale of 1. The run's single user-facing noise scale multiplies all of them, so 0 = perfect
    /// readings, 1 = this profile, 2 = twice as bad.
    ///
    /// Noise is applied ONLY when the run has SLAM on. With SLAM off, sensors use exact readings and never
    /// read the noise scale. (SLAM on with a noise scale of 0 still runs the noisy code path, with zero error.)
    ///
    /// Starting values, to be tuned by testing.
    /// </summary>
    public static class SensorNoiseProfile
    {
        // ---- Lidar ----------------------------------------------------------------------------------------

        /// <summary>Range error that doesn't depend on distance: standard deviation, metres.</summary>
        public const float LidarRangeSigmaMeters = 0.01f;

        /// <summary>Range error proportional to distance: standard deviation as a fraction of the range (0.01 = 1%).</summary>
        public const float LidarRangeSigmaFraction = 0.01f;

        /// <summary>
        /// Beam direction error: standard deviation, degrees. The sideways error this causes grows with
        /// distance (about 0.9 cm at 1 m, about 7 cm at 8 m at 0.5°).
        /// </summary>
        public const float LidarBearingSigmaDegrees = 0.5f;

        /// <summary>Chance a ray returns nothing at 1 m or closer (0.01 = 1%).</summary>
        public const float LidarMissChanceAtOneMeter = 0.01f;

        /// <summary>
        /// Extra miss chance per metre beyond 1 m, by ABSOLUTE distance (not relative to the lidar's maximum
        /// range), so a longer-range lidar misses more at its far edge than a shorter one does at its own.
        /// 0.0114 gives about 9% at 8 m and about 16% at 15 m.
        /// </summary>
        public const float LidarMissChancePerMeter = 0.0114f;

        /// <summary>The miss chance never exceeds this, whatever the distance or noise scale.</summary>
        public const float LidarMissChanceCap = 0.5f;

        // ---- Whisker --------------------------------------------------------------------------------------

        /// <summary>Error in the felt floor drag (ease): standard deviation as a fraction (0.05 = 5%). Bumps are never noisy.</summary>
        public const float WhiskerDragSigmaFraction = 0.05f;

        // ---- Odometry and the vacuum resistance sensor (slice 2) -------------------------------------------

        /// <summary>
        /// Distance error: standard deviation as a fraction of the distance driven, MEASURED OVER
        /// <see cref="OdometryDistanceReferenceMeters"/>. The error is a random walk (a little per physics step), so
        /// it grows with the square root of the distance: 1.5 cm after 1 m, about 4.7 cm after 10 m, about 15 cm
        /// after 100 m. (A per-step error that was simply 1.5% of each tiny step would average away to almost
        /// nothing over a run.)
        /// </summary>
        public const float OdometryDistanceSigmaFraction = 0.015f;

        /// <summary>The distance over which <see cref="OdometryDistanceSigmaFraction"/> holds exactly.</summary>
        public const float OdometryDistanceReferenceMeters = 1f;

        /// <summary>
        /// Heading error: standard deviation as a fraction of the angle turned, measured over
        /// <see cref="OdometryHeadingReferenceDegrees"/> (one full turn). 0.003 ≈ 1.1° per full turn: a robot with a
        /// gyro, as real robot vacuums have (Eric's decision, 2026-11-02; it was 0.015 ≈ 5.4° per turn, wheels alone).
        /// </summary>
        public const float OdometryHeadingSigmaFraction = 0.003f;

        /// <summary>The turn over which <see cref="OdometryHeadingSigmaFraction"/> holds exactly.</summary>
        public const float OdometryHeadingReferenceDegrees = 360f;

        /// <summary>Carpet slip: the distance error is multiplied by 1 / the floor's ease, but never by more than this.</summary>
        public const float OdometryMaxSlipFactor = 10f;

        /// <summary>Vacuum resistance reading error: standard deviation as a fraction.</summary>
        public const float FloorSensorSigmaFraction = 0.05f;
    }
}
