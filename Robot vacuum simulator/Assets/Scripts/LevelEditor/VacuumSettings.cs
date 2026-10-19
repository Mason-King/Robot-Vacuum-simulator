using RobotVacuum.Sim;
using UnityEngine;

namespace RobotVacuum.LevelEditor
{
    /// <summary>
    /// The vacuum's tunable drive and battery settings. The editor keeps one of these so changes
    /// carry over from run to run, even though every run builds a fresh robot.
    /// </summary>
    public sealed class VacuumSettings
    {
        public const float MetresToFeet = 3.28084f;

        public float driveSpeed;
        public float reverseSpeed;
        public float turnSpeed;
        public float batteryCapacity;
        public bool useFeet;

        /// <summary>Reads the settings a robot currently has.</summary>
        public static VacuumSettings From(VacuumRobot robot, Battery battery) => new VacuumSettings
        {
            driveSpeed = robot.DriveSpeedMetersPerSecond,
            reverseSpeed = robot.ReverseSpeedMetersPerSecond,
            turnSpeed = robot.TurnSpeedDegreesPerSecond,
            batteryCapacity = battery != null ? battery.Capacity : 0f,
        };

        /// <summary>
        /// Pushes every setting onto a robot. Setting a different battery life recharges the battery,
        /// so an unchanged life is left alone rather than topping the battery up.
        /// </summary>
        public void ApplyTo(VacuumRobot robot, Battery battery)
        {
            robot.SetDriveSpeedMetersPerSecond(driveSpeed);
            robot.SetReverseSpeedMetersPerSecond(reverseSpeed);
            robot.SetTurnSpeedDegreesPerSecond(turnSpeed);

            if (battery != null && !Mathf.Approximately(battery.Capacity, batteryCapacity))
                battery.SetCapacity(batteryCapacity);
        }

        public string FormatSpeed(float metresPerSecond) =>
            useFeet ? $"{metresPerSecond * MetresToFeet:0.00} ft/s" : $"{metresPerSecond:0.00} m/s";

        public static string FormatDuration(float seconds)
        {
            var span = System.TimeSpan.FromSeconds(Mathf.Max(0f, seconds));
            return $"{(int)span.TotalMinutes:00}:{span.Seconds:00}";
        }

        public static string FormatBattery(float currentCharge, float capacity)
        {
            if (currentCharge <= 0f) return "Empty";

            float percent = capacity > 0f ? currentCharge / capacity * 100f : 0f;
            return $"{percent:0}% · {currentCharge:0} units";
        }

        public static bool IsBatteryLow(float currentCharge, float capacity) =>
            capacity > 0f && currentCharge <= capacity * 0.2f;

        public static string FormatCapacity(float capacity) => $"{capacity:0} units";
    }
}
