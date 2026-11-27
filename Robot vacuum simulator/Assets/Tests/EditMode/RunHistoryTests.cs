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

        // Algorithm ids with their names given, so the tests don't depend on what the catalog has installed.
        static RunReport Algo(RunReport run, string id, string name)
        {
            run.algorithmId = id;
            run.algorithmName = name;
            return run;
        }

        static RunReport Trial(string algorithm, int seed, float coverage, string batchId)
        {
            var run = Algo(Run("Flat", coverage, 60f, "2026-05-01T10:00:00"), algorithm, algorithm);
            run.id = Guid.NewGuid().ToString("N");
            run.batchId = batchId;
            run.seed = seed;
            run.coverageSamples.AddRange(new[] { 0f, coverage });
            run.sampleInterval = 60f;
            return run;
        }

        [Test]
        public void ComparingByAlgorithm_PoolsSinglesAndBatches_BestFirst_AndSkipsSwitchedRuns()
        {
            var mixed = Algo(Run("Flat", 99f, 60f), "Spiral", "Spiral");
            mixed.id = "mixed";
            mixed.algorithmSwitches.Add(new RunAlgorithmSwitch { simSeconds = 20f, fromId = "Spiral", toId = "Random" });

            var batch = BatchArchive.Build("b1", new[]
            {
                Trial("Lawnmower", 1, 80f, "b1"),
                Trial("Lawnmower", 2, 84f, "b1"),
                Trial("Random", 1, 50f, "b1"),
                Trial("Random", 2, 54f, "b1"),
            });
            var single = Algo(Run("Flat", 46f, 60f), "Random", "Random");
            single.id = "single";

            var sources = new[] { CompareSource.Of(batch), CompareSource.Of(single), CompareSource.Of(mixed) };
            var groups = RunComparison.Build(sources, CompareGrouping.Algorithm, CompareMetric.Coverage);

            Assert.AreEqual(2, groups.Count, "a run that switched algorithm part-way belongs to no one algorithm");
            Assert.AreEqual("Lawnmower", groups[0].label);
            Assert.AreEqual(82f, groups[0].metric.mean, 1e-4);
            Assert.AreEqual(3, groups[1].runs.Count, "the single run joins the batch's Random trials");
            Assert.AreEqual(50f, groups[1].metric.mean, 1e-4);

            var byPick = RunComparison.Build(sources, CompareGrouping.Selection, CompareMetric.Coverage);
            Assert.AreEqual(4, byPick.Count, "each batch algorithm and each single run is its own group");
        }

        [Test]
        public void FailedRuns_StayOutOfTheStatistics_ButCountAsRuns()
        {
            var trials = new[]
            {
                Trial("Lawnmower", 1, 80f, "b"),
                Trial("Lawnmower", 2, 90f, "b"),
                Trial("Lawnmower", 3, 5f, "b"),
            };
            trials[2].failure = "boom";

            var groups = RunComparison.Build(new[] { CompareSource.Of(BatchArchive.Build("b", trials)) },
                CompareGrouping.Algorithm, CompareMetric.Coverage);

            Assert.AreEqual(3, groups[0].runs.Count);
            Assert.AreEqual(2, groups[0].metric.count);
            Assert.AreEqual(85f, groups[0].metric.mean, 1e-4);
            Assert.AreEqual(1, groups[0].stats.failed);
        }

        [Test]
        public void Runs_GroupByAlgorithmId_SoPluginsStayApartFromBuiltIns()
        {
            var plugin = Run("Flat", 70f, 60f);
            plugin.id = "plugin";
            Algo(plugin, "my-plugin", "My plugin");
            var builtIn = Run("Flat", 50f, 60f);
            builtIn.id = "builtin";

            var groups = RunComparison.Build(new[] { CompareSource.Of(plugin), CompareSource.Of(builtIn) },
                CompareGrouping.Algorithm, CompareMetric.Coverage);

            Assert.AreEqual(2, groups.Count);
            Assert.AreEqual("My plugin", groups[0].label);
        }

        [Test]
        public void ImportedBatchIds_BecomeSafeFileNames()
        {
            Assert.AreEqual("camp_Flat_base", BatchArchive.FileNameFor("camp/Flat/base"));
        }

        [Test]
        public void LowerIsBetter_RanksSmallestFirst_AndRunsWithoutAValueGoLast()
        {
            var fast = Algo(Run("Flat", 95f, 60f), "Spiral", "Spiral");
            fast.id = "fast";
            fast.timeTo90 = 30f;
            var slow = Algo(Run("Flat", 95f, 60f), "Lawnmower", "Lawnmower");
            slow.id = "slow";
            slow.timeTo90 = 50f;
            var never = Algo(Run("Flat", 40f, 60f), "Random", "Random");
            never.id = "never";

            var groups = RunComparison.Build(new[] { CompareSource.Of(never), CompareSource.Of(slow), CompareSource.Of(fast) },
                CompareGrouping.Algorithm, CompareMetric.TimeTo90);

            Assert.AreEqual("Spiral", groups[0].label);
            Assert.AreEqual("Lawnmower", groups[1].label);
            Assert.AreEqual(0, groups[2].metric.count);
        }

        [Test]
        public void Judge_CallsALeadClear_OnlyWhenTheIntervalsDontOverlap()
        {
            RunReport[] Trials(string algorithm, params float[] coverage)
            {
                var trials = new RunReport[coverage.Length];
                for (int i = 0; i < coverage.Length; i++) trials[i] = Trial(algorithm, i, coverage[i], "b");
                return trials;
            }

            var apart = new System.Collections.Generic.List<RunReport>();
            apart.AddRange(Trials("Lawnmower", 90f, 91f, 92f, 91f));
            apart.AddRange(Trials("Random", 50f, 52f, 51f, 53f));
            var clear = RunComparison.Build(new[] { CompareSource.Of(BatchArchive.Build("b", apart)) }, CompareGrouping.Algorithm, CompareMetric.Coverage);
            Assert.IsTrue(RunComparison.Judge(clear, CompareMetric.Coverage).clear);

            var close = new System.Collections.Generic.List<RunReport>();
            close.AddRange(Trials("Lawnmower", 60f, 80f, 70f, 75f));
            close.AddRange(Trials("Random", 55f, 78f, 66f, 70f));
            var overlap = RunComparison.Build(new[] { CompareSource.Of(BatchArchive.Build("b", close)) }, CompareGrouping.Algorithm, CompareMetric.Coverage);
            Assert.IsFalse(RunComparison.Judge(overlap, CompareMetric.Coverage).clear);
        }

        [Test]
        public void BatchFile_RoundTrips_WithTrialsAndAveragedCurves()
        {
            string folder = Path.Combine(Path.GetDirectoryName(path), "Batches");
            var batch = BatchArchive.Build("b2", new[]
            {
                Trial("Lawnmower", 1, 80f, "b2"),
                Trial("Lawnmower", 2, 90f, "b2"),
            });
            BatchArchive.Save(batch, folder);

            var loaded = BatchArchive.LoadAll(folder);
            Assert.AreEqual(1, loaded.Count);
            Assert.AreEqual(2, loaded[0].trials.Count);
            Assert.AreEqual(1, loaded[0].algorithms.Count);
            Assert.AreEqual(85f, loaded[0].algorithms[0].CoverageAt(60f), 1e-3f, "the mean curve ends at the mean coverage");
            Assert.IsTrue(File.Exists(Path.Combine(folder, "b2.csv")));

            BatchArchive.Delete("b2", folder);
            Assert.IsEmpty(BatchArchive.LoadAll(folder));
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
