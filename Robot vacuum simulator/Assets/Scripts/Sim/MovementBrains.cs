using UnityEngine;

namespace RobotVacuum.Sim
{
    /// <summary>How the vacuum chooses where to go. The order matches <see cref="MovementBrain.Labels"/>.</summary>
    public enum MovementPattern { RandomBounce, Spiral, WallFollow, Lawnmower }

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
    /// A movement algorithm. <see cref="VacuumRobot"/> does the driving, backing off and turning; a brain
    /// only decides how to steer while driving freely and what to do after each bump.
    /// </summary>
    public abstract class MovementBrain
    {
        public static readonly string[] Labels = { "Random", "Spiral", "Wall follow", "Lawnmower" };

        public static MovementBrain Create(MovementPattern pattern) => pattern switch
        {
            MovementPattern.Spiral => new SpiralBrain(),
            MovementPattern.WallFollow => new WallFollowBrain(),
            MovementPattern.Lawnmower => new LawnmowerBrain(),
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
}
