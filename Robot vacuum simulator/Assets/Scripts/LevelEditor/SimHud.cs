using System;
using System.Collections.Generic;
using RobotVacuum.Level;
using RobotVacuum.Sim;
using RobotVacuum.Sim.Control;
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
        const float EscapeGraceSeconds = SimWorld.EscapeGraceSeconds;

        /// <summary>
        /// Optional. When set, restarting calls this instead of restarting the session itself, so the owner of the run
        /// (SimTestDriver, through SimulationSession) can start a properly recorded new run. The argument is true for
        /// Run Again (exactly the same seed) and false for the bar's Restart button (the configuration's seed choice).
        /// </summary>
        public Action<bool> RestartRequested;

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
        VisualElement resumeButton; // shown while the run is held at a stop (2.11): the minimized stop card
        bool stopPaused;            // the simulation is paused because the run is held at a stop
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
                if (session != null)
                {
                    session.Finished -= OnRunEnded;
                    session.Stopped -= OnRunStopped;
                }
                session = active;
                if (session != null)
                {
                    session.Finished += OnRunEnded;
                    session.Stopped += OnRunStopped;
                }
            }

            // A run that meets a stop condition is held here, for the user to resume, change, run again or finish
            // (2.11); only a Terminal failure or the safety cap ends it outright.
            if (session != null) session.HoldOnStop = true;
            if (stopPaused)
            {
                stopPaused = false;
                SetPaused(false);
            }
            if (resumeButton != null) Ui.SetVisible(resumeButton, false);

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
                () => Restart(false), "le-btn--ghost");

            pauseButton = Ui.IconButton(bar, IconKind.Pause, "Pause or resume the simulation", TogglePause, "le-btn--ghost");

            // The stop card, minimized: carry on with a run that is held at a stop.
            resumeButton = Ui.Button(bar, "Resume", IconKind.Play, RequestResume, "le-btn--primary");
            Ui.Tooltip(resumeButton, "The run is stopped. Resume it from here, or open the stop details again.");
            Ui.SetVisible(resumeButton, false);

            // Speed is how many simulated seconds pass per real second. The physics step never changes, so the
            // results are the same at every speed; only the wait differs.
            // Dragging snaps to whole speeds above 1×; a typed value (e.g. 2.5) is used exactly.
            ValueSlider speedSlider = null;
            speedSlider = new ValueSlider(MinSpeed, MaxSpeed, speed, v => $"{v:0.##}×", true);
            speedSlider.AddToClassList("le-run-speed");
            speedSlider.ValueChanged += value =>
            {
                speed = speedSlider.IsDragging && value >= 1f ? Mathf.Round(value) : value;
                speedSlider.SetValueWithoutNotify(speed);
                ApplySpeed();
            };
            Ui.Tooltip(speedSlider, "Simulation speed, from ¼× to 100× real time. Drag, or type a value and press Enter.");
            bar.Add(speedSlider);

            VisualElement settingsButton = null;
            settingsButton = Ui.Button(bar, "Settings", IconKind.Sliders, () => ShowVacuumSettings(settingsButton), "le-btn--ghost");
            Ui.Tooltip(settingsButton, "Vacuum speed, battery and units. Changes apply immediately and are logged.");

            VisualElement furnitureButton = null;
            furnitureButton = Ui.Button(bar, "Furniture", IconKind.Obstacle, () => ShowFurniture(furnitureButton), "le-btn--ghost");
            Ui.Tooltip(furnitureButton, "Change or remove furniture mid-run. Changes are logged.");

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
                rate.text = Held ? "Stopped" : paused ? "Paused" : world != null && !world.Running ? "Ended" : $"{achievedSpeed:0.#}×";
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

        /// <summary>True while the run is held at a stop condition (<see cref="SimulationSession.Phase.Stopped"/>).</summary>
        bool Held => session != null && session.State == SimulationSession.Phase.Stopped;

        /// <summary>
        /// The session ended the run itself. With runs held at their stops, that only happens for a Terminal failure
        /// (the simulation's own code failed, the algorithm left too many threads) or the 24 h safety cap: save it while
        /// the grid is still there and go straight to its analysis. If it can't even be saved, offer the way back.
        /// </summary>
        void OnRunEnded(SimulationSession ended)
        {
            if (ended != session || saved) return;

            var result = ended.Result;
            string reason = SimWorld.Describe(result != null ? result.endReason : RunEndReason.None);
            string failure = result != null && !result.succeeded ? result.errorMessage : null;
            if (Save(reason, failure)) OpenAnalysis();
            else ShowSaveFailed(reason, failure);
        }

        /// <summary>The run's results couldn't be written, so there is no analysis to show: offer the way back to the setup.</summary>
        void ShowSaveFailed(string reason, string failure)
        {
            SetPaused(true);
            overlay?.ShowDialog("The run ended, and couldn't be saved",
                (failure ?? reason) + "\n\nIts results could not be written, so there is no analysis to show. See the Console for why.",
                null,
                new DialogButton("Back", ButtonStyle.Primary, () =>
                {
                    recorder?.Detach();
                    recorder = null;
                    session?.Close();
                    leaving = SimLauncher.ReturnFromSimulation();
                }));
        }

        /// <summary>The run met a stop condition and is held (2.11): pause the simulation and show what happened.</summary>
        void OnRunStopped(SimulationSession stopped)
        {
            if (stopped != session) return;

            stopPaused = true;
            SetPaused(true);
            ShowStopCard();
        }

        static string StopTitle(RunEndReason reason) => reason switch
        {
            RunEndReason.Completed => "Cleaning finished",
            RunEndReason.ReturnedLowBattery => "Returned to the dock: battery low",
            RunEndReason.BatteryDepleted => "Battery empty",
            RunEndReason.AlgorithmFault => "The algorithm failed",
            RunEndReason.Stuck => "No progress",
            RunEndReason.Degenerate => "The dock can't be reached",
            RunEndReason.TimeLimit => "Time limit reached",
            _ => SimWorld.Describe(reason),
        };

        /// <summary>The stop card: what stopped the run, and what can be done about it.</summary>
        void ShowStopCard()
        {
            if (!Held || overlay == null) return;
            Ui.SetVisible(resumeButton, true);

            var reason = session.StopReason;
            string message = session.StopMessage;
            var brain = world?.Brain;
            if (brain != null && brain.Failed && reason != RunEndReason.AlgorithmFault)
                message = (string.IsNullOrEmpty(message) ? "" : message + "\n") + brain.Failure; // e.g. a reimport that broke it

            var body = Ui.Div(null);
            Ui.Text(body, $"Cleaned {(world != null ? world.Coverage : 0f):0.0}%  ·  {VacuumSettings.FormatDuration(SimTime)} simulated  ·  seed {(world != null ? world.Seed : 0)}",
                "le-dialog__message");

            var fault = session.LastFault;
            bool algorithmFailed = reason == RunEndReason.AlgorithmFault || (brain != null && brain.Failed);
            if (algorithmFailed && fault != null && !string.IsNullOrEmpty(fault.stackTrace))
            {
                var details = new Foldout { text = "Details", value = false };
                details.Add(new Label($"{fault.kind} in {fault.call}" +
                                      (string.IsNullOrEmpty(fault.exceptionType) ? "" : $": {fault.exceptionType}") +
                                      $"\n{fault.stackTrace}"));
                body.Add(details);
            }
            Ui.Text(body, "Change Parameters closes this so you can use the settings and furniture menus; Resume carries on from here.",
                "le-empty-hint");

            var buttons = new List<DialogButton>
            {
                new DialogButton("Run Again", ButtonStyle.Ghost, () => Restart(true)),
                new DialogButton("Change Parameters", ButtonStyle.Ghost, null), // just closes the card; Resume stays on the bar
            };
            if (algorithmFailed) buttons.Add(new DialogButton("Reimport Algorithms", ButtonStyle.Ghost, Reimport));
            buttons.Add(new DialogButton("Resume", ButtonStyle.Ghost, RequestResume));
            buttons.Add(new DialogButton("Finish", ButtonStyle.Primary, Finish));

            overlay.ShowDialog(StopTitle(reason), message, body, null, buttons.ToArray());
            overlay.DialogCard?.AddToClassList("le-dialog--wide"); // up to five buttons: room for each label
        }

        /// <summary>Resume, from the card, the bar's Resume or the pause button: after a stop, only once the user confirms.</summary>
        void RequestResume()
        {
            if (!Held)
            {
                if (paused) SetPaused(false);
                return;
            }

            string title = StopTitle(session.StopReason);
            overlay.ShowDialog("Resume the run?",
                $"It stopped: {title}. Resume anyway? The robot carries on from where it is and the no-progress timer " +
                "starts again. If the cause is still there (an empty battery, an algorithm with nothing left to do) it " +
                "will simply stop again. Resuming after the time limit lifts it for the rest of this run.",
                null,
                new DialogButton("Cancel", ButtonStyle.Ghost, null),
                new DialogButton("Show details", ButtonStyle.Ghost, ShowStopCard),
                new DialogButton("Resume", ButtonStyle.Primary, DoResume));
        }

        void DoResume()
        {
            if (!Held) return;

            var reason = session.StopReason;
            string message = session.StopMessage;
            if (!session.Resume()) return;

            recorder?.LogParameter($"Run stopped: {SimWorld.Describe(reason)}",
                string.IsNullOrEmpty(message) ? "stopped" : message, "resumed");
            Ui.SetVisible(resumeButton, false);
            stopPaused = false;
            SetPaused(false);
        }

        /// <summary>How the run will be labelled in its history if it ends now.</summary>
        string EndLabel() => Held ? SimWorld.Describe(session.StopReason)
            : world != null && world.Running ? "Stopped" : "Finished";

        /// <summary>The failure to show in Analysis for a run that ends now (a stop that is an algorithm failure).</summary>
        string EndFailure() => Held && session.StopReason == RunEndReason.AlgorithmFault ? session.StopMessage : null;

        /// <param name="sameSeed">True: Run Again, exactly the same seed. False: the bar's Restart (the configuration's seed choice).</param>
        void Restart(bool sameSeed)
        {
            if (escaped) SetPaused(false);
            SaveIfWorthKeeping();

            if (RestartRequested != null)
            {
                RestartRequested(sameSeed);
                return;
            }

            if (session != null && session.Restart(sameSeed, out string error)) Rebind();
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
                new DialogButton("Restart", ButtonStyle.Ghost, () => Restart(false)),
                new DialogButton(SimLauncher.Origin == SimOrigin.Editor ? "Back to editor" : "Edit floor plan", ButtonStyle.Primary,
                    () =>
                    {
                        CloseSession("Escaped through an open wall");
                        leaving = SimLauncher.OpenLevelEditor(SimLauncher.SimulatedPath);
                    }));
        }

        bool Save(string reason, string failure = null)
        {
            if (recorder == null) return false;

            try
            {
                lastSavedId = recorder.Finish(reason, failure: failure ?? EndFailure()).id;
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
            if (!saved && SimTime >= MinRecordedSeconds) Save(EndLabel());
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

            string label = Held ? EndLabel() : "Finished";
            if (!saved && !Save(label))
            {
                ShowSaveFailed(label, EndFailure());
                return;
            }
            session?.Finish(); // ends it as finished (a held stop keeps its reason), before Close tears it down
            CloseSession(label);
            OpenAnalysis();
        }

        void OpenAnalysis()
        {
            if (!leaving) leaving = SimLauncher.OpenAnalysis(lastSavedId ?? recorder?.Report.id);
        }

        /// <summary>True when this run has a recording to open on the Analysis page.</summary>
        public bool CanViewAnalysis => lastSavedId != null || recorder != null;

        /// <summary>Opens the Analysis page on this run (the run-end panel's "View analysis").</summary>
        public void ViewAnalysis()
        {
            if (!saved) SaveIfWorthKeeping();
            OpenAnalysis();
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

        void TogglePause()
        {
            if (Held) RequestResume(); // a stopped run is resumed deliberately, never by just unpausing
            else SetPaused(!paused);
        }

        void SetPaused(bool value)
        {
            paused = value;
            pauseButton.Q<IconElement>().Kind = paused ? IconKind.Play : IconKind.Pause;
            ApplySpeed();
        }

        // ---------------------------------------------------------------- speed

        const float MinSpeed = 0.25f;
        const float MaxSpeed = SimulationSpeed.MaxPlaybackSpeed;

        /// <summary>
        /// Plays the run at the chosen speed through the time scale. <see cref="SimulationSpeed"/> also lets
        /// Unity take more physics steps per frame, otherwise its per-frame cap would hold the run near 20×.
        /// </summary>
        void ApplySpeed()
        {
            if (paused) Time.timeScale = 0f;
            else SimulationSpeed.Apply(speed, savedMaximumDeltaTime);
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

            if (session != null && session.Settings != null)
            {
                panel.Add(new RunSettingsPanel(session.Settings, session, refusal => overlay.Toast(refusal, durationMs: 7600),
                    algorithmSwitched: (from, to) => recorder?.LogParameter("Algorithm",
                        $"{AlgorithmCatalog.NameOf(from)} [{AlgorithmCatalog.Fingerprint(from)}]",
                        $"{AlgorithmCatalog.NameOf(to)} [{AlgorithmCatalog.Fingerprint(to)}]"),
                    reimport: Reimport)); // the button sits directly below the Algorithm list
                Ui.Div(panel, "le-divider");
            }

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
            var capacity = AddVacuumSetting(panel, "Battery capacity", 10f, Battery.MaxCapacity, Settings.batteryCapacity, VacuumSettings.FormatCapacity,
                value => Settings.batteryCapacity = value);

            // The field shows capacity, not the charge left. Selecting it at all, even without changing the figure,
            // recharges the vacuum, so a user who opens it to "refill to the same capacity" gets what they meant.
            capacity.RegisterCallback<FocusInEvent>(_ => RefillBattery(quiet: true), TrickleDown.TrickleDown);
            capacity.RegisterCallback<PointerDownEvent>(_ => RefillBattery(quiet: true), TrickleDown.TrickleDown);

            if (session != null && session.Settings != null)
            {
                var refill = Ui.Button(panel, "Refill battery", IconKind.Plus, () => RefillBattery(), "le-btn--ghost le-run-settings__wide");
                Ui.Tooltip(refill, "Charge the battery back to full. Logged with the simulated time.");
            }
            Ui.Text(panel, "Selecting or changing battery capacity recharges the vacuum. Every change is logged with the simulated time.", "le-empty-hint");

            return panel;
        }

        /// <summary>
        /// Reimport Algorithms (2.16): pauses the simulation if it is going, rescans the Algorithms folder, rebuilds this
        /// run's algorithm from it (always: its memory starts over) and carries on. If the algorithm no longer loads (its
        /// file removed or renamed, it doesn't compile, it can't run with SLAM, it throws or hangs as it starts) the run
        /// is held at a stop instead, until it is fixed and reimported, another algorithm is picked, or the run finished.
        /// The algorithm's fingerprint before and after is logged, so a mid-run change of code shows in the results.
        /// </summary>
        void Reimport()
        {
            if (session == null || world == null || world.Brain == null) return;

            bool wasGoing = session.State == SimulationSession.Phase.Running && !paused;
            if (wasGoing) SetPaused(true); // no physics step (so no algorithm call) runs while the list changes

            string id = world.Robot != null ? world.Robot.AlgorithmId : session.Settings.algorithmId;
            string before = AlgorithmCatalog.Fingerprint(id);
            var report = AlgorithmCatalog.RescanForPausedRun();
            Debug.Log("Reimport Algorithms:\n" + report.Text());
            string after = AlgorithmCatalog.Fingerprint(id);
            recorder?.LogParameter($"Algorithm reimported: {AlgorithmCatalog.NameOf(id)}", before, after + " (rebuilt)");

            world.Brain.Reload();

            if (world.Brain.Failed)
            {
                // It doesn't load any more: hold the run rather than carry on without an algorithm.
                if (session.State == SimulationSession.Phase.Running) session.CheckNow(); // stops it (the card comes with the stop)
                else ShowStopCard();
                return;
            }

            if (wasGoing) SetPaused(false);
            overlay.Toast($"Algorithms reimported: {report.AlgorithmCount} loaded from the folder" +
                          (report.FailedUnits > 0 ? $", {report.FailedUnits} failed (see the Console)." : ".") +
                          $" {AlgorithmCatalog.NameOf(id)} was rebuilt.", durationMs: 6000);
        }

        /// <param name="quiet">From selecting the capacity field: refill without a message, and only if it isn't already full.</param>
        void RefillBattery(bool quiet = false)
        {
            var battery = world?.Battery;
            if (battery == null) return;
            if (quiet && battery.CurrentCharge >= battery.Capacity) return;

            string before = VacuumSettings.FormatBattery(battery.CurrentCharge, battery.Capacity);
            battery.ResetCharge();
            recorder?.LogParameter("Battery", before, "refilled to " + VacuumSettings.FormatBattery(battery.CurrentCharge, battery.Capacity));
            if (!quiet) overlay.Toast("Battery refilled." + (Held ? " Resume to carry on." : ""));
        }

        ValueSlider AddVacuumSetting(VisualElement parent, string label, float min, float max, float value,
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
            return slider;
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
            if (session != null)
            {
                session.Finished -= OnRunEnded;
                session.Stopped -= OnRunStopped;
            }
            recorder?.Detach();
            overlay = null;
            Time.timeScale = savedTimeScale;
            if (savedMaximumDeltaTime > 0f) Time.maximumDeltaTime = savedMaximumDeltaTime;
            if (panelSettings != null) Destroy(panelSettings);
            if (level != null) Destroy(level);
        }
    }
}
