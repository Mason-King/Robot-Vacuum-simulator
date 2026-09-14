using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using RobotVacuum.Level;
using RobotVacuum.LevelEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace RobotVacuum.Tests
{
    public class EditorSessionTests
    {
        // Blank level: one 4 × 3 m room centred on the origin, corners in order
        // (-2,-1.5) (2,-1.5) (2,1.5) (-2,1.5), so edge 0 is the bottom wall.
        EditorSession session;
        string folder;
        readonly List<string> notices = new List<string>();

        [SetUp]
        public void SetUp()
        {
            folder = Path.Combine(Path.GetTempPath(), "RobotVacuumTests", Guid.NewGuid().ToString("N"));
            session = Open(SampleLevels.PopulateBlank);
        }

        [TearDown]
        public void TearDown()
        {
            // Not session.Dispose(): it calls Object.Destroy, which errors outside play mode.
            Object.DestroyImmediate(session.Level);
            session = null;
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }

        EditorSession Open(Action<LevelData> populate)
        {
            if (session != null) Object.DestroyImmediate(session.Level);

            notices.Clear();
            var opened = new EditorSession(TestLevels.Snapshot("Test Plan", populate), null, Path.Combine(folder, "test-plan.json"));
            opened.Notice += notices.Add;
            return opened;
        }

        Room Room(int index) => session.Level.Rooms[index];

        static List<Vector2> FarSquare() => TestLevels.Square(new Vector2(10f, 10f), 2f);

        // ---------------------------------------------------------------- document state

        [Test]
        public void NewSession_IsCleanWithNoHistory()
        {
            Assert.AreEqual("Test Plan", session.Name);
            Assert.AreEqual("Test Plan", session.Level.name);
            Assert.AreEqual(1, session.Level.Rooms.Count);
            Assert.IsFalse(session.IsDirty);
            Assert.IsFalse(session.CanUndo);
            Assert.IsFalse(session.CanRedo);
            Assert.IsFalse(session.HasSelection);
        }

        [Test]
        public void Edit_MarksDirty_AndUndoingItClearsDirty()
        {
            session.AddRoom(FarSquare());

            Assert.AreEqual(2, session.Level.Rooms.Count);
            Assert.IsTrue(session.IsDirty);
            Assert.AreEqual("Add Room", session.UndoLabel);

            Assert.IsTrue(session.Undo());

            Assert.AreEqual(1, session.Level.Rooms.Count);
            Assert.IsFalse(session.IsDirty);
            Assert.IsFalse(session.CanUndo);
            Assert.AreEqual("Add Room", session.RedoLabel);
            Assert.AreEqual("Undo Add Room", notices.Last());
        }

        [Test]
        public void Redo_ReappliesTheEdit()
        {
            session.AddRoom(FarSquare());
            session.Undo();

            Assert.IsTrue(session.Redo());

            Assert.AreEqual(2, session.Level.Rooms.Count);
            TestLevels.AssertNear(new Vector2(10f, 10f), Room(1).outline[0]);
            Assert.IsTrue(session.IsDirty);
            Assert.IsFalse(session.CanRedo);
            Assert.AreEqual("Redo Add Room", notices.Last());
        }

        [Test]
        public void UndoAndRedo_WithNothingRecorded_ReturnFalse()
        {
            Assert.IsFalse(session.Undo());
            Assert.IsFalse(session.Redo());
            Assert.IsEmpty(notices);
        }

        [Test]
        public void NewEdit_AfterUndo_DiscardsRedo()
        {
            session.AddRoom(FarSquare());
            session.Undo();

            session.SetSpawn(Vector2.one);

            Assert.IsFalse(session.CanRedo);
        }

        [Test]
        public void MultipleEdits_UndoInReverseOrder()
        {
            session.SetSpawn(new Vector2(1f, 1f));
            session.SetWall(0, 0, false);
            session.RenameRoom(0, "Den");

            session.Undo();
            Assert.AreEqual("Room 1", Room(0).name);
            Assert.IsFalse(Room(0).HasWall(0));

            session.Undo();
            Assert.IsTrue(Room(0).HasWall(0));
            Assert.AreEqual(new Vector2(1f, 1f), session.Level.RobotSpawn);

            session.Undo();
            Assert.AreEqual(Vector2.zero, session.Level.RobotSpawn);
            Assert.IsFalse(session.IsDirty);
        }

        [Test]
        public void CheckpointWithoutAChange_LeavesNoUndoStep()
        {
            session.Checkpoint("Move Point");
            session.Commit();

            Assert.IsFalse(session.CanUndo);
            Assert.IsFalse(session.IsDirty);
        }

        [Test]
        public void LiveDrag_IsOneUndoStep_AndOnlyRaisesLiveChangesUntilCommit()
        {
            var changes = new List<bool>();
            session.LevelChanged += changes.Add;

            session.Checkpoint("Wall Thickness");
            session.SetWallThicknessLive(0.2f);
            session.SetWallThicknessLive(0.3f);
            session.Commit();

            CollectionAssert.AreEqual(new[] { true, true, false }, changes);
            Assert.AreEqual(0.3f, session.Level.WallThickness, 1e-6f);

            session.Undo();

            Assert.AreEqual(0.12f, session.Level.WallThickness, 1e-6f);
            Assert.IsFalse(session.CanUndo);
        }

        [Test]
        public void Edit_RaisesLevelAndDocumentChanged()
        {
            int level = 0, document = 0;
            session.LevelChanged += live => { if (!live) level++; };
            session.DocumentChanged += () => document++;

            session.SetSpawn(Vector2.one);

            Assert.AreEqual(1, level);
            Assert.AreEqual(1, document);
        }

        [Test]
        public void Save_WritesTheLevel_AndClearsDirty()
        {
            session.Rename("Kitchen Plan");
            session.AddRoom(FarSquare());

            session.Save();

            Assert.IsFalse(session.IsDirty);
            var saved = LevelLibrary.Read(session.FilePath);
            Assert.AreEqual("Kitchen Plan", saved.name);
            Assert.AreEqual(2, saved.rooms.Count);
        }

        [Test]
        public void Undo_PastASave_MarksDirtyAgain()
        {
            session.AddRoom(FarSquare());
            session.Save();

            session.Undo();
            Assert.IsTrue(session.IsDirty);

            session.Redo();
            Assert.IsFalse(session.IsDirty);
        }

        [Test]
        public void Rename_TrimsInput_AndIgnoresBlankOrUnchanged()
        {
            session.Rename("  Den  ");
            session.Rename("   ");
            session.Rename(null);
            session.Rename("Den");

            Assert.AreEqual("Den", session.Name);
            Assert.AreEqual("Den", session.Level.name);

            session.Undo();
            Assert.AreEqual("Test Plan", session.Name);
            Assert.IsFalse(session.CanUndo, "only the first rename should be recorded");
        }

        // ---------------------------------------------------------------- selection & options

        [Test]
        public void SelectRoom_InvalidIndex_ClearsSelection()
        {
            session.SelectRoom(0);
            Assert.AreSame(Room(0), session.SelectedRoomData);

            session.SelectRoom(5);

            Assert.AreEqual(-1, session.SelectedRoom);
            Assert.IsFalse(session.HasSelection);
        }

        [Test]
        public void SelectionChanged_OnlyFiresOnActualChange()
        {
            int fired = 0;
            session.SelectionChanged += () => fired++;

            session.SelectRoom(0);
            session.SelectRoom(0);
            session.ClearSelection();
            session.ClearSelection();

            Assert.AreEqual(2, fired);
        }

        [Test]
        public void SelectingADoorway_DeselectsTheRoom_AndViceVersa()
        {
            session.PlaceDoorway(new Vector2(0f, -1.5f));
            session.SelectRoom(0);
            Assert.AreEqual(-1, session.SelectedDoorway);

            session.SelectDoorway(0);
            Assert.AreEqual(-1, session.SelectedRoom);
            Assert.IsNotNull(session.SelectedDoorwayData);
        }

        [Test]
        public void Undo_DropsASelectionThatNoLongerExists()
        {
            session.AddRoom(FarSquare());
            Assert.AreEqual(1, session.SelectedRoom);

            session.Undo();

            Assert.AreEqual(-1, session.SelectedRoom);
        }

        [Test]
        public void ToolChanged_FiresOncePerChange()
        {
            int fired = 0;
            session.ToolChanged += () => fired++;

            session.Tool = EditorTool.Pen;
            session.Tool = EditorTool.Pen;

            Assert.AreEqual(EditorTool.Pen, session.Tool);
            Assert.AreEqual(1, fired);
        }

        [Test]
        public void Options_ClampAndNotify()
        {
            int fired = 0;
            session.OptionsChanged += () => fired++;

            session.PaintFloor = -4;
            session.SnapIncrement = 0f;
            session.ShowGrid = true; // already on

            Assert.AreEqual(0, session.PaintFloor);
            Assert.AreEqual(0.01f, session.SnapIncrement, 1e-6f);
            Assert.AreEqual(1, fired, "PaintFloor was already 0, so only the snap increment changed");
        }

        // ---------------------------------------------------------------- rooms

        [Test]
        public void AddRoom_UsesCurrentFloorAndWallDefaults_AndSelectsIt()
        {
            session.PaintFloor = 3;
            session.NewRoomsHaveWalls = false;

            session.AddRoom(FarSquare());

            Assert.AreEqual(3, Room(1).floorIndex);
            Assert.AreEqual("Room 2", Room(1).name);
            Assert.AreEqual(0, session.WallCount(Room(1)));
            Assert.AreEqual(1, session.SelectedRoom);
        }

        [Test]
        public void AddRoom_SkipsNamesAlreadyInUse()
        {
            session.AddRoom(FarSquare());
            session.RenameRoom(0, "Room 3");

            session.AddRoom(TestLevels.Square(new Vector2(20f, 20f)));

            Assert.AreEqual("Room 4", Room(2).name);
        }

        [Test]
        public void DeleteRoom_RemovesItsDoorways_AndUndoBringsThemBack()
        {
            session = Open(SampleLevels.PopulateApartment);
            session.SelectRoom(0);

            session.DeleteSelection();

            Assert.AreEqual(3, session.Level.Rooms.Count);
            Assert.AreEqual(1, session.Level.Doorways.Count);
            Assert.AreEqual((1, 2), (session.Level.Doorways[0].roomA, session.Level.Doorways[0].roomB));
            Assert.IsFalse(session.HasSelection);
            Assert.AreEqual("Deleted Living Room", notices.Last());

            session.Undo();

            Assert.AreEqual(4, session.Level.Rooms.Count);
            Assert.AreEqual(3, session.Level.Doorways.Count);
            Assert.AreEqual("Living Room", Room(0).name);
        }

        [Test]
        public void DeleteRoom_InvalidIndex_RecordsNothing()
        {
            session.DeleteRoom(7);

            Assert.IsFalse(session.CanUndo);
            Assert.IsEmpty(notices);
        }

        [Test]
        public void DuplicateRoom_PlacesACopyBesideTheOriginal()
        {
            session.SetWall(0, 2, false);

            session.DuplicateRoom(0);

            var copy = Room(1);
            Assert.AreEqual("Room 1 copy", copy.name);
            Assert.AreEqual(1, session.SelectedRoom);
            for (int v = 0; v < 4; v++)
                TestLevels.AssertNear(Room(0).outline[v] + new Vector2(4.4f, 0f), copy.outline[v]);
            Assert.IsFalse(copy.HasWall(2), "deleted walls come along");

            copy.outline[0] = Vector2.zero;
            Assert.AreEqual(new Vector2(-2f, -1.5f), Room(0).outline[0], "copy must not share the outline list");
        }

        [Test]
        public void SetRoomFloor_ChangesCovering_AndBecomesThePaintFloor()
        {
            session.SetRoomFloor(0, 4);

            Assert.AreEqual(4, Room(0).floorIndex);
            Assert.AreEqual(4, session.PaintFloor);
            Assert.AreEqual("Floor Covering", session.UndoLabel);

            session.Undo();
            session.SetRoomFloor(0, 0); // already 0

            Assert.AreEqual(0, session.PaintFloor);
            Assert.IsFalse(session.CanUndo);
        }

        [Test]
        public void SetWall_RemovesAndRestoresOneWall()
        {
            session.SetWall(0, 1, false);
            session.SetWall(0, 1, false); // no-op, not recorded

            Assert.AreEqual(3, session.WallCount(Room(0)));
            Assert.AreEqual("Remove Wall", session.UndoLabel);

            session.SetWall(0, 1, true);
            Assert.AreEqual(4, session.WallCount(Room(0)));
            Assert.AreEqual("Restore Wall", session.UndoLabel);

            session.Undo();
            session.Undo();
            Assert.IsFalse(session.CanUndo);
        }

        [Test]
        public void SetAllWalls_TogglesEveryEdge()
        {
            session.SetAllWalls(0, false);
            Assert.AreEqual(0, session.WallCount(Room(0)));

            session.SetAllWalls(0, true);
            Assert.AreEqual(4, session.WallCount(Room(0)));
            Assert.AreEqual(0, session.WallCount(null));
        }

        [Test]
        public void InsertVertex_KeepsWallFlagsOnTheRightEdges()
        {
            session.SetWall(0, 2, false); // top wall

            session.InsertVertex(0, 0, new Vector2(0f, -1.5f));

            Assert.AreEqual(5, Room(0).outline.Count);
            Assert.AreEqual(new[] { true, true, true, false, true }, Room(0).wallEdges);
            Assert.AreEqual("Add Point", session.UndoLabel);

            session.Undo();
            Assert.AreEqual(4, Room(0).outline.Count);
            Assert.AreEqual(new[] { true, true, false, true }, Room(0).wallEdges);
        }

        [Test]
        public void RemoveVertex_StopsAtATriangle()
        {
            session.RemoveVertex(0, 0);
            Assert.AreEqual(3, Room(0).outline.Count);

            session.RemoveVertex(0, 0);

            Assert.AreEqual(3, Room(0).outline.Count);
            session.Undo();
            Assert.IsFalse(session.CanUndo, "the refused removal should not be recorded");
        }

        // ---------------------------------------------------------------- doorways & free walls

        [Test]
        public void PlaceDoorway_NearAWall_SnapsOntoIt()
        {
            Assert.IsTrue(session.PlaceDoorway(new Vector2(0.5f, -1.4f)));

            var doorway = session.Level.Doorways.Single();
            TestLevels.AssertNear(new Vector2(0.5f, -1.5f), doorway.center);
            Assert.AreEqual(0, doorway.roomA);
            Assert.AreEqual(-1, doorway.roomB, "an outer wall opens onto the outside");
            Assert.AreEqual(EditorSession.DefaultDoorwayWidth, doorway.width);
            Assert.AreEqual(0, session.SelectedDoorway);
            Assert.AreEqual("Room 1 ↔ Outside", session.DoorwayTitle(doorway));
        }

        [Test]
        public void PlaceDoorway_AwayFromWalls_IsRefused()
        {
            Assert.IsFalse(session.PlaceDoorway(new Vector2(20f, 20f)));

            Assert.IsEmpty(session.Level.Doorways);
            Assert.IsFalse(session.CanUndo);
            Assert.AreEqual("Click on a wall to place a doorway", notices.Last());
        }

        [Test]
        public void PlaceDoorway_OnASharedWall_LinksBothRooms()
        {
            session = Open(SampleLevels.PopulateApartment);

            Assert.IsTrue(session.PlaceDoorway(new Vector2(1.65f, -1f)));

            var doorway = session.Level.Doorways.Last();
            CollectionAssert.AreEquivalent(new[] { 0, 1 }, new[] { doorway.roomA, doorway.roomB });
        }

        [Test]
        public void MoveDoorwayLive_RelinksToTheNewWall_WithoutRecordingHistory()
        {
            session = Open(SampleLevels.PopulateApartment);

            session.MoveDoorwayLive(0, new Vector2(-2.2f, -2.6f)); // hall ↔ bedroom wall

            var doorway = session.Level.Doorways[0];
            CollectionAssert.AreEquivalent(new[] { 2, 3 }, new[] { doorway.roomA, doorway.roomB });
            Assert.IsFalse(session.CanUndo);
        }

        [Test]
        public void MoveDoorwayLive_OffEveryWall_KeepsItsLinks()
        {
            session = Open(SampleLevels.PopulateApartment);

            session.MoveDoorwayLive(0, new Vector2(40f, 40f));

            var doorway = session.Level.Doorways[0];
            Assert.AreEqual((0, 1), (doorway.roomA, doorway.roomB));
            TestLevels.AssertNear(new Vector2(40f, 40f), doorway.center);
        }

        [Test]
        public void SetDoorwayWidthLive_ClampsToSensibleWidths()
        {
            session.PlaceDoorway(new Vector2(0f, -1.5f));

            session.SetDoorwayWidthLive(0, 50f);
            Assert.AreEqual(EditorSession.MaxDoorwayWidth, session.Level.Doorways[0].width);

            session.SetDoorwayWidthLive(0, 0f);
            Assert.AreEqual(EditorSession.MinDoorwayWidth, session.Level.Doorways[0].width);
        }

        [Test]
        public void DeleteSelection_RemovesTheSelectedDoorway()
        {
            session.PlaceDoorway(new Vector2(0f, -1.5f));

            session.DeleteSelection();

            Assert.IsEmpty(session.Level.Doorways);
            Assert.AreEqual(-1, session.SelectedDoorway);
            Assert.AreEqual("Delete Doorway", session.UndoLabel);
        }

        [Test]
        public void WallStrokes_CanBeDrawnFoundAndDeleted()
        {
            session.AddWallStroke(new Vector2(0f, -1.5f), new Vector2(0f, 1.5f));

            Assert.AreEqual(0, session.FindWallStroke(new Vector2(0.05f, 0f), 0.1f));
            Assert.AreEqual(-1, session.FindWallStroke(new Vector2(1f, 0f), 0.1f));

            session.DeleteWallStroke(5); // out of range, ignored
            session.DeleteWallStroke(0);

            Assert.IsEmpty(session.Level.WallStrokes);
            session.Undo();
            Assert.AreEqual(1, session.Level.WallStrokes.Count);
        }

        [Test]
        public void SetWallColor_SameColour_RecordsNothing()
        {
            session.SetWallColor(session.Level.WallColor);
            Assert.IsFalse(session.CanUndo);

            session.SetWallColor(Color.red);
            Assert.AreEqual(Color.red, session.Level.WallColor);
            Assert.AreEqual("Wall Colour", session.UndoLabel);
        }

        // ---------------------------------------------------------------- snapping & hit testing

        [Test]
        public void Snap_RoundsToTheGrid()
        {
            session.SnapToVertices = false;
            session.SnapIncrement = 0.25f;

            TestLevels.AssertNear(new Vector2(0.25f, 0.25f), session.Snap(new Vector2(0.13f, 0.37f), 100f));
            TestLevels.AssertNear(new Vector2(-0.5f, 1f), session.Snap(new Vector2(-0.62f, 1.1f), 100f));
        }

        [Test]
        public void Snap_PrefersANearbyCornerOverTheGrid()
        {
            session.SnapIncrement = 1f;

            // Grid alone would give (2, 1); the room corner at (2, 1.5) is within reach.
            TestLevels.AssertNear(new Vector2(2f, 1.5f), session.Snap(new Vector2(1.95f, 1.45f), 100f));
        }

        [Test]
        public void Snap_CornerReachScalesWithZoom()
        {
            session.SnapIncrement = 1f;
            var point = new Vector2(1.6f, 1.2f);

            // Zoomed in, the corner 0.5 m away is out of reach; zoomed out, it is not.
            TestLevels.AssertNear(new Vector2(2f, 1f), session.Snap(point, 100f));
            TestLevels.AssertNear(new Vector2(2f, 1.5f), session.Snap(point, 10f));
        }

        [Test]
        public void Snap_IgnoresTheCornerBeingDragged()
        {
            session.SnapIncrement = 1f;
            var point = new Vector2(1.95f, 1.45f);

            TestLevels.AssertNear(new Vector2(2f, 1f), session.Snap(point, 100f, skipRoom: 0, skipVertex: 2));
            TestLevels.AssertNear(new Vector2(2f, 1f), session.Snap(point, 100f, skipRoom: 0));
            TestLevels.AssertNear(new Vector2(2f, 1.5f), session.Snap(point, 100f, skipRoom: 0, skipVertex: 1));
        }

        [Test]
        public void Snap_WithEverythingOff_ReturnsThePoint()
        {
            session.SnapToGrid = false;
            session.SnapToVertices = false;
            var point = new Vector2(1.95f, 1.45f);

            Assert.AreEqual(point, session.Snap(point, 100f));
        }

        [Test]
        public void FindVertex_ReturnsTheClosestCornerWithinTolerance()
        {
            Assert.AreEqual(2, session.FindVertex(0, new Vector2(1.9f, 1.4f), 0.2f));
            Assert.AreEqual(-1, session.FindVertex(0, Vector2.zero, 0.2f));
            Assert.AreEqual(-1, session.FindVertex(3, Vector2.zero, 10f));
        }

        [Test]
        public void TryFindEdge_ProjectsOntoTheNearestEdge()
        {
            Assert.IsTrue(EditorSession.TryFindEdge(Room(0), new Vector2(0.5f, -1.4f), 0.2f, out int edge, out Vector2 onEdge));
            Assert.AreEqual(0, edge);
            TestLevels.AssertNear(new Vector2(0.5f, -1.5f), onEdge);

            Assert.IsFalse(EditorSession.TryFindEdge(Room(0), Vector2.zero, 0.2f, out edge, out _));
            Assert.AreEqual(-1, edge);
            Assert.IsFalse(EditorSession.TryFindEdge(null, Vector2.zero, 10f, out _, out _));
        }

        [Test]
        public void TryFindAnyEdge_SearchesEveryRoom()
        {
            session.AddRoom(FarSquare());

            Assert.IsTrue(session.TryFindAnyEdge(new Vector2(11f, 9.9f), 0.2f, out int room, out int edge, out Vector2 onEdge));
            Assert.AreEqual((1, 0), (room, edge));
            TestLevels.AssertNear(new Vector2(11f, 10f), onEdge);
        }

        [Test]
        public void NearestWall_GivesDistanceAndDirection()
        {
            Assert.AreEqual(0.5f, session.NearestWall(new Vector2(0f, -1f), out Vector2 direction), TestLevels.Tolerance);
            TestLevels.AssertNear(Vector2.right, direction);
        }

        [Test]
        public void FindDoorway_WithinTolerance()
        {
            session.PlaceDoorway(new Vector2(0f, -1.5f));

            Assert.AreEqual(0, session.FindDoorway(new Vector2(0.1f, -1.5f), 0.2f));
            Assert.AreEqual(-1, session.FindDoorway(Vector2.zero, 0.2f));
        }

        // ---------------------------------------------------------------- validation

        [Test]
        public void Validate_SampleApartment_IsClean()
        {
            session = Open(SampleLevels.PopulateApartment);

            Assert.IsEmpty(session.Validate().Select(i => i.message));
        }

        [Test]
        public void Validate_EmptyLevel_PromptsForARoom()
        {
            session = Open(null);

            var issue = session.Validate().Single();
            Assert.AreEqual(IssueSeverity.Warning, issue.severity);
            Assert.AreEqual("Draw a room to get started.", issue.message);
        }

        [Test]
        public void Validate_SelfCrossingRoom_IsAnError()
        {
            session.AddRoom(new List<Vector2> { new Vector2(0, 0), new Vector2(1, 1), new Vector2(1, 0), new Vector2(0, 1) });

            var issues = session.Validate();

            Assert.IsTrue(issues.Any(i => i.severity == IssueSeverity.Error && i.roomIndex == 1 && i.message.Contains("crosses itself")));
        }

        [Test]
        public void Validate_RoomWithTooFewCorners_IsAnError()
        {
            session.Level.Rooms.Add(new Room { name = "Sliver", outline = { Vector2.zero, Vector2.one } });

            var issues = session.Validate();

            Assert.IsTrue(issues.Any(i => i.severity == IssueSeverity.Error && i.roomIndex == 1 && i.message == "Sliver has fewer than 3 corners."));
        }

        [Test]
        public void Validate_SpawnOutsideEveryRoom_Warns()
        {
            session.SetSpawn(new Vector2(50f, 50f));

            Assert.AreEqual("The vacuum starts outside every room.", session.Validate().Single().message);
        }

        [Test]
        public void Validate_RoomWithNoDoorwayPath_Warns()
        {
            session.AddRoom(FarSquare());

            var issue = session.Validate().Single();
            Assert.AreEqual(1, issue.roomIndex);
            Assert.AreEqual("No doorway path to Room 2.", issue.message);
        }

        [Test]
        public void Validate_DoorwayNotOnAWall_Warns()
        {
            session.Level.Doorways.Add(new Doorway { roomA = 0, center = Vector2.zero });

            Assert.AreEqual("A doorway isn't on a wall, so it opens nothing.", session.Validate().Single().message);
        }
    }
}
