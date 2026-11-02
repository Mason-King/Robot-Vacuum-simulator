using System.Collections.Generic;
using RobotVacuum.Level;
using RobotVacuum.Sim.Control;
using RobotVacuum.Sim.Sensors;
using RobotVacuumSim.EnvironmentModel;
using UnityEngine;

namespace RobotVacuum.Sim
{
    /// <summary>
    /// THE CONTROLLER (the lower of the two layers; design doc, sections 2 and 4). It owns the sensors and is the
    /// only writer to the Internal Model. Every physics step, in this order:
    ///
    ///   1. sense      the sensors measure the world from the TRUE pose (they are physical)
    ///   2. pose       the believed pose: Normal = the true pose; SLAM = the true pose for now (noisy odometry is
    ///                 slice 2, correction slice 3)
    ///   3. observe    readings become per-cell observations, placed using the BELIEVED pose
    ///   4. believe    observations are applied to the Internal Model (assigned in Normal, weighted in SLAM)
    ///   5. goal       every 0.5 s simulated (and at once when a goal ends): report status, ask the algorithm
    ///   6. decide     drive toward the waypoint; local avoidance; recovery
    ///   7. command    forward speed + turn rate to the body (<see cref="VacuumRobot.Command"/>)
    ///
    /// The SLAM bool is the single gate. SLAM off (Normal): the Internal Model is preloaded with everything static in
    /// the level, readings are exact, beliefs are assigned, and the noise setting is never read (a constant 0 is
    /// carried instead). SLAM on: no preload, noisy readings, weighted belief updates.
    ///
    /// Added to every running <see cref="VacuumRobot"/> automatically. <see cref="SimulationRunner"/> calls
    /// <see cref="Configure"/> with the run's settings; a robot started any other way runs Normal with defaults.
    /// The run (re)starts each time the body respawns.
    /// </summary>
    [DefaultExecutionOrder(-50)] // after the session clock (-1000), before the body (0) applies the command
    [RequireComponent(typeof(VacuumRobot))]
    public sealed class MovementBrain : MonoBehaviour
    {
        /// <summary>Display names, in <see cref="MovementPattern"/> order.</summary>
        public static readonly string[] Labels = { "Random", "Spiral", "Wall follow", "Lawnmower" };

        ControllerSettings settings = ControllerSettings.Default;

        VacuumRobot robot;
        Rigidbody2D body;
        LevelRenderer levelRenderer;
        VacuumCleaningController cleaning;
        RandomProvider random;

        InternalModelWriter writer;
        InternalModelView view;
        ObservationMapper observations;

        readonly List<IRobotSensor> sensors = new List<IRobotSensor>();
        readonly SensorFrame frame = new SensorFrame();
        LidarSensor lidar;

        MovementPattern activePattern;
        ICleaningAlgorithm algorithm;
        LegacyPatternDriver legacy;
        readonly WaypointDriver driver = new WaypointDriver();

        RobotPose believedPose;
        float simTime;
        float nextGoalTime;
        float vacuumPower;
        bool ready;

        // ---- Public read-outs (the debug overlay, a future HUD, tests) ----------------------------------

        /// <summary>True once the run has started (the body respawned, or the first physics step ran).</summary>
        public bool Ready => ready;

        /// <summary>True when the algorithm threw or returned something invalid; the robot stops and the run ends with an error.</summary>
        public bool Failed { get; private set; }

        /// <summary>Why the algorithm failed, or null.</summary>
        public string Failure { get; private set; }

        /// <summary>The settings this run uses.</summary>
        public ControllerSettings Settings => settings;

        public bool Slam => settings.slam;

        /// <summary>The Internal Model, read-only, exactly as an algorithm sees it. Null until <see cref="Ready"/>.</summary>
        public InternalModelView Model => view;

        /// <summary>Where the robot believes it is.</summary>
        public RobotPose BelievedPose => believedPose;

        /// <summary>The waypoint being driven to, or null (also null for the legacy patterns, which have none).</summary>
        public Vector2? CurrentWaypoint => legacy == null ? driver.Goal : null;

        /// <summary>How the current waypoint is going.</summary>
        public GoalStatus CurrentGoalStatus => driver.Status;

        /// <summary>The algorithm in use, or null when a legacy pattern is running.</summary>
        public ICleaningAlgorithm Algorithm => algorithm;

        /// <summary>
        /// Distance between the believed and the true position, metres. Always 0 in Normal (no pose estimation).
        /// In SLAM it stays 0 until noisy odometry arrives in slice 2.
        /// </summary>
        public float DriftMeters => settings.slam && body != null ? Vector2.Distance(believedPose.position, body.position) : 0f;

