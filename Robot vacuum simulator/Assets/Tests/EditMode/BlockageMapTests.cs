using NUnit.Framework;
using RobotVacuum.AlgorithmApi;
using RobotVacuum.Sim.Control;
using RobotVacuumSim.EnvironmentModel;
using UnityEngine;

namespace RobotVacuum.Tests
{
    /// <summary>
    /// The boxed-in controller's map checks (<see cref="BlockageMap"/>) and the writer's flicker tracking, on small
    /// hand-built Normal-mode maps: two rooms joined by a doorway, a cat that sits in it, chair legs the lidar half sees.
    /// The map is only ever read by these checks.
    /// </summary>
    public class BlockageMapTests
    {
        const float Cell = 0.05f;
        const float Radius = 0.17f;

        InternalModelWriter writer;
        InternalModelView view;
        MapAnalysis analysis;
        BlockageMap blockage;

        // Two 1.5 m × 1.0 m rooms side by side (x 0–1.5 and 1.5–3), a wall between them with a 0.6 m doorway at
        // y 0.2–0.8, and the robot (and dock) in the left room.
        static readonly Vector2 Robot = new Vector2(0.5f, 0.5f);
        static readonly Vector2 Doorway = new Vector2(1.5f, 0.5f);
        static readonly Vector2 FarRoom = new Vector2(2.4f, 0.5f);

        [SetUp]
        public void SetUp()
        {
            writer = new InternalModelWriter(new InternalModelGrid(Vector2.zero, Cell));
            view = new InternalModelView(writer);
            var planner = new PathPlanner(view, Radius + ControllerConstants.ClearanceMarginMeters);
            analysis = new MapAnalysis(new BeliefMapAdapter(view, planner));
            blockage = new BlockageMap();
        }

        void Wall(Vector2 a, Vector2 b)
        {
            bool okA = view.TryWorldToCell(a, out var ca);
            bool okB = view.TryWorldToCell(b, out var cb);
            Assert.IsTrue(okA && okB);
            foreach (var c in GridLine.Cells(ca.row, ca.col, cb.row, cb.col)) writer.SetTraversability(c.row, c.col, 0f);
        }

        void TwoRooms(bool doorway = true)
        {
            Wall(new Vector2(0f, 0f), new Vector2(3f, 0f));
            Wall(new Vector2(3f, 0f), new Vector2(3f, 1f));
            Wall(new Vector2(3f, 1f), new Vector2(0f, 1f));
            Wall(new Vector2(0f, 1f), new Vector2(0f, 0f));
            if (doorway)
            {
                Wall(new Vector2(1.5f, 0f), new Vector2(1.5f, 0.2f));
                Wall(new Vector2(1.5f, 0.8f), new Vector2(1.5f, 1f));
            }
            else Wall(new Vector2(1.5f, 0f), new Vector2(1.5f, 1f));
        }

        /// <summary>Sets every cell whose centre is inside the rectangle to <paramref name="value"/>.</summary>
        void Fill(float minX, float minY, float maxX, float maxY, float value)
        {
            for (float x = minX; x <= maxX + 1e-4f; x += Cell)
                for (float y = minY; y <= maxY + 1e-4f; y += Cell)
                    if (view.TryWorldToCell(new Vector2(x, y), out var c)) writer.SetTraversability(c.row, c.col, value);
        }

        void Update() => analysis.Update(new Vec2(Robot.x, Robot.y));

        /// <summary>The robot has driven around with the way open: what it could reach is recorded.</summary>
        void RecordReach()
        {
            Update();
            blockage.Sample(analysis);
        }

        bool BoxedIn(out BlockageMap.Check check, float dirtyBelow = 1f, Vector2? focus = null, bool focusIsFailure = false,
            System.Func<int, int, bool> flickers = null)
        {
            Update();
            return blockage.BoxedIn(analysis, view.CellCenter, dirtyBelow, focus ?? Robot, focusIsFailure, flickers, out check);
        }

        (int row, int col) CellAt(Vector2 world)
        {
            Assert.IsTrue(view.TryWorldToCell(world, out var c));
            return (c.row, c.col);
        }

        // ---- Cut off ----------------------------------------------------------------------------------------

        [Test]
        public void NothingInTheWay_IsNotBoxedIn()
        {
            TwoRooms();
            RecordReach();
            Assert.IsFalse(BoxedIn(out _));
        }

        [Test]
        public void NoRecordYet_IsNotBoxedIn()
        {
            TwoRooms();
            Fill(1.4f, 0.2f, 1.6f, 0.8f, 0f);
            Assert.IsFalse(blockage.HasRecord);
            Assert.IsFalse(BoxedIn(out _));
        }

