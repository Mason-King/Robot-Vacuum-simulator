using System;
using RobotVacuum.AlgorithmApi;

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
    ///
    /// Written against the algorithm API only (no Unity), exactly as a plugin in the Algorithms folder would be.
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
        Vec2? goal;

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

        public Vec2? NextGoal(IBeliefMap view, Pose2 believedPose, GoalStatus lastStatus)
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

        void PickDirection(IBeliefMap view, Vec2 from)
        {
            float bestLength = -1f;
            float bestDirection = 0f;
            Vec2 bestGoal = from;

            for (int draw = 0; draw < MaxDraws; draw++)
            {
                float candidate = context.random.NextFloat(0f, 360f);
                Vec2 target = Project(view, from, candidate, out float length);

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
        Vec2 Project(IBeliefMap view, Vec2 from, float heading, out float length)
        {
            Vec2 forward = Pose2.DirectionOf(heading);
            float free = view.FreeDistance(from, forward, MaxLegMeters);
            float clearance = context.robotRadius + WallMarginMeters;

            length = free >= MaxLegMeters ? MaxLegMeters : Math.Max(0f, free - clearance);

            float step = Math.Max(0.001f, context.cellSizeMeters);
            while (length > 0f && !BodyFits(view, from + forward * length, clearance))
                length = Math.Max(0f, length - step);

            return from + forward * length;
        }

        /// <summary>
        /// True if no cell believed impassable comes within <paramref name="clearance"/> of <paramref name="point"/>,
        /// measured to the cell's edge (a mapped wall may fill its cell), the same rule the path planner uses.
        /// </summary>
        static bool BodyFits(IBeliefMap view, Vec2 point, float clearance)
        {
            Vec2 reach = new Vec2(clearance, clearance);
            if (!view.TryWorldToCell(point - reach, out var low) || !view.TryWorldToCell(point + reach, out var high)) return false;

            float clearanceSq = clearance * clearance;
            float half = view.CellSizeMeters * 0.5f;
            for (int row = low.row - 1; row <= high.row + 1; row++)
                for (int col = low.col - 1; col <= high.col + 1; col++)
                {
                    if (!view.IsBlocked(row, col)) continue;
                    Vec2 centre = view.CellCenter(row, col);
                    float dx = Math.Max(Math.Abs(point.x - centre.x) - half, 0f);
                    float dy = Math.Max(Math.Abs(point.y - centre.y) - half, 0f);
                    if (dx * dx + dy * dy < clearanceSq) return false;
                }
            return true;
        }
    }
}
