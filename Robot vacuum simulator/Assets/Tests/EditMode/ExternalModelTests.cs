using System.IO;
using NUnit.Framework;
using RobotVacuum.Level;
using RobotVacuum.Sim;
using RobotVacuumSim.EnvironmentModel;
using UnityEngine;
using Object = UnityEngine.Object;

namespace RobotVacuum.Tests
{
    public class ExternalModelTests
    {
        LevelData level;

        [SetUp]
        public void SetUp() => level = TestLevels.NewLevel(SampleLevels.PopulateBlank); // 4 x 3 m room

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(level);

        [Test]
        public void Cell_StartsEmpty_AndAccumulates()
        {
            var grid = new ExternalModelGrid(level, null, 0.5f);

            Assert.AreEqual(0f, grid.GetCell(0, 0).DwellSeconds);
            Assert.AreEqual(0f, grid.GetCell(0, 0).Cleanliness);

            grid.Accumulate(0, 0, 1f, 2f, 0f);
            grid.Accumulate(0, 0, 0.5f, 1f, 1f);

            Assert.AreEqual(1.5f, grid.GetCell(0, 0).DwellSeconds, 1e-5f);
            Assert.AreEqual(3f, grid.GetCell(0, 0).Cleanliness, 1e-5f);
        }

        [Test]
        public void ValueBelowZero_CountsAsNothing_InCoverageAndColours()
        {
            var grid = new ExternalModelGrid(level, null, 0.5f, 2f);
            grid.Accumulate(0, 0, -3f, -3f, 0f); // not something the writer does, but readers must cope

            Assert.AreEqual(0f, grid.CalculateCoveragePercent(), 1e-5f);
            Assert.AreEqual(0f, HeatmapColors.Normalize(HeatmapMode.FloorCleanliness, -3f, -3f, 2f));
            Assert.AreEqual(0f, HeatmapColors.Normalize(HeatmapMode.FloorCoverage, -3f, -3f, 2f));
            Assert.AreEqual(0, HeatmapColors.Evaluate(HeatmapMode.FloorOverCoverage, -3f, -3f, 2f).a);
        }

        [Test]
        public void Grid_SitsOnTheSharedLattice_WhenGivenAnAnchor()
        {
            const float cell = 0.5f;
            var anchor = new Vector2(0.13f, 0.07f);
            var grid = new ExternalModelGrid(level, null, cell, 2f, anchor);
            Rect bounds = level.Bounds();

            // The corner is a whole number of cells from the anchor, and just below-left of the level.
            Assert.AreEqual(0f, Mathf.Repeat((grid.OriginWorld.x - anchor.x) / cell + 0.5f, 1f) - 0.5f, 1e-3f);
            Assert.AreEqual(0f, Mathf.Repeat((grid.OriginWorld.y - anchor.y) / cell + 0.5f, 1f) - 0.5f, 1e-3f);
            Assert.LessOrEqual(grid.OriginWorld.x, bounds.xMin + 1e-4f);
            Assert.Greater(grid.OriginWorld.x, bounds.xMin - cell);
            Assert.GreaterOrEqual(grid.OriginWorld.x + grid.Cols * cell, bounds.xMax - 1e-4f);
            Assert.GreaterOrEqual(grid.OriginWorld.y + grid.Rows * cell, bounds.yMax - 1e-4f);

            // Lattice index = local index + offset.
            Vector2 center = grid.GridIndexToWorldCenter(2, 3);
            Assert.AreEqual(3 + grid.ColOffset, Mathf.FloorToInt((center.x - anchor.x) / cell));
            Assert.AreEqual(2 + grid.RowOffset, Mathf.FloorToInt((center.y - anchor.y) / cell));
        }

        [Test]
        public void ExternalAndInternal_AgreeOnWhichCellAPointIsIn()
        {
            const float cell = 0.5f;
            var anchor = new Vector2(0.13f, 0.07f);
            var external = new ExternalModelGrid(level, null, cell, 2f, anchor);
            var internalGrid = new InternalModelGrid(anchor, cell);
            var point = new Vector2(0.33f, 0.21f);

            Assert.IsTrue(external.TryWorldToGridIndex(point, out int row, out int col));
            Assert.IsTrue(internalGrid.TryWorldToGridIndex(point, out int latticeRow, out int latticeCol));

            Assert.AreEqual(row + external.RowOffset, latticeRow);
            Assert.AreEqual(col + external.ColOffset, latticeCol);
        }

