using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using RobotVacuum.Headless;
using RobotVacuum.LevelEditor;
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
            ""sweep"": { ""algorithms"": [""RandomBounce"", ""spiral""], ""seedStart"": 5, ""seedCount"": 3 }"
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
            var job = HeadlessJob.Parse(Job().Replace("\"spiral\"", "\"Teleport\""), out var errors);
            Assert.IsNull(job);
            Assert.IsTrue(errors.Any(e => e.Contains("Teleport")));
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
