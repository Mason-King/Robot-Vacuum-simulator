using System;
using System.Collections.Generic;
using RobotVacuum.Level;
using RobotVacuum.Sim;
using UnityEngine;
using UnityEngine.UIElements;

namespace RobotVacuum.LevelEditor
{
    /// <summary>
    /// The bar laid over the simulation scene: what is running, for how long, how much floor is clean, and
    /// the way back. Each run is recorded by a <see cref="RunRecorder"/> and saved for the Analysis screen
    /// when it finishes, when the battery runs flat, or when the player leaves. Every setting, including the
    /// furniture, stays editable mid-run, and each change is logged against the simulated time it happened.
    /// Added by <see cref="SimLauncher"/>.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class SimHud : MonoBehaviour
    {
        /// <summary>Runs shorter than this are not worth keeping when the player leaves.</summary>
        const float MinRecordedSeconds = 2f;

        /// <summary>How long the vacuum must be off the floor plan before the run is stopped.</summary>
        const float EscapeGraceSeconds = 0.5f;

        PanelSettings panelSettings;
        SimWorld world;
        OverlayLayer overlay;
        RunRecorder recorder;
        int seed;
        bool leaving;
        bool savedOnEmpty;
        bool escaped;
        float offFloorSince = -1f;
        VisualElement pauseButton;
        Label seedLabel;

        VacuumSettings Settings => SimConfig.Vacuum;
        SimEngine Engine => world.Engine;
        LevelData Level => world.Level;

        /// <summary>Builds the bar and starts the first run. The world's level is destroyed with the scene.</summary>
        public void Show(SimWorld simWorld)
        {
            world = simWorld;

            var root = UiPanel.Create(transform, "Simulator HUD", 1f, out panelSettings);
            root.pickingMode = PickingMode.Ignore;

            var app = Ui.Div(root, "le-app le-app--running");
            app.pickingMode = PickingMode.Ignore;
            overlay = new OverlayLayer();
            var screen = Ui.Div(app, "le-screen");
            screen.pickingMode = PickingMode.Ignore;

            BuildControls(Ui.Div(screen, "le-float le-run-bar"));
            BuildStats(Ui.Div(screen, "le-float le-run-bar le-run-bar--stats"));
            app.Add(overlay);

            if (root.panel != null) HookEscape(root.panel);
            else root.RegisterCallback<AttachToPanelEvent>(evt => HookEscape(evt.destinationPanel));

            BeginRun(SimConfig.NextRunSeed());
        }

        void BuildControls(VisualElement bar)
        {
            bool fromEditor = SimLauncher.Origin == SimOrigin.Editor;
            var back = Ui.Button(bar, fromEditor ? "Editor" : "Back", IconKind.Back, Leave, "le-btn--ghost");
            Ui.Tooltip(back, fromEditor ? "Back to the level editor   Esc" : "Back to the floor plan list   Esc");

            Ui.IconButton(bar, IconKind.Undo, "Restart on a dirty floor (same seed, unless the configuration draws a new one each run)",
                Restart, "le-btn--ghost");

            pauseButton = Ui.IconButton(bar, IconKind.Pause, "Pause or resume the simulation", TogglePause, "le-btn--ghost");

            // Speed is how many simulated seconds pass per real second. The step size never changes, so the
            // results are the same at every speed; only the wait differs.
            var speed = new ValueSlider(SimEngine.MinSpeed, SimEngine.MaxSpeed, Engine.Speed,
                v => v < 1f ? $"{v:0.##}×" : $"{Mathf.Round(v):0}×");
            speed.AddToClassList("le-run-speed");
            speed.ValueChanged += value => Engine.Speed = value < 1f ? value : Mathf.Round(value);
            Ui.Tooltip(speed, "Simulation speed, from ¼× to 100× real time");
            bar.Add(speed);

            VisualElement settingsButton = null;
            settingsButton = Ui.Button(bar, "Settings", IconKind.Sliders, () => ShowVacuumSettings(settingsButton), "le-btn--ghost");
            Ui.Tooltip(settingsButton, "Vacuum speed, battery and units. Changes apply immediately and are logged.");

            VisualElement furnitureButton = null;
            furnitureButton = Ui.Button(bar, "Furniture", IconKind.Obstacle, () => ShowFurniture(furnitureButton), "le-btn--ghost");
            Ui.Tooltip(furnitureButton, "Change or remove furniture mid-run. Changes are logged.");

            var picture = new Segmented(Pictures.Names, (int)SimLauncher.Picture);
            picture.SelectionChanged += index =>
            {
                if ((PictureKind)index == PictureKind.Image && !ImageFilePicker.TryLoad(out string problem))
                {
                    picture.SetSelectedWithoutNotify((int)SimLauncher.Picture);
                    if (problem != null) overlay.Toast(problem);
                    return;
                }

                // A new picture is a new drawing: start it from the top on a clean floor.
                SimLauncher.Picture = (PictureKind)index;
                Restart();
            };
            Ui.SetVisible(picture, SimLauncher.MovementPattern == MovementPattern.Picture);

            var movement = new Segmented(MovementBrain.Labels, (int)SimLauncher.MovementPattern);
            movement.SelectionChanged += index =>
            {
                var next = (MovementPattern)index;
                recorder?.LogParameter("Algorithm", MovementAlgorithms.LabelOf(SimLauncher.MovementPattern), MovementAlgorithms.LabelOf(next));
                if (recorder != null && Engine.SimTime > 0f) recorder.MarkMixedPatterns();

                SimLauncher.MovementPattern = next;
                if (world.Robot != null) world.Robot.Pattern = next;
                Ui.SetVisible(picture, next == MovementPattern.Picture);
            };
            Ui.Tooltip(movement, "Movement algorithm. Switching mid-run is logged, and the run counts as mixed.");
            bar.Add(movement);
            bar.Add(picture);

            var finish = Ui.Button(bar, "Finish", IconKind.Check, Finish, "le-btn--primary");
            Ui.Tooltip(finish, "End the run and see its analysis");
        }

        void BuildStats(VisualElement bar)
        {
            var live = Ui.Div(bar, "le-run-live");
            Ui.Div(live, "le-run-live__dot");
            Ui.Text(live, Level != null ? Level.name : "Floor plan");

            var runtime = Ui.RunStat(bar, "Sim time");
            var rate = Ui.RunStat(bar, "Actual speed");
            var speedStat = Ui.RunStat(bar, "Vacuum");
            var batteryStat = Ui.RunStat(bar, "Battery");
            var distance = Ui.RunStat(bar, "Distance");
            var cleaned = Ui.RunStat(bar, "Cleaned");
            var surface = Ui.RunStat(bar, "Surface");
            var blocked = Ui.RunStat(bar, "Blocked");
            seedLabel = Ui.RunStat(bar, "Seed");

            bar.schedule.Execute(() =>
            {
                var robot = world.Robot;
                var battery = world.Battery;
                runtime.text = VacuumSettings.FormatDuration(Engine.SimTime);
                rate.text = Engine.Paused ? "Paused" : $"{Engine.AchievedSpeed:0.#}×";
                speedStat.text = Settings.FormatSpeed(robot != null ? robot.SpeedMetersPerSecond : 0f);
                if (battery != null)
                {
                    batteryStat.text = VacuumSettings.FormatBattery(battery.CurrentCharge, battery.Capacity);
                    Ui.SetClass(batteryStat, "le-run-stat__value--warn", VacuumSettings.IsBatteryLow(battery.CurrentCharge, battery.Capacity));
                }
                else batteryStat.text = "—";
                distance.text = Ui.FormatLength(robot != null ? robot.DistanceTravelled : 0f);
                surface.text = robot != null && robot.CurrentFloor != null ? robot.CurrentFloor.Label : "—";
                bool hasGrid = world.Cleaning != null && world.Cleaning.Grid != null;
                blocked.text = hasGrid ? Ui.FormatArea(world.Cleaning.Grid.NonCleanableAreaSquareMeters) : "—";
                cleaned.text = hasGrid ? $"{world.Coverage:0.0}%" : "—";
            }).Every(250);
        }

        // ---------------------------------------------------------------- run lifecycle

        void BeginRun(int runSeed)
        {
            recorder?.Detach();
            seed = runSeed;
            world.BeginRun(seed, SimLauncher.MovementPattern, SimLauncher.Picture);
            recorder = new RunRecorder(world, seed);
            savedOnEmpty = false;
            escaped = false;
            offFloorSince = -1f;
            if (seedLabel != null) seedLabel.text = seed.ToString();
        }

        void Restart()
        {
            if (escaped) SetPaused(false);
            BeginRun(SimConfig.NextRunSeed());
        }

        void Update()
        {
            if (recorder == null || leaving) return;

            // A flat battery stops the vacuum but not the session: the run is saved, and if the battery
            // is changed afterwards the vacuum carries on and the saved results are updated on Finish.
            var battery = world.Battery;
            if (battery != null && !battery.CanOperate && !savedOnEmpty)
            {
                savedOnEmpty = true;
                if (Save("Battery empty"))
                    overlay.Toast("Battery empty · run saved. Settings stay editable.", "View analysis",
                        () => SimLauncher.OpenAnalysis(recorder.Report.id));
            }

            CheckEscape();
        }

        /// <summary>Stops the run if the vacuum has driven out through an open wall into empty space.</summary>
        void CheckEscape()
        {
            if (escaped || Engine.Paused) return;

            if (!world.OffFloor)
            {
                offFloorSince = -1f;
                return;
            }

            if (offFloorSince < 0f) offFloorSince = Engine.SimTime;
            if (Engine.SimTime - offFloorSince < EscapeGraceSeconds) return;

            escaped = true;
            SetPaused(true);
            overlay.ShowDialog("The vacuum escaped",
                "It drove out through a gap in an outside wall and will keep going forever. Add the missing wall in the level editor, or restart.",
                null,
                new DialogButton("Restart", ButtonStyle.Ghost, Restart),
                new DialogButton(SimLauncher.Origin == SimOrigin.Editor ? "Back to editor" : "Edit floor plan", ButtonStyle.Primary,
                    () => leaving = SimLauncher.OpenLevelEditor(SimLauncher.SimulatedPath)));
        }

        bool Save(string reason)
        {
            try
            {
                recorder.Finish(reason);
                return true;
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"Simulator: could not save the run: {exception.Message}");
                overlay.Toast("Couldn't save this run's results.");
                return false;
            }
        }

