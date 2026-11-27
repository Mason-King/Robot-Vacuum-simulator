using System;
using System.Collections.Generic;
using RobotVacuum.AlgorithmApi;

namespace RobotVacuum.Algorithms
{
    /// <summary>
    /// Free-standing obstacles, for laying out a plan through them: chair legs, a small table, the cat out in the open.
    ///
    /// An obstacle here is a connected patch of cells the body doesn't fit on (the blocked cells and the clearance
    /// around them, and any unmapped cells inside). It is free-standing in a room when
    ///   * every cell around it is floor of that room the body fits on: the robot has seen floor on every side of it,
    ///     and it touches no wall, no unmapped space and no other room (a wall between two rooms has floor on both sides,
    ///     but of two different rooms; a cat in a doorway too);
    ///   * its blocked cells span no more than <see cref="MaxObstacleMeters"/> either way (a sofa is not walked round).
    /// A plan's lanes and rings run straight through such an obstacle; the driver goes round it when the robot gets
    /// there (the plan puts a waypoint on the floor just before it and just after it).
    ///
    /// Cost: nothing per step. Each obstacle is flood-filled once, when first asked about, and the answer is kept until
    /// the map analysis changes (<see cref="IMapAnalysis.Version"/>).
    /// </summary>
    public sealed class FreeStanding
    {
        /// <summary>An obstacle whose blocked cells span more than this either way is not walked round.</summary>
        public const float MaxObstacleMeters = 0.5f;

        readonly IMapAnalysis analysis;
        int version = -1;
        int row0, col0, rows, cols;
        // Per cell: 0 not looked at; -1 not part of a free-standing obstacle; otherwise 1 + the room it stands in.
        int[] state = Array.Empty<int>();
        readonly Queue<int> queue = new Queue<int>();
        readonly List<int> component = new List<int>();

        public FreeStanding(IMapAnalysis analysis)
        {
            this.analysis = analysis ?? throw new ArgumentNullException(nameof(analysis));
        }

        /// <summary>Is this cell part of an obstacle standing free in <paramref name="room"/>?</summary>
        public bool Passable(int row, int col, int room)
        {
            if (room < 0 || !analysis.Contains(row, col) || analysis.BodyFits(row, col)) return false;
            Refresh();
            int index = Index(row, col);
            if (index < 0) return false;
            if (state[index] == 0) Fill(index);
            return state[index] == room + 1;
        }

        /// <summary>Is this cell part of an obstacle standing free in any room?</summary>
        public bool IsFreeStanding(int row, int col)
        {
            if (!analysis.Contains(row, col) || analysis.BodyFits(row, col)) return false;
            Refresh();
            int index = Index(row, col);
            if (index < 0) return false;
            if (state[index] == 0) Fill(index);
            return state[index] > 0;
        }

        // ---- Distance to the walls ------------------------------------------------------------------------

        float[] wallSq = Array.Empty<float>();
        int wallVersion = -1;

        /// <summary>
        /// As <see cref="IMapAnalysis.ObstacleDistance(int, int)"/> (from the cell's center, less half a cell's diagonal),
        /// but leaving out free-standing obstacles: the distance to the nearest wall, furniture against a wall, or
        /// unmapped space. For laying out rings that follow the room's shape, not a chair's or the cat's. Worked out for
        /// the whole map once per map analysis version (an exact distance transform), only when first asked.
        /// </summary>
        public float WallDistance(int row, int col)
        {
            Refresh();
            int index = Index(row, col);
            if (index < 0) return 0f;
            if (wallVersion != version || wallSq.Length != rows * cols) BuildWallDistance();
            float sq = wallSq[index];
            if (float.IsPositiveInfinity(sq)) return float.PositiveInfinity;
            float cell = analysis.CellSize;
            return Math.Max(0f, (float)Math.Sqrt(sq) * cell - 0.5f * cell * (float)Math.Sqrt(2.0));
        }

        void BuildWallDistance()
        {
            wallVersion = version;
            if (wallSq.Length != rows * cols) wallSq = new float[rows * cols];
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < cols; c++)
                {
                    int row = row0 + r, col = col0 + c;
                    bool source = (analysis.IsBlocked(row, col) || !analysis.IsExplored(row, col)) && !IsFreeStanding(row, col);
                    wallSq[r * cols + c] = source ? 0f : float.PositiveInfinity;
                }

