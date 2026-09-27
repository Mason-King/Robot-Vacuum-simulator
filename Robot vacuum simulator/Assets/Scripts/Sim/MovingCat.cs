using System.Collections.Generic;
using RobotVacuum.Level;
using UnityEngine;

namespace RobotVacuum.Sim
{
    [RequireComponent(typeof(Rigidbody2D))]
    [RequireComponent(typeof(CircleCollider2D))]
    public sealed class MovingCat : MonoBehaviour
    {
        const float Radius = 0.15f;
        const float Speed = 0.45f;
        Rigidbody2D body;
        Vector2 direction;
        Rect bounds;
        float turnTimer;
        System.Random random;
        Mesh catMesh;
        Material catMaterial;

        public Vector2 Direction => direction;
        public float MoveSpeed => Speed;
        public CircleCollider2D CollisionShape { get; private set; }

        void Awake()
        {
            Configure();
        }

        void Configure()
        {
            body = GetComponent<Rigidbody2D>();
            CollisionShape = GetComponent<CircleCollider2D>();

            body.bodyType = RigidbodyType2D.Dynamic;
            body.gravityScale = 0f;
            body.freezeRotation = true;
            body.collisionDetectionMode = CollisionDetectionMode2D.Continuous;
            body.interpolation = RigidbodyInterpolation2D.Interpolate;

            CollisionShape.radius = Radius;
            CollisionShape.isTrigger = false;
            if (catMesh == null) BuildVisual();
        }

        public void Initialize(Rect movementBounds, Vector2 position, Vector2 initialDirection, int seed)
        {
            if (body == null) Configure();
            bounds = movementBounds;
            body.position = position;
            transform.position = position;
            direction = initialDirection.sqrMagnitude > 0f ? initialDirection.normalized : Vector2.right;
            random = new System.Random(seed);
            turnTimer = NextTurnInterval();
        }

        void FixedUpdate()
        {
            if (!Application.isPlaying || random == null) return;

            float dt = Time.fixedDeltaTime;
            turnTimer -= dt;
            if (turnTimer <= 0f)
            {
                direction = Rotate(direction, NextRange(-55f, 55f));
                turnTimer = NextTurnInterval();
            }

            Vector2 next = body.position + direction * Speed * dt;
            float minX = bounds.xMin + Radius;
            float maxX = bounds.xMax - Radius;
            float minY = bounds.yMin + Radius;
            float maxY = bounds.yMax - Radius;

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

            body.linearVelocity = direction * Speed;
        }

        void OnCollisionEnter2D(Collision2D collision)
        {
            if (!Application.isPlaying || collision.contactCount == 0) return;

            Vector2 normal = collision.GetContact(0).normal;
            direction = ReflectDirection(direction, normal);
            direction = Rotate(direction, NextRange(-20f, 20f));
        }

        public static Vector2 ReflectDirection(Vector2 incoming, Vector2 contactNormal)
        {
            if (incoming.sqrMagnitude <= Mathf.Epsilon) return Vector2.right;
            if (contactNormal.sqrMagnitude <= Mathf.Epsilon) return -incoming.normalized;

            Vector2 normal = contactNormal.normalized;
            if (Vector2.Dot(incoming, normal) > 0f) normal = -normal;
            return Vector2.Reflect(incoming.normalized, normal).normalized;
        }

