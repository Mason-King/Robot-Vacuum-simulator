using System;
using System.Collections.Generic;

namespace RobotVacuum.LevelEditor
{
    /// <summary>The outcome of one hypothesis test. <see cref="pValue"/> is NaN when the test could not be run.</summary>
    public sealed class TestResult
    {
        public string test;
        public double statistic = double.NaN;
        public double pValue = double.NaN;

        /// <summary>Observations the test used: pairs for paired tests, runs for unpaired ones, blocks for Friedman.</summary>
        public int n;

        /// <summary>True when the p-value comes from the exact permutation distribution, not an approximation.</summary>
        public bool exact;

        public string effectName;
        public double effect = double.NaN;

        /// <summary>Why the test was not run, or a caveat about it.</summary>
        public string note;

        public bool Valid => !double.IsNaN(pValue);

        public string Magnitude => Statistics.Magnitude(effectName, effect);

        public static TestResult NotRun(string test, string why) => new TestResult { test = test, note = why };
    }

    /// <summary>
    /// The statistics behind comparative reports. Algorithm results are rarely normal (coverage piles up
    /// against 100%, runs that never reach a milestone are censored), so the primary tests are rank-based:
    /// Wilcoxon signed-rank for paired seeds, Mann–Whitney U otherwise, Friedman and Kruskal–Wallis across
    /// several algorithms. Small samples get exact permutation p-values. t-tests are kept as a secondary check.
    /// Values may be +∞ for "never reached"; rank tests treat that as worse than any finite time.
    /// </summary>
    public static class Statistics
    {
        /// <summary>Above this many observations, the exact permutation distribution gives way to the normal approximation.</summary>
        public const int ExactLimit = 60;

        // ---------------------------------------------------------------- descriptive

        public static double Median(IReadOnlyList<double> values) => Quantile(values, 0.5);

        /// <summary>The <paramref name="q"/> quantile with linear interpolation (type 7, as R and NumPy default to).</summary>
        public static double Quantile(IReadOnlyList<double> values, double q)
        {
            int n = values?.Count ?? 0;
            if (n == 0) return double.NaN;
            var sorted = new double[n];
            for (int i = 0; i < n; i++) sorted[i] = values[i];
            Array.Sort(sorted);
            return QuantileSorted(sorted, q);
        }

        static double QuantileSorted(double[] sorted, double q)
        {
            double position = (sorted.Length - 1) * Math.Max(0, Math.Min(1, q));
            int low = (int)Math.Floor(position);
            int high = Math.Min(low + 1, sorted.Length - 1);
            double fraction = position - low;
            if (fraction <= 0 || sorted[high] == sorted[low]) return sorted[low];
            if (double.IsInfinity(sorted[high])) return sorted[high];
            return sorted[low] + (sorted[high] - sorted[low]) * fraction;
        }

        /// <summary>A percentile bootstrap 95% confidence interval for the median, reproducible for a given seed.</summary>
        public static (double low, double high) BootstrapMedianCi(IReadOnlyList<double> values, int resamples = 2000, int seed = 20251118)
        {
            int n = values?.Count ?? 0;
            if (n < 2 || resamples < 10) return (double.NaN, double.NaN);

            var random = new Random(seed);
            var medians = new double[resamples];
            var sample = new double[n];
            for (int b = 0; b < resamples; b++)
            {
                for (int i = 0; i < n; i++) sample[i] = values[random.Next(n)];
                Array.Sort(sample);
                medians[b] = QuantileSorted(sample, 0.5);
            }

            Array.Sort(medians);
            return (QuantileSorted(medians, 0.025), QuantileSorted(medians, 0.975));
        }

        /// <summary>Mid-ranks (1-based, ties share their average rank) and Σ(t³ − t) over the tie groups.</summary>
        public static double[] Ranks(IReadOnlyList<double> values, out double tieSum)
        {
            int n = values.Count;
            var order = new int[n];
            for (int i = 0; i < n; i++) order[i] = i;
            Array.Sort(order, (a, b) => values[a].CompareTo(values[b]));

            var ranks = new double[n];
            tieSum = 0;
            for (int i = 0; i < n;)
            {
                int j = i;
                while (j + 1 < n && values[order[j + 1]].CompareTo(values[order[i]]) == 0) j++;
                double rank = (i + j) / 2.0 + 1;
                for (int m = i; m <= j; m++) ranks[order[m]] = rank;
                double t = j - i + 1;
                tieSum += t * t * t - t;
                i = j + 1;
            }
            return ranks;
        }

