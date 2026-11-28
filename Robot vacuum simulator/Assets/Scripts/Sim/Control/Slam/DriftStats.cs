using System.Collections.Generic;
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

        // The run's track every TrackEvery steps (believed and true position, signed heading error), for the end-of-run
        // fit that separates whole-map distortion from local accuracy (Aligned).
        const int TrackEvery = 10;
        readonly List<Vector2> believedTrack = new List<Vector2>();
        readonly List<Vector2> trueTrack = new List<Vector2>();
        readonly List<float> headingErrors = new List<float>();
        int sinceSample;

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
            believedTrack.Clear();
            trueTrack.Clear();
            headingErrors.Clear();
            sinceSample = 0;
        }

        /// <summary>One step's believed and true pose: as <see cref="Add(float, float)"/>, and keeps the track for <see cref="Aligned"/>.</summary>
        public void Add(RobotPose believed, RobotPose truth)
        {
            float headingError = Mathf.DeltaAngle(truth.heading, believed.heading);
            Add(Vector2.Distance(believed.position, truth.position), Mathf.Abs(headingError));
            if (sinceSample++ % TrackEvery != 0) return;
            believedTrack.Add(believed.position);
            trueTrack.Add(truth.position);
            headingErrors.Add(headingError);
        }

        /// <summary>
        /// The run's error split in two. Map drift: one best-fit scale, rotation and offset that carry the true track onto
        /// the believed one (believed ≈ scale · R(rotation) · true + shift, least squares). The robot's map is drawn from
        /// its believed track, so it carries the same distortion: scale below 0 % = the map is drawn smaller than the
        /// home. Local accuracy: the position and heading error left once that distortion is removed, the way SLAM
        /// systems are usually scored. Zeros until the robot has moved.
        /// </summary>
        public AlignedDrift Aligned()
        {
            var result = new AlignedDrift { samples = trueTrack.Count };
            int n = trueTrack.Count;
            if (n < 3) return result;
            double mbx = 0, mby = 0, mtx = 0, mty = 0;
            for (int i = 0; i < n; i++)
            {
                mbx += believedTrack[i].x; mby += believedTrack[i].y;
                mtx += trueTrack[i].x; mty += trueTrack[i].y;
            }
            mbx /= n; mby /= n; mtx /= n; mty /= n;
            double dot = 0, cross = 0, tt = 0;
            for (int i = 0; i < n; i++)
            {
                double tx = trueTrack[i].x - mtx, ty = trueTrack[i].y - mty, bx = believedTrack[i].x - mbx, by = believedTrack[i].y - mby;
                dot += tx * bx + ty * by;
                cross += tx * by - ty * bx;
                tt += tx * tx + ty * ty;
            }
            if (tt < 1e-6) return result; // never moved
            double angle = System.Math.Atan2(cross, dot), scale = System.Math.Sqrt(dot * dot + cross * cross) / tt;
            double c = System.Math.Cos(angle) * scale, s = System.Math.Sin(angle) * scale;
            result.scalePercent = (float)((scale - 1) * 100);
            result.rotationDegrees = (float)(angle * 180 / System.Math.PI);
            result.offsetMeters = (float)System.Math.Sqrt((mbx - mtx) * (mbx - mtx) + (mby - mty) * (mby - mty));

            var position = new float[n];
            var heading = new float[n];
            double sumSq = 0, sumHeadingSq = 0;
            for (int i = 0; i < n; i++)
            {
                double tx = trueTrack[i].x - mtx, ty = trueTrack[i].y - mty;
                double fx = c * tx - s * ty + mbx, fy = s * tx + c * ty + mby;
                double ex = believedTrack[i].x - fx, ey = believedTrack[i].y - fy;
                position[i] = (float)System.Math.Sqrt(ex * ex + ey * ey);
                heading[i] = Mathf.Abs(Mathf.DeltaAngle(result.rotationDegrees, headingErrors[i]));
                sumSq += position[i] * (double)position[i];
                sumHeadingSq += heading[i] * (double)heading[i];
            }
            System.Array.Sort(position);
            System.Array.Sort(heading);
            int p99 = Mathf.Clamp((int)System.Math.Ceiling(0.99 * n) - 1, 0, n - 1);
            result.maxMeters = position[n - 1];
            result.p99Meters = position[p99];
            result.rmsMeters = (float)System.Math.Sqrt(sumSq / n);
            result.maxDegrees = heading[n - 1];
            result.p99Degrees = heading[p99];
            result.rmsDegrees = (float)System.Math.Sqrt(sumHeadingSq / n);
            return result;
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

    /// <summary><see cref="DriftStats.Aligned"/>: map drift (scale %, rotation °, mean offset m) and the local accuracy left after it.</summary>
    public struct AlignedDrift
    {
        public int samples;
        public float scalePercent, rotationDegrees, offsetMeters;
        public float maxMeters, p99Meters, rmsMeters;
        public float maxDegrees, p99Degrees, rmsDegrees;
    }
}
