using System;
using System.IO;
using NUnit.Framework;
using RobotVacuum.LevelEditor;
using RobotVacuum.Sim;
using RobotVacuum.Sim.Control;

namespace RobotVacuum.Tests
{
    /// <summary>History tests use a throwaway file so they never touch the app's real run history.</summary>
    public class RunHistoryTests
    {
        string path;

        [SetUp]
        public void SetUp() => path = Path.Combine(Path.GetTempPath(), "RobotVacuumTests", Guid.NewGuid().ToString("N"), "runs.json");

        [TearDown]
        public void TearDown()
        {
            string folder = Path.GetDirectoryName(path);
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }

        static RunReport Run(string level, float coverage, float seconds, string finishedAt = null, string algorithmId = null) => new RunReport
        {
            levelName = level,
            levelPath = level + ".json",
            algorithmId = algorithmId,
            coveragePercent = coverage,
            durationSeconds = seconds,
            finishedAt = finishedAt,
            vacuum = VacuumSettings.Defaults(),
        };

        [Test]
        public void LoadAll_WithNoFile_IsEmpty()
        {
            Assert.IsEmpty(RunHistory.LoadAll(path));
        }

        [Test]
        public void Add_StampsTheRun_AndRoundTripsIt()
        {
            var report = Run("Flat", 42.5f, 90f);
            report.AddSample(0f);
            report.AddSample(10f);

            RunHistory.Add(report, path);
            var loaded = RunHistory.LoadAll(path);

            Assert.AreEqual(1, loaded.Count);
            Assert.IsFalse(string.IsNullOrEmpty(loaded[0].id));
            Assert.AreNotEqual(DateTime.MinValue, loaded[0].FinishedAt);
            Assert.AreEqual("RandomBounce", loaded[0].AlgorithmKey, "no id recorded = Random");
            Assert.AreEqual("Random", loaded[0].AlgorithmName);
            Assert.AreEqual(42.5f, loaded[0].coveragePercent, 1e-4f);
            Assert.AreEqual(new[] { 0f, 10f }, loaded[0].coverageSamples.ToArray());
            Assert.AreEqual(0.65f, loaded[0].vacuum.driveSpeed, 1e-5f);
        }

        [Test]
        public void LoadAll_IsNewestFirst()
        {
            RunHistory.Add(Run("Old", 10f, 60f, "2026-01-01T10:00:00"), path);
            RunHistory.Add(Run("New", 10f, 60f, "2026-03-01T10:00:00"), path);
            RunHistory.Add(Run("Middle", 10f, 60f, "2026-02-01T10:00:00"), path);

            var loaded = RunHistory.LoadAll(path);

            Assert.AreEqual(new[] { "New", "Middle", "Old" }, loaded.ConvertAll(run => run.levelName).ToArray());
        }

        [Test]
        public void Delete_RemovesOnlyThatRun_AndClearRemovesAll()
        {
            var keep = Run("Keep", 1f, 1f);
            var drop = Run("Drop", 1f, 1f);
            RunHistory.Add(keep, path);
            RunHistory.Add(drop, path);

            RunHistory.Delete(drop.id, path);
            var loaded = RunHistory.LoadAll(path);
            Assert.AreEqual(1, loaded.Count);
            Assert.AreEqual(keep.id, loaded[0].id);

            RunHistory.Clear(path);
            Assert.IsEmpty(RunHistory.LoadAll(path));
        }

        [Test]
        public void LoadAll_WithACorruptFile_IsEmpty()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, "{ not json");

            Assert.IsEmpty(RunHistory.LoadAll(path));
        }

        [Test]
        public void AddSample_ThinsLongRuns_AndDoublesTheInterval()
        {
            var report = new RunReport { sampleInterval = 1f };
            for (int i = 0; i < RunReport.MaxSamples; i++) report.AddSample(i);

            Assert.Less(report.coverageSamples.Count, RunReport.MaxSamples);
            Assert.AreEqual(2f, report.sampleInterval, 1e-5f);
            Assert.AreEqual(0f, report.coverageSamples[0]);
            Assert.AreEqual(2f, report.coverageSamples[1], "every other sample is kept");
        }

        [Test]
        public void DerivedFigures_FollowTheRecordedValues()
        {
            var report = Run("Flat", 50f, 120f);
            report.floorArea = 30f;
            report.blockedArea = 10f;
            report.batteryUsed = 75f;
            report.vacuum.batteryCapacity = 300f;

            Assert.AreEqual(10f, report.CleanedArea, 1e-4f);
            Assert.AreEqual(25f, report.CoveragePerMinute, 1e-4f);
            Assert.AreEqual(25f, report.BatteryUsedPercent, 1e-4f);
            Assert.AreEqual("Random", report.AlgorithmName);
        }

        [Test]
        public void CompareAlgorithms_AveragesPerAlgorithm_BestFirst()
        {
            var runs = new[]
            {
                Run("Flat", 40f, 60f),
                Run("Flat", 60f, 120f),
                Run("Flat", 80f, 60f, algorithmId: "BoustrophedonSweep"),
                Run("House", 90f, 60f, algorithmId: "Mouse"),
            };

            var all = AnalysisView.CompareAlgorithms(runs);
            Assert.AreEqual(3, all.Count);
            Assert.AreEqual("Mouse", all[0].algorithmId);

            var flat = AnalysisView.CompareAlgorithms(runs, "Flat.json", "Flat");
            Assert.AreEqual(2, flat.Count);
            Assert.AreEqual("BoustrophedonSweep", flat[0].algorithmId);
            Assert.AreEqual("RandomBounce", flat[1].algorithmId);
            Assert.AreEqual(2, flat[1].runs);
            Assert.AreEqual(50f, flat[1].averageCoverage, 1e-4f);
            Assert.AreEqual(90f, flat[1].averageSeconds, 1e-4f);
            Assert.AreEqual(100f / 3f, flat[1].coveragePerMinute, 1e-3f, "total coverage over total minutes");
        }
    }

    public class SimConfigTests
    {
        [Test]
        public void Parse_Nothing_GivesDefaults()
        {
            var data = SimConfig.Data.Parse(null);

            Assert.AreEqual(AlgorithmCatalog.RandomBounceId, data.runSettings.algorithmId);
            Assert.AreEqual(0.65f, data.vacuum.driveSpeed, 1e-5f);
            Assert.AreEqual(300f, data.vacuum.batteryCapacity, 1e-5f);
        }

        [Test]
        public void Parse_RoundTripsSavedSettings()
        {
            var saved = new SimConfig.Data();
            saved.runSettings.algorithmId = "BoustrophedonSweep";
            saved.vacuum.driveSpeed = 1.1f;
            saved.vacuum.useFeet = true;

            var data = SimConfig.Data.Parse(saved.ToJson());

            Assert.AreEqual("BoustrophedonSweep", data.runSettings.algorithmId);
            Assert.AreEqual(1.1f, data.vacuum.driveSpeed, 1e-5f);
            Assert.IsTrue(data.vacuum.useFeet);
        }

        [TestCase("{ broken")]
        [TestCase("{\"pattern\":99}")]
        [TestCase("{\"runSettings\":{\"algorithmId\":\"Legacy.Spiral\"}}")]
        public void Parse_BadInput_FallsBackToDefaults(string json)
        {
            var data = SimConfig.Data.Parse(json);

            Assert.AreEqual(AlgorithmCatalog.RandomBounceId, data.runSettings.algorithmId);
            Assert.IsNotNull(data.vacuum);
        }
    }
}
