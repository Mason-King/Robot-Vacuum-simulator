using System;
using System.Collections.Generic;
using RobotVacuum.LevelEditor;
using RobotVacuum.Sim;
using UnityEngine;
using UnityEngine.UI;

namespace RobotVacuum.UI
{
    /// <summary>
    /// Every run setting as one form, bound to a <see cref="RunSettings"/> it edits in place: the global
    /// defaults on the Settings page, the next run on a Run setup page. The controls live in the
    /// RunSettingsForm prefab; this only fills them in and writes changes back.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class RunSettingsFormView : MonoBehaviour
    {
        const float MetresToInches = 39.3701f;

        [Header("Movement")]
        [SerializeField] SegmentedControl algorithm;
        [SerializeField] SwitchField randomSeed;
        [SerializeField] InputField seed;

        [Header("Vacuum")]
        [SerializeField] SliderField driveSpeed;
        [SerializeField] SliderField reverseSpeed;
        [SerializeField] SliderField turnSpeed;
        [SerializeField] SliderField batteryCapacity;

        [Header("Cleaning")]
        [SerializeField] SliderField cellSize;
        [SerializeField] SliderField vacuumPower;
        [SerializeField] SliderField targetMaxClean;
        [SerializeField] SegmentedControl heatmap;

        RunSettings settings;
        bool hooked;

        /// <summary>Raised after any value changes.</summary>
        public event Action Changed;

        /// <summary>Shows and edits <paramref name="bound"/> from now on.</summary>
        public void Bind(RunSettings bound)
        {
            settings = bound;
            Hook();
            Refresh();
        }

        /// <summary>Redraws every control from the bound settings, e.g. after a reset or a change of units.</summary>
        public void Refresh()
        {
            if (settings == null) return;

            algorithm?.SetLabels(MovementBrain.Labels);
            algorithm?.SetSelectedWithoutNotify((int)settings.pattern);
            randomSeed?.SetValueWithoutNotify(settings.randomSeed);
            if (seed != null)
            {
                seed.SetTextWithoutNotify(settings.seed.ToString());
                seed.interactable = !settings.randomSeed;
            }

            var units = RunPreferences.VacuumOf(settings);
            driveSpeed?.Configure("Drive speed", 0.05f, 2f, settings.driveSpeed, units.FormatSpeed);
            reverseSpeed?.Configure("Reverse speed", 0.05f, 1.5f, settings.reverseSpeed, units.FormatSpeed);
            turnSpeed?.Configure("Turn speed", 30f, 720f, settings.turnSpeed, v => $"{v:0} °/s");
            batteryCapacity?.Configure("Battery capacity", 10f, Battery.MaxCapacity, settings.batteryCapacity, VacuumSettings.FormatCapacity);

            cellSize?.Configure("Grid cell size", 0.0127f, 0.1524f, settings.cellSizeMeters, FormatCell);
            vacuumPower?.Configure("Vacuum power", 0.1f, 5f, settings.vacuumPower, v => $"{v:0.##}");
            targetMaxClean?.Configure("Target cleanliness", 0.1f, 10f, settings.targetMaxClean, v => $"{v:0.##}");

            if (heatmap != null)
            {
                var names = new List<string>();
                foreach (HeatmapMode mode in Enum.GetValues(typeof(HeatmapMode))) names.Add(HeatmapDefaults.DisplayName(mode));
                heatmap.SetLabels(names);
                heatmap.SetSelectedWithoutNotify((int)settings.heatmapMode);
            }
        }

        void Hook()
        {
            if (hooked) return;
            hooked = true;

            if (algorithm != null) algorithm.SelectionChanged += i => Set(() => settings.pattern = (MovementPattern)i);
            if (randomSeed != null) randomSeed.ValueChanged += on => Set(() =>
            {
                settings.randomSeed = on;
                if (seed != null) seed.interactable = !on;
            });
            if (seed != null) seed.onEndEdit.AddListener(text =>
            {
                if (settings != null && int.TryParse(text, out int value)) Set(() => settings.seed = value);
                if (settings != null) seed.SetTextWithoutNotify(settings.seed.ToString());
            });

            Slider(driveSpeed, v => settings.driveSpeed = v);
            Slider(reverseSpeed, v => settings.reverseSpeed = v);
            Slider(turnSpeed, v => settings.turnSpeed = v);
            Slider(batteryCapacity, v => settings.batteryCapacity = v);
            Slider(cellSize, v => settings.cellSizeMeters = v);
            Slider(vacuumPower, v => settings.vacuumPower = v);
            Slider(targetMaxClean, v => settings.targetMaxClean = v);

            if (heatmap != null) heatmap.SelectionChanged += i => Set(() => settings.heatmapMode = (HeatmapMode)i);
        }

        void Slider(SliderField field, Action<float> store)
        {
            if (field != null) field.ValueChanged += v => Set(() => store(v));
        }

        void Set(Action apply)
        {
            if (settings == null) return;
            apply();
            Changed?.Invoke();
        }

        static string FormatCell(float metres) => RunPreferences.UseFeet
            ? $"{metres * MetresToInches:0.##} in"
            : $"{metres * 100f:0.##} cm";
    }
}
