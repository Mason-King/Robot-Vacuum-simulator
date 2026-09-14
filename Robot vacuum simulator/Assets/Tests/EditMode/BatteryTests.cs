using NUnit.Framework;
using RobotVacuum;
using UnityEngine;

namespace RobotVacuum.Tests
{
    public class BatteryTests
    {
        GameObject vacuum;
        Battery battery;

        [SetUp]
        public void SetUp()
        {
            vacuum = new GameObject("Battery test vacuum");
            battery = vacuum.AddComponent<Battery>();
            battery.BatteryLifeSeconds = 10f;
            battery.ResetBattery();
        }

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(vacuum);

        [Test]
        public void Consume_ReducesLife_AndStopsAtZero()
        {
            battery.Consume(3.5f);
            Assert.AreEqual(6.5f, battery.CurrentLifeSeconds, 1e-5f);
            Assert.IsTrue(battery.CanOperate);

            battery.Consume(10f);
            Assert.AreEqual(0f, battery.CurrentLifeSeconds, 1e-5f);
            Assert.IsFalse(battery.CanOperate);
        }

        [Test]
        public void ResetBattery_RestoresConfiguredLife()
        {
            battery.Consume(10f);

            battery.ResetBattery();

            Assert.AreEqual(10f, battery.CurrentLifeSeconds, 1e-5f);
            Assert.IsTrue(battery.CanOperate);
        }

        [Test]
        public void BatteryLifeSeconds_ClampsToNonNegative()
        {
            battery.BatteryLifeSeconds = -5f;

            Assert.AreEqual(0f, battery.BatteryLifeSeconds, 1e-5f);
            Assert.AreEqual(0f, battery.CurrentLifeSeconds, 1e-5f);
            Assert.IsFalse(battery.CanOperate);
        }

        [Test]
        public void SetBatteryLifeSeconds_RechargesDepletedBattery()
        {
            battery.Consume(10f);

            battery.SetBatteryLifeSeconds(25f);

            Assert.AreEqual(25f, battery.BatteryLifeSeconds, 1e-5f);
            Assert.AreEqual(25f, battery.CurrentLifeSeconds, 1e-5f);
            Assert.IsTrue(battery.CanOperate);
        }
    }
}