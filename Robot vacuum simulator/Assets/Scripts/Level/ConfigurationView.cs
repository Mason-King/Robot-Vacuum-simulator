using System;
using System.Globalization;
using System.IO;
using RobotVacuum.Level;
using RobotVacuum.Sim;
using RobotVacuum.Sim.Control;
using UnityEngine;
using UnityEngine.UIElements;

namespace RobotVacuum.LevelEditor
{
    /// <summary>
    /// The start screen's Configuration page: the movement algorithm and vacuum settings every run uses,
    /// from the editor's preview to the simulator. Changes are saved through <see cref="SimConfig"/> as they happen.
    /// </summary>
    public sealed class ConfigurationView : VisualElement
    {
        readonly OverlayLayer overlay;
        readonly FloorPalette palette;
        readonly ScrollView content;
        readonly Label subtitle;
        readonly VisualElement undoButton, redoButton;

        public event Action BackRequested;
        public event Action SimulateRequested;

        public ConfigurationView(OverlayLayer overlay, FloorPalette palette)
        {
            this.overlay = overlay;
            this.palette = palette;
            AddToClassList("le-screen");
            AddToClassList("le-configuration");

            var header = Ui.PageHeader(this, "Configuration", "Start screen", () => BackRequested?.Invoke(), out subtitle);
            subtitle.text = "Used by every simulation and preview run";
            undoButton = Ui.IconButton(header, IconKind.Undo, $"Undo ({Shortcut("Z")})", () => Step(true), "le-btn--ghost");
            redoButton = Ui.IconButton(header, IconKind.Redo, $"Redo ({Shortcut("Shift+Z")})", () => Step(false), "le-btn--ghost");
            var history = Ui.Button(header, "History", null, ShowHistory, "le-btn--ghost");
            Ui.Tooltip(history, "Saved versions of the configuration: compare or restore them");
            var saveVersion = Ui.Button(header, "Save version", null, AskSaveVersion, "le-btn--ghost");
            Ui.Tooltip(saveVersion, "Keep the settings as they are now as a named version");
            Ui.Button(header, "Reset to defaults", IconKind.Undo, () =>
            {
                SimConfig.ResetToDefaults(); // undoable; rebuilt through SimConfig.Replaced
                overlay.Toast("Configuration reset to defaults", "Undo", () => Step(true));
            }, "le-btn--ghost");
            Ui.Button(header, "Load…", IconKind.Plus, LoadConfiguration, "le-btn--ghost");
            Ui.Button(header, "Save…", IconKind.Save, SaveConfiguration, "le-btn--ghost");
            Ui.Button(header, "Start simulation", IconKind.Play, () => SimulateRequested?.Invoke(), "le-btn--primary");

            content = new ScrollView(ScrollViewMode.Vertical);
            content.AddToClassList("le-page__content");
            content.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            Add(content);

            // Undo and redo follow every change; the controls are rebuilt when the settings are replaced from outside
            // them (undo, redo, a restored version, a load, a reset). Leaving the screen keeps the changes as a version.
            focusable = true;
            RegisterCallback<KeyDownEvent>(OnKeyDown, TrickleDown.TrickleDown);
            RegisterCallback<AttachToPanelEvent>(_ =>
            {
                SimConfig.Edited += RefreshUndo;
                SimConfig.Replaced += OnReplaced;
                RefreshUndo();
            });
            RegisterCallback<DetachFromPanelEvent>(_ =>
            {
                SimConfig.Edited -= RefreshUndo;
                SimConfig.Replaced -= OnReplaced;
                SimConfig.CommitVersion("Configuration edited");
            });

            Rebuild();
        }

        // ---- undo, redo, versions -------------------------------------------------------------------------------

        void Step(bool back)
        {
            string label = back ? SimConfig.UndoLabel : SimConfig.RedoLabel;
            if (!(back ? SimConfig.Undo() : SimConfig.Redo())) return;
            overlay.Toast($"{(back ? "Undo" : "Redo")} {label}");
        }

        void RefreshUndo()
        {
            undoButton.SetEnabled(SimConfig.CanUndo);
            redoButton.SetEnabled(SimConfig.CanRedo);
        }

        void OnReplaced()
        {
            Rebuild();
            RefreshUndo();
        }

        static bool IsMac => Application.platform == RuntimePlatform.OSXPlayer || Application.platform == RuntimePlatform.OSXEditor;
        static string Shortcut(string key) => IsMac ? $"⌘{key.Replace("Shift+", "⇧")}" : $"Ctrl+{key}";

