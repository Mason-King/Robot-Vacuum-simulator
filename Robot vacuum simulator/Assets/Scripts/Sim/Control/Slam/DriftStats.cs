using UnityEngine;

namespace RobotVacuum.Sim.Control
{
    /// <summary>
    /// How far the believed pose was from the truth over a whole run, step by step (SLAM only; reporting only, never
    /// read by the simulation). Keeps the worst, the root-mean-square and the percentiles the accuracy target is stated
    /// in (within 5 cm and 2° for 99% of the time). Percentiles come from 1 mm / 0.01° bins and are reported as the
    /// bin's upper edge, so they are never optimistic by more than one bin.
    /// </summary>
    public sealed class DriftStats
    {
        const float PositionBinMeters = 0.001f;
        const int PositionBins = 2000; // 2 m; anything beyond lands in the last bin
        const float HeadingBinDegrees = 0.01f;
        const int HeadingBins = 18000; // 180°

        readonly int[] positionCounts = new int[PositionBins];
        readonly int[] headingCounts = new int[HeadingBins];
        double sumPositionSq, sumHeadingSq;

        public int Count { get; private set; }
        public float MaxMeters { get; private set; }
        public float MaxDegrees { get; private set; }

        public float RmsMeters => Count > 0 ? (float)System.Math.Sqrt(sumPositionSq / Count) : 0f;
        public float RmsDegrees => Count > 0 ? (float)System.Math.Sqrt(sumHeadingSq / Count) : 0f;

        public void Clear()
        {
            System.Array.Clear(positionCounts, 0, positionCounts.Length);
            System.Array.Clear(headingCounts, 0, headingCounts.Length);
            sumPositionSq = sumHeadingSq = 0;
            Count = 0;
            MaxMeters = MaxDegrees = 0f;
        }

        /// <summary>One step's error: position (metres) and heading (degrees, 0–180).</summary>
        public void Add(float meters, float degrees)
        {
            if (float.IsNaN(meters) || float.IsNaN(degrees)) return;

            Count++;
            sumPositionSq += (double)meters * meters;
            sumHeadingSq += (double)degrees * degrees;
            if (meters > MaxMeters) MaxMeters = meters;
            if (degrees > MaxDegrees) MaxDegrees = degrees;

            positionCounts[Mathf.Clamp((int)(meters / PositionBinMeters), 0, PositionBins - 1)]++;
            headingCounts[Mathf.Clamp((int)(degrees / HeadingBinDegrees), 0, HeadingBins - 1)]++;
        }

        /// <summary>The position error the run stayed within for <paramref name="fraction"/> of its steps (0.99 = 99%).</summary>
        public float PositionPercentile(float fraction) =>
            Percentile(positionCounts, PositionBinMeters, fraction, MaxMeters);

        /// <summary>The heading error the run stayed within for <paramref name="fraction"/> of its steps.</summary>
        public float HeadingPercentile(float fraction) =>
            Percentile(headingCounts, HeadingBinDegrees, fraction, MaxDegrees);

        float Percentile(int[] counts, float bin, float fraction, float max)
        {
            if (Count == 0) return 0f;

            long needed = (long)System.Math.Ceiling(Mathf.Clamp01(fraction) * Count);
            long seen = 0;
            for (int i = 0; i < counts.Length; i++)
            {
                seen += counts[i];
                if (seen >= needed) return Mathf.Min((i + 1) * bin, max);
            }
            return max;
        }
    }
}
