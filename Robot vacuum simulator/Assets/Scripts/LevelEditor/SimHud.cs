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
    /// the way back. It shows the active <see cref="SimulationSession"/>'s run; whoever owns the session
    /// (<see cref="SimTestDriver"/>) adds it and calls <see cref="Rebind"/> after each new run. Each run is
    /// also recorded by a <see cref="RunRecorder"/> for the Analysis screen, saved when the session ends it,
    /// or when the player restarts, finishes or leaves. Every setting, including the furniture, stays
    /// editable mid-run, and each change is logged against the simulated time it happened.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class SimHud : MonoBehaviour
    {
        /// <summary>Runs shorter than this are not worth keeping when the player leaves.</summary>
        const float MinRecordedSeconds = 2f;

        /// <summary>How long the vacuum must be off the floor plan before the run is stopped.</summary>
        const float EscapeGraceSeconds = 0.5f;

        /// <summary>
        /// Optional. When set, the Restart button calls this instead of only moving the robot back, so the
        /// owner of the run (SimTestDriver, through SimulationSession) can start a properly recorded new run.
        /// </summary>
        public Action RestartRequested;

        /// <summary>
        /// Optional. When set, the Editor button and Escape call this instead of going straight back to the
        /// editor, so the owner of the run can close it (and save it) first.
        /// </summary>
        public Action LeaveRequested;

        PanelSettings panelSettings;
        LevelData level;
        SimulationSession session;
        SimWorld world;
        OverlayLayer overlay;
        RunRecorder recorder;
        bool saved;
        string lastSavedId;
        bool leaving;
        bool escaped;
        bool paused;
        float speed = 1f;
        float offFloorSince = -1f;
        float savedTimeScale = 1f;
        float savedMaximumDeltaTime;
        float measuredSince;
        float measuredSimTime;
        float achievedSpeed;
        VisualElement pauseButton;
        Label seedLabel;

        VacuumSettings Settings => SimConfig.Vacuum;
        LevelData Level => level;
        float SimTime => world != null ? world.SimTime : 0f;

        /// <summary>Builds the bar over the active session's run. <paramref name="handedLevel"/> is destroyed along with the HUD.</summary>
        public void Show(LevelData handedLevel)
        {
            level = handedLevel;
            savedTimeScale = Time.timeScale;
            savedMaximumDeltaTime = Time.maximumDeltaTime;

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

            Rebind();
        }

        /// <summary>
        /// Picks up the active session's current run: a new robot and grid after a restart. Starts recording
        /// it, so call it as the run begins.
        /// </summary>
        public void Rebind()
        {
            var active = SimulationSession.Active;
            if (active != session)
            {
                if (session != null) session.Finished -= OnRunEnded;
                session = active;
                if (session != null) session.Finished += OnRunEnded;
            }

            recorder?.Detach();
            recorder = null;
            world = session != null ? new SimWorld(session) : null;
            if (world == null || world.Robot == null) return;

            world.ApplyVacuumSettings();
            recorder = new RunRecorder(world);
            saved = false;
            escaped = false;
            offFloorSince = -1f;
            measuredSince = 0f;
            if (seedLabel != null) seedLabel.text = world.Seed.ToString();
            ApplySpeed();
        }

        void BuildControls(VisualElement bar)
        {
            bool fromEditor = SimLauncher.Origin == SimOrigin.Editor;
            var back = Ui.Button(bar, fromEditor ? "Editor" : "Back", IconKind.Back, Leave, "le-btn--ghost");
            Ui.Tooltip(back, fromEditor ? "Back to the level editor   Esc" : "Back to the floor plan list   Esc");

            Ui.IconButton(bar, IconKind.Undo, "Restart on a dirty floor (same seed, unless the configuration draws a new one each run)",
                Restart, "le-btn--ghost");

            pauseButton = Ui.IconButton(bar, IconKind.Pause, "Pause or resume the simulation", TogglePause, "le-btn--ghost");

            // Speed is how many simulated seconds pass per real second. The physics step never changes, so the
            // results are the same at every speed; only the wait differs.
            var speedSlider = new ValueSlider(MinSpeed, MaxSpeed, speed,
                v => v < 1f ? $"{v:0.##}×" : $"{Mathf.Round(v):0}×");
            speedSlider.AddToClassList("le-run-speed");
            speedSlider.ValueChanged += value =>
            {
                speed = value < 1f ? value : Mathf.Round(value);
                ApplySpeed();
            };
            Ui.Tooltip(speedSlider, "Simulation speed, from ¼× to 100× real time");
            bar.Add(speedSlider);

            VisualElement settingsButton = null;
            settingsButton = Ui.Button(bar, "Settings", IconKind.Sliders, () => ShowVacuumSettings(settingsButton), "le-btn--ghost");
            Ui.Tooltip(settingsButton, "Vacuum speed, battery and units. Changes apply immediately and are logged.");

            VisualElement furnitureButton = null;
            furnitureButton = Ui.Button(bar, "Furniture", IconKind.Obstacle, () => ShowFurniture(furnitureButton), "le-btn--ghost");
            Ui.Tooltip(furnitureButton, "Change or remove furniture mid-run. Changes are logged.");

            var movement = new Segmented(MovementBrain.Labels, (int)SimLauncher.MovementPattern);
            movement.SelectionChanged += index =>
            {
                var next = (MovementPattern)index;
                recorder?.LogParameter("Algorithm", MovementAlgorithms.LabelOf(SimLauncher.MovementPattern), MovementAlgorithms.LabelOf(next));
                if (recorder != null && SimTime > 0f) recorder.MarkMixedPatterns();

                SimLauncher.MovementPattern = next;
                if (world?.Robot != null) world.Robot.Pattern = next;
            };
            Ui.Tooltip(movement, "Movement algorithm. Switching mid-run is logged, and the run counts as mixed.");
            bar.Add(movement);

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
                MeasureSpeed();
                var robot = world?.Robot;
                var battery = world?.Battery;
                runtime.text = VacuumSettings.FormatDuration(SimTime);
                rate.text = paused ? "Paused" : world != null && !world.Running ? "Ended" : $"{achievedSpeed:0.#}×";
                speedStat.text = Settings.FormatSpeed(robot != null ? robot.SpeedMetersPerSecond : 0f);
                if (battery != null)
                {
                    batteryStat.text = VacuumSettings.FormatBattery(battery.CurrentCharge, battery.Capacity);
                    Ui.SetClass(batteryStat, "le-run-stat__value--warn", VacuumSettings.IsBatteryLow(battery.CurrentCharge, battery.Capacity));
                }
                else batteryStat.text = "—";
                distance.text = Ui.FormatLength(robot != null ? robot.DistanceTravelled : 0f);
                surface.text = robot != null && robot.CurrentFloor != null ? robot.CurrentFloor.Label : "—";
                bool hasGrid = world?.Cleaning != null && world.Cleaning.Grid != null;
                blocked.text = hasGrid ? Ui.FormatArea(world.Cleaning.Grid.NonCleanableAreaSquareMeters) : "—";
                cleaned.text = hasGrid ? $"{world.Coverage:0.0}%" : "—";
            }).Every(250);
        }

        // ---------------------------------------------------------------- run lifecycle

        /// <summary>The session ended the run itself (battery, time cap, failed algorithm): save it while the grid is still there.</summary>
        void OnRunEnded(SimulationSession ended)
        {
            if (ended != session || saved) return;

            string reason = SimWorld.Describe(ended.Result != null ? ended.Result.endReason : RunEndReason.None);
            if (Save(reason) && overlay != null)
                overlay.Toast($"{reason} · run saved.", "View analysis", () => OpenAnalysis());
        }

        void Restart()
        {
            if (escaped) SetPaused(false);
            SaveIfWorthKeeping();

            if (RestartRequested != null)
            {
                RestartRequested();
                return;
            }

            if (session != null && session.Restart(out string error)) Rebind();
            else Debug.LogWarning("Simulator: the run couldn't be restarted.");
        }

        void Update()
        {
            if (recorder == null || leaving) return;
            CheckEscape();
        }

        /// <summary>Stops the run if the vacuum has driven out through an open wall into empty space.</summary>
        void CheckEscape()
        {
            if (escaped || paused || world == null || !world.Running) return;

            if (!world.OffFloor)
            {
                offFloorSince = -1f;
                return;
            }

            if (offFloorSince < 0f) offFloorSince = SimTime;
            if (SimTime - offFloorSince < EscapeGraceSeconds) return;

            escaped = true;
            SetPaused(true);
            overlay.ShowDialog("The vacuum escaped",
                "It drove out through a gap in an outside wall and will keep going forever. Add the missing wall in the level editor, or restart.",
                null,
                new DialogButton("Restart", ButtonStyle.Ghost, Restart),
                new DialogButton(SimLauncher.Origin == SimOrigin.Editor ? "Back to editor" : "Edit floor plan", ButtonStyle.Primary,
                    () =>
                    {
                        CloseSession("Escaped through an open wall");
                        leaving = SimLauncher.OpenLevelEditor(SimLauncher.SimulatedPath);
                    }));
        }

        bool Save(string reason)
        {
            if (recorder == null) return false;

            try
            {
                lastSavedId = recorder.Finish(reason).id;
                saved = true;
                return true;
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"Simulator: could not save the run: {exception.Message}");
                overlay?.Toast("Couldn't save this run's results.");
                return false;
            }
        }

        /// <summary>Saves a run the player is walking away from, unless it is too short to mean anything or already saved.</summary>
        void SaveIfWorthKeeping()
        {
            if (!saved && SimTime >= MinRecordedSeconds) Save(world != null && world.Running ? "Stopped" : "Finished");
        }

        /// <summary>Saves the run, then has the session end it and write its own result.</summary>
        void CloseSession(string reason)
        {
            if (!saved) Save(reason);
            recorder?.Detach();
            recorder = null;
            session?.Close();
        }

        void Finish()
        {
            if (leaving) return;

            CloseSession(world != null && world.Running ? "Finished" : "Battery empty");
            OpenAnalysis();
        }

        void OpenAnalysis()
        {
            if (!leaving) leaving = SimLauncher.OpenAnalysis(lastSavedId ?? recorder?.Report.id);
        }

        void Leave()
        {
            if (leaving) return;

            SaveIfWorthKeeping();
            if (LeaveRequested != null)
            {
                leaving = true;
                LeaveRequested();
                return;
            }

            CloseSession("Stopped");
            leaving = SimLauncher.ReturnFromSimulation();
        }

        void TogglePause() => SetPaused(!paused);

        void SetPaused(bool value)
        {
            paused = value;
            pauseButton.Q<IconElement>().Kind = paused ? IconKind.Play : IconKind.Pause;
            ApplySpeed();
        }

        // ---------------------------------------------------------------- speed

        const float MinSpeed = 0.25f;
        const float MaxSpeed = 100f;

        /// <summary>
        /// Plays the run at the chosen speed through the time scale. At high speeds Unity is also allowed more
        /// physics steps per frame, otherwise its per-frame cap would hold the run near 20×.
        /// </summary>
        void ApplySpeed()
        {
            Time.timeScale = paused ? 0f : speed;
            Time.maximumDeltaTime = Mathf.Max(savedMaximumDeltaTime, Mathf.Min(2f, speed / 30f));
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
                if (world?.Robot != null) Settings.ApplyTo(world.Robot, world.Battery);
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
            if (world?.Renderer != null) world.Renderer.Rebuild();
            if (world?.Cleaning != null) world.Cleaning.RefreshObstacles();
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
            if (session != null) session.Finished -= OnRunEnded;
            recorder?.Detach();
            overlay = null;
            Time.timeScale = savedTimeScale;
            if (savedMaximumDeltaTime > 0f) Time.maximumDeltaTime = savedMaximumDeltaTime;
            if (panelSettings != null) Destroy(panelSettings);
            if (level != null) Destroy(level);
        }
    }
}
