using System;
using System.Collections.Generic;
using NUnit.Framework;
using RobotVacuum.LevelEditor;
using RobotVacuum.Sim;

namespace RobotVacuum.Tests
{
    public class StatisticsTests
    {
        [Test]
        public void Distributions_MatchTables()
        {
            Assert.AreEqual(0.05, Statistics.StudentTTwoSidedP(2.262, 9), 1e-3);
            Assert.AreEqual(0.05, Statistics.StudentTTwoSidedP(12.706, 1), 1e-3);
            Assert.AreEqual(0.05, Statistics.NormalTwoSidedP(1.959964), 1e-5);
            Assert.AreEqual(0.05, Statistics.ChiSquareSurvival(3.841, 1), 1e-3);
            Assert.AreEqual(0.05, Statistics.ChiSquareSurvival(5.991, 2), 1e-3);
            Assert.AreEqual(Math.Exp(-4), Statistics.ChiSquareSurvival(8, 2), 1e-9, "χ²(2) survival is e^(−x/2)");
        }

        [Test]
        public void Ranks_ShareTies()
        {
            var ranks = Statistics.Ranks(new[] { 10.0, 20, 20, 30, double.PositiveInfinity }, out double ties);
            CollectionAssert.AreEqual(new[] { 1.0, 2.5, 2.5, 4, 5 }, ranks);
            Assert.AreEqual(6.0, ties, "one pair: 2³ − 2");
        }

        [Test]
        public void Wilcoxon_ExactP_MatchesEnumeration()
        {
            // Five positive differences: only 1 of the 2⁵ sign patterns is as extreme in each tail.
            var test = Statistics.WilcoxonSignedRank(new double[] { 2, 4, 6, 8, 10 }, new double[] { 1, 2, 3, 4, 5 });
            Assert.IsTrue(test.exact);
            Assert.AreEqual(2.0 / 32, test.pValue, 1e-12);
            Assert.AreEqual(1.0, test.effect, 1e-12, "every pair favours x");

            var ten = Statistics.WilcoxonSignedRank(new double[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 }, new double[10]);
            Assert.AreEqual(2.0 / 1024, ten.pValue, 1e-12);
        }

        [Test]
        public void Wilcoxon_AllTied_IsNotSignificant()
        {
            var test = Statistics.WilcoxonSignedRank(new double[] { 5, 5, double.PositiveInfinity }, new double[] { 5, 5, double.PositiveInfinity });
            Assert.AreEqual(1.0, test.pValue);
        }

        [Test]
        public void MannWhitney_ExactP_MatchesEnumeration()
        {
            // Complete separation of 3 vs 3: 2 of C(6,3) = 20 splits are as extreme.
            var test = Statistics.MannWhitney(new double[] { 1, 2, 3 }, new double[] { 4, 5, 6 });
            Assert.IsTrue(test.exact);
            Assert.AreEqual(0.1, test.pValue, 1e-12);
            Assert.AreEqual(-1.0, test.effect, 1e-12, "Cliff's δ: x always smaller");
            Assert.AreEqual(0.0, test.statistic);
        }

        [Test]
        public void WelchT_MatchesHandWorkedValues()
        {
            // t = −3 / √2.5, Welch–Satterthwaite df = 6.25 / 1.0625 ≈ 5.88; p from direct integration of the t density.
            var test = Statistics.WelchT(new double[] { 1, 2, 3, 4, 5 }, new double[] { 2, 4, 6, 8, 10 });
            Assert.AreEqual(-3 / Math.Sqrt(2.5), test.statistic, 1e-9);
            Assert.AreEqual(0.107531, test.pValue, 1e-5);
        }

        [Test]
        public void Wilcoxon_MatchesPublishedExample()
        {
            // Hollander & Wolfe's depression scores (also SciPy's wilcoxon example): exact two-sided p = 5/128.
            var test = Statistics.WilcoxonSignedRank(
                new[] { 1.83, 0.50, 1.62, 2.48, 1.68, 1.88, 1.55, 3.06, 1.30 },
                new[] { 0.878, 0.647, 0.598, 2.05, 1.06, 1.29, 1.06, 3.14, 1.29 });
            Assert.AreEqual(0.0390625, test.pValue, 1e-12);
        }

