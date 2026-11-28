using System;
using System.Collections.Generic;
using RobotVacuum.AlgorithmApi;

namespace RobotVacuum.Sim.Control
{
    /// <summary>
    /// How far the robot would have to drive to every cell, over floor it KNOWS (explored, and the body fits): one
    /// Dijkstra flood from the robot, 8-connected, no cutting corners past cells where the body doesn't fit.
    ///
    /// This is what "reachable" and "nearest" mean for choosing work. Unexplored floor never connects anything, so
    /// cells the lidar glimpsed through a gap in a half-drawn wall (outside the house) are simply unreachable, instead
    /// of targets that every path search fails on. And one flood answers "how far by path" for every candidate at
    /// once, so choosing needs no A* searches at all.
    /// </summary>
    public sealed class ReachField
    {
        /// <summary>When the robot's own cell is too close to a wall for the body (it often is), the flood starts from fitting cells within this distance.</summary>
        public const float StartSearchMeters = 0.4f;

        float[] distance = new float[0];
        // A binary min-heap in two arrays (index, key). Pushes and pops follow the same order as the List version it
        // replaced, so ties are broken the same way and the distances come out bit-identical.
        int[] heapIndex = new int[0];
        float[] heapKey = new float[0];
        int heapCount;
        MapSnapshot snapshot;

        public float Distance(int row, int col)
        {
            int i = snapshot != null ? snapshot.IndexOf(row, col) : -1;
            return i < 0 ? float.PositiveInfinity : distance[i];
        }

        public bool Reachable(int row, int col) => !float.IsPositiveInfinity(Distance(row, col));

        public void Build(MapSnapshot snapshot, IBeliefMap map, Vec2 from)
        {
            this.snapshot = snapshot;
            int rows = snapshot.Rows, cols = snapshot.Cols, n = rows * cols;
            if (distance.Length < n) distance = new float[n];
            for (int i = 0; i < n; i++) distance[i] = float.PositiveInfinity;
            heapCount = 0;
            if (n == 0 || !map.TryWorldToCell(from, out var start)) return;

            float cell = snapshot.CellSize;
            int reach = (int)Math.Ceiling(StartSearchMeters / cell);
            for (int r = start.row - reach; r <= start.row + reach; r++)
                for (int c = start.col - reach; c <= start.col + reach; c++)
                {
                    if (!snapshot.BodyFits(r, c)) continue;
                    float d = Vec2.Distance(map.CellCenter(r, c), from);
                    if (d > StartSearchMeters && !(r == start.row && c == start.col)) continue;
                    int i = snapshot.IndexOf(r, c);
                    if (d < distance[i])
                    {
                        distance[i] = d;
                        Push(i, d);
                    }
                }

            float diagonal = cell * 1.41421356f;
            bool[] fits = snapshot.FitsByIndex;
            while (heapCount > 0)
            {
                Pop(out int i, out float d);
                if (d > distance[i]) continue;
                int r = i / cols, c = i % cols;
                for (int dr = -1; dr <= 1; dr++)
                    for (int dc = -1; dc <= 1; dc++)
                    {
                        if (dr == 0 && dc == 0) continue;
                        int rr = r + dr, cc = c + dc;
                        if (rr < 0 || cc < 0 || rr >= rows || cc >= cols) continue;
                        int j = rr * cols + cc;
                        if (!fits[j]) continue;
                        // No cutting a corner: both cells beside a diagonal step must fit too (both are inside the grid).
                        if (dr != 0 && dc != 0 && (!fits[r * cols + cc] || !fits[rr * cols + c])) continue;
                        float next = d + (dr != 0 && dc != 0 ? diagonal : cell);
                        if (next >= distance[j]) continue;
                        distance[j] = next;
                        Push(j, next);
                    }
            }
        }

        /// <summary>
        /// Copies which cells are reachable into <paramref name="into"/> (resized to the snapshot) and returns true when
        /// that set differs from what <paramref name="into"/> held. Lets the room split be reused when nothing it reads changed.
        /// </summary>
        internal bool CopyReachable(ref bool[] into)
        {
            int n = snapshot != null ? snapshot.Rows * snapshot.Cols : 0;
            bool differs = false;
            if (into == null || into.Length != n)
            {
                into = new bool[n];
                differs = true;
            }
            for (int i = 0; i < n; i++)
            {
                bool reachable = !float.IsPositiveInfinity(distance[i]);
                if (into[i] != reachable)
                {
                    into[i] = reachable;
                    differs = true;
                }
            }
            return differs;
        }

        void Push(int index, float key)
        {
            if (heapCount == heapIndex.Length)
            {
                int size = Math.Max(1024, heapIndex.Length * 2);
                Array.Resize(ref heapIndex, size);
                Array.Resize(ref heapKey, size);
            }
            heapIndex[heapCount] = index;
            heapKey[heapCount] = key;
            int i = heapCount++;
            while (i > 0)
            {
                int up = (i - 1) / 2;
                if (heapKey[up] <= heapKey[i]) break;
                Swap(i, up);
                i = up;
            }
        }

        void Pop(out int index, out float key)
        {
            index = heapIndex[0];
            key = heapKey[0];
            int last = --heapCount;
            heapIndex[0] = heapIndex[last];
            heapKey[0] = heapKey[last];
            int i = 0;
            while (true)
            {
                int l = 2 * i + 1, r = l + 1, s = i;
                if (l < heapCount && heapKey[l] < heapKey[s]) s = l;
                if (r < heapCount && heapKey[r] < heapKey[s]) s = r;
                if (s == i) break;
                Swap(i, s);
                i = s;
            }
        }

        void Swap(int a, int b)
        {
            (heapIndex[a], heapIndex[b]) = (heapIndex[b], heapIndex[a]);
            (heapKey[a], heapKey[b]) = (heapKey[b], heapKey[a]);
        }
    }
}
