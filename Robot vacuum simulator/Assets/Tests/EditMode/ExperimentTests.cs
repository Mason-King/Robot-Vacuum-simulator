using System.Collections.Generic;
using NUnit.Framework;
using RobotVacuum.Level;
using RobotVacuum.LevelEditor;
using RobotVacuum.Sim;
using RobotVacuumSim.EnvironmentModel;
using UnityEngine;

namespace RobotVacuum.Tests
{
    public class SimRandomTests
    {
        [Test]
        public void SameSeed_GivesTheSameSequence_DifferentSeedDoesNot()
        {
            SimRandom.Reseed(42);
            var first = new[] { SimRandom.Value, SimRandom.Range(5f, 9f), SimRandom.Next(100) };

            SimRandom.Reseed(42);
            var again = new[] { SimRandom.Value, SimRandom.Range(5f, 9f), SimRandom.Next(100) };

            SimRandom.Reseed(43);
            var other = new[] { SimRandom.Value, SimRandom.Range(5f, 9f), SimRandom.Next(100) };

            Assert.AreEqual(first, again);
            Assert.AreNotEqual(first, other);
            Assert.AreEqual(43, SimRandom.Seed);
        }

        [Test]
        public void Range_StaysInBounds()
        {
            SimRandom.Reseed(7);
            for (int i = 0; i < 1000; i++)
            {
                float value = SimRandom.Range(-2f, 3f);
                Assert.GreaterOrEqual(value, -2f);
                Assert.Less(value, 3f);
            }
        }
    }

    public class MovementAlgorithmTests
    {
        [Test]
        public void BuiltInAlgorithms_AreRegisteredInEnumOrder()
        {
            Assert.GreaterOrEqual(MovementAlgorithms.Count, 4, "at least four algorithms (1.6.1)");
            Assert.AreEqual("Random", MovementAlgorithms.LabelOf(MovementPattern.RandomBounce));
            Assert.AreEqual("Lawnmower", MovementAlgorithms.LabelOf(MovementPattern.Lawnmower));
            Assert.IsInstanceOf<IMovementAlgorithm>(MovementAlgorithms.Create(MovementPattern.WallFollow, PictureKind.Heart));
        }

        [Test]
        public void UnknownAlgorithm_IsNotDefined_AndFallsBackSafely()
        {
            var unknown = (MovementPattern)999;

            Assert.IsFalse(MovementAlgorithms.IsDefined(unknown));
            StringAssert.Contains("999", MovementAlgorithms.LabelOf(unknown));
            Assert.IsInstanceOf<RandomBounceBrain>(MovementAlgorithms.Create(unknown, PictureKind.Heart));
        }
    }

    public class BatchStatsTests
    {
        static RunReport Run(MovementPattern pattern, float coverage, float seconds, int seed = 1) => new RunReport
        {
            pattern = pattern,
            coveragePercent = coverage,
            durationSeconds = seconds,
            seed = seed,
            timeTo90 = -1f,
        };

        [Test]
        public void Summary_MatchesHandWorkedValues()
        {
            var summary = Summary.Of(new List<double> { 2, 4, 4, 4, 5, 5, 7, 9 });

            Assert.AreEqual(8, summary.count);
            Assert.AreEqual(5.0, summary.mean, 1e-9);
            Assert.AreEqual(32.0 / 7.0, summary.variance, 1e-9, "sample variance divides by n - 1");
            Assert.AreEqual(2.365 * System.Math.Sqrt(32.0 / 7.0 / 8.0), summary.ci95, 1e-6);
        }

        [Test]
        public void Summary_OfOneOrNoValues_HasNoSpread()
        {
            Assert.AreEqual(0, Summary.Of(new List<double>()).count);
            var single = Summary.Of(new List<double> { 3 });
            Assert.AreEqual(3.0, single.mean);
            Assert.AreEqual(0.0, single.ci95);
        }

        [TestCase(1, 12.706)]
        [TestCase(9, 2.262)]
        [TestCase(30, 2.042)]
        [TestCase(1000, 1.96)]
        public void TCritical_FollowsTheTable(int df, double expected)
        {
            Assert.AreEqual(expected, BatchStats.TCritical95(df), 1e-3);
        }

        [Test]
        public void Summarise_GroupsByAlgorithm_BestFirst()
        {
            var runs = new[]
            {
                Run(MovementPattern.RandomBounce, 40f, 60f, 1),
                Run(MovementPattern.RandomBounce, 60f, 60f, 2),
                Run(MovementPattern.Lawnmower, 90f, 60f, 1),
                Run(MovementPattern.Lawnmower, 80f, 60f, 2),
            };

            var groups = BatchStats.Summarise(runs);

            Assert.AreEqual(2, groups.Count);
            Assert.AreEqual(MovementPattern.Lawnmower, groups[0].pattern);
            Assert.AreEqual(85.0, groups[0].coverage.mean, 1e-6);
            Assert.AreEqual(50.0, groups[1].coverage.mean, 1e-6);
            Assert.AreEqual(new[] { 1, 2 }, groups[0].runs.ConvertAll(r => r.seed).ToArray(), "trials sorted by seed");
        }