        [Test]
        public void WilsonInterval_MatchesReferenceValues()
        {
            var (low, high) = Statistics.WilsonInterval(2, 10);
            Assert.AreEqual(0.0567, low, 1e-4);
            Assert.AreEqual(0.5098, high, 1e-4);

            (low, high) = Statistics.WilsonInterval(0, 10);
            Assert.AreEqual(0.0, low, 1e-12, "never below 0");
            Assert.AreEqual(0.2775, high, 1e-4, "but not 0 wide either");
        }

        [Test]
        public void Friedman_PerfectAgreement()
        {
            var blocks = new List<double[]> { new double[] { 1, 2, 3 }, new double[] { 2, 5, 9 }, new double[] { 0, 1, 4 }, new double[] { 3, 4, 5 } };
            var test = Statistics.Friedman(blocks);
            Assert.AreEqual(8.0, test.statistic, 1e-9);
            Assert.AreEqual(Math.Exp(-4), test.pValue, 1e-9);
            Assert.AreEqual(1.0, test.effect, 1e-9, "Kendall's W");
        }

        [Test]
        public void KruskalWallis_SeparatedGroups()
        {
            var test = Statistics.KruskalWallis(new List<IReadOnlyList<double>>
            {
                new double[] { 1, 2, 3 }, new double[] { 4, 5, 6 }, new double[] { 7, 8, 9 },
            });
            Assert.AreEqual(7.2, test.statistic, 1e-9);
            Assert.AreEqual(Math.Exp(-3.6), test.pValue, 1e-9);
        }

        [Test]
        public void Holm_IsMonotoneAndCapped()
        {
            var adjusted = Statistics.HolmAdjust(new[] { 0.01, 0.04, 0.03, double.NaN });
            Assert.AreEqual(0.03, adjusted[0], 1e-12);
            Assert.AreEqual(0.06, adjusted[1], 1e-12);
            Assert.AreEqual(0.06, adjusted[2], 1e-12);
            Assert.IsNaN(adjusted[3]);
        }

        [Test]
        public void BootstrapCi_BracketsTheMedian_AndIsReproducible()
        {
            var values = new double[] { 3, 1, 4, 1, 5, 9, 2, 6, 5, 3 };
            var (low, high) = Statistics.BootstrapMedianCi(values);
            Assert.LessOrEqual(low, Statistics.Median(values));
            Assert.GreaterOrEqual(high, Statistics.Median(values));
            Assert.AreEqual((low, high), Statistics.BootstrapMedianCi(values));
        }
    }

    public class ComparativeReportTests
    {
        static RunReport Run(MovementPattern pattern, int seed, float coverage, string level = "Flat", float timeTo90 = -1f) => new RunReport
        {
            id = Guid.NewGuid().ToString("N"),
            levelName = level,
            pattern = pattern,
            seed = seed,
            coveragePercent = coverage,
            durationSeconds = 600f,
            distanceMeters = 200f,
            batteryUsed = 100f,
            floorArea = 40f,
            revisitCount = 50,
            timeTo90 = timeTo90,
            conditionsRecorded = true,
            lidarEnabled = true,
            lidarHalfAngleDegrees = 180f,
            lidarRayCount = 360,
            floorSensorEnabled = true,
        };

        // Lawnmower clearly ahead; Random and Wall follow indistinguishable, all on seeds 1–10.
        static List<RunReport> Batch(string level = "Flat", float lawnmowerOnSeed7 = -1f)
        {
            var random = new Random(4);
            var runs = new List<RunReport>();
            for (int seed = 1; seed <= 10; seed++)
            {
                float lawn = seed == 7 && lawnmowerOnSeed7 >= 0 ? lawnmowerOnSeed7 : 92f + (float)random.NextDouble() * 4f;
                runs.Add(Run(MovementPattern.Lawnmower, seed, lawn, level, lawn >= 90f ? 300f : -1f));
                runs.Add(Run(MovementPattern.RandomBounce, seed, 60f + (float)random.NextDouble() * 10f, level));
                runs.Add(Run(MovementPattern.WallFollow, seed, 60f + (float)random.NextDouble() * 10f, level));
            }
            return runs;
        }

