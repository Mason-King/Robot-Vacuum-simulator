using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using RobotVacuum.Level;
using RobotVacuum.Sim;
using RobotVacuum.Sim.Control;
using RobotVacuum.Sim.Sensors;
using RobotVacuumSim.EnvironmentModel;
using UnityEngine;
using Object = UnityEngine.Object;

namespace RobotVacuum.Tests
{
    public class BeliefRuleTests
    {
        [Test]
        public void DistanceWeight_IsFullUpToOneMetre_ThenFallsToAQuarterAtMaxRange()
        {
            Assert.AreEqual(1f, BeliefRules.DistanceWeight(0.5f, 8f), 1e-6f);
            Assert.AreEqual(1f, BeliefRules.DistanceWeight(1f, 8f), 1e-6f);
            Assert.AreEqual(0.625f, BeliefRules.DistanceWeight(4.5f, 8f), 1e-5f);
            Assert.AreEqual(0.25f, BeliefRules.DistanceWeight(8f, 8f), 1e-6f);
        }

        [Test]
        public void TwoNearHits_MakeAFreeCellImpassable_AndItSnapsToExactlyZero()
        {
            float belief = 1f;
            belief = BeliefRules.Step(belief, 0f, BeliefUpdateConstants.HitAlpha, 0.5f, 8f, snapToZero: true);
            Assert.Greater(belief, 0f, "one reading is not enough");
            Assert.Less(belief, 0.2f, "a drastic near contradiction adapts fast");

            belief = BeliefRules.Step(belief, 0f, BeliefUpdateConstants.HitAlpha, 0.5f, 8f, snapToZero: true);
            Assert.AreEqual(0f, belief, "snapped to exactly impassable");
        }

        [Test]
        public void NearReadings_MoveTheBeliefMoreThanFarOnes()
        {
            float near = BeliefRules.Step(0.3f, 1f, BeliefUpdateConstants.ClearAlpha, 0.5f, 8f, snapToZero: true);
            float far = BeliefRules.Step(0.3f, 1f, BeliefUpdateConstants.ClearAlpha, 8f, 8f, snapToZero: true);

            Assert.Greater(near, far);
            Assert.Greater(far, 0.3f);
        }

        [Test]
        public void WithoutSnap_SmallValuesStay()
        {
            float value = BeliefRules.Step(0.12f, 0.05f, BeliefUpdateConstants.DragAlpha, 0f, 0f, snapToZero: false);
            Assert.Greater(value, 0f);
        }
    }

    public class InternalModelControlTests
    {
        const float Cell = 0.05f;

        LevelData level;
        InternalModelWriter writer;
        InternalModelView view;

        [SetUp]
        public void SetUp()
        {
            level = TestLevels.NewLevel(SampleLevels.PopulateBlank); // 4 × 3 m room centred on the origin, walls on every edge
            writer = new InternalModelWriter(new InternalModelGrid(Vector2.zero, Cell));
            view = new InternalModelView(writer);
            view.SetBelievedPose(new RobotPose(Vector2.zero, 0f));
        }

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(level);

        float TraversabilityAt(Vector2 world)
        {
            Assert.IsTrue(view.TryWorldToCell(world, out var cell));
            return view.GetCell(cell).traversabilityBelief;
        }

        [Test]
        public void NormalPreload_WallsAndBlockingFurnitureAreZero_FloorPassUnderAndCatAreNot()
        {
            level.Obstacles.Add(new Obstacle { kind = ObstacleKind.Box, center = new Vector2(-1f, 0f), size = Vector2.one * 0.5f, blocksVacuum = true });
            level.Obstacles.Add(new Obstacle { kind = ObstacleKind.Table, center = new Vector2(1f, 0.8f), size = Vector2.one * 0.5f, blocksVacuum = false });
            level.Obstacles.Add(new Obstacle { kind = ObstacleKind.Cat, center = new Vector2(1f, -0.8f), size = Vector2.one * 0.5f, blocksVacuum = true });

            int blocked = NormalPreload.Run(writer, level, null);

            Assert.Greater(blocked, 0);
            Assert.AreEqual(1f, TraversabilityAt(new Vector2(0.01f, 0.01f)), "open floor");
            Assert.AreEqual(0f, TraversabilityAt(new Vector2(2.01f, 0.01f)), "inside the right-hand wall");
            Assert.AreEqual(0f, TraversabilityAt(new Vector2(-0.99f, 0.01f)), "blocking furniture");
            Assert.AreEqual(1f, TraversabilityAt(new Vector2(1.01f, 0.81f)), "pass-under furniture stays passable");
            Assert.AreEqual(1f, TraversabilityAt(new Vector2(1.01f, -0.79f)), "the cat is never preloaded");
            Assert.AreEqual(1f, TraversabilityAt(new Vector2(2.6f, 0.01f)), "outside the rooms is left unknown, not blocked");
        }