        /// <summary>The lidar as built for this run (null before the run starts), for its range and ray count.</summary>
        public LidarSensor Lidar => lidar;

        // ---- Setup ---------------------------------------------------------------------------------------

        /// <summary>
        /// The run's settings. Call before the body's <see cref="VacuumRobot.ResetToSpawn"/>; they take effect when the
        /// run (re)starts.
        /// </summary>
        public void Configure(ControllerSettings runSettings)
        {
            settings = runSettings;
            ready = false;
        }

        void Awake()
        {
            robot = GetComponent<VacuumRobot>();
            body = GetComponent<Rigidbody2D>();
        }

        void OnEnable()
        {
            if (robot == null) robot = GetComponent<VacuumRobot>();
            if (robot != null) robot.Respawned += BeginRun;
        }

        void OnDisable()
        {
            if (robot != null) robot.Respawned -= BeginRun;
        }

        /// <summary>Starts (or restarts) the run: a fresh Internal Model, the Normal preload, new sensors, the algorithm.</summary>
        void BeginRun()
        {
            if (!Application.isPlaying) return;

            if (robot == null) robot = GetComponent<VacuumRobot>();
            if (body == null) body = GetComponent<Rigidbody2D>();
            levelRenderer = robot.LevelRenderer;
            cleaning = GetComponent<VacuumCleaningController>();

            Failed = false;
            Failure = null;
            simTime = 0f;
            nextGoalTime = 0f;
            driver.Reset();

            // The run's single shared random source (the body rebuilds it from the seed on every respawn).
            if (robot.RandomSource == null) robot.RandomSource = new RandomProvider((uint)robot.Seed);
            random = robot.RandomSource;

            // The Internal Model shares the External Model's lattice: same cell size, same anchor (the spawn).
            float cellSize = cleaning != null && cleaning.Grid != null ? cleaning.Grid.CellSizeMeters : SimDefaults.CellSizeMeters;
            vacuumPower = cleaning != null ? cleaning.VacuumPower : HeatmapDefaults.BaseVacuumPower;

            var level = robot.Level;
            Vector2 anchor = levelRenderer != null && level != null
                ? (Vector2)levelRenderer.LevelToWorld(level.RobotSpawn)
                : (body != null ? body.position : (Vector2)transform.position);

            writer = new InternalModelWriter(new InternalModelGrid(anchor, cellSize));
            view = new InternalModelView(writer);
            observations = new ObservationMapper(writer, settings.slam);

            if (!settings.slam && level != null)
                NormalPreload.Run(writer, level, levelRenderer);

            sensors.Clear();
            sensors.Add(new WhiskerSensor());
            lidar = new LidarSensor(settings.lidarEnabled, settings.lidarRangeMeters, settings.lidarHalfAngleDegrees,
                settings.lidarRayCount, cellSize);
            sensors.Add(lidar);

            believedPose = TruePose();
            view.SetBelievedPose(believedPose);

            activePattern = robot.Pattern;
            BuildAlgorithm(activePattern);

            ready = true;
        }

        void BuildAlgorithm(MovementPattern pattern)
        {
            algorithm = null;
            legacy = null;
            driver.Reset();
            nextGoalTime = simTime;

            if (settings.slam && !AlgorithmCatalog.SupportsSlam(pattern))
            {
                Fail(AlgorithmCatalog.SlamRefusal(pattern));
                return;
            }

            if (AlgorithmCatalog.IsLegacyPattern(pattern))
            {
                legacy = new LegacyPatternDriver(LegacyPatternBrain.Create(pattern, random), robot);
                if (legacy.Failed) Fail(legacy.Failure);
                return;
            }

            algorithm = AlgorithmCatalog.CreateGoalAlgorithm(pattern);
            try
            {
                algorithm.Initialize(new AlgorithmContext(random, robot.Radius, robot.CleaningWidth,
                    writer.Grid.CellSizeMeters, settings.slam, lidar != null && lidar.Enabled ? lidar.Range : 0f));
            }
            catch (System.Exception exception)
            {
                Fail($"Initialize failed: {exception.GetType().Name}: {exception.Message}");
            }
        }

        // ---- The step ------------------------------------------------------------------------------------

