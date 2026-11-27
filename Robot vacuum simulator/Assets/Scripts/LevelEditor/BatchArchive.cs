using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using RobotVacuum.Sim;
using UnityEngine;

namespace RobotVacuum.LevelEditor
{
    /// <summary>
    /// One trial of a batch, kept small: the headline numbers and per-room figures, without the coverage
    /// timeline or map. The full report stays in the trial's data folder (summary.json) for when it is opened.
    /// </summary>
    [Serializable]
    public sealed class BatchTrial
    {
        public string runId;
        public string dataFolder;
        public string algorithmId;
        public string algorithmName;
        public int seed;
        public int trial;
        public string endReason;
        public string failure;

        public float durationSeconds;
        public float coveragePercent;
        public float distanceMeters;
        public float batteryUsed;
        public bool batteryEmptied;
        public float floorArea;
        public float blockedArea;
        public long revisitCount;
        public float timeTo50 = -1f;
        public float timeTo75 = -1f;
        public float timeTo90 = -1f;

        /// <summary>Per room, aligned with <see cref="BatchResult.roomNames"/>. Empty when not tracked.</summary>
        public List<float> roomCoverage = new List<float>();
        public List<float> roomBattery = new List<float>();
        public List<float> roomSeconds = new List<float>();
        public List<float> roomDistance = new List<float>();
        public List<int> roomVisits = new List<int>();

        /// <summary>Per floor type, aligned with <see cref="BatchResult.floorNames"/>. Empty when not recorded.</summary>
        public List<float> floorCoverage = new List<float>();

        public static BatchTrial From(RunReport run) => new BatchTrial
        {
            runId = run.id,
            dataFolder = run.dataFolder,
            algorithmId = run.AlgorithmKey,
            algorithmName = run.AlgorithmName,
            seed = run.seed,
            trial = run.trial,
            endReason = run.endReason,
            failure = run.failure,
            durationSeconds = run.durationSeconds,
            coveragePercent = run.coveragePercent,
            distanceMeters = run.distanceMeters,
            batteryUsed = run.batteryUsed,
            batteryEmptied = run.batteryEmptied,
            floorArea = run.floorArea,
            blockedArea = run.blockedArea,
            revisitCount = run.revisitCount,
            timeTo50 = run.timeTo50,
            timeTo75 = run.timeTo75,
            timeTo90 = run.timeTo90,
            roomCoverage = new List<float>(run.roomCoverage),
            roomBattery = new List<float>(run.roomBattery),
            roomSeconds = new List<float>(run.roomSeconds),
            roomDistance = new List<float>(run.roomDistance),
            roomVisits = new List<int>(run.roomVisits),
            floorCoverage = new List<float>(run.floorCoverage ?? new List<float>()),
        };
    }

    /// <summary>One algorithm's averages over a batch's trials: the mean coverage curve and coverage map.</summary>
    [Serializable]
    public sealed class BatchAlgorithm
    {
        /// <summary>The algorithm's <see cref="RunReport.AlgorithmKey"/>.</summary>
        public string algorithmId;
        public string name;
        public int runs;

        /// <summary>
        /// Mean coverage percent at evenly spaced times from 0 to <see cref="curveDuration"/>, over the trials that
        /// recorded a timeline. Empty when none did (runs imported from their numbers alone).
        /// </summary>
        public List<float> curve = new List<float>();
        public float curveDuration;

        /// <summary>Standard deviation of coverage across the trials at each point of <see cref="curve"/>. Empty in older batch files.</summary>
        public List<float> curveSpread = new List<float>();

        /// <summary>Mean coverage map in the same encoding as <see cref="RunReport.coverageMap"/>, or empty.</summary>
        public string map;

        /// <summary>Mean coverage at simulated time <paramref name="t"/>; holds the final value after the curve ends.</summary>
        public float CoverageAt(float t) => Sample(curve, t);

        /// <summary>Spread of coverage across the trials at simulated time <paramref name="t"/>; 0 when not saved.</summary>
        public float SpreadAt(float t) => curveSpread.Count == curve.Count ? Sample(curveSpread, t) : 0f;

        float Sample(List<float> series, float t)
        {
            if (series.Count == 0) return 0f;
            if (series.Count == 1 || t >= curveDuration) return series[series.Count - 1];

            float position = Mathf.Max(0f, t) / Mathf.Max(1e-4f, curveDuration) * (series.Count - 1);
            int index = Mathf.Clamp(Mathf.FloorToInt(position), 0, series.Count - 1);
            int next = Mathf.Min(index + 1, series.Count - 1);
            return Mathf.Lerp(series[index], series[next], position - index);
        }
    }

