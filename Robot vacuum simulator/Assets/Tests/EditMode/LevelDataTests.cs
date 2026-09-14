using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using RobotVacuum.Level;
using UnityEngine;

namespace RobotVacuum.Tests
{
    public class RoomTests
    {
        static Room SquareRoom() => new Room { name = "Square", outline = TestLevels.Square(Vector2.zero, 2f) };

        [Test]
        public void SyncWallEdges_AddsWallsForNewEdges_AndTrimsExtras()
        {
            var room = SquareRoom();
            room.wallEdges = null;

            room.SyncWallEdges();
            Assert.AreEqual(new[] { true, true, true, true }, room.wallEdges);

            room.wallEdges.AddRange(new[] { false, false });
            room.SyncWallEdges();
            Assert.AreEqual(4, room.wallEdges.Count);
        }

        [Test]
        public void HasWall_OutOfRange_IsFalse()
        {
            var room = SquareRoom();

            Assert.IsTrue(room.HasWall(0));
            Assert.IsFalse(room.HasWall(-1));
            Assert.IsFalse(room.HasWall(4));
        }

        [Test]
        public void SetWall_ToggleWall_AndSetAllWalls()
        {
            var room = SquareRoom();

            room.SetWall(1, false);
            Assert.IsFalse(room.HasWall(1));
            Assert.IsTrue(room.HasWall(0));

            room.ToggleWall(1);
            Assert.IsTrue(room.HasWall(1));

            room.SetAllWalls(false);
            Assert.IsTrue(Enumerable.Range(0, 4).All(e => !room.HasWall(e)));

            room.SetWall(99, true); // ignored, not thrown
        }

        [Test]
        public void InsertVertex_BothHalvesInheritTheSplitEdgesWall()
        {
            var room = SquareRoom();
            room.SetWall(1, false);
            room.SetWall(2, false);
            room.SetWall(2, true);

            room.InsertVertex(1, new Vector2(2f, 1f));

            Assert.AreEqual(5, room.outline.Count);
            TestLevels.AssertNear(new Vector2(2f, 1f), room.outline[2]);
            Assert.AreEqual(new[] { true, false, false, true, true }, room.wallEdges);
        }

        [Test]
        public void InsertVertex_InvalidEdge_DoesNothing()
        {
            var room = SquareRoom();

            room.InsertVertex(-1, Vector2.one);
            room.InsertVertex(4, Vector2.one);

            Assert.AreEqual(4, room.outline.Count);
            Assert.AreEqual(4, room.wallEdges.Count);
        }

        [Test]
        public void RemoveVertex_KeepsWallFlagsAlignedWithEdges()
        {
            var room = SquareRoom();
            room.InsertVertex(0, new Vector2(1f, 0f));

            room.RemoveVertex(0);

            Assert.AreEqual(4, room.outline.Count);
            Assert.AreEqual(room.EdgeCount, room.wallEdges.Count);
        }

        [Test]
        public void RemoveVertex_NeverLeavesFewerThanThreeCorners()
        {
            var room = new Room { outline = new List<Vector2> { Vector2.zero, Vector2.right, Vector2.up } };

            room.RemoveVertex(0);

            Assert.AreEqual(3, room.outline.Count);
            Assert.IsTrue(room.IsValid);
        }

        [Test]
        public void EdgeEnd_WrapsToFirstVertex()
        {
            var room = SquareRoom();

            Assert.AreEqual(room.outline[3], room.EdgeStart(3));
            Assert.AreEqual(room.outline[0], room.EdgeEnd(3));
        }
    }

    public class LevelDataTests
    {
        LevelData level;

        [SetUp]
        public void SetUp() => level = TestLevels.NewLevel(SampleLevels.PopulateApartment);

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(level);

        [Test]
        public void GetRoom_OutOfRange_IsNull()
        {
            Assert.IsNotNull(level.GetRoom(0));
            Assert.IsNull(level.GetRoom(-1));
            Assert.IsNull(level.GetRoom(level.Rooms.Count));
        }

