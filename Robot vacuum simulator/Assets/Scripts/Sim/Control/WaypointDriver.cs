using System.Collections.Generic;
using RobotVacuum.AlgorithmApi;
using UnityEngine;
using K = RobotVacuum.Sim.Control.ControllerConstants;

namespace RobotVacuum.Sim.Control
{
    /// <summary>Everything the waypoint driver needs for one physics step.</summary>
    public readonly struct DriveInput
    {
        public readonly RobotPose pose;
        public readonly float simTime;
        public readonly float dt;

        /// <summary>The whisker reports contact (or something right in front), and in which direction (robot frame, degrees).</summary>
        public readonly bool bumped;
        public readonly float bumpBearing;

        public readonly InternalModelView view;
        public readonly float radius;
        public readonly float driveSpeed;
        public readonly float reverseSpeed;
        public readonly float turnSpeed;

        public DriveInput(RobotPose pose, float simTime, float dt, bool bumped, float bumpBearing, InternalModelView view,
            float radius, float driveSpeed, float reverseSpeed, float turnSpeed)
        {
            this.pose = pose;
            this.simTime = simTime;
            this.dt = dt;
            this.bumped = bumped;
            this.bumpBearing = bumpBearing;
            this.view = view;
            this.radius = radius;
            this.driveSpeed = driveSpeed;
            this.reverseSpeed = reverseSpeed;
            this.turnSpeed = turnSpeed;
        }
    }

    /// <summary>
    /// The lower layer's driving: takes the robot to one goal at a time and reports how it is going (design doc,
    /// section 4). Everything here works from the BELIEVED pose and the believed map.
    ///
    ///   Path          with a <see cref="Planner"/>, the goal is reached along a planned path that keeps the body clear
    ///                 of believed obstacles; without one, the robot drives straight at the goal.
    ///   Driving       turn toward the next path corner (in place if it is far off to the side), then drive while
    ///                 steering to hold the bearing, slowing near the goal. Reached within the arrival tolerance.
    ///   Grazing       (with a planner) contact well off to the side (beyond <see cref="K.GlancingContactDegrees"/>):
    ///                 turn away from it and keep going along the path. Too many grazes on one leg is an encounter.
    ///   Re-routing    (with a planner) running into something - contact near the front, or no progress for the
    ///                 blocked timeout - backs off and re-plans around it, keeping the
    ///                 goal InProgress, up to <see cref="K.MaxReroutes"/> times. If nothing new was mapped (the
    ///                 obstacle is invisible to the sensors), it turns toward the freer side and sidesteps first. When
    ///                 the map changes further along the path, the path is re-planned without counting a re-route.
    ///                 A re-planned path longer than <see cref="K.DetourFactor"/> × the first + <see cref="K.DetourExtraMeters"/>
    ///                 ends the goal Blocked.
    ///   Blocked       re-routes used up, a detour too long, or (without a planner) at once on contact or no progress.
    ///   Unreachable   the goal is in a cell believed impassable, or no path for the body exists.
    ///   recovery      if the algorithm asks for the SAME goal again after Blocked, the robot turns toward the side
    ///                 with more believed free space, sidesteps a short way, and tries again (re-routes start over).
    ///
    /// Nothing loops forever: a goal that keeps failing is reported Blocked, and the algorithm decides what to do.
    /// </summary>
    public sealed class WaypointDriver
    {
        public enum Phase { Idle, Driving, BackingOff, TurningAway, Sidestepping }

        Vector2 goal;
        bool hasGoal;
        float bestDistance;
        float lastProgressTime;
        float phaseMetersLeft;
        float turnAwayHeading;
        bool retryAfterBackOff;

        // Planned path (with a planner only).
        readonly List<Vector2> path = new List<Vector2>();
        int corner;              // the path point being driven to
        float firstPlanLength;   // the goal's first plan, for the detour limit
        int reroutes;
        int plannedVersion;      // the map's topology when the path was planned
        int checkedVersion;      // ... when the path was last checked
        float nextCheckTime;
        bool replanWhenDriving;
        bool nextPlanIsFirst;
        bool rerouteAfterBackOff;
        int glances;             // separate grazes on the current leg
        bool passThrough;        // the goal is a point on the way: no slowing for it, Reached early
        bool wasTouching;

        public Phase CurrentPhase { get; private set; } = Phase.Idle;
        public GoalStatus Status { get; private set; } = GoalStatus.None;

        /// <summary>Plans the paths. Null = drive straight at the goal (the behaviour before the planner).</summary>
        public PathPlanner Planner { get; set; }

        /// <summary>True when the current goal is a point on the way (see <see cref="SetGoal"/>).</summary>
        public bool PassThrough => passThrough;

