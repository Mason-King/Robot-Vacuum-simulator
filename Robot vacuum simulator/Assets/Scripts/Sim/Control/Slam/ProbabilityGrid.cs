using System;
using System.Collections.Generic;
using RobotVacuum.AlgorithmApi;
using UnityEngine;
using S = RobotVacuum.Sim.Control.LocalSlamConstants;

namespace RobotVacuum.Sim.Control
{
    /// <summary>
    /// SLAM ONLY: one submap's map, a probability grid (design doc, section 19; Cartographer's ProbabilityGrid and
    /// ProbabilityGridRangeDataInserter2D). Each cell holds the probability that it is occupied, or "unknown".
    ///
    /// Inserting a scan, from the believed pose:
    ///   * each HIT cell (where a return landed): unknown → <see cref="S.HitProbability"/>; known → its odds are
    ///     multiplied by the hit odds;
    ///   * each cell a ray passes through before its return (or along a no-return ray): the same with the miss odds;
    ///   * a cell is updated at most once per scan, hits first, so a scan never clears a cell it also hit;
    ///   * probabilities stay within [<see cref="S.MinProbability"/>, <see cref="S.MaxProbability"/>].
    ///
    /// The grid sits on the Internal Model's lattice (same anchor, same cell size) in the frame local SLAM tracks in,
    /// and grows on write. Rays are traced exactly (every cell the line from the robot to the return crosses).
    /// </summary>
    public sealed class ProbabilityGrid
    {
        readonly Vector2 anchor;
        readonly float cellSize;
        readonly int padding;
        readonly int maxIndex;

        // Row-major over [minRow, minRow + rows) × [minCol, minCol + cols). 0 = unknown.
        internal float[] probability = new float[0];
        int[] stamp = new int[0];

        internal int minRow, minCol, rows, cols;
        int scanId;

        static float HitProbability => S.HitProbability;
        static float MissProbability => S.MissProbability;
        static float HitOdds => Odds(HitProbability);
        static float MissOdds => Odds(MissProbability);

        public ProbabilityGrid(Vector2 anchorWorld, float cellSizeMeters,
            int growthPaddingCells = SimDefaults.InternalGrowthPaddingCells,
            float maxHalfWidthMeters = SimDefaults.InternalMaxHalfWidthMeters)
        {
            anchor = anchorWorld;
            cellSize = cellSizeMeters;
            padding = Mathf.Max(0, growthPaddingCells);
            maxIndex = Mathf.Max(1, Mathf.CeilToInt(maxHalfWidthMeters / cellSizeMeters));
        }

        public Vector2 AnchorWorld => anchor;
        public float CellSizeMeters => cellSize;

        /// <summary>Scans inserted so far.</summary>
        public int ScanCount { get; private set; }

        public bool TryCell(Vector2 point, out int row, out int col)
        {
            float c = (point.x - anchor.x) / cellSize;
            float r = (point.y - anchor.y) / cellSize;
            row = 0;
            col = 0;
            if (float.IsNaN(c) || float.IsNaN(r) || Mathf.Abs(c) > maxIndex || Mathf.Abs(r) > maxIndex) return false;
            col = Mathf.FloorToInt(c);
            row = Mathf.FloorToInt(r);
            return true;
        }

        /// <summary>The cell's probability of being occupied, or 0 if unknown.</summary>
        public float Probability(int row, int col) => TryIndex(row, col, out int i) ? probability[i] : 0f;

        public bool IsKnown(int row, int col) => Probability(row, col) > 0f;

        // ---- Inserting a scan -----------------------------------------------------------------------------

        /// <summary>
        /// Inserts one scan taken from <paramref name="pose"/>, with no range limit or free-space margin (tests).
        /// </summary>
        public void Insert(RobotPose pose, IReadOnlyList<Sensors.LidarRay> rays, float noReturnLength, List<long> changed) =>
            Insert(pose, rays, noReturnLength, float.PositiveInfinity, 0f, changed);