        [Test]
        public void Batch_IsComparedSeedBySeed_WithCorrectedTests()
        {
            var report = ComparativeReport.Build(Batch(), new ReportOptions { assisted = false });

            Assert.AreEqual(1, report.conditions.Count);
            var coverage = report.conditions[0].For(ReportMetrics.Coverage);
            Assert.AreEqual("Friedman", coverage.omnibus.test, "shared seeds → Friedman");
            Assert.Less(coverage.omnibus.pValue, 0.001);
            Assert.AreEqual(MovementPattern.Lawnmower, coverage.ranking[0].runs[0].pattern);

            foreach (var pair in coverage.pairs)
            {
                Assert.IsTrue(pair.paired);
                Assert.AreEqual("Wilcoxon signed-rank", pair.test.test);
                Assert.GreaterOrEqual(pair.pAdjusted, pair.test.pValue, "Holm never lowers p");
                bool involvesLawnmower = pair.a.runs[0].pattern == MovementPattern.Lawnmower || pair.b.runs[0].pattern == MovementPattern.Lawnmower;
                if (involvesLawnmower)
                {
                    Assert.IsTrue(pair.Significant);
                    Assert.AreEqual(MovementPattern.Lawnmower, pair.better.runs[0].pattern);
                    Assert.AreEqual("large", pair.test.Magnitude);
                }
            }

            var t90 = report.conditions[0].For(ReportMetrics.TimeTo90);
            Assert.AreEqual(MovementPattern.Lawnmower, t90.ranking[0].runs[0].pattern, "runs that never reach 90% rank last");
            Assert.AreEqual(10, t90.ranking[1][ReportMetrics.TimeTo90].censored);
        }

        [Test]
        public void Report_RendersMarkdownAndSummary()
        {
            var report = ComparativeReport.Build(Batch(), new ReportOptions { assisted = false });
            string md = report.ToMarkdown();

            StringAssert.Contains("Wilcoxon signed-rank", md);
            StringAssert.Contains("p (Holm)", md);
            StringAssert.Contains("## Methods", md);
            Assert.IsNull(report.assisted, "ML-assisted section is optional");
            Assert.IsTrue(report.summary.Exists(line => line.Contains("Lawnmower") && line.Contains("significantly better")), string.Join("\n", report.summary));
        }

        [Test]
        public void Conditions_AreSeparated_AndComparedAcross()
        {
            var runs = Batch("Flat");
            runs.AddRange(Batch("Cluttered"));
            var slam = Batch("Flat");
            foreach (var run in slam) { run.slam = true; run.noiseScale = 1f; }
            runs.AddRange(slam);

            var report = ComparativeReport.Build(runs, new ReportOptions { assisted = false });

            Assert.AreEqual(3, report.conditions.Count, "floor plan and sensing both define a condition");
            Assert.IsNotNull(report.cross);
            Assert.AreEqual(3, report.cross.conditions.Count);
            int lawnmower = report.cross.algorithmNames.IndexOf(MovementAlgorithms.LabelOf(MovementPattern.Lawnmower));
            Assert.AreEqual(1.0, report.cross.meanRanks[lawnmower], 1e-9);
            Assert.AreEqual(3, report.cross.wins[lawnmower]);
            Assert.AreEqual("Friedman", report.cross.friedman.test);
        }

