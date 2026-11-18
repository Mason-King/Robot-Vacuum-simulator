using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using NUnit.Framework;
using RobotVacuum.Headless;
using RobotVacuum.Level;
using RobotVacuum.LevelEditor;
using RobotVacuum.Network;
using RobotVacuum.Sim.Control;
using UnityEngine;
using RunOutcome = RobotVacuum.Headless.RunOutcome; // Sim.Control has a RunOutcome too

namespace RobotVacuum.Tests
{
    /// <summary>
    /// Network batches (3.9, 4.5, 5.5 local cluster, 2.18, 3.12): the shard split, the job built from the batch dialog,
    /// carried plugin source, merged results, the import into Analysis, and whole campaigns over loopback with fake
    /// simulations, including a worker that is switched off half way.
    /// </summary>
    public class NetworkBatchTests
    {
        const string Version = "batch-test-1.0";
        const string Plan = @"{ ""name"": ""Plan A"", ""rooms"": [ { ""name"": ""Box"", ""floor"": ""Hardwood"",
            ""outline"": [ {""x"":0,""y"":0}, {""x"":4,""y"":0}, {""x"":4,""y"":3}, {""x"":0,""y"":3} ] } ], ""robotSpawn"": {""x"":1,""y"":1} }";

        string root;
        TimeSpan savedLost, savedPoll;
        readonly List<IDisposable> cleanup = new List<IDisposable>();

        [SetUp]
        public void SetUp()
        {
            root = Path.Combine(Path.GetTempPath(), "RobotVacuumBatchTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            savedLost = NetworkCampaign.LostAfter;
            savedPoll = NetworkCampaign.PollInterval;
            NetworkCampaign.LostAfter = TimeSpan.FromSeconds(2);
            NetworkCampaign.PollInterval = TimeSpan.FromMilliseconds(200);
            WorkerRegistry.UseForTests(Path.Combine(root, "client"), Version, new MemorySecretStore());
        }

        [TearDown]
        public void TearDown()
        {
            NetworkCampaign.Current?.Cancel();
            foreach (var item in cleanup) { try { item.Dispose(); } catch (Exception) { } }
            cleanup.Clear();
            NetworkCampaign.LostAfter = savedLost;
            NetworkCampaign.PollInterval = savedPoll;
            WorkerRegistry.Initialize(); // back to the app's own folder
            try { Directory.Delete(root, true); }
            catch (Exception) { }
        }

        static LevelSnapshot Level() => LevelSnapshot.Parse(Plan);

        static BatchSpec Spec(int trials = 3, params string[] algorithms) => new BatchSpec
        {
            algorithmIds = (algorithms.Length > 0 ? algorithms : new[] { AlgorithmCatalog.RandomBounceId, "BoustrophedonSweep" }).ToList(),
            trials = trials,
            baseSeed = 5,
            maxMinutes = 10,
        };

        // ---------------------------------------------------------------- the split

        [Test]
        public void ShardPlan_IsProportionalToSlots_AndNeverMoreShardsThanRuns()
        {
            var split = ShardPlan.Assign(new[] { 4, 2 }, 100, out int count);
            Assert.AreEqual(6, count);
            Assert.AreEqual(4, split[0].Count);
            Assert.AreEqual(2, split[1].Count);
            CollectionAssert.AreEquivalent(Enumerable.Range(0, 6), split.SelectMany(s => s));

            ShardPlan.Assign(new[] { 8 }, 3, out count);
            Assert.AreEqual(3, count, "no empty shards");

            split = ShardPlan.Assign(new[] { 3, 3, 1 }, 1000, out count);
            CollectionAssert.AreEqual(new[] { 3, 3, 1 }, split.Select(s => s.Count));

            split = ShardPlan.Assign(new[] { 0, 2 }, 10, out count);
            Assert.IsEmpty(split[0]);
            Assert.AreEqual(2, split[1].Count);
        }

        [Test]
        public void ShardPlan_RunCounts_MatchTheHeadlessSplit()
        {
            var job = new HeadlessJob();
            for (int i = 0; i < 23; i++) job.runs.Add(new RunSpec { runIndex = i });
            for (int shard = 0; shard < 5; shard++)
                Assert.AreEqual(job.RunsForShard(shard, 5).Count, ShardPlan.RunsIn(shard, 5, 23), $"shard {shard}");
        }

        // ---------------------------------------------------------------- the job

        [Test]
        public void TheJob_UsesTheSameSeedsAndSettingsAsTheInAppBatch()
        {
            var job = NetworkJobBuilder.FromBatch(Level(), Spec(), null, out string problem);
            Assert.IsNotNull(job, problem);
            Assert.IsEmpty(job.Validate(), "valid as a worker would check it");

            Assert.AreEqual(6, job.runs.Count, "2 algorithms x 3 seeds");
            CollectionAssert.AreEqual(new[] { 5, 6, 7, 5, 6, 7 }, job.runs.Select(r => r.seed), "trial n uses baseSeed + n, as in the app");
            var config = job.configurations.Single();
            Assert.AreEqual(SimConfig.Vacuum.driveSpeed, config.vacuum.driveSpeed, 1e-6f);
            Assert.AreEqual(SimConfig.Vacuum.batteryCapacity, config.battery.capacity, 1e-6f);
            Assert.AreEqual(SimConfig.CellSize, config.simulation.cellSizeMeters, 1e-6f);
            Assert.AreEqual(SimConfig.TelemetryInterval, config.simulation.telemetryInterval, 1e-6f);
            Assert.AreEqual(600f, config.simulation.maxSimulatedSeconds, 1e-3f, "the batch's time limit");
            Assert.AreEqual(SimConfig.RunSettings.slam, config.sensing.slam);
            Assert.IsFalse(job.execution.saveExternalModel, "the 2-20 MB per-cell model stays out of the transfer");
            Assert.IsEmpty(job.algorithmSources, "built-in algorithms need no source");
            StringAssert.StartsWith("net-", job.campaignId);
        }

        [Test]
        public void APluginAlgorithm_TravelsAsSource()
        {
            string folder = Path.Combine(root, "Algorithms");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "Carry.cs"), @"using RobotVacuum.AlgorithmApi;
namespace CarryTest
{
    public sealed class Carry : ICleaningAlgorithm
    {
        public string Name => ""Carry (network test)"";
        public bool SupportsSlam => true;
        public void Initialize(AlgorithmContext context) { }
        public Vec2? NextGoal(IBeliefMap map, Pose2 pose, GoalStatus lastStatus) { return null; }
    }
}");
            try
            {
                AlgorithmCatalog.RescanFolder(folder);
                Assume.That(AlgorithmCatalog.Find("CarryTest.Carry"), Is.Not.Null, AlgorithmCatalog.LastReport?.Text());
                var job = NetworkJobBuilder.FromBatch(Level(), Spec(2, "CarryTest.Carry"), null, out string problem);
                Assert.IsNotNull(job, problem);
                var source = job.algorithmSources.Single();
                Assert.AreEqual("Carry.cs", source.path);
                StringAssert.Contains("class Carry", source.source);
            }
            finally
            {
                AlgorithmCatalog.Rescan();
            }
        }

        // ---------------------------------------------------------------- results and import

        static void WriteShard(string campaign, int shard, string campaignId, params (int index, bool ok)[] runs)
        {
            string folder = Path.Combine(campaign, "shards", $"shard-{shard:D4}");
            var results = new ShardResults { campaignId = campaignId, shardIndex = shard, status = "completed" };
            foreach (var (index, ok) in runs)
            {
                string guid = Guid.NewGuid().ToString("N");
                string run = Path.Combine(folder, "runs", guid);
                Directory.CreateDirectory(run);
                File.WriteAllText(Path.Combine(run, "result.json"), "{}");
                File.WriteAllText(Path.Combine(run, "summary.json"), JsonUtility.ToJson(new RunReport { coveragePercent = 40 + index, levelName = "Plan A" }));
                results.runs.Add(new RunOutcome
                {
                    runIndex = index, floorPlan = "Plan A", configuration = "default", algorithm = AlgorithmCatalog.RandomBounceId,
                    seed = index, succeeded = ok, endReason = ok ? "BatteryEmpty" : "Error", coveragePercent = 40 + index, runFolder = "runs/" + guid,
                });
            }
            results.WriteTo(folder);
        }

        [Test]
        public void Results_FromEveryShard_AreMerged_AndARerunCountsOnce()
        {
            string campaign = Path.Combine(root, "c");
            WriteShard(campaign, 0, "c1", (0, true), (2, false));
            WriteShard(campaign, 1, "c1", (1, true), (2, true)); // run 2 failed on one computer, succeeded after moving
            WriteShard(campaign, 2, "other", (3, true));          // not this campaign

            var file = NetworkResultsFile.FromShards(campaign, "c1", "Campaign one", 4);
            CollectionAssert.AreEqual(new[] { 0, 1, 2 }, file.runs.Select(r => r.runIndex));
            Assert.IsTrue(file.runs.Single(r => r.runIndex == 2).succeeded, "the successful attempt wins");
            Assert.AreEqual(3, file.returnedRuns);
            Assert.AreEqual(0, file.failedRuns);
            Assert.AreEqual("network", file.transport);
        }

        [Test]
        public void ANetworkCampaign_ImportsIntoAnalysis_LabelledNetwork()
        {
            string campaign = Path.Combine(root, "net-1");
            WriteShard(campaign, 0, "net-1", (0, true), (1, true));
            File.WriteAllText(Path.Combine(campaign, "manifest.json"), "{ \"transport\": \"network\", \"campaignId\": \"net-1\" }");
            File.WriteAllText(Path.Combine(campaign, CampaignImport.ResultsFileName), JsonUtility.ToJson(NetworkResultsFile.FromShards(campaign, "net-1", "LAN batch", 2)));

            string history = Path.Combine(root, "history.json");
            var result = CampaignImport.Import(campaign, history, Path.Combine(root, "Runs"));
            Assert.AreEqual(2, result.imported, string.Join("\n", result.problems));
            var runs = RunHistory.LoadAll(history);
            Assert.IsTrue(runs.TrueForAll(r => r.source == "network" && r.SourceLabel == "Network" && r.campaignName == "LAN batch"));
            Assert.AreEqual(41f, runs.Single(r => r.trial == 1).coveragePercent, 1e-4f, "details come from the run's summary.json");
            Assert.AreEqual("Network", CampaignImport.Find(new[] { root }, runs).Single().SourceLabel);
        }

        [Test]
        public void Downloads_CannotEscapeTheCampaignFolder()
        {
            string folder = Path.Combine(root, "shard");
            Assert.IsTrue(WorkerJobs.TryResolveInsideOrNew(folder, "runs/abc/result.json", out string inside));
            StringAssert.StartsWith(Path.GetFullPath(folder), inside);
            foreach (string bad in new[] { "../x", "runs/../../x", "/tmp/x", "C:\\x", "runs//x", "" })
                Assert.IsFalse(WorkerJobs.TryResolveInsideOrNew(folder, bad, out _), bad);
        }

        // ---------------------------------------------------------------- whole campaigns over loopback

        sealed class DoneProcess : IShardProcess
        {
            public bool HasExited { get; set; } = true;
            public int ExitCode { get; set; }
            public void Kill() { HasExited = true; ExitCode = 1; }
        }

        /// <summary>A pretend simulator: validates instantly, then either writes a shard's runs and exits, or hangs.</summary>
        sealed class FakeSimulator : IShardLauncher
        {
            public bool hang;
            public readonly List<int> shardsStarted = new List<int>();

            public IShardProcess Start(ShardLaunch launch, out string problem)
            {
                problem = null;
                if (launch.validateOnly) return new DoneProcess();
                lock (shardsStarted) shardsStarted.Add(launch.shardIndex);
                if (hang) return new DoneProcess { HasExited = false };

                var job = JsonUtility.FromJson<HeadlessJob>(File.ReadAllText(launch.jobPath));
                var results = new ShardResults
                {
                    campaignId = job.campaignId, campaignName = job.campaignName, shardIndex = launch.shardIndex, shardCount = launch.shardCount, status = "completed",
                };
                foreach (var run in job.runs.Where(r => r.runIndex % launch.shardCount == launch.shardIndex))
                {
                    string guid = Guid.NewGuid().ToString("N");
                    string folder = Path.Combine(launch.outputFolder, "runs", guid);
                    Directory.CreateDirectory(folder);
                    File.WriteAllText(Path.Combine(folder, "summary.json"), JsonUtility.ToJson(new RunReport { coveragePercent = 60, levelName = "Plan A" }));
                    File.WriteAllText(Path.Combine(folder, "result.json"), "{}");
                    results.runs.Add(new RunOutcome
                    {
                        runIndex = run.runIndex, floorPlan = "Plan A", configuration = "default", algorithm = run.algorithm, seed = run.seed,
                        succeeded = true, endReason = "BatteryEmpty", coveragePercent = 60, runFolder = "runs/" + guid,
                    });
                }
                results.totalRuns = results.completedRuns = results.runs.Count;
                results.WriteTo(launch.outputFolder);
                Directory.CreateDirectory(Path.Combine(launch.outputFolder, "logs"));
                File.WriteAllText(Path.Combine(launch.outputFolder, "logs", "unity.log"), $"[headless] shard {launch.shardIndex} done\n");
                return new DoneProcess();
            }
        }

        (WorkerServer server, WorkerJobs jobs, FakeSimulator simulator, PairedWorker paired) StartWorker(string name, int processes, bool hang = false)
        {
            string folder = Path.Combine(root, name);
            var simulator = new FakeSimulator { hang = hang };
            var jobs = new WorkerJobs(Path.Combine(folder, "clients"), simulator, processes, null);
            var server = new WorkerServer(new WorkerServerOptions
            {
                port = 0, bindAddress = IPAddress.Loopback, dataFolder = folder, workerName = name, appVersion = Version,
                jobSchemaVersion = 1, secrets = new MemorySecretStore(), jobs = jobs,
            });
            server.Start();
            cleanup.Add(server);
            cleanup.Add(jobs);
            var paired = WorkerRegistry.Pair($"127.0.0.1:{server.Port}", server.OpenPairing(TimeSpan.FromMinutes(5)));
            return (server, jobs, simulator, paired);
        }

        NetworkCampaign Start(HeadlessJob job, params PairedWorker[] workers) =>
            NetworkCampaign.StartInternal(job, workers.Select(w => new CampaignTarget { worker = w }).ToList(),
                Path.Combine(root, "campaigns"), null, 1, withRunner: false, Path.Combine(root, "history.json"), Path.Combine(root, "Runs"));

        static void WaitFor(NetworkCampaign campaign, int seconds = 60)
        {
            var until = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < until && campaign.Phase != NetworkCampaign.PhaseImporting && !campaign.IsOver) Thread.Sleep(100);
            Assert.AreEqual(NetworkCampaign.PhaseImporting, campaign.Phase, campaign.Problem ?? File.ReadAllText(Path.Combine(campaign.Folder, "network.log")));
            campaign.MainThreadStep();
            Assert.AreEqual(NetworkCampaign.PhaseFinished, campaign.Phase, campaign.Problem);
        }

        [Test, Timeout(120000)]
        public void ABatch_SplitAcrossTwoWorkers_ComesBackIntoAnalysis()
        {
            var a = StartWorker("worker-a", 2);
            var b = StartWorker("worker-b", 1);
            var job = NetworkJobBuilder.FromBatch(Level(), Spec(), null, out string problem);
            Assert.IsNotNull(job, problem);

            var campaign = Start(job, a.paired, b.paired);
            WaitFor(campaign);

            Assert.AreEqual(6, campaign.Imported.imported);
            CollectionAssert.AreEquivalent(new[] { 0, 1 }, a.simulator.shardsStarted, "2 slots, 2 shards");
            CollectionAssert.AreEquivalent(new[] { 2 }, b.simulator.shardsStarted);
            var runs = RunHistory.LoadAll(Path.Combine(root, "history.json"));
            Assert.AreEqual(6, runs.Count);
            Assert.IsTrue(runs.TrueForAll(r => r.SourceLabel == "Network"));

            // Everything about the batch in one place (3.12): the client's log, every shard's log, the job, the results.
            foreach (string file in new[] { "network.log", "job.json", "manifest.json", CampaignImport.ResultsFileName })
                Assert.IsTrue(File.Exists(Path.Combine(campaign.Folder, file)), file);
            for (int shard = 0; shard < 3; shard++)
                Assert.IsTrue(File.Exists(Path.Combine(campaign.Folder, "shards", $"shard-{shard:D4}", "logs", "unity.log")), $"shard {shard} log");

            Assert.IsEmpty(a.jobs.Summary(), "the workers delete the batch once it is collected");
            Assert.IsEmpty(b.jobs.Summary());
        }

        [Test, Timeout(120000)]
        public void AWorkerSwitchedOffHalfWay_HasItsShardsMovedToAnother()
        {
            var a = StartWorker("worker-a", 1);
            var b = StartWorker("worker-b", 1, hang: true);
            var job = NetworkJobBuilder.FromBatch(Level(), Spec(), null, out string problem);
            Assert.IsNotNull(job, problem);

            var campaign = Start(job, a.paired, b.paired);
            var until = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < until && b.simulator.shardsStarted.Count == 0) Thread.Sleep(100);
            Assume.That(b.simulator.shardsStarted, Is.Not.Empty, "worker B started its shard");

            b.server.Dispose(); // switched off
            b.jobs.Dispose();

            WaitFor(campaign);
            Assert.AreEqual(6, campaign.Imported.imported, "every run came back");
            CollectionAssert.AreEquivalent(new[] { 0, 1 }, a.simulator.shardsStarted, "A ran its own shard and B's");
            StringAssert.Contains("moved from worker-b to worker-a", File.ReadAllText(Path.Combine(campaign.Folder, "network.log")));
        }
    }
}
