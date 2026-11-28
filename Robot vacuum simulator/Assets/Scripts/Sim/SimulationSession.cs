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
    /// Ends come in two kinds (2.11). A stop condition (the algorithm finished and docked, docked on low battery,
    /// failed, got stuck, the battery or the run's time limit ran out, the dock was out of reach) can be HELD
    /// (<see cref="HoldOnStop"/>, graphics only): the run waits in <see cref="Phase.Stopped"/>, unsaved, until the
    /// caller resumes, restarts or finishes it. A Terminal end (the simulation's own code threw, the algorithm
    /// left too many threads running, the 24 h safety cap) always ends the run at once. Every stop is recorded in
    /// result.json's stopEvents, and an algorithm failure in full in its fault.
    ///
    /// Only one session can be active at a time (<see cref="Active"/>).
    /// </summary>
    public sealed class SimulationSession
    {
        public enum Phase
        {
            /// <summary>The run is going.</summary>
            Running,

            /// <summary>
            /// A stop condition was met (the algorithm finished and docked, failed, the battery ran out, the robot
            /// got stuck, the time limit...) and the run is held, frozen, NOT yet saved: the caller can
            /// <see cref="Resume"/> it, run it again, or finish it. Only when <see cref="HoldOnStop"/> is set (a
            /// graphics run whose screen offers those choices); otherwise a stop goes straight on to Finished.
            /// </summary>
            Stopped,

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

        /// <summary>
        /// The program's safety cap on simulated time (24 h, <see cref="SimDefaults.MaxSimulatedSeconds"/>). It guards
        /// the program, not the algorithm: reaching it ends the run at once (<see cref="RunEndReason.TimeCap"/>), never
        /// held, whatever <see cref="HoldOnStop"/> says.
        /// </summary>
        public float SafetyCapSeconds => SimDefaults.MaxSimulatedSeconds;

        /// <summary>
        /// The run's own time limit in simulated seconds (<see cref="RunSettings.maxSimulatedSeconds"/>), or 0 for none.
        /// Reaching it is a stop condition (<see cref="RunEndReason.TimeLimit"/>), like the battery running out.
        /// </summary>
        public float UserLimitSeconds => !userLimitLifted && Settings != null && Settings.maxSimulatedSeconds > 0f
            ? Mathf.Min(Settings.maxSimulatedSeconds, SimDefaults.MaxSimulatedSeconds)
            : 0f;

        // Resuming a run stopped by its own time limit lifts that limit for the rest of the run (otherwise it would only
        // stop again at once). The 24 h safety cap still applies.
        bool userLimitLifted;

        /// <summary>The first of the two limits the run will meet (for progress bars).</summary>
        public float TimeCapSeconds => UserLimitSeconds > 0f ? UserLimitSeconds : SafetyCapSeconds;

        /// <summary>
        /// Graphics only: hold a run that meets a stop condition (<see cref="Phase.Stopped"/>) so the user can resume
        /// it, change parameters, run it again or finish it. Off (the default, and always in headless): a stop ends
        /// and saves the run straight away, as before.
        /// </summary>
        public bool HoldOnStop { get; set; }

        /// <summary>Why the run is stopped, while <see cref="State"/> is <see cref="Phase.Stopped"/>.</summary>
        public RunEndReason StopReason { get; private set; }

        /// <summary>The stop's message (an algorithm failure, why it is stuck, ...), or null.</summary>
        public string StopMessage { get; private set; }

        /// <summary>The run's latest algorithm or simulation failure (even one resumed past), or null.</summary>
        public RunFault LastFault => lastFault;

        /// <summary>Every stop so far in the current run, oldest first.</summary>
        public System.Collections.Generic.IReadOnlyList<RunStopEvent> StopEvents => stopEvents;

        readonly System.Collections.Generic.List<RunStopEvent> stopEvents = new System.Collections.Generic.List<RunStopEvent>();

        /// <summary>The algorithm the current run started with (<see cref="Settings"/> follows mid-run switches).</summary>
        public string StartingAlgorithmId { get; private set; }

        /// <summary>Every mid-run algorithm switch so far in the current run, oldest first.</summary>
        public System.Collections.Generic.IReadOnlyList<RunAlgorithmSwitch> AlgorithmSwitches => algorithmSwitches;

        readonly System.Collections.Generic.List<RunAlgorithmSwitch> algorithmSwitches = new System.Collections.Generic.List<RunAlgorithmSwitch>();

        /// <summary>Records a mid-run switch of algorithm (2.16) at the current simulated time, for the results.</summary>
        public void RecordAlgorithmSwitch(string fromId, string toId)
        {
            if (fromId == toId || State == Phase.Closed) return;
            algorithmSwitches.Add(new RunAlgorithmSwitch
            {
                simSeconds = SimulatedSeconds,
                fromId = fromId ?? "",
                fromName = Control.AlgorithmCatalog.NameOf(fromId),
                toId = toId ?? "",
                toName = Control.AlgorithmCatalog.NameOf(toId),
            });
        }

        RunFault lastFault;      // the run's latest failure (kept even if it was resumed past), for result.json
        int? repeatSeed;         // Restart(sameSeed): the seed the next run must use

        /// <summary>Raised once when a run ends on its own (a stop, a failure, the safety cap). Not raised by Restart, Close or Finish.</summary>
        public event Action<SimulationSession> Finished;

        /// <summary>Raised when a run meets a stop condition, before it is held (<see cref="HoldOnStop"/>) or ended.</summary>
        public event Action<SimulationSession> Stopped;

        /// <summary>Raised once when the session is torn down.</summary>
        public event Action<SimulationSession> Closed;

        /// <summary>
        /// Ends the current run if it is still going (recorded as stopped by user) or held at a stop (recorded
        /// with that stop's reason), then starts a fresh one in a new run folder. <paramref name="sameSeed"/>: with
        /// exactly the seed just used (Run Again); otherwise from the requested settings, so a configuration that
        /// draws a random seed each run draws a new one. Returns false, and closes the session, if the new run
        /// couldn't be built.
        /// </summary>
        public bool Restart(out string error) => Restart(false, out error);

        /// <summary>
        /// Ends and saves the run as left by Run Again, for a rerun that is built elsewhere (a fresh scene load, see
        /// SimLauncher.SimulateAgain). Close the session afterwards.
        /// </summary>
        public void EndForRunAgain() => EndIfNotEnded(RunStopEvent_RunAgain);

        public bool Restart(bool sameSeed, out string error)
        {
            if (State == Phase.Closed)
            {
                error = "This session is closed.";
                return false;
            }

            if (sameSeed && Settings != null) repeatSeed = Settings.seed;
            EndIfNotEnded(RunStopEvent_RunAgain);
            TearDownRunner();

            if (BeginRun(out error)) return true;

            Close();
            return false;
        }

        /// <summary>
        /// Carries on a run held at a stop (<see cref="Phase.Stopped"/>): the robot and the cleaning start again from
        /// where they are. After an algorithm failure a fresh algorithm is built (whatever is selected now); the
        /// no-progress check starts over; after the run's own time limit, that limit is lifted for the rest of the run.
        /// If the stop's cause is still there (an empty battery, an algorithm that still has nothing to do) the run
        /// simply stops again. False if not stopped.
        /// </summary>
        public bool Resume()
        {
            if (State != Phase.Stopped || Runner == null) return false;

            if (stopEvents.Count > 0) stopEvents[stopEvents.Count - 1].action = RunStopEvent_Resumed;
            if (StopReason == RunEndReason.TimeLimit) userLimitLifted = true;
            StopMessage = null;
            UnfreezeRun();
            Runner.Brain?.Resume();
            Runner.Robot?.ClearStuck();
            State = Phase.Running;
            return true;
        }

        /// <summary>
        /// Ends and saves the run now: a run held at a stop keeps that stop's reason; one still going is recorded as
        /// stopped by the user. The run stays on screen (<see cref="Phase.Finished"/>) until <see cref="Close"/>.
        /// Does not raise <see cref="Finished"/> (the caller asked for it).
        /// </summary>
        public void Finish() => EndIfNotEnded(RunStopEvent_Finished);

        /// <summary>
        /// Ends the current run if it is still going (recorded as stopped by user, with everything up to that
        /// moment saved) or held at a stop (recorded with that stop's reason), tears everything down and puts the
        /// time scale back as it was before the session.
        /// </summary>
        public void Close()
        {
            if (State == Phase.Closed) return;

            EndIfNotEnded(RunStopEvent_Back);
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
                if (repeatSeed.HasValue) Settings.seed = repeatSeed.Value; // Run Again: exactly the seed just used
                repeatSeed = null;
                Result = null;
                SimulatedSeconds = 0f;
                RealSeconds = 0f;
                stopEvents.Clear();
                algorithmSwitches.Clear();
                StartingAlgorithmId = Settings.algorithmId;
                lastFault = null;
                userLimitLifted = false;
                StopMessage = null;

                // The folder first, so the Console is captured into it from the very start (console_log.txt).
                RunFolder = RunArtifacts.NewRunFolder();
                log = new RunLog(RunFolder);

                Runner = SimulationRunner.Start(Level, SimulationRunner.Options.From(Settings, Headless));

                // Unset vacuum values keep the robot's own defaults; record what it actually ended up with.
                Settings.ApplyVacuumTo(Runner.Robot, Runner.Battery);
                Settings.RecordVacuumFrom(Runner.Robot, Runner.Battery);

                // The inputs are saved before the run moves, so even a run that dies part-way is reproducible.
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

                if (!Headless)
                    Debug.Log($"Run started: {Control.AlgorithmCatalog.NameOf(Settings.algorithmId)}, seed {Settings.seed}, time limit " +
                              (UserLimitSeconds > 0f ? $"{UserLimitSeconds:0} simulated s" : "none (safety cap 24 h)") + ".");

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
                StopLog();
                TearDownRunner();
                return false;
            }
        }

        // Called by the clock every physics step, before the robot and the cleaning grid run (see SessionClock).
        void Step(float dt)
        {
            if (State != Phase.Running || Runner == null) return;

            SimulatedSeconds += dt;
            CheckEnds();
        }

        /// <summary>
        /// Checks the stop and end conditions now, without advancing time: after a change made while the simulation
        /// was paused (a reimported algorithm that no longer loads, say), so the run stops at once rather than on the
        /// next step. Nothing if the run isn't going.
        /// </summary>
        public void CheckNow()
        {
            if (State == Phase.Running && Runner != null) CheckEnds();
        }

        void CheckEnds()
        {
            var robot = Runner.Robot;
            var brain = Runner.Brain;
            var outcome = robot != null ? robot.Outcome : Control.RunOutcome.None;

            // Terminal: the run can't carry on, so it ends now, held or not (2.11).
            if (brain != null && brain.SimException != null)
            {
                var ex = brain.SimException;
                Terminate(RunEndReason.Error,
                    $"The simulation itself failed (not the algorithm): {ex.GetType().Name}: {ex.Message}",
                    new RunFault
                    {
                        kind = "Simulation",
                        call = "",
                        algorithmId = Settings.algorithmId,
                        algorithmName = Control.AlgorithmCatalog.NameOf(Settings.algorithmId),
                        exceptionType = ex.GetType().FullName,
                        message = ex.Message,
                        stackTrace = Control.AlgorithmFault.TrimStack(ex.StackTrace),
                        simSeconds = SimulatedSeconds,
                        terminal = true,
                    });
            }
            else if (brain != null && brain.Failed && brain.Fault != null && brain.Fault.Terminal)
                Terminate(RunEndReason.Error, brain.Failure, brain.Fault.ToRecord());
            else if (SimulatedSeconds >= SafetyCapSeconds)
                Terminate(RunEndReason.TimeCap,
                    $"The program's safety cap of {SafetyCapSeconds / 3600f:0} simulated hours was reached.", null);

            // Stop conditions: held for the user in graphics (HoldOnStop), ended at once otherwise.
            else if (brain != null && brain.Failed)
                StopRun(RunEndReason.AlgorithmFault, brain.Failure, brain.Fault != null ? brain.Fault.ToRecord() : null);
            else if (robot != null && robot.Stuck)
                StopRun(RunEndReason.Stuck, robot.StuckReason, null);
            else if (outcome == Control.RunOutcome.Completed)
                StopRun(RunEndReason.Completed, brain != null && brain.EndedByBlockage ? brain.BlockageReason : null, null);
            else if (outcome == Control.RunOutcome.DockedLowBattery)
                StopRun(RunEndReason.ReturnedLowBattery, null, null);
            else if (outcome == Control.RunOutcome.DockUnreachable)
                StopRun(RunEndReason.Degenerate, robot.OutcomeMessage, null);
            else if (Runner.Battery != null && !Runner.Battery.CanOperate)
                StopRun(RunEndReason.BatteryDepleted, null, null);
            else if (UserLimitSeconds > 0f && SimulatedSeconds >= UserLimitSeconds)
                StopRun(RunEndReason.TimeLimit, $"The run's time limit of {UserLimitSeconds:0} simulated seconds was reached.", null);
        }

        void Tick(float unscaledDt)
        {
            if (State == Phase.Running) RealSeconds += unscaledDt;
        }

        // What was done after a stop, as saved in stopEvents[].action.
        const string RunStopEvent_Resumed = "Resumed";
        const string RunStopEvent_RunAgain = "RunAgain";
        const string RunStopEvent_Finished = "Finished";
        const string RunStopEvent_Back = "Back";
        const string RunStopEvent_Terminated = "Terminated";

        /// <summary>A stop condition: hold the run for the user (graphics, <see cref="HoldOnStop"/>), or end it now.</summary>
        void StopRun(RunEndReason reason, string message, RunFault fault)
        {
            if (State != Phase.Running) return;

            FreezeRun();
            State = Phase.Stopped;
            StopReason = reason;
            StopMessage = message;
            if (fault != null) lastFault = fault;
            stopEvents.Add(new RunStopEvent
            {
                simSeconds = SimulatedSeconds,
                reason = reason.ToString(),
                message = message ?? "",
                action = "",
            });

            Stopped?.Invoke(this);

            // (A handler may already have acted on the stop: resumed, finished or closed it.)
            if (State == Phase.Stopped && (!HoldOnStop || Headless))
                EndRun(reason, message, raiseFinished: true, RunStopEvent_Terminated);
        }

        /// <summary>A Terminal failure or the safety cap: the run ends now, whatever <see cref="HoldOnStop"/> says.</summary>
        void Terminate(RunEndReason reason, string message, RunFault fault)
        {
            if (State != Phase.Running) return;
            if (fault != null) lastFault = fault;
            if (reason == RunEndReason.Error) Debug.LogError($"The run was ended ({reason}): {message}");
            else Debug.LogWarning($"The run was ended ({reason}): {message}");
            EndRun(reason, message, raiseFinished: true, RunStopEvent_Terminated);
        }

        /// <summary>Ends a run that is going (as stopped by the user) or held at a stop (with that stop's reason). Nothing if already ended.</summary>
        void EndIfNotEnded(string action)
        {
            if (State == Phase.Running) EndRun(RunEndReason.StoppedByUser, null, raiseFinished: false, action);
            else if (State == Phase.Stopped) EndRun(StopReason, StopMessage, raiseFinished: false, action);
        }

        void EndRun(RunEndReason reason, string errorMessage, bool raiseFinished, string action)
        {
            if (State != Phase.Running && State != Phase.Stopped) return;

            // The stop event: a held stop gets what was done with it; any other end is one more stop.
            if (State == Phase.Stopped && stopEvents.Count > 0)
                stopEvents[stopEvents.Count - 1].action = action;
            else
                stopEvents.Add(new RunStopEvent
                {
                    simSeconds = SimulatedSeconds,
                    reason = reason.ToString(),
                    message = errorMessage ?? "",
                    action = action,
                });

            State = Phase.Finished;

            FreezeRun();

            var result = new RunResult
            {
                succeeded = reason != RunEndReason.Error && reason != RunEndReason.AlgorithmFault,
                errorMessage = errorMessage,
                endReason = reason,
                endReasonName = reason.ToString(),
                hasFault = lastFault != null,
                fault = lastFault ?? new RunFault(),
                stopEvents = new System.Collections.Generic.List<RunStopEvent>(stopEvents),
                levelName = Level.name,
                seed = Settings.seed,
                algorithmId = StartingAlgorithmId,
                algorithmName = Control.AlgorithmCatalog.NameOf(StartingAlgorithmId),
                algorithmSwitches = new System.Collections.Generic.List<RunAlgorithmSwitch>(algorithmSwitches),
                targetMaxClean = Settings.targetMaxClean,
                coveragePercent = Runner != null ? Runner.CoveragePercent : 0f,
                blockedAreaSquareMeters = Runner != null ? Runner.BlockedArea : 0f,
                simulatedSeconds = SimulatedSeconds,
                realSeconds = RealSeconds,
                slam = Settings.slam,
            };

            var brain = Runner != null ? Runner.Brain : null;
            if (brain != null)
            {
                result.driftMeters = brain.DriftMeters;
                result.driftHeadingDegrees = brain.DriftHeadingDegrees;
                result.msSensingPerSimSecond = brain.SensingMsPerSimSecond;
                result.msPosePerSimSecond = brain.PoseMsPerSimSecond;
                result.msMappingPerSimSecond = brain.MappingMsPerSimSecond;
                result.msLoopClosurePerSimSecond = brain.LoopClosureMsPerSimSecond;
                result.msControlPerSimSecond = brain.ControlMsPerSimSecond;
                result.msAlgorithmPerSimSecond = brain.AlgorithmMsPerSimSecond;
                result.msPlanningPerSimSecond = brain.PlanningMsPerSimSecond;
                result.msLongestPerceptionStep = brain.LongestPerceptionStepMs;
                result.msLongestFullStep = brain.LongestFullStepMs;

                result.blockageEvents = brain.BlockageEvents;
                result.endedByBlockage = brain.EndedByBlockage;
                result.blockageLeftSquareMeters = brain.BlockageLeftSquareMeters;
                result.blockageMessage = brain.BlockageReason ?? "";

                var drift = brain.Drift;
                if (Settings.slam && drift != null)
                {
                    result.driftMaxMeters = drift.MaxMeters;
                    result.driftP99Meters = drift.PositionPercentile(0.99f);
                    result.driftP95Meters = drift.PositionPercentile(0.95f);
                    result.driftRmsMeters = drift.RmsMeters;
                    result.driftMaxHeadingDegrees = drift.MaxDegrees;
                    result.driftP99HeadingDegrees = drift.HeadingPercentile(0.99f);
                    result.driftRmsHeadingDegrees = drift.RmsDegrees;

                    var aligned = drift.Aligned();
                    result.mapScalePercent = aligned.scalePercent;
                    result.mapRotationDegrees = aligned.rotationDegrees;
                    result.mapOffsetMeters = aligned.offsetMeters;
                    result.localDriftMaxMeters = aligned.maxMeters;
                    result.localDriftP99Meters = aligned.p99Meters;
                    result.localDriftRmsMeters = aligned.rmsMeters;
                    result.localDriftMaxHeadingDegrees = aligned.maxDegrees;
                    result.localDriftP99HeadingDegrees = aligned.p99Degrees;
                    result.localDriftRmsHeadingDegrees = aligned.rmsDegrees;
                }

                var tracking = brain.SlamTracking;
                if (tracking != null)
                {
                    result.scanMatches = tracking.Matches;
                    result.scanMatchesTooFewHits = tracking.TooFewHits;
                    result.scansInserted = tracking.ScansInserted;
                    result.submaps = tracking.Submaps.Count;
                }

                var loop = brain.SlamLoopClosure;
                if (loop != null)
                {
                    result.loopClosure = true;
                    result.loopSearches = loop.Searches;
                    result.loopLinks = loop.LoopLinks;
                    result.poseGraphSolves = loop.Optimisations;
                }
            }

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

            LagLog.EndRun(); // the lag summary, while the run's console_log.txt is still open
            WriteResult(result);
            Result = result;
            StopLog();

            if (raiseFinished) Finished?.Invoke(this);
        }

        RunLog log;

        void StopLog()
        {
            log?.Dispose();
            log = null;
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

        // Undoes FreezeRun: the robot, its body and the cleaning go again from exactly where they were.
        void UnfreezeRun()
        {
            if (Runner == null) return;

            if (Runner.Cleaning != null) Runner.Cleaning.enabled = true;

            if (Runner.Robot != null)
            {
                var body = Runner.Robot.GetComponent<Rigidbody2D>();
                if (body != null)
                {
                    body.simulated = true;
                    body.linearVelocity = Vector2.zero;
                    body.angularVelocity = 0f;
                }
                Runner.Robot.enabled = true;
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
            StopLog();
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
