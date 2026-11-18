using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using RobotVacuum.Sim;
using UnityEngine;

namespace RobotVacuum.LevelEditor
{
    /// <summary>One measure a report compares algorithms on.</summary>
    public sealed class ReportMetric
    {
        public readonly string key;
        public readonly string label;
        public readonly string unit;
        public readonly bool higherIsBetter;

        /// <summary>True when some runs have no value yet (never reached the milestone), read as +∞.</summary>
        public readonly bool censored;

        readonly Func<RunReport, double> read;

        public ReportMetric(string key, string label, string unit, bool higherIsBetter, Func<RunReport, double> read, bool censored = false)
        {
            this.key = key;
            this.label = label;
            this.unit = unit;
            this.higherIsBetter = higherIsBetter;
            this.read = read;
            this.censored = censored;
        }

        public double Read(RunReport run) => read(run);

        public string Format(double value)
        {
            if (double.IsNaN(value)) return "–";
            if (double.IsPositiveInfinity(value)) return "not reached";
            switch (unit)
            {
                case "%": return Invariant($"{value:0.0}%");
                case "s": return VacuumSettings.FormatDuration((float)value);
                case "%/min": return Invariant($"{value:0.00} %/min");
                case "m²/unit": return Invariant($"{value:0.000} m²/unit");
                case "": return Invariant($"{value:0}");
                default: return Invariant($"{value:0.#} {unit}");
            }
        }

        /// <summary>A signed difference in this metric's units, e.g. "+4.2 pts".</summary>
        public string FormatDifference(double value)
        {
            if (double.IsNaN(value)) return "–";
            if (double.IsInfinity(value)) return value > 0 ? "+∞" : "−∞";
            string sign = value >= 0 ? "+" : "−";
            double a = Math.Abs(value);
            switch (unit)
            {
                case "%": return Invariant($"{sign}{a:0.0} pts");
                case "s": return $"{sign}{VacuumSettings.FormatDuration((float)a)}";
                case "%/min": return Invariant($"{sign}{a:0.00} %/min");
                case "m²/unit": return Invariant($"{sign}{a:0.000}");
                case "": return Invariant($"{sign}{a:0}");
                default: return Invariant($"{sign}{a:0.#} {unit}");
            }
        }

        static string Invariant(FormattableString text) => FormattableString.Invariant(text);
    }

    public static class ReportMetrics
    {
        public static readonly ReportMetric Coverage = new ReportMetric("coverage", "Coverage", "%", true, r => r.coveragePercent);
        public static readonly ReportMetric Rate = new ReportMetric("coverage_rate", "Coverage rate", "%/min", true, r => r.CoveragePerMinute);
        public static readonly ReportMetric TimeTo90 = new ReportMetric("time_to_90", "Time to 90%", "s", false,
            r => r.timeTo90 >= 0f ? r.timeTo90 : double.PositiveInfinity, censored: true);
        public static readonly ReportMetric Efficiency = new ReportMetric("area_per_battery", "Area per battery unit", "m²/unit", true, r => r.CleanedPerBatteryUnit);
        public static readonly ReportMetric BatteryUsed = new ReportMetric("battery_used", "Battery used", "units", false, r => r.batteryUsed);
        public static readonly ReportMetric Revisits = new ReportMetric("revisits", "Revisits", "", false, r => r.revisitCount);

        /// <summary>Every compared metric; the first is the primary one that ranks algorithms.</summary>
        public static readonly ReportMetric[] All = { Coverage, Rate, TimeTo90, Efficiency, BatteryUsed, Revisits };
    }

    /// <summary>
    /// What a condition is: the floor plan, the sensing setup and the vacuum's settings. Runs are only
    /// compared directly with runs in the same condition; different conditions meet in the cross-condition section.
    /// </summary>
    public static class ReportConditions
    {
        public static string KeyOf(RunReport run) =>
            $"{(string.IsNullOrEmpty(run.levelPath) ? run.levelName : run.levelPath)}|{SensingLabel(run)}|{VacuumLabel(run)}|{(ChangedMidRun(run) ? "changed" : "")}";

        public static string LabelOf(RunReport run) =>
            $"{run.levelName} · {SensingLabel(run)} · {VacuumLabel(run)}{(ChangedMidRun(run) ? " · settings changed mid-run" : string.Empty)}";

