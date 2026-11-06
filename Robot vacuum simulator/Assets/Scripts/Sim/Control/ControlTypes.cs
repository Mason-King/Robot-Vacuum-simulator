using UnityEngine;

namespace RobotVacuum.Sim.Control
{
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
}
