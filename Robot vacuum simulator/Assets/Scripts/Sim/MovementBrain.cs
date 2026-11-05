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
    ///   2. pose       the believed pose: Normal = the true pose; SLAM = dead reckoning from noisy wheel odometry,
    ///                 then fitted by local SLAM to the current submap (which then takes the scan)
    ///   3. observe    readings become per-cell observations, placed using the BELIEVED pose
    ///   4. believe    observations are applied to the Internal Model (assigned in Normal; from the submaps in SLAM)
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
        readonly OdometrySensor odometry = new OdometrySensor();
        LidarSensor lidar;

        // SLAM only.
        LocalSlam localSlam;
        GlobalSlam globalSlam;
        RobotPose slamLocalPose; // SLAM: the pose in local SLAM's frame (the believed pose is it carried into the global frame)
        float startSpinRemaining;
        readonly DriftStats drift = new DriftStats();

        // Timing (real milliseconds spent, for result.json; reporting only, never read by the simulation).
        readonly System.Diagnostics.Stopwatch stopwatch = new System.Diagnostics.Stopwatch();
        double msSensing, msPose, msMapping, msLoopClosure;
        float msLongestStep;

        MovementPattern activePattern;
        ICleaningAlgorithm algorithm;
        LegacyPatternDriver legacy;
        readonly WaypointDriver driver = new WaypointDriver();

        RobotPose believedPose;
        RobotPose trueAtBelief; // the true pose at the same moment the believed pose was computed, for drift
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
        /// Distance between the believed and the true position, metres, both taken at the same physics step (odometry
        /// reports a step after it happens, so comparing with the body's current position would count that one-step
        /// lag as drift). Always 0 in Normal (no pose estimation).
        /// </summary>
        public float DriftMeters => settings.slam ? Vector2.Distance(believedPose.position, trueAtBelief.position) : 0f;

        /// <summary>Difference between the believed and the true heading, degrees (0–180), at the same step. Always 0 in Normal.</summary>
        public float DriftHeadingDegrees => settings.slam ? Mathf.Abs(Mathf.DeltaAngle(believedPose.heading, trueAtBelief.heading)) : 0f;

        /// <summary>SLAM only: local SLAM, its submaps and scan matching (null in a Normal run).</summary>
        public LocalSlam SlamTracking => localSlam;

        /// <summary>SLAM only: global SLAM, i.e. loop closure (null in a Normal run, or when it is switched off).</summary>
        public GlobalSlam SlamLoopClosure => globalSlam;

        /// <summary>SLAM only: the believed pose's error over the run so far, step by step (reporting only).</summary>
        public DriftStats Drift => settings.slam ? drift : null;

        /// <summary>Real milliseconds spent per simulated second on each part of the step (performance read-outs; reporting only). In SLAM, pose includes adding scans to the submaps.</summary>
        public float SensingMsPerSimSecond => simTime > 0f ? (float)(msSensing / simTime) : 0f;
        public float PoseMsPerSimSecond => simTime > 0f ? (float)(msPose / simTime) : 0f;
        public float MappingMsPerSimSecond => simTime > 0f ? (float)(msMapping / simTime) : 0f;
        public float LoopClosureMsPerSimSecond => simTime > 0f ? (float)(msLoopClosure / simTime) : 0f;

        /// <summary>The longest single physics step so far (sense + pose + map + loop closure), real milliseconds.</summary>
        public float LongestStepMs => msLongestStep;

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

            localSlam = settings.slam ? new LocalSlam(anchor, cellSize, settings.correctionQuality, settings.noiseScale) : null;
            globalSlam = settings.slam && settings.loopClosure ? new GlobalSlam(localSlam) : null;
            observations = new ObservationMapper(writer, settings.slam, localSlam, globalSlam);
            msSensing = msPose = msMapping = msLoopClosure = 0;
            msLongestStep = 0f;
            drift.Clear();

            if (!settings.slam && level != null)
                NormalPreload.Run(writer, level, levelRenderer);

            sensors.Clear();
            sensors.Add(new WhiskerSensor());
            lidar = new LidarSensor(settings.lidarEnabled, settings.lidarRangeMeters, settings.lidarHalfAngleDegrees,
                settings.lidarRayCount, cellSize);
            sensors.Add(lidar);
            sensors.Add(new FloorSensor(settings.floorSensorEnabled));

            // The robot knows where it starts (its dock / spawn): the believed pose begins exactly at the true pose.
            believedPose = TruePose();
            trueAtBelief = believedPose;
            odometry.Reset(believedPose.position, believedPose.heading);
            view.SetBelievedPose(believedPose);

            // SLAM: start with a full spin in place at the dock, mapping from the one pose known exactly.
            localSlam?.Begin(believedPose);
            globalSlam?.Begin();
            slamLocalPose = believedPose;
            startSpinRemaining = settings.slam ? LocalSlamConstants.StartSpinDegrees : 0f;

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
            stopwatch.Restart();
            frame.Clear();
            bool collided = robot.TakeCollision(out Vector2 contact);
            var context = new SensorContext(body.position, body.rotation, robot.Radius, body, levelRenderer,
                collided, contact, simTime, settings.slam,
                settings.slam ? settings.noiseScale : ControllerSettings.NormalNoiseScale, random);

            for (int i = 0; i < sensors.Count; i++)
                if (sensors[i].Enabled) sensors[i].Sense(context, frame);
            double stepMs = stopwatch.Elapsed.TotalMilliseconds;
            msSensing += stopwatch.Elapsed.TotalMilliseconds;

            // 2. Pose. Normal: the believed pose IS the true pose (no odometry, no noise read).
            //    SLAM: dead reckoning, then local SLAM fits the scan to the current submap and (after the motion
            //    filter) adds it to the submaps; that time counts as pose.
            stopwatch.Restart();
            trueAtBelief = TruePose();
            float odometryTurn = 0f;
            bool redraw = false;
            if (settings.slam)
            {
                slamLocalPose = DeadReckon(slamLocalPose, out _, out odometryTurn);
                slamLocalPose = localSlam.Track(frame, slamLocalPose, simTime);
                stepMs += stopwatch.Elapsed.TotalMilliseconds;
                msPose += stopwatch.Elapsed.TotalMilliseconds;

                // Global SLAM (loop closure): new submaps, the loop search, and a re-solve when due.
                stopwatch.Restart();
                if (globalSlam != null) redraw = globalSlam.AfterTrack(frame);
                believedPose = globalSlam != null ? globalSlam.ToGlobal(slamLocalPose) : slamLocalPose;
                stepMs += stopwatch.Elapsed.TotalMilliseconds;
                msLoopClosure += stopwatch.Elapsed.TotalMilliseconds;
            }
            else
            {
                believedPose = trueAtBelief;
                stepMs += stopwatch.Elapsed.TotalMilliseconds;
                msPose += stopwatch.Elapsed.TotalMilliseconds;
            }
            view.SetBelievedPose(believedPose);

            // 3 + 4. Observe and believe, placed by the believed pose.
            stopwatch.Restart();
            if (redraw) observations.RedrawFromSubmaps(); // the submaps moved: redraw before this step's updates
            observations.Apply(frame, believedPose);
            observations.ApplyFootprint(believedPose, robot.Radius, dt, vacuumPower);
            stepMs += stopwatch.Elapsed.TotalMilliseconds;
            msMapping += stopwatch.Elapsed.TotalMilliseconds;

            if (stepMs > msLongestStep) msLongestStep = (float)stepMs;
            if (settings.slam) drift.Add(DriftMeters, DriftHeadingDegrees);

            // 5 - 7. Goal, decide, command.
            float forward, turn;
            if (startSpinRemaining > 0f)
            {
                // SLAM start: spin in place at the dock before the algorithm takes over.
                startSpinRemaining -= Mathf.Abs(odometryTurn);
                forward = 0f;
                turn = LocalSlamConstants.StartSpinDegreesPerSecond;
            }
            else if (legacy != null)
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

        /// <summary>SLAM only: <paramref name="from"/> moved by this step's (noisy) odometry, integrated along the mid-turn heading.</summary>
        RobotPose DeadReckon(RobotPose from, out float forward, out float turn)
        {
            var truth = TruePose();
            var floor = levelRenderer != null ? levelRenderer.FloorTypeAtWorld(truth.position) : null;
            float ease = floor != null ? floor.speedMultiplier : 1f;

            odometry.Measure(truth.position, truth.heading, ease, slam: true, settings.noiseScale, random,
                out forward, out turn);

            float heading = from.heading + turn;
            Vector2 position = from.position + RobotPose.DirectionOf(from.heading + turn * 0.5f) * forward;
            return new RobotPose(position, Mathf.DeltaAngle(0f, heading)); // keep the heading in (-180, 180]
        }

        RobotPose TruePose() => body != null
            ? new RobotPose(body.position, body.rotation)
            : new RobotPose(transform.position, transform.eulerAngles.z);

        static bool IsFinite(Vector2 v) =>
            !float.IsNaN(v.x) && !float.IsNaN(v.y) && !float.IsInfinity(v.x) && !float.IsInfinity(v.y);
    }
}