        /// <summary>The goal being driven to, or null.</summary>
        public Vector2? Goal => hasGoal ? goal : (Vector2?)null;

        /// <summary>The point being steered at right now: the next corner of the path, or the goal.</summary>
        public Vector2? Target => !hasGoal ? (Vector2?)null : Target0();

        /// <summary>The planned path (start, corners, goal); empty without a planner or a goal.</summary>
        public IReadOnlyList<Vector2> Path => path;

        /// <summary>The index in <see cref="Path"/> of the corner being driven to.</summary>
        public int PathCorner => corner;

        /// <summary>Re-routes used on the current goal (see <see cref="K.MaxReroutes"/>).</summary>
        public int Reroutes => reroutes;

        /// <summary>Re-plans of any kind since the driver was made (reporting and tests).</summary>
        public int Replans { get; private set; }

        /// <summary>True once the current status has been passed to the algorithm, so a finished goal isn't re-asked about every step.</summary>
        public bool StatusReported { get; set; }

        bool Planning => Planner != null;

        Vector2 Target0() => Planning && path.Count >= 2 && corner < path.Count ? path[corner] : goal;

        public void Reset()
        {
            hasGoal = false;
            CurrentPhase = Phase.Idle;
            Status = GoalStatus.None;
            StatusReported = false;
            retryAfterBackOff = false;
            rerouteAfterBackOff = false;
            replanWhenDriving = false;
            path.Clear();
            reroutes = 0;
            passThrough = false;
        }

        /// <summary>
        /// Starts driving to <paramref name="waypoint"/> (null = stop). A waypoint within
        /// <see cref="K.SameGoalMeters"/> of the current one is treated as the same goal, nudged: progress timing and
        /// re-routes carry on, so an algorithm can't keep a stuck robot "busy" by moving its goal a hair each time.
        /// </summary>
        /// <param name="passThrough">
        /// The goal is a point on the way, not a destination: no slowing down for it, and it is reported Reached as soon
        /// as the robot is within <see cref="DriveRequests.PassThroughMeters"/>, while the robot keeps moving, so the
        /// next goal carries the motion on without a stop.
        /// </param>
        public void SetGoal(Vector2? waypoint, RobotPose pose, float simTime, InternalModelView view, bool passThrough = false)
        {
            this.passThrough = passThrough && waypoint.HasValue;
            if (!waypoint.HasValue)
            {
                hasGoal = false;
                Status = GoalStatus.None;
                StatusReported = false;
                path.Clear();
                if (CurrentPhase != Phase.BackingOff) CurrentPhase = Phase.Idle;
                return;
            }

            Vector2 next = waypoint.Value;
            bool nudge = hasGoal && Status == GoalStatus.InProgress && Vector2.Distance(next, goal) <= K.SameGoalMeters;

            goal = next;
            hasGoal = true;
            Status = GoalStatus.InProgress;
            StatusReported = false;
            retryAfterBackOff = false;
            if (!nudge)
            {
                reroutes = 0;
                rerouteAfterBackOff = false;
                replanWhenDriving = false;
            }

            if (CurrentPhase != Phase.BackingOff) CurrentPhase = Phase.Driving;

            if (view != null && view.IsBlockedAt(goal))
            {
                path.Clear();
                Finish(GoalStatus.Unreachable);
                return;
            }

            if (Planning && view != null)
            {
                // A nudged goal keeps the path when the last leg can just be moved to it.
                bool moved = nudge && path.Count >= 2 && LastLegClear(pose);
                if (moved) path[path.Count - 1] = goal;
                else if (!Plan(pose, view, first: !nudge)) return;
            }

            if (!nudge)
            {
                bestDistance = Remaining(pose);
                lastProgressTime = simTime;
            }
        }

        /// <summary>The algorithm asked for the same goal after it was Blocked: turn away, sidestep, and try again.</summary>
        public void RetryAfterBlocked(RobotPose pose, float simTime, InternalModelView view)
        {
            if (!hasGoal) return;

            Status = GoalStatus.InProgress;
            StatusReported = false;
            lastProgressTime = simTime;
            bestDistance = Remaining(pose);
            reroutes = 0;
            if (Planning)
            {
                replanWhenDriving = true; // after the sidestep, a fresh plan
                nextPlanIsFirst = true;
            }

            if (CurrentPhase == Phase.BackingOff) retryAfterBackOff = true;
            else BeginTurnAway(pose, view);
        }

