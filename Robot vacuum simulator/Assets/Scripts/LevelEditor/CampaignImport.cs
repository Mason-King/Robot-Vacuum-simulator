using System;
using System.Collections.Generic;
using System.IO;
using RobotVacuum.LevelEditor.Cloud;
using RobotVacuum.Sim.Control;
using UnityEngine;

namespace RobotVacuum.LevelEditor
{
    /// <summary>
    /// Brings the results of a headless campaign (2.17) into the run history, so cloud and local headless batches
    /// show in Analysis through the same results interface as runs made in the app (2.14, 2.18, 4.6, C.13).
    ///
    /// A campaign folder is what <c>vacuumctl fetch</c> or <c>vacuumctl local</c> writes: campaign_results.json
    /// (every run's outcome, from the nodes' results.json) and shards/shard-NNNN/runs/&lt;guid&gt;/ with each run's
    /// summary.json, which is this app's own <see cref="RunReport"/>, plus its replay and CSVs. Only those local
    /// files are read; the app never talks to the cloud (1.1.1).
    ///
    /// The run outcome is authoritative for what was run (algorithm, seed, floor plan, configuration, success), the
    /// summary for what happened (coverage map, curve, rooms). A run with no summary (it failed to start, or its
    /// files weren't downloaded) is still imported from its outcome, so the statistics stay complete. Each campaign
    /// becomes one batch per floor plan and configuration, so statistics never pool different conditions. Run ids
    /// are derived from the campaign and run index, so importing again replaces rather than duplicates (C.10).
    /// </summary>
    public static class CampaignImport
    {
        public const string ResultsFileName = "campaign_results.json";
        public const string CloudSource = "cloud";
        public const string HeadlessSource = "headless";

        /// <summary>Files not copied into the app's data folder: the full per-cell model is large and Analysis doesn't use it.</summary>
        static readonly HashSet<string> SkippedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "external_model.json" };

        // ---------------------------------------------------------------- the file vacuumctl writes (cloud/vacuumctl/stats.py aggregate)

        [Serializable]
        public sealed class ResultsFile
        {
            public int schemaVersion;
            public string campaignId;
            public string campaignName;
            public string generatedUtc;
            public List<Outcome> runs = new List<Outcome>();
        }

        /// <summary>One run's outcome: the node's RunOutcome (Assets/Scripts/Headless/HeadlessResults.cs), as merged by vacuumctl.</summary>
        [Serializable]
        public sealed class Outcome
        {
            public int runIndex;
            public string floorPlan;
            public string configuration;
            public string algorithm;
            public string algorithmName;
            public int seed;
            public bool succeeded;
            public string error;
            public string endReason;
            public string runFolder;
            public float coveragePercent;
            public float simulatedSeconds;
            public float batteryCapacity;
            public float batteryUsed;
            public bool batteryEmptied;
            public float distanceMeters;
            public long revisitCount;
            public float timeTo50 = -1f;
            public float timeTo75 = -1f;
            public float timeTo90 = -1f;
            public float floorAreaSquareMeters;
            public float blockedAreaSquareMeters;
            public float cellSizeMeters;
        }

        /// <summary>A campaign folder found on disk, for the import list.</summary>
        public sealed class Campaign
        {
            public string folder;
            public string campaignId;
            public string name;
            public string source;
            public int runs;
            public int failed;
            public DateTime generated;
            public bool imported;

            public string SourceLabel => source == CloudSource ? "Cloud" : "Headless";
        }

        /// <summary>What an import did.</summary>
        public sealed class Result
        {
            public int imported;
            public int failed;
            public int withoutDetails;
            public readonly List<string> batchIds = new List<string>();
            public readonly List<string> problems = new List<string>();
            public string FirstBatchId => batchIds.Count > 0 ? batchIds[0] : null;
        }

        public static string BatchIdFor(string campaignId, string floorPlan, string configuration) =>
            $"{campaignId}/{floorPlan}/{configuration}";

        public static string RunIdFor(string campaignId, int runIndex) => $"{campaignId}-run{runIndex:D5}";

        // ---------------------------------------------------------------- finding campaigns