        /// <summary>
        /// Inserts one scan taken from <paramref name="pose"/>. Rays are given robot-relative (bearing, range, hit).
        /// Returns beyond <paramref name="maxRange"/> aren't mapped and free space stops there; a no-return ray is free
        /// for <paramref name="noReturnLength"/>; free space along a ray that hit stops <paramref name="marginPerSigma"/>
        /// × σ(range) short of the return, σ being the lidar's range noise at that range at noise 1. Every cell whose
        /// probability changed is added to <paramref name="changed"/> (packed with <see cref="CellIndex.Pack"/>).
        /// </summary>
        public void Insert(RobotPose pose, IReadOnlyList<Sensors.LidarRay> rays, float noReturnLength, float maxRange,
            float marginPerSigma, List<long> changed) =>
            Insert(pose, rays, noReturnLength, maxRange, marginPerSigma, 0f, changed);

        /// <param name="marginFromRange">The free-space margin applies only to returns further than this (0 = all).</param>
        public void Insert(RobotPose pose, IReadOnlyList<Sensors.LidarRay> rays, float noReturnLength, float maxRange,
            float marginPerSigma, float marginFromRange, List<long> changed) =>
            Insert(pose, rays, noReturnLength, maxRange, marginPerSigma, marginFromRange, changed, null);

        /// <param name="record">If given, every cell this scan touches is recorded in order, so another grid on the
        /// same lattice can take the same scan with <see cref="Replay"/> instead of tracing it again.</param>
        internal void Insert(RobotPose pose, IReadOnlyList<Sensors.LidarRay> rays, float noReturnLength, float maxRange,
            float marginPerSigma, float marginFromRange, List<long> changed, ScanCells record)
        {
            scanId++;
            ScanCount++;
            record?.Clear();
            float hitOdds = HitOdds, hitProbability = HitProbability;
            float missOdds = MissOdds, missProbability = MissProbability;

            // Hits first: each hit cell is raised once, and no ray of this scan may lower it.
            for (int i = 0; i < rays.Count; i++)
            {
                if (!rays[i].hit || rays[i].range > maxRange) continue;
                Vector2 end = pose.position + RobotPose.DirectionOf(pose.heading + rays[i].bearing) * rays[i].range;
                if (!TryCell(end, out int row, out int col) || !Ensure(row, col, out int index)) continue;
                if (stamp[index] == scanId)
                {
                    record?.Add(row, col, ScanCells.HitAgain, false);
                    continue;
                }
                stamp[index] = scanId;
                bool didChange = Update(index, row, col, hitOdds, hitProbability, changed);
                record?.Add(row, col, ScanCells.Hit, didChange);
            }

            // Then the free space each ray passed through.
            for (int i = 0; i < rays.Count; i++)
            {
                float length;
                if (rays[i].hit)
                {
                    float sigma = Sensors.SensorNoiseProfile.LidarRangeSigmaMeters + Sensors.SensorNoiseProfile.LidarRangeSigmaFraction * rays[i].range;
                    length = rays[i].range > marginFromRange ? rays[i].range - marginPerSigma * sigma : rays[i].range;
                }
                else
                {
                    length = Mathf.Min(noReturnLength, rays[i].range);
                }
                length = Mathf.Min(length, maxRange);
                if (length <= 0f) continue;

                Vector2 end = pose.position + RobotPose.DirectionOf(pose.heading + rays[i].bearing) * length;
                // The last cell a ray that hit reaches is never cleared by it (as measured: see FreeSpaceMarginSigmas).
                TraceMisses(pose.position, end, includeEnd: !rays[i].hit, missOdds, missProbability, changed, record);
            }
        }

        /// <summary>True if this grid and <paramref name="other"/> share a lattice (same anchor, cell size and limit), so
        /// a scan's cells in one are its cells in the other.</summary>
        internal bool SameLattice(ProbabilityGrid other) =>
            anchor.x == other.anchor.x && anchor.y == other.anchor.y && cellSize == other.cellSize && maxIndex == other.maxIndex;

