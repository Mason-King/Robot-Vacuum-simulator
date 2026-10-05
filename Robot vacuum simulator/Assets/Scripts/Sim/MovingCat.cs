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
        static readonly Vector2[] CatSilhouette =
        {
            new Vector2(-0.08f, -0.17f), new Vector2(-0.11f, -0.12f), new Vector2(-0.12f, -0.02f),
            new Vector2(-0.105f, 0.08f), new Vector2(-0.105f, 0.20f), new Vector2(-0.08f, 0.34f),
            new Vector2(-0.015f, 0.27f), new Vector2(0f, 0.285f), new Vector2(0.015f, 0.27f),
            new Vector2(0.08f, 0.34f), new Vector2(0.105f, 0.20f), new Vector2(0.105f, 0.08f),
            new Vector2(0.12f, -0.02f), new Vector2(0.11f, -0.12f), new Vector2(0.08f, -0.17f),
            new Vector2(0.04f, -0.19f), new Vector2(-0.04f, -0.19f),
        };
        Rigidbody2D body;
        Vector2 direction;
        Rect bounds;
        float turnTimer;
        System.Random random;
        Mesh[] catMeshes;
        Material[] catMaterials;

        public Vector2 Direction => direction;
        public float MoveSpeed => Speed;
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
            CollisionShape.SetPath(0, CatSilhouette);
            CollisionShape.isTrigger = false;
            if (catMeshes == null) BuildVisual();
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
            var silhouetteVertices = new List<Vector3>(CatSilhouette.Length);
            foreach (Vector2 point in CatSilhouette)
                silhouetteVertices.Add(point);
            CreateVisualLayer(0, "Cat silhouette", silhouetteVertices, new List<int>(Poly2D.Triangulate(CatSilhouette)), new Color(0.83f, 0.48f, 0.27f));

            var markingVertices = new List<Vector3>();
            var markingTriangles = new List<int>();
            AddEllipse(markingVertices, markingTriangles, new Vector2(0f, -0.075f), new Vector2(0.042f, 0.075f), 16);
            AddTriangle(markingVertices, markingTriangles, new Vector2(-0.078f, 0.30f), new Vector2(-0.052f, 0.30f), new Vector2(-0.075f, 0.245f));
            AddTriangle(markingVertices, markingTriangles, new Vector2(0.052f, 0.30f), new Vector2(0.078f, 0.30f), new Vector2(0.075f, 0.245f));
            AddEllipse(markingVertices, markingTriangles, new Vector2(-0.027f, 0.09f), new Vector2(0.025f, 0.018f), 12);
            AddEllipse(markingVertices, markingTriangles, new Vector2(0.027f, 0.09f), new Vector2(0.025f, 0.018f), 12);
            CreateVisualLayer(1, "Cat markings", markingVertices, markingTriangles, new Color(0.98f, 0.72f, 0.55f));

            var featureVertices = new List<Vector3>();
            var featureTriangles = new List<int>();
            AddEllipse(featureVertices, featureTriangles, new Vector2(-0.034f, 0.145f), new Vector2(0.008f, 0.013f), 10);
            AddEllipse(featureVertices, featureTriangles, new Vector2(0.034f, 0.145f), new Vector2(0.008f, 0.013f), 10);
            AddTriangle(featureVertices, featureTriangles, new Vector2(-0.012f, 0.112f), new Vector2(0.012f, 0.112f), new Vector2(0f, 0.098f));
            AddRibbon(featureVertices, featureTriangles, new Vector2(0f, 0.098f), new Vector2(0f, 0.082f), 0.003f);
            AddRibbon(featureVertices, featureTriangles, new Vector2(0f, 0.082f), new Vector2(-0.012f, 0.075f), 0.003f);
            AddRibbon(featureVertices, featureTriangles, new Vector2(0f, 0.082f), new Vector2(0.012f, 0.075f), 0.003f);
            AddRibbon(featureVertices, featureTriangles, new Vector2(-0.048f, 0.09f), new Vector2(-0.09f, 0.105f), 0.0025f);
            AddRibbon(featureVertices, featureTriangles, new Vector2(-0.048f, 0.084f), new Vector2(-0.095f, 0.084f), 0.0025f);
            AddRibbon(featureVertices, featureTriangles, new Vector2(-0.048f, 0.078f), new Vector2(-0.09f, 0.063f), 0.0025f);
            AddRibbon(featureVertices, featureTriangles, new Vector2(0.048f, 0.09f), new Vector2(0.09f, 0.105f), 0.0025f);
            AddRibbon(featureVertices, featureTriangles, new Vector2(0.048f, 0.084f), new Vector2(0.095f, 0.084f), 0.0025f);
            AddRibbon(featureVertices, featureTriangles, new Vector2(0.048f, 0.078f), new Vector2(0.09f, 0.063f), 0.0025f);
            CreateVisualLayer(2, "Cat face", featureVertices, featureTriangles, new Color(0.20f, 0.12f, 0.10f));
        }

        void CreateVisualLayer(int index, string layerName, List<Vector3> vertices, List<int> triangles, Color color)
        {
            var mesh = new Mesh { name = layerName };
            mesh.SetVertices(vertices);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateBounds();
            catMeshes ??= new Mesh[3];
            catMaterials ??= new Material[3];
            catMeshes[index] = mesh;

            var visual = new GameObject(layerName);
            visual.transform.SetParent(transform, false);
            visual.transform.localPosition = new Vector3(0f, 0f, -0.04f + index * 0.0001f);
            visual.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = visual.AddComponent<MeshRenderer>();
            var shader = Shader.Find("Universal Render Pipeline/Unlit")
                         ?? Shader.Find("Unlit/Color")
                         ?? Shader.Find("Sprites/Default");
            if (shader != null)
            {
                catMaterials[index] = new Material(shader)
                {
                    name = $"MovingCat {layerName} (generated)",
                    hideFlags = HideFlags.HideAndDontSave,
                    color = color,
                };
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

        static void AddRibbon(List<Vector3> vertices, List<int> triangles, Vector2 start, Vector2 end, float width)
        {
            Vector2 normal = (end - start).normalized * (width * 0.5f);
            int first = vertices.Count;
            vertices.Add(start + normal);
            vertices.Add(start - normal);
            vertices.Add(end - normal);
            vertices.Add(end + normal);
            triangles.Add(first);
            triangles.Add(first + 1);
            triangles.Add(first + 2);
            triangles.Add(first);
            triangles.Add(first + 2);
            triangles.Add(first + 3);
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