        // ---------------------------------------------------------------- two-sample tests

        /// <summary>
        /// Wilcoxon signed-rank test on pairs (x[i], y[i]); zero differences are dropped (Wilcoxon's method).
        /// Effect size: matched-pairs rank-biserial correlation, positive when x tends to be larger.
        /// </summary>
        public static TestResult WilcoxonSignedRank(IReadOnlyList<double> x, IReadOnlyList<double> y)
        {
            const string Name = "Wilcoxon signed-rank";
            if (x == null || y == null || x.Count != y.Count) return TestResult.NotRun(Name, "pairs do not line up");

            var diffs = new List<double>();
            for (int i = 0; i < x.Count; i++)
            {
                double d = Difference(x[i], y[i]);
                if (Math.Abs(d) > 1e-9) diffs.Add(d);
            }

            int n = diffs.Count;
            if (n == 0)
                return new TestResult { test = Name, statistic = 0, pValue = 1, n = x.Count, exact = true, effectName = "rank-biserial r", effect = 0, note = "every pair tied" };

            var magnitudes = diffs.ConvertAll(Math.Abs);
            var ranks = Ranks(magnitudes, out double tieSum);
            double plus = 0;
            for (int i = 0; i < n; i++) if (diffs[i] > 0) plus += ranks[i];
            double total = n * (n + 1) / 2.0;
            double minus = total - plus;

            var result = new TestResult
            {
                test = Name,
                statistic = Math.Min(plus, minus),
                n = x.Count,
                effectName = "rank-biserial r",
                effect = (plus - minus) / total,
            };
            if (n < x.Count) result.note = $"{x.Count - n} tied pair(s) dropped";

            if (n <= ExactLimit)
            {
                // Under H0 each rank's sign is a fair coin. Doubled mid-ranks are integers, so the null
                // distribution of the doubled W+ is a subset-sum count.
                var doubled = new int[n];
                int maxSum = 0;
                for (int i = 0; i < n; i++) maxSum += doubled[i] = (int)Math.Round(2 * ranks[i]);
                var counts = new double[maxSum + 1];
                counts[0] = 1;
                foreach (int r in doubled)
                    for (int s = maxSum; s >= r; s--) counts[s] += counts[s - r];

                int observed = (int)Math.Round(2 * plus);
                result.pValue = TwoSidedFromCounts(counts, observed, Math.Pow(2, n));
                result.exact = true;
            }
            else
            {
                double mean = total / 2;
                double variance = n * (n + 1) * (2 * n + 1) / 24.0 - tieSum / 48.0;
                result.pValue = variance > 0 ? NormalTwoSidedP(ContinuityZ(plus, mean, variance)) : 1;
            }
            return result;
        }

        /// <summary>Paired t-test on x[i] − y[i]. Effect size: Cohen's d<sub>z</sub> (mean difference over its SD).</summary>
        public static TestResult PairedT(IReadOnlyList<double> x, IReadOnlyList<double> y)
        {
            const string Name = "paired t";
            if (x == null || y == null || x.Count != y.Count) return TestResult.NotRun(Name, "pairs do not line up");
            if (x.Count < 2) return TestResult.NotRun(Name, "needs at least 2 pairs");

            var diffs = new List<double>();
            for (int i = 0; i < x.Count; i++)
            {
                if (double.IsInfinity(x[i]) || double.IsInfinity(y[i])) return TestResult.NotRun(Name, "not defined when some runs never reached the milestone");
                diffs.Add(x[i] - y[i]);
            }

            var summary = Summary.Of(diffs);
            double sd = summary.StandardDeviation;
            var result = new TestResult { test = Name, n = diffs.Count, effectName = "Cohen's dz" };
            if (sd <= 1e-12)
            {
                result.statistic = 0;
                result.pValue = Math.Abs(summary.mean) <= 1e-12 ? 1 : 0;
                result.note = "differences have no spread";
                return result;
            }

            result.statistic = summary.mean / (sd / Math.Sqrt(diffs.Count));
            result.pValue = StudentTTwoSidedP(result.statistic, diffs.Count - 1);
            result.effect = summary.mean / sd;
            return result;
        }

