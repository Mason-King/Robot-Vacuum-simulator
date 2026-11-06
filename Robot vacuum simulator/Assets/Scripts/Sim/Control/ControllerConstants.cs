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

        // ---- Path planning and re-routing ----

        /// <summary>Switch for the planner: off = drive straight at the waypoint, as before the planner existed (for comparisons).</summary>
        public static readonly bool PlanPaths = true;

        /// <summary>Planned paths keep the body this far (beyond its radius) from anything believed impassable.</summary>
        public const float ClearanceMarginMeters = 0.02f;

        /// <summary>A gentle path corner counts as passed when the robot's centre is this close to it ...</summary>
        public const float PathCornerToleranceMeters = 0.10f;

        /// <summary>... gentle meaning the path bends by at most this much there; a sharper corner must be reached (arrival tolerance), so it isn't cut.</summary>
        public const float GentleCornerDegrees = 20f;

        /// <summary>How often the rest of the path is checked against the map while driving, when the map's obstacles changed.</summary>
        public const float PathCheckPeriodSeconds = 0.25f;

        /// <summary>
        /// Re-routes after running into something (contact, a believed obstacle just ahead, or no progress) before the
        /// goal is reported Blocked. Re-routes because the map changed further along the path are not counted.
        /// </summary>
        public const int MaxReroutes = 3;

        /// <summary>
        /// Contact further than this from straight ahead (but within <see cref="FrontContactDegrees"/>) is a graze: the
        /// robot turns away from it and keeps going, without stopping or using a re-route. Nearer the front, it is an
        /// encounter.
        /// </summary>
        public const float GlancingContactDegrees = 45f;

        /// <summary>While grazing: turn away from the contact at this rate ...</summary>
        public const float GlanceTurnDegreesPerSecond = 90f;

        /// <summary>... at this fraction of drive speed.</summary>
        public const float GlanceSpeedFraction = 0.6f;

        /// <summary>More separate grazes than this on one leg of the path count as one encounter (no endless scraping).</summary>
        public const int MaxGlancesPerLeg = 5;

        /// <summary>A re-routed path may be at most this many times the goal's first planned path ...</summary>
        public const float DetourFactor = 2f;

        /// <summary>... plus this much; a longer detour reports Blocked instead (so a goal behind a long wall doesn't send the robot round the house).</summary>
        public const float DetourExtraMeters = 4f;

        /// <summary>The planner's search area: the known map, the start and the goal, plus this much on every side.</summary>
        public const float PlannerPadMeters = 1f;

        /// <summary>A search that expands more cells than this gives up (no path).</summary>
        public const int PlannerMaxExpansions = 400_000;

        /// <summary>Cells closer to an obstacle than the clearance (only allowed leaving the start or reaching the goal) cost this many times more.</summary>
        public const float PlannerNearWallCostFactor = 3f;

        /// <summary>Traversability below this is costed as this (so a very hard floor is avoided, not treated as nearly a wall).</summary>
        public const float PlannerMinTraversability = 0.2f;
    }
}
