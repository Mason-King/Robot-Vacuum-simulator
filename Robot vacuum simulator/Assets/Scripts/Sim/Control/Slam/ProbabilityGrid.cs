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

        // Where hits actually landed, per cell: their count and the sum of their positions (relative to the anchor).
        // The hit-field matcher scores scan points against these instead of against cell centres.
        internal int[] hitCount = new int[0];
        internal double[] hitSumX = new double[0], hitSumY = new double[0];
        internal int minRow, minCol, rows, cols;
        int scanId;

        // ---- Diagnostics only (tests): hit/miss probability experiments. 0 = the constants. ----
        internal static float HitProbabilityOverride = 0f;
        internal static float MissProbabilityOverride = 0f;

        static float HitProbability => HitProbabilityOverride > 0f ? HitProbabilityOverride : S.HitProbability;
        static float MissProbability => MissProbabilityOverride > 0f ? MissProbabilityOverride : S.MissProbability;
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
            float marginPerSigma, float marginFromRange, List<long> changed)
        {
            scanId++;
            ScanCount++;

            // Hits first: each hit cell is raised once, and no ray of this scan may lower it.
            for (int i = 0; i < rays.Count; i++)
            {
                if (!rays[i].hit || rays[i].range > maxRange) continue;
                Vector2 end = pose.position + RobotPose.DirectionOf(pose.heading + rays[i].bearing) * rays[i].range;
                if (!TryCell(end, out int row, out int col) || !Ensure(row, col, out int index)) continue;
                hitCount[index]++;
                hitSumX[index] += end.x - anchor.x;
                hitSumY[index] += end.y - anchor.y;
                if (stamp[index] == scanId) continue;
                stamp[index] = scanId;
                Update(index, row, col, HitOdds, HitProbability, changed);
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
                TraceMisses(pose.position, end, includeEnd: !rays[i].hit, changed);
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

        void Update(int index, int row, int col, float odds, float first, List<long> changed)
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
            if (next == old) return;
            probability[index] = next;
            changed?.Add(CellIndex.Pack(row, col));
        }

        /// <summary>Every cell the segment from <paramref name="from"/> to <paramref name="to"/> crosses (Amanatides–Woo), as misses.</summary>
        void TraceMisses(Vector2 from, Vector2 to, bool includeEnd, List<long> changed)
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
                    if (Ensure(row, col, out int index) && stamp[index] != scanId)
                    {
                        stamp[index] = scanId;
                        Update(index, row, col, MissOdds, MissProbability, changed);
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

        /// <summary>Forgets every observation (the extent is kept): for rebuilding the grid from its scans.</summary>
        public void Clear()
        {
            Array.Clear(probability, 0, probability.Length);
            Array.Clear(hitCount, 0, hitCount.Length);
            Array.Clear(hitSumX, 0, hitSumX.Length);
            Array.Clear(hitSumY, 0, hitSumY.Length);
            ScanCount = 0;
        }

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
            hitCount = Regrow(hitCount, newMinRow, newMinCol, newRows, newCols);
            hitSumX = Regrow(hitSumX, newMinRow, newMinCol, newRows, newCols);
            hitSumY = Regrow(hitSumY, newMinRow, newMinCol, newRows, newCols);
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
