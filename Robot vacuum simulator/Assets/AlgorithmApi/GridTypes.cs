using System;
using System.Collections.Generic;

namespace RobotVacuum.AlgorithmApi
{
    /// <summary>A cell on the shared lattice (the same indices in the robot's map and the simulator's). May be negative.</summary>
    public readonly struct CellIndex : IEquatable<CellIndex>
    {
        public readonly int row;
        public readonly int col;

        public CellIndex(int row, int col)
        {
            this.row = row;
            this.col = col;
        }

        /// <summary>One number per cell, for hash sets and dictionaries.</summary>
        public long Key => Pack(row, col);

        public static long Pack(int row, int col) => ((long)row << 32) | (uint)col;

        public bool Equals(CellIndex other) => row == other.row && col == other.col;
        public override bool Equals(object obj) => obj is CellIndex other && Equals(other);
        public override int GetHashCode() => (row * 397) ^ col;
        public static bool operator ==(CellIndex a, CellIndex b) => a.Equals(b);
        public static bool operator !=(CellIndex a, CellIndex b) => !a.Equals(b);

        public override string ToString() => $"({row}, {col})";
    }

    /// <summary>Which belief a batch read (<see cref="IBeliefMap.ReadRegion"/>) returns.</summary>
    public enum BeliefField
    {
        Traversability,
        VacuumResistance,
        Dwell,
        Cleanliness,

        /// <summary>1 where the robot has sensed the cell (free or blocked), 0 where it never has. Blocks give the explored fraction.</summary>
        Explored,
    }

    /// <summary>Helpers for <see cref="IBeliefMap.ReadRegion"/>.</summary>
    public static class BeliefRegion
    {
        /// <summary>The buffer length a region read needs for a region and block size.</summary>
        public static int BufferLength(int rows, int cols, int blockSize)
        {
            blockSize = Math.Max(1, blockSize);
            return ((rows + blockSize - 1) / blockSize) * ((cols + blockSize - 1) / blockSize);
        }
    }

    /// <summary>Grid line walking (Bresenham): every cell on the straight line between two cells, ends included.</summary>
    public static class GridLine
    {
        public static IEnumerable<CellIndex> Cells(int r0, int c0, int r1, int c1)
        {
            int dc = Math.Abs(c1 - c0), sc = c0 < c1 ? 1 : -1;
            int dr = -Math.Abs(r1 - r0), sr = r0 < r1 ? 1 : -1;
            int error = dc + dr;

            int r = r0, c = c0;
            while (true)
            {
                yield return new CellIndex(r, c);
                if (r == r1 && c == c1) yield break;

                int twice = 2 * error;
                if (twice >= dr)
                {
                    error += dr;
                    c += sc;
                }
                if (twice <= dc)
                {
                    error += dc;
                    r += sr;
                }
            }
        }
    }
}
