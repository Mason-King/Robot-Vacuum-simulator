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

        /// <summary>Outside every room (not floor): left out of the percentages. Older saves without the mask count everything.</summary>
        bool OutsideRooms(int i) => outsideRooms != null && i < outsideRooms.Length && outsideRooms[i];

        /// <summary>
        /// Average goal progress across cleanable cells inside the rooms, 0 to 100. A cell at half the target counts
        /// as half clean; a cell past the target counts as fully clean and no more.
        /// </summary>
        public float CoveragePercent()
        {
            if (!IsValid) return 0f;

            int counted = 0;
            float sum = 0f;
            for (int i = 0; i < cleanliness.Length; i++)
            {
                if (nonCleanable[i] || OutsideRooms(i)) continue;
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
                if (nonCleanable[i] || OutsideRooms(i)) continue;
                counted++;
                if (cleanliness[i] >= targetMaxClean) reached++;
            }
            return counted == 0 ? 0f : reached / (float)counted * 100f;
        }

        /// <summary>A cell counts as over-cleaned past this multiple of the target (see <see cref="OverCleaning"/>).</summary>
        public const float OverCleanedFactor = 1.1f;

        /// <summary>Over-cleaning across the cleanable cells inside the rooms (the cells the percentages count).</summary>
        public struct OverCleaningStats
        {
            /// <summary>All cleaning done ÷ the cleaning the target asks for: total cleanliness ÷ (cells × target). 1 = exactly the target everywhere on average.</summary>
            public float meanCleanRatio;

            /// <summary>The median and 90th percentile of each cell's cleanliness ÷ target.</summary>
            public float medianCleanRatio;
            public float p90CleanRatio;

            /// <summary>Share of cells past <see cref="OverCleanedFactor"/> × target, 0 to 100.</summary>
            public float overCleanedPercent;

            /// <summary>Cleaning beyond the target, summed over cells: Σ max(0, cleanliness − target) × cell area ÷ target. In m² of floor cleaned once more to the target.</summary>
            public float overCleaningSquareMeters;

            /// <summary>Share of all cleaning done that went beyond the target, 0 to 100.</summary>
            public float overCleaningPercentOfTotal;
        }

        /// <summary>The over-cleaning figures for <see cref="OverCleaningStats"/>. All zero for invalid data or a target of 0.</summary>
        public OverCleaningStats OverCleaning()
        {
            var stats = new OverCleaningStats();
            if (!IsValid || targetMaxClean <= 0f) return stats;

            var ratios = new System.Collections.Generic.List<float>(cleanliness.Length);
            double total = 0.0, beyond = 0.0;
            int over = 0;
            float overLine = OverCleanedFactor * targetMaxClean;
            for (int i = 0; i < cleanliness.Length; i++)
            {
                if (nonCleanable[i] || OutsideRooms(i)) continue;
                float c = Mathf.Max(0f, cleanliness[i]);
                ratios.Add(c / targetMaxClean);
                total += c;
                if (c > targetMaxClean) beyond += c - targetMaxClean;
                if (c > overLine) over++;
            }
            int n = ratios.Count;
            if (n == 0) return stats;

            ratios.Sort();
            stats.meanCleanRatio = (float)(total / (n * (double)targetMaxClean));
            stats.medianCleanRatio = Percentile(ratios, 0.5f);
            stats.p90CleanRatio = Percentile(ratios, 0.9f);
            stats.overCleanedPercent = over / (float)n * 100f;
            stats.overCleaningSquareMeters = (float)(beyond / targetMaxClean * cellSizeMeters * cellSizeMeters);
            stats.overCleaningPercentOfTotal = total > 0.0 ? (float)(beyond / total * 100.0) : 0f;
            return stats;
        }

        /// <summary>Linear-interpolated percentile of a sorted list (q from 0 to 1).</summary>
        static float Percentile(System.Collections.Generic.List<float> sorted, float q)
        {
            float at = q * (sorted.Count - 1);
            int low = (int)at;
            int high = Mathf.Min(low + 1, sorted.Count - 1);
            return sorted[low] + (sorted[high] - sorted[low]) * (at - low);
        }
    }
}
