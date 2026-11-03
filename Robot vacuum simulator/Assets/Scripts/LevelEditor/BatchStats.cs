using System;
using System.Collections.Generic;
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

    /// <summary>One algorithm's results across a batch's seeds.</summary>
    public sealed class AlgorithmSummary
    {
        public MovementPattern pattern;
        public List<RunReport> runs = new List<RunReport>();
        public Summary coverage;
        public Summary seconds;
        public Summary batteryUsed;
        public Summary distance;
        public Summary revisits;
        public Summary timeTo90;

        public string Label => MovementAlgorithms.LabelOf(pattern);
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
            var byPattern = new Dictionary<MovementPattern, AlgorithmSummary>();
            foreach (var run in runs)
            {
                if (!byPattern.TryGetValue(run.pattern, out var group))
                    byPattern[run.pattern] = group = new AlgorithmSummary { pattern = run.pattern };
                group.runs.Add(run);
            }

            var result = new List<AlgorithmSummary>(byPattern.Values);
            foreach (var group in result)
            {
                group.runs.Sort((a, b) => a.seed.CompareTo(b.seed));
                group.coverage = Summary.Of(group.runs.ConvertAll(r => (double)r.coveragePercent));
                group.seconds = Summary.Of(group.runs.ConvertAll(r => (double)r.durationSeconds));
                group.batteryUsed = Summary.Of(group.runs.ConvertAll(r => (double)r.batteryUsed));
                group.distance = Summary.Of(group.runs.ConvertAll(r => (double)r.distanceMeters));
                group.revisits = Summary.Of(group.runs.ConvertAll(r => (double)r.revisitCount));
                var reached = group.runs.FindAll(r => r.timeTo90 >= 0f);
                group.timeTo90 = Summary.Of(reached.ConvertAll(r => (double)r.timeTo90));
            }

            result.Sort((a, b) => b.coverage.mean.CompareTo(a.coverage.mean));
            return result;
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