        /// <summary>One physics step: the forward speed (m/s, negative = reverse) and turn rate (deg/s, positive = left) to command.</summary>
        public void Step(in DriveInput input, out float forward, out float turn)
        {
            forward = 0f;
            turn = 0f;
            if (!input.bumped) wasTouching = false;

            // A retry that keeps failing in recovery (turn, sidestep, contact, back off, ...) still times out.
            if ((CurrentPhase == Phase.TurningAway || CurrentPhase == Phase.Sidestepping) &&
                Status == GoalStatus.InProgress && input.simTime - lastProgressTime > K.BlockedTimeoutSeconds)
            {
                Status = GoalStatus.Blocked;
                StatusReported = false;
                CurrentPhase = Phase.Idle;
                return;
            }

            switch (CurrentPhase)
            {
                case Phase.BackingOff:
                    StepBackOff(input, out forward);
                    return;

                case Phase.TurningAway:
                    turn = TurnToward(input.pose.heading, turnAwayHeading, input.turnSpeed);
                    if (Mathf.Abs(Mathf.DeltaAngle(input.pose.heading, turnAwayHeading)) <= K.TurnDoneDegrees)
                    {
                        CurrentPhase = Phase.Sidestepping;
                        phaseMetersLeft = K.SidestepMeters;
                    }
                    return;

                case Phase.Sidestepping:
                    // With a planner, only real contact interrupts: the path is re-planned after the sidestep anyway,
                    // and the map look-ahead would trip on the very wall the robot is stepping along.
                    if (Planning ? ContactAhead(input) : ContactOrBlockedAhead(input))
                    {
                        BeginBackOff(retry: true);
                        StepBackOff(input, out forward);
                        return;
                    }
                    forward = input.driveSpeed;
                    phaseMetersLeft -= forward * input.dt;
                    if (phaseMetersLeft <= 0f) CurrentPhase = Phase.Driving;
                    return;

                case Phase.Driving:
                    StepDrive(input, out forward, out turn);
                    return;

                default:
                    return;
            }
        }

        void StepDrive(in DriveInput input, out float forward, out float turn)
        {
            forward = 0f;
            turn = 0f;
            if (!hasGoal || Status != GoalStatus.InProgress) return; // finished: wait for the algorithm

            var pose = input.pose;

            if (Planning && input.view != null)
            {
                if (replanWhenDriving)
                {
                    replanWhenDriving = false;
                    bool first = nextPlanIsFirst;
                    nextPlanIsFirst = false;
                    if (!Plan(pose, input.view, first)) return;
                    bestDistance = Remaining(pose);
                    lastProgressTime = input.simTime;
                }

                while (corner < path.Count - 1 && Vector2.Distance(pose.position, path[corner]) <= CornerTolerance(corner))
                {
                    corner++;
                    glances = 0; // a new leg
                }

                // The map's obstacles changed: is the rest of the path still clear? If not, re-plan (not a re-route).
                if (input.simTime >= nextCheckTime)
                {
                    nextCheckTime = input.simTime + K.PathCheckPeriodSeconds;
                    if (input.view.TopologyVersion != checkedVersion)
                    {
                        checkedVersion = input.view.TopologyVersion;
                        if (!RestOfPathClear(pose))
                        {
                            if (!Plan(pose, input.view, first: false)) return;
                            bestDistance = Remaining(pose);
                            lastProgressTime = input.simTime;
                        }
                    }
                }
            }

            float toGoal = Vector2.Distance(pose.position, goal);
            if (passThrough && toGoal <= DriveRequests.PassThroughMeters)
            {
                // Passed: report it, and keep rolling toward it until the next goal arrives (asked for at once).
                Status = GoalStatus.Reached;
                StatusReported = false;
                float passError = pose.BearingTo(goal);
                turn = Mathf.Clamp(passError * K.SteeringGain, -input.turnSpeed, input.turnSpeed);
                forward = Mathf.Abs(passError) > K.TurnInPlaceDegrees ? 0f : input.driveSpeed * Mathf.Cos(passError * Mathf.Deg2Rad);
                return;
            }

            if (toGoal <= K.ArrivalToleranceMeters)
            {
                Finish(GoalStatus.Reached);
                return;
            }

            if (input.view != null && input.view.IsBlockedAt(goal))
            {
                Finish(GoalStatus.Unreachable);
                return;
            }

            float remaining = Remaining(pose);
            if (remaining < bestDistance - K.ProgressEpsilonMeters)
            {
                bestDistance = remaining;
                lastProgressTime = input.simTime;
            }
            else if (input.simTime - lastProgressTime > K.BlockedTimeoutSeconds)
            {
                if (Planning) Encounter(input, backOff: false, out forward);
                else Finish(GoalStatus.Blocked);
                return;
            }

            Vector2 target = Target0();
            float error = pose.BearingTo(target);
            turn = Mathf.Clamp(error * K.SteeringGain, -input.turnSpeed, input.turnSpeed);

            if (Mathf.Abs(error) > K.TurnInPlaceDegrees) return; // face it first

            // Slow down for the goal (not for a point on the way), and for a sharp corner (it must be reached, not cut).
            float slowFor = passThrough ? float.PositiveInfinity : remaining;
            if (Planning && corner < path.Count - 1 && CornerTolerance(corner) <= K.ArrivalToleranceMeters)
                slowFor = Mathf.Min(slowFor, Vector2.Distance(pose.position, path[corner]));
            float approach = Mathf.Clamp(slowFor / K.SlowdownRadiusMeters, K.MinApproachFraction, 1f);
            forward = input.driveSpeed * approach * Mathf.Cos(error * Mathf.Deg2Rad);

            // Avoidance only matters when actually moving forward: turning in place next to a wall is fine.
            if (forward <= 0f) return;

            if (Planning)
            {
                // Contact is a real encounter. A believed obstacle just ahead is not, by itself: the path was planned
                // clear of the map, so it is either an obstacle mapped since (then the path is no longer clear and is
                // re-planned, without using a re-route) or a wall beside a goal planned next to it.
                if (ContactAhead(input))
                {
                    bool graze = Mathf.Abs(input.bumpBearing) > K.GlancingContactDegrees;
                    if (graze && !wasTouching) glances++;
                    wasTouching = true;

                    if (graze && glances <= K.MaxGlancesPerLeg)
                    {
                        // Slide along it, turning away.
                        forward = input.driveSpeed * K.GlanceSpeedFraction;
                        turn = -Mathf.Sign(input.bumpBearing) * Mathf.Min(K.GlanceTurnDegreesPerSecond, input.turnSpeed);
                        return;
                    }

                    turn = 0f;
                    Encounter(input, backOff: true, out forward);
                }
                else if (BlockedAheadInMap(input))
                {
                    checkedVersion = int.MinValue; // have the path checked at the next check, even if the map hasn't changed
                }
                return;
            }

            if (ContactOrBlockedAhead(input))
            {
                turn = 0f;
                Finish(GoalStatus.Blocked);
                BeginBackOff(retry: false);
                StepBackOff(input, out forward);
            }
        }