        [Test]
        public void Cleanliness_IsNotCappedAtTheTarget()
        {
            var grid = new ExternalModelGrid(level, null, 0.5f, 2f);

            grid.Accumulate(0, 0, 10f, 10f, 0f);

            Assert.AreEqual(10f, grid.GetCell(0, 0).Cleanliness, 1e-5f);
        }

        [Test]
        public void Coverage_IsAGradedAverageAgainstTheTarget()
        {
            var grid = new ExternalModelGrid(level, null, 0.5f, 2f);
            int cells = grid.Rows * grid.Cols;
            Assert.AreEqual(0f, grid.CalculateCoveragePercent(), 1e-4f);

            grid.Accumulate(0, 0, 1f, 1f, 0f); // half the target: counts as half a cell
            Assert.AreEqual(0.5f / cells * 100f, grid.CalculateCoveragePercent(), 1e-3f);

            grid.Accumulate(0, 0, 9f, 9f, 1f); // far past the target: counts as one cell, no more
            Assert.AreEqual(1f / cells * 100f, grid.CalculateCoveragePercent(), 1e-3f);
            Assert.AreEqual(1f / cells * 100f, grid.CalculateFullyCleanedPercent(), 1e-3f);
        }

        [Test]
        public void SavedData_MatchesTheGrid()
        {
            var grid = new ExternalModelGrid(level, null, 0.5f, 2f);
            grid.Accumulate(1, 2, 3f, 4f, 0f);
            grid.SetNonCleanable(0, 0, true);

            var data = grid.ToData();

            Assert.IsTrue(data.IsValid);
            Assert.AreEqual(3f, data.dwellSeconds[data.Index(1, 2)], 1e-5f);
            Assert.AreEqual(4f, data.cleanliness[data.Index(1, 2)], 1e-5f);
            Assert.IsTrue(data.nonCleanable[data.Index(0, 0)]);
            Assert.AreEqual(grid.CalculateCoveragePercent(), data.CoveragePercent(), 1e-4f);
            Assert.AreEqual(grid.CalculateFullyCleanedPercent(), data.FullyCleanedPercent(), 1e-4f);
        }

        [Test]
        public void SavedData_SurvivesAJsonRoundTrip()
        {
            var grid = new ExternalModelGrid(level, null, 0.5f, 2f);
            grid.Accumulate(1, 1, 1.5f, 2.5f, 0f);

            var copy = JsonUtility.FromJson<ExternalModelData>(JsonUtility.ToJson(grid.ToData()));

            Assert.IsTrue(copy.IsValid);
            Assert.AreEqual(2.5f, copy.cleanliness[copy.Index(1, 1)], 1e-5f);
        }

        [Test]
        public void Colors_UseTheRightRangePerMode()
        {
            const float target = 2f;

            // Cleanliness: 0 to target. Coverage and OverCoverage: 0 to twice the target.
            Assert.AreEqual(1f, HeatmapColors.Normalize(HeatmapMode.FloorCleanliness, 0f, 2f, target), 1e-5f);
            Assert.AreEqual(0.5f, HeatmapColors.Normalize(HeatmapMode.FloorCoverage, 2f, 0f, target), 1e-5f);
            Assert.AreEqual(0.5f, HeatmapColors.Normalize(HeatmapMode.FloorOverCoverage, 0f, 2f, target), 1e-5f);

            // Coverage reads dwell, the other two read cleanliness.
            Assert.AreEqual(0f, HeatmapColors.Normalize(HeatmapMode.FloorCoverage, 0f, 4f, target));
            Assert.AreEqual(0f, HeatmapColors.Normalize(HeatmapMode.FloorOverCoverage, 4f, 0f, target));

            // Past the top of the range stays at the top.
            Assert.AreEqual(1f, HeatmapColors.Normalize(HeatmapMode.FloorCoverage, 100f, 0f, target));
        }

        [Test]
        public void Colors_EmptyIsClear_AndNothingIsFullyOpaque()
        {
            foreach (HeatmapMode mode in System.Enum.GetValues(typeof(HeatmapMode)))
            {
                var palette = HeatmapColors.Palette(mode);
                Assert.AreEqual(0, palette[0].a, mode.ToString());

                for (int i = 1; i < palette.Length; i++)
                    Assert.Less(palette[i].a, 255, mode + " " + i);
            }
        }

