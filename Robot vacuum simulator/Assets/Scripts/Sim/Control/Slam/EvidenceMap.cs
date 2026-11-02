using System;
using UnityEngine;
using E = RobotVacuum.Sim.Control.EvidenceConstants;

namespace RobotVacuum.Sim.Control
{
    /// <summary>
    /// SLAM ONLY: the robot's evidence map (design doc, section 17). It sits on the same lattice as the Internal Model
    /// (same anchor, same cell size) and is owned by the controller -- it is not a field of <c>BeliefCell</c>.
    ///
    /// Each cell keeps:
    ///   * an EVIDENCE score: 0 = unknown, positive = seen blocked, negative = seen clear. A hit adds
    ///     <see cref="E.HitStep"/>; a clear ray subtracts <see cref="E.ClearNear"/> within 1 m of the robot or
    ///     <see cref="E.ClearFar"/> beyond -- but a far clear ray never lowers a CONFIRMED wall (score ≥
    ///     <see cref="E.Confirmed"/>). That is what stops the map redrawing itself around a drifting robot.
    ///   * WHERE the hits in it actually landed (their mean position), so pose correction can score against the wall's
    ///     real surface instead of the cell's centre (a half-cell bias that otherwise ratchets the pose).
    ///
    /// Each scan updates a cell at most once for clearing (the many rays near the robot don't stack), and a cell hit
    /// in a scan is not cleared by that same scan. Grows on write like <see cref="RobotVacuumSim.EnvironmentModel.InternalModelGrid"/>.
    /// </summary>
    public sealed class EvidenceMap
    {
        readonly Vector2 anchor;
        readonly float cellSize;
        readonly int padding;
        readonly int maxIndex;

        // Row-major over [minRow, minRow + rows) × [minCol, minCol + cols).
        internal float[] evidence = new float[0];
        internal float[] meanX = new float[0];   // mean hit position, world metres RELATIVE to the anchor
        internal float[] meanY = new float[0];
        internal int[] hitCount = new int[0];
        int[] clearStamp = new int[0];
        int[] hitStamp = new int[0];
        internal int minRow, minCol, rows, cols;
        int scanId = 1; // stamps start at 0, so the first scan never looks already-applied

