using NUnit.Framework;
using RobotVacuum.Level;
using RobotVacuum.Sim;
using UnityEngine;
using Object = UnityEngine.Object;

namespace RobotVacuum.Tests
{
    public class SimulationResultsTests
    {
        [Test]
        public void SpeedUp_IsSimulatedTimePerRealSecond()
        {
            var results = new SimulationResults("Flat", simulatedSeconds: 300f, realSeconds: 12f,
                coveragePercent: 64f, metresDriven: 190f, blockedArea: 1.2f, batteryRanOut: false);

            Assert.AreEqual(25f, results.SpeedUp, 1e-4f);
        }

        [Test]
        public void SpeedUp_BeforeAnyRealTimeHasPassed_IsZero()
        {
            var results = new SimulationResults("Flat", 5f, 0f, 0f, 0f, 0f, false);

            Assert.AreEqual(0f, results.SpeedUp);
        }

        [TestCase(0f, "00:00")]
        [TestCase(65.4f, "01:05")]
        [TestCase(-3f, "00:00")]
        [TestCase(900f, "15:00")]
        public void FormatDuration_IsMinutesAndSeconds(float seconds, string expected)
        {
            Assert.AreEqual(expected, SimulationResults.FormatDuration(seconds));
        }
    }

    public class HeadlessSimulationTests
    {
        LevelData level;
        float savedTimeScale;
        float savedMaxDeltaTime;

        [SetUp]
        public void SetUp()
        {
            level = TestLevels.NewLevel(SampleLevels.PopulateApartment);
            savedTimeScale = Time.timeScale;
            savedMaxDeltaTime = Time.maximumDeltaTime;
        }

        [TearDown]
        public void TearDown()
        {
            Time.timeScale = savedTimeScale;
            Time.maximumDeltaTime = savedMaxDeltaTime;
            if (level != null) Object.DestroyImmediate(level);
        }

        [Test]
        public void Start_RunsWithNoVisuals_AndStepsFasterThanRealTime()
        {
            var simulation = HeadlessSimulation.Start(level, "Flat", 300f, MovementPattern.RandomBounce, PictureKind.Heart,
                seed: 4242, speed: 30f);

            Assert.IsTrue(simulation.Running);
            Assert.AreEqual(4242, simulation.Seed);
            Assert.AreEqual(300f, simulation.TargetSeconds, 1e-4f);
            Assert.AreEqual(30f, Time.timeScale, 1e-4f, "simulated time runs ahead of real time");
            Assert.AreEqual(HeadlessSimulation.MaxFrameSeconds, Time.maximumDeltaTime, 1e-4f, "many physics steps per frame");
            Assert.IsEmpty(simulation.GetComponentsInChildren<MeshRenderer>(), "nothing is drawn");
            Assert.IsEmpty(Object.FindObjectsByType<CoverageHeatmapRenderer>(FindObjectsSortMode.None), "no heatmap either");

            simulation.Stop();
        }

        [Test]
        public void Stop_EndsTheRun_ReportsResults_AndPutsTimeBack()
        {
            SimulationResults reported = default;
            bool told = false;

            var simulation = HeadlessSimulation.Start(level, "Flat", 300f, MovementPattern.Lawnmower, PictureKind.Heart, seed: 99);
            simulation.Finished += results => { reported = results; told = true; };

            simulation.Stop();

            Assert.IsTrue(told, "the run reports what it managed");
            Assert.AreEqual("Flat", reported.levelName);
            Assert.AreEqual(99, reported.seed, "the seed goes in the results, so the run can be repeated");
            Assert.IsFalse(reported.batteryRanOut);
            Assert.AreEqual(savedTimeScale, Time.timeScale, 1e-4f);
            Assert.AreEqual(savedMaxDeltaTime, Time.maximumDeltaTime, 1e-4f);

            // The run owns the level copy it was handed, so that is gone too.
            level = null;
        }
    }
}
