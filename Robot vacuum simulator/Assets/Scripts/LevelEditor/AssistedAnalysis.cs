using System;
using System.Collections.Generic;
using System.Text;

namespace RobotVacuum.LevelEditor
{
    /// <summary>A run that stands out from the other runs of the same algorithm in the same condition.</summary>
    public sealed class Anomaly
    {
        public RunReport run;
        public string condition;
        public string algorithm;
        public string metric;
        public double value = double.NaN;
        public double groupMedian = double.NaN;

        /// <summary>Modified z-score (Iglewicz–Hoaglin); NaN for rule-based flags.</summary>
        public double score = double.NaN;

        public string reason;
    }

    /// <summary>A group of runs with similar outcomes, found by k-means on standardised metrics.</summary>
    public sealed class RunCluster
    {
        public int index;
        public string description;
        public List<RunReport> runs = new List<RunReport>();

        /// <summary>Mean of each feature over the cluster's runs, in the feature's own units.</summary>
        public double[] centroid;

        /// <summary>The same means as z-scores against all clustered runs.</summary>
        public double[] centroidZ;

        public readonly Dictionary<string, int> algorithms = new Dictionary<string, int>();
        public readonly Dictionary<string, int> conditions = new Dictionary<string, int>();
    }

    public sealed class AssistedFindings
    {
        public List<Anomaly> anomalies = new List<Anomaly>();
        public List<RunCluster> clusters = new List<RunCluster>();
        public readonly Dictionary<RunReport, int> clusterOf = new Dictionary<RunReport, int>();
        public double silhouette = double.NaN;

        /// <summary>Why no clusters are reported, when none are.</summary>
        public string clusteringNote;

        public List<string> narrative = new List<string>();

        public void WriteMarkdown(StringBuilder md)
        {
            md.AppendLine("## ML-assisted analysis");
            md.AppendLine();
            md.AppendLine("_Exploratory: these findings point at runs and patterns worth a closer look (replay them from the Analysis page). They are not hypothesis tests._");
            md.AppendLine();
            foreach (string line in narrative) md.Append("- ").AppendLine(line);
            md.AppendLine();

            md.AppendLine("### Anomalous runs");
            md.AppendLine();
            if (anomalies.Count == 0) md.AppendLine("None: every run is within the usual spread of its algorithm and condition.");
            else
            {
                md.AppendLine("| Condition | Algorithm | Seed | Metric | Value | Group median | Robust z | Why |");
                md.AppendLine("|---|---|---:|---|---:|---:|---:|---|");
                foreach (var a in anomalies)
                    md.AppendLine($"| {a.condition} | {a.algorithm} | {a.run.seed} | {a.metric} | {ComparativeReport.Num(a.value)} | {ComparativeReport.Num(a.groupMedian)} | " +
                                  $"{(double.IsNaN(a.score) ? "–" : a.score.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture))} | {a.reason} |");
            }
            md.AppendLine();

            md.AppendLine("### Outcome clusters");
            md.AppendLine();
            if (clusters.Count == 0) md.AppendLine(clusteringNote ?? "No clusters.");
            else
            {
                md.AppendLine(FormattableString.Invariant($"k-means (k-means++ seeding, k chosen by silhouette = {silhouette:0.00}) on standardised coverage, coverage rate, battery used, path length and revisits per metre."));
                md.AppendLine();
                md.Append("| Cluster | Runs | Profile |");
                foreach (string f in AssistedAnalysis.FeatureNames) md.Append(' ').Append(f).Append(" |");
                md.AppendLine(" Algorithms |");
                md.Append("|---|---:|---|");
                foreach (var _ in AssistedAnalysis.FeatureNames) md.Append("---:|");
                md.AppendLine("---|");
                foreach (var c in clusters)
                {
                    md.Append($"| {c.index + 1} | {c.runs.Count} | {c.description} |");
                    foreach (double v in c.centroid) md.Append(FormattableString.Invariant($" {v:0.##} |"));
                    md.Append(' ').Append(AssistedAnalysis.Composition(c.algorithms, c.runs.Count)).AppendLine(" |");
                }
            }
            md.AppendLine();
        }