    /// <summary>
    /// A whole batch experiment in one file: what was run, every trial's results, and each algorithm's
    /// averaged curve and map. Hundreds of trials stay a few hundred kilobytes, and listing or comparing
    /// batches never has to open the trials' own folders.
    /// </summary>
    [Serializable]
    public sealed class BatchResult
    {
        public int version = 1;
        public string batchId;
        public string levelName;
        public string levelPath;
        public string finishedAt;
        public bool headless;
        public VacuumSettings vacuum;
        public float cellSize;

        /// <summary>For a campaign imported from the cloud or headless runs (see <see cref="RunReport.source"/>); empty otherwise.</summary>
        public string source;
        public string campaignName;
        public string configuration;

        public List<string> roomNames = new List<string>();
        public List<float> roomArea = new List<float>();

        /// <summary>Floor types, for each trial's <see cref="BatchTrial.floorCoverage"/>.</summary>
        public List<string> floorNames = new List<string>();

        /// <summary>Shape of every algorithm's <see cref="BatchAlgorithm.map"/>.</summary>
        public int mapWidth;
        public int mapHeight;
        public Rect mapRect;

        public List<BatchTrial> trials = new List<BatchTrial>();
        public List<BatchAlgorithm> algorithms = new List<BatchAlgorithm>();

        [NonSerialized] List<RunReport> reports;

        public DateTime FinishedAt =>
            DateTime.TryParse(finishedAt, null, DateTimeStyles.RoundtripKind, out var time) ? time : DateTime.MinValue;

        public int MinSeed
        {
            get
            {
                int min = int.MaxValue;
                foreach (var t in trials) min = Math.Min(min, t.seed);
                return trials.Count > 0 ? min : 0;
            }
        }

        public int MaxSeed
        {
            get
            {
                int max = int.MinValue;
                foreach (var t in trials) max = Math.Max(max, t.seed);
                return trials.Count > 0 ? max : 0;
            }
        }

        public BatchAlgorithm Algorithm(string algorithmId) => algorithms.Find(a => a.algorithmId == algorithmId);

        public bool IsImported => !string.IsNullOrEmpty(source);

        /// <summary>"Cloud", "Headless" or "Network" for an imported campaign; empty otherwise.</summary>
        public string SourceLabel => source == "cloud" ? "Cloud" : source == "headless" ? "Headless" : source == "network" ? "Network" : string.Empty;

        /// <summary>The batch's name in lists: the campaign for an imported one, else the floor plan.</summary>
        public string Title => IsImported ? $"{SourceLabel} · {campaignName}" : $"Batch · {levelName}";

        /// <summary>
        /// The trials as run reports, for the statistics and tables that work on reports. They carry the
        /// headline numbers and rooms, not the timeline or map; <see cref="BatchArchive.LoadTrial"/> reads a
        /// trial's full report.
        /// </summary>
        public List<RunReport> Reports()
        {
            if (reports != null) return reports;

            reports = new List<RunReport>(trials.Count);
            foreach (var t in trials)
            {
                reports.Add(new RunReport
                {
                    id = t.runId,
                    dataFolder = t.dataFolder,
                    batchId = batchId,
                    levelName = levelName,
                    levelPath = levelPath,
                    finishedAt = finishedAt,
                    headless = headless,
                    vacuum = vacuum,
                    cellSize = cellSize,
                    algorithmId = t.algorithmId,
                    algorithmName = t.algorithmName,
                    source = source,
                    campaignName = campaignName,
                    configuration = configuration,
                    seed = t.seed,
                    trial = t.trial,
                    endReason = t.endReason,
                    failure = t.failure,
                    durationSeconds = t.durationSeconds,
                    coveragePercent = t.coveragePercent,
                    distanceMeters = t.distanceMeters,
                    batteryUsed = t.batteryUsed,
                    batteryEmptied = t.batteryEmptied,
                    floorArea = t.floorArea,
                    blockedArea = t.blockedArea,
                    revisitCount = t.revisitCount,
                    timeTo50 = t.timeTo50,
                    timeTo75 = t.timeTo75,
                    timeTo90 = t.timeTo90,
                    roomNames = t.roomCoverage.Count == roomNames.Count ? roomNames : new List<string>(),
                    roomArea = t.roomCoverage.Count == roomNames.Count ? roomArea : new List<float>(),
                    roomCoverage = t.roomCoverage,
                    roomBattery = t.roomBattery,
                    roomSeconds = t.roomSeconds,
                    roomDistance = t.roomDistance,
                    roomVisits = t.roomVisits,
                    floorNames = t.floorCoverage.Count == floorNames.Count ? floorNames : new List<string>(),
                    floorCoverage = t.floorCoverage.Count == floorNames.Count ? t.floorCoverage : new List<float>(),
                });
            }
            return reports;
        }
    }

