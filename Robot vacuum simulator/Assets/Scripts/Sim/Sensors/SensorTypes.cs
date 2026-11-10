using System.Collections.Generic;
using RobotVacuum.Level;
using UnityEngine;

namespace RobotVacuum.Sim.Sensors
{
    /// <summary>
    /// Every sensor has this shape, so the controller treats them all alike. A sensor measures the world from
    /// the robot's TRUE pose (sensors are physical objects in the scene) and writes its readings into the
    /// frame. It never touches the Internal Model: the controller turns readings into beliefs, using where the
    /// robot BELIEVES it is.
    /// </summary>
    public interface IRobotSensor
    {
        bool Enabled { get; }

        /// <summary>Measures this physics step and writes the readings into <paramref name="frame"/>.</summary>
        void Sense(in SensorContext context, SensorFrame frame);
    }

    /// <summary>What a sensor needs to take one measurement. Built fresh by the controller every physics step.</summary>
    public readonly struct SensorContext
    {
        /// <summary>Where the robot really is, in world metres. Sensors measure from here.</summary>
        public readonly Vector2 truePosition;

        /// <summary>Where the robot really faces: degrees counter-clockwise, 0 = +y (the VacuumRobot convention).</summary>
        public readonly float trueHeading;

        /// <summary>The robot's body radius, metres.</summary>
        public readonly float robotRadius;

        /// <summary>The robot's own body, so its collider is never mistaken for an obstacle.</summary>
        public readonly Rigidbody2D self;

        /// <summary>The level as drawn in the scene, for floor-type lookups. May be null in bare tests.</summary>
        public readonly LevelRenderer level;

        /// <summary>True if the body touched something during the last physics step (a real collision).</summary>
        public readonly bool collidedThisStep;

        /// <summary>Where that collision touched, in world metres. Meaningless when <see cref="collidedThisStep"/> is false.</summary>
        public readonly Vector2 collisionPoint;

        /// <summary>Simulated seconds since the run started.</summary>
        public readonly float simTime;

        /// <summary>SLAM on: sensors apply noise. SLAM off: exact readings, and <see cref="noiseScale"/> is never read.</summary>
        public readonly bool slam;

        /// <summary>The run's noise scale. Read ONLY when <see cref="slam"/> is true.</summary>
        public readonly float noiseScale;

        /// <summary>The run's random streams. Sensors draw from <see cref="RandomProvider.Subsystem.SensorNoise"/>.</summary>
        public readonly RandomProvider random;

        public SensorContext(Vector2 truePosition, float trueHeading, float robotRadius, Rigidbody2D self,
            LevelRenderer level, bool collidedThisStep, Vector2 collisionPoint, float simTime,
            bool slam, float noiseScale, RandomProvider random)
        {
            this.truePosition = truePosition;
            this.trueHeading = trueHeading;
            this.robotRadius = robotRadius;
            this.self = self;
            this.level = level;
            this.collidedThisStep = collidedThisStep;
            this.collisionPoint = collisionPoint;
            this.simTime = simTime;
            this.slam = slam;
            this.noiseScale = noiseScale;
            this.random = random;
        }

        /// <summary>A world direction for a bearing in the robot's frame (degrees CCW from straight ahead), using the TRUE heading.</summary>
        public Vector2 WorldDirection(float bearingDegrees) => HeadingToDirection(trueHeading + bearingDegrees);

        /// <summary>Unit vector for a heading in the VacuumRobot convention (degrees CCW, 0 = +y).</summary>
        public static Vector2 HeadingToDirection(float headingDegrees)
        {
            float radians = headingDegrees * Mathf.Deg2Rad;
            return new Vector2(-Mathf.Sin(radians), Mathf.Cos(radians));
        }

        /// <summary>A standard-normal draw scaled by <paramref name="sigma"/> × the noise scale. Only call when <see cref="slam"/> is true.</summary>
        public float Noise(float sigma) =>
            (float)random.NextGaussian(RandomProvider.Subsystem.SensorNoise, 0.0, sigma * noiseScale);

        /// <summary>True with probability <paramref name="chance"/>. Only call when <see cref="slam"/> is true.</summary>
        public bool Chance(float chance) =>
            random.NextDouble(RandomProvider.Subsystem.SensorNoise) < chance;
    }

    /// <summary>One lidar ray's reading, in the robot's frame.</summary>
    public struct LidarRay
    {
        /// <summary>The direction the robot believes this ray pointed: degrees CCW from straight ahead (its nominal bearing).</summary>
        public float bearing;

        /// <summary>Measured distance from the robot's centre, metres. Equals the lidar's range when nothing was hit.</summary>
        public float range;

        /// <summary>True if the ray reported an obstacle at <see cref="range"/>; false = clear all the way to the lidar's range.</summary>
        public bool hit;
    }

    /// <summary>
    /// One physics step's readings from every sensor. Owned by the controller and reused every step (no
    /// allocation per step); <see cref="Clear"/> resets it before the sensors run.
    /// </summary>
    public sealed class SensorFrame
    {
        // ---- Lidar ----
        /// <summary>True if the lidar took a scan this step (it scans at a fixed rate, not every step).</summary>
        public bool hasLidarScan;

        /// <summary>The scan's rays, in layout order (straight ahead first). Valid only when <see cref="hasLidarScan"/>.</summary>
        public readonly List<LidarRay> lidarRays = new List<LidarRay>(360);

        /// <summary>The lidar's range for this scan, metres, so a reader can tell "clear to range" apart from a hit.</summary>
        public float lidarRange;

        // ---- Whisker ----
        /// <summary>True if the robot is touching something, or something is right in front of it. Certain: never noisy.</summary>
        public bool bumped;

        /// <summary>Direction of the contact in the robot's frame: degrees CCW from straight ahead. Valid only when <see cref="bumped"/>.</summary>
        public float bumpBearing;

        /// <summary>Distance from the robot's centre to the contact point, metres. Valid only when <see cref="bumped"/>.</summary>
        public float bumpDistance;

        /// <summary>
        /// How easily the floor under the robot lets it move, as felt through the drive: 1 = normal, lower =
        /// harder (carpet). Noisy with SLAM on.
        /// </summary>
        public float floorEase = 1f;

        // ---- Vacuum resistance (floor) sensor ----
        /// <summary>True if the floor sensor took a reading this step (it is off when disabled).</summary>
        public bool hasVacuumResistance;

        /// <summary>How hard the floor under the robot is to clean: 1 = normal, higher = harder. Noisy with SLAM on.</summary>
        public float vacuumResistance = 1f;

        // ---- Cliff sensor (commented out until the Level Object has drops; see CliffSensor.cs) ----
        // public readonly List<CliffPoint> cliffPoints = new List<CliffPoint>(4);

        public void Clear()
        {
            hasLidarScan = false;
            lidarRays.Clear();
            lidarRange = 0f;
            bumped = false;
            bumpBearing = 0f;
            bumpDistance = 0f;
            floorEase = 1f;
            hasVacuumResistance = false;
            vacuumResistance = 1f;
            // cliffPoints.Clear();
        }
    }
}