        void FixedUpdate()
        {
            if (!Application.isPlaying) return;
            if (robot == null || !robot.isActiveAndEnabled) return; // the run is frozen on display

            if (!ready) BeginRun(); // a robot nobody respawned (dropped straight into a scene)
            if (!ready) return;

            if (Failed)
            {
                robot.Command(0f, 0f);
                return;
            }
            if (!robot.CanOperate) return;

            float dt = Time.fixedDeltaTime;
            simTime += dt;

            if (robot.Pattern != activePattern) SwitchPattern(robot.Pattern);
            if (Failed) return;

            // 1. Sense, from the true pose.
            frame.Clear();
            bool collided = robot.TakeCollision(out Vector2 contact);
            var context = new SensorContext(body.position, body.rotation, robot.Radius, body, levelRenderer,
                collided, contact, simTime, settings.slam,
                settings.slam ? settings.noiseScale : ControllerSettings.NormalNoiseScale, random);

            for (int i = 0; i < sensors.Count; i++)
                if (sensors[i].Enabled) sensors[i].Sense(context, frame);

            // 2. Pose. Normal: the believed pose IS the true pose. SLAM: also the true pose until slice 2 adds odometry.
            believedPose = TruePose();
            view.SetBelievedPose(believedPose);

            // 3 + 4. Observe and believe, placed by the believed pose.
            observations.Apply(frame, believedPose);
            observations.ApplyFootprint(believedPose, robot.Radius, dt, vacuumPower);

            // 5 - 7. Goal, decide, command.
            float forward, turn;
            if (legacy != null)
            {
                legacy.Tick(dt, frame.bumped, out forward, out turn);
                if (legacy.Failed)
                {
                    Fail(legacy.Failure);
                    return;
                }
            }
            else
            {
                AskForGoalIfDue();
                if (Failed) return;

                // The full drive speed is commanded; the body caps it for the floor it is really on.
                var input = new DriveInput(believedPose, simTime, dt, frame.bumped, frame.bumpBearing, view,
                    robot.Radius, robot.DriveSpeedMetersPerSecond, robot.ReverseSpeedMetersPerSecond, robot.TurnSpeedDegreesPerSecond);
                driver.Step(input, out forward, out turn);
            }

            robot.Command(forward, turn);
        }

        void AskForGoalIfDue()
        {
            var status = driver.Status;
            bool goalEnded = (status == GoalStatus.Reached || status == GoalStatus.Blocked || status == GoalStatus.Unreachable)
                             && !driver.StatusReported;

            if (simTime < nextGoalTime && !goalEnded) return;
            nextGoalTime = simTime + ControllerConstants.GoalPeriodSeconds;

            Vector2? next;
            try
            {
                next = algorithm.NextGoal(view, believedPose, status);
            }
            catch (System.Exception exception)
            {
                Fail($"NextGoal failed: {exception.GetType().Name}: {exception.Message}");
                return;
            }

            driver.StatusReported = true;

            if (next.HasValue && !IsFinite(next.Value))
            {
                Fail($"NextGoal returned an invalid waypoint: {next.Value}.");
                return;
            }

            var current = driver.Goal;
            bool same = next.HasValue && current.HasValue && (next.Value - current.Value).sqrMagnitude < 1e-8f;

            if (same && status == GoalStatus.Blocked) driver.RetryAfterBlocked(believedPose, simTime, view);
            else if (!same || status == GoalStatus.InProgress) driver.SetGoal(next, believedPose, simTime, view);
            // Same waypoint after Reached / Unreachable: leave it finished; the algorithm is asked again next period.
        }

        void SwitchPattern(MovementPattern pattern)
        {
            if (settings.slam && !AlgorithmCatalog.SupportsSlam(pattern))
            {
                // Refuse the switch rather than end the run: put the selection back.
                Debug.LogWarning(AlgorithmCatalog.SlamRefusal(pattern) + " Keeping the current algorithm.", this);
                robot.Pattern = activePattern;
                return;
            }

            activePattern = pattern;
            BuildAlgorithm(pattern);
        }

        void Fail(string failure)
        {
            if (Failed) return;

            Failed = true;
            Failure = failure;
            if (robot != null) robot.Command(0f, 0f);
            Debug.LogError($"Movement algorithm '{AlgorithmCatalog.NameOf(activePattern)}' stopped: {failure}", this);
        }

        RobotPose TruePose() => body != null
            ? new RobotPose(body.position, body.rotation)
            : new RobotPose(transform.position, transform.eulerAngles.z);

        static bool IsFinite(Vector2 v) =>
            !float.IsNaN(v.x) && !float.IsNaN(v.y) && !float.IsInfinity(v.x) && !float.IsInfinity(v.y);
    }
}