        void Finish()
        {
            if (leaving) return;

            string reason = world.Battery != null && !world.Battery.CanOperate ? "Battery empty" : "Finished";
            if (Save(reason)) leaving = SimLauncher.OpenAnalysis(recorder.Report.id);
        }

        void Leave()
        {
            if (leaving) return;

            if (Engine.SimTime >= MinRecordedSeconds) Save(savedOnEmpty ? "Battery empty" : "Stopped");
            leaving = SimLauncher.ReturnFromSimulation();
        }

        void TogglePause() => SetPaused(!Engine.Paused);

        void SetPaused(bool paused)
        {
            Engine.Paused = paused;
            pauseButton.Q<IconElement>().Kind = paused ? IconKind.Play : IconKind.Pause;
        }

        // ---------------------------------------------------------------- settings

        void ShowVacuumSettings(VisualElement anchor)
        {
            overlay.ShowMenuBelow(anchor, new List<MenuEntry>
            {
                MenuEntry.Heading("VACUUM"),
                MenuEntry.Custom(_ => BuildVacuumSettings(anchor)),
            });
        }

        VisualElement BuildVacuumSettings(VisualElement anchor)
        {
            var panel = Ui.Div(null, "le-run-settings");

            var units = new Segmented(new[] { "Imperial (ft)", "Metric (m)" }, Settings.useFeet ? 0 : 1);
            units.SelectionChanged += index =>
            {
                SimConfig.UseFeet = index == 0;
                ShowVacuumSettings(anchor); // rebuild so the slider read-outs switch unit too
            };
            panel.Add(units);

            AddVacuumSetting(panel, "Drive speed", 0.05f, 2f, Settings.driveSpeed, Settings.FormatSpeed,
                value => Settings.driveSpeed = value);
            AddVacuumSetting(panel, "Reverse speed", 0.05f, 1.5f, Settings.reverseSpeed, Settings.FormatSpeed,
                value => Settings.reverseSpeed = value);
            AddVacuumSetting(panel, "Turn speed", 30f, 720f, Settings.turnSpeed, value => $"{value:0} °/s",
                value => Settings.turnSpeed = value);
            AddVacuumSetting(panel, "Battery capacity", 10f, 1800f, Settings.batteryCapacity, VacuumSettings.FormatCapacity,
                value => Settings.batteryCapacity = value);
            Ui.Text(panel, "Changing battery capacity recharges the vacuum. Every change is logged with the simulated time.", "le-empty-hint");

            return panel;
        }