        [Test]
        public void Image_DrawsFromSavedDataAlone()
        {
            var grid = new ExternalModelGrid(level, null, 0.5f, 2f);
            grid.Accumulate(0, 0, 2f, 2f, 0f);
            var data = grid.ToData();

            var texture = CoverageImage.Create(data, HeatmapMode.FloorCleanliness);
            try
            {
                Assert.IsNotNull(texture);
                Assert.AreEqual(data.cols, texture.width);
                Assert.AreEqual(data.rows, texture.height);
            }
            finally
            {
                Object.DestroyImmediate(texture);
            }
        }

        [Test]
        public void RunArtifacts_WritesTheModelAndThreeImages_AndReadsTheModelBack()
        {
            var grid = new ExternalModelGrid(level, null, 0.5f, 2f);
            grid.Accumulate(1, 1, 2f, 2f, 0f);

            string folder = Path.Combine(Path.GetTempPath(), "rv_artifacts_test_" + System.Guid.NewGuid().ToString("N"));
            try
            {
                var result = new RunResult { succeeded = true };
                RunArtifacts.Write(grid.ToData(), folder, result);

                Assert.IsTrue(File.Exists(result.externalModelPath));
                Assert.IsTrue(File.Exists(result.cleanlinessImagePath));
                Assert.IsTrue(File.Exists(result.coverageImagePath));
                Assert.IsTrue(File.Exists(result.overCoverageImagePath));
                Assert.AreEqual(result.cleanlinessImagePath, result.heatmapImagePath);
                Assert.AreEqual(2f, result.targetMaxClean, 1e-5f);

                var loaded = RunArtifacts.LoadModel(result.externalModelPath);
                Assert.IsNotNull(loaded);
                Assert.AreEqual(2f, loaded.cleanliness[loaded.Index(1, 1)], 1e-5f);

                var texture = RunArtifacts.RenderFromFolder(folder, HeatmapMode.FloorCoverage);
                Assert.IsNotNull(texture);
                Object.DestroyImmediate(texture);
            }
            finally
            {
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
            }
        }

        [Test]
        public void RunSettings_Resolved_FillsEveryUnsetValueWithItsDefault()
        {
            var resolved = new RunSettings { randomSeed = false, seed = 3 }.Resolved();

            Assert.AreEqual(3, resolved.seed);
            Assert.AreEqual(HeatmapDefaults.TargetMaxClean, resolved.targetMaxClean, 1e-5f);
            Assert.AreEqual(SimDefaults.CellSizeMeters, resolved.cellSizeMeters, 1e-6f);
            Assert.AreEqual(HeatmapDefaults.BaseVacuumPower, resolved.vacuumPower, 1e-5f);
        }

        [Test]
        public void RunSettings_Resolved_KeepsSetValues_AndLeavesTheOriginalUntouched()
        {
            var original = new RunSettings { randomSeed = false, seed = 7, targetMaxClean = 5f, cellSizeMeters = 0.1f, vacuumPower = 2f };
            var resolved = original.Resolved();

            Assert.AreEqual(5f, resolved.targetMaxClean, 1e-5f);
            Assert.AreEqual(0.1f, resolved.cellSizeMeters, 1e-6f);
            Assert.AreEqual(2f, resolved.vacuumPower, 1e-5f);
            Assert.AreNotSame(original, resolved);
        }

        [Test]
        public void RunSettings_Resolved_TurnsARandomSeedIntoAConcreteOne()
        {
            var original = new RunSettings { randomSeed = true };
            var resolved = original.Resolved();

            Assert.IsFalse(resolved.randomSeed, "the saved settings must replay the same seed");
            Assert.GreaterOrEqual(resolved.seed, 0);
            Assert.IsTrue(original.randomSeed, "the caller's settings still ask for a new seed next time");
        }

        [Test]
        public void RunConfig_RoundTripsThroughJson()
        {
            var config = new RunConfig
            {
                settings = new RunSettings { randomSeed = false, seed = 11, targetMaxClean = 4f }.Resolved(),
                headless = true,
                startedUtc = "2026-10-22T00:00:00Z",
            };

            var read = JsonUtility.FromJson<RunConfig>(JsonUtility.ToJson(config));

            Assert.AreEqual(11, read.settings.seed);
            Assert.AreEqual(4f, read.settings.targetMaxClean, 1e-5f);
            Assert.IsTrue(read.headless);
            Assert.AreEqual(SimDefaults.MaxSimulatedSeconds, read.maxSimulatedSeconds, 1e-3f);
        }

        [Test]
        public void RunResult_Failure_RecordsAnErrorEndReason()
        {
            var result = RunResult.Failure("boom");

            Assert.IsFalse(result.succeeded);
            Assert.AreEqual(RunEndReason.Error, result.endReason);
        }
    }
}