        /// <summary>
        /// Where vacuumctl writes results by default: cloud-results (fetch, run) and local-results (local) in the
        /// repository's cloud folder, found the same way the Network Settings page finds vacuumctl.
        /// </summary>
        public static List<string> DefaultRoots()
        {
            var roots = new List<string>();
            try
            {
                var connection = CloudConnection.Load(CloudConnection.DefaultPath, out _, out _);
                string cloud = VacuumctlProcess.FindCloudFolder(connection, out _);
                if (cloud != null)
                {
                    roots.Add(Path.Combine(cloud, "cloud-results"));
                    roots.Add(Path.Combine(cloud, "local-results"));
                }
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"Campaign import: could not find the cloud folder: {exception.Message}");
            }
            return roots;
        }

        /// <summary>Every campaign folder directly under <paramref name="roots"/>, newest first.</summary>
        public static List<Campaign> Find(IEnumerable<string> roots, IReadOnlyCollection<RunReport> history)
        {
            var known = new HashSet<string>();
            if (history != null) foreach (var run in history) if (run != null && run.id != null) known.Add(run.id);

            var found = new List<Campaign>();
            foreach (string root in roots)
            {
                if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;
                foreach (string folder in Directory.GetDirectories(root))
                {
                    var file = Read(folder, out _);
                    if (file == null) continue;
                    var campaign = new Campaign
                    {
                        folder = folder,
                        campaignId = file.campaignId,
                        name = string.IsNullOrEmpty(file.campaignName) ? file.campaignId : file.campaignName,
                        source = SourceOf(folder),
                        runs = file.runs.Count,
                        failed = file.runs.FindAll(r => !r.succeeded).Count,
                        generated = ParseTime(file.generatedUtc, Path.Combine(folder, ResultsFileName)),
                        imported = file.runs.Count > 0 && known.Contains(RunIdFor(file.campaignId, file.runs[0].runIndex)),
                    };
                    found.Add(campaign);
                }
            }
            found.Sort((a, b) => b.generated.CompareTo(a.generated));
            return found;
        }

        /// <summary>The folder's campaign_results.json, or null with the reason.</summary>
        public static ResultsFile Read(string folder, out string problem)
        {
            problem = null;
            string path = Path.Combine(folder ?? string.Empty, ResultsFileName);
            if (!File.Exists(path))
            {
                problem = $"There is no {ResultsFileName} in {folder}. Fetch the campaign first (python -m vacuumctl fetch <id>).";
                return null;
            }
            try
            {
                var file = JsonUtility.FromJson<ResultsFile>(File.ReadAllText(path));
                if (file == null || string.IsNullOrEmpty(file.campaignId)) throw new FormatException("it has no campaignId");
                if (file.schemaVersion != 1) throw new FormatException($"schemaVersion {file.schemaVersion} isn't supported (expected 1)");
                file.runs ??= new List<Outcome>();
                file.runs.RemoveAll(r => r == null);
                return file;
            }
            catch (Exception exception)
            {
                problem = $"{path} couldn't be read: {exception.Message}";
                return null;
            }
        }

        // ---------------------------------------------------------------- importing

        /// <summary>
        /// Imports one campaign folder into the run history at <paramref name="historyPath"/> (default: the app's),
        /// copying each run's files under <paramref name="dataRoot"/> (default: the app's Runs folder), so deleting
        /// the run in Analysis never touches the downloaded campaign.
        /// </summary>
        public static Result Import(string folder, string historyPath = null, string dataRoot = null)
        {
            var result = new Result();
            var file = Read(folder, out string problem);
            if (file == null)
            {
                result.problems.Add(problem);
                return result;
            }

            string source = SourceOf(folder);
            string finished = ParseTime(file.generatedUtc, Path.Combine(folder, ResultsFileName)).ToString("o");
            dataRoot ??= Path.GetDirectoryName(RunHistory.DataFolderFor("x"));
            var runFolders = IndexRunFolders(folder);

            var reports = new List<RunReport>();
            foreach (var outcome in file.runs)
            {
                string id = RunIdFor(file.campaignId, outcome.runIndex);
                string runName = string.IsNullOrEmpty(outcome.runFolder) ? null : Path.GetFileName(outcome.runFolder.TrimEnd('/', '\\'));
                string runFolder = runName != null && runFolders.TryGetValue(runName, out string found) ? found : null;

                RunReport report = runFolder != null ? ReadSummary(runFolder, result) : null;
                if (report == null)
                {
                    report = FromOutcome(outcome);
                    result.withoutDetails++;
                }

                // What was run comes from the outcome: older builds didn't record the algorithm id in summary.json.
                report.id = id;
                report.batchId = BatchIdFor(file.campaignId, outcome.floorPlan, outcome.configuration);
                report.trial = outcome.runIndex;
                report.seed = outcome.seed;
                report.algorithmId = outcome.algorithm;
                report.algorithmName = !string.IsNullOrEmpty(outcome.algorithmName) ? outcome.algorithmName : AlgorithmCatalog.NameOf(outcome.algorithm);
                var pattern = AlgorithmCatalog.PatternOf(outcome.algorithm);
                if (pattern.HasValue) report.pattern = pattern.Value;
                report.mixedPatterns = false;
                report.headless = true;
                report.source = source;
                report.campaignName = string.IsNullOrEmpty(file.campaignName) ? file.campaignId : file.campaignName;
                report.configuration = outcome.configuration;
                if (string.IsNullOrEmpty(report.levelName)) report.levelName = outcome.floorPlan;
                report.levelPath = string.Empty; // the floor plan lives in the campaign's job file, not the level library
                report.failure = outcome.succeeded ? string.Empty
                    : !string.IsNullOrEmpty(outcome.error) ? outcome.error : $"The run didn't succeed ({outcome.endReason}).";
                if (string.IsNullOrEmpty(report.endReason)) report.endReason = outcome.endReason;
                report.finishedAt = finished;
                report.dataFolder = runFolder != null ? CopyRunFolder(runFolder, Path.Combine(dataRoot, id), result) : null;

                if (!outcome.succeeded) result.failed++;
                if (!result.batchIds.Contains(report.batchId)) result.batchIds.Add(report.batchId);
                reports.Add(report);
            }

            int afterImport = reports.Count + RunHistory.LoadAll(historyPath).FindAll(r => !r.id.StartsWith(file.campaignId + "-run")).Count;
            if (afterImport > RunHistory.MaxRuns)
                result.problems.Add($"The history keeps the newest {RunHistory.MaxRuns} runs, so {afterImport - RunHistory.MaxRuns} older run(s) were dropped.");

            RunHistory.AddRange(reports, historyPath);
            result.imported = reports.Count;
            return result;
        }

        /// <summary>"cloud" when the campaign ran on AWS (its manifest names a bucket), else "headless" (vacuumctl local).</summary>
        static string SourceOf(string folder)
        {
            try
            {
                string manifest = Path.Combine(folder, "manifest.json");
                return File.Exists(manifest) && File.ReadAllText(manifest).Contains("\"bucket\"") ? CloudSource : HeadlessSource;
            }
            catch (Exception)
            {
                return HeadlessSource;
            }
        }

        /// <summary>Run folder name (the guid) → its path, across every shard.</summary>
        static Dictionary<string, string> IndexRunFolders(string folder)
        {
            var index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string shards = Path.Combine(folder, "shards");
            if (!Directory.Exists(shards)) return index;
            foreach (string shard in Directory.GetDirectories(shards))
            {
                string runs = Path.Combine(shard, "runs");
                if (!Directory.Exists(runs)) continue;
                foreach (string run in Directory.GetDirectories(runs)) index[Path.GetFileName(run)] = run;
            }
            return index;
        }

        static RunReport ReadSummary(string runFolder, Result result)
        {
            string path = Path.Combine(runFolder, "summary.json");
            if (!File.Exists(path)) return null;
            try
            {
                return JsonUtility.FromJson<RunReport>(File.ReadAllText(path));
            }
            catch (Exception exception)
            {
                result.problems.Add($"{path} couldn't be read ({exception.Message}); imported from its outcome instead.");
                return null;
            }
        }

        /// <summary>A run report built from the outcome alone: the numbers, without the map, curve or rooms.</summary>
        static RunReport FromOutcome(Outcome outcome) => new RunReport
        {
            levelName = outcome.floorPlan,
            endReason = outcome.endReason,
            durationSeconds = outcome.simulatedSeconds,
            coveragePercent = outcome.coveragePercent,
            distanceMeters = outcome.distanceMeters,
            batteryUsed = outcome.batteryUsed,
            batteryEmptied = outcome.batteryEmptied,
            revisitCount = outcome.revisitCount,
            timeTo50 = outcome.timeTo50,
            timeTo75 = outcome.timeTo75,
            timeTo90 = outcome.timeTo90,
            floorArea = outcome.floorAreaSquareMeters,
            blockedArea = outcome.blockedAreaSquareMeters,
            cellSize = outcome.cellSizeMeters,
            vacuum = new VacuumSettings { batteryCapacity = outcome.batteryCapacity },
        };

        static string CopyRunFolder(string from, string to, Result result)
        {
            try
            {
                Directory.CreateDirectory(to);
                foreach (string file in Directory.GetFiles(from))
                    if (!SkippedFiles.Contains(Path.GetFileName(file)))
                        File.Copy(file, Path.Combine(to, Path.GetFileName(file)), true);
                return to;
            }
            catch (Exception exception)
            {
                result.problems.Add($"Couldn't copy {from}: {exception.Message}");
                return null;
            }
        }

        static DateTime ParseTime(string text, string fallbackFile)
        {
            if (DateTime.TryParse(text, null, System.Globalization.DateTimeStyles.RoundtripKind, out var time)) return time.ToLocalTime();
            try { return File.GetLastWriteTime(fallbackFile); }
            catch (Exception) { return DateTime.Now; }
        }
    }
}
