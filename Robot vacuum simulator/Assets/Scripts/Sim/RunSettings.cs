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

        [Header("Advanced")]
        [Tooltip("Grid cell edge length in metres, shared by both models. 0 = default (SimDefaults.CellSizeMeters, 2 inches).")]
        public float cellSizeMeters;

        [Tooltip("Cleanliness gained per second of dwell on a floor with a cleaning effort of 1. 0 = default (HeatmapDefaults.BaseVacuumPower).")]
        public float vacuumPower;

        /// <summary>An independent copy, so one run's settings can be edited without touching another's.</summary>
        public RunSettings Copy() => (RunSettings)MemberwiseClone();

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
