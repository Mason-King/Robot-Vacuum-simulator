using System.Collections.Generic;
using System.Linq;
using System.Threading;
using NUnit.Framework;
using RobotVacuum.Headless;
using RobotVacuum.Level;
using RobotVacuum.Sim;
using UnityEngine;

namespace RobotVacuum.Tests
{
    /// <summary>
    /// Nonsense input that must give a plain message, never a crash, hang or out-of-memory: floor plans the simulator
    /// can't hold (<see cref="RunLimits"/>), sweeps too big for one job, wall thickness, and a custom algorithm that
    /// never returns (<see cref="AlgorithmWatchdog"/>).
    /// </summary>
    public class EdgeCaseTests
    {
        static LevelData Level(params List<Vector2>[] rooms) => TestLevels.NewLevel(level =>
        {
            foreach (var outline in rooms) level.AddRoom(new Room { name = "R", outline = outline });
            level.RobotSpawn = new Vector2(0.5f, 0.5f);
        });

        static void WithLevel(LevelData level, System.Action<LevelData> check)
        {
            try { check(level); }
            finally { Object.DestroyImmediate(level); }
        }

        // ---- RunLimits ---------------------------------------------------------------------------------------

        [Test]
        public void AnOrdinaryPlanIsWithinTheLimits()
        {
            WithLevel(Level(TestLevels.Square(Vector2.zero, 4f)), level => Assert.IsNull(RunLimits.Problem(level, 0.05f)));
        }

        [Test]
        public void NoPlanAtAllIsExplained()
        {
            Assert.IsNotNull(RunLimits.Problem(null, 0.05f));
        }

        [TestCase(1e30f)]
        [TestCase(float.PositiveInfinity)]
        [TestCase(float.NaN)]
        [TestCase(-20000f)]
        public void FarOrBrokenCoordinatesAreRefused(float value)
        {
            var outline = TestLevels.Square(Vector2.zero, 4f);
            outline[2] = new Vector2(value, 4f);
            WithLevel(Level(outline), level =>
            {
                string problem = RunLimits.Problem(level, 0.05f);
                StringAssert.Contains("10000 m", problem);
                StringAssert.Contains("corner", problem);
            });
        }

        [Test]
        public void ARoomWithNoAreaIsRefused()
        {
            var line = new List<Vector2> { Vector2.zero, new Vector2(1f, 0f), new Vector2(2f, 0f) };
            WithLevel(Level(line), level => StringAssert.Contains("no floor to clean", RunLimits.Problem(level, 0.05f)));
        }

        [Test]
        public void ARoomWithRepeatedCornersIsRefused()
        {
            var same = new List<Vector2> { Vector2.one, Vector2.one, Vector2.one, Vector2.one };
            WithLevel(Level(same), level => StringAssert.Contains("no floor to clean", RunLimits.Problem(level, 0.05f)));
        }

        [Test]
        public void AGridTooBigForMemoryIsRefusedWithTheFix()
        {
            WithLevel(Level(TestLevels.Square(Vector2.zero, 2000f)), level =>
            {
                string problem = RunLimits.Problem(level, 0.0508f);
                StringAssert.Contains("larger cell size", problem);
                Assert.IsNull(RunLimits.Problem(level, 0.5f), "the same plan fits at a coarser cell size");
            });
        }

        [Test]
        public void FurtherOutFurnitureAndSpawnAreRefused()
        {
            WithLevel(Level(TestLevels.Square(Vector2.zero, 4f)), level =>
            {
                level.RobotSpawn = new Vector2(float.NaN, 0f);
                StringAssert.Contains("vacuum starts", RunLimits.Problem(level, 0.05f));
            });
        }

        [Test]
        public void ARunOnANonsensePlanFailsToStartWithTheReason()
        {
            var outline = TestLevels.Square(Vector2.zero, 4f);
            outline[1] = new Vector2(float.PositiveInfinity, 0f);
            WithLevel(Level(outline), level =>
            {
                bool started = SimulationSession.TryStart(level, new RunSettings(), headless: true, null, out var session, out string error);
                try
                {
                    Assert.IsFalse(started);
                    StringAssert.Contains("10000 m", error);
                }
                finally
                {
                    session?.Close();
                }
            });
        }

        [Test]
        public void ARoomWhoseOutlineCrossesItselfIsWarnedAbout()
        {
            var bowtie = new List<Vector2> { Vector2.zero, new Vector2(4f, 3f), new Vector2(4f, 0f), new Vector2(0f, 3f) };
            WithLevel(Level(bowtie), level =>
                StringAssert.Contains("crosses itself", string.Join(" ", LevelEditor.RunChecks.Problems(level))));
            WithLevel(Level(TestLevels.Square(Vector2.zero, 4f)), level =>
                Assert.IsFalse(string.Join(" ", LevelEditor.RunChecks.Problems(level)).Contains("crosses itself")));
        }

        // ---- Wall thickness ------------------------------------------------------------------------------------

        [TestCase(100f, LevelData.MaxWallThickness)]
        [TestCase(-3f, LevelData.MinWallThickness)]
        [TestCase(0f, LevelData.MinWallThickness)]
        [TestCase(float.NaN, LevelData.MinWallThickness)]
        [TestCase(0.15f, 0.15f)]
        public void WallThicknessIsKeptInRange(float requested, float expected)
        {
            WithLevel(Level(TestLevels.Square(Vector2.zero, 4f)), level =>
            {
                level.WallThickness = requested;
                Assert.AreEqual(expected, level.WallThickness, 1e-6f);
            });
        }

