using UnityEngine;

namespace RobotVacuum.Sim.Sensors
{
    /// <summary>
    /// The whisker / bumper: always on. Reports two things:
    ///   * a bump — the body touched something this step, or something is right in front of it (a short
    ///     forward sweep, the same check the old VacuumRobot used). A bump is certain: never noisy, even in SLAM;
    ///   * the floor's ease — how easily the floor under the robot lets it move, as felt through the drive
    ///     (the floor type's speed multiplier: 1 = normal, lower = carpet). Noisy in SLAM.
    /// </summary>
    public sealed class WhiskerSensor : IRobotSensor
    {
        /// <summary>How far ahead the forward sweep looks, metres (the old VacuumRobot's lookAhead).</summary>
        public const float LookAheadMeters = 0.09f;

        /// <summary>The sweep's radius as a fraction of the body radius (slightly narrower, so side-scrapes don't count).</summary>
        public const float SweepRadiusFraction = 0.95f;

        readonly RaycastHit2D[] hits = new RaycastHit2D[8];

        public bool Enabled => true;

        public void Sense(in SensorContext context, SensorFrame frame)
        {
            SenseBump(context, frame);
            SenseFloorEase(context, frame);
        }

        void SenseBump(in SensorContext context, SensorFrame frame)
        {
            // A real collision this step wins: it carries the exact contact point.
            if (context.collidedThisStep)
            {
                frame.bumped = true;
                frame.bumpBearing = BearingTo(context, context.collisionPoint);
                frame.bumpDistance = Vector2.Distance(context.truePosition, context.collisionPoint);
                return;
            }

            var filter = ContactFilter2D.noFilter;
            filter.useTriggers = false;

            Vector2 forward = context.WorldDirection(0f);
            int count = Physics2D.CircleCast(context.truePosition, context.robotRadius * SweepRadiusFraction,
                forward, filter, hits, LookAheadMeters);

            for (int i = 0; i < count; i++)
            {
                var hit = hits[i];
                if (hit.collider == null) continue;
                if (context.self != null && hit.collider.attachedRigidbody == context.self) continue;

                frame.bumped = true;
                frame.bumpBearing = BearingTo(context, hit.point);
                frame.bumpDistance = Vector2.Distance(context.truePosition, hit.point);
                return;
            }
        }

        void SenseFloorEase(in SensorContext context, SensorFrame frame)
        {
            var floor = context.level != null ? context.level.FloorTypeAtWorld(context.truePosition) : null;
            float ease = floor != null ? floor.speedMultiplier : 1f;

            if (context.slam)
                ease *= 1f + context.Noise(SensorNoiseProfile.WhiskerDragSigmaFraction);

            frame.floorEase = Mathf.Max(0.01f, ease); // a felt drag is never "impassable"; only bumps make 0
        }

        /// <summary>Bearing of a world point in the robot's frame: degrees CCW from straight ahead, in (-180, 180].</summary>
        static float BearingTo(in SensorContext context, Vector2 worldPoint)
        {
            Vector2 offset = worldPoint - context.truePosition;
            if (offset.sqrMagnitude < 1e-10f) return 0f;

            Vector2 forward = context.WorldDirection(0f);
            return Vector2.SignedAngle(forward, offset); // Unity's SignedAngle is CCW-positive, matching our convention
        }
    }
}
