using System;
using UnityEngine;

namespace RobotVacuum.Sim
{
    /// <summary>
    /// Everything a caller can choose about one run, before it starts. Any value left unset uses its
    /// default: for numbers, zero or less means "unset", since none of these can meaningfully be zero.
    /// The seed is the exception (zero is a valid seed), so it has its own <see cref="randomSeed"/> switch.
    ///
    /// The same type is used by every caller -- the PlayScene test driver, the Setup screen, and the
    /// headless job -- so a run is described the same way no matter what started it. It holds no UI and
    /// no references to scene objects, only plain values, so it serializes straight into config.json.
    /// </summary>
    [Serializable]
    public class RunSettings
    {
        [Header("Run")]
        [Tooltip("The movement algorithm the vacuum uses.")]
        public MovementPattern pattern = MovementPattern.RandomBounce;

        [Tooltip("On: a fresh random seed every run. Off: use Seed below. Either way, the seed actually used is saved in the run's config.json, so the run can be repeated.")]
        public bool randomSeed = true;

        [Tooltip("Used when Random Seed is off. The same level, settings and seed give the same run.")]
        public int seed;

        [Tooltip("target_max_clean: the cleanliness each cell is aiming for. Fixed for the whole run. 0 = default (HeatmapDefaults.TargetMaxClean).")]
        public float targetMaxClean;

        [Tooltip("Which heatmap view the run starts in. It can still be switched during the run.")]
        public HeatmapMode heatmapMode = HeatmapMode.FloorCleanliness;

        [Header("Vacuum")]
        [Tooltip("Forward speed in m/s. 0 = the robot's own default.")]
        public float driveSpeed;

        [Tooltip("Reverse speed in m/s. 0 = the robot's own default.")]
        public float reverseSpeed;

        [Tooltip("Turn speed in degrees per second. 0 = the robot's own default.")]
        public float turnSpeed;

        [Tooltip("Battery capacity in work units. 0 = the battery's own default.")]
        public float batteryCapacity;

        [Header("Sensing")]
        [Tooltip("SLAM on: the robot starts with no map, its sensors and odometry are noisy, and it must build its map and track its own position. SLAM off (Normal): the map is preloaded and readings are exact.")]
        public bool slam;

        [Tooltip("How noisy the sensors are in a SLAM run: 0 = perfect, 1 = a realistic budget robot, higher = worse (max 10). Used ONLY when SLAM is on; a Normal run never reads it. Unlike most settings here, 0 is a real value, not 'default'.")]
        public float noiseScale = 1f;

        [Tooltip("Lidar on/off. Off = the robot senses by whisker only.")]
        public bool lidarEnabled = true;

        [Tooltip("Lidar maximum range in metres. 0 = default (8 m). Never below 0.2 m.")]
        public float lidarRangeMeters;

        [Tooltip("Lidar view half-angle in degrees, measured from straight ahead and spread equally to both sides: 180 = full circle, 0 = no rays (whisker only). Clamped to 0–180. Unlike most settings here, 0 is a real value.")]
        public float lidarHalfAngleDegrees = 180f;

        [Tooltip("Number of lidar rays, 8–360, used exactly as set. One ray always points straight ahead. Lower it if 360 is too slow; a warning appears if the rays are packed closer than the grid can use.")]
        public int lidarRayCount = 360;

        [Tooltip("The vacuum resistance (floor) sensor on/off: it reads how hard the floor under the robot is to clean. Off in a SLAM run = the robot assumes every floor cleans normally.")]
        public bool floorSensorEnabled = true;

        [Header("Debug")]
        [Tooltip("Draws what the robot believes (its Internal Model, believed position and current waypoint) over the floor. Graphics runs only. Can also be switched during a run on the 'Belief Overlay (debug)' object.")]
        public bool showBeliefOverlay;

        [Header("Advanced")]
        [Tooltip("SLAM only: how many lidar returns each scan match uses. Performance = every 3rd, least CPU; Balanced = every 2nd, the default; Reliable = every return, most CPU.")]
        public Control.PoseCorrectionQuality slamCorrectionQuality = Control.PoseCorrectionQuality.Balanced;

        [Tooltip("SLAM only: loop closure. When the robot comes back somewhere it has been, it recognises the place and corrects the drift built up since, across its whole path and map. On by default; switch off to see pose correction alone.")]
        public bool slamLoopClosure = true;

