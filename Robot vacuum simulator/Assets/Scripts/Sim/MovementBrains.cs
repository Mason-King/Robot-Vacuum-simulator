using System.Collections.Generic;
using RobotVacuum.Level;
using UnityEngine;

namespace RobotVacuum.Sim
{
    /// <summary>How the vacuum chooses where to go. The order matches <see cref="MovementBrain.Labels"/>.</summary>
    public enum MovementPattern { RandomBounce, Spiral, WallFollow, Lawnmower, Picture }

    /// <summary>
    /// What the vacuum does after backing off from a bump: turn, then optionally drive a set distance
    /// and turn again. Angles are degrees, positive to the left.
    /// </summary>
    public readonly struct Manoeuvre
    {
        public readonly float turn;
        public readonly float driveAfter;
        public readonly float turnAfter;

        public Manoeuvre(float turn, float driveAfter = 0f, float turnAfter = 0f)
        {
            this.turn = turn;
            this.driveAfter = driveAfter;
            this.turnAfter = turnAfter;
        }
    }

    /// <summary>
    /// A horizontal strip of a picture, in level metres, that the cleaning controller writes straight into
    /// the coverage grid under the robot. Only the Picture pattern uses it.
    /// </summary>
    public readonly struct PrintStrip
    {
        public readonly PixelPicture picture;
        public readonly Rect canvas;
        public readonly float yMin;
        public readonly float yMax;

        public PrintStrip(PixelPicture picture, Rect canvas, float yMin, float yMax)
        {
            this.picture = picture;
            this.canvas = canvas;
            this.yMin = yMin;
            this.yMax = yMax;
        }
    }

    /// <summary>
    /// A movement algorithm. <see cref="VacuumRobot"/> does the driving, backing off and turning; a brain
    /// only decides how to steer while driving freely and what to do after each bump.
    /// </summary>
    public abstract class MovementBrain
    {
        public static readonly string[] Labels = { "Random", "Spiral", "Wall follow", "Lawnmower", "Picture" };

        public static MovementBrain Create(MovementPattern pattern, PictureKind picture = PictureKind.Heart) => pattern switch
        {
            MovementPattern.Spiral => new SpiralBrain(),
            MovementPattern.WallFollow => new WallFollowBrain(),
            MovementPattern.Lawnmower => new LawnmowerBrain(),
            MovementPattern.Picture => new PictureBrain(picture),
            _ => new RandomBounceBrain(),
        };

        /// <summary>Called when the brain starts driving a robot, and when the robot is reset.</summary>
        public virtual void Reset(VacuumRobot robot) { }

        /// <summary>Degrees per second to rotate while driving forward freely. Positive turns left.</summary>
        public abstract float Steer(VacuumRobot robot, float dt);

        /// <summary>Called when the robot bumps into something; it backs off, then performs the result.</summary>
        public abstract Manoeuvre AfterBump(VacuumRobot robot);

        protected static float RandomTurn(VacuumRobot robot)
        {
            float amount = Random.Range(robot.TurnAngleRange.x, robot.TurnAngleRange.y);
            return Random.value < 0.5f ? -amount : amount;
        }

        /// <summary>A slow drift so straight runs don't retrace identical paths.</summary>
        protected static float Wander(VacuumRobot robot) =>
            (Mathf.PerlinNoise(Time.time * 0.35f, 0f) - 0.5f) * 2f * robot.WanderDegreesPerSecond;
    }

    /// <summary>Straight runs with a slight drift and a random turn at every bump. Simple, thorough eventually, wasteful.</summary>
    public sealed class RandomBounceBrain : MovementBrain
    {
        public override float Steer(VacuumRobot robot, float dt) => Wander(robot);

        public override Manoeuvre AfterBump(VacuumRobot robot) => new Manoeuvre(RandomTurn(robot));
    }

    /// <summary>
    /// Spirals outward from wherever it is, each loop one cleaning width wider than the last. A bump or an
    /// oversized spiral switches to random bouncing for a while, then a fresh spiral starts.
    /// </summary>
    public sealed class SpiralBrain : MovementBrain
    {
        public const float StartRadius = 0.15f;
        public const float MaxRadius = 2.5f;
        public const float BounceSeconds = 10f;

        float radius = StartRadius;
        float bounceTimer;

        public bool Spiralling => bounceTimer <= 0f;

