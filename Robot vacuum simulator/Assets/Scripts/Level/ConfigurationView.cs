using System;
using System.Globalization;
using System.IO;
using RobotVacuum.Level;
using RobotVacuum.Sim;
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

        public event Action BackRequested;
        public event Action SimulateRequested;

        public ConfigurationView(OverlayLayer overlay, FloorPalette palette)
        {
            this.overlay = overlay;
            this.palette = palette;
            AddToClassList("le-screen");

            var header = Ui.PageHeader(this, "Configuration", "Start screen", () => BackRequested?.Invoke(), out subtitle);
            subtitle.text = "Used by every simulation and preview run";
            Ui.Button(header, "Reset to defaults", IconKind.Undo, () =>
            {
                SimConfig.ResetToDefaults();
                Rebuild();
                overlay.Toast("Configuration reset to defaults");
            }, "le-btn--ghost");
            Ui.Button(header, "Load…", IconKind.Plus, LoadConfiguration, "le-btn--ghost");
            Ui.Button(header, "Save…", IconKind.Save, SaveConfiguration, "le-btn--ghost");
            Ui.Button(header, "Start simulation", IconKind.Play, () => SimulateRequested?.Invoke(), "le-btn--primary");

            content = new ScrollView(ScrollViewMode.Vertical);
            content.AddToClassList("le-page__content");
            content.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            Add(content);

            Rebuild();
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

            Ui.Text(panel, "Algorithm", "le-field-label");
            var movement = new Segmented(MovementBrain.Labels, (int)SimConfig.Pattern);
            panel.Add(movement);
            movement.SelectionChanged += index => SimConfig.Pattern = (MovementPattern)index;
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
            AddSlider(panel, "Battery capacity", 10f, 1800f, settings.batteryCapacity, VacuumSettings.FormatCapacity,
                v => settings.batteryCapacity = v);
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

            Ui.Text(panel, "Batch trial time limit", "le-field-label le-panel__gap");
            var limit = new ValueSlider(1f, 240f, SimConfig.MaxRunMinutes, v => $"{v:0} min", true);
            limit.ValueChanged += value => SimConfig.MaxRunMinutes = Mathf.Round(value);
            panel.Add(limit);
            Ui.Text(panel, "A batch trial ends when the battery runs out, the floor is clean, or this much simulated time has passed.", "le-empty-hint");
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
                Rebuild();
                overlay.Toast("Configuration loaded");
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
