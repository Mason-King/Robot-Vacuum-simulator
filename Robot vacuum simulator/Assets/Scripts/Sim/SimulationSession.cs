using System;
using System.IO;
using RobotVacuum.Level;
using RobotVacuum.LevelEditor;
using UnityEngine;

namespace RobotVacuum.Sim
{
    /// <summary>
    /// Owns one run from start to finish: builds it through <see cref="SimulationRunner"/>, saves its inputs,
    /// counts simulated time, ends it, and saves its results. Every caller starts runs through this class --
    /// the PlayScene test driver, the Setup screen, and the headless job -- so a run behaves and is recorded
    /// the same way whatever started it.
    ///
    /// It has NO UI and never decides what happens after a run ends. When a run ends it freezes it in place
    /// (the floor, robot and heatmap stay as they are) and raises <see cref="Finished"/>. A Graphics caller
    /// then shows its own end-of-run panel and calls <see cref="Restart"/> or <see cref="Close"/> on input; a
    /// headless caller calls <see cref="Close"/> straight away and moves on.
    ///
    /// Only one session can be active at a time (<see cref="Active"/>).
    /// </summary>
    public sealed class SimulationSession
    {
        public enum Phase
        {
            /// <summary>The run is going.</summary>
            Running,

            /// <summary>The run has ended and been saved, and is held on screen until the caller acts.</summary>
            Finished,

            /// <summary>Torn down. The session can't be used again.</summary>
            Closed,
        }

        /// <summary>
        /// How much faster than real time a headless run plays. The physics stepping mechanism is still to be
        /// chosen by testing the final product (SDD 2.5); this is the value the earlier headless path used.
        /// </summary>
        public const float HeadlessTimeScale = 100f;

        /// <summary>The session currently running or holding a finished run, or null.</summary>
        public static SimulationSession Active { get; private set; }

        /// <summary>
        /// Starts a run of <paramref name="level"/>. <paramref name="settings"/> is what the caller asked for;
        /// unset values use their defaults (see <see cref="RunSettings"/>). Null settings means all defaults.
        /// Returns false, with a reason in <paramref name="error"/>, if a session is already active or the run
        /// couldn't be built.
        /// </summary>
        public static bool TryStart(LevelData level, RunSettings settings, bool headless,
            out SimulationSession session, out string error) =>
            TryStart(level, settings, headless, null, out session, out error);

        /// <summary>
        /// As above, and calls <paramref name="runBegan"/> each time a run has been built and is about to
        /// move: the first run and every <see cref="Restart"/>. Use it to adjust the fresh robot or attach a
        /// recorder; it is called before the first physics step.
        /// </summary>
        public static bool TryStart(LevelData level, RunSettings settings, bool headless,
            Action<SimulationSession> runBegan, out SimulationSession session, out string error)
        {
            session = null;

            if (Active != null)
            {
                error = "A run is already active. Close it before starting another.";
                return false;
            }

            if (level == null)
            {
                error = "No floor plan to run.";
                return false;
            }

            var created = new SimulationSession(level, settings ?? new RunSettings(), headless) { runBegan = runBegan };
            if (!created.BeginRun(out error))
            {
                created.State = Phase.Closed;
                return false;
            }

            Active = created;
            session = created;
            return true;
        }

        readonly RunSettings requested;
        readonly float savedTimeScale;
        readonly float savedMaximumDeltaTime;
        SessionClock clock;
        Action<SimulationSession> runBegan;

        SimulationSession(LevelData level, RunSettings requested, bool headless)
        {
            Level = level;
            Headless = headless;

            // Kept as given, so Restart asks for the same things again: a fixed seed repeats the run exactly,
            // Random Seed gets a fresh one.
            this.requested = requested;
            savedTimeScale = Time.timeScale;
            savedMaximumDeltaTime = Time.maximumDeltaTime;
        }

        public LevelData Level { get; }
        public bool Headless { get; }
        public Phase State { get; private set; }

        /// <summary>The current run's settings with every default filled in: exactly what config.json holds.</summary>
        public RunSettings Settings { get; private set; }

        /// <summary>The current run's objects (level, robot, cleaning, heatmap). Null once closed.</summary>
        public SimulationRunner Runner { get; private set; }

