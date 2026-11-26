using System;
using System.Collections.Generic;
using RobotVacuum.Sim;
using UnityEngine;

namespace RobotVacuum.LevelEditor
{
    /// <summary>What the comparison ranks groups by.</summary>
    public enum CompareMetric { Coverage, TimeTo90, CoveragePerMinute, AreaPerBattery, BatteryUsed }

    /// <summary>How picked runs are grouped: pooled per algorithm, or one group per pick.</summary>
    public enum CompareGrouping { Algorithm, Selection }

    /// <summary>One thing picked for comparison on the Analysis page: a single run or a whole batch.</summary>
    public sealed class CompareSource
    {
        public RunReport single;
        public BatchResult batch;

        public string Id => single != null ? single.id : batch.batchId;
        public string LevelName => single != null ? single.levelName : batch.levelName;
        public string LevelPath => single != null ? single.levelPath : batch.levelPath;
        public VacuumSettings Vacuum => single != null ? single.vacuum : batch.vacuum;
        public float CellSize => single != null ? single.cellSize : batch.cellSize;
        public int RunCount => single != null ? 1 : batch.trials.Count;

        /// <summary>Which floor plan this ran on: its file when it has one, its name otherwise. Only one plan is compared at a time.</summary>
        public string PlanKey => !string.IsNullOrEmpty(LevelPath) ? LevelPath : LevelName;

        public static CompareSource Of(RunReport run) => new CompareSource { single = run };
        public static CompareSource Of(BatchResult batch) => new CompareSource { batch = batch };
    }

    /// <summary>A set of runs compared as one: their statistics on the chosen metric and a mean coverage curve.</summary>
    public sealed class CompareGroup
    {
        public string label;
        public readonly List<RunReport> runs = new List<RunReport>();

        /// <summary>The chosen metric over the runs that have a value for it.</summary>
        public Summary metric;

        /// <summary>Coverage, time, battery and the rest, as the batch table shows them.</summary>
        public AlgorithmSummary stats;

        readonly List<(RunReport run, BatchAlgorithm algorithm, int weight)> curves = new List<(RunReport, BatchAlgorithm, int)>();

        public void AddRun(RunReport run)
        {
            runs.Add(run);
            curves.Add((run, null, 1));
        }

        public void AddBatchRuns(List<RunReport> batchRuns, BatchAlgorithm algorithm)
        {
            runs.AddRange(batchRuns);
            if (algorithm != null && algorithm.curve.Count > 0 && batchRuns.Count > 0) curves.Add((null, algorithm, batchRuns.Count));
        }

        /// <summary>The longest run's length: how far the group's curve reaches.</summary>
        public float CurveDuration
        {
            get
            {
                float duration = 0f;
                foreach (var (run, algorithm, _) in curves)
                    duration = Mathf.Max(duration, run != null ? run.durationSeconds : algorithm.curveDuration);
                return duration;
            }
        }

        /// <summary>
        /// Mean coverage at <paramref name="points"/> evenly spaced times up to <paramref name="duration"/>,
        /// weighting a batch's averaged curve by its trial count. A run that has ended holds its final value.
        /// </summary>
        public float[] Curve(int points, float duration)
        {
            var curve = new float[Mathf.Max(2, points)];
            int total = 0;
            foreach (var entry in curves) total += entry.weight;
            if (total == 0 || duration <= 0f) return curve;

            for (int i = 0; i < curve.Length; i++)
            {
                float t = duration * i / (curve.Length - 1);
                float sum = 0f;
                foreach (var (run, algorithm, weight) in curves)
                    sum += weight * (run != null ? BatchStats.CoverageAt(run, t) : algorithm.CoverageAt(t));
                curve[i] = sum / total;
            }
            return curve;
        }
    }

    /// <summary>The comparison's answer: which group leads, and whether the lead is more than chance.</summary>
    public readonly struct CompareVerdict
    {
        public readonly string headline;
        public readonly string detail;

        /// <summary>True when the leader's 95% interval clears the runner-up's.</summary>
        public readonly bool clear;

        public CompareVerdict(string headline, string detail, bool clear)
        {
            this.headline = headline;
            this.detail = detail;
            this.clear = clear;
        }
    }

