using System;
using RobotVacuum.Level;
using RobotVacuum.Sim;
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

        void Start()
        {
            if (palette == null) runtimePalette = FloorPalette.CreateDefault();

            var root = UiPanel.Create(transform, "Start Screen UI", uiScale, out panelSettings);
            app = Ui.Div(root, "le-app");
            overlay = new OverlayLayer();

            if (root.panel != null) HookKeyboard(root.panel);
            else root.RegisterCallback<AttachToPanelEvent>(evt => HookKeyboard(evt.destinationPanel));

            Show(SimLauncher.TakeStartPage());
        }

        void Show(StartPage next)
        {
            page = next;
            overlay.Dismiss();
            app.Clear();
            app.Add(BuildPage(next));
            app.Add(overlay);
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

            Ui.Text(hero, "Robot Vacuum Simulator", "le-start__title");
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

        static void BuildHeader(VisualElement screen)
        {
            var header = Ui.Div(screen, "le-topbar");
            var brand = Ui.Div(header, "le-brand");
            var mark = new IconElement(IconKind.Logo);
            mark.AddToClassList("le-brand__mark");
            brand.Add(mark);

            var titles = Ui.Div(brand, "le-library__title-block");
            Ui.Text(titles, "Robot Vacuum Simulator", "le-library__title");

            Ui.Div(header, "le-topbar__spacer");

            // Quitting does nothing inside the Unity Editor, so only offer it in builds.
            if (!Application.isEditor)
                Ui.Button(header, "Quit", IconKind.Close, Application.Quit, "le-btn--ghost");
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
            var chips = Ui.Div(body, "le-chip-row");
            for (int i = 0; i < MovementAlgorithms.Count; i++)
            {
                var pattern = (MovementPattern)i;
                if (pattern == MovementPattern.Picture) continue; // draws a picture rather than cleaning

                spec.patterns.Add(pattern);
                VisualElement chip = null;
                chip = Ui.Button(chips, MovementAlgorithms.LabelOf(pattern), null, () =>
                {
                    bool on = !spec.patterns.Contains(pattern);
                    if (on) spec.patterns.Add(pattern);
                    else spec.patterns.Remove(pattern);
                    Ui.SetClass(chip, "le-chip--on", on);
                }, "le-chip le-chip--on");
            }

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

            var headless = new SwitchToggle("Headless: don't draw, run as fast as possible", spec.headless);
            headless.ValueChanged += value => spec.headless = value;
            body.Add(headless);

            Ui.Text(body, "Every algorithm runs on the same seeds, so they're compared like for like. Results open in Analysis.", "le-empty-hint");

            overlay.ShowDialog("Batch experiment", level.name, body, null,
                new DialogButton("Cancel", ButtonStyle.Ghost, null),
                new DialogButton("Run batch", ButtonStyle.Primary, () =>
                {
                    if (spec.patterns.Count == 0) overlay.Toast("Pick at least one algorithm.");
                    else RunBatch(level, path, spec);
                }));
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
            if (runtimePalette != null)
            {
                foreach (var floor in runtimePalette.Entries) Destroy(floor);
                Destroy(runtimePalette);
            }

            if (panelSettings != null) Destroy(panelSettings);
        }
    }
}
