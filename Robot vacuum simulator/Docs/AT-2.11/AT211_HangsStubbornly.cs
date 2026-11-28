using System;
using System.Collections.Generic;
using RobotVacuum.AlgorithmApi;

// AT-2.11 test algorithm: deliberately broken. Copy into the Algorithms folder to use; never ship it.
// For its first 30 simulated seconds it wanders like the template (short straight legs), then: NextGoal loops forever inside a finally block,
// where a thread can't be aborted. Expected: about 7 s of freeze, then "Algorithm failed": hung, and its thread
// was abandoned. The program stays usable; the abandoned thread uses a CPU core until the program closes. After 3 such
// threads in one session, every run is refused until the program is restarted (the guard rail).

namespace AT211
{
    public sealed class HangsStubbornly : ICleaningAlgorithm
    {
        const float FailAfterSeconds = 30f;
        AlgorithmContext context;
        Vec2? goal;

        public string Name => "AT-2.11 Hangs stubbornly";
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
                try { }
                finally
                {
                    Spin();
                }
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

        // An endless loop with calls in it (so the simulation can stop it).
        static void Spin()
        {
            var list = new List<int>();
            while (true)
            {
                list.Add(1);
                if (list.Count > 1000) list.Clear();
            }
        }
    }
}
