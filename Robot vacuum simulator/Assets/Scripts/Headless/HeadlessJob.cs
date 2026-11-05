using System;
using System.Collections.Generic;
using System.IO;
using RobotVacuum.Level;
using RobotVacuum.LevelEditor;
using RobotVacuum.Sim;
using UnityEngine;

namespace RobotVacuum.Headless
{
    /// <summary>
    /// A headless job file (SDD 2.1 "Job contents"): everything a node needs to run part or all of an
    /// experiment campaign with no GUI. The same file is what the vacuumctl CLI uploads to AWS, what the
    /// container hands to this build with -configPath, and what a local headless run reads.
    ///
    /// The format is documented in cloud/docs/job-format.md. All lengths are metres, speeds m/s, turn speed
    /// degrees/s, battery in work units, times in simulated seconds. Any field left out takes the default
    /// shown on it, which matches the GUI's defaults.
    ///
    /// Runs are either listed explicitly in <see cref="runs"/> (the CLI always does this) or expanded from
    /// <see cref="sweep"/> in a fixed order: floor plan, then configuration, then algorithm, then seed.
    /// </summary>
    [Serializable]
    public sealed class HeadlessJob
    {
        public const int CurrentSchemaVersion = 1;

        public int schemaVersion = CurrentSchemaVersion;

        /// <summary>Unique ID for this campaign; every run and the results carry it. Made up if missing.</summary>
        public string campaignId;
        public string campaignName = "Campaign";
        public string description;

        /// <summary>The floor coverings rooms may use. Empty = the app's stock six coverings.</summary>
        public List<FloorTypeSpec> floorTypes = new List<FloorTypeSpec>();

        /// <summary>Floor plans in the declarative format of Docs/floor-plan-format.md (2.13).</summary>
        public List<LevelSnapshot> floorPlans = new List<LevelSnapshot>();

        /// <summary>Named vacuum + simulation configurations. Empty = one configuration with every default.</summary>
        public List<RunConfiguration> configurations = new List<RunConfiguration>();

        public SweepSpec sweep = new SweepSpec();

        /// <summary>The individual runs. Empty = expand <see cref="sweep"/>.</summary>
        public List<RunSpec> runs = new List<RunSpec>();

        public ExecutionSpec execution = new ExecutionSpec();

        // ---------------------------------------------------------------- reading

        public static HeadlessJob Load(string path, out List<string> errors)
        {
            errors = new List<string>();
            if (!File.Exists(path))
            {
                errors.Add($"Job file not found: {path}");
                return null;
            }
            return Parse(File.ReadAllText(path), out errors);
        }

        /// <summary>Parses, fills defaults, expands the sweep and validates. Null (with reasons) if unusable.</summary>
        public static HeadlessJob Parse(string json, out List<string> errors)
        {
            errors = new List<string>();
            if (string.IsNullOrWhiteSpace(json))
            {
                errors.Add("The job file is empty.");
                return null;
            }

            HeadlessJob job;
            try
            {
                job = JsonUtility.FromJson<HeadlessJob>(json);
            }
            catch (ArgumentException ex)
            {
                errors.Add($"The job file is not valid JSON: {ex.Message}");
                return null;
            }

            if (job == null)
            {
                errors.Add("The job file could not be read.");
                return null;
            }

            job.Normalise();
            errors = job.Validate();
            return errors.Count == 0 ? job : null;
        }

        public string ToJson(bool pretty = true) => JsonUtility.ToJson(this, pretty);

        // ---------------------------------------------------------------- defaults

