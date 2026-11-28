using NUnit.Framework;
using RobotVacuum.AlgorithmApi;
using RobotVacuum.Sim.Control;
using RobotVacuumSim.EnvironmentModel;
using UnityEngine;

namespace RobotVacuum.Tests
{
    /// <summary>
    /// The controller's blockage detection (<see cref="BlockageCheck"/>) on hand-built Normal maps: two rooms joined by
    /// a doorway, the robot in the left one. claude/Boxed_In_Improvements_Design.md, section 5.
    /// </summary>
    public class BlockageCheckTests
    {
        const float Radius = 0.17f;
        const float Target = 2f;
        static float DirtyBelow => Target * ControllerConstants.BoxedInDirtyFraction;

        static readonly Vector2 RobotAt = new Vector2(-1f, 0f);
        static readonly Vector2 DockAt = new Vector2(1f, 0f);

        InternalModelWriter writer;
        InternalModelView view;
        BeliefMapAdapter map;
        MapAnalysis analysis;
        BlockageCheck check;

        [SetUp]
        public void SetUp()
        {
            writer = new InternalModelWriter(new InternalModelGrid(Vector2.zero, 0.05f));
            view = new InternalModelView(writer);
            var planner = new PathPlanner(view, Radius + ControllerConstants.ClearanceMarginMeters);
            map = new BeliefMapAdapter(view, planner);
            analysis = new MapAnalysis(map);
            check = new BlockageCheck(view, map);

            // Left room x -2..0, right room x 0..2, y -1..1; a doorway 0.7 m wide in the wall between them.
            Wall(new Vector2(-2f, -1f), new Vector2(2f, -1f));
            Wall(new Vector2(2f, -1f), new Vector2(2f, 1f));
            Wall(new Vector2(2f, 1f), new Vector2(-2f, 1f));
            Wall(new Vector2(-2f, 1f), new Vector2(-2f, -1f));
            Wall(new Vector2(0f, -1f), new Vector2(0f, -0.35f));
            Wall(new Vector2(0f, 0.35f), new Vector2(0f, 1f));
        }

        void Wall(Vector2 a, Vector2 b)
        {
            bool okA = view.TryWorldToCell(a, out var ca);
            bool okB = view.TryWorldToCell(b, out var cb);
            Assert.IsTrue(okA && okB);
            foreach (var c in GridLine.Cells(ca.row, ca.col, cb.row, cb.col)) writer.SetTraversability(c.row, c.col, 0f);
        }

        void Fill(float minX, float minY, float maxX, float maxY, float traversability)
        {
            for (float x = minX; x <= maxX + 1e-4f; x += 0.025f)
                for (float y = minY; y <= maxY + 1e-4f; y += 0.025f)
                    if (view.TryWorldToCell(new Vector2(x, y), out var c)) writer.SetTraversability(c.row, c.col, traversability);
        }

        /// <summary>A cat (or a box) sitting in the doorway.</summary>
        void BlockDoorway(float traversability = 0f) => Fill(-0.15f, -0.4f, 0.15f, 0.4f, traversability);

        /// <summary>The reach sample the controller takes every 10 s (the algorithm keeps the analysis current).</summary>
        void SampleAt(float time)
        {
            analysis.Update(RobotAt.ToApi());
            check.Sample(analysis, time);
        }

        void SampleEvery10s(float from, float to)
        {
            for (float t = from; t <= to + 1e-3f; t += 10f) SampleAt(t);
        }

        bool BoxedIn(float now, float episodeStart, out int cutOff) =>
            check.BoxedIn(analysis, RobotAt, now, episodeStart, DirtyBelow, out cutOff);

        // ---- 1. Cat in the doorway at the finish ---------------------------------------------------------------------

        [Test]
        public void CatInTheDoorway_CutsOffTheOtherRoom_UntilItLeaves()
        {
            SampleEvery10s(0f, 40f);   // both rooms reached
            BlockDoorway();            // the cat sits down at 45 s
            SampleAt(50f);

            Assert.IsTrue(BoxedIn(60f, -1f, out int cutOff), check.LastLine);
            Assert.IsFalse(check.LastByBlockage, "cut-off floor, not a blockage");
            Assert.GreaterOrEqual(cutOff, ControllerConstants.BoxedInCutOffCells);
            Assert.Greater(check.Watched.Count, 0, "the cat's cells are watched");

            BlockDoorway(1f);          // the cat leaves
            Assert.IsFalse(BoxedIn(70f, 60f, out _), check.LastLine);
        }

