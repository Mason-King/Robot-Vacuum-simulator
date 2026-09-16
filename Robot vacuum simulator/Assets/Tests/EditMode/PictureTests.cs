using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using RobotVacuum.Level;
using RobotVacuum.Sim;
using UnityEngine;
using Object = UnityEngine.Object;

namespace RobotVacuum.Tests
{
    public class PixelPictureTests
    {
        [Test]
        public void Parse_MapsSymbolsToShades_AndPadsShortRows()
        {
            var picture = PixelPicture.Parse("Test", "#+o.", " #");

            Assert.AreEqual(4, picture.Width);
            Assert.AreEqual(2, picture.Height);
            Assert.AreEqual(1f, picture[0, 0]);
            Assert.AreEqual(0.6f, picture[1, 0], 1e-5f);
            Assert.AreEqual(0.4f, picture[2, 0], 1e-5f);
            Assert.AreEqual(0.15f, picture[3, 0], 1e-5f);
            Assert.AreEqual(0f, picture[0, 1]);
            Assert.AreEqual(1f, picture[1, 1]);
            Assert.AreEqual(0f, picture[3, 1], "padded");
        }

        [Test]
        public void Sample_ReadsTopRowAtTheTop_AndNothingOutside()
        {
            var picture = PixelPicture.Parse("Test", "# ", " #");
            var canvas = new Rect(0f, 0f, 2f, 2f);

            Assert.AreEqual(1f, picture.Sample(canvas, new Vector2(0.5f, 1.5f)), "top-left");
            Assert.AreEqual(0f, picture.Sample(canvas, new Vector2(1.5f, 1.5f)), "top-right");
            Assert.AreEqual(1f, picture.Sample(canvas, new Vector2(1.5f, 0.5f)), "bottom-right");
            Assert.AreEqual(0f, picture.Sample(canvas, new Vector2(3f, 1f)), "outside");
        }

        [Test]
        public void EveryPictureKind_HasANameAndSomethingToDraw()
        {
            var kinds = (PictureKind[])Enum.GetValues(typeof(PictureKind));
            Assert.AreEqual(kinds.Length, Pictures.Names.Length);

            foreach (var kind in kinds)
            {
                var picture = Pictures.Get(kind);
                Assert.Greater(picture.Width, 0, kind.ToString());

                bool anyInk = false;
                for (int y = 0; y < picture.Height && !anyInk; y++)
                    for (int x = 0; x < picture.Width && !anyInk; x++)
                        anyInk = picture[x, y] > 0f;
                Assert.IsTrue(anyInk, kind.ToString());
            }
        }
    }

    public class ImagePictureTests
    {
        string folder;

        [SetUp]
        public void SetUp() => folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "RobotVacuumTests", Guid.NewGuid().ToString("N"));

        [TearDown]
        public void TearDown()
        {
            Pictures.ClearCustomImage();
            if (System.IO.Directory.Exists(folder)) System.IO.Directory.Delete(folder, true);
        }

        // Every row: white, white, black, and white made fully transparent.
        static Texture2D Stripes()
        {
            var texture = new Texture2D(4, 2, TextureFormat.RGBA32, false);
            var row = new[] { Color.white, Color.white, Color.black, new Color(1f, 1f, 1f, 0f) };
            texture.SetPixels(row.Concat(row).ToArray());
            texture.Apply();
            return texture;
        }

        [Test]
        public void FromTexture_BrightIsClean_DarkAndTransparentLeaveTheFloor()
        {
            var texture = Stripes();
            try
            {
                var picture = PixelPicture.FromTexture("Stripes", texture);

                Assert.AreEqual(4, picture.Width);
                Assert.AreEqual(2, picture.Height);
                Assert.AreEqual(1f, picture[0, 0], 0.02f);
                Assert.AreEqual(1f, picture[1, 1], 0.02f);
                Assert.AreEqual(0f, picture[2, 0], 0.02f);
                Assert.AreEqual(0f, picture[3, 1], 0.02f);
            }
            finally
            {
                Object.DestroyImmediate(texture);
            }
        }

        [Test]
        public void FromTexture_ScalesLargeImagesDown()
        {
            var texture = new Texture2D(1000, 10);
            try
            {
                var picture = PixelPicture.FromTexture("Wide", texture, 256);

                Assert.AreEqual(256, picture.Width);
                Assert.AreEqual(3, picture.Height);
            }
            finally
            {
                Object.DestroyImmediate(texture);
            }
        }