        [Test]
        public void CatInTheDoorway_CutsOffTheFarRoom()
        {
            TwoRooms();
            RecordReach();
            Fill(1.4f, 0.2f, 1.6f, 0.8f, 0f); // the cat lies across the doorway

            Assert.IsTrue(BoxedIn(out var check));
            Assert.IsFalse(check.ByBlockage, "found as cut-off floor, not by the blockage rules");
            Assert.GreaterOrEqual(check.CutOff, ControllerConstants.BoxedInCutOffCells);

            var lookout = blockage.FindLookout(analysis, view.CellCenter);
            Assert.IsTrue(lookout.HasValue);
            Assert.Less(lookout.Value.x, 1.5f, "the lookout is on the robot's side");
            Assert.Less(Vector2.Distance(lookout.Value, Doorway), 0.7f, "and beside the doorway, where the lidar can see the cat");
        }

        [Test]
        public void CutOffFloor_AlreadyClean_IsNotMissed()
        {
            TwoRooms();
            RecordReach();
            Fill(1.4f, 0.2f, 1.6f, 0.8f, 0f);
            Assert.IsFalse(BoxedIn(out _, dirtyBelow: 0f), "nothing is still to clean behind the cat");
        }

        [Test]
        public void RoomTheRobotNeverReached_IsNotCutOff()
        {
            TwoRooms(doorway: false); // the far room is shut off by a wall from the start (seen through a window, say)
            RecordReach();
            Assert.IsFalse(BoxedIn(out var check));
            Assert.AreEqual(0, check.CutOff);
        }

        [Test]
        public void CatLeaves_TheWayIsOpenAgain()
        {
            TwoRooms();
            RecordReach();
            Fill(1.4f, 0.2f, 1.6f, 0.8f, 0f);
            Assert.IsTrue(BoxedIn(out _));
            Fill(1.4f, 0.2f, 1.6f, 0.8f, 1f); // the lidar sees the doorway clear
            Assert.IsFalse(BoxedIn(out _));
        }

        // ---- The way home ------------------------------------------------------------------------------------

        [Test]
        public void DockReachable_UntilTheCatSitsOnIt()
        {
            TwoRooms();
            RecordReach();
            var dock = CellAt(FarRoom);
            int reach = Mathf.CeilToInt(Radius / Cell);
            Update();
            Assert.IsTrue(BlockageMap.DockReachable(analysis, dock.row, dock.col, reach));

            Fill(FarRoom.x - 0.3f, FarRoom.y - 0.3f, FarRoom.x + 0.3f, FarRoom.y + 0.3f, 0f);
            Update();
            Assert.IsFalse(BlockageMap.DockReachable(analysis, dock.row, dock.col, reach));
        }

        [Test]
        public void HomeLookout_IsBesideTheBlockage()
        {
            TwoRooms();
            RecordReach();
            Fill(1.4f, 0.2f, 1.6f, 0.8f, 0f);
            Update();
            var dock = CellAt(FarRoom);
            var lookout = blockage.FindHomeLookout(analysis, view.CellCenter, dock.row, dock.col);
            Assert.IsTrue(lookout.HasValue);
            Assert.Less(lookout.Value.x, 1.5f);
            Assert.Less(Vector2.Distance(lookout.Value, Doorway), 0.7f);
        }

        [Test]
        public void HomeLookout_None_WhenTheWayWasNeverOpen()
        {
            TwoRooms(doorway: false);
            RecordReach();
            var dock = CellAt(FarRoom);
            Assert.IsFalse(blockage.FindHomeLookout(analysis, view.CellCenter, dock.row, dock.col).HasValue);
        }

        [Test]
        public void MovedNear_CountsTheCat_NotWallsThatWereAlwaysThere()
        {
            TwoRooms();
            RecordReach();
            Update();
            Assert.AreEqual(0, blockage.MovedNear(analysis, view.CellCenter, Doorway, null, 1f, null), "walls were never reached");

            Fill(1.4f, 0.2f, 1.6f, 0.8f, 0f);
            Update();
            Assert.GreaterOrEqual(blockage.MovedNear(analysis, view.CellCenter, Doorway, null, 1f, null), ControllerConstants.BoxedInHomeMovedCells);
            Assert.AreEqual(0, blockage.MovedNear(analysis, view.CellCenter, new Vector2(0.2f, 0.2f), null, 0.3f, null), "only near the given places");
        }

        [Test]
        public void MovedNear_SkipsFlickeringCells()
        {
            TwoRooms();
            RecordReach();
            Fill(1.4f, 0.2f, 1.6f, 0.8f, 0f);
            Update();
            Assert.AreEqual(0, blockage.MovedNear(analysis, view.CellCenter, Doorway, null, 1f, (r, c) => true));
        }

        // ---- Blockage near the robot: the cat vs. chair legs --------------------------------------------------

