namespace RobotVacuum.Sim.Control
{
    /// <summary>
    /// The controller's tuning values, in one place. Every value here is a STARTING value, to be tuned by
    /// implementing and testing (design doc, sections 4 and 11). Distances in metres, times in simulated seconds,
    /// angles in degrees.
    /// </summary>
    public static class ControllerConstants
    {
        // ---- Goal handoff ----

        /// <summary>How often the algorithm is asked for its next waypoint. It is also asked at once when a goal ends.</summary>
        public const float GoalPeriodSeconds = 0.5f;

        /// <summary>A waypoint counts as Reached when the robot's centre is this close to it (start: the body radius).</summary>
        public const float ArrivalToleranceMeters = 0.04f;

        /// <summary>A goal is Blocked when the distance to it hasn't shrunk by <see cref="ProgressEpsilonMeters"/> for this long.</summary>
        public const float BlockedTimeoutSeconds = 3f;

        /// <summary>The smallest improvement in distance-to-goal that counts as progress.</summary>
        public const float ProgressEpsilonMeters = 0.05f;

        /// <summary>A new waypoint closer than this to the current one is treated as the same goal, nudged: progress timing carries on.</summary>
        public const float SameGoalMeters = 0.25f;

        // ---- Sensing ----

        /// <summary>Lidar scans per simulated second (CPU vs map freshness). <see cref="Sensors.LidarSensor.ScanRateHz"/> reads this.</summary>
        public const float LidarScanRateHz = 10f;

        // ---- Driving ----

        /// <summary>Turn rate per degree of heading error, deg/s per deg. Clamped to the robot's turn speed.</summary>
        public const float SteeringGain = 6f;

        /// <summary>Above this heading error the robot turns in place before driving.</summary>
        public const float TurnInPlaceDegrees = 35f;

        /// <summary>The robot starts slowing down this far from a waypoint.</summary>
        public const float SlowdownRadiusMeters = 0.4f;

        /// <summary>The slowest approach, as a fraction of drive speed, so the robot never crawls to a stop short of the goal.</summary>
        public const float MinApproachFraction = 0.25f;

        // ---- Local avoidance and recovery ----

        /// <summary>How far ahead of the body's edge the believed map is checked for a blocked cell while driving forward.</summary>
        public const float BeliefLookAheadMeters = 0.05f;

        /// <summary>A whisker contact within this many degrees of straight ahead stops forward driving.</summary>
        public const float FrontContactDegrees = 100f;

        /// <summary>After contact, the robot backs straight off this far.</summary>
        public const float BackOffMeters = 0.08f;

        /// <summary>When retrying a blocked goal: turn this far toward the side with more believed free space ...</summary>
        public const float TurnAwayDegrees = 60f;

        /// <summary>... then drive this far before aiming at the goal again.</summary>
        public const float SidestepMeters = 0.25f;

        /// <summary>How far each side is probed (through the belief) when choosing which way to turn away.</summary>
        public const float SideProbeMeters = 0.8f;

        /// <summary>Heading error at which a turn-in-place counts as finished.</summary>
        public const float TurnDoneDegrees = 3f;
    }
}