        public override void Reset(VacuumRobot robot)
        {
            radius = StartRadius;
            bounceTimer = 0f;
        }

        public override float Steer(VacuumRobot robot, float dt)
        {
            if (bounceTimer > 0f)
            {
                bounceTimer -= dt;
                if (bounceTimer <= 0f) radius = StartRadius;
                return Wander(robot);
            }

            float angularSpeed = robot.DriveSpeedNow / Mathf.Max(radius, 0.01f);

            // Growing by one cleaning width per full turn makes neighbouring loops just touch.
            radius += robot.CleaningWidth / (2f * Mathf.PI) * angularSpeed * dt;
            if (radius > MaxRadius) bounceTimer = BounceSeconds;

            return angularSpeed * Mathf.Rad2Deg;
        }

        public override Manoeuvre AfterBump(VacuumRobot robot)
        {
            bounceTimer = BounceSeconds;
            return new Manoeuvre(RandomTurn(robot));
        }
    }

    /// <summary>
    /// Drives until it finds a wall, then keeps it on its right, hugging walls and furniture edges all the
    /// way round. Covers perimeters and skirting well; leaves open floor untouched.
    /// </summary>
    public sealed class WallFollowBrain : MovementBrain
    {
        /// <summary>The gap it tries to keep between its edge and the wall, in metres.</summary>
        public const float Gap = 0.05f;

        const float SenseRange = 0.5f;
        const float Gain = 400f;
        const float MaxTurn = 150f;
        const float CornerTurn = 110f;
        const float GiveUpSeconds = 2.5f;

        bool hasWall;
        float lostSeconds;

        public bool HasWall => hasWall;

        public override void Reset(VacuumRobot robot)
        {
            hasWall = false;
            lostSeconds = 0f;
        }

        public override float Steer(VacuumRobot robot, float dt)
        {
            float side = robot.ProbeDistance(robot.Right, SenseRange);
            float diagonal = robot.ProbeDistance((robot.Forward + robot.Right).normalized, SenseRange) * 0.7071f;
            float distance = Mathf.Min(side, diagonal);

            if (float.IsPositiveInfinity(distance))
            {
                if (!hasWall) return 0f; // head out in a straight line until something turns up

                // Lost the wall: curl right round the corner, and give up if nothing comes back.
                lostSeconds += dt;
                if (lostSeconds > GiveUpSeconds) hasWall = false;
                return -CornerTurn;
            }

            hasWall = true;
            lostSeconds = 0f;
            return Steering(distance);
        }

        /// <summary>Proportional steering: too far from the wall turns right towards it, too close turns left away.</summary>
        public static float Steering(float distanceToWall) =>
            Mathf.Clamp((Gap - distanceToWall) * Gain, -MaxTurn, MaxTurn);

        public override Manoeuvre AfterBump(VacuumRobot robot)
        {
            hasWall = true;
            lostSeconds = 0f;
            return new Manoeuvre(60f); // turn left so the wall it hit ends up on its right
        }
    }

    /// <summary>
    /// Back-and-forth lanes: at each end it turns, steps over by slightly less than one cleaning width, and
    /// turns again, alternating sides. Efficient in open rectangular rooms, awkward around clutter.
    /// </summary>
    public sealed class LawnmowerBrain : MovementBrain
    {
        public const float LaneOverlap = 0.9f;

        bool turnLeftNext = true;

        public override void Reset(VacuumRobot robot) => turnLeftNext = true;

        public override float Steer(VacuumRobot robot, float dt) => 0f;

        public override Manoeuvre AfterBump(VacuumRobot robot)
        {
            float turn = turnLeftNext ? 90f : -90f;
            turnLeftNext = !turnLeftNext;
            return new Manoeuvre(turn, robot.CleaningWidth * LaneOverlap, turn);
        }
    }

    /// <summary>
    /// Prints a picture into the coverage heatmap, just for fun. It sweeps the room it starts in row by row
    /// with suction off, and as it passes over the picture the cleaning controller writes each grid cell
    /// straight to the picture's shade. That skips the dirt model entirely, so even a photo comes out at
    /// the grid's full resolution. Unlike the other patterns it plans a route, since it scales to the room.
    /// </summary>
    public sealed class PictureBrain : MovementBrain
    {
        /// <summary>Rows are at most this fraction of the cleaning width apart, like a real lawnmower pass.</summary>
        public const float LaneOverlap = 0.85f;

