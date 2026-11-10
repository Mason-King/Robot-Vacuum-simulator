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

        /// <summary>The cleaning algorithm reported the job done and the robot drove back to the dock.</summary>
        Completed = 1,

        /// <summary>The battery ran out: the normal way a run ends (SDD 1.3.3).</summary>
        BatteryDepleted = 2,

        /// <summary>The program's safety cap on simulated time was reached (SimDefaults.MaxSimulatedSeconds).</summary>
        TimeCap = 3,

        /// <summary>The user closed the run before it ended. The data up to that moment is still saved.</summary>
        StoppedByUser = 4,

        /// <summary>Something failed. Detection is planned, not built (SDD Section 12).</summary>
        Error = 5,

        /// <summary>
        /// The run could not complete without an error: the robot stopped making progress, or (built) it finished or ran
        /// low but could not reach the dock. errorMessage says which.
        /// </summary>
        Degenerate = 6,

        /// <summary>The battery ran low, so the robot drove back to the dock before the job was done.</summary>
        ReturnedLowBattery = 7,
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

        // The algorithm by catalog id and display name. For a plugin, pattern is just what the settings held; algorithmId is the truth.
        public string algorithmId;
        public string algorithmName;

        // Graded average against the target (see ExternalModelData.CoveragePercent).
        public float coveragePercent;

        // Share of cleanable cells that reached the target.
        public float fullyCleanedPercent;

        // target_max_clean this run was measured against.
        public float targetMaxClean;
        public float blockedAreaSquareMeters;
        public float simulatedSeconds;
        public float realSeconds;

        // SLAM runs: how far the robot's believed pose had drifted from the truth when the run ended. Always 0
        // in a Normal run, where the believed pose is the true pose.
        public bool slam;
        public float driftMeters;
        public float driftHeadingDegrees;

        // SLAM local tracking: scans matched to the map, scans with too few returns to match, scans added to the
        // submaps (after the motion filter), and submaps started.
        public int scanMatches;
        public int scanMatchesTooFewHits;
        public int scansInserted;
        public int submaps;

        // Performance: real milliseconds spent per simulated second on each part of the controller's step.
        public float msSensingPerSimSecond;
        public float msPosePerSimSecond;
        public float msMappingPerSimSecond;

        // SLAM accuracy over the whole run, step by step (the target: within 5 cm and 2° for 99% of the time, never
        // beyond 8 cm / 3°, within 5 cm at the end). Percentiles are to the nearest millimetre / 0.01°, rounded up.
        public float driftMaxMeters;
        public float driftP99Meters;
        public float driftP95Meters;
        public float driftRmsMeters;
        public float driftMaxHeadingDegrees;
        public float driftP99HeadingDegrees;
        public float driftRmsHeadingDegrees;

        // SLAM loop closure (global SLAM): whether it ran, submap searches made, loop-closure links accepted, pose
        // graph solves, and its cost.
        public bool loopClosure;
        public int loopSearches;
        public int loopLinks;
        public int poseGraphSolves;
        public float msLoopClosurePerSimSecond;

        // Real ms per simulated second on the algorithm, path planning and driving.
        public float msControlPerSimSecond;

        // Parts of it: the algorithm's NextGoal, and the path planner (whoever asked: the driver or the algorithm,
        // so the two can overlap when an algorithm plans paths itself).
        public float msAlgorithmPerSimSecond;
        public float msPlanningPerSimSecond;

        // The longest single physics step the controller took (sense + pose + map + loop closure), real milliseconds.
        public float msLongestStep;

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
