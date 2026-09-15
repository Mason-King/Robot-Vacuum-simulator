using RobotVacuum.Level;
using UnityEngine;
using RobotVacuum;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace RobotVacuum.Sim
{
    /// <summary>
    /// A simple random-bounce robot vacuum: drive forward, and when a whisker or a real
    /// collision finds a wall, back off, turn a random amount, and carry on. Drive speed is
    /// scaled by whatever floor covering it is currently sitting on.
    /// </summary>
    [ExecuteAlways]
    [RequireComponent(typeof(Rigidbody2D))]
    [RequireComponent(typeof(CircleCollider2D))]
    [RequireComponent(typeof(Battery))]
    public class VacuumRobot : MonoBehaviour
    {
        enum State { Driving, Backing, Turning }

        [Header("Level")]
        [Tooltip("Left empty, the robot finds the first LevelRenderer in the scene.")]
        [SerializeField] LevelRenderer levelRenderer;

        [Header("Chassis")]
        [SerializeField] float radius = 0.17f;
        [SerializeField] Color bodyColor = new Color(0.93f, 0.94f, 0.96f);
        [SerializeField] Color noseColor = new Color(0.2f, 0.72f, 0.95f);

        [Header("Drive")]
        [SerializeField] float driveSpeed = 0.65f;
        [SerializeField] float reverseSpeed = 0.35f;
        [SerializeField] float turnSpeed = 320f;

        [Tooltip("How far ahead the bump whisker looks, in metres.")]
        [SerializeField] float lookAhead = 0.09f;

        [SerializeField] float backupDuration = 0.28f;
        [SerializeField] Vector2 turnAngleRange = new Vector2(70f, 250f);

        [Tooltip("Small random heading drift so the robot does not retrace identical paths.")]
        [SerializeField] float wanderDegreesPerSecond = 12f;

        Rigidbody2D body;
        CircleCollider2D circle;
        Battery battery;
        State state = State.Driving;
        float stateTimer;
        float targetHeading;
        readonly RaycastHit2D[] whiskerHits = new RaycastHit2D[8];

        /// <summary>Metres driven since the last reset, for coverage read-outs.</summary>
        public float DistanceTravelled { get; private set; }

        public float SpeedMetersPerSecond => body != null ? body.linearVelocity.magnitude : 0f;

        public float DriveSpeedMetersPerSecond => driveSpeed;
        public float ReverseSpeedMetersPerSecond => reverseSpeed;
        public float TurnSpeedDegreesPerSecond => turnSpeed;

        public void SetDriveSpeedMetersPerSecond(float speed)
        {
            driveSpeed = Mathf.Max(0f, speed);
        }

        public void SetReverseSpeedMetersPerSecond(float speed)
        {
            reverseSpeed = Mathf.Max(0f, speed);
        }

        public void SetTurnSpeedDegreesPerSecond(float speed)
        {
            turnSpeed = Mathf.Max(0f, speed);
        }

        public FloorType CurrentFloor =>
            levelRenderer != null ? levelRenderer.FloorTypeAtWorld(transform.position) : null;

        void Reset()
        {
            ConfigureComponents();
            BuildVisual();
        }

        void OnValidate()
        {
            radius = Mathf.Max(0.02f, radius);
            turnAngleRange.x = Mathf.Max(5f, turnAngleRange.x);
            turnAngleRange.y = Mathf.Max(turnAngleRange.x, turnAngleRange.y);

            if (!Application.isPlaying) ConfigureComponents();
        }

        void Awake()
        {
            ConfigureComponents();
            if (levelRenderer == null)
                levelRenderer = FindAnyObjectByType<LevelRenderer>(FindObjectsInactive.Include);
        }

        void OnEnable()
        {
            if (transform.Find("Visual") == null) BuildVisual();
        }

        void ConfigureComponents()
        {
            body = GetComponent<Rigidbody2D>();
            circle = GetComponent<CircleCollider2D>();
            battery = GetComponent<Battery>();

            if (body != null)
            {
                body.bodyType = RigidbodyType2D.Dynamic;
                body.gravityScale = 0f;
                body.freezeRotation = true;
                body.collisionDetectionMode = CollisionDetectionMode2D.Continuous;
                body.interpolation = RigidbodyInterpolation2D.Interpolate;
            }

            if (circle != null) circle.radius = radius;
        }

        // ---------------------------------------------------------------- behaviour

        void FixedUpdate()
        {
            if (!Application.isPlaying || body == null) return;

            if (!battery.CanOperate)
            {
                body.linearVelocity = Vector2.zero;
                return;
            }

            float dt = Time.fixedDeltaTime;

            switch (state)
            {
                case State.Driving:
                    Drive(dt);
                    break;

                case State.Backing:
                    body.linearVelocity = -Forward * reverseSpeed;
                    if ((stateTimer -= dt) <= 0f) BeginTurn();
                    break;

                case State.Turning:
                    body.linearVelocity = Vector2.zero;
                    TurnTowardsTarget(dt);
                    break;
            }

        }

        void Drive(float dt)
        {
            float speed = driveSpeed * SurfaceSpeedMultiplier();
            body.linearVelocity = Forward * speed;
            DistanceTravelled += speed * dt;

            if (wanderDegreesPerSecond > 0f)
            {
                float drift = (Mathf.PerlinNoise(Time.time * 0.35f, 0f) - 0.5f) * 2f;
                body.MoveRotation(body.rotation + drift * wanderDegreesPerSecond * dt);
            }

            if (WhiskerBlocked()) BeginBackup();
        }

        float SurfaceSpeedMultiplier()
        {
            var floor = CurrentFloor;
            return floor != null ? floor.speedMultiplier : 1f;
        }

        Vector2 Forward
        {
            get
            {
                float radians = body.rotation * Mathf.Deg2Rad;
                return new Vector2(-Mathf.Sin(radians), Mathf.Cos(radians));
            }
        }

        bool WhiskerBlocked()
        {
            var filter = ContactFilter2D.noFilter;
            filter.useTriggers = false;

            int count = Physics2D.CircleCast(
                body.position, radius * 0.95f, Forward, filter, whiskerHits, lookAhead);

            for (int i = 0; i < count; i++)
            {
                var hit = whiskerHits[i];
                if (hit.collider == null || hit.collider.attachedRigidbody == body) continue;
                return true;
            }
            return false;
        }

        void OnCollisionEnter2D(Collision2D collision)
        {
            if (Application.isPlaying && state == State.Driving) BeginBackup();
        }

        void BeginBackup()
        {
            state = State.Backing;
            stateTimer = backupDuration;
        }

        void BeginTurn()
        {
            state = State.Turning;

            float amount = Random.Range(turnAngleRange.x, turnAngleRange.y);
            if (Random.value < 0.5f) amount = -amount;
            targetHeading = body.rotation + amount;
        }

        void TurnTowardsTarget(float dt)
        {
            float next = Mathf.MoveTowardsAngle(body.rotation, targetHeading, turnSpeed * dt);
            body.MoveRotation(next);

            if (Mathf.Abs(Mathf.DeltaAngle(next, targetHeading)) < 1f)
                state = State.Driving;
        }

        /// <summary>Drops the robot back on the level's spawn point and clears its odometer.</summary>
        public void ResetToSpawn()
        {
            if (levelRenderer == null || levelRenderer.Level == null) return;

            transform.position = levelRenderer.LevelToWorld(
                levelRenderer.Level.RobotSpawn, levelRenderer.WallDepth - 0.05f);

            DistanceTravelled = 0f;
            state = State.Driving;
            battery.ResetBattery();

            if (body != null) body.linearVelocity = Vector2.zero;
        }

        // ---------------------------------------------------------------- visual

        /// <summary>Generates the disc-and-nose mesh so the robot is visible without any art assets.</summary>
        public void BuildVisual()
        {
            var existing = transform.Find("Visual");
            if (existing != null)
            {
                if (Application.isPlaying) Destroy(existing.gameObject);
                else DestroyImmediate(existing.gameObject);
            }

            var visual = new GameObject("Visual");
            visual.transform.SetParent(transform, false);

            CreatePart(visual.transform, "Body", BuildDisc(radius, 32), bodyColor, 0f);
            CreatePart(visual.transform, "Nose", BuildNose(radius), noseColor, -0.01f);
        }

        void CreatePart(Transform parent, string name, Mesh mesh, Color color, float depth)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = new Vector3(0f, 0f, depth);

            go.AddComponent<MeshFilter>().sharedMesh = mesh;

            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = SharedVisualMaterial();
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;

            var block = new MaterialPropertyBlock();
            block.SetColor("_BaseColor", color);
            block.SetColor("_Color", color);
            renderer.SetPropertyBlock(block);
        }

        static Material sharedVisualMaterial;

        static Material SharedVisualMaterial()
        {
            if (sharedVisualMaterial != null) return sharedVisualMaterial;

            var shader = Shader.Find("Universal Render Pipeline/Unlit")
                         ?? Shader.Find("Unlit/Color")
                         ?? Shader.Find("Sprites/Default");

            sharedVisualMaterial = new Material(shader)
            {
                name = "VacuumRobot (generated)",
                hideFlags = HideFlags.HideAndDontSave,
            };

            if (sharedVisualMaterial.HasProperty("_Cull"))
                sharedVisualMaterial.SetFloat("_Cull", (float)UnityEngine.Rendering.CullMode.Off);

            return sharedVisualMaterial;
        }

        static Mesh BuildDisc(float radius, int segments)
        {
            var vertices = new Vector3[segments + 1];
            var triangles = new int[segments * 3];

            vertices[0] = Vector3.zero;
            for (int i = 0; i < segments; i++)
            {
                float angle = i * Mathf.PI * 2f / segments;
                vertices[i + 1] = new Vector3(Mathf.Cos(angle) * radius, Mathf.Sin(angle) * radius, 0f);

                triangles[i * 3] = 0;
                triangles[i * 3 + 1] = i + 1;
                triangles[i * 3 + 2] = (i + 1) % segments + 1;
            }

            var mesh = new Mesh { name = "VacuumBody" };
            mesh.vertices = vertices;
            mesh.triangles = triangles;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        static Mesh BuildNose(float radius)
        {
            var mesh = new Mesh { name = "VacuumNose" };
            mesh.vertices = new[]
            {
                new Vector3(-radius * 0.45f, radius * 0.25f, 0f),
                new Vector3(radius * 0.45f, radius * 0.25f, 0f),
                new Vector3(0f, radius * 0.92f, 0f),
            };
            mesh.triangles = new[] { 0, 1, 2 };
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }
    }

#if UNITY_EDITOR
    [InitializeOnLoad]
    static class VacuumRobotEditorLifecycle
    {
        static VacuumRobotEditorLifecycle()
        {
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.EnteredEditMode)
                EditorApplication.delayCall += RebuildSceneVisuals;
        }

        static void RebuildSceneVisuals()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;

            foreach (var robot in Object.FindObjectsByType<VacuumRobot>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
                robot.BuildVisual();
        }
    }
#endif
}
