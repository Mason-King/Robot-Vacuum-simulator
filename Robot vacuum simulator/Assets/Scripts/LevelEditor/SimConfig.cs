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
            Replace(Data.Parse(json), "Load configuration");
            CommitVersion("Loaded from a file");
            return true;
        }

        /// <summary>A one-line description for summaries, e.g. "Random · 0.65 m/s · 300 units".</summary>
        public static string Summary =>
            $"{AlgorithmCatalog.NameOf(RunSettings.algorithmId)} · {Vacuum.FormatSpeed(Vacuum.driveSpeed)} · {VacuumSettings.FormatCapacity(Vacuum.batteryCapacity)}";

        /// <summary>
        /// Stores the settings (after changing fields on them directly) and makes the change undoable. Changes to the
        /// same setting less than <see cref="MergeSeconds"/> apart (a slider being dragged) are one undo step.
        /// </summary>
        public static void Save()
        {
            string now = Current.ToJson();
            PlayerPrefs.SetString(PrefsKey, now);
            if (lastState == null || now == lastState)
            {
                lastState = now;
                return;
            }

            string label = ConfigDiff.Label(Data.Parse(lastState), current);
            double time = Clock();
            bool merge = label == lastPushLabel && time - lastPushTime < MergeSeconds;
            if (!merge)
            {
                undo.Push(label, lastState);
                editLog.Add(label);
            }
            lastPushLabel = label;
            lastPushTime = time;
            lastState = now;
            Edited?.Invoke();
        }

        public static void ResetToDefaults()
        {
            var defaults = new Data();
            defaults.vacuum.useFeet = Current.vacuum.useFeet;
            Replace(defaults, "Reset to defaults");
            CommitVersion("Reset to defaults");
        }

        static Data Load()
        {
            var data = Data.Parse(PlayerPrefs.GetString(PrefsKey, null));

            FloorTuning.ClearAll();
            foreach (var entry in data.floorEfforts) FloorTuning.SetCleaningEffort(entry.floor, entry.effort);
            lastState = loadedState = data.ToJson();
            if (!quitHooked)
            {
                // Whatever changed since the last version is kept when the program closes.
                quitHooked = true;
                Application.quitting += () => CommitVersion("When the program closed");
            }
            return data;
        }

        static bool quitHooked;

        // ---- Undo/redo and version history ----------------------------------------------------------------------
        //
        // Undo/redo covers every change made through Save (the Configuration screen, the run setup, the simulator's
        // bar) for as long as the program runs. Versions are the lasting record: one is committed when the user leaves
        // the Configuration screen, saves a version, loads or resets, and before a run starts, if anything changed.

        /// <summary>Changes to the same setting closer together than this, real seconds, are one undo step.</summary>
        public const double MergeSeconds = 1.0;

        static readonly EditHistory undo = new EditHistory();
        static readonly List<string> editLog = new List<string>();
        static string lastState;    // the settings as last stored: the "before" of the next change
        static string loadedState;  // as loaded at startup: the first version, if there was none
        static string lastPushLabel;
        static double lastPushTime = double.NegativeInfinity;
        static VersionHistory versions;

        /// <summary>Real time for merging changes, seconds. Tests replace it.</summary>
        internal static System.Func<double> Clock = () => Time.realtimeSinceStartupAsDouble;

        /// <summary>Any change, including each step of a slider drag: refresh undo/redo state, not the controls.</summary>
        public static event System.Action Edited;

        /// <summary>
        /// The settings were replaced from outside the controls showing them (undo, redo, a restored version, a loaded
        /// file, a reset): screens showing them should rebuild.
        /// </summary>
        public static event System.Action Replaced;

        public static bool CanUndo => undo.CanUndo;
        public static bool CanRedo => undo.CanRedo;
        public static string UndoLabel => undo.UndoLabel;
        public static string RedoLabel => undo.RedoLabel;

        public static bool Undo() => Step(true);
        public static bool Redo() => Step(false);

        static bool Step(bool back)
        {
            string now = Current.ToJson();
            string state, label;
            bool stepped = back ? undo.TryUndo(now, out state, out label) : undo.TryRedo(now, out state, out label);
            if (!stepped) return false;
            Adopt(Data.Parse(state));
            ApplyFloorTuning();
            lastState = current.ToJson();
            PlayerPrefs.SetString(PrefsKey, lastState);
            editLog.Add($"{(back ? "Undo" : "Redo")} {label}");
            lastPushLabel = null;
            Edited?.Invoke();
            Replaced?.Invoke();
            return true;
        }

        /// <summary>Replaces all the settings as one undoable step.</summary>
        static void Replace(Data next, string label)
        {
            string before = Current.ToJson();
            Adopt(next);
            ApplyFloorTuning();
            string now = current.ToJson();
            PlayerPrefs.SetString(PrefsKey, now);
            lastState = now;
            lastPushLabel = null;
            if (now == before) return;
            undo.Push(label, before);
            editLog.Add(label);
            Edited?.Invoke();
            Replaced?.Invoke();
        }

        /// <summary>
        /// Takes on <paramref name="next"/>'s values while keeping the objects screens hold (Vacuum, RunSettings, the
        /// effort list): nested settings are overwritten in place, not swapped, so a screen built before an undo or a
        /// restore still edits the live settings.
        /// </summary>
        static void Adopt(Data next)
        {
            if (current == null)
            {
                current = next;
                return;
            }
            foreach (var field in typeof(Data).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            {
                object mine = field.GetValue(current), theirs = field.GetValue(next);
                if (mine is System.Collections.IList list && theirs is System.Collections.IList items)
                {
                    list.Clear();
                    foreach (object item in items) list.Add(item);
                }
                else if (mine != null && theirs != null && !field.FieldType.IsValueType && field.FieldType != typeof(string))
                    JsonUtility.FromJsonOverwrite(JsonUtility.ToJson(theirs), mine);
                else field.SetValue(current, theirs);
            }
        }

        /// <summary>Every saved version of the configuration.</summary>
        public static VersionHistory Versions => versions ??= VersionHistory.ForConfiguration();

        /// <summary>The changes since the last version, repeats folded: what the next version will list.</summary>
        public static List<string> UnversionedEdits => VersionHistory.Summarise(editLog);

        /// <summary>The settings as a version stores them (indented JSON).</summary>
        public static string VersionContent => JsonUtility.ToJson(Current, true);

        /// <summary>
        /// Records the settings as a new version unless they equal the latest one. The first time, the settings as
        /// they were at startup go in first, so the first change can be compared. A history that can't be written is
        /// logged, not thrown. Returns the version, or null.
        /// </summary>
        public static VersionInfo CommitVersion(string label)
        {
            try
            {
                string content = VersionContent;
                if (Versions.Latest == null && loadedState != null && loadedState != Current.ToJson())
                    Versions.Record(JsonUtility.ToJson(Data.Parse(loadedState), true), "Starting configuration");
                var version = Versions.Record(content, label, UnversionedEdits);
                if (version != null) editLog.Clear();
                return version;
            }
            catch (System.Exception exception)
            {
                Debug.LogWarning($"Version history: could not record a version of the configuration: {exception.Message}");
                return null;
            }
        }

        /// <summary>Brings back version <paramref name="number"/> as one undoable change. False if it can't be read.</summary>
        public static bool RestoreVersion(int number)
        {
            string json = Versions.Read(number);
            if (string.IsNullOrEmpty(json)) return false;
            Replace(Data.Parse(json), $"Restore v{number}");
            return true;
        }

        // Statics survive entering Play Mode when domain reload is off.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            current = null;
            undo.Clear();
            editLog.Clear();
            lastState = loadedState = lastPushLabel = null;
            lastPushTime = double.NegativeInfinity;
            versions = null;
        }

        /// <summary>Tests: forget everything held in memory; the next read loads from PlayerPrefs again.</summary>
        internal static void ResetForTests() => ResetStatics();
    }
}
