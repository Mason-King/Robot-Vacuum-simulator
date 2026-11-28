using System.Collections.Generic;
using RobotVacuum.Level;
using UnityEngine;

namespace RobotVacuum.Sim
{
    /// <summary>
    /// A cat that wanders the floor plan. It behaves like a cat (wanders along walls, sits, naps, watches the
    /// vacuum, bolts when it comes close) but, like a real cat that has lived with one for a while, it doesn't
    /// keep getting in its way:
    ///  - it watches the vacuum from a distance instead of walking up to it;
    ///  - when the vacuum drives at it, it steps aside, to the side it is already on, and when startled it bolts
    ///    sideways out of the vacuum's path rather than straight ahead of it;
    ///  - after a scare it stays wary for a while: no watching, and it gives the vacuum's path a wider berth;
    ///  - it only stops (sits, naps, watches) where it isn't in the way: not in a doorway, a narrow hallway or on
    ///    the dock. Naps are along walls and in corners.
    /// Every random choice comes from the run's DynamicObstacles stream, so a seed's cats are the same every run.
    /// </summary>
    [RequireComponent(typeof(Rigidbody2D))]
    [RequireComponent(typeof(PolygonCollider2D))]
    public sealed class MovingCat : MonoBehaviour
    {
        const float Speed = 0.45f;
        const float InterestDistance = 1.7f;
        const float StartleDistance = 0.42f;
        const float TurnSpeed = 150f;

        /// <summary>
        /// Roaming: walking somewhere. Investigating: watching the vacuum from a distance (creeping a little nearer
        /// at most). Startled: bolting out of its way. Resting: sitting a moment. Napping: lying down for a while,
        /// along a wall. Yielding: stepping aside out of the vacuum's path.
        /// </summary>
        public enum Behavior { Roaming, Investigating, Startled, Resting, Napping, Yielding }

        // Fastest the cat's body swings round to face where it is walking, in degrees per second.
        const float FacingTurnSpeed = 360f;

        // The floor fence: how far ahead the cat checks it is still over the floor plan, metres, and how many
        // tries it gets to pick a roaming target that is on the floor.
        const float FenceLookAhead = 0.3f;
        const int TargetTries = 8;

        // Watching: closest it creeps to the vacuum, metres; how long it stays interested, then how long it
        // ignores the vacuum afterwards, seconds.
        const float WatchStandoff = 1.1f;
        const float WatchMinSeconds = 4f, WatchMaxSeconds = 8f;
        const float BoredMinSeconds = 10f, BoredMaxSeconds = 20f;

        // The vacuum's path: the strip ahead of it, this long and this wide either side of its centre line,
        // metres. A cat inside it steps aside (wary: a longer strip). The vacuum must be moving faster than
        // PathMinSpeed, m/s. YieldClearance: how far past the strip's edge the cat steps.
        const float PathLookAhead = 1.3f;
        const float WaryPathLookAhead = 2f;
        const float NappingPathLookAhead = 0.8f;
        const float PathHalfWidth = 0.45f;
        const float PathMinSpeed = 0.05f;
        const float YieldClearance = 0.3f;
        const float YieldMaxSeconds = 2.5f;

        // After a startle, wary for this long, seconds.
        const float WaryMinSeconds = 20f, WaryMaxSeconds = 40f;

        // Where the cat won't stop: within this of a doorway's edge, of the dock, or where walls on opposite sides
        // are both this close (a hallway), metres. NearWall: close enough to a wall to count as "along a wall".
        const float DoorwayKeepOut = 0.45f;
        const float DockKeepOut = 0.8f;
        const float NarrowWall = 0.55f;
        const float NearWall = 0.45f;

        // Chance, on arriving somewhere it may stop, to nap (if along a wall), else to sit a moment, else walk on.
        const float NapChance = 0.3f;
        const float SitChance = 0.6f;
        const float NapMinSeconds = 8f, NapMaxSeconds = 25f;
        const float SitMinSeconds = 1f, SitMaxSeconds = 3f;
        // Chance a roaming trip goes into a neighbouring room through a doorway.
        const float OtherRoomChance = 0.35f;
        // Roaming targets are this far in from a wall, metres (cats walk along the edges of a room).
        const float WallInsetMin = 0.22f, WallInsetMax = 0.35f;