        // A 3 m × 2 m room with the robot near the middle; things placed 0.6 m from it.
        void OpenRoom()
        {
            Wall(new Vector2(0f, 0f), new Vector2(3f, 0f));
            Wall(new Vector2(3f, 0f), new Vector2(3f, 2f));
            Wall(new Vector2(3f, 2f), new Vector2(0f, 2f));
            Wall(new Vector2(0f, 2f), new Vector2(0f, 0f));
        }

        static readonly Vector2 RoomRobot = new Vector2(1.0f, 1.0f);
        static readonly Vector2 Spot = new Vector2(1.6f, 1.0f);

        void RecordReachAt(Vector2 robot)
        {
            analysis.Update(new Vec2(robot.x, robot.y));
            blockage.Sample(analysis);
        }

        bool BoxedInAt(Vector2 robot, out BlockageMap.Check check, System.Func<int, int, bool> flickers)
        {
            analysis.Update(new Vec2(robot.x, robot.y));
            return blockage.BoxedIn(analysis, view.CellCenter, 1f, robot, false, flickers, out check);
        }

        void ChairLegs(float value)
        {
            foreach (var leg in new[] { new Vector2(-0.2f, -0.2f), new Vector2(0.2f, -0.2f), new Vector2(-0.2f, 0.2f), new Vector2(0.2f, 0.2f) })
                Fill(Spot.x + leg.x, Spot.y + leg.y, Spot.x + leg.x + Cell, Spot.y + leg.y + Cell, value);
        }

        [Test]
        public void CatSittingOnFloorStillToClean_IsABlockage()
        {
            OpenRoom();
            RecordReachAt(RoomRobot);
            Fill(Spot.x - 0.15f, Spot.y - 0.1f, Spot.x + 0.15f, Spot.y + 0.1f, 0f);

            Assert.IsTrue(BoxedInAt(RoomRobot, out var check, writer.Flickers));
            Assert.IsTrue(check.ByBlockage);
            Assert.GreaterOrEqual(check.Moved, ControllerConstants.BoxedInMovedMinCells);
            Assert.AreEqual(0, check.Flickering);
        }

        [Test]
        public void ChairLegsThatFlicker_AreNotABlockage()
        {
            OpenRoom();
            RecordReachAt(RoomRobot);

            // The lidar half sees the legs: their cells flip between blocked and clear, scan after scan.
            float t = 100f;
            for (int i = 0; i < ControllerConstants.FlickerFlips + 2; i++)
            {
                writer.Clock = t;
                ChairLegs(i % 2 == 0 ? 0f : 1f);
                t += 0.2f;
            }
            writer.Clock = t;
            ChairLegs(0f);

            Assert.IsTrue(BoxedInAt(RoomRobot, out var withoutFlicker, null),
                "without flicker tracking the legs pass as a cat (the false alarm from the held Sheep run)");
            Assert.IsTrue(withoutFlicker.ByBlockage);

            Assert.IsFalse(BoxedInAt(RoomRobot, out var check, writer.Flickers));
            Assert.Greater(check.Flickering, 0);
            Assert.Less(check.Moved, ControllerConstants.BoxedInMovedMinCells);
        }

        // ---- Flicker -------------------------------------------------------------------------------------------

        void Flip(int times, float every, float from = 10f)
        {
            for (int i = 0; i < times; i++)
            {
                writer.Clock = from + i * every;
                writer.SetTraversability(5, 5, i % 2 == 0 ? 0f : 1f);
            }
        }

        [Test]
        public void QuickFlipsInARow_Flicker()
        {
            Flip(ControllerConstants.FlickerFlips, 0.2f);
            Assert.IsTrue(writer.Flickers(5, 5));
            Assert.IsFalse(writer.Flickers(5, 6));
            Assert.AreEqual(1, writer.FlickerCellCount);
        }

        [Test]
        public void ACatComingAndGoing_DoesNotFlicker()
        {
            Flip(ControllerConstants.FlickerFlips + 2, ControllerConstants.FlickerGapSeconds + 2f);
            Assert.IsFalse(writer.Flickers(5, 5));
        }

        [Test]
        public void SeveralWritesInOneStep_CountOnce()
        {
            writer.Clock = 10f;
            for (int i = 0; i < ControllerConstants.FlickerFlips * 2; i++) writer.SetTraversability(5, 5, i % 2 == 0 ? 0f : 1f);
            Assert.IsFalse(writer.Flickers(5, 5));
        }

        [Test]
        public void Flicker_IsForgottenAfterAWhile()
        {
            Flip(ControllerConstants.FlickerFlips, 0.2f);
            writer.Clock += ControllerConstants.FlickerMemorySeconds + 1f;
            Assert.IsFalse(writer.Flickers(5, 5));
        }

        [Test]
        public void Flicker_NeverChangesTheMap()
        {
            Flip(ControllerConstants.FlickerFlips + 1, 0.2f); // ends blocked
            Assert.IsTrue(writer.Flickers(5, 5));
            Assert.AreEqual(0f, writer.Traversability(5, 5));
        }
    }
}
