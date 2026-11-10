using System.Collections.Generic;
using UnityEngine;

namespace RobotVacuum.Sim.Control
{
    /// <summary>
    /// Runs one of the OLD reactive patterns (Spiral, Wall follow, Lawnmower) on the new body. This is the drive /
    /// back off / turn / shift state machine that used to live inside VacuumRobot, moved here unchanged in behaviour;
    /// instead of setting the body's velocity itself, it returns a command.
    ///
    /// Temporary: it exists so those patterns keep working until the four intelligent algorithms replace them, and
    /// it can be deleted in one piece then. It reads the TRUE heading and lets its brains probe the true world, which
    /// is why these patterns are refused in SLAM runs.
    /// </summary>
    public sealed class LegacyPatternDriver
    {
        enum State { Driving, Backing, Turning, Shifting }

        struct Step
        {
            public bool turn;
            public float amount; // degrees for a turn, metres for a drive
        }

        readonly LegacyPatternBrain brain;
        readonly VacuumRobot robot;
        readonly Queue<Step> steps = new Queue<Step>();

        State state = State.Driving;
        float stateTimer;
        float targetHeading;

        public LegacyPatternDriver(LegacyPatternBrain brain, VacuumRobot robot)
        {
            this.brain = brain;
            this.robot = robot;

            if (!LegacyPatternExecution.TryReset(brain, robot, out string failure))
                Fail($"Reset failed: {failure}");
        }

        /// <summary>Set when the brain threw or returned something invalid; the controller then stops the run.</summary>
        public string Failure { get; private set; }
        public bool Failed => Failure != null;

        /// <param name="bumped">The whisker's bump: a real collision, or something right in front.</param>
        public void Tick(float dt, bool bumped, out float forward, out float turn)
        {
            forward = 0f;
            turn = 0f;
            if (Failed) return;

            float heading = robot.Heading;

            switch (state)
            {
                case State.Driving:
                    forward = robot.DriveSpeedNow;
                    if (!LegacyPatternExecution.TrySteer(brain, robot, dt, out float steer, out string failure))
                    {
                        Fail($"Steering failed: {failure}");
                        forward = 0f;
                        return;
                    }
                    turn = steer;
                    if (bumped) BeginBackup();
                    break;

                case State.Backing:
                    forward = -robot.ReverseSpeedMetersPerSecond;
                    if ((stateTimer -= dt) <= 0f) NextStep(heading);
                    break;

                case State.Turning:
                    float remaining = Mathf.DeltaAngle(heading, targetHeading);
                    if (Mathf.Abs(remaining) < 1f)
                    {
                        NextStep(heading);
                        break;
                    }
                    float maxTurn = robot.TurnSpeedDegreesPerSecond;
                    turn = dt > 0f ? Mathf.Clamp(remaining / dt, -maxTurn, maxTurn) : 0f;
                    break;

                case State.Shifting:
                    forward = robot.DriveSpeedNow;
                    if ((stateTimer -= forward * dt) <= 0f || bumped) NextStep(heading);
                    break;
            }
        }

        void BeginBackup()
        {
            state = State.Backing;
            stateTimer = robot.BackupDuration;

            if (!LegacyPatternExecution.TryAfterBump(brain, robot, out var manoeuvre, out string failure))
            {
                Fail($"Bump handling failed: {failure}");
                return;
            }

            steps.Clear();
            steps.Enqueue(new Step { turn = true, amount = manoeuvre.turn });

            if (manoeuvre.driveAfter > 0f)
            {
                steps.Enqueue(new Step { turn = false, amount = manoeuvre.driveAfter });
                steps.Enqueue(new Step { turn = true, amount = manoeuvre.turnAfter });
            }
        }

        void NextStep(float heading)
        {
            if (steps.Count == 0)
            {
                state = State.Driving;
                return;
            }

            var step = steps.Dequeue();
            if (step.turn)
            {
                state = State.Turning;
                targetHeading = heading + step.amount;
            }
            else
            {
                state = State.Shifting;
                stateTimer = step.amount;
            }
        }

        void Fail(string failure)
        {
            if (Failed) return;
            Failure = failure;
            state = State.Driving;
            steps.Clear();
        }
    }
}