        Rigidbody2D body;
        Vector2 spawnPosition;
        Vector2 direction;
        Vector2 target;
        Vector2? afterWaypoint; // walking to a doorway first: where to go once through it
        Rect bounds;
        float behaviorTimer;
        float startleTimer;
        float waryUntil;        // sim time (the cat's own clock) until which it is wary of the vacuum
        float boredUntil;       // ... until which it ignores the vacuum (lost interest)
        float clock;            // seconds stepped since Initialize
        // The run's shared random source. Every random choice a cat makes is drawn from its DynamicObstacles
        // stream, so the cats' state is part of RandomProvider.SnapshotState like everything else's.
        RandomProvider randomSource;
        bool initialized;
        bool herded;
        VacuumRobot vacuum;
        LevelRenderer floor;
        Vector2 lastOnFloor;
        Mesh[] catMeshes;
        Material[] catMaterials;

        public Vector2 Direction => direction;

        public float MoveSpeed => CurrentBehavior switch
        {
            Behavior.Startled => Speed * 1.7f,
            Behavior.Yielding => Speed * 1.1f,
            Behavior.Investigating => Speed * 0.4f,
            _ => Speed * 0.8f,
        };
        public Behavior CurrentBehavior { get; private set; }

        /// <summary>True while the cat, after a scare, keeps clear of the vacuum.</summary>
        public bool Wary => clock < waryUntil;

        /// <summary>True when the cat is fenced in to the floor plan (placed cats are; see <see cref="ConfineTo"/>).</summary>
        public bool Confined => floor != null && floor.Level != null;

        /// <summary>
        /// Keeps the cat on <paramref name="renderer"/>'s floor plan: it turns back before stepping off the floor
        /// (an open wall, a gap to the outside), only roams towards points on the floor, and is put back where it
        /// last stood on the floor if something (the vacuum) shoves it off. Also what tells it where doorways,
        /// walls and the dock are.
        /// </summary>
        public void ConfineTo(LevelRenderer renderer)
        {
            floor = renderer;
            if (body != null) lastOnFloor = body.position;
        }

        /// <summary>Whether <paramref name="world"/> is over the floor plan (a room or a wall), or the cat isn't fenced.</summary>
        public bool IsOnFloor(Vector2 world) =>
            !Confined || !LevelGeometry.IsOffFloor(floor.Level, floor.WorldToLevel(world));
        public PolygonCollider2D CollisionShape { get; private set; }

        void Awake()
        {
            Configure();
        }

        void Configure()
        {
            body = GetComponent<Rigidbody2D>();
            CollisionShape = GetComponent<PolygonCollider2D>();

            body.bodyType = RigidbodyType2D.Dynamic;
            body.gravityScale = 0f;
            body.freezeRotation = true;
            body.collisionDetectionMode = CollisionDetectionMode2D.Continuous;
            body.interpolation = RigidbodyInterpolation2D.Interpolate;

            CollisionShape.pathCount = 1;
            CollisionShape.SetPath(0, CatShape.Silhouette);
            CollisionShape.isTrigger = false;
            if (catMeshes == null) BuildVisual();
        }

        /// <summary>
        /// Places the cat and points it at the run's shared random source; it draws every random choice from
        /// that source's <see cref="RandomProvider.Subsystem.DynamicObstacles"/> stream.
        /// </summary>
        public void Initialize(Rect movementBounds, Vector2 position, RandomProvider randomSource)
        {
            if (body == null) Configure();
            bounds = movementBounds;
            spawnPosition = position;
            this.randomSource = randomSource ?? throw new System.ArgumentNullException(nameof(randomSource));
            initialized = true;

            body.position = position;
            body.linearVelocity = Vector2.zero;
            transform.position = position;
            lastOnFloor = position;
            direction = RandomDirection();
            body.rotation = HeadingDegrees(direction);
            transform.rotation = Quaternion.Euler(0f, 0f, body.rotation);
            CurrentBehavior = Behavior.Roaming;
            startleTimer = 0f;
            clock = 0f;
            waryUntil = boredUntil = 0f;
            afterWaypoint = null;
            behaviorTimer = NextRange(3f, 6f);
            target = position + direction * NextRange(0.6f, 1.2f);
            vacuum = FindAnyObjectByType<VacuumRobot>();
        }