        [Test]
        public void AverageCurve_HoldsEachRunsFinalValueAfterItEnds()
        {
            var shortRun = Run(MovementPattern.Spiral, 50f, 10f);
            shortRun.sampleInterval = 10f;
            shortRun.coverageSamples = new List<float> { 0f, 50f };
            var longRun = Run(MovementPattern.Spiral, 100f, 20f);
            longRun.sampleInterval = 10f;
            longRun.coverageSamples = new List<float> { 0f, 50f, 100f };

            var curve = BatchStats.AverageCurve(new[] { shortRun, longRun }, 3, out float duration);

            Assert.AreEqual(20f, duration);
            Assert.AreEqual(0f, curve[0], 1e-4f);
            Assert.AreEqual(50f, curve[1], 1e-4f);
            Assert.AreEqual(75f, curve[2], 1e-4f, "(50 held + 100) / 2");
        }

        [Test]
        public void DecodeMap_ReadsCleanlinessAndNoFloor()
        {
            string map = System.Convert.ToBase64String(new byte[] { 0, 125, 250, 255 });

            var values = BatchStats.DecodeMap(map, 4);

            Assert.AreEqual(new[] { 0f, 0.5f, 1f, -1f }, values);
            Assert.IsNull(BatchStats.DecodeMap(map, 5), "wrong length");
            Assert.IsNull(BatchStats.DecodeMap("not base64!", 4));
        }

        [Test]
        public void AverageMap_AveragesFloorBlocks_AndSkipsNoFloor()
        {
            var a = Run(MovementPattern.Spiral, 0f, 1f);
            a.coverageMap = System.Convert.ToBase64String(new byte[] { 250, 0, 255 });
            a.mapWidth = 3; a.mapHeight = 1;
            var b = Run(MovementPattern.Spiral, 0f, 1f);
            b.coverageMap = System.Convert.ToBase64String(new byte[] { 0, 0, 255 });
            b.mapWidth = 3; b.mapHeight = 1;

            var average = BatchStats.AverageMap(new[] { a, b }, out var shape);

            Assert.AreSame(a, shape);
            Assert.AreEqual(new[] { 0.5f, 0f, -1f }, average);
        }
    }

    public class CoverageGridTests
    {
        LevelData level;

        [TearDown]
        public void TearDown()
        {
            if (level != null) Object.DestroyImmediate(level);
        }

        [Test]
        public void RunningCoverage_MatchesAFullRescan()
        {
            level = TestLevels.NewLevel(SampleLevels.PopulateBlank);
            level.Obstacles.Add(new Obstacle { center = new Vector2(1f, 1f), size = Vector2.one, blocksVacuum = true });
            var grid = new ExternalModelGrid(level, null, 0.1f);
            grid.PopulateFromLevelObject();

            var random = new System.Random(3);
            for (int i = 0; i < 2000; i++)
                grid.SetDirtiness(random.Next(grid.Rows), random.Next(grid.Cols), (float)random.NextDouble(), i);
            grid.SetNonCleanable(0, 0, true);
            grid.SetNonCleanable(0, 0, false);

            Assert.AreEqual(grid.CalculateCoveragePercent(), grid.CoveragePercent, 1e-3f);
        }

        [Test]
        public void CellsOutsideEveryRoom_DontCountAsDirtyFloor()
        {
            // An L shape: the bounding box's top-right quarter isn't floor.
            level = TestLevels.NewLevel(l =>
            {
                l.Rooms.Add(new Room { name = "Bottom", outline = TestLevels.Square(Vector2.zero, 2f) });
                l.Rooms.Add(new Room { name = "Right", outline = TestLevels.Square(new Vector2(2f, 0f), 2f) });
                l.Rooms.Add(new Room { name = "Top", outline = TestLevels.Square(new Vector2(0f, 2f), 2f) });
            });
            var grid = new ExternalModelGrid(level, null, 0.5f);
            grid.PopulateFromLevelObject();

            Assert.AreEqual(64, grid.Rows * grid.Cols);
            Assert.AreEqual(16, grid.OffFloorCellCount, "the empty 2 × 2 m corner");
            Assert.AreEqual(0, grid.NonCleanableCellCount, "off-floor cells aren't blocked area");

            for (int row = 0; row < grid.Rows; row++)
                for (int col = 0; col < grid.Cols; col++)
                    if (grid.GetCell(row, col).CountsTowardCoverage) grid.SetDirtiness(row, col, 0f, 0f);

            Assert.AreEqual(100f, grid.CoveragePercent, 1e-3f, "every floor cell clean means 100%");
            Assert.AreEqual(100f, grid.CalculateCoveragePercent(), 1e-3f);
        }

