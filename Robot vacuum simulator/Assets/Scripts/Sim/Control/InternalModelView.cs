using System;
using System.Collections.Generic;
using RobotVacuum.AlgorithmApi;
using RobotVacuumSim.EnvironmentModel;
using UnityEngine;

namespace RobotVacuum.Sim.Control
{
    /// <summary>
    /// What an algorithm sees of the Internal Model: the LIVE grid, read-only, with nothing copied (design doc 4.1).
    /// It has no setters, so an algorithm cannot change a belief; the controller is the only writer.
    ///
    ///   * Single-cell reads and world/cell conversion; the extent of what has been written so far.
    ///   * Batch reads of a region at a block size the algorithm chooses, into a buffer the algorithm owns
    ///     and reuses (no allocation per call).
    ///   * A change feed: the cells whose traversability or vacuum resistance changed since the last call.
    ///     Read the whole map once at the start (the Normal preload is NOT in the feed), then follow the feed.
    ///   * Reachability from the robot's believed position: a cached flood fill over passable cells,
    ///     recomputed only when a cell has flipped between blocked and passable. This is also what keeps
    ///     unexplored space outside the rooms off-limits (design doc 7).
    ///   * Believed line of sight between two points.
    ///
    /// Cells never written read as <see cref="BeliefCell.Default"/>: passable, normal floor, nothing done.
    /// </summary>
    public sealed class InternalModelView
    {
        readonly InternalModelWriter writer;
        readonly InternalModelGrid grid;

        // Reachability cache.
        bool[] reached;
        int reachedMinRow, reachedMinCol, reachedRows, reachedCols;
        int reachedVersion = -1;
        bool reachesBeyondKnownMap;
        int[] floodQueue;

        RobotPose robotPose;

        Func<int, int, bool> explored; // null = everything explored (a Normal run)

        /// <summary>Where "explored" comes from: SLAM passes the observation mapper's drawn cells; null = everything is explored.</summary>
        public void SetExploredSource(Func<int, int, bool> source) => explored = source;

        /// <summary>True if the robot has sensed the cell (always, in a Normal run).</summary>
        public bool IsExplored(int row, int col) => explored == null || explored(row, col);

        public InternalModelView(InternalModelWriter writer)
        {
            this.writer = writer ?? throw new ArgumentNullException(nameof(writer));
            grid = writer.Grid;
        }

        // ---- Geometry --------------------------------------------------------

        public float CellSizeMeters => grid.CellSizeMeters;
        public Vector2 AnchorWorld => grid.AnchorWorld;

        /// <summary>The robot's believed pose as of this physics step (the same value the algorithm is handed).</summary>
        public RobotPose BelievedPose => robotPose;

        public bool TryWorldToCell(Vector2 world, out CellIndex cell)
        {
            bool ok = grid.TryWorldToGridIndex(world, out int row, out int col);
            cell = new CellIndex(row, col);
            return ok;
        }

        public Vector2 CellCenter(CellIndex cell) => grid.GridIndexToWorldCenter(cell.row, cell.col);
        public Vector2 CellCenter(int row, int col) => grid.GridIndexToWorldCenter(row, col);

        // ---- Extent of what has been written ---------------------------------

        /// <summary>True until the first belief is written (always the case at the start of a SLAM run).</summary>
        public bool IsEmpty => grid.IsEmpty;
        public int MinRow => grid.MinRow;
        public int MinCol => grid.MinCol;
        public int Rows => grid.Rows;
        public int Cols => grid.Cols;

        // ---- Single-cell reads -----------------------------------------------

        /// <summary>A copy of the cell's beliefs (a struct copy: changing it changes nothing in the model).</summary>
        public BeliefCell GetCell(int row, int col) => grid.GetCell(row, col);
        public BeliefCell GetCell(CellIndex cell) => grid.GetCell(cell.row, cell.col);

        public float Traversability(int row, int col) => grid.GetCell(row, col).traversabilityBelief;

        /// <summary>True when the cell is believed impassable (traversability exactly 0).</summary>
        public bool IsBlocked(int row, int col) => grid.GetCell(row, col).traversabilityBelief <= 0f;

        public bool IsBlockedAt(Vector2 world) =>
            grid.TryWorldToGridIndex(world, out int row, out int col) && IsBlocked(row, col);

        // ---- Batch reads -------------------------------------------------------

        /// <summary>Buffer length <see cref="ReadRegion"/> needs for a region and block size.</summary>
        public static int RegionBufferLength(int rows, int cols, int blockSize) => BeliefRegion.BufferLength(rows, cols, blockSize);

