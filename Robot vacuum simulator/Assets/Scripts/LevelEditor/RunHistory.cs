using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using RobotVacuum.Sim;
using UnityEngine;

namespace RobotVacuum.LevelEditor
{
    /// <summary>A setting changed while a run was going, stamped with the simulated time it happened.</summary>
    [Serializable]
    public sealed class ParameterEvent
    {
        public float time;
        public string parameter;
        public string from;
        public string to;
    }

    /// <summary>What one simulator run achieved, recorded when it ends and shown on the Analysis screen.</summary>
    [Serializable]
    public sealed class RunReport
    {
        /// <summary>Coverage is sampled at most this many times; longer runs thin the samples out.</summary>
        public const int MaxSamples = 480;

        public string id;
        public string levelName;
        public string levelPath;
        public string finishedAt;
        public string endReason;

        public MovementPattern pattern;
        public bool mixedPatterns;
        public VacuumSettings vacuum;

        /// <summary>The random seed the run started from; the same seed and settings replay it exactly.</summary>
        public int seed;

        /// <summary>Set for runs made by a batch experiment; all of its trials share it.</summary>
        public string batchId;
        public int trial;

        /// <summary>True when the run was stepped headless, without drawing.</summary>
        public bool headless;

        /// <summary>Why the algorithm was stopped, if it failed.</summary>
        public string failure;

        public float cellSize;
        public float telemetryInterval;

        public float durationSeconds;
        public float coveragePercent;
        public float distanceMeters;
        public float batteryUsed;
        public bool batteryEmptied;
        public float floorArea;
        public float blockedArea;

        /// <summary>Times the vacuum went back over floor it had already covered and left (cell entries).</summary>
        public long revisitCount;

        /// <summary>Simulated seconds to reach 50, 75 and 90% coverage, or -1 when the run never did.</summary>
        public float timeTo50 = -1f;
        public float timeTo75 = -1f;
        public float timeTo90 = -1f;

        /// <summary>Each room's own coverage, with <see cref="roomNames"/> alongside.</summary>
        public List<string> roomNames = new List<string>();
        public List<float> roomCoverage = new List<float>();

        /// <summary>
        /// Per room, aligned with <see cref="roomNames"/>: battery units used, simulated seconds and metres
        /// driven while the vacuum was in it, how many times it entered, and the room's floor area. Time in a
        /// doorway counts toward the room the vacuum came from. Empty for runs recorded before these existed.
        /// </summary>
        public List<float> roomBattery = new List<float>();
        public List<float> roomSeconds = new List<float>();
        public List<float> roomDistance = new List<float>();
        public List<int> roomVisits = new List<int>();
        public List<float> roomArea = new List<float>();

        /// <summary>Every setting changed during the run.</summary>
        public List<ParameterEvent> parameterEvents = new List<ParameterEvent>();

        /// <summary>
        /// The final coverage at coarse resolution, <see cref="mapWidth"/> × <see cref="mapHeight"/> bytes in
        /// base64, row by row from the bottom: 0–250 is how clean (250 = fully), 255 is no floor. It covers
        /// <see cref="mapRect"/>, in level metres.
        /// </summary>
        public string coverageMap;
        public int mapWidth;
        public int mapHeight;
        public Rect mapRect;

        /// <summary>Where the run's telemetry, replay and CSV files were written.</summary>
        public string dataFolder;

        /// <summary>Coverage percent every <see cref="sampleInterval"/> simulated seconds, starting at zero.</summary>
        public List<float> coverageSamples = new List<float>();
        public float sampleInterval = 1f;

        public string PatternLabel => mixedPatterns ? "Mixed" : MovementAlgorithms.LabelOf(pattern);

        public bool IsBatch => !string.IsNullOrEmpty(batchId);

        public DateTime FinishedAt =>
            DateTime.TryParse(finishedAt, null, System.Globalization.DateTimeStyles.RoundtripKind, out var time) ? time : DateTime.MinValue;

        /// <summary>Floor area the coverage percent applies to: everything not under blocking furniture.</summary>
        public float CleanableArea => Mathf.Max(0f, floorArea - blockedArea);

        public float CleanedArea => CleanableArea * coveragePercent / 100f;

        /// <summary>Percentage points of coverage gained per simulated minute.</summary>
        public float CoveragePerMinute => durationSeconds > 0f ? coveragePercent / (durationSeconds / 60f) : 0f;

        public float BatteryUsedPercent =>
            vacuum != null && vacuum.batteryCapacity > 0f ? Mathf.Clamp(batteryUsed / vacuum.batteryCapacity * 100f, 0f, 100f) : 0f;

        public float AverageSpeed => durationSeconds > 0f ? distanceMeters / durationSeconds : 0f;

        /// <summary>Square metres cleaned per battery work unit: how far the charge goes.</summary>
        public float CleanedPerBatteryUnit => batteryUsed > 0f ? CleanedArea / batteryUsed : 0f;

