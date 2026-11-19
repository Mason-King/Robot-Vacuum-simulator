using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RobotVacuum.Headless;
using RobotVacuum.Level;
using RobotVacuum.LevelEditor;
using RobotVacuum.Sim;
using RobotVacuum.Sim.Control;
using RunOutcome = RobotVacuum.Headless.RunOutcome; // Sim.Control has a RunOutcome too

namespace RobotVacuum.Network
{
    /// <summary>
    /// Turns a batch set up in the GUI (one floor plan, some algorithms, N seeds) into a headless job, with the same
    /// settings the in-app batch would use for every trial (<see cref="SimConfig.ToRunSettings(string, int?)"/>), so a
    /// batch gives the same results wherever it runs (2.1, 2.17).
    /// </summary>
    public static class NetworkJobBuilder
    {
        public static HeadlessJob FromBatch(LevelSnapshot level, BatchSpec spec, FloorPalette palette, out string problem)
        {
            problem = null;
            if (level == null || spec == null || spec.algorithmIds.Count == 0 || spec.trials < 1)
            {
                problem = "Choose a floor plan, at least one algorithm and at least one seed.";
                return null;
            }

            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var job = new HeadlessJob
            {
                campaignId = $"net-{stamp}-{Guid.NewGuid().ToString("N").Substring(0, 6)}",
                campaignName = WorkerProtocol.CleanName($"{level.name} batch {DateTime.Now:yyyy-MM-dd HH:mm}", "Network batch"),
                description = $"Network batch from {WorkerRegistry.ClientName}",
            };

            var plan = LevelSnapshot.Parse(level.ToJson(false));
            if (string.IsNullOrWhiteSpace(plan.name)) plan.name = "Floor plan";
            job.floorPlans.Add(plan);

            if (palette != null)
            {
                for (int i = 0; i < palette.Count; i++)
                {
                    var floor = palette.Get(i);
                    if (floor == null) continue;
                    job.floorTypes.Add(new FloorTypeSpec
                    {
                        name = floor.Label,
                        speedMultiplier = floor.speedMultiplier,
                        energyCostMultiplier = floor.energyCostMultiplier,
                        cleaningEffort = floor.cleaningEffort,
                        soilingRate = floor.soilingRate,
                        stepHeight = floor.stepHeight,
                    });
                }
            }

            job.configurations.Add(ConfigurationFromApp(spec, palette));

            // The same seeds as the in-app batch: trial n uses baseSeed + n, for every algorithm.
            job.sweep.algorithms = new List<string>(spec.algorithmIds);
            job.sweep.seedStart = spec.baseSeed;
            job.sweep.seedCount = spec.trials;
            job.runs = job.ExpandSweep(out var sweepErrors);
            if (sweepErrors.Count > 0)
            {
                problem = string.Join(" ", sweepErrors);
                return null;
            }

            // Network batches keep everything Analysis shows; the full per-cell model (2-20 MB a run) stays out of the transfer.
            job.execution.saveExternalModel = false;
            job.execution.saveHeatmapImages = true;

            if (!CarryAlgorithms(job, spec.algorithmIds, out problem)) return null;
            return job;
        }

        /// <summary>The app's current configuration, as the in-app batch applies it to each trial.</summary>
        public static RunConfiguration ConfigurationFromApp(BatchSpec spec, FloorPalette palette)
        {
            var settings = SimConfig.ToRunSettings(spec.algorithmIds.FirstOrDefault() ?? AlgorithmCatalog.RandomBounceId, spec.baseSeed);
            var config = new RunConfiguration { name = "default" };

            if (RunSettings.IsSet(settings.driveSpeed)) config.vacuum.driveSpeed = settings.driveSpeed;
            if (RunSettings.IsSet(settings.reverseSpeed)) config.vacuum.reverseSpeed = settings.reverseSpeed;
            if (RunSettings.IsSet(settings.turnSpeed)) config.vacuum.turnSpeed = settings.turnSpeed;
            if (RunSettings.IsSet(settings.vacuumPower)) config.vacuum.vacuumPower = settings.vacuumPower;
            config.vacuum.shape = settings.shape.ToString();
            if (RunSettings.IsSet(settings.batteryCapacity)) config.battery.capacity = settings.batteryCapacity;

            if (RunSettings.IsSet(settings.cellSizeMeters)) config.simulation.cellSizeMeters = settings.cellSizeMeters;
            if (RunSettings.IsSet(settings.targetMaxClean)) config.simulation.targetMaxClean = settings.targetMaxClean;
            config.simulation.maxSimulatedSeconds = Math.Max(60f, spec.maxMinutes * 60f);
            config.simulation.telemetryInterval = SimConfig.TelemetryInterval;
            config.simulation.heatmapMode = settings.heatmapMode.ToString();

            config.sensing.slam = settings.slam;
            config.sensing.noiseScale = settings.noiseScale;
            config.sensing.lidarEnabled = settings.lidarEnabled;
            if (RunSettings.IsSet(settings.lidarRangeMeters)) config.sensing.lidarRangeMeters = settings.lidarRangeMeters;
            config.sensing.lidarHalfAngleDegrees = settings.lidarHalfAngleDegrees;
            config.sensing.lidarRayCount = settings.lidarRayCount;
            config.sensing.floorSensorEnabled = settings.floorSensorEnabled;
            config.sensing.slamCorrectionQuality = settings.slamCorrectionQuality.ToString();
            config.sensing.slamLoopClosure = settings.slamLoopClosure;

            if (palette != null)
                for (int i = 0; i < palette.Count; i++)
                {
                    string label = palette.LabelOf(i);
                    float? effort = SimConfig.CleaningEffortFor(label);
                    if (effort.HasValue) config.floorEfforts.Add(new SimConfig.FloorEffort { floor = label, effort = effort.Value });
                }
            return config;
        }

