using System;
using RobotVacuum.AlgorithmApi;

namespace RobotVacuum.Sim.Control
{
    /// <summary>
    /// A copy of the belief map's written rectangle, taken in batch reads, plus where the robot's BODY fits (a
    /// Euclidean distance transform of the blocked cells). Part of <see cref="MapAnalysis"/>. The distance transform is
    /// only redone when the blocked cells or the explored area changed since the last copy.
    ///
    /// Indexing: cell (row, col) of the map is element (row - MinRow) * Cols + (col - MinCol).
    /// </summary>
    public sealed class MapSnapshot
    {
        bool[] blocked = new bool[0];        // map.IsBlocked: traversability 0, or a shaded SLAM wall
        float[] explored = new float[0];
        float[] dwell = new float[0];
        float[] cleanliness = new float[0];
        float[] resistance = new float[0];
        bool[] wasBlocked = new bool[0];      // the last copy's blocked and explored cells, to tell when they change
        bool[] wasExplored = new bool[0];
        int lastMinRow, lastMinCol, lastRows = -1, lastCols = -1;
        float[] distance = new float[0];   // metres from the cell's centre to the nearest blocked cell's edge (conservative)
        float[] scratchF = new float[0];
        int[] scratchV = new int[0];
        float[] scratchZ = new float[0];
        float[] scratchCol = new float[0];
        float[] scratchOut = new float[0];

        public int MinRow { get; private set; }
        public int MinCol { get; private set; }
        public int Rows { get; private set; }
        public int Cols { get; private set; }
        public float CellSize { get; private set; }
        public float Clearance { get; private set; }

        /// <summary>The map's change counter when this was taken: compare with <see cref="IBeliefMap.ChangeCounter"/> to know it is stale.</summary>
        public int TakenAtChange { get; private set; } = int.MinValue;

        public bool IsEmpty => Rows == 0 || Cols == 0;

        /// <summary>
        /// Copies the map. Returns true when the extent, the blocked cells or the explored cells differ from the last
        /// copy (and the distance transform was redone); false when only dwell, cleanliness or resistance may differ.
        /// </summary>
        public bool Take(IBeliefMap map)
        {
            TakenAtChange = map.ChangeCounter;
            CellSize = map.CellSizeMeters;
            Clearance = map.BodyClearanceMeters;

            if (map.IsEmpty)
            {
                bool wasEmpty = lastRows == 0;
                Rows = Cols = 0;
                lastRows = lastCols = 0;
                return !wasEmpty;
            }
            MinRow = map.MinRow;
            MinCol = map.MinCol;
            Rows = map.Rows;
            Cols = map.Cols;

            int n = Rows * Cols;
            Ensure(ref explored, n);
            Ensure(ref dwell, n);
            Ensure(ref cleanliness, n);
            Ensure(ref resistance, n);
            Ensure(ref distance, n);
            map.ReadRegion(MinRow, MinCol, Rows, Cols, 1, BeliefField.Explored, explored);
            map.ReadRegion(MinRow, MinCol, Rows, Cols, 1, BeliefField.Dwell, dwell);
            map.ReadRegion(MinRow, MinCol, Rows, Cols, 1, BeliefField.Cleanliness, cleanliness);
            map.ReadRegion(MinRow, MinCol, Rows, Cols, 1, BeliefField.VacuumResistance, resistance);

            if (blocked.Length < n) blocked = new bool[n];
            for (int i = 0; i < n; i++) blocked[i] = map.IsBlocked(MinRow + i / Cols, MinCol + i % Cols);

            bool changed = MinRow != lastMinRow || MinCol != lastMinCol || Rows != lastRows || Cols != lastCols;
            if (wasBlocked.Length < n)
            {
                wasBlocked = new bool[n];
                wasExplored = new bool[n];
                changed = true;
            }
            for (int i = 0; i < n; i++)
            {
                bool wall = blocked[i], known = explored[i] > 0.5f;
                if (wall != wasBlocked[i] || known != wasExplored[i]) changed = true;
                wasBlocked[i] = wall;
                wasExplored[i] = known;
            }
            lastMinRow = MinRow;
            lastMinCol = MinCol;
            lastRows = Rows;
            lastCols = Cols;

            if (changed) DistanceTransform();
            return changed;
        }

        // ---- Per-cell queries (false / 0 outside the snapshot) --------------------------------------------

        public bool Contains(int row, int col) => row >= MinRow && col >= MinCol && row < MinRow + Rows && col < MinCol + Cols;

        int Index(int row, int col) => (row - MinRow) * Cols + (col - MinCol);

        public bool IsBlocked(int row, int col) => Contains(row, col) && blocked[Index(row, col)];

        public bool IsExplored(int row, int col) => Contains(row, col) && explored[Index(row, col)] > 0.5f;

        public bool IsVisited(int row, int col) => Contains(row, col) && dwell[Index(row, col)] > 0f;

