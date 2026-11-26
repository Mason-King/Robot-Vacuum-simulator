using System.Collections.Generic;
using RobotVacuum.Level;
using RobotVacuum.Sim;
using RobotVacuum.Sim.Control;
using UnityEngine;

namespace RobotVacuum.LevelEditor
{
    /// <summary>
    /// The simulation configuration every screen shares: the movement algorithm, the seed policy, the grid
    /// and the vacuum's settings. The Configuration screen, the editor's preview run and the simulator HUD all
    /// read and write this one copy, and it is kept in PlayerPrefs so it survives quitting.
    /// </summary>
    public static class SimConfig
    {
        const string PrefsKey = "RobotVacuum.SimConfig";

        public const float MinCellSize = 0.0127f;   // ½ in
        public const float MaxCellSize = 0.1524f;   // 6 in
        public const float MinTelemetryInterval = 0.05f;
        public const float MaxTelemetryInterval = 10f;

        [System.Serializable]
        public sealed class FloorEffort
        {
            public string floor;
            public float effort;
        }

        [System.Serializable]
        public sealed class Data
        {
            public VacuumSettings vacuum = VacuumSettings.Defaults();
            public RunSettings runSettings = new RunSettings();

            /// <summary>The seed runs use when <see cref="randomSeed"/> is off.</summary>
            public int seed = 1;

            /// <summary>Draw a new seed for every run instead of reusing <see cref="seed"/>.</summary>
            public bool randomSeed = true;

            /// <summary>Coverage grid cell edge, in metres.</summary>
            public float cellSize = SimDefaults.CellSizeMeters;

            /// <summary>Simulated seconds between telemetry samples.</summary>
            public float telemetryInterval = 0.5f;

            /// <summary>
            /// The run time limit, in simulated seconds (1 to 86,400, the program's 24 h cap): every batch trial uses it;
            /// a single run only when <see cref="runLimitEnabled"/>. (Replaces the old maxRunMinutes, which is dropped.)
            /// </summary>
            public float runLimitSeconds = DefaultRunLimitSeconds;

            /// <summary>Single (graphics) runs stop at <see cref="runLimitSeconds"/>. Off by default: the battery is the usual end.</summary>
            public bool runLimitEnabled;

            /// <summary>Cleaning effort overrides, by floor covering name.</summary>
            public List<FloorEffort> floorEfforts = new List<FloorEffort>();

            public string ToJson() => JsonUtility.ToJson(this);

            /// <summary>Reads saved settings, falling back to defaults for anything missing or out of range.</summary>
            public static Data Parse(string json)
            {
                var data = new Data();
                if (!string.IsNullOrEmpty(json))
                {
                    try { JsonUtility.FromJsonOverwrite(json, data); }
                    catch (System.ArgumentException) { data = new Data(); }
                }

                if (data.vacuum == null || data.vacuum.driveSpeed <= 0f) data.vacuum = VacuumSettings.Defaults();
                data.runSettings ??= new RunSettings();
                if (AlgorithmCatalog.Find(data.runSettings.algorithmId) == null)
                    data.runSettings.algorithmId = AlgorithmCatalog.Entries[0].Id;
                data.cellSize = data.cellSize > 0f ? Mathf.Clamp(data.cellSize, MinCellSize, MaxCellSize) : SimDefaults.CellSizeMeters;
                data.telemetryInterval = data.telemetryInterval > 0f
                    ? Mathf.Clamp(data.telemetryInterval, MinTelemetryInterval, MaxTelemetryInterval) : 0.5f;
                data.runLimitSeconds = data.runLimitSeconds > 0f ? Mathf.Clamp(data.runLimitSeconds, 1f, MaxRunLimitSeconds) : DefaultRunLimitSeconds;
                data.floorEfforts ??= new List<FloorEffort>();
                data.floorEfforts.RemoveAll(entry => entry == null || string.IsNullOrEmpty(entry.floor));
                return data;
            }
        }

        static Data current;

        static Data Current => current ??= Load();

        /// <summary>The live settings. Change fields on it directly, then call <see cref="Save"/>.</summary>
        public static VacuumSettings Vacuum => Current.vacuum;

        /// <summary>The algorithm, heatmap and SLAM options shown on the setup screen.</summary>
        public static RunSettings RunSettings => Current.runSettings;

        /// <summary>Imperial (feet) or metric units for every length, area and speed in the interface.</summary>
        public static bool UseFeet
        {
            get => Current.vacuum.useFeet;
            set { Current.vacuum.useFeet = value; Save(); }
        }

        public static int Seed
        {
            get => Current.seed;
            set { Current.seed = value; Save(); }
        }

        public static bool RandomSeed
        {
            get => Current.randomSeed;
            set { Current.randomSeed = value; Save(); }
        }

        public static float CellSize
        {
            get => Current.cellSize;
            set { Current.cellSize = Mathf.Clamp(value, MinCellSize, MaxCellSize); Save(); }
        }

        public static float TelemetryInterval
        {
            get => Current.telemetryInterval;
            set { Current.telemetryInterval = Mathf.Clamp(value, MinTelemetryInterval, MaxTelemetryInterval); Save(); }
        }

        /// <summary>The run time limit's default: one simulated hour.</summary>
        public const float DefaultRunLimitSeconds = 3600f;

