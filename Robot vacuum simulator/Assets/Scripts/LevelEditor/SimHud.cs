using System;
using RobotVacuum.Level;
using RobotVacuum.Sim;
using UnityEngine;
using UnityEngine.UIElements;

namespace RobotVacuum.LevelEditor
{
    /// <summary>
    /// The bar laid over the simulation scene when a floor plan was opened from the editor: what is
    /// running, for how long, how much floor is clean, and the way back. Added by <see cref="SimLauncher"/>.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class SimHud : MonoBehaviour
    {
        static readonly float[] Speeds = { 0.5f, 1f, 2f, 4f };
        static readonly string[] SpeedLabels = { "0.5×", "1×", "2×", "4×" };

        PanelSettings panelSettings;
        LevelData level;
        VacuumCleaningController cleaning;
        float startTime;
        float savedTimeScale = 1f;
        bool leaving;

        /// <summary>Builds the bar. <paramref name="handedLevel"/> is destroyed along with the scene.</summary>
        public void Show(LevelData handedLevel)
        {
            level = handedLevel;
            cleaning = FindAnyObjectByType<VacuumCleaningController>();
            startTime = Time.time;
            savedTimeScale = Time.timeScale;

            var root = UiPanel.Create(transform, "Simulator HUD", 1f, out panelSettings);
            root.pickingMode = PickingMode.Ignore;

            var app = Ui.Div(root, "le-app le-app--running");
            app.pickingMode = PickingMode.Ignore;
            var screen = Ui.Div(app, "le-screen");
            screen.pickingMode = PickingMode.Ignore;

            var bar = Ui.Div(screen, "le-float le-run-bar");
            Ui.Button(bar, "Editor", IconKind.Back, Leave, "le-btn--ghost");

            // Scales simulated time, so the robot, the cleaning rate and the clock all speed up together.
            var speed = new Segmented(SpeedLabels, Array.IndexOf(Speeds, 1f));
            speed.SelectionChanged += index => Time.timeScale = Speeds[index];
            bar.Add(speed);

            var movement = new Segmented(MovementBrain.Labels, (int)SimLauncher.MovementPattern);
            movement.SelectionChanged += index =>
            {
                SimLauncher.MovementPattern = (MovementPattern)index;
                foreach (var robot in FindObjectsByType<VacuumRobot>(FindObjectsSortMode.None))
                    robot.Pattern = SimLauncher.MovementPattern;
            };
            bar.Add(movement);

            var live = Ui.Div(bar, "le-run-live");
            Ui.Div(live, "le-run-live__dot");
            Ui.Text(live, level != null ? level.name : "Floor plan");

            var time = Ui.RunStat(bar, "Time");
            var cleaned = Ui.RunStat(bar, "Cleaned");
            var blocked = Ui.RunStat(bar, "Blocked");

            bar.schedule.Execute(() =>
            {
                var elapsed = TimeSpan.FromSeconds(Time.time - startTime);
                time.text = $"{(int)elapsed.TotalMinutes:00}:{elapsed.Seconds:00}";
                bool hasGrid = cleaning != null && cleaning.Grid != null;
                cleaned.text = hasGrid ? $"{cleaning.CoveragePercent:0.0}%" : "—";
                blocked.text = hasGrid ? Ui.FormatArea(cleaning.Grid.NonCleanableAreaSquareMeters) : "—";
            }).Every(500);

            if (root.panel != null) HookEscape(root.panel);
            else root.RegisterCallback<AttachToPanelEvent>(evt => HookEscape(evt.destinationPanel));
        }

        void HookEscape(IPanel panel)
        {
            panel.visualTree.RegisterCallback<KeyDownEvent>(evt =>
            {
                if (evt.keyCode == KeyCode.Escape) Leave();
            }, TrickleDown.TrickleDown);
        }

        void Leave()
        {
            if (!leaving) leaving = SimLauncher.ReturnToEditor();
        }

        void OnDestroy()
        {
            Time.timeScale = savedTimeScale;
            if (panelSettings != null) Destroy(panelSettings);
            if (level != null) Destroy(level);
        }
    }
}