        /// <summary>
        /// Mann–Whitney U test for two independent samples. Effect size: Cliff's δ, positive when x tends to be larger.
        /// </summary>
        public static TestResult MannWhitney(IReadOnlyList<double> x, IReadOnlyList<double> y)
        {
            const string Name = "Mann–Whitney U";
            int n1 = x?.Count ?? 0, n2 = y?.Count ?? 0;
            if (n1 < 1 || n2 < 1) return TestResult.NotRun(Name, "each group needs runs");

            var combined = new List<double>(n1 + n2);
            combined.AddRange(x);
            combined.AddRange(y);
            var ranks = Ranks(combined, out double tieSum);
            double rankSum = 0;
            for (int i = 0; i < n1; i++) rankSum += ranks[i];
            double u1 = rankSum - n1 * (n1 + 1) / 2.0;
            double product = (double)n1 * n2;

            var result = new TestResult
            {
                test = Name,
                statistic = Math.Min(u1, product - u1),
                n = n1 + n2,
                effectName = "Cliff's δ",
                effect = 2 * u1 / product - 1,
            };

            int total = n1 + n2;
            if (total <= 40)
            {
                // Exact: every way of choosing n1 of the (doubled) mid-ranks is equally likely under H0.
                var doubled = new int[total];
                int maxSum = 0;
                for (int i = 0; i < total; i++) maxSum += doubled[i] = (int)Math.Round(2 * ranks[i]);
                var ways = new double[n1 + 1, maxSum + 1];
                ways[0, 0] = 1;
                for (int item = 0; item < total; item++)
                    for (int k = Math.Min(item + 1, n1); k >= 1; k--)
                        for (int s = maxSum; s >= doubled[item]; s--)
                            ways[k, s] += ways[k - 1, s - doubled[item]];

                var counts = new double[maxSum + 1];
                double all = 0;
                for (int s = 0; s <= maxSum; s++) all += counts[s] = ways[n1, s];
                result.pValue = TwoSidedFromCounts(counts, (int)Math.Round(2 * rankSum), all);
                result.exact = true;
            }
            else
            {
                double variance = product / 12.0 * (total + 1 - tieSum / ((double)total * (total - 1)));
                result.pValue = variance > 0 ? NormalTwoSidedP(ContinuityZ(u1, product / 2, variance)) : 1;
            }
            return result;
        }

        /// <summary>Welch's unequal-variance t-test. Effect size: Cohen's d with the pooled SD.</summary>
        public static TestResult WelchT(IReadOnlyList<double> x, IReadOnlyList<double> y)
        {
            const string Name = "Welch t";
            if ((x?.Count ?? 0) < 2 || (y?.Count ?? 0) < 2) return TestResult.NotRun(Name, "each group needs at least 2 runs");
            foreach (double v in x) if (double.IsInfinity(v)) return TestResult.NotRun(Name, "not defined when some runs never reached the milestone");
            foreach (double v in y) if (double.IsInfinity(v)) return TestResult.NotRun(Name, "not defined when some runs never reached the milestone");

            var a = Summary.Of(x);
            var b = Summary.Of(y);
            double va = a.variance / a.count, vb = b.variance / b.count;
            double se = Math.Sqrt(va + vb);
            var result = new TestResult { test = Name, n = a.count + b.count, effectName = "Cohen's d" };
            double pooled = Math.Sqrt(((a.count - 1) * a.variance + (b.count - 1) * b.variance) / (a.count + b.count - 2));
            if (se <= 1e-12)
            {
                result.statistic = 0;
                result.pValue = Math.Abs(a.mean - b.mean) <= 1e-12 ? 1 : 0;
                result.note = "groups have no spread";
                return result;
            }

            result.statistic = (a.mean - b.mean) / se;
            double df = (va + vb) * (va + vb) / (va * va / (a.count - 1) + vb * vb / (b.count - 1));
            result.pValue = StudentTTwoSidedP(result.statistic, df);
            result.effect = pooled > 1e-12 ? (a.mean - b.mean) / pooled : double.NaN;
            return result;
        }