        /// <summary>The current run's folder under <see cref="RunArtifacts.RunsRoot"/>.</summary>
        public string RunFolder { get; private set; }

        /// <summary>The current run's outcome. Null while it is still running.</summary>
        public RunResult Result { get; private set; }

        public float SimulatedSeconds { get; private set; }
        public float RealSeconds { get; private set; }

        /// <summary>Raised once when a run ends on its own (battery, time cap, error). Not raised by Restart or Close.</summary>
        public event Action<SimulationSession> Finished;

        /// <summary>Raised once when the session is torn down.</summary>
        public event Action<SimulationSession> Closed;

        /// <summary>
        /// Ends the current run if it is still going (recorded as stopped by user), then starts a fresh one
        /// from the same requested settings, in a new run folder. Returns false, and closes the session, if
        /// the new run couldn't be built.
        /// </summary>
        public bool Restart(out string error)
        {
            if (State == Phase.Closed)
            {
                error = "This session is closed.";
                return false;
            }

            if (State == Phase.Running) EndRun(RunEndReason.StoppedByUser, null, raiseFinished: false);
            TearDownRunner();

            if (BeginRun(out error)) return true;

            Close();
            return false;
        }

        /// <summary>
        /// Ends the current run if it is still going (recorded as stopped by user, with everything up to that
        /// moment saved), tears everything down and puts the time scale back as it was before the session.
        /// </summary>
        public void Close()
        {
            if (State == Phase.Closed) return;

            if (State == Phase.Running) EndRun(RunEndReason.StoppedByUser, null, raiseFinished: false);
            TearDownRunner();

            Time.timeScale = savedTimeScale;
            Time.maximumDeltaTime = savedMaximumDeltaTime;
            State = Phase.Closed;
            if (Active == this) Active = null;

            Closed?.Invoke(this);
        }

        // ---------------------------------------------------------------- one run

        bool BeginRun(out string error)
        {
            try
            {
                Settings = requested.Resolved();
                Result = null;
                SimulatedSeconds = 0f;
                RealSeconds = 0f;

                Runner = SimulationRunner.Start(Level, SimulationRunner.Options.From(Settings, Headless));

                // Unset vacuum values keep the robot's own defaults; record what it actually ended up with.
                Settings.ApplyVacuumTo(Runner.Robot, Runner.Battery);
                Settings.RecordVacuumFrom(Runner.Robot, Runner.Battery);

                // The inputs are saved before the run moves, so even a run that dies part-way is reproducible.
                RunFolder = RunArtifacts.NewRunFolder();
                new RunConfig
                {
                    level = LevelSnapshot.FromLevel(Level, Level.name),
                    settings = Settings,
                    headless = Headless,
                    playbackSpeed = Headless ? SimulationSpeed.MaxPlaybackSpeed : 1f,
                    startedUtc = DateTime.UtcNow.ToString("o"),
                }.WriteTo(Path.Combine(RunFolder, RunArtifacts.ConfigFileName));

                clock = Runner.Root.AddComponent<SessionClock>();
                clock.Bind(this);

                if (Headless) SimulationSpeed.Apply(HeadlessTimeScale, savedMaximumDeltaTime);

                State = Phase.Running;
                runBegan?.Invoke(this);
                error = null;
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                error = $"Could not start the run: {ex.Message}";

                // A folder whose config.json exists but whose result.json doesn't reads as a crashed run, so
                // record the failure there if the folder was already made.
                if (RunFolder != null) WriteResult(RunResult.Failure(error));
                TearDownRunner();
                return false;
            }
        }

        // Called by the clock every physics step, before the robot and the cleaning grid run (see SessionClock).
        void Step(float dt)
        {
            if (State != Phase.Running || Runner == null) return;

            SimulatedSeconds += dt;

            if (Runner.Robot != null && Runner.Robot.AlgorithmFailed)
                EndRun(RunEndReason.Error, "The movement algorithm failed. See the Console for details.", raiseFinished: true);
            else if (Runner.Battery != null && !Runner.Battery.CanOperate)
                EndRun(RunEndReason.BatteryDepleted, null, raiseFinished: true);
            else if (SimulatedSeconds >= SimDefaults.MaxSimulatedSeconds)
                EndRun(RunEndReason.TimeCap, null, raiseFinished: true);
        }

