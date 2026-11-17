// A starting point for a new cleaning algorithm.
//
// Copy this file, rename the copy WITHOUT the leading underscore (files starting with '_' are skipped), change the
// namespace, class name and Name, then rescan (Tools > Algorithms > Rescan in the editor).
// See README.md in this folder for the rules.

using System;
using RobotVacuum.AlgorithmApi;

namespace MyAlgorithms
{
    public sealed class StraightLines : ICleaningAlgorithm
    {
        AlgorithmContext context;
        Vec2? goal;

        // Shown in the algorithm list. Must differ from every other algorithm's name.
        public string Name => "Straight lines (template)";

        // True only if this works with no map at the start and a map that may be wrong.
        public bool SupportsSlam => true;

        public void Initialize(AlgorithmContext context)
        {
            this.context = context;
            goal = null;
        }

        public Vec2? NextGoal(IBeliefMap map, Pose2 believedPose, GoalStatus lastStatus)
        {
            // Still driving to the last waypoint: keep it.
            if (goal.HasValue && lastStatus == GoalStatus.InProgress) return goal;

            // Otherwise pick a random direction and go as far as the map says is free (at most 2 m), stopping a
            // body radius plus 2 cm short of anything believed in the way.
            float heading = context.random.NextFloat(0f, 360f);
            Vec2 direction = Pose2.DirectionOf(heading);
            float free = map.FreeDistance(believedPose.position, direction, 2f);
            float length = Math.Max(0f, free - context.robotRadius - 0.02f);

            goal = believedPose.position + direction * length;
            return goal;
        }
    }
}
