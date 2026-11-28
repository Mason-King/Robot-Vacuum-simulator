using NUnit.Framework;
using RobotVacuum.AlgorithmApi;
using RobotVacuum.Sim.Control;
using RobotVacuumSim.EnvironmentModel;
using UnityEngine;

namespace RobotVacuum.Tests
{
    /// <summary>
    /// Where the robot goes to look at cut-off floor (<see cref="BlockageCheck.FindLookout"/>): on its own side of the
    /// blocker, never across a wall from the cut-off floor. Two rooms side by side (A, where the robot is, and C) with no
    /// door between them, both opening onto a hall below through doorways in a thick wall. The cat sits in C's doorway.
    /// The nearest reachable floor to C as the crow flies is in A, through the wall; the lookout must be in the hall, by
    /// C's doorway. claude/Boxed_In_Improvements_Design.md, section 10.
    /// </summary>
    public class BlockageLookoutTests
    {
        const float Radius = 0.17f;
        const float Target = 2f;
        static float DirtyBelow => Target * ControllerConstants.BoxedInDirtyFraction;

        static readonly Vector2 RobotAt = new Vector2(-1f, 0.5f);   // in room A
        static readonly Vector2 DoorC = new Vector2(1.5f, -0.15f);  // C's doorway onto the hall

        InternalModelWriter writer;
        InternalModelView view;
        MapAnalysis analysis;
        BlockageCheck check;

        [SetUp]
        public void SetUp()
        {
            writer = new InternalModelWriter(new InternalModelGrid(Vector2.zero, 0.05f));
            view = new InternalModelView(writer);
            var planner = new PathPlanner(view, Radius + ControllerConstants.ClearanceMarginMeters);
            var map = new BeliefMapAdapter(view, planner);
            analysis = new MapAnalysis(map);
            check = new BlockageCheck(view, map);

            // Outer walls: x -2..2, y -1.5..1.
            Wall(new Vector2(-2f, -1.5f), new Vector2(2f, -1.5f));
            Wall(new Vector2(2f, -1.5f), new Vector2(2f, 1f));
            Wall(new Vector2(2f, 1f), new Vector2(-2f, 1f));
            Wall(new Vector2(-2f, 1f), new Vector2(-2f, -1.5f));
            // A | C: no door.
            Wall(new Vector2(0f, 0f), new Vector2(0f, 1f));
            // The thick wall between the rooms and the hall (y -0.3..0), with A's doorway at x -1.85..-1.15 and C's at
            // x 1.15..1.85.
            Fill(-2f, -0.3f, -1.85f, 0f, 0f);
            Fill(-1.15f, -0.3f, 1.15f, 0f, 0f);
            Fill(1.85f, -0.3f, 2f, 0f, 0f);
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

        void SampleAt(float time)
        {
            analysis.Update(RobotAt.ToApi());
            check.Sample(analysis, time);
        }

        [Test]
        public void Lookout_IsByTheBlockedDoorway_NotAcrossTheWall()
        {
            for (float t = 0f; t <= 40f; t += 10f) SampleAt(t); // every room reached
            writer.Clock = 45f;
            Fill(1.1f, -0.35f, 1.9f, 0.05f, 0f);              // the cat sits in C's doorway
            SampleAt(50f);

            Assert.IsTrue(check.BoxedIn(analysis, RobotAt, 60f, -1f, DirtyBelow, out int cutOff), check.LastLine);
            Assert.IsFalse(check.LastByBlockage, "cut-off floor, not a blockage");
            Assert.GreaterOrEqual(cutOff, ControllerConstants.BoxedInCutOffCells);

            Vector2? lookout = check.FindLookout(analysis);
            Assert.IsTrue(lookout.HasValue);
            Assert.Less(lookout.Value.y, -0.3f, $"the lookout {lookout.Value} must be in the hall, not in room A across the wall");
            Assert.Less(Vector2.Distance(lookout.Value, DoorC), 1f, $"the lookout {lookout.Value} must be by C's doorway");
        }

        [Test]
        public void Lookout_ReachedAndWatched_SeesTheBlocker()
        {
            for (float t = 0f; t <= 40f; t += 10f) SampleAt(t);
            writer.Clock = 45f;
            Fill(1.1f, -0.35f, 1.9f, 0.05f, 0f);
            SampleAt(50f);
            Assert.IsTrue(check.BoxedIn(analysis, RobotAt, 60f, -1f, DirtyBelow, out _), check.LastLine);
            Assert.Greater(check.Watched.Count, 0, "the cat's cells are watched");

            Vector2 lookout = check.FindLookout(analysis).Value;
            // Every watched cell is within lidar range of the lookout and nothing blocked lies between them but the
            // blocker itself: no wall cell (never reached) on the straight line.
            foreach (var (row, col) in check.Watched)
            {
                Vector2 cell = view.CellCenter(row, col);
                Assert.Less(Vector2.Distance(cell, lookout), 1.5f);
                Assert.IsTrue(view.TryWorldToCell(lookout, out var from));
                foreach (var c in GridLine.Cells(from.row, from.col, row, col))
                {
                    Vector2 at = view.CellCenter(c.row, c.col);
                    bool wall = view.IsBlocked(c.row, c.col) && (at.y > 0.06f || at.x < 1.08f || at.x > 1.92f);
                    Assert.IsFalse(wall, $"a wall at {at} lies between the lookout {lookout} and the watched cell {cell}");
                }
            }
        }
    }
}