        [Test]
        public void Reachability_InsideTheRoomYes_OutsideTheWallsNo()
        {
            NormalPreload.Run(writer, level, null);

            Assert.IsTrue(view.IsReachable(new Vector2(1.5f, 1f)));
            Assert.IsFalse(view.IsReachable(new Vector2(2.6f, 0f)), "behind the wall, inside the written map");
            Assert.IsFalse(view.IsReachable(new Vector2(40f, 0f)), "far off the map: the room is closed, so no route leads out");
        }

        [Test]
        public void Reachability_ARoomEdgeWithoutAWall_OpensTheOutside()
        {
            level.Rooms[0].SetWall(1, false); // drop one wall
            NormalPreload.Run(writer, level, null);

            Assert.IsTrue(view.IsReachable(new Vector2(40f, 0f)), "an opening in the walls leads to unexplored space, which may be roamed");
        }

        [Test]
        public void ChangeFeed_PreloadIsNotAChange_LaterWritesAre_AndEachIsReportedOnce()
        {
            NormalPreload.Run(writer, level, null);
            var changed = new List<CellIndex>();

            Assert.IsTrue(view.TakeChangedCells(changed));
            Assert.AreEqual(0, changed.Count, "the preload is the starting map, not a change");

            int counter = view.ChangeCounter;
            writer.SetTraversability(3, 4, 0f);
            writer.SetTraversability(3, 4, 0.5f);

            Assert.Greater(view.ChangeCounter, counter);
            Assert.IsTrue(view.TakeChangedCells(changed));
            Assert.AreEqual(1, changed.Count);
            Assert.AreEqual(3, changed[0].row);
            Assert.AreEqual(4, changed[0].col);

            Assert.IsTrue(view.TakeChangedCells(changed));
            Assert.AreEqual(0, changed.Count);
        }

        [Test]
        public void ReadRegion_TraversabilityIsTheBlocksMinimum()
        {
            writer.SetTraversability(0, 1, 0f);
            var buffer = new float[InternalModelView.RegionBufferLength(4, 4, 2)];

            int written = view.ReadRegion(0, 0, 4, 4, 2, BeliefField.Traversability, buffer);

            Assert.AreEqual(4, written);
            Assert.AreEqual(0f, buffer[0], "one blocked cell blocks its block");
            Assert.AreEqual(1f, buffer[1]);
            Assert.Throws<ArgumentException>(() => view.ReadRegion(0, 0, 4, 4, 1, BeliefField.Traversability, buffer));
        }

        [Test]
        public void LineOfSight_IsCutByABelievedObstacle()
        {
            NormalPreload.Run(writer, level, null);

            Assert.IsTrue(view.HasLineOfSight(new Vector2(-1.5f, 0f), new Vector2(1.5f, 0f)));
            Assert.IsFalse(view.HasLineOfSight(new Vector2(0f, 0f), new Vector2(2.6f, 0f)), "through the wall");
        }

        [Test]
        public void GridLine_IncludesBothEnds()
        {
            var cells = GridLine.Cells(0, 0, 3, 5).ToList();
            Assert.AreEqual(0, cells[0].row);
            Assert.AreEqual(0, cells[0].col);
            Assert.AreEqual(3, cells[cells.Count - 1].row);
            Assert.AreEqual(5, cells[cells.Count - 1].col);
        }

        // ---- Observations ----

        static SensorFrame OneRayStraightAhead(float range, bool hit, float lidarRange = 8f)
        {
            var frame = new SensorFrame();
            frame.Clear();
            frame.hasLidarScan = true;
            frame.lidarRange = lidarRange;
            frame.lidarRays.Add(new LidarRay { bearing = 0f, range = range, hit = hit });
            return frame;
        }

        [Test]
        public void Normal_ALidarHitIsAssigned_AndTheClearPathBeforeItToo()
        {
            var mapper = new ObservationMapper(writer, slam: false);
            writer.SetTraversability(10, 0, 0.3f); // something stale on the path

            mapper.Apply(OneRayStraightAhead(1.02f, hit: true), new RobotPose(new Vector2(0.01f, 0.01f), 0f)); // facing +y

            Assert.AreEqual(0f, TraversabilityAt(new Vector2(0.01f, 1.02f)), "the hit cell");
            Assert.AreEqual(1f, TraversabilityAt(new Vector2(0.01f, 0.51f)), "seen clear: assigned, not averaged");
        }

