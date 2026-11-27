using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using RobotVacuum.Sim;
using UnityEngine;

namespace RobotVacuum.LevelEditor
{
    /// <summary>Every measure the comparison can rank by or show in its scorecard.</summary>
    public enum CompareMetric
    {
        Coverage, TimeTo50, TimeTo75, TimeTo90, CoveragePerMinute, AreaPerBattery, AreaPerMetre,
        BatteryUsed, Revisits, RevisitsPerMetre, Distance, SimTime, ReachedNinety, Succeeded, BatteryRanOut,
    }

    /// <summary>Which way is better on a measure. Neutral measures are shown but never called better or worse.</summary>
    public enum Better { Higher, Lower, Neutral }

    /// <summary>How picked runs are grouped: pooled per algorithm, or one group per pick.</summary>
    public enum CompareGrouping { Algorithm, Selection }

    /// <summary>One measure: what it's called, which way is better, how to read it from a run and how to show it.</summary>
    public sealed class MetricInfo
    {
        public CompareMetric id;
        public string label;
        public string description;
        public Better better;

        /// <summary>The run's value, or null when it has none (e.g. never reached 90%).</summary>
        public Func<RunReport, double?> value;
        public Func<double, string> format;
        public Func<double, string> spread;

        /// <summary>True for a rate over every run, failed ones included (success rate). Other measures use successful runs only (2.8).</summary>
        public bool includesFailed;
    }

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

    /// <summary>A set of runs compared as one: statistics on every measure, a mean coverage curve and a mean heatmap.</summary>
    public sealed class CompareGroup
    {
        public string label;
        public readonly List<RunReport> runs = new List<RunReport>();

        /// <summary>The ranking measure over the runs that have a value for it.</summary>
        public Summary metric;

        /// <summary>Coverage, time and battery as the batch statistics table shows them.</summary>
        public AlgorithmSummary stats;

        // Where the curve and map come from: single runs, or a batch algorithm's saved averages (weighted by trial count).
        readonly List<(RunReport run, BatchAlgorithm algorithm, BatchResult batch, int weight)> sources =
            new List<(RunReport, BatchAlgorithm, BatchResult, int)>();
        readonly Dictionary<CompareMetric, Summary> summaries = new Dictionary<CompareMetric, Summary>();

        public void AddRun(RunReport run)
        {
            runs.Add(run);
            sources.Add((run, null, null, 1));
        }

        public void AddBatchRuns(List<RunReport> batchRuns, BatchAlgorithm algorithm, BatchResult batch)
        {
            runs.AddRange(batchRuns);
            if (algorithm != null && batchRuns.Count > 0) sources.Add((null, algorithm, batch, batchRuns.Count));
        }

        /// <summary>The runs a measure is computed over: the successful ones, or all of them for a rate that counts failures.</summary>
        public List<RunReport> RunsFor(MetricInfo info) => info.includesFailed ? runs : runs.FindAll(BatchStats.Succeeded);

        /// <summary>Each counted run's value on <paramref name="metric"/>, alongside the run.</summary>
        public List<(RunReport run, double value)> Values(CompareMetric metric)
        {
            var info = RunComparison.Info(metric);
            var values = new List<(RunReport, double)>();
            foreach (var run in RunsFor(info))
            {
                var value = info.value(run);
                if (value.HasValue && !double.IsNaN(value.Value) && !double.IsInfinity(value.Value)) values.Add((run, value.Value));
            }
            return values;
        }

        /// <summary>Mean, spread and 95% confidence interval on <paramref name="metric"/>, computed once.</summary>
        public Summary Stat(CompareMetric metric)
        {
            if (summaries.TryGetValue(metric, out var summary)) return summary;
            summary = Summary.Of(Values(metric).ConvertAll(v => v.value));
            summaries[metric] = summary;
            return summary;
        }

        /// <summary>The longest run's length: how far the group's curve reaches.</summary>
        public float CurveDuration
        {
            get
            {
                float duration = 0f;
                foreach (var (run, algorithm, _, _) in sources)
                    if (run != null ? run.coverageSamples.Count > 0 : algorithm.curve.Count > 0)
                        duration = Mathf.Max(duration, run != null ? run.durationSeconds : algorithm.curveDuration);
                return duration;
            }
        }

