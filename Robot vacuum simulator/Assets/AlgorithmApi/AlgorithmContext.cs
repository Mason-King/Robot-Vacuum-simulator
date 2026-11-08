using System;

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

        /// <summary>The cleanliness the run aims for in every cell (the run's target max clean). Compare with the map's Cleanliness.</summary>
        public readonly float targetCleanliness;

        /// <summary>The vacuum's power: cleanliness per second under the brush on floor of resistance 1.</summary>
        public readonly float vacuumPower;

        /// <summary>Requests to the controller: speed mode, pass-through goals, and "finished". Never null.</summary>
        public readonly DriveRequests drive;

        /// <summary>
        /// The controller's analysis of the map: where the body fits, reach over known floor, rooms and doorways. Shared
        /// by every algorithm; call its Update when deciding. Null only in a context built without one.
        /// </summary>
        public readonly IMapAnalysis analysis;

        /// <summary>Where <see cref="Log"/> goes (the Console, when the run logs algorithm decisions). May be null.</summary>
        public readonly Action<string> log;

        /// <summary>The brush's radius (the brush covers the robot's footprint).</summary>
        public float BrushRadius => cleaningWidth * 0.5f;

        /// <summary>Writes a line to the run's log (the Console) when the run logs algorithm decisions; otherwise nothing.</summary>
        public void Log(string message) => log?.Invoke(message);

        public AlgorithmContext(IRandom random, float robotRadius, float cleaningWidth, float cellSizeMeters,
            bool slam, float lidarRangeMeters)
            : this(random, robotRadius, cleaningWidth, cellSizeMeters, slam, lidarRangeMeters, 2f, 1f, new DriveRequests())
        {
        }

        public AlgorithmContext(IRandom random, float robotRadius, float cleaningWidth, float cellSizeMeters,
            bool slam, float lidarRangeMeters, float targetCleanliness, float vacuumPower, DriveRequests drive,
            IMapAnalysis analysis = null, Action<string> log = null)
        {
            this.analysis = analysis;
            this.log = log;
            this.random = random;
            this.robotRadius = robotRadius;
            this.cleaningWidth = cleaningWidth;
            this.cellSizeMeters = cellSizeMeters;
            this.slam = slam;
            this.lidarRangeMeters = lidarRangeMeters;
            this.targetCleanliness = targetCleanliness;
            this.vacuumPower = vacuumPower;
            this.drive = drive ?? new DriveRequests();
        }
    }
}
