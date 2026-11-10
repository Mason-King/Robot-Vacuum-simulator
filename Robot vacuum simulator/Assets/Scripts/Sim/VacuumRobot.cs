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
    /// The vacuum's BODY (design doc, section 3). It executes motion commands, owns the physics (rigidbody and
    /// collider), the speed limits, the floor's speed effect, battery drain from the work actually done, its visuals,
    /// and its TRUE pose. It decides nothing: <see cref="MovementBrain"/>, the controller on the same object, senses,
    /// believes and decides, then calls <see cref="Command"/> every physics step.
    ///
    /// The true pose (<see cref="Heading"/>, the rigidbody position) is for the sensors, the External Model and
    /// reporting. The controller's decision logic and the algorithms never read it; they use the believed pose.
    /// (The legacy reactive patterns are the one exception, which is why they are refused in SLAM runs.)
    /// </summary>
    [ExecuteAlways]
    [RequireComponent(typeof(Rigidbody2D))]
    [RequireComponent(typeof(CircleCollider2D))]
    [RequireComponent(typeof(Battery))]
    public class VacuumRobot : MonoBehaviour
    {
        const float StalledMovementThreshold = 0.0005f;
        const float StalledTurnThreshold = 0.05f;
        const float CommandEpsilon = 1e-4f;

        /// <summary>A run whose vacuum moves less than this in <see cref="NoProgressSeconds"/> is treated as stuck.</summary>
        const float NoProgressDistance = 0.05f;
        const float NoProgressSeconds = 120f;

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

        [Header("Legacy patterns (Spiral, Wall follow, Lawnmower)")]
        [SerializeField] float backupDuration = 0.28f;
        [SerializeField] Vector2 turnAngleRange = new Vector2(70f, 250f);

        [Tooltip("Small random heading drift so the robot does not retrace identical paths.")]
        [SerializeField] float wanderDegreesPerSecond = 12f;

        // The built-in, for older code. Hidden in the Inspector: the Algorithm dropdown below sets it for a built-in.
        [HideInInspector]
        [SerializeField] MovementPattern movementPattern = MovementPattern.RandomBounce;

        [Header("Movement")]
        [Tooltip("The algorithm: a built-in, or one from the Algorithms folder. The controller (MovementBrain) picks up a change at its next step.")]
        [AlgorithmId(nameof(movementPattern))]
        [SerializeField] string algorithmId = "";

        [Tooltip("Seeds every random choice the vacuum makes. The same seed drives the same route each run.")]
        [SerializeField] int seed;

        Rigidbody2D body;
        CircleCollider2D circle;
        Battery battery;
        MovementBrain brain;

        float commandForward;
        float commandTurn;

        bool collided;
        Vector2 collisionPoint;

        Vector2 batterySamplePosition;
        float batterySampleHeading;
        bool hasBatterySample;

        Vector2 progressAnchor;
        float progressTimer;
        string stuckReason;

        readonly RaycastHit2D[] probeHits = new RaycastHit2D[8];

        /// <summary>Raised at the end of <see cref="ResetToSpawn"/>, once the body is back on its spawn point. The controller starts its run here.</summary>
        public event System.Action Respawned;

        /// <summary>Metres actually moved since the last reset (forward, reverse or pushed), for coverage read-outs.</summary>
        public float DistanceTravelled { get; private set; }

        /// <summary>
        /// Seconds of simulated time since the last reset, accumulated on the physics step while the battery can
        /// operate. The legacy brains steer by this rather than <see cref="Time.time"/>.
        /// </summary>
        public float SimTime { get; private set; }

        /// <summary>
        /// True when the controller stopped because its algorithm failed, or the body stopped a run that was going
        /// nowhere (the run then ends with an error).
        /// </summary>
        public bool AlgorithmFailed => stuckReason != null || (Brain != null && Brain.Failed);

        /// <summary>Why the algorithm was stopped, or null while it runs.</summary>
        public string FailureReason => stuckReason ?? (Brain != null && Brain.Failed ? Brain.Failure : null);

        /// <summary>How the robot's job ended, if it has (finished and docked, docked on low battery, or the dock unreachable).</summary>
        public Control.RunOutcome Outcome => Brain != null ? Brain.Outcome : Control.RunOutcome.None;

        /// <summary>Why, for an outcome that is a problem (e.g. the dock unreachable).</summary>
        public string OutcomeMessage => Brain != null ? Brain.OutcomeMessage : null;

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

        /// <summary>
        /// The movement algorithm, as one of the built-in patterns. Setting it selects that built-in (and clears any
        /// <see cref="AlgorithmId"/>). Switching mid-run: the controller carries on from where the robot is.
        /// </summary>
        public MovementPattern Pattern
        {
            get => movementPattern;
            set
            {
                movementPattern = value;
                algorithmId = "";
            }
        }

        /// <summary>
        /// The movement algorithm, by catalog id (<see cref="Control.AlgorithmCatalog"/>): a built-in, or a plugin from the
        /// Algorithms folder. Reads the pattern's id when no id was set. Setting a built-in's id also sets <see cref="Pattern"/>.
        /// </summary>
        public string AlgorithmId
        {
            get => string.IsNullOrEmpty(algorithmId) ? Control.AlgorithmCatalog.IdOf(movementPattern) : algorithmId;
            set
            {
                var pattern = Control.AlgorithmCatalog.PatternOf(value);
                if (pattern.HasValue) movementPattern = pattern.Value;
                algorithmId = pattern.HasValue ? "" : value ?? "";
            }
        }

        /// <summary>Seeds the vacuum's random choices. Takes effect at the next <see cref="ResetToSpawn"/>.</summary>
        public int Seed
        {
            get => seed;
            set => seed = value;
        }

        /// <summary>
        /// The shared random source for this run. SimulationRunner builds ONE of these per run and assigns it here,
        /// so every subsystem that draws randomness (the algorithm, sensor noise, and eventually debris
        /// accumulation) pulls from the same instance -- needed for RNG-state snapshot/restore to mean anything
        /// (SDD 2.9/2.10). <see cref="ResetToSpawn"/> rebuilds it from <see cref="Seed"/>, so a restart repeats the
        /// run from the top.
        /// </summary>
        public RandomProvider RandomSource { get; set; }

        public LevelData Level => levelRenderer != null ? levelRenderer.Level : null;

        /// <summary>The level as placed in the scene (the sensors and the Normal preload read it).</summary>
        public LevelRenderer LevelRenderer => levelRenderer;

        /// <summary>The controller on this object, or null (a bare body in a test).</summary>
        public MovementBrain Brain
        {
            get
            {
                if (brain == null) brain = GetComponent<MovementBrain>();
                return brain;
            }
        }

        /// <summary>The physics body. Its position and rotation are the TRUE pose.</summary>
        public Rigidbody2D Body => body;

        /// <summary>
        /// Where the body actually is. The transform is interpolated for smooth drawing, so it trails the
        /// physics state by part of a frame and moves with the frame rate; everything the simulation
        /// decides from reads this instead, or a headless run would disagree with a watched one.
        /// </summary>
        Vector3 WorldPosition => body != null ? (Vector3)body.position : transform.position;

        /// <summary>Where the vacuum is, in level metres.</summary>
        public Vector2 LevelPosition =>
            levelRenderer != null ? levelRenderer.WorldToLevel(WorldPosition) : (Vector2)WorldPosition;

        public float Radius => radius;

        /// <summary>Width of floor cleaned in one pass, in metres.</summary>
        public float CleaningWidth => radius * 2f;

        public Vector2 TurnAngleRange => turnAngleRange;
        public float WanderDegreesPerSecond => wanderDegreesPerSecond;
        public float BackupDuration => backupDuration;

        /// <summary>True while the battery has charge to move.</summary>
        public bool CanOperate => battery != null && battery.CanOperate;

        /// <summary>Forward speed limit on the floor it is on now, in metres per second.</summary>
        public float DriveSpeedNow => driveSpeed * SurfaceSpeedMultiplier();

        /// <summary>TRUE facing, in degrees counter-clockwise; 0 faces +y.</summary>
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

            // Every running body gets a controller. Added here, not with RequireComponent, so a bare body in an
            // edit-mode test stays bare, and the two components don't require each other in a loop.
            if (Application.isPlaying && GetComponent<MovementBrain>() == null)
                brain = gameObject.AddComponent<MovementBrain>();

            ResetBatteryMotionSample();
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

        // ---------------------------------------------------------------- commands

        /// <summary>
        /// What to do on this physics step: forward speed in m/s (negative = reverse) and turn rate in deg/s
        /// (positive = left). Clamped to [-reverse speed, drive speed × the floor's speed multiplier] and
        /// [-turn speed, turn speed]. Applied once, on this step's FixedUpdate, then cleared: a body that is not
        /// commanded again stops. Nothing moves when the battery is empty.
        /// </summary>
        public void Command(float forwardMetersPerSecond, float turnDegreesPerSecond)
        {
            commandForward = IsFinite(forwardMetersPerSecond) ? forwardMetersPerSecond : 0f;
            commandTurn = IsFinite(turnDegreesPerSecond) ? turnDegreesPerSecond : 0f;
        }

        /// <summary>
        /// Hands over (and clears) the collision the body had since the controller last asked. Collisions are
        /// reported by physics after a step, so the controller reads them at the start of the next.
        /// </summary>
        public bool TakeCollision(out Vector2 point)
        {
            point = collisionPoint;
            bool result = collided;
            collided = false;
            return result;
        }

        // ---------------------------------------------------------------- behaviour

        void FixedUpdate()
        {
            if (!Application.isPlaying || body == null) return;

            // The work done by the last step's motion is paid for before this step's command is applied.
            ConsumeCompletedWork();

            float forward = commandForward;
            float turn = commandTurn;
            commandForward = 0f;
            commandTurn = 0f;

            if (!battery.CanOperate || stuckReason != null)
            {
                body.linearVelocity = Vector2.zero;
                return;
            }

            float dt = Time.fixedDeltaTime;
            SimTime += dt;

            // Running costs something every second, whether or not the vacuum is moving.
            battery.ConsumeOperatingTime(dt);
            WatchProgress(dt);
            if (stuckReason != null)
            {
                body.linearVelocity = Vector2.zero;
                return;
            }

            forward = Mathf.Clamp(forward, -reverseSpeed, DriveSpeedNow);
            turn = Mathf.Clamp(turn, -turnSpeed, turnSpeed);

            body.linearVelocity = Forward * forward;
            if (Mathf.Abs(turn) > CommandEpsilon) body.MoveRotation(body.rotation + turn * dt);

            lastForward = forward;
            lastTurn = turn;
        }

        float lastForward;
        float lastTurn;

        /// <summary>
        /// Stops a run that has left the vacuum going nowhere, so a stuck run can't run forever. Only while the job
        /// is under way: a vacuum parked on its dock at the end is meant to sit still.
        /// </summary>
        void WatchProgress(float dt)
        {
            if (Outcome != Control.RunOutcome.None || (Brain != null && Brain.Failed))
            {
                progressTimer = 0f;
                return;
            }

            Vector2 position = body.position;
            if (Vector2.Distance(position, progressAnchor) > NoProgressDistance)
            {
                progressAnchor = position;
                progressTimer = 0f;
                return;
            }

            if ((progressTimer += dt) >= NoProgressSeconds)
            {
                stuckReason = $"No progress for {NoProgressSeconds:0} s: the algorithm appears stuck in a loop.";
                Debug.LogError($"Movement algorithm '{AlgorithmId}' stopped: {stuckReason}", this);
            }
        }

        void ConsumeCompletedWork()
        {
            Vector2 position = body.position;
            float heading = body.rotation;

            if (hasBatterySample)
            {
                float distance = Vector2.Distance(batterySamplePosition, position);
                float turn = Mathf.Abs(Mathf.DeltaAngle(batterySampleHeading, heading));
                Vector2 midpoint = (batterySamplePosition + position) * 0.5f;
                FloorType floor = levelRenderer != null ? levelRenderer.FloorTypeAtWorld(midpoint) : null;
                float surfaceMultiplier = floor != null ? floor.energyCostMultiplier : 1f;
                battery.ConsumeWork(distance, turn, surfaceMultiplier);
                DistanceTravelled += distance;

                if (IsMotionStalled(distance, turn)) battery.ConsumeStalledTime(Time.fixedDeltaTime);
            }

            batterySamplePosition = position;
            batterySampleHeading = heading;
            hasBatterySample = true;
        }

        /// <summary>Commanded to move on the last step, but didn't (pushing against something): the motors still draw power.</summary>
        bool IsMotionStalled(float distance, float turn)
        {
            if (Mathf.Abs(lastForward) > CommandEpsilon) return distance < StalledMovementThreshold;
            if (Mathf.Abs(lastTurn) > CommandEpsilon) return turn < StalledTurnThreshold;
            return false;
        }

        void ResetBatteryMotionSample()
        {
            if (body == null) return;

            batterySamplePosition = body.position;
            batterySampleHeading = body.rotation;
            hasBatterySample = true;
            lastForward = 0f;
            lastTurn = 0f;
        }

        float SurfaceSpeedMultiplier()
        {
            var floor = CurrentFloor;
            return floor != null ? floor.speedMultiplier : 1f;
        }

        /// <summary>
        /// LEGACY PATTERNS ONLY (Wall follow): distance from the robot's edge to the nearest wall or furniture along
        /// <paramref name="direction"/>, or positive infinity when nothing is within <paramref name="maxDistance"/>.
        /// It probes the TRUE world, which is why those patterns are refused in SLAM runs.
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

            collided = true;
            collisionPoint = collision.contactCount > 0 ? collision.GetContact(0).point : (Vector2)collision.transform.position;
        }

        /// <summary>Drops the robot back on the level's spawn point, clears its odometer, and tells the controller to start its run again.</summary>
        public void ResetToSpawn()
        {
            SimTime = 0f;
            commandForward = 0f;
            commandTurn = 0f;
            collided = false;
            stuckReason = null;
            progressTimer = 0f;

            // Back to the same random sequence, so a repeat of a run matches it step for step. A fresh
            // RandomProvider from the same seed reproduces the same per-subsystem streams from the top --
            // see RandomSource's doc comment for why this has to be a real rebuild, not just telling the
            // existing shared instance to rewind.
            RandomSource = new RandomProvider((uint)seed);

            if (levelRenderer != null && levelRenderer.Level != null)
            {
                transform.position = levelRenderer.LevelToWorld(
                    levelRenderer.Level.RobotSpawn, levelRenderer.WallDepth - 0.05f);
                transform.rotation = Quaternion.identity;

                DistanceTravelled = 0f;
                battery.ResetCharge();

                if (body != null)
                {
                    // Move the body itself, and face the same way every time: the transform alone would leave
                    // the first physics step starting from wherever the last run happened to end up.
                    body.position = transform.position;
                    body.rotation = 0f;
                    body.linearVelocity = Vector2.zero;
                    body.angularVelocity = 0f;
                }
                ResetBatteryMotionSample();
            }

            progressAnchor = body != null ? body.position : (Vector2)transform.position;
            Respawned?.Invoke();
        }

        static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

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
