using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using RobotVacuum.LevelEditor;
using UnityEngine;

namespace RobotVacuum.Tests
{
    /// <summary>
    /// Requirement 2.8 statistics in batches, and importing cloud / headless campaign results into the run history so
    /// Analysis shows them (2.18, 4.6). cloud/tests/test_results_in_app.py checks the vacuumctl side of the same contract.
    /// </summary>
    public class CampaignImportTests
    {
        string root;

        [SetUp]
        public void SetUp() => root = Path.Combine(Path.GetTempPath(), "campaign-import-" + Guid.NewGuid().ToString("N"));

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }

        static RunReport Run(string algorithm, float coverage, float seconds, float battery, int seed, string failure = "") => new RunReport
        {
            algorithmId = algorithm,
            algorithmName = algorithm,
            coveragePercent = coverage,
            durationSeconds = seconds,
            batteryUsed = battery,
            seed = seed,
            timeTo90 = -1f,
            failure = failure,
        };

        // ---------------------------------------------------------------- 2.8 statistics

        [Test]
        public void Summary_SharedFixture_MatchesVacuumctl()
        {
            // The same input and numbers as cloud/tests/test_results_in_app.py: 80, 82, 84, 86.
            var s = Summary.Of(new List<double> { 80, 82, 84, 86 });
            Assert.AreEqual(83.0, s.mean, 1e-12);
            Assert.AreEqual(6.666666666666667, s.variance, 1e-12, "sample variance (n - 1)");
            Assert.AreEqual(Math.Sqrt(6.666666666666667), s.StandardDeviation, 1e-12);
            Assert.AreEqual(4.107944335877334, s.ci95, 1e-9, "t(0.975, 3) = 3.182");
            Assert.AreEqual(83.0 - 4.107944335877334, s.Low, 1e-9);
            Assert.AreEqual(83.0 + 4.107944335877334, s.High, 1e-9);
        }

        [Test]
        public void Summarise_LeavesFailedRunsOutOfTheStatistics_ButListsThem()
        {
            var runs = new List<RunReport>
            {
                Run("A", 80f, 100f, 50f, 1),
                Run("A", 90f, 110f, 60f, 2),
                Run("A", 5f, 3f, 1f, 3, failure: "Algorithm failed"),
            };
            var group = BatchStats.Summarise(runs).Single();
            Assert.AreEqual(3, group.runs.Count);
            Assert.AreEqual(2, group.succeeded.Count);
            Assert.AreEqual(1, group.failed);
            Assert.AreEqual(2, group.coverage.count);
            Assert.AreEqual(85.0, group.coverage.mean, 1e-9);
            Assert.AreEqual(105.0, group.seconds.mean, 1e-9);
            Assert.AreEqual(55.0, group.batteryUsed.mean, 1e-9);
            Assert.AreEqual(50.0, group.coverage.variance, 1e-9);
        }

        [Test]
        public void ToCsv_HasEveryRunAndTheStatisticsForCoverageTimeAndBattery()
        {
            var runs = new List<RunReport> { Run("A", 80f, 10f, 1f, 1), Run("A", 82f, 10f, 1f, 2), Run("A", 84f, 10f, 1f, 3), Run("A", 86f, 10f, 1f, 4) };
            string csv = BatchStats.ToCsv(runs);
            var lines = csv.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToList();
            Assert.AreEqual(4, lines.Count(l => l.StartsWith("run,")));
            var summaries = lines.Where(l => l.StartsWith("summary,")).ToList();
            Assert.AreEqual(3, summaries.Count);
            var coverage = summaries.Single(l => l.Contains(",coverage_percent,")).Split(',');
            Assert.AreEqual("4", coverage[3]);
            Assert.AreEqual(83.0, double.Parse(coverage[4], System.Globalization.CultureInfo.InvariantCulture), 1e-9);
            Assert.AreEqual(6.666666666666667, double.Parse(coverage[5], System.Globalization.CultureInfo.InvariantCulture), 1e-9);
            Assert.AreEqual(3.182, double.Parse(coverage[10], System.Globalization.CultureInfo.InvariantCulture), 1e-9);
        }

        // ---------------------------------------------------------------- import