        public static string SensingLabel(RunReport run)
        {
            if (!run.conditionsRecorded) return "sensing not recorded";
            var text = new StringBuilder(run.slam ? FormattableString.Invariant($"SLAM, noise ×{run.noiseScale:0.##}") : "Normal sensing");
            if (!run.lidarEnabled || run.lidarHalfAngleDegrees <= 0f) text.Append(", whisker only");
            else
            {
                text.Append(FormattableString.Invariant($", lidar {run.lidarRayCount} rays"));
                if (run.lidarRangeMeters > 0f) text.Append(FormattableString.Invariant($" {run.lidarRangeMeters:0.#} m"));
                if (run.lidarHalfAngleDegrees < 180f) text.Append(FormattableString.Invariant($" {run.lidarHalfAngleDegrees * 2f:0}°"));
            }
            if (!run.floorSensorEnabled) text.Append(", no floor sensor");
            return text.ToString();
        }

        public static string VacuumLabel(RunReport run)
        {
            var v = run.vacuum;
            if (v == null) return "vacuum not recorded";
            return FormattableString.Invariant($"{v.driveSpeed:0.00} m/s, {v.turnSpeed:0}°/s, {v.batteryCapacity:0}-unit battery");
        }

        static bool ChangedMidRun(RunReport run) => run.parameterEvents != null && run.parameterEvents.Count > 0;
    }

    /// <summary>Descriptive statistics of one metric for one algorithm in one condition.</summary>
    public sealed class Descriptive
    {
        public int n;

        /// <summary>Runs with no value (never reached the milestone). Mean and CI cover the others; the median counts them as worst.</summary>
        public int censored;

        public double mean = double.NaN, sd = double.NaN, ci95 = double.NaN;
        public double median = double.NaN, q1 = double.NaN, q3 = double.NaN;
        public double medianLow = double.NaN, medianHigh = double.NaN;

        public static Descriptive Of(IReadOnlyList<double> values, int resamples)
        {
            var result = new Descriptive { n = values.Count };
            if (values.Count == 0) return result;

            var finite = new List<double>();
            foreach (double v in values)
                if (double.IsInfinity(v)) result.censored++;
                else finite.Add(v);

            if (finite.Count > 0)
            {
                var summary = Summary.Of(finite);
                result.mean = summary.mean;
                result.sd = summary.StandardDeviation;
                result.ci95 = summary.ci95;
            }

            result.median = Statistics.Median(values);
            result.q1 = Statistics.Quantile(values, 0.25);
            result.q3 = Statistics.Quantile(values, 0.75);
            (result.medianLow, result.medianHigh) = Statistics.BootstrapMedianCi(values, resamples);
            return result;
        }
    }

    /// <summary>One algorithm's runs in one condition.</summary>
    public sealed class AlgorithmGroup
    {
        public string id;
        public string name;
        public List<RunReport> runs = new List<RunReport>();
        public readonly Dictionary<string, Descriptive> stats = new Dictionary<string, Descriptive>();

        public Descriptive this[ReportMetric metric] => stats[metric.key];

        public List<double> Values(ReportMetric metric) => runs.ConvertAll(r => metric.Read(r));

        /// <summary>The metric per seed. A seed run more than once is averaged (identical settings replay identically).</summary>
        public Dictionary<int, double> BySeed(ReportMetric metric)
        {
            var sums = new Dictionary<int, (double sum, int finite, int count)>();
            foreach (var run in runs)
            {
                double v = metric.Read(run);
                sums.TryGetValue(run.seed, out var s);
                sums[run.seed] = double.IsInfinity(v) ? (s.sum, s.finite, s.count + 1) : (s.sum + v, s.finite + 1, s.count + 1);
            }

            var result = new Dictionary<int, double>();
            foreach (var pair in sums)
                result[pair.Key] = pair.Value.finite > 0 ? pair.Value.sum / pair.Value.finite : double.PositiveInfinity;
            return result;
        }
    }

    /// <summary>Two algorithms compared on one metric.</summary>
    public sealed class PairwiseComparison
    {
        public AlgorithmGroup a;
        public AlgorithmGroup b;

        /// <summary>True when compared seed by seed (the runs share seeds); false for independent samples.</summary>
        public bool paired;

        public TestResult test;
        public TestResult parametric;

        /// <summary>Holm-adjusted p-value within this metric's family of comparisons.</summary>
        public double pAdjusted = double.NaN;

        /// <summary>a − b: the median of per-seed differences when paired, the difference of medians otherwise.</summary>
        public double difference = double.NaN;

        /// <summary>The better of the two, when the difference is significant after adjustment; otherwise null.</summary>
        public AlgorithmGroup better;

        public bool Significant => better != null;
    }