        // ---- 2 / 3. Blocked long ago: furniture, not a cat ------------------------------------------------------------

        [Test]
        public void DoorwayBlockedLongAgo_IsAWall_NotACutOff()
        {
            SampleEvery10s(0f, 0f);    // both rooms reached at the start
            BlockDoorway();            // furniture mapped at 5 s, still there
            SampleEvery10s(10f, 400f);

            Assert.IsFalse(BoxedIn(400f, -1f, out _), check.LastLine);
        }

        [Test]
        public void Recent_IsMeasuredFromTheEpisodeStart()
        {
            // Blocked at 5 s; an episode that began at 100 s looks back to -20 s, so it is still recent to that episode.
            SampleEvery10s(0f, 0f);
            BlockDoorway();
            SampleEvery10s(10f, 300f);

            Assert.IsTrue(BoxedIn(300f, 100f, out _), check.LastLine);
        }

        /// <summary>Four chair legs near where the algorithm gave up, in floor still to clean (the held Sheep run).</summary>
        void ChairLegs()
        {
            foreach (var leg in new[] { new Vector2(-1.6f, -0.7f), new Vector2(-1.2f, -0.7f), new Vector2(-1.6f, -0.3f), new Vector2(-1.2f, -0.3f) })
                Fill(leg.x - 0.02f, leg.y - 0.02f, leg.x + 0.02f, leg.y + 0.02f, 0f);
        }

        [Test]
        public void ChairLegsMappedLongAgo_AreNotABlockage()
        {
            SampleEvery10s(0f, 0f);    // the preload said floor; reached at the start
            ChairLegs();               // the lidar maps the legs at 5 s
            SampleEvery10s(10f, 1640f);
            check.NoteFailedGoal(new Vector2(-1.4f, -0.5f), 1645f);

            Assert.IsFalse(BoxedIn(1649f, -1f, out _), check.LastLine);
        }

        [Test]
        public void SameCellsBlockedRecently_AreABlockage()
        {
            SampleEvery10s(0f, 1630f);
            ChairLegs();               // something moved in at 1635 s
            SampleAt(1640f);
            check.NoteFailedGoal(new Vector2(-1.4f, -0.5f), 1645f);

            Assert.IsTrue(BoxedIn(1649f, -1f, out _), check.LastLine);
            Assert.IsTrue(check.LastByBlockage, check.LastLine);
            Assert.Greater(check.Watched.Count, 0);
        }

        /// <summary>
        /// Something thin the lidar half sees: its cells flip between blocked and clear several times a second, ending
        /// blocked. The flips are stamped with the writer's clock, as the controller does every step.
        /// </summary>
        void Flicker(System.Action<float> setTo, float from)
        {
            float t = from;
            for (int i = 0; i < 8; i++, t += 0.25f)
            {
                writer.Clock = t;
                setTo(i % 2 == 0 ? 0f : 1f);
            }
            writer.Clock = t;
            setTo(0f);
        }

        void SetChairLegs(float traversability)
        {
            foreach (var leg in new[] { new Vector2(-1.6f, -0.7f), new Vector2(-1.2f, -0.7f), new Vector2(-1.6f, -0.3f), new Vector2(-1.2f, -0.3f) })
                Fill(leg.x - 0.02f, leg.y - 0.02f, leg.x + 0.02f, leg.y + 0.02f, traversability);
        }

        [Test]
        public void FlickeringChairLegs_AreNotABlockage_EvenWhenRecent()
        {
            SampleEvery10s(0f, 1630f);
            Flicker(SetChairLegs, 1635f); // the lidar half sees the legs: they flicker, then read blocked
            SampleAt(1640f);
            writer.Clock = 1649f;
            check.NoteFailedGoal(new Vector2(-1.4f, -0.5f), 1645f);

            Assert.IsTrue(view.TryWorldToCell(new Vector2(-1.2f, -0.3f), out var leg));
            Assert.IsTrue(view.Flickers(leg.row, leg.col), "the leg flickers");
            Assert.IsFalse(BoxedIn(1649f, -1f, out _), check.LastLine);
            StringAssert.Contains("flickering", check.LastLine);
        }