        /// <summary>The longest run time limit: the program's 24 h safety cap.</summary>
        public const float MaxRunLimitSeconds = SimDefaults.MaxSimulatedSeconds;

        /// <summary>The run time limit in simulated seconds (1 or more): always used by batches, by single runs when <see cref="RunLimitEnabled"/>.</summary>
        public static float RunLimitSeconds
        {
            get => Current.runLimitSeconds;
            set { Current.runLimitSeconds = Mathf.Clamp(value, 1f, MaxRunLimitSeconds); Save(); }
        }

        /// <summary>A time limit in seconds as h:mm:ss (or m:ss under an hour).</summary>
        public static string FormatLimit(float seconds)
        {
            int total = Mathf.Max(0, Mathf.RoundToInt(seconds));
            int h = total / 3600, m = total / 60 % 60, s = total % 60;
            return h > 0 ? $"{h}:{m:00}:{s:00}" : $"{m}:{s:00}";
        }

        /// <summary>Whether a single run stops at <see cref="RunLimitSeconds"/> (off by default).</summary>
        public static bool RunLimitEnabled
        {
            get => Current.runLimitEnabled;
            set { Current.runLimitEnabled = value; Save(); }
        }

        /// <summary>
        /// The settings a <see cref="SimulationSession"/> run is started with: this configuration's algorithm,
        /// seed policy, grid and vacuum. <paramref name="algorithmId"/> overrides the algorithm: any entry in
        /// <see cref="AlgorithmCatalog"/>, plugins included. A batch starts each trial this way.
        /// </summary>
        public static RunSettings ToRunSettings(string algorithmId = null, int? fixedSeed = null) => new RunSettings
        {
            randomSeed = !fixedSeed.HasValue && RandomSeed,
            seed = fixedSeed ?? Seed,
            cellSizeMeters = CellSize,
            algorithmId = !string.IsNullOrEmpty(algorithmId) ? algorithmId : RunSettings.algorithmId,
            heatmapMode = RunSettings.heatmapMode,
            slam = RunSettings.slam,
            showBeliefOverlay = RunSettings.showBeliefOverlay,
            slamLoopClosure = RunSettings.slamLoopClosure,
            slamCorrectionQuality = RunSettings.slamCorrectionQuality,
            driveSpeed = Vacuum.driveSpeed,
            reverseSpeed = Vacuum.reverseSpeed,
            turnSpeed = Vacuum.turnSpeed,
            batteryCapacity = Vacuum.batteryCapacity,
            shape = Vacuum.shape,
            maxSimulatedSeconds = RunLimitEnabled ? RunLimitSeconds : 0f, // 0: no limit of its own (the 24 h cap still applies)
        };

        /// <summary>The adjusted cleaning effort for a covering, or null when it uses the palette's value.</summary>
        public static float? CleaningEffortFor(string floor)
        {
            var entry = Current.floorEfforts.Find(e => e.floor == floor);
            return entry != null ? entry.effort : (float?)null;
        }

        public static void SetCleaningEffort(string floor, float? effort)
        {
            Current.floorEfforts.RemoveAll(e => e.floor == floor);
            if (effort.HasValue)
                Current.floorEfforts.Add(new FloorEffort
                {
                    floor = floor,
                    effort = Mathf.Clamp(effort.Value, FloorTuning.MinCleaningEffort, FloorTuning.MaxCleaningEffort),
                });
            ApplyFloorTuning();
            Save();
        }

        /// <summary>Pushes the saved cleaning-effort adjustments into the simulation.</summary>
        static void ApplyFloorTuning()
        {
            FloorTuning.ClearAll();
            foreach (var entry in Current.floorEfforts) FloorTuning.SetCleaningEffort(entry.floor, entry.effort);
        }

        /// <summary>The whole configuration as JSON, for saving next to a run's results.</summary>
        public static string ToJson() => Current.ToJson();

        /// <summary>Replaces the configuration with one saved earlier. Returns false if the text isn't one.</summary>
        public static bool TryImport(string json)
        {
            if (string.IsNullOrWhiteSpace(json) || !json.TrimStart().StartsWith("{") || !json.Contains("\"vacuum\"")) return false;
            current = Data.Parse(json);
            ApplyFloorTuning();
            Save();
            return true;
        }

        /// <summary>A one-line description for summaries, e.g. "Random · 0.65 m/s · 300 units".</summary>
        public static string Summary =>
            $"{AlgorithmCatalog.NameOf(RunSettings.algorithmId)} · {Vacuum.FormatSpeed(Vacuum.driveSpeed)} · {VacuumSettings.FormatCapacity(Vacuum.batteryCapacity)}";

        public static void Save() => PlayerPrefs.SetString(PrefsKey, Current.ToJson());

        public static void ResetToDefaults()
        {
            bool feet = Current.vacuum.useFeet;
            current = new Data();
            current.vacuum.useFeet = feet;
            ApplyFloorTuning();
            Save();
        }

        static Data Load()
        {
            var data = Data.Parse(PlayerPrefs.GetString(PrefsKey, null));

            FloorTuning.ClearAll();
            foreach (var entry in data.floorEfforts) FloorTuning.SetCleaningEffort(entry.floor, entry.effort);
            return data;
        }

        // Statics survive entering Play Mode when domain reload is off.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => current = null;
    }
}