    /// <summary>Batch experiments, one <see cref="BatchResult"/> file each, beside the run history.</summary>
    public static class BatchArchive
    {
        /// <summary>Points on each algorithm's saved mean coverage curve.</summary>
        public const int CurvePoints = 120;

        public static string DefaultFolder => Path.Combine(Application.persistentDataPath, "Batches");

        static string PathFor(string folder, string batchId) => Path.Combine(folder, FileNameFor(batchId) + ".json");

        /// <summary>
        /// A batch id as a file name. Imported campaigns' ids hold slashes (campaign/floor plan/configuration), so
        /// anything a file name can't have becomes an underscore; the real id is kept inside the file.
        /// </summary>
        public static string FileNameFor(string batchId)
        {
            var name = new StringBuilder(batchId.Length);
            var invalid = Path.GetInvalidFileNameChars();
            foreach (char c in batchId) name.Append(c == '/' || c == '\\' || Array.IndexOf(invalid, c) >= 0 ? '_' : c);
            return name.ToString();
        }

        /// <summary>Condenses a batch's finished trials into one result: compact trials plus per-algorithm averages.</summary>
        public static BatchResult Build(string batchId, IReadOnlyList<RunReport> runs)
        {
            var result = new BatchResult { batchId = batchId };
            if (runs == null || runs.Count == 0) return result;

            RunReport newest = runs[0];
            foreach (var run in runs)
                if (run.FinishedAt > newest.FinishedAt) newest = run;

            var first = runs[0];
            result.levelName = first.levelName;
            result.levelPath = first.levelPath;
            result.finishedAt = string.IsNullOrEmpty(newest.finishedAt) ? DateTime.Now.ToString("o") : newest.finishedAt;
            result.headless = first.headless;
            result.vacuum = first.vacuum;
            result.cellSize = first.cellSize;
            result.source = first.source;
            result.campaignName = first.campaignName;
            result.configuration = first.configuration;

            var withRooms = new List<RunReport>(runs).Find(r => r.HasRoomEnergy);
            if (withRooms != null)
            {
                result.roomNames = new List<string>(withRooms.roomNames);
                result.roomArea = new List<float>(withRooms.roomArea);
            }

            var withFloors = new List<RunReport>(runs).Find(r => r.floorNames != null && r.floorNames.Count > 0);
            if (withFloors != null) result.floorNames = new List<string>(withFloors.floorNames);

            foreach (var run in runs)
            {
                var trial = BatchTrial.From(run);
                if (!SameNames(run.floorNames, result.floorNames)) trial.floorCoverage.Clear();
                if (run.roomNames.Count != result.roomNames.Count)
                {
                    trial.roomCoverage.Clear();
                    trial.roomBattery.Clear();
                    trial.roomSeconds.Clear();
                    trial.roomDistance.Clear();
                    trial.roomVisits.Clear();
                }
                result.trials.Add(trial);
            }

            foreach (var summary in BatchStats.Summarise(runs))
            {
                var algorithm = new BatchAlgorithm { algorithmId = summary.algorithmId, name = summary.name, runs = summary.runs.Count };

                // Only trials that recorded a timeline: one imported from its numbers alone would count its final
                // coverage from the first second.
                var timed = summary.runs.FindAll(r => r.coverageSamples != null && r.coverageSamples.Count > 0);
                if (timed.Count > 0)
                {
                    algorithm.curve.AddRange(BatchStats.AverageCurve(timed, CurvePoints, out algorithm.curveDuration));
                    algorithm.curveSpread.AddRange(BatchStats.CurveSpread(timed, algorithm.curve, algorithm.curveDuration));
                }

                var map = BatchStats.AverageMap(summary.runs, out var shape);
                if (map != null)
                {
                    algorithm.map = BatchStats.EncodeMap(map);
                    if (result.mapWidth == 0)
                    {
                        result.mapWidth = shape.mapWidth;
                        result.mapHeight = shape.mapHeight;
                        result.mapRect = shape.mapRect;
                    }
                    else if (shape.mapWidth != result.mapWidth || shape.mapHeight != result.mapHeight) algorithm.map = null;
                }
                result.algorithms.Add(algorithm);
            }

            return result;
        }