        void Tick(float unscaledDt)
        {
            if (State == Phase.Running) RealSeconds += unscaledDt;
        }

        void EndRun(RunEndReason reason, string errorMessage, bool raiseFinished)
        {
            if (State != Phase.Running) return;
            State = Phase.Finished;

            FreezeRun();

            var result = new RunResult
            {
                succeeded = reason != RunEndReason.Error,
                errorMessage = errorMessage,
                endReason = reason,
                levelName = Level.name,
                seed = Settings.seed,
                pattern = Settings.pattern,
                targetMaxClean = Settings.targetMaxClean,
                coveragePercent = Runner != null ? Runner.CoveragePercent : 0f,
                blockedAreaSquareMeters = Runner != null ? Runner.BlockedArea : 0f,
                simulatedSeconds = SimulatedSeconds,
                realSeconds = RealSeconds,
            };

            // The one moment the live grid still exists: save the model and the three PNGs, then result.json
            // last, since its existence is what marks the run as finished.
            var grid = Runner != null && Runner.Cleaning != null ? Runner.Cleaning.Grid : null;
            if (grid != null)
            {
                try
                {
                    RunArtifacts.Write(grid.ToData(), RunFolder, result);
                }
                catch (Exception ex)
                {
                    Debug.LogException(ex);
                    result.succeeded = false;
                    result.errorMessage = $"The run ended ({reason}) but its model and images could not be saved: {ex.Message}";
                }
            }

            WriteResult(result);
            Result = result;

            if (raiseFinished) Finished?.Invoke(this);
        }

        // Holds the end state on screen: nothing moves or cleans any more, but nothing is destroyed.
        void FreezeRun()
        {
            if (Runner == null) return;

            if (Runner.Cleaning != null) Runner.Cleaning.enabled = false;

            if (Runner.Robot != null)
            {
                Runner.Robot.enabled = false;

                var body = Runner.Robot.GetComponent<Rigidbody2D>();
                if (body != null)
                {
                    body.linearVelocity = Vector2.zero;
                    body.angularVelocity = 0f;
                    body.simulated = false; // nothing else can push it while it's on display
                }
            }
        }

        void WriteResult(RunResult result)
        {
            try
            {
                result.WriteTo(Path.Combine(RunFolder, RunArtifacts.ResultFileName));
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
            }
        }

        void TearDownRunner()
        {
            if (clock != null) clock.Unbind();
            clock = null;

            // Immediate, because Restart builds the next run in the same frame (see SimulationRunner.Stop).
            Runner?.Stop(immediate: true);
            Runner = null;
        }

        // Someone destroyed the run's objects without going through Close (a scene unload, leaving Play mode).
        // Nothing is left to save, so just let go.
        void OnRunnerLost()
        {
            if (State == Phase.Closed) return;

            clock = null;
            Runner = null;
            Time.timeScale = savedTimeScale;
            Time.maximumDeltaTime = savedMaximumDeltaTime;
            State = Phase.Closed;
            if (Active == this) Active = null;

            Closed?.Invoke(this);
        }

        // Statics survive entering Play Mode when domain reload is off (it is, in this project).
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => Active = null;

        // ---------------------------------------------------------------- clock

        /// <summary>
        /// Gives the session a physics step to count time and check the end conditions on. Runs before every
        /// other script's FixedUpdate, so a run that ends this step stops the cleaning grid before it adds
        /// another step of dwell. Lives on the run's root object, so it goes away with the run.
        /// </summary>
        [DefaultExecutionOrder(-1000)]
        sealed class SessionClock : MonoBehaviour
        {
            SimulationSession session;

            public void Bind(SimulationSession owner) => session = owner;
            public void Unbind() => session = null;

            void FixedUpdate() => session?.Step(Time.fixedDeltaTime);

            void Update() => session?.Tick(Time.unscaledDeltaTime);

            void OnDestroy()
            {
                var owner = session;
                session = null;
                owner?.OnRunnerLost();
            }
        }
    }
}