        /// <summary>
        /// Back to the placed position, drawing from <paramref name="randomSource"/>: the run's source, which the
        /// robot rebuilds from the seed on its own reset, so reset the robot first.
        /// </summary>
        public void ResetToSpawn(RandomProvider randomSource) => Initialize(bounds, spawnPosition, randomSource);

        // Placed cats are stepped by their CatHerd, in level order: they share one random stream, so the order
        // they draw in has to be the same every run. Unity doesn't promise an order between objects' FixedUpdates.
        void FixedUpdate()
        {
            if (!herded) Step(Time.fixedDeltaTime);
        }

        public void Step(float dt)
        {
            if (!Application.isPlaying || !initialized) return;
            clock += dt;

            if (vacuum == null) vacuum = FindAnyObjectByType<VacuumRobot>();

            Vector2 position = body.position;

            // Shoved off the floor plan (through a gap, by the vacuum): back to where it last stood on the floor.
            if (Confined)
            {
                if (IsOnFloor(position)) lastOnFloor = position;
                else
                {
                    position = lastOnFloor;
                    body.position = position;
                    transform.position = position;
                    body.linearVelocity = Vector2.zero;
                    direction = -direction;
                    target = position + direction * 0.5f;
                    afterWaypoint = null;
                }
            }

            Vector2 robot = vacuum != null ? (Vector2)vacuum.transform.position : Vector2.zero;
            float vacuumDistance = vacuum != null ? Vector2.Distance(position, robot) : float.PositiveInfinity;
            float vacuumSpeed = vacuum != null ? vacuum.SpeedMetersPerSecond : 0f;
            Vector2 forward = vacuum != null ? vacuum.Forward : Vector2.up;
            Behavior reaction = ChooseBehavior(vacuumDistance, vacuumSpeed);

            // 1. Too close: bolt, sideways out of the vacuum's path. (Re-aimed only if it is still close near the end.)
            if (reaction == Behavior.Startled && (CurrentBehavior != Behavior.Startled || startleTimer < 0.5f))
                BeginStartle(position, robot, forward);
            else if (CurrentBehavior == Behavior.Startled && (startleTimer -= dt) <= 0f)
                BeginRoaming(position);

            if (CurrentBehavior != Behavior.Startled)
            {
                // 2. In the vacuum's path (it is driving at the cat): step aside. A napping cat only stirs when it is
                // nearly there; a wary one moves for it sooner.
                float lookAhead = CurrentBehavior == Behavior.Napping ? NappingPathLookAhead : Wary ? WaryPathLookAhead : PathLookAhead;
                if (CurrentBehavior != Behavior.Yielding && vacuum != null &&
                    InVacuumPath(position, robot, forward, vacuumSpeed, lookAhead, out float side))
                    BeginYielding(position, robot, forward, side);

                switch (CurrentBehavior)
                {
                    case Behavior.Yielding:
                        if ((target - position).sqrMagnitude < 0.1f * 0.1f || (behaviorTimer -= dt) <= 0f) BeginRoaming(position);
                        break;

                    case Behavior.Investigating:
                        // Watching: lose interest after a while, or when the vacuum stops or goes; don't stay where
                        // the cat is in the way.
                        if ((behaviorTimer -= dt) <= 0f || reaction != Behavior.Investigating || !GoodStopSpot(position))
                        {
                            boredUntil = clock + NextRange(BoredMinSeconds, BoredMaxSeconds);
                            BeginRoaming(position);
                        }
                        else
                        {
                            // Creep a little nearer, never closer than the standoff, and never across its path.
                            Vector2 away = position - robot;
                            target = away.sqrMagnitude > WatchStandoff * WatchStandoff
                                ? robot + away.normalized * WatchStandoff
                                : position;
                        }
                        break;

                    case Behavior.Roaming:
                        // Curious about a vacuum on the move nearby: watch it, if not wary, bored, or in the way here.
                        if (reaction == Behavior.Investigating && !Wary && clock >= boredUntil && GoodStopSpot(position))
                        {
                            CurrentBehavior = Behavior.Investigating;
                            afterWaypoint = null;
                            behaviorTimer = NextRange(WatchMinSeconds, WatchMaxSeconds);
                            target = position;
                            break;
                        }
                        behaviorTimer -= dt;
                        if ((target - position).sqrMagnitude < 0.12f * 0.12f)
                        {
                            if (afterWaypoint.HasValue)
                            {
                                target = afterWaypoint.Value; // through the doorway: on to the spot in the next room
                                afterWaypoint = null;
                                behaviorTimer = NextRange(3f, 6f);
                            }
                            else Arrive(position);
                        }
                        else if (behaviorTimer <= 0f) Arrive(position);
                        break;

                    case Behavior.Resting:
                    case Behavior.Napping:
                        if ((behaviorTimer -= dt) <= 0f) BeginRoaming(position);
                        break;
                }
            }

            if (CurrentBehavior == Behavior.Resting || CurrentBehavior == Behavior.Napping ||
                CurrentBehavior == Behavior.Investigating && (target - position).sqrMagnitude < 0.05f * 0.05f)
            {
                body.linearVelocity = Vector2.zero;
                // Watching: turn to face the vacuum.
                if (CurrentBehavior == Behavior.Investigating && vacuum != null && (robot - position).sqrMagnitude > Mathf.Epsilon)
                {
                    direction = (robot - position).normalized;
                    body.rotation = Mathf.MoveTowardsAngle(body.rotation, HeadingDegrees(direction), FacingTurnSpeed * dt);
                }
                return;
            }

            Vector2 toTarget = target - position;
            if (toTarget.sqrMagnitude > Mathf.Epsilon)
            {
                float turn = Mathf.Clamp(Vector2.SignedAngle(direction, toTarget), -TurnSpeed * dt, TurnSpeed * dt);
                direction = Rotate(direction, turn);
            }

            // The floor fence: about to walk off the floor plan, turn back into it.
            if (Confined && direction.sqrMagnitude > Mathf.Epsilon && !IsOnFloor(position + direction.normalized * FenceLookAhead))
            {
                direction = Rotate(-direction, NextRange(-30f, 30f));
                if (CurrentBehavior != Behavior.Startled) behaviorTimer = NextRange(2f, 4f);
                target = position + direction * NextRange(0.6f, 1.2f);
                afterWaypoint = null;
            }

            Vector2 next = position + direction * MoveSpeed * dt;
            Bounds silhouetteBounds = CollisionShape.bounds;
            float minX = bounds.xMin - silhouetteBounds.min.x + body.position.x;
            float maxX = bounds.xMax - silhouetteBounds.max.x + body.position.x;
            float minY = bounds.yMin - silhouetteBounds.min.y + body.position.y;
            float maxY = bounds.yMax - silhouetteBounds.max.y + body.position.y;

            if (next.x < minX || next.x > maxX)
            {
                direction.x = -direction.x;
                next.x = Mathf.Clamp(next.x, minX, maxX);
            }
            if (next.y < minY || next.y > maxY)
            {
                direction.y = -direction.y;
                next.y = Mathf.Clamp(next.y, minY, maxY);
            }

            body.rotation = Mathf.MoveTowardsAngle(body.rotation, HeadingDegrees(direction), FacingTurnSpeed * dt);
            body.linearVelocity = direction * MoveSpeed;
        }