            // Exact squared Euclidean distance transform (Felzenszwalb & Huttenlocher): columns, then rows, in cells.
            int n = Math.Max(rows, cols);
            var f = new float[n];
            var d = new float[n];
            var v = new int[n];
            var z = new float[n + 1];
            for (int c = 0; c < cols; c++)
            {
                for (int r = 0; r < rows; r++) f[r] = wallSq[r * cols + c];
                Transform(f, rows, d, v, z);
                for (int r = 0; r < rows; r++) wallSq[r * cols + c] = d[r];
            }
            for (int r = 0; r < rows; r++)
            {
                for (int c = 0; c < cols; c++) f[c] = wallSq[r * cols + c];
                Transform(f, cols, d, v, z);
                for (int c = 0; c < cols; c++) wallSq[r * cols + c] = d[c];
            }
        }

        static void Transform(float[] f, int n, float[] d, int[] v, float[] z)
        {
            int k = -1;
            for (int q = 0; q < n; q++)
            {
                if (float.IsPositiveInfinity(f[q])) continue;
                if (k < 0)
                {
                    k = 0;
                    v[0] = q;
                    z[0] = float.NegativeInfinity;
                    z[1] = float.PositiveInfinity;
                    continue;
                }
                float s;
                while (true)
                {
                    int p = v[k];
                    s = ((f[q] + q * q) - (f[p] + p * p)) / (2f * (q - p));
                    if (s <= z[k] && k > 0) { k--; continue; }
                    break;
                }
                if (s <= z[k])
                {
                    // k == 0 and the new parabola is lower everywhere: replace it.
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
            if (k < 0)
            {
                for (int q = 0; q < n; q++) d[q] = float.PositiveInfinity;
                return;
            }
            int j = 0;
            for (int q = 0; q < n; q++)
            {
                while (z[j + 1] < q) j++;
                int p = v[j];
                d[q] = (q - p) * (q - p) + f[p];
            }
        }

        void Refresh()
        {
            if (version == analysis.Version && rows == analysis.Rows && cols == analysis.Cols &&
                row0 == analysis.MinRow && col0 == analysis.MinCol) return;
            version = analysis.Version;
            row0 = analysis.MinRow;
            col0 = analysis.MinCol;
            rows = analysis.Rows;
            cols = analysis.Cols;
            if (state.Length != rows * cols) state = new int[rows * cols];
            else Array.Clear(state, 0, state.Length);
        }

        int Index(int row, int col)
        {
            int r = row - row0, c = col - col0;
            return r < 0 || r >= rows || c < 0 || c >= cols ? -1 : r * cols + c;
        }

        /// <summary>Flood-fills the obstacle containing the cell and marks all of it with the verdict.</summary>
        void Fill(int start)
        {
            queue.Clear();
            component.Clear();
            state[start] = -2; // in this fill
            queue.Enqueue(start);
            int room = -1;
            bool free = true;
            int minRow = int.MaxValue, maxRow = int.MinValue, minCol = int.MaxValue, maxCol = int.MinValue;

            while (queue.Count > 0)
            {
                int index = queue.Dequeue();
                component.Add(index);
                int r = index / cols, c = index % cols;
                int row = row0 + r, col = col0 + c;
                if (analysis.IsBlocked(row, col))
                {
                    minRow = Math.Min(minRow, row); maxRow = Math.Max(maxRow, row);
                    minCol = Math.Min(minCol, col); maxCol = Math.Max(maxCol, col);
                }
                for (int k = 0; k < 4; k++)
                {
                    int nr = r + (k == 0 ? 1 : k == 1 ? -1 : 0), nc = c + (k == 2 ? 1 : k == 3 ? -1 : 0);
                    if (nr < 0 || nr >= rows || nc < 0 || nc >= cols)
                    {
                        free = false; // runs off the map
                        continue;
                    }
                    int n = nr * cols + nc;
                    int nRow = row0 + nr, nCol = col0 + nc;
                    if (analysis.BodyFits(nRow, nCol))
                    {
                        // Floor around the obstacle: all of it must be mapped floor of one room.
                        int nRoom = analysis.RoomOf(nRow, nCol);
                        if (nRoom < 0 || !analysis.IsExplored(nRow, nCol)) free = false;
                        else if (room < 0) room = nRoom;
                        else if (nRoom != room) free = false;
                        continue;
                    }
                    if (state[n] != 0) continue; // already in this fill
                    state[n] = -2;
                    queue.Enqueue(n);
                }
            }

            float cell = analysis.CellSize;
            if (minRow == int.MaxValue) free = false; // nothing blocked in it (a pinch between two things): not an obstacle
            else if ((maxRow - minRow + 1) * cell > MaxObstacleMeters || (maxCol - minCol + 1) * cell > MaxObstacleMeters) free = false;
            if (room < 0) free = false;

            int verdict = free ? room + 1 : -1;
            foreach (int index in component) state[index] = verdict;
        }
    }
}
