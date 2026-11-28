using System;
using System.Collections.Generic;
using RobotVacuum.AlgorithmApi;

// AT-2.11 test algorithm: deliberately broken. Copy into the Algorithms folder to use; never ship it.
// For its first 30 simulated seconds it wanders like the template (short straight legs), then: NextGoal throws an InvalidOperationException.
// Expected: the run stops with "Algorithm failed" (AlgorithmFault), the exception named, the program still usable.

namespace AT211
{
    public sealed class ThrowsInNextGoal : ICleaningAlgorithm
    {
        const float FailAfterSeconds = 30f;
        AlgorithmContext context;
        Vec2? goal;

        public string Name => "AT-2.11 Throws in NextGoal";
        public bool SupportsSlam => true;

        public void Initialize(AlgorithmContext context)
        {

            this.context = context;
            goal = null;
        }

        public Vec2? NextGoal(IBeliefMap map, Pose2 believedPose, GoalStatus lastStatus)
        {
            if (context.SimSeconds >= FailAfterSeconds)
            {
                throw new InvalidOperationException("Deliberate AT-2.11 test failure.");
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
