using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using RobotVacuum.Level;
using RobotVacuum.Network;
using RobotVacuum.Sim.Control;
using UnityEngine;
using UnityEngine.UIElements;

namespace RobotVacuum.LevelEditor
{
    /// <summary>
    /// The interface size the user chose on Settings > General, applied to every UI panel (C.7). Panels made by
    /// <see cref="UiPanel.Create"/> register here, so a change applies at once to the open screen.
    /// </summary>
    public static class InterfaceSize
    {
        const string PrefsKey = "RobotVacuum.InterfaceSize";
        public static readonly float[] Steps = { 0.85f, 1f, 1.15f, 1.3f };
        public static readonly string[] Labels = { "Small", "Normal", "Large", "Larger" };

        static readonly List<(PanelSettings settings, float baseScale)> panels = new List<(PanelSettings, float)>();

        public static float Value => Mathf.Clamp(PlayerPrefs.GetFloat(PrefsKey, 1f), 0.75f, 1.5f);

        public static int StepIndex
        {
            get
            {
                float value = Value;
                int best = 1;
                for (int i = 0; i < Steps.Length; i++)
                    if (Mathf.Abs(Steps[i] - value) < Mathf.Abs(Steps[best] - value)) best = i;
                return best;
            }
        }

        public static void Set(float value)
        {
            value = Mathf.Clamp(value, 0.75f, 1.5f);
            PlayerPrefs.SetFloat(PrefsKey, value);
            PlayerPrefs.Save();
            panels.RemoveAll(p => p.settings == null);
            foreach (var (settings, baseScale) in panels) settings.scale = baseScale * value;
        }

        public static void Track(PanelSettings settings, float baseScale)
        {
            panels.RemoveAll(p => p.settings == null);
            panels.Add((settings, baseScale));
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => panels.Clear();
    }

    /// <summary>Shared pieces of the Settings tabs.</summary>
    static class SettingsUi
    {
        public static void Title(VisualElement panel, IconKind icon, string title, string text)
        {
            var top = Ui.Div(panel, "le-panel__header");
            var tile = Ui.Div(top, "le-start__step-icon");
            tile.Add(new IconElement(icon));
            var titles = Ui.Div(top, "le-panel__titles");
            Ui.Text(titles, title, "le-panel__title");
            Ui.Text(titles, text, "le-panel__text");
        }

        public static string FormatBytes(long bytes)
        {
            if (bytes < 0) return "…";
            if (bytes < 1024) return $"{bytes} B";
            double value = bytes;
            string[] units = { "KB", "MB", "GB", "TB" };
            int unit = -1;
            do { value /= 1024; unit++; } while (value >= 1024 && unit < units.Length - 1);
            return value >= 100 ? $"{value:0} {units[unit]}" : $"{value:0.#} {units[unit]}";
        }
    }

    // ==================================================================== General

    /// <summary>Settings > General: units, interface size, window size, and restoring these defaults.</summary>
    public sealed class GeneralSettingsView : VisualElement
    {
        static readonly (string label, int width, int height)[] WindowSizes =
        {
            ("1280 × 720", 1280, 720), ("1600 × 900", 1600, 900), ("1920 × 1080", 1920, 1080),
        };

        readonly OverlayLayer overlay;
        Label windowNow;

        public GeneralSettingsView(OverlayLayer overlay)
        {
            this.overlay = overlay;
            Build();
            var pump = schedule.Execute(RefreshWindow).Every(1000);
            RegisterCallback<DetachFromPanelEvent>(_ => pump.Pause());
            RegisterCallback<AttachToPanelEvent>(_ => pump.Resume());
        }