        void Normalise()
        {
            if (string.IsNullOrWhiteSpace(campaignId)) campaignId = Guid.NewGuid().ToString("N");
            if (string.IsNullOrWhiteSpace(campaignName)) campaignName = "Campaign";
            floorTypes ??= new List<FloorTypeSpec>();
            floorPlans ??= new List<LevelSnapshot>();
            configurations ??= new List<RunConfiguration>();
            sweep ??= new SweepSpec();
            runs ??= new List<RunSpec>();
            execution ??= new ExecutionSpec();

            if (configurations.Count == 0) configurations.Add(new RunConfiguration());
            for (int i = 0; i < configurations.Count; i++)
            {
                var config = configurations[i] ??= new RunConfiguration();
                if (string.IsNullOrWhiteSpace(config.name)) config.name = $"config-{i}";
                config.vacuum ??= new VacuumSpec();
                config.battery ??= new BatterySpec();
                config.simulation ??= new SimulationSpec();
                config.floorEfforts ??= new List<SimConfig.FloorEffort>();
            }

            // LevelSnapshot.Parse sanitises (missing lists, wall flags per edge, minimum sizes), exactly as an
            // imported floor plan file is treated in the GUI.
            for (int i = 0; i < floorPlans.Count; i++)
                if (floorPlans[i] != null) floorPlans[i] = LevelSnapshot.Parse(floorPlans[i].ToJson(false));

            if (runs.Count == 0) runs = ExpandSweep(out _);
        }

        /// <summary>Every run the sweep describes, in the documented order. Errors name unknown filters.</summary>
        public List<RunSpec> ExpandSweep(out List<string> errors)
        {
            errors = new List<string>();
            var expanded = new List<RunSpec>();

            var planIndices = Select(sweep.floorPlans, floorPlans.Count, i => floorPlans[i]?.name, "floor plan", errors);
            var configIndices = Select(sweep.configurations, configurations.Count, i => configurations[i]?.name, "configuration", errors);

            var algorithms = sweep.algorithms != null && sweep.algorithms.Count > 0
                ? sweep.algorithms
                : new List<string> { MovementPattern.RandomBounce.ToString() };

            var seeds = new List<int>();
            if (sweep.seeds != null && sweep.seeds.Count > 0) seeds.AddRange(sweep.seeds);
            else for (int s = 0; s < Mathf.Max(1, sweep.seedCount); s++) seeds.Add(sweep.seedStart + s);

            int index = 0;
            foreach (int plan in planIndices)
            foreach (int config in configIndices)
            foreach (string algorithm in algorithms)
            foreach (int seed in seeds)
                expanded.Add(new RunSpec { runIndex = index++, floorPlan = plan, configuration = config, algorithm = algorithm, seed = seed });

            return expanded;
        }

        static List<int> Select(List<string> names, int count, Func<int, string> nameOf, string what, List<string> errors)
        {
            var picked = new List<int>();
            if (names == null || names.Count == 0)
            {
                for (int i = 0; i < count; i++) picked.Add(i);
                return picked;
            }

            foreach (string name in names)
            {
                int found = -1;
                for (int i = 0; i < count; i++)
                    if (string.Equals(nameOf(i), name, StringComparison.OrdinalIgnoreCase)) { found = i; break; }

                if (found < 0) errors.Add($"sweep: no {what} named \"{name}\".");
                else picked.Add(found);
            }
            return picked;
        }

        // ---------------------------------------------------------------- validation (4.8, C.10)

