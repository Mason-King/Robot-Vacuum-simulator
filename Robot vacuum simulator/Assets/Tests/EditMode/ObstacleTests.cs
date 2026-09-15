using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using RobotVacuum.Level;
using RobotVacuum.LevelEditor;
using RobotVacuumSim.EnvironmentModel;
using UnityEngine;
using Object = UnityEngine.Object;

namespace RobotVacuum.Tests
{
    public class ObstacleTests
    {
        [Test]
        public void Contains_RespectsRotation()
        {
            var obstacle = new Obstacle { center = Vector2.zero, size = new Vector2(2f, 0.4f), rotation = 90f };

            Assert.IsTrue(obstacle.Contains(new Vector2(0f, 0.9f)));
            Assert.IsFalse(obstacle.Contains(new Vector2(0.9f, 0f)));
        }

        [Test]
        public void Presets_LetTheVacuumUnderTablesBedsAndChairsOnly()
        {
            Assert.IsFalse(Obstacle.DefaultBlocks(ObstacleKind.Table));
            Assert.IsFalse(Obstacle.DefaultBlocks(ObstacleKind.Bed));
            Assert.IsFalse(Obstacle.DefaultBlocks(ObstacleKind.Chair));
            Assert.IsTrue(Obstacle.DefaultBlocks(ObstacleKind.Sofa));
            Assert.IsTrue(Obstacle.DefaultBlocks(ObstacleKind.Cabinet));
            Assert.IsTrue(Obstacle.DefaultBlocks(ObstacleKind.Box));
        }

        [Test]
        public void Clone_IsIndependent()
        {
            var original = new Obstacle { name = "Desk", center = Vector2.one };

            var copy = original.Clone();
            copy.name = "Other";
            copy.center = Vector2.zero;

            Assert.AreEqual("Desk", original.name);
            Assert.AreEqual(Vector2.one, original.center);
        }
    }

    public class LevelFurnitureTests
    {
        LevelData level;

        [SetUp]
        public void SetUp() => level = TestLevels.NewLevel(SampleLevels.PopulateBlank); // 4 × 3 m room

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(level);

        [Test]
        public void ObstacleIndexAt_PicksTheTopMost()
        {
            level.Obstacles.Add(new Obstacle { center = Vector2.zero, size = Vector2.one });
            level.Obstacles.Add(new Obstacle { center = new Vector2(0.4f, 0f), size = Vector2.one });

            Assert.AreEqual(1, level.ObstacleIndexAt(new Vector2(0.2f, 0f)));
            Assert.AreEqual(0, level.ObstacleIndexAt(new Vector2(-0.4f, 0f)));
            Assert.AreEqual(-1, level.ObstacleIndexAt(new Vector2(1.5f, 1f)));
        }

        [Test]
        public void IsBlocked_IgnoresFurnitureTheVacuumPassesUnder()
        {
            level.Obstacles.Add(new Obstacle { center = Vector2.zero, size = Vector2.one, blocksVacuum = false });
            Assert.IsFalse(level.IsBlocked(Vector2.zero));

            level.Obstacles[0].blocksVacuum = true;
            Assert.IsTrue(level.IsBlocked(Vector2.zero));
        }

        [Test]
        public void Snapshot_RoundTripsFurniture()
        {
            level.Obstacles.Add(new Obstacle
            {
                name = "Couch", kind = ObstacleKind.Sofa, center = new Vector2(1f, -0.5f),
                size = new Vector2(2f, 0.9f), rotation = 30f, blocksVacuum = true,
            });

            var snapshot = LevelSnapshot.Parse(LevelSnapshot.Serialize(level, "Flat"));
            var copy = snapshot.obstacles.Single();

            Assert.AreEqual("Couch", copy.name);
            Assert.AreEqual(ObstacleKind.Sofa, copy.kind);
            TestLevels.AssertNear(new Vector2(1f, -0.5f), copy.center);
            TestLevels.AssertNear(new Vector2(2f, 0.9f), copy.size);
            Assert.AreEqual(30f, copy.rotation, 1e-4f);
            Assert.IsTrue(copy.blocksVacuum);

            var other = TestLevels.NewLevel();
            try
            {
                snapshot.ApplyTo(other);
                Assert.AreEqual(1, other.Obstacles.Count);
            }
            finally
            {
                Object.DestroyImmediate(other);
            }
        }