        /// <summary>
        /// Reads a rectangle of cells, <paramref name="blockSize"/> × <paramref name="blockSize"/> cells per
        /// value, into <paramref name="buffer"/> row by row (lowest row first). Traversability gives each block's
        /// MINIMUM (one blocked cell makes the block blocked, the safe reading for a planner); the other fields give
        /// the block's mean. Returns the number of values written. The buffer must be at least
        /// <see cref="RegionBufferLength"/> long; reuse it between calls.
        /// </summary>
        public int ReadRegion(int minRow, int minCol, int rows, int cols, int blockSize, BeliefField field, float[] buffer)
        {
            if (rows <= 0 || cols <= 0) return 0;
            blockSize = Mathf.Max(1, blockSize);

            int blockRows = (rows + blockSize - 1) / blockSize;
            int blockCols = (cols + blockSize - 1) / blockSize;
            int needed = blockRows * blockCols;
            if (buffer == null || buffer.Length < needed)
                throw new ArgumentException($"ReadRegion needs a buffer of at least {needed} values (see RegionBufferLength).", nameof(buffer));

            for (int br = 0; br < blockRows; br++)
            {
                int r0 = minRow + br * blockSize;
                int r1 = Mathf.Min(r0 + blockSize, minRow + rows);

                for (int bc = 0; bc < blockCols; bc++)
                {
                    int c0 = minCol + bc * blockSize;
                    int c1 = Mathf.Min(c0 + blockSize, minCol + cols);

                    float min = float.PositiveInfinity;
                    float sum = 0f;
                    int count = 0;

                    for (int r = r0; r < r1; r++)
                    {
                        for (int c = c0; c < c1; c++)
                        {
                            var cell = grid.GetCell(r, c);
                            float value = field switch
                            {
                                BeliefField.Explored => IsExplored(r, c) ? 1f : 0f,
                                BeliefField.Traversability => cell.traversabilityBelief,
                                BeliefField.VacuumResistance => cell.vacuumResistanceBelief,
                                BeliefField.Dwell => cell.dwellBelief,
                                _ => cell.cleanlinessBelief,
                            };
                            if (value < min) min = value;
                            sum += value;
                            count++;
                        }
                    }

                    buffer[br * blockCols + bc] = field == BeliefField.Traversability ? min : sum / Mathf.Max(1, count);
                }
            }

            return needed;
        }

        // ---- Change feed -------------------------------------------------------

        /// <summary>Goes up whenever a reported change happens. Compare with the last value seen to know if anything changed.</summary>
        public int ChangeCounter => writer.ChangeCounter;

        /// <summary>Goes up whenever a cell flips between impassable and passable (what a planner's cache depends on).</summary>
        public int TopologyVersion => writer.TopologyVersion;

        /// <summary>
        /// Fills <paramref name="into"/> (cleared first) with the cells whose traversability or vacuum resistance
        /// changed since the last call. Returns false when too much changed to list: rescan the whole map instead.
        /// </summary>
        public bool TakeChangedCells(List<CellIndex> into) => writer.TakeChanges(into);

        // ---- Reachability ------------------------------------------------------

        /// <summary>
        /// True if a path of passable cells (4-connected, traversability above 0) leads from the robot's believed
        /// cell to this one. Cells outside the written map count as reachable only if the passable region runs
        /// off the map's edge (an opening into unexplored space). Note this is CELL connectivity: a gap narrower
        /// than the robot still connects. The flood fill is cached and redone only when a cell flipped between
        /// blocked and passable, the map grew, or the robot is outside the region found last time.
        /// </summary>
        public bool IsReachable(int row, int col)
        {
            if (grid.IsEmpty) return true; // nothing known yet: nothing rules it out
            EnsureReachability();

            if (row < reachedMinRow || row >= reachedMinRow + reachedRows || col < reachedMinCol || col >= reachedMinCol + reachedCols)
                return reachesBeyondKnownMap;

            return reached[(row - reachedMinRow) * reachedCols + (col - reachedMinCol)];
        }

        public bool IsReachable(Vector2 world) =>
            grid.TryWorldToGridIndex(world, out int row, out int col) && IsReachable(row, col);

