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
        float msLongestPerceptionStep; // sense + pose + map + loop closure
        float msLongestFullStep;       // the whole step, the algorithm's decision and the driver included
        readonly double[] lagParts = new double[(int)LagLog.Part.Count]; // this step's parts, for the lag log
        MapAnalysis mapAnalysis; // the algorithm's map analysis, for the lag log's breakdown

        string activeAlgorithmId;
        AlgorithmEntry activeEntry;
        ICleaningAlgorithm algorithm;
        readonly AlgorithmHost host = new AlgorithmHost(); // every call into the algorithm goes through it (2.11)
        double plannerMsCarried; // planner time from planners replaced after an algorithm fault (reporting)
        int mainThreadId;        // the algorithm's log lines name the robot only from the main thread
        BeliefMapAdapter map; // the view, behind the algorithm API
        PathPlanner planner;  // the controller's, also offered to the algorithm through the map

        // What the algorithm asks of the driving (speed mode, pass-through goals, finished), and what the current
        // goal was given with.
        DriveRequests drive = new DriveRequests();
        DriveMode goalMode = DriveMode.Travel;
        float goalExposure;
        float goalTargetFraction = 1f;
        float targetCleanliness = 2f;

        // The dock: where the run started (the one pose known exactly), and the trip back to it.
        enum Homing { None, Finished, LowBattery }
        Homing homing;
        bool parked;
        int homeAttempts;

        // Boxed in or cut off (see ControllerConstants.BoxedInCutOffCells): waiting until this sim time (negative: not waiting),
        // whether to set off home again after the wait (else ask the algorithm again), and the tries so far.
        float boxedWaitUntil = -1f;
        bool boxedResumeHome;
        int boxedTries;     // when the algorithm finished
        int boxedHomeTries; // on the way home (counted apart, so a boxing-in at the finish leaves the trip home its own tries)
        MapAnalysis boxedAnalysis; // for the check when the algorithm has none of its own
        // Cut off at the finish: first drive to the reachable floor nearest the cut-off floor, so the sensors watch the
        // blockage (the map only clears where the lidar sees), then wait there. True while driving there.
        bool boxedGoingToLook;
        bool boxedFinalHeld, boxedHomeFinalHeld; // the long last hold (BoxedInFinalHoldSeconds) has been used
        // No way home with the usual clearance: one try along a path planned with less clearance (the belief map's
        // cells make a sitting cat look wider than it is), driven with this planner.
        PathPlanner squeezePlanner;
        bool squeezing;
        readonly List<Vector2> squeezePath = new List<Vector2>();
        bool boxedFinalHolding;  // in the long last hold now: checked every BoxedInFinalPollSeconds, left as soon as the way opens
        float nextBoxedPoll;
        float boxedLookWait;    // the wait to start on arrival
        // The algorithm's last goal that ended Blocked or Unreachable, and when; and, once a blockage next to it has been
        // found, that goal kept as the place to watch until the algorithm finds work again.
        Vector2 lastFailedGoal;
        float lastFailedTime = float.NegativeInfinity;
        Vector2? blockageFocus;
        bool lastBoxedByBlockage; // the last BoxedIn found a blockage (not cut-off floor)
        int lastUnmappedEdge;     // the last BoxedIn: unmapped cells bordering the cut-off side
        // Every floor cell the robot has been able to reach this run (sampled), and the checks made against it.
        readonly BlockageMap blockage = new BlockageMap();
        string noWaitReason; // the way home failed and WaitIfBoxedIn chose not to wait: why (for the Dock Unreachable message)
        float nextReachSample;
        Vector2 dockPosition;
        float nextBatteryCheckTime;
        float nextBatteryCheckCharge;   // the next check waits until the charge has fallen to this
        float lastBatteryCheckCharge;   // the charge at the last check: a higher charge means a recharge
        Battery battery;
        readonly List<Vector2> homePath = new List<Vector2>();
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

        /// <summary>
        /// True when the algorithm failed: it threw, hung past its time budget, returned something invalid, or can't
        /// be used in this run. The robot stops where it is; the session stops the run (2.11).
        /// </summary>
        public bool Failed { get; private set; }

        /// <summary>Why the algorithm failed, in one line, or null.</summary>
        public string Failure { get; private set; }

        /// <summary>The algorithm's failure in full (kind, call, exception, stack, when), or null.</summary>
        public AlgorithmFault Fault { get; private set; }

        /// <summary>
        /// The simulation's own code (sensing, pose, mapping, driving: not the algorithm) threw during a step. The run
        /// can't be trusted after that, so the session ends it (a Terminal failure). Null if not.
        /// </summary>
        public System.Exception SimException { get; private set; }

        /// <summary>The settings this run uses.</summary>
        public ControllerSettings Settings => settings;

        public bool Slam => settings.slam;

        /// <summary>The Internal Model, read-only, exactly as an algorithm sees it. Null until <see cref="Ready"/>.</summary>
        public InternalModelView Model => view;

        /// <summary>Where the robot believes it is.</summary>
        public RobotPose BelievedPose => believedPose;

        /// <summary>The waypoint being driven to, or null.</summary>
        public Vector2? CurrentWaypoint => driver.Goal;

        /// <summary>The planned path to the waypoint (start, corners, waypoint); empty when there is none (no planner, no goal).</summary>
        public IReadOnlyList<Vector2> CurrentPath => driver.Path;

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

        /// <summary>
        /// Blockages this run (cut off, blocked, or no way home that led to a wait), and whether the run ended by giving
        /// up on one: the floor left behind (m², still to clean, as the robot believed) and the warning given.
        /// </summary>
        public int BlockageEvents { get; private set; }
        public bool EndedByBlockage { get; private set; }
        public float BlockageLeftSquareMeters { get; private set; }
        public string BlockageReason { get; private set; }

        /// <summary>
        /// True while the robot holds still on purpose, waiting for a blockage (the cat) to clear. The robot's
        /// no-progress check skips this time.
        /// </summary>
        public bool WaitingForBlockage => simTime < boxedWaitUntil && !parked;

        /// <summary>The body planner (clearance = radius + margin), or null before the run starts.</summary>
        public PathPlanner Planner => planner;

        /// <summary>How the current waypoint is going.</summary>
        public GoalStatus CurrentGoalStatus => driver.Status;

        /// <summary>The algorithm in use (null before the run starts or if it failed to build).</summary>
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

        /// <summary>
        /// The true pose at the step the believed pose was computed (world frame), paired with <see cref="BelievedPose"/>
        /// so a recorder can split the drift into its direction (reporting only). Equals the believed pose in Normal.
        /// </summary>
        public RobotPose TrueAtBelief => trueAtBelief;

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
        public float PlanningMsPerSimSecond => simTime > 0f && planner != null ? (float)((planner.Milliseconds + plannerMsCarried) / simTime) : 0f;

        /// <summary>
        /// The longest perception part of a physics step so far: sensing, pose, mapping and loop closure, real milliseconds.
        /// Not the algorithm's decision, the driver or path planning: see <see cref="LongestFullStepMs"/>.
        /// </summary>
        public float LongestPerceptionStepMs => msLongestPerceptionStep;

        /// <summary>
        /// The longest whole physics step so far, real milliseconds: perception plus the goal, the algorithm's decision,
        /// the driver and path planning (what the lag log calls the worst step).
        /// </summary>
        public float LongestFullStepMs => msLongestFullStep;

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
            mainThreadId = System.Threading.Thread.CurrentThread.ManagedThreadId;
        }

        // The algorithm's worker thread ends with the robot (a hung one was already dealt with when it hung).
        void OnDestroy() => host.Dispose();

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
            Fault = null;
            SimException = null;
            plannerMsCarried = 0.0;
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
            boxedWaitUntil = -1f;
            boxedResumeHome = false;
            boxedTries = 0;
            boxedHomeTries = 0;
            boxedGoingToLook = false;
            boxedLookWait = 0f;
            boxedFinalHeld = boxedHomeFinalHeld = false;
            boxedFinalHolding = false;
            nextBoxedPoll = 0f;
            BlockageEvents = 0;
            EndedByBlockage = false;
            BlockageLeftSquareMeters = 0f;
            BlockageReason = null;
            squeezing = false;
            squeezePlanner = null;
            lastFailedTime = float.NegativeInfinity;
            blockageFocus = null;
            lastBoxedByBlockage = false;
            blockage.Clear();
            noWaitReason = null;
            nextReachSample = 0f;
            Outcome = RunOutcome.None;
            OutcomeMessage = null;
            nextBatteryCheckTime = 0f;
            nextBatteryCheckCharge = float.PositiveInfinity;
            lastBatteryCheckCharge = float.NegativeInfinity; // so the first step checks at once

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
            msLongestPerceptionStep = 0f;
            msLongestFullStep = 0f;
            drift.Clear();
            LagLog.BeginRun(planner, observations);
            if (localSlam != null) LagLog.SetSlam(localSlam, globalSlam);

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
            driver.Reset();
            driver.PathNotice = settings.logAlgorithmDecisions
                ? message => Debug.LogWarning($"[Planner {simTime:F1} s] {message}", this)
                : (System.Action<string>)null;
            nextGoalTime = simTime;

            activeEntry = AlgorithmCatalog.Find(id);
            string refusal = AlgorithmCatalog.Refusal(id, settings.slam);
            if (refusal != null)
            {
                Fail(new AlgorithmFault { Kind = AlgorithmFaultKind.Refused, Call = AlgorithmCall.Create, Message = refusal });
                return;
            }

            // Built on the algorithm's own thread (AlgorithmHost): a constructor or Initialize that throws or never
            // returns stops the run without freezing the program.
            var created = host.Create(activeEntry);
            if (!created.Ok)
            {
                Fail(created.Fault, AlgorithmCall.Create);
                return;
            }
            if (created.Value == null)
            {
                Fail(new AlgorithmFault { Kind = AlgorithmFaultKind.InvalidOutput, Call = AlgorithmCall.Create, Message = "creating it gave nothing (null)." });
                return;
            }

            drive = new DriveRequests();
            goalMode = DriveMode.Travel;
            System.Action<string> log = null;
            if (settings.logAlgorithmDecisions)
            {
                string name = AlgorithmCatalog.NameOf(id);
                // Called on the algorithm's thread: Debug.Log is safe there, but the robot is only named from the main thread.
                log = message =>
                {
                    string line = $"[{name} {simTime:F1} s] {message}";
                    if (System.Threading.Thread.CurrentThread.ManagedThreadId == mainThreadId) Debug.Log(line, this);
                    else Debug.Log(line);
                };
            }
            var context = new AlgorithmContext(new ProviderRandom(random), robot.Radius, robot.CleaningWidth,
                writer.Grid.CellSizeMeters, settings.slam, lidar != null && lidar.Enabled ? lidar.Range : 0f,
                targetCleanliness, vacuumPower, drive, mapAnalysis = new MapAnalysis(map), log, () => simTime,
                () => robot.DriveSpeedMetersPerSecond, () => robot.TurnSpeedDegreesPerSecond);

            var initialized = host.Initialize(created.Value, context);
            if (!initialized.Ok)
            {
                Fail(initialized.Fault, AlgorithmCall.Initialize);
                return;
            }

            algorithm = created.Value;
            LagLog.SetMapAnalysis(mapAnalysis);
        }

        // ---- The step ------------------------------------------------------------------------------------

        void FixedUpdate()
        {
            if (!Application.isPlaying) return;
            if (robot == null || !robot.isActiveAndEnabled) return; // the run is frozen on display

            if (!ready) BeginRun(); // a robot nobody respawned (dropped straight into a scene)
            if (!ready) return;
            if (SimException != null)
            {
                robot.Command(0f, 0f); // the run is being ended (Terminal): stay still until it is
                return;
            }

            // Everything below is the simulation's own code; the algorithm's calls are caught inside it (AlgorithmHost).
            // Anything thrown here is a fault in the simulation itself: stop, and let the session end the run.
            try
            {
                Step();
            }
            catch (System.Exception exception)
            {
                SimException = exception;
                robot.Command(0f, 0f);
                Debug.LogError($"The simulation failed during a step at {simTime:F1} s (not the algorithm): " +
                               $"{exception.GetType().Name}: {exception.Message}. The run will be ended.\n{exception.StackTrace}", this);
            }
        }

        void Step()
        {
            if (Failed)
            {
                // Picking another algorithm is how a failed one is replaced, so a new choice is still built here.
                if (robot.AlgorithmId != activeAlgorithmId) SwitchAlgorithm(robot.AlgorithmId);
                if (Failed)
                {
                    robot.Command(0f, 0f);
                    return;
                }
            }
            if (!robot.CanOperate) return;

            float dt = Time.fixedDeltaTime;
            simTime += dt;
            writer.Clock = simTime; // for telling flickering cells (thin furniture) from something that moved in
            long fullStepStarted = System.Diagnostics.Stopwatch.GetTimestamp(); // for msLongestFullStep

            if (robot.AlgorithmId != activeAlgorithmId) SwitchAlgorithm(robot.AlgorithmId);
            if (Failed) return;

            // 1. Sense, from the true pose.
            stopwatch.Restart();
            frame.Clear();
            bool collided = robot.TakeCollision(out Vector2 contact);
            var context = new SensorContext(body.position, body.rotation, robot.Shape, body, levelRenderer,
                collided, contact, simTime, settings.slam,
                settings.slam ? settings.noiseScale : ControllerSettings.NormalNoiseScale, random);

            for (int i = 0; i < sensors.Count; i++)
            {
                if (!sensors[i].Enabled) continue;
                long sensorStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                sensors[i].Sense(context, frame);
                if (LagLog.Enabled) LagLog.AddSensorTime(sensors[i], sensorStarted);
            }
            double stepMs = stopwatch.Elapsed.TotalMilliseconds;
            msSensing += stopwatch.Elapsed.TotalMilliseconds;
            System.Array.Clear(lagParts, 0, lagParts.Length);
            lagParts[(int)LagLog.Part.Sensing] = stopwatch.Elapsed.TotalMilliseconds;

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
                lagParts[(int)LagLog.Part.Track] = stopwatch.Elapsed.TotalMilliseconds;

                // Global SLAM (loop closure): new submaps, the loop search, and a re-solve when due.
                stopwatch.Restart();
                if (globalSlam != null) redraw = globalSlam.AfterTrack(frame);
                believedPose = globalSlam != null ? globalSlam.ToGlobal(slamLocalPose) : slamLocalPose;
                stepMs += stopwatch.Elapsed.TotalMilliseconds;
                msLoopClosure += stopwatch.Elapsed.TotalMilliseconds;
                if (globalSlam != null)
                {
                    lagParts[(int)LagLog.Part.Search] = globalSlam.LastSearchMs;
                    lagParts[(int)LagLog.Part.Solve] = globalSlam.LastSolveMs;
                    lagParts[(int)LagLog.Part.LoopOther] = System.Math.Max(0.0,
                        stopwatch.Elapsed.TotalMilliseconds - globalSlam.LastSearchMs - globalSlam.LastSolveMs);
                }
            }
            else
            {
                believedPose = trueAtBelief;
                stepMs += stopwatch.Elapsed.TotalMilliseconds;
                msPose += stopwatch.Elapsed.TotalMilliseconds;
                lagParts[(int)LagLog.Part.Track] = stopwatch.Elapsed.TotalMilliseconds;
            }
            view.SetBelievedPose(believedPose);

            // 3 + 4. Observe and believe, placed by the believed pose.
            stopwatch.Restart();
            if (redraw) observations.RedrawFromSubmaps(); // the submaps moved: redraw before this step's updates
            double redrawMs = stopwatch.Elapsed.TotalMilliseconds;
            observations.Apply(frame, believedPose);
            observations.ApplyFootprint(believedPose, robot.Shape, dt, vacuumPower);
            stepMs += stopwatch.Elapsed.TotalMilliseconds;
            msMapping += stopwatch.Elapsed.TotalMilliseconds;
            lagParts[(int)LagLog.Part.Redraw] = redrawMs;
            lagParts[(int)LagLog.Part.Mapping] = stopwatch.Elapsed.TotalMilliseconds - redrawMs;

            if (stepMs > msLongestPerceptionStep) msLongestPerceptionStep = (float)stepMs;
            if (settings.slam) drift.Add(believedPose, trueAtBelief);

            // 5 - 7. Goal, decide, command.
            double algorithmMsBefore = msAlgorithm;
            double plannerMsBefore = planner != null ? planner.Milliseconds : 0.0;
            float forward, turn;
            if (startSpinRemaining > 0f)
            {
                // SLAM start: spin in place at the dock before the algorithm takes over.
                startSpinRemaining -= Mathf.Abs(odometryTurn);
                forward = 0f;
                turn = LocalSlamConstants.StartSpinDegreesPerSecond;
            }
            else if (parked)
            {
                forward = turn = 0f;
            }
            else
            {
                stopwatch.Restart();
                if (simTime < boxedWaitUntil && boxedFinalHolding && simTime >= nextBoxedPoll)
                {
                    // The long last hold: leave it as soon as the way opens, not only at its end (the cat may wander
                    // off and back while the robot waits).
                    nextBoxedPoll = simTime + ControllerConstants.BoxedInFinalPollSeconds;
                    if (WayOpen())
                    {
                        Debug.Log($"The way is open again at {simTime:F0} s: ending the hold early.", this);
                        boxedWaitUntil = simTime;
                    }
                }
                if (simTime < boxedWaitUntil)
                {
                    forward = turn = 0f; // boxed in: wait for the way to open
                }
                else
                {
                    if (boxedWaitUntil >= 0f) EndBoxedWait();
                    if (simTime >= nextReachSample) SampleReach();
                    if (boxedGoingToLook)
                    {
                        CheckLookoutArrival(); // driving to look at the cut-off floor: the algorithm isn't asked
                    }
                    else if (homing == Homing.None)
                    {
                        AskForGoalIfDue();
                        if (Failed) return;
                        if (drive.Finished)
                        {
                            if (!WaitIfBoxedIn(false)) StartHoming(Homing.Finished);
                        }
                        else CheckBattery();
                    }

                    if (simTime < boxedWaitUntil)
                    {
                        forward = turn = 0f;
                    }
                    else
                    {
                        // Travel: the full drive speed is commanded; the body caps it for the floor it is really on.
                        // Sweep: slow enough for the floor under the brush to reach the target in one pass.
                        float speed = robot.DriveSpeedMetersPerSecond;
                        if (homing == Homing.None && goalMode == DriveMode.Sweep) speed = Mathf.Min(speed, SweepSpeedHere());

                        var input = new DriveInput(believedPose, simTime, dt, frame.bumped, frame.bumpBearing, view,
                            robot.Radius, speed, robot.ReverseSpeedMetersPerSecond, robot.TurnSpeedDegreesPerSecond);
                        driver.Step(input, out forward, out turn);
                        if (homing != Homing.None && !boxedGoingToLook) CheckHomeArrival();
                        if (parked) forward = turn = 0f;
                    }
                }
                msControl += stopwatch.Elapsed.TotalMilliseconds;
                double algorithmMs = msAlgorithm - algorithmMsBefore;
                lagParts[(int)LagLog.Part.Algorithm] = algorithmMs;
                lagParts[(int)LagLog.Part.Driver] = System.Math.Max(0.0, stopwatch.Elapsed.TotalMilliseconds - algorithmMs);
            }

            if (LagLog.Enabled)
                LagLog.Step(simTime, lagParts, planner != null ? planner.Milliseconds - plannerMsBefore : 0.0,
                    localSlam != null ? localSlam.Submaps.Count : 0);

            double fullStepMs = (System.Diagnostics.Stopwatch.GetTimestamp() - fullStepStarted) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            if (fullStepMs > msLongestFullStep) msLongestFullStep = (float)fullStepMs;

            robot.Command(forward, turn);
        }

        void AskForGoalIfDue()
        {
            var status = driver.Status;
            bool goalEnded = (status == GoalStatus.Reached || status == GoalStatus.Blocked || status == GoalStatus.Unreachable)
                             && !driver.StatusReported;

            if (simTime < nextGoalTime && !goalEnded) return;
            nextGoalTime = simTime + ControllerConstants.GoalPeriodSeconds;
            if (goalEnded && (status != GoalStatus.Reached || driver.EndReason != null)) LogGoalEnd(status);
            if (goalEnded && (status == GoalStatus.Blocked || status == GoalStatus.Unreachable) && driver.Goal.HasValue)
            {
                lastFailedGoal = driver.Goal.Value;
                lastFailedTime = simTime;
            }

            // On the algorithm's own thread (AlgorithmHost): an exception or a hang comes back as a fault.
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            AlgorithmHost.Outcome<Vec2?> asked;
            try
            {
                planner.Caller = PathPlanner.PlanCaller.Algorithm; // lag log only
                asked = host.NextGoal(algorithm, map, believedPose.ToApi(), status);
            }
            finally
            {
                planner.Caller = PathPlanner.PlanCaller.Driver;
                msAlgorithm += (System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            }
            if (!asked.Ok)
            {
                Fail(asked.Fault, AlgorithmCall.NextGoal);
                return;
            }

            driver.StatusReported = true;

            string invalid = InvalidOutput(asked.Value);
            if (invalid != null)
            {
                Fail(new AlgorithmFault { Kind = AlgorithmFaultKind.InvalidOutput, Call = AlgorithmCall.NextGoal, Message = invalid });
                return;
            }
            Vector2? next = asked.Value.HasValue ? asked.Value.Value.ToUnity() : (Vector2?)null;

            var current = driver.Goal;
            bool same = next.HasValue && current.HasValue && (next.Value - current.Value).sqrMagnitude < 1e-8f;

            if (drive.Finished) return; // done: the trip home starts this step
            if (next.HasValue)
            {
                // The algorithm found work again: a later boxing-in gets its own tries and its own last hold.
                boxedTries = 0;
                boxedFinalHeld = false;
                blockageFocus = null;
            }

            goalMode = drive.Mode;
            goalExposure = drive.SweepExposureMeters;
            goalTargetFraction = Mathf.Max(0.05f, drive.SweepTargetFraction);
            if (same && status == GoalStatus.Blocked) driver.RetryAfterBlocked(believedPose, simTime, view);
            // A hold asked for again at the same waypoint after Reached is a new hold (the tile wasn't clean yet).
            else if (!same || status == GoalStatus.InProgress || (status == GoalStatus.Reached && drive.Hold != HoldMode.None))
                driver.SetGoal(next, believedPose, simTime, view, drive.PassThrough, HoldFromRequest());
            // Otherwise the same waypoint after Reached / Unreachable: leave it finished; the algorithm is asked again next period.
        }

        /// <summary>
        /// Diagnostics: a goal that ended Blocked or Unreachable (or was reached by the bumper), with the driver's reason, where the robot was, and how
        /// much room the map gives at the goal and at the robot (the planner's clearance, capped just above what it needs).
        /// </summary>
        void LogGoalEnd(GoalStatus status)
        {
            var goal = driver.Goal;
            if (!goal.HasValue) return;
            Vector2 at = believedPose.position;
            string room = "";
            if (planner != null && view != null)
            {
                string Clear(Vector2 p) => view.TryWorldToCell(p, out var cell) ? planner.ClearanceOf(cell.row, cell.col).ToString("F3") : "?";
                room = $"; clearance at goal {Clear(goal.Value)} m, at robot {Clear(at)} m (the planner wants {planner.Clearance:F3})";
            }
            Debug.Log($"[Driver {simTime:F1} s] goal ({goal.Value.x:F2}, {goal.Value.y:F2}) {status}: {driver.EndReason ?? "no reason recorded"}. " +
                      $"Robot at ({at.x:F2}, {at.y:F2}), {Vector2.Distance(at, goal.Value):F3} m from the goal{room}.", this);
        }

        /// <summary>The algorithm's hold request for its goal, with the radius cut to the robot's and the vacuum's power added.</summary>
        HoldRequest HoldFromRequest() =>
            drive.Hold == HoldMode.None
                ? HoldRequest.None
                : new HoldRequest(drive.Hold, drive.HoldSeconds, drive.HoldCleanliness,
                    Mathf.Clamp(drive.HoldRadiusMeters, 0f, robot.Radius), vacuumPower);

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
            var shape = robot.Shape;
            float radius = shape.IsRound ? robot.Radius : shape.TurnRadius;
            Vector2 p = believedPose.position;
            if (view.TryWorldToCell(p - new Vector2(radius, radius), out var low) && view.TryWorldToCell(p + new Vector2(radius, radius), out var high))
            {
                for (int row = low.row; row <= high.row; row++)
                    for (int col = low.col; col <= high.col; col++)
                    {
                        bool under = shape.IsRound
                            ? (view.CellCenter(row, col) - p).sqrMagnitude <= radius * radius
                            : shape.Contains(VacuumShape.ToLocal(view.CellCenter(row, col) - p, believedPose.heading));
                        if (under) resistance = Mathf.Max(resistance, view.GetCell(row, col).vacuumResistanceBelief);
                    }
            }
            float speed = CoverageMath.SweepSpeed(goalExposure, vacuumPower, resistance, targetCleanliness * goalTargetFraction) / ControllerConstants.SweepSpeedSafety;
            return Mathf.Max(ControllerConstants.SweepSpeedFloor, speed);
        }

        // ---- The dock ------------------------------------------------------------------------------------------

        /// <summary>Low battery: if the charge is down to what the trip home would cost (plus a margin), head home now.</summary>
        /// <summary>
        /// Costs the trip home and heads home when the charge is down to the reserve. Planning a route home is the
        /// expensive part, so it is not done on a clock: after each check, the next waits until the robot has used
        /// <see cref="ControllerConstants.BatteryCheckSpareFraction"/> of its SPARE charge (charge minus reserve),
        /// and never comes sooner than <see cref="ControllerConstants.BatteryCheckSeconds"/> after the last.
        ///
        /// Why a third is safe: driving d metres costs at least d of charge and can lengthen the trip home by at
        /// most d, which raises the reserve by at most about 1.9 times the charge spent (the 1.25 reserve factor
        /// times the 1.5 floor margin, plus the operating drain for the longer trip). After a third of the spare is
        /// used, the reserve can have grown by at most about two thirds of it, so the robot is still above it. A
        /// healthy battery is checked rarely (about 20 times over a full charge), a nearly empty one often, and a
        /// tiny one (a runner sets 6 units) from the first step. Limit: a newly mapped wall (SLAM) can lengthen the
        /// way home by more than the distance driven.
        ///
        /// A charge above the last check's means a recharge (changing capacity mid-run recharges the battery): the
        /// next step checks at once. Between checks each step costs two comparisons.
        /// </summary>
        void CheckBattery()
        {
            if (battery == null) return;
            float charge = battery.CurrentCharge;
            bool recharged = charge > lastBatteryCheckCharge;
            if (!recharged && (charge > nextBatteryCheckCharge || simTime < nextBatteryCheckTime)) return;

            nextBatteryCheckTime = simTime + ControllerConstants.BatteryCheckSeconds;
            lastBatteryCheckCharge = charge;
            nextBatteryCheckCharge = float.PositiveInfinity; // no route home: check again after the minimum gap

            planner.Caller = PathPlanner.PlanCaller.Battery; // lag log only
            bool found = planner.FindPath(believedPose.position, dockPosition, homePath, out float length);
            planner.Caller = PathPlanner.PlanCaller.Driver;
            if (!found) return; // no way home anyway
            float turning = 180f + 90f * Mathf.Max(0, homePath.Count - 2); // turning to set off, and at each corner
            // The trip's time costs too: the battery drains every second the vacuum runs, moving or not.
            float seconds = length / Mathf.Max(0.05f, robot.DriveSpeedMetersPerSecond) + turning / Mathf.Max(1f, robot.TurnSpeedDegreesPerSecond);
            float cost = (length * battery.DistanceEnergyPerMeter + turning * battery.TurnEnergyPerDegree) * ControllerConstants.HomeFloorMargin
                         + seconds * battery.OperatingEnergyPerSecond;
            float reserve = cost * ControllerConstants.HomeReserveFactor + ControllerConstants.HomeReserveExtra;
            if (charge <= reserve)
            {
                StartHoming(Homing.LowBattery);
                return;
            }
            nextBatteryCheckCharge = charge - (charge - reserve) * ControllerConstants.BatteryCheckSpareFraction;
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
                    if (squeezing) EndSqueeze();       // the tighter path failed too: wait as usual
                    else if (StartSqueeze()) break;    // first try a tighter path past the blockage
                    if (WaitIfBoxedIn(true)) break;
                    var at = believedPose.position;
                    Park(RunOutcome.DockUnreachable,
                        $"The dock could not be reached ({(homing == Homing.Finished ? "job finished" : "battery low")}): " +
                        $"{(driver.Status == GoalStatus.Unreachable ? "no path" : "blocked")} from ({at.x:F2}, {at.y:F2}) to ({dockPosition.x:F2}, {dockPosition.y:F2})" +
                        (noWaitReason != null ? $"; {noWaitReason}." : "."));
                    break;
            }
        }

        /// <summary>
        /// No way home with the usual clearance: plan with less (BoxedInSqueezeClearanceFraction of the radius) and,
        /// if that finds a way, drive it. Contact on the way is handled by the driver as usual.
        /// </summary>
        bool StartSqueeze()
        {
            if (planner == null || driver.Planner == null) return false;
            float clearance = robot.Radius * ControllerConstants.BoxedInSqueezeClearanceFraction;
            squeezePlanner ??= new PathPlanner(view, clearance);
            if (!squeezePlanner.FindPath(believedPose.position, dockPosition, squeezePath, out float length)) return false;
            Debug.Log($"No way home with the usual clearance at {simTime:F0} s: trying a tighter path past the blockage " +
                      $"({length:F1} m, clearance {clearance:F3} m instead of {planner.Clearance:F3} m).", this);
            squeezing = true;
            driver.Reset();
            driver.Planner = squeezePlanner;
            goalMode = DriveMode.Travel;
            driver.SetGoal(dockPosition, believedPose, simTime, view);
            return true;
        }

        void EndSqueeze()
        {
            squeezing = false;
            driver.Planner = planner;
        }

        // ---- Boxed in / cut off ------------------------------------------------------------------------------
        //
        // Checked only when the algorithm finishes or the trip home finds no way (rarely): a cat pinning the robot in a
        // corner, or sitting in a hallway, cuts off floor the robot could reach before, so "done" or "no way home" would
        // be wrong. Wait, then try again; after BoxedInTries, accept it and say so.

        /// <summary>
        /// If the robot is boxed in and tries are left: start a wait (afterwards, home again or ask the algorithm again)
        /// and return true. Otherwise false (carry on as normal).
        /// </summary>
        bool WaitIfBoxedIn(bool homeward)
        {
            int tries = homeward ? boxedHomeTries : boxedTries;
            bool final = tries >= ControllerConstants.BoxedInTries;
            // On the way home any failure gets the wait: the cat may be sitting on the dock (or the way to it) without
            // boxing the robot in at all. When the algorithm finishes, only a real boxing-in does.
            bool boxed = BoxedIn(out int cutOff);
            noWaitReason = null;
            if (!boxed && !homeward) return false;
            // On the way home and not boxed in: wait only if something that moved in is in the way (the cat on the dock
            // or in a doorway). A way home that was never open (a closed door, a level without a gap) won't open by
            // waiting, so park now rather than after the waits and the hold. Judged once, at the first failure.
            if (homeward && !boxed && tries == 0 && !SomethingMovedOnWayHome(out noWaitReason)) return false;
            if (final && (homeward ? boxedHomeFinalHeld : boxedFinalHeld))
            {
                // All used and still blocked: give up (go home / park) and say so in the results.
                EndedByBlockage = true;
                var cells = mapAnalysis ?? boxedAnalysis;
                float cellArea = cells != null ? cells.CellSize * cells.CellSize : 0f;
                BlockageLeftSquareMeters = boxed ? cutOff * cellArea : 0f;
                BlockageReason = homeward
                    ? $"Unresolvable blockage: no way to the dock at {simTime:F0} s after the waits and the {ControllerConstants.BoxedInFinalHoldSeconds:F0} s hold."
                    : $"Unresolvable blockage: {(lastBoxedByBlockage ? "the way was still blocked" : "floor was still cut off")} at {simTime:F0} s " +
                      $"after the waits and the {ControllerConstants.BoxedInFinalHoldSeconds:F0} s hold; about {BlockageLeftSquareMeters:F2} m² left uncleaned.";
                return false;
            }
            if (tries == 0) BlockageEvents++;

            float wait;
            if (final)
            {
                // The short tries are used up: say so plainly, then one long hold before giving up.
                if (homeward) boxedHomeFinalHeld = true;
                else boxedFinalHeld = true;
                wait = ControllerConstants.BoxedInFinalHoldSeconds;
                Debug.LogWarning(homeward
                    ? $"Unresolvable blockage detected at {simTime:F0} s: no way to the dock after {tries} tries. Pausing " +
                      $"{wait / 60f:F0} minutes (sim time) to allow it to clear; if it doesn't, the run ends where the robot is."
                    : $"Unresolvable blockage detected at {simTime:F0} s: " +
                      (lastBoxedByBlockage ? "the way is still blocked" : $"{cutOff} floor cells still cut off") + $" after {tries} tries. " +
                      $"Pausing {wait / 60f:F0} minutes (sim time) to allow it to clear; if it doesn't, the robot leaves " +
                      "that floor uncleaned and concludes the run (returns to the dock).", this);
            }
            else
            {
                tries++;
                if (homeward) boxedHomeTries = tries;
                else boxedTries = tries;
                // Each wait twice the last (5, 10, 20 s): a cat that settles in gets longer to move on.
                wait = ControllerConstants.BoxedInWaitSeconds * (1 << (tries - 1));
            }
            boxedResumeHome = homeward;
            boxedFinalHolding = final;
            nextBoxedPoll = simTime + ControllerConstants.BoxedInFinalPollSeconds;
            if (!homeward) drive.ClearFinish();
            driver.Reset();

            // Go where the sensors can see the blockage first: at the finish, the reachable floor nearest the cut-off
            // floor; on the way home, the reachable floor nearest the dock along the way (next to whatever blocks it).
            Vector2? lookout = homeward ? FindHomeLookout() : boxed ? FindLookout() : null;
            float distance = lookout.HasValue ? Vector2.Distance(lookout.Value, believedPose.position) : 0f;
            bool go = lookout.HasValue && distance > ControllerConstants.BoxedInLookoutNearMeters;
            if (!final) Debug.LogWarning((boxed
                                 ? (lastBoxedByBlockage
                                     ? $"Blocked at {simTime:F0} s: something is sitting on {cutOff} cells the robot drove over earlier, next to where the algorithm last gave up "
                                     : $"Cut off at {simTime:F0} s: {cutOff} floor cells (and {lastUnmappedEdge} cells of unmapped edge) can't be reached now but could be through places reached earlier ")
                                 : $"No way to the dock at {simTime:F0} s (something may be on it or in the way) ") +
                             $"({(homeward ? "no way home" : "the algorithm finished")}). " +
                             (go ? $"Driving {distance:F1} m to look at it, then waiting " : "Waiting ") +
                             $"{wait:F0} s, then trying again (try {tries} of {ControllerConstants.BoxedInTries}).", this);
            if (go)
            {
                boxedGoingToLook = true;
                boxedLookWait = wait;
                goalMode = DriveMode.Travel;
                driver.SetGoal(lookout.Value, believedPose, simTime, view);
            }
            else boxedWaitUntil = simTime + wait;
            if (final && go) Debug.Log($"Driving {distance:F1} m to look at the cut-off floor first.", this);
            return true;
        }

        /// <summary>
        /// Driving to look at cut-off floor: once the trip ends (reached, or blocked on the way), start the wait there,
        /// or ask the algorithm again at once if the sensors have already cleared the way.
        /// </summary>
        void CheckLookoutArrival()
        {
            var status = driver.Status;
            if (status != GoalStatus.Reached && status != GoalStatus.Blocked && status != GoalStatus.Unreachable) return;
            boxedGoingToLook = false;
            driver.Reset();
            if (boxedResumeHome)
            {
                if (WayOpen())
                {
                    Debug.Log($"The way home is open again at {simTime:F0} s.", this);
                    EndBoxedWait();
                }
                else boxedWaitUntil = simTime + boxedLookWait;
                return;
            }
            if (!BoxedIn(out _))
            {
                Debug.Log($"The cut-off floor can be reached again at {simTime:F0} s: asking the algorithm.", this);
                nextGoalTime = simTime;
                return;
            }
            boxedWaitUntil = simTime + boxedLookWait;
        }

        /// <summary>On the way home: the reachable floor nearest the dock along the way (see <see cref="BlockageMap.FindHomeLookout"/>).</summary>
        Vector2? FindHomeLookout()
        {
            if (!view.TryWorldToCell(dockPosition, out var dock)) return null;
            return blockage.FindHomeLookout(mapAnalysis ?? boxedAnalysis, view.CellCenter, dock.row, dock.col);
        }

        /// <summary>The reachable floor nearest the cut-off floor the last BoxedIn found (see <see cref="BlockageMap.FindLookout"/>).</summary>
        Vector2? FindLookout() => blockage.FindLookout(mapAnalysis ?? boxedAnalysis, view.CellCenter);

        void EndBoxedWait()
        {
            boxedWaitUntil = -1f;
            boxedFinalHolding = false;
            if (boxedResumeHome)
            {
                boxedResumeHome = false;
                driver.Reset();
                driver.SetGoal(dockPosition, believedPose, simTime, view);
            }
            else nextGoalTime = simTime; // ask the algorithm again now
        }

        /// <summary>
        /// During the long last hold: is the way open again? On the way home, the dock (any cell within the robot's
        /// radius of it) can be reached; otherwise nothing is cut off or blocked any more.
        /// </summary>
        bool WayOpen()
        {
            if (!boxedResumeHome) return !BoxedIn(out _, false);
            var analysis = mapAnalysis ?? (boxedAnalysis ??= new MapAnalysis(map));
            analysis.Update(believedPose.position.ToApi());
            if (analysis.IsEmpty || !view.TryWorldToCell(dockPosition, out var dock)) return false;
            return BlockageMap.DockReachable(analysis, dock.row, dock.col, Mathf.CeilToInt(robot.Radius / analysis.CellSize));
        }

        /// <summary>
        /// Boxed in: floor still to clean is cut off, or something is sitting in the way near where the algorithm
        /// stopped (see <see cref="BlockageMap.BoxedIn"/>). Updates the map analysis first.
        /// </summary>
        bool BoxedIn(out int cutOff, bool log = true)
        {
            var analysis = mapAnalysis ?? (boxedAnalysis ??= new MapAnalysis(map));
            analysis.Update(believedPose.position.ToApi());
            cutOff = 0;
            lastBoxedByBlockage = false;

            // Where to look for a blockage: the one found before; else the algorithm's last failed goal if that was
            // just before the finish; else the robot (it stopped where the algorithm ran out of floor).
            bool fromFailure = false;
            Vector2? focus = blockageFocus;
            if (!focus.HasValue)
            {
                fromFailure = simTime - lastFailedTime <= ControllerConstants.BoxedInRecentFailSeconds;
                focus = fromFailure ? lastFailedGoal : believedPose.position;
            }

            bool boxed = blockage.BoxedIn(analysis, view.CellCenter, targetCleanliness * ControllerConstants.BoxedInDirtyFraction,
                focus.Value, blockageFocus.HasValue || fromFailure, writer.Flickers, out var check);
            lastUnmappedEdge = check.UnmappedEdge;
            cutOff = check.CutOff;
            lastBoxedByBlockage = check.ByBlockage;

            if (log && blockage.HasRecord && !analysis.IsEmpty)
                Debug.Log($"[Blockage check {simTime:F0} s] near {(blockageFocus.HasValue ? "the blockage" : fromFailure ? "the failed goal" : "the robot")} " +
                          $"({focus.Value.x:F2}, {focus.Value.y:F2}): cut-off floor {(check.ByBlockage ? 0 : check.CutOff)}, unmapped edge past it {check.UnmappedEdge}, " +
                          $"in the way {check.InWay} ({check.DirtyInWay} still to clean), moved {check.Moved} (span {check.MovedSpan:F2} m), " +
                          $"flickering {check.Flickering}, unmapped next to moved {check.Hidden}.", this);

            if (check.ByBlockage) blockageFocus = focus;
            return boxed;
        }

        /// <summary>
        /// The way home failed and the robot isn't boxed in: has something moved into the way (cells the robot reached
        /// earlier, blocked now and not flickering, near the dock or the blockage)? With no record yet, assume so.
        /// </summary>
        bool SomethingMovedOnWayHome(out string reason)
        {
            reason = null;
            if (!blockage.HasRecord) return true;
            var analysis = mapAnalysis ?? boxedAnalysis; // just updated by BoxedIn
            Vector2? lookout = FindHomeLookout();
            int moved = blockage.MovedNear(analysis, view.CellCenter, dockPosition, lookout,
                ControllerConstants.BoxedInBlockageRadiusMeters, writer.Flickers);
            if (moved >= ControllerConstants.BoxedInHomeMovedCells) return true;
            reason = lookout.HasValue
                ? $"nothing that moved is in the way ({moved} cell(s) blocked now that were reached earlier), so it was not waited out"
                : "no way to it through floor reached earlier, so nothing in the way can move off it";
            Debug.Log($"No way to the dock at {simTime:F0} s and {reason}.", this);
            return false;
        }

        /// <summary>Every BoxedInSampleSeconds: records the floor reachable in the algorithm's latest map analysis (one pass, no update of its own).</summary>
        void SampleReach()
        {
            nextReachSample = simTime + ControllerConstants.BoxedInSampleSeconds;
            blockage.Sample(mapAnalysis);
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

            // A new algorithm replaces a failed one: the failure is over (a new one may come from building it).
            if (Failed) ClearFault();
            activeAlgorithmId = id;
            BuildAlgorithm(id);
        }

        void Fail(AlgorithmFault fault, AlgorithmCall call)
        {
            fault.Call = call;
            Fail(fault);
        }

        void Fail(AlgorithmFault fault)
        {
            if (Failed) return;

            fault.AlgorithmId = activeAlgorithmId;
            fault.SimSeconds = simTime;
            Failed = true;
            Fault = fault;
            Failure = fault.Describe();
            algorithm = null; // never called again: Resume builds a fresh one
            if (robot != null) robot.Command(0f, 0f);
            Debug.LogError($"The movement algorithm stopped at {simTime:F1} s. {Failure}" +
                           (string.IsNullOrEmpty(fault.StackTrace) ? "" : "\n" + fault.StackTrace), this);
        }

        void ClearFault()
        {
            Failed = false;
            Fault = null;
            Failure = null;
        }

        /// <summary>
        /// What is wrong with what the algorithm returned from NextGoal (the waypoint and its drive requests), or null
        /// if nothing. Values the simulation can't act on are a fault of the algorithm, caught here rather than
        /// left to break the driving later.
        /// </summary>
        string InvalidOutput(Vec2? waypoint)
        {
            if (waypoint.HasValue)
            {
                var w = waypoint.Value;
                if (!IsFinite(new Vector2(w.x, w.y))) return $"the waypoint ({w.x}, {w.y}) is not a finite number.";
                if ((new Vector2(w.x, w.y) - dockPosition).sqrMagnitude > MaxWaypointMeters * MaxWaypointMeters)
                    return $"the waypoint ({w.x:F1}, {w.y:F1}) is more than {MaxWaypointMeters:0} m from the dock.";
            }

            if (!System.Enum.IsDefined(typeof(DriveMode), drive.Mode)) return $"drive mode {(int)drive.Mode} is not a defined DriveMode.";
            if (drive.Mode == DriveMode.Sweep)
            {
                if (!IsFiniteNumber(drive.SweepExposureMeters) || drive.SweepExposureMeters < 0f)
                    return $"SweepExposureMeters = {drive.SweepExposureMeters} (must be a finite number, 0 or more).";
                if (!IsFiniteNumber(drive.SweepTargetFraction) || drive.SweepTargetFraction < 0f || drive.SweepTargetFraction > MaxSweepTargetFraction)
                    return $"SweepTargetFraction = {drive.SweepTargetFraction} (must be from 0 to {MaxSweepTargetFraction:0}).";
            }

            if (!System.Enum.IsDefined(typeof(HoldMode), drive.Hold)) return $"hold mode {(int)drive.Hold} is not a defined HoldMode.";
            if (drive.Hold != HoldMode.None)
            {
                if (!IsFiniteNumber(drive.HoldSeconds) || drive.HoldSeconds < 0f)
                    return $"HoldSeconds = {drive.HoldSeconds} (must be a finite number, 0 or more).";
                if (!IsFiniteNumber(drive.HoldCleanliness))
                    return $"HoldCleanliness = {drive.HoldCleanliness} (must be a finite number).";
                if (!IsFiniteNumber(drive.HoldRadiusMeters) || drive.HoldRadiusMeters < 0f)
                    return $"HoldRadiusMeters = {drive.HoldRadiusMeters} (must be a finite number, 0 or more).";
            }
            return null;
        }

        /// <summary>A waypoint farther than this from the dock is treated as invalid output (no home is that big).</summary>
        const float MaxWaypointMeters = 1000f;

        /// <summary>The largest SweepTargetFraction accepted (sweeping to 10× the target is already absurd).</summary>
        const float MaxSweepTargetFraction = 10f;

        static bool IsFiniteNumber(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        /// <summary>
        /// Carries on after the session stopped the run (2.11, 2.16): after a failure, a fresh algorithm is built
        /// (whatever is selected now, so a reimported or newly picked one takes over); after docking, a blockage
        /// give-up or the dock being out of reach, the same algorithm is asked for work again (one that still thinks
        /// it is done finishes again, and the run stops again). The robot carries on from where it is, with what it
        /// has mapped. The session also clears the robot's no-progress check (<see cref="VacuumRobot.ClearStuck"/>).
        /// </summary>
        public void Resume()
        {
            bool afterFault = Failed;
            ClearFault();
            SimException = null;

            Outcome = RunOutcome.None;
            OutcomeMessage = null;
            homing = Homing.None;
            parked = false;
            homeAttempts = 0;
            if (squeezing) EndSqueeze();
            boxedWaitUntil = -1f;
            boxedResumeHome = false;
            boxedTries = 0;
            boxedHomeTries = 0;
            boxedGoingToLook = false;
            boxedLookWait = 0f;
            boxedFinalHeld = boxedHomeFinalHeld = false;
            boxedFinalHolding = false;
            EndedByBlockage = false;
            BlockageLeftSquareMeters = 0f;
            BlockageReason = null;
            nextBatteryCheckTime = simTime;
            nextBatteryCheckCharge = float.PositiveInfinity;
            lastBatteryCheckCharge = float.NegativeInfinity; // check the battery at once (it may have been refilled)
            driver.Reset();

            if (afterFault)
            {
                // A hung call was aborted part-way, possibly inside the shared path planner: start that afresh.
                RebuildPlanner();
                activeAlgorithmId = robot.AlgorithmId;
                BuildAlgorithm(activeAlgorithmId);
            }
            else if (robot.AlgorithmId != activeAlgorithmId)
            {
                activeAlgorithmId = robot.AlgorithmId;
                BuildAlgorithm(activeAlgorithmId);
            }
            else
            {
                drive.ClearFinish();
                nextGoalTime = simTime;
            }
        }

        /// <summary>
        /// Builds the current algorithm again from the catalog (after a reimport, so a changed file takes effect).
        /// The robot keeps its place and its map; the algorithm starts over with Initialize.
        /// </summary>
        public void Reload()
        {
            if (!ready) return;
            ClearFault();
            driver.Reset();
            activeAlgorithmId = robot.AlgorithmId;
            BuildAlgorithm(activeAlgorithmId);
        }

        void RebuildPlanner()
        {
            if (view == null || robot == null) return;
            if (planner != null) plannerMsCarried += planner.Milliseconds;
            planner = new PathPlanner(view, robot.Radius + ControllerConstants.ClearanceMarginMeters);
            driver.Planner = ControllerConstants.PlanPaths ? planner : null;
            map = new BeliefMapAdapter(view, planner);
            squeezePlanner = null;
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