        void Build()
        {
            Clear();

            var units = Ui.Div(this, "le-panel");
            SettingsUi.Title(units, IconKind.Sliders, "Units",
                "How lengths, areas and speeds are shown everywhere in the program (1.3.5). The same switch is also on the Configuration screen, in the editor and while a run plays.");
            var unitChoice = new Segmented(new[] { "Metric (m, m/s)", "Imperial (ft, ft/s)" }, SimConfig.UseFeet ? 1 : 0);
            unitChoice.SelectionChanged += index => SimConfig.UseFeet = index == 1;
            units.Add(unitChoice);

            var display = Ui.Div(this, "le-panel");
            SettingsUi.Title(display, IconKind.Fit, "Display", "Make the interface easier to read, and size the window.");

            Ui.Text(display, "Interface size", "le-field-label");
            var size = new Segmented(InterfaceSize.Labels.Select((label, i) => $"{label} ({InterfaceSize.Steps[i] * 100:0}%)").ToList(), InterfaceSize.StepIndex);
            size.SelectionChanged += index => InterfaceSize.Set(InterfaceSize.Steps[index]);
            display.Add(size);
            Ui.Text(display, "Changes every screen at once. Text and buttons keep their shape; everything grows or shrinks together.", "le-empty-hint");

            Ui.Text(display, "Window", "le-field-label le-panel__gap");
            var sizes = Ui.Div(display, "le-settings__actions");
            var screen = Screen.currentResolution;
            foreach (var (label, width, height) in WindowSizes)
            {
                var button = Ui.Button(sizes, label, null, () => SetWindow(width, height), "le-btn--ghost");
                if (!Application.isEditor && (width > screen.width || height > screen.height))
                {
                    button.SetEnabled(false);
                    Ui.Tooltip(button, "Larger than this screen.");
                }
            }
            Ui.Button(sizes, "Fit the screen", IconKind.Fit, () => SetWindow(Mathf.RoundToInt(screen.width * 0.9f), Mathf.RoundToInt(screen.height * 0.9f)), "le-btn--ghost");
            Ui.Button(sizes, "Full screen", null, () => Screen.SetResolution(screen.width, screen.height, FullScreenMode.FullScreenWindow), "le-btn--ghost");
            windowNow = Ui.Text(display, string.Empty, "le-empty-hint");
            Ui.Text(display, Application.isEditor
                ? "The window size applies in the built program; the Unity editor keeps its Game view size."
                : "The program always opens in a window you can resize by dragging its edges. Full screen lasts until it closes; Alt+Enter switches too.", "le-empty-hint");
            RefreshWindow();

            var intro = Ui.Div(this, "le-panel");
            SettingsUi.Title(intro, IconKind.Play, "Intro video", "The 10-second VACSim video shown when the program opens. A click or any key skips it.");
            var introChoice = new Segmented(new[] { "With sound", "Muted", "Off" }, (int)IntroVideo.Mode);
            introChoice.SelectionChanged += index => IntroVideo.Mode = (IntroVideoMode)index;
            intro.Add(introChoice);
            var introRow = Ui.Div(intro, "le-settings__actions");
            Ui.Button(introRow, "Play now", IconKind.Play, () => IntroVideo.Play(IntroVideo.Mode == IntroVideoMode.Muted), "le-btn--ghost");
            if (Application.isEditor)
                Ui.Text(intro, "In the Unity editor it plays at Play only when Robot Vacuum › Play Intro Video in Play Mode is ticked.", "le-empty-hint");

            var reset = Ui.Div(this, "le-panel");
            SettingsUi.Title(reset, IconKind.Undo, "Restore defaults",
                "Puts units, interface size, the intro video and the window back as they were when the program was installed. Floor plans, runs, the run configuration and network settings are not touched.");
            var row = Ui.Div(reset, "le-settings__actions");
            Ui.Button(row, "Restore defaults", IconKind.Undo, ConfirmReset, "le-btn--ghost");
        }

        void RefreshWindow()
        {
            if (windowNow == null) return;
            string mode = Screen.fullScreenMode == FullScreenMode.Windowed ? "in a window" : "full screen";
            windowNow.text = $"Now {Screen.width} × {Screen.height}, {mode}.";
        }

        static void SetWindow(int width, int height)
        {
            var screen = Screen.currentResolution;
            Screen.SetResolution(Mathf.Min(width, screen.width), Mathf.Min(height, screen.height), FullScreenMode.Windowed);
        }