        void OnKeyDown(KeyDownEvent evt)
        {
            if (evt.keyCode != KeyCode.Z || !(IsMac ? evt.commandKey : evt.ctrlKey) || Ui.IsTyping(panel)) return;
            Step(!evt.shiftKey);
            evt.StopPropagation();
        }

        void ShowHistory()
        {
            SimConfig.CommitVersion("Configuration edited"); // so what is on screen now can be compared too
            VersionHistoryDialog.Show(overlay, new VersionHistoryDialog.Source
            {
                Name = "Configuration",
                History = SimConfig.Versions,
                CurrentContent = () => SimConfig.VersionContent,
                UnsavedEdits = () => SimConfig.UnversionedEdits,
                Compare = (a, b) => ConfigDiff.Compare(SimConfig.Data.Parse(a), SimConfig.Data.Parse(b)),
                Preview = null,
                Restore = SimConfig.RestoreVersion,
                RestoredNote = "Undo puts it back.",
            });
        }

        void AskSaveVersion()
        {
            string name = string.Empty;
            var body = Ui.Div(null, "le-history");
            var field = Ui.TextInput(body, name, value => name = value);
            field.RegisterValueChangedCallback(evt => name = evt.newValue);
            overlay.ShowDialog("Save a version", "Optional: a name to find it by, such as “Slow carpet test”.", body, null,
                new DialogButton("Cancel", ButtonStyle.Ghost, null),
                new DialogButton("Save version", ButtonStyle.Primary, () =>
                {
                    var version = SimConfig.CommitVersion(string.IsNullOrWhiteSpace(name) ? "Saved" : name);
                    if (version != null) overlay.Toast($"Saved as v{version.number}");
                    else if (SimConfig.Versions.Latest is VersionInfo latest)
                    {
                        // Nothing changed since the latest version: name that one instead, if a name was given.
                        if (!string.IsNullOrWhiteSpace(name)) SimConfig.Versions.Relabel(latest.number, name);
                        overlay.Toast($"No changes since v{latest.number}{(string.IsNullOrWhiteSpace(name) ? "" : "; named it instead")}");
                    }
                    else overlay.Toast("Couldn't save a version (see the Console).");
                }));
            field.schedule.Execute(() => field.Focus());
        }

        void Rebuild()
        {
            content.Clear();
            var column = Ui.Div(content, "le-page__column");

            BuildMovement(Ui.Div(column, "le-panel"));
            BuildVacuum(Ui.Div(column, "le-panel"));
            BuildFloors(Ui.Div(column, "le-panel"));
            BuildExperiment(Ui.Div(column, "le-panel"));

            Ui.Text(column, "Changes save automatically. The simulator's Settings menu edits these same values mid-run.",
                "le-empty-hint le-page__footnote");
        }

        void BuildMovement(VisualElement panel)
        {
            PanelTitle(panel, IconKind.Spawn, "Movement", "How the vacuum chooses where to go next.");
            panel.Add(new RunSettingsPanel(SimConfig.RunSettings, null, refusal => overlay.Toast(refusal, durationMs: 7600),
                SimConfig.Save, reimport: ReimportAlgorithms));
        }

        /// <summary>Rescans the Algorithms folder so new or changed algorithms show in the list (the panel refreshes it).</summary>
        void ReimportAlgorithms()
        {
            var report = AlgorithmCatalog.Rescan();
            if (report.refused)
            {
                overlay.Toast(report.refusedReason, durationMs: 7600);
                return;
            }
            Debug.Log("Reimport Algorithms:\n" + report.Text());
            overlay.Toast(report.FailedUnits > 0
                ? $"{report.AlgorithmCount} algorithm(s) loaded from the folder; {report.FailedUnits} failed (see the load report in Settings, or the Console)."
                : $"{report.AlgorithmCount} algorithm(s) loaded from the folder.", durationMs: 6000);
        }

        void BuildVacuum(VisualElement panel)
        {
            var settings = SimConfig.Vacuum;
            PanelTitle(panel, IconKind.Sliders, "Vacuum", "Drive speeds and how much work a full battery holds.");

            Ui.Text(panel, "Units, everywhere in the app", "le-field-label");
            var units = new Segmented(new[] { "Imperial (ft, ft/s)", "Metric (m, m/s)" }, settings.useFeet ? 0 : 1);
            units.SelectionChanged += index =>
            {
                SimConfig.UseFeet = index == 0;
                Rebuild(); // so the slider read-outs switch unit too
            };
            panel.Add(units);

            AddSlider(panel, "Drive speed", 0.05f, 2f, settings.driveSpeed, settings.FormatSpeed, v => settings.driveSpeed = v);
            AddSlider(panel, "Reverse speed", 0.05f, 1.5f, settings.reverseSpeed, settings.FormatSpeed, v => settings.reverseSpeed = v);
            AddSlider(panel, "Turn speed", 30f, 720f, settings.turnSpeed, v => $"{v:0} °/s", v => settings.turnSpeed = v);
            AddSlider(panel, "Battery capacity", 10f, Battery.MaxCapacity, settings.batteryCapacity, VacuumSettings.FormatCapacity,
                v => settings.batteryCapacity = v);

            AddShape(panel, settings);
        }