    /// <summary>All algorithms in one condition compared on one metric.</summary>
    public sealed class MetricComparison
    {
        public ReportMetric metric;
        public TestResult omnibus;
        public List<PairwiseComparison> pairs = new List<PairwiseComparison>();

        /// <summary>Best first, by median then mean.</summary>
        public List<AlgorithmGroup> ranking = new List<AlgorithmGroup>();
    }

    public sealed class ConditionSection
    {
        public string key;
        public string label;
        public string levelName;
        public List<AlgorithmGroup> algorithms = new List<AlgorithmGroup>();
        public List<MetricComparison> metrics = new List<MetricComparison>();

        public MetricComparison For(ReportMetric metric) => metrics.Find(m => m.metric == metric);
    }

    /// <summary>Algorithms compared over several conditions: each condition is one block of a Friedman test.</summary>
    public sealed class CrossCondition
    {
        public ReportMetric metric;
        public List<string> algorithmIds = new List<string>();
        public List<string> algorithmNames = new List<string>();
        public List<ConditionSection> conditions = new List<ConditionSection>();

        /// <summary>medians[condition][algorithm] for the algorithms and conditions above.</summary>
        public List<double[]> medians = new List<double[]>();

        public double[] meanRanks;
        public int[] wins;
        public TestResult friedman;

        /// <summary>Per algorithm (every condition it ran in): does the condition change its result?</summary>
        public List<(string name, int conditions, TestResult test)> sensitivity = new List<(string, int, TestResult)>();
    }

    public sealed class ReportOptions
    {
        public string title = "Comparative performance report";

        /// <summary>Adds the ML-assisted section: anomaly detection, clustering and their narrative.</summary>
        public bool assisted = true;

        public int bootstrapResamples = 2000;

        /// <summary>Significance level for the Holm-adjusted pairwise comparisons.</summary>
        public double alpha = 0.05;
    }

    /// <summary>
    /// A comparative performance report over a set of runs, generated from scratch each time. Within each
    /// condition every algorithm is compared on every metric: an omnibus test (Friedman on shared seeds,
    /// otherwise Kruskal–Wallis), then pairwise rank tests (Wilcoxon signed-rank on shared seeds, otherwise
    /// Mann–Whitney) with Holm correction and effect sizes, beside medians with bootstrap CIs and means with
    /// t-intervals. Across conditions, algorithms are ranked per condition and compared by a Friedman test.
    /// </summary>
    public sealed class ComparativeReport
    {
        const string AssistedPref = "Analysis.AssistedReport";

        public string title;
        public DateTime generated;
        public ReportOptions options;
        public int runCount;
        public int excludedMixed;
        public List<ConditionSection> conditions = new List<ConditionSection>();
        public CrossCondition cross;
        public AssistedFindings assisted;
        public List<string> summary = new List<string>();

        /// <summary>Whether reports include the ML-assisted section. Remembered between sessions; on by default.</summary>
        public static bool AssistedEnabled
        {
            get { try { return PlayerPrefs.GetInt(AssistedPref, 1) == 1; } catch { return true; } }
            set { try { PlayerPrefs.SetInt(AssistedPref, value ? 1 : 0); PlayerPrefs.Save(); } catch { /* not persisted */ } }
        }

        public static string ReportsFolder => Path.Combine(Application.persistentDataPath, "Reports");

        /// <summary>Where a batch's report is written when the batch finishes (and when it is exported again).</summary>
        public static string BatchFolder(string batchId) => Path.Combine(ReportsFolder, $"batch-{batchId}");

        public static ReportOptions BatchOptions(string levelName) =>
            new ReportOptions { title = $"Batch experiment report · {levelName}", assisted = AssistedEnabled };

