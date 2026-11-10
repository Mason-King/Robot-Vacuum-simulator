using UnityEngine;

namespace RobotVacuum.Sim.Sensors
{
    /// <summary>
    /// The vacuum resistance sensor (design doc 5.5): how hard the floor directly under the robot is to clean, as
    /// the brush feels it (the floor type's cleaning effort: 1 = normal, higher = harder, e.g. carpet). Reads the
    /// cell under the robot only. On/off setting. Noisy in SLAM (σ in <see cref="SensorNoiseProfile"/>).
    /// </summary>
    public sealed class FloorSensor : IRobotSensor
    {
        /// <summary>The lowest resistance a reading can report: rate formulas divide by it.</summary>
        public const float MinReading = 0.01f;

        public FloorSensor(bool enabled) => Enabled = enabled;

        public bool Enabled { get; }

        public void Sense(in SensorContext context, SensorFrame frame)
        {
            if (!Enabled) return;

            var floor = context.level != null ? context.level.FloorTypeAtWorld(context.truePosition) : null;
            float resistance = floor != null && floor.cleaningEffort > 0f ? floor.cleaningEffort : 1f;

            if (context.slam)
                resistance *= 1f + context.Noise(SensorNoiseProfile.FloorSensorSigmaFraction);

            frame.hasVacuumResistance = true;
            frame.vacuumResistance = Mathf.Max(MinReading, resistance);
        }
    }
}
