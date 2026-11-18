using System.Collections.Generic;
using NUnit.Framework;
using RobotVacuum.Sim.Control;
using RobotVacuumSim.EnvironmentModel;
using UnityEngine;

namespace RobotVacuum.Tests
{
    /// <summary>
    /// Temporary (dynamic) obstacles such as a cat: told apart from walls by what the robot already knew, waited out
    /// on the way to the dock, and forgotten once stale so a cleared doorway opens again.
    /// </summary>
    public class DynamicObstacleTests
    {
        const float Cell = 0.05f;
        InternalModelWriter writer;
        InternalModelView view;

        [SetUp]
        public void SetUp()
        {
            writer = new InternalModelWriter(new InternalModelGrid(Vector2.zero, Cell));
            view = new InternalModelView(writer);
        }

        // A 4 m × 1 m corridor of preloaded floor, walled in all round (as a Normal preload does).
        const int Rows = 20, Cols = 80;

        void PreloadCorridor(int wallColumn = int.MinValue)
        {
            for (int row = -1; row <= Rows; row++)
                for (int col = -1; col <= Cols; col++)
                {
                    bool wall = row < 0 || row == Rows || col < 0 || col == Cols || col == wallColumn;
                    if (wall)
                    {
                        writer.SetTraversability(row, col, 0f);
                        writer.MarkPermanent(row, col);
                    }
                    else writer.Preload(row, col, 1f, 1f);
                }
        }

        void BlockColumn(int col)
        {
            for (int row = 0; row < Rows; row++) writer.SetTraversability(row, col, 0f);
        }

        Vector2 Center(int row, int col) => writer.Grid.GridIndexToWorldCenter(row, col);

        // ---- telling a cat from a wall ---------------------------------------------------------------------

        [Test]
        public void KnownFloorThatBecomesBlocked_IsTemporary()
        {
            PreloadCorridor();
            writer.Clock = 10f;
            writer.SetTraversability(5, 5, 0f); // a cat walks onto known floor

            Assert.IsTrue(writer.IsTransient(5, 5));
            Assert.IsTrue(writer.HasTransientBlocks);
        }

        [Test]
        public void WallsAndFirstSightings_AreNotTemporary()
        {
            PreloadCorridor();
            Assert.IsFalse(writer.HasTransientBlocks, "preloaded walls are static");

            writer.SetTraversability(500, 500, 0f); // never-seen cell, blocked at first sight (SLAM: a new wall)
            Assert.IsFalse(writer.IsTransient(500, 500));

            writer.SetTraversability(-1, 10, 0f); // a permanent wall observed again
            Assert.IsFalse(writer.IsTransient(-1, 10));
        }

        [Test]
        public void SeenClearAgain_IsNoLongerAnObstacle()
        {
            PreloadCorridor();
            writer.SetTraversability(5, 5, 0f);
            writer.SetTraversability(5, 5, writer.EaseAt(5, 5)); // the cat walked off, in view

            Assert.IsFalse(writer.IsTransient(5, 5));
            Assert.IsFalse(writer.HasTransientBlocks);
        }

        [Test]
        public void SeenClearInSlam_ThenBlocked_IsTemporary()
        {
            // No preload: the cell becomes known floor by being seen clear first.
            writer.SetTraversability(3, 3, 1f);
            writer.SetTraversability(3, 3, 0f);
            Assert.IsTrue(writer.IsTransient(3, 3));
        }

        // ---- forgetting stale ones ----------------------------------------------------------------------------

        [Test]
        public void Forget_ClearsOnlyThoseNotSeenLately()
        {
            PreloadCorridor();
            writer.Clock = 10f;
            writer.SetTraversability(5, 5, 0f);
            writer.SetTraversability(6, 6, 0f);

            writer.Clock = 18f;
            writer.SetTraversability(6, 6, 0f); // still being seen blocked: the cat is still there

            int cleared = writer.ForgetTransients(seenBefore: 15f);

            Assert.AreEqual(1, cleared);
            Assert.AreEqual(1f, writer.Traversability(5, 5), "back to its floor's ease");
            Assert.AreEqual(0f, writer.Traversability(6, 6), "seen recently: kept");
            Assert.IsTrue(writer.IsTransient(6, 6));
        }

        [Test]
        public void CatInTheCorridor_CutsThePath_UntilForgotten()
        {
            PreloadCorridor();
            var planner = new PathPlanner(view, 0.2f);
            var path = new List<Vector2>();
            Vector2 robot = Center(10, 5), dock = Center(10, 75);
            Assert.IsTrue(planner.FindPath(robot, dock, path, out _), "open corridor");

            writer.Clock = 100f;
            BlockColumn(40); // the cat lies across the corridor
            Assert.IsFalse(planner.FindPath(robot, dock, path, out _), "no way home while it is there");
            Assert.IsTrue(writer.HasTransientBlocks, "but it is temporary, so worth waiting for");

            Assert.AreEqual(0, writer.ForgetTransients(100f - ControllerConstants.TransientForgetSeconds), "just seen: not forgotten yet");
            Assert.AreEqual(Rows, writer.ForgetTransients(120f - ControllerConstants.TransientForgetSeconds));
            Assert.IsTrue(planner.FindPath(robot, dock, path, out _), "the way home opens again");
        }

        [Test]
        public void PreloadedWallAcrossTheCorridor_IsNotWaitedFor()
        {
            PreloadCorridor(wallColumn: 40);
            BlockColumn(40); // the robot keeps seeing it

            Assert.IsFalse(writer.HasTransientBlocks, "a wall from the floor plan is static: the robot gives up instead of waiting");
            Assert.IsFalse(new PathPlanner(view, 0.2f).FindPath(Center(10, 5), Center(10, 75), new List<Vector2>(), out _));
        }

        // ---- the waiting policy ---------------------------------------------------------------------------------

        [Test]
        public void Wait_OnlyForTemporaryObstacles()
        {
            var wait = new WayHomeWait(replanSeconds: 1f, maxSeconds: 120f);
            Assert.IsFalse(wait.TryWait(0f, temporaryObstacles: false), "nothing temporary: give up at once");
            Assert.IsFalse(wait.EverWaited);
        }

        [Test]
        public void Wait_ReplansEverySecond_AndGivesUpAfterTheLimit()
        {
            var wait = new WayHomeWait(replanSeconds: 1f, maxSeconds: 120f);

            Assert.IsTrue(wait.TryWait(50f, true));
            Assert.IsTrue(wait.Waiting);
            Assert.IsFalse(wait.ReplanDue(50.5f));
            Assert.IsTrue(wait.ReplanDue(51f));
            Assert.IsFalse(wait.Waiting, "driving again after the re-plan");
            Assert.IsFalse(wait.ReplanDue(52f), "once per wait");

            Assert.IsTrue(wait.TryWait(100f, true), "still within the limit");
            Assert.AreEqual(50f, wait.WaitedSeconds(100f), 1e-4f, "measured from the first wait");
            Assert.IsFalse(wait.TryWait(170f, true), "120 s in all: give up");
        }

        [Test]
        public void Wait_ResetsForANewTripHome()
        {
            var wait = new WayHomeWait(1f, 120f);
            wait.TryWait(0f, true);
            wait.Reset();
            Assert.IsFalse(wait.EverWaited);
            Assert.IsFalse(wait.Waiting);
            Assert.IsTrue(wait.TryWait(500f, true), "a fresh limit");
        }
    }
}
