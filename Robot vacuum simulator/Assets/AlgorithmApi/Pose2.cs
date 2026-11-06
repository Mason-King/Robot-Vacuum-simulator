using System;

namespace RobotVacuum.AlgorithmApi
{
    /// <summary>
    /// Where the robot is and which way it faces: world metres, and a heading in degrees, counter-clockwise, with
    /// 0 = facing +y (up the floor plan) and 90 = facing -x.
    /// </summary>
    public readonly struct Pose2
    {
        /// <summary>Degrees to radians: the same single-precision constant as Unity's Mathf.Deg2Rad, so the results match Unity's.</summary>
        public const float Deg2Rad = (float)Math.PI * 2f / 360f;

        /// <summary>Radians to degrees.</summary>
        public const float Rad2Deg = 1f / Deg2Rad;

        public readonly Vec2 position;
        public readonly float heading;

        public Pose2(Vec2 position, float heading)
        {
            this.position = position;
            this.heading = heading;
        }

        /// <summary>The unit vector the robot faces.</summary>
        public Vec2 Forward => DirectionOf(heading);

        /// <summary>Unit vector for a heading (degrees CCW, 0 = +y).</summary>
        public static Vec2 DirectionOf(float headingDegrees)
        {
            float radians = headingDegrees * Deg2Rad;
            return new Vec2(-(float)Math.Sin(radians), (float)Math.Cos(radians));
        }

        /// <summary>The heading that faces along <paramref name="direction"/> (the inverse of <see cref="DirectionOf"/>).</summary>
        public static float HeadingOf(Vec2 direction) => (float)Math.Atan2(-direction.x, direction.y) * Rad2Deg;

        /// <summary>The shortest signed difference from heading <paramref name="from"/> to <paramref name="to"/>, degrees in (-180, 180] (as Unity's Mathf.DeltaAngle).</summary>
        public static float DeltaAngle(float from, float to)
        {
            float delta = to - from;
            delta -= (float)Math.Floor(delta / 360f) * 360f;
            if (delta < 0f) delta = 0f;
            else if (delta > 360f) delta = 360f;
            if (delta > 180f) delta -= 360f;
            return delta;
        }

        /// <summary>Signed degrees to turn (positive = left) to face <paramref name="point"/>.</summary>
        public float BearingTo(Vec2 point) => DeltaAngle(heading, HeadingOf(point - position));

        public override string ToString() => $"{position} facing {heading:F1}°";
    }
}