        public static List<MovingCat> SpawnPlacedCats(LevelRenderer renderer, Transform parent)
        {
            var spawned = new List<MovingCat>();
            if (renderer == null || renderer.Level == null) return spawned;

            LevelData level = renderer.Level;
            Rect bounds = WorldBounds(renderer, level.Bounds());
            var random = new System.Random(6321);

            foreach (Obstacle obstacle in level.Obstacles)
            {
                if (obstacle == null || obstacle.kind != ObstacleKind.Cat) continue;

                Vector2 position = renderer.LevelToWorld(obstacle.center);
                var catObject = new GameObject(string.IsNullOrWhiteSpace(obstacle.name)
                    ? Obstacle.DefaultName(ObstacleKind.Cat)
                    : obstacle.name);
                if (parent != null) catObject.transform.SetParent(parent, true);
                catObject.transform.position = position;
                var cat = catObject.AddComponent<MovingCat>();
                cat.Initialize(bounds, position, RandomDirection(random), random.Next());
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
            catMesh = new Mesh { name = "Cat silhouette" };
            var vertices = new List<Vector3>();
            var triangles = new List<int>();
            AddEllipse(vertices, triangles, Vector2.zero, new Vector2(0.12f, 0.19f), 20);
            AddEllipse(vertices, triangles, new Vector2(0f, 0.18f), new Vector2(0.105f, 0.105f), 16);
            AddTriangle(vertices, triangles, new Vector2(-0.09f, 0.22f), new Vector2(-0.08f, 0.34f), new Vector2(-0.015f, 0.27f));
            AddTriangle(vertices, triangles, new Vector2(0.015f, 0.27f), new Vector2(0.08f, 0.34f), new Vector2(0.09f, 0.22f));
            AddTriangle(vertices, triangles, new Vector2(-0.035f, 0.17f), new Vector2(0.035f, 0.17f), new Vector2(0f, 0.13f));
            catMesh.SetVertices(vertices);
            catMesh.SetTriangles(triangles, 0);
            catMesh.RecalculateBounds();

            var visual = new GameObject("Cat visual");
            visual.transform.SetParent(transform, false);
            visual.transform.localPosition = new Vector3(0f, 0f, -0.04f);
            visual.AddComponent<MeshFilter>().sharedMesh = catMesh;
            var renderer = visual.AddComponent<MeshRenderer>();
            var shader = Shader.Find("Universal Render Pipeline/Unlit")
                         ?? Shader.Find("Unlit/Color")
                         ?? Shader.Find("Sprites/Default");
            if (shader != null)
            {
                catMaterial = new Material(shader)
                {
                    name = "MovingCat (generated)",
                    hideFlags = HideFlags.HideAndDontSave,
                    color = new Color(0.83f, 0.48f, 0.27f),
                };
                if (catMaterial.HasProperty("_Cull"))
                    catMaterial.SetFloat("_Cull", (float)UnityEngine.Rendering.CullMode.Off);
                renderer.sharedMaterial = catMaterial;
            }
        }

        void OnDestroy()
        {
            if (Application.isPlaying)
            {
                if (catMesh != null) Destroy(catMesh);
                if (catMaterial != null) Destroy(catMaterial);
            }
            else
            {
                if (catMesh != null) DestroyImmediate(catMesh);
                if (catMaterial != null) DestroyImmediate(catMaterial);
            }
        }

        static void AddEllipse(List<Vector3> vertices, List<int> triangles, Vector2 center, Vector2 radii, int sides)
        {
            int first = vertices.Count;
            vertices.Add(center);
            for (int i = 0; i < sides; i++)
            {
                float angle = i * Mathf.PI * 2f / sides;
                vertices.Add(center + new Vector2(Mathf.Cos(angle) * radii.x, Mathf.Sin(angle) * radii.y));
            }

            for (int i = 0; i < sides; i++)
            {
                triangles.Add(first);
                triangles.Add(first + 1 + i);
                triangles.Add(first + 1 + (i + 1) % sides);
            }
        }

        static void AddTriangle(List<Vector3> vertices, List<int> triangles, Vector2 a, Vector2 b, Vector2 c)
        {
            int first = vertices.Count;
            vertices.Add(a);
            vertices.Add(b);
            vertices.Add(c);
            triangles.Add(first);
            triangles.Add(first + 1);
            triangles.Add(first + 2);
        }

        float NextTurnInterval() => NextRange(1.8f, 4.5f);
        float NextRange(float min, float max) => NextRange(random, min, max);

        static float NextRange(System.Random random, float min, float max) =>
            min + (float)random.NextDouble() * (max - min);

        static Vector2 RandomDirection(System.Random random)
        {
            float angle = NextRange(random, -180f, 180f) * Mathf.Deg2Rad;
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
