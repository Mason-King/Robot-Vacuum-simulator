using System;
using System.Collections.Generic;
using System.IO;
using RobotVacuum.Level;
using RobotVacuum.LevelEditor;
using RobotVacuum.Sim;
using RobotVacuum.Sim.Control;
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

        /// <summary>
        /// Cleaning algorithms carried in the job as C# source (requirement 3.8). Each file is written into a copy of the
        /// simulator's Algorithms folder at its <see cref="AlgorithmSourceFile.path"/> (so a subfolder is one algorithm
        /// project, as on the desktop), and that copy is scanned before the runs are checked. Empty = the algorithms
        /// that shipped with the build only.
        /// </summary>
        public List<AlgorithmSourceFile> algorithmSources = new List<AlgorithmSourceFile>();

        /// <summary>Notes from loading the job (algorithm load report, replaced files) for the log. Not part of the file.</summary>
        [NonSerialized] public List<string> loadNotes = new List<string>();

        public const int MaxAlgorithmFiles = 64;
        public const int MaxAlgorithmFileBytes = 1024 * 1024;
        public const int MaxAlgorithmTotalBytes = 4 * 1024 * 1024;

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
            if (errors.Count == 0) job.LoadAlgorithms(errors);
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
            algorithmSources ??= new List<AlgorithmSourceFile>();
            loadNotes ??= new List<string>();

            if (configurations.Count == 0) configurations.Add(new RunConfiguration());
            for (int i = 0; i < configurations.Count; i++)
            {
                var config = configurations[i] ??= new RunConfiguration();
                if (string.IsNullOrWhiteSpace(config.name)) config.name = $"config-{i}";
                config.vacuum ??= new VacuumSpec();
                config.battery ??= new BatterySpec();
                config.simulation ??= new SimulationSpec();
                config.sensing ??= new SensingSpec();
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
                : new List<string> { AlgorithmCatalog.RandomBounceId };

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

            // The ID becomes an S3 prefix and an IAM resource name in the cloud. Same rule as vacuumctl/campaign.py.
            if (!System.Text.RegularExpressions.Regex.IsMatch(campaignId ?? string.Empty, "^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$"))
                errors.Add($"campaignId \"{campaignId}\": use 1-128 letters, digits, '.', '_' or '-', starting with a letter or digit.");

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
                if (!c.vacuum.TryShape(out _))
                    errors.Add($"{at}.vacuum.shape: \"{c.vacuum.shape}\" is not one of Round, DShaped, Rectangular.");

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

                Range(errors, $"{at}.sensing.noiseScale", c.sensing.noiseScale, 0f, RunSettings.MaxNoiseScale);
                Range(errors, $"{at}.sensing.lidarRangeMeters", c.sensing.lidarRangeMeters, Sim.Sensors.LidarSensor.MinRangeMeters, SensingSpec.MaxLidarRangeMeters);
                Range(errors, $"{at}.sensing.lidarHalfAngleDegrees", c.sensing.lidarHalfAngleDegrees, 0f, 180f);
                if (c.sensing.lidarRayCount < Sim.Sensors.LidarRayLayout.MinRays || c.sensing.lidarRayCount > Sim.Sensors.LidarRayLayout.MaxRays)
                    errors.Add($"{at}.sensing.lidarRayCount = {c.sensing.lidarRayCount} is outside {Sim.Sensors.LidarRayLayout.MinRays}-{Sim.Sensors.LidarRayLayout.MaxRays}.");
                if (!c.sensing.TryCorrectionQuality(out _))
                    errors.Add($"{at}.sensing.slamCorrectionQuality: \"{c.sensing.slamCorrectionQuality}\" is not one of {string.Join(", ", Enum.GetNames(typeof(PoseCorrectionQuality)))}.");

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
                if (string.IsNullOrWhiteSpace(run.algorithm)) errors.Add($"run {run.runIndex}: needs an algorithm.");
            }

            // Execution
            if (execution.shardCount < 1 || execution.shardCount > 10000) errors.Add("execution.shardCount must be 1-10000.");
            if (execution.reportEveryNRuns < 1) errors.Add("execution.reportEveryNRuns must be at least 1.");
            if (execution.maxRealSecondsPerRun < 0f) errors.Add("execution.maxRealSecondsPerRun can't be negative (0 = no limit).");

            errors.AddRange(ValidateAlgorithmSources());
            return errors;
        }

        // ---------------------------------------------------------------- algorithms (2.3, 3.8, 4.8)

        /// <summary>Checks the carried source files before anything is written to disk: relative .cs paths, sizes.</summary>
        List<string> ValidateAlgorithmSources()
        {
            var errors = new List<string>();
            if (algorithmSources.Count > MaxAlgorithmFiles)
                errors.Add($"algorithmSources: {algorithmSources.Count} files is more than the {MaxAlgorithmFiles} allowed.");

            long total = 0;
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < algorithmSources.Count; i++)
            {
                var file = algorithmSources[i];
                string at = $"algorithmSources[{i}]";
                if (file == null || string.IsNullOrWhiteSpace(file.path)) { errors.Add($"{at}: needs a path."); continue; }

                string path = AlgorithmSourceFile.NormalisePath(file.path);
                at = $"algorithmSources[{i}] \"{file.path}\"";
                string[] parts = path.Split('/');
                if (Path.IsPathRooted(file.path) || file.path.Contains(":") || Array.Exists(parts, p => p.Length == 0 || p == "." || p == ".."))
                    errors.Add($"{at}: must be a plain relative path inside the Algorithms folder (no drive, leading slash or '..').");
                else if (!path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                    errors.Add($"{at}: only C# source files (.cs) can be carried in a job.");
                else if (Array.Exists(parts, p => p.StartsWith("_") || p.StartsWith(".")))
                    errors.Add($"{at}: names starting with '_' or '.' are skipped by the loader, so this file would never be used.");
                else if (path.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
                    errors.Add($"{at}: the path has characters that can't be used in a file name.");
                if (!paths.Add(path)) errors.Add($"{at}: the same path is given twice.");

                int bytes = System.Text.Encoding.UTF8.GetByteCount(file.source ?? string.Empty);
                if (bytes == 0) errors.Add($"{at}: the source is empty.");
                if (bytes > MaxAlgorithmFileBytes) errors.Add($"{at}: {bytes} bytes is more than the {MaxAlgorithmFileBytes} allowed per file.");
                total += bytes;
            }
            if (total > MaxAlgorithmTotalBytes)
                errors.Add($"algorithmSources: {total} bytes in all is more than the {MaxAlgorithmTotalBytes} allowed.");
            return errors;
        }

        /// <summary>
        /// Makes the algorithm catalog match this job, then checks every run's algorithm against it. With no carried
        /// sources the shipped Algorithms folder is scanned as usual. With sources, a copy of the shipped folder plus the
        /// job's files is written to a fresh temporary folder and scanned instead, so a job never changes the installed
        /// simulator's own Algorithms folder. A carried file that fails to compile rejects the job, since every run that
        /// uses it would fail. Each run's algorithm is rewritten to its catalog id (so "boustrophedon sweep" is recorded
        /// as "BoustrophedonSweep"), and a SLAM configuration paired with an algorithm that can't do SLAM is rejected.
        /// </summary>
        void LoadAlgorithms(List<string> errors)
        {
            AlgorithmLoadReport report;
            if (algorithmSources.Count > 0)
            {
                string folder = Path.Combine(Path.GetTempPath(), "robot-vacuum-algorithms", Guid.NewGuid().ToString("N"));
                WriteAlgorithmFolder(folder);
                report = AlgorithmCatalog.RescanFolder(folder);
            }
            else
            {
                report = AlgorithmCatalog.LastReport;
            }

            if (report != null)
            {
                if (report.refused) { errors.Add($"Algorithms could not be loaded: {report.refusedReason}"); return; }
                loadNotes.Add(report.Text());

                var carriedUnits = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var file in algorithmSources) carriedUnits.Add(AlgorithmSourceFile.NormalisePath(file.path).Split('/')[0]);
                foreach (var unit in report.units)
                    if (unit != null && unit.errors.Count > 0 && carriedUnits.Contains(AlgorithmSourceFile.NormalisePath(unit.name).Split('/')[0]))
                        foreach (string error in unit.errors) errors.Add($"algorithmSources: {error}");
            }
            if (errors.Count > 0) return;

            var reported = new HashSet<string>();
            foreach (var run in runs)
            {
                string id = ResolveAlgorithmId(run.algorithm);
                if (id == null)
                {
                    if (reported.Add("?" + run.algorithm))
                    {
                        var known = new List<string>();
                        foreach (var entry in AlgorithmCatalog.Entries) known.Add(entry.Id);
                        errors.Add($"run {run.runIndex}: unknown algorithm \"{run.algorithm}\". Known: {string.Join(", ", known)}.");
                    }
                    continue;
                }
                run.algorithm = id;

                var config = configurations[run.configuration];
                string refusal = AlgorithmCatalog.Refusal(id, config.sensing.slam);
                if (refusal != null && reported.Add(config.name + "|" + id))
                    errors.Add($"run {run.runIndex} (configuration \"{config.name}\"): {refusal}");
            }
        }

        void WriteAlgorithmFolder(string folder)
        {
            string shipped = AlgorithmLoader.DefaultFolder;
            if (Directory.Exists(shipped)) CopyFolder(shipped, folder);
            Directory.CreateDirectory(folder);

            foreach (var file in algorithmSources)
            {
                string relative = AlgorithmSourceFile.NormalisePath(file.path);
                string target = Path.Combine(folder, relative.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(target)) loadNotes.Add($"algorithmSources: \"{relative}\" replaces the file of the same name that shipped with this build.");
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.WriteAllText(target, file.source, new System.Text.UTF8Encoding(false));
            }
        }

        static void CopyFolder(string from, string to)
        {
            Directory.CreateDirectory(to);
            foreach (string file in Directory.GetFiles(from)) File.Copy(file, Path.Combine(to, Path.GetFileName(file)), true);
            foreach (string directory in Directory.GetDirectories(from)) CopyFolder(directory, Path.Combine(to, Path.GetFileName(directory)));
        }

        /// <summary>
        /// The catalog id an algorithm name stands for, or null: a catalog id ("RandomBounce", "BoustrophedonSweep", a
        /// plugin's "Namespace.Class") or a display name ("Random", "Boustrophedon Sweep"). Case doesn't matter.
        /// </summary>
        public static string ResolveAlgorithmId(string name)
        {
            name = name?.Trim();
            if (string.IsNullOrEmpty(name)) return null;
            var entries = AlgorithmCatalog.Entries;
            foreach (var entry in entries)
                if (string.Equals(entry.Id, name, StringComparison.OrdinalIgnoreCase)) return entry.Id;
            foreach (var entry in entries)
                if (string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase)) return entry.Id;
            return null;
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
        public SensingSpec sensing = new SensingSpec();

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
            shape = vacuum.TryShape(out var kind) ? kind : VacuumShapeKind.Round,
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

        /// <summary>The body's outline (3.3): Round, DShaped or Rectangular, any case. Missing or empty = Round, the original body.</summary>
        public string shape = nameof(VacuumShapeKind.Round);

        public bool TryShape(out VacuumShapeKind kind) => VacuumShape.TryParse(shape, out kind);
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

        /// <summary>Write the algorithm's decisions to the log. Off by default here: it is very chatty, and the log goes to CloudWatch.</summary>
        public bool logAlgorithmDecisions;

        public bool TryHeatmapMode(out HeatmapMode mode) =>
            Enum.TryParse(string.IsNullOrWhiteSpace(heatmapMode) ? nameof(HeatmapMode.FloorCleanliness) : heatmapMode.Trim(), true, out mode)
            && Enum.IsDefined(typeof(HeatmapMode), mode);
    }

    /// <summary>
    /// What the robot senses with, and whether it must build its own map (SLAM). Same fields and defaults as the GUI's
    /// run settings (<see cref="RunSettings"/>, Sensing and Advanced). Normal (slam = false): the map is preloaded and
    /// readings are exact.
    /// </summary>
    [Serializable]
    public sealed class SensingSpec
    {
        public const float MaxLidarRangeMeters = 50f;

        public bool slam;

        /// <summary>SLAM only: 0 = perfect sensors, 1 = a realistic budget robot, up to 10.</summary>
        public float noiseScale = 1f;

        public bool lidarEnabled = true;
        public float lidarRangeMeters = Sim.Sensors.LidarSensor.DefaultRangeMeters;

        /// <summary>Half the lidar's field of view, from straight ahead: 180 = full circle, 0 = no rays.</summary>
        public float lidarHalfAngleDegrees = 180f;

        public int lidarRayCount = 360;
        public bool floorSensorEnabled = true;

        /// <summary>SLAM only: Performance, Balanced or Reliable.</summary>
        public string slamCorrectionQuality = nameof(PoseCorrectionQuality.Balanced);

        /// <summary>SLAM only.</summary>
        public bool slamLoopClosure = true;

        public bool TryCorrectionQuality(out PoseCorrectionQuality quality)
        {
            string text = string.IsNullOrWhiteSpace(slamCorrectionQuality) ? nameof(PoseCorrectionQuality.Balanced) : slamCorrectionQuality.Trim();
            quality = PoseCorrectionQuality.Balanced;
            return !int.TryParse(text, out _) && Enum.TryParse(text, true, out quality) && Enum.IsDefined(typeof(PoseCorrectionQuality), quality);
        }
    }

    /// <summary>One C# source file of a carried algorithm (see <see cref="HeadlessJob.algorithmSources"/>).</summary>
    [Serializable]
    public sealed class AlgorithmSourceFile
    {
        /// <summary>Relative to the Algorithms folder, '/'-separated: "MyAlgo.cs", or "MyProject/Helpers.cs" for a multi-file project.</summary>
        public string path;

        public string source;

        public static string NormalisePath(string path) => (path ?? string.Empty).Trim().Replace('\\', '/');
    }

    /// <summary>A parameter sweep: every floor plan x configuration x algorithm x seed (4.5).</summary>
    [Serializable]
    public sealed class SweepSpec
    {
        /// <summary>Algorithm ids or names (see <see cref="HeadlessJob.ResolveAlgorithmId"/>). Empty = RandomBounce.</summary>
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
        /// <summary>The algorithm's catalog id once the job is loaded (any accepted name is rewritten to it).</summary>
        public string algorithm = AlgorithmCatalog.RandomBounceId;
        public int seed;
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