        void AddVacuumSetting(VisualElement parent, string label, float min, float max, float value,
            Func<float, string> format, Action<float> store)
        {
            Ui.Text(parent, label, "le-field-label");

            float current = value;
            var slider = new ValueSlider(min, max, value, format, true);
            slider.ValueChanged += next =>
            {
                recorder?.LogParameter(label, format(current), format(next));
                current = next;
                store(next);
                SimConfig.Save();
                if (world.Robot != null) Settings.ApplyTo(world.Robot, world.Battery);
                if (world.Battery != null && world.Battery.CanOperate) savedOnEmpty = false;
            };
            parent.Add(slider);
        }

        // ---------------------------------------------------------------- furniture

        void ShowFurniture(VisualElement anchor)
        {
            var entries = new List<MenuEntry> { MenuEntry.Heading("FURNITURE") };

            bool any = false;
            if (Level != null)
            {
                foreach (var obstacle in Level.Obstacles)
                {
                    if (obstacle == null || obstacle.kind == ObstacleKind.Cat) continue;
                    any = true;

                    var captured = obstacle;
                    entries.Add(MenuEntry.Custom(_ => BuildFurnitureRow(captured, anchor)));
                }
            }

            if (!any) entries.Add(MenuEntry.Custom(_ => Ui.Text(null, "This floor plan has no furniture.", "le-empty-hint le-run-settings")));
            overlay.ShowMenuBelow(anchor, entries);
        }

