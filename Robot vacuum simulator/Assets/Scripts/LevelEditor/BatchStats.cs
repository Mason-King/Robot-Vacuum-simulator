using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using RobotVacuum.Sim;
using UnityEngine;

namespace RobotVacuum.LevelEditor
{
    /// <summary>Mean, spread and a 95% confidence interval for one measure over a set of runs.</summary>
    public readonly struct Summary
    {
        public readonly int count;
        public readonly double mean;
        public readonly double variance;

        /// <summary>Half the width of the 95% confidence interval for the mean (Student's t).</summary>
        public readonly double ci95;

        public Summary(int count, double mean, double variance, double ci95)
        {
            this.count = count;
            this.mean = mean;
            this.variance = variance;
            this.ci95 = ci95;
        }

        public double StandardDeviation => Math.Sqrt(variance);

        /// <summary>The 95% confidence interval of the mean: mean − ci95 to mean + ci95.</summary>
        public double Low => mean - ci95;
        public double High => mean + ci95;

        public static Summary Of(IReadOnlyList<double> values)
        {
            int n = values?.Count ?? 0;
            if (n == 0) return new Summary(0, 0, 0, 0);

            double mean = 0;
            foreach (double value in values) mean += value;
            mean /= n;

            if (n == 1) return new Summary(1, mean, 0, 0);

            double squares = 0;
            foreach (double value in values) squares += (value - mean) * (value - mean);
            double variance = squares / (n - 1); // sample variance
            double ci = BatchStats.TCritical95(n - 1) * Math.Sqrt(variance / n);
            return new Summary(n, mean, variance, ci);
        }
    }

    /// <summary>
    /// One algorithm's results across a batch's seeds. <see cref="runs"/> holds every trial, failed ones included,
    /// so each can be opened; the statistics use only the trials that succeeded (<see cref="succeeded"/>), the same
    /// rule as the cloud's campaign_results.json, so both report the same numbers for the same runs (2.8).
    /// </summary>
    public sealed class AlgorithmSummary
    {
        /// <summary>The algorithm's <see cref="RunReport.AlgorithmKey"/>; runs are grouped by it.</summary>
        public string algorithmId;
        public string name;
        public List<RunReport> runs = new List<RunReport>();

        /// <summary>The trials the statistics are computed from: those whose algorithm didn't fail.</summary>
        public List<RunReport> succeeded = new List<RunReport>();

        /// <summary>Trials left out of the statistics because the algorithm failed.</summary>
        public int failed;
        public Summary coverage;
        public Summary seconds;
        public Summary batteryUsed;
        public Summary distance;
        public Summary revisits;
        public Summary timeTo90;

        public string Label => name;
    }

    /// <summary>Aggregates batch results: per-algorithm statistics, averaged coverage curves and heatmaps.</summary>
    public static class BatchStats
    {
        // Two-sided 95% critical values of Student's t for 1–30 degrees of freedom.
        static readonly double[] TTable =
        {
            12.706, 4.303, 3.182, 2.776, 2.571, 2.447, 2.365, 2.306, 2.262, 2.228,
            2.201, 2.179, 2.160, 2.145, 2.131, 2.120, 2.110, 2.101, 2.093, 2.086,
            2.080, 2.074, 2.069, 2.064, 2.060, 2.056, 2.052, 2.048, 2.045, 2.042,
        };

        public static double TCritical95(int degreesOfFreedom)
        {
            if (degreesOfFreedom < 1) return 0;
            if (degreesOfFreedom <= TTable.Length) return TTable[degreesOfFreedom - 1];
            if (degreesOfFreedom <= 60) return 2.000;
            if (degreesOfFreedom <= 120) return 1.980;
            return 1.960;
        }

        /// <summary>Per-algorithm statistics for one batch's runs, best mean coverage first.</summary>
        public static List<AlgorithmSummary> Summarise(IEnumerable<RunReport> runs)
        {
            var byAlgorithm = new Dictionary<string, AlgorithmSummary>();
            foreach (var run in runs)
            {
                string key = run.AlgorithmKey;
                if (!byAlgorithm.TryGetValue(key, out var group))
                    byAlgorithm[key] = group = new AlgorithmSummary { algorithmId = key, name = run.AlgorithmName };
                group.runs.Add(run);
            }

            var result = new List<AlgorithmSummary>(byAlgorithm.Values);
            foreach (var group in result)
            {
                group.runs.Sort((a, b) => a.seed.CompareTo(b.seed));
                group.succeeded = group.runs.FindAll(Succeeded);
                group.failed = group.runs.Count - group.succeeded.Count;
                var good = group.succeeded;
                group.coverage = Summary.Of(good.ConvertAll(r => (double)r.coveragePercent));
                group.seconds = Summary.Of(good.ConvertAll(r => (double)r.durationSeconds));
                group.batteryUsed = Summary.Of(good.ConvertAll(r => (double)r.batteryUsed));
                group.distance = Summary.Of(good.ConvertAll(r => (double)r.distanceMeters));
                group.revisits = Summary.Of(good.ConvertAll(r => (double)r.revisitCount));
                var reached = good.FindAll(r => r.timeTo90 >= 0f);
                group.timeTo90 = Summary.Of(reached.ConvertAll(r => (double)r.timeTo90));
            }

            result.Sort((a, b) => b.coverage.mean.CompareTo(a.coverage.mean));
            return result;
        }

        /// <summary>True for a run whose algorithm didn't fail; only these count toward the statistics.</summary>
        public static bool Succeeded(RunReport run) => run != null && string.IsNullOrEmpty(run.failure);

