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
        public MovementPattern pattern;
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

        public static BatchTrial From(RunReport run) => new BatchTrial
        {
            runId = run.id,
            dataFolder = run.dataFolder,
            pattern = run.pattern,
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
        };
    }

    /// <summary>One algorithm's averages over a batch's trials: the mean coverage curve and coverage map.</summary>
    [Serializable]
    public sealed class BatchAlgorithm
    {
        public MovementPattern pattern;
        public int runs;

        /// <summary>Mean coverage percent at evenly spaced times from 0 to <see cref="curveDuration"/>.</summary>
        public List<float> curve = new List<float>();
        public float curveDuration;

        /// <summary>Mean coverage map in the same encoding as <see cref="RunReport.coverageMap"/>, or empty.</summary>
        public string map;

        /// <summary>Mean coverage at simulated time <paramref name="t"/>; holds the final value after the curve ends.</summary>
        public float CoverageAt(float t)
        {
            if (curve.Count == 0) return 0f;
            if (curve.Count == 1 || t >= curveDuration) return curve[curve.Count - 1];

            float position = Mathf.Max(0f, t) / Mathf.Max(1e-4f, curveDuration) * (curve.Count - 1);
            int index = Mathf.Clamp(Mathf.FloorToInt(position), 0, curve.Count - 1);
            int next = Mathf.Min(index + 1, curve.Count - 1);
            return Mathf.Lerp(curve[index], curve[next], position - index);
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

        public List<string> roomNames = new List<string>();
        public List<float> roomArea = new List<float>();

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

        public BatchAlgorithm Algorithm(MovementPattern pattern) => algorithms.Find(a => a.pattern == pattern);

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
                    pattern = t.pattern,
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

        static string PathFor(string folder, string batchId) => Path.Combine(folder, batchId + ".json");

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

            var withRooms = new List<RunReport>(runs).Find(r => r.HasRoomEnergy);
            if (withRooms != null)
            {
                result.roomNames = new List<string>(withRooms.roomNames);
                result.roomArea = new List<float>(withRooms.roomArea);
            }

            foreach (var run in runs)
            {
                var trial = BatchTrial.From(run);
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
                var algorithm = new BatchAlgorithm { pattern = summary.pattern, runs = summary.runs.Count };
                algorithm.curve.AddRange(BatchStats.AverageCurve(summary.runs, CurvePoints, out algorithm.curveDuration));

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

        /// <summary>Writes the batch's file, and a CSV of its trials beside it for spreadsheets.</summary>
        public static void Save(BatchResult batch, string folder = null)
        {
            folder ??= DefaultFolder;
            Directory.CreateDirectory(folder);

            string path = PathFor(folder, batch.batchId);
            string temp = path + ".tmp";
            File.WriteAllText(temp, JsonUtility.ToJson(batch), Encoding.UTF8);
            if (File.Exists(path)) File.Delete(path);
            File.Move(temp, path);

            try { File.WriteAllText(Path.Combine(folder, batch.batchId + ".csv"), TrialsCsv(batch), Encoding.UTF8); }
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
            string csv = Path.Combine(folder, batchId + ".csv");
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

        /// <summary>One row per trial, for spreadsheets and plotting tools.</summary>
        public static string TrialsCsv(BatchResult batch)
        {
            var csv = new StringBuilder("algorithm,seed,trial,end_reason,coverage_pct,duration_s,battery_used,battery_emptied,distance_m,revisits,time_to_50_s,time_to_75_s,time_to_90_s,run_id\n");
            foreach (var t in batch.trials)
            {
                csv.Append(MovementAlgorithms.LabelOf(t.pattern)).Append(',').Append(t.seed).Append(',').Append(t.trial).Append(',')
                    .Append((t.endReason ?? string.Empty).Replace(',', ' ')).Append(',')
                    .Append(F(t.coveragePercent)).Append(',').Append(F(t.durationSeconds)).Append(',')
                    .Append(F(t.batteryUsed)).Append(',').Append(t.batteryEmptied ? 1 : 0).Append(',')
                    .Append(F(t.distanceMeters)).Append(',').Append(t.revisitCount).Append(',')
                    .Append(F(t.timeTo50)).Append(',').Append(F(t.timeTo75)).Append(',').Append(F(t.timeTo90)).Append(',')
                    .Append(t.runId).Append('\n');
            }
            return csv.ToString();
        }

        /// <summary>
        /// Moves batch trials an older version stored as full reports in the run history into batch files,
        /// and takes them out of the history. Their data folders are kept.
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

        static string F(float value) => value.ToString("0.####", CultureInfo.InvariantCulture);
    }
}
