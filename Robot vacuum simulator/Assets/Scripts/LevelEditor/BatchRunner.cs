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
    /// Runs a <see cref="BatchSpec"/> in the simulation scene. Each trial starts through
    /// <see cref="SimWorld.BeginRun"/> exactly as an interactive run does and steps the same engine, so a
    /// trial's results match a rendered run with the same seed and settings. Headless trials switch the
    /// camera off and step as fast as the frame budget allows. Results go to the Analysis page.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class BatchRunner : MonoBehaviour
    {
        const double FrameBudgetMilliseconds = 40.0;
        const int StepsPerCheck = 25;

        readonly Stopwatch frameTimer = new Stopwatch();
        readonly Stopwatch batchTimer = new Stopwatch();
        readonly List<RunReport> finished = new List<RunReport>();
        readonly List<Behaviour> hidden = new List<Behaviour>();

        SimWorld world;
        BatchSpec spec;
        PanelSettings panelSettings;
        RunRecorder recorder;
        int patternIndex;
        int trial;
        bool complete;
        bool cancelled;

        Label statusLabel;
        Label detailLabel;
        VisualElement progressFill;

        int RunIndex => patternIndex * spec.trials + trial;

        public void Run(SimWorld simWorld, BatchSpec batch)
        {
            world = simWorld;
            spec = batch;
            world.Engine.ManualStepping = true;

            if (spec.headless) HideRendering();
            BuildUi();

            batchTimer.Start();
            StartTrial();
        }

        void Update()
        {
            if (complete || recorder == null) return;

            frameTimer.Restart();
            while (!complete && frameTimer.Elapsed.TotalMilliseconds < FrameBudgetMilliseconds)
            {
                world.Engine.StepMany(StepsPerCheck, () => TrialOver() != null);

                string reason = TrialOver() ?? (world.OffFloor ? "Escaped through an open wall" : null);
                if (reason != null) EndTrial(reason);
            }

            RefreshUi();
        }

        /// <summary>Why the current trial should stop, or null to keep going. Cheap enough to check every step.</summary>
        string TrialOver()
        {
            if (cancelled) return "Cancelled";
            if (world.Robot == null) return "No vacuum in the scene";
            if (world.Robot.AlgorithmFailed) return "Algorithm failed";
            if (world.Battery != null && !world.Battery.CanOperate) return "Battery empty";
            if (world.Engine.SimTime >= spec.maxMinutes * 60f) return "Time limit";
            if (world.Coverage >= 99.95f) return "Fully clean";
            return null;
        }

        void StartTrial()
        {
            var pattern = spec.patterns[patternIndex];
            int seed = spec.baseSeed + trial;

            world.BeginRun(seed, pattern, SimConfig.Picture);
            recorder = new RunRecorder(world, seed, spec.batchId, trial, spec.headless);
        }

        void EndTrial(string reason)
        {
            var report = recorder.Finish(reason, save: false);
            recorder.WriteFiles();
            recorder.Detach();
            recorder = null;
            if (reason != "Cancelled") finished.Add(report);

            if (cancelled || !Advance())
            {
                Complete();
                return;
            }

            StartTrial();
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

        void HideRendering()
        {
            foreach (var camera in FindObjectsByType<Camera>(FindObjectsSortMode.None))
                if (camera.enabled) { camera.enabled = false; hidden.Add(camera); }
            foreach (var heatmap in FindObjectsByType<CoverageHeatmapRenderer>(FindObjectsSortMode.None))
                if (heatmap.enabled) { heatmap.enabled = false; hidden.Add(heatmap); }
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
            Ui.Text(titles, $"{world.Level?.name} · {spec.patterns.Count} algorithm(s) × {spec.trials} seeds" +
                            (spec.headless ? " · headless" : string.Empty), "le-panel__text");

            statusLabel = Ui.Text(card, string.Empty, "le-batch__status");
            var track = Ui.Div(card, "le-compare-row__track le-batch__track");
            progressFill = Ui.Div(track, "le-compare-row__fill");
            detailLabel = Ui.Text(card, string.Empty, "le-empty-hint");

            var actions = Ui.Div(card, "le-empty__actions");
            Ui.Button(actions, "Stop and keep results", IconKind.Stop, () => cancelled = true, "le-btn--ghost");

            if (spec.headless)
                Ui.Text(card, "Drawing is switched off so the trials run as fast as possible. Results are identical to a rendered run with the same seed.",
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
            progressFill.style.width = Length.Percent(total > 0 ? (RunIndex + Mathf.Clamp01(world.Engine.SimTime / (spec.maxMinutes * 60f))) / total * 100f : 0f);

            double perRun = RunIndex > 0 ? batchTimer.Elapsed.TotalSeconds / RunIndex : 0;
            string eta = RunIndex > 0 ? $" · about {VacuumSettings.FormatDuration((float)(perRun * (total - RunIndex)))} left" : string.Empty;
            detailLabel.text = $"Simulated {VacuumSettings.FormatDuration(world.Engine.SimTime)} · {world.Coverage:0.0}% clean · " +
                               $"{world.Engine.AchievedSpeed:0}× real time{eta}";
        }

        void OnDestroy()
        {
            recorder?.Detach();
            foreach (var behaviour in hidden)
                if (behaviour != null) behaviour.enabled = true;
            if (panelSettings != null) Destroy(panelSettings);
            if (world != null && world.Level != null) Destroy(world.Level);
        }
    }
}