        [Test]
        public void Downsample_AveragesBlocks_AndMarksNoFloor()
        {
            level = TestLevels.NewLevel(l => l.Rooms.Add(new Room { name = "A", outline = TestLevels.Square(Vector2.zero, 1f) }));
            var grid = new ExternalModelGrid(level, null, 0.25f);
            grid.PopulateFromLevelObject();
            grid.SetDirtiness(0, 0, 0f, 0f);

            var values = grid.Downsample(2, out int width, out int height);

            Assert.AreEqual(2, width);
            Assert.AreEqual(2, height);
            Assert.AreEqual(0.25f, values[0], 1e-4f, "one clean cell of four");
            Assert.AreEqual(0f, values[3], 1e-4f);
        }

        [Test]
        public void FloorTuning_OverridesCleaningEffort_WithinBounds()
        {
            var floor = ScriptableObject.CreateInstance<FloorType>();
            try
            {
                floor.displayName = "Test carpet";
                floor.cleaningEffort = 2f;
                Assert.AreEqual(0.5f, VacuumCleaningController.CleaningRate(1f, floor), 1e-5f);

                FloorTuning.SetCleaningEffort("Test carpet", 99f);
                Assert.AreEqual(FloorTuning.MaxCleaningEffort, FloorTuning.CleaningEffort(floor), 1e-5f, "clamped to the bounds");
                Assert.AreEqual(1f / FloorTuning.MaxCleaningEffort, VacuumCleaningController.CleaningRate(1f, floor), 1e-5f);
            }
            finally
            {
                FloorTuning.ClearCleaningEffort("Test carpet");
                Object.DestroyImmediate(floor);
            }
        }
    }

    public class RunChecksTests
    {
        LevelData level;

        [TearDown]
        public void TearDown()
        {
            if (level != null) Object.DestroyImmediate(level);
        }

        [Test]
        public void ConnectedClosedPlan_HasNoWarnings()
        {
            level = TestLevels.NewLevel(SampleLevels.PopulateApartment);

            Assert.IsEmpty(RunChecks.Problems(level));
        }

        [Test]
        public void UnreachableRoom_IsNamed_WithItsConsequence()
        {
            level = TestLevels.NewLevel(l =>
            {
                l.Rooms.Add(new Room { name = "Hall", outline = TestLevels.Square(Vector2.zero, 3f) });
                l.Rooms.Add(new Room { name = "Closet", outline = TestLevels.Square(new Vector2(5f, 0f), 2f) });
                l.RobotSpawn = new Vector2(1f, 1f);
            });

            var problems = RunChecks.Problems(level);

            Assert.AreEqual(1, problems.Count);
            StringAssert.Contains("Closet", problems[0]);
            StringAssert.Contains("0% coverage", problems[0]);
        }

        [Test]
        public void SpawnOutside_SaysEveryRoomScoresZero()
        {
            level = TestLevels.NewLevel(SampleLevels.PopulateBlank);
            level.RobotSpawn = new Vector2(50f, 50f);

            var problems = RunChecks.Problems(level);

            Assert.AreEqual(1, problems.Count);
            StringAssert.Contains("0% coverage", problems[0]);
        }
    }

    public class FloorPlanFileTests
    {
        [Test]
        public void FloorNames_ResolveToPaletteIndices_CaseInsensitively()
        {
            var palette = FloorPalette.CreateDefault();
            try
            {
                var level = new LevelSnapshot();
                level.rooms.Add(new Room { name = "A", floor = "high carpet", floorIndex = 0, outline = TestLevels.Square(Vector2.zero) });
                level.rooms.Add(new Room { name = "B", floor = "Moon dust", floorIndex = 1, outline = TestLevels.Square(Vector2.one) });

                FloorPlanFiles.ResolveFloorNames(level, palette);

                Assert.AreEqual("High Carpet", palette.LabelOf(level.rooms[0].floorIndex));
                Assert.AreEqual(1, level.rooms[1].floorIndex, "an unknown name keeps the index");
                Assert.IsNull(level.rooms[0].floor, "names are dropped once resolved");
            }
            finally
            {
                foreach (var floor in palette.Entries) Object.DestroyImmediate(floor);
                Object.DestroyImmediate(palette);
            }
        }
    }

    public class SimConfigExperimentTests
    {
        [Test]
        public void Parse_ClampsExperimentSettings_ToTheirBounds()
        {
            var data = SimConfig.Data.Parse("{\"vacuum\":{\"driveSpeed\":0.5},\"cellSize\":9,\"telemetryInterval\":0.0001,\"maxRunMinutes\":-3}");

            Assert.AreEqual(SimConfig.MaxCellSize, data.cellSize, 1e-6f);
            Assert.AreEqual(SimConfig.MinTelemetryInterval, data.telemetryInterval, 1e-6f);
            Assert.AreEqual(30f, data.maxRunMinutes, 1e-6f, "a nonsense limit falls back to the default");
            Assert.IsNotNull(data.floorEfforts);
        }
    }
}