        public string AnomaliesCsv()
        {
            var csv = new StringBuilder("run_id,condition,algorithm,seed,metric,value,group_median,robust_z,reason,data_folder\n");
            foreach (var a in anomalies)
                csv.Append(ComparativeReport.Csv(a.run.id)).Append(',').Append(ComparativeReport.Csv(a.condition)).Append(',')
                    .Append(ComparativeReport.Csv(a.algorithm)).Append(',').Append(a.run.seed).Append(',')
                    .Append(ComparativeReport.Csv(a.metric)).Append(',').Append(ComparativeReport.Num(a.value)).Append(',')
                    .Append(ComparativeReport.Num(a.groupMedian)).Append(',').Append(ComparativeReport.Num(a.score)).Append(',')
                    .Append(ComparativeReport.Csv(a.reason)).Append(',').Append(ComparativeReport.Csv(a.run.dataFolder)).Append('\n');
            return csv.ToString();
        }

        public string ClustersCsv()
        {
            var csv = new StringBuilder("run_id,algorithm,seed,condition,cluster,cluster_profile\n");
            foreach (var c in clusters)
                foreach (var run in c.runs)
                    csv.Append(ComparativeReport.Csv(run.id)).Append(',').Append(ComparativeReport.Csv(run.AlgorithmName)).Append(',')
                        .Append(run.seed).Append(',').Append(ComparativeReport.Csv(ReportConditions.LabelOf(run))).Append(',')
                        .Append(c.index + 1).Append(',').Append(ComparativeReport.Csv(c.description)).Append('\n');
            return csv.ToString();
        }
    }

    /// <summary>
    /// The optional ML-assisted layer of a report: unsupervised anomaly detection (robust z-scores within
    /// each algorithm and condition, plus failure rules) and k-means clustering of run outcomes, with a
    /// plain-language reading of both. Everything runs locally and deterministically.
    /// </summary>
    public static class AssistedAnalysis
    {
        /// <summary>|modified z| above this flags a run (Iglewicz and Hoaglin's recommended cut-off).</summary>
        public const double AnomalyThreshold = 3.5;

        /// <summary>Groups smaller than this are not screened: a median and MAD of a handful of runs say little.</summary>
        public const int MinGroupForAnomalies = 5;

        public const int MinRunsForClustering = 8;

        /// <summary>Below this best silhouette the runs are reported as not forming distinct groups.</summary>
        public const double MinSilhouette = 0.35;

        public static readonly string[] FeatureNames = { "Coverage %", "Coverage %/min", "Battery used %", "Path m", "Revisits/m" };

        static readonly (string high, string low)[] FeatureWords =
        {
            ("high coverage", "low coverage"),
            ("fast cleaning", "slow cleaning"),
            ("heavy battery use", "light battery use"),
            ("long paths", "short paths"),
            ("many revisits", "few revisits"),
        };

        static readonly ReportMetric[] Screened = { ReportMetrics.Coverage, ReportMetrics.Rate, ReportMetrics.BatteryUsed, ReportMetrics.Revisits };

        public static AssistedFindings Analyse(ComparativeReport report)
        {
            var findings = new AssistedFindings();
            DetectAnomalies(report, findings);
            Cluster(report, findings);
            findings.narrative = Narrate(report, findings);
            return findings;
        }

        // ---------------------------------------------------------------- anomaly detection