        [Test]
        public void LoadImage_ReadsAPng_AndBecomesTheImagePicture()
        {
            System.IO.Directory.CreateDirectory(folder);
            string path = System.IO.Path.Combine(folder, "stripes.png");
            var texture = Stripes();
            System.IO.File.WriteAllBytes(path, texture.EncodeToPNG());
            Object.DestroyImmediate(texture);

            var picture = Pictures.LoadImage(path);

            Assert.IsNotNull(picture);
            Assert.AreEqual("stripes", picture.Name);
            Assert.AreSame(picture, Pictures.Get(PictureKind.Image));
            Assert.AreEqual(1f, picture[0, 0], 0.02f);
            Assert.AreEqual(0f, picture[2, 0], 0.02f);
        }

        [Test]
        public void LoadImage_MissingFile_ReturnsNull_AndImageFallsBackToTheHeart()
        {
            Assert.IsNull(Pictures.LoadImage(System.IO.Path.Combine(folder, "nope.png")));
            Assert.AreSame(Pictures.Get(PictureKind.Heart), Pictures.Get(PictureKind.Image));
        }
    }

    public class PictureBrainTests
    {
        LevelData level;
        GameObject levelHost;
        GameObject robotHost;
        VacuumRobot robot;

        [SetUp]
        public void SetUp()
        {
            level = TestLevels.NewLevel(SampleLevels.PopulatePictureCanvas);

            // The robot finds the renderer when it wakes, so the level goes in first.
            levelHost = new GameObject("Canvas");
            levelHost.AddComponent<LevelRenderer>().Level = level;

            robotHost = new GameObject("Painter");
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

        [Test]
        public void PictureCanvas_IsOneEmptySquareRoom_WithTheVacuumInside()
        {
            Assert.AreEqual(1, level.Rooms.Count);
            Assert.IsEmpty(level.Obstacles);
            Assert.AreEqual(36f, level.TotalFloorArea(), 1e-3f);
            Assert.AreEqual(0, level.RoomIndexAt(level.RobotSpawn));
        }

        [Test]
        public void Plan_FitsThePictureInsideTheRoom_WithRowsCloseEnoughToLeaveNoGaps()
        {
            var brain = new PictureBrain(Pictures.Get(PictureKind.Heart));
            brain.Reset(robot);

            brain.Steer(robot, 0.02f);

            var room = Poly2D.Bounds(level.Rooms[0].outline);
            Assert.IsNotEmpty(brain.Waypoints);
            Assert.AreEqual(0, brain.Waypoints.Count % 2);

            foreach (var point in brain.Waypoints)
            {
                Assert.Greater(point.x - robot.Radius, room.xMin, "clear of the left wall");
                Assert.Less(point.x + robot.Radius, room.xMax, "clear of the right wall");
                Assert.IsTrue(brain.Canvas.yMin < point.y && point.y < brain.Canvas.yMax);
            }

            var rowHeights = brain.Waypoints.Select(p => p.y).Distinct().OrderByDescending(y => y).ToList();
            for (int i = 1; i < rowHeights.Count; i++)
                Assert.LessOrEqual(rowHeights[i - 1] - rowHeights[i], robot.CleaningWidth * PictureBrain.LaneOverlap + 1e-4f);

            var picture = brain.Picture;
            Assert.AreEqual((float)picture.Width / picture.Height, brain.Canvas.width / brain.Canvas.height, 1e-3f);
        }

        [Test]
        public void Steer_OnTheWayToTheFirstRow_KeepsSuctionOff()
        {
            var brain = new PictureBrain(Pictures.Get(PictureKind.Invader));
            brain.Reset(robot);

            brain.Steer(robot, 0.02f);

            Assert.AreEqual(0f, robot.CleaningLimit);
            Assert.IsFalse(robot.Print.HasValue, "nothing is printed until it reaches a row");
            Assert.IsFalse(brain.Finished);
        }

        [Test]
        public void Steer_WithNoLevel_StopsTheRobot()
        {
            // No renderer in the scene, so a robot woken now has no level to draw in.
            Object.DestroyImmediate(levelHost);
            levelHost = new GameObject("Empty");

            var lonely = new GameObject("Lonely").AddComponent<VacuumRobot>();
            try
            {
                var brain = new PictureBrain(Pictures.Get(PictureKind.Heart));
                brain.Reset(lonely);
                brain.Steer(lonely, 0.02f);

                Assert.IsTrue(brain.Finished);
                Assert.AreEqual(0f, lonely.SpeedScale);
            }
            finally
            {
                Object.DestroyImmediate(lonely.gameObject);
            }
        }

        [Test]
        public void Robot_PatternSwitch_ResetsSpeedAndSuction()
        {
            robot.SpeedScale = 0.2f;
            robot.CleaningLimit = 0f;

            robot.Pattern = MovementPattern.Picture;

            Assert.AreEqual(1f, robot.SpeedScale);
            Assert.AreEqual(1f, robot.CleaningLimit);
        }
    }
}