        void ConfirmReset()
        {
            overlay.ShowDialog("Restore the defaults?", "Units go back to metric, the interface to normal size, the intro video to with sound and the window to 1600 × 900.", null,
                new DialogButton("Cancel", ButtonStyle.Ghost, null),
                new DialogButton("Restore", ButtonStyle.Primary, () =>
                {
                    SimConfig.UseFeet = false;
                    InterfaceSize.Set(1f);
                    IntroVideo.Mode = IntroVideoMode.WithSound;
                    if (!Application.isEditor) SetWindow(1600, 900);
                    Build();
                    overlay.Toast("Defaults restored.");
                }));
        }
    }

    // ==================================================================== Data & storage

    /// <summary>
    /// Settings > Data & storage: where everything the program keeps is stored on this computer, how much room each
    /// takes, the free space left, the run history, and the Algorithms folder with its last load report (2.12, 3.8,
    /// 4.8, C.2, C.10; SDD 2.3 "project directory", SDD 3.1 storage planning). Shown only, nothing moved or deleted.
    /// </summary>
    public sealed class DataStorageView : VisualElement
    {
        sealed class Place
        {
            public string name;
            public string path;
            public string note;
            public long bytes = -1;
            public int files;
            public Label sizeLabel;
        }

        readonly OverlayLayer overlay;
        readonly List<Place> places = new List<Place>();
        Label freeLabel;
        long freeBytes = -1;
        volatile bool measured;

        public DataStorageView(OverlayLayer overlay)
        {
            this.overlay = overlay;
            string data = Application.persistentDataPath;
            var cloudRoots = CampaignImport.DefaultRoots().Skip(1).ToList(); // [0] is the network campaigns folder
            string cloud = cloudRoots.FirstOrDefault(Directory.Exists) ?? cloudRoots.FirstOrDefault();

            AddPlace("Floor plans", LevelLibrary.Folder, "One file per floor plan, in the declarative format (2.13).");
            AddPlace("Runs", Path.Combine(data, "Runs"), "Each run's results, replay and telemetry; about 20 MB a run with the full per-cell model.");
            AddPlace("Network batches", Path.Combine(data, "NetworkCampaigns"), "Batches run across network workers: results and every computer's logs.");
            if (cloud != null) AddPlace("Cloud results", cloud, "Campaigns brought back from AWS by vacuumctl.");
            AddPlace("Algorithms", AlgorithmCatalog.Folder, "Your own cleaning algorithms (.cs or .dll), loaded at run time (3.8).");
            AddPlace("This PC's worker", WorkerFiles.DataFolder, "The worker's log, the PCs paired with it and the batches it is running.");
            string log = Application.consoleLogPath;
            if (!string.IsNullOrEmpty(log)) AddPlace("Program log", Path.GetDirectoryName(log), $"{Path.GetFileName(log)}: what the program reported, for finding problems.");
            AddPlace("Settings and run history", data, "Network settings, the paired-workers list and the run history (keys are in Windows Credential Manager).");

            Build();
            new Thread(Measure) { IsBackground = true, Name = "Storage sizes" }.Start();
            var pump = schedule.Execute(RefreshSizes).Every(500);
            RegisterCallback<DetachFromPanelEvent>(_ => pump.Pause());
            RegisterCallback<AttachToPanelEvent>(_ => pump.Resume());
        }

        void AddPlace(string name, string path, string note)
        {
            if (string.IsNullOrEmpty(path)) return;
            places.Add(new Place { name = name, path = Path.GetFullPath(path), note = note });
        }