        static void DetectAnomalies(ComparativeReport report, AssistedFindings findings)
        {
            foreach (var section in report.conditions)
                foreach (var group in section.algorithms)
                {
                    foreach (var run in group.runs)
                    {
                        if (!string.IsNullOrEmpty(run.failure))
                            findings.anomalies.Add(new Anomaly { run = run, condition = section.label, algorithm = group.name, metric = "—", reason = $"algorithm failed{(string.IsNullOrEmpty(run.failureKind) ? "" : $" ({run.failureKind})")}: {run.failure}" });
                        else if (!string.IsNullOrEmpty(run.endReason) && run.endReason.IndexOf("Escaped", StringComparison.OrdinalIgnoreCase) >= 0)
                            findings.anomalies.Add(new Anomaly { run = run, condition = section.label, algorithm = group.name, metric = "—", reason = run.endReason });
                    }

                    if (group.runs.Count < MinGroupForAnomalies) continue;
                    foreach (var metric in Screened)
                    {
                        var values = group.Values(metric);
                        double median = Statistics.Median(values);
                        double scale = RobustScale(values, median);
                        if (!(scale > 1e-9)) continue;

                        for (int i = 0; i < values.Count; i++)
                        {
                            double z = (values[i] - median) / scale;
                            if (Math.Abs(z) <= AnomalyThreshold) continue;
                            bool worse = (z > 0) != metric.higherIsBetter;
                            findings.anomalies.Add(new Anomaly
                            {
                                run = group.runs[i],
                                condition = section.label,
                                algorithm = group.name,
                                metric = metric.label,
                                value = values[i],
                                groupMedian = median,
                                score = z,
                                reason = $"{(worse ? "far worse" : "far better")} than this algorithm's other runs",
                            });
                        }
                    }
                }

            findings.anomalies.Sort((a, b) => Severity(b).CompareTo(Severity(a)));
            static double Severity(Anomaly a) => double.IsNaN(a.score) ? double.MaxValue : Math.Abs(a.score);
        }

        // MAD scaled to be consistent with the SD (modified z = 0.6745 (x − median) / MAD); mean absolute deviation if MAD is 0.
        static double RobustScale(List<double> values, double median)
        {
            var deviations = values.ConvertAll(v => Math.Abs(v - median));
            double mad = Statistics.Median(deviations);
            if (mad > 1e-9) return mad / 0.6745;
            double sum = 0;
            foreach (double d in deviations) sum += d;
            return sum / deviations.Count * 1.253314;
        }

        // ---------------------------------------------------------------- clustering

        public static double[] Features(RunReport run) => new double[]
        {
            run.coveragePercent,
            run.CoveragePerMinute,
            run.BatteryUsedPercent,
            run.distanceMeters,
            run.distanceMeters > 0.01f ? run.revisitCount / run.distanceMeters : 0,
        };

        static void Cluster(ComparativeReport report, AssistedFindings findings)
        {
            var runs = new List<RunReport>();
            foreach (var section in report.conditions)
                foreach (var group in section.algorithms)
                    runs.AddRange(group.runs);

            if (runs.Count < MinRunsForClustering)
            {
                findings.clusteringNote = $"Not enough runs to look for clusters (needs {MinRunsForClustering}, have {runs.Count}).";
                return;
            }

            int dims = FeatureNames.Length;
            var raw = runs.ConvertAll(Features);
            var mean = new double[dims];
            var sd = new double[dims];
            for (int f = 0; f < dims; f++)
            {
                var column = raw.ConvertAll(x => x[f]);
                var s = Summary.Of(column);
                mean[f] = s.mean;
                sd[f] = s.StandardDeviation;
            }
            var points = raw.ConvertAll(x =>
            {
                var z = new double[dims];
                for (int f = 0; f < dims; f++) z[f] = sd[f] > 1e-9 ? (x[f] - mean[f]) / sd[f] : 0;
                return z;
            });

            int maxK = Math.Min(5, runs.Count / 3);
            int[] best = null;
            double bestScore = double.NegativeInfinity;
            for (int k = 2; k <= maxK; k++)
            {
                var labels = KMeans(points, k, restarts: 8, seed: 1729 + k);
                double score = Silhouette(points, labels, k);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = labels;
                }
            }

            findings.silhouette = bestScore;
            if (best == null || bestScore < MinSilhouette)
            {
                findings.clusteringNote = best == null
                    ? "Not enough runs to look for clusters."
                    : FormattableString.Invariant($"The runs do not form distinct groups (best silhouette {bestScore:0.00}, below {MinSilhouette:0.00}): outcomes vary continuously rather than falling into separate regimes.");
                return;
            }

