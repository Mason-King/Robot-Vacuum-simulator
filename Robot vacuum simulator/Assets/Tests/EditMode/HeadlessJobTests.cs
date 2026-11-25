using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using RobotVacuum.Headless;
using RobotVacuum.LevelEditor;
using RobotVacuum.Sim.Control;
using UnityEngine;

namespace RobotVacuum.Tests
{
    /// <summary>The headless job file: parsing, defaults, validation and sharding (2.17, 4.5, 4.8, 5.5).</summary>
    public class HeadlessJobTests
    {
        const string Room = @"{ ""name"": ""Box"", ""floor"": ""Hardwood"",
            ""outline"": [ {""x"":0,""y"":0}, {""x"":4,""y"":0}, {""x"":4,""y"":3}, {""x"":0,""y"":3} ] }";

        static string Job(string extra = "", string room = Room) => @"{
            ""schemaVersion"": 1,
            ""campaignName"": ""test"",
            ""floorPlans"": [ { ""name"": ""Plan A"", ""rooms"": [ " + room + @" ], ""robotSpawn"": {""x"":1,""y"":1} } ],
            ""sweep"": { ""algorithms"": [""RandomBounce"", ""Mouse""], ""seedStart"": 5, ""seedCount"": 3 }"
            + extra + "}";

        [Test]
        public void MinimalJobParsesAndExpandsTheSweep()
        {
            var job = HeadlessJob.Parse(Job(), out var errors);
            Assert.IsNotNull(job, string.Join("\n", errors));
            Assert.AreEqual(6, job.runs.Count);
            Assert.AreEqual(1, job.configurations.Count, "a default configuration is supplied");
            CollectionAssert.AreEqual(new[] { 5, 6, 7, 5, 6, 7 }, job.runs.Select(r => r.seed).ToArray());
            CollectionAssert.AreEqual(Enumerable.Range(0, 6).ToArray(), job.runs.Select(r => r.runIndex).ToArray());
            Assert.IsFalse(string.IsNullOrEmpty(job.campaignId));
        }