        void Finish(GoalStatus status)
        {
            Status = status;
            StatusReported = false;
            if (CurrentPhase == Phase.Driving) CurrentPhase = Phase.Idle;
        }

        // ---- Paths and re-routing --------------------------------------------------------------------

        /// <summary>Plans from the pose to the goal. False (and the goal finished Unreachable or Blocked) if there is no path or it is too long a detour.</summary>
        bool Plan(RobotPose pose, InternalModelView view, bool first)
        {
            Replans++;
            plannedVersion = checkedVersion = view.TopologyVersion;
            corner = 1;
            glances = 0;

            if (!Planner.FindPath(pose.position, goal, path, out float length))
            {
                path.Clear();
                Finish(GoalStatus.Unreachable);
                return false;
            }

            if (first || firstPlanLength <= 0f)
            {
                firstPlanLength = length;
            }
            else if (length > K.DetourFactor * firstPlanLength + K.DetourExtraMeters)
            {
                Finish(GoalStatus.Blocked);
                return false;
            }
            return true;
        }

        /// <summary>
        /// The robot ran into something (or made no progress). Uses up a re-route: back off if asked, then re-plan,
        /// sidestepping first if the obstacle never reached the map. Out of re-routes: Blocked.
        /// </summary>
        void Encounter(in DriveInput input, bool backOff, out float forward)
        {
            forward = 0f;
            reroutes++;
            lastProgressTime = input.simTime; // recovery gets its own timeout

            if (reroutes > K.MaxReroutes)
            {
                Finish(GoalStatus.Blocked);
                if (backOff)
                {
                    BeginBackOff(retry: false);
                    StepBackOff(input, out forward);
                }
                return;
            }

            if (backOff)
            {
                BeginBackOff(retry: false);
                rerouteAfterBackOff = true;
                StepBackOff(input, out forward);
            }
            else
            {
                Reroute(input.pose, input.view);
            }
        }

        void Reroute(RobotPose pose, InternalModelView view)
        {
            replanWhenDriving = true;
            if (view != null && view.TopologyVersion == plannedVersion) BeginTurnAway(pose, view); // nothing new mapped: sidestep, then re-plan
            else CurrentPhase = Phase.Driving;
        }

