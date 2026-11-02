using System;
using UnityEngine;

namespace RobotVacuum.Sim.Sensors
{
    /// <summary>
    /// Where the lidar's rays point, given the ray count and the view half-angle. Pure maths, no Unity scene,
    /// so it is fully testable.
    ///
    /// Rules (decided 2026-11-01):
    ///   * The ray count is used EXACTLY as set, after clamping to <see cref="MinRays"/>–<see cref="MaxRays"/>.
    ///   * A ray straight ahead (bearing 0) always exists, and it is always the first ray.
    ///   * Full view (half-angle 180): N rays evenly spaced at 360/N, starting at 0. No duplicate where +180
    ///     meets -180: an even N has one rear ray at exactly 180, an odd N has none.
    ///   * Partial view (half-angle h &lt; 180): the forward ray, then the other N-1 rays split between the
    ///     two sides; when N-1 is odd the LEFT (counter-clockwise, positive bearing) side gets the extra ray.
    ///     Each side is evenly spaced out to and including its edge, +h or -h.
    ///   * Half-angle 0: no rays at all (whisker-only); the count is ignored.
    ///   * Wasteful settings are never silently changed: if neighbouring rays are closer together than one
    ///     grid cell at maximum range, they hit the same cells and only cost CPU, so a warning says so and
    ///     suggests the largest count that avoids it.
    ///
    /// Bearings are degrees, counter-clockwise positive (left), 0 = straight ahead, in (-180, 180].
    /// </summary>
    public static class LidarRayLayout
    {
        public const int MinRays = 8;
        public const int MaxRays = 360;
        public const float MaxHalfAngle = 180f;

        /// <summary>The ray count actually used: the setting clamped to 8–360.</summary>
        public static int ClampCount(int requested) => Mathf.Clamp(requested, MinRays, MaxRays);

        /// <summary>The half-angle actually used: clamped to 0–180 (no negatives).</summary>
        public static float ClampHalfAngle(float requested) =>
            float.IsNaN(requested) ? MaxHalfAngle : Mathf.Clamp(requested, 0f, MaxHalfAngle);

        /// <summary>
        /// The bearings, straight ahead first. Fills <paramref name="bearings"/> (resized if needed) and returns
        /// how many were written. 0 when the half-angle is 0.
        /// </summary>
        public static int Compute(int requestedCount, float requestedHalfAngle, ref float[] bearings)
        {
            int count = ClampCount(requestedCount);
            float halfAngle = ClampHalfAngle(requestedHalfAngle);
            if (halfAngle <= 0f) return 0;

            if (bearings == null || bearings.Length < count) bearings = new float[count];

            if (halfAngle >= MaxHalfAngle)
            {
                // Full circle. Computed in double so 360/N steps don't drift across many rays.
                double step = 360.0 / count;
                for (int i = 0; i < count; i++)
                {
                    double bearing = i * step;
                    if (bearing > 180.0) bearing -= 360.0; // keep (-180, 180]; exactly 180 stays 180
                    bearings[i] = (float)bearing;
                }
                return count;
            }

            // Partial view: forward, then the left side outward, then the right side outward.
            int rest = count - 1;
            int left = (rest + 1) / 2;   // ceiling: the extra ray goes left
            int right = rest / 2;        // floor

            bearings[0] = 0f;
            int index = 1;
            for (int k = 1; k <= left; k++) bearings[index++] = (float)(halfAngle * (double)k / left);
            for (int k = 1; k <= right; k++) bearings[index++] = (float)(-halfAngle * (double)k / right);
            return count;
        }

        /// <summary>The smallest angle between two neighbouring rays, degrees. 0 when there are no rays.</summary>
        public static float SmallestSpacing(int requestedCount, float requestedHalfAngle)
        {
            int count = ClampCount(requestedCount);
            float halfAngle = ClampHalfAngle(requestedHalfAngle);
            if (halfAngle <= 0f) return 0f;

            if (halfAngle >= MaxHalfAngle) return 360f / count;

            int left = count / 2; // ceiling of (count-1)/2: the denser side
            return halfAngle / left;
        }

        /// <summary>
        /// The angle one grid cell covers at the lidar's maximum range, degrees. Rays closer together than this
        /// land in the same cells at range and add nothing but CPU cost.
        /// </summary>
        public static float CellAngleAtRange(float cellSizeMeters, float rangeMeters) =>
            rangeMeters > 0f ? (float)(Math.Atan2(cellSizeMeters, rangeMeters) * (180.0 / Math.PI)) : 0f;

        /// <summary>
        /// A warning for the user when the rays overlap at range, or null when they don't. The count is NOT
        /// changed; the message names the largest count that would avoid the overlap.
        /// </summary>
        public static string OverlapWarning(int requestedCount, float requestedHalfAngle, float cellSizeMeters, float rangeMeters)
        {
            int count = ClampCount(requestedCount);
            float halfAngle = ClampHalfAngle(requestedHalfAngle);
            if (halfAngle <= 0f) return null;

            float spacing = SmallestSpacing(count, halfAngle);
            float cellAngle = CellAngleAtRange(cellSizeMeters, rangeMeters);
            if (cellAngle <= 0f || spacing >= cellAngle) return null;

            int suggested = LargestCountWithoutOverlap(halfAngle, cellAngle);
            return $"Lidar: {count} rays over a {halfAngle * 2f:0.#}° view puts neighbouring rays {spacing:0.###}° apart, " +
                   $"closer than one {cellSizeMeters * 100f:0.#} cm grid cell at the {rangeMeters:0.#} m range ({cellAngle:0.###}°). " +
                   $"Nearby rays will hit the same cells, costing CPU for no extra detail. All {count} rays are still used; " +
                   (suggested >= MinRays
                       ? $"{suggested} or fewer would avoid the overlap."
                       : $"even the minimum of {MinRays} rays overlaps in a view this narrow.");
        }

        static int LargestCountWithoutOverlap(float halfAngle, float cellAngle)
        {
            if (halfAngle >= MaxHalfAngle)
                return Mathf.Min(MaxRays, Mathf.FloorToInt(360f / cellAngle));

            // Partial: the denser (left) side holds count/2 rays, spacing halfAngle / (count/2) >= cellAngle.
            int perSide = Mathf.FloorToInt(halfAngle / cellAngle);
            return Mathf.Min(MaxRays, 2 * perSide + 1);
        }
    }
}
