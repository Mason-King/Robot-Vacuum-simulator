namespace RobotVacuum.AlgorithmApi
{
    /// <summary>What an algorithm is told once, when a run starts (or restarts).</summary>
    public readonly struct AlgorithmContext
    {
        /// <summary>The run's random stream. Draw from this only, so runs stay reproducible.</summary>
        public readonly IRandom random;

        /// <summary>The robot body's radius, metres.</summary>
        public readonly float robotRadius;

        /// <summary>Width of floor cleaned in one pass, metres.</summary>
        public readonly float cleaningWidth;

        /// <summary>The map's cell size, metres.</summary>
        public readonly float cellSizeMeters;

        /// <summary>True for a SLAM run: the map starts empty, fills in from sensing, and may be wrong.</summary>
        public readonly bool slam;

        /// <summary>The lidar's maximum range, metres; 0 when the lidar is off.</summary>
        public readonly float lidarRangeMeters;

        public AlgorithmContext(IRandom random, float robotRadius, float cleaningWidth, float cellSizeMeters,
            bool slam, float lidarRangeMeters)
        {
            this.random = random;
            this.robotRadius = robotRadius;
            this.cleaningWidth = cleaningWidth;
            this.cellSizeMeters = cellSizeMeters;
            this.slam = slam;
            this.lidarRangeMeters = lidarRangeMeters;
        }
    }
}