        /// <summary>Two cells beside the legs, blocked once (not yet flagged as flickering): more of the same chair.</summary>
        void TwoUnflaggedLegCells(float traversability)
        {
            foreach (var at in new[] { new Vector2(-1.6f, -0.5f), new Vector2(-1.2f, -0.5f) })
            {
                Assert.IsTrue(view.TryWorldToCell(at, out var c));
                writer.SetTraversability(c.row, c.col, traversability); // exactly one cell each
            }
        }

        [Test]
        public void AFewUnflaggedCells_BesideFlickeringLegs_AreNotABlockage()
        {
            // The seed-38533298 run at 1296 s: "moved 18 (recently blocked 2, flickering 16)" started a hold.
            SampleEvery10s(0f, 1630f);
            Flicker(SetChairLegs, 1635f);
            writer.Clock = 1638f;
            TwoUnflaggedLegCells(0f);
            SampleAt(1640f);
            writer.Clock = 1649f;
            check.NoteFailedGoal(new Vector2(-1.4f, -0.5f), 1645f);

            Assert.IsFalse(BoxedIn(1649f, -1f, out _), check.LastLine);
        }

        [Test]
        public void ACat_BesideFlickeringLegs_IsStillABlockage()
        {
            SampleEvery10s(0f, 1630f);
            Flicker(SetChairLegs, 1635f);
            writer.Clock = 1638f;
            Fill(-1.5f, -0.6f, -1.3f, -0.4f, 0f); // a cat sits down between the legs: a dozen cells or more, flipped once
            SampleAt(1640f);
            writer.Clock = 1649f;
            check.NoteFailedGoal(new Vector2(-1.4f, -0.5f), 1645f);

            Assert.IsTrue(BoxedIn(1649f, -1f, out _), check.LastLine);
            Assert.IsTrue(check.LastByBlockage, check.LastLine);
        }

        [Test]
        public void FlickeringDoorway_IsNotACutOff()
        {
            SampleEvery10s(0f, 40f);
            Flicker(t => BlockDoorway(t), 45f);
            SampleAt(50f);
            writer.Clock = 60f;

            Assert.IsFalse(BoxedIn(60f, -1f, out _), check.LastLine);
        }

        [Test]
        public void FlickeringLegsElsewhere_DoNotHideACatInTheDoorway()
        {
            // v6: the "several cells beside flickering ones" rule belongs to the blockage check only. The seed-540684684
            // run: legs flickering in one room let a cat sealing the other room's doorway pass as furniture.
            SampleEvery10s(0f, 30f);
            Flicker(SetChairLegs, 35f);
            writer.Clock = 45f;
            BlockDoorway();            // the cat, flipped once
            SampleAt(50f);
            writer.Clock = 60f;

            Assert.IsTrue(BoxedIn(60f, -1f, out _), check.LastLine);
            Assert.IsFalse(check.LastByBlockage, "cut-off floor, not a blockage");
        }

        [Test]
        public void CatSittingDown_FlipsOnce_AndDoesNotFlicker()
        {
            SampleEvery10s(0f, 40f);
            writer.Clock = 45f;
            BlockDoorway();
            SampleAt(50f);
            writer.Clock = 60f;

            Assert.IsTrue(view.TryWorldToCell(Vector2.zero, out var door));
            Assert.IsFalse(view.Flickers(door.row, door.col));
            Assert.IsTrue(BoxedIn(60f, -1f, out _), check.LastLine);
        }

        // ---- Held floor: what "open" means at the finish (v6) -----------------------------------------------------------

        [Test]
        public void HeldFloor_StaysOutOfReach_EvenWhenTheCheckNoLongerFires()
        {
            // The seed-540684684 run: as the hold went on, the doorway cells stopped counting as recently blocked, the
            // check stopped firing, and the hold ended "open" with the room still sealed.
            SampleEvery10s(0f, 40f);
            writer.Clock = 45f;
            BlockDoorway();
            SampleAt(50f);

            Assert.IsTrue(BoxedIn(60f, -1f, out _), check.LastLine);
            Assert.GreaterOrEqual(check.HeldCellCount, ControllerConstants.BoxedInCutOffCells);
            Assert.AreEqual(false, check.HeldFloorReachable(analysis, RobotAt), "the other room is still sealed");

            SampleEvery10s(60f, 1000f);
            Assert.IsFalse(BoxedIn(1000f, 1000f, out _), "the doorway is old now, so the check no longer fires");
            Assert.AreEqual(false, check.HeldFloorReachable(analysis, RobotAt), "but the held floor is still out of reach");

            BlockDoorway(1f);          // the cat leaves
            Assert.AreEqual(true, check.HeldFloorReachable(analysis, RobotAt));
        }