        /// <summary>
        /// Job-wide checks. Problems confined to one run (a floor plan whose furniture overlaps a wall, say)
        /// are left to that run, so one bad run never stops the rest of the batch (4.5).
        /// </summary>
        public List<string> Validate()
        {
            var errors = new List<string>();

            if (schemaVersion != CurrentSchemaVersion)
                errors.Add($"schemaVersion {schemaVersion} is not supported (this build reads {CurrentSchemaVersion}).");

            // Floor types
            var floorNames = new List<string>();
            for (int i = 0; i < floorTypes.Count; i++)
            {
                var floor = floorTypes[i];
                if (floor == null || string.IsNullOrWhiteSpace(floor.name)) { errors.Add($"floorTypes[{i}]: needs a name."); continue; }
                if (floorNames.Exists(n => string.Equals(n, floor.name, StringComparison.OrdinalIgnoreCase)))
                    errors.Add($"floorTypes[{i}]: the name \"{floor.name}\" is used twice.");
                floorNames.Add(floor.name);
                Range(errors, $"floorTypes[{i}].speedMultiplier", floor.speedMultiplier, 0.1f, 2f);
                Range(errors, $"floorTypes[{i}].energyCostMultiplier", floor.energyCostMultiplier, 0.1f, 4f);
                Range(errors, $"floorTypes[{i}].cleaningEffort", floor.cleaningEffort, FloorTuning.MinCleaningEffort, FloorTuning.MaxCleaningEffort);
                Range(errors, $"floorTypes[{i}].soilingRate", floor.soilingRate, 0f, 4f);
                Range(errors, $"floorTypes[{i}].stepHeight", floor.stepHeight, 0f, 0.1f);
            }
            if (floorNames.Count == 0) floorNames.AddRange(StockFloorNames);

            // Floor plans
            if (floorPlans.Count == 0) errors.Add("floorPlans: at least one floor plan is needed.");
            for (int i = 0; i < floorPlans.Count; i++)
            {
                var plan = floorPlans[i];
                if (plan == null || plan.ValidRoomCount == 0) { errors.Add($"floorPlans[{i}]: needs at least one room with 3 or more corners."); continue; }

                for (int r = 0; r < plan.rooms.Count; r++)
                {
                    var room = plan.rooms[r];
                    if (room == null) continue;
                    if (!string.IsNullOrWhiteSpace(room.floor))
                    {
                        if (!floorNames.Exists(n => string.Equals(n, room.floor.Trim(), StringComparison.OrdinalIgnoreCase)))
                            errors.Add($"floorPlans[{i}] \"{plan.name}\" room \"{room.name}\": unknown floor \"{room.floor}\". Known: {string.Join(", ", floorNames)}.");
                    }
                    else if (room.floorIndex < 0 || room.floorIndex >= floorNames.Count)
                        errors.Add($"floorPlans[{i}] \"{plan.name}\" room \"{room.name}\": floorIndex {room.floorIndex} is out of range 0-{floorNames.Count - 1}.");
                }

                foreach (var doorway in plan.doorways)
                    if (doorway != null && (doorway.roomA >= plan.rooms.Count || doorway.roomB >= plan.rooms.Count))
                        errors.Add($"floorPlans[{i}] \"{plan.name}\": a doorway links a room index that doesn't exist.");
            }

            // Configurations
            var configNames = new List<string>();
            for (int i = 0; i < configurations.Count; i++)
            {
                var c = configurations[i];
                string at = $"configurations[{i}] \"{c.name}\"";
                if (configNames.Exists(n => string.Equals(n, c.name, StringComparison.OrdinalIgnoreCase)))
                    errors.Add($"{at}: the name is used twice.");
                configNames.Add(c.name);

                Range(errors, $"{at}.vacuum.driveSpeed", c.vacuum.driveSpeed, 0.01f, 5f);
                Range(errors, $"{at}.vacuum.reverseSpeed", c.vacuum.reverseSpeed, 0.01f, 5f);
                Range(errors, $"{at}.vacuum.turnSpeed", c.vacuum.turnSpeed, 1f, 2000f);
                Range(errors, $"{at}.vacuum.vacuumPower", c.vacuum.vacuumPower, 0.01f, 100f);

                Range(errors, $"{at}.battery.capacity", c.battery.capacity, 1f, Battery.MaxCapacity);
                Range(errors, $"{at}.battery.distanceEnergyPerMeter", c.battery.distanceEnergyPerMeter, 0f, 1000f);
                Range(errors, $"{at}.battery.turnEnergyPerDegree", c.battery.turnEnergyPerDegree, 0f, 100f);
                Range(errors, $"{at}.battery.operatingEnergyPerSecond", c.battery.operatingEnergyPerSecond, 0f, 1000f);
                Range(errors, $"{at}.battery.stalledEnergyPerSecond", c.battery.stalledEnergyPerSecond, 0f, 1000f);

                Range(errors, $"{at}.simulation.cellSizeMeters", c.simulation.cellSizeMeters, SimConfig.MinCellSize, SimConfig.MaxCellSize);
                Range(errors, $"{at}.simulation.targetMaxClean", c.simulation.targetMaxClean, 0.01f, 1000f);
                Range(errors, $"{at}.simulation.maxSimulatedSeconds", c.simulation.maxSimulatedSeconds, 1f, SimDefaults.MaxSimulatedSeconds);
                Range(errors, $"{at}.simulation.telemetryInterval", c.simulation.telemetryInterval, SimConfig.MinTelemetryInterval, SimConfig.MaxTelemetryInterval);
                if (!c.simulation.TryHeatmapMode(out _))
                    errors.Add($"{at}.simulation.heatmapMode: \"{c.simulation.heatmapMode}\" is not one of FloorCleanliness, FloorCoverage, FloorOverCoverage.");

                foreach (var effort in c.floorEfforts)
                {
                    if (effort == null || string.IsNullOrWhiteSpace(effort.floor)) { errors.Add($"{at}.floorEfforts: every entry needs a floor name."); continue; }
                    if (!floorNames.Exists(n => string.Equals(n, effort.floor, StringComparison.OrdinalIgnoreCase)))
                        errors.Add($"{at}.floorEfforts: unknown floor \"{effort.floor}\".");
                    Range(errors, $"{at}.floorEfforts[{effort.floor}]", effort.effort, FloorTuning.MinCleaningEffort, FloorTuning.MaxCleaningEffort);
                }
            }

            // Sweep filters that name a floor plan or configuration that doesn't exist
            ExpandSweep(out var sweepErrors);
            errors.AddRange(sweepErrors);

            // Runs
            if (runs.Count == 0) errors.Add("runs: nothing to run (no runs listed and the sweep is empty).");
            var seen = new HashSet<int>();
            foreach (var run in runs)
            {
                if (run == null) { errors.Add("runs: an entry is empty."); continue; }
                if (!seen.Add(run.runIndex)) errors.Add($"runs: runIndex {run.runIndex} appears twice.");
                if (run.runIndex < 0) errors.Add($"runs: runIndex {run.runIndex} is negative.");
                if (run.floorPlan < 0 || run.floorPlan >= floorPlans.Count) errors.Add($"run {run.runIndex}: floorPlan {run.floorPlan} is out of range.");
                if (run.configuration < 0 || run.configuration >= configurations.Count) errors.Add($"run {run.runIndex}: configuration {run.configuration} is out of range.");
                if (!run.TryPattern(out _)) errors.Add($"run {run.runIndex}: unknown algorithm \"{run.algorithm}\". Known: {string.Join(", ", Enum.GetNames(typeof(MovementPattern)))}.");
            }

            // Execution
            if (execution.shardCount < 1 || execution.shardCount > 10000) errors.Add("execution.shardCount must be 1-10000.");
            if (execution.reportEveryNRuns < 1) errors.Add("execution.reportEveryNRuns must be at least 1.");
            if (execution.maxRealSecondsPerRun < 0f) errors.Add("execution.maxRealSecondsPerRun can't be negative (0 = no limit).");

            return errors;
        }

