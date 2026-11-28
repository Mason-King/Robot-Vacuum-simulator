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
        /// <summary>Scans per simulated second. Starting value, to be tuned by testing (CPU vs map freshness). Set in ControllerConstants.</summary>
        public const float ScanRateHz = Control.ControllerConstants.LidarScanRateHz;

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
            Scans++;
            selfCollider = null; // learned again each scan (see CastRay)
            System.Array.Clear(others, 0, otherCount);
            otherCount = 0;

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
            bool timed = LagLog.Enabled;
            long started = timed ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            int count = Physics2D.Raycast(context.truePosition, direction, filter, hits, range);
            if (timed) RaycastMs += (System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            Rays++;
            HitsReturned += count;

            // The nearest hit that isn't the robot. A hit no nearer than the best so far is skipped before asking
            // Unity about its collider (each of those questions is a call into the engine), so the answer is the same
            // as checking every hit, and since Unity returns hits nearest first, everything after the first wall is
            // skipped. The ray starts inside the robot's own collider, so nearly every ray's first hit is the robot:
            // once a scan has seen which collider that is, it is recognised without asking for its rigidbody again.
            float nearest = range;
            for (int i = 0; i < count; i++)
            {
                var hit = hits[i];
                if (hit.distance >= nearest) continue;
                var collider = hit.collider;
                if (collider == null) continue;
                if (selfCollider is object && ReferenceEquals(collider, selfCollider)) continue;
                if (!IsOther(collider))
                {
                    if (context.self != null && collider.attachedRigidbody == context.self)
                    {
                        selfCollider = collider;
                        continue;
                    }
                    if (otherCount < others.Length) others[otherCount++] = collider;
                }
                nearest = hit.distance;
            }
            return nearest;
        }

        Collider2D selfCollider; // the robot's collider, as this scan has met it

        // Colliders this scan has already found not to be the robot (walls, furniture, a cat): the same few are hit
        // by most of the 360 rays, so each is checked once a scan instead of once a ray. Cleared every scan.
        readonly Collider2D[] others = new Collider2D[32];
        int otherCount;

        bool IsOther(Collider2D collider)
        {
            for (int i = 0; i < otherCount; i++)
                if (ReferenceEquals(others[i], collider)) return true;
            return false;
        }

        /// <summary>Diagnostics (the lag log): scans, rays cast, hits Unity returned, and real ms inside Physics2D.Raycast.</summary>
        public int Scans { get; private set; }
        public long Rays { get; private set; }
        public long HitsReturned { get; private set; }
        public double RaycastMs { get; private set; }
    }
}
