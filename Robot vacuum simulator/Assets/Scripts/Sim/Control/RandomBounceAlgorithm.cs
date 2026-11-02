using UnityEngine;

namespace RobotVacuum.Sim.Control
{
    /// <summary>
    /// The built-in default algorithm, rebuilt as a goal picker (design doc, section 9). It changes direction only
    /// when it meets something: contact, or an obstacle in its believed map. There are no random-distance stops.
    ///
    /// It picks a random direction and projects a waypoint along it to just short of the first cell believed
    /// impassable. Where the belief shows nothing ahead (unexplored space in SLAM), the waypoint is far away, so the
    /// robot drives on until the lidar maps something (the waypoint is re-projected every goal period and pulls in)
    /// or the whisker touches something (Blocked). Reached, Blocked or Unreachable: a new random direction.
    ///
    /// Supports SLAM: it needs no map at the start, and a wrong map only costs it a wasted leg.
    /// </summary>
    public sealed class RandomBounceAlgorithm : ICleaningAlgorithm
    {
        /// <summary>The waypoint stops this far (beyond the body radius) short of a believed obstacle.</summary>
        public const float WallMarginMeters = 0.02f;

        /// <summary>How far a waypoint is projected into space where nothing is believed in the way.</summary>
        public const float MaxLegMeters = 20f;

        /// <summary>A direction offering a shorter leg than this is redrawn (it would end Reached almost at once) ...</summary>
        public const float MinLegMeters = 0.3f;

        /// <summary>... up to this many draws, after which the longest leg drawn is taken.</summary>
        public const int MaxDraws = 8;

        AlgorithmContext context;
        bool hasDirection;
        float direction; // heading, degrees
        Vector2? goal;

        public string Name => "Random";
        public bool SupportsSlam => true;

        /// <summary>The direction of the current leg (degrees, VacuumRobot convention), for tests and the overlay.</summary>
        public float Direction => direction;

        public void Initialize(AlgorithmContext context)
        {
            this.context = context;
            hasDirection = false;
            goal = null;
        }

        public Vector2? NextGoal(InternalModelView view, RobotPose believedPose, GoalStatus lastStatus)
        {
            bool keepLeg = hasDirection && goal.HasValue && lastStatus == GoalStatus.InProgress;

            if (keepLeg)
            {
                // Same direction, re-projected from where the robot is now: if the map has filled in ahead, the
                // waypoint pulls in short of what was found.
                goal = Project(view, believedPose.position, direction, out _);
                return goal;
            }

            PickDirection(view, believedPose.position);
            return goal;
        }

        void PickDirection(InternalModelView view, Vector2 from)
        {
            float bestLength = -1f;
            float bestDirection = 0f;
            Vector2 bestGoal = from;

            for (int draw = 0; draw < MaxDraws; draw++)
            {
                float candidate = context.random.NextFloat(RandomProvider.Subsystem.RandomBounceAlgorithm, 0f, 360f);
                Vector2 target = Project(view, from, candidate, out float length);

                if (length > bestLength)
                {
                    bestLength = length;
                    bestDirection = candidate;
                    bestGoal = target;
                }
                if (length >= MinLegMeters) break;
            }

            direction = bestDirection;
            goal = bestGoal;
            hasDirection = true;
        }

        /// <summary>
        /// The waypoint along <paramref name="heading"/> from <paramref name="from"/>: the free distance through the
        /// belief, less the body radius and a margin, capped at <see cref="MaxLegMeters"/>; then pulled back along the
        /// line, a cell at a time, until the whole body would fit there. (Backing off along the line alone isn't
        /// enough: heading into a wall at a shallow angle, a point 0.19 m back along the line can be a few
        /// centimetres from the wall.)
        /// </summary>
        Vector2 Project(InternalModelView view, Vector2 from, float heading, out float length)
        {
            Vector2 forward = RobotPose.DirectionOf(heading);
            float free = view.FreeDistance(from, forward, MaxLegMeters);
            float clearance = context.robotRadius + WallMarginMeters;

            length = free >= MaxLegMeters ? MaxLegMeters : Mathf.Max(0f, free - clearance);

            float step = Mathf.Max(0.001f, context.cellSizeMeters);
            while (length > 0f && !BodyFits(view, from + forward * length, clearance))
                length = Mathf.Max(0f, length - step);

            return from + forward * length;
        }

        /// <summary>True if no cell believed impassable has its centre within <paramref name="clearance"/> of <paramref name="point"/>.</summary>
        static bool BodyFits(InternalModelView view, Vector2 point, float clearance)
        {
            Vector2 reach = new Vector2(clearance, clearance);
            if (!view.TryWorldToCell(point - reach, out var low) || !view.TryWorldToCell(point + reach, out var high)) return false;

            float clearanceSq = clearance * clearance;
            for (int row = low.row; row <= high.row; row++)
                for (int col = low.col; col <= high.col; col++)
                    if (view.IsBlocked(row, col) && (view.CellCenter(row, col) - point).sqrMagnitude <= clearanceSq)
                        return false;
            return true;
        }
    }
}
