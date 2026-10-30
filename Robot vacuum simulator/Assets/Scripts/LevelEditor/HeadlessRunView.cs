using System;
using RobotVacuum.Level;
using RobotVacuum.Sim;
using UnityEngine;
using UnityEngine.UIElements;

namespace RobotVacuum.LevelEditor
{
    /// <summary>
    /// What a headless run shows instead of the floor: live progress while the session plays as fast as it
    /// can, then the results, with the way to run it again, open its files or go back. Added by
    /// <see cref="SimTestDriver"/> for runs started with <see cref="RunMode.Headless"/>.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class HeadlessRunView : MonoBehaviour
    {
        PanelSettings panelSettings;
        SimulationSession session;
        LevelData level;
        Action leave;
        bool leaving;

        Label title;
        Label subtitle;
        Label simulated;
        Label coverage;
        Label battery;
        Label rate;
        VisualElement coverageFill;
        VisualElement results;
        Label resultsText;
        Label folderText;
        VisualElement runningActions;
        VisualElement finishedActions;

        float measuredSince;
        float measuredSimTime;
        float achievedSpeed;

        /// <summary>Shows <paramref name="headlessSession"/>. <paramref name="handedLevel"/> is destroyed with this view.</summary>
        public void Show(SimulationSession headlessSession, LevelData handedLevel, Action onLeave)
        {
            session = headlessSession;
            level = handedLevel;
            leave = onLeave;
            session.Finished += OnFinished;

            var root = UiPanel.Create(transform, "Headless Run UI", 1f, out panelSettings);
            var screen = Ui.Div(Ui.Div(root, "le-app"), "le-screen le-headless");
            var card = Ui.Div(screen, "le-headless__card");

            var header = Ui.Div(card, "le-headless__header");
            var tile = Ui.Div(header, "le-headless__icon");
            tile.Add(new IconElement(IconKind.Headless));
            var titles = Ui.Div(header, "le-headless__titles");
            title = Ui.Text(titles, string.Empty, "le-headless__title");
            subtitle = Ui.Text(titles, string.Empty, "le-headless__subtitle");

            var stats = Ui.Div(card, "le-headless__stats");
            simulated = Stat(stats, "Simulated");
            coverage = Stat(stats, "Coverage");
            battery = Stat(stats, "Battery left");
            rate = Stat(stats, "Speed");

            var track = Ui.Div(card, "le-headless__track");
            coverageFill = Ui.Div(track, "le-headless__fill");

            results = Ui.Div(card, "le-headless__results");
            resultsText = Ui.Text(results, string.Empty, "le-headless__results-text");
            folderText = Ui.Text(results, string.Empty, "le-headless__folder");

            var actions = Ui.Div(card, "le-headless__actions");
            runningActions = Ui.Div(actions, "le-headless__action-row");
            Ui.Button(runningActions, "Stop and go back", IconKind.Stop, Leave, "le-btn--ghost");

            finishedActions = Ui.Div(actions, "le-headless__action-row");
            Ui.Button(finishedActions, "Back", IconKind.Back, Leave, "le-btn--ghost");
            Ui.Button(finishedActions, "Open run folder", IconKind.Save, OpenFolder, "le-btn--ghost");
            Ui.Button(finishedActions, "Run again", IconKind.Undo, RunAgain, "le-btn--primary");

            root.schedule.Execute(Refresh).Every(200);
            ShowRunning();
        }

        static Label Stat(VisualElement parent, string label)
        {
            var stat = Ui.Div(parent, "le-headless__stat");
            var value = Ui.Text(stat, "—", "le-headless__stat-value");
            Ui.Text(stat, label, "le-headless__stat-label");
            return value;
        }

        void ShowRunning()
        {
            title.text = "Running headless";
            subtitle.text = Describe();
            Ui.SetVisible(results, false);
            Ui.SetVisible(runningActions, true);
            Ui.SetVisible(finishedActions, false);
            measuredSince = 0f;
            Refresh();
        }

        void OnFinished(SimulationSession finished)
        {
            var result = finished.Result;
            title.text = result != null ? $"Run finished: {EndReason(result.endReason)}" : "Run finished";
            subtitle.text = Describe();

            if (result != null)
            {
                resultsText.text =
                    $"{result.coveragePercent:0.0}% coverage · {result.fullyCleanedPercent:0.0}% of the floor fully clean · " +
                    $"{VacuumSettings.FormatDuration(result.simulatedSeconds)} simulated in {VacuumSettings.FormatDuration(result.realSeconds)}" +
                    (string.IsNullOrEmpty(result.errorMessage) ? string.Empty : $"\n{result.errorMessage}");
            }
            folderText.text = finished.RunFolder ?? string.Empty;

            Ui.SetVisible(results, true);
            Ui.SetVisible(runningActions, false);
            Ui.SetVisible(finishedActions, true);
            Refresh();
        }

        void Refresh()
        {
            if (session == null || session.State == SimulationSession.Phase.Closed) return;

            float seconds = session.SimulatedSeconds;
            MeasureSpeed(seconds);

            var runner = session.Runner;
            float percent = runner != null ? runner.CoveragePercent : 0f;
            simulated.text = VacuumSettings.FormatDuration(seconds);
            coverage.text = $"{percent:0.0}%";
            coverageFill.style.width = Length.Percent(Mathf.Clamp(percent, 0f, 100f));

            var cell = runner != null ? runner.Battery : null;
            battery.text = cell != null && cell.Capacity > 0f ? $"{cell.CurrentCharge / cell.Capacity * 100f:0}%" : "—";
            rate.text = session.State == SimulationSession.Phase.Running ? $"{achievedSpeed:0}×" : "Done";
        }

        void MeasureSpeed(float seconds)
        {
            float now = Time.unscaledTime;
            if (measuredSince <= 0f || seconds < measuredSimTime)
            {
                measuredSince = now;
                measuredSimTime = seconds;
                return;
            }

            float window = now - measuredSince;
            if (window < 1f) return;

            achievedSpeed = Mathf.Max(0f, seconds - measuredSimTime) / window;
            measuredSince = now;
            measuredSimTime = seconds;
        }

        string Describe()
        {
            var settings = session.Settings;
            string algorithm = settings != null ? MovementBrain.Labels[Mathf.Clamp((int)settings.pattern, 0, MovementBrain.Labels.Length - 1)] : "—";
            return $"{(level != null ? level.name : "Floor plan")} · {algorithm} · seed {(settings != null ? settings.seed : 0)}";
        }

        static string EndReason(RunEndReason reason) => reason switch
        {
            RunEndReason.Completed => "completed",
            RunEndReason.BatteryDepleted => "battery empty",
            RunEndReason.TimeCap => "time limit reached",
            RunEndReason.StoppedByUser => "stopped",
            RunEndReason.Error => "error",
            RunEndReason.Degenerate => "no progress",
            _ => "unknown",
        };

        void RunAgain()
        {
            if (session == null) return;
            if (session.Restart(out string error)) ShowRunning();
            else Debug.LogError($"Headless run: {error}");
        }

        void OpenFolder()
        {
            if (session != null && !string.IsNullOrEmpty(session.RunFolder))
                Application.OpenURL("file://" + session.RunFolder);
        }

        void Leave()
        {
            if (leaving) return;
            leaving = true;
            leave?.Invoke();
        }

        void OnDestroy()
        {
            if (session != null) session.Finished -= OnFinished;
            if (panelSettings != null) Destroy(panelSettings);
            if (level != null) Destroy(level);
        }
    }
}
