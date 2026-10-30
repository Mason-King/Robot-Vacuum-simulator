using System;
using System.Collections.Generic;
using System.Diagnostics;
using RobotVacuum.Level;
using RobotVacuum.Sim;
using UnityEngine;
using UnityEngine.UIElements;

namespace RobotVacuum.LevelEditor
{
    /// <summary>
    /// A batch experiment: every listed algorithm runs <see cref="trials"/> times on one floor plan, trial n
    /// using seed <see cref="baseSeed"/> + n for every algorithm, so the comparison is like for like.
    /// </summary>
    [Serializable]
    public sealed class BatchSpec
    {
        public string batchId = Guid.NewGuid().ToString("N");
        public List<MovementPattern> patterns = new List<MovementPattern>();
        public int trials = 10;
        public int baseSeed = 1;
        public float maxMinutes = 30f;

        /// <summary>Step without drawing anything, as fast as the machine allows.</summary>
        public bool headless = true;

        public int TotalRuns => patterns.Count * Mathf.Max(0, trials);
    }

    /// <summary>
    /// Runs a <see cref="BatchSpec"/> in the simulation scene, one <see cref="SimulationSession"/> per trial,
    /// started exactly as an interactive run is but with the trial's algorithm and seed. Headless trials skip
    /// every mesh and the heatmap, switch the camera off and play at the session's headless time scale.
    /// Results go to the Analysis page.
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(1001)] // checks a trial after the robot, the grid and the recorder have stepped
    public sealed class BatchRunner : MonoBehaviour
    {
        /// <summary>Time scale for a batch that is drawn: as fast as Unity allows, the same as headless.</summary>
        const float RenderedTimeScale = SimulationSession.HeadlessTimeScale;

        readonly Stopwatch batchTimer = new Stopwatch();
        readonly List<RunReport> finished = new List<RunReport>();
        readonly List<Behaviour> hidden = new List<Behaviour>();

        LevelData level;
        BatchSpec spec;
        PanelSettings panelSettings;
        SimulationSession session;
        SimWorld world;
        RunRecorder recorder;
        string pendingEnd;
        int patternIndex;
        int trial;
        bool complete;
        bool cancelled;
        float savedTimeScale;
        float savedMaximumDeltaTime;
        float measuredSince;
        float measuredSimTime;
        float achievedSpeed;

        Label statusLabel;
        Label detailLabel;
        VisualElement progressFill;

        int RunIndex => patternIndex * spec.trials + trial;

        float SimTime => world != null ? world.SimTime : 0f;

        /// <summary>Runs every trial of <paramref name="batch"/> on <paramref name="batchLevel"/>, which it then destroys.</summary>
        public void Run(LevelData batchLevel, BatchSpec batch)
        {
            level = batchLevel;
            spec = batch;
            savedTimeScale = Time.timeScale;
            savedMaximumDeltaTime = Time.maximumDeltaTime;

            if (spec.headless) HideCameras();
            BuildUi();

            batchTimer.Start();
            StartTrial();
        }

        void FixedUpdate()
        {
            if (complete || recorder == null || pendingEnd != null || world == null || !world.Running) return;

            string reason = TrialOver();
            if (reason == null) return;

            // Stop the trial on this very step; it is saved and torn down in Update.
            recorder.Freeze();
            FreezeRun();
            pendingEnd = reason;
        }

        void Update()
        {
            if (complete) return;
            if (pendingEnd != null) EndTrial(pendingEnd);
            MeasureSpeed();
            RefreshUi();
        }

        /// <summary>Why the current trial should stop, or null to keep going. The session ends battery and failure runs itself.</summary>
        string TrialOver()
        {
            if (cancelled) return "Cancelled";
            if (world.Robot == null) return "No vacuum in the scene";
            if (SimTime >= spec.maxMinutes * 60f) return "Time limit";
            if (world.Coverage >= 99.95f) return "Fully clean";
            if (world.OffFloor) return "Escaped through an open wall";
            return null;
        }

        void StartTrial()
        {
            pendingEnd = null;
            var settings = SimConfig.ToRunSettings(spec.patterns[patternIndex], spec.baseSeed + trial);
            if (!SimulationSession.TryStart(level, settings, spec.headless, OnRunBegan, out session, out string error))
            {
                UnityEngine.Debug.LogWarning($"Batch: trial {RunIndex + 1} could not start: {error}");
                session = null;
                NextOrComplete();
                return;
            }

            session.Finished += OnSessionFinished;
            if (spec.headless) return;

            SimulationSpeed.Apply(RenderedTimeScale, savedMaximumDeltaTime);
            var renderer = session.Runner.Level;
            if (Camera.main != null && renderer != null)
            {
                var bounds = level.Bounds();
                Vector2 a = renderer.LevelToWorld(bounds.min);
                Vector2 b = renderer.LevelToWorld(bounds.max);
                SimLauncher.FrameCamera(Camera.main,
                    Rect.MinMaxRect(Mathf.Min(a.x, b.x), Mathf.Min(a.y, b.y), Mathf.Max(a.x, b.x), Mathf.Max(a.y, b.y)));
            }
        }

        void OnRunBegan(SimulationSession started)
        {
            world = new SimWorld(started);
            world.ApplyVacuumSettings();
            recorder = new RunRecorder(world, spec.batchId, trial, spec.headless);
            measuredSince = 0f;
        }

        // The session ended the trial itself: battery empty, failed algorithm or its time cap. The run is
        // already frozen in place, so only the saving is left, done in Update.
        void OnSessionFinished(SimulationSession ended)
        {
            if (ended != session || pendingEnd != null) return;
            recorder?.Freeze();
            pendingEnd = SimWorld.Describe(ended.Result != null ? ended.Result.endReason : RunEndReason.None);
        }

        void FreezeRun()
        {
            if (world.Cleaning != null) world.Cleaning.enabled = false;
            if (world.Robot == null) return;

            world.Robot.enabled = false;
            if (world.Robot.TryGetComponent(out Rigidbody2D body))
            {
                body.linearVelocity = Vector2.zero;
                body.angularVelocity = 0f;
                body.simulated = false;
            }
        }

        void EndTrial(string reason)
        {
            pendingEnd = null;

            // Saved while the run's grid still exists, then the session writes its own result and tears down.
            var report = recorder.Finish(reason, save: false);
            recorder.WriteFiles();
            recorder.Detach();
            recorder = null;
            if (reason != "Cancelled") finished.Add(report);

            CloseSession();
            NextOrComplete();
        }

        void NextOrComplete()
        {
            if (cancelled || !Advance())
            {
                Complete();
                return;
            }

            StartTrial();
        }

        void CloseSession()
        {
            if (session == null) return;
            session.Finished -= OnSessionFinished;
            session.Close();
            session = null;
            world = null;
        }

        bool Advance()
        {
            if (++trial < spec.trials) return true;
            trial = 0;
            return ++patternIndex < spec.patterns.Count;
        }

        void Complete()
        {
            complete = true;
            try
            {
                if (finished.Count > 0) RunHistory.AddRange(finished);
            }
            catch (Exception exception)
            {
                UnityEngine.Debug.LogWarning($"Batch: could not save results: {exception.Message}");
            }

            if (finished.Count > 0) SimLauncher.OpenAnalysis(spec.batchId);
            else SimLauncher.OpenStartScreen(StartPage.Simulate);
        }

        void HideCameras()
        {
            foreach (var camera in FindObjectsByType<Camera>(FindObjectsSortMode.None))
                if (camera.enabled) { camera.enabled = false; hidden.Add(camera); }
        }

        void MeasureSpeed()
        {
            float now = Time.unscaledTime;
            if (measuredSince <= 0f || SimTime < measuredSimTime)
            {
                measuredSince = now;
                measuredSimTime = SimTime;
                return;
            }

            float window = now - measuredSince;
            if (window < 1f) return;

            achievedSpeed = Mathf.Max(0f, SimTime - measuredSimTime) / window;
            measuredSince = now;
            measuredSimTime = SimTime;
        }

        // ---------------------------------------------------------------- UI

        void BuildUi()
        {
            var root = UiPanel.Create(transform, "Batch UI", 1f, out panelSettings);
            var app = Ui.Div(root, spec.headless ? "le-app" : "le-app le-app--running");
            var screen = Ui.Div(app, "le-screen le-batch");
            if (!spec.headless) screen.pickingMode = PickingMode.Ignore;

            var card = Ui.Div(screen, "le-panel le-batch__card");
            var header = Ui.Div(card, "le-panel__header");
            var tile = Ui.Div(header, "le-start__step-icon");
            tile.Add(new IconElement(IconKind.Chart));
            var titles = Ui.Div(header, "le-panel__titles");
            Ui.Text(titles, "Running batch experiment", "le-panel__title");
            Ui.Text(titles, $"{level?.name} · {spec.patterns.Count} algorithm(s) × {spec.trials} seeds" +
                            (spec.headless ? " · headless" : string.Empty), "le-panel__text");

            statusLabel = Ui.Text(card, string.Empty, "le-batch__status");
            var track = Ui.Div(card, "le-compare-row__track le-batch__track");
            progressFill = Ui.Div(track, "le-compare-row__fill");
            detailLabel = Ui.Text(card, string.Empty, "le-empty-hint");

            var actions = Ui.Div(card, "le-empty__actions");
            Ui.Button(actions, "Stop and keep results", IconKind.Stop, () => cancelled = true, "le-btn--ghost");

            if (spec.headless)
                Ui.Text(card, "Drawing is switched off so the trials run as fast as possible. Each trial is a normal run with its own seed, so it can be replayed exactly.",
                    "le-empty-hint");
            RefreshUi();
        }

        void RefreshUi()
        {
            if (statusLabel == null || spec == null) return;

            int total = spec.TotalRuns;
            int current = Mathf.Min(RunIndex + 1, total);
            string algorithm = patternIndex < spec.patterns.Count ? MovementAlgorithms.LabelOf(spec.patterns[patternIndex]) : "Done";

            statusLabel.text = $"{algorithm} · seed {spec.baseSeed + trial} · run {current} of {total}";
            progressFill.style.width = Length.Percent(total > 0 ? (RunIndex + Mathf.Clamp01(SimTime / (spec.maxMinutes * 60f))) / total * 100f : 0f);

            double perRun = RunIndex > 0 ? batchTimer.Elapsed.TotalSeconds / RunIndex : 0;
            string eta = RunIndex > 0 ? $" · about {VacuumSettings.FormatDuration((float)(perRun * (total - RunIndex)))} left" : string.Empty;
            detailLabel.text = $"Simulated {VacuumSettings.FormatDuration(SimTime)} · {(world != null ? world.Coverage : 0f):0.0}% clean · " +
                               $"{achievedSpeed:0}× real time{eta}";
        }

        void OnDestroy()
        {
            recorder?.Detach();
            CloseSession();
            Time.timeScale = savedTimeScale;
            if (savedMaximumDeltaTime > 0f) Time.maximumDeltaTime = savedMaximumDeltaTime;
            foreach (var behaviour in hidden)
                if (behaviour != null) behaviour.enabled = true;
            if (panelSettings != null) Destroy(panelSettings);
            if (level != null) Destroy(level);
        }
    }
}