        VisualElement BuildFurnitureRow(Obstacle obstacle, VisualElement anchor)
        {
            var row = Ui.Div(null, "le-run-furniture");
            Ui.Text(row, obstacle.name, "le-run-furniture__name");

            var mode = new Segmented(new[] { "Blocks", "Passes under" }, obstacle.blocksVacuum ? 0 : 1);
            mode.SelectionChanged += index =>
            {
                bool blocks = index == 0;
                if (blocks == obstacle.blocksVacuum) return;

                recorder?.LogParameter($"Furniture: {obstacle.name}", Describe(obstacle.blocksVacuum), Describe(blocks));
                obstacle.blocksVacuum = blocks;
                ApplyFurnitureChange();
            };
            row.Add(mode);

            Ui.IconButton(row, IconKind.Trash, "Remove for the rest of this run", () =>
            {
                recorder?.LogParameter($"Furniture: {obstacle.name}", Describe(obstacle.blocksVacuum), "removed");
                Level.Obstacles.Remove(obstacle);
                ApplyFurnitureChange();
                ShowFurniture(anchor);
            }, "le-btn--ghost le-btn--small");

            return row;
        }

        static string Describe(bool blocks) => blocks ? "blocks" : "passes under";

        /// <summary>Rebuilds furniture colliders and re-marks the floor it blocks, keeping cleaning progress.</summary>
        void ApplyFurnitureChange()
        {
            if (world.Renderer != null) world.Renderer.Rebuild();
            if (world.Cleaning != null) world.Cleaning.RefreshObstacles();
            Physics2D.SyncTransforms();
        }

        void HookEscape(IPanel panel)
        {
            panel.visualTree.RegisterCallback<KeyDownEvent>(evt =>
            {
                if (evt.keyCode != KeyCode.Escape) return;
                if (overlay != null && overlay.Dismiss()) return;
                Leave();
            }, TrickleDown.TrickleDown);
        }

        void OnDestroy()
        {
            recorder?.Detach();
            overlay = null;
            if (panelSettings != null) Destroy(panelSettings);
            if (world != null && world.Level != null) Destroy(world.Level);
        }
    }
}
