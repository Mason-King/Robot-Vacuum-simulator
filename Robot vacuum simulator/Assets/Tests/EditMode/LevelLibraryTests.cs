using System;
using System.IO;
using NUnit.Framework;
using RobotVacuum.LevelEditor;

namespace RobotVacuum.Tests
{
    /// <summary>
    /// File tests write to a throwaway temp folder. <see cref="LevelLibrary.Create"/> and
    /// <see cref="LevelLibrary.LoadAll"/> are left out because they always use the real
    /// persistent-data folder, where test files would show up in the app's library.
    /// </summary>
    public class LevelLibraryTests
    {
        string folder;

        [SetUp]
        public void SetUp() => folder = Path.Combine(Path.GetTempPath(), "RobotVacuumTests", Guid.NewGuid().ToString("N"));

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }

        [Test]
        public void UniqueName_KeepsAFreeName()
        {
            Assert.AreEqual("Kitchen", LevelLibrary.UniqueName("Kitchen", new[] { "Hall" }));
        }

        [Test]
        public void UniqueName_NumbersPastTakenNames_IgnoringCase()
        {
            Assert.AreEqual("Kitchen 2", LevelLibrary.UniqueName("Kitchen", new[] { "kitchen" }));
            Assert.AreEqual("Kitchen 4", LevelLibrary.UniqueName("Kitchen", new[] { "Kitchen", "Kitchen 2", "KITCHEN 3" }));
        }

        [Test]
        public void Write_CreatesTheFolder_AndLeavesNoTempFile()
        {
            string path = Path.Combine(folder, "nested", "plan.json");

            LevelLibrary.Write(path, "{\"name\":\"Plan\"}");

            Assert.IsTrue(File.Exists(path));
            Assert.IsFalse(File.Exists(path + ".tmp"));
            Assert.AreEqual("Plan", LevelLibrary.Read(path).name);
        }

        [Test]
        public void Write_ReplacesAnExistingFile()
        {
            string path = Path.Combine(folder, "plan.json");
            LevelLibrary.Write(path, "{\"name\":\"First\"}");

            LevelLibrary.Write(path, "{\"name\":\"Second\"}");

            Assert.AreEqual("Second", LevelLibrary.Read(path).name);
            Assert.AreEqual(1, Directory.GetFiles(folder).Length);
        }

        [Test]
        public void Read_FileThatIsNotALevel_ReturnsNull()
        {
            string path = Path.Combine(folder, "notes.json");
            Directory.CreateDirectory(folder);
            File.WriteAllText(path, "shopping list: milk, eggs");

            Assert.IsNull(LevelLibrary.Read(path));
        }

        [Test]
        public void Delete_RemovesTheFile_AndIgnoresMissingOnes()
        {
            string path = Path.Combine(folder, "plan.json");
            LevelLibrary.Write(path, "{}");

            LevelLibrary.Delete(path);
            Assert.IsFalse(File.Exists(path));

            Assert.DoesNotThrow(() => LevelLibrary.Delete(path));
        }
    }
}