        public static ComparativeReport Build(IEnumerable<RunReport> runs, ReportOptions options = null)
        {
            options ??= new ReportOptions();
            var report = new ComparativeReport { title = options.title, generated = DateTime.Now, options = options };

            var byCondition = new Dictionary<string, ConditionSection>();
            foreach (var run in runs)
            {
                if (run == null) continue;
                if (run.mixedPatterns)
                {
                    report.excludedMixed++;
                    continue;
                }

                report.runCount++;
                string key = ReportConditions.KeyOf(run);
                if (!byCondition.TryGetValue(key, out var section))
                {
                    byCondition[key] = section = new ConditionSection { key = key, label = ReportConditions.LabelOf(run), levelName = run.levelName };
                    report.conditions.Add(section);
                }

                string algorithm = run.AlgorithmKey;
                var group = section.algorithms.Find(g => g.id == algorithm);
                if (group == null) section.algorithms.Add(group = new AlgorithmGroup { id = algorithm, name = run.AlgorithmName });
                group.runs.Add(run);
            }

            report.conditions.Sort((x, y) => string.CompareOrdinal(x.label, y.label));
            foreach (var section in report.conditions)
            {
                foreach (var group in section.algorithms)
                {
                    group.runs.Sort((x, y) => x.seed.CompareTo(y.seed));
                    foreach (var metric in ReportMetrics.All)
                        group.stats[metric.key] = Descriptive.Of(group.Values(metric), options.bootstrapResamples);
                }

                foreach (var metric in ReportMetrics.All)
                    section.metrics.Add(Compare(section.algorithms, metric, options.alpha));

                // Order the section's algorithms by the primary metric, so tables read best first.
                var primary = section.metrics[0].ranking;
                section.algorithms.Sort((x, y) => primary.IndexOf(x).CompareTo(primary.IndexOf(y)));
            }

            report.cross = BuildCross(report.conditions, ReportMetrics.Coverage);
            if (options.assisted) report.assisted = AssistedAnalysis.Analyse(report);
            report.summary = ReportNarrative.Summarise(report);
            return report;
        }

        // ---------------------------------------------------------------- within a condition

        static MetricComparison Compare(List<AlgorithmGroup> groups, ReportMetric metric, double alpha)
        {
            var comparison = new MetricComparison { metric = metric, ranking = new List<AlgorithmGroup>(groups) };
            comparison.ranking.Sort((x, y) => CompareBest(x[metric], y[metric], metric));
            if (groups.Count < 2) return comparison;

            comparison.omnibus = Omnibus(groups, metric);

            for (int i = 0; i < groups.Count; i++)
                for (int j = i + 1; j < groups.Count; j++)
                    comparison.pairs.Add(ComparePair(groups[i], groups[j], metric));

            var adjusted = Statistics.HolmAdjust(comparison.pairs.ConvertAll(p => p.test.pValue));
            for (int i = 0; i < comparison.pairs.Count; i++)
            {
                var pair = comparison.pairs[i];
                pair.pAdjusted = adjusted[i];
                if (!(pair.pAdjusted < alpha) || double.IsNaN(pair.test.effect) || pair.test.effect == 0) continue;
                bool aLarger = pair.test.effect > 0;
                pair.better = aLarger == metric.higherIsBetter ? pair.a : pair.b;
            }
            return comparison;
        }

        static int CompareBest(Descriptive x, Descriptive y, ReportMetric metric)
        {
            int sign = metric.higherIsBetter ? -1 : 1;
            int byMedian = sign * NanLast(x.median).CompareTo(NanLast(y.median));
            if (byMedian != 0) return byMedian;
            return sign * NanLast(x.mean).CompareTo(NanLast(y.mean));

            double NanLast(double v) => double.IsNaN(v) ? (metric.higherIsBetter ? double.NegativeInfinity : double.PositiveInfinity) : v;
        }

        static TestResult Omnibus(List<AlgorithmGroup> groups, ReportMetric metric)
        {
            var bySeed = groups.ConvertAll(g => g.BySeed(metric));
            var common = new List<int>(bySeed[0].Keys);
            foreach (var seeds in bySeed) common.RemoveAll(seed => !seeds.ContainsKey(seed));
            common.Sort();

            if (common.Count >= 3)
            {
                var blocks = common.ConvertAll(seed =>
                {
                    var row = new double[groups.Count];
                    for (int j = 0; j < groups.Count; j++) row[j] = bySeed[j][seed];
                    return row;
                });
                var friedman = Statistics.Friedman(blocks);
                friedman.note = Join(friedman.note, $"blocked on {common.Count} shared seeds");
                return friedman;
            }

            var values = groups.ConvertAll(g => (IReadOnlyList<double>)g.Values(metric));
            if (values.Exists(v => v.Count < 2)) return TestResult.NotRun("Kruskal–Wallis", "needs at least 2 runs per algorithm");
            var kw = Statistics.KruskalWallis(values);
            kw.note = Join(kw.note, "runs do not share seeds, so compared as independent samples");
            return kw;
        }

