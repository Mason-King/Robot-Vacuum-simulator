using RobotVacuum.Level;
using RobotVacuum.Sim;
using UnityEngine;
using UnityEngine.UIElements;

namespace RobotVacuum.LevelEditor
{
    /// <summary>
    /// The app's first screen: open the level editor, or run a saved floor plan. A run is either watched,
    /// which loads the simulation scene, or headless, which runs here with no visuals at many times normal
    /// speed and then shows the results. Every run carries a seed, so a fast run can be watched back exactly
    /// as it happened. Drop this on an empty GameObject in the start scene.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class StartScreenApp : MonoBehaviour
    {
        static readonly float[] RunMinutes = { 1f, 5f, 15f };
        static readonly string[] RunMinuteLabels = { "1 min", "5 min", "15 min" };

        [Tooltip("Floor coverings rooms can use. Left empty, the built-in six-covering palette is used.")]
        [SerializeField] FloorPalette palette;

        [Tooltip("Multiplies the UI size on top of the screen's DPI scaling.")]
        [SerializeField, Range(0.6f, 2f)] float uiScale = 1f;

        [Tooltip("How many simulated seconds a headless run tries to cover per real second.")]
        [SerializeField, Range(2f, 100f)] float fastRunSpeed = HeadlessSimulation.DefaultSpeed;

        PanelSettings panelSettings;
        FloorPalette runtimePalette;
        VisualElement app;
        HeadlessSimulation run;
        LevelLibrary.Entry running;
        Texture2D resultImage;
        int minutesIndex = 1;

        FloorPalette Palette => palette != null ? palette : runtimePalette;

        void Start()
        {
            if (palette == null) runtimePalette = FloorPalette.CreateDefault();

            var root = UiPanel.Create(transform, "Start Screen UI", uiScale, out panelSettings);
            app = Ui.Div(root, "le-app");

            ShowHome();
        }

        void OnDestroy()
        {
            run?.Stop();
            ClearResultImage();

            if (runtimePalette != null)
            {
                foreach (var floor in runtimePalette.Entries) Destroy(floor);
                Destroy(runtimePalette);
            }

            if (panelSettings != null) Destroy(panelSettings);
        }

        void ShowScreen(VisualElement screen)
        {
            app.Clear();
            app.Add(screen);
        }

        // ---------------------------------------------------------------- home

        void ShowHome()
        {
            var screen = Ui.Div(null, "le-screen le-start");

            var mark = new IconElement(IconKind.Logo);
            mark.AddToClassList("le-start__mark");
            screen.Add(mark);

            Ui.Text(screen, "Robot Vacuum Simulator", "le-start__title");
            Ui.Text(screen, "Draw a floor plan and watch the vacuum clean it, or run one headless and just read the results.",
                "le-start__text");

            var actions = Ui.Div(screen, "le-start__actions");
            Ui.Button(actions, "Level editor", IconKind.Pen, () => SimLauncher.OpenLevelEditor(), "le-btn--primary le-start__button");
            Ui.Button(actions, "Run a floor plan", IconKind.Play, ShowPicker, "le-btn--ghost le-start__button");

            // Quitting does nothing inside the Unity Editor, so only offer it in builds.
            if (!Application.isEditor)
                Ui.Button(actions, "Quit", IconKind.Close, Application.Quit, "le-btn--ghost le-start__button");

            ShowScreen(screen);
        }

        // ---------------------------------------------------------------- pick a floor plan

        void ShowPicker()
        {
            var screen = Ui.Div(null, "le-screen");

            var header = Ui.Div(screen, "le-topbar");
            Ui.IconButton(header, IconKind.Back, "Back", ShowHome, "le-btn--ghost");
            Ui.Text(header, "Run a floor plan", "le-library__title");
            Ui.Div(header, "le-topbar__spacer");

            var minutes = new Segmented(RunMinuteLabels, minutesIndex);
            minutes.SelectionChanged += index => minutesIndex = index;
            Ui.Tooltip(minutes, "How much cleaning time a fast run simulates");
            header.Add(minutes);

            var list = new ScrollView(ScrollViewMode.Vertical);
            list.AddToClassList("le-run-list");
            screen.Add(list);

            var entries = LevelLibrary.LoadAll();
            if (entries.Count == 0) BuildEmptyState(list);
            else foreach (var entry in entries) list.Add(BuildLevelRow(entry));

            ShowScreen(screen);
        }

        void BuildEmptyState(VisualElement parent)
        {
            var empty = Ui.Div(parent, "le-empty");

            var icon = new IconElement(IconKind.Logo);
            icon.AddToClassList("le-empty__icon");
            empty.Add(icon);

            Ui.Text(empty, "No floor plans yet", "le-empty__title");
            Ui.Text(empty, "Draw one in the level editor, then come back and run it.", "le-empty__text");
            Ui.Button(Ui.Div(empty, "le-empty__actions"), "Level editor", IconKind.Pen,
                () => SimLauncher.OpenLevelEditor(), "le-btn--primary");
        }

        VisualElement BuildLevelRow(LevelLibrary.Entry entry)
        {
            var row = Ui.Div(null, "le-list-row le-run-row");

            var text = Ui.Div(row, "le-run-row__text");
            Ui.Text(text, entry.level.name, "le-list-row__label");

            int rooms = entry.level.ValidRoomCount;
            string roomText = rooms == 1 ? "1 room" : $"{rooms} rooms";
            Ui.Text(text, $"{roomText} · {Ui.FormatArea(entry.level.TotalFloorArea())}", "le-list-row__meta");

            var actions = Ui.Div(row, "le-run-row__actions");
            var watch = Ui.Button(actions, "Watch", IconKind.Play, () => Watch(entry, Rng.NewSeed()), "le-btn--ghost");
            Ui.Tooltip(watch, "Open it in the simulator and watch it clean");

            var fast = Ui.Button(actions, "Fast run", IconKind.Fit, () => FastRun(entry, Rng.NewSeed()), "le-btn--primary");
            Ui.Tooltip(fast, "Run it headless at speed and show the results");

            return row;
        }

        /// <summary>Watches a run in the simulator scene. The same seed gives the same run a fast run had.</summary>
        void Watch(LevelLibrary.Entry entry, int seed)
        {
            SimLauncher.Seed = seed;

            if (!SimLauncher.Simulate(entry.level, palette, entry.path))
                Debug.LogError("The simulator scene isn't in the build's scene list.");
        }

        // ---------------------------------------------------------------- headless run

        /// <summary>The heatmap from the last run belongs to this screen, so it goes when the run does.</summary>
        void ClearResultImage()
        {
            if (resultImage == null) return;

            Destroy(resultImage);
            resultImage = null;
        }

        void FastRun(LevelLibrary.Entry entry, int seed)
        {
            running = entry;
            ClearResultImage();

            // The run owns this copy of the level and destroys it when it ends.
            var level = SimLauncher.BuildLevel(entry.level, Palette);
            run = HeadlessSimulation.Start(level, entry.level.name, RunMinutes[minutesIndex] * 60f,
                SimLauncher.MovementPattern, SimLauncher.Picture, seed, fastRunSpeed);
            run.Finished += ShowResults;

            ShowRunning(entry, seed);
        }

        void ShowRunning(LevelLibrary.Entry entry, int seed)
        {
            var screen = Ui.Div(null, "le-screen le-start");

            Ui.Text(screen, $"Running {entry.level.name}", "le-start__title");
            Ui.Text(screen, "No visuals: the vacuum is cleaning as fast as this machine will simulate it.", "le-start__text");

            var bar = Ui.Div(screen, "le-progress");
            var fill = Ui.Div(bar, "le-progress__fill");

            var stats = Ui.Div(screen, "le-stats le-run-stats");
            var cleaned = Ui.Stat(stats, "Cleaned", "0%");
            var simulated = Ui.Stat(stats, "Simulated", "00:00");
            var real = Ui.Stat(stats, "Real time", "00:00");
            var speed = Ui.Stat(stats, "Speed", "—");

            Ui.Text(screen, $"Seed {seed}", "le-run-seed");
            Ui.Button(Ui.Div(screen, "le-start__actions"), "Stop", IconKind.Stop, () => run?.Stop(), "le-btn--ghost le-start__button");

            screen.schedule.Execute(() =>
            {
                if (run == null) return;

                fill.style.width = Length.Percent(run.Progress * 100f);
                cleaned.text = $"{run.CoveragePercent:0.0}%";
                simulated.text = SimulationResults.FormatDuration(run.SimulatedSeconds);
                real.text = SimulationResults.FormatDuration(run.RealSeconds);
                speed.text = run.RealSeconds > 0.5f ? $"{run.SimulatedSeconds / run.RealSeconds:0}×" : "—";
            }).Every(100);

            ShowScreen(screen);
        }

        void ShowResults(SimulationResults results)
        {
            run = null;
            resultImage = results.coverage; // handed over with the results; destroyed with the next run

            var screen = Ui.Div(null, "le-screen le-start");

            Ui.Text(screen, $"{results.levelName} · {results.coveragePercent:0.0}% cleaned", "le-start__title");

            if (resultImage != null)
            {
                var heatmap = new Image { image = resultImage, scaleMode = ScaleMode.ScaleToFit };
                heatmap.AddToClassList("le-heatmap");
                Ui.Tooltip(heatmap, "Green is clean, red barely touched, grey blocked by furniture");
                screen.Add(heatmap);
            }
            Ui.Text(screen, results.batteryRanOut
                ? $"The battery ran out after {SimulationResults.FormatDuration(results.simulatedSeconds)} of cleaning."
                : $"Simulated {SimulationResults.FormatDuration(results.simulatedSeconds)} of cleaning in " +
                  $"{SimulationResults.FormatDuration(results.realSeconds)}, about {results.SpeedUp:0}× real time.",
                "le-start__text");

            var stats = Ui.Div(screen, "le-stats le-run-stats");
            Ui.Stat(stats, "Cleaned", $"{results.coveragePercent:0.0}%");
            Ui.Stat(stats, "Distance", Ui.FormatMetres(results.metresDriven));
            Ui.Stat(stats, "Blocked", Ui.FormatArea(results.blockedArea));
            Ui.Stat(stats, "Simulated", SimulationResults.FormatDuration(results.simulatedSeconds));
            Ui.Stat(stats, "Real time", SimulationResults.FormatDuration(results.realSeconds));

            Ui.Text(screen, $"Seed {results.seed} · watching it replays this exact run", "le-run-seed");

            var actions = Ui.Div(screen, "le-start__actions");
            Ui.Button(actions, "Run again", IconKind.Redo, () => FastRun(running, Rng.NewSeed()), "le-btn--primary le-start__button");
            Ui.Button(actions, "Watch it", IconKind.Play, () => Watch(running, results.seed), "le-btn--ghost le-start__button");
            Ui.Button(actions, "Floor plans", IconKind.Back, ShowPicker, "le-btn--ghost le-start__button");

            ShowScreen(screen);
        }
    }
}
