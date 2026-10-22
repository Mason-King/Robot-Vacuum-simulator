using NUnit.Framework;
using RobotVacuumSim.EnvironmentModel;
using UnityEngine;

namespace RobotVacuum.Tests
{
    public class InternalModelTests
    {
        const float Cell = 0.5f;

        [Test]
        public void NewCell_DefaultsToUnvisitedPassableNormalFloor()
        {
            var cell = BeliefCell.Default;

            Assert.AreEqual(0f, cell.dwellBelief);
            Assert.AreEqual(0f, cell.cleanlinessBelief);
            Assert.AreEqual(1f, cell.traversabilityBelief);
            Assert.AreEqual(1f, cell.vacuumResistanceBelief);
        }

        [Test]
        public void NewGrid_IsEmpty_AndReadsDefaultsWithoutGrowing()
        {
            var grid = new InternalModelGrid(Vector2.zero, Cell);

            var far = grid.GetCell(5000, -5000);

            Assert.IsTrue(grid.IsEmpty);
            Assert.AreEqual(1f, far.traversabilityBelief);
            Assert.AreEqual(1f, far.vacuumResistanceBelief);
            Assert.IsTrue(grid.IsEmpty); // reading did not allocate
        }

        [Test]
        public void Write_GrowsTheMap_AndNeighboursStillReadAsPassable()
        {
            var grid = new InternalModelGrid(Vector2.zero, Cell, 4);
            var wall = BeliefCell.Default;
            wall.traversabilityBelief = 0f;

            Assert.IsTrue(grid.SetCell(3, -2, wall));

            Assert.IsFalse(grid.IsEmpty);
            Assert.AreEqual(0f, grid.GetCell(3, -2).traversabilityBelief);

            // Cells inside the new allocation that were never written must be normal floor, not zero ("blocked").
            Assert.AreEqual(1f, grid.GetCell(3, -1).traversabilityBelief);
            Assert.AreEqual(1f, grid.GetCell(0, 0).vacuumResistanceBelief);
            Assert.AreEqual(1f, grid.GetCell(-1, -6).traversabilityBelief);
        }

        [Test]
        public void Growth_KeepsEarlierBeliefs_InAnyDirection()
        {
            var grid = new InternalModelGrid(Vector2.zero, Cell, 2);
            var a = BeliefCell.Default; a.dwellBelief = 7f;
            var b = BeliefCell.Default; b.cleanlinessBelief = 9f;
            var c = BeliefCell.Default; c.traversabilityBelief = 0.5f;

            grid.SetCell(0, 0, a);
            grid.SetCell(40, 30, b);   // grows up and right
            grid.SetCell(-25, -35, c); // grows down and left

            Assert.AreEqual(7f, grid.GetCell(0, 0).dwellBelief);
            Assert.AreEqual(9f, grid.GetCell(40, 30).cleanlinessBelief);
            Assert.AreEqual(0.5f, grid.GetCell(-25, -35).traversabilityBelief);
            Assert.AreEqual(1f, grid.GetCell(10, 10).traversabilityBelief);
        }

        [Test]
        public void Beliefs_AreFreelyReversible()
        {
            var grid = new InternalModelGrid(Vector2.zero, Cell, 2);
            var cell = BeliefCell.Default;

            cell.dwellBelief = 5f;
            cell.traversabilityBelief = 0f;
            grid.SetCell(1, 1, cell);
            cell.dwellBelief = 1f;
            cell.traversabilityBelief = 1f;
            grid.SetCell(1, 1, cell);

            Assert.AreEqual(1f, grid.GetCell(1, 1).dwellBelief);
            Assert.AreEqual(1f, grid.GetCell(1, 1).traversabilityBelief);
        }

        [Test]
        public void WorldAndIndex_RoundTrip_AroundTheAnchor()
        {
            var anchor = new Vector2(10f, -4f);
            var grid = new InternalModelGrid(anchor, Cell);

            Assert.IsTrue(grid.TryWorldToGridIndex(anchor + new Vector2(-0.1f, 0.1f), out int row, out int col));
            Assert.AreEqual(-1, col); // FloorToInt, not truncation toward zero
            Assert.AreEqual(0, row);

            Vector2 center = grid.GridIndexToWorldCenter(row, col);
            Assert.IsTrue(grid.TryWorldToGridIndex(center, out int row2, out int col2));
            Assert.AreEqual(row, row2);
            Assert.AreEqual(col, col2);
        }

        [Test]
        public void RunawayGuard_RejectsBadCoordinates()
        {
            var grid = new InternalModelGrid(Vector2.zero, Cell, 2, 10f); // 20 cells each way

            Assert.IsFalse(grid.TryWorldToGridIndex(new Vector2(float.NaN, 0f), out _, out _));
            Assert.IsFalse(grid.TryWorldToGridIndex(new Vector2(0f, float.PositiveInfinity), out _, out _));
            Assert.IsFalse(grid.TryWorldToGridIndex(new Vector2(1000f, 0f), out _, out _));
            Assert.IsFalse(grid.SetCell(10000, 0, BeliefCell.Default));
            Assert.IsTrue(grid.IsEmpty);
        }
    }
}