        /// <summary>The body rotation that points the cat's nose (its local +y) along <paramref name="heading"/>.</summary>
        public static float HeadingDegrees(Vector2 heading) => Mathf.Atan2(heading.y, heading.x) * Mathf.Rad2Deg - 90f;

        void OnCollisionEnter2D(Collision2D collision)
        {
            if (!Application.isPlaying || collision.contactCount == 0) return;

            // Bumped by the vacuum: that's a scare.
            if (vacuum != null && collision.rigidbody != null && collision.rigidbody.gameObject == vacuum.gameObject)
            {
                BeginStartle(body.position, vacuum.transform.position, vacuum.Forward);
                return;
            }

            Vector2 normal = collision.GetContact(0).normal;
            direction = ReflectDirection(direction, normal);
            direction = Rotate(direction, NextRange(-20f, 20f));
            if (CurrentBehavior != Behavior.Startled)
            {
                if (CurrentBehavior == Behavior.Investigating || CurrentBehavior == Behavior.Yielding) CurrentBehavior = Behavior.Roaming;
                target = body.position + direction * NextRange(0.6f, 1.2f);
                afterWaypoint = null;
                behaviorTimer = NextRange(2f, 4f);
            }
        }

        public static Behavior ChooseBehavior(float vacuumDistance, float vacuumSpeed)
        {
            if (vacuumDistance <= StartleDistance) return Behavior.Startled;
            if (vacuumDistance <= InterestDistance && vacuumSpeed > 0.08f) return Behavior.Investigating;
            return Behavior.Roaming;
        }