        [Test]
        public void RoomIndexAt_FindsContainingRoom_LaterRoomsWinOverlaps()
        {
            Assert.AreEqual(0, level.RoomIndexAt(level.RobotSpawn));
            Assert.AreEqual(1, level.RoomIndexAt(new Vector2(3f, 0f)));
            Assert.AreEqual(-1, level.RoomIndexAt(new Vector2(50f, 50f)));

            level.AddRoom(new Room { outline = TestLevels.Square(new Vector2(-1f, -1f), 2f) });
            Assert.AreEqual(4, level.RoomIndexAt(Vector2.zero));
        }

        [Test]
        public void ReachableFrom_FollowsDoorways()
        {
            CollectionAssert.AreEquivalent(new[] { 0, 1, 2, 3 }, level.ReachableFrom(0));
            CollectionAssert.AreEquivalent(new[] { 0, 1, 2, 3 }, level.ReachableFrom(3));
            Assert.IsEmpty(level.ReachableFrom(-1));
        }

        [Test]
        public void UnreachableRooms_ListsRoomsCutOffFromSpawn()
        {
            Assert.IsEmpty(level.UnreachableRooms());

            level.Doorways.RemoveAt(2); // hall ↔ bedroom

            CollectionAssert.AreEqual(new[] { 3 }, level.UnreachableRooms());
        }

        [Test]
        public void UnreachableRooms_IsEmpty_WhenSpawnIsOutside()
        {
            level.Doorways.Clear();
            level.RobotSpawn = new Vector2(50f, 50f);

            Assert.IsEmpty(level.UnreachableRooms());
        }

        [Test]
        public void NeighboursOf_IgnoresDoorwaysToOutside()
        {
            level.Doorways.Add(new Doorway { roomA = 1, roomB = -1, center = new Vector2(4.6f, 0f) });

            CollectionAssert.AreEquivalent(new[] { 0 }, level.NeighboursOf(1).ToList());
        }

        [Test]
        public void RemoveRoom_DropsItsDoorways_AndReindexesTheRest()
        {
            level.RemoveRoom(1); // kitchen: doorway 0 goes, the others shift down

            Assert.AreEqual(3, level.Rooms.Count);
            Assert.AreEqual(2, level.Doorways.Count);
            Assert.AreEqual((0, 1), (level.Doorways[0].roomA, level.Doorways[0].roomB));
            Assert.AreEqual((1, 2), (level.Doorways[1].roomA, level.Doorways[1].roomB));
        }

        [Test]
        public void RemoveRoom_OutOfRange_DoesNothing()
        {
            level.RemoveRoom(-1);
            level.RemoveRoom(10);

            Assert.AreEqual(4, level.Rooms.Count);
            Assert.AreEqual(3, level.Doorways.Count);
        }

        [Test]
        public void WallThickness_HasAFloor_AndDrivesDoorwaySnapDistance()
        {
            level.WallThickness = -1f;
            Assert.AreEqual(0.01f, level.WallThickness, 1e-6f);
            Assert.AreEqual(0.12f, level.DoorwaySnapDistance, 1e-6f);

            level.WallThickness = 0.3f;
            Assert.AreEqual(0.3f, level.DoorwaySnapDistance, 1e-6f);
        }

        [Test]
        public void Bounds_OfEmptyLevel_IsADefaultViewport()
        {
            level.Rooms.Clear();

            Assert.AreEqual(new Rect(-5f, -5f, 10f, 10f), level.Bounds());
        }

        [Test]
        public void TotalFloorArea_SkipsInvalidRooms()
        {
            var blank = TestLevels.NewLevel(SampleLevels.PopulateBlank);
            try
            {
                blank.Rooms.Add(new Room { outline = new List<Vector2> { Vector2.zero, Vector2.one } });
                Assert.AreEqual(12f, blank.TotalFloorArea(), TestLevels.Tolerance);
                Assert.AreEqual(new Rect(-2f, -1.5f, 4f, 3f), blank.Bounds());
            }
            finally
            {
                Object.DestroyImmediate(blank);
            }
        }