        // ---------------------------------------------------------------- k-sample tests

        /// <summary>
        /// Friedman test: <paramref name="blocks"/>[i][j] is treatment j in block i (a seed, or a condition).
        /// Effect size: Kendall's W (0 = no agreement on the ordering, 1 = every block ranks them the same).
        /// </summary>
        public static TestResult Friedman(IReadOnlyList<double[]> blocks)
        {
            const string Name = "Friedman";
            int n = blocks?.Count ?? 0;
            if (n < 2) return TestResult.NotRun(Name, "needs at least 2 blocks");
            int k = blocks[0].Length;
            if (k < 2) return TestResult.NotRun(Name, "needs at least 2 treatments");

            var rankSums = new double[k];
            double tieSum = 0;
            foreach (var block in blocks)
            {
                if (block.Length != k) return TestResult.NotRun(Name, "blocks differ in size");
                var ranks = Ranks(block, out double ties);
                tieSum += ties;
                for (int j = 0; j < k; j++) rankSums[j] += ranks[j];
            }

            double squares = 0;
            foreach (double r in rankSums) squares += r * r;
            double chi = 12.0 / (n * k * (k + 1)) * squares - 3.0 * n * (k + 1);
            double correction = 1 - tieSum / (n * ((double)k * k * k - k));
            var result = new TestResult { test = Name, n = n, effectName = "Kendall's W" };
            if (correction <= 1e-12)
            {
                result.statistic = 0;
                result.pValue = 1;
                result.effect = 0;
                result.note = "every block tied";
                return result;
            }

            chi = Math.Max(0, chi / correction);
            result.statistic = chi;
            result.pValue = ChiSquareSurvival(chi, k - 1);
            result.effect = chi / (n * (k - 1));
            if (n < 10 && k <= 3) result.note = "χ² approximation with few blocks";
            return result;
        }

        /// <summary>Kruskal–Wallis H test for independent groups. Effect size: ε² = H / (N − 1).</summary>
        public static TestResult KruskalWallis(IReadOnlyList<IReadOnlyList<double>> groups)
        {
            const string Name = "Kruskal–Wallis";
            if (groups == null || groups.Count < 2) return TestResult.NotRun(Name, "needs at least 2 groups");

            var combined = new List<double>();
            foreach (var group in groups)
            {
                if (group == null || group.Count == 0) return TestResult.NotRun(Name, "a group has no runs");
                combined.AddRange(group);
            }

            int total = combined.Count;
            if (total <= groups.Count) return TestResult.NotRun(Name, "needs more runs than groups");
            var ranks = Ranks(combined, out double tieSum);

            double h = 0;
            int offset = 0;
            foreach (var group in groups)
            {
                double sum = 0;
                for (int i = 0; i < group.Count; i++) sum += ranks[offset + i];
                h += sum * sum / group.Count;
                offset += group.Count;
            }
            h = 12.0 / (total * (total + 1.0)) * h - 3.0 * (total + 1);

            var result = new TestResult { test = Name, n = total, effectName = "ε²" };
            double correction = 1 - tieSum / ((double)total * total * total - total);
            if (correction <= 1e-12)
            {
                result.statistic = 0;
                result.pValue = 1;
                result.effect = 0;
                result.note = "every run tied";
                return result;
            }

            h = Math.Max(0, h / correction);
            result.statistic = h;
            result.pValue = ChiSquareSurvival(h, groups.Count - 1);
            result.effect = h / (total - 1);
            return result;
        }

        // ---------------------------------------------------------------- multiple comparisons, effect sizes

