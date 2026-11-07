using System.Collections.Generic;
using RobotVacuum.AlgorithmApi;
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
        double msSensing, msPose, msMapping, msLoopClosure, msControl, msAlgorithm;
        float msLongestStep;

        string activeAlgorithmId;
        AlgorithmEntry activeEntry;
        ICleaningAlgorithm algorithm;
        BeliefMapAdapter map; // the view, behind the algorithm API
        PathPlanner planner;  // the controller's, also offered to the algorithm through the map

        // What the algorithm asks of the driving (speed mode, pass-through goals, finished), and what the current
        // goal was given with.
        DriveRequests drive = new DriveRequests();
        DriveMode goalMode = DriveMode.Travel;
        float goalExposure;
        float targetCleanliness = 2f;

        // The dock: where the run started (the one pose known exactly), and the trip back to it.
        enum Homing { None, Finished, LowBattery }
        Homing homing;
        bool parked;
        int homeAttempts;
        Vector2 dockPosition;
        float nextBatteryCheckTime;
        Battery battery;
        readonly List<Vector2> homePath = new List<Vector2>();
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

        /// <summary>The planned path to the waypoint (start, corners, waypoint); empty when there is none (no planner, a legacy pattern, no goal).</summary>
        public IReadOnlyList<Vector2> CurrentPath => legacy == null ? driver.Path : (IReadOnlyList<Vector2>)System.Array.Empty<Vector2>();

        /// <summary>Re-routes used on the current waypoint, and re-plans of any kind this run (reporting).</summary>
        public int CurrentPathCorner => driver.PathCorner;
        public int CurrentReroutes => driver.Reroutes;
        public int Replans => driver.Replans;

        /// <summary>How the job ended, once it has: finished and docked, docked on low battery, or the dock unreachable.</summary>
        public RunOutcome Outcome { get; private set; }

        /// <summary>The reason, for an outcome that is a problem.</summary>
        public string OutcomeMessage { get; private set; }

        /// <summary>True while the robot is driving back to the dock (finished, or battery low).</summary>
        public bool ReturningToDock => homing != Homing.None && !parked;

        /// <summary>The body planner (clearance = radius + margin), or null before the run starts.</summary>
        public PathPlanner Planner => planner;

        /// <summary>How the current waypoint is going.</summary>
        public GoalStatus CurrentGoalStatus => driver.Status;

        /// <summary>The algorithm in use, or null when a legacy pattern is running.</summary>
        public ICleaningAlgorithm Algorithm => algorithm;

        /// <summary>The catalog entry of the algorithm in use (null before the run starts, or if its id was not found).</summary>
        public AlgorithmEntry AlgorithmEntry => activeEntry;

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

        /// <summary>Real milliseconds per simulated second on the goal and driving: the algorithm, path planning and re-routing.</summary>
        public float ControlMsPerSimSecond => simTime > 0f ? (float)(msControl / simTime) : 0f;

        /// <summary>The part of <see cref="ControlMsPerSimSecond"/> spent inside the algorithm's NextGoal (including any paths it asks for).</summary>
        public float AlgorithmMsPerSimSecond => simTime > 0f ? (float)(msAlgorithm / simTime) : 0f;

        /// <summary>Real milliseconds per simulated second in the path planner: plans and leg checks, whoever asked (the driver or the algorithm).</summary>
        public float PlanningMsPerSimSecond => simTime > 0f && planner != null ? (float)(planner.Milliseconds / simTime) : 0f;

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
            targetCleanliness = cleaning != null && cleaning.Grid != null ? cleaning.Grid.TargetMaxClean : HeatmapDefaults.TargetMaxClean;
            battery = GetComponent<Battery>();
            homing = Homing.None;
            parked = false;
            Outcome = RunOutcome.None;
            OutcomeMessage = null;
            nextBatteryCheckTime = 0f;

            var level = robot.Level;
            Vector2 anchor = levelRenderer != null && level != null
                ? (Vector2)levelRenderer.LevelToWorld(level.RobotSpawn)
                : (body != null ? body.position : (Vector2)transform.position);

            writer = new InternalModelWriter(new InternalModelGrid(anchor, cellSize));
            view = new InternalModelView(writer);
            planner = new PathPlanner(view, robot.Radius + ControllerConstants.ClearanceMarginMeters);
            driver.Planner = ControllerConstants.PlanPaths ? planner : null;
            map = new BeliefMapAdapter(view, planner);

            localSlam = settings.slam ? new LocalSlam(anchor, cellSize, settings.correctionQuality, settings.noiseScale) : null;
            globalSlam = settings.slam && settings.loopClosure ? new GlobalSlam(localSlam) : null;
            observations = new ObservationMapper(writer, settings.slam, localSlam, globalSlam);
            view.SetExploredSource(settings.slam ? observations.IsDrawn : (System.Func<int, int, bool>)null);
            msSensing = msPose = msMapping = msLoopClosure = msControl = msAlgorithm = 0;
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
            dockPosition = believedPose.position;
            odometry.Reset(believedPose.position, believedPose.heading);
            view.SetBelievedPose(believedPose);

            // SLAM: start with a full spin in place at the dock, mapping from the one pose known exactly.
            localSlam?.Begin(believedPose);
            globalSlam?.Begin();
            slamLocalPose = believedPose;
            startSpinRemaining = settings.slam ? LocalSlamConstants.StartSpinDegrees : 0f;

            activeAlgorithmId = robot.AlgorithmId;
            BuildAlgorithm(activeAlgorithmId);

            ready = true;
        }

        void BuildAlgorithm(string id)
        {
            algorithm = null;
            legacy = null;
            driver.Reset();
            nextGoalTime = simTime;

            activeEntry = AlgorithmCatalog.Find(id);
            string refusal = AlgorithmCatalog.Refusal(id, settings.slam);
            if (refusal != null)
            {
                Fail(refusal);
                return;
            }

            if (activeEntry.IsLegacy)
            {
                legacy = new LegacyPatternDriver(LegacyPatternBrain.Create(activeEntry.LegacyPattern.Value, random), robot);
                if (legacy.Failed) Fail(legacy.Failure);
                return;
            }

            try
            {
                algorithm = activeEntry.Create();
                drive = new DriveRequests();
                goalMode = DriveMode.Travel;
                algorithm.Initialize(new AlgorithmContext(new ProviderRandom(random), robot.Radius, robot.CleaningWidth,
                    writer.Grid.CellSizeMeters, settings.slam, lidar != null && lidar.Enabled ? lidar.Range : 0f,
                    targetCleanliness, vacuumPower, drive, settings.cleaningPriority));
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

            if (robot.AlgorithmId != activeAlgorithmId) SwitchAlgorithm(robot.AlgorithmId);
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
            else if (parked)
            {
                forward = turn = 0f;
            }
            else
            {
                stopwatch.Restart();
                if (homing == Homing.None)
                {
                    AskForGoalIfDue();
                    if (Failed) return;
                    if (drive.Finished) StartHoming(Homing.Finished);
                    else CheckBattery();
                }

                // Travel: the full drive speed is commanded; the body caps it for the floor it is really on.
                // Sweep: slow enough for the floor under the brush to reach the target in one pass.
                float speed = robot.DriveSpeedMetersPerSecond;
                if (homing == Homing.None && goalMode == DriveMode.Sweep) speed = Mathf.Min(speed, SweepSpeedHere());

                var input = new DriveInput(believedPose, simTime, dt, frame.bumped, frame.bumpBearing, view,
                    robot.Radius, speed, robot.ReverseSpeedMetersPerSecond, robot.TurnSpeedDegreesPerSecond);
                driver.Step(input, out forward, out turn);
                if (homing != Homing.None) CheckHomeArrival();
                if (parked) forward = turn = 0f;
                msControl += stopwatch.Elapsed.TotalMilliseconds;
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
                long started = System.Diagnostics.Stopwatch.GetTimestamp();
                Vec2? picked;
                try
                {
                    picked = algorithm.NextGoal(map, believedPose.ToApi(), status);
                }
                finally
                {
                    msAlgorithm += (System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                }
                next = picked.HasValue ? picked.Value.ToUnity() : (Vector2?)null;
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

            if (drive.Finished) return; // done: the trip home starts this step

            goalMode = drive.Mode;
            goalExposure = drive.SweepExposureMeters;
            if (same && status == GoalStatus.Blocked) driver.RetryAfterBlocked(believedPose, simTime, view);
            else if (!same || status == GoalStatus.InProgress) driver.SetGoal(next, believedPose, simTime, view, drive.PassThrough);
            // Same waypoint after Reached / Unreachable: leave it finished; the algorithm is asked again next period.
        }

        // ---- Sweep speed --------------------------------------------------------------------------------------

        /// <summary>
        /// The sweep speed for the floor under the brush now: v = exposure × power / (resistance × target), with the
        /// HIGHEST believed vacuum resistance under the brush (so a tough patch at its edge still gets clean), slowed
        /// a little more for safety (drift between lanes). The floor sensor keeps the resistance belief current as the
        /// robot rolls onto new floor.
        /// </summary>
        float SweepSpeedHere()
        {
            float resistance = 1f;
            float radius = robot.Radius;
            Vector2 p = believedPose.position;
            if (view.TryWorldToCell(p - new Vector2(radius, radius), out var low) && view.TryWorldToCell(p + new Vector2(radius, radius), out var high))
            {
                for (int row = low.row; row <= high.row; row++)
                    for (int col = low.col; col <= high.col; col++)
                        if ((view.CellCenter(row, col) - p).sqrMagnitude <= radius * radius)
                            resistance = Mathf.Max(resistance, view.GetCell(row, col).vacuumResistanceBelief);
            }
            float speed = CoverageMath.SweepSpeed(goalExposure, vacuumPower, resistance, targetCleanliness) / ControllerConstants.SweepSpeedSafety;
            return Mathf.Max(ControllerConstants.SweepSpeedFloor, speed);
        }

        // ---- The dock ------------------------------------------------------------------------------------------

        /// <summary>Low battery: if the charge is down to what the trip home would cost (plus a margin), head home now.</summary>
        void CheckBattery()
        {
            if (battery == null || simTime < nextBatteryCheckTime) return;
            nextBatteryCheckTime = simTime + ControllerConstants.BatteryCheckSeconds;

            if (!planner.FindPath(believedPose.position, dockPosition, homePath, out float length)) return; // no way home anyway
            float turning = 180f + 90f * Mathf.Max(0, homePath.Count - 2); // turning to set off, and at each corner
            float cost = (length * battery.DistanceEnergyPerMeter + turning * battery.TurnEnergyPerDegree) * ControllerConstants.HomeFloorMargin;
            float reserve = cost * ControllerConstants.HomeReserveFactor + ControllerConstants.HomeReserveExtra;
            if (battery.CurrentCharge <= reserve) StartHoming(Homing.LowBattery);
        }

        void StartHoming(Homing why)
        {
            homing = why;
            homeAttempts = 0;
            goalMode = DriveMode.Travel;
            driver.Reset();
            driver.SetGoal(dockPosition, believedPose, simTime, view);
            Debug.Log(why == Homing.Finished
                ? $"{AlgorithmCatalog.NameOf(activeAlgorithmId)} finished at {simTime:F0} s: returning to the dock."
                : $"Battery low at {simTime:F0} s ({battery.CurrentCharge:F0} left): returning to the dock.", this);
        }

        /// <summary>On the way home: arrived, retry after Blocked (a few times), or give up and park where it is.</summary>
        void CheckHomeArrival()
        {
            switch (driver.Status)
            {
                case GoalStatus.Reached:
                    Park(homing == Homing.Finished ? RunOutcome.Completed : RunOutcome.DockedLowBattery, null);
                    break;

                case GoalStatus.Blocked when homeAttempts < ControllerConstants.HomeAttempts:
                    homeAttempts++;
                    driver.RetryAfterBlocked(believedPose, simTime, view);
                    break;

                case GoalStatus.Blocked:
                case GoalStatus.Unreachable:
                    var at = believedPose.position;
                    Park(RunOutcome.DockUnreachable,
                        $"The dock could not be reached ({(homing == Homing.Finished ? "job finished" : "battery low")}): " +
                        $"{(driver.Status == GoalStatus.Unreachable ? "no path" : "blocked")} from ({at.x:F2}, {at.y:F2}) to ({dockPosition.x:F2}, {dockPosition.y:F2}).");
                    break;
            }
        }

        void Park(RunOutcome outcome, string message)
        {
            parked = true;
            Outcome = outcome;
            OutcomeMessage = message;
            if (message != null) Debug.LogWarning(message, this);
        }

        void SwitchAlgorithm(string id)
        {
            string refusal = AlgorithmCatalog.Refusal(id, settings.slam);
            if (refusal != null)
            {
                // Refuse the switch rather than end the run: put the selection back.
                Debug.LogWarning(refusal + " Keeping the current algorithm.", this);
                robot.AlgorithmId = activeAlgorithmId;
                return;
            }

            activeAlgorithmId = id;
            BuildAlgorithm(id);
        }

        void Fail(string failure)
        {
            if (Failed) return;

            Failed = true;
            Failure = failure;
            if (robot != null) robot.Command(0f, 0f);
            Debug.LogError($"Movement algorithm '{AlgorithmCatalog.NameOf(activeAlgorithmId)}' stopped: {failure}", this);
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
