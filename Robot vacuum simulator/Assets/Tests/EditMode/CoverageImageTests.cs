using NUnit.Framework;
using RobotVacuum.Level;
using RobotVacuum.Sim;
using UnityEngine;
using Object = UnityEngine.Object;

namespace RobotVacuum.Tests
{
    /// <summary>
    /// A headless run draws no heatmap, so the coverage it managed is turned into a texture instead. These
    /// cover the drawing itself; the grid it reads from is built the way a real run builds it.
    /// </summary>
    public class CoverageImageTests
    {
        LevelData level;
        SimulationRunner runner;
        Texture2D texture;

        [SetUp]
        public void SetUp()
        {
            CoverageImage.Forget();
            level = TestLevels.NewLevel(SampleLevels.PopulateApartment);
            runner = SimulationRunner.Start(level, SimulationRunner.Options.Headless());
        }

        [TearDown]
        public void TearDown()
        {
            if (texture != null) Object.DestroyImmediate(texture);
            runner?.Stop();
            if (level != null) Object.DestroyImmediate(level);
            CoverageImage.Forget();
        }

        [Test]
        public void Create_DrawsOnePixelPerCell_WithinTheSizeCap()
        {
            var grid = runner.Cleaning.Grid;

            texture = CoverageImage.Create(grid);

            Assert.IsNotNull(texture);
            Assert.LessOrEqual(Mathf.Max(texture.width, texture.height), CoverageImage.MaxSize);
            Assert.LessOrEqual(texture.width, grid.Cols);
            Assert.LessOrEqual(texture.height, grid.Rows);
        }

        [Test]
        public void AnUncleanedFloor_HasNoCleanedColourAnywhere()
        {
            texture = CoverageImage.Create(runner.Cleaning.Grid, level, runner.Level);

            foreach (var pixel in texture.GetPixels32())
            {
                bool known = pixel.Equals(CoverageImage.Untouched)
                             || pixel.Equals(CoverageImage.Blocked)
                             || pixel.Equals(CoverageImage.Outside);
                Assert.IsTrue(known, $"nothing is cleaned yet, so no cell should be coloured: {pixel}");
            }
        }

        /// <summary>
        /// The grid is a rectangle around the whole plan. Without the level to check rooms against, every
        /// cell reads as floor and the rooms cannot be made out — so passing it must change the picture.
        /// </summary>
        [Test]
        public void GivenTheLevel_FloorOutsideEveryRoom_IsDrawnClear()
        {
            texture = CoverageImage.Create(runner.Cleaning.Grid, level, runner.Level);
            int outside = CountOf(texture, CoverageImage.Outside);

            Assert.Greater(outside, 0, "an apartment does not fill its own bounding box");

            Object.DestroyImmediate(texture);
            CoverageImage.Forget();
            texture = CoverageImage.Create(runner.Cleaning.Grid);

            Assert.AreEqual(0, CountOf(texture, CoverageImage.Outside), "without the level, everything is floor");
        }

        [Test]
        public void SomeOfThePlan_IsStillFloor()
        {
            texture = CoverageImage.Create(runner.Cleaning.Grid, level, runner.Level);

            Assert.Greater(CountOf(texture, CoverageImage.Untouched), 0, "the rooms themselves are floor to clean");
        }

        [Test]
        public void Repaint_RefusesATextureThatDoesNotFitTheGrid()
        {
            texture = new Texture2D(4, 4, TextureFormat.RGBA32, false);

            Assert.IsFalse(CoverageImage.Repaint(texture, runner.Cleaning.Grid));
        }

        [Test]
        public void Repaint_RefreshesAnImageItAlreadyFits()
        {
            texture = CoverageImage.Create(runner.Cleaning.Grid, level, runner.Level);

            Assert.IsTrue(CoverageImage.Repaint(texture, runner.Cleaning.Grid, level, runner.Level));
        }

        [Test]
        public void WithNoGrid_ThereIsNoImage()
        {
            Assert.IsNull(CoverageImage.Create(null));
            Assert.IsFalse(CoverageImage.Repaint(null, runner.Cleaning.Grid));
        }

        static int CountOf(Texture2D texture, Color32 color)
        {
            int count = 0;
            foreach (var pixel in texture.GetPixels32())
                if (pixel.Equals(color)) count++;
            return count;
        }
    }
}
