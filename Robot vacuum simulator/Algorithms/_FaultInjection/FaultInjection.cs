using System;
using RobotVacuum.AlgorithmApi;

// Deliberately broken algorithms for testing fault isolation. This folder starts with "_", so the simulator skips
// it. To use them, rename the folder to "FaultInjection" and rescan (Tools > Algorithms > Rescan, or reopen the
// simulator). Each one drives normally for a while, then fails in one specific way; the run should end with a
// clear error while the simulator (and a batch's other trials) carries on. See README.md in this folder.
namespace FaultInjection
{
    /// <summary>A plain wanderer: drive to the free space ahead, turn a random amount, repeat.</summary>
    public abstract class Wanderer : ICleaningAlgorithm
    {
        protected AlgorithmContext context;
        protected int goals;

        public abstract string Name { get; }
        public bool SupportsSlam => true;

        public virtual void Initialize(AlgorithmContext context)
        {
            this.context = context;
            goals = 0;
        }

        public Vec2? NextGoal(IBeliefMap map, Pose2 pose, GoalStatus lastStatus)
        {
            goals++;
            return Misbehave(map, pose) ?? Wander(map, pose, lastStatus);
        }

        /// <summary>Return a goal to override the wanderer (or fail here); null to wander.</summary>
        protected abstract Vec2? Misbehave(IBeliefMap map, Pose2 pose);

        Vec2 Wander(IBeliefMap map, Pose2 pose, GoalStatus lastStatus)
        {
            float heading = pose.heading;
            if (lastStatus == GoalStatus.Reached || lastStatus == GoalStatus.Blocked || lastStatus == GoalStatus.Unreachable)
                heading += context.random.NextFloat(90f, 270f);
            var direction = Pose2.DirectionOf(heading);
            float free = map.FreeDistance(pose.position, direction, 2f);
            return pose.position + direction * Math.Max(0.1f, free - context.robotRadius - 0.05f);
        }
    }

    /// <summary>Crash: throws from NextGoal after 20 goals.</summary>
    public sealed class CrashAfterAWhile : Wanderer
    {
        public override string Name => "Fault: crash";
        protected override Vec2? Misbehave(IBeliefMap map, Pose2 pose)
        {
            if (goals < 20) return null;
            int[] cells = new int[3];
            return new Vec2(cells[goals], 0f); // IndexOutOfRangeException
        }
    }

    /// <summary>Infinite loop: NextGoal never returns, after 20 goals.</summary>
    public sealed class InfiniteLoop : Wanderer
    {
        public override string Name => "Fault: infinite loop";
        protected override Vec2? Misbehave(IBeliefMap map, Pose2 pose)
        {
            if (goals < 20) return null;
            long spins = 0;
            while (true) spins++;
        }
    }

    /// <summary>Infinite loop at the very start: Initialize never returns.</summary>
    public sealed class HangInInitialize : Wanderer
    {
        public override string Name => "Fault: hang in Initialize";
        public override void Initialize(AlgorithmContext context)
        {
            base.Initialize(context);
            while (true) { }
        }
        protected override Vec2? Misbehave(IBeliefMap map, Pose2 pose) => null;
    }

    /// <summary>Invalid move: a NaN waypoint after 20 goals.</summary>
    public sealed class NaNWaypoint : Wanderer
    {
        public override string Name => "Fault: NaN waypoint";
        protected override Vec2? Misbehave(IBeliefMap map, Pose2 pose) =>
            goals < 20 ? (Vec2?)null : new Vec2(float.NaN, pose.position.y);
    }

    /// <summary>Invalid move: a waypoint 10 km away after 20 goals.</summary>
    public sealed class FarWaypoint : Wanderer
    {
        public override string Name => "Fault: waypoint 10 km away";
        protected override Vec2? Misbehave(IBeliefMap map, Pose2 pose) =>
            goals < 20 ? (Vec2?)null : pose.position + new Vec2(10000f, 0f);
    }

    /// <summary>Invalid move: a sweep with a negative exposure after 20 goals.</summary>
    public sealed class BadDriveRequest : Wanderer
    {
        public override string Name => "Fault: bad drive request";
        protected override Vec2? Misbehave(IBeliefMap map, Pose2 pose)
        {
            if (goals < 20) return null;
            context.drive.Mode = DriveMode.Sweep;
            context.drive.SweepExposureMeters = -1f;
            return null; // the wanderer's goal goes with the bad request
        }
    }

    /// <summary>A healthy control: never fails. Run it in the same batch to see the other trials carry on.</summary>
    public sealed class Healthy : Wanderer
    {
        public override string Name => "Fault: none (control)";
        protected override Vec2? Misbehave(IBeliefMap map, Pose2 pose) => null;
    }
}
