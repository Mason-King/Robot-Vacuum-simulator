using NUnit.Framework;
using RobotVacuum;
using RobotVacuum.Level;
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
            battery.Capacity = 10f;
            battery.ResetCharge();
        }

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(vacuum);

        [Test]
        public void ConsumeWork_ChargesDistanceAndTurningWithSurfaceResistance()
        {
            battery.ConsumeWork(2f, 90f, 2f);

            Assert.AreEqual(4.2f, battery.CurrentCharge, 1e-5f);
            Assert.IsTrue(battery.CanOperate);

            battery.ConsumeWork(10f, 0f, 1f);
            Assert.AreEqual(0f, battery.CurrentCharge, 1e-5f);
            Assert.IsFalse(battery.CanOperate);
        }

        [Test]
        public void DefaultCarpetUsesMoreChargeThanHardFloorForTheSameWork()
        {
            FloorPalette palette = FloorPalette.CreateDefault();
            try
            {
                FloorType hardwood = palette.Get(0);
                FloorType highCarpet = palette.Get(4);
                battery.ConsumeWork(1f, 0f, hardwood.energyCostMultiplier);
                float hardwoodCharge = battery.CurrentCharge;

                battery.ResetCharge();
                battery.ConsumeWork(1f, 0f, highCarpet.energyCostMultiplier);

                Assert.Less(battery.CurrentCharge, hardwoodCharge);
            }
            finally
            {
                foreach (FloorType floor in palette.Entries) Object.DestroyImmediate(floor);
                Object.DestroyImmediate(palette);
            }
        }

        [Test]
        public void ResetCharge_RestoresConfiguredCapacity()
        {
            battery.ConsumeWork(10f, 0f, 1f);

            battery.ResetCharge();

            Assert.AreEqual(10f, battery.CurrentCharge, 1e-5f);
            Assert.IsTrue(battery.CanOperate);
        }

        [Test]
        public void Capacity_ClampsToNonNegative()
        {
            battery.Capacity = -5f;

            Assert.AreEqual(0f, battery.Capacity, 1e-5f);
            Assert.AreEqual(0f, battery.CurrentCharge, 1e-5f);
            Assert.IsFalse(battery.CanOperate);
        }

        [Test]
        public void SetCapacity_RechargesDepletedBattery()
        {
            battery.ConsumeWork(10f, 0f, 1f);

            battery.SetCapacity(25f);

            Assert.AreEqual(25f, battery.Capacity, 1e-5f);
            Assert.AreEqual(25f, battery.CurrentCharge, 1e-5f);
            Assert.IsTrue(battery.CanOperate);
        }
    }
}