        [Test]
        public void FloorTypeAt_UsesPalette_OrNullWithout()
        {
            Assert.IsNull(level.FloorTypeAt(level.RobotSpawn), "no palette assigned");

            var palette = FloorPalette.CreateDefault();
            try
            {
                level.Palette = palette;
                Assert.AreEqual("Hardwood", level.FloorTypeAt(level.RobotSpawn).Label);
                Assert.AreEqual("Tile", level.FloorTypeAt(new Vector2(3f, 0f)).Label);
                Assert.IsNull(level.FloorTypeAt(new Vector2(50f, 50f)));
            }
            finally
            {
                foreach (var floor in palette.Entries) Object.DestroyImmediate(floor);
                Object.DestroyImmediate(palette);
            }
        }

        [TestCase(0f)]
        [TestCase(37f)]
        [TestCase(90f)]
        public void RectangleOutline_KeepsSizeAndCentreAtAnyRotation(float degrees)
        {
            var outline = LevelData.RectangleOutline(new Vector2(1f, 2f), new Vector2(4f, 3f), degrees);

            Assert.AreEqual(4, outline.Count);
            Assert.AreEqual(12f, Poly2D.Area(outline), TestLevels.Tolerance);
            TestLevels.AssertNear(new Vector2(1f, 2f), Poly2D.Centroid(outline));
            Assert.AreEqual(4f, Vector2.Distance(outline[0], outline[1]), TestLevels.Tolerance);
        }

        [Test]
        public void RegularOutline_HasAtLeastThreeSides_OnTheRadius()
        {
            var outline = LevelData.RegularOutline(Vector2.one, 2f, 1);

            Assert.AreEqual(3, outline.Count);
            foreach (var point in outline)
                Assert.AreEqual(2f, Vector2.Distance(Vector2.one, point), TestLevels.Tolerance);
        }
    }

    public class SampleLevelsTests
    {
        [Test]
        public void Apartment_RoomsAreSimpleAndDoorwaysSitOnWalls()
        {
            var level = TestLevels.NewLevel(SampleLevels.PopulateApartment);
            try
            {
                foreach (var room in level.Rooms)
                    Assert.IsTrue(Poly2D.IsSimple(room.outline), $"{room.name} crosses itself");

                // Each doorway cuts both of the rooms it claims to join.
                foreach (var doorway in level.Doorways)
                {
                    foreach (int roomIndex in new[] { doorway.roomA, doorway.roomB })
                    {
                        var room = level.Rooms[roomIndex];
                        float nearest = Enumerable.Range(0, room.EdgeCount)
                            .Min(e => Poly2D.DistanceToSegment(doorway.center, room.EdgeStart(e), room.EdgeEnd(e), out _));
                        Assert.LessOrEqual(nearest, level.DoorwaySnapDistance, $"doorway misses {room.name}");
                    }
                }
            }
            finally
            {
                Object.DestroyImmediate(level);
            }
        }

        [Test]
        public void PopulateBlank_ReplacesExistingContent()
        {
            var level = TestLevels.NewLevel(SampleLevels.PopulateApartment);
            try
            {
                level.WallStrokes.Add(new WallStroke { a = Vector2.zero, b = Vector2.one });

                SampleLevels.PopulateBlank(level);

                Assert.AreEqual(1, level.Rooms.Count);
                Assert.IsEmpty(level.Doorways);
                Assert.IsEmpty(level.WallStrokes);
                Assert.AreEqual(0, level.RoomIndexAt(level.RobotSpawn));
            }
            finally
            {
                Object.DestroyImmediate(level);
            }
        }
    }
}