        /// <summary>
        /// Takes the scan another grid on the same lattice just inserted with a <paramref name="record"/>: exactly as
        /// <see cref="Insert(RobotPose, IReadOnlyList{Sensors.LidarRay}, float, float, float, float, List{long})"/>
        /// would (the same cells, updated in the same order, the hit sums added in the same order), without tracing
        /// the rays again. A cell is added to <paramref name="changed"/> only if it changed here and not in the grid
        /// that recorded the scan, which is what de-duplicating the two grids' changes kept.
        /// </summary>
        internal void Replay(ScanCells record, List<long> changed)
        {
            scanId++;
            ScanCount++;
            float hitOdds = HitOdds, hitProbability = HitProbability;
            float missOdds = MissOdds, missProbability = MissProbability;

            var cells = record.cells;
            for (int i = 0; i < record.count; i++)
            {
                var cell = cells[i];
                // Never fails: the recording grid ensured this cell, and the limit is the same.
                if (!Ensure(cell.row, cell.col, out int index)) continue;
                stamp[index] = scanId;
                if (cell.kind == ScanCells.Miss)
                {
                    Update(index, cell.row, cell.col, missOdds, missProbability, cell.changedThere ? null : changed);
                    continue;
                }
                if (cell.kind == ScanCells.Hit)
                    Update(index, cell.row, cell.col, hitOdds, hitProbability, cell.changedThere ? null : changed);
            }
        }

        /// <summary>The cells one scan touched in a grid, in order (see <see cref="Replay"/>).</summary>
        internal sealed class ScanCells
        {
            internal const byte Hit = 0, HitAgain = 1, Miss = 2;

            internal struct Cell
            {
                public int row, col;
                public byte kind;
                public bool changedThere;     // the recording grid's probability changed
            }

            internal Cell[] cells = new Cell[4096];
            internal int count;

            internal void Clear() => count = 0;

            internal void Add(int row, int col, byte kind, bool changedThere)
            {
                if (count == cells.Length) Array.Resize(ref cells, cells.Length * 2);
                cells[count++] = new Cell { row = row, col = col, kind = kind, changedThere = changedThere };
            }
        }

        /// <summary>A certain obstacle (a bump): the cell goes to the maximum probability.</summary>
        public void SetCertain(Vector2 point, List<long> changed)
        {
            if (!TryCell(point, out int row, out int col) || !Ensure(row, col, out int index)) return;
            if (probability[index] == S.MaxProbability) return;
            probability[index] = S.MaxProbability;
            changed?.Add(CellIndex.Pack(row, col));
        }

        bool Update(int index, int row, int col, float odds, float first, List<long> changed)
        {
            float old = probability[index];
            float next;
            if (old <= 0f)
            {
                next = first;
            }
            else
            {
                float o = Odds(old) * odds;
                next = Mathf.Clamp(o / (1f + o), S.MinProbability, S.MaxProbability);
            }
            if (next == old) return false;
            probability[index] = next;
            changed?.Add(CellIndex.Pack(row, col));
            return true;
        }