        public EvidenceMap(Vector2 anchorWorld, float cellSizeMeters,
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
        public bool IsEmpty => rows == 0 || cols == 0;

        public bool TryCell(Vector2 world, out int row, out int col)
        {
            float c = (world.x - anchor.x) / cellSize;
            float r = (world.y - anchor.y) / cellSize;
            row = 0;
            col = 0;
            if (float.IsNaN(c) || float.IsNaN(r) || Mathf.Abs(c) > maxIndex || Mathf.Abs(r) > maxIndex) return false;
            col = Mathf.FloorToInt(c);
            row = Mathf.FloorToInt(r);
            return true;
        }

        // ---- Reads ---------------------------------------------------------------------------------------

        public float Evidence(int row, int col) => TryIndex(row, col, out int i) ? evidence[i] : 0f;

        public bool IsConfirmed(int row, int col) => Evidence(row, col) >= E.Confirmed;

        public int HitCount(int row, int col) => TryIndex(row, col, out int i) ? hitCount[i] : 0;

        /// <summary>Where the hits in this cell landed (world metres), if it is a confirmed wall. A confirmed cell with no lidar hits (a bump) uses its centre.</summary>
        public bool TryGetHitPoint(int row, int col, out Vector2 point)
        {
            point = default;
            if (!TryIndex(row, col, out int i) || evidence[i] < E.Confirmed) return false;

            point = hitCount[i] > 0
                ? new Vector2(anchor.x + meanX[i], anchor.y + meanY[i])
                : new Vector2(anchor.x + (col + 0.5f) * cellSize, anchor.y + (row + 0.5f) * cellSize);
            return true;
        }

        /// <summary>The traversability an algorithm sees for an evidence score: confirmed wall = 0, unknown or seen clear = the floor's ease, in between = shaded.</summary>
        public static float Traversability(float score, float ease)
        {
            if (score >= E.Confirmed) return 0f;
            if (score <= 0f) return ease;
            return ease * (1f - score / E.Confirmed);
        }

        // ---- Writes ----------------------------------------------------------------------------------------

        /// <summary>Starts a new scan: clears from it apply at most once per cell, and never to a cell it hit.</summary>
        public int BeginScan() => ++scanId;

        /// <summary>Marks a cell as hit by the current scan (call for every hit before applying any clears).</summary>
        public void MarkHit(int row, int col)
        {
            if (Ensure(row, col, out int i)) hitStamp[i] = scanId;
        }

        /// <summary>A lidar hit landed at <paramref name="worldPoint"/> in this cell. Returns the new score.</summary>
        public float AddHit(int row, int col, Vector2 worldPoint)
        {
            if (!Ensure(row, col, out int i)) return 0f;

            evidence[i] = Mathf.Min(E.Max, evidence[i] + E.HitStep);

            int n = ++hitCount[i];
            meanX[i] += (worldPoint.x - anchor.x - meanX[i]) / n;
            meanY[i] += (worldPoint.y - anchor.y - meanY[i]) / n;
            return evidence[i];
        }

        /// <summary>
        /// A clear ray passed through the cell at <paramref name="distance"/> from the robot. Returns true if the score
        /// changed (at most once per cell per scan; never for a cell hit in this scan; never lowers a confirmed wall from
        /// beyond <see cref="E.NearRangeMeters"/>).
        /// </summary>
        public bool Clear(int row, int col, float distance)
        {
            if (!Ensure(row, col, out int i)) return false;
            if (clearStamp[i] == scanId || hitStamp[i] == scanId) return false;
            clearStamp[i] = scanId;

            bool near = distance <= E.NearRangeMeters;
            float old = evidence[i];
            if (!near && old >= E.Confirmed) return false;

            float next = Mathf.Max(E.Min, old - (near ? E.ClearNear : E.ClearFar));
            evidence[i] = next;
            if (next <= 0f) ForgetHits(i);
            return next != old;
        }

        /// <summary>A certain obstacle (a bump): the score goes to the maximum.</summary>
        public void SetCertain(int row, int col)
        {
            if (Ensure(row, col, out int i)) evidence[i] = E.Max;
        }

        /// <summary>Certainly free (the robot is standing on it): the score goes to the minimum. Returns true if it changed.</summary>
        public bool SetFree(int row, int col)
        {
            if (!TryIndex(row, col, out int i) || evidence[i] <= E.Min) return false;
            evidence[i] = E.Min;
            ForgetHits(i);
            return true;
        }

        void ForgetHits(int i)
        {
            hitCount[i] = 0;
            meanX[i] = 0f;
            meanY[i] = 0f;
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
            if (IsEmpty)
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

            int newRows = newMaxRow - newMinRow + 1;
            int newCols = newMaxCol - newMinCol + 1;

            evidence = Regrow(evidence, newMinRow, newMinCol, newRows, newCols);
            meanX = Regrow(meanX, newMinRow, newMinCol, newRows, newCols);
            meanY = Regrow(meanY, newMinRow, newMinCol, newRows, newCols);
            hitCount = Regrow(hitCount, newMinRow, newMinCol, newRows, newCols);
            clearStamp = Regrow(clearStamp, newMinRow, newMinCol, newRows, newCols);
            hitStamp = Regrow(hitStamp, newMinRow, newMinCol, newRows, newCols);

            minRow = newMinRow;
            minCol = newMinCol;
            rows = newRows;
            cols = newCols;
        }

        T[] Regrow<T>(T[] old, int newMinRow, int newMinCol, int newRows, int newCols)
        {
            var grown = new T[newRows * newCols]; // zero = unknown evidence, no hits, no stamps: the right default here
            for (int r = 0; r < rows; r++)
                Array.Copy(old, r * cols, grown, (minRow + r - newMinRow) * newCols + (minCol - newMinCol), cols);
            return grown;
        }
    }
}
