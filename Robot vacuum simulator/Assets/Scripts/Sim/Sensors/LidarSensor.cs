using UnityEngine;

namespace RobotVacuum.Sim.Sensors
{
    /// <summary>
    /// The lidar: a ring of rays in one horizontal plane that report the distance to the nearest geometry
    /// (walls, furniture, the cat). It does not see floor type, and as a horizontal scanner it cannot see
    /// drops (that is the cliff sensor's job).
    ///
    /// Scans at a fixed rate (<see cref="ScanRateHz"/>), not every physics step: a full 360-ray scan is the
    /// controller's main CPU cost once the readings are turned into beliefs.
    ///
    /// Noise (SLAM on only), sizes in <see cref="SensorNoiseProfile"/>, all × the noise scale:
    ///   * the physical beam wanders off its nominal direction (bearing jitter), but the reading keeps the
    ///     NOMINAL bearing: the robot believes it pointed where it meant to, which is what makes the sideways
    ///     error grow with distance;
    ///   * the measured range gets an error with a fixed part plus a part proportional to the distance;
    ///   * a hit can be missed outright, more often the farther away it is (by absolute distance).
    /// </summary>
    public sealed class LidarSensor : IRobotSensor
    {
        /// <summary>Scans per simulated second. Starting value, to be tuned by testing (CPU vs map freshness).</summary>
        public const float ScanRateHz = 10f;

        /// <summary>The shortest range the lidar can be set to, metres.</summary>
        public const float MinRangeMeters = 0.2f;

        public const float DefaultRangeMeters = 8f;

        readonly float range;
        readonly float[] bearings;
        readonly int rayCount;
        readonly RaycastHit2D[] hits = new RaycastHit2D[8];
        float nextScanTime;

        /// <param name="enabled">The lidar on/off setting.</param>
        /// <param name="rangeMeters">Maximum range; 0 or less = the default (8 m); never below <see cref="MinRangeMeters"/>.</param>
        /// <param name="halfAngleDegrees">View half-angle, 0–180 (180 = full circle, 0 = no rays).</param>
        /// <param name="requestedRayCount">Ray count; used exactly after clamping to 8–360.</param>
        /// <param name="cellSizeMeters">The grid cell size, only for the overlap warning.</param>
        public LidarSensor(bool enabled, float rangeMeters, float halfAngleDegrees, int requestedRayCount, float cellSizeMeters)
        {
            range = rangeMeters > 0f ? Mathf.Max(MinRangeMeters, rangeMeters) : DefaultRangeMeters;

            float[] layout = null;
            rayCount = LidarRayLayout.Compute(requestedRayCount, halfAngleDegrees, ref layout);
            bearings = layout ?? new float[0];

            // Half-angle 0 means whisker-only: the lidar is effectively off, without errors downstream.
            Enabled = enabled && rayCount > 0;

            if (Enabled)
            {
                string warning = LidarRayLayout.OverlapWarning(requestedRayCount, halfAngleDegrees, cellSizeMeters, range);
                if (warning != null) Debug.LogWarning(warning);
            }
        }

        public bool Enabled { get; }
        public float Range => range;
        public int RayCount => Enabled ? rayCount : 0;

        public void Sense(in SensorContext context, SensorFrame frame)
        {
            if (!Enabled || context.simTime < nextScanTime) return;

            // Schedule from the previous scan time, not "now", so the rate holds steady regardless of step size.
            float interval = 1f / ScanRateHz;
            nextScanTime = nextScanTime <= 0f ? context.simTime + interval : nextScanTime + interval;
            if (nextScanTime <= context.simTime) nextScanTime = context.simTime + interval; // never fall behind into a burst

            frame.hasLidarScan = true;
            frame.lidarRange = range;

            var filter = ContactFilter2D.noFilter;
            filter.useTriggers = false;

            for (int i = 0; i < rayCount; i++)
            {
                float nominal = bearings[i];

                // Where the beam physically goes. Only SLAM perturbs it.
                float beamBearing = context.slam ? nominal + context.Noise(SensorNoiseProfile.LidarBearingSigmaDegrees) : nominal;

                float trueRange = CastRay(context, beamBearing, filter);
                bool hit = trueRange < range;
                float measured = trueRange;

                if (context.slam && hit)
                {
                    float missChance = SensorNoiseProfile.LidarMissChanceAtOneMeter +
                                       SensorNoiseProfile.LidarMissChancePerMeter * Mathf.Max(0f, trueRange - 1f);
                    missChance = Mathf.Min(SensorNoiseProfile.LidarMissChanceCap, missChance * context.noiseScale);

                    if (context.Chance(missChance))
                    {
                        hit = false;
                        measured = range;
                    }
                    else
                    {
                        float sigma = SensorNoiseProfile.LidarRangeSigmaMeters + SensorNoiseProfile.LidarRangeSigmaFraction * trueRange;
                        measured = Mathf.Clamp(trueRange + context.Noise(sigma), 0f, range);
                    }
                }

                frame.lidarRays.Add(new LidarRay { bearing = nominal, range = measured, hit = hit });
            }
        }

        /// <summary>Distance from the robot's centre to the nearest non-self, non-trigger collider along the bearing; <see cref="range"/> if none.</summary>
        float CastRay(in SensorContext context, float bearing, ContactFilter2D filter)
        {
            Vector2 direction = context.WorldDirection(bearing);
            int count = Physics2D.Raycast(context.truePosition, direction, filter, hits, range);

            float nearest = range;
            for (int i = 0; i < count; i++)
            {
                var hit = hits[i];
                if (hit.collider == null) continue;
                if (context.self != null && hit.collider.attachedRigidbody == context.self) continue;
                if (hit.distance < nearest) nearest = hit.distance;
            }
            return nearest;
        }
    }
}
