using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using RobotVacuum.Level;
using UnityEngine;

namespace RobotVacuum.Tests
{
    public class FloorPatternTests
    {
        // 2 × 2 m with the top-right quadrant missing.
        static List<Vector2> LShape() => new List<Vector2>
        {
            new Vector2(0, 0), new Vector2(2, 0), new Vector2(2, 1),
            new Vector2(1, 1), new Vector2(1, 2), new Vector2(0, 2),
        };

        [Test]
        public void ClipSegment_KeepsOnlyTheInsidePart()
        {
            var segments = new List<Vector2>();

            FloorPatterns.ClipSegment(new Vector2(-1f, 1.5f), new Vector2(3f, 1.5f), LShape(), segments);

            Assert.AreEqual(2, segments.Count);
            TestLevels.AssertNear(new Vector2(0f, 1.5f), segments[0]);
            TestLevels.AssertNear(new Vector2(1f, 1.5f), segments[1]);
        }

        [Test]
        public void ClipSegment_AcrossANotch_SplitsInTwo()
        {
            var uShape = new List<Vector2>
            {
                new Vector2(0, 0), new Vector2(3, 0), new Vector2(3, 2), new Vector2(2, 2),
                new Vector2(2, 1), new Vector2(1, 1), new Vector2(1, 2), new Vector2(0, 2),
            };
            var segments = new List<Vector2>();

            FloorPatterns.ClipSegment(new Vector2(-1f, 1.5f), new Vector2(4f, 1.5f), uShape, segments);

            Assert.AreEqual(4, segments.Count);
            TestLevels.AssertNear(new Vector2(1f, 1.5f), segments[1]);
            TestLevels.AssertNear(new Vector2(2f, 1.5f), segments[2]);
        }

        [Test]
        public void ClipSegment_InsideIsKeptWhole_OutsideAddsNothing()
        {
            var square = TestLevels.Square(Vector2.zero, 2f);
            var segments = new List<Vector2>();

            FloorPatterns.ClipSegment(new Vector2(0.5f, 0.5f), new Vector2(1.5f, 1.5f), square, segments);
            FloorPatterns.ClipSegment(new Vector2(5f, 5f), new Vector2(6f, 5f), square, segments);

            Assert.AreEqual(2, segments.Count);
            TestLevels.AssertNear(new Vector2(1.5f, 1.5f), segments[1]);
        }

        [TestCase(FloorPattern.Planks)]
        [TestCase(FloorPattern.Tiles)]
        [TestCase(FloorPattern.Carpet)]
        [TestCase(FloorPattern.Shag)]
        [TestCase(FloorPattern.Rug)]
        [TestCase(FloorPattern.Hazard)]
        public void Collect_EverySegmentLiesInsideTheRoom(FloorPattern pattern)
        {
            var outline = LShape();
            var segments = new List<Vector2>();

            FloorPatterns.Collect(pattern, outline, segments);

            Assert.IsNotEmpty(segments);
            Assert.AreEqual(0, segments.Count % 2);
            for (int i = 0; i < segments.Count; i += 2)
                Assert.IsTrue(Poly2D.ContainsPoint(outline, (segments[i] + segments[i + 1]) * 0.5f), $"segment {i / 2} is outside");
        }

        [Test]
        public void Collect_Plain_DrawsNothing()
        {
            var segments = new List<Vector2>();

            FloorPatterns.Collect(FloorPattern.Plain, LShape(), segments);

            Assert.IsEmpty(segments);
        }

        [Test]
        public void Collect_Tiles_IsAGridAtTileSize()
        {
            // 0.1 → 1.3 m square: grid lines at 0.4, 0.8 and 1.2 m in each direction.
            var segments = new List<Vector2>();

            FloorPatterns.Collect(FloorPattern.Tiles, TestLevels.Square(new Vector2(0.1f, 0.1f), 1.2f), segments);

            Assert.AreEqual(12, segments.Count);
        }

        [Test]
        public void Textures_AreGeneratedOncePerPattern_AndRepeat()
        {
            Assert.IsNull(FloorTextures.Get(FloorPattern.Plain));

            var planks = FloorTextures.Get(FloorPattern.Planks);

            Assert.IsNotNull(planks);
            Assert.AreSame(planks, FloorTextures.Get(FloorPattern.Planks));
            Assert.AreEqual(TextureWrapMode.Repeat, planks.wrapMode);
        }

        [Test]
        public void Textures_ForDifferentPatterns_LookDifferent()
        {
            var planks = FloorTextures.Get(FloorPattern.Planks).GetPixels32();
            var tiles = FloorTextures.Get(FloorPattern.Tiles).GetPixels32();

            int differing = Enumerable.Range(0, planks.Length).Count(i => planks[i].r != tiles[i].r);

            Assert.Greater(differing, planks.Length / 2);
        }

        [Test]
        public void OnlyHazardCarriesItsOwnColours()
        {
            Assert.IsTrue(FloorTextures.IsSelfColoured(FloorPattern.Hazard));
            Assert.IsFalse(FloorTextures.IsSelfColoured(FloorPattern.Planks));
        }
    }
}
