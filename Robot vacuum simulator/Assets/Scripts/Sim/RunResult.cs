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

        // The algorithm by catalog id and display name.
        public string algorithmId;
        public string algorithmName;

        // Graded average against the target (see ExternalModelData.CoveragePercent).
        public float coveragePercent;

        // Share of cleanable cells that reached the target.
        public float fullyCleanedPercent;

        // Over-cleaning across the same cleanable cells (see ExternalModelData.OverCleaning):
        // meanCleanRatio: all cleaning done ÷ what the target asks for (1 = the target everywhere, on average);
        // median/p90CleanRatio: each cell's cleanliness ÷ target; overCleanedPercent: share of cells past 1.1 × target;
        // overCleaningSquareMeters: cleaning beyond the target, as m² of floor cleaned once more to the target;
        // overCleaningPercentOfTotal: share of all cleaning done that went beyond the target.
        public float meanCleanRatio;
        public float medianCleanRatio;
        public float p90CleanRatio;
        public float overCleanedPercent;
        public float overCleaningSquareMeters;
        public float overCleaningPercentOfTotal;

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

        // SLAM accuracy split in two (end of run, reporting only). Map drift: one best-fit scale, rotation and offset of
        // the robot's whole track against the truth; its map is drawn from that track, so it carries the same distortion
        // (scale below 0 = the map is drawn smaller than the home). Local drift: the position and heading error left once
        // that distortion is removed, the way SLAM systems are usually scored.
        public float mapScalePercent;
        public float mapRotationDegrees;
        public float mapOffsetMeters;
        public float localDriftMaxMeters;
        public float localDriftP99Meters;
        public float localDriftRmsMeters;
        public float localDriftMaxHeadingDegrees;
        public float localDriftP99HeadingDegrees;
        public float localDriftRmsHeadingDegrees;

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

        // Blockages (the cat): how many times the controller found floor cut off or blocked, or no way home, and
        // waited; and whether the run ended by giving up on one (after the waits and the long hold). Then
        // blockageLeftSquareMeters is the floor still to clean that was left behind (as the robot believed; 0 when
        // the blocked way was home), and blockageMessage the warning. A run that gave up on a blockage while cleaning
        // still ends Completed (it docked), and on the way home DockUnreachable: these fields say why.
        public int blockageEvents;
        public bool endedByBlockage;
        public float blockageLeftSquareMeters;
        public string blockageMessage;

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
