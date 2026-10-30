using System.Collections.Generic;
using RobotVacuum.Level;
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
    /// The contract every movement algorithm meets. The engine only ever talks to an algorithm through these
    /// three calls (wrapped by <see cref="MovementBrainExecution"/>, which isolates failures), so algorithms
    /// never touch engine internals.
    /// </summary>
    public interface IMovementAlgorithm
    {
        /// <summary>Called when the algorithm starts driving a robot, and when the robot is reset.</summary>
        void Reset(VacuumRobot robot);

        /// <summary>Degrees per second to rotate while driving forward freely. Positive turns left.</summary>
        float Steer(VacuumRobot robot, float dt);

        /// <summary>Called when the robot bumps into something; it backs off, then performs the result.</summary>
        Manoeuvre AfterBump(VacuumRobot robot);
    }

    /// <summary>
    /// Registers a <see cref="MovementBrain"/> subclass as a selectable algorithm under <see cref="Label"/>.
    /// The class needs a public parameterless constructor. Nothing else in the project has to change: the
    /// algorithm appears in every algorithm picker, runs in the simulator and shows up in the analysis.
    /// </summary>
    [System.AttributeUsage(System.AttributeTargets.Class, Inherited = false)]
    public sealed class MovementAlgorithmAttribute : System.Attribute
    {
        public MovementAlgorithmAttribute(string label) => Label = label;

        public string Label { get; }
    }

    /// <summary>
    /// Every available algorithm: the built-in ones in <see cref="MovementPattern"/> order, then any class
    /// marked <see cref="MovementAlgorithmAttribute"/>, numbered after them in name order.
    /// </summary>
    public static class MovementAlgorithms
    {
        sealed class Entry
        {
            public string label;
            public System.Func<MovementBrain> create;
        }

        static List<Entry> entries;
        static string[] labels;

        static List<Entry> Entries
        {
            get
            {
                if (entries != null) return entries;

                entries = new List<Entry>
                {
                    new Entry { label = "Random", create = () => new RandomBounceBrain() },
                    new Entry { label = "Spiral", create = () => new SpiralBrain() },
                    new Entry { label = "Wall follow", create = () => new WallFollowBrain() },
                    new Entry { label = "Lawnmower", create = () => new LawnmowerBrain() },
                };
                entries.AddRange(DiscoverPlugins());
                labels = entries.ConvertAll(entry => entry.label).ToArray();
                return entries;
            }
        }

        public static int Count => Entries.Count;

        public static string[] Labels
        {
            get
            {
                _ = Entries;
                return labels;
            }
        }

        public static bool IsDefined(MovementPattern pattern) => (int)pattern >= 0 && (int)pattern < Count;

        public static string LabelOf(MovementPattern pattern) => IsDefined(pattern) ? Labels[(int)pattern] : $"Algorithm {(int)pattern}";

        public static MovementBrain Create(MovementPattern pattern) =>
            (IsDefined(pattern) ? Entries[(int)pattern] : Entries[0]).create();

        static IEnumerable<Entry> DiscoverPlugins()
        {
            var found = new List<(string name, Entry entry)>();
            foreach (var assembly in System.AppDomain.CurrentDomain.GetAssemblies())
            {
                System.Type[] types;
                try { types = assembly.GetTypes(); }
                catch (System.Reflection.ReflectionTypeLoadException exception) { types = exception.Types; }

                foreach (var type in types)
                {
                    if (type == null || type.IsAbstract || !typeof(MovementBrain).IsAssignableFrom(type)) continue;

                    var attribute = (MovementAlgorithmAttribute)System.Attribute.GetCustomAttribute(type, typeof(MovementAlgorithmAttribute));
                    if (attribute == null || type.GetConstructor(System.Type.EmptyTypes) == null) continue;

                    var captured = type;
                    found.Add((type.FullName, new Entry
                    {
                        label = string.IsNullOrWhiteSpace(attribute.Label) ? type.Name : attribute.Label,
                        create = () => (MovementBrain)System.Activator.CreateInstance(captured),
                    }));
                }
            }

            found.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
            return found.ConvertAll(item => item.entry);
        }
    }

    /// <summary>
    /// A movement algorithm. <see cref="VacuumRobot"/> does the driving, backing off and turning; a brain
    /// only decides how to steer while driving freely and what to do after each bump. To add one, subclass
    /// this and mark it <see cref="MovementAlgorithmAttribute"/>.
    /// </summary>
    public abstract class MovementBrain : IMovementAlgorithm
    {
        /// <summary>Every algorithm's display name, indexed by <see cref="MovementPattern"/>.</summary>
        public static string[] Labels => MovementAlgorithms.Labels;

        /// <summary>
        /// Where every random choice a brain makes comes from. Shared with the rest of the run (assigned by
        /// <see cref="AssignRandomSource"/>, not owned here) so the same seed always steers the same way --
        /// and, since it's the SAME instance rather than a fresh one built from the same seed, its position
        /// within the sequence can be snapshotted and resumed mid-run, not just restarted from the top.
        /// </summary>
        protected RandomProvider RandomSource { get; private set; }

        /// <summary>Offset into the wander noise, so runs with different seeds drift apart from the first step.</summary>
        float wanderPhase;

        public static MovementBrain Create(MovementPattern pattern, RandomProvider randomSource)
        {
            MovementBrain brain = MovementAlgorithms.Create(pattern);
            brain.AssignRandomSource(randomSource);
            return brain;
        }

        /// <summary>Points this brain at the run's shared random source and restarts its wander drift.</summary>
        public void AssignRandomSource(RandomProvider randomSource)
        {
            RandomSource = randomSource;
            wanderPhase = (float)RandomSource.NextDouble(RandomProvider.Subsystem.RandomBounceAlgorithm) * 1000f;
        }

        /// <summary>Called when the brain starts driving a robot, and when the robot is reset.</summary>
        public virtual void Reset(VacuumRobot robot) { }

        /// <summary>Degrees per second to rotate while driving forward freely. Positive turns left.</summary>
        public abstract float Steer(VacuumRobot robot, float dt);

        /// <summary>Called when the robot bumps into something; it backs off, then performs the result.</summary>
        public abstract Manoeuvre AfterBump(VacuumRobot robot);

        /// <summary>Randomness for algorithms. Always use these, never UnityEngine.Random, so runs replay from their seed.</summary>
        protected float RandomValue => (float)RandomSource.NextDouble(RandomProvider.Subsystem.RandomBounceAlgorithm);

        protected float RandomRange(float min, float max) =>
            RandomSource.NextFloat(RandomProvider.Subsystem.RandomBounceAlgorithm, min, max);

        protected float RandomTurn(VacuumRobot robot)
        {
            float amount = RandomSource.NextFloat(RandomProvider.Subsystem.RandomBounceAlgorithm, robot.TurnAngleRange.x, robot.TurnAngleRange.y);
            return RandomSource.NextDouble(RandomProvider.Subsystem.RandomBounceAlgorithm) < 0.5 ? -amount : amount;
        }

        /// <summary>
        /// A slow drift so straight runs don't retrace identical paths. It reads the robot's own clock, not
        /// <see cref="Time.time"/>, which counts from app start and is scaled: a headless run at 25× would
        /// otherwise sample completely different noise from a watched one.
        /// </summary>
        protected float Wander(VacuumRobot robot) =>
            (Mathf.PerlinNoise(wanderPhase + robot.SimTime * 0.35f, 0f) - 0.5f) * 2f * robot.WanderDegreesPerSecond;
    }

    /// <summary>
    /// Calls into an algorithm on the engine's behalf and contains whatever goes wrong: an exception, an
    /// invalid result, or a call that takes far too long, all become a failure message instead of taking the
    /// session down. (A call that never returns can't be pre-empted on Unity's main thread; the robot's
    /// no-progress watchdog catches algorithms that loop without getting anywhere.)
    /// </summary>
    public static class MovementBrainExecution
    {
        const double MaxCallMilliseconds = 250.0;
        const float MaxSteeringDegreesPerSecond = 360f;
        const float MaxTurnDegrees = 360f;
        const float MaxDriveDistance = 100f;

        public static bool TryReset(MovementBrain brain, VacuumRobot robot, out string failure)
        {
            failure = null;
            try
            {
                brain.Reset(robot);
                return true;
            }
            catch (System.Exception exception)
            {
                failure = $"{exception.GetType().Name}: {exception.Message}";
                return false;
            }
        }

        public static bool TrySteer(MovementBrain brain, VacuumRobot robot, float dt, out float steering, out string failure)
        {
            steering = 0f;
            failure = null;

            try
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                steering = brain.Steer(robot, dt);
                if (TooSlow(clock, out failure)) return false;
                if (IsFinite(steering) && Mathf.Abs(steering) <= MaxSteeringDegreesPerSecond) return true;
                failure = $"Invalid steering output: {steering}.";
            }
            catch (System.Exception exception)
            {
                failure = $"{exception.GetType().Name}: {exception.Message}";
            }

            return false;
        }

        public static bool TryAfterBump(MovementBrain brain, VacuumRobot robot, out Manoeuvre manoeuvre, out string failure)
        {
            manoeuvre = default;
            failure = null;

            try
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                manoeuvre = brain.AfterBump(robot);
                if (TooSlow(clock, out failure)) return false;
                if (IsFinite(manoeuvre.turn) && Mathf.Abs(manoeuvre.turn) <= MaxTurnDegrees &&
                    IsFinite(manoeuvre.driveAfter) && manoeuvre.driveAfter >= 0f && manoeuvre.driveAfter <= MaxDriveDistance &&
                    IsFinite(manoeuvre.turnAfter) && Mathf.Abs(manoeuvre.turnAfter) <= MaxTurnDegrees) return true;
                failure = "Invalid manoeuvre output.";
            }
            catch (System.Exception exception)
            {
                failure = $"{exception.GetType().Name}: {exception.Message}";
            }

            return false;
        }

        static bool TooSlow(System.Diagnostics.Stopwatch clock, out string failure)
        {
            failure = clock.Elapsed.TotalMilliseconds > MaxCallMilliseconds
                ? $"A single call took {clock.Elapsed.TotalMilliseconds:0} ms (limit {MaxCallMilliseconds:0} ms)."
                : null;
            return failure != null;
        }

        static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
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
