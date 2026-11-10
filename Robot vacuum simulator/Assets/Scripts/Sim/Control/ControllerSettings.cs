namespace RobotVacuum.Sim.Control
{
    /// <summary>
    /// The run settings the controller needs, copied once at the start of a run. Built from resolved
    /// <see cref="RunSettings"/> for a real run, or <see cref="Default"/> for a robot started any other way
    /// (the Level Editor's quick run, a robot dropped into a scene).
    /// </summary>
    public struct ControllerSettings
    {
        /// <summary>The single mode switch. Off = Normal (preloaded map, exact readings); on = SLAM.</summary>
        public bool slam;

        /// <summary>The sensor noise scale. Meaningful ONLY when <see cref="slam"/> is on; a Normal run carries a constant 0 and never reads the user's value.</summary>
        public float noiseScale;

        public bool lidarEnabled;
        public float lidarRangeMeters;
        public float lidarHalfAngleDegrees;
        public int lidarRayCount;

        /// <summary>The vacuum resistance (floor) sensor on/off.</summary>
        public bool floorSensorEnabled;

        /// <summary>SLAM pose correction preset (CPU vs reliability). Not used in a Normal run.</summary>
        public PoseCorrectionQuality correctionQuality;

        /// <summary>SLAM loop closure on/off (on by default). Not used in a Normal run.</summary>
        public bool loopClosure;

        /// <summary>Debug: algorithm decisions, planner warnings and the finishing report go to the Console.</summary>
        public bool logAlgorithmDecisions;

        /// <summary>The noise scale a Normal run carries: a constant, never the user's setting.</summary>
        public const float NormalNoiseScale = 0f;

        public static ControllerSettings Default => new ControllerSettings
        {
            slam = false,
            noiseScale = NormalNoiseScale,
            lidarEnabled = true,
            lidarRangeMeters = Sensors.LidarSensor.DefaultRangeMeters,
            lidarHalfAngleDegrees = 180f,
            lidarRayCount = Sensors.LidarRayLayout.MaxRays,
            floorSensorEnabled = true,
            correctionQuality = PoseCorrectionQuality.Balanced,
            loopClosure = true,
        };

        /// <summary>From already-resolved settings. The noise scale is read only when SLAM is on.</summary>
        public static ControllerSettings From(RunSettings resolved)
        {
            if (resolved == null) return Default;

            return new ControllerSettings
            {
                slam = resolved.slam,
                noiseScale = resolved.slam ? resolved.noiseScale : NormalNoiseScale,
                lidarEnabled = resolved.lidarEnabled,
                lidarRangeMeters = resolved.lidarRangeMeters,
                lidarHalfAngleDegrees = resolved.lidarHalfAngleDegrees,
                lidarRayCount = resolved.lidarRayCount,
                floorSensorEnabled = resolved.floorSensorEnabled,
                correctionQuality = resolved.slamCorrectionQuality,
                loopClosure = resolved.slamLoopClosure,
                logAlgorithmDecisions = resolved.logAlgorithmDecisions,
            };
        }
    }
}
