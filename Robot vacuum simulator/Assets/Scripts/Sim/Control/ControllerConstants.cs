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

        /// <summary>
        /// A head-on bump (within <see cref="BumperArrivalDegrees"/> of straight ahead) this close to the goal counts as
        /// arriving: the robot is as close as its bumper lets it get. The bumper fires on anything within
        /// <see cref="Sensors.WhiskerSensor.LookAheadMeters"/> (9 cm) of the body's front, so a goal right in front of
        /// a wall (an edge run's corner, a door jamb, a lane's end in a corner) can't be driven onto: without this, the
        /// robot backed off and tried again until its re-routes ran out and the goal ended Blocked, about 5 cm short,
        /// after several seconds. 7 cm and 20°: those failures stopped 4–6 cm short at 0–16°. Wider (10 cm, 45°) also
        /// stopped edge runs early where the wall was to the side, which backing off and re-routing does reach, and
        /// left the strips by the walls dirtier.
        /// </summary>
        public const float BumperArrivalMeters = 0.07f;

        /// <summary>See <see cref="BumperArrivalMeters"/>.</summary>
        public const float BumperArrivalDegrees = 20f;

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

        // ---- Sweeping and the dock ----

        /// <summary>Sweep speed is the one-pass speed divided by this, for drift between lanes and floor the sensor hasn't reported yet.</summary>
        public const float SweepSpeedSafety = RobotVacuum.AlgorithmApi.DriveRequests.SweepSpeedSafety;

        /// <summary>
        /// The slowest sweep. Slower would reach the target in fewer passes, but it is too slow to watch: where one pass
        /// at this speed isn't enough, the algorithm lays its lanes closer or makes more passes instead.
        /// </summary>
        public const float SweepSpeedFloor = RobotVacuum.AlgorithmApi.DriveRequests.SweepSpeedFloor;

        /// <summary>The shortest gap between two costings of the trip home against the battery, simulated seconds.</summary>
        public const float BatteryCheckSeconds = 1f;

        /// <summary>
        /// The trip home is costed again once the robot has used this much of its spare charge (charge minus the
        /// homing reserve) since the last costing. A third keeps the robot above the reserve even if the trip home
        /// grows as fast as driving away can make it (see MovementBrain.CheckBattery).
        /// </summary>
        public const float BatteryCheckSpareFraction = 1f / 3f;

        /// <summary>The trip home is costed on normal floor times this (the robot doesn't know every floor's energy cost).</summary>
        public const float HomeFloorMargin = 1.5f;

        /// <summary>Head home when the charge is down to this many times the trip's cost ...</summary>
        public const float HomeReserveFactor = 1.25f;

        /// <summary>... plus this much charge (detours, re-routes).</summary>
        public const float HomeReserveExtra = 2f;

        /// <summary>Blocked on the way home: retries (turn away, sidestep, try again) before giving up.</summary>
        public const int HomeAttempts = 3;

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

        /// <summary>
        /// Unexplored cells (SLAM) read as open floor, but a path across them is a guess: they cost this many times
        /// more, so a known way round is taken when there is one, and unknown floor only when it is much shorter.
        /// </summary>
        public const float PlannerUnknownCostFactor = 4f;

        /// <summary>
        /// Diagnostics: a cell with traversability above 0 but below this is a SHADED wall (SLAM draws a wall it is not
        /// yet sure of as low traversability rather than impassable). A planned path that crosses one is reported when
        /// the run logs algorithm decisions.
        /// </summary>
        public const float ShadedTraversability = 0.5f;

        /// <summary>
        /// A cell is a wall to the planner and the map analysis when its traversability is below this fraction of its
        /// floor's ease: SLAM shades a wall it isn't sure of from the ease (wall probability 0.5) down to 0 (0.75), so
        /// this is "more likely wall than not, by a margin" (about 0.625). Normal runs have no shaded cells.
        /// </summary>
        public const float WallEaseFraction = 0.5f;

        /// <summary>Traversability below this is costed as this (so a very hard floor is avoided, not treated as nearly a wall).</summary>
        public const float PlannerMinTraversability = 0.2f;

        /// <summary>
        /// Cut off: when the algorithm finishes, or the trip home finds no way, and at least this many cells of mapped
        /// floor can't be reached now but could be through places the robot reached earlier in the run, something (the
        /// cat) is blocking the way to them: wait and try again instead of ending. About 0.1 m².
        /// </summary>
        public const int BoxedInCutOffCells = 40;

        /// <summary>
        /// Cut off: the robot first drives to the reachable floor nearest the cut-off floor (so its sensors can see
        /// the blockage clear) unless it is already within this distance, meters, of it.
        /// </summary>
        public const float BoxedInLookoutNearMeters = 0.3f;

        /// <summary>
        /// After BoxedInTries short waits fail: one last hold, sim seconds, with a warning, before giving up (the
        /// algorithm finished: return to the dock; on the way home: end the run where the robot is).
        /// </summary>
        public const float BoxedInFinalHoldSeconds = 180f;

        /// <summary>
        /// No way home with the usual clearance (radius + ClearanceMarginMeters): one try along a path planned with this
        /// fraction of the radius instead, before waiting. The belief map's cells make a sitting cat look wider than it
        /// is, so a gap the body can squeeze through may still be there.
        /// </summary>
        public const float BoxedInSqueezeClearanceFraction = 0.85f;

        /// <summary>
        /// During that last hold, how often, sim seconds, the way is checked: the hold ends as soon as it is open.
        /// </summary>
        public const float BoxedInFinalPollSeconds = 5f;

        /// <summary>
        /// Blockage: when nothing measurable is cut off, the algorithm finishing within this many sim seconds of a goal
        /// that ended Blocked or Unreachable makes the controller look for a blockage next to that goal.
        /// </summary>
        public const float BoxedInRecentFailSeconds = 10f;

        /// <summary>Blockage: how far, meters, from the failed goal to look for cells the robot drove over that are blocked now.</summary>
        public const float BoxedInBlockageRadiusMeters = 1f;

        /// <summary>
        /// Blockage: at least this many cells the robot drove over earlier, blocked now, near the failed goal, count as a
        /// blockage (something sitting in the way, such as the cat). About 0.05 m².
        /// </summary>
        public const int BoxedInBlockageCells = 20;

        /// <summary>
        /// Blockage near the robot (no failed goal just before the finish): at least this many cells mapped blocked now
        /// that the robot could reach earlier (something that moves is sitting there)...
        /// </summary>
        public const int BoxedInMovedCells = 6;

        /// <summary>
        /// Blockage near the robot: the moved cells (blocked now, reachable earlier) must span no more than this either
        /// way: a cat, not a stretch of wall redrawn a few centimeters over by a SLAM pose correction.
        /// </summary>
        public const float BoxedInMovedMaxSpanMeters = 0.6f;

        /// <summary>...with at least this many unmapped cells within BoxedInHiddenRadiusMeters of them (what it hides).</summary>
        public const int BoxedInHiddenCells = 10;

        /// <summary>Blockage near the robot: how close, meters, unmapped cells must be to the moved cells to count as hidden by them.</summary>
        public const float BoxedInHiddenRadiusMeters = 0.5f;

        /// <summary>
        /// Cut off: this many unmapped cells bordering floor reachable only through the blockage also count, but only
        /// together with some cut-off floor still to clean (alone they were SLAM's redrawn map edges): about 0.5 m of edge.
        /// </summary>
        public const int BoxedInUnmappedEdgeCells = 10;

        /// <summary>
        /// Floor counts as still to clean (for the cut-off and blockage checks) below this fraction of the target
        /// cleanliness, the robot's own belief. Floor already cleaned that is cut off or sat on is not missed.
        /// </summary>
        public const float BoxedInDirtyFraction = 0.8f;

        /// <summary>
        /// Blockage: at least this many cells still to clean, reachable earlier, that the body doesn't fit on now near
        /// where the algorithm stopped: something (the cat) is sitting on floor that is left. About 0.05 m².
        /// </summary>
        public const int BoxedInDirtyInWayCells = 20;

        /// <summary>How often, sim seconds, the floor the robot can reach is recorded (the cut-off check's reference).</summary>
        public const float BoxedInSampleSeconds = 10f;

        /// <summary>The first wait, sim seconds, before trying again when boxed in; each further wait is twice the last.</summary>
        public const float BoxedInWaitSeconds = 5f;

        /// <summary>Tries while boxed in before accepting it (a robot truly shut in is still reported).</summary>
        public const int BoxedInTries = 3;

        /// <summary>
        /// The way home failed and the robot isn't boxed in: wait for it to clear only if at least this many cells the
        /// robot reached earlier are blocked now (and not flickering) within BoxedInBlockageRadiusMeters of the dock or
        /// of the reachable floor next to the blockage: something moved in. Otherwise nothing there will move (a closed
        /// door, a wall the level lacks a gap in) and the run parks as Dock Unreachable at once instead of ~215 s later.
        /// </summary>
        public const int BoxedInHomeMovedCells = 3;

        /// <summary>
        /// When flickering cells are among the blocked ones near where the algorithm stopped, the "in the way" rules
        /// also need at least this many moved cells that don't flicker: the in-way cells may all be the clearance around
        /// a chair's legs.
        /// </summary>
        public const int BoxedInMovedMinCells = 3;

        // ---- Flicker (InternalModelWriter.Flickers) ----

        /// <summary>Flips of one cell between blocked and clear at most this far apart, sim seconds, count as one streak.</summary>
        public const float FlickerGapSeconds = 1f;

        /// <summary>A streak of this many flips marks the cell as flickering: something thin the lidar half sees, not the cat.</summary>
        public const int FlickerFlips = 4;

        /// <summary>A cell counts as flickering for this long, sim seconds, after its last streak (static furniture stays put).</summary>
        public const float FlickerMemorySeconds = 600f;

        /// <summary>An UntilClean hold ends after this many times the time its dirtiest cell needed when the hold began.</summary>
        public const float HoldCapFactor = 1.5f;

        /// <summary>During a hold the first contact is ignored; a contact at least this long after it ends the hold as Blocked, sim seconds.</summary>
        public const float HoldSecondContactSeconds = 3f;
    }
}