        void EnsureReachability()
        {
            bool boundsChanged = reached == null || reachedMinRow != grid.MinRow || reachedMinCol != grid.MinCol ||
                                 reachedRows != grid.Rows || reachedCols != grid.Cols;

            bool robotOutside = false;
            if (!boundsChanged && grid.TryWorldToGridIndex(robotPose.position, out int rr, out int rc))
            {
                bool inside = rr >= reachedMinRow && rr < reachedMinRow + reachedRows && rc >= reachedMinCol && rc < reachedMinCol + reachedCols;
                robotOutside = !inside || !reached[(rr - reachedMinRow) * reachedCols + (rc - reachedMinCol)];
            }

            if (!boundsChanged && !robotOutside && reachedVersion == writer.TopologyVersion) return;

            Flood();
        }

        void Flood()
        {
            reachedMinRow = grid.MinRow;
            reachedMinCol = grid.MinCol;
            reachedRows = grid.Rows;
            reachedCols = grid.Cols;
            reachedVersion = writer.TopologyVersion;
            reachesBeyondKnownMap = false;

            int total = reachedRows * reachedCols;
            if (reached == null || reached.Length < total) reached = new bool[total];
            else Array.Clear(reached, 0, total);
            if (floodQueue == null || floodQueue.Length < total) floodQueue = new int[total];

            if (!grid.TryWorldToGridIndex(robotPose.position, out int startRow, out int startCol)) return;

            int sr = startRow - reachedMinRow;
            int sc = startCol - reachedMinCol;
            if (sr < 0 || sr >= reachedRows || sc < 0 || sc >= reachedCols)
            {
                // The robot is off the written map, in unexplored space: everything off the map is reachable.
                reachesBeyondKnownMap = true;
                return;
            }

            // The robot's own cell counts as reachable even if it is (wrongly) believed blocked.
            int head = 0, tail = 0;
            int start = sr * reachedCols + sc;
            reached[start] = true;
            floodQueue[tail++] = start;

            while (head < tail)
            {
                int index = floodQueue[head++];
                int r = index / reachedCols;
                int c = index - r * reachedCols;

                if (r == 0 || c == 0 || r == reachedRows - 1 || c == reachedCols - 1) reachesBeyondKnownMap = true;

                if (r > 0) Visit(index - reachedCols, r - 1, c, ref tail);
                if (r < reachedRows - 1) Visit(index + reachedCols, r + 1, c, ref tail);
                if (c > 0) Visit(index - 1, r, c - 1, ref tail);
                if (c < reachedCols - 1) Visit(index + 1, r, c + 1, ref tail);
            }
        }

        void Visit(int index, int localRow, int localCol, ref int tail)
        {
            if (reached[index]) return;
            if (grid.GetCell(localRow + reachedMinRow, localCol + reachedMinCol).traversabilityBelief <= 0f) return;

            reached[index] = true;
            floodQueue[tail++] = index;
        }

        // ---- Line of sight -----------------------------------------------------

        /// <summary>
        /// True if no cell believed impassable lies on the grid line between the two points (the start cell is
        /// ignored, so a robot standing in a wrongly-blocked cell can still see out).
        /// </summary>
        public bool HasLineOfSight(Vector2 from, Vector2 to)
        {
            if (!grid.TryWorldToGridIndex(from, out int r0, out int c0)) return false;
            if (!grid.TryWorldToGridIndex(to, out int r1, out int c1)) return false;

            bool first = true;
            foreach (var cell in GridLine.Cells(r0, c0, r1, c1))
            {
                if (first)
                {
                    first = false;
                    continue;
                }
                if (IsBlocked(cell.row, cell.col)) return false;
            }
            return true;
        }

        /// <summary>
        /// Distance from <paramref name="from"/> along <paramref name="direction"/> to the first cell believed
        /// impassable, capped at <paramref name="maxDistance"/>. The start cell is ignored.
        /// </summary>
        public float FreeDistance(Vector2 from, Vector2 direction, float maxDistance)
        {
            if (direction.sqrMagnitude < 1e-12f) return 0f;
            direction.Normalize();

            float step = CellSizeMeters * 0.5f;
            if (!grid.TryWorldToGridIndex(from, out int startRow, out int startCol)) return 0f;

            for (float d = step; d <= maxDistance; d += step)
            {
                if (!grid.TryWorldToGridIndex(from + direction * d, out int row, out int col)) return d;
                if (row == startRow && col == startCol) continue;
                if (IsBlocked(row, col)) return Mathf.Max(0f, d - step);
            }
            return maxDistance;
        }

        // ---- Controller side ---------------------------------------------------

        /// <summary>Called by the controller once per step, before the algorithm is asked anything.</summary>
        internal void SetBelievedPose(RobotPose pose) => robotPose = pose;
    }
}
