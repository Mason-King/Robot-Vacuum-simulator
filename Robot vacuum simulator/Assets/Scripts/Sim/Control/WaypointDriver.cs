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
    /// The lower layer's driving: takes the robot to one waypoint at a time and reports how it is going
    /// (design doc, section 4). Everything here works from the BELIEVED pose and the believed map.
    ///
    ///   Driving       turn toward the waypoint (in place if it is far off to the side), then drive while steering
    ///                 to hold the bearing, slowing near the waypoint. Reached within the arrival tolerance.
    ///   avoidance     contact ahead, or a believed-impassable cell just ahead, while driving forward: the goal is
    ///                 Blocked at once and the robot backs off a little.
    ///   recovery      if the algorithm then asks for the SAME waypoint again, the robot turns toward the side with
    ///                 more believed free space, sidesteps a short way, and tries again.
    ///   Blocked       also when the distance to the waypoint has not shrunk for the blocked timeout.
    ///   Unreachable   the waypoint is in a cell believed impassable.
    ///
    /// Recovery never loops forever: a goal that keeps failing is reported Blocked each time, and the algorithm
    /// decides what to do about it.
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

        public Phase CurrentPhase { get; private set; } = Phase.Idle;
        public GoalStatus Status { get; private set; } = GoalStatus.None;

        /// <summary>The waypoint being driven to, or null.</summary>
        public Vector2? Goal => hasGoal ? goal : (Vector2?)null;

        /// <summary>True once the current status has been passed to the algorithm, so a finished goal isn't re-asked about every step.</summary>
        public bool StatusReported { get; set; }

        public void Reset()
        {
            hasGoal = false;
            CurrentPhase = Phase.Idle;
            Status = GoalStatus.None;
            StatusReported = false;
            retryAfterBackOff = false;
        }

        /// <summary>
        /// Starts driving to <paramref name="waypoint"/> (null = stop). A waypoint within
        /// <see cref="K.SameGoalMeters"/> of the current one is treated as the same goal, nudged: progress timing
        /// carries on, so an algorithm can't keep a stuck robot "busy" by moving its goal a hair each time.
        /// </summary>
        public void SetGoal(Vector2? waypoint, RobotPose pose, float simTime, InternalModelView view)
        {
            if (!waypoint.HasValue)
            {
                hasGoal = false;
                Status = GoalStatus.None;
                StatusReported = false;
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
                bestDistance = Vector2.Distance(pose.position, goal);
                lastProgressTime = simTime;
            }

            if (CurrentPhase != Phase.BackingOff) CurrentPhase = Phase.Driving;

            if (view != null && view.IsBlockedAt(goal)) Finish(GoalStatus.Unreachable);
        }

        /// <summary>The algorithm asked for the same waypoint after it was Blocked: turn away, sidestep, and try again.</summary>
        public void RetryAfterBlocked(RobotPose pose, float simTime, InternalModelView view)
        {
            if (!hasGoal) return;

            Status = GoalStatus.InProgress;
            StatusReported = false;
            lastProgressTime = simTime;
            bestDistance = Vector2.Distance(pose.position, goal);

            if (CurrentPhase == Phase.BackingOff) retryAfterBackOff = true;
            else BeginTurnAway(pose, view);
        }

        /// <summary>One physics step: the forward speed (m/s, negative = reverse) and turn rate (deg/s, positive = left) to command.</summary>
        public void Step(in DriveInput input, out float forward, out float turn)
        {
            forward = 0f;
            turn = 0f;

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
                    if (ContactOrBlockedAhead(input))
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
            float distance = Vector2.Distance(pose.position, goal);

            if (distance <= K.ArrivalToleranceMeters)
            {
                Finish(GoalStatus.Reached);
                return;
            }

            if (input.view != null && input.view.IsBlockedAt(goal))
            {
                Finish(GoalStatus.Unreachable);
                return;
            }

            if (distance < bestDistance - K.ProgressEpsilonMeters)
            {
                bestDistance = distance;
                lastProgressTime = input.simTime;
            }
            else if (input.simTime - lastProgressTime > K.BlockedTimeoutSeconds)
            {
                Finish(GoalStatus.Blocked);
                return;
            }

            float error = pose.BearingTo(goal);
            turn = Mathf.Clamp(error * K.SteeringGain, -input.turnSpeed, input.turnSpeed);

            if (Mathf.Abs(error) > K.TurnInPlaceDegrees) return; // face it first

            float approach = Mathf.Clamp(distance / K.SlowdownRadiusMeters, K.MinApproachFraction, 1f);
            forward = input.driveSpeed * approach * Mathf.Cos(error * Mathf.Deg2Rad);

            // Avoidance only matters when actually moving forward: turning in place next to a wall is fine.
            if (forward > 0f && ContactOrBlockedAhead(input))
            {
                Finish(GoalStatus.Blocked);
                BeginBackOff(retry: false);
                turn = 0f;
                StepBackOff(input, out forward);
            }
        }

        void Finish(GoalStatus status)
        {
            Status = status;
            StatusReported = false;
            if (CurrentPhase == Phase.Driving) CurrentPhase = Phase.Idle;
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

        static bool ContactOrBlockedAhead(in DriveInput input)
        {
            if (input.bumped && Mathf.Abs(input.bumpBearing) <= K.FrontContactDegrees) return true;
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
