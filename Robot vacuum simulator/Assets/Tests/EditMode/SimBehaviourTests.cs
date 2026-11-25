using System;
using NUnit.Framework;
using RobotVacuum.Level;
using RobotVacuum.Sim;
using RobotVacuum.Sim.Control;
using RobotVacuumSim.EnvironmentModel;
using UnityEngine;
using Object = UnityEngine.Object;

namespace RobotVacuum.Tests
{
    public class SimulationSpeedTests
    {
        /// <summary>
        /// Unity applies Time.maximumDeltaTime to a frame's REAL time and then multiplies by the time scale, so the cap
        /// stays at the base real-time value while the simulated catch-up per frame (cap × speed) expands with speed.
        /// (Edit mode: no governor runs, so the cap is the base.)
        /// </summary>
        [TestCase(2f)]
        [TestCase(100f)]
        public void Apply_PreservesFixedTimestepAndExpandsFrameCatchupLimit(float speed)
        {
            float originalTimeScale = Time.timeScale;
            float originalMaximumDeltaTime = Time.maximumDeltaTime;
            float originalFixedDeltaTime = Time.fixedDeltaTime;
            const float baseMaximumDeltaTime = 0.33333334f;

            try
            {
                SimulationSpeed.Apply(speed, baseMaximumDeltaTime);

                Assert.AreEqual(speed, Time.timeScale);
                Assert.AreEqual(baseMaximumDeltaTime, Time.maximumDeltaTime, 1e-5f, "the cap is real time");
                Assert.AreEqual(baseMaximumDeltaTime * speed, Time.maximumDeltaTime * Time.timeScale, 1e-3f,
                    "simulated catch-up per frame expands with speed");
                Assert.AreEqual(originalFixedDeltaTime, Time.fixedDeltaTime);
            }
            finally
            {
                Time.timeScale = originalTimeScale;
                Time.maximumDeltaTime = originalMaximumDeltaTime;
            }
        }

        [Test]
        public void Clamp_LimitsPlaybackSpeedToOneHundredTimes()
        {
            Assert.AreEqual(SimulationSpeed.MaxPlaybackSpeed, SimulationSpeed.ClampPlaybackSpeed(101f));
        }
    }

    public class RobotAlgorithmTests
    {
        GameObject host;
        VacuumRobot robot;

        [SetUp]
        public void SetUp()
        {
            host = new GameObject("Algorithm test vacuum");
            robot = host.AddComponent<VacuumRobot>();
        }

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(host);

        [Test]
        public void AtLeastFourBuiltInAlgorithms_RandomIsAGoalPicker()
        {
            Assert.GreaterOrEqual(AlgorithmCatalog.BuiltIns.Count, 4, "at least four algorithms (1.6.1)");
            Assert.IsInstanceOf<RandomBounceAlgorithm>(AlgorithmCatalog.Find(AlgorithmCatalog.RandomBounceId).Create());
        }

        [Test]
        public void Robot_AlgorithmIsChosenById_AndEmptyMeansRandom()
        {
            Assert.AreEqual(AlgorithmCatalog.RandomBounceId, robot.AlgorithmId, "a new robot runs Random");

            string other = AlgorithmCatalog.BuiltIns[1].Id;
            robot.AlgorithmId = other;
            Assert.AreEqual(other, robot.AlgorithmId);

            robot.AlgorithmId = "";
            Assert.AreEqual(AlgorithmCatalog.RandomBounceId, robot.AlgorithmId);
        }
    }

    public class CleaningEfficiencyTests
    {
        [Test]
        public void CleaningRate_IsDividedByTheFloorsEffort()
        {
            var carpet = ScriptableObject.CreateInstance<FloorType>();
            var hardwood = ScriptableObject.CreateInstance<FloorType>();
            try
            {
                carpet.cleaningEffort = 2f;
                hardwood.cleaningEffort = 0.8f;

                Assert.AreEqual(0.25f, VacuumCleaningController.CleaningRate(0.5f, carpet), 1e-5f);
                Assert.AreEqual(0.625f, VacuumCleaningController.CleaningRate(0.5f, hardwood), 1e-5f);
                Assert.Greater(VacuumCleaningController.CleaningRate(0.5f, hardwood), VacuumCleaningController.CleaningRate(0.5f, carpet));
            }
            finally
            {
                Object.DestroyImmediate(carpet);
                Object.DestroyImmediate(hardwood);
            }
        }

        [Test]
        public void CleaningRate_OffTheFloorOrWithoutEffort_IsTheBaseRate()
        {
            var unset = ScriptableObject.CreateInstance<FloorType>();
            try
            {
                unset.cleaningEffort = 0f;

                Assert.AreEqual(0.5f, VacuumCleaningController.CleaningRate(0.5f, null), 1e-5f);
                Assert.AreEqual(0.5f, VacuumCleaningController.CleaningRate(0.5f, unset), 1e-5f);
            }
            finally
            {
                Object.DestroyImmediate(unset);
            }
        }
    }

