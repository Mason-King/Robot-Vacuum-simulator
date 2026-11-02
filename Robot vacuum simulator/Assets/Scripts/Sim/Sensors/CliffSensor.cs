// ============================================================================================================
// CLIFF SENSOR — BUILT BUT COMMENTED OUT ON PURPOSE (decided 2026-11-01).
//
// Why it is commented out: the Level Object (LevelData) has no drop / cliff / stairs data yet, so this sensor
// would always read "floor present" and only cost CPU. The lidar can't do this job: it scans one horizontal
// plane at bumper height, so like a real robot's lidar it cannot see a drop. Real robots use small downward
// sensors under the front edge instead, and requirement 3.4 names cliff sensors explicitly.
//
// To enable it once the Level Editor can author drops:
//   1. Give LevelData a way to answer "is this point a drop?" and replace the IsDropAt placeholder below.
//   2. Remove the /* and */ around the class.
//   3. Uncomment the cliffPoints list and its Clear() line in SensorFrame (SensorTypes.cs).
//   4. Add the sensor to the controller's sensor list.
// The controller then treats a "no floor" reading like a bump: the cell ahead becomes impassable (0) at once.
// ============================================================================================================

namespace RobotVacuum.Sim.Sensors
{
/*
    /// <summary>One downward point under the robot's front edge.</summary>
    public struct CliffPoint
    {
        /// <summary>Where the point sits, in the robot's frame: degrees CCW from straight ahead.</summary>
        public float bearing;

        /// <summary>True if there is floor under the point; false = a drop.</summary>
        public bool floorPresent;
    }

    /// <summary>
    /// Downward "is there floor here?" points along the robot's front edge. A drop reading is certain (like a
    /// bump) — a robot that doubts its cliff sensor falls down the stairs — so it is never made noisy.
    /// </summary>
    public sealed class CliffSensor : IRobotSensor
    {
        /// <summary>Bearings of the points along the front edge, degrees CCW from straight ahead.</summary>
        static readonly float[] PointBearings = { -45f, -15f, 15f, 45f };

        /// <summary>How far in from the body's edge the points sit, as a fraction of the radius.</summary>
        const float EdgeFraction = 0.9f;

        public CliffSensor(bool enabled) => Enabled = enabled;

        public bool Enabled { get; }

        public void Sense(in SensorContext context, SensorFrame frame)
        {
            if (!Enabled) return;

            foreach (float bearing in PointBearings)
            {
                Vector2 point = context.truePosition + context.WorldDirection(bearing) * (context.robotRadius * EdgeFraction);
                frame.cliffPoints.Add(new CliffPoint { bearing = bearing, floorPresent = !IsDropAt(context, point) });
            }
        }

        // PLACEHOLDER: replace with the Level Object's real drop query once drops exist.
        static bool IsDropAt(in SensorContext context, UnityEngine.Vector2 worldPoint) => false;
    }
*/
}