        /// <summary>
        /// Mean coverage at <paramref name="points"/> evenly spaced times up to <paramref name="duration"/>, and the
        /// standard deviation across the runs at each time (empty when no source recorded one). A batch's saved
        /// curve counts once per trial; a run that has ended holds its final value.
        /// </summary>
        public float[] Curve(int points, float duration, out float[] spread)
        {
            var mean = new float[Mathf.Max(2, points)];
            spread = new float[mean.Length];
            int total = 0;
            foreach (var source in sources)
                if (source.run != null ? source.run.coverageSamples.Count > 0 : source.algorithm.curve.Count > 0) total += source.weight;
            if (total == 0 || duration <= 0f) return mean;

            for (int i = 0; i < mean.Length; i++)
            {
                float t = duration * i / (mean.Length - 1);
                double sum = 0, squares = 0;
                foreach (var (run, algorithm, _, weight) in sources)
                {
                    if (run != null ? run.coverageSamples.Count == 0 : algorithm.curve.Count == 0) continue;
                    double m = run != null ? BatchStats.CoverageAt(run, t) : algorithm.CoverageAt(t);
                    double sd = run != null ? 0 : algorithm.SpreadAt(t);
                    sum += weight * m;
                    squares += weight * (sd * sd + m * m); // pooled: each source's variance plus its mean's distance
                }
                double average = sum / total;
                mean[i] = (float)average;
                spread[i] = (float)Math.Sqrt(Math.Max(0, squares / total - average * average));
            }
            return mean;
        }

        /// <summary>
        /// The group's mean coverage map: each source's map (a run's own, or a batch algorithm's average) weighted by
        /// its runs. Only maps the same shape as the first are averaged. Null when none was saved.
        /// </summary>
        public float[] Map(out int width, out int height, out Rect rect)
        {
            width = height = 0;
            rect = default;
            float[] sums = null;
            float[] weights = null;

            foreach (var (run, algorithm, batch, weight) in sources)
            {
                int w = run != null ? run.mapWidth : batch.mapWidth;
                int h = run != null ? run.mapHeight : batch.mapHeight;
                var values = BatchStats.DecodeMap(run != null ? run.coverageMap : algorithm.map, w * h);
                if (values == null) continue;

                if (sums == null)
                {
                    width = w;
                    height = h;
                    rect = run != null ? run.mapRect : batch.mapRect;
                    sums = new float[values.Length];
                    weights = new float[values.Length];
                }
                else if (w != width || h != height) continue;

                for (int i = 0; i < values.Length; i++)
                {
                    if (values[i] < 0f) continue;
                    sums[i] += values[i] * weight;
                    weights[i] += weight;
                }
            }

            if (sums == null) return null;
            for (int i = 0; i < sums.Length; i++) sums[i] = weights[i] > 0f ? sums[i] / weights[i] : -1f;
            return sums;
        }
    }

    /// <summary>Two groups on the seeds they share: who did better on each, and the paired difference.</summary>
    public sealed class HeadToHead
    {
        public int shared;
        public int wins;
        public int ties;
        public int losses;

        /// <summary>Per shared seed, A minus B, signed so that positive means A did better (raw for a neutral measure).</summary>
        public Summary difference;

        /// <summary>True when the paired difference's 95% interval is clear of zero in A's favour.</summary>
        public bool ClearlyBetter => difference.count >= 2 && difference.Low > 0;

        public bool ClearlyWorse => difference.count >= 2 && difference.High < 0;
    }

    /// <summary>A room or floor type, with each group's mean on it, aligned with the groups.</summary>
    public sealed class AreaRow
    {
        public string name;
        public float area;
        public Summary[] perGroup;
    }

    /// <summary>The comparison's answer: which group leads, and whether the lead is more than chance.</summary>
    public readonly struct CompareVerdict
    {
        public readonly string headline;
        public readonly string detail;

        /// <summary>True when the leader is ahead by more than chance would explain.</summary>
        public readonly bool clear;

        public CompareVerdict(string headline, string detail, bool clear)
        {
            this.headline = headline;
            this.detail = detail;
            this.clear = clear;
        }
    }