        /// <summary>
        /// A batch as CSV, for checking the statistics by hand (AT-2.8-2) or analysing them elsewhere (2.19): one
        /// "run" row per trial, then one "summary" row per algorithm and measure (coverage, time, battery) with the
        /// count, mean, sample variance, standard deviation and the 95% confidence interval. Same columns as the
        /// campaign_runs.csv vacuumctl writes. Invariant culture, so it opens the same in any locale.
        /// </summary>
        public static string ToCsv(IEnumerable<RunReport> runs)
        {
            var list = new List<RunReport>(runs);
            var summaries = Summarise(list);
            var csv = new StringBuilder();
            csv.AppendLine("section,algorithm,seed,succeeded,coverage_percent,time_s,battery_used,distance_m,revisits,time_to_90_s,floor_plan,configuration,end_reason");
            foreach (var summary in summaries)
                foreach (var run in summary.runs)
                    csv.AppendLine(string.Join(",", "run", Field(summary.name), Num(run.seed), Succeeded(run) ? "true" : "false",
                        Num(run.coveragePercent), Num(run.durationSeconds), Num(run.batteryUsed), Num(run.distanceMeters),
                        Num(run.revisitCount), run.timeTo90 >= 0f ? Num(run.timeTo90) : string.Empty, Field(run.levelName),
                        Field(run.configuration), Field(run.endReason)));

            csv.AppendLine();
            csv.AppendLine("section,algorithm,measure,n,mean,variance,sd,ci95_half_width,ci95_low,ci95_high,t_critical,floor_plan,configuration");
            foreach (var summary in summaries)
            {
                var first = summary.runs[0];
                AppendSummary(csv, summary.name, "coverage_percent", summary.coverage, first);
                AppendSummary(csv, summary.name, "time_s", summary.seconds, first);
                AppendSummary(csv, summary.name, "battery_used", summary.batteryUsed, first);
            }
            return csv.ToString();
        }

        static void AppendSummary(StringBuilder csv, string algorithm, string measure, Summary s, RunReport first) =>
            csv.AppendLine(string.Join(",", "summary", Field(algorithm), measure, Num(s.count), Num(s.mean), Num(s.variance),
                Num(s.StandardDeviation), Num(s.ci95), Num(s.Low), Num(s.High), Num(TCritical95(s.count - 1)),
                Field(first.levelName), Field(first.configuration)));

        static string Num(double value) => value.ToString("R", CultureInfo.InvariantCulture);
        static string Num(long value) => value.ToString(CultureInfo.InvariantCulture);

        static string Field(string text)
        {
            text ??= string.Empty;
            return text.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0 ? text : "\"" + text.Replace("\"", "\"\"") + "\"";
        }

        /// <summary>
        /// The mean coverage curve across <paramref name="runs"/>, resampled to <paramref name="points"/> evenly
        /// spaced times up to the longest run. A run that has ended holds its final value.
        /// </summary>
        public static float[] AverageCurve(IReadOnlyList<RunReport> runs, int points, out float duration)
        {
            duration = 0f;
            foreach (var run in runs) duration = Mathf.Max(duration, run.durationSeconds);

            var curve = new float[Mathf.Max(2, points)];
            if (runs.Count == 0 || duration <= 0f) return curve;

            for (int i = 0; i < curve.Length; i++)
            {
                float t = duration * i / (curve.Length - 1);
                float sum = 0f;
                foreach (var run in runs) sum += CoverageAt(run, t);
                curve[i] = sum / runs.Count;
            }
            return curve;
        }

        /// <summary>A run's coverage at simulated time <paramref name="t"/>, from its samples.</summary>
        public static float CoverageAt(RunReport run, float t)
        {
            var samples = run.coverageSamples;
            if (samples == null || samples.Count == 0) return run.coveragePercent;
            if (t >= run.durationSeconds) return run.coveragePercent;

            float position = t / Mathf.Max(1e-4f, run.sampleInterval);
            int index = Mathf.Clamp(Mathf.FloorToInt(position), 0, samples.Count - 1);
            int next = Mathf.Min(index + 1, samples.Count - 1);
            return Mathf.Lerp(samples[index], samples[next], position - index);
        }

        /// <summary>
        /// The per-block average of several runs' coverage maps, as cleanliness 0–1 or -1 for no floor.
        /// Only maps the same shape as the first are averaged. Null when no run has a map.
        /// </summary>
        public static float[] AverageMap(IReadOnlyList<RunReport> runs, out RunReport shape)
        {
            shape = null;
            float[] sums = null;
            int[] counts = null;

            foreach (var run in runs)
            {
                var values = DecodeMap(run.coverageMap, run.mapWidth * run.mapHeight);
                if (values == null) continue;

                if (shape == null)
                {
                    shape = run;
                    sums = new float[values.Length];
                    counts = new int[values.Length];
                }
                else if (run.mapWidth != shape.mapWidth || run.mapHeight != shape.mapHeight) continue;

                for (int i = 0; i < values.Length; i++)
                {
                    if (values[i] < 0f) continue;
                    sums[i] += values[i];
                    counts[i]++;
                }
            }

            if (sums == null) return null;
            for (int i = 0; i < sums.Length; i++) sums[i] = counts[i] > 0 ? sums[i] / counts[i] : -1f;
            return sums;
        }

        /// <summary>Decodes a base64 coverage map: 0–250 → cleanliness 0–1, 255 → -1 (no floor).</summary>
        public static float[] DecodeMap(string map, int expectedLength)
        {
            if (string.IsNullOrEmpty(map) || expectedLength <= 0) return null;

            byte[] bytes;
            try { bytes = Convert.FromBase64String(map); }
            catch (FormatException) { return null; }
            if (bytes.Length != expectedLength) return null;

            var values = new float[bytes.Length];
            for (int i = 0; i < bytes.Length; i++) values[i] = bytes[i] == 255 ? -1f : bytes[i] / 250f;
            return values;
        }
    }
}
