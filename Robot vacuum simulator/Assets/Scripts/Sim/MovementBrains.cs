using System.Collections.Generic;
using RobotVacuum.Level;
using UnityEngine;

namespace RobotVacuum.Sim
{
    /// <summary>How the vacuum chooses where to go. The order matches <see cref="MovementBrain.Labels"/>.</summary>
    public enum MovementPattern { RandomBounce, Spiral, WallFollow, Lawnmower, Picture, James }

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
    /// A patch of a picture, in level metres, that the cleaning controller writes straight into the coverage
    /// grid: <see cref="canvas"/> is where the whole picture sits, <see cref="area"/> the part to print now.
    /// The Picture pattern prints a strip along the row it is on; James prints scattered tiles.
    /// </summary>
    public readonly struct PrintPatch
    {
        public readonly PixelPicture picture;
        public readonly Rect canvas;
        public readonly Rect area;

        /// <summary>Above 0, only the round brush of this radius inside <see cref="area"/> is printed.</summary>
        public readonly float radius;

        public PrintPatch(PixelPicture picture, Rect canvas, Rect area, float radius = 0f)
        {
            this.picture = picture;
            this.canvas = canvas;
            this.area = area;
            this.radius = radius;
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
            public System.Func<PictureKind, MovementBrain> create;
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
                    new Entry { label = "Random", create = _ => new RandomBounceBrain() },
                    new Entry { label = "Spiral", create = _ => new SpiralBrain() },
                    new Entry { label = "Wall follow", create = _ => new WallFollowBrain() },
                    new Entry { label = "Lawnmower", create = _ => new LawnmowerBrain() },
                    new Entry { label = "Picture", create = picture => new PictureBrain(picture) },
                    new Entry { label = "James", create = _ => new JamesBrain() },
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

        public static MovementBrain Create(MovementPattern pattern, PictureKind picture) =>
            (IsDefined(pattern) ? Entries[(int)pattern] : Entries[0]).create(picture);

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
                        create = _ => (MovementBrain)System.Activator.CreateInstance(captured),
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

        /// <summary>Clearance kept between a printed picture and the room's walls, in metres.</summary>
        protected const float WallMargin = 0.25f;

        public static MovementBrain Create(MovementPattern pattern, PictureKind picture = PictureKind.Heart) =>
            MovementAlgorithms.Create(pattern, picture);

        /// <summary>Fits a picture, as large as it goes, into the room the robot is standing in.</summary>
        protected static bool TryFitCanvas(VacuumRobot robot, PixelPicture picture, out Rect canvas)
        {
            canvas = default;

            var level = robot.Level;
            if (level == null || picture == null || picture.Width == 0) return false;

            int roomIndex = level.RoomIndexAt(robot.LevelPosition);
            var room = level.GetRoom(roomIndex >= 0 ? roomIndex : 0);
            if (room == null || !room.IsValid) return false;

            var bounds = Poly2D.Bounds(room.outline);
            float margin = robot.Radius + WallMargin;
            float pixel = Mathf.Min((bounds.width - margin * 2f) / picture.Width, (bounds.height - margin * 2f) / picture.Height);
            if (pixel <= 0f) return false;

            var size = new Vector2(picture.Width * pixel, picture.Height * pixel);
            canvas = new Rect(bounds.center - size * 0.5f, size);
            return true;
        }

        /// <summary>Called when the brain starts driving a robot, and when the robot is reset.</summary>
        public virtual void Reset(VacuumRobot robot) { }

        /// <summary>Degrees per second to rotate while driving forward freely. Positive turns left.</summary>
        public abstract float Steer(VacuumRobot robot, float dt);

        /// <summary>Called when the robot bumps into something; it backs off, then performs the result.</summary>
        public abstract Manoeuvre AfterBump(VacuumRobot robot);

        /// <summary>Randomness for algorithms. Always use this, never UnityEngine.Random, so runs replay from their seed.</summary>
        protected static float RandomValue => SimRandom.Value;

        protected static float RandomRange(float min, float max) => SimRandom.Range(min, max);

        protected static float RandomTurn(VacuumRobot robot)
        {
            float amount = SimRandom.Range(robot.TurnAngleRange.x, robot.TurnAngleRange.y);
            return SimRandom.Value < 0.5f ? -amount : amount;
        }

        /// <summary>A slow drift so straight runs don't retrace identical paths. Follows simulated time, not the clock.</summary>
        protected static float Wander(VacuumRobot robot) =>
            (Mathf.PerlinNoise(robot.SimTime * 0.35f, robot.WanderPhase) - 0.5f) * 2f * robot.WanderDegreesPerSecond;
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
            float x = robot.LevelPosition.x;
            robot.Print = next % 2 == 1
                ? new PrintPatch(Picture, Canvas,
                    Rect.MinMaxRect(x - robot.Radius, rowY - lane * 0.5f, x + robot.Radius, rowY + lane * 0.5f))
                : (PrintPatch?)null;
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
            if (!TryFitCanvas(robot, Picture, out Rect canvas)) return;

            Canvas = canvas;
            int rows = Mathf.CeilToInt(canvas.height / (robot.CleaningWidth * LaneOverlap));
            lane = canvas.height / rows;

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

    /// <summary>
    /// James. Wanders at random like the bouncer, suction off, uncovering one particular photo from the app's
    /// files wherever it goes: the floor under its brush turns into that part of the picture. The photo comes
    /// in along the trail it happens to drive, so it fills in slowly and never in any tidy order.
    /// </summary>
    public sealed class JamesBrain : MovementBrain
    {
        /// <summary>File name, without extension, looked for in <see cref="Pictures.SearchFolders"/>.</summary>
        public const string PictureName = "james";

        readonly PixelPicture fixedPicture;
        bool planned;

        public JamesBrain() { }

        /// <summary>Uncovers this picture instead of looking for James's photo.</summary>
        public JamesBrain(PixelPicture picture) => fixedPicture = picture;

        public PixelPicture Picture { get; private set; }

        /// <summary>Where the photo sits in the room. Empty when it wouldn't fit.</summary>
        public Rect Canvas { get; private set; }

        public override void Reset(VacuumRobot robot) => planned = false;

        public override float Steer(VacuumRobot robot, float dt)
        {
            if (!planned) Plan(robot);

            robot.CleaningLimit = 0f; // uncovering the photo, not cleaning
            robot.Print = null;

            if (Picture != null && Canvas.width > 0f)
            {
                // A round brush the width of the vacuum, so the photo appears along the trail it drives.
                float radius = robot.CleaningWidth * 0.5f;
                Vector2 position = robot.LevelPosition;
                var brush = Rect.MinMaxRect(position.x - radius, position.y - radius, position.x + radius, position.y + radius);

                if (Canvas.Overlaps(brush)) robot.Print = new PrintPatch(Picture, Canvas, brush, radius);
            }

            return Wander(robot);
        }

        public override Manoeuvre AfterBump(VacuumRobot robot) => new Manoeuvre(RandomTurn(robot));

        void Plan(VacuumRobot robot)
        {
            planned = true;

            Picture = fixedPicture ?? Pictures.LoadNamed(PictureName);
            if (Picture == null)
            {
                Debug.LogWarning($"James has no photo yet: put {PictureName}.png in " +
                                 $"{string.Join(" or ", Pictures.SearchFolders)}. Uncovering the smiley instead.");
                Picture = Pictures.Get(PictureKind.Smiley);
            }

            Canvas = TryFitCanvas(robot, Picture, out Rect canvas) ? canvas : default;
        }
    }
}