        /// <summary>
        /// How close counts as passing corner <paramref name="index"/>: the loose tolerance where the path barely bends,
        /// the arrival tolerance where it turns sharply. Turning early at a sharp corner would cut it, and the inside of
        /// a corner is usually the obstacle the path is going round.
        /// </summary>
        float CornerTolerance(int index)
        {
            if (index <= 0 || index >= path.Count - 1) return K.ArrivalToleranceMeters;
            float bend = Vector2.Angle(path[index] - path[index - 1], path[index + 1] - path[index]);
            return bend <= K.GentleCornerDegrees ? K.PathCornerToleranceMeters : K.ArrivalToleranceMeters;
        }

        float Remaining(RobotPose pose)
        {
            if (!Planning || path.Count < 2 || corner >= path.Count) return Vector2.Distance(pose.position, goal);
            float length = Vector2.Distance(pose.position, path[corner]);
            for (int i = corner; i < path.Count - 1; i++) length += Vector2.Distance(path[i], path[i + 1]);
            return length;
        }

        bool RestOfPathClear(RobotPose pose)
        {
            if (path.Count < 2 || corner >= path.Count) return true;
            if (!Planner.SegmentClear(pose.position, path[corner])) return false;
            for (int i = corner; i < path.Count - 1; i++)
                if (!Planner.SegmentClear(path[i], path[i + 1])) return false;
            return true;
        }

        bool LastLegClear(RobotPose pose)
        {
            Vector2 from = corner >= path.Count - 1 ? pose.position : path[path.Count - 2];
            return Planner.SegmentClear(from, goal);
        }

        // ---- Recovery ------------------------------------------------------------

        void BeginBackOff(bool retry)
        {
            CurrentPhase = Phase.BackingOff;
            phaseMetersLeft = K.BackOffMeters;
            retryAfterBackOff = retry;
        }

        void StepBackOff(in DriveInput input, out float forward)
        {
            // Contact from behind while backing (the whisker reports any real collision) ends the back-off early.
            bool hitBehind = input.bumped && Mathf.Abs(input.bumpBearing) > K.FrontContactDegrees;

            forward = -input.reverseSpeed;
            phaseMetersLeft -= input.reverseSpeed * input.dt;
            if (phaseMetersLeft > 0f && !hitBehind) return;

            forward = 0f;
            if (retryAfterBackOff && hasGoal)
            {
                retryAfterBackOff = false;
                BeginTurnAway(input.pose, input.view);
            }
            else if (rerouteAfterBackOff && hasGoal && Status == GoalStatus.InProgress)
            {
                rerouteAfterBackOff = false;
                Reroute(input.pose, input.view);
            }
            else
            {
                CurrentPhase = hasGoal && Status == GoalStatus.InProgress ? Phase.Driving : Phase.Idle;
            }
        }

        void BeginTurnAway(RobotPose pose, InternalModelView view)
        {
            // The side with more believed free space wins; a tie turns left.
            float left = SideRoom(pose, view, +K.TurnAwayDegrees);
            float right = SideRoom(pose, view, -K.TurnAwayDegrees);
            turnAwayHeading = pose.heading + (right > left ? -K.TurnAwayDegrees : K.TurnAwayDegrees);
            CurrentPhase = Phase.TurningAway;
        }

        static float SideRoom(RobotPose pose, InternalModelView view, float offset) =>
            view != null
                ? view.FreeDistance(pose.position, RobotPose.DirectionOf(pose.heading + offset), K.SideProbeMeters)
                : K.SideProbeMeters;

        // ---- Sensing helpers -------------------------------------------------------

        static bool ContactOrBlockedAhead(in DriveInput input) => ContactAhead(input) || BlockedAheadInMap(input);

        static bool ContactAhead(in DriveInput input) => input.bumped && Mathf.Abs(input.bumpBearing) <= K.FrontContactDegrees;

        static bool BlockedAheadInMap(in DriveInput input)
        {
            if (input.view == null) return false;

            // Three points just beyond the front of the body: straight ahead, and 35° to either side.
            var pose = input.pose;
            float reach = input.radius + K.BeliefLookAheadMeters;
            return input.view.IsBlockedAt(pose.position + RobotPose.DirectionOf(pose.heading) * reach) ||
                   input.view.IsBlockedAt(pose.position + RobotPose.DirectionOf(pose.heading + 35f) * reach) ||
                   input.view.IsBlockedAt(pose.position + RobotPose.DirectionOf(pose.heading - 35f) * reach);
        }

        static float TurnToward(float heading, float target, float turnSpeed) =>
            Mathf.Clamp(Mathf.DeltaAngle(heading, target) * K.SteeringGain, -turnSpeed, turnSpeed);
    }
}