    public class BlockedAreaTests
    {
        LevelData level;

        [SetUp]
        public void SetUp() => level = TestLevels.NewLevel(SampleLevels.PopulateBlank); // 4 × 3 m room at the origin

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(level);

        [Test]
        public void BlockedFloorArea_CountsBlockingFurnitureOnly()
        {
            level.Obstacles.Add(new Obstacle { center = Vector2.zero, size = Vector2.one, blocksVacuum = true });
            level.Obstacles.Add(new Obstacle { center = new Vector2(-1.2f, 0.8f), size = Vector2.one * 0.5f, blocksVacuum = false });

            Assert.AreEqual(1f, level.BlockedFloorArea(), 0.02f);
        }

        [Test]
        public void BlockedFloorArea_CountsOverlapsOnce()
        {
            level.Obstacles.Add(new Obstacle { center = Vector2.zero, size = Vector2.one });
            level.Obstacles.Add(new Obstacle { center = new Vector2(0.5f, 0f), size = Vector2.one });

            Assert.AreEqual(1.5f, level.BlockedFloorArea(), 0.03f);
        }

        [Test]
        public void BlockedFloorArea_IgnoresFurnitureOutsideTheRooms()
        {
            level.Obstacles.Add(new Obstacle { center = new Vector2(2f, 0f), size = Vector2.one }); // straddles the right wall

            Assert.AreEqual(0.5f, level.BlockedFloorArea(), 0.03f);
        }

        [Test]
        public void ModelGrid_ReportsTheAreaItExcludes()
        {
            level.Obstacles.Add(new Obstacle { center = new Vector2(-1f, 0f), size = Vector2.one, blocksVacuum = true });

            var grid = new ExternalModelGrid(level, null, 0.1f);
            grid.PopulateFromLevelObject();

            Assert.AreEqual(1f, grid.NonCleanableAreaSquareMeters, 0.12f);
            Assert.AreEqual(grid.NonCleanableCellCount * 0.1f * 0.1f, grid.NonCleanableAreaSquareMeters, 1e-4f);
        }

        [Test]
        public void ModelGrid_SetNonCleanable_KeepsTheCountInStep()
        {
            var grid = new ExternalModelGrid(level, null, 0.5f);

            grid.SetNonCleanable(0, 0, true);
            grid.SetNonCleanable(0, 0, true);
            Assert.AreEqual(1, grid.NonCleanableCellCount);

            grid.SetNonCleanable(0, 0, false);
            Assert.AreEqual(0, grid.NonCleanableCellCount);
        }
    }

    public class ObstacleResizeTests
    {
        [Test]
        public void ResizeFromCorner_KeepsTheOppositeCornerFixed()
        {
            // 2 × 1 at the origin; drag the top-right corner out to (2, 1.5).
            Obstacle.ResizeFromCorner(Vector2.zero, new Vector2(2f, 1f), 0f, 2, new Vector2(2f, 1.5f), out var center, out var size);

            TestLevels.AssertNear(new Vector2(3f, 2f), size);
            TestLevels.AssertNear(new Vector2(0.5f, 0.5f), center);
        }

        [Test]
        public void ResizeFromCorner_WorksInTheObstaclesRotatedFrame()
        {
            // Rotated 90°, corner 2 sits at (-0.5, 1) and the opposite corner at (0.5, -1).
            Obstacle.ResizeFromCorner(Vector2.zero, new Vector2(2f, 1f), 90f, 2, new Vector2(-1f, 2f), out var center, out var size);

            TestLevels.AssertNear(new Vector2(3f, 1.5f), size);
            TestLevels.AssertNear(new Vector2(-0.25f, 0.5f), center);
        }

        [Test]
        public void ResizeFromCorner_DraggedPastTheOppositeCorner_StopsAtMinimumSize()
        {
            Obstacle.ResizeFromCorner(Vector2.zero, new Vector2(2f, 1f), 0f, 2, new Vector2(-5f, -5f), out _, out var size);

            TestLevels.AssertNear(Vector2.one * Obstacle.MinSize, size);
        }

        [Test]
        public void ResizeFromSide_ChangesOneDimension()
        {
            Obstacle.ResizeFromSide(Vector2.zero, new Vector2(2f, 1f), 0f, 1, new Vector2(3f, 7f), out var center, out var size);
            TestLevels.AssertNear(new Vector2(4f, 1f), size);
            TestLevels.AssertNear(new Vector2(1f, 0f), center);

            Obstacle.ResizeFromSide(Vector2.zero, new Vector2(2f, 1f), 0f, 2, new Vector2(0f, 2f), out center, out size);
            TestLevels.AssertNear(new Vector2(2f, 2.5f), size);
            TestLevels.AssertNear(new Vector2(0f, 0.75f), center);
        }
    }
}