        static PairwiseComparison ComparePair(AlgorithmGroup a, AlgorithmGroup b, ReportMetric metric)
        {
            var pair = new PairwiseComparison { a = a, b = b };
            var seedsA = a.BySeed(metric);
            var seedsB = b.BySeed(metric);
            var common = new List<int>();
            foreach (int seed in seedsA.Keys) if (seedsB.ContainsKey(seed)) common.Add(seed);
            common.Sort();

            // Pair on seeds when most runs have a partner: same seed, same condition, so only the algorithm differs.
            if (common.Count >= 3 && common.Count * 2 >= Math.Min(seedsA.Count, seedsB.Count))
            {
                var x = common.ConvertAll(seed => seedsA[seed]);
                var y = common.ConvertAll(seed => seedsB[seed]);
                pair.paired = true;
                pair.test = Statistics.WilcoxonSignedRank(x, y);
                pair.parametric = Statistics.PairedT(x, y);
                var diffs = new List<double>();
                for (int i = 0; i < x.Count; i++) diffs.Add(Statistics.Difference(x[i], y[i]));
                pair.difference = Statistics.Median(diffs);
                return pair;
            }

            var va = a.Values(metric);
            var vb = b.Values(metric);
            if (va.Count < 2 || vb.Count < 2)
            {
                pair.test = TestResult.NotRun("Mann–Whitney U", "needs at least 2 runs of each algorithm");
                pair.parametric = TestResult.NotRun("Welch t", "needs at least 2 runs of each algorithm");
            }
            else
            {
                pair.test = Statistics.MannWhitney(va, vb);
                pair.parametric = Statistics.WelchT(va, vb);
            }
            pair.difference = Statistics.Difference(a[metric].median, b[metric].median);
            return pair;
        }

        // ---------------------------------------------------------------- across conditions

        static CrossCondition BuildCross(List<ConditionSection> sections, ReportMetric metric)
        {
            if (sections.Count < 2) return null;
            var cross = new CrossCondition { metric = metric };

            // How each algorithm fares as the condition changes, over every condition it ran in.
            var everywhere = new Dictionary<string, (string name, List<IReadOnlyList<double>> groups)>();
            foreach (var section in sections)
                foreach (var group in section.algorithms)
                {
                    if (!everywhere.TryGetValue(group.id, out var entry)) everywhere[group.id] = entry = (group.name, new List<IReadOnlyList<double>>());
                    entry.groups.Add(group.Values(metric));
                }

            foreach (var entry in everywhere.Values)
            {
                if (entry.groups.Count < 2) continue;
                var usable = entry.groups.FindAll(g => g.Count >= 2);
                var test = usable.Count >= 2 ? Statistics.KruskalWallis(usable) : TestResult.NotRun("Kruskal–Wallis", "needs 2+ runs in at least 2 conditions");
                cross.sensitivity.Add((entry.name, entry.groups.Count, test));
            }

            // Algorithms that met in at least two conditions; drop the most sparsely tested until the rest share 2+.
            var candidates = new List<string>();
            foreach (var pair in everywhere) if (pair.Value.groups.Count >= 2) candidates.Add(pair.Key);
            List<ConditionSection> shared;
            while (true)
            {
                shared = sections.FindAll(s => candidates.TrueForAll(id => s.algorithms.Exists(g => g.id == id)));
                if (shared.Count >= 2 || candidates.Count <= 2) break;
                candidates.Sort((x, y) => everywhere[y].groups.Count.CompareTo(everywhere[x].groups.Count));
                candidates.RemoveAt(candidates.Count - 1);
            }

            if (candidates.Count < 2 || shared.Count < 2) return cross;

            cross.algorithmIds = candidates;
            cross.algorithmNames = candidates.ConvertAll(id => everywhere[id].name);
            cross.conditions = shared;
            int k = candidates.Count;
            cross.meanRanks = new double[k];
            cross.wins = new int[k];

            var blocks = new List<double[]>();
            foreach (var section in shared)
            {
                var row = new double[k];
                for (int j = 0; j < k; j++) row[j] = section.algorithms.Find(g => g.id == candidates[j])[metric].median;
                cross.medians.Add(row);

                // Rank 1 = best; for a higher-is-better metric rank the negated values.
                var oriented = new double[k];
                for (int j = 0; j < k; j++) oriented[j] = metric.higherIsBetter ? -row[j] : row[j];
                blocks.Add(oriented);
                var ranks = Statistics.Ranks(oriented, out _);
                double best = double.MaxValue;
                foreach (double r in ranks) best = Math.Min(best, r);
                for (int j = 0; j < k; j++)
                {
                    cross.meanRanks[j] += ranks[j] / shared.Count;
                    if (ranks[j] == best) cross.wins[j]++;
                }
            }

            cross.friedman = Statistics.Friedman(blocks);
            cross.friedman.note = Join(cross.friedman.note, $"blocked on {shared.Count} conditions, one median per algorithm each");
            return cross;
        }