    /// <summary>
    /// Compares any mix of single runs and batches on one floor plan, picked on the Analysis page: groups them, ranks
    /// the groups on any measure with mean and 95% confidence interval, pairs them up on shared seeds, says whether the
    /// leader is clearly ahead, and points out differences in conditions that could explain the result.
    /// </summary>
    public static class RunComparison
    {
        static readonly Dictionary<CompareMetric, MetricInfo> infos = BuildInfos();

        /// <summary>Every measure, in the order the scorecard lists them.</summary>
        public static readonly CompareMetric[] Metrics =
        {
            CompareMetric.Coverage, CompareMetric.TimeTo50, CompareMetric.TimeTo75, CompareMetric.TimeTo90,
            CompareMetric.CoveragePerMinute, CompareMetric.AreaPerBattery, CompareMetric.AreaPerMetre, CompareMetric.BatteryUsed,
            CompareMetric.Revisits, CompareMetric.RevisitsPerMetre, CompareMetric.Distance, CompareMetric.SimTime,
            CompareMetric.ReachedNinety, CompareMetric.Succeeded, CompareMetric.BatteryRanOut,
        };

        public static MetricInfo Info(CompareMetric metric) => infos[metric];

        public static string Label(CompareMetric metric) => infos[metric].label;

        public static bool HigherIsBetter(CompareMetric metric) => infos[metric].better != Better.Lower;

        public static double? Value(RunReport run, CompareMetric metric)
        {
            var info = infos[metric];
            if (!info.includesFailed && !BatchStats.Succeeded(run)) return null;
            return info.value(run);
        }

        public static string Format(CompareMetric metric, double value) => infos[metric].format(value);

        /// <summary>The ± part, in the measure's unit.</summary>
        public static string FormatSpread(CompareMetric metric, double ci) => infos[metric].spread(ci);

        static Dictionary<CompareMetric, MetricInfo> BuildInfos()
        {
            var list = new[]
            {
                new MetricInfo
                {
                    id = CompareMetric.Coverage, label = "Coverage", better = Better.Higher,
                    description = "Share of the cleanable floor cleaned to the target by the end of the run.",
                    value = r => r.coveragePercent, format = v => $"{v:0.0}%", spread = v => $"{v:0.0}",
                },
                Milestone(CompareMetric.TimeTo50, "Time to 50%", r => r.timeTo50),
                Milestone(CompareMetric.TimeTo75, "Time to 75%", r => r.timeTo75),
                Milestone(CompareMetric.TimeTo90, "Time to 90%", r => r.timeTo90),
                new MetricInfo
                {
                    id = CompareMetric.CoveragePerMinute, label = "Coverage per minute", better = Better.Higher,
                    description = "Final coverage over the run's simulated minutes: how fast it got there on average.",
                    value = r => r.durationSeconds > 0f ? r.CoveragePerMinute : (double?)null,
                    format = v => $"{v:0.0}%/min", spread = v => $"{v:0.0}",
                },
                new MetricInfo
                {
                    id = CompareMetric.AreaPerBattery, label = "Area per battery unit", better = Better.Higher,
                    description = "Floor cleaned for each battery work unit spent. Higher means the charge goes further.",
                    value = r => r.batteryUsed > 0f ? r.CleanedArea / r.batteryUsed : (double?)null,
                    format = v => $"{Ui.FormatArea((float)v)}/unit", spread = v => Ui.FormatArea((float)v),
                },
                new MetricInfo
                {
                    id = CompareMetric.AreaPerMetre, label = "Effective cleaning width", better = Better.Higher,
                    description = "Floor cleaned per metre driven, which works out as a width: how much new floor each metre of path " +
                                  "picked up. Close to the vacuum's own width means little driving was wasted on cleaned floor.",
                    value = r => r.distanceMeters > 0f ? r.CleanedArea / r.distanceMeters : (double?)null,
                    format = v => Ui.FormatLength((float)v), spread = v => Ui.FormatLength((float)v),
                },
                new MetricInfo
                {
                    id = CompareMetric.BatteryUsed, label = "Battery used", better = Better.Lower,
                    description = "Battery work units used over the whole run.",
                    value = r => r.batteryUsed, format = v => $"{v:0} units", spread = v => $"{v:0}",
                },
                new MetricInfo
                {
                    id = CompareMetric.Revisits, label = "Revisits", better = Better.Lower,
                    description = "Times the vacuum went back over floor it had already covered and left.",
                    value = r => r.revisitCount, format = v => $"{v:N0}", spread = v => $"{v:N0}",
                },
                new MetricInfo
                {
                    id = CompareMetric.RevisitsPerMetre, label = "Revisits per metre", better = Better.Lower,
                    description = "Revisits per metre driven: how much of the path retraced old ground.",
                    value = r => r.distanceMeters > 0f ? r.revisitCount / r.distanceMeters : (double?)null,
                    format = v => $"{v:0.00}", spread = v => $"{v:0.00}",
                },
                new MetricInfo
                {
                    id = CompareMetric.Distance, label = "Path length", better = Better.Neutral,
                    description = "Distance driven. Neither better nor worse on its own: see area per metre.",
                    value = r => r.distanceMeters, format = v => Ui.FormatLength((float)v), spread = v => Ui.FormatLength((float)v),
                },
                new MetricInfo
                {
                    id = CompareMetric.SimTime, label = "Run length", better = Better.Neutral,
                    description = "Simulated time until the run ended: battery, time limit, fully clean or stopped.",
                    value = r => r.durationSeconds, format = v => VacuumSettings.FormatDuration((float)v), spread = v => $"{v:0}s",
                },
                Rate(CompareMetric.ReachedNinety, "Reached 90%", Better.Higher, false,
                    "Share of successful runs that cleaned 90% of the floor.", r => r.timeTo90 >= 0f),
                Rate(CompareMetric.Succeeded, "Succeeded", Better.Higher, true,
                    "Share of runs whose algorithm didn't fail. Failed runs are left out of every other measure.", BatchStats.Succeeded),
                Rate(CompareMetric.BatteryRanOut, "Battery ran out", Better.Neutral, false,
                    "Share of successful runs that ended on an empty battery rather than finishing.", r => r.batteryEmptied),
            };

            var result = new Dictionary<CompareMetric, MetricInfo>();
            foreach (var info in list) result[info.id] = info;
            return result;
        }