        /// <summary>The believed dwell, seconds (0 outside the snapshot).</summary>
        public float Dwell(int row, int col) => Contains(row, col) ? dwell[Index(row, col)] : 0f;

        /// <summary>The believed cleanliness (0 outside the snapshot).</summary>
        public float Cleanliness(int row, int col) => Contains(row, col) ? cleanliness[Index(row, col)] : 0f;

        /// <summary>The believed vacuum resistance (1 outside the snapshot).</summary>
        public float VacuumResistance(int row, int col) => Contains(row, col) ? resistance[Index(row, col)] : 1f;

        /// <summary>Element index of a cell, for per-cell arrays sized <see cref="Rows"/> × <see cref="Cols"/>; -1 outside.</summary>
        public int IndexOf(int row, int col) => Contains(row, col) ? Index(row, col) : -1;

        /// <summary>Distance from the cell's centre to the nearest blocked cell's edge, metres (large when nothing is near).</summary>
        public float ObstacleDistance(int row, int col) => Contains(row, col) ? distance[Index(row, col)] : float.PositiveInfinity;

        /// <summary>The body fits with its centre here: explored, and at least the clearance from anything blocked.</summary>
        public bool BodyFits(int row, int col) => IsExplored(row, col) && ObstacleDistance(row, col) >= Clearance;

        // ---- Euclidean distance transform ----------------------------------------------------------------

        /// <summary>
        /// Exact squared Euclidean distance (in cells) from every cell to the nearest blocked cell, by Felzenszwalb &amp;
        /// Huttenlocher's separable algorithm (2012): a 1-D lower-envelope-of-parabolas pass along every column, then
        /// along every row. O(cells). Converted to metres from the cell's centre to the blocked cell's EDGE, using
        /// d - half a diagonal (exact along an axis, conservative by up to 1 cm on a diagonal), so "the body fits" here
        /// never claims more room than the path planner, which measures exactly.
        /// </summary>
        void DistanceTransform()
        {
            const float Infinity = 1e20f;
            int n = Rows * Cols;
            for (int i = 0; i < n; i++) distance[i] = blocked[i] ? 0f : Infinity;

            int longest = Math.Max(Rows, Cols);
            Ensure(ref scratchF, longest);
            Ensure(ref scratchOut, longest);
            Ensure(ref scratchZ, longest + 1);
            Ensure(ref scratchCol, longest);
            if (scratchV.Length < longest) scratchV = new int[longest];

            // Columns.
            for (int c = 0; c < Cols; c++)
            {
                for (int r = 0; r < Rows; r++) scratchCol[r] = distance[r * Cols + c];
                Transform1D(scratchCol, Rows);
                for (int r = 0; r < Rows; r++) distance[r * Cols + c] = scratchOut[r];
            }

            // Rows.
            for (int r = 0; r < Rows; r++)
            {
                Array.Copy(distance, r * Cols, scratchCol, 0, Cols);
                Transform1D(scratchCol, Cols);
                Array.Copy(scratchOut, 0, distance, r * Cols, Cols);
            }

            float halfDiagonal = CellSize * 0.70710678f;
            for (int i = 0; i < n; i++)
            {
                float d2 = distance[i];
                distance[i] = d2 <= 0f ? 0f : d2 >= Infinity * 0.5f ? float.PositiveInfinity : Math.Max(0f, (float)Math.Sqrt(d2) * CellSize - halfDiagonal);
            }
        }

        /// <summary>
        /// One dimension of the transform: out[q] = min over p of (q - p)² + f[p]. The lower envelope of the parabolas
        /// rooted at each p is built left to right (v = their roots, z = where each takes over), then read off.
        /// </summary>
        void Transform1D(float[] f, int length)
        {
            var v = scratchV;
            var z = scratchZ;
            int k = 0;
            v[0] = 0;
            z[0] = float.NegativeInfinity;
            z[1] = float.PositiveInfinity;

            for (int q = 1; q < length; q++)
            {
                float s;
                while (true)
                {
                    int p = v[k];
                    s = ((f[q] + q * (float)q) - (f[p] + p * (float)p)) / (2f * q - 2f * p);
                    if (s <= z[k] && k > 0) k--;
                    else break;
                }
                if (s <= z[k])
                {
                    // k == 0 and the new parabola is lower everywhere: replace the root.
                    v[0] = q;
                    z[0] = float.NegativeInfinity;
                    z[1] = float.PositiveInfinity;
                    continue;
                }
                k++;
                v[k] = q;
                z[k] = s;
                z[k + 1] = float.PositiveInfinity;
            }

            k = 0;
            for (int q = 0; q < length; q++)
            {
                while (z[k + 1] < q) k++;
                int p = v[k];
                scratchOut[q] = (q - p) * (float)(q - p) + f[p];
            }
        }

        static void Ensure(ref float[] array, int length)
        {
            if (array.Length < length) array = new float[length];
        }
    }
}
