using System;

namespace RobotVacuum.AlgorithmApi
{
    /// <summary>
    /// A point or direction on the floor, in world metres (x right, y up). The API's own type, so an algorithm needs
    /// nothing from Unity. The arithmetic is plain single-precision, the same as Unity's Vector2.
    /// </summary>
    public readonly struct Vec2 : IEquatable<Vec2>
    {
        public readonly float x;
        public readonly float y;

        public Vec2(float x, float y)
        {
            this.x = x;
            this.y = y;
        }

        public static Vec2 Zero => new Vec2(0f, 0f);

        public float LengthSquared => x * x + y * y;
        public float Length => (float)Math.Sqrt(x * x + y * y);

        /// <summary>This direction with length 1, or <see cref="Zero"/> for a (near) zero vector.</summary>
        public Vec2 Normalized
        {
            get
            {
                float length = Length;
                return length > 1e-5f ? new Vec2(x / length, y / length) : Zero;
            }
        }

        public static float Dot(Vec2 a, Vec2 b) => a.x * b.x + a.y * b.y;

        /// <summary>The 2D cross product (z of the 3D one): positive when <paramref name="b"/> is counter-clockwise of <paramref name="a"/>.</summary>
        public static float Cross(Vec2 a, Vec2 b) => a.x * b.y - a.y * b.x;

        public static float Distance(Vec2 a, Vec2 b) => (a - b).Length;

        public static Vec2 Lerp(Vec2 a, Vec2 b, float t) => new Vec2(a.x + (b.x - a.x) * t, a.y + (b.y - a.y) * t);

        public static Vec2 operator +(Vec2 a, Vec2 b) => new Vec2(a.x + b.x, a.y + b.y);
        public static Vec2 operator -(Vec2 a, Vec2 b) => new Vec2(a.x - b.x, a.y - b.y);
        public static Vec2 operator -(Vec2 a) => new Vec2(-a.x, -a.y);
        public static Vec2 operator *(Vec2 a, float d) => new Vec2(a.x * d, a.y * d);
        public static Vec2 operator *(float d, Vec2 a) => new Vec2(a.x * d, a.y * d);
        public static Vec2 operator /(Vec2 a, float d) => new Vec2(a.x / d, a.y / d);

        public static bool operator ==(Vec2 a, Vec2 b) => a.x == b.x && a.y == b.y;
        public static bool operator !=(Vec2 a, Vec2 b) => !(a == b);

        public bool Equals(Vec2 other) => x.Equals(other.x) && y.Equals(other.y);
        public override bool Equals(object obj) => obj is Vec2 other && Equals(other);
        public override int GetHashCode() => (x.GetHashCode() * 397) ^ y.GetHashCode();
        public override string ToString() => $"({x:F2}, {y:F2})";
    }
}
