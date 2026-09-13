using NUnit.Framework;
using RobotVacuum.Level;
using RobotVacuum.LevelEditor;
using UnityEngine;

namespace RobotVacuum.Tests
{
    public class LevelSnapshotTests
    {
        LevelData level;

        [SetUp]
        public void SetUp() => level = TestLevels.NewLevel(SampleLevels.PopulateApartment);

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(level);

        [Test]
        public void SerializeThenParse_PreservesEverything()
        {
            level.Rooms[0].SetWall(1, false);
            level.WallStrokes.Add(new WallStroke { a = new Vector2(1f, 2f), b = new Vector2(3f, 4f) });
            level.WallThickness = 0.2f;
            level.WallColor = new Color(0.1f, 0.2f, 0.3f, 1f);
            level.RobotSpawn = new Vector2(3f, 0.5f);

            var snapshot = LevelSnapshot.Parse(LevelSnapshot.Serialize(level, "Flat"));

            Assert.IsNotNull(snapshot);
            Assert.AreEqual(LevelSnapshot.CurrentVersion, snapshot.version);
            Assert.AreEqual("Flat", snapshot.name);
            Assert.AreEqual(4, snapshot.rooms.Count);
            Assert.AreEqual("Bedroom", snapshot.rooms[3].name);
            Assert.AreEqual(4, snapshot.rooms[3].floorIndex);
            CollectionAssert.AreEqual(level.Rooms[1].outline, snapshot.rooms[1].outline);
            Assert.IsFalse(snapshot.rooms[0].HasWall(1));
            Assert.IsTrue(snapshot.rooms[0].HasWall(0));

            Assert.AreEqual(3, snapshot.doorways.Count);
            Assert.AreEqual((2, 3), (snapshot.doorways[2].roomA, snapshot.doorways[2].roomB));
            Assert.AreEqual(1.1f, snapshot.doorways[1].width, 1e-6f);

            Assert.AreEqual(1, snapshot.wallStrokes.Count);
            Assert.AreEqual(new Vector2(3f, 4f), snapshot.wallStrokes[0].b);
            Assert.AreEqual(0.2f, snapshot.wallThickness, 1e-6f);
            Assert.AreEqual(level.WallColor, snapshot.wallColor);
            Assert.AreEqual(new Vector2(3f, 0.5f), snapshot.robotSpawn);
        }

        [Test]
        public void FromLevel_IsADeepCopy()
        {
            var snapshot = LevelSnapshot.FromLevel(level, "Copy");

            snapshot.rooms[0].outline[0] = new Vector2(99f, 99f);
            snapshot.rooms.RemoveAt(1);
            snapshot.doorways[0].width = 3f;

            Assert.AreEqual(4, level.Rooms.Count);
            Assert.AreEqual(new Vector2(-2.4f, -1.8f), level.Rooms[0].outline[0]);
            Assert.AreEqual(1f, level.Doorways[0].width, 1e-6f);
        }

        [Test]
        public void ApplyTo_ReplacesTheLevelsContents()
        {
            level.WallStrokes.Add(new WallStroke { a = Vector2.zero, b = Vector2.one });
            var blank = TestLevels.Snapshot("Blank", SampleLevels.PopulateBlank);
            blank.wallThickness = 0.25f;

            blank.ApplyTo(level);

            Assert.AreEqual(1, level.Rooms.Count);
            Assert.IsEmpty(level.Doorways);
            Assert.IsEmpty(level.WallStrokes);
            Assert.AreEqual(0.25f, level.WallThickness, 1e-6f);
            Assert.AreEqual(Vector2.zero, level.RobotSpawn);
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("   ")]
        [TestCase("this is not json")]
        [TestCase("{\"rooms\": [")]
        public void Parse_UnreadableInput_ReturnsNull(string json)
        {
            Assert.IsNull(LevelSnapshot.Parse(json));
        }

        [Test]
        public void Parse_RepairsMissingAndOutOfRangeValues()
        {
            const string json = @"{
                ""name"": ""  "",
                ""wallThickness"": -3,
                ""rooms"": [ { ""name"": ""Tri"", ""outline"": [ {""x"":0,""y"":0}, {""x"":1,""y"":0}, {""x"":0,""y"":1} ] } ],
                ""doorways"": [ { ""width"": 0 } ]
            }";

            var snapshot = LevelSnapshot.Parse(json);

            Assert.IsNotNull(snapshot);
            Assert.AreEqual(LevelSnapshot.DefaultName, snapshot.name);
            Assert.AreEqual(0.01f, snapshot.wallThickness, 1e-6f);
            Assert.AreEqual(new[] { true, true, true }, snapshot.rooms[0].wallEdges, "missing wall flags default to walls");
            Assert.AreEqual(0.05f, snapshot.doorways[0].width, 1e-6f);
            Assert.IsNotNull(snapshot.wallStrokes);
        }

        [Test]
        public void Parse_EmptyObject_GivesAnEmptyUsableLevel()
        {
            var snapshot = LevelSnapshot.Parse("{}");

            Assert.IsNotNull(snapshot);
            Assert.AreEqual(LevelSnapshot.DefaultName, snapshot.name);
            Assert.IsEmpty(snapshot.rooms);
            Assert.IsEmpty(snapshot.doorways);
            Assert.IsEmpty(snapshot.wallStrokes);
            Assert.AreEqual(0, snapshot.ValidRoomCount);
        }

        [Test]
        public void ToJson_RoundTrips()
        {
            var original = LevelSnapshot.FromLevel(level, "Round trip");

            var copy = LevelSnapshot.Parse(original.ToJson());

            Assert.AreEqual(original.ToJson(false), copy.ToJson(false));
        }

        [Test]
        public void Summaries_MatchTheLevel()
        {
            level.Rooms.Add(new Room { outline = { Vector2.zero, Vector2.one } }); // invalid, ignored
            var snapshot = LevelSnapshot.FromLevel(level, "Summary");

            Assert.AreEqual(4, snapshot.ValidRoomCount);
            Assert.AreEqual(level.TotalFloorArea(), snapshot.TotalFloorArea(), TestLevels.Tolerance);
            Assert.AreEqual(level.Bounds(), snapshot.Bounds());
        }
    }
}