        static string Join(string a, string b) => string.IsNullOrEmpty(a) ? b : string.IsNullOrEmpty(b) ? a : $"{a}; {b}";

        // ---------------------------------------------------------------- output

        /// <summary>Writes report.md and the CSV tables to <paramref name="folder"/>; returns the report.md path.</summary>
        public string WriteTo(string folder)
        {
            Directory.CreateDirectory(folder);
            string markdown = Path.Combine(folder, "report.md");
            File.WriteAllText(markdown, ToMarkdown(), Encoding.UTF8);
            File.WriteAllText(Path.Combine(folder, "groups.csv"), GroupsCsv(), Encoding.UTF8);
            File.WriteAllText(Path.Combine(folder, "pairwise.csv"), PairwiseCsv(), Encoding.UTF8);
            if (assisted != null)
            {
                File.WriteAllText(Path.Combine(folder, "anomalies.csv"), assisted.AnomaliesCsv(), Encoding.UTF8);
                File.WriteAllText(Path.Combine(folder, "clusters.csv"), assisted.ClustersCsv(), Encoding.UTF8);
            }
            return markdown;
        }

        public string ToMarkdown()
        {
            var md = new StringBuilder();
            md.Append("# ").AppendLine(title);
            md.AppendLine();
            md.AppendLine($"Generated {generated:yyyy-MM-dd HH:mm} · {runCount} runs · {conditions.Count} condition(s)" +
                          (excludedMixed > 0 ? $" · {excludedMixed} run(s) that switched algorithm mid-run left out" : string.Empty));
            md.AppendLine();

            md.AppendLine("## Summary");
            md.AppendLine();
            foreach (string line in summary) md.Append("- ").AppendLine(line);
            md.AppendLine();

            if (cross != null) WriteCross(md);

            foreach (var section in conditions)
            {
                md.Append("## ").AppendLine(section.label);
                md.AppendLine();
                foreach (var comparison in section.metrics) WriteMetric(md, section, comparison);
            }

            if (assisted != null) assisted.WriteMarkdown(md);
            WriteMethods(md);
            return md.ToString();
        }

        void WriteCross(StringBuilder md)
        {
            md.AppendLine($"## Across conditions ({cross.metric.label.ToLowerInvariant()})");
            md.AppendLine();
            if (cross.conditions.Count >= 2)
            {
                md.Append("| Condition |");
                foreach (string name in cross.algorithmNames) md.Append(' ').Append(Cell(name)).Append(" |");
                md.AppendLine();
                md.Append("|---|");
                foreach (var _ in cross.algorithmNames) md.Append("---:|");
                md.AppendLine();
                for (int i = 0; i < cross.conditions.Count; i++)
                {
                    md.Append("| ").Append(Cell(cross.conditions[i].label)).Append(" |");
                    foreach (double v in cross.medians[i]) md.Append(' ').Append(cross.metric.Format(v)).Append(" |");
                    md.AppendLine();
                }
                md.Append("| **Mean rank** (1 = best) |");
                foreach (double r in cross.meanRanks) md.Append(FormattableString.Invariant($" {r:0.00} |"));
                md.AppendLine();
                md.Append("| **Conditions won** |");
                foreach (int w in cross.wins) md.Append($" {w} |");
                md.AppendLine();
                md.AppendLine();
                md.AppendLine($"Cells are median {cross.metric.label.ToLowerInvariant()} per condition. {DescribeTest(cross.friedman)}");
                md.AppendLine();
            }
            else md.AppendLine("No two algorithms were run in the same two or more conditions, so no ranking across conditions is possible.\n");

            if (cross.sensitivity.Count > 0)
            {
                md.AppendLine("Does the condition change each algorithm's result?");
                md.AppendLine();
                md.AppendLine("| Algorithm | Conditions | Test | p | Effect |");
                md.AppendLine("|---|---:|---|---:|---|");
                foreach (var (name, count, test) in cross.sensitivity)
                    md.AppendLine($"| {Cell(name)} | {count} | {test.test} | {FormatP(test.pValue)} | {FormatEffect(test)} |");
                md.AppendLine();
            }
        }

