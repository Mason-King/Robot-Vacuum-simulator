using System.Collections.Generic;
using UnityEngine;

namespace RobotVacuum.Sim
{
    /// <summary>
    /// The vacuum's body outline (requirement 3.3). Chosen before a run and fixed for it; Round is the default, so
    /// a run that names no shape is the same run it always was.
    /// </summary>
    public enum VacuumShapeKind
    {
        /// <summary>A 34 cm disc: the original body. The default.</summary>
        Round = 0,

        /// <summary>A round back and a square front, 34 × 34 cm, the front corners rounded 2 cm (Neato / Roomba S style).</summary>
        DShaped = 1,

        /// <summary>A 34 cm wide, 40 cm long box, the corners rounded 1.5 cm. Its long axis runs front to back.</summary>
        Rectangular = 2,
    }

    /// <summary>
    /// The geometry of a <see cref="VacuumShapeKind"/>, in the robot's own frame: metres, +y straight ahead, +x to the
    /// right, the origin at the turning centre (the midpoint between the drive wheels, which is also the rigidbody's
    /// centre of mass and where the lidar sits). Every shape is 34 cm wide, so lane spacing, the cleaning width and the
    /// planner's clearance are the same for all three; they differ in length and in how far they reach when turning.
    ///
    /// This is the one place the outline is defined. The colliders (<see cref="VacuumRobot"/>), the true and believed
    /// cleaning footprints, the bump look-ahead and the drawings all read it, so they always agree.
    /// </summary>
    public readonly struct VacuumShape
    {
        /// <summary>Half the body's width for every shape, metres (the original robot's radius).</summary>
        public const float DefaultHalfWidth = 0.17f;

        /// <summary>The D's front corner radius, metres.</summary>
        public const float DCornerRadius = 0.02f;

        /// <summary>The rectangle's length, front to back, metres.</summary>
        public const float RectangleLength = 0.40f;

        /// <summary>The rectangle's corner radius, metres.</summary>
        public const float RectangleCornerRadius = 0.015f;

        public readonly VacuumShapeKind kind;

        /// <summary>Half the width, metres. For Round this is the radius.</summary>
        public readonly float halfWidth;

        /// <summary>Turning centre to the front edge, metres.</summary>
        public readonly float front;

        /// <summary>Turning centre to the back edge, metres.</summary>
        public readonly float back;

        /// <summary>The corner radius of the square parts, metres (Round: the radius).</summary>
        public readonly float corner;

        VacuumShape(VacuumShapeKind kind, float halfWidth, float front, float back, float corner)
        {
            this.kind = kind;
            this.halfWidth = halfWidth;
            this.front = front;
            this.back = back;
            this.corner = corner;
        }

        /// <summary>The shape of <paramref name="kind"/> for a body <paramref name="halfWidth"/> wide on each side.</summary>
        public static VacuumShape For(VacuumShapeKind kind, float halfWidth = DefaultHalfWidth)
        {
            float r = Mathf.Max(0.02f, halfWidth);
            switch (kind)
            {
                case VacuumShapeKind.DShaped:
                    return new VacuumShape(kind, r, r, r, Mathf.Min(DCornerRadius, r * 0.5f));
                case VacuumShapeKind.Rectangular:
                {
                    float half = r * (RectangleLength / (2f * DefaultHalfWidth));
                    return new VacuumShape(kind, r, half, half, Mathf.Min(RectangleCornerRadius, r * 0.5f));
                }
                default:
                    return new VacuumShape(VacuumShapeKind.Round, r, r, r, r);
            }
        }

        public static VacuumShape Round(float radius = DefaultHalfWidth) => For(VacuumShapeKind.Round, radius);

        public bool IsRound => kind == VacuumShapeKind.Round;

        /// <summary>Body width, metres.</summary>
        public float Width => halfWidth * 2f;

        /// <summary>Body length, front to back, metres.</summary>
        public float Length => front + back;

        /// <summary>
        /// The farthest the body reaches from its turning centre: the radius of the circle it sweeps when it turns in
        /// place. Round 17 cm, D about 23 cm, Rectangular about 26 cm.
        /// </summary>
        public float TurnRadius
        {
            get
            {
                switch (kind)
                {
                    case VacuumShapeKind.DShaped:
                    {
                        float inset = halfWidth - corner;
                        return Mathf.Max(back, Mathf.Sqrt(inset * inset + (front - corner) * (front - corner)) + corner);
                    }
                    case VacuumShapeKind.Rectangular:
                    {
                        float x = halfWidth - corner, y = front - corner;
                        return Mathf.Sqrt(x * x + y * y) + corner;
                    }
                    default:
                        return halfWidth;
                }
            }
        }

        /// <summary>Display name, as the Configuration screen shows it.</summary>
        public static string Label(VacuumShapeKind kind)
        {
            switch (kind)
            {
                case VacuumShapeKind.DShaped: return "D-shaped";
                case VacuumShapeKind.Rectangular: return "Rectangular";
                default: return "Round";
            }
        }

        /// <summary>Reads a shape name from a job file: Round, DShaped (or D, D-shaped), Rectangular (or Rectangle), any case. Empty = Round.</summary>
        public static bool TryParse(string text, out VacuumShapeKind kind)
        {
            kind = VacuumShapeKind.Round;
            if (string.IsNullOrWhiteSpace(text)) return true;

            string key = text.Trim().Replace("-", "").Replace("_", "").Replace(" ", "").ToLowerInvariant();
            switch (key)
            {
                case "round": case "circle": case "circular": kind = VacuumShapeKind.Round; return true;
                case "d": case "dshaped": case "dshape": kind = VacuumShapeKind.DShaped; return true;
                case "rectangular": case "rectangle": case "rect": kind = VacuumShapeKind.Rectangular; return true;
                default: return false;
            }
        }

        // ---------------------------------------------------------------- frames

        /// <summary>A world offset from the turning centre, in the robot's frame for a body facing <paramref name="headingDegrees"/> (CCW, 0 = +y).</summary>
        public static Vector2 ToLocal(Vector2 worldOffset, float headingDegrees)
        {
            float radians = headingDegrees * Mathf.Deg2Rad;
            float c = Mathf.Cos(radians), s = Mathf.Sin(radians);
            return new Vector2(worldOffset.x * c + worldOffset.y * s, -worldOffset.x * s + worldOffset.y * c);
        }

        /// <summary>The inverse of <see cref="ToLocal"/>: a point in the robot's frame as a world offset from the turning centre.</summary>
        public static Vector2 ToWorld(Vector2 local, float headingDegrees)
        {
            float radians = headingDegrees * Mathf.Deg2Rad;
            float c = Mathf.Cos(radians), s = Mathf.Sin(radians);
            return new Vector2(local.x * c - local.y * s, local.x * s + local.y * c);
        }

        // ---------------------------------------------------------------- geometry

        /// <summary>
        /// Signed distance from a point in the robot's frame to the body's edge, metres: negative inside, positive
        /// outside. Exact outside the body (and for Round and Rectangular everywhere); for the D it never overstates
        /// how deep inside a point is.
        /// </summary>
        public float SignedDistance(Vector2 local)
        {
            switch (kind)
            {
                case VacuumShapeKind.DShaped:
                {
                    // The union of the full disc and the front box (see FrontBox): exactly the colliders' shape.
                    FrontBox(out Vector2 centre, out Vector2 halfExtents);
                    float disc = local.magnitude - halfWidth;
                    float box = RoundedBox(local - centre, halfExtents, corner);
                    return Mathf.Min(disc, box);
                }
                case VacuumShapeKind.Rectangular:
                    return RoundedBox(local - new Vector2(0f, (front - back) * 0.5f), new Vector2(halfWidth, (front + back) * 0.5f), corner);
                default:
                    return local.magnitude - halfWidth;
            }
        }

        /// <summary>True when the point (robot frame) is on or inside the body.</summary>
        public bool Contains(Vector2 local) => SignedDistance(local) <= 0f;

        /// <summary>
        /// The box that, with its corners rounded by <see cref="corner"/>, forms the body's square part, in the robot's
        /// frame (full half-extents, rounding included).
        /// D: the front half, from just behind the axle line (-corner) to the front edge. Its rounded back corners then
        /// touch the disc at (±halfWidth, 0) and lie inside it everywhere else, so disc ∪ box is exactly the D, with
        /// flat sides from the axle line forward. Rectangular: the whole body. Round: none.
        /// </summary>
        public bool FrontBox(out Vector2 centre, out Vector2 halfExtents)
        {
            switch (kind)
            {
                case VacuumShapeKind.DShaped:
                    centre = new Vector2(0f, (front - corner) * 0.5f);
                    halfExtents = new Vector2(halfWidth, (front + corner) * 0.5f);
                    return true;
                case VacuumShapeKind.Rectangular:
                    centre = new Vector2(0f, (front - back) * 0.5f);
                    halfExtents = new Vector2(halfWidth, (front + back) * 0.5f);
                    return true;
                default:
                    centre = Vector2.zero;
                    halfExtents = Vector2.zero;
                    return false;
            }
        }

        /// <summary>
        /// Distance from the turning centre to the body's edge along a bearing in the robot's frame (degrees CCW from
        /// straight ahead). Round: the radius, exactly.
        /// </summary>
        public float EdgeDistance(float bearingDegrees)
        {
            if (IsRound) return halfWidth;

            float radians = bearingDegrees * Mathf.Deg2Rad;
            var direction = new Vector2(-Mathf.Sin(radians), Mathf.Cos(radians));
            float inside = 0f, outside = TurnRadius + 0.001f;
            for (int i = 0; i < 40; i++)
            {
                float mid = (inside + outside) * 0.5f;
                if (SignedDistance(direction * mid) <= 0f) inside = mid;
                else outside = mid;
            }
            return inside;
        }

        /// <summary>
        /// The outline in the robot's frame, counter-clockwise, for drawing. Every shape is convex and contains the
        /// turning centre, so a triangle fan from the origin fills it.
        /// </summary>
        public List<Vector2> Outline(int segmentsPerQuarter = 8)
        {
            int n = Mathf.Max(2, segmentsPerQuarter);
            var points = new List<Vector2>();
            switch (kind)
            {
                case VacuumShapeKind.DShaped:
                {
                    float cx = halfWidth - corner, cy = front - corner;
                    Arc(points, new Vector2(cx, cy), corner, 0f, 90f, n);      // front right
                    Arc(points, new Vector2(-cx, cy), corner, 90f, 180f, n);   // front left
                    Arc(points, Vector2.zero, halfWidth, 180f, 360f, n * 2);   // the round back, left side to right side
                    break;
                }
                case VacuumShapeKind.Rectangular:
                {
                    float cx = halfWidth - corner, top = front - corner, bottom = -(back - corner);
                    Arc(points, new Vector2(cx, top), corner, 0f, 90f, n);
                    Arc(points, new Vector2(-cx, top), corner, 90f, 180f, n);
                    Arc(points, new Vector2(-cx, bottom), corner, 180f, 270f, n);
                    Arc(points, new Vector2(cx, bottom), corner, 270f, 360f, n);
                    break;
                }
                default:
                    Arc(points, Vector2.zero, halfWidth, 0f, 360f, n * 4, closed: false);
                    break;
            }
            return points;
        }

        /// <summary>Points along an arc, standard maths angles (0 = +x, CCW), both ends included unless <paramref name="closed"/> is false.</summary>
        static void Arc(List<Vector2> points, Vector2 centre, float radius, float fromDegrees, float toDegrees, int segments, bool closed = true)
        {
            int count = closed ? segments + 1 : segments;
            for (int i = 0; i < count; i++)
            {
                float angle = Mathf.Lerp(fromDegrees, toDegrees, i / (float)segments) * Mathf.Deg2Rad;
                points.Add(centre + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * radius);
            }
        }

        /// <summary>Signed distance to a box with these full half-extents whose corners are rounded by <paramref name="radius"/>.</summary>
        static float RoundedBox(Vector2 p, Vector2 halfExtents, float radius)
        {
            float qx = Mathf.Abs(p.x) - (halfExtents.x - radius);
            float qy = Mathf.Abs(p.y) - (halfExtents.y - radius);
            float outside = new Vector2(Mathf.Max(qx, 0f), Mathf.Max(qy, 0f)).magnitude;
            return outside + Mathf.Min(Mathf.Max(qx, qy), 0f) - radius;
        }

        public override string ToString() => $"{Label(kind)} {Width * 100f:0} × {Length * 100f:0} cm";
    }
}