        [Test]
        public void HeldFloor_NothingHeld_IsNull()
        {
            SampleEvery10s(0f, 40f);
            Assert.IsFalse(BoxedIn(60f, -1f, out _));
            Assert.IsNull(check.HeldFloorReachable(analysis, RobotAt));
        }

        // ---- 4. Watched cells ---------------------------------------------------------------------------------------

        [Test]
        public void WatchedCells_TriggerOnlyWhenTheBlockerClears()
        {
            SampleEvery10s(0f, 40f);
            BlockDoorway();
            SampleAt(50f);
            Assert.IsTrue(BoxedIn(60f, -1f, out _), check.LastLine);
            Assert.IsFalse(check.WatchedCellCleared(), "nothing changed yet");

            // An unrelated cell clearing (a corner of the far wall redrawn) doesn't trigger.
            Assert.IsTrue(view.TryWorldToCell(new Vector2(2f, 1f), out var corner));
            writer.SetTraversability(corner.row, corner.col, 1f);
            Assert.IsFalse(check.WatchedCellCleared(), "unrelated cell");

            // One of the cat's cells clearing does; after a refresh, only a new change would.
            var first = check.Watched[0];
            writer.SetTraversability(first.row, first.col, 1f);
            Assert.IsTrue(check.WatchedCellCleared(), "the blocker moved");
            check.RefreshWatched();
            Assert.IsFalse(check.WatchedCellCleared(), "refreshed");
        }

        // ---- 5. The way home -----------------------------------------------------------------------------------------

        [Test]
        public void DockReachable_FollowsTheDoorway()
        {
            Assert.IsTrue(check.DockReachable(analysis, RobotAt, DockAt, Radius), "open");
            BlockDoorway();
            Assert.IsFalse(check.DockReachable(analysis, RobotAt, DockAt, Radius), "cat in the doorway");
            BlockDoorway(1f);
            Assert.IsTrue(check.DockReachable(analysis, RobotAt, DockAt, Radius), "cat gone");
        }

        [Test]
        public void CatOnTheDock_DockUnreachable_AndItsCellsAreWatched()
        {
            SampleEvery10s(0f, 20f);
            Fill(DockAt.x - 0.15f, DockAt.y - 0.15f, DockAt.x + 0.15f, DockAt.y + 0.15f, 0f);

            Assert.IsFalse(check.DockReachable(analysis, RobotAt, DockAt, Radius));
            var lookout = check.FindHomeLookout(analysis, DockAt);
            Assert.IsTrue(lookout.HasValue, "somewhere beside the dock to look from");
            Assert.IsTrue(view.TryWorldToCell(DockAt, out var dock));
            CollectionAssert.Contains(check.Watched, (dock.row, dock.col), "the dock's own cell is watched");
        }

        [Test]
        public void CatInTheDoorway_HomeLookoutIsBesideIt()
        {
            SampleEvery10s(0f, 20f);
            BlockDoorway();
            Assert.IsFalse(check.DockReachable(analysis, RobotAt, DockAt, Radius));
            var lookout = check.FindHomeLookout(analysis, DockAt);
            Assert.IsTrue(lookout.HasValue);
            Assert.Less(lookout.Value.x, 0f, "on the robot's side");
            Assert.Less(Vector2.Distance(lookout.Value, Vector2.zero), 0.8f, "next to the doorway");
            Assert.Greater(check.Watched.Count, 0, "the cat's cells on the way are watched");
        }

        // ---- 6. The reach record grows with the map ----------------------------------------------------------------

        [Test]
        public void ReachRecord_KeepsWhatItHeld_WhenTheMapGrows()
        {
            SampleAt(10f);             // both rooms reached
            BlockDoorway();            // the cat sits down
            // The map grows down and to the left (a SLAM map growing): the record's rectangle grows, keeping its marks.
            Wall(new Vector2(-4f, -3f), new Vector2(-3.5f, -3f));
            SampleAt(20f);             // the right room can't be reached now, so only the old marks lead there

            Assert.IsTrue(BoxedIn(30f, -1f, out int cutOff), check.LastLine);
            Assert.GreaterOrEqual(cutOff, ControllerConstants.BoxedInCutOffCells);
        }

        [Test]
        public void NoRecord_NoBlockage()
        {
            BlockDoorway();
            Assert.IsFalse(BoxedIn(30f, -1f, out _), "nothing sampled yet");
        }
    }
}