        void WriteMetric(StringBuilder md, ConditionSection section, MetricComparison comparison)
        {
            var metric = comparison.metric;
            md.Append("### ").Append(metric.label).AppendLine(metric.higherIsBetter ? " (higher is better)" : " (lower is better)");
            md.AppendLine();
            md.AppendLine("| Algorithm | n | Median [95% CI] | IQR | Mean ± 95% CI | SD |");
            md.AppendLine("|---|---:|---|---|---|---:|");
            foreach (var group in comparison.ranking)
            {
                var d = group[metric];
                string censored = d.censored > 0 ? $" ({d.censored} not reached)" : string.Empty;
                md.AppendLine($"| {Cell(group.name)} | {d.n} | {metric.Format(d.median)} [{metric.Format(d.medianLow)} – {metric.Format(d.medianHigh)}] | " +
                              $"{metric.Format(d.q1)} – {metric.Format(d.q3)} | {metric.Format(d.mean)} ± {FormatPlain(d.ci95)}{censored} | {FormatPlain(d.sd)} |");
            }
            md.AppendLine();

            if (comparison.omnibus != null)
            {
                md.AppendLine($"Omnibus: {DescribeTest(comparison.omnibus)}");
                md.AppendLine();
            }

            if (comparison.pairs.Count > 0)
            {
                md.AppendLine("| A vs B | Design | Δ (A − B) | Test | p | p (Holm) | Effect | Parametric check | Verdict |");
                md.AppendLine("|---|---|---:|---|---:|---:|---|---|---|");
                foreach (var pair in comparison.pairs)
                {
                    string verdict = pair.Significant ? $"**{Cell(pair.better.name)} better**" : pair.test.Valid ? "no significant difference" : pair.test.note;
                    string parametric = pair.parametric != null && pair.parametric.Valid
                        ? $"{pair.parametric.test} {PText(pair.parametric.pValue)}"
                        : pair.parametric?.note ?? "–";
                    md.AppendLine($"| {Cell(pair.a.name)} vs {Cell(pair.b.name)} | {(pair.paired ? $"paired, {pair.test.n} seeds" : "independent")} | " +
                                  $"{metric.FormatDifference(pair.difference)} | {pair.test.test}{(pair.test.exact ? " (exact)" : string.Empty)} | " +
                                  $"{FormatP(pair.test.pValue)} | {FormatP(pair.pAdjusted)} | {FormatEffect(pair.test)} | {parametric} | {verdict} |");
                }
                md.AppendLine();
            }
        }

        void WriteMethods(StringBuilder md)
        {
            md.AppendLine("## Methods");
            md.AppendLine();
            md.AppendLine("- **Conditions.** Runs are grouped by floor plan, sensing setup (SLAM/noise, lidar, floor sensor) and vacuum settings. Algorithms are compared directly only within a condition.");
            md.AppendLine("- **Pairing.** Batch trials give every algorithm the same seeds, so two algorithms are compared seed by seed (paired) when most of their runs share a seed; otherwise as independent samples.");
            md.AppendLine("- **Tests.** Rank-based tests are primary because coverage is bounded at 100% and skewed: Wilcoxon signed-rank (paired) or Mann–Whitney U (independent), with exact permutation p-values for small samples. With 3+ algorithms an omnibus Friedman (shared seeds) or Kruskal–Wallis test comes first. Paired/Welch t-tests are shown only as a parametric cross-check.");
            md.AppendLine($"- **Multiple comparisons.** Pairwise p-values are Holm–Bonferroni adjusted within each metric and condition; a difference is called significant at adjusted p < {options.alpha:0.##}.");
            md.AppendLine("- **Effect sizes.** Rank-biserial r (paired) and Cliff's δ (independent), with |0.147| / |0.33| / |0.474| as small / medium / large; Kendall's W for Friedman; ε² for Kruskal–Wallis. A significant result with a negligible effect is unlikely to matter in practice.");
            md.AppendLine($"- **Intervals.** Medians carry a percentile bootstrap 95% CI ({options.bootstrapResamples} resamples, fixed seed so reports are reproducible); means carry a Student-t 95% CI.");
            md.AppendLine("- **Censoring.** A run that never reached 90% coverage counts as slower than any run that did (ranked last) instead of being dropped; it is left out of the mean.");
            md.AppendLine("- **Across conditions.** Each shared condition contributes one median per algorithm; algorithms are ranked within each condition and compared with a Friedman test over conditions (Demšar, 2006).");
            md.AppendLine("- **Caveat.** \"No significant difference\" with only a few runs per group is inconclusive, not evidence that algorithms are equal. Run more seeds to narrow the intervals.");
            md.AppendLine();
        }