        /// <summary>The body's outline (3.3). Before a run only: unlike the sliders above, it can't change mid-run.</summary>
        static void AddShape(VisualElement panel, VacuumSettings settings)
        {
            var kinds = (VacuumShapeKind[])Enum.GetValues(typeof(VacuumShapeKind));
            var labels = Array.ConvertAll(kinds, VacuumShape.Label);

            Ui.Text(panel, "Body shape", "le-field-label le-panel__gap");
            var choice = new Segmented(labels, Mathf.Max(0, Array.IndexOf(kinds, settings.shape)));
            panel.Add(choice);
            var hint = Ui.Text(panel, ShapeHint(settings.shape), "le-empty-hint");

            choice.SelectionChanged += index =>
            {
                settings.shape = kinds[index];
                SimConfig.Save();
                hint.text = ShapeHint(settings.shape);
            };
        }

        static string ShapeHint(VacuumShapeKind kind)
        {
            var shape = VacuumShape.For(kind);
            string size = SimConfig.UseFeet
                ? $"{shape.Width * VacuumSettings.MetresToFeet * 12f:0.#} × {shape.Length * VacuumSettings.MetresToFeet * 12f:0.#} in, needs {shape.TurnRadius * 2f * VacuumSettings.MetresToFeet * 12f:0.#} in to turn in place"
                : $"{shape.Width * 100f:0} × {shape.Length * 100f:0} cm, needs {shape.TurnRadius * 200f:0} cm to turn in place";
            string about = kind == VacuumShapeKind.Round
                ? "The original disc."
                : kind == VacuumShapeKind.DShaped
                    ? "Round back, flat front that reaches into corners."
                    : "Long axis front to back; it must line up to fit narrow gaps.";
            return $"{about} {size}. Set before a run; it can't change mid-run.";
        }

        void BuildFloors(VisualElement panel)
        {
            PanelTitle(panel, IconKind.Grid, "Floor coverings",
                $"Effort to clean each covering, from {FloorTuning.MinCleaningEffort:0.#} to {FloorTuning.MaxCleaningEffort:0} (higher cleans slower). The clean rate is 1 ÷ effort.");

            if (palette == null)
            {
                Ui.Text(panel, "No floor palette is available.", "le-empty-hint");
                return;
            }

            for (int i = 0; i < palette.Count; i++)
            {
                var floor = palette.Get(i);
                if (floor == null || !floor.isFloor) continue;

                var row = Ui.Div(panel, "le-config-floor");
                Ui.Swatch(row, palette.ColorOf(i), "le-config-floor__swatch");
                Ui.Text(row, floor.Label, "le-config-floor__name");

                string label = floor.Label;
                float stock = floor.cleaningEffort;
                var slider = new ValueSlider(FloorTuning.MinCleaningEffort, FloorTuning.MaxCleaningEffort,
                    SimConfig.CleaningEffortFor(label) ?? stock, v => $"{v:0.0} · {1f / v:0.00}× rate", true);
                slider.AddToClassList("le-config-floor__slider");
                slider.ValueChanged += value => SimConfig.SetCleaningEffort(label, Mathf.Approximately(value, stock) ? (float?)null : value);
                row.Add(slider);

                Ui.IconButton(row, IconKind.Undo, $"Back to the default, {stock:0.0}", () =>
                {
                    SimConfig.SetCleaningEffort(label, null);
                    slider.SetValueWithoutNotify(stock);
                }, "le-btn--ghost le-btn--small");
            }
        }

        void BuildExperiment(VisualElement panel)
        {
            PanelTitle(panel, IconKind.Chart, "Experiment",
                "Randomness, measurement resolution and batch limits. The same seed and settings always reproduce the same run.");

            var random = new SwitchToggle("New random seed every run", SimConfig.RandomSeed);
            panel.Add(random);

            var seedRow = Ui.Div(panel, "le-config-seed");
            Ui.Text(seedRow, "Fixed seed", "le-field-label");
            var seed = Ui.TextInput(seedRow, SimConfig.Seed.ToString(CultureInfo.InvariantCulture), text =>
            {
                if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)) SimConfig.Seed = value;
            }, "le-config-seed__input");
            seed.SetEnabled(!SimConfig.RandomSeed);
            random.ValueChanged += value =>
            {
                SimConfig.RandomSeed = value;
                seed.SetEnabled(!value);
            };