        /// <summary>
        /// True when <paramref name="cat"/> is in the strip the vacuum is about to drive over: ahead of it (within
        /// <paramref name="lookAhead"/>) and within PathHalfWidth of its centre line, while it is moving.
        /// <paramref name="side"/> is +1 when the cat is to the vacuum's left, -1 to its right.
        /// </summary>
        public static bool InVacuumPath(Vector2 cat, Vector2 vacuum, Vector2 forward, float vacuumSpeed, float lookAhead, out float side)
        {
            Vector2 offset = cat - vacuum;
            float cross = forward.x * offset.y - forward.y * offset.x; // + left of the vacuum's heading
            side = cross >= 0f ? 1f : -1f;
            if (vacuumSpeed <= PathMinSpeed) return false;
            float along = Vector2.Dot(offset, forward);
            return along > 0f && along <= lookAhead && Mathf.Abs(cross) <= PathHalfWidth;
        }

        /// <summary>Where to step to get out of the vacuum's path: sideways, past the strip's edge, on <paramref name="side"/>.</summary>
        public static Vector2 YieldTarget(Vector2 cat, Vector2 vacuum, Vector2 forward, float side)
        {
            Vector2 left = new Vector2(-forward.y, forward.x);
            Vector2 offset = cat - vacuum;
            float lateral = Vector2.Dot(offset, left);
            float needed = side * (PathHalfWidth + YieldClearance) - lateral;
            return cat + left * needed;
        }

        /// <summary>
        /// Where to bolt to: away from the vacuum and sideways out of its path (on the side the cat is on), 1.2 to
        /// 1.8 m off. <paramref name="side"/> as from <see cref="InVacuumPath"/>.
        /// </summary>
        public static Vector2 FleeDirection(Vector2 cat, Vector2 vacuum, Vector2 forward, float side)
        {
            Vector2 away = cat - vacuum;
            away = away.sqrMagnitude > Mathf.Epsilon ? away.normalized : -forward;
            Vector2 left = new Vector2(-forward.y, forward.x);
            return (away + left * side * 1.5f).normalized;
        }

        void BeginStartle(Vector2 position, Vector2 robot, Vector2 forward)
        {
            CurrentBehavior = Behavior.Startled;
            afterWaypoint = null;
            startleTimer = NextRange(1.1f, 1.8f);
            waryUntil = clock + NextRange(WaryMinSeconds, WaryMaxSeconds);
            InVacuumPath(position, robot, forward, 1f, 0f, out float side);
            float distance = NextRange(1.2f, 1.8f);
            // The side it is on first, then the other side, then straight away: wherever is floor.
            Vector2 first = position + FleeDirection(position, robot, forward, side) * distance;
            Vector2 second = position + FleeDirection(position, robot, forward, -side) * distance;
            Vector2 away = position - robot;
            Vector2 last = position + (away.sqrMagnitude > Mathf.Epsilon ? away.normalized : -direction) * distance;
            target = IsOnFloor(first) ? first : IsOnFloor(second) ? second : last;
        }

        void BeginYielding(Vector2 position, Vector2 robot, Vector2 forward, float side)
        {
            Vector2 aside = YieldTarget(position, robot, forward, side);
            if (!IsOnFloor(aside) || NearDoorway(aside))
            {
                Vector2 other = YieldTarget(position, robot, forward, -side);
                if (IsOnFloor(other)) aside = other;
            }
            CurrentBehavior = Behavior.Yielding;
            afterWaypoint = null;
            behaviorTimer = YieldMaxSeconds;
            target = aside;
        }

