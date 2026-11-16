using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace RobotVacuum.Sim
{
    /// <summary>
    /// LAG LOG (diagnostic; reporting only, never read by the simulation). Finds out where real time goes when the
    /// app lags, before anything is changed to fix it.
    ///
    ///   * Slow step: a brain step over <see cref="StepThresholdMs"/> logs one line with its parts, slowest first.
    ///   * Slow frame: a frame over <see cref="FrameThresholdMs"/> logs one line: how many sim steps it ran, how much
    ///     of it was the brain, and the rest (physics, the body, cleaning grid, cats, recorder, UI, drawing).
    ///   * Summary: at the end of the run (<see cref="EndRun"/>), counts of slow steps and frames, and each part's
    ///     worst step and its share of the time spent in slow steps.
    ///
    /// The brain's parts: sensing; track (SLAM: dead reckoning + local SLAM's scan match and insert; Normal: the
    /// true pose); loop search; graph solve; loop other (new submaps, bookkeeping); redraw (Internal Model from the
    /// submaps after a solve moved them); mapping (this step's observations and footprint); algorithm (NextGoal,
    /// including any paths it asks for); driver (the rest of the goal and driving: driver step, re-plans, battery
    /// check, homing). "Planner" is shown separately as "of which planner": it is time inside algorithm and driver,
    /// not extra.
    ///
    /// Lines go to the Console without a stack trace, so they also land in the run's console_log.txt.
    /// </summary>
    public static class LagLog
    {
        /// <summary>Master switch. Off: nothing is timed into the log and nothing is printed.</summary>
        public static readonly bool Enabled = true;

        /// <summary>A brain step slower than this (real ms) is logged.</summary>
        public const float StepThresholdMs = 50f;

        /// <summary>A frame slower than this (real ms) is logged: below 10 frames per second the GUI feels stuck.</summary>
        public const float FrameThresholdMs = 100f;

        public enum Part { Sensing, Track, Search, Solve, LoopOther, Redraw, Mapping, Algorithm, Driver, Count }

        static readonly string[] Names =
            { "sensing", "track", "loop search", "graph solve", "loop other", "redraw", "mapping", "algorithm", "driver" };

        static readonly float[] StepBands = { 20f, 50f, 100f, 200f };
        static readonly float[] FrameBands = { 100f, 250f, 500f, 1000f };

        // The run so far.
        static bool running;
        static int steps;
        static readonly int[] stepsOver = new int[StepBands.Length];
        static int frames;
        static readonly int[] framesOver = new int[FrameBands.Length];
        static readonly double[] partWorst = new double[(int)Part.Count];
        static readonly double[] partInSlowSteps = new double[(int)Part.Count];
        static double plannerWorst;
        static double worstStepMs;
        static float worstStepAt;
        static double worstFrameMs;
        static float lastSimTime;
        static Control.PathPlanner planner; // the run's planner, for its cost read-outs in the summary
        static Control.ObservationMapper observations; // the run's mapper, for its cell-flip read-outs
        static Control.MapAnalysis analysis;           // the algorithm's map analysis, for its breakdown
        static Control.LocalSlam localSlam;            // SLAM: for the pose breakdown
        static Control.GlobalSlam globalSlam;          // SLAM: for the time spent waiting for loop closure
        static readonly double[] partTotal = new double[(int)Part.Count]; // every step's parts, summed over the run
        static readonly List<object> sensorKeys = new List<object>();
        static readonly List<string> sensorNames = new List<string>();
        static readonly List<double> sensorMs = new List<double>();

        // The frame being stepped now, and the one before it (Unity's unscaledDeltaTime in frame F is the length of
        // frame F-1, whose steps ran before F-1's Update; so a frame's duration is matched with the steps saved then).
        static int frameSteps, previousFrameSteps;
        static double frameBrainMs, previousFrameBrainMs;
        static double frameWorstStepMs, previousFrameWorstStepMs;
        // The time settings the previous frame's steps ran under (raw, formatted only when a line is printed).
        static float govCapSteps, govTimeScale, govRequested, govAllowed;
        static bool govActive;
        static double govFast, govSlow;

        static readonly List<int> order = new List<int>();
        static readonly StringBuilder line = new StringBuilder(256);

        /// <summary>Clears the counts for a new run. Called when the brain (re)starts a run.</summary>
        public static void BeginRun(Control.PathPlanner runPlanner = null, Control.ObservationMapper runObservations = null)
        {
            if (!Enabled) return;
            LagLogFrames.Ensure();

            running = true;
            planner = runPlanner;
            observations = runObservations;
            analysis = null;
            localSlam = null;
            globalSlam = null;
            System.Array.Clear(partTotal, 0, partTotal.Length);
            sensorKeys.Clear();
            sensorNames.Clear();
            sensorMs.Clear();
            lastSimTime = 0f;
            steps = frames = 0;
            System.Array.Clear(stepsOver, 0, stepsOver.Length);
            System.Array.Clear(framesOver, 0, framesOver.Length);
            System.Array.Clear(partWorst, 0, partWorst.Length);
            System.Array.Clear(partInSlowSteps, 0, partInSlowSteps.Length);
            plannerWorst = worstStepMs = worstFrameMs = 0;
            worstStepAt = 0f;
            frameSteps = previousFrameSteps = 0;
            frameBrainMs = previousFrameBrainMs = 0;
            frameWorstStepMs = previousFrameWorstStepMs = 0;
        }

        /// <summary>One brain step: its parts in real ms (indexed by <see cref="Part"/>), and the planner time inside them.</summary>
        public static void Step(float simTime, double[] parts, double plannerMs, int submaps)
        {
            if (!Enabled || !running) return;
            lastSimTime = simTime;

            double total = 0;
            for (int i = 0; i < (int)Part.Count; i++)
            {
                total += parts[i];
                partTotal[i] += parts[i];
                if (parts[i] > partWorst[i]) partWorst[i] = parts[i];
            }
            if (plannerMs > plannerWorst) plannerWorst = plannerMs;

            steps++;
            for (int b = 0; b < StepBands.Length; b++)
                if (total > StepBands[b]) stepsOver[b]++;
            if (total > worstStepMs)
            {
                worstStepMs = total;
                worstStepAt = simTime;
            }

            frameSteps++;
            frameBrainMs += total;
            if (total > frameWorstStepMs) frameWorstStepMs = total;

            if (total <= StepThresholdMs) return;

            for (int i = 0; i < (int)Part.Count; i++) partInSlowSteps[i] += parts[i];

            line.Clear();
            line.Append("[Lag ").Append(simTime.ToString("F1")).Append(" s] slow step ").Append(total.ToString("F0")).Append(" ms:");
            AppendParts(parts, 0.05);
            if (plannerMs >= 0.05) line.Append(" (of which planner ").Append(plannerMs.ToString("F1")).Append(')');
            if (submaps > 0) line.Append(" | submaps ").Append(submaps);
            Print(line.ToString());
        }

        /// <summary>SLAM runs: local and global SLAM, for the pose breakdown and the loop-closure wait.</summary>
        public static void SetSlam(Control.LocalSlam local, Control.GlobalSlam global)
        {
            if (!Enabled) return;
            localSlam = local;
            globalSlam = global;
        }

        /// <summary>The algorithm's map analysis for this run (set when the algorithm is given its context).</summary>
        public static void SetMapAnalysis(Control.MapAnalysis runAnalysis)
        {
            if (Enabled) analysis = runAnalysis;
        }

        /// <summary>Adds one sensor's real time this step (since <paramref name="started"/>, a Stopwatch timestamp), by its type.</summary>
        public static void AddSensorTime(object sensor, long started)
        {
            if (!running) return;
            double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            int index = -1;
            for (int i = 0; i < sensorKeys.Count; i++)
                if (ReferenceEquals(sensorKeys[i], sensor)) { index = i; break; }
            if (index < 0)
            {
                sensorKeys.Add(sensor);
                sensorNames.Add(sensor.GetType().Name);
                sensorMs.Add(ms);
            }
            else sensorMs[index] += ms;
        }

        /// <summary>Once per frame, from <see cref="LagLogFrames"/>.</summary>
        internal static void Frame(float realSeconds)
        {
            if (!Enabled) return;

            if (running && previousFrameSteps > 0)
            {
                double realMs = realSeconds * 1000.0;
                frames++;
                for (int b = 0; b < FrameBands.Length; b++)
                    if (realMs > FrameBands[b]) framesOver[b]++;
                if (realMs > worstFrameMs) worstFrameMs = realMs;

                if (realMs > FrameThresholdMs)
                {
                    double rest = System.Math.Max(0.0, realMs - previousFrameBrainMs);
                    Print($"[Lag frame] {realMs:F0} ms real: {previousFrameSteps} sim step{(previousFrameSteps == 1 ? "" : "s")}, " +
                          $"brain {previousFrameBrainMs:F0} ms (slowest step {previousFrameWorstStepMs:F0}), " +
                          $"everything else {rest:F0} ms (physics, body, cleaning grid, cats, recorder, UI, drawing) | " +
                          $"governor: cap {govCapSteps:F0} steps, timeScale {govTimeScale:0.##}, requested {govRequested:0.##}, " +
                          $"active {govActive}, cost fast {govFast:F2} / slow {govSlow:F2} ms, allowed {govAllowed:F0}");
                }
            }

            previousFrameSteps = frameSteps;
            previousFrameBrainMs = frameBrainMs;
            previousFrameWorstStepMs = frameWorstStepMs;
            // The time settings this frame's steps ran under (they ran before this Update; the governor's Update and
            // any speed change come after it), kept to print with this frame's line next frame.
            govCapSteps = Time.maximumDeltaTime * Mathf.Max(1f, Time.timeScale) / Time.fixedDeltaTime; // the cap is real time × time scale
            govTimeScale = Time.timeScale;
            govRequested = SimulationSpeed.Requested;
            govActive = SpeedGovernor.LastActive;
            govFast = SpeedGovernor.FastCostMs;
            govSlow = SpeedGovernor.SlowCostMs;
            govAllowed = SpeedGovernor.AllowedSteps;
            frameSteps = 0;
            frameBrainMs = 0;
            frameWorstStepMs = 0;
        }

        /// <summary>Prints the run's summary and stops counting. Called when the session ends the run.</summary>
        public static void EndRun()
        {
            if (!Enabled || !running) return;
            running = false;

            line.Clear();
            line.Append("[Lag summary] ").Append(steps).Append(" brain steps; over");
            for (int b = 0; b < StepBands.Length; b++)
                line.Append(b == 0 ? " " : ", ").Append(StepBands[b].ToString("F0")).Append(" ms: ").Append(stepsOver[b]);
            line.Append(". Worst step ").Append(worstStepMs.ToString("F0")).Append(" ms at ").Append(worstStepAt.ToString("F1")).Append(" s.");

            line.Append(" Frames with sim steps ").Append(frames).Append("; over");
            for (int b = 0; b < FrameBands.Length; b++)
                line.Append(b == 0 ? " " : ", ").Append(FrameBands[b].ToString("F0")).Append(" ms: ").Append(framesOver[b]);
            line.Append(". Worst frame ").Append(worstFrameMs.ToString("F0")).Append(" ms.");

            line.Append("\n  Worst single step per part:");
            AppendParts(partWorst, 0.05);
            line.Append(" (planner ").Append(plannerWorst.ToString("F1")).Append(')');

            double slowTotal = 0;
            for (int i = 0; i < partInSlowSteps.Length; i++) slowTotal += partInSlowSteps[i];
            if (slowTotal > 0)
            {
                line.Append("\n  Share of time in slow steps (> ").Append(StepThresholdMs.ToString("F0")).Append(" ms):");
                SortParts(partInSlowSteps);
                bool first = true;
                foreach (int i in order)
                {
                    double share = partInSlowSteps[i] / slowTotal;
                    if (share < 0.005) continue;
                    line.Append(first ? " " : ", ").Append(Names[i]).Append(' ').Append((share * 100).ToString("F0")).Append('%');
                    first = false;
                }
            }
            AppendPlanner();
            AppendFlips();
            AppendSensorsAndAnalysis();
            AppendSlam();
            Print(line.ToString());
            planner = null;
            observations = null;
            analysis = null;
        }

        /// <summary>SLAM: track split into scan match, insert and the rest (dead reckoning, bookkeeping); loop-closure wait.</summary>
        static void AppendSlam()
        {
            if (localSlam == null || lastSimTime <= 0f) return;
            double seconds = lastSimTime;
            double track = partTotal[(int)Part.Track];
            double rest = System.Math.Max(0.0, track - localSlam.MatchMs - localSlam.InsertMs);
            line.Append("\n  Track (ms per sim second): ").Append((track / seconds).ToString("F2"))
                .Append(" = scan match ").Append((localSlam.MatchMs / seconds).ToString("F2"))
                .Append(" (").Append((localSlam.Matches / seconds).ToString("F1")).Append(" matches/s");
            if (localSlam.Matches > 0)
                line.Append(", ").Append(((double)localSlam.MatchIterations / localSlam.Matches).ToString("F1")).Append(" iterations and ")
                    .Append(((double)localSlam.MatchPoints / localSlam.Matches).ToString("F0")).Append(" points each");
            line.Append("), insert ").Append((localSlam.InsertMs / seconds).ToString("F2"))
                .Append(" (").Append((localSlam.ScansInserted / seconds).ToString("F1")).Append(" inserts/s), rest ")
                .Append((rest / seconds).ToString("F2")).Append('.');
            if (globalSlam != null)
                line.Append(" Loop closure on the main thread ")
                    .Append(((partTotal[(int)Part.Search] + partTotal[(int)Part.Solve] + partTotal[(int)Part.LoopOther]) / seconds).ToString("F2"))
                    .Append(" ms per sim second, of which waiting for the worker ").Append((globalSlam.WaitMs / seconds).ToString("F2")).Append('.');
        }

        /// <summary>Sensing by sensor, and the algorithm's map-analysis updates by part (ms per simulated second).</summary>
        static void AppendSensorsAndAnalysis()
        {
            if (lastSimTime <= 0f) return;
            double seconds = lastSimTime;

            if (sensorNames.Count > 0)
            {
                line.Append("\n  Sensing by sensor (ms per sim second):");
                for (int i = 0; i < sensorNames.Count; i++)
                    line.Append(i == 0 ? " " : ", ").Append(sensorNames[i]).Append(' ').Append((sensorMs[i] / seconds).ToString("F2"));
            }

            if (analysis != null && analysis.Updates > 0)
            {
                double total = analysis.SnapshotMs + analysis.ReachMs + analysis.RoomsMs;
                line.Append("\n  Map analysis (inside algorithm): ").Append((analysis.Updates / seconds).ToString("F2"))
                    .Append(" updates per sim second (").Append(analysis.ChangedUpdates).Append(" with the map changed), ")
                    .Append((total / seconds).ToString("F2")).Append(" ms per sim second: snapshot ")
                    .Append((analysis.SnapshotMs / seconds).ToString("F2")).Append(", reach ")
                    .Append((analysis.ReachMs / seconds).ToString("F2")).Append(", rooms ")
                    .Append((analysis.RoomsMs / seconds).ToString("F2")).Append("; per update ")
                    .Append((total / analysis.Updates).ToString("F1")).Append(" ms (snapshot: copy ")
                    .Append((analysis.Snapshot.CopyMs / analysis.Updates).ToString("F1")).Append(", distance transform ")
                    .Append((analysis.Snapshot.TransformMs / System.Math.Max(1, analysis.ChangedUpdates)).ToString("F1"))
                    .Append(" per changed update).");
            }
        }

        /// <summary>Cells flipped between blocked and passable, by source (each flip makes the next path rebuild the clearance field).</summary>
        static void AppendFlips()
        {
            if (observations == null || lastSimTime <= 0f) return;
            double seconds = lastSimTime;

            line.Append("\n  Map flips (per sim second):");
            int total = 0;
            bool first = true;
            for (int i = 0; i < (int)Control.ObservationMapper.FlipSource.Count; i++)
            {
                var source = (Control.ObservationMapper.FlipSource)i;
                int count = observations.Flips(source);
                total += count;
                if (count == 0) continue;
                line.Append(first ? " " : ", ").Append(source).Append(' ').Append((count / seconds).ToString("F2"));
                first = false;
            }
            line.Append(first ? " none" : "").Append(". Total ").Append((total / seconds).ToString("F2"))
                .Append(". Normal settled floor: ray cells skipped ").Append((observations.SettledSkips / seconds).ToString("F0"))
                .Append(", face hits ignored ").Append((observations.FaceHitsIgnored / seconds).ToString("F1"))
                .Append(", face bumps ignored ").Append(observations.FaceBumpsIgnored).Append(" in the run.");
        }

        /// <summary>The planner's work over the run: counts per simulated second, and real ms per simulated second.</summary>
        static void AppendPlanner()
        {
            if (planner == null || lastSimTime <= 0f) return;
            double seconds = lastSimTime;

            line.Append("\n  Planner (per sim second: calls, ms):");
            double pathMs = 0;
            for (int c = 0; c < (int)Control.PathPlanner.PlanCaller.Count; c++)
            {
                var caller = (Control.PathPlanner.PlanCaller)c;
                pathMs += planner.PathMs(caller);
                line.Append(c == 0 ? " FindPath by " : ", ").Append(caller.ToString().ToLowerInvariant()).Append(' ')
                    .Append((planner.PathCalls(caller) / seconds).ToString("F2")).Append(" / ")
                    .Append((planner.PathMs(caller) / seconds).ToString("F2"));
            }
            line.Append("; grid searches ").Append((planner.GridSearches / seconds).ToString("F2"))
                .Append("; field rebuilds ").Append((planner.FieldRebuilds / seconds).ToString("F2")).Append(" / ")
                .Append((planner.FieldMs / seconds).ToString("F2"))
                .Append(", field updates ").Append((planner.FieldUpdates / seconds).ToString("F2")).Append(" / ")
                .Append((planner.FieldUpdateMs / seconds).ToString("F2")).Append(" (both inside FindPath)")
                .Append("; leg checks ").Append((planner.SegmentChecks / seconds).ToString("F1")).Append(" / ")
                .Append((planner.SegmentMs / seconds).ToString("F2"))
                .Append("; direct clearances ").Append((planner.DirectClearances / seconds).ToString("F0"))
                .Append(". Total ").Append((planner.Milliseconds / seconds).ToString("F1")).Append(" ms per sim second")
                .Append(" (FindPath ").Append((pathMs / seconds).ToString("F1"))
                .Append(", leg checks ").Append((planner.SegmentMs / seconds).ToString("F1")).Append(").");
        }

        static void AppendParts(double[] parts, double minimum)
        {
            SortParts(parts);
            bool first = true;
            foreach (int i in order)
            {
                if (parts[i] < minimum) continue;
                line.Append(first ? " " : ", ").Append(Names[i]).Append(' ').Append(parts[i].ToString("F1"));
                first = false;
            }
        }

        static void SortParts(double[] parts)
        {
            order.Clear();
            for (int i = 0; i < (int)Part.Count; i++) order.Add(i);
            order.Sort((a, b) => parts[b].CompareTo(parts[a]));
        }

        static void Print(string message) =>
            Debug.LogFormat(LogType.Log, LogOption.NoStacktrace, null, "{0}", message);
    }

    /// <summary>Calls <see cref="LagLog.Frame"/> once per frame, before anything else's Update.</summary>
    [DefaultExecutionOrder(-2100)]
    sealed class LagLogFrames : MonoBehaviour
    {
        static LagLogFrames instance;

        public static void Ensure()
        {
            if (instance != null || !Application.isPlaying) return;
            var go = new GameObject("Lag Log") { hideFlags = HideFlags.HideInHierarchy };
            DontDestroyOnLoad(go);
            instance = go.AddComponent<LagLogFrames>();
        }

        void Update() => LagLog.Frame(Time.unscaledDeltaTime);

        void OnDestroy()
        {
            if (instance == this) instance = null;
        }
    }
}
