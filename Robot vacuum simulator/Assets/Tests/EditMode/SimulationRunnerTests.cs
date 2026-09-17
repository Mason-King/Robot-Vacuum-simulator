using NUnit.Framework;
using RobotVacuum.Level;
using RobotVacuum.Sim;
using UnityEngine;
using Object = UnityEngine.Object;

namespace RobotVacuum.Tests
{
    public class SimulationRunnerTests
    {
        LevelData level;
        SimulationRunner runner;

        [SetUp]
        public void SetUp() => level = TestLevels.NewLevel(SampleLevels.PopulateApartment);

        [TearDown]
        public void TearDown()
        {
            runner?.Stop();
            runner = null;
            Object.DestroyImmediate(level);
        }

        [Test]
        public void Headless_BuildsCollidersAndAVacuum_WithNothingToLookAt()
        {
            runner = SimulationRunner.Start(level, SimulationRunner.Options.Headless());

            Assert.IsNotNull(runner.Robot, "a headless run still needs the vacuum");
            Assert.IsNotNull(runner.Cleaning, "and its coverage tracking");
            Assert.AreSame(level, runner.Level.Level);
            Assert.IsNotEmpty(runner.Root.GetComponentsInChildren<BoxCollider2D>(), "walls it can bump into");

            Assert.IsEmpty(runner.Root.GetComponentsInChildren<CoverageHeatmapRenderer>(), "no heatmap without visuals");
            Assert.IsFalse(runner.Level.DrawMeshes);
            Assert.IsEmpty(runner.Level.GetComponentsInChildren<MeshRenderer>(), "no level meshes without visuals");
        }

        [Test]
        public void Visible_AddsTheMeshesAndTheHeatmap()
        {
            runner = SimulationRunner.Start(level, SimulationRunner.Options.Visible());

            Assert.IsTrue(runner.Level.DrawMeshes);
            Assert.IsNotEmpty(runner.Level.GetComponentsInChildren<MeshRenderer>());
            Assert.IsNotEmpty(runner.Root.GetComponentsInChildren<CoverageHeatmapRenderer>());
            Assert.IsNotEmpty(runner.Root.GetComponentsInChildren<BoxCollider2D>());
        }

        [Test]
        public void Start_PutsTheVacuumOnTheLevelsStartPoint_WithTheChosenAlgorithm()
        {
            runner = SimulationRunner.Start(level, SimulationRunner.Options.Headless(MovementPattern.WallFollow, PictureKind.Invader));

            Assert.AreEqual(MovementPattern.WallFollow, runner.Robot.Pattern);
            Assert.AreEqual(PictureKind.Invader, runner.Robot.Picture);
            TestLevels.AssertNear(level.RobotSpawn, runner.Robot.LevelPosition, 1e-3f);
        }

        [Test]
        public void Stop_TakesTheWholeSimulationWithIt()
        {
            var stopping = SimulationRunner.Start(level, SimulationRunner.Options.Headless());
            var root = stopping.Root;

            stopping.Stop();

            Assert.IsTrue(root == null, "the root object is gone");
        }
    }
}
