using System;
using System.Collections.Generic;
using RobotVacuum.AlgorithmApi;

// AT-2.11 test algorithm: deliberately broken. Copy into the Algorithms folder to use; never ship it.
// Initialize throws at once.
// Expected: the run stops at 0 s with "Algorithm failed" in Initialize; the robot never moves.

namespace AT211
{
    public sealed class ThrowsInInitialize : ICleaningAlgorithm
    {
        const float FailAfterSeconds = 0f; // (never reached: Initialize throws)
        AlgorithmContext context;
        Vec2? goal;

        public string Name => "AT-2.11 Throws in Initialize";
        public bool SupportsSlam => true;

        public void Initialize(AlgorithmContext context)
        {
            if (context.robotRadius >= 0f) throw new InvalidOperationException("Deliberate AT-2.11 test failure in Initialize.");
            this.context = context;
            goal = null;
        }

        public Vec2? NextGoal(IBeliefMap map, Pose2 believedPose, GoalStatus lastStatus)
        {
            if (context.SimSeconds >= FailAfterSeconds)
            {
                return null;
            }
            return Wander(map, believedPose, lastStatus);
        }

        Vec2? Wander(IBeliefMap map, Pose2 believedPose, GoalStatus lastStatus)
        {
            if (goal.HasValue && lastStatus == GoalStatus.InProgress) return goal;
            Vec2 direction = Pose2.DirectionOf(context.random.NextFloat(0f, 360f));
            float length = map.FreeDistance(believedPose.position, direction, 2f);
            Vec2 target = believedPose.position + direction * length;
            while (length > 0f && map.ObstacleDistance(target) < map.BodyClearanceMeters)
            {
                length = Math.Max(0f, length - map.CellSizeMeters);
                target = believedPose.position + direction * length;
            }
            goal = target;
            return goal;
        }

    }
}
