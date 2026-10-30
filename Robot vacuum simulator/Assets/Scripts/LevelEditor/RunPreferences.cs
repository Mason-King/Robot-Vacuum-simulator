using System;
using RobotVacuum.Sim;
using UnityEngine;

namespace RobotVacuum.LevelEditor
{
    /// <summary>How a run is played: drawn and watched, or headless (nothing drawn, as fast as possible).</summary>
    public enum RunMode { Watch, Headless }

    /// <summary>
    /// The two layers of run settings. <see cref="Global"/> holds the defaults every run starts from and is
    /// kept in PlayerPrefs. <see cref="Run"/> is what the next run uses: the global settings, unless they
    /// have been customized for this run on the Run setup page or in the editor. Customizations last until
    /// <see cref="ResetRunToGlobal"/> or the app quits, so they survive moving between the floor plans, the
    /// editor and the simulator.
    /// </summary>
    public static class RunPreferences
    {
        const string GlobalKey = "RobotVacuum.GlobalRunSettings";
        const string UnitsKey = "RobotVacuum.UseFeet";

        static RunSettings global;
        static RunSettings run;
        static bool customized;

        /// <summary>Raised after the global or per-run settings change.</summary>
        public static event Action Changed;

        /// <summary>The defaults every run starts from. Change fields directly, then call <see cref="SaveGlobal"/>.</summary>
        public static RunSettings Global => global ??= LoadGlobal();

        /// <summary>The settings the next run uses. Editing them marks the run as customized.</summary>
        public static RunSettings Run
        {
            get
            {
                // One object for the whole session, so a form bound to it keeps editing the right thing.
                // Until it is customized it simply mirrors the global settings.
                if (run == null) run = Global.Copy();
                else if (!customized) JsonUtility.FromJsonOverwrite(JsonUtility.ToJson(Global), run);
                return run;
            }
        }

        /// <summary>True when the next run differs from the global settings.</summary>
        public static bool IsRunCustomized => customized;

        /// <summary>Imperial (feet) or metric units across the interface.</summary>
        public static bool UseFeet
        {
            get => PlayerPrefs.GetInt(UnitsKey, 1) == 1;
            set
            {
                PlayerPrefs.SetInt(UnitsKey, value ? 1 : 0);
                Changed?.Invoke();
            }
        }

        /// <summary>Call after changing a field of <see cref="Run"/>: the next run now has its own settings.</summary>
        public static void MarkRunCustomized()
        {
            customized = true;
            Changed?.Invoke();
        }

        /// <summary>Puts the next run back on the global settings, in place, so bound forms just refresh.</summary>
        public static void ResetRunToGlobal()
        {
            customized = false;
            _ = Run;
            Changed?.Invoke();
        }

        public static void SaveGlobal()
        {
            PlayerPrefs.SetString(GlobalKey, JsonUtility.ToJson(Global));
            Changed?.Invoke();
        }

        public static void ResetGlobalToDefaults()
        {
            global = Defaults();
            SaveGlobal();
        }

        /// <summary>
        /// The built-in defaults with every value concrete, so each control has something to show: the
        /// robot's and battery's own defaults, and the shared simulation defaults.
        /// </summary>
        public static RunSettings Defaults() => new RunSettings
        {
            pattern = MovementPattern.RandomBounce,
            randomSeed = true,
            seed = 1,
            targetMaxClean = HeatmapDefaults.TargetMaxClean,
            heatmapMode = HeatmapMode.FloorCleanliness,
            driveSpeed = 0.65f,
            reverseSpeed = 0.35f,
            turnSpeed = 320f,
            batteryCapacity = 300f,
            cellSizeMeters = SimDefaults.CellSizeMeters,
            vacuumPower = HeatmapDefaults.BaseVacuumPower,
        };

        /// <summary>The vacuum part of <paramref name="settings"/>, for read-outs and the editor's preview run.</summary>
        public static VacuumSettings VacuumOf(RunSettings settings) => new VacuumSettings
        {
            driveSpeed = settings.driveSpeed,
            reverseSpeed = settings.reverseSpeed,
            turnSpeed = settings.turnSpeed,
            batteryCapacity = settings.batteryCapacity,
            useFeet = UseFeet,
        };

        static RunSettings LoadGlobal()
        {
            var loaded = Defaults();
            string json = PlayerPrefs.GetString(GlobalKey, null);
            if (!string.IsNullOrEmpty(json))
            {
                try { JsonUtility.FromJsonOverwrite(json, loaded); }
                catch (ArgumentException) { loaded = Defaults(); }
            }

            // Anything missing or out of range falls back to its default, so a stale save can't break a run.
            var fallback = Defaults();
            if (!Enum.IsDefined(typeof(MovementPattern), loaded.pattern)) loaded.pattern = fallback.pattern;
            if (!Enum.IsDefined(typeof(HeatmapMode), loaded.heatmapMode)) loaded.heatmapMode = fallback.heatmapMode;
            if (!RunSettings.IsSet(loaded.targetMaxClean)) loaded.targetMaxClean = fallback.targetMaxClean;
            if (!RunSettings.IsSet(loaded.driveSpeed)) loaded.driveSpeed = fallback.driveSpeed;
            if (!RunSettings.IsSet(loaded.reverseSpeed)) loaded.reverseSpeed = fallback.reverseSpeed;
            if (!RunSettings.IsSet(loaded.turnSpeed)) loaded.turnSpeed = fallback.turnSpeed;
            if (!RunSettings.IsSet(loaded.batteryCapacity)) loaded.batteryCapacity = fallback.batteryCapacity;
            if (!RunSettings.IsSet(loaded.cellSizeMeters)) loaded.cellSizeMeters = fallback.cellSizeMeters;
            if (!RunSettings.IsSet(loaded.vacuumPower)) loaded.vacuumPower = fallback.vacuumPower;
            return loaded;
        }

        // Statics survive entering Play Mode when domain reload is off.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            global = null;
            run = null;
            customized = false;
            Changed = null;
        }
    }
}