        /// <summary>
        /// Puts the source of every chosen plugin algorithm in the job, so a worker without it can run it (3.8). Only
        /// .cs source travels; the worker compiles it behind the plugin sandbox (4.8). A DLL plugin can't be sent.
        /// </summary>
        static bool CarryAlgorithms(HeadlessJob job, IEnumerable<string> algorithmIds, out string problem)
        {
            problem = null;
            string folder = AlgorithmCatalog.LastReport?.folder ?? AlgorithmCatalog.Folder; // where the catalog's plugins were loaded from
            var carried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string id in algorithmIds)
            {
                var entry = AlgorithmCatalog.Find(id);
                if (entry == null || entry.BuiltIn) continue;
                string origin = AlgorithmSourceFile.NormalisePath(entry.Origin);
                if (origin.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                {
                    problem = $"{entry.Name} is a DLL plugin, and only .cs algorithms can be sent to other computers (they are checked by the plugin sandbox there). Run it on This PC only, or add its source to the Algorithms folder.";
                    return false;
                }

                string path = Path.Combine(folder, origin.Replace('/', Path.DirectorySeparatorChar));
                var files = new List<string>();
                if (File.Exists(path)) files.Add(path);
                else if (Directory.Exists(path)) files.AddRange(Directory.GetFiles(path, "*.cs", SearchOption.AllDirectories));
                if (files.Count == 0)
                {
                    problem = $"The source of {entry.Name} ({origin}) is no longer in the Algorithms folder. Rescan the algorithms and try again.";
                    return false;
                }

                foreach (string file in files)
                {
                    string relative = AlgorithmSourceFile.NormalisePath(file.Substring(folder.Length).TrimStart('/', '\\'));
                    if (!carried.Add(relative)) continue;
                    job.algorithmSources.Add(new AlgorithmSourceFile { path = relative, source = File.ReadAllText(file) });
                }
            }
            return true;
        }
    }

    /// <summary>Which shards go to which computer: in proportion to how many simulations each runs at once.</summary>
    public static class ShardPlan
    {
        /// <summary>
        /// Splits <paramref name="runCount"/> runs into min(total slots, runs) shards (run i goes to shard i mod count, as on
        /// a cloud node) and gives each target shards in proportion to its slots (D'Hondt), so every simulation slot gets
        /// one shard. Returns one list of shard indices per target.
        /// </summary>
        public static List<List<int>> Assign(IReadOnlyList<int> slots, int runCount, out int shardCount)
        {
            var result = slots.Select(_ => new List<int>()).ToList();
            int totalSlots = slots.Sum(s => Math.Max(0, s));
            shardCount = Math.Max(1, Math.Min(totalSlots, runCount));
            if (totalSlots == 0) return result;

            for (int shard = 0; shard < shardCount; shard++)
            {
                int best = -1;
                double bestQuotient = -1;
                for (int t = 0; t < slots.Count; t++)
                {
                    if (slots[t] <= 0 || result[t].Count >= slots[t]) continue;
                    double quotient = slots[t] / (double)(result[t].Count + 1);
                    if (quotient > bestQuotient) { bestQuotient = quotient; best = t; }
                }
                if (best < 0) break;
                result[best].Add(shard);
            }
            return result;
        }

        /// <summary>How many runs shard <paramref name="shard"/> of <paramref name="shardCount"/> has (run i → shard i mod count).</summary>
        public static int RunsIn(int shard, int shardCount, int runCount) =>
            shardCount <= 0 ? 0 : runCount / shardCount + (shard < runCount % shardCount ? 1 : 0);
    }

    /// <summary>
    /// campaign_results.json for a network batch, in the format <see cref="CampaignImport"/> reads (the same file
    /// vacuumctl writes for cloud campaigns): every run's outcome, from every shard's results.json.
    /// </summary>
    [Serializable]
    public sealed class NetworkResultsFile
    {
        public int schemaVersion = 1;
        public string campaignId;
        public string campaignName;
        public string generatedUtc;
        public string transport = NetworkCampaign.Transport;
        public int plannedRuns;
        public int returnedRuns;
        public int failedRuns;
        public List<RunOutcome> runs = new List<RunOutcome>();

        /// <summary>Merges the shards' results under <paramref name="campaignFolder"/>/shards. A run that ran twice (a moved shard) counts once.</summary>
        public static NetworkResultsFile FromShards(string campaignFolder, string campaignId, string campaignName, int plannedRuns)
        {
            var file = new NetworkResultsFile
            {
                campaignId = campaignId,
                campaignName = campaignName,
                generatedUtc = DateTime.UtcNow.ToString("o"),
                plannedRuns = plannedRuns,
            };
            var byIndex = new SortedDictionary<int, RunOutcome>();
            string shards = Path.Combine(campaignFolder, "shards");
            if (Directory.Exists(shards))
            {
                foreach (string shard in Directory.GetDirectories(shards).OrderBy(s => s, StringComparer.Ordinal))
                {
                    var results = ShardResults.TryRead(shard);
                    if (results?.runs == null || results.campaignId != campaignId) continue;
                    foreach (var outcome in results.runs)
                    {
                        if (outcome == null) continue;
                        if (!byIndex.TryGetValue(outcome.runIndex, out var existing) || (!existing.succeeded && outcome.succeeded))
                            byIndex[outcome.runIndex] = outcome;
                    }
                }
            }
            file.runs = byIndex.Values.ToList();
            file.returnedRuns = file.runs.Count;
            file.failedRuns = file.runs.Count(r => !r.succeeded);
            return file;
        }
    }
}