            Ui.Text(panel, "Coverage grid cell size", "le-field-label le-panel__gap");
            var cell = new ValueSlider(SimConfig.MinCellSize, SimConfig.MaxCellSize, SimConfig.CellSize, Ui.FormatSmallLength, true);
            cell.ValueChanged += value => SimConfig.CellSize = value;
            panel.Add(cell);
            Ui.Text(panel, "Finer cells trace edges more exactly but cost more time. Coverage is a share of area, so results stay comparable across sizes.", "le-empty-hint");

            Ui.Text(panel, "Telemetry sampling interval", "le-field-label le-panel__gap");
            var telemetry = new ValueSlider(SimConfig.MinTelemetryInterval, SimConfig.MaxTelemetryInterval, SimConfig.TelemetryInterval,
                v => $"{v:0.##} s of simulated time", true);
            telemetry.ValueChanged += value => SimConfig.TelemetryInterval = value;
            panel.Add(telemetry);

            Ui.Text(panel, "Run time limit", "le-field-label le-panel__gap");
            var limitOn = new SwitchToggle("Stop single runs at the time limit", SimConfig.RunLimitEnabled);
            panel.Add(limitOn);
            var limit = new ValueSlider(1f, SimConfig.MaxRunLimitSeconds, SimConfig.RunLimitSeconds, v => SimConfig.FormatLimit(v) + " (h:mm:ss)", true);
            limit.ValueChanged += value => SimConfig.RunLimitSeconds = Mathf.Max(1f, Mathf.Round(value));
            panel.Add(limit);
            limitOn.ValueChanged += on => SimConfig.RunLimitEnabled = on;
            Ui.Text(panel, "Simulated seconds (type an exact value in the box). Every batch trial ends here at the latest; a single run only when the switch is on " +
                           "(it then stops and can be resumed). Runs also end when the battery runs out or the floor is clean, " +
                           "and the program never lets one go past 24 h.", "le-empty-hint");
        }

        void SaveConfiguration()
        {
            try
            {
                string path = FloorPlanFiles.PickSavePath("Save configuration", "vacuum configuration", "json");
                if (path == null) return;

                File.WriteAllText(path, SimConfig.ToJson());
                overlay.Toast($"Saved to {path}", "Show", () => FloorPlanFiles.Reveal(Path.GetDirectoryName(path)));
            }
            catch (Exception exception)
            {
                overlay.Toast($"Couldn't save: {exception.Message}");
            }
        }

        void LoadConfiguration()
        {
            var paths = FloorPlanFiles.PickImports("Load configuration");
            if (paths.Count == 0)
            {
                if (!Application.isEditor)
                    overlay.Toast($"Put a configuration .json in {FloorPlanFiles.ImportFolder}, then press Load again.",
                        "Show folder", () => FloorPlanFiles.Reveal(FloorPlanFiles.ImportFolder));
                return;
            }

            string path = paths[paths.Count - 1];
            try
            {
                if (!SimConfig.TryImport(File.ReadAllText(path)))
                {
                    overlay.Toast($"{Path.GetFileName(path)} isn't a configuration file.");
                    return;
                }

                FloorPlanFiles.MarkImported(path);
                overlay.Toast("Configuration loaded (saved as a version; Undo puts the old one back)");
            }
            catch (Exception exception)
            {
                overlay.Toast($"Couldn't load: {exception.Message}");
            }
        }

        static void PanelTitle(VisualElement panel, IconKind icon, string title, string text)
        {
            var top = Ui.Div(panel, "le-panel__header");
            var tile = Ui.Div(top, "le-start__step-icon");
            tile.Add(new IconElement(icon));

            var titles = Ui.Div(top, "le-panel__titles");
            Ui.Text(titles, title, "le-panel__title");
            Ui.Text(titles, text, "le-panel__text");
        }

        static void AddSlider(VisualElement parent, string label, float min, float max, float value,
            Func<float, string> format, Action<float> store)
        {
            Ui.Text(parent, label, "le-field-label le-panel__gap");

            var slider = new ValueSlider(min, max, value, format, true);
            slider.ValueChanged += next =>
            {
                store(next);
                SimConfig.Save();
            };
            parent.Add(slider);
        }
    }
}