        /// <summary>Holm–Bonferroni adjusted p-values (family-wise error control). NaN entries are left out and stay NaN.</summary>
        public static double[] HolmAdjust(IReadOnlyList<double> pValues)
        {
            var adjusted = new double[pValues.Count];
            var valid = new List<int>();
            for (int i = 0; i < pValues.Count; i++)
            {
                adjusted[i] = double.NaN;
                if (!double.IsNaN(pValues[i])) valid.Add(i);
            }

            valid.Sort((a, b) => pValues[a].CompareTo(pValues[b]));
            int m = valid.Count;
            double running = 0;
            for (int rank = 0; rank < m; rank++)
            {
                int i = valid[rank];
                running = Math.Max(running, Math.Min(1, (m - rank) * pValues[i]));
                adjusted[i] = running;
            }
            return adjusted;
        }

        /// <summary>A conventional verbal label for an effect size (Romano et al. for δ and r, Cohen for d, Landis-style for W).</summary>
        public static string Magnitude(string effectName, double effect)
        {
            if (string.IsNullOrEmpty(effectName) || double.IsNaN(effect)) return "n/a";
            double a = Math.Abs(effect);
            switch (effectName)
            {
                case "Kendall's W": return a < 0.1 ? "negligible" : a < 0.3 ? "small" : a < 0.5 ? "moderate" : "large";
                case "ε²": return a < 0.01 ? "negligible" : a < 0.08 ? "small" : a < 0.26 ? "medium" : "large";
                case "Cohen's d":
                case "Cohen's dz": return a < 0.2 ? "negligible" : a < 0.5 ? "small" : a < 0.8 ? "medium" : "large";
                default: return a < 0.147 ? "negligible" : a < 0.33 ? "small" : a < 0.474 ? "medium" : "large";
            }
        }

        // ---------------------------------------------------------------- distributions

        public static double NormalCdf(double z) => 0.5 * Erfc(-z / Math.Sqrt(2));

        public static double NormalTwoSidedP(double z) => Math.Min(1, Erfc(Math.Abs(z) / Math.Sqrt(2)));

        /// <summary>Two-sided p-value of Student's t with <paramref name="df"/> degrees of freedom.</summary>
        public static double StudentTTwoSidedP(double t, double df)
        {
            if (df <= 0 || double.IsNaN(t)) return double.NaN;
            if (double.IsInfinity(t)) return 0;
            return Math.Min(1, RegularizedBeta(df / (df + t * t), df / 2, 0.5));
        }

        /// <summary>P(X ≥ x) for a χ² variable with <paramref name="df"/> degrees of freedom.</summary>
        public static double ChiSquareSurvival(double x, double df)
        {
            if (df <= 0 || double.IsNaN(x)) return double.NaN;
            if (x <= 0) return 1;
            double a = df / 2, half = x / 2;
            return half < a + 1 ? 1 - GammaSeries(a, half) : GammaContinuedFraction(a, half);
        }

        /// <summary>ln Γ(x) for x &gt; 0 (Lanczos approximation, as in Numerical Recipes, 3rd ed.).</summary>
        public static double LogGamma(double x)
        {
            double y = x, tmp = x + 5.24218750000000000;
            tmp = (x + 0.5) * Math.Log(tmp) - tmp;
            double series = 0.999999999999997092;
            foreach (double c in Lanczos) series += c / ++y;
            return tmp + Math.Log(2.5066282746310005 * series / x);
        }

        /// <summary>The regularized incomplete beta function I<sub>x</sub>(a, b).</summary>
        public static double RegularizedBeta(double x, double a, double b)
        {
            if (x <= 0) return 0;
            if (x >= 1) return 1;
            double front = Math.Exp(LogGamma(a + b) - LogGamma(a) - LogGamma(b) + a * Math.Log(x) + b * Math.Log(1 - x));
            return x < (a + 1) / (a + b + 2)
                ? front * BetaContinuedFraction(x, a, b) / a
                : 1 - front * BetaContinuedFraction(1 - x, b, a) / b;
        }

        static readonly double[] Lanczos =
        {
            57.1562356658629235, -59.5979603554754912, 14.1360979747417471, -0.491913816097620199,
            .339946499848118887e-4, .465236289270485756e-4, -.983744753048795646e-4, .158088703224912494e-3,
            -.210264441724104883e-3, .217439618115212643e-3, -.164318106536763890e-3, .844182239838527433e-4,
            -.261908384015814087e-4, .368991826595316234e-5,
        };