        [Test]
        public void Parse_LevelSavedBeforeFurniture_HasNone()
        {
            var snapshot = LevelSnapshot.Parse("{\"name\":\"Old\",\"rooms\":[]}");

            Assert.IsNotNull(snapshot.obstacles);
            Assert.IsEmpty(snapshot.obstacles);
        }

        [Test]
        public void Parse_RepairsTinyOrUnnamedFurniture()
        {
            var snapshot = LevelSnapshot.Parse("{\"obstacles\":[{\"name\":\"\",\"kind\":1,\"size\":{\"x\":0,\"y\":-1}}]}");
            var obstacle = snapshot.obstacles.Single();

            Assert.AreEqual("Bed", obstacle.name);
            Assert.AreEqual(Obstacle.MinSize, obstacle.size.x, 1e-6f);
            Assert.AreEqual(Obstacle.MinSize, obstacle.size.y, 1e-6f);
        }

        [Test]
        public void ModelGrid_ExcludesOnlyCellsUnderBlockingFurniture()
        {
            level.Obstacles.Add(new Obstacle { center = new Vector2(-1f, 0f), size = Vector2.one, blocksVacuum = true });
            level.Obstacles.Add(new Obstacle { center = new Vector2(1f, 0f), size = Vector2.one, blocksVacuum = false });

            var grid = new ExternalModelGrid(level, null, 0.1f);
            grid.PopulateFromLevelObject();

            int excluded = 0;
            for (int row = 0; row < grid.Rows; row++)
            {
                for (int col = 0; col < grid.Cols; col++)
                {
                    if (!grid.GetCell(row, col).isNonCleanable) continue;

                    excluded++;
                    Assert.IsTrue(level.IsBlocked(grid.GridIndexToWorldCenter(row, col)));
                }
            }

            Assert.AreEqual(100, excluded, 12, "a 1 × 1 m footprint at 10 cm cells");
        }
    }

    public class EditorSessionFurnitureTests
    {
        EditorSession session;

        [SetUp]
        public void SetUp()
        {
            string path = Path.Combine(Path.GetTempPath(), "RobotVacuumTests", Guid.NewGuid().ToString("N"), "plan.json");
            session = new EditorSession(TestLevels.Snapshot("Plan", SampleLevels.PopulateBlank), null, path);
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(session.Level);
            session = null;
        }

        [Test]
        public void AddObstacle_UsesTheKindsDefaults_AndSelectsIt()
        {
            session.ObstacleKind = ObstacleKind.Table;

            session.AddObstacle(new Vector2(0.5f, 0.2f), new Vector2(1.4f, 0.8f));

            var table = session.SelectedObstacleData;
            Assert.IsNotNull(table);
            Assert.AreEqual("Table", table.name);
            Assert.AreEqual(ObstacleKind.Table, table.kind);
            Assert.IsFalse(table.blocksVacuum);
            TestLevels.AssertNear(new Vector2(0.5f, 0.2f), table.center);
            Assert.AreEqual(-1, session.SelectedRoom);
            Assert.AreEqual("Add Furniture", session.UndoLabel);

            session.AddObstacle(Vector2.zero, Vector2.one);
            Assert.AreEqual("Table 2", session.SelectedObstacleData.name);
        }

        [Test]
        public void AddObstacle_ClampsTinySizes_AndWrapsRotation()
        {
            session.AddObstacle(Vector2.zero, new Vector2(0f, 0.01f), -90f);

            var obstacle = session.GetObstacle(0);
            TestLevels.AssertNear(Vector2.one * Obstacle.MinSize, obstacle.size);
            Assert.AreEqual(270f, obstacle.rotation, 1e-4f);
        }

        [Test]
        public void SelectingARoom_DeselectsFurniture_AndViceVersa()
        {
            session.AddObstacle(Vector2.zero, Vector2.one);

            session.SelectRoom(0);
            Assert.AreEqual(-1, session.SelectedObstacle);

            session.SelectObstacle(0);
            Assert.AreEqual(-1, session.SelectedRoom);
            Assert.IsTrue(session.HasSelection);
        }