        /// <summary>Every cell the segment from <paramref name="from"/> to <paramref name="to"/> crosses (Amanatides–Woo), as misses.</summary>
        void TraceMisses(Vector2 from, Vector2 to, bool includeEnd, float missOdds, float missProbability,
            List<long> changed, ScanCells record)
        {
            if (!TryCell(from, out int row, out int col) || !TryCell(to, out int endRow, out int endCol)) return;

            float x0 = (from.x - anchor.x) / cellSize, y0 = (from.y - anchor.y) / cellSize;
            float dx = (to.x - anchor.x) / cellSize - x0, dy = (to.y - anchor.y) / cellSize - y0;

            int stepCol = dx > 0 ? 1 : -1, stepRow = dy > 0 ? 1 : -1;
            float tDeltaX = dx != 0 ? Mathf.Abs(1f / dx) : float.PositiveInfinity;
            float tDeltaY = dy != 0 ? Mathf.Abs(1f / dy) : float.PositiveInfinity;
            float tMaxX = dx != 0 ? ((dx > 0 ? col + 1 - x0 : x0 - col) * tDeltaX) : float.PositiveInfinity;
            float tMaxY = dy != 0 ? ((dy > 0 ? row + 1 - y0 : y0 - row) * tDeltaY) : float.PositiveInfinity;

            int guard = Mathf.Abs(endRow - row) + Mathf.Abs(endCol - col) + 2;
            for (int n = 0; n < guard; n++)
            {
                bool atEnd = row == endRow && col == endCol;
                if (!atEnd || includeEnd)
                {
                    // Ensure, with its common case (the cell is already inside the grid) written out: this loop runs
                    // for every cell of every ray, and most of those cells were already visited by an earlier ray.
                    int r = row - minRow, c = col - minCol, index;
                    bool inside;
                    if ((uint)r < (uint)rows && (uint)c < (uint)cols)
                    {
                        index = r * cols + c;
                        inside = true;
                    }
                    else inside = Ensure(row, col, out index);
                    if (inside && stamp[index] != scanId)
                    {
                        stamp[index] = scanId;
                        bool didChange = Update(index, row, col, missOdds, missProbability, changed);
                        record?.Add(row, col, ScanCells.Miss, didChange);
                    }
                }
                if (atEnd) return;

                if (tMaxX < tMaxY)
                {
                    col += stepCol;
                    tMaxX += tDeltaX;
                }
                else
                {
                    row += stepRow;
                    tMaxY += tDeltaY;
                }
            }
        }

        static float Odds(float p) => p / (1f - p);

        // ---- Storage -----------------------------------------------------------------------------------------

        internal bool TryIndex(int row, int col, out int index)
        {
            int r = row - minRow, c = col - minCol;
            if (r < 0 || c < 0 || r >= rows || c >= cols)
            {
                index = -1;
                return false;
            }
            index = r * cols + c;
            return true;
        }

        bool Ensure(int row, int col, out int index)
        {
            if (TryIndex(row, col, out index)) return true;
            if (Mathf.Abs(row) > maxIndex || Mathf.Abs(col) > maxIndex) return false;
            Grow(row, col);
            return TryIndex(row, col, out index);
        }

        void Grow(int row, int col)
        {
            int newMinRow, newMaxRow, newMinCol, newMaxCol;
            if (rows == 0 || cols == 0)
            {
                newMinRow = row - padding;
                newMaxRow = row + padding;
                newMinCol = col - padding;
                newMaxCol = col + padding;
            }
            else
            {
                newMinRow = row < minRow ? row - padding : minRow;
                newMaxRow = row >= minRow + rows ? row + padding : minRow + rows - 1;
                newMinCol = col < minCol ? col - padding : minCol;
                newMaxCol = col >= minCol + cols ? col + padding : minCol + cols - 1;
            }

            int newRows = newMaxRow - newMinRow + 1, newCols = newMaxCol - newMinCol + 1;
            probability = Regrow(probability, newMinRow, newMinCol, newRows, newCols);
            stamp = Regrow(stamp, newMinRow, newMinCol, newRows, newCols);
            minRow = newMinRow;
            minCol = newMinCol;
            rows = newRows;
            cols = newCols;
        }

        T[] Regrow<T>(T[] old, int newMinRow, int newMinCol, int newRows, int newCols)
        {
            var grown = new T[newRows * newCols];
            for (int r = 0; r < rows; r++)
                Array.Copy(old, r * cols, grown, (minRow + r - newMinRow) * newCols + (minCol - newMinCol), cols);
            return grown;
        }
    }
}
