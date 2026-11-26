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

        /// <summary>The program's safety cap on simulated time was reached (SimDefaults.MaxSimulatedSeconds, 24 h). See also <see cref="TimeLimit"/>.</summary>
        TimeCap = 3,

        /// <summary>The user closed the run before it ended. The data up to that moment is still saved.</summary>
        StoppedByUser = 4,

        /// <summary>
        /// The run was ended by a failure it can't carry on from (Terminal, 2.11): the simulation's own code threw
        /// during a step, or the algorithm left too many threads that would not stop. errorMessage and fault say what.
        /// </summary>
        Error = 5,

        /// <summary>
        /// The robot finished or ran low but could not reach the dock (it parked where it was). errorMessage says
        /// why. (Before 2.11 this also covered a robot that stopped making progress: that is now <see cref="Stuck"/>.)
        /// </summary>
        Degenerate = 6,

        /// <summary>The battery ran low, so the robot drove back to the dock before the job was done.</summary>
        ReturnedLowBattery = 7,

        /// <summary>The run's own time limit (the user's, or a headless job's) was reached. See also <see cref="TimeCap"/>.</summary>
        TimeLimit = 8,

        /// <summary>
        /// The algorithm failed: it threw, hung past its time budget, or returned something invalid (2.11). The
        /// simulation itself was fine. fault says what, where and when.
        /// </summary>
        AlgorithmFault = 9,

        /// <summary>The robot made no progress for the watchdog's time (VacuumRobot.NoProgressSeconds) and was stopped.</summary>
        Stuck = 10,
    }

    /// <summary>
    /// An algorithm failure as saved in result.json (see Control.AlgorithmFault). <see cref="RunResult.hasFault"/> says
    /// whether there is one (JsonUtility always writes the object).
    /// </summary>
    [Serializable]
    public class RunFault
    {
        public string kind;          // Exception, Hang, InvalidOutput, Refused, GuardTripped; or Simulation (the simulation's own code threw)
        public string call;          // Create, Initialize, NextGoal; empty for Simulation
        public string algorithmId;
        public string algorithmName;
        public string exceptionType;
        public string message;
        public string stackTrace;
        public float simSeconds;
        public bool terminal;        // true: the run was ended (not something to carry on from)
    }

    /// <summary>
    /// One time the run stopped (a stop condition, 2.11 / 2.16), and what happened next. A headless run stops once
    /// and is terminated; a graphics run may stop, be resumed, and stop again.
    /// </summary>
    [Serializable]
    public class RunStopEvent
    {
        public float simSeconds;
        public string reason;        // RunEndReason name
        public string message;
        public string action;        // Resumed, RunAgain, Finished, Back, Terminated; empty while still stopped
    }

    /// <summary>
    /// One mid-run switch of algorithm (2.16), saved in result.json and summary.json. The run's algorithmId is the one
    /// it started with; the last switch's toId is the one it ended with.
    /// </summary>
    [Serializable]
    public class RunAlgorithmSwitch
    {
        public float simSeconds;
        public string fromId;
        public string fromName;
        public string toId;
        public string toName;
    }

    /// <summary>
    /// A run's outcome, saved as result.json in its run folder: the numbers, why the run ended, and the
    /// paths of the other files saved next to it (the External Model state and the three heatmap PNGs --
    /// see RunArtifacts). result.json is written last, so its existence means the run finished.
    /// </summary>
    [Serializable]
    public class RunResult
    {
        /// <summary>
        /// False only when the run failed: <see cref="RunEndReason.Error"/> or <see cref="RunEndReason.AlgorithmFault"/>.
        /// A run stopped by the user, stuck, or out of time still succeeded (it ran correctly; the result is just poor).
        /// </summary>
        public bool succeeded;
        public string errorMessage; // the failure, or the problem for an end that has one (Stuck, Degenerate); else empty

        public RunEndReason endReason;
        public string endReasonName; // endReason as text, for people reading the file

        // The algorithm's failure, if the run had one (2.11). hasFault says whether fault holds anything.
        public bool hasFault;
        public RunFault fault = new RunFault();

        // Every time the run stopped, in order, and what was done (resumed, run again, finished, ...).
        public System.Collections.Generic.List<RunStopEvent> stopEvents = new System.Collections.Generic.List<RunStopEvent>();

        public string levelName;
        public int seed;

        // The algorithm the run started with, by catalog id and display name; algorithmSwitches lists any changes.
        public string algorithmId;
        public string algorithmName;
        public System.Collections.Generic.List<RunAlgorithmSwitch> algorithmSwitches = new System.Collections.Generic.List<RunAlgorithmSwitch>();

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

        // The longest perception part of a physics step (sense + pose + map + loop closure), real milliseconds. Not the
        // algorithm's decision, the driver or path planning. (Named msLongestStep before 2026-11-26.)
        public float msLongestPerceptionStep;

        // The longest whole physics step, real milliseconds: perception plus the goal, the algorithm's decision, the
        // driver and path planning (the lag log's "worst step").
        public float msLongestFullStep;

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
            new RunResult { succeeded = false, errorMessage = message, endReason = RunEndReason.Error, endReasonName = nameof(RunEndReason.Error) };

        public void WriteTo(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, JsonUtility.ToJson(this, prettyPrint: true));
        }

        public static RunResult ReadFrom(string path) =>
            JsonUtility.FromJson<RunResult>(File.ReadAllText(path));
    }
}
