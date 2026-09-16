using System;
using System.Collections.Generic;
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
        VacuumRobot robot;
        Battery battery;
        VacuumSettings vacuumSettings;
        OverlayLayer overlay;
        float startTime;
        float savedTimeScale = 1f;
        bool leaving;

        /// <summary>Builds the bar. <paramref name="handedLevel"/> is destroyed along with the scene.</summary>
        public void Show(LevelData handedLevel)
        {
            level = handedLevel;
            cleaning = FindAnyObjectByType<VacuumCleaningController>();
            robot = FindAnyObjectByType<VacuumRobot>();
            battery = robot != null ? robot.GetComponent<Battery>() : null;
            if (robot != null) vacuumSettings = VacuumSettings.From(robot, battery);
            startTime = Time.time;
            savedTimeScale = Time.timeScale;

            var root = UiPanel.Create(transform, "Simulator HUD", 1f, out panelSettings);
            root.pickingMode = PickingMode.Ignore;

            var app = Ui.Div(root, "le-app le-app--running");
            app.pickingMode = PickingMode.Ignore;
            overlay = new OverlayLayer();
            var screen = Ui.Div(app, "le-screen");
            screen.pickingMode = PickingMode.Ignore;

            var bar = Ui.Div(screen, "le-float le-run-bar");
            Ui.Button(bar, "Editor", IconKind.Back, Leave, "le-btn--ghost");
            Ui.IconButton(bar, IconKind.Undo, "Restart", Restart, "le-btn--ghost");

            VisualElement settingsButton = null;
            settingsButton = Ui.Button(bar, "Settings", IconKind.Spawn,
                () => ShowVacuumSettings(settingsButton), "le-btn--ghost");
            Ui.Tooltip(settingsButton, "Vacuum speed and battery");

            // Scales simulated time, so the robot, cleaning and the runtime readout stay together.
            var speed = new Segmented(SpeedLabels, Array.IndexOf(Speeds, 1f));
            speed.SelectionChanged += index => Time.timeScale = Speeds[index];
            Ui.Tooltip(speed, "Simulation speed");
            bar.Add(speed);

            var movement = new Segmented(MovementBrain.Labels, (int)SimLauncher.MovementPattern);
            movement.SelectionChanged += index =>
            {
                SimLauncher.MovementPattern = (MovementPattern)index;
                foreach (var robot in FindObjectsByType<VacuumRobot>(FindObjectsSortMode.None))
                    robot.Pattern = SimLauncher.MovementPattern;
            };
            Ui.Tooltip(movement, "Movement algorithm");
            bar.Add(movement);

            var live = Ui.Div(bar, "le-run-live");
            Ui.Div(live, "le-run-live__dot");
            Ui.Text(live, level != null ? level.name : "Floor plan");

            var runtime = Ui.RunStat(bar, "Runtime");
            var speedStat = Ui.RunStat(bar, "Speed");
            var batteryStat = Ui.RunStat(bar, "Battery");
            var distance = Ui.RunStat(bar, "Distance");
            var surface = Ui.RunStat(bar, "Surface");
            var blocked = Ui.RunStat(bar, "Blocked");

            bar.schedule.Execute(() =>
            {
                var elapsed = TimeSpan.FromSeconds(Time.time - startTime);
                runtime.text = $"{(int)elapsed.TotalMinutes:00}:{elapsed.Seconds:00}";
                speedStat.text = new VacuumSettings().FormatSpeed(
                    robot != null ? robot.SpeedMetersPerSecond : 0f);
                batteryStat.text = battery != null
                    ? VacuumSettings.FormatBattery(battery.CurrentLifeSeconds, battery.BatteryLifeSeconds)
                    : "—";
                distance.text = Ui.FormatMetres(robot != null ? robot.DistanceTravelled : 0f);
                surface.text = robot != null && robot.CurrentFloor != null ? robot.CurrentFloor.Label : "—";
                bool hasGrid = cleaning != null && cleaning.Grid != null;
                blocked.text = hasGrid ? Ui.FormatArea(cleaning.Grid.NonCleanableAreaSquareMeters) : "—";
            }).Every(500);

            app.Add(overlay);

            if (root.panel != null) HookEscape(root.panel);
            else root.RegisterCallback<AttachToPanelEvent>(evt => HookEscape(evt.destinationPanel));
        }

        void Restart()
        {
            if (robot != null) robot.ResetToSpawn();
        }

        void ShowVacuumSettings(VisualElement anchor)
        {
            if (vacuumSettings == null) return;

            overlay.ShowMenuBelow(anchor, new List<MenuEntry>
            {
                MenuEntry.Heading("VACUUM"),
                MenuEntry.Custom(_ => BuildVacuumSettings(anchor)),
            });
        }

        VisualElement BuildVacuumSettings(VisualElement anchor)
        {
            var panel = Ui.Div(null, "le-run-settings");

            var units = new Segmented(new[] { "m/s", "ft/s" }, vacuumSettings.useFeet ? 1 : 0);
            units.SelectionChanged += index =>
            {
                vacuumSettings.useFeet = index == 1;
                ShowVacuumSettings(anchor);
            };
            panel.Add(units);

            AddVacuumSetting(panel, "Drive speed", 0.05f, 2f,
                vacuumSettings.driveSpeed, vacuumSettings.FormatSpeed, value => vacuumSettings.driveSpeed = value);
            AddVacuumSetting(panel, "Reverse speed", 0.05f, 1.5f,
                vacuumSettings.reverseSpeed, vacuumSettings.FormatSpeed, value => vacuumSettings.reverseSpeed = value);
            AddVacuumSetting(panel, "Turn speed", 30f, 720f,
                vacuumSettings.turnSpeed, value => $"{value:0} °/s", value => vacuumSettings.turnSpeed = value);
            AddVacuumSetting(panel, "Battery life", 10f, 1800f,
                vacuumSettings.batteryLifeSeconds, VacuumSettings.FormatDuration,
                value => vacuumSettings.batteryLifeSeconds = value);
            Ui.Text(panel, "Changing battery life recharges the vacuum.", "le-empty-hint");

            return panel;
        }

        void AddVacuumSetting(VisualElement parent, string label, float min, float max, float value,
            Func<float, string> format, Action<float> store)
        {
            Ui.Text(parent, label, "le-field-label");

            var slider = new ValueSlider(min, max, value, format, true);
            slider.ValueChanged += next =>
            {
                store(next);
                if (robot != null) vacuumSettings.ApplyTo(robot, battery);
            };
            parent.Add(slider);
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
            overlay = null;
            if (panelSettings != null) Destroy(panelSettings);
            if (level != null) Destroy(level);
        }
    }
}
