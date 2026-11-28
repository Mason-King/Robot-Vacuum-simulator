using System;
using System.IO;
using NUnit.Framework;
using RobotVacuum.LevelEditor;
using RobotVacuum.Sim;
using UnityEngine;

namespace RobotVacuum.Tests
{
    /// <summary>
    /// Analysis > Open in Configuration screen: <see cref="SimConfig.LoadRun"/> puts a finished run's settings and
    /// seed into the configuration. Run folders are written under a temp folder; the configuration's PlayerPrefs
    /// entry is put back after each test.
    /// </summary>
    public class SimConfigLoadRunTests
    {
        const string ConfigPrefsKey = "RobotVacuum.SimConfig";

        string folder;
        string savedConfig;
        bool hadConfig;

        [SetUp]
        public void SetUp()
        {
            folder = Path.Combine(Path.GetTempPath(), "RobotVacuumTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            VersionHistory.Root = Path.Combine(folder, "History");

            hadConfig = PlayerPrefs.HasKey(ConfigPrefsKey);
            savedConfig = PlayerPrefs.GetString(ConfigPrefsKey, null);
            PlayerPrefs.DeleteKey(ConfigPrefsKey);
            SimConfig.ResetForTests();
        }

        [TearDown]
        public void TearDown()
        {
            if (hadConfig) PlayerPrefs.SetString(ConfigPrefsKey, savedConfig);
            else PlayerPrefs.DeleteKey(ConfigPrefsKey);
            SimConfig.ResetForTests();

            VersionHistory.Root = null;
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }

        /// <summary>A run folder whose configuration.json and config.json disagree on the drive speed and SLAM.</summary>
        RunReport RunWithFiles(int seed)
        {
            string runFolder = Path.Combine(folder, "run");
            Directory.CreateDirectory(runFolder);

            var saved = new SimConfig.Data();
            saved.randomSeed = true;
            saved.seed = 4527525;
            saved.telemetryInterval = 2f;
            saved.vacuum.driveSpeed = 0.5f;
            saved.runSettings.slam = true;
            File.WriteAllText(Path.Combine(runFolder, "configuration.json"), saved.ToJson());

            var started = new RunConfig
            {
                settings = new RunSettings
                {
                    algorithmId = saved.runSettings.algorithmId,
                    seed = seed,
                    randomSeed = false,
                    driveSpeed = 0.65f,
                    slam = false,
                    cellSizeMeters = 0.0508f,
                    maxSimulatedSeconds = 1800f,
                },
            };
            started.WriteTo(Path.Combine(runFolder, RunArtifacts.ConfigFileName));

            return new RunReport { dataFolder = runFolder, seed = seed, levelName = "Manor" };
        }

        [Test]
        public void LoadRun_FixesTheRunsSeed()
        {
            Assert.IsTrue(SimConfig.LoadRun(RunWithFiles(64883917)));

            Assert.AreEqual(64883917, SimConfig.Seed);
            Assert.IsFalse(SimConfig.RandomSeed);
        }

        [Test]
        public void LoadRun_TakesTheSettingsTheRunStartedWith_OverTheSavedConfiguration()
        {
            SimConfig.LoadRun(RunWithFiles(7));

            Assert.AreEqual(0.65f, SimConfig.Vacuum.driveSpeed, 1e-4f, "config.json, not configuration.json");
            Assert.IsFalse(SimConfig.RunSettings.slam);
            Assert.AreEqual(0.0508f, SimConfig.CellSize, 1e-4f);
            Assert.IsTrue(SimConfig.RunLimitEnabled);
            Assert.AreEqual(1800f, SimConfig.RunLimitSeconds, 1e-3f);
        }

        [Test]
        public void LoadRun_KeepsTheSavedConfigurationsOtherSettings()
        {
            SimConfig.LoadRun(RunWithFiles(7));

            Assert.AreEqual(2f, SimConfig.TelemetryInterval, 1e-4f);
        }

        [Test]
        public void LoadRun_CanBeUndone()
        {
            SimConfig.Seed = 1;
            SimConfig.RandomSeed = true;

            SimConfig.LoadRun(RunWithFiles(7));
            Assert.IsTrue(SimConfig.Undo());

            Assert.AreEqual(1, SimConfig.Seed);
            Assert.IsTrue(SimConfig.RandomSeed);
        }

        [Test]
        public void LoadRun_WithoutRunFiles_UsesTheSummary()
        {
            var run = new RunReport
            {
                dataFolder = Path.Combine(folder, "missing"),
                seed = 99,
                slam = true,
                cellSize = 0.0254f,
                vacuum = new VacuumSettings { driveSpeed = 0.4f, reverseSpeed = 0.2f, turnSpeed = 200f, batteryCapacity = 1000f },
            };

            Assert.IsTrue(SimConfig.LoadRun(run));

            Assert.AreEqual(99, SimConfig.Seed);
            Assert.IsTrue(SimConfig.RunSettings.slam);
            Assert.AreEqual(0.4f, SimConfig.Vacuum.driveSpeed, 1e-4f);
            Assert.AreEqual(0.0254f, SimConfig.CellSize, 1e-4f);
        }

        [Test]
        public void LoadRun_Null_IsRefused()
        {
            Assert.IsFalse(SimConfig.LoadRun(null));
        }
    }
}