        static void Range(List<string> errors, string field, float value, float min, float max)
        {
            if (float.IsNaN(value) || value < min || value > max)
                errors.Add($"{field} = {value} is outside {min}-{max}.");
        }

        // ---------------------------------------------------------------- sharding

        /// <summary>
        /// The runs one node does when the campaign is split across <paramref name="shardCount"/> nodes:
        /// run i goes to node i mod shardCount, which interleaves algorithms and seeds so every node gets a
        /// similar mix of short and long runs. The CLI uses the same rule for its manifest.
        /// </summary>
        public List<RunSpec> RunsForShard(int shardIndex, int shardCount)
        {
            shardCount = Mathf.Max(1, shardCount);
            var mine = new List<RunSpec>();
            foreach (var run in runs)
                if (run != null && run.runIndex % shardCount == shardIndex) mine.Add(run);
            mine.Sort((a, b) => a.runIndex.CompareTo(b.runIndex));
            return mine;
        }

        // ---------------------------------------------------------------- floor palette

        static string[] stockFloorNames;

        /// <summary>The names of the app's stock coverings, in palette order.</summary>
        public static string[] StockFloorNames
        {
            get
            {
                if (stockFloorNames != null) return stockFloorNames;
                var palette = FloorPalette.CreateDefault();
                stockFloorNames = palette.Labels();
                DestroyPalette(palette);
                return stockFloorNames;
            }
        }