    /// <summary>
    /// Compares any mix of single runs and batches on one floor plan, picked on the Analysis page: groups them, ranks the
    /// groups on a metric with mean and 95% confidence interval, says whether the leader is clearly ahead,
    /// and points out differences in conditions that could explain the result.
    /// </summary>
    public static class RunComparison
    {
        public static readonly CompareMetric[] Metrics =
        {
            CompareMetric.Coverage, CompareMetric.TimeTo90, CompareMetric.CoveragePerMinute,
            CompareMetric.AreaPerBattery, CompareMetric.BatteryUsed,
        };

        public static string Label(CompareMetric metric) => metric switch
        {
            CompareMetric.TimeTo90 => "Time to 90%",
            CompareMetric.CoveragePerMinute => "Coverage per minute",
            CompareMetric.AreaPerBattery => "Area per battery unit",
            CompareMetric.BatteryUsed => "Battery used",
            _ => "Coverage",
        };

        public static bool HigherIsBetter(CompareMetric metric) =>
            metric != CompareMetric.TimeTo90 && metric != CompareMetric.BatteryUsed;

        /// <summary>The run's value on <paramref name="metric"/>, or null when it has none (e.g. never reached 90%).</summary>
        public static double? Value(RunReport run, CompareMetric metric) => metric switch
        {
            CompareMetric.TimeTo90 => run.timeTo90 >= 0f ? run.timeTo90 : (double?)null,
            CompareMetric.CoveragePerMinute => run.durationSeconds > 0f ? run.CoveragePerMinute : (double?)null,
            CompareMetric.AreaPerBattery => run.batteryUsed > 0f ? run.CleanedArea / run.batteryUsed : (double?)null,
            CompareMetric.BatteryUsed => run.batteryUsed,
            _ => run.coveragePercent,
        };

        public static string Format(CompareMetric metric, double value) => metric switch
        {
            CompareMetric.TimeTo90 => VacuumSettings.FormatDuration((float)value),
            CompareMetric.CoveragePerMinute => $"{value:0.0}%/min",
            CompareMetric.AreaPerBattery => $"{Ui.FormatArea((float)value)}/unit",
            CompareMetric.BatteryUsed => $"{value:0} units",
            _ => $"{value:0.0}%",
        };

        /// <summary>The ± part, in the metric's unit.</summary>
        public static string FormatSpread(CompareMetric metric, double ci) => metric switch
        {
            CompareMetric.TimeTo90 => $"{ci:0}s",
            CompareMetric.CoveragePerMinute => $"{ci:0.0}",
            CompareMetric.AreaPerBattery => Ui.FormatArea((float)ci),
            CompareMetric.BatteryUsed => $"{ci:0}",
            _ => $"{ci:0.0}",
        };

        /// <summary>Groups the picked runs and ranks the groups on <paramref name="metric"/>, best first.</summary>
        public static List<CompareGroup> Build(IEnumerable<CompareSource> sources, CompareGrouping grouping, CompareMetric metric)
        {
            var groups = new List<CompareGroup>();
            var byKey = new Dictionary<string, CompareGroup>();

            CompareGroup Group(string key, string label)
            {
                if (!byKey.TryGetValue(key, out var group))
                {
                    byKey[key] = group = new CompareGroup { label = label };
                    groups.Add(group);
                }
                return group;
            }

            foreach (var source in sources)
            {
                if (source.single != null)
                {
                    var run = source.single;
                    if (grouping == CompareGrouping.Algorithm)
                    {
                        // A run that switched algorithm part-way says nothing about any one algorithm.
                        if (run.mixedPatterns) continue;
                        Group(run.pattern.ToString(), MovementAlgorithms.LabelOf(run.pattern)).AddRun(run);
                    }
                    else Group(run.id, $"{run.PatternLabel} · seed {run.seed}").AddRun(run);
                    continue;
                }

                var batch = source.batch;
                var reports = batch.Reports();
                var patterns = new List<MovementPattern>();
                foreach (var run in reports)
                    if (!patterns.Contains(run.pattern)) patterns.Add(run.pattern);

                foreach (var pattern in patterns)
                {
                    var trials = reports.FindAll(run => run.pattern == pattern);
                    string algorithm = MovementAlgorithms.LabelOf(pattern);
                    var group = grouping == CompareGrouping.Algorithm
                        ? Group(pattern.ToString(), algorithm)
                        : Group($"{batch.batchId}/{pattern}", $"Batch {batch.FinishedAt:d MMM HH:mm} · {algorithm}");
                    group.AddBatchRuns(trials, batch.Algorithm(pattern));
                }
            }

            foreach (var group in groups)
            {
                var values = new List<double>();
                foreach (var run in group.runs)
                {
                    var value = Value(run, metric);
                    if (value.HasValue) values.Add(value.Value);
                }
                group.metric = Summary.Of(values);
                group.stats = BatchStats.SummariseGroup(group.label, group.runs);
            }

            bool higher = HigherIsBetter(metric);
            groups.Sort((a, b) =>
            {
                // Groups with no value on the metric (e.g. none reached 90%) go last.
                if (a.metric.count == 0 || b.metric.count == 0) return b.metric.count.CompareTo(a.metric.count);
                return higher ? b.metric.mean.CompareTo(a.metric.mean) : a.metric.mean.CompareTo(b.metric.mean);
            });
            return groups;
        }

