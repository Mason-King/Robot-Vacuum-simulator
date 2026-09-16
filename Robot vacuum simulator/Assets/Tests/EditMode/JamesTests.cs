using System.IO;
using NUnit.Framework;
using RobotVacuum.Level;
using RobotVacuum.Sim;
using UnityEngine;
using Object = UnityEngine.Object;

namespace RobotVacuum.Tests
{
    public class JamesBrainTests
    {
        const float Step = 0.02f;

        LevelData level;
        GameObject levelHost;
        GameObject robotHost;
        VacuumRobot robot;

        [SetUp]
        public void SetUp()
        {
            level = TestLevels.NewLevel(SampleLevels.PopulatePictureCanvas);

            levelHost = new GameObject("Canvas");
            levelHost.AddComponent<LevelRenderer>().Level = level;

            robotHost = new GameObject("James");
            robotHost.transform.position = level.RobotSpawn;
            robot = robotHost.AddComponent<VacuumRobot>();
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(robotHost);
            Object.DestroyImmediate(levelHost);
            Object.DestroyImmediate(level);
        }

        JamesBrain Started(PictureKind kind = PictureKind.Invader)
        {
            var brain = new JamesBrain(Pictures.Get(kind));
            brain.Reset(robot);
            brain.Steer(robot, Step); // plans the canvas
            return brain;
        }

        [Test]
        public void UncoversThePhotoUnderTheVacuum_WithARoundBrush()
        {
            var brain = Started();
            robotHost.transform.position = brain.Canvas.center;

            brain.Steer(robot, Step);

            Assert.IsTrue(robot.Print.HasValue);
            var patch = robot.Print.Value;
            Assert.AreEqual(robot.CleaningWidth * 0.5f, patch.radius, 1e-4f, "a round brush the width of the vacuum");
            TestLevels.AssertNear(brain.Canvas.center, patch.area.center);
            Assert.AreEqual(robot.CleaningWidth, patch.area.width, 1e-4f);
            Assert.AreEqual(brain.Canvas, patch.canvas);
        }

        [Test]
        public void TheRevealFollowsTheVacuum()
        {
            var brain = Started();
            var somewhere = new Vector2(brain.Canvas.xMin + 0.4f, brain.Canvas.yMin + 0.4f);
            robotHost.transform.position = somewhere;

            brain.Steer(robot, Step);

            TestLevels.AssertNear(somewhere, robot.Print.Value.area.center, 1e-3f);
        }

        [Test]
        public void OffThePicture_NothingIsUncovered()
        {
            var brain = Started();
            robotHost.transform.position = new Vector2(brain.Canvas.xMax + 1f, brain.Canvas.yMax + 1f);

            brain.Steer(robot, Step);

            Assert.IsFalse(robot.Print.HasValue);
        }

        [Test]
        public void KeepsSuctionOff_AndBouncesAroundAtRandom()
        {
            var brain = Started(PictureKind.Heart);

            Assert.AreEqual(0f, robot.CleaningLimit, "it uncovers the photo rather than cleaning");
            Assert.AreEqual(1f, robot.SpeedScale, "it wanders at its usual speed");

            var manoeuvre = brain.AfterBump(robot);
            Assert.GreaterOrEqual(Mathf.Abs(manoeuvre.turn), robot.TurnAngleRange.x);
            Assert.LessOrEqual(Mathf.Abs(manoeuvre.turn), robot.TurnAngleRange.y);
            Assert.AreEqual(0f, manoeuvre.driveAfter, "no lanes: it just bounces");
        }

        [Test]
        public void WithoutItsPhoto_SaysSo_AndUncoversABuiltInPicture()
        {
            Pictures.ClearNamedImages();
            var brain = new JamesBrain();
            brain.Reset(robot);

            UnityEngine.TestTools.LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("James has no photo"));
            brain.Steer(robot, Step);

            Assert.IsNotNull(brain.Picture);
            Assert.Greater(brain.Canvas.width, 0f);
        }
    }

    public class NamedPictureTests
    {
        string folder;

        [SetUp]
        public void SetUp() => folder = Path.Combine(Path.GetTempPath(), "RobotVacuumTests", System.Guid.NewGuid().ToString("N"));

        [TearDown]
        public void TearDown()
        {
            Pictures.ClearNamedImages();
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }

        [Test]
        public void LoadNamed_WithNoSuchFile_IsNull()
        {
            Assert.IsNull(Pictures.LoadNamed("definitely-not-a-picture-here"));
        }

        [Test]
        public void FindNamedFile_IgnoresCapitalisation_AndTakesAnyImageExtension()
        {
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, "James.JPEG");
            File.WriteAllText(path, "not really an image, but it is the file we should find");

            Assert.AreEqual(path, Pictures.FindNamedFile(folder, "james"));
            Assert.IsNull(Pictures.FindNamedFile(folder, "someone-else"));
        }

        [Test]
        public void FindNamedFile_SkipsFilesThatArentImages()
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "james.txt"), "notes about james");

            Assert.IsNull(Pictures.FindNamedFile(folder, "james"));
        }

        [Test]
        public void FindNamedFile_WithNoSuchFolder_IsNull()
        {
            Assert.IsNull(Pictures.FindNamedFile(folder, "james"));
        }

        [Test]
        public void SearchFolders_AreTheAppsOwnAndThePlayersImages()
        {
            var folders = Pictures.SearchFolders;

            Assert.AreEqual(2, folders.Length);
            Assert.AreEqual(Application.streamingAssetsPath, folders[0]);
            Assert.AreEqual(Path.Combine(Application.persistentDataPath, "Images"), folders[1]);
        }
    }
}