        static bool SameNames(List<string> a, List<string> b)
        {
            if (a == null || a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
                if (a[i] != b[i]) return false;
            return true;
        }

        /// <summary>
        /// Writes the batch's file, and beside it a CSV of every trial and the per-algorithm statistics, in the same
        /// columns as vacuumctl's campaign_runs.csv (<see cref="BatchStats.ToCsv"/>).
        /// </summary>
        public static void Save(BatchResult batch, string folder = null)
        {
            folder ??= DefaultFolder;
            Directory.CreateDirectory(folder);

            string path = PathFor(folder, batch.batchId);
            string temp = path + ".tmp";
            File.WriteAllText(temp, JsonUtility.ToJson(batch), Encoding.UTF8);
            if (File.Exists(path)) File.Delete(path);
            File.Move(temp, path);

            try { File.WriteAllText(Path.Combine(folder, FileNameFor(batch.batchId) + ".csv"), BatchStats.ToCsv(batch.Reports()), Encoding.UTF8); }
            catch (Exception exception) { Debug.LogWarning($"Batch archive: could not write the CSV: {exception.Message}"); }
        }

        /// <summary>Every saved batch, newest first. Batches recorded into the run history by older versions are moved here first.</summary>
        public static List<BatchResult> LoadAll(string folder = null)
        {
            if (folder == null) MigrateLegacy();
            folder ??= DefaultFolder;

            var batches = new List<BatchResult>();
            if (!Directory.Exists(folder)) return batches;

            foreach (string path in Directory.GetFiles(folder, "*.json"))
            {
                try
                {
                    var batch = JsonUtility.FromJson<BatchResult>(File.ReadAllText(path));
                    if (batch != null && !string.IsNullOrEmpty(batch.batchId) && batch.trials.Count > 0) batches.Add(batch);
                }
                catch (Exception exception)
                {
                    Debug.LogWarning($"Batch archive: could not read '{path}': {exception.Message}");
                }
            }

            batches.Sort((a, b) => b.FinishedAt.CompareTo(a.FinishedAt));
            return batches;
        }

        /// <summary>A trial's full report from its data folder, or null if it is gone.</summary>
        public static RunReport LoadTrial(BatchTrial trial) => LoadTrial(trial.dataFolder);

        public static RunReport LoadTrial(string dataFolder)
        {
            if (string.IsNullOrEmpty(dataFolder)) return null;
            string path = Path.Combine(dataFolder, "summary.json");
            if (!File.Exists(path)) return null;

            try { return JsonUtility.FromJson<RunReport>(File.ReadAllText(path)); }
            catch (Exception) { return null; }
        }

        /// <summary>Deletes a batch's file and every trial's data folder.</summary>
        public static void Delete(string batchId, string folder = null)
        {
            folder ??= DefaultFolder;
            string path = PathFor(folder, batchId);
            if (!File.Exists(path)) return;

            BatchResult batch = null;
            try { batch = JsonUtility.FromJson<BatchResult>(File.ReadAllText(path)); }
            catch (Exception) { }

            File.Delete(path);
            string csv = Path.Combine(folder, FileNameFor(batchId) + ".csv");
            if (File.Exists(csv)) File.Delete(csv);

            if (batch == null) return;
            foreach (var trial in batch.trials)
            {
                try
                {
                    if (!string.IsNullOrEmpty(trial.dataFolder) && Directory.Exists(trial.dataFolder)) Directory.Delete(trial.dataFolder, true);
                }
                catch (Exception exception)
                {
                    Debug.LogWarning($"Batch archive: could not delete '{trial.dataFolder}': {exception.Message}");
                }
            }
        }

        public static void Clear(string folder = null)
        {
            foreach (var batch in LoadAll(folder)) Delete(batch.batchId, folder);
        }

        /// <summary>
        /// Moves batch trials stored as full reports in the run history into batch files, and takes them out of the
        /// history; their data folders are kept. That covers batches recorded by older versions and campaigns
        /// brought in by <see cref="CampaignImport"/>, which imports into the history.
        /// </summary>
        static void MigrateLegacy()
        {
            var legacy = RunHistory.LoadAll().FindAll(run => run.IsBatch);
            if (legacy.Count == 0) return;

            var byBatch = new Dictionary<string, List<RunReport>>();
            foreach (var run in legacy)
            {
                if (!byBatch.TryGetValue(run.batchId, out var group)) byBatch[run.batchId] = group = new List<RunReport>();
                group.Add(run);
            }

            try
            {
                foreach (var pair in byBatch) Save(Build(pair.Key, pair.Value));
                RunHistory.RemoveBatchTrials();
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"Batch archive: could not move older batches out of the run history: {exception.Message}");
            }
        }
    }
}