        [Test]
        public void SetObstacleBlocks_IsUndoable_AndIgnoresNoOps()
        {
            session.ObstacleKind = ObstacleKind.Sofa;
            session.AddObstacle(Vector2.zero, Vector2.one);

            session.SetObstacleBlocks(0, false);
            Assert.IsFalse(session.GetObstacle(0).blocksVacuum);
            Assert.AreEqual("Let Vacuum Pass", session.UndoLabel);

            session.Undo();
            Assert.IsTrue(session.GetObstacle(0).blocksVacuum);

            session.SetObstacleBlocks(0, true);
            Assert.AreEqual("Add Furniture", session.UndoLabel);
        }

        [Test]
        public void SetObstacleKind_TakesOnPassability_AndRenamesOnlyDefaultNames()
        {
            session.ObstacleKind = ObstacleKind.Sofa;
            session.AddObstacle(Vector2.zero, Vector2.one);

            session.SetObstacleKind(0, ObstacleKind.Table);

            var obstacle = session.GetObstacle(0);
            Assert.AreEqual("Table", obstacle.name);
            Assert.AreEqual(ObstacleKind.Table, obstacle.kind);
            Assert.IsFalse(obstacle.blocksVacuum);
            Assert.AreEqual(ObstacleKind.Table, session.ObstacleKind, "the next piece placed is a table too");

            session.RenameObstacle(0, "Dining table");
            session.SetObstacleKind(0, ObstacleKind.Cabinet);
            Assert.AreEqual("Dining table", session.GetObstacle(0).name);
        }

        [Test]
        public void DeleteSelection_RemovesTheSelectedFurniture()
        {
            session.AddObstacle(Vector2.zero, Vector2.one);

            session.DeleteSelection();

            Assert.IsEmpty(session.Level.Obstacles);
            Assert.IsFalse(session.HasSelection);

            session.Undo();
            Assert.AreEqual(1, session.Level.Obstacles.Count);
        }

        [Test]
        public void DuplicateObstacle_PlacesACopyBesideTheOriginal()
        {
            session.AddObstacle(Vector2.zero, new Vector2(1f, 0.5f));
            session.RenameObstacle(0, "Desk");

            session.DuplicateObstacle(0);

            var copy = session.GetObstacle(1);
            Assert.AreEqual("Desk copy", copy.name);
            TestLevels.AssertNear(new Vector2(1.2f, 0f), copy.center);
            Assert.AreEqual(1, session.SelectedObstacle);
        }

        [Test]
        public void LiveResizeAndRotate_ClampAndUndoAsOneStep()
        {
            session.AddObstacle(Vector2.zero, Vector2.one);

            session.Checkpoint("Resize Furniture");
            session.SetObstacleSizeLive(0, new Vector2(3f, 0f));
            session.SetObstacleRotationLive(0, 450f);
            session.Commit();

            TestLevels.AssertNear(new Vector2(3f, Obstacle.MinSize), session.GetObstacle(0).size);
            Assert.AreEqual(90f, session.GetObstacle(0).rotation, 1e-4f);

            session.Undo();
            TestLevels.AssertNear(Vector2.one, session.GetObstacle(0).size);
        }

        [Test]
        public void Validate_WarnsAboutFurnitureOutsideRooms_AndAVacuumStartingInsideIt()
        {
            session.ObstacleKind = ObstacleKind.Sofa;
            session.AddObstacle(new Vector2(20f, 20f), Vector2.one);
            session.ObstacleKind = ObstacleKind.Cabinet;
            session.AddObstacle(Vector2.zero, Vector2.one);

            var messages = session.Validate().Select(i => i.message).ToList();

            CollectionAssert.Contains(messages, "Sofa isn't inside any room.");
            CollectionAssert.Contains(messages, "The vacuum starts inside Cabinet.");
        }

        [Test]
        public void Validate_PassableFurnitureOverTheStart_IsFine()
        {
            session.ObstacleKind = ObstacleKind.Table;
            session.AddObstacle(Vector2.zero, Vector2.one);

            Assert.IsEmpty(session.Validate());
        }

        [Test]
        public void SnapToGridStep_IgnoresRoomCorners()
        {
            session.SnapIncrement = 1f;

            TestLevels.AssertNear(new Vector2(2f, 1f), session.SnapToGridStep(new Vector2(1.95f, 1.45f)));
        }
    }
}