        /// <summary>
        /// The palette runs use: the job's <see cref="floorTypes"/> in order, or the stock palette. The caller
        /// owns it and frees it with <see cref="DestroyPalette"/>.
        /// </summary>
        public FloorPalette BuildPalette()
        {
            var palette = FloorPalette.CreateDefault();
            if (floorTypes.Count == 0) return palette;

            // Stock colours and patterns are kept for coverings with a stock name, so heatmap PNGs read the same.
            var stock = palette;
            palette = ScriptableObject.CreateInstance<FloorPalette>();
            palette.name = "Job floor palette";
            foreach (var spec in floorTypes)
            {
                var floor = ScriptableObject.CreateInstance<FloorType>();
                floor.name = spec.name;
                floor.displayName = spec.name;
                for (int i = 0; i < stock.Count; i++)
                {
                    var match = stock.Get(i);
                    if (match != null && string.Equals(match.Label, spec.name, StringComparison.OrdinalIgnoreCase))
                    {
                        floor.color = match.color;
                        floor.pattern = match.pattern;
                    }
                }
                floor.speedMultiplier = spec.speedMultiplier;
                floor.energyCostMultiplier = spec.energyCostMultiplier;
                floor.cleaningEffort = spec.cleaningEffort;
                floor.soilingRate = spec.soilingRate;
                floor.stepHeight = spec.stepHeight;
                palette.Add(floor);
            }
            DestroyPalette(stock);
            return palette;
        }

        public static void DestroyPalette(FloorPalette palette)
        {
            if (palette == null) return;
            foreach (var floor in palette.Entries) Destroy(floor);
            Destroy(palette);
        }

        static void Destroy(UnityEngine.Object obj)
        {
            if (obj == null) return;
            if (Application.isPlaying) UnityEngine.Object.Destroy(obj);
            else UnityEngine.Object.DestroyImmediate(obj);
        }
    }

    /// <summary>A floor covering's behaviour (1.7.7, 1.7.8, 3.6, 3.13). Same ranges as the FloorType asset.</summary>
    [Serializable]
    public sealed class FloorTypeSpec
    {
        public string name;
        public float speedMultiplier = 1f;
        public float energyCostMultiplier = 1f;
        public float cleaningEffort = 1f;
        public float soilingRate = 1f;
        public float stepHeight;
    }

    /// <summary>One named configuration: the vacuum, its battery, and the run's simulation settings.</summary>
    [Serializable]
    public sealed class RunConfiguration
    {
        public string name = "default";
        public VacuumSpec vacuum = new VacuumSpec();
        public BatterySpec battery = new BatterySpec();
        public SimulationSpec simulation = new SimulationSpec();

        /// <summary>Cleaning effort overrides by covering name, as the GUI's Configuration screen sets them (1.7.8).</summary>
        public List<SimConfig.FloorEffort> floorEfforts = new List<SimConfig.FloorEffort>();

        /// <summary>The vacuum values in the GUI's own type, for summaries.</summary>
        public VacuumSettings ToVacuumSettings() => new VacuumSettings
        {
            driveSpeed = vacuum.driveSpeed,
            reverseSpeed = vacuum.reverseSpeed,
            turnSpeed = vacuum.turnSpeed,
            batteryCapacity = battery.capacity,
            useFeet = false,
        };
    }

    /// <summary>Drive settings (1.3.5, 1.3.6). Metric only: m/s and degrees/s.</summary>
    [Serializable]
    public sealed class VacuumSpec
    {
        public float driveSpeed = 0.65f;
        public float reverseSpeed = 0.35f;
        public float turnSpeed = 320f;