        static MetricInfo Milestone(CompareMetric id, string label, Func<RunReport, float> time) => new MetricInfo
        {
            id = id, label = label, better = Better.Lower,
            description = $"Simulated time until coverage first reached {label.Substring(8)}. Runs that never did are left out; see Reached 90%.",
            value = r => time(r) >= 0f ? time(r) : (double?)null,
            format = v => VacuumSettings.FormatDuration((float)v), spread = v => $"{v:0}s",
        };

        static MetricInfo Rate(CompareMetric id, string label, Better better, bool includesFailed, string description, Predicate<RunReport> test) =>
            new MetricInfo
            {
                id = id, label = label, better = better, includesFailed = includesFailed, description = description,
                value = r => test(r) ? 100 : 0, format = v => $"{v:0}%", spread = v => $"{v:0}",
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
                        if (Switched(run)) continue;
                        Group(run.AlgorithmKey, run.AlgorithmName).AddRun(run);
                    }
                    else Group(run.id, $"{run.AlgorithmName}{(Switched(run) ? " (switched)" : "")} · seed {run.seed}").AddRun(run);
                    continue;
                }

                var batch = source.batch;
                var reports = batch.Reports();
                var keys = new List<string>();
                foreach (var run in reports)
                    if (!keys.Contains(run.AlgorithmKey)) keys.Add(run.AlgorithmKey);

