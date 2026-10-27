using System;
using System.IO;
using UnityEngine;

namespace RobotVacuum.Sim
{
    /// <summary>
    /// Why a run ended. Saved in result.json as its number, so the values are fixed: add new reasons at
    /// the end, never renumber.
    /// </summary>
    public enum RunEndReason
    {
        /// <summary>Not ended (a result with this reason was never finished).</summary>
        None = 0,

        /// <summary>The cleaning algorithm reported the job done. Reserved: no algorithm reports this yet.</summary>
        Completed = 1,

        /// <summary>The battery ran out: the normal way a run ends (SDD 1.3.3).</summary>
        BatteryDepleted = 2,

        /// <summary>The program's safety cap on simulated time was reached (SimDefaults.MaxSimulatedSeconds).</summary>
        TimeCap = 3,

        /// <summary>The user closed the run before it ended. The data up to that moment is still saved.</summary>
        StoppedByUser = 4,

        /// <summary>Something failed. Detection is planned, not built (SDD Section 12).</summary>
        Error = 5,

        /// <summary>The run stopped making progress without an error (stuck robot and similar). Planned, not built.</summary>
        Degenerate = 6,
    }

    /// <summary>
    /// A run's outcome, saved as result.json in its run folder: the numbers, why the run ended, and the
    /// paths of the other files saved next to it (the External Model state and the three heatmap PNGs --
    /// see RunArtifacts). result.json is written last, so its existence means the run finished.
    /// </summary>
    [Serializable]
    public class RunResult
    {
        /// <summary>False only when the run failed (<see cref="RunEndReason.Error"/>). A run stopped by the user still succeeded.</summary>
        public bool succeeded;
        public string errorMessage; // populated only when succeeded is false

        public RunEndReason endReason;

        public string levelName;
        public int seed;
        public MovementPattern pattern;

        // Graded average against the target (see ExternalModelData.CoveragePercent).
        public float coveragePercent;

        // Share of cleanable cells that reached the target.
        public float fullyCleanedPercent;

        // target_max_clean this run was measured against.
        public float targetMaxClean;
        public float blockedAreaSquareMeters;
        public float simulatedSeconds;
        public float realSeconds;

        // Null/empty when a file wasn't produced (e.g. the run failed before
        // completing). heatmapImagePath is the older single-image field and
        // points at the Floor Cleanliness PNG.
        public string heatmapImagePath;
        public string externalModelPath;
        public string cleanlinessImagePath;
        public string coverageImagePath;
        public string overCoverageImagePath;

        public static RunResult Failure(string message) =>
            new RunResult { succeeded = false, errorMessage = message, endReason = RunEndReason.Error };

        public void WriteTo(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, JsonUtility.ToJson(this, prettyPrint: true));
        }

        public static RunResult ReadFrom(string path) =>
            JsonUtility.FromJson<RunResult>(File.ReadAllText(path));
    }
}