        /// <summary>Cleanliness gained per second of dwell on a floor with cleaning effort 1 (SDD 5.2).</summary>
        public float vacuumPower = 1f;
    }

    /// <summary>Battery size and its work-based drain (1.3.4, 3.6). Defaults match the Battery component.</summary>
    [Serializable]
    public sealed class BatterySpec
    {
        public float capacity = 300f;
        public float distanceEnergyPerMeter = 1f;
        public float turnEnergyPerDegree = 0.01f;
        public float operatingEnergyPerSecond = 0.1f;
        public float stalledEnergyPerSecond = 0.03f;
    }

    /// <summary>Per-run simulation settings (2.7, 2.19, SDD 4.3).</summary>
    [Serializable]
    public sealed class SimulationSpec
    {
        public float cellSizeMeters = SimDefaults.CellSizeMeters;
        public float targetMaxClean = HeatmapDefaults.TargetMaxClean;

        /// <summary>The run ends as TimeCap after this many simulated seconds if the battery lasts that long.</summary>
        public float maxSimulatedSeconds = 1800f;

        /// <summary>Simulated seconds between telemetry samples (2.19).</summary>
        public float telemetryInterval = 0.5f;

        public string heatmapMode = nameof(HeatmapMode.FloorCleanliness);

        public bool TryHeatmapMode(out HeatmapMode mode) =>
            Enum.TryParse(string.IsNullOrWhiteSpace(heatmapMode) ? nameof(HeatmapMode.FloorCleanliness) : heatmapMode.Trim(), true, out mode)
            && Enum.IsDefined(typeof(HeatmapMode), mode);
    }

    /// <summary>A parameter sweep: every floor plan x configuration x algorithm x seed (4.5).</summary>
    [Serializable]
    public sealed class SweepSpec
    {
        /// <summary>Algorithm names (RandomBounce, Spiral, WallFollow, Lawnmower). Empty = RandomBounce.</summary>
        public List<string> algorithms = new List<string>();

        /// <summary>Seeds seedStart, seedStart+1, ... seedCount of them. Ignored when <see cref="seeds"/> is given.</summary>
        public int seedStart = 1;
        public int seedCount = 1;

        /// <summary>Explicit seeds.</summary>
        public List<int> seeds = new List<int>();

        /// <summary>Floor plans to include, by name. Empty = all.</summary>
        public List<string> floorPlans = new List<string>();

        /// <summary>Configurations to include, by name. Empty = all.</summary>
        public List<string> configurations = new List<string>();
    }

    /// <summary>One run: a floor plan and configuration (by index), an algorithm and a seed.</summary>
    [Serializable]
    public sealed class RunSpec
    {
        public int runIndex;
        public int floorPlan;
        public int configuration;
        public string algorithm = nameof(MovementPattern.RandomBounce);
        public int seed;

        public bool TryPattern(out MovementPattern pattern) =>
            Enum.TryParse(algorithm?.Trim() ?? string.Empty, true, out pattern) && MovementAlgorithms.IsDefined(pattern);
    }

    /// <summary>How a node carries the job out.</summary>
    [Serializable]
    public sealed class ExecutionSpec
    {
        /// <summary>Number of nodes (AWS Batch array children) the runs are split across.</summary>
        public int shardCount = 1;

        /// <summary>results.json is rewritten after every N finished runs, and at the end (SDD 8: stepped returns).</summary>
        public int reportEveryNRuns = 1;

        /// <summary>Write each run's external_model.json (full per-cell data, ~2-20 MB per run).</summary>
        public bool saveExternalModel = true;

        /// <summary>Write each run's three heatmap PNGs.</summary>
        public bool saveHeatmapImages = true;

        /// <summary>Wall-clock seconds one run may take before it is stopped and recorded as an error (2.11). 0 = no limit.</summary>
        public float maxRealSecondsPerRun = 3600f;
    }
}