        [Test]
        public void Normal_APreloadedWallIsNeverClearedByAGrazingRay()
        {
            NormalPreload.Run(writer, level, null);
            var mapper = new ObservationMapper(writer, slam: false);
            Vector2 insideWall = new Vector2(2.01f, 0.01f);
            Assert.AreEqual(0f, TraversabilityAt(insideWall));

            // A (physically impossible) ray that passes clean through the wall: the wall stays.
            mapper.Apply(OneRayStraightAhead(3f, hit: false), new RobotPose(new Vector2(1.01f, 0.01f), -90f)); // facing +x

            Assert.AreEqual(0f, TraversabilityAt(insideWall));
        }

        [Test]
        public void Slam_OneHitIsNotEnough_ButRepeatedHitsAre()
        {
            var mapper = new ObservationMapper(writer, slam: true);
            var pose = new RobotPose(new Vector2(0.01f, 0.01f), 0f);
            var frame = OneRayStraightAhead(0.52f, hit: true);

            mapper.Apply(frame, pose);
            float once = TraversabilityAt(new Vector2(0.01f, 0.52f));
            Assert.Greater(once, 0f);
            Assert.Less(once, 1f);

            mapper.Apply(frame, pose);
            Assert.AreEqual(0f, TraversabilityAt(new Vector2(0.01f, 0.52f)));
        }

        [Test]
        public void ABump_SetsTheCellBeyondTheContactToZero_InBothModes()
        {
            foreach (bool slam in new[] { false, true })
            {
                var w = new InternalModelWriter(new InternalModelGrid(Vector2.zero, Cell));
                var mapper = new ObservationMapper(w, slam);
                var frame = new SensorFrame();
                frame.Clear();
                frame.bumped = true;
                frame.bumpBearing = 0f;
                frame.bumpDistance = 0.17f;

                mapper.Apply(frame, new RobotPose(new Vector2(0.01f, 0.01f), 0f));

                Assert.IsTrue(w.Grid.TryWorldToGridIndex(new Vector2(0.01f, 0.01f + 0.17f + Cell * 0.25f), out int row, out int col));
                Assert.AreEqual(0f, w.Traversability(row, col), $"slam = {slam}");
            }
        }
    }

    public class AlgorithmAndDriverTests
    {
        LevelData level;
        InternalModelWriter writer;
        InternalModelView view;

        [SetUp]
        public void SetUp()
        {
            level = TestLevels.NewLevel(SampleLevels.PopulateBlank);
            writer = new InternalModelWriter(new InternalModelGrid(Vector2.zero, 0.05f));
            view = new InternalModelView(writer);
            NormalPreload.Run(writer, level, null);
        }

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(level);

        [Test]
        public void Catalog_OnlyRandomSupportsSlam_AndSlamRunsWithOthersAreRefused()
        {
            Assert.IsTrue(AlgorithmCatalog.SupportsSlam(MovementPattern.RandomBounce));
            Assert.IsNull(AlgorithmCatalog.SlamRefusal(MovementPattern.RandomBounce));

            foreach (var legacy in new[] { MovementPattern.Spiral, MovementPattern.WallFollow, MovementPattern.Lawnmower })
            {
                Assert.IsFalse(AlgorithmCatalog.SupportsSlam(legacy));
                Assert.That(AlgorithmCatalog.SlamRefusal(legacy), Does.Contain("does not support SLAM"));
            }

            var options = SimulationRunner.Options.Visible(MovementPattern.Spiral);
            var slam = ControllerSettings.Default;
            slam.slam = true;
            options.controller = slam;
            Assert.Throws<InvalidOperationException>(() => SimulationRunner.Start(level, options), "refused before anything is built");
        }

        [Test]
        public void ControllerSettings_NormalRunCarriesAConstantZeroNoise_NeverTheUsersValue()
        {
            var normal = ControllerSettings.From(new RunSettings { randomSeed = false, slam = false, noiseScale = 7f }.Resolved());
            Assert.AreEqual(ControllerSettings.NormalNoiseScale, normal.noiseScale);

            var slam = ControllerSettings.From(new RunSettings { randomSeed = false, slam = true, noiseScale = 7f }.Resolved());
            Assert.AreEqual(7f, slam.noiseScale);
        }