        [Tooltip("Grid cell edge length in metres, shared by both models. 0 = default (SimDefaults.CellSizeMeters, 2 inches).")]
        public float cellSizeMeters;

        [Tooltip("Cleanliness gained per second of dwell on a floor with a cleaning effort of 1. 0 = default (HeatmapDefaults.BaseVacuumPower).")]
        public float vacuumPower;

        /// <summary>The highest noise scale allowed.</summary>
        public const float MaxNoiseScale = 10f;

        /// <summary>True for a number the caller actually set.</summary>
        public static bool IsSet(float value) => value > 0f;

        /// <summary>
        /// A copy with every unset run value replaced by its default and a random seed turned into the
        /// concrete seed it will use. This is what gets saved in config.json, so the saved settings can
        /// rebuild the same run exactly. The four vacuum values stay as they are here, because their
        /// defaults live on the robot components; <see cref="RecordVacuumFrom"/> fills them in after the
        /// robot is built.
        /// </summary>
        public RunSettings Resolved()
        {
            var resolved = (RunSettings)MemberwiseClone();

            if (resolved.randomSeed)
            {
                resolved.seed = NewRandomSeed();
                resolved.randomSeed = false;
            }

            if (!IsSet(resolved.targetMaxClean)) resolved.targetMaxClean = HeatmapDefaults.TargetMaxClean;
            if (!IsSet(resolved.cellSizeMeters)) resolved.cellSizeMeters = SimDefaults.CellSizeMeters;
            if (!IsSet(resolved.vacuumPower)) resolved.vacuumPower = HeatmapDefaults.BaseVacuumPower;

            // Sensing. The noise scale is touched ONLY when SLAM is on (clamped to 0–MaxNoiseScale). In a Normal
            // run it is neither read nor changed: it is recorded exactly as set, and nothing uses it.
            if (resolved.slam)
                resolved.noiseScale = float.IsNaN(resolved.noiseScale) ? 1f : Mathf.Clamp(resolved.noiseScale, 0f, MaxNoiseScale);
            if (!IsSet(resolved.lidarRangeMeters)) resolved.lidarRangeMeters = Sensors.LidarSensor.DefaultRangeMeters;
            resolved.lidarRangeMeters = Mathf.Max(Sensors.LidarSensor.MinRangeMeters, resolved.lidarRangeMeters);
            resolved.lidarHalfAngleDegrees = Sensors.LidarRayLayout.ClampHalfAngle(resolved.lidarHalfAngleDegrees);
            resolved.lidarRayCount = Sensors.LidarRayLayout.ClampCount(resolved.lidarRayCount);

            return resolved;
        }

        /// <summary>Applies only the vacuum values that were set; anything unset keeps the robot's default.</summary>
        public void ApplyVacuumTo(VacuumRobot robot, Battery battery)
        {
            if (robot != null)
            {
                if (IsSet(driveSpeed)) robot.SetDriveSpeedMetersPerSecond(driveSpeed);
                if (IsSet(reverseSpeed)) robot.SetReverseSpeedMetersPerSecond(reverseSpeed);
                if (IsSet(turnSpeed)) robot.SetTurnSpeedDegreesPerSecond(turnSpeed);
            }

            // Setting a capacity recharges the battery, which is fine here: this runs before the run starts.
            if (battery != null && IsSet(batteryCapacity)) battery.SetCapacity(batteryCapacity);
        }

        /// <summary>
        /// Copies the values the robot actually ended up with into these settings, so config.json records
        /// real numbers rather than "default".
        /// </summary>
        public void RecordVacuumFrom(VacuumRobot robot, Battery battery)
        {
            if (robot != null)
            {
                driveSpeed = robot.DriveSpeedMetersPerSecond;
                reverseSpeed = robot.ReverseSpeedMetersPerSecond;
                turnSpeed = robot.TurnSpeedDegreesPerSecond;
            }

            if (battery != null) batteryCapacity = battery.Capacity;
        }

        // Non-negative, so it reads naturally in config.json. Guid supplies the randomness; the clock is not
        // used, so two runs started in the same tick still get different seeds.
        static int NewRandomSeed() => Guid.NewGuid().GetHashCode() & int.MaxValue;
    }
}