            int clusters = 0;
            foreach (int label in best) clusters = Math.Max(clusters, label + 1);
            for (int c = 0; c < clusters; c++) findings.clusters.Add(new RunCluster { index = c, centroid = new double[dims], centroidZ = new double[dims] });
            for (int i = 0; i < runs.Count; i++)
            {
                var cluster = findings.clusters[best[i]];
                cluster.runs.Add(runs[i]);
                findings.clusterOf[runs[i]] = best[i];
                for (int f = 0; f < dims; f++)
                {
                    cluster.centroid[f] += raw[i][f];
                    cluster.centroidZ[f] += points[i][f];
                }
                Increment(cluster.algorithms, runs[i].AlgorithmName);
                Increment(cluster.conditions, ReportConditions.LabelOf(runs[i]));
            }

            findings.clusters.RemoveAll(c => c.runs.Count == 0);
            // Best coverage first; renumber to match.
            findings.clusters.Sort((a, b) => b.centroid[0].CompareTo(a.centroid[0]));
            for (int c = 0; c < findings.clusters.Count; c++)
            {
                var cluster = findings.clusters[c];
                for (int f = 0; f < dims; f++)
                {
                    cluster.centroid[f] /= cluster.runs.Count;
                    cluster.centroidZ[f] /= cluster.runs.Count;
                }
                foreach (var run in cluster.runs) findings.clusterOf[run] = c;
                cluster.index = c;
                cluster.description = Describe(cluster.centroidZ);
            }
        }

        static void Increment(Dictionary<string, int> counts, string key)
        {
            counts.TryGetValue(key, out int n);
            counts[key] = n + 1;
        }

        static string Describe(double[] z)
        {
            var order = new List<int>();
            for (int f = 0; f < z.Length; f++) if (Math.Abs(z[f]) >= 0.5) order.Add(f);
            order.Sort((a, b) => Math.Abs(z[b]).CompareTo(Math.Abs(z[a])));
            if (order.Count == 0) return "typical runs";
            var words = new List<string>();
            for (int i = 0; i < Math.Min(2, order.Count); i++) words.Add(z[order[i]] > 0 ? FeatureWords[order[i]].high : FeatureWords[order[i]].low);
            return string.Join(", ", words);
        }

        /// <summary>Lloyd's k-means with k-means++ seeding; the lowest-inertia labelling of several restarts.</summary>
        public static int[] KMeans(IReadOnlyList<double[]> points, int k, int restarts, int seed)
        {
            int n = points.Count;
            var random = new Random(seed);
            int[] bestLabels = null;
            double bestInertia = double.PositiveInfinity;

            for (int attempt = 0; attempt < restarts; attempt++)
            {
                var centres = SeedCentres(points, k, random);
                var labels = new int[n];
                for (int iteration = 0; iteration < 100; iteration++)
                {
                    bool changed = false;
                    for (int i = 0; i < n; i++)
                    {
                        int nearest = Nearest(points[i], centres, out _);
                        if (nearest == labels[i]) continue;
                        labels[i] = nearest;
                        changed = true;
                    }

                    var sums = new double[k][];
                    var counts = new int[k];
                    for (int c = 0; c < k; c++) sums[c] = new double[points[0].Length];
                    for (int i = 0; i < n; i++)
                    {
                        counts[labels[i]]++;
                        for (int f = 0; f < points[i].Length; f++) sums[labels[i]][f] += points[i][f];
                    }
                    for (int c = 0; c < k; c++)
                    {
                        if (counts[c] == 0) { centres[c] = (double[])points[random.Next(n)].Clone(); changed = true; continue; }
                        for (int f = 0; f < sums[c].Length; f++) sums[c][f] /= counts[c];
                        centres[c] = sums[c];
                    }
                    if (!changed && iteration > 0) break;
                }

                double inertia = 0;
                for (int i = 0; i < n; i++) inertia += Distance2(points[i], centres[labels[i]]);
                if (inertia < bestInertia)
                {
                    bestInertia = inertia;
                    bestLabels = labels;
                }
            }
            return bestLabels;
        }

