// An example custom algorithm carried to the cloud nodes in a campaign file (see slam-custom.example.json).
// vacuumctl inlines this file into the job; each node compiles it against the algorithm API before any run starts.
// Same rules as the desktop Algorithms folder: C# 9, only RobotVacuum.AlgorithmApi and base .NET, randomness only
// from context.random.

using System;
using RobotVacuum.AlgorithmApi;

namespace CampaignAlgorithms
{
    public sealed class LongStraights : ICleaningAlgorithm
    {
        AlgorithmContext context;
        Vec2? goal;

        public string Name => "Long straights (campaign example)";

        public bool SupportsSlam => true;

        public void Initialize(AlgorithmContext context)
        {
            this.context = context;
            goal = null;
        }

        public Vec2? NextGoal(IBeliefMap map, Pose2 believedPose, GoalStatus lastStatus)
        {
            if (goal.HasValue && lastStatus == GoalStatus.InProgress) return goal;

            // Try a few random headings and take the one with the most free space ahead (at most 4 m).
            Vec2 bestDirection = Pose2.DirectionOf(0f);
            float bestFree = -1f;
            for (int i = 0; i < 6; i++)
            {
                Vec2 direction = Pose2.DirectionOf(context.random.NextFloat(0f, 360f));
                float free = map.FreeDistance(believedPose.position, direction, 4f);
                if (free > bestFree)
                {
                    bestFree = free;
                    bestDirection = direction;
                }
            }

            float length = Math.Max(0f, bestFree - context.robotRadius - 0.02f);
            goal = believedPose.position + bestDirection * length;
            return goal;
        }
    }
}
