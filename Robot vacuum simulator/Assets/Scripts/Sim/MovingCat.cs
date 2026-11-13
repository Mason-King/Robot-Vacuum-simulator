using System.Collections.Generic;
using RobotVacuum.Level;
using UnityEngine;

namespace RobotVacuum.Sim
{
    [RequireComponent(typeof(Rigidbody2D))]
    [RequireComponent(typeof(PolygonCollider2D))]
    public sealed class MovingCat : MonoBehaviour
    {
        const float Speed = 0.45f;
        const float InterestDistance = 1.7f;
        const float StartleDistance = 0.42f;
        const float TurnSpeed = 150f;

        public enum Behavior { Roaming, Investigating, Startled, Resting }

        // Fastest the cat's body swings round to face where it is walking, in degrees per second.
        const float FacingTurnSpeed = 360f;

        Rigidbody2D body;
        Vector2 spawnPosition;
        Vector2 direction;
        Vector2 target;
        Rect bounds;
        float behaviorTimer;
        float startleTimer;
        // The run's shared random source. Every random choice a cat makes is drawn from its DynamicObstacles
        // stream, so the cats' state is part of RandomProvider.SnapshotState like everything else's.
        RandomProvider randomSource;
        bool initialized;
        bool herded;
        VacuumRobot vacuum;
        Mesh[] catMeshes;
        Material[] catMaterials;

        public Vector2 Direction => direction;

        public float MoveSpeed => CurrentBehavior switch
        {
            Behavior.Startled => Speed * 1.7f,
            Behavior.Investigating => Speed * 0.7f,
            _ => Speed * 0.8f,
        };
        public Behavior CurrentBehavior { get; private set; }
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
            direction = RandomDirection();
            body.rotation = HeadingDegrees(direction);
            transform.rotation = Quaternion.Euler(0f, 0f, body.rotation);
            CurrentBehavior = Behavior.Roaming;
            startleTimer = 0f;
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

            if (vacuum == null) vacuum = FindAnyObjectByType<VacuumRobot>();

            Vector2 position = body.position;
            float vacuumDistance = vacuum != null
                ? Vector2.Distance(position, (Vector2)vacuum.transform.position)
                : float.PositiveInfinity;
            float vacuumSpeed = vacuum != null ? vacuum.SpeedMetersPerSecond : 0f;
            Behavior reaction = ChooseBehavior(vacuumDistance, vacuumSpeed);

            if (reaction == Behavior.Startled)
            {
                CurrentBehavior = Behavior.Startled;
                startleTimer = NextRange(1.1f, 1.8f);
                Vector2 away = position - (Vector2)vacuum.transform.position;
                if (away.sqrMagnitude <= Mathf.Epsilon) away = -direction;
                target = position + away.normalized * NextRange(1.2f, 1.8f);
            }
            else if (CurrentBehavior == Behavior.Startled && (startleTimer -= dt) <= 0f)
                BeginRoaming(position);

            if (CurrentBehavior != Behavior.Startled)
            {
                if (reaction == Behavior.Investigating)
                {
                    CurrentBehavior = Behavior.Investigating;
                    target = vacuum.transform.position;
                }
                else if (CurrentBehavior == Behavior.Investigating)
                    BeginRoaming(position);

                if (CurrentBehavior == Behavior.Roaming)
                {
                    behaviorTimer -= dt;
                    if ((target - position).sqrMagnitude < 0.12f * 0.12f || behaviorTimer <= 0f)
                        BeginResting();
                }
                else if (CurrentBehavior == Behavior.Resting && (behaviorTimer -= dt) <= 0f)
                    BeginRoaming(position);
            }

            if (CurrentBehavior == Behavior.Resting)
            {
                body.linearVelocity = Vector2.zero;
                return;
            }

            Vector2 toTarget = target - position;
            if (toTarget.sqrMagnitude > Mathf.Epsilon)
            {
                float turn = Mathf.Clamp(Vector2.SignedAngle(direction, toTarget), -TurnSpeed * dt, TurnSpeed * dt);
                direction = Rotate(direction, turn);
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

            Vector2 normal = collision.GetContact(0).normal;
            direction = ReflectDirection(direction, normal);
            direction = Rotate(direction, NextRange(-20f, 20f));
            if (CurrentBehavior != Behavior.Startled)
            {
                if (CurrentBehavior == Behavior.Investigating) CurrentBehavior = Behavior.Roaming;
                target = body.position + direction * NextRange(0.6f, 1.2f);
                behaviorTimer = NextRange(2f, 4f);
            }
        }

        public static Behavior ChooseBehavior(float vacuumDistance, float vacuumSpeed)
        {
            if (vacuumDistance <= StartleDistance) return Behavior.Startled;
            if (vacuumDistance <= InterestDistance && vacuumSpeed > 0.08f) return Behavior.Investigating;
            return Behavior.Roaming;
        }

        void BeginRoaming(Vector2 position)
        {
            CurrentBehavior = Behavior.Roaming;
            behaviorTimer = NextRange(3f, 6f);
            float minX = bounds.xMin + 0.18f;
            float maxX = bounds.xMax - 0.18f;
            float minY = bounds.yMin + 0.18f;
            float maxY = bounds.yMax - 0.18f;
            target = new Vector2(
                NextRange(Mathf.Min(minX, maxX), Mathf.Max(minX, maxX)),
                NextRange(Mathf.Min(minY, maxY), Mathf.Max(minY, maxY)));
            if ((target - position).sqrMagnitude < 0.12f * 0.12f)
                target = position + RandomDirection() * 0.5f;
        }

        void BeginResting()
        {
            CurrentBehavior = Behavior.Resting;
            behaviorTimer = NextRange(0.8f, 2.2f);
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
