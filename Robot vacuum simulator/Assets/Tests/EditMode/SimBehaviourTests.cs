using System;
using NUnit.Framework;
using RobotVacuum.Level;
using RobotVacuum.Sim;
using RobotVacuumSim.EnvironmentModel;
using UnityEngine;
using Object = UnityEngine.Object;

namespace RobotVacuum.Tests
{
    public class MovementBrainTests
    {
        GameObject host;
        VacuumRobot robot;

        [SetUp]
        public void SetUp()
        {
            host = new GameObject("Brain test vacuum");
            robot = host.AddComponent<VacuumRobot>();
        }

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(host);

        [Test]
        public void AtLeastFourAlgorithms_EachWithABrainAndALabel()
        {
            var patterns = (MovementPattern[])Enum.GetValues(typeof(MovementPattern));

            Assert.GreaterOrEqual(patterns.Length, 4);
            Assert.AreEqual(patterns.Length, MovementBrain.Labels.Length);
            Assert.IsInstanceOf<RandomBounceBrain>(MovementBrain.Create(MovementPattern.RandomBounce));
            Assert.IsInstanceOf<SpiralBrain>(MovementBrain.Create(MovementPattern.Spiral));
            Assert.IsInstanceOf<WallFollowBrain>(MovementBrain.Create(MovementPattern.WallFollow));
            Assert.IsInstanceOf<LawnmowerBrain>(MovementBrain.Create(MovementPattern.Lawnmower));
            Assert.IsInstanceOf<PictureBrain>(MovementBrain.Create(MovementPattern.Picture));
            Assert.IsInstanceOf<JamesBrain>(MovementBrain.Create(MovementPattern.James));
        }

        [Test]
        public void Robot_PatternCanBeSwitched()
        {
            robot.Pattern = MovementPattern.Lawnmower;

            Assert.AreEqual(MovementPattern.Lawnmower, robot.Pattern);
        }

        [Test]
        public void RandomBounce_TurnsWithinTheRobotsRange()
        {
            var brain = new RandomBounceBrain();

            for (int i = 0; i < 25; i++)
            {
                var manoeuvre = brain.AfterBump(robot);
                float magnitude = Mathf.Abs(manoeuvre.turn);

                Assert.GreaterOrEqual(magnitude, robot.TurnAngleRange.x);
                Assert.LessOrEqual(magnitude, robot.TurnAngleRange.y);
                Assert.AreEqual(0f, manoeuvre.driveAfter);
            }
        }

        [Test]
        public void Spiral_WidensOverTime_BouncesAfterABump_ThenSpiralsAgain()
        {
            var brain = new SpiralBrain();
            brain.Reset(robot);
            const float dt = 0.02f;

            float first = brain.Steer(robot, dt);
            float later = first;
            for (int i = 0; i < 200; i++) later = brain.Steer(robot, dt);

            Assert.Greater(first, later, "the turn rate eases off as the spiral widens");
            Assert.Greater(later, 0f);
            Assert.IsTrue(brain.Spiralling);

            brain.AfterBump(robot);
            Assert.IsFalse(brain.Spiralling);

            int bounceSteps = Mathf.CeilToInt(SpiralBrain.BounceSeconds / dt) + 1;
            for (int i = 0; i < bounceSteps; i++) brain.Steer(robot, dt);
            Assert.IsTrue(brain.Spiralling);
        }

        [Test]
        public void Lawnmower_StepsOverOneLane_AlternatingSides()
        {
            var brain = new LawnmowerBrain();
            brain.Reset(robot);

            var first = brain.AfterBump(robot);
            var second = brain.AfterBump(robot);

            Assert.AreEqual(90f, first.turn);
            Assert.AreEqual(90f, first.turnAfter);
            Assert.AreEqual(robot.CleaningWidth * LawnmowerBrain.LaneOverlap, first.driveAfter, 1e-5f);
            Assert.AreEqual(-90f, second.turn);
            Assert.AreEqual(-90f, second.turnAfter);
            Assert.AreEqual(0f, brain.Steer(robot, 0.02f), "lanes are straight");
        }

        [Test]
        public void WallFollow_SteersToHoldTheGap()
        {
            Assert.Less(WallFollowBrain.Steering(0.3f), 0f, "too far: turn right towards the wall");
            Assert.Greater(WallFollowBrain.Steering(0f), 0f, "too close: turn left away");
            Assert.AreEqual(0f, WallFollowBrain.Steering(WallFollowBrain.Gap), 1e-4f);
        }

        [Test]
        public void WallFollow_DrivesStraightUntilItFindsAWall_ThenCurlsRoundCorners()
        {
            var brain = new WallFollowBrain();
            brain.Reset(robot);

            Assert.AreEqual(0f, brain.Steer(robot, 0.02f), "nothing sensed and no wall yet");

            var bump = brain.AfterBump(robot);
            Assert.Greater(bump.turn, 0f, "turns left to put the wall on its right");
            Assert.IsTrue(brain.HasWall);

            Assert.Less(brain.Steer(robot, 0.02f), 0f, "lost the wall: curl right round the corner");
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