        /// <summary>Got where it was going: nap (along a wall), sit a moment, or walk on, but only stop where it isn't in the way.</summary>
        void Arrive(Vector2 position)
        {
            if (!GoodStopSpot(position))
            {
                BeginRoaming(position); // a doorway, a hallway, the dock: keep going
                return;
            }
            float roll = NextRange(0f, 1f);
            if (roll < NapChance && NearAWall(position))
            {
                CurrentBehavior = Behavior.Napping;
                behaviorTimer = NextRange(NapMinSeconds, NapMaxSeconds);
            }
            else if (roll < SitChance)
            {
                CurrentBehavior = Behavior.Resting;
                behaviorTimer = NextRange(SitMinSeconds, SitMaxSeconds);
            }
            else BeginRoaming(position);
        }

        void BeginRoaming(Vector2 position)
        {
            CurrentBehavior = Behavior.Roaming;
            behaviorTimer = NextRange(3f, 6f);
            afterWaypoint = null;
            if (Confined && PickWallsideTarget(position)) return;

            float minX = bounds.xMin + 0.18f;
            float maxX = bounds.xMax - 0.18f;
            float minY = bounds.yMin + 0.18f;
            float maxY = bounds.yMax - 0.18f;
            // Somewhere on the floor plan: the bounds are a rectangle, the floor need not be (an L-shaped flat).
            for (int attempt = 0; attempt < (Confined ? TargetTries : 1); attempt++)
            {
                target = new Vector2(
                    NextRange(Mathf.Min(minX, maxX), Mathf.Max(minX, maxX)),
                    NextRange(Mathf.Min(minY, maxY), Mathf.Max(minY, maxY)));
                if (IsOnFloor(target)) break;
            }
            if ((target - position).sqrMagnitude < 0.12f * 0.12f)
                target = position + RandomDirection() * 0.5f;
        }

        // ---- The floor plan: walls, doorways, the dock (level coordinates inside, world outside) ------------------

        /// <summary>
        /// A spot a little in from a wall of this room or, sometimes, of a neighbouring room (then via the doorway).
        /// Cats walk the edges of a room more than its middle.
        /// </summary>
        bool PickWallsideTarget(Vector2 position)
        {
            LevelData level = floor.Level;
            Vector2 here = floor.WorldToLevel(position);
            int roomIndex = level.RoomIndexAt(here);
            if (roomIndex < 0) return false;

            Vector2? doorway = null;
            int goal = roomIndex;
            if (NextRange(0f, 1f) < OtherRoomChance)
            {
                var exits = new List<Doorway>();
                foreach (var d in level.Doorways)
                    if (d != null && d.Links(roomIndex) && level.GetRoom(d.Other(roomIndex)) != null) exits.Add(d);
                if (exits.Count > 0)
                {
                    var exit = exits[Mathf.Min(exits.Count - 1, (int)NextRange(0f, exits.Count))];
                    goal = exit.Other(roomIndex);
                    doorway = exit.center;
                }
            }

            Room room = level.GetRoom(goal);
            if (room == null || !room.IsValid) return false;
            for (int attempt = 0; attempt < TargetTries; attempt++)
            {
                int edge = Mathf.Min(room.EdgeCount - 1, (int)NextRange(0f, room.EdgeCount));
                Vector2 a = room.EdgeStart(edge), b = room.EdgeEnd(edge);
                Vector2 along = b - a;
                if (along.sqrMagnitude < 0.25f) continue; // an edge shorter than 0.5 m
                Vector2 onWall = a + along * NextRange(0.1f, 0.9f);
                Vector2 normal = new Vector2(-along.y, along.x).normalized;
                if (!room.Contains(onWall + normal * 0.1f)) normal = -normal;
                Vector2 spot = onWall + normal * NextRange(WallInsetMin, WallInsetMax);
                if (!room.Contains(spot)) continue;
                Vector2 world = floor.LevelToWorld(spot);
                if (!IsOnFloor(world)) continue;
                if (doorway.HasValue)
                {
                    target = floor.LevelToWorld(doorway.Value);
                    afterWaypoint = world;
                }
                else target = world;
                return (target - position).sqrMagnitude >= 0.12f * 0.12f;
            }
            return false;
        }