        /// <summary>A fetched cloud campaign: two conditions, a run with full files, one without, one that failed.</summary>
        string WriteCampaign()
        {
            string folder = Path.Combine(root, "cloud-results", "camp-1");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "manifest.json"), "{ \"bucket\": \"b\", \"campaignId\": \"camp-1\" }");

            var results = new CampaignImport.ResultsFile
            {
                schemaVersion = 1,
                campaignId = "camp-1",
                campaignName = "Test campaign",
                generatedUtc = "2026-11-17T22:57:53.657543+00:00",
                runs = new List<CampaignImport.Outcome>
                {
                    new CampaignImport.Outcome { runIndex = 0, floorPlan = "Two rooms", configuration = "short", algorithm = "BoustrophedonSweep",
                        algorithmName = "Boustrophedon Sweep", seed = 1, succeeded = true, runFolder = "runs/aaa", coveragePercent = 80f,
                        simulatedSeconds = 120f, batteryUsed = 100f, endReason = "TimeCap" },
                    new CampaignImport.Outcome { runIndex = 1, floorPlan = "Two rooms", configuration = "short", algorithm = "RandomBounce",
                        algorithmName = "Random", seed = 1, succeeded = true, runFolder = "runs/bbb", coveragePercent = 60f,
                        simulatedSeconds = 120f, batteryUsed = 90f, batteryCapacity = 300f, endReason = "TimeCap" },
                    new CampaignImport.Outcome { runIndex = 2, floorPlan = "Two rooms", configuration = "slam", algorithm = "BoustrophedonSweep",
                        algorithmName = "Boustrophedon Sweep", seed = 1, succeeded = false, error = "boom", endReason = "Error" },
                },
            };
            File.WriteAllText(Path.Combine(folder, CampaignImport.ResultsFileName), JsonUtility.ToJson(results, true));

            // Run 0's files, as a node wrote them; its summary is from an older build that didn't record the algorithm id.
            string run = Path.Combine(folder, "shards", "shard-0000", "runs", "aaa");
            Directory.CreateDirectory(run);
            var summary = new RunReport { levelName = "Two rooms", coveragePercent = 80f, durationSeconds = 120f, batteryUsed = 100f,
                coverageMap = "AAAA", mapWidth = 2, mapHeight = 1, dataFolder = "/work/shard-0000/runs/aaa" };
            File.WriteAllText(Path.Combine(run, "summary.json"), JsonUtility.ToJson(summary, true));
            File.WriteAllText(Path.Combine(run, "replay.json"), "{}");
            File.WriteAllText(Path.Combine(run, "external_model.json"), "{ \"large\": true }");
            return folder;
        }

        [Test]
        public void Import_AddsEveryRun_OneBatchPerCondition()
        {
            string folder = WriteCampaign();
            string history = Path.Combine(root, "history.json");
            string data = Path.Combine(root, "Runs");

            var result = CampaignImport.Import(folder, history, data);

            CollectionAssert.IsEmpty(result.problems, string.Join("\n", result.problems));
            Assert.AreEqual(3, result.imported);
            Assert.AreEqual(1, result.failed);
            Assert.AreEqual(2, result.withoutDetails, "run 1 had no files, run 2 failed before writing any");
            CollectionAssert.AreEquivalent(new[] { "camp-1/Two rooms/short", "camp-1/Two rooms/slam" }, result.batchIds);

            var runs = RunHistory.LoadAll(history);
            Assert.AreEqual(3, runs.Count);
            var withFiles = runs.Single(r => r.trial == 0);
            Assert.AreEqual(CampaignImport.RunIdFor("camp-1", 0), withFiles.id);
            Assert.AreEqual("BoustrophedonSweep", withFiles.algorithmId, "the outcome's algorithm wins over an old summary");
            Assert.AreEqual("Boustrophedon Sweep", withFiles.algorithmName);
            Assert.AreEqual("cloud", withFiles.source);
            Assert.AreEqual("Cloud", withFiles.SourceLabel);
            Assert.AreEqual("Test campaign", withFiles.campaignName);
            Assert.AreEqual("short", withFiles.configuration);
            Assert.AreEqual("AAAA", withFiles.coverageMap, "the map comes from summary.json");
            Assert.IsTrue(withFiles.headless);
            Assert.IsTrue(withFiles.IsImported);
            Assert.AreEqual(Path.Combine(data, withFiles.id), withFiles.dataFolder, "files are copied into the app's own folder");
            Assert.IsTrue(File.Exists(Path.Combine(withFiles.dataFolder, "summary.json")));
            Assert.IsTrue(File.Exists(Path.Combine(withFiles.dataFolder, "replay.json")));
            Assert.IsFalse(File.Exists(Path.Combine(withFiles.dataFolder, "external_model.json")), "the large model isn't copied");

            var numbersOnly = runs.Single(r => r.trial == 1);
            Assert.AreEqual(60f, numbersOnly.coveragePercent);
            Assert.AreEqual(300f, numbersOnly.vacuum.batteryCapacity);
            Assert.IsTrue(string.IsNullOrEmpty(numbersOnly.dataFolder), "no files to point at (saved as \"\" by JsonUtility)");

            var failed = runs.Single(r => r.trial == 2);
            Assert.AreEqual("boom", failed.failure);
            Assert.AreEqual("camp-1/Two rooms/slam", failed.batchId);

            // The short condition's batch: two algorithms, statistics as for a batch run in the app.
            var shortBatch = BatchStats.Summarise(runs.FindAll(r => r.batchId == "camp-1/Two rooms/short"));
            Assert.AreEqual(2, shortBatch.Count);
            Assert.AreEqual("BoustrophedonSweep", shortBatch[0].algorithmId, "best mean coverage first");
            var slamBatch = BatchStats.Summarise(runs.FindAll(r => r.batchId == "camp-1/Two rooms/slam")).Single();
            Assert.AreEqual(1, slamBatch.failed);
            Assert.AreEqual(0, slamBatch.coverage.count);
        }

        [Test]
        public void ImportingAgain_ReplacesInsteadOfDuplicating()
        {
            string folder = WriteCampaign();
            string history = Path.Combine(root, "history.json");
            string data = Path.Combine(root, "Runs");
            RunHistory.Add(new RunReport { id = "local-run", levelName = "Mine", finishedAt = DateTime.Now.ToString("o") }, history);

            CampaignImport.Import(folder, history, data);
            CampaignImport.Import(folder, history, data);

            var runs = RunHistory.LoadAll(history);
            Assert.AreEqual(4, runs.Count, "3 imported runs once, plus the existing local run");
            Assert.IsTrue(runs.Exists(r => r.id == "local-run"));
        }

        [Test]
        public void Find_ListsCampaigns_AndKnowsWhichAreImported()
        {
            string folder = WriteCampaign();
            Directory.CreateDirectory(Path.Combine(root, "cloud-results", "not-a-campaign"));
            string history = Path.Combine(root, "history.json");
            var roots = new[] { Path.Combine(root, "cloud-results"), Path.Combine(root, "local-results") };

            var before = CampaignImport.Find(roots, RunHistory.LoadAll(history));
            Assert.AreEqual(1, before.Count);
            Assert.AreEqual("Test campaign", before[0].name);
            Assert.AreEqual("cloud", before[0].source);
            Assert.AreEqual(3, before[0].runs);
            Assert.AreEqual(1, before[0].failed);
            Assert.IsFalse(before[0].imported);

            CampaignImport.Import(folder, history, Path.Combine(root, "Runs"));
            Assert.IsTrue(CampaignImport.Find(roots, RunHistory.LoadAll(history))[0].imported);
        }

        [Test]
        public void ALocalHeadlessCampaign_IsLabelledHeadless()
        {
            string folder = WriteCampaign();
            File.WriteAllText(Path.Combine(folder, "manifest.json"), "{ \"campaignId\": \"camp-1\" }"); // vacuumctl local: no bucket
            string history = Path.Combine(root, "history.json");
            CampaignImport.Import(folder, history, Path.Combine(root, "Runs"));
            Assert.IsTrue(RunHistory.LoadAll(history).TrueForAll(r => r.source == "headless" && r.SourceLabel == "Headless"));
        }

        [Test]
        public void AFolderWithoutResults_IsReportedNotThrown()
        {
            Directory.CreateDirectory(root);
            var result = CampaignImport.Import(root, Path.Combine(root, "history.json"), Path.Combine(root, "Runs"));
            Assert.AreEqual(0, result.imported);
            StringAssert.Contains("campaign_results.json", result.problems.Single());
        }

        [Test]
        public void ANewerResultsFormat_IsRefused()
        {
            string folder = WriteCampaign();
            string path = Path.Combine(folder, CampaignImport.ResultsFileName);
            var file = JsonUtility.FromJson<CampaignImport.ResultsFile>(File.ReadAllText(path));
            file.schemaVersion = 2;
            File.WriteAllText(path, JsonUtility.ToJson(file, true));
            var result = CampaignImport.Import(folder, Path.Combine(root, "history.json"), Path.Combine(root, "Runs"));
            Assert.AreEqual(0, result.imported);
            StringAssert.Contains("schemaVersion 2", result.problems.Single());
        }
    }
}
