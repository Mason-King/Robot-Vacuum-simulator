using NUnit.Framework;
using RobotVacuum.Level;
using RobotVacuum.LevelEditor;
using UnityEngine;

namespace RobotVacuum.Tests
{
    public class WallOpeningTests
    {
        LevelData level;

        [TearDown]
        public void TearDown()
        {
            if (level != null) Object.DestroyImmediate(level);
        }

        static Room Square(string name, Vector2 origin, float size = 4f) =>
            new Room { name = name, outline = TestLevels.Square(origin, size) };

        [Test]
        public void ClosedRoom_HasNoOpenings()
        {
            level = TestLevels.NewLevel(SampleLevels.PopulateBlank);

            Assert.IsEmpty(LevelGeometry.FindOpenings(level));
        }

        [Test]
        public void SampleApartment_HasNoOpenings()
        {
            level = TestLevels.NewLevel(SampleLevels.PopulateApartment);

            Assert.IsEmpty(LevelGeometry.FindOpenings(level));
        }

        [Test]
        public void DeletedOutsideWall_IsAnOpeningAlongThatSide()
        {
            level = TestLevels.NewLevel(l => l.Rooms.Add(Square("Kitchen", Vector2.zero)));
            level.Rooms[0].SetWall(1, false); // the right-hand side, x = 4

            var openings = LevelGeometry.FindOpenings(level);

            Assert.AreEqual(1, openings.Count);
            Assert.AreEqual(0, openings[0].roomIndex);
            Assert.AreEqual(1, openings[0].edge);
            Assert.AreEqual(4f, openings[0].Center.x, 0.01f);
            Assert.Greater(openings[0].Width, 3f);
            StringAssert.Contains("Kitchen", EditorSession.DescribeOpenings(level));
        }

        [Test]
        public void DeletedWallBetweenTwoRooms_IsNotAnOpening()
        {
            level = TestLevels.NewLevel(l =>
            {
                l.Rooms.Add(Square("Left", Vector2.zero));
                l.Rooms.Add(Square("Right", new Vector2(4f, 0f)));
            });
            level.Rooms[0].SetWall(1, false);
            level.Rooms[1].SetWall(3, false);

            Assert.IsEmpty(LevelGeometry.FindOpenings(level));
            Assert.IsNull(EditorSession.DescribeOpenings(level));
        }

        [Test]
        public void DoorwayInAnOutsideWall_IsAnOpening()
        {
            level = TestLevels.NewLevel(l =>
            {
                l.Rooms.Add(Square("Hall", Vector2.zero));
                l.Doorways.Add(new Doorway { roomA = 0, center = new Vector2(2f, 0f), width = 0.9f });
            });

            var openings = LevelGeometry.FindOpenings(level);

            Assert.AreEqual(1, openings.Count);
            Assert.AreEqual(0.9f, openings[0].Width, 0.1f);
        }

        [Test]
        public void GapNarrowerThanTheVacuum_IsIgnored()
        {
            level = TestLevels.NewLevel(l =>
            {
                l.Rooms.Add(Square("Hall", Vector2.zero));
                l.Doorways.Add(new Doorway { roomA = 0, center = new Vector2(2f, 0f), width = 0.2f });
            });

            Assert.IsEmpty(LevelGeometry.FindOpenings(level));
        }

        [Test]
        public void FreeStandingWallAcrossTheGap_ClosesIt()
        {
            level = TestLevels.NewLevel(l =>
            {
                l.Rooms.Add(Square("Kitchen", Vector2.zero));
                l.WallStrokes.Add(new WallStroke { a = new Vector2(4f, 0f), b = new Vector2(4f, 4f) });
            });
            level.Rooms[0].SetWall(1, false);

            Assert.IsEmpty(LevelGeometry.FindOpenings(level));
        }

        [Test]
        public void IsOffFloor_OnlyPastFloorAndWalls()
        {
            level = TestLevels.NewLevel(l => l.Rooms.Add(Square("Kitchen", Vector2.zero)));

            Assert.IsFalse(LevelGeometry.IsOffFloor(level, new Vector2(2f, 2f)), "inside the room");
            Assert.IsFalse(LevelGeometry.IsOffFloor(level, new Vector2(4.03f, 2f)), "inside the wall's thickness");
            Assert.IsTrue(LevelGeometry.IsOffFloor(level, new Vector2(6f, 2f)), "out in empty space");
        }

        // Two rooms whose shared side misses by `gap`, with a doorway through it (as hand-placed corners can).
        void RoomsWithSeam(float gap)
        {
            level = TestLevels.NewLevel(l =>
            {
                l.Rooms.Add(Square("Living", Vector2.zero));
                l.Rooms.Add(Square("Hall", new Vector2(4f + gap, 0f)));
                l.Doorways.Add(new Doorway { roomA = 0, roomB = 1, center = new Vector2(4f + gap * 0.5f, 2f), width = 1f });
            });
        }

        [Test]
        public void HairlineSeamInADoorway_IsStillFloor()
        {
            RoomsWithSeam(0.005f); // 5 mm: the gap found in a saved sample apartment

            Assert.IsFalse(LevelGeometry.IsOffFloor(level, new Vector2(4.0025f, 2f)), "driving through the doorway is not an escape");
            Assert.IsEmpty(LevelGeometry.FindOpenings(level), "and the plan has no opening to the outside");
        }

        [Test]
        public void RealGapBetweenRooms_IsStillOffFloor()
        {
            RoomsWithSeam(0.3f);

            Assert.IsTrue(LevelGeometry.IsOffFloor(level, new Vector2(4.15f, 2f)), "15 cm from either room is empty space");
        }
    }
}