        static double[][] SeedCentres(IReadOnlyList<double[]> points, int k, Random random)
        {
            var centres = new double[k][];
            centres[0] = (double[])points[random.Next(points.Count)].Clone();
            var distances = new double[points.Count];
            for (int c = 1; c < k; c++)
            {
                double total = 0;
                for (int i = 0; i < points.Count; i++)
                {
                    double nearest = double.PositiveInfinity;
                    for (int j = 0; j < c; j++) nearest = Math.Min(nearest, Distance2(points[i], centres[j]));
                    total += distances[i] = nearest;
                }

                int chosen = random.Next(points.Count);
                if (total > 0)
                {
                    double target = random.NextDouble() * total;
                    for (int i = 0; i < points.Count; i++)
                    {
                        target -= distances[i];
                        if (target <= 0) { chosen = i; break; }
                    }
                }
                centres[c] = (double[])points[chosen].Clone();
            }
            return centres;
        }

        /// <summary>Mean silhouette width (−1 to 1); deterministic subsample of 600 points for large sets.</summary>
        public static double Silhouette(IReadOnlyList<double[]> points, int[] labels, int k)
        {
            int n = points.Count;
            int step = Math.Max(1, n / 600);
            double total = 0;
            int counted = 0;
            for (int i = 0; i < n; i += step)
            {
                var sums = new double[k];
                var counts = new int[k];
                for (int j = 0; j < n; j++)
                {
                    if (j == i) continue;
                    sums[labels[j]] += Math.Sqrt(Distance2(points[i], points[j]));
                    counts[labels[j]]++;
                }

                int own = labels[i];
                if (counts[own] == 0) { counted++; continue; } // a singleton scores 0
                double a = sums[own] / counts[own];
                double b = double.PositiveInfinity;
                for (int c = 0; c < k; c++)
                    if (c != own && counts[c] > 0) b = Math.Min(b, sums[c] / counts[c]);
                if (double.IsPositiveInfinity(b)) return double.NaN; // only one cluster is populated
                double max = Math.Max(a, b);
                total += max > 0 ? (b - a) / max : 0;
                counted++;
            }
            return counted > 0 ? total / counted : double.NaN;
        }

        static int Nearest(double[] point, double[][] centres, out double distance)
        {
            int best = 0;
            distance = double.PositiveInfinity;
            for (int c = 0; c < centres.Length; c++)
            {
                double d = Distance2(point, centres[c]);
                if (d < distance) { distance = d; best = c; }
            }
            return best;
        }

        static double Distance2(double[] a, double[] b)
        {
            double sum = 0;
            for (int i = 0; i < a.Length; i++) sum += (a[i] - b[i]) * (a[i] - b[i]);
            return sum;
        }

        public static string Composition(Dictionary<string, int> counts, int total)
        {
            var entries = new List<KeyValuePair<string, int>>(counts);
            entries.Sort((a, b) => b.Value.CompareTo(a.Value));
            var parts = entries.ConvertAll(e => $"{e.Key} {e.Value}/{total}");
            return string.Join(", ", parts);
        }

        // ---------------------------------------------------------------- narrative