        const double Tiny = 1e-300;

        static double BetaContinuedFraction(double x, double a, double b)
        {
            double qab = a + b, qap = a + 1, qam = a - 1;
            double c = 1, d = 1 - qab * x / qap;
            if (Math.Abs(d) < Tiny) d = Tiny;
            d = 1 / d;
            double h = d;
            for (int m = 1; m <= 300; m++)
            {
                int m2 = 2 * m;
                double aa = m * (b - m) * x / ((qam + m2) * (a + m2));
                d = 1 + aa * d; if (Math.Abs(d) < Tiny) d = Tiny;
                c = 1 + aa / c; if (Math.Abs(c) < Tiny) c = Tiny;
                d = 1 / d;
                h *= d * c;
                aa = -(a + m) * (qab + m) * x / ((a + m2) * (qap + m2));
                d = 1 + aa * d; if (Math.Abs(d) < Tiny) d = Tiny;
                c = 1 + aa / c; if (Math.Abs(c) < Tiny) c = Tiny;
                d = 1 / d;
                double step = d * c;
                h *= step;
                if (Math.Abs(step - 1) < 1e-15) break;
            }
            return h;
        }

        // Lower regularized gamma P(a, x) by its series; converges fast for x < a + 1.
        static double GammaSeries(double a, double x)
        {
            double ap = a, sum = 1 / a, term = sum;
            for (int i = 0; i < 1000; i++)
            {
                ap++;
                term *= x / ap;
                sum += term;
                if (Math.Abs(term) < Math.Abs(sum) * 1e-15) break;
            }
            return sum * Math.Exp(-x + a * Math.Log(x) - LogGamma(a));
        }

        // Upper regularized gamma Q(a, x) by its continued fraction; converges fast for x ≥ a + 1.
        static double GammaContinuedFraction(double a, double x)
        {
            double b = x + 1 - a, c = 1 / Tiny, d = 1 / b, h = d;
            for (int i = 1; i < 1000; i++)
            {
                double an = -i * (i - a);
                b += 2;
                d = an * d + b; if (Math.Abs(d) < Tiny) d = Tiny;
                c = b + an / c; if (Math.Abs(c) < Tiny) c = Tiny;
                d = 1 / d;
                double step = d * c;
                h *= step;
                if (Math.Abs(step - 1) < 1e-15) break;
            }
            return Math.Exp(-x + a * Math.Log(x) - LogGamma(a)) * h;
        }

        // Complementary error function (Numerical Recipes erfcc), fractional error below 1.2e-7.
        static double Erfc(double x)
        {
            double z = Math.Abs(x);
            double t = 1 / (1 + 0.5 * z);
            double r = t * Math.Exp(-z * z - 1.26551223 + t * (1.00002368 + t * (0.37409196 + t * (0.09678418 +
                       t * (-0.18628806 + t * (0.27886807 + t * (-1.13520398 + t * (1.48851587 +
                       t * (-0.82215223 + t * 0.17087277)))))))));
            return x >= 0 ? r : 2 - r;
        }

        // ---------------------------------------------------------------- helpers

        /// <summary>x − y, with "both never reached" (∞ − ∞) counted as a tie.</summary>
        public static double Difference(double x, double y)
        {
            double d = x - y;
            return double.IsNaN(d) ? 0 : d;
        }

        static double ContinuityZ(double observed, double mean, double variance)
        {
            double deviation = observed - mean;
            double corrected = Math.Max(0, Math.Abs(deviation) - 0.5);
            return Math.Sign(deviation) * corrected / Math.Sqrt(variance);
        }

        static double TwoSidedFromCounts(double[] counts, int observed, double total)
        {
            observed = Math.Max(0, Math.Min(counts.Length - 1, observed));
            double lower = 0, upper = 0;
            for (int s = 0; s <= observed; s++) lower += counts[s];
            for (int s = observed; s < counts.Length; s++) upper += counts[s];
            return Math.Min(1, 2 * Math.Min(lower, upper) / total);
        }
    }
}
