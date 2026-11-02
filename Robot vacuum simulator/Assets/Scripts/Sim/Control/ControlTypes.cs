using UnityEngine;

namespace RobotVacuum.Sim.Control
{
    /// <summary>A cell on the shared lattice (the same indices in the Internal and External Models). May be negative.</summary>
    public readonly struct CellIndex
    {
        public readonly int row;
        public readonly int col;

        public CellIndex(int row, int col)
        {
            this.row = row;
            this.col = col;
        }

        /// <summary>One number per cell, for hash sets and dictionaries.</summary>
        public long Key => Pack(row, col);

        public static long Pack(int row, int col) => ((long)row << 32) | (uint)col;

        public override string ToString() => $"({row}, {col})";
    }

    /// <summary>
    /// Where the robot is and which way it faces, in world metres and degrees (counter-clockwise, 0 = +y, the
    /// VacuumRobot convention). Named RobotPose, not Pose, because UnityEngine already has a Pose.
    /// </summary>
    public readonly struct RobotPose
    {
        public readonly Vector2 position;
        public readonly float heading;

        public RobotPose(Vector2 position, float heading)
        {
            this.position = position;
            this.heading = heading;
        }

        public Vector2 Forward => DirectionOf(heading);

        /// <summary>Unit vector for a heading (degrees CCW, 0 = +y).</summary>
        public static Vector2 DirectionOf(float headingDegrees)
        {
            float radians = headingDegrees * Mathf.Deg2Rad;
            return new Vector2(-Mathf.Sin(radians), Mathf.Cos(radians));
        }

        /// <summary>The heading that faces along <paramref name="direction"/> (the inverse of <see cref="DirectionOf"/>).</summary>
        public static float HeadingOf(Vector2 direction) => Mathf.Atan2(-direction.x, direction.y) * Mathf.Rad2Deg;

        /// <summary>Signed degrees to turn (positive = left) to face <paramref name="point"/>.</summary>
        public float BearingTo(Vector2 point) => Mathf.DeltaAngle(heading, HeadingOf(point - position));
    }

    /// <summary>How the controller is getting on with the current waypoint. Reported to the algorithm when it is next asked.</summary>
    public enum GoalStatus
    {
        /// <summary>No waypoint yet (the first question of a run), or the algorithm returned none.</summary>
        None,

        /// <summary>The robot's centre came within the arrival tolerance.</summary>
        Reached,

        /// <summary>Still driving toward it.</summary>
        InProgress,

        /// <summary>Contact ahead, a believed obstacle ahead, or no progress within the blocked timeout.</summary>
        Blocked,

        /// <summary>The waypoint lies in a cell believed impassable (0).</summary>
        Unreachable,
    }
}