        static List<string> Narrate(ComparativeReport report, AssistedFindings findings)
        {
            var lines = new List<string>();

            var flaggedRuns = new HashSet<RunReport>();
            var failedRuns = new HashSet<RunReport>();
            foreach (var a in findings.anomalies)
            {
                flaggedRuns.Add(a.run);
                if (double.IsNaN(a.score)) failedRuns.Add(a.run);
            }
            if (findings.anomalies.Count == 0) lines.Add("No anomalous runs: every run is within the usual spread of its algorithm and condition.");
            else
            {
                var top = findings.anomalies[0];
                string example = double.IsNaN(top.score)
                    ? $"{top.algorithm} seed {top.run.seed} ({top.reason})"
                    : FormattableString.Invariant($"{top.algorithm} seed {top.run.seed}: {top.metric.ToLowerInvariant()} {top.value:0.##} against a median of {top.groupMedian:0.##} (robust z {top.score:+0.0;−0.0})");
                lines.Add($"{flaggedRuns.Count} run(s) flagged as anomalous ({failedRuns.Count} failed outright); the most extreme is {example}. " +
                          "Replay these runs before trusting the averages, since one bad seed can hide a bug or an unlucky layout.");
            }

            if (findings.clusters.Count == 0)
            {
                if (findings.clusteringNote != null) lines.Add(findings.clusteringNote);
                return lines;
            }

            var profiles = findings.clusters.ConvertAll(c => $"{c.index + 1}: {c.description} ({(c.runs.Count == 1 ? "1 run" : $"{c.runs.Count} runs")})");
            lines.Add(FormattableString.Invariant($"Runs fall into {findings.clusters.Count} outcome groups (silhouette {findings.silhouette:0.00}): ") + string.Join("; ", profiles) + ".");

            double algorithmPurity = Purity(findings.clusters, c => c.algorithms);
            double conditionPurity = Purity(findings.clusters, c => c.conditions);
            if (report.conditions.Count > 1 && conditionPurity >= 0.8 && conditionPurity > algorithmPurity)
                lines.Add(FormattableString.Invariant($"The groups follow the condition more than the algorithm ({conditionPurity * 100:0}% of runs sit with their condition's majority): the floor plan or settings shape the outcome more than the choice of algorithm."));
            else if (algorithmPurity >= 0.8)
                lines.Add(FormattableString.Invariant($"The groups line up with the algorithms ({algorithmPurity * 100:0}% of runs share a group with most of their algorithm's runs): each algorithm behaves consistently."));

            // An algorithm whose runs split between groups far apart in coverage behaves inconsistently.
            foreach (var section in report.conditions)
                foreach (var group in section.algorithms)
                {
                    if (group.runs.Count < 4) continue;
                    var byCluster = new Dictionary<int, int>();
                    foreach (var run in group.runs)
                        if (findings.clusterOf.TryGetValue(run, out int c)) Increment(byCluster, c);
                    if (byCluster.Count < 2) continue;

                    int high = int.MaxValue, low = int.MinValue;
                    foreach (int c in byCluster.Keys)
                    {
                        if (byCluster[c] < 2) continue;
                        high = Math.Min(high, c);
                        low = Math.Max(low, c);
                    }
                    if (high == int.MaxValue || high == low) continue;
                    double gap = findings.clusters[high].centroid[0] - findings.clusters[low].centroid[0];
                    if (gap < 10) continue;
                    lines.Add(FormattableString.Invariant($"{group.name} on {section.levelName} is inconsistent: {byCluster[high]} of {group.runs.Count} runs land in group {high + 1} ({findings.clusters[high].description}) and {byCluster[low]} in group {low + 1} ({findings.clusters[low].description}), {gap:0} coverage points apart. Its outcome depends on the seed, so its median is a better guide than its mean."));
                }
            return lines;
        }

        static void Increment(Dictionary<int, int> counts, int key)
        {
            counts.TryGetValue(key, out int n);
            counts[key] = n + 1;
        }

