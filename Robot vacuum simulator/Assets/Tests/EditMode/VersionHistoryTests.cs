using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using RobotVacuum.Level;
using RobotVacuum.LevelEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace RobotVacuum.Tests
{
    /// <summary>
    /// Version history of floor plans and the configuration: the store, the library and editor hooks, comparing
    /// versions, and the configuration's undo/redo. Everything is written under a temp folder; the configuration's
    /// PlayerPrefs entry is put back after each test.
    /// </summary>
    public class VersionHistoryTests
    {
        const string ConfigPrefsKey = "RobotVacuum.SimConfig";

        string folder;
        string savedConfig;
        bool hadConfig;
        double clock;
        Func<double> realClock;

        [SetUp]
        public void SetUp()
        {
            folder = Path.Combine(Path.GetTempPath(), "RobotVacuumTests", Guid.NewGuid().ToString("N"));
            VersionHistory.Root = Path.Combine(folder, "History");
            LevelLibrary.FolderOverride = Path.Combine(folder, "Levels");

            hadConfig = PlayerPrefs.HasKey(ConfigPrefsKey);
            savedConfig = PlayerPrefs.GetString(ConfigPrefsKey, null);
            PlayerPrefs.DeleteKey(ConfigPrefsKey);
            SimConfig.ResetForTests();
            realClock = SimConfig.Clock;
            clock = 100.0;
            SimConfig.Clock = () => clock;
        }

        [TearDown]
        public void TearDown()
        {
            SimConfig.Clock = realClock;
            if (hadConfig) PlayerPrefs.SetString(ConfigPrefsKey, savedConfig);
            else PlayerPrefs.DeleteKey(ConfigPrefsKey);
            SimConfig.ResetForTests();

            VersionHistory.Root = null;
            LevelLibrary.FolderOverride = null;
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }

        // ---- the store -------------------------------------------------------------------------------------------

        [Test]
        public void Record_NumbersVersions_AndSkipsAnUnchangedSave()
        {
            var history = new VersionHistory(Path.Combine(folder, "doc"));
            Assert.AreEqual(1, history.Record("{\"a\":1}", "Created").number);
            Assert.IsNull(history.Record("{\"a\":1}", "Saved"), "nothing changed: no new version");
            var second = history.Record("{\"a\":2}", "Saved", new[] { "Add Room" });

            Assert.AreEqual(2, second.number);
            Assert.AreEqual(2, history.Versions.Count);
            Assert.AreEqual("{\"a\":1}", history.Read(1));
            Assert.AreEqual("{\"a\":2}", history.Read(2));
            CollectionAssert.AreEqual(new[] { "Add Room" }, second.edits);
            Assert.IsNull(history.Read(3));
        }

        [Test]
        public void History_IsKeptOnDisk_AndLabelsCanBeChanged()
        {
            string path = Path.Combine(folder, "doc");
            new VersionHistory(path).Record("one", "Created");
            new VersionHistory(path).Record("two", "Saved");

            var reopened = new VersionHistory(path);
            Assert.AreEqual(2, reopened.Versions.Count);
            Assert.IsTrue(reopened.Relabel(1, "Before the kitchen"));
            Assert.AreEqual("Before the kitchen", new VersionHistory(path).Find(1).label);
            Assert.AreEqual("one", new VersionHistory(path).Read(1), "relabelling leaves the content alone");
        }

        [Test]
        public void DeleteAll_RemovesTheHistory()
        {
            var history = new VersionHistory(Path.Combine(folder, "doc"));
            history.Record("one", "Created");
            history.DeleteAll();
            Assert.AreEqual(0, new VersionHistory(Path.Combine(folder, "doc")).Versions.Count);
        }

        [Test]
        public void Summarise_FoldsRepeats()
        {
            CollectionAssert.AreEqual(new[] { "Move Room ×3", "Add Doorway", "Move Room" },
                VersionHistory.Summarise(new[] { "Move Room", "Move Room", "Move Room", "Add Doorway", "Move Room" }));
            Assert.IsEmpty(VersionHistory.Summarise(new string[0]));
        }

        // ---- floor plans -----------------------------------------------------------------------------------------

        static LevelSnapshot Plan(string name = "Flat") => TestLevels.Snapshot(name, SampleLevels.PopulateApartment);

        [Test]
        public void CreatingAPlan_GivesItAnId_AndVersion1()
        {
            var plan = Plan();
            string path = LevelLibrary.Create(plan, "Created");

            Assert.IsNotEmpty(plan.id);
            Assert.AreEqual(plan.id, LevelLibrary.Read(path).id);
            var history = VersionHistory.ForFloorPlan(plan.id);
            Assert.AreEqual(1, history.Versions.Count);
            Assert.AreEqual("Created", history.Latest.label);
        }

        [Test]
        public void ADuplicate_GetsAHistoryOfItsOwn_AndDeletingRemovesIt()
        {
            var plan = Plan();
            string path = LevelLibrary.Create(plan);
            var copy = LevelSnapshot.Parse(LevelLibrary.Read(path).ToJson(false));
            string copyPath = LevelLibrary.Create(copy, "Duplicated");

            Assert.AreNotEqual(plan.id, copy.id);
            LevelLibrary.Delete(copyPath);
            Assert.IsFalse(File.Exists(copyPath));
            Assert.AreEqual(0, VersionHistory.ForFloorPlan(copy.id).Versions.Count);
            Assert.AreEqual(1, VersionHistory.ForFloorPlan(plan.id).Versions.Count, "the original's history is untouched");
        }

        [Test]
        public void APlanFromBeforeHistory_GetsAnIdWhenOpened()
        {
            var plan = Plan();
            Directory.CreateDirectory(LevelLibrary.Folder);
            string path = Path.Combine(LevelLibrary.Folder, "old.json");
            File.WriteAllText(path, plan.ToJson()); // no id: saved before version history

            var opened = LevelLibrary.Read(path);
            string id = LevelLibrary.EnsureId(path, opened);

            Assert.IsNotEmpty(id);
            Assert.AreEqual(id, LevelLibrary.Read(path).id);
            Assert.AreEqual("Before version history", VersionHistory.ForFloorPlan(id).Latest.label);
            Assert.AreEqual(id, LevelLibrary.EnsureId(path, LevelLibrary.Read(path)), "only once");
        }

        EditorSession OpenSession(out string path)
        {
            var plan = Plan();
            path = LevelLibrary.Create(plan);
            return new EditorSession(LevelLibrary.Read(path), null, path);
        }

        [Test]
        public void EachSave_IsAVersion_WithTheEditsMadeSinceTheLastOne()
        {
            var session = OpenSession(out _);
            try
            {
                session.Rename("Flat with study");
                session.AddRoom(TestLevels.Square(new Vector2(10f, 10f), 2f));
                session.Undo();
                session.Redo();
                CollectionAssert.AreEqual(new[] { "Rename", "Add Room", "Undo Add Room", "Redo Add Room" }, session.UnsavedEdits);

                var version = session.Save();

                Assert.AreEqual(2, version.number);
                Assert.AreEqual("Saved", version.label);
                CollectionAssert.AreEqual(new[] { "Rename", "Add Room", "Undo Add Room", "Redo Add Room" }, version.edits);
                Assert.IsEmpty(session.UnsavedEdits);
                Assert.IsNull(session.Save(), "saving again with no changes makes no version");
                Assert.AreEqual(2, session.History.Versions.Count);
            }
            finally
            {
                Object.DestroyImmediate(session.Level);
            }
        }

        [Test]
        public void RestoringAVersion_IsAnUndoableEdit_AndSavingKeepsEveryVersion()
        {
            var session = OpenSession(out _);
            try
            {
                int rooms = session.Level.Rooms.Count;
                session.AddRoom(TestLevels.Square(new Vector2(10f, 10f), 2f));
                session.Save(); // v2

                Assert.IsTrue(session.RestoreVersion(1));
                Assert.AreEqual(rooms, session.Level.Rooms.Count);
                Assert.IsTrue(session.IsDirty);
                Assert.AreEqual("Restore v1", session.UndoLabel);

                session.Undo();
                Assert.AreEqual(rooms + 1, session.Level.Rooms.Count);

                session.Redo();
                var version = session.Save(); // v3: the same as v1, kept as a new version
                Assert.AreEqual(3, version.number);
                Assert.AreEqual(3, session.History.Versions.Count);
                Assert.IsFalse(session.RestoreVersion(42));
            }
            finally
            {
                Object.DestroyImmediate(session.Level);
            }
        }

        // ---- comparing floor plans -------------------------------------------------------------------------------

        [Test]
        public void LevelDiff_OfTheSamePlan_IsEmpty()
        {
            Assert.IsEmpty(LevelDiff.Compare(Plan(), Plan()));
        }

        [Test]
        public void LevelDiff_ListsRoomsDoorwaysFurnitureAndTheStart()
        {
            var before = Plan();
            var after = Plan();
            after.name = "Flat 2";
            after.rooms.Add(new Room { name = "Study", outline = TestLevels.Square(new Vector2(10f, 10f), 2f) });
            after.rooms[1].floorIndex = before.rooms[1].floorIndex + 1;
            for (int i = 0; i < after.rooms[0].outline.Count; i++) after.rooms[0].outline[i] += new Vector2(0.5f, 0f);
            after.doorways.RemoveAt(2);
            after.obstacles.Add(new Obstacle { name = "Sofa", kind = ObstacleKind.Sofa, center = Vector2.one });
            after.robotSpawn += new Vector2(1f, 0f);

            string all = string.Join("\n", LevelDiff.Compare(before, after));

            StringAssert.Contains("Renamed “Flat” → “Flat 2”", all);
            StringAssert.Contains("Room “Study” added", all);
            StringAssert.Contains($"Room “{after.rooms[0].name}” moved", all);
            StringAssert.Contains($"Room “{after.rooms[1].name}” floor", all);
            StringAssert.Contains("Doorway", all);
            StringAssert.Contains("removed", all);
            StringAssert.Contains("“Sofa” added", all);
            StringAssert.Contains("Vacuum start moved", all);
        }

        [Test]
        public void LevelDiff_TellsAReshapeFromAMove_AndReportsRemovedRooms()
        {
            var before = Plan();
            var after = Plan();
            after.rooms[0].outline[2] += new Vector2(0.5f, 0.5f);
            string removed = after.rooms[3].name;
            after.rooms.RemoveAt(3);

            string all = string.Join("\n", LevelDiff.Compare(before, after));
            StringAssert.Contains($"Room “{after.rooms[0].name}” reshaped", all);
            StringAssert.Contains($"Room “{removed}” removed", all);
        }

        // ---- comparing configurations ----------------------------------------------------------------------------

        [Test]
        public void ConfigDiff_ListsChangedSettings_ByName()
        {
            var before = new SimConfig.Data();
            var after = new SimConfig.Data();
            after.seed = 42;
            after.vacuum.driveSpeed = before.vacuum.driveSpeed + 0.1f;
            after.floorEfforts.Add(new SimConfig.FloorEffort { floor = "Carpet", effort = 1.5f });

            var lines = ConfigDiff.Compare(before, after);
            string all = string.Join("\n", lines);

            StringAssert.Contains("Seed: 1 → 42", all);
            StringAssert.Contains("Vacuum › Drive speed:", all);
            StringAssert.Contains("Floor efforts › Carpet: (none) → 1.5", all);
            Assert.IsEmpty(ConfigDiff.Compare(before, new SimConfig.Data()));
        }

        [Test]
        public void ConfigDiff_Label_NamesTheSetting()
        {
            var before = new SimConfig.Data();
            var one = new SimConfig.Data { seed = 7 };
            var two = new SimConfig.Data { seed = 7, randomSeed = !before.randomSeed };
            Assert.AreEqual("Seed", ConfigDiff.Label(before, one));
            Assert.AreEqual("Seed and 1 more", ConfigDiff.Label(before, two));
        }

        // ---- the configuration's undo/redo and versions ----------------------------------------------------------

        [Test]
        public void ConfigChanges_CanBeUndoneAndRedone()
        {
            int seed = SimConfig.Seed;
            SimConfig.Seed = seed + 5;
            Assert.IsTrue(SimConfig.CanUndo);
            Assert.AreEqual("Seed", SimConfig.UndoLabel);

            Assert.IsTrue(SimConfig.Undo());
            Assert.AreEqual(seed, SimConfig.Seed);
            Assert.IsTrue(SimConfig.Redo());
            Assert.AreEqual(seed + 5, SimConfig.Seed);
        }

        [Test]
        public void ADraggedSlider_IsOneUndoStep_ButSeparateChangesAreNot()
        {
            float start = SimConfig.Vacuum.driveSpeed;
            for (int i = 1; i <= 5; i++)
            {
                SimConfig.Vacuum.driveSpeed = start + i * 0.01f;
                SimConfig.Save();
                clock += 0.1; // a drag: changes a tenth of a second apart
            }
            clock += 5.0;
            SimConfig.Vacuum.driveSpeed = start + 0.2f;
            SimConfig.Save();

            Assert.IsTrue(SimConfig.Undo());
            Assert.AreEqual(start + 0.05f, SimConfig.Vacuum.driveSpeed, 1e-5f, "the later change on its own");
            Assert.IsTrue(SimConfig.Undo());
            Assert.AreEqual(start, SimConfig.Vacuum.driveSpeed, 1e-5f, "the whole drag in one step");
            Assert.IsFalse(SimConfig.CanUndo);
        }

        [Test]
        public void Undo_KeepsTheObjectsScreensHold()
        {
            var vacuum = SimConfig.Vacuum;
            float start = vacuum.driveSpeed;
            vacuum.driveSpeed = start + 0.1f;
            SimConfig.Save();
            SimConfig.Undo();

            Assert.AreSame(vacuum, SimConfig.Vacuum);
            Assert.AreEqual(start, vacuum.driveSpeed, 1e-5f);
        }

        [Test]
        public void ConfigVersions_StartFromTheSettingsAtStartup_AndRestoreIsUndoable()
        {
            int seed = SimConfig.Seed;
            SimConfig.Seed = seed + 1;
            var first = SimConfig.CommitVersion("Configuration edited");

            Assert.AreEqual(2, first.number, "the starting settings went in first");
            Assert.AreEqual("Starting configuration", SimConfig.Versions.Find(1).label);
            CollectionAssert.AreEqual(new[] { "Seed" }, first.edits);
            Assert.IsNull(SimConfig.CommitVersion("Before a run"), "nothing changed since");

            Assert.IsTrue(SimConfig.RestoreVersion(1));
            Assert.AreEqual(seed, SimConfig.Seed);
            Assert.AreEqual("Restore v1", SimConfig.UndoLabel);
            SimConfig.Undo();
            Assert.AreEqual(seed + 1, SimConfig.Seed);
        }

        [Test]
        public void ResetToDefaults_IsUndoable_AndAVersion()
        {
            SimConfig.Seed = 1234;
            SimConfig.ResetToDefaults();
            Assert.AreEqual(new SimConfig.Data().seed, SimConfig.Seed);
            Assert.AreEqual("Reset to defaults", SimConfig.Versions.Latest.label);

            SimConfig.Undo();
            Assert.AreEqual(1234, SimConfig.Seed);
        }
    }
}