        [Test]
        public void RandomBounce_PicksWaypointsInsideTheRoom_ShortOfTheWalls()
        {
            var algorithm = new RandomBounceAlgorithm();
            algorithm.Initialize(new AlgorithmContext(new RandomProvider(3u), 0.17f, 0.34f, 0.05f, slam: false, lidarRangeMeters: 8f));

            var pose = new RobotPose(Vector2.zero, 0f);
            view.SetBelievedPose(pose);

            // Inner face of the walls: 2 m and 1.5 m from the centre, less half a 0.12 m wall.
            float maxX = 2f - 0.06f - 0.17f + 0.05f; // one cell of grid rounding allowed
            float maxY = 1.5f - 0.06f - 0.17f + 0.05f;

            for (int i = 0; i < 40; i++)
            {
                var goal = algorithm.NextGoal(view, pose, i == 0 ? GoalStatus.None : GoalStatus.Reached);
                Assert.IsTrue(goal.HasValue);
                Assert.LessOrEqual(Mathf.Abs(goal.Value.x), maxX, $"goal {goal.Value}");
                Assert.LessOrEqual(Mathf.Abs(goal.Value.y), maxY, $"goal {goal.Value}");
                Assert.IsFalse(view.IsBlockedAt(goal.Value));
            }
        }

        [Test]
        public void RandomBounce_IsReproducibleFromTheSeed()
        {
            List<Vector2> Run()
            {
                var algorithm = new RandomBounceAlgorithm();
                algorithm.Initialize(new AlgorithmContext(new RandomProvider(11u), 0.17f, 0.34f, 0.05f, false, 8f));
                var list = new List<Vector2>();
                for (int i = 0; i < 10; i++)
                    list.Add(algorithm.NextGoal(view, new RobotPose(Vector2.zero, 0f), GoalStatus.Blocked).Value);
                return list;
            }

            CollectionAssert.AreEqual(Run(), Run());
        }

        static DriveInput Input(RobotPose pose, float time, InternalModelView view, bool bumped = false) =>
            new DriveInput(pose, time, 0.02f, bumped, 0f, view, 0.17f, 0.65f, 0.35f, 320f);

        [Test]
        public void Driver_ReportsReached_WithinTheTolerance()
        {
            var driver = new WaypointDriver();
            var pose = new RobotPose(Vector2.zero, 0f);
            driver.SetGoal(new Vector2(0f, ControllerConstants.ArrivalToleranceMeters * 0.5f), pose, 0f, view);

            driver.Step(Input(pose, 0.02f, view), out float forward, out _);

            Assert.AreEqual(GoalStatus.Reached, driver.Status);
            Assert.AreEqual(0f, forward);
        }

        [Test]
        public void Driver_ReportsUnreachable_ForAGoalInsideAWall()
        {
            var driver = new WaypointDriver();
            driver.SetGoal(new Vector2(2.01f, 0f), new RobotPose(Vector2.zero, 0f), 0f, view);

            Assert.AreEqual(GoalStatus.Unreachable, driver.Status);
        }

        [Test]
        public void Driver_ReportsBlocked_WhenNoProgressForTheTimeout()
        {
            var driver = new WaypointDriver();
            var stuck = new RobotPose(Vector2.zero, 0f);
            driver.SetGoal(new Vector2(0f, 1f), stuck, 0f, view);

            float t = 0f;
            while (t <= ControllerConstants.BlockedTimeoutSeconds + 0.1f && driver.Status == GoalStatus.InProgress)
            {
                t += 0.02f;
                driver.Step(Input(stuck, t, view), out _, out _);
            }

            Assert.AreEqual(GoalStatus.Blocked, driver.Status);
            Assert.Greater(t, ControllerConstants.BlockedTimeoutSeconds - 0.05f);
        }

        [Test]
        public void Driver_ContactAhead_BlocksAtOnceAndBacksOff()
        {
            var driver = new WaypointDriver();
            var pose = new RobotPose(Vector2.zero, 0f);
            driver.SetGoal(new Vector2(0f, 1f), pose, 0f, view);

            driver.Step(Input(pose, 0.02f, view, bumped: true), out float forward, out _);

            Assert.AreEqual(GoalStatus.Blocked, driver.Status);
            Assert.AreEqual(WaypointDriver.Phase.BackingOff, driver.CurrentPhase);
            Assert.Less(forward, 0f, "reversing");
        }

        [Test]
        public void Driver_TurnsInPlace_WhenTheGoalIsBehind()
        {
            var driver = new WaypointDriver();
            var pose = new RobotPose(Vector2.zero, 0f); // facing +y
            driver.SetGoal(new Vector2(0f, -1f), pose, 0f, view);

            driver.Step(Input(pose, 0.02f, view), out float forward, out float turn);

            Assert.AreEqual(0f, forward);
            Assert.AreNotEqual(0f, turn);
        }
    }
}
