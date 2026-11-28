using System.Collections.Generic;
using NUnit.Framework;
using RobotVacuum.AlgorithmApi;
using RobotVacuum.Sim.Control;
using RobotVacuumSim.EnvironmentModel;
using UnityEngine;

namespace RobotVacuum.Tests
{
    /// <summary>
    /// The map analysis speedups: the body-fits array, the array-heap reach flood and reusing the room split when
    /// the walls and the reachable cells are unchanged. Each must give exactly what the slower version gave. Same
    /// two-room map as <see cref="BlockageCheckTests"/>.
    /// </summary>
    public class MapAnalysisReuseTests
    {
        const float Radius = 0.17f;

        static readonly Vector2 RobotAt = new Vector2(-1f, 0f);
        static readonly Vector2 ElsewhereInTheRoom = new Vector2(-1.5f, 0.5f);

        InternalModelWriter writer;
        InternalModelView view;
        BeliefMapAdapter map;
        MapAnalysis analysis;

        [SetUp]
        public void SetUp()
        {
            writer = new InternalModelWriter(new InternalModelGrid(Vector2.zero, 0.05f));
            view = new InternalModelView(writer);
            var planner = new PathPlanner(view, Radius + ControllerConstants.ClearanceMarginMeters);
            map = new BeliefMapAdapter(view, planner);
            analysis = new MapAnalysis(map);

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

        void BlockDoorway()
        {
            for (float x = -0.15f; x <= 0.15f + 1e-4f; x += 0.025f)
                for (float y = -0.4f; y <= 0.4f + 1e-4f; y += 0.025f)
                    if (view.TryWorldToCell(new Vector2(x, y), out var c)) writer.SetTraversability(c.row, c.col, 0f);
        }

        /// <summary>Every cell's room label, row by row.</summary>
        static List<int> Labels(MapAnalysis of)
        {
            var labels = new List<int>();
            for (int r = of.MinRow; r < of.MinRow + of.Rows; r++)
                for (int c = of.MinCol; c < of.MinCol + of.Cols; c++)
                    labels.Add(of.RoomOf(r, c));
            return labels;
        }

        /// <summary>The same map analysed from scratch, robot at <paramref name="robot"/>.</summary>
        MapAnalysis Fresh(Vector2 robot)
        {
            var fresh = new MapAnalysis(map);
            fresh.Update(robot.ToApi());
            return fresh;
        }

        // ---- body fits ---------------------------------------------------------------------------------------------

        [Test]
        public void BodyFits_IsExploredAndClearOfWalls_ForEveryCell()
        {
            analysis.Update(RobotAt.ToApi());
            var snapshot = analysis.Snapshot;
            int fitting = 0;

            for (int r = snapshot.MinRow - 1; r <= snapshot.MinRow + snapshot.Rows; r++)
                for (int c = snapshot.MinCol - 1; c <= snapshot.MinCol + snapshot.Cols; c++)
                {
                    bool expected = snapshot.IsExplored(r, c) && snapshot.ObstacleDistance(r, c) >= snapshot.Clearance;
                    Assert.AreEqual(expected, snapshot.BodyFits(r, c), $"cell ({r}, {c})");
                    if (expected) fitting++;
                }
            Assert.Greater(fitting, 100, "the rooms have floor the body fits on");
        }

        [Test]
        public void BodyFits_FollowsTheWalls_AfterTheMapChanges()
        {
            analysis.Update(RobotAt.ToApi());
            Assert.IsTrue(view.TryWorldToCell(Vector2.zero, out var doorway));
            Assert.IsTrue(analysis.BodyFits(doorway.row, doorway.col), "the doorway is open");

            BlockDoorway();
            analysis.Update(RobotAt.ToApi());

            Assert.IsFalse(analysis.BodyFits(doorway.row, doorway.col));
        }

        // ---- the reach flood ---------------------------------------------------------------------------------------

        [Test]
        public void Reach_MatchesAPlainDijkstra()
        {
            analysis.Update(RobotAt.ToApi());
            var snapshot = analysis.Snapshot;
            var expected = ReferenceReach(snapshot, map, RobotAt.ToApi());
            int reachable = 0;

            for (int i = 0; i < expected.Length; i++)
            {
                int r = snapshot.MinRow + i / snapshot.Cols, c = snapshot.MinCol + i % snapshot.Cols;
                float got = analysis.ReachDistance(r, c);
                if (float.IsPositiveInfinity(expected[i]))
                {
                    Assert.IsTrue(float.IsPositiveInfinity(got), $"cell ({r}, {c}) should be unreachable");
                    continue;
                }
                reachable++;
                Assert.AreEqual(expected[i], got, 1e-4f, $"cell ({r}, {c})");
            }
            Assert.Greater(reachable, 100, "both rooms are reachable");
        }

        /// <summary>The flood as it was written before the speedup: BodyFits per neighbour, a sorted-set queue.</summary>
        static float[] ReferenceReach(MapSnapshot snapshot, IBeliefMap beliefs, Vec2 from)
        {
            int rows = snapshot.Rows, cols = snapshot.Cols, n = rows * cols;
            var distance = new float[n];
            for (int i = 0; i < n; i++) distance[i] = float.PositiveInfinity;
            if (!beliefs.TryWorldToCell(from, out var start)) return distance;

            bool Fits(int row, int col) => snapshot.IsExplored(row, col) && snapshot.ObstacleDistance(row, col) >= snapshot.Clearance;

            var queue = new SortedSet<(float d, int i)>();
            float cell = snapshot.CellSize;
            int around = (int)System.Math.Ceiling(ReachField.StartSearchMeters / cell);
            for (int r = start.row - around; r <= start.row + around; r++)
                for (int c = start.col - around; c <= start.col + around; c++)
                {
                    if (!Fits(r, c)) continue;
                    float d = Vec2.Distance(beliefs.CellCenter(r, c), from);
                    if (d > ReachField.StartSearchMeters && !(r == start.row && c == start.col)) continue;
                    int i = snapshot.IndexOf(r, c);
                    if (d < distance[i])
                    {
                        distance[i] = d;
                        queue.Add((d, i));
                    }
                }

            float diagonal = cell * 1.41421356f;
            while (queue.Count > 0)
            {
                var (d, i) = queue.Min;
                queue.Remove(queue.Min);
                if (d > distance[i]) continue;
                int row0 = i / cols, col0 = i % cols;
                for (int dr = -1; dr <= 1; dr++)
                    for (int dc = -1; dc <= 1; dc++)
                    {
                        if (dr == 0 && dc == 0) continue;
                        int rr = row0 + dr, cc = col0 + dc;
                        if (rr < 0 || cc < 0 || rr >= rows || cc >= cols) continue;
                        int row = snapshot.MinRow + rr, col = snapshot.MinCol + cc;
                        if (!Fits(row, col)) continue;
                        if (dr != 0 && dc != 0 && (!Fits(row - dr, col) || !Fits(row, col - dc))) continue;
                        float next = d + (dr != 0 && dc != 0 ? diagonal : cell);
                        int j = rr * cols + cc;
                        if (next >= distance[j]) continue;
                        distance[j] = next;
                        queue.Add((next, j));
                    }
            }
            return distance;
        }

        // ---- reusing the room split --------------------------------------------------------------------------------

        [Test]
        public void Rooms_AreReused_WhenNothingChanged()
        {
            analysis.Update(RobotAt.ToApi());
            var first = Labels(analysis);

            analysis.Update(RobotAt.ToApi());

            Assert.AreEqual(1, analysis.RoomBuilds);
            CollectionAssert.AreEqual(first, Labels(analysis));
        }

        [Test]
        public void Rooms_Reused_AfterTheRobotMoves_MatchAFreshSplit()
        {
            analysis.Update(RobotAt.ToApi());
            analysis.Update(ElsewhereInTheRoom.ToApi());

            Assert.AreEqual(1, analysis.RoomBuilds, "same walls, same reachable floor");
            CollectionAssert.AreEqual(Labels(Fresh(ElsewhereInTheRoom)), Labels(analysis));
        }

        [Test]
        public void Rooms_AreRedone_WhenTheWallsChange()
        {
            analysis.Update(RobotAt.ToApi());

            BlockDoorway(); // a cat sits in the doorway: the right room is no longer reachable
            analysis.Update(RobotAt.ToApi());

            Assert.AreEqual(2, analysis.RoomBuilds);
            CollectionAssert.AreEqual(Labels(Fresh(RobotAt)), Labels(analysis));
            Assert.IsTrue(view.TryWorldToCell(new Vector2(1f, 0f), out var right));
            Assert.AreEqual(-1, analysis.RoomOf(right.row, right.col), "the far room is cut off");
        }

        [Test]
        public void Rooms_AreRedone_WhenTheRobotIsOnFloorCutOffFromBefore()
        {
            BlockDoorway();
            analysis.Update(RobotAt.ToApi());
            Assert.IsTrue(view.TryWorldToCell(new Vector2(1f, 0f), out var right));
            Assert.AreEqual(-1, analysis.RoomOf(right.row, right.col));

            analysis.Update(new Vector2(1f, 0f).ToApi()); // the robot is in the other room now; no wall changed

            Assert.AreEqual(2, analysis.RoomBuilds);
            CollectionAssert.AreEqual(Labels(Fresh(new Vector2(1f, 0f))), Labels(analysis));
            Assert.GreaterOrEqual(analysis.RoomOf(right.row, right.col), 0);
        }
    }
}
