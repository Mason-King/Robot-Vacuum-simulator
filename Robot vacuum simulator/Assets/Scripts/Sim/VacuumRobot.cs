using System.Collections.Generic;
using RobotVacuum.Level;
using UnityEngine;
using RobotVacuum;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace RobotVacuum.Sim
{
    /// <summary>
    /// The vacuum's body: drives forward, and when a whisker or a real collision finds something, backs
    /// off and performs a manoeuvre. Drive speed is scaled by the floor covering beneath it. Where it
    /// steers is decided by the selected <see cref="MovementPattern"/> (see <see cref="MovementBrain"/>).
    /// </summary>
    [ExecuteAlways]
    [RequireComponent(typeof(Rigidbody2D))]
    [RequireComponent(typeof(CircleCollider2D))]
    [RequireComponent(typeof(Battery))]
    public class VacuumRobot : MonoBehaviour
    {
        enum State { Driving, Backing, Turning, Shifting }

        struct Step
        {
            public bool turn;
            public float amount; // degrees for a turn, metres for a drive
        }

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

        [Header("Movement")]
        [SerializeField] MovementPattern movementPattern = MovementPattern.RandomBounce;

        [Tooltip("What the Picture movement pattern draws into the coverage heatmap.")]
        [SerializeField] PictureKind picture = PictureKind.Heart;

        [Tooltip("Seeds every random choice the vacuum makes. The same seed drives the same route each run.")]
        [SerializeField] int seed;

        Rigidbody2D body;
        CircleCollider2D circle;
        Battery battery;
        State state = State.Driving;
        float stateTimer;
        float targetHeading;
        MovementBrain brain;
        MovementPattern brainPattern;
        PictureKind brainPicture;
        int brainSeed;
        float speedScale = 1f;
        float cleaningLimit = 1f;
        readonly Queue<Step> steps = new Queue<Step>();
        readonly RaycastHit2D[] whiskerHits = new RaycastHit2D[8];
        readonly RaycastHit2D[] probeHits = new RaycastHit2D[8];

        /// <summary>Metres driven since the last reset, for coverage read-outs.</summary>
        public float DistanceTravelled { get; private set; }

        /// <summary>
        /// Seconds of simulated time since the last reset, accumulated on the physics step. Brains steer by
        /// this rather than <see cref="Time.time"/>, which counts from app start and is scaled by run speed.
        /// </summary>
        public float SimTime { get; private set; }

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
            levelRenderer != null ? levelRenderer.FloorTypeAtWorld(WorldPosition) : null;

        /// <summary>The movement algorithm. Switching mid-run carries on from where the robot is.</summary>
        public MovementPattern Pattern
        {
            get => movementPattern;
            set
            {
                if (movementPattern == value) return;
                movementPattern = value;
                steps.Clear();
                state = State.Driving;
                speedScale = 1f;
                cleaningLimit = 1f;
                Print = null;
            }
        }

        /// <summary>What the Picture pattern draws. Changing it starts the drawing again.</summary>
        public PictureKind Picture
        {
            get => picture;
            set
            {
                if (picture == value) return;
                picture = value;
                Print = null;
                steps.Clear();
                state = State.Driving;
            }
        }

        /// <summary>
        /// Seeds the vacuum's random choices. Setting it starts the algorithm's sequence again, so two runs
        /// of the same level, pattern and seed take the same route.
        /// </summary>
        public int Seed
        {
            get => seed;
            set
            {
                if (seed == value) return;
                seed = value;
                brain = null; // rebuilt from the new seed on next use
            }
        }

        /// <summary>Multiplies drive speed, so a brain can slow down for careful work. Back to 1 on reset.</summary>
        public float SpeedScale
        {
            get => speedScale;
            set => speedScale = Mathf.Clamp01(value);
        }

        /// <summary>
        /// The cleanest the vacuum may leave the floor under it: 0 is suction off, 1 (the default) no limit.
        /// The cleaning controller respects it; the Picture pattern shades with it.
        /// </summary>
        public float CleaningLimit
        {
            get => cleaningLimit;
            set => cleaningLimit = Mathf.Clamp01(value);
        }

        /// <summary>A patch of a picture to print straight into the coverage grid, or null. Set by the printing patterns.</summary>
        public PrintPatch? Print { get; set; }

        public LevelData Level => levelRenderer != null ? levelRenderer.Level : null;

        /// <summary>
        /// Where the body actually is. The transform is interpolated for smooth drawing, so it trails the
        /// physics state by part of a frame and moves with the frame rate; everything the simulation
        /// decides from reads this instead, or a headless run would disagree with a watched one.
        /// </summary>
        Vector3 WorldPosition => body != null ? (Vector3)body.position : transform.position;

        /// <summary>Where the vacuum is, in level metres.</summary>
        public Vector2 LevelPosition =>
            levelRenderer != null ? levelRenderer.WorldToLevel(WorldPosition) : (Vector2)WorldPosition;

        MovementBrain Brain
        {
            get
            {
                bool pictureChanged = movementPattern == MovementPattern.Picture && brainPicture != picture;
                if (brain == null || brainPattern != movementPattern || pictureChanged || brainSeed != seed)
                {
                    brain = MovementBrain.Create(movementPattern, picture, seed);
                    brainPattern = movementPattern;
                    brainPicture = picture;
                    brainSeed = seed;
                    brain.Reset(this);
                }
                return brain;
            }
        }

        public float Radius => radius;

        /// <summary>Width of floor cleaned in one pass, in metres.</summary>
        public float CleaningWidth => radius * 2f;

        public Vector2 TurnAngleRange => turnAngleRange;
        public float WanderDegreesPerSecond => wanderDegreesPerSecond;

        /// <summary>Forward speed on the floor it is on now, in metres per second.</summary>
        public float DriveSpeedNow => driveSpeed * SurfaceSpeedMultiplier() * speedScale;

        /// <summary>Facing, in degrees counter-clockwise; 0 faces +y.</summary>
        public float Heading => body != null ? body.rotation : transform.eulerAngles.z;

        public Vector2 Forward
        {
            get
            {
                float radians = Heading * Mathf.Deg2Rad;
                return new Vector2(-Mathf.Sin(radians), Mathf.Cos(radians));
            }
        }

        public Vector2 Right
        {
            get
            {
                var forward = Forward;
                return new Vector2(forward.y, -forward.x);
            }
        }

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
            // The generated material is never saved, so a visual stored in a scene comes back with
            // empty material slots and draws nothing. Rebuild it rather than leave the robot invisible.
            if (!HasUsableVisual()) BuildVisual();
        }

        bool HasUsableVisual()
        {
            var visual = transform.Find("Visual");
            if (visual == null) return false;

            foreach (var part in visual.GetComponentsInChildren<MeshRenderer>(true))
                if (part.sharedMaterial == null) return false;
            return true;
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
            SimTime += dt;

            switch (state)
            {
                case State.Driving:
                    Drive(dt);
                    break;

                case State.Backing:
                    body.linearVelocity = -Forward * reverseSpeed;
                    if ((stateTimer -= dt) <= 0f) NextStep();
                    break;

                case State.Turning:
                    body.linearVelocity = Vector2.zero;
                    TurnTowardsTarget(dt);
                    break;

                case State.Shifting:
                    Shift(dt);
                    break;
            }

        }

        void Drive(float dt)
        {
            float speed = DriveSpeedNow;
            body.linearVelocity = Forward * speed;
            DistanceTravelled += speed * dt;

            float steer = Brain.Steer(this, dt);
            if (steer != 0f) body.MoveRotation(body.rotation + steer * dt);

            if (WhiskerBlocked()) BeginBackup();
        }

        /// <summary>A set-distance drive inside a manoeuvre, such as stepping over to the next lawnmower lane.</summary>
        void Shift(float dt)
        {
            float speed = DriveSpeedNow;
            body.linearVelocity = Forward * speed;
            DistanceTravelled += speed * dt;

            if ((stateTimer -= speed * dt) <= 0f || WhiskerBlocked()) NextStep();
        }

        float SurfaceSpeedMultiplier()
        {
            var floor = CurrentFloor;
            return floor != null ? floor.speedMultiplier : 1f;
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

        /// <summary>
        /// Distance from the robot's edge to the nearest wall or furniture along <paramref name="direction"/>,
        /// or positive infinity when nothing is within <paramref name="maxDistance"/>.
        /// </summary>
        public float ProbeDistance(Vector2 direction, float maxDistance)
        {
            if (body == null) return float.PositiveInfinity;

            var filter = ContactFilter2D.noFilter;
            filter.useTriggers = false;

            int count = Physics2D.Raycast(body.position, direction.normalized, filter, probeHits, maxDistance + radius);
            float nearest = float.PositiveInfinity;

            for (int i = 0; i < count; i++)
            {
                var hit = probeHits[i];
                if (hit.collider == null || hit.collider.attachedRigidbody == body) continue;
                nearest = Mathf.Min(nearest, Mathf.Max(0f, hit.distance - radius));
            }

            return nearest;
        }

        void OnCollisionEnter2D(Collision2D collision)
        {
            if (!Application.isPlaying) return;

            if (state == State.Driving) BeginBackup();
            else if (state == State.Shifting) NextStep();
        }

        void BeginBackup()
        {
            state = State.Backing;
            stateTimer = backupDuration;

            var manoeuvre = Brain.AfterBump(this);
            steps.Clear();
            steps.Enqueue(new Step { turn = true, amount = manoeuvre.turn });

            if (manoeuvre.driveAfter > 0f)
            {
                steps.Enqueue(new Step { turn = false, amount = manoeuvre.driveAfter });
                steps.Enqueue(new Step { turn = true, amount = manoeuvre.turnAfter });
            }
        }

        void NextStep()
        {
            if (steps.Count == 0)
            {
                state = State.Driving;
                return;
            }

            var step = steps.Dequeue();
            if (step.turn)
            {
                state = State.Turning;
                targetHeading = body.rotation + step.amount;
            }
            else
            {
                state = State.Shifting;
                stateTimer = step.amount;
            }
        }

        void TurnTowardsTarget(float dt)
        {
            float next = Mathf.MoveTowardsAngle(body.rotation, targetHeading, turnSpeed * dt);
            body.MoveRotation(next);

            if (Mathf.Abs(Mathf.DeltaAngle(next, targetHeading)) < 1f) NextStep();
        }

        /// <summary>Drops the robot back on the level's spawn point, clears its odometer and restarts its algorithm.</summary>
        public void ResetToSpawn()
        {
            steps.Clear();
            state = State.Driving;
            speedScale = 1f;
            cleaningLimit = 1f;
            SimTime = 0f;
            Print = null;

            // Back to the same random sequence, so a repeat of a run matches it step for step.
            Brain.Seed(seed);
            Brain.Reset(this);

            if (levelRenderer == null || levelRenderer.Level == null) return;

            transform.position = levelRenderer.LevelToWorld(
                levelRenderer.Level.RobotSpawn, levelRenderer.WallDepth - 0.05f);

            DistanceTravelled = 0f;
            battery.ResetBattery();

            if (body != null)
            {
                // Move the body itself, and face the same way every time: the transform alone would leave
                // the first physics step starting from wherever the last run happened to end up.
                body.position = transform.position;
                body.rotation = 0f;
                body.linearVelocity = Vector2.zero;
            }
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
