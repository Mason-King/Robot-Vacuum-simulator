using NUnit.Framework;
using RobotVacuum.LevelEditor;
using RobotVacuum.Sim;
using UnityEngine;

namespace RobotVacuum.Tests
{
    public class VacuumSettingsTests
    {
        GameObject host;
        VacuumRobot robot;
        Battery battery;

        [SetUp]
        public void SetUp()
        {
            host = new GameObject("Settings test vacuum");
            robot = host.AddComponent<VacuumRobot>();
            battery = host.GetComponent<Battery>();
        }

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(host);

        [Test]
        public void Robot_KeepsItsBattery_ButNotTheOnScreenDisplays()
        {
            Assert.IsNotNull(battery);
            Assert.IsNull(host.GetComponent<BatteryDisplay>());
            Assert.IsNull(host.GetComponent<SpeedDisplay>());
        }

        [Test]
        public void From_ReadsTheRobotsCurrentSettings()
        {
            robot.SetDriveSpeedMetersPerSecond(0.8f);
            robot.SetReverseSpeedMetersPerSecond(0.4f);
            robot.SetTurnSpeedDegreesPerSecond(200f);
            battery.SetCapacity(120f);

            var settings = VacuumSettings.From(robot, battery);

            Assert.AreEqual(0.8f, settings.driveSpeed, 1e-5f);
            Assert.AreEqual(0.4f, settings.reverseSpeed, 1e-5f);
            Assert.AreEqual(200f, settings.turnSpeed, 1e-5f);
            Assert.AreEqual(120f, settings.batteryCapacity, 1e-5f);
            Assert.IsFalse(settings.useFeet);
        }

        [Test]
        public void ApplyTo_SetsSpeeds_AndOnlyRechargesWhenCapacityChanges()
        {
            battery.SetCapacity(100f);
            battery.ConsumeWork(40f, 0f, 1f);
            var settings = new VacuumSettings { driveSpeed = 1.2f, reverseSpeed = 0.5f, turnSpeed = 400f, batteryCapacity = 100f };

            settings.ApplyTo(robot, battery);

            Assert.AreEqual(1.2f, robot.DriveSpeedMetersPerSecond, 1e-5f);
            Assert.AreEqual(0.5f, robot.ReverseSpeedMetersPerSecond, 1e-5f);
            Assert.AreEqual(400f, robot.TurnSpeedDegreesPerSecond, 1e-5f);
            Assert.AreEqual(60f, battery.CurrentCharge, 1e-4f, "an unchanged capacity must not recharge");

            settings.batteryCapacity = 250f;
            settings.ApplyTo(robot, battery);

            Assert.AreEqual(250f, battery.Capacity, 1e-5f);
            Assert.AreEqual(250f, battery.CurrentCharge, 1e-5f);
        }

        [Test]
        public void ApplyTo_WithoutABattery_StillSetsSpeeds()
        {
            new VacuumSettings { driveSpeed = 0.3f, reverseSpeed = 0.2f, turnSpeed = 90f }.ApplyTo(robot, null);

            Assert.AreEqual(0.3f, robot.DriveSpeedMetersPerSecond, 1e-5f);
        }

        [TestCase(false, 1f, "1.00 m/s")]
        [TestCase(true, 1f, "3.28 ft/s")]
        [TestCase(true, 0f, "0.00 ft/s")]
        public void FormatSpeed_UsesTheChosenUnit(bool feet, float metresPerSecond, string expected)
        {
            Assert.AreEqual(expected, new VacuumSettings { useFeet = feet }.FormatSpeed(metresPerSecond));
        }

        [TestCase(0f, "00:00")]
        [TestCase(65.4f, "01:05")]
        [TestCase(-3f, "00:00")]
        public void FormatDuration_IsMinutesAndSeconds(float seconds, string expected)
        {
            Assert.AreEqual(expected, VacuumSettings.FormatDuration(seconds));
        }

        [TestCase(300f, 300f, "100% · 300 units")]
        [TestCase(75f, 300f, "25% · 75 units")]
        [TestCase(0f, 300f, "Empty")]
        [TestCase(0f, 0f, "Empty")]
        public void FormatBattery_ShowsPercentAndCharge(float current, float capacity, string expected)
        {
            Assert.AreEqual(expected, VacuumSettings.FormatBattery(current, capacity));
        }

        [TestCase(60f, 300f, true)]
        [TestCase(61f, 300f, false)]
        [TestCase(0f, 0f, false)]
        public void IsBatteryLow_AtOneFifthOrLess(float current, float life, bool expected)
        {
            Assert.AreEqual(expected, VacuumSettings.IsBatteryLow(current, life));
        }
    }
}