        /// <summary>Which group leads on <paramref name="metric"/>, and whether its 95% interval clears the runner-up's.</summary>
        public static CompareVerdict Judge(IReadOnlyList<CompareGroup> groups, CompareMetric metric)
        {
            string name = Label(metric).ToLowerInvariant();
            if (groups.Count == 0 || groups[0].metric.count == 0)
                return new CompareVerdict("Nothing to rank yet", $"None of the picked runs has a value for {name}.", false);

            var best = groups[0];
            if (groups.Count == 1 || groups[1].metric.count == 0)
                return new CompareVerdict($"{best.label} is the only group with a result",
                    "Pick runs of other algorithms to compare against.", false);

            var second = groups[1];
            string numbers = $"{best.label} {Format(metric, best.metric.mean)} ± {FormatSpread(metric, best.metric.ci95)} vs " +
                             $"{second.label} {Format(metric, second.metric.mean)} ± {FormatSpread(metric, second.metric.ci95)}.";

            if (best.metric.count < 2 || second.metric.count < 2)
                return new CompareVerdict($"{best.label} leads on {name}, from too few runs to be sure",
                    numbers + " With one run per group the difference could be chance. Compare batches, which run each algorithm on many seeds.", false);

            bool higher = HigherIsBetter(metric);
            bool clear = higher
                ? best.metric.mean - best.metric.ci95 > second.metric.mean + second.metric.ci95
                : best.metric.mean + best.metric.ci95 < second.metric.mean - second.metric.ci95;

            return clear
                ? new CompareVerdict($"{best.label} is best on {name}",
                    numbers + " The 95% confidence intervals don't overlap, so the lead is very unlikely to be chance.", true)
                : new CompareVerdict($"{best.label} leads on {name}, but not clearly",
                    numbers + " The 95% confidence intervals overlap, so the difference could be chance. More seeds would settle it.", false);
        }

        /// <summary>
        /// Differences between the picked runs, other than algorithm, that could explain a result. They are
        /// always on one floor plan (the Analysis page only lets one plan be picked), so that isn't checked.
        /// </summary>
        public static List<string> Caveats(IReadOnlyList<CompareSource> sources, CompareGrouping grouping)
        {
            var caveats = new List<string>();
            if (sources.Count < 2) return caveats;

            var vacuums = new HashSet<string>();
            var cells = new HashSet<string>();
            int mixed = 0;
            foreach (var source in sources)
            {
                var v = source.Vacuum;
                if (v != null) vacuums.Add($"{v.driveSpeed:0.###}/{v.reverseSpeed:0.###}/{v.turnSpeed:0}/{v.batteryCapacity:0}");
                if (source.CellSize > 0f) cells.Add($"{source.CellSize:0.####}");
                if (source.single != null && source.single.mixedPatterns) mixed++;
            }

            if (vacuums.Count > 1)
                caveats.Add("The vacuum's speed or battery settings differ between these runs.");
            if (cells.Count > 1)
                caveats.Add("The coverage grid cell size differs between these runs.");
            if (mixed > 0 && grouping == CompareGrouping.Algorithm)
                caveats.Add($"{mixed} run(s) switched algorithm part-way and are left out when grouping by algorithm.");
            return caveats;
        }
    }
}
