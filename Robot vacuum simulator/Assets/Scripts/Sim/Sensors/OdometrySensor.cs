using UnityEngine;

namespace RobotVacuum.Sim.Sensors
{
    /// <summary>
    /// Wheel odometry: how far the robot drove along its own axis, and how far it turned, since the last physics
    /// step. Used ONLY with SLAM on (in a Normal run the believed pose is the true pose and this is never called).
    ///
    /// What the wheels can and can't feel:
    ///   * driving forward or back along the body's axis: measured;
    ///   * turning: measured;
    ///   * being pushed sideways (by the cat, or sliding along a wall): NOT measured -- wheels don't turn for it.
    ///
    /// Noise (SLAM only, × the noise scale), sizes in <see cref="SensorNoiseProfile"/>: a random walk on distance
    /// (worse on carpet: × 1 / floor ease, i.e. slip) and on heading. Draws come from the run's own Odometry stream,
    /// so switching another sensor on or off doesn't change the odometry's errors.
    /// </summary>
    public sealed class OdometrySensor
    {
        Vector2 lastPosition;
        float lastHeading;
        bool hasSample;

        /// <summary>Starts measuring from this (true) pose; call when the run starts.</summary>
        public void Reset(Vector2 truePosition, float trueHeading)
        {
            lastPosition = truePosition;
            lastHeading = trueHeading;
            hasSample = true;
        }

        /// <summary>
        /// The motion since the last call. <paramref name="forwardMeters"/> is along the body's axis (negative =
        /// reversing); <paramref name="turnDegrees"/> is counter-clockwise positive.
        /// </summary>
        /// <param name="floorEase">The ease of the floor really under the robot (its speed multiplier), for slip.</param>
        public void Measure(Vector2 truePosition, float trueHeading, float floorEase, bool slam, float noiseScale,
            RandomProvider random, out float forwardMeters, out float turnDegrees)
        {
            if (!hasSample) Reset(truePosition, trueHeading);

            Vector2 moved = truePosition - lastPosition;
            turnDegrees = Mathf.DeltaAngle(lastHeading, trueHeading);

            // Only motion along the body's axis turns the wheels; the axis is taken halfway through the turn.
            forwardMeters = Vector2.Dot(moved, SensorContext.HeadingToDirection(lastHeading + turnDegrees * 0.5f));

            lastPosition = truePosition;
            lastHeading = trueHeading;

            if (!slam || random == null) return;

            float slip = Mathf.Min(SensorNoiseProfile.OdometryMaxSlipFactor, 1f / Mathf.Max(0.0001f, floorEase));

            float distanceSigma = SensorNoiseProfile.OdometryDistanceSigmaFraction * noiseScale * slip *
                                  Mathf.Sqrt(Mathf.Abs(forwardMeters) * SensorNoiseProfile.OdometryDistanceReferenceMeters);
            float headingSigma = SensorNoiseProfile.OdometryHeadingSigmaFraction * noiseScale *
                                 Mathf.Sqrt(Mathf.Abs(turnDegrees) * SensorNoiseProfile.OdometryHeadingReferenceDegrees);

            // Always two draws each, even when a sigma is 0, so the stream stays in step however the robot moved.
            forwardMeters += (float)random.NextGaussian(RandomProvider.Subsystem.Odometry, 0.0, distanceSigma);
            turnDegrees += (float)random.NextGaussian(RandomProvider.Subsystem.Odometry, 0.0, headingSigma);
        }
    }
}