        [Test]
        public void UnsharedSeeds_FallBackToIndependentTests()
        {
            var runs = new List<RunReport>();
            for (int i = 0; i < 6; i++)
            {
                runs.Add(Run(MovementPattern.Lawnmower, 100 + i, 90f + i));
                runs.Add(Run(MovementPattern.RandomBounce, 200 + i, 50f + i));
            }

            var pair = ComparativeReport.Build(runs, new ReportOptions { assisted = false }).conditions[0].For(ReportMetrics.Coverage).pairs[0];
            Assert.IsFalse(pair.paired);
            Assert.AreEqual("Mann–Whitney U", pair.test.test);
            Assert.IsTrue(pair.test.exact);
        }

        [Test]
        public void Assisted_FlagsTheOutlierTrial_AndClusters()
        {
            var report = ComparativeReport.Build(Batch(lawnmowerOnSeed7: 20f), new ReportOptions { assisted = true });

            Assert.IsNotNull(report.assisted);
            var flagged = report.assisted.anomalies.Find(a => a.run.seed == 7 && a.run.pattern == MovementPattern.Lawnmower && a.metric == "Coverage");
            Assert.IsNotNull(flagged, "the 20% Lawnmower trial stands out from its 92–96% siblings");
            Assert.Less(flagged.score, -AssistedAnalysis.AnomalyThreshold);

            Assert.GreaterOrEqual(report.assisted.clusters.Count, 2, report.assisted.clusteringNote);
            int total = 0;
            foreach (var cluster in report.assisted.clusters) total += cluster.runs.Count;
            Assert.AreEqual(30, total);
            StringAssert.Contains("ML-assisted analysis", report.ToMarkdown());
        }

        [Test]
        public void Failures_AreCountedByKind_AndShownBesideTheResults()
        {
            var runs = Batch();
            var lawnmower = runs.FindAll(r => r.pattern == MovementPattern.Lawnmower);
            lawnmower[0].failure = "NextGoal threw IndexOutOfRangeException";
            lawnmower[0].failureKind = "Crash";
            lawnmower[1].failure = "NextGoal did not return within 2 s";
            lawnmower[1].failureKind = "Hang";

            var report = ComparativeReport.Build(runs, new ReportOptions { assisted = false });
            var group = report.conditions[0].algorithms.Find(g => g.runs[0].pattern == MovementPattern.Lawnmower);

            Assert.AreEqual(2, group.Failed);
            CollectionAssert.AreEqual(new[] { "Crash", "Hang" }, group.FailureKinds().Keys);
            Assert.AreEqual(10, group.runs.Count, "failed runs stay in the comparisons");
            StringAssert.StartsWith("2 of 10 (20%, 95% CI 6–51%)", group.DescribeFailures());

            string md = report.ToMarkdown();
            StringAssert.Contains("### Reliability", md);
            StringAssert.Contains("| 10 | 2 | 20% [6–51%] | Crash 1, Hang 1 |", md);
            Assert.IsTrue(report.summary.Exists(l => l.StartsWith("Failures (kept in the comparisons)") && l.Contains("Lawnmower failed 2 of 10")),
                string.Join(" | ", report.summary));
        }

        [Test]
        public void NoFailures_AddNoSummaryLine()
        {
            var report = ComparativeReport.Build(Batch(), new ReportOptions { assisted = false });
            Assert.IsFalse(report.summary.Exists(l => l.StartsWith("Failures")));
            Assert.AreEqual("none of 10", report.conditions[0].algorithms[0].DescribeFailures());
        }

        [Test]
        public void KMeans_SeparatesObviousGroups()
        {
            var points = new List<double[]>();
            for (int i = 0; i < 10; i++) points.Add(new[] { i * 0.01, 0.0 });
            for (int i = 0; i < 10; i++) points.Add(new[] { 10 + i * 0.01, 10.0 });

            var labels = AssistedAnalysis.KMeans(points, 2, restarts: 4, seed: 1);
            for (int i = 1; i < 10; i++) Assert.AreEqual(labels[0], labels[i]);
            for (int i = 11; i < 20; i++) Assert.AreEqual(labels[10], labels[i]);
            Assert.AreNotEqual(labels[0], labels[10]);
            Assert.Greater(AssistedAnalysis.Silhouette(points, labels, 2), 0.9);
        }
    }
}