        [Test]
        public void DefaultsMatchTheGui()
        {
            var job = HeadlessJob.Parse(Job(), out _);
            var config = job.configurations[0];
            var gui = VacuumSettings.Defaults();
            Assert.AreEqual(gui.driveSpeed, config.vacuum.driveSpeed);
            Assert.AreEqual(gui.reverseSpeed, config.vacuum.reverseSpeed);
            Assert.AreEqual(gui.turnSpeed, config.vacuum.turnSpeed);
            Assert.AreEqual(gui.batteryCapacity, config.battery.capacity);

            var go = new GameObject("battery");
            try
            {
                var battery = go.AddComponent<Battery>();
                Assert.AreEqual(battery.DistanceEnergyPerMeter, config.battery.distanceEnergyPerMeter);
                Assert.AreEqual(battery.TurnEnergyPerDegree, config.battery.turnEnergyPerDegree);
                Assert.AreEqual(battery.OperatingEnergyPerSecond, config.battery.operatingEnergyPerSecond);
                Assert.AreEqual(battery.StalledEnergyPerSecond, config.battery.stalledEnergyPerSecond);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void UnknownAlgorithmIsRejected()
        {
            var job = HeadlessJob.Parse(Job().Replace("\"Mouse\"", "\"Teleport\""), out var errors);
            Assert.IsNull(job);
            Assert.IsTrue(errors.Any(e => e.Contains("Teleport")));
        }

        // A tiny plugin algorithm, carried in the job as source. JSON-escaped (quotes and newlines) by Carried().
        const string PluginSource = @"using RobotVacuum.AlgorithmApi;
namespace HeadlessTests
{
    public sealed class StandStill : ICleaningAlgorithm
    {
        public string Name => ""Stand still (headless test)"";
        public bool SupportsSlam => true;
        public void Initialize(AlgorithmContext context) { }
        public Vec2? NextGoal(IBeliefMap map, Pose2 believedPose, GoalStatus lastStatus) => null;
    }
}";

        static string Carried(string path, string source) =>
            @", ""algorithmSources"": [ { ""path"": """ + path + @""", ""source"": """ +
            source.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "").Replace("\n", "\\n") + @""" } ]";

        [TearDown]
        public void RestoreAlgorithmCatalog() => AlgorithmCatalog.Rescan(); // a carried-source test points it at a temporary folder

        [Test]
        public void AlgorithmNamesAreRecordedAsCatalogIds()
        {
            var job = HeadlessJob.Parse(Job().Replace("\"Mouse\"", "\"boustrophedon sweep\", \"sheep\""), out var errors);
            Assert.IsNotNull(job, string.Join("\n", errors));
            CollectionAssert.AreEquivalent(new[] { "RandomBounce", "BoustrophedonSweep", "Sheep" },
                job.runs.Select(r => r.algorithm).Distinct().ToArray());
        }

        [Test]
        public void RemovedPatternNames_AreUnknownAlgorithms()
        {
            foreach (string name in new[] { "spiral", "Wall Follow", "Legacy.Lawnmower" })
            {
                var job = HeadlessJob.Parse(Job().Replace("\"Mouse\"", "\"" + name + "\""), out var errors);
                Assert.IsNull(job, name);
                Assert.IsTrue(errors.Any(e => e.Contains("unknown algorithm \"" + name + "\"")), string.Join("\n", errors));
            }
        }

        [Test]
        public void SlamRunsAreAccepted_ForEveryBuiltIn()
        {
            string configs = @", ""configurations"": [ { ""name"": ""slam"", ""sensing"": { ""slam"": true, ""noiseScale"": 2 } } ]";
            var job = HeadlessJob.Parse(Job(configs), out var errors);
            Assert.IsNotNull(job, string.Join("\n", errors));
        }

        [Test]
        public void SensingDefaultsMatchRunSettings()
        {
            var job = HeadlessJob.Parse(Job(), out _);
            var sensing = job.configurations[0].sensing;
            var gui = new RobotVacuum.Sim.RunSettings();
            Assert.AreEqual(gui.slam, sensing.slam);
            Assert.AreEqual(gui.noiseScale, sensing.noiseScale);
            Assert.AreEqual(gui.lidarEnabled, sensing.lidarEnabled);
            Assert.AreEqual(gui.lidarHalfAngleDegrees, sensing.lidarHalfAngleDegrees);
            Assert.AreEqual(gui.lidarRayCount, sensing.lidarRayCount);
            Assert.AreEqual(gui.floorSensorEnabled, sensing.floorSensorEnabled);
            Assert.AreEqual(gui.slamLoopClosure, sensing.slamLoopClosure);
            Assert.IsTrue(sensing.TryCorrectionQuality(out var quality));
            Assert.AreEqual(gui.slamCorrectionQuality, quality);
        }

        [Test]
        public void CarriedAlgorithmSourceIsCompiledAndUsable()
        {
            string json = Job(Carried("Headless/StandStill.cs", PluginSource)).Replace("\"Mouse\"", "\"HeadlessTests.StandStill\"");
            var job = HeadlessJob.Parse(json, out var errors);
            Assert.IsNotNull(job, string.Join("\n", errors));
            Assert.IsTrue(job.runs.Any(r => r.algorithm == "HeadlessTests.StandStill"));
            Assert.IsNotNull(AlgorithmCatalog.Find("HeadlessTests.StandStill"));
        }

        [Test]
        public void CarriedAlgorithmThatDoesNotCompileIsRejected()
        {
            var job = HeadlessJob.Parse(Job(Carried("Broken.cs", PluginSource.Replace("=> null;", "=> 42;"))), out var errors);
            Assert.IsNull(job);
            Assert.IsTrue(errors.Any(e => e.StartsWith("algorithmSources:") && e.Contains("Broken.cs")), string.Join("\n", errors));
        }

        [Test]
        public void CarriedAlgorithmPathMustStayInsideTheFolder()
        {
            var job = HeadlessJob.Parse(Job(Carried("../escape.cs", PluginSource)), out var errors);
            Assert.IsNull(job);
            Assert.IsTrue(errors.Any(e => e.Contains("relative path")), string.Join("\n", errors));
        }

        [Test]
        public void OutOfRangeSettingsAreRejected()
        {
            string configs = @", ""configurations"": [ { ""name"": ""bad"",
                ""simulation"": { ""cellSizeMeters"": 5 }, ""battery"": { ""capacity"": 999999 } } ]";
            var job = HeadlessJob.Parse(Job(configs), out var errors);
            Assert.IsNull(job);
            Assert.IsTrue(errors.Any(e => e.Contains("cellSizeMeters")));
            Assert.IsTrue(errors.Any(e => e.Contains("battery.capacity")));
        }

        [Test]
        public void UnknownFloorCoveringIsRejected()
        {
            var job = HeadlessJob.Parse(Job(room: Room.Replace("Hardwood", "Lava")), out var errors);
            Assert.IsNull(job);
            Assert.IsTrue(errors.Any(e => e.Contains("Lava")));
        }

        [Test]
        public void CustomFloorTypesReplaceTheStockPalette()
        {
            string floors = @", ""floorTypes"": [ { ""name"": ""Lava"", ""cleaningEffort"": 3 } ]";
            var job = HeadlessJob.Parse(Job(floors, Room.Replace("Hardwood", "lava")), out var errors);
            Assert.IsNotNull(job, string.Join("\n", errors));

            var palette = job.BuildPalette();
            try
            {
                Assert.AreEqual(1, palette.Count);
                Assert.AreEqual(3f, palette.Get(0).cleaningEffort);
            }
            finally
            {
                HeadlessJob.DestroyPalette(palette);
            }
        }

        [Test]
        public void ShardsCoverEveryRunExactlyOnce()
        {
            var job = HeadlessJob.Parse(Job(), out _);
            var seen = new List<int>();
            for (int shard = 0; shard < 4; shard++)
                seen.AddRange(job.RunsForShard(shard, 4).Select(r => r.runIndex));
            seen.Sort();
            CollectionAssert.AreEqual(job.runs.Select(r => r.runIndex).OrderBy(i => i).ToArray(), seen.ToArray());
        }

        [Test]
        public void CommandLineIsParsed()
        {
            var args = HeadlessArgs.Parse(new[] { "app", "-batchmode", "-nographics", "-configPath", "job.json",
                "-outputPath", "out", "-shardIndex", "2", "-shardCount", "5", "-noResume" });
            Assert.AreEqual("job.json", args.configPath);
            Assert.AreEqual("out", args.outputPath);
            Assert.AreEqual(2, args.shardIndex);
            Assert.AreEqual(5, args.shardCount);
            Assert.IsFalse(args.resume);
            Assert.IsNull(HeadlessArgs.Parse(new[] { "app" }).configPath, "no -configPath means a GUI launch");
        }
    }
}