        // ---- Sweeps too big for one job --------------------------------------------------------------------------

        const string Plan = @"{ ""name"": ""Plan"", ""rooms"": [ { ""name"": ""Box"", ""floor"": ""Hardwood"",
            ""outline"": [ {""x"":0,""y"":0}, {""x"":4,""y"":0}, {""x"":4,""y"":3}, {""x"":0,""y"":3} ] } ], ""robotSpawn"": {""x"":1,""y"":1} }";

        static string Sweep(string seedCount, string seedStart = "1") => @"{ ""schemaVersion"": 1, ""campaignName"": ""big"",
            ""floorPlans"": [ " + Plan + @" ],
            ""sweep"": { ""algorithms"": [""RandomBounce""], ""seedStart"": " + seedStart + @", ""seedCount"": " + seedCount + " } }";

        [Test]
        public void ASweepOfABillionRunsIsRejectedNotBuilt()
        {
            var job = HeadlessJob.Parse(Sweep("1000000000"), out var errors);
            Assert.IsNull(job);
            Assert.IsTrue(errors.Any(e => e.Contains("100000")), string.Join("\n", errors));
        }

        [Test]
        public void ASweepAtTheLimitIsAccepted()
        {
            var job = HeadlessJob.Parse(Sweep(HeadlessJob.MaxRuns.ToString()), out var errors);
            Assert.IsNotNull(job, string.Join("\n", errors));
            Assert.AreEqual(HeadlessJob.MaxRuns, job.runs.Count);
        }

        [Test]
        public void SeedsNearTheTopOfTheRangeWrapInsteadOfThrowing()
        {
            var job = HeadlessJob.Parse(Sweep("3", int.MaxValue.ToString()), out var errors);
            Assert.IsNotNull(job, string.Join("\n", errors));
            Assert.AreEqual(3, job.runs.Count);
        }

        [Test]
        public void AJobFileWithoutAnIdGetsTheSameIdEachTime()
        {
            var first = HeadlessJob.Parse(Sweep("2"), out var errors);
            Assert.IsNotNull(first, string.Join("\n", errors));
            var again = HeadlessJob.Parse(Sweep("2"), out _);
            var other = HeadlessJob.Parse(Sweep("3"), out _);
            Assert.AreEqual(first.campaignId, again.campaignId, "the same file resumes");
            Assert.AreNotEqual(first.campaignId, other.campaignId, "a different file starts fresh");
            StringAssert.StartsWith("job-", first.campaignId);
        }

        // ---- A custom algorithm that never returns ---------------------------------------------------------------

        [Test]
        public void TheWatchdogStopsACallThatNeverReturns()
        {
            float before = AlgorithmWatchdog.LimitSeconds;
            AlgorithmWatchdog.LimitSeconds = 0.5f;
            bool stopped = false;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                try
                {
                    AlgorithmWatchdog.Enter();
                    try
                    {
                        long spins = 0;
                        while (clock.Elapsed.TotalSeconds < 15) spins++; // a stuck algorithm
                    }
                    finally
                    {
                        AlgorithmWatchdog.Exit();
                    }
                }
                catch (System.Exception exception)
                {
                    stopped = AlgorithmWatchdog.ClaimTimeout(exception);
                    if (!stopped) throw;
                }
            }
            finally
            {
                AlgorithmWatchdog.LimitSeconds = before;
            }

            Assert.IsTrue(stopped, "the endless loop was interrupted");
            Assert.Less(clock.Elapsed.TotalSeconds, 10, "and well before it would have ended by itself");
            Assert.IsFalse(AlgorithmWatchdog.InsideAlgorithm);

            // The thread carries on normally afterwards: no abort is left pending.
            Thread.Sleep(600);
            Assert.Pass();
        }

        [Test]
        public void TheWatchdogLeavesQuickCallsAlone()
        {
            float before = AlgorithmWatchdog.LimitSeconds;
            AlgorithmWatchdog.LimitSeconds = 0.5f;
            try
            {
                for (int i = 0; i < 20; i++)
                {
                    AlgorithmWatchdog.Enter();
                    try { Thread.Sleep(50); }
                    finally { AlgorithmWatchdog.Exit(); }
                }
                Thread.Sleep(800); // outside any call: nothing to stop
            }
            finally
            {
                AlgorithmWatchdog.LimitSeconds = before;
            }
            Assert.IsFalse(AlgorithmWatchdog.InsideAlgorithm);
        }

        [Test]
        public void SlamWithTheLidarOffIsWarnedAbout()
        {
            var settings = new RunSettings { slam = true, lidarEnabled = false };
            StringAssert.Contains("lidar is off", string.Join(" ", LevelEditor.RunChecks.SettingsProblems(settings)));
            Assert.IsEmpty(LevelEditor.RunChecks.SettingsProblems(new RunSettings { slam = true }), "the default lidar is fine");
            Assert.IsEmpty(LevelEditor.RunChecks.SettingsProblems(new RunSettings { slam = false, lidarEnabled = false }), "no SLAM, no map to plan from");
        }

        [Test]
        public void ARunThatFailsSaysWhy()
        {
            StringAssert.Contains("NextGoal returned an invalid waypoint",
                SimulationSession.AlgorithmFailureMessage("NextGoal returned an invalid waypoint: (NaN, 0)."));
            StringAssert.Contains("Console", SimulationSession.AlgorithmFailureMessage(null));
        }
    }
}
