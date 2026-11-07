using System;
using RobotVacuum.AlgorithmApi;

namespace RobotVacuum.Algorithms
{
    /// <summary>
    /// A copy of the belief map's written rectangle, taken in three batch reads, plus what the coverage algorithms
    /// need from it: where the robot's BODY fits (a Euclidean distance transform of the blocked cells), what has been
    /// explored, and what has been visited. Taken when an algorithm re-plans (every few seconds), not every step.
    ///
    /// Indexing: cell (row, col) of the map is element (row - MinRow) * Cols + (col - MinCol).
    /// </summary>
    public sealed class MapSnapshot
    {
        float[] traversability = new float[0];
        float[] explored = new float[0];
        float[] dwell = new float[0];
        float[] cleanliness = new float[0];
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

        public void Take(IBeliefMap map)
        {
            TakenAtChange = map.ChangeCounter;
            CellSize = map.CellSizeMeters;
            Clearance = map.BodyClearanceMeters;

            if (map.IsEmpty)
            {
                Rows = Cols = 0;
                return;
            }
            MinRow = map.MinRow;
            MinCol = map.MinCol;
            Rows = map.Rows;
            Cols = map.Cols;

            int n = Rows * Cols;
            Ensure(ref traversability, n);
            Ensure(ref explored, n);
            Ensure(ref dwell, n);
            Ensure(ref cleanliness, n);
            Ensure(ref distance, n);
            map.ReadRegion(MinRow, MinCol, Rows, Cols, 1, BeliefField.Traversability, traversability);
            map.ReadRegion(MinRow, MinCol, Rows, Cols, 1, BeliefField.Explored, explored);
            map.ReadRegion(MinRow, MinCol, Rows, Cols, 1, BeliefField.Dwell, dwell);
            map.ReadRegion(MinRow, MinCol, Rows, Cols, 1, BeliefField.Cleanliness, cleanliness);
            DistanceTransform();
        }

        // ---- Per-cell queries (false / 0 outside the snapshot) --------------------------------------------

        public bool Contains(int row, int col) => row >= MinRow && col >= MinCol && row < MinRow + Rows && col < MinCol + Cols;

        int Index(int row, int col) => (row - MinRow) * Cols + (col - MinCol);

        public bool IsBlocked(int row, int col) => Contains(row, col) && traversability[Index(row, col)] <= 0f;

        public bool IsExplored(int row, int col) => Contains(row, col) && explored[Index(row, col)] > 0.5f;

        public bool IsVisited(int row, int col) => Contains(row, col) && dwell[Index(row, col)] > 0f;

        /// <summary>The believed dwell, seconds (0 outside the snapshot).</summary>
        public float Dwell(int row, int col) => Contains(row, col) ? dwell[Index(row, col)] : 0f;

        /// <summary>The believed cleanliness (0 outside the snapshot).</summary>
        public float Cleanliness(int row, int col) => Contains(row, col) ? cleanliness[Index(row, col)] : 0f;

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
            for (int i = 0; i < n; i++) distance[i] = traversability[i] <= 0f ? 0f : Infinity;

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
