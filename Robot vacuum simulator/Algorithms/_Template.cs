// A starting point for a new cleaning algorithm.
//
// Copy this file, rename the copy WITHOUT the leading underscore (files starting with '_' are skipped), change the
// namespace, class name and Name, then rescan (Tools > Algorithms > Rescan in the editor, or the Rescan button beside
// the Algorithm dropdown). See README.md in this folder for the rules.

using System;
using RobotVacuum.AlgorithmApi;

namespace MyAlgorithms
{
    public sealed class StraightLines : ICleaningAlgorithm
    {
        const float MaxLegMeters = 2f;

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
            // Still driving to the last waypoint: keep it. (The controller plans the route there and steers round
            // anything in the way; it reports Blocked or Unreachable if it can't get there.)
            if (goal.HasValue && lastStatus == GoalStatus.InProgress) return goal;

            // Otherwise: a random direction, as far as the map says is free (at most 2 m) ...
            float heading = context.random.NextFloat(0f, 360f);
            Vec2 direction = Pose2.DirectionOf(heading);
            float length = map.FreeDistance(believedPose.position, direction, MaxLegMeters);

            // ... then pulled back until the whole body fits there. Approaching a wall at a shallow angle, a point
            // short of the wall along the line can still be right beside it, so check the point, not the line.
            float step = map.CellSizeMeters;
            Vec2 target = believedPose.position + direction * length;
            while (length > 0f && map.ObstacleDistance(target) < map.BodyClearanceMeters)
            {
                length = Math.Max(0f, length - step);
                target = believedPose.position + direction * length;
            }

            goal = target;
            return goal;
        }
    }
}
