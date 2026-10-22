using UnityEngine;

// ============================================================================
//
// STATUS: the Internal Model's data structure only -- no SLAM, no sensor
// fusion, no getters for algorithms yet. Those are separate, later work that
// ACTS ON this grid from the outside. Nothing here computes a belief; it only
// stores one.
//
// Differences from the External Model, all deliberate:
//   * It is the ROBOT's belief, not ground truth, so every value is freely
//     settable and reversible (no increase-only rule, no clamps).
//   * It has no idea what the level looks like. It is anchored to a single
//     world point (the robot's spawn) and starts EMPTY; it grows only when
//     something is written. Its bounds therefore reveal nothing but what has
//     been written.
//   * It shares the External Model's LATTICE: same cell size, edges at
//     anchor + k * cellSize. Cells with the same (row, col) lattice index are
//     the same physical square in both models. Indices can be negative.
//   * It is never saved and has no event log (not required by the SDD).
//
// No obstacle flag: an impassable cell is traversabilityBelief == 0 exactly.
// ============================================================================

namespace RobotVacuumSim.EnvironmentModel
{
    // ------------------------------------------------------------------
    // BeliefCell: the robot's current best guess about one cell, never
    // ground truth. All four fields are freely settable.
    //
    // CAUTION: C# zero-fills new arrays, and zero is NOT a valid default
    // here (traversability 0 means "impassable"). Anything that allocates
    // BeliefCells in bulk must fill with BeliefCell.Default -- the grid does
    // this for you.
    // ------------------------------------------------------------------
    public struct BeliefCell
    {
        // Seconds the robot believes it has spent over this cell. Same scale
        // as External's dwell, unbounded. Above 0 also tells an algorithm
        // "I have been here".
        public float dwellBelief;

        // Cleanliness the robot believes it has accomplished here. Same scale
        // as External's cleanliness, unbounded, compared against the run's
        // target_max_clean (which the robot knows, but which is NOT stored
        // here -- it comes from the run config).
        public float cleanlinessBelief;

        // How easily the robot believes it can cross this cell. A multiplier
        // on movement: 1.0 = normal floor, 0.5 = twice as hard, and EXACTLY 0
        // = impassable (an obstacle). Pathfinding treats 0 as a wall, not a
        // large cost, so a planner can never route through it. Independent
        // of the vacuum resistance below: a floor's drag on the wheels need
        // not match its drag on the brush.
        public float traversabilityBelief;

        // How hard the robot believes this floor is to clean (brush/floor
        // difficulty). A RESISTANCE: higher = harder, 1.0 = normal. Rate
        // formulas divide by it, so it must never be 0.
        public float vacuumResistanceBelief;

        /// <summary>An unvisited cell: nothing done, normal floor, passable.</summary>
        public static BeliefCell Default => new BeliefCell
        {
            dwellBelief = 0f,
            cleanlinessBelief = 0f,
            traversabilityBelief = 1f,
            vacuumResistanceBelief = 1f,
        };
    }

    // ------------------------------------------------------------------
    // The grid container: a dense rectangle that grows on write.
    //
    //   * Starts with no cells. Reads anywhere return BeliefCell.Default and
    //     never allocate.
    //   * A write outside the current rectangle reallocates a larger one
    //     (extended past the write by growthPaddingCells on each side that
    //     needs to grow) and copies the old data across. Growth is rare and
    //     O(cells); reads and writes inside the rectangle are plain array
    //     indexing.
    //   * A write further than maxHalfWidthMeters from the anchor is ignored
    //     with a warning: a runaway guard against bad coordinates, not a
    //     floor-size limit.
    // ------------------------------------------------------------------
    public class InternalModelGrid
    {
        private readonly float cellSizeMeters;
        private readonly Vector2 anchorWorld;
        private readonly int growthPaddingCells;
        private readonly int maxIndex;

        // Row-major: index = (row - minRow) * cols + (col - minCol).
        private BeliefCell[] cells = new BeliefCell[0];
        private int minRow;
        private int minCol;
        private int rows;
        private int cols;
        private bool warnedAboutLimit;

        // anchorWorld: the world point the lattice passes through. For a run
        // this must be the SAME point the ExternalModelGrid was given.
        public InternalModelGrid(Vector2 anchorWorld, float cellSizeMeters,
            int growthPaddingCells = RobotVacuum.Sim.SimDefaults.InternalGrowthPaddingCells,
            float maxHalfWidthMeters = RobotVacuum.Sim.SimDefaults.InternalMaxHalfWidthMeters)
        {
            this.anchorWorld = anchorWorld;
            this.cellSizeMeters = cellSizeMeters;
            this.growthPaddingCells = Mathf.Max(0, growthPaddingCells);
            maxIndex = Mathf.Max(1, Mathf.CeilToInt(maxHalfWidthMeters / cellSizeMeters));
        }

