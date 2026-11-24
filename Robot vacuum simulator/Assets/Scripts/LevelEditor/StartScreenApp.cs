using System;
using System.Collections.Generic;
using System.Linq;
using RobotVacuum.Level;
using RobotVacuum.Network;
using RobotVacuum.Sim;
using RobotVacuum.Sim.Control;
using UnityEngine;
using UnityEngine.UIElements;

namespace RobotVacuum.LevelEditor
{
    /// <summary>
    /// The app's first screen and its hub: Configuration, the level editor, Start simulation and Analysis,
    /// each one step of the workflow. Drop this on an empty GameObject in the start scene.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class StartScreenApp : MonoBehaviour
    {
        [Tooltip("Floor coverings the floor plan thumbnails use. Left empty, the built-in palette is used.")]
        [SerializeField] FloorPalette palette;

        [Tooltip("Multiplies the UI size on top of the screen's DPI scaling.")]
        [SerializeField, Range(0.6f, 2f)] float uiScale = 1f;

        PanelSettings panelSettings;
        FloorPalette runtimePalette;
        VisualElement app;
        OverlayLayer overlay;
        StartPage page;

        FloorPalette Palette => palette != null ? palette : runtimePalette;

        /// <summary>The program's name as shown on screen.</summary>
        public const string AppName = "VACSim Vacuum Autonomous Controller";

        void Start()
        {
            if (palette == null) runtimePalette = FloorPalette.CreateDefault();

            var root = UiPanel.Create(transform, "Start Screen UI", uiScale, out panelSettings);
            app = Ui.Div(root, "le-app");
            overlay = new OverlayLayer();

            if (root.panel != null) HookKeyboard(root.panel);
            else root.RegisterCallback<AttachToPanelEvent>(evt => HookKeyboard(evt.destinationPanel));
            NetworkCampaign.Ended += OnNetworkBatchEnded;

            Show(SimLauncher.TakeStartPage());
        }

        void Show(StartPage next)
        {
            page = next;
            overlay.Dismiss();
            app.Clear();
            app.Add(BuildPage(next));
            app.Add(new NetworkBatchBar(overlay, OpenRun)); // a network batch carries on whichever page is open (3.9)
            app.Add(overlay);
        }

        void OpenRun(string runId)
        {
            SimLauncher.FocusRun(runId);
            Show(StartPage.Analysis);
        }

        void OnNetworkBatchEnded(NetworkCampaign campaign)
        {
            if (overlay == null) return;
            switch (campaign.Phase)
            {
                case NetworkCampaign.PhaseFinished:
                    overlay.Toast($"Network batch finished: {campaign.Imported?.imported ?? 0} run(s) are in Analysis.", "Open",
                        () => OpenRun(CampaignImport.RunIdFor(campaign.CampaignId, 0)));
                    if (page == StartPage.Analysis) Show(StartPage.Analysis);
                    break;
                case NetworkCampaign.PhaseCancelled:
                    overlay.Toast("Network batch cancelled. The runs that came back can be brought in with Analysis > Import results.");
                    break;
                default:
                    overlay.Toast("The network batch stopped. See Details on the bar at the bottom.");
                    break;
            }
        }

        VisualElement BuildPage(StartPage next)
        {
            switch (next)
            {
                case StartPage.Configuration:
                {
                    var view = new ConfigurationView(overlay, Palette);
                    view.BackRequested += () => Show(StartPage.Home);
                    view.SimulateRequested += () => Show(StartPage.Simulate);
                    return view;
                }

                case StartPage.Simulate:
                {
                    var view = new SimulateView(Palette);
                    view.BackRequested += () => Show(StartPage.Home);
                    view.ConfigureRequested += () => Show(StartPage.Configuration);
                    view.EditRequested += path => SimLauncher.OpenLevelEditor(path);
                    view.RunRequested += entry => Run(entry.level, entry.path);
                    view.BatchRequested += entry => ShowBatchSetup(entry.level, entry.path);
                    return view;
                }

                case StartPage.Analysis:
                {
                    var view = new AnalysisView(overlay, Palette, SimLauncher.TakeFocusedRun());
                    view.BackRequested += () => Show(StartPage.Home);
                    view.SimulateRequested += () => Show(StartPage.Simulate);
                    view.EditRequested += path => SimLauncher.OpenLevelEditor(path);
                    view.RerunRequested += Rerun;
                    return view;
                }

                case StartPage.Settings:
                {
                    var view = new SettingsView(overlay);
                    view.BackRequested += () => Show(StartPage.Home);
                    return view;
                }

                default:
                    return BuildHome();
            }
        }

        // ---------------------------------------------------------------- home

        VisualElement BuildHome()
        {
            var screen = Ui.Div(null, "le-screen");
            BuildHeader(screen);

            var body = Ui.Div(screen, "le-start");
            var hero = Ui.Div(body, "le-start__hero");

            var mark = new IconElement(IconKind.Logo);
            mark.AddToClassList("le-start__mark");
            hero.Add(mark);

            Ui.Text(hero, AppName, "le-start__title");
            Ui.Text(hero, "Configure the vacuum, draw a floor plan, run it and compare how well each algorithm cleans.", "le-start__text");

            int levels = LevelLibrary.LoadAll().Count;
            int runs = RunHistory.LoadAll().Count;

            var steps = Ui.Div(body, "le-start__steps");
            BuildStep(steps, 1, IconKind.Sliders, "Configuration",
                "Pick the movement algorithm and tune the vacuum's speed and battery.",
                SimConfig.Summary, () => Show(StartPage.Configuration));
            BuildStep(steps, 2, IconKind.Pen, "Level editor",
                "Draw rooms, cut doorways and add furniture for the vacuum to work around.",
                levels == 1 ? "1 floor plan" : $"{levels} floor plans", () => SimLauncher.OpenLevelEditor());
            BuildStep(steps, 3, IconKind.Play, "Start simulation",
                "Choose a floor plan and watch the vacuum clean it with your configuration.",
                levels == 0 ? "Draw a floor plan first" : "Choose a floor plan", () => Show(StartPage.Simulate));
            BuildStep(steps, 4, IconKind.Chart, "Analysis",
                "Review each run's coverage over time and compare algorithms side by side.",
                runs == 1 ? "1 recorded run" : $"{runs} recorded runs", () => Show(StartPage.Analysis));

            return screen;
        }

        void BuildHeader(VisualElement screen)
        {
            var header = Ui.Div(screen, "le-topbar");
            var brand = Ui.Div(header, "le-brand");
            var mark = new IconElement(IconKind.Logo);
            mark.AddToClassList("le-brand__mark");
            brand.Add(mark);

            var titles = Ui.Div(brand, "le-library__title-block");
            Ui.Text(titles, AppName, "le-library__title");

            Ui.Div(header, "le-topbar__spacer");

            // Settings: network workers (3.9), the cloud connection (5.5) and program-wide options. Opening it touches no network (1.1.1).
            Ui.Button(header, "Settings", IconKind.Sliders, () => Show(StartPage.Settings), "le-btn--ghost");

            // Quitting does nothing inside the Unity Editor, so only offer it in builds.
            if (!Application.isEditor)
                Ui.Button(header, "Quit", IconKind.Close, ConfirmQuit, "le-btn--ghost");
        }

        /// <summary>Quitting stops a running network batch, so it asks first (C.9).</summary>
        void ConfirmQuit()
        {
            var campaign = NetworkCampaign.Current;
            if (campaign == null || campaign.IsOver)
            {
                Application.Quit();
                return;
            }
            overlay.ShowDialog("Quit during a network batch?",
                "The batch stops on every computer. The runs that already came back stay in its folder and can be imported later.",
                null,
                new DialogButton("Keep running", ButtonStyle.Ghost, null),
                new DialogButton("Quit", ButtonStyle.Danger, Application.Quit));
        }

        static void BuildStep(VisualElement parent, int number, IconKind icon, string title, string text, string status,
            Action open)
        {
            var step = Ui.Div(parent, "le-start__step");

            var top = Ui.Div(step, "le-start__step-top");
            var tile = Ui.Div(top, "le-start__step-icon");
            tile.Add(new IconElement(icon));
            Ui.Text(top, $"Step {number}", "le-start__step-number");

            Ui.Text(step, title, "le-start__step-title");
            Ui.Text(step, text, "le-start__step-text");

            var footer = Ui.Div(step, "le-start__step-footer");
            Ui.Text(footer, status, "le-start__step-status");
            var arrow = new IconElement(IconKind.Back);
            arrow.AddToClassList("le-start__step-arrow");
            footer.Add(arrow);

            step.AddManipulator(new Clickable(open));
            Ui.RepaintIconsOnInteraction(step);
        }

        // ---------------------------------------------------------------- simulate

        void Run(LevelSnapshot level, string path)
        {
            if (level == null || level.ValidRoomCount == 0)
            {
                overlay.Toast("Draw a room in this floor plan before running it.", "Edit", () => SimLauncher.OpenLevelEditor(path));
                return;
            }

            RunChecks.ConfirmThen(overlay, RunChecks.Problems(level), () =>
            {
                // Only a serialized palette asset outlives this scene; the launcher supplies its own otherwise.
                if (!SimLauncher.Simulate(level, palette, path, SimOrigin.StartScreen))
                    overlay.Toast("The simulator scene isn't in the build's scene list.");
            }, "Fix in editor", () => SimLauncher.OpenLevelEditor(path));
        }

        /// <summary>Asks which algorithms, how many seeds and how to run a batch on <paramref name="level"/>.</summary>
        void ShowBatchSetup(LevelSnapshot level, string path)
        {
            var spec = new BatchSpec
            {
                trials = 10,
                baseSeed = SimConfig.Seed,
                maxMinutes = SimConfig.MaxRunMinutes,
                headless = true,
            };

            var body = Ui.Div(null, "le-batch-setup");
            Ui.Text(body, "Algorithms", "le-field-label");

            // One dropdown per algorithm in the batch; + adds another, × takes one out. Starts with the configured one.
            var entries = AlgorithmCatalog.Entries.ToList();
            var names = entries.Select(entry => entry.Name).ToList();
            var chosen = new List<string>();
            string configured = SimConfig.RunSettings.algorithmId;
            chosen.Add(entries.Exists(entry => entry.Id == configured) ? configured : entries[0].Id);

            var algorithmList = Ui.Div(body, "le-settings-panel le-batch-algorithms");
            void RebuildAlgorithmRows()
            {
                algorithmList.Clear();
                for (int i = 0; i < chosen.Count; i++)
                {
                    int row = i;
                    var line = Ui.Div(algorithmList, "le-batch-algorithm-row");

                    var dropdown = new DropdownField(names, Mathf.Max(0, entries.FindIndex(entry => entry.Id == chosen[row])));
                    dropdown.RegisterValueChangedCallback(_ =>
                    {
                        if (dropdown.index >= 0 && dropdown.index < entries.Count) chosen[row] = entries[dropdown.index].Id;
                    });
                    line.Add(dropdown);

                    if (chosen.Count > 1)
                        Ui.IconButton(line, IconKind.Close, "Remove this algorithm from the batch", () =>
                        {
                            chosen.RemoveAt(row);
                            RebuildAlgorithmRows();
                        }, "le-btn--ghost");

                    if (row == chosen.Count - 1)
                        Ui.IconButton(line, IconKind.Plus, "Add another algorithm to the batch", () =>
                        {
                            var unused = entries.Find(entry => !chosen.Contains(entry.Id));
                            if (unused == null)
                            {
                                overlay.Toast("Every algorithm is already in the batch.");
                                return;
                            }
                            chosen.Add(unused.Id);
                            RebuildAlgorithmRows();
                        }, "le-btn--ghost");
                }
            }
            RebuildAlgorithmRows();

            Ui.Text(body, "Seeds per algorithm", "le-field-label le-panel__gap");
            var trials = new ValueSlider(1f, 100f, spec.trials, v => $"{Mathf.Round(v):0}", true);
            trials.ValueChanged += value => spec.trials = Mathf.RoundToInt(value);
            body.Add(trials);

            var seedRow = Ui.Div(body, "le-config-seed");
            Ui.Text(seedRow, "First seed", "le-field-label");
            Ui.TextInput(seedRow, spec.baseSeed.ToString(), text =>
            {
                if (int.TryParse(text, out int value)) spec.baseSeed = value;
            }, "le-config-seed__input");

            Ui.Text(body, "Time limit per run", "le-field-label le-panel__gap");
            var limit = new ValueSlider(1f, 240f, spec.maxMinutes, v => $"{Mathf.Round(v):0} min of simulated time", true);
            limit.ValueChanged += value => spec.maxMinutes = Mathf.Round(value);
            body.Add(limit);

            // Run on (3.9): this PC alone runs the batch in the app, as before. With network workers chosen it becomes one
            // headless batch split across them (and this PC, if on), brought into Analysis when it's done.
            var workers = WorkerRegistry.Workers();
            bool onThisPc = true;
            var onWorkers = new HashSet<string>();
            Label networkNote = null;
            VisualElement headless = null;
            void UpdateRunOn()
            {
                bool network = onWorkers.Count > 0;
                if (headless != null) Ui.SetVisible(headless, !network);
                if (networkNote == null) return;
                networkNote.text = network
                    ? "Runs headless on the chosen computers, split by how many simulations each runs at once, and comes into Analysis " +
                      "when it's done. Each run's full per-cell model (external_model.json) stays behind; everything Analysis shows comes back."
                    : onThisPc ? string.Empty : "Choose at least one computer.";
                Ui.SetVisible(networkNote, networkNote.text.Length > 0);
            }

            if (workers.Count > 0)
            {
                Ui.Text(body, "Run on", "le-field-label le-panel__gap");
                var thisPc = new SwitchToggle("This PC", true);
                thisPc.ValueChanged += value =>
                {
                    onThisPc = value;
                    UpdateRunOn();
                };
                body.Add(thisPc);
                foreach (var worker in workers)
                {
                    string id = worker.workerId;
                    var toggle = new SwitchToggle($"{worker.name} ({worker.Address})", false);
                    toggle.ValueChanged += value =>
                    {
                        if (value) onWorkers.Add(id);
                        else onWorkers.Remove(id);
                        UpdateRunOn();
                    };
                    body.Add(toggle);
                }
                networkNote = Ui.Text(body, string.Empty, "le-empty-hint");
            }

            var headlessToggle = new SwitchToggle("Headless: don't draw, run as fast as possible", spec.headless);
            headlessToggle.ValueChanged += value => spec.headless = value;
            body.Add(headlessToggle);
            headless = headlessToggle;
            UpdateRunOn();

            Ui.Text(body, "Every algorithm runs on the same seeds, so they're compared like for like. Results open in Analysis." +
                          (workers.Count == 0 ? " To split batches across other computers, pair them in Settings." : ""), "le-empty-hint");

            overlay.ShowDialog("Batch experiment", level.name, body, null,
                new DialogButton("Cancel", ButtonStyle.Ghost, null),
                new DialogButton("Run batch", ButtonStyle.Primary, () =>
                {
                    string problem = BatchAlgorithmProblem(chosen);
                    if (problem != null)
                    {
                        overlay.Toast(problem);
                        return;
                    }

                    if (!onThisPc && onWorkers.Count == 0)
                    {
                        overlay.Toast("Choose at least one computer to run the batch on.");
                        return;
                    }

                    spec.algorithmIds.AddRange(chosen);
                    if (onWorkers.Count == 0)
                    {
                        RunBatch(level, path, spec);
                        return;
                    }

                    var targets = new List<CampaignTarget>();
                    if (onThisPc) targets.Add(new CampaignTarget());
                    targets.AddRange(workers.Where(w => onWorkers.Contains(w.workerId)).Select(w => new CampaignTarget { worker = w }));
                    RunNetworkBatch(level, path, spec, targets);
                }));
        }

        /// <summary>Sends a batch to network workers (and this PC) as one headless campaign (3.9), after the same checks as a run.</summary>
        void RunNetworkBatch(LevelSnapshot level, string path, BatchSpec spec, List<CampaignTarget> targets)
        {
            if (level == null || level.ValidRoomCount == 0)
            {
                overlay.Toast("Draw a room in this floor plan before running it.");
                return;
            }

            RunChecks.ConfirmThen(overlay, RunChecks.Problems(level), () =>
            {
                var job = NetworkJobBuilder.FromBatch(level, spec, Palette, out string problem);
                var campaign = job != null ? NetworkCampaign.TryStart(job, targets, out problem) : null;
                if (campaign == null)
                {
                    overlay.Toast(problem);
                    return;
                }
                overlay.Toast($"Network batch started: {campaign.TotalRuns} runs on {targets.Count} computer(s). It carries on while you use the app.");
            }, "Fix in editor", () => SimLauncher.OpenLevelEditor(path));
        }

        /// <summary>Why the chosen algorithms can't run as a batch, or null when they can.</summary>
        static string BatchAlgorithmProblem(List<string> algorithmIds)
        {
            if (algorithmIds.Count == 0) return "Add at least one algorithm.";

            var seen = new HashSet<string>();
            foreach (string id in algorithmIds)
            {
                if (!seen.Add(id)) return $"{AlgorithmCatalog.NameOf(id)} is in the batch twice. Pick a different algorithm or remove one.";

                // Every trial uses the configured SLAM setting, so an algorithm that can't run with it would fail every seed.
                string refusal = AlgorithmCatalog.Refusal(id, SimConfig.RunSettings.slam);
                if (!string.IsNullOrEmpty(refusal)) return refusal;
            }

            return null;
        }

        /// <summary>Runs a batch experiment on <paramref name="level"/>, after the same checks as a single run.</summary>
        void RunBatch(LevelSnapshot level, string path, BatchSpec spec)
        {
            if (level == null || level.ValidRoomCount == 0)
            {
                overlay.Toast("Draw a room in this floor plan before running it.");
                return;
            }

            RunChecks.ConfirmThen(overlay, RunChecks.Problems(level), () =>
            {
                if (!SimLauncher.RunBatch(level, palette, path, spec))
                    overlay.Toast("The simulator scene isn't in the build's scene list.");
            }, "Fix in editor", () => SimLauncher.OpenLevelEditor(path));
        }

        void Rerun(RunReport report)
        {
            LevelSnapshot level = null;
            try { level = LevelLibrary.Read(report.levelPath); }
            catch (Exception exception) { Debug.LogWarning($"Start screen: could not read '{report.levelPath}': {exception.Message}"); }

            if (level == null) overlay.Toast("That floor plan no longer exists.");
            else Run(level, report.levelPath);
        }

        void HookKeyboard(IPanel panel)
        {
            panel.visualTree.RegisterCallback<KeyDownEvent>(evt =>
            {
                if (evt.keyCode != KeyCode.Escape || Ui.IsTyping(panel)) return;
                if (overlay.Dismiss() || page == StartPage.Home) return;
                Show(StartPage.Home);
            }, TrickleDown.TrickleDown);
        }

        void OnDestroy()
        {
            NetworkCampaign.Ended -= OnNetworkBatchEnded;
            if (runtimePalette != null)
            {
                foreach (var floor in runtimePalette.Entries) Destroy(floor);
                Destroy(runtimePalette);
            }

            if (panelSettings != null) Destroy(panelSettings);
        }
    }
}
