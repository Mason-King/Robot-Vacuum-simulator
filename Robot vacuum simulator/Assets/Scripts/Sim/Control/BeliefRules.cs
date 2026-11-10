using UnityEngine;
using C = RobotVacuum.Sim.Control.BeliefUpdateConstants;

namespace RobotVacuum.Sim.Control
{
    /// <summary>
    /// The SLAM belief-update rule (design doc, section 6.2), as pure functions so it can be tested on its own.
    /// SLAM ONLY: in a Normal run the controller assigns observations directly and never calls these.
    ///
    ///   α      = α_base × w(r)
    ///   if |observed − belief| > 0.5:  α = min(α × 3, 0.9)     (drastic contradiction → adapt fast)
    ///   belief += α × (observed − belief)
    ///   traversability only: if belief &lt; 0.1, belief = 0     (snap to impassable)
    ///
    /// Recency is built in: the newest reading always carries weight α and older ones fade by (1 − α) per reading.
    /// </summary>
    public static class BeliefRules
    {
        /// <summary>w(r): 1 at or inside 1 m, falling linearly to 0.25 at the lidar's maximum range.</summary>
        public static float DistanceWeight(float range, float maxRange)
        {
            if (range <= C.FullWeightRangeMeters || maxRange <= C.FullWeightRangeMeters) return 1f;

            float t = Mathf.Clamp01((range - C.FullWeightRangeMeters) / (maxRange - C.FullWeightRangeMeters));
            return Mathf.Lerp(1f, C.WeightAtMaxRange, t);
        }

        /// <summary>One weighted step of <paramref name="belief"/> toward <paramref name="observed"/>.</summary>
        /// <param name="alphaBase">The reading's base step: <see cref="C.HitAlpha"/>, <see cref="C.ClearAlpha"/> or <see cref="C.DragAlpha"/>.</param>
        /// <param name="range">Distance from the robot to the observed cell, metres (0 for the cell under the robot).</param>
        /// <param name="maxRange">The lidar's maximum range, metres.</param>
        /// <param name="snapToZero">True for traversability (snap to impassable); false for floor ease and vacuum resistance.</param>
        public static float Step(float belief, float observed, float alphaBase, float range, float maxRange, bool snapToZero)
        {
            float difference = observed - belief;
            float alpha = alphaBase * DistanceWeight(range, maxRange);
            if (Mathf.Abs(difference) > C.ContradictionThreshold) alpha = Mathf.Min(alpha * C.ContradictionBoost, C.MaxAlpha);

            float next = belief + alpha * difference;
            if (snapToZero && next < C.SnapToZeroBelow) next = 0f;
            return next;
        }
    }
}