        // ---- Coordinates (lattice indices, may be negative) ---------------

        /// <summary>
        /// World position to lattice index. False only if the position is not a finite number or lies beyond
        /// the runaway guard; there is no "out of bounds", because the map has no fixed bounds.
        /// </summary>
        public bool TryWorldToGridIndex(Vector2 worldPos, out int row, out int col)
        {
            Vector2 relative = worldPos - anchorWorld;
            float c = relative.x / cellSizeMeters;
            float r = relative.y / cellSizeMeters;

            row = 0;
            col = 0;
            if (float.IsNaN(c) || float.IsNaN(r) || float.IsInfinity(c) || float.IsInfinity(r)) return false;
            if (Mathf.Abs(c) > maxIndex || Mathf.Abs(r) > maxIndex) return false;

            col = Mathf.FloorToInt(c);
            row = Mathf.FloorToInt(r);
            return true;
        }

        /// <summary>World position of a lattice cell's centre.</summary>
        public Vector2 GridIndexToWorldCenter(int row, int col) => new Vector2(
            anchorWorld.x + (col + 0.5f) * cellSizeMeters,
            anchorWorld.y + (row + 0.5f) * cellSizeMeters);

        // ---- Cells ---------------------------------------------------------

        /// <summary>The belief at a lattice index; <see cref="BeliefCell.Default"/> anywhere not yet written.</summary>
        public BeliefCell GetCell(int row, int col)
        {
            if (!IsAllocated(row, col)) return BeliefCell.Default;
            return cells[(row - minRow) * cols + (col - minCol)];
        }

        /// <summary>
        /// Writes a belief, growing the map if the cell lies outside it. False (and nothing written) if the
        /// index is beyond the runaway guard.
        /// </summary>
        public bool SetCell(int row, int col, BeliefCell value)
        {
            if (!IsAllocated(row, col) && !GrowToInclude(row, col)) return false;

            cells[(row - minRow) * cols + (col - minCol)] = value;
            return true;
        }

        // ---- Extent (what has been allocated, in lattice indices) ---------

        public bool IsEmpty => rows == 0 || cols == 0;
        public int MinRow => minRow;
        public int MinCol => minCol;
        public int Rows => rows;
        public int Cols => cols;
        public float CellSizeMeters => cellSizeMeters;
        public Vector2 AnchorWorld => anchorWorld;

        bool IsAllocated(int row, int col) =>
            row >= minRow && row < minRow + rows && col >= minCol && col < minCol + cols;

        bool GrowToInclude(int row, int col)
        {
            if (row > maxIndex || row < -maxIndex || col > maxIndex || col < -maxIndex)
            {
                if (!warnedAboutLimit)
                {
                    warnedAboutLimit = true;
                    Debug.LogWarning(
                        $"InternalModelGrid: a write at lattice index ({row}, {col}) is beyond the runaway guard " +
                        $"of {maxIndex} cells from the anchor; ignored. This guard exists to catch bad coordinates.");
                }
                return false;
            }

            int newMinRow, newMaxRow, newMinCol, newMaxCol;
            if (IsEmpty)
            {
                newMinRow = row - growthPaddingCells;
                newMaxRow = row + growthPaddingCells;
                newMinCol = col - growthPaddingCells;
                newMaxCol = col + growthPaddingCells;
            }
            else
            {
                // Only the sides that need to grow are extended (with padding).
                newMinRow = row < minRow ? row - growthPaddingCells : minRow;
                newMaxRow = row >= minRow + rows ? row + growthPaddingCells : minRow + rows - 1;
                newMinCol = col < minCol ? col - growthPaddingCells : minCol;
                newMaxCol = col >= minCol + cols ? col + growthPaddingCells : minCol + cols - 1;
            }

            int newRows = newMaxRow - newMinRow + 1;
            int newCols = newMaxCol - newMinCol + 1;

            // A fresh array must be filled with the real default: zero would mean "impassable".
            var grown = new BeliefCell[newRows * newCols];
            BeliefCell fill = BeliefCell.Default;
            for (int i = 0; i < grown.Length; i++) grown[i] = fill;

            for (int r = 0; r < rows; r++)
            {
                System.Array.Copy(cells, r * cols, grown, (minRow + r - newMinRow) * newCols + (minCol - newMinCol), cols);
            }

            cells = grown;
            minRow = newMinRow;
            minCol = newMinCol;
            rows = newRows;
            cols = newCols;
            return true;
        }
    }
}