        /// <summary>True when the per-room battery, time and visit lists line up with the room names.</summary>
        public bool HasRoomEnergy =>
            roomNames.Count > 0 && roomBattery.Count == roomNames.Count && roomSeconds.Count == roomNames.Count &&
            roomVisits.Count == roomNames.Count && roomArea.Count == roomNames.Count;

        /// <summary>
        /// Appends a sample. When the list is full every other sample is dropped and the interval doubles,
        /// so a long run keeps an even, bounded timeline.
        /// </summary>
        public void AddSample(float coverage)
        {
            coverageSamples.Add(coverage);
            if (coverageSamples.Count < MaxSamples) return;

            var thinned = new List<float>(MaxSamples / 2 + 1);
            for (int i = 0; i < coverageSamples.Count; i += 2) thinned.Add(coverageSamples[i]);
            coverageSamples = thinned;
            sampleInterval *= 2f;
        }
    }

    /// <summary>Every recorded run, newest first, kept in one JSON file beside the level library.</summary>
    public static class RunHistory
    {
        /// <summary>Enough for several large batch experiments; the oldest runs drop off beyond it.</summary>
        public const int MaxRuns = 2000;

        [Serializable]
        sealed class Stored
        {
            public List<RunReport> runs = new List<RunReport>();
        }

        public static string DefaultPath => Path.Combine(Application.persistentDataPath, "RunHistory.json");

        public static List<RunReport> LoadAll(string path = null)
        {
            path ??= DefaultPath;
            if (!File.Exists(path)) return new List<RunReport>();

            try
            {
                var stored = JsonUtility.FromJson<Stored>(File.ReadAllText(path));
                var runs = stored?.runs ?? new List<RunReport>();
                runs.RemoveAll(run => run == null || string.IsNullOrEmpty(run.id));
                runs.Sort((a, b) => b.FinishedAt.CompareTo(a.FinishedAt));
                return runs;
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"Run history: could not read '{path}': {exception.Message}");
                return new List<RunReport>();
            }
        }

        /// <summary>Stamps the report with an id and time if it has none, then saves it at the front.</summary>
        public static void Add(RunReport report, string path = null)
        {
            if (string.IsNullOrEmpty(report.id)) report.id = Guid.NewGuid().ToString("N");
            if (string.IsNullOrEmpty(report.finishedAt)) report.finishedAt = DateTime.Now.ToString("o");

            var runs = LoadAll(path);
            runs.RemoveAll(run => run.id == report.id);
            runs.Insert(0, report);
            if (runs.Count > MaxRuns) runs.RemoveRange(MaxRuns, runs.Count - MaxRuns);
            Write(runs, path);
        }

        /// <summary>Saves several reports at once, newest first: a whole batch in one write.</summary>
        public static void AddRange(IEnumerable<RunReport> reports, string path = null)
        {
            var runs = LoadAll(path);
            foreach (var report in reports)
            {
                if (string.IsNullOrEmpty(report.id)) report.id = Guid.NewGuid().ToString("N");
                if (string.IsNullOrEmpty(report.finishedAt)) report.finishedAt = DateTime.Now.ToString("o");
                runs.RemoveAll(run => run.id == report.id);
                runs.Insert(0, report);
            }
            runs.Sort((a, b) => b.FinishedAt.CompareTo(a.FinishedAt));
            if (runs.Count > MaxRuns) runs.RemoveRange(MaxRuns, runs.Count - MaxRuns);
            Write(runs, path);
        }

        public static void Delete(string id, string path = null)
        {
            var runs = LoadAll(path);
            var doomed = runs.FindAll(run => run.id == id || run.batchId == id);
            if (doomed.Count == 0) return;

            runs.RemoveAll(run => run.id == id || run.batchId == id);
            Write(runs, path);
            foreach (var run in doomed) DeleteData(run);
        }

        public static void Clear(string path = null)
        {
            foreach (var run in LoadAll(path)) DeleteData(run);
            path ??= DefaultPath;
            if (File.Exists(path)) File.Delete(path);
        }

        /// <summary>The folder a run's exported files go in.</summary>
        public static string DataFolderFor(string runId) => Path.Combine(Application.persistentDataPath, "Runs", runId);

        static void DeleteData(RunReport run)
        {
            try
            {
                if (!string.IsNullOrEmpty(run.dataFolder) && Directory.Exists(run.dataFolder)) Directory.Delete(run.dataFolder, true);
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"Run history: could not delete '{run.dataFolder}': {exception.Message}");
            }
        }

        static void Write(List<RunReport> runs, string path)
        {
            path ??= DefaultPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");

            string temp = path + ".tmp";
            File.WriteAllText(temp, JsonUtility.ToJson(new Stored { runs = runs }), Encoding.UTF8);
            if (File.Exists(path)) File.Delete(path);
            File.Move(temp, path);
        }
    }
}
