namespace RobotVacuum.Sim.Control
{
    /// <summary>
    /// The SLAM belief-update rule's constants (design doc, section 6.2). Used ONLY when SLAM is on; a Normal run
    /// assigns observations directly and never reads these. Starting values, to be tuned by testing.
    /// </summary>
    public static class BeliefUpdateConstants
    {
        /// <summary>Base step toward the observation for a lidar hit (obstacles are believed faster).</summary>
        public const float HitAlpha = 0.5f;

        /// <summary>Base step toward the observation for a clear view.</summary>
        public const float ClearAlpha = 0.25f;

        /// <summary>Base step for the floor-ease reading felt through the drive (the cell under the robot, distance 0).</summary>
        public const float DragAlpha = 0.25f;

        /// <summary>Base step for the vacuum resistance (floor) sensor's reading of the cell under the robot.</summary>
        public const float FloorSensorAlpha = 0.25f;

        /// <summary>Readings at or inside this range get full weight ...</summary>
        public const float FullWeightRangeMeters = 1f;

        /// <summary>... falling linearly to this weight at the lidar's maximum range.</summary>
        public const float WeightAtMaxRange = 0.25f;

        /// <summary>An observation further than this from the belief is a drastic contradiction (a moved cat, moved furniture) ...</summary>
        public const float ContradictionThreshold = 0.5f;

        /// <summary>... and its step is multiplied by this ...</summary>
        public const float ContradictionBoost = 3f;

        /// <summary>... but never above this.</summary>
        public const float MaxAlpha = 0.9f;

        /// <summary>A traversability belief below this snaps to exactly 0 (impassable).</summary>
        public const float SnapToZeroBelow = 0.1f;
    }
}
