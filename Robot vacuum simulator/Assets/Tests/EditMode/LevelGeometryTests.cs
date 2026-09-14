using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using RobotVacuum.Level;
using UnityEngine;

namespace RobotVacuum.Tests
{
    public class LevelGeometryTests
    {
        LevelData level;

        [SetUp]
        public void SetUp() => level = TestLevels.NewLevel(SampleLevels.PopulateBlank); // 4 × 3 m room

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(level);

        static float TotalLength(IEnumerable<WallSegment> segments) => segments.Sum(s => s.Length);

        [Test]
        public void Complement_WithoutGaps_IsTheWholeLine()
        {
            CollectionAssert.AreEqual(new[] { new Vector2(0f, 1f) }, LevelGeometry.Complement(new List<Vector2>()));
            CollectionAssert.AreEqual(new[] { new Vector2(0f, 1f) }, LevelGeometry.Complement(null));
        }

        [Test]
        public void Complement_SplitsAroundAGap()
        {
            var solid = LevelGeometry.Complement(new List<Vector2> { new Vector2(0.4f, 0.6f) });

            CollectionAssert.AreEqual(new[] { new Vector2(0f, 0.4f), new Vector2(0.6f, 1f) }, solid);
        }

        [Test]
        public void Complement_MergesOverlappingGaps_InAnyOrder()
        {
            var solid = LevelGeometry.Complement(new List<Vector2> { new Vector2(0.5f, 0.7f), new Vector2(0.2f, 0.55f) });

            CollectionAssert.AreEqual(new[] { new Vector2(0f, 0.2f), new Vector2(0.7f, 1f) }, solid);
        }

        [Test]
        public void Complement_ClampsGapsHangingOffTheEnds()
        {
            var solid = LevelGeometry.Complement(new List<Vector2> { new Vector2(-0.2f, 0.1f), new Vector2(0.9f, 1.3f) });

            CollectionAssert.AreEqual(new[] { new Vector2(0.1f, 0.9f) }, solid);
            Assert.IsEmpty(LevelGeometry.Complement(new List<Vector2> { new Vector2(-1f, 2f) }));
        }

        [Test]
        public void CollectWallSegments_OneSegmentPerWalledEdge()
        {
            var segments = LevelGeometry.CollectWallSegments(level);

            Assert.AreEqual(4, segments.Count);
            Assert.AreEqual(14f, TotalLength(segments), TestLevels.Tolerance);
            Assert.IsTrue(segments.All(s => s.roomIndex == 0));
        }

        [Test]
        public void CollectWallSegments_OfNullLevel_IsEmpty()
        {
            Assert.IsEmpty(LevelGeometry.CollectWallSegments(null));
        }

        [Test]
        public void CollectWallSegments_SkipsDeletedWalls()
        {
            level.Rooms[0].SetWall(0, false);

            var segments = LevelGeometry.CollectWallSegments(level);

            Assert.AreEqual(3, segments.Count);
            Assert.AreEqual(10f, TotalLength(segments), TestLevels.Tolerance);
        }

        [Test]
        public void CollectWallSegments_DoorwayCutsAGapOfItsWidth()
        {
            level.Doorways.Add(new Doorway { roomA = 0, center = new Vector2(0.5f, -1.5f), width = 1f });

            var bottom = LevelGeometry.CollectWallSegments(level)
                .Where(s => Mathf.Abs(s.a.y + 1.5f) < 1e-4f && Mathf.Abs(s.b.y + 1.5f) < 1e-4f)
                .OrderBy(s => s.a.x)
                .ToList();

            Assert.AreEqual(2, bottom.Count);
            TestLevels.AssertNear(new Vector2(-2f, -1.5f), bottom[0].a);
            TestLevels.AssertNear(new Vector2(0f, -1.5f), bottom[0].b);
            TestLevels.AssertNear(new Vector2(1f, -1.5f), bottom[1].a);
            TestLevels.AssertNear(new Vector2(2f, -1.5f), bottom[1].b);
        }

        [Test]
        public void CollectWallSegments_DoorwaySlightlyOffTheWall_StillCuts()
        {
            level.Doorways.Add(new Doorway { center = new Vector2(0f, -1.45f), width = 1f });

            Assert.AreEqual(13f, TotalLength(LevelGeometry.CollectWallSegments(level)), TestLevels.Tolerance);
        }

        [Test]
        public void CollectWallSegments_DoorwayFarFromWalls_CutsNothing()
        {
            level.Doorways.Add(new Doorway { center = Vector2.zero, width = 1f });

            Assert.AreEqual(14f, TotalLength(LevelGeometry.CollectWallSegments(level)), TestLevels.Tolerance);
        }

        [Test]
        public void CollectWallSegments_IncludesFreeStandingWalls()
        {
            level.WallStrokes.Add(new WallStroke { a = new Vector2(0f, -1.5f), b = new Vector2(0f, 1.5f) });
            level.WallStrokes.Add(new WallStroke { a = Vector2.one, b = Vector2.one }); // zero length, dropped

            var strokes = LevelGeometry.CollectWallSegments(level).Where(s => s.roomIndex == -1).ToList();

            Assert.AreEqual(1, strokes.Count);
            Assert.AreEqual(3f, strokes[0].Length, TestLevels.Tolerance);
        }

        [Test]
        public void CollectWallSegments_SharedWallDoorway_OpensBothRooms()
        {
            SampleLevels.PopulateApartment(level);
            var sealedLevel = TestLevels.NewLevel(SampleLevels.PopulateApartment);
            try
            {
                sealedLevel.Doorways.Clear();

                var open = LevelGeometry.CollectWallSegments(level);
                var closed = LevelGeometry.CollectWallSegments(sealedLevel);

                // Living room ↔ kitchen doorway is 1 m wide on the shared edge: each room loses 1 m of wall.
                float Lost(int room) => TotalLength(closed.Where(s => s.roomIndex == room)) - TotalLength(open.Where(s => s.roomIndex == room));

                Assert.AreEqual(1f, Lost(1), 1e-3f, "kitchen");
                Assert.AreEqual(1f + 1.1f, Lost(0), 1e-3f, "living room also has the hall doorway");
            }
            finally
            {
                Object.DestroyImmediate(sealedLevel);
            }
        }
    }
}