        void Build()
        {
            var where = Ui.Div(this, "le-panel");
            SettingsUi.Title(where, IconKind.Save, "Where your data is kept",
                "Everything the program saves on this computer. Open a folder to back it up, copy results elsewhere or look at the files.");
            var list = Ui.Div(where, "le-network__list");
            foreach (var place in places)
            {
                var row = Ui.Div(list, "le-network__row le-storage__row");
                var names = Ui.Div(row, "le-storage__names");
                Ui.Text(names, place.name, "le-network__row-name le-storage__name");
                place.sizeLabel = Ui.Text(names, "measuring…", "le-storage__size");
                var texts = Ui.Div(row, "le-storage__texts");
                Ui.Text(texts, place.path, "le-storage__path");
                Ui.Text(texts, place.note, "le-network__row-text");
                var captured = place;
                Ui.Button(row, "Open", null, () => FloorPlanFiles.Reveal(captured.path), "le-btn--ghost le-settings__inline-btn");
            }
            freeLabel = Ui.Text(where, "Free space: measuring…", "le-empty-hint le-panel__gap");

            int runs = RunHistory.LoadAll().Count;
            var history = Ui.Div(this, "le-panel");
            SettingsUi.Title(history, IconKind.Chart, "Run history",
                $"{runs:N0} run(s) kept. The history keeps the newest {RunHistory.MaxRuns:N0}; older runs drop off as new ones are added.");
            Ui.Text(history, "To remove runs, delete them in Analysis. Network batches and cloud campaigns stay in their folders until you delete them, so they can be imported again.", "le-empty-hint");

            var algorithms = Ui.Div(this, "le-panel");
            var report = AlgorithmCatalog.LastReport;
            string summary = report == null ? "Not scanned yet."
                : $"{report.AlgorithmCount} algorithm(s) loaded from the folder" + (report.FailedUnits > 0 ? $"; {report.FailedUnits} file(s) or folder(s) refused or failed to compile." : ".");
            SettingsUi.Title(algorithms, IconKind.Pen, "Algorithms", summary);
            Ui.Text(algorithms, "Algorithm code runs in a sandbox: only the algorithm API, maths, strings, collections and LINQ, with no files, network, other programs, " +
                                "reflection, threads or clocks (4.8). README.md in the folder has the rules and a template.", "le-empty-hint");
            var actions = Ui.Div(algorithms, "le-settings__actions");
            Ui.Button(actions, "Rescan", IconKind.Check, Rescan, "le-btn--ghost");
            Ui.Button(actions, "Show load report", null, ShowReport, "le-btn--ghost");
        }

        void Rescan()
        {
            var report = AlgorithmCatalog.Rescan();
            if (report.refused)
            {
                overlay.Toast(report.refusedReason);
                return;
            }
            overlay.Toast(report.FailedUnits > 0
                ? $"{report.AlgorithmCount} algorithm(s) loaded; {report.FailedUnits} failed. See the load report."
                : $"{report.AlgorithmCount} algorithm(s) loaded.");
            Clear();
            Build();
            measured = true; // sizes are already known; just show them again
        }

        void ShowReport()
        {
            var report = AlgorithmCatalog.LastReport;
            var body = Ui.Div(null, "le-batch-setup");
            var scroll = new ScrollView(ScrollViewMode.Vertical);
            scroll.AddToClassList("le-import__list");
            body.Add(scroll);
            foreach (string line in (report?.Text() ?? "Not scanned yet.").Split('\n'))
                if (line.Trim().Length > 0) Ui.Text(scroll, line.TrimEnd('\r'), "le-settings__output-line");
            overlay.ShowDialog("Algorithm load report", null, body, null,
                new DialogButton("Open folder", ButtonStyle.Ghost, () => FloorPlanFiles.Reveal(AlgorithmCatalog.Folder)),
                new DialogButton("Close", ButtonStyle.Primary, null));
        }

