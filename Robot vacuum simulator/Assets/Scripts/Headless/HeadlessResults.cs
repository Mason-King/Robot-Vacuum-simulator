using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace RobotVacuum.Headless
{
    /// <summary>
    /// What one headless node returns: results.json in its output folder. It is rewritten after every few
    /// runs (ExecutionSpec.reportEveryNRuns) and at the end, so a node that dies part-way still returns
    /// what it finished, and a retried node can pick up where it left off. The vacuumctl CLI merges every
    /// node's file into campaign_results.json with the batch statistics (2.8).
    /// </summary>
    [Serializable]
    public sealed class ShardResults
    {
        public const string FileName = "results.json";
        public const int CurrentSchemaVersion = 1;

        public int schemaVersion = CurrentSchemaVersion;
        public string campaignId;
        public string campaignName;
        public int shardIndex;
        public int shardCount = 1;

        /// <summary>running, completed, failed (the job itself could not run), or invalid (the job file was rejected).</summary>
        public string status = "running";
        public List<string> errors = new List<string>();

        public string startedUtc;
        public string updatedUtc;
        public string finishedUtc;
        public HostInfo host = new HostInfo();

        public int totalRuns;
        public int completedRuns;
        public int failedRuns;
        public List<RunOutcome> runs = new List<RunOutcome>();

        public void WriteTo(string folder)
        {
            updatedUtc = DateTime.UtcNow.ToString("o");
            Directory.CreateDirectory(folder);

            // Written to a temp file and moved into place, so an uploader never picks up half a file.
            string path = Path.Combine(folder, FileName);
            string temp = path + ".tmp";
            File.WriteAllText(temp, JsonUtility.ToJson(this, true));
            if (File.Exists(path)) File.Delete(path);
            File.Move(temp, path);
        }

        public static ShardResults TryRead(string folder)
        {
            try
            {
                string path = Path.Combine(folder, FileName);
                return File.Exists(path) ? JsonUtility.FromJson<ShardResults>(File.ReadAllText(path)) : null;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Headless: ignoring unreadable {FileName}: {ex.Message}");
                return null;
            }
        }
    }

    /// <summary>The machine a shard ran on, so cross-machine determinism checks can be traced (SDD 10).</summary>
    [Serializable]
    public sealed class HostInfo
    {
        public string platform;
        public string operatingSystem;
        public string unityVersion;
        public string appVersion;
        public string processorType;
        public int processorCount;
        public int systemMemoryMB;
        public string machineName;
        public string awsBatchJobId;

        public static HostInfo Current() => new HostInfo
        {
            platform = Application.platform.ToString(),
            operatingSystem = SystemInfo.operatingSystem,
            unityVersion = Application.unityVersion,
            appVersion = Application.version,
            processorType = SystemInfo.processorType,
            processorCount = SystemInfo.processorCount,
            systemMemoryMB = SystemInfo.systemMemorySize,
            machineName = Environment.MachineName,
            awsBatchJobId = Environment.GetEnvironmentVariable("AWS_BATCH_JOB_ID"),
        };
    }

    /// <summary>One run's outcome and metrics (1.7.10, 2.4).</summary>
    [Serializable]
    public sealed class RunOutcome
    {
        public int runIndex;
        public int floorPlanIndex;
        public string floorPlan;
        public int configurationIndex;
        public string configuration;
        public string algorithm;
        public int seed;

        public bool succeeded;
        public string error;

        /// <summary>Completed, BatteryDepleted, TimeCap, StoppedByUser, Error, Degenerate, or NotStarted.</summary>
        public string endReason;

        public float coveragePercent;
        public float fullyCleanedPercent;
        public float simulatedSeconds;
        public float realSeconds;

        public float batteryCapacity;
        public float batteryUsed;
        public bool batteryEmptied;
        public float distanceMeters;
        public long revisitCount;

        /// <summary>Simulated seconds to reach 50 / 75 / 90% coverage; -1 when never reached.</summary>
        public float timeTo50 = -1f;
        public float timeTo75 = -1f;
        public float timeTo90 = -1f;

        public float floorAreaSquareMeters;
        public float blockedAreaSquareMeters;
        public float targetMaxClean;
        public float cellSizeMeters;

        /// <summary>The run's folder (config.json, result.json, telemetry.csv, ...) relative to the output folder.</summary>
        public string runFolder;

        public List<RoomOutcome> rooms = new List<RoomOutcome>();

        /// <summary>Pre-run layout warnings (1.2.4, 1.2.5). The run still goes ahead, as in the GUI.</summary>
        public List<string> warnings = new List<string>();
    }

    [Serializable]
    public sealed class RoomOutcome
    {
        public string name;
        public float areaSquareMeters;
        public float coveragePercent;
        public float seconds;
        public float distanceMeters;
        public float batteryUsed;
        public int entries;
    }

    /// <summary>heartbeat.json: rewritten every couple of real seconds while a node works (SDD 2.1, C.5).</summary>
    [Serializable]
    public sealed class Heartbeat
    {
        public const string FileName = "heartbeat.json";

        public string updatedUtc;
        public string status;
        public int currentRunIndex = -1;
        public int completedRuns;
        public int totalRuns;
        public float currentSimulatedSeconds;
        public float currentCoveragePercent;

        public void WriteTo(string folder)
        {
            updatedUtc = DateTime.UtcNow.ToString("o");
            try
            {
                string path = Path.Combine(folder, FileName);
                File.WriteAllText(path + ".tmp", JsonUtility.ToJson(this, false));
                if (File.Exists(path)) File.Delete(path);
                File.Move(path + ".tmp", path);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Headless: could not write heartbeat: {ex.Message}");
            }
        }
    }
}