                foreach (string key in keys)
                {
                    var trials = reports.FindAll(run => run.AlgorithmKey == key);
                    string algorithm = trials[0].AlgorithmName;
                    string pick = batch.IsImported ? $"{batch.SourceLabel} {batch.campaignName}" : $"Batch {batch.FinishedAt:d MMM HH:mm}";
                    var group = grouping == CompareGrouping.Algorithm
                        ? Group(key, algorithm)
                        : Group($"{batch.batchId}|{key}", $"{pick} · {algorithm}");
                    group.AddBatchRuns(trials, batch.Algorithm(key), batch);
                }
            }

            foreach (var group in groups)
            {
                group.metric = group.Stat(metric);
                group.stats = BatchStats.SummariseGroup(group.label, group.runs);
            }

            Rank(groups, metric);
            return groups;
        }

        /// <summary>Best first on <paramref name="metric"/>; groups with no value on it go last.</summary>
        public static void Rank(List<CompareGroup> groups, CompareMetric metric)
        {
            var better = infos[metric].better;
            foreach (var group in groups) group.metric = group.Stat(metric);
            groups.Sort((a, b) =>
            {
                if (a.metric.count == 0 || b.metric.count == 0) return b.metric.count.CompareTo(a.metric.count);
                return better == Better.Lower ? a.metric.mean.CompareTo(b.metric.mean) : b.metric.mean.CompareTo(a.metric.mean);
            });
        }

        /// <summary>
        /// True for a run that switched algorithm part-way (2.16). It is filed under the algorithm it started with,
        /// but says nothing about any one algorithm, so grouping by algorithm leaves it out.
        /// </summary>
        public static bool Switched(RunReport run) => run.algorithmSwitches != null && run.algorithmSwitches.Count > 0;

        /// <summary>
        /// A and B on the seeds both ran. A seed run more than once in a group (two batches on the same seeds) counts
        /// its mean. Equal results within a hair are ties.
        /// </summary>
        public static HeadToHead Compare(CompareGroup a, CompareGroup b, CompareMetric metric)
        {
            var bySeedA = BySeed(a, metric);
            var bySeedB = BySeed(b, metric);
            var better = infos[metric].better;
            var result = new HeadToHead();
            var differences = new List<double>();

            foreach (var pair in bySeedA)
            {
                if (!bySeedB.TryGetValue(pair.Key, out double other)) continue;
                double difference = better == Better.Lower ? other - pair.Value : pair.Value - other;
                differences.Add(difference);
                result.shared++;
                if (Math.Abs(difference) <= 1e-6 * Math.Max(1, Math.Abs(pair.Value))) result.ties++;
                else if (difference > 0) result.wins++;
                else result.losses++;
            }

            result.difference = Summary.Of(differences);
            return result;
        }

        static Dictionary<int, double> BySeed(CompareGroup group, CompareMetric metric)
        {
            var sums = new Dictionary<int, (double sum, int count)>();
            foreach (var (run, value) in group.Values(metric))
            {
                sums.TryGetValue(run.seed, out var s);
                sums[run.seed] = (s.sum + value, s.count + 1);
            }

            var result = new Dictionary<int, double>();
            foreach (var pair in sums) result[pair.Key] = pair.Value.sum / pair.Value.count;
            return result;
        }

        /// <summary>
        /// Which group leads on <paramref name="metric"/>, and whether the lead is more than chance. When the top two
        /// share seeds the paired difference decides (far more sensitive: each seed is the same floor and the same
        /// luck for both); otherwise their 95% confidence intervals must not overlap.
        /// </summary>
        public static CompareVerdict Judge(IReadOnlyList<CompareGroup> groups, CompareMetric metric)
        {
            var info = infos[metric];
            string name = info.label.ToLowerInvariant();
            if (groups.Count == 0 || groups[0].metric.count == 0)
                return new CompareVerdict("Nothing to rank yet", $"None of the picked runs has a value for {name}.", false);

            var best = groups[0];
            if (groups.Count == 1 || groups[1].metric.count == 0)
                return new CompareVerdict($"{best.label} is the only group with a result", "Pick runs of other algorithms to compare against.", false);

            var second = groups[1];
            string numbers = $"{best.label} {Format(metric, best.metric.mean)}{Spread(metric, best.metric)} vs " +
                             $"{second.label} {Format(metric, second.metric.mean)}{Spread(metric, second.metric)}.";

            if (info.better == Better.Neutral)
                return new CompareVerdict($"{best.label} is highest on {name}",
                    numbers + " This measure isn't better or worse on its own, so rank by another to pick a winner.", false);

            var paired = Compare(best, second, metric);
            if (paired.shared >= 2)
            {
                string pairs = $" On the {paired.shared} seeds both ran, {best.label} did better on {paired.wins}" +
                               (paired.ties > 0 ? $", tied on {paired.ties}" : "") +
                               $" and worse on {paired.losses}, by {FormatSpread(metric, paired.difference.mean)} ± {FormatSpread(metric, paired.difference.ci95)} on average.";
                return paired.ClearlyBetter
                    ? new CompareVerdict($"{best.label} is best on {name}",
                        numbers + pairs + " The paired 95% confidence interval is clear of zero, so the lead is very unlikely to be chance.", true)
                    : new CompareVerdict($"{best.label} leads on {name}, but not clearly",
                        numbers + pairs + " The paired 95% confidence interval includes zero, so the difference could be chance. More seeds would settle it.", false);
            }

            if (best.metric.count < 2 || second.metric.count < 2)
                return new CompareVerdict($"{best.label} leads on {name}, from too few successful runs to be sure",
                    numbers + " With one run per group the difference could be chance. Compare batches, which run each algorithm on many seeds.", false);

            bool clear = info.better == Better.Higher ? best.metric.Low > second.metric.High : best.metric.High < second.metric.Low;
            return clear
                ? new CompareVerdict($"{best.label} is best on {name}",
                    numbers + " The 95% confidence intervals don't overlap, so the lead is very unlikely to be chance.", true)
                : new CompareVerdict($"{best.label} leads on {name}, but not clearly",
                    numbers + " The 95% confidence intervals overlap, so the difference could be chance. Running both on the same seeds, or more seeds, would settle it.", false);
        }

        static string Spread(CompareMetric metric, Summary summary) =>
            summary.count > 1 ? $" ± {FormatSpread(metric, summary.ci95)}" : string.Empty;

        /// <summary>Minimum, lower quartile, median, upper quartile and maximum of <paramref name="values"/>.</summary>
        public static (double min, double q1, double median, double q3, double max) Quartiles(List<double> values)
        {
            if (values.Count == 0) return (0, 0, 0, 0, 0);
            var sorted = new List<double>(values);
            sorted.Sort();

            double At(double p)
            {
                double position = p * (sorted.Count - 1);
                int index = (int)Math.Floor(position);
                int next = Math.Min(index + 1, sorted.Count - 1);
                return sorted[index] + (sorted[next] - sorted[index]) * (position - index);
            }

            return (sorted[0], At(0.25), At(0.5), At(0.75), sorted[sorted.Count - 1]);
        }

        /// <summary>
        /// Each room's mean over each group's successful runs, matched by room name. <paramref name="measure"/> picks
        /// what: coverage, battery or time per room.
        /// </summary>
        public static List<AreaRow> Rooms(IReadOnlyList<CompareGroup> groups, Func<RunReport, int, float> measure)
        {
            var rows = new List<AreaRow>();
            var index = new Dictionary<string, (AreaRow row, List<double>[] values)>();
            for (int g = 0; g < groups.Count; g++)
            {
                foreach (var run in groups[g].runs.FindAll(BatchStats.Succeeded))
                {
                    if (!run.HasRoomEnergy) continue;
                    for (int i = 0; i < run.roomNames.Count; i++)
                    {
                        string name = run.roomNames[i];
                        if (!index.TryGetValue(name, out var entry))
                        {
                            entry = (new AreaRow { name = name, area = run.roomArea[i] }, NewLists(groups.Count));
                            index[name] = entry;
                            rows.Add(entry.row);
                        }
                        entry.values[g].Add(measure(run, i));
                    }
                }
            }

            foreach (var (row, values) in index.Values) row.perGroup = Array.ConvertAll(values, v => Summary.Of(v));
            return rows;
        }

        /// <summary>Each floor type's mean coverage over each group's successful runs.</summary>
        public static List<AreaRow> Floors(IReadOnlyList<CompareGroup> groups)
        {
            var rows = new List<AreaRow>();
            var index = new Dictionary<string, (AreaRow row, List<double>[] values)>();
            for (int g = 0; g < groups.Count; g++)
            {
                foreach (var run in groups[g].runs.FindAll(BatchStats.Succeeded))
                {
                    if (run.floorNames == null || run.floorCoverage == null || run.floorNames.Count != run.floorCoverage.Count) continue;
                    for (int i = 0; i < run.floorNames.Count; i++)
                    {
                        string name = run.floorNames[i];
                        if (!index.TryGetValue(name, out var entry))
                        {
                            entry = (new AreaRow { name = name }, NewLists(groups.Count));
                            index[name] = entry;
                            rows.Add(entry.row);
                        }
                        entry.values[g].Add(run.floorCoverage[i]);
                    }
                }
            }

            foreach (var (row, values) in index.Values) row.perGroup = Array.ConvertAll(values, v => Summary.Of(v));
            return rows;
        }

        static List<double>[] NewLists(int count)
        {
            var lists = new List<double>[count];
            for (int i = 0; i < count; i++) lists[i] = new List<double>();
            return lists;
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
            var configurations = new HashSet<string>();
            int mixed = 0;
            foreach (var source in sources)
            {
                var v = source.Vacuum;
                if (v != null) vacuums.Add($"{v.driveSpeed:0.###}/{v.reverseSpeed:0.###}/{v.turnSpeed:0}/{v.batteryCapacity:0}");
                if (source.CellSize > 0f) cells.Add($"{source.CellSize:0.####}");
                string configuration = source.single != null ? source.single.configuration : source.batch.configuration;
                if (!string.IsNullOrEmpty(configuration)) configurations.Add(configuration);
                if (source.single != null && Switched(source.single)) mixed++;
            }

            if (vacuums.Count > 1)
                caveats.Add("The vacuum's speed or battery settings differ between these runs.");
            if (configurations.Count > 1)
                caveats.Add($"These runs come from {configurations.Count} different campaign configurations.");
            if (cells.Count > 1)
                caveats.Add("The coverage grid cell size differs between these runs.");
            if (mixed > 0 && grouping == CompareGrouping.Algorithm)
                caveats.Add($"{mixed} run(s) switched algorithm part-way and are left out when grouping by algorithm.");
            return caveats;
        }

        /// <summary>
        /// The comparison as CSV: one row per run with its group and every measure, then one row per group and
        /// measure with n, mean, SD and the 95% confidence interval. Invariant culture, raw numbers (metres, seconds).
        /// </summary>
        public static string ToCsv(IReadOnlyList<CompareGroup> groups)
        {
            var csv = new StringBuilder();
            csv.Append("section,group,run_id,seed,succeeded");
            foreach (var metric in Metrics) csv.Append(',').Append(Column(metric));
            csv.AppendLine();
            foreach (var group in groups)
            {
                foreach (var run in group.runs)
                {
                    csv.Append("run,").Append(Field(group.label)).Append(',').Append(run.id).Append(',')
                        .Append(run.seed.ToString(CultureInfo.InvariantCulture)).Append(',').Append(BatchStats.Succeeded(run) ? "true" : "false");
                    foreach (var metric in Metrics)
                    {
                        var value = Value(run, metric);
                        csv.Append(',').Append(value.HasValue ? value.Value.ToString("R", CultureInfo.InvariantCulture) : string.Empty);
                    }
                    csv.AppendLine();
                }
            }

            csv.AppendLine();
            csv.AppendLine("section,group,measure,better,n,mean,sd,ci95_half_width,ci95_low,ci95_high");
            foreach (var group in groups)
            {
                foreach (var metric in Metrics)
                {
                    var s = group.Stat(metric);
                    csv.AppendLine(string.Join(",", "summary", Field(group.label), Column(metric), infos[metric].better.ToString().ToLowerInvariant(),
                        s.count.ToString(CultureInfo.InvariantCulture), N(s.mean), N(s.StandardDeviation), N(s.ci95), N(s.Low), N(s.High)));
                }
            }
            return csv.ToString();
        }

        static string Column(CompareMetric metric) => metric switch
        {
            CompareMetric.Coverage => "coverage_pct",
            CompareMetric.TimeTo50 => "time_to_50_s",
            CompareMetric.TimeTo75 => "time_to_75_s",
            CompareMetric.TimeTo90 => "time_to_90_s",
            CompareMetric.CoveragePerMinute => "coverage_pct_per_min",
            CompareMetric.AreaPerBattery => "area_m2_per_battery_unit",
            CompareMetric.AreaPerMetre => "area_m2_per_m",
            CompareMetric.BatteryUsed => "battery_used",
            CompareMetric.Revisits => "revisits",
            CompareMetric.RevisitsPerMetre => "revisits_per_m",
            CompareMetric.Distance => "distance_m",
            CompareMetric.SimTime => "sim_time_s",
            CompareMetric.ReachedNinety => "reached_90_pct",
            CompareMetric.Succeeded => "succeeded_pct",
            _ => "battery_ran_out_pct",
        };

        static string N(double value) => value.ToString("R", CultureInfo.InvariantCulture);

        static string Field(string text)
        {
            text ??= string.Empty;
            return text.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0 ? text : "\"" + text.Replace("\"", "\"\"") + "\"";
        }
    }
}