        const float TurnSpeed = 0.05f;
        const float ArriveDistance = 0.1f;
        const float SteeringGain = 10f;
        const float WallMargin = 0.25f;

        readonly PixelPicture fixedPicture;
        readonly PictureKind kind;
        readonly List<Vector2> waypoints = new List<Vector2>();
        bool planned;
        int next;
        float lane;

        /// <summary>Always draws this picture.</summary>
        public PictureBrain(PixelPicture picture) => fixedPicture = picture;

        /// <summary>Draws a library picture, looked up again whenever the route is planned, so a newly loaded image is used after a reset.</summary>
        public PictureBrain(PictureKind kind) => this.kind = kind;

        /// <summary>The picture being drawn. Set once the route is planned.</summary>
        public PixelPicture Picture { get; private set; }

        /// <summary>Where the picture lands, in level metres. Set once the route is planned.</summary>
        public Rect Canvas { get; private set; }

        /// <summary>Pairs of row starts and ends, top row first, alternating direction.</summary>
        public IReadOnlyList<Vector2> Waypoints => waypoints;

        public bool Finished => planned && next >= waypoints.Count;

        public override void Reset(VacuumRobot robot)
        {
            planned = false;
            next = 0;
            waypoints.Clear();
        }

        public override float Steer(VacuumRobot robot, float dt)
        {
            if (!planned) Plan(robot);

            robot.CleaningLimit = 0f; // no ordinary cleaning: the picture is printed instead

            if (next >= waypoints.Count)
            {
                robot.SpeedScale = 0f;
                robot.Print = null;
                return 0f;
            }

            Vector2 toTarget = waypoints[next] - robot.LevelPosition;
            if (toTarget.magnitude < ArriveDistance)
            {
                next++;
                robot.Print = null;
                return 0f;
            }

            float desired = Mathf.Atan2(-toTarget.x, toTarget.y) * Mathf.Rad2Deg;
            float error = Mathf.DeltaAngle(robot.Heading, desired);

            // Odd waypoints are the far ends of rows, so heading for one prints that row's strip.
            float rowY = waypoints[next].y;
            robot.Print = next % 2 == 1
                ? new PrintStrip(Picture, Canvas, rowY - lane * 0.5f, rowY + lane * 0.5f)
                : (PrintStrip?)null;
            robot.SpeedScale = Mathf.Abs(error) > 20f ? TurnSpeed : 1f;

            return Mathf.Clamp(error * SteeringGain, -360f, 360f);
        }

        public override Manoeuvre AfterBump(VacuumRobot robot)
        {
            // Something is in the way: give up on this point and carry on to the next.
            if (next < waypoints.Count) next++;
            return new Manoeuvre(0f);
        }

        void Plan(VacuumRobot robot)
        {
            planned = true;
            waypoints.Clear();
            next = 0;
            Picture = fixedPicture ?? Pictures.Get(kind);

            var level = robot.Level;
            if (level == null || Picture == null || Picture.Width == 0) return;

            int roomIndex = level.RoomIndexAt(robot.LevelPosition);
            var room = level.GetRoom(roomIndex >= 0 ? roomIndex : 0);
            if (room == null || !room.IsValid) return;

            // As large as the room allows, keeping the footprint clear of the walls.
            var bounds = Poly2D.Bounds(room.outline);
            float margin = robot.Radius + WallMargin;
            float pixel = Mathf.Min((bounds.width - margin * 2f) / Picture.Width, (bounds.height - margin * 2f) / Picture.Height);
            if (pixel <= 0f) return;

            var size = new Vector2(Picture.Width * pixel, Picture.Height * pixel);
            Canvas = new Rect(bounds.center - size * 0.5f, size);

            int rows = Mathf.CeilToInt(size.y / (robot.CleaningWidth * LaneOverlap));
            lane = size.y / rows;

            for (int r = 0; r < rows; r++)
            {
                float y = Canvas.yMax - (r + 0.5f) * lane;
                var left = new Vector2(Canvas.xMin - robot.Radius, y);
                var right = new Vector2(Canvas.xMax + robot.Radius, y);

                waypoints.Add(r % 2 == 0 ? left : right);
                waypoints.Add(r % 2 == 0 ? right : left);
            }
        }
    }
}