        // Share of runs that sit in the cluster holding most of their category's runs.
        static double Purity(List<RunCluster> clusters, Func<RunCluster, Dictionary<string, int>> category)
        {
            var bestCount = new Dictionary<string, int>();
            int total = 0;
            foreach (var cluster in clusters)
                foreach (var pair in category(cluster))
                {
                    total += pair.Value;
                    bestCount.TryGetValue(pair.Key, out int best);
                    if (pair.Value > best) bestCount[pair.Key] = pair.Value;
                }
            int pure = 0;
            foreach (int v in bestCount.Values) pure += v;
            return total > 0 ? (double)pure / total : 0;
        }
    }

    /// <summary>The plain-language summary at the top of every report, written from its test results.</summary>
    public static class ReportNarrative
    {
        public static List<string> Summarise(ComparativeReport report)
        {
            var lines = new List<string>();
            if (report.runCount == 0)
            {
                lines.Add("No runs to compare.");
                return lines;
            }

            foreach (var section in report.conditions)
            {
                var coverage = section.For(ReportMetrics.Coverage);
                if (section.algorithms.Count < 2)
                {
                    var only = section.algorithms[0];
                    lines.Add($"**{section.label}**: only {only.name} was run ({only.runs.Count} run(s), median coverage {ReportMetrics.Coverage.Format(only[ReportMetrics.Coverage].median)}), so there is nothing to compare it with.");
                    continue;
                }

                lines.Add($"**{section.label}**: {Leader(coverage)}");
                foreach (var metric in ReportMetrics.All)
                {
                    if (metric == ReportMetrics.Coverage) continue;
                    string line = SecondaryLine(section.For(metric));
                    if (line != null) lines.Add(line);
                }
            }

            int small = 0;
            foreach (var section in report.conditions)
                foreach (var group in section.algorithms)
                    if (group.runs.Count < 5 && section.algorithms.Count > 1) small++;
            if (small > 0)
                lines.Add($"{small} algorithm group(s) have fewer than 5 runs, so the tests have little power there: read \"no significant difference\" as inconclusive and run more seeds.");

            var cross = report.cross;
            if (cross != null && cross.conditions.Count >= 2 && cross.meanRanks != null)
            {
                int best = 0;
                for (int j = 1; j < cross.meanRanks.Length; j++) if (cross.meanRanks[j] < cross.meanRanks[best]) best = j;
                string verdict = cross.friedman != null && cross.friedman.Valid
                    ? cross.friedman.pValue < report.options.alpha
                        ? $"and the ranking is consistent across conditions (Friedman {ComparativeReport.PText(cross.friedman.pValue)})"
                        : $"but the ranking is not consistent enough across conditions to be significant (Friedman {ComparativeReport.PText(cross.friedman.pValue)})"
                    : "(too few conditions to test the ranking)";
                lines.Add(FormattableString.Invariant($"Across {cross.conditions.Count} shared conditions, {cross.algorithmNames[best]} ranks best on coverage on average (mean rank {cross.meanRanks[best]:0.0}, best in {cross.wins[best]}) ") + verdict + ".");
            }

            if (cross != null)
                foreach (var (name, count, test) in cross.sensitivity)
                    if (test.Valid && test.pValue < report.options.alpha)
                        lines.Add($"{name}'s coverage changes significantly with the condition ({count} conditions, Kruskal–Wallis {ComparativeReport.PText(test.pValue)}, {test.Magnitude} effect).");

            if (report.assisted != null) lines.AddRange(report.assisted.narrative);
            return lines;
        }

        static string Leader(MetricComparison comparison)
        {
            var metric = comparison.metric;
            var leader = comparison.ranking[0];
            var d = leader[metric];
            var text = new StringBuilder($"{leader.name} has the highest median {metric.label.ToLowerInvariant()} ({metric.Format(d.median)}, 95% CI {metric.Format(d.medianLow)}–{metric.Format(d.medianHigh)}, n = {d.n}).");

            var beats = new List<string>();
            var ties = new List<string>();
            foreach (var pair in comparison.pairs)
            {
                if (pair.a != leader && pair.b != leader) continue;
                var other = pair.a == leader ? pair.b : pair.a;
                if (pair.better == leader) beats.Add($"{other.name} ({ComparativeReport.PText(pair.pAdjusted)}, {pair.test.Magnitude} effect)");
                else if (pair.better == null && pair.test.Valid) ties.Add(other.name);
            }

            if (comparison.omnibus != null && comparison.omnibus.Valid && comparison.omnibus.pValue >= 0.05 && beats.Count == 0)
                text.Append($" Overall the algorithms do not differ significantly ({comparison.omnibus.test} {ComparativeReport.PText(comparison.omnibus.pValue)}).");
            if (beats.Count > 0) text.Append(" It is significantly better than ").Append(string.Join(", ", beats)).Append(" after Holm correction.");
            if (ties.Count > 0) text.Append(" It cannot be told apart from ").Append(string.Join(", ", ties)).Append('.');
            return text.ToString();
        }

        static string SecondaryLine(MetricComparison comparison)
        {
            if (comparison == null || comparison.pairs.Count == 0) return null;
            var leader = comparison.ranking[0];
            int wins = 0, others = comparison.ranking.Count - 1;
            foreach (var pair in comparison.pairs)
                if ((pair.a == leader || pair.b == leader) && pair.better == leader) wins++;
            if (wins == 0) return null;

            var metric = comparison.metric;
            string direction = metric.higherIsBetter ? "best" : "lowest";
            return $"{metric.label}: {leader.name} is {direction} ({metric.Format(leader[metric].median)} median), significantly ahead of {wins} of {others} other algorithm(s).";
        }
    }
}