        /// <summary>Somewhere the cat may stop without being in the way: not in a doorway, a narrow hallway or on the dock.</summary>
        bool GoodStopSpot(Vector2 world)
        {
            if (!Confined) return true;
            LevelData level = floor.Level;
            Vector2 at = floor.WorldToLevel(world);
            if ((at - level.RobotSpawn).sqrMagnitude < DockKeepOut * DockKeepOut) return false;
            if (NearDoorway(world)) return false;
            return !Narrow(level, at);
        }

        bool NearDoorway(Vector2 world)
        {
            if (!Confined) return false;
            Vector2 at = floor.WorldToLevel(world);
            foreach (var d in floor.Level.Doorways)
            {
                if (d == null) continue;
                float reach = d.width * 0.5f + DoorwayKeepOut;
                if ((at - d.center).sqrMagnitude < reach * reach) return true;
            }
            return false;
        }

        bool NearAWall(Vector2 world)
        {
            if (!Confined) return false;
            LevelData level = floor.Level;
            Vector2 at = floor.WorldToLevel(world);
            Room room = level.GetRoom(level.RoomIndexAt(at));
            if (room == null || !room.IsValid) return false;
            for (int edge = 0; edge < room.EdgeCount; edge++)
                if (Poly2D.DistanceToSegment(at, room.EdgeStart(edge), room.EdgeEnd(edge), out _) <= NearWall) return true;
            return false;
        }

        /// <summary>
        /// A hallway: walls within NarrowWall on roughly opposite sides of <paramref name="at"/> (level coordinates).
        /// Every room's walls count, so the two sides of a doorway's wall do too.
        /// </summary>
        public static bool Narrow(LevelData level, Vector2 at)
        {
            var toWalls = new List<Vector2>();
            foreach (var room in level.Rooms)
            {
                if (room == null || !room.IsValid) continue;
                for (int edge = 0; edge < room.EdgeCount; edge++)
                {
                    Vector2 a = room.EdgeStart(edge), b = room.EdgeEnd(edge);
                    if (Poly2D.DistanceToSegment(at, a, b, out float t) > NarrowWall) continue;
                    Vector2 nearest = a + (b - a) * t - at;
                    if (nearest.sqrMagnitude > 1e-6f) toWalls.Add(nearest.normalized);
                }
            }
            for (int i = 0; i < toWalls.Count; i++)
                for (int j = i + 1; j < toWalls.Count; j++)
                    if (Vector2.Dot(toWalls[i], toWalls[j]) < -0.7f) return true;
            return false;
        }

        public static Vector2 ReflectDirection(Vector2 incoming, Vector2 contactNormal)
        {
            if (incoming.sqrMagnitude <= Mathf.Epsilon) return Vector2.right;
            if (contactNormal.sqrMagnitude <= Mathf.Epsilon) return -incoming.normalized;

            Vector2 normal = contactNormal.normalized;
            if (Vector2.Dot(incoming, normal) > 0f) normal = -normal;
            return Vector2.Reflect(incoming.normalized, normal).normalized;
        }

        /// <summary>
        /// Spawns the level's placed cats, all drawing from <paramref name="randomSource"/> and stepped in
        /// <see cref="LevelData.Obstacles"/> order by one <see cref="CatHerd"/>, so the cats are the same for the
        /// same run seed. The cats sit under a "Cats" object (with the herd) under <paramref name="parent"/>.
        /// </summary>
        public static List<MovingCat> SpawnPlacedCats(LevelRenderer renderer, Transform parent, RandomProvider randomSource)
        {
            var spawned = new List<MovingCat>();
            if (renderer == null || renderer.Level == null) return spawned;
            if (randomSource == null) throw new System.ArgumentNullException(nameof(randomSource));

            LevelData level = renderer.Level;
            Rect bounds = WorldBounds(renderer, level.Bounds());
            CatHerd herd = null;
            foreach (Obstacle obstacle in level.Obstacles)
            {
                if (obstacle == null || obstacle.kind != ObstacleKind.Cat) continue;

                Vector2 position = renderer.LevelToWorld(obstacle.center);
                var catObject = new GameObject(string.IsNullOrWhiteSpace(obstacle.name)
                    ? Obstacle.DefaultName(ObstacleKind.Cat)
                    : obstacle.name);
                if (herd == null)
                {
                    var herdObject = new GameObject("Cats");
                    if (parent != null) herdObject.transform.SetParent(parent, false);
                    herd = herdObject.AddComponent<CatHerd>();
                }
                catObject.transform.SetParent(herd.transform, true);
                catObject.transform.position = position;
                var cat = catObject.AddComponent<MovingCat>();
                cat.herded = true;
                cat.Initialize(bounds, position, randomSource);
                cat.ConfineTo(renderer);
                herd.Add(cat);
                spawned.Add(cat);
            }

            return spawned;
        }

