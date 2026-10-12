using System;
using UnityEngine;

namespace RobotVacuum.Sim
{
    /// <summary>
    /// A plain, saveable copy of the External Model at one moment: no references to the scene, the level or
    /// the live grid. A finished run writes one of these, and anything that wants to draw or analyse the run
    /// later (the exported PNGs, the analysis screen) works from it alone. Arrays are indexed
    /// <c>row * cols + col</c>; row 0 is the lowest Y, col 0 the lowest X.
    /// </summary>
    [Serializable]
    public class ExternalModelData
    {
        public int rows;
        public int cols;

        /// <summary>Cell edge length, in metres.</summary>
        public float cellSizeMeters;

        /// <summary>World-space position of the corner of cell (0, 0), in metres.</summary>
        public float originX;
        public float originY;

        /// <summary>target_max_clean in force when this was captured. The colour ranges derive from it.</summary>
        public float targetMaxClean = HeatmapDefaults.TargetMaxClean;

        /// <summary>Seconds the robot has spent over each cell.</summary>
        public float[] dwellSeconds;

        /// <summary>Cleanliness accumulated in each cell. Not capped at the target.</summary>
        public float[] cleanliness;

        /// <summary>True under blocking furniture: excluded from coverage.</summary>
        public bool[] nonCleanable;

        /// <summary>True outside every room: not floor at all.</summary>
        public bool[] outsideRooms;

        public int Index(int row, int col) => row * cols + col;

        public bool IsValid =>
            rows > 0 && cols > 0 &&
            dwellSeconds != null && cleanliness != null && nonCleanable != null && outsideRooms != null &&
            dwellSeconds.Length == rows * cols && cleanliness.Length == rows * cols &&
            nonCleanable.Length == rows * cols && outsideRooms.Length == rows * cols;

        /// <summary>
        /// How much of a cell's cleaning goal is met, 0 to 1: its cleanliness over the target, clamped to that
        /// range (a value at or below 0 counts as nothing, one past the target counts as fully met).
        /// The one definition both the live grid and saved data use for coverage.
        /// </summary>
        public static float CleanFraction(float cleanliness, float targetMaxClean) =>
            targetMaxClean <= 0f ? 1f : Mathf.Clamp01(cleanliness / targetMaxClean);

        /// <summary>
        /// Average goal progress across cleanable cells, 0 to 100. A cell at half the target counts as half
        /// clean; a cell past the target counts as fully clean and no more.
        /// </summary>
        public float CoveragePercent()
        {
            if (!IsValid) return 0f;

            int counted = 0;
            float sum = 0f;
            for (int i = 0; i < cleanliness.Length; i++)
            {
                if (nonCleanable[i]) continue;
                counted++;
                sum += CleanFraction(cleanliness[i], targetMaxClean);
            }
            return counted == 0 ? 0f : sum / counted * 100f;
        }

        /// <summary>The share of cleanable cells that have reached the target, 0 to 100.</summary>
        public float FullyCleanedPercent()
        {
            if (!IsValid) return 0f;

            int counted = 0;
            int reached = 0;
            for (int i = 0; i < cleanliness.Length; i++)
            {
                if (nonCleanable[i]) continue;
                counted++;
                if (cleanliness[i] >= targetMaxClean) reached++;
            }
            return counted == 0 ? 0f : reached / (float)counted * 100f;
        }
    }
}