        string GroupsCsv()
        {
            var csv = new StringBuilder("condition,algorithm_id,algorithm,metric,n,not_reached,mean,sd,ci95,median,median_ci_low,median_ci_high,q1,q3\n");
            foreach (var section in conditions)
                foreach (var group in section.algorithms)
                    foreach (var metric in ReportMetrics.All)
                    {
                        var d = group[metric];
                        csv.Append(Csv(section.label)).Append(',').Append(Csv(group.id)).Append(',').Append(Csv(group.name)).Append(',')
                            .Append(metric.key).Append(',').Append(d.n).Append(',').Append(d.censored).Append(',')
                            .Append(Num(d.mean)).Append(',').Append(Num(d.sd)).Append(',').Append(Num(d.ci95)).Append(',')
                            .Append(Num(d.median)).Append(',').Append(Num(d.medianLow)).Append(',').Append(Num(d.medianHigh)).Append(',')
                            .Append(Num(d.q1)).Append(',').Append(Num(d.q3)).Append('\n');
                    }
            return csv.ToString();
        }

        string PairwiseCsv()
        {
            var csv = new StringBuilder("condition,metric,algorithm_a,algorithm_b,paired,n,test,exact,statistic,p,p_holm,effect_name,effect,magnitude,difference_a_minus_b,parametric_test,parametric_p,better\n");
            foreach (var section in conditions)
                foreach (var comparison in section.metrics)
                    foreach (var pair in comparison.pairs)
                    {
                        csv.Append(Csv(section.label)).Append(',').Append(comparison.metric.key).Append(',')
                            .Append(Csv(pair.a.name)).Append(',').Append(Csv(pair.b.name)).Append(',')
                            .Append(pair.paired ? "1" : "0").Append(',').Append(pair.test.n).Append(',')
                            .Append(Csv(pair.test.test)).Append(',').Append(pair.test.exact ? "1" : "0").Append(',')
                            .Append(Num(pair.test.statistic)).Append(',').Append(Num(pair.test.pValue)).Append(',').Append(Num(pair.pAdjusted)).Append(',')
                            .Append(Csv(pair.test.effectName)).Append(',').Append(Num(pair.test.effect)).Append(',').Append(pair.test.Magnitude).Append(',')
                            .Append(Num(pair.difference)).Append(',')
                            .Append(Csv(pair.parametric?.test)).Append(',').Append(Num(pair.parametric?.pValue ?? double.NaN)).Append(',')
                            .Append(Csv(pair.better?.name)).Append('\n');
                    }
            return csv.ToString();
        }

        // ---------------------------------------------------------------- formatting

        public static string FormatP(double p)
        {
            if (double.IsNaN(p)) return "–";
            if (p < 0.001) return "< 0.001";
            return p.ToString(p < 0.01 ? "0.000" : "0.00", CultureInfo.InvariantCulture);
        }

        /// <summary>"p = 0.03" or "p < 0.001", for running text.</summary>
        public static string PText(double p) => double.IsNaN(p) ? "p n/a" : p < 0.001 ? "p < 0.001" : $"p = {FormatP(p)}";

        public static string FormatEffect(TestResult test)
        {
            if (test == null || double.IsNaN(test.effect)) return "–";
            return FormattableString.Invariant($"{test.effectName} = {test.effect:0.00} ({test.Magnitude})");
        }

        public static string DescribeTest(TestResult test)
        {
            if (test == null) return "not run.";
            if (!test.Valid) return $"{test.test} not run: {test.note}.";
            string statistic = test.test switch
            {
                "Friedman" => "χ²",
                "Kruskal–Wallis" => "H",
                _ => "statistic",
            };
            return FormattableString.Invariant($"{test.test} {statistic} = {test.statistic:0.00}, {PText(test.pValue)}, {test.effectName} = {test.effect:0.00} ({test.Magnitude})") +
                   (string.IsNullOrEmpty(test.note) ? "." : $" — {test.note}.");
        }

        static string FormatPlain(double v) => double.IsNaN(v) ? "–" : v.ToString(Math.Abs(v) >= 100 ? "0" : "0.##", CultureInfo.InvariantCulture);

        static string Cell(string text) => (text ?? string.Empty).Replace("|", "\\|");

        internal static string Num(double v) =>
            double.IsNaN(v) ? string.Empty : double.IsPositiveInfinity(v) ? "inf" : double.IsNegativeInfinity(v) ? "-inf" : v.ToString("0.######", CultureInfo.InvariantCulture);

        internal static string Csv(string value)
        {
            value ??= string.Empty;
            return value.IndexOfAny(new[] { ',', '"', '\n' }) < 0 ? value : "\"" + value.Replace("\"", "\"\"") + "\"";
        }
    }
}