        static Rect WorldBounds(LevelRenderer renderer, Rect levelBounds)
        {
            Vector2 min = new Vector2(float.PositiveInfinity, float.PositiveInfinity);
            Vector2 max = new Vector2(float.NegativeInfinity, float.NegativeInfinity);
            Vector2[] corners =
            {
                levelBounds.min,
                new Vector2(levelBounds.xMin, levelBounds.yMax),
                new Vector2(levelBounds.xMax, levelBounds.yMin),
                levelBounds.max,
            };

            foreach (Vector2 corner in corners)
            {
                Vector2 world = renderer.LevelToWorld(corner);
                min = Vector2.Min(min, world);
                max = Vector2.Max(max, world);
            }

            return Rect.MinMaxRect(min.x, min.y, max.x, max.y);
        }

        void BuildVisual()
        {
            var layers = CatShape.Layers;
            catMeshes = new Mesh[layers.Count];
            catMaterials = new Material[layers.Count];
            for (int i = 0; i < layers.Count; i++)
                CreateVisualLayer(i, layers[i]);
        }

        void CreateVisualLayer(int index, CatShape.Layer layer)
        {
            var vertices = new Vector3[layer.vertices.Length];
            for (int i = 0; i < vertices.Length; i++) vertices[i] = layer.vertices[i];

            var mesh = new Mesh { name = layer.name };
            mesh.vertices = vertices;
            mesh.triangles = layer.triangles;
            mesh.RecalculateBounds();
            catMeshes[index] = mesh;

            // Nearer the camera (more negative z) for each later layer, so details sit on top of the fur.
            var visual = new GameObject(layer.name);
            visual.transform.SetParent(transform, false);
            visual.transform.localPosition = new Vector3(0f, 0f, -0.04f - index * 0.002f);
            visual.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = visual.AddComponent<MeshRenderer>();
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            var shader = Shader.Find("Universal Render Pipeline/Unlit")
                         ?? Shader.Find("Unlit/Color")
                         ?? Shader.Find("Sprites/Default");
            if (shader != null)
            {
                catMaterials[index] = new Material(shader)
                {
                    name = $"MovingCat {layer.name} (generated)",
                    hideFlags = HideFlags.HideAndDontSave,
                    color = layer.color,
                };
                if (catMaterials[index].HasProperty("_BaseColor"))
                    catMaterials[index].SetColor("_BaseColor", layer.color);
                if (catMaterials[index].HasProperty("_Cull"))
                    catMaterials[index].SetFloat("_Cull", (float)UnityEngine.Rendering.CullMode.Off);
                renderer.sharedMaterial = catMaterials[index];
            }
        }

        void OnDestroy()
        {
            if (Application.isPlaying)
            {
                if (catMeshes != null)
                    foreach (Mesh mesh in catMeshes)
                        if (mesh != null) Destroy(mesh);
                if (catMaterials != null)
                    foreach (Material material in catMaterials)
                        if (material != null) Destroy(material);
            }
            else
            {
                if (catMeshes != null)
                    foreach (Mesh mesh in catMeshes)
                        if (mesh != null) DestroyImmediate(mesh);
                if (catMaterials != null)
                    foreach (Material material in catMaterials)
                        if (material != null) DestroyImmediate(material);
            }
        }

        float NextRange(float min, float max) => randomSource.NextFloat(RandomProvider.Subsystem.DynamicObstacles, min, max);

        Vector2 RandomDirection()
        {
            float angle = NextRange(-180f, 180f) * Mathf.Deg2Rad;
            return new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
        }

        static Vector2 Rotate(Vector2 value, float degrees)
        {
            float radians = degrees * Mathf.Deg2Rad;
            float sin = Mathf.Sin(radians);
            float cos = Mathf.Cos(radians);
            return new Vector2(value.x * cos - value.y * sin, value.x * sin + value.y * cos).normalized;
        }
    }
}
