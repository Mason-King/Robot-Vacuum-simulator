using System;
using System.IO;
using NUnit.Framework;
using RobotVacuum.LevelEditor;
using RobotVacuum.Sim;

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

        static RunReport Run(string level, MovementPattern pattern, float coverage, float seconds, string finishedAt = null) => new RunReport
        {
            levelName = level,
            levelPath = level + ".json",
            pattern = pattern,
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
            var report = Run("Flat", MovementPattern.Spiral, 42.5f, 90f);
            report.AddSample(0f);
            report.AddSample(10f);

            RunHistory.Add(report, path);
            var loaded = RunHistory.LoadAll(path);

            Assert.AreEqual(1, loaded.Count);
            Assert.IsFalse(string.IsNullOrEmpty(loaded[0].id));
            Assert.AreNotEqual(DateTime.MinValue, loaded[0].FinishedAt);
            Assert.AreEqual(MovementPattern.Spiral, loaded[0].pattern);
            Assert.AreEqual(42.5f, loaded[0].coveragePercent, 1e-4f);
            Assert.AreEqual(new[] { 0f, 10f }, loaded[0].coverageSamples.ToArray());
            Assert.AreEqual(0.65f, loaded[0].vacuum.driveSpeed, 1e-5f);
        }

        [Test]
        public void LoadAll_IsNewestFirst()
        {
            RunHistory.Add(Run("Old", MovementPattern.RandomBounce, 10f, 60f, "2026-01-01T10:00:00"), path);
            RunHistory.Add(Run("New", MovementPattern.RandomBounce, 10f, 60f, "2026-03-01T10:00:00"), path);
            RunHistory.Add(Run("Middle", MovementPattern.RandomBounce, 10f, 60f, "2026-02-01T10:00:00"), path);

            var loaded = RunHistory.LoadAll(path);

            Assert.AreEqual(new[] { "New", "Middle", "Old" }, loaded.ConvertAll(run => run.levelName).ToArray());
        }

        [Test]
        public void Delete_RemovesOnlyThatRun_AndClearRemovesAll()
        {
            var keep = Run("Keep", MovementPattern.Spiral, 1f, 1f);
            var drop = Run("Drop", MovementPattern.Spiral, 1f, 1f);
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
            var report = Run("Flat", MovementPattern.Spiral, 50f, 120f);
            report.floorArea = 30f;
            report.blockedArea = 10f;
            report.batteryUsed = 75f;
            report.vacuum.batteryCapacity = 300f;

            Assert.AreEqual(10f, report.CleanedArea, 1e-4f);
            Assert.AreEqual(25f, report.CoveragePerMinute, 1e-4f);
            Assert.AreEqual(25f, report.BatteryUsedPercent, 1e-4f);
            Assert.AreEqual("Spiral", report.PatternLabel);

            report.mixedPatterns = true;
            Assert.AreEqual("Mixed", report.PatternLabel);
        }

        [Test]
        public void ComparePatterns_AveragesPerPattern_BestFirst_AndSkipsMixedRuns()
        {
            var mixed = Run("Flat", MovementPattern.Spiral, 99f, 60f);
            mixed.mixedPatterns = true;
            var runs = new[]
            {
                Run("Flat", MovementPattern.RandomBounce, 40f, 60f),
                Run("Flat", MovementPattern.RandomBounce, 60f, 120f),
                Run("Flat", MovementPattern.Lawnmower, 80f, 60f),
                Run("House", MovementPattern.Spiral, 90f, 60f),
                mixed,
            };

            var all = AnalysisView.ComparePatterns(runs);
            Assert.AreEqual(3, all.Count);
            Assert.AreEqual(MovementPattern.Spiral, all[0].pattern);

            var flat = AnalysisView.ComparePatterns(runs, "Flat.json", "Flat");
            Assert.AreEqual(2, flat.Count);
            Assert.AreEqual(MovementPattern.Lawnmower, flat[0].pattern);
            Assert.AreEqual(MovementPattern.RandomBounce, flat[1].pattern);
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

            Assert.AreEqual(MovementPattern.RandomBounce, data.pattern);
            Assert.AreEqual(PictureKind.Heart, data.picture);
            Assert.AreEqual(0.65f, data.vacuum.driveSpeed, 1e-5f);
            Assert.AreEqual(300f, data.vacuum.batteryCapacity, 1e-5f);
        }

        [Test]
        public void Parse_RoundTripsSavedSettings()
        {
            var saved = new SimConfig.Data { pattern = MovementPattern.Lawnmower, picture = PictureKind.Smiley };
            saved.vacuum.driveSpeed = 1.1f;
            saved.vacuum.useFeet = true;

            var data = SimConfig.Data.Parse(saved.ToJson());

            Assert.AreEqual(MovementPattern.Lawnmower, data.pattern);
            Assert.AreEqual(PictureKind.Smiley, data.picture);
            Assert.AreEqual(1.1f, data.vacuum.driveSpeed, 1e-5f);
            Assert.IsTrue(data.vacuum.useFeet);
        }

        [TestCase("{ broken")]
        [TestCase("{\"pattern\":99,\"picture\":-4}")]
        public void Parse_BadInput_FallsBackToDefaults(string json)
        {
            var data = SimConfig.Data.Parse(json);

            Assert.AreEqual(MovementPattern.RandomBounce, data.pattern);
            Assert.AreEqual(PictureKind.Heart, data.picture);
            Assert.IsNotNull(data.vacuum);
        }
    }
}
