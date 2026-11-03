using System.Collections.Generic;
using RobotVacuum.Level;
using RobotVacuum.Sim;
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
            public MovementPattern pattern = MovementPattern.RandomBounce;
            public VacuumSettings vacuum = VacuumSettings.Defaults();

            /// <summary>The seed runs use when <see cref="randomSeed"/> is off.</summary>
            public int seed = 1;

            /// <summary>Draw a new seed for every run instead of reusing <see cref="seed"/>.</summary>
            public bool randomSeed = true;

            /// <summary>Coverage grid cell edge, in metres.</summary>
            public float cellSize = SimDefaults.CellSizeMeters;

            /// <summary>Simulated seconds between telemetry samples.</summary>
            public float telemetryInterval = 0.5f;

            /// <summary>A batch trial ends after this many simulated minutes if the battery hasn't run out.</summary>
            public float maxRunMinutes = 30f;

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

                if (!MovementAlgorithms.IsDefined(data.pattern)) data.pattern = MovementPattern.RandomBounce;
                if (data.vacuum == null || data.vacuum.driveSpeed <= 0f) data.vacuum = VacuumSettings.Defaults();
                data.cellSize = data.cellSize > 0f ? Mathf.Clamp(data.cellSize, MinCellSize, MaxCellSize) : SimDefaults.CellSizeMeters;
                data.telemetryInterval = data.telemetryInterval > 0f
                    ? Mathf.Clamp(data.telemetryInterval, MinTelemetryInterval, MaxTelemetryInterval) : 0.5f;
                data.maxRunMinutes = data.maxRunMinutes > 0f ? Mathf.Clamp(data.maxRunMinutes, 1f, 240f) : 30f;
                data.floorEfforts ??= new List<FloorEffort>();
                data.floorEfforts.RemoveAll(entry => entry == null || string.IsNullOrEmpty(entry.floor));
                return data;
            }
        }

        static Data current;

        static Data Current => current ??= Load();

        public static MovementPattern Pattern
        {
            get => Current.pattern;
            set { Current.pattern = value; Save(); }
        }

        /// <summary>The live settings. Change fields on it directly, then call <see cref="Save"/>.</summary>
        public static VacuumSettings Vacuum => Current.vacuum;

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

        public static float MaxRunMinutes
        {
            get => Current.maxRunMinutes;
            set { Current.maxRunMinutes = Mathf.Clamp(value, 1f, 240f); Save(); }
        }

        /// <summary>
        /// The settings a <see cref="SimulationSession"/> run is started with: this configuration's algorithm,
        /// seed policy, grid and vacuum. <paramref name="pattern"/> overrides the algorithm, e.g. for a batch.
        /// </summary>
        public static RunSettings ToRunSettings(MovementPattern? pattern = null, int? fixedSeed = null) => new RunSettings
        {
            pattern = pattern ?? Pattern,
            randomSeed = !fixedSeed.HasValue && RandomSeed,
            seed = fixedSeed ?? Seed,
            cellSizeMeters = CellSize,
            driveSpeed = Vacuum.driveSpeed,
            reverseSpeed = Vacuum.reverseSpeed,
            turnSpeed = Vacuum.turnSpeed,
            batteryCapacity = Vacuum.batteryCapacity,
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

        /// <summary>A one-line description for summaries, e.g. "Spiral · 0.65 m/s · 300 units".</summary>
        public static string Summary =>
            $"{MovementAlgorithms.LabelOf(Pattern)} · {Vacuum.FormatSpeed(Vacuum.driveSpeed)} · {VacuumSettings.FormatCapacity(Vacuum.batteryCapacity)}";

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