        void Measure()
        {
            foreach (var place in places)
            {
                long bytes = 0;
                int files = 0;
                try
                {
                    if (Directory.Exists(place.path))
                    {
                        // The settings folder holds the others: count only its own files, not theirs again.
                        var option = place.name == "Settings and run history" ? SearchOption.TopDirectoryOnly : SearchOption.AllDirectories;
                        foreach (string file in Directory.EnumerateFiles(place.path, "*", option))
                        {
                            try { bytes += new FileInfo(file).Length; files++; }
                            catch (Exception) { }
                        }
                    }
                }
                catch (Exception) { }
                place.files = files;
                place.bytes = bytes;
            }
            try
            {
                string root = Path.GetPathRoot(Path.GetFullPath(places.FirstOrDefault()?.path ?? "."));
                freeBytes = new DriveInfo(root).AvailableFreeSpace;
            }
            catch (Exception) { freeBytes = -2; }
            measured = true;
        }

        void RefreshSizes()
        {
            foreach (var place in places)
                if (place.sizeLabel != null)
                    place.sizeLabel.text = place.bytes < 0 ? "measuring…" : place.files == 0 ? "empty" : $"{SettingsUi.FormatBytes(place.bytes)} · {place.files:N0} file(s)";
            if (freeLabel != null && measured)
                freeLabel.text = freeBytes == -2 ? "Free space: unknown." : $"Free space on this drive: {SettingsUi.FormatBytes(freeBytes)}.";
        }
    }

    // ==================================================================== About

    /// <summary>Settings > About: what the program is, who made it, and which version this is (C.8).</summary>
    public sealed class AboutView : VisualElement
    {
        public AboutView(OverlayLayer overlay)
        {
            var hero = Ui.Div(this, "le-panel le-about__hero");
            var mark = new IconElement(IconKind.Logo);
            mark.AddToClassList("le-about__mark");
            hero.Add(mark);
            Ui.Text(hero, "VACSim", "le-about__title");
            Ui.Text(hero, "Vacuum Autonomous Controller", "le-about__subtitle");
            Ui.Text(hero, "A simulator for testing robot vacuum cleaning algorithms. Run them over different floor plans and floor coverings, " +
                          "at scale and with repeatable seeds, and compare how well each one cleans.", "le-about__text");

            var credits = Ui.Div(this, "le-panel le-about__credits");
            Ui.Text(credits, "CREATED BY", "le-about__eyebrow");
            var names = Ui.Div(credits, "le-about__names");
            // Alphabetical by last name.
            foreach (string name in new[] { "Kristie Byler", "Mason King", "Eric Olivares", "Emmi Phillips", "Tommy Rasmussen" })
                Ui.Text(names, name, "le-about__name");
            Ui.Text(credits, "UNDER THE GUIDANCE OF", "le-about__eyebrow le-about__eyebrow--gap");
            Ui.Text(credits, "Professor James Williamson", "le-about__name");
            Ui.Text(credits, "UAH College of Computer Science", "le-about__place");

            var version = Ui.Div(this, "le-panel");
            SettingsUi.Title(version, IconKind.Check, "Version",
                "Every computer in a network batch must run the same version, so a run gives the same result wherever it runs.");
            string build = Application.isEditor ? "Unity editor (not a build)" : (string.IsNullOrEmpty(Application.buildGUID) ? "unknown" : Application.buildGUID.Substring(0, Math.Min(8, Application.buildGUID.Length)));
            var lines = new List<(string label, string value)>
            {
                ("Version", Application.version),
                ("Build", build),
                ("Unity", Application.unityVersion),
                ("System", SystemInfo.operatingSystem),
            };
            var list = Ui.Div(version, "le-network__list");
            foreach (var (label, value) in lines)
            {
                var row = Ui.Div(list, "le-network__row");
                Ui.Text(row, label, "le-network__row-name");
                Ui.Text(row, value, "le-network__row-text");
            }
            var actions = Ui.Div(version, "le-settings__actions le-panel__gap");
            Ui.Button(actions, "Copy version details", IconKind.Duplicate, () =>
            {
                GUIUtility.systemCopyBuffer = "VACSim Vacuum Autonomous Controller\n" + string.Join("\n", lines.Select(l => $"{l.label}: {l.value}"));
                overlay.Toast("Version details copied. Paste them into a bug report or a message to the team.");
            }, "le-btn--ghost");
        }
    }
}
