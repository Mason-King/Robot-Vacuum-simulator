using System;
using System.Collections.Generic;
using RobotVacuum.Sim;
using UnityEngine;
using UnityEngine.UIElements;

namespace RobotVacuum.LevelEditor
{
    /// <summary>
    /// Every run setting as one form, bound to a <see cref="RunSettings"/> it edits in place. The Settings
    /// page binds it to the global defaults; the Run setup page and the editor bind it to the next run.
    /// </summary>
    public sealed class RunSettingsForm : VisualElement
    {
        const float MetresToInches = 39.3701f;

        readonly RunSettings settings;
        TextField seedField;

        /// <summary>Raised after any value changes.</summary>
        public event Action Changed;

        public RunSettingsForm(RunSettings settings)
        {
            this.settings = settings;
            AddToClassList("le-form");
            Build();
        }

        void Build()
        {
            Clear();
            BuildMovement(Group("Movement", "How the vacuum chooses where to go, and the seed behind its random choices."));
            BuildVacuum(Group("Vacuum", "Drive speeds and how much work a full battery holds."));
            BuildCleaning(Group("Cleaning", "How the floor is measured. Set before a run and fixed for it."));
        }

        VisualElement Group(string title, string text)
        {
            var group = Ui.Div(this, "le-form__group");
            var head = Ui.Div(group, "le-form__head");
            Ui.Text(head, title, "le-form__title");
            Ui.Text(head, text, "le-form__text");
            return Ui.Div(group, "le-form__body");
        }

        void BuildMovement(VisualElement body)
        {
            Field(body, "Algorithm");
            var movement = new Segmented(MovementBrain.Labels, (int)settings.pattern);
            movement.SelectionChanged += index =>
            {
                settings.pattern = (MovementPattern)index;
                Changed?.Invoke();
            };
            body.Add(movement);

            var random = new SwitchToggle("New random seed every run", settings.randomSeed);
            random.AddToClassList("le-form__switch");
            body.Add(random);

            var seedRow = Ui.Div(body, "le-form__row");
            Ui.Text(seedRow, "Seed", "le-form__row-label");
            seedField = Ui.TextInput(seedRow, settings.seed.ToString(), value =>
            {
                if (int.TryParse(value, out int seed))
                {
                    settings.seed = seed;
                    Changed?.Invoke();
                }
                seedField.SetValueWithoutNotify(settings.seed.ToString());
            }, "le-form__seed");
            Ui.Text(body, "The same floor plan, settings and seed always give the same run.", "le-form__hint");

            SetSeedEnabled(!settings.randomSeed);
            random.ValueChanged += value =>
            {
                settings.randomSeed = value;
                SetSeedEnabled(!value);
                Changed?.Invoke();
            };
        }

        void SetSeedEnabled(bool enabled) => seedField?.SetEnabled(enabled);

        void BuildVacuum(VisualElement body)
        {
            var units = RunPreferences.VacuumOf(settings);

            Slider(body, "Drive speed", 0.05f, 2f, settings.driveSpeed, units.FormatSpeed, v => settings.driveSpeed = v);
            Slider(body, "Reverse speed", 0.05f, 1.5f, settings.reverseSpeed, units.FormatSpeed, v => settings.reverseSpeed = v);
            Slider(body, "Turn speed", 30f, 720f, settings.turnSpeed, v => $"{v:0} °/s", v => settings.turnSpeed = v);
            Slider(body, "Battery capacity", 10f, Battery.MaxCapacity, settings.batteryCapacity, VacuumSettings.FormatCapacity,
                v => settings.batteryCapacity = v);
        }

        void BuildCleaning(VisualElement body)
        {
            Slider(body, "Grid cell size", 0.0127f, 0.1524f, settings.cellSizeMeters, FormatCell, v => settings.cellSizeMeters = v);
            Slider(body, "Vacuum power", 0.1f, 5f, settings.vacuumPower, v => $"{v:0.##}", v => settings.vacuumPower = v);
            Slider(body, "Target cleanliness", 0.1f, 10f, settings.targetMaxClean, v => $"{v:0.##}", v => settings.targetMaxClean = v);
            Ui.Text(body, "A cell counts as fully clean once its cleanliness reaches the target. Power is cleanliness gained per second over the easiest floor.",
                "le-form__hint");

            Field(body, "Heatmap the run opens with");
            var modes = new List<string>();
            foreach (HeatmapMode mode in Enum.GetValues(typeof(HeatmapMode))) modes.Add(HeatmapDefaults.DisplayName(mode));
            var heatmap = new Segmented(modes, (int)settings.heatmapMode);
            heatmap.SelectionChanged += index =>
            {
                settings.heatmapMode = (HeatmapMode)index;
                Changed?.Invoke();
            };
            body.Add(heatmap);
        }

        static string FormatCell(float metres) => RunPreferences.UseFeet
            ? $"{metres * MetresToInches:0.##} in"
            : $"{metres * 100f:0.##} cm";

        static void Field(VisualElement parent, string label) => Ui.Text(parent, label, "le-field-label");

        void Slider(VisualElement parent, string label, float min, float max, float value, Func<float, string> format, Action<float> store)
        {
            Field(parent, label);
            var slider = new ValueSlider(min, max, Mathf.Clamp(value, min, max), format, true);
            slider.ValueChanged += next =>
            {
                store(next);
                Changed?.Invoke();
            };
            parent.Add(slider);
        }

        /// <summary>Redraws every control from the bound settings, e.g. after a reset or a change of units.</summary>
        public void Refresh() => Build();
    }
}
