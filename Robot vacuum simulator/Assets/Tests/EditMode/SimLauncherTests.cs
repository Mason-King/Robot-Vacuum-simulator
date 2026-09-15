using NUnit.Framework;
using RobotVacuum.Level;
using RobotVacuum.LevelEditor;
using UnityEngine;

namespace RobotVacuum.Tests
{
    public class LevelHandoffTests
    {
        LevelData level;

        [SetUp]
        public void SetUp()
        {
            LevelHandoff.Clear();
            level = TestLevels.NewLevel(SampleLevels.PopulateBlank);
        }

        [TearDown]
        public void TearDown()
        {
            LevelHandoff.Clear();
            Object.DestroyImmediate(level);
        }

        [Test]
        public void TryTake_WithNothingSent_ReturnsFalse()
        {
            Assert.IsFalse(LevelHandoff.HasPending);
            Assert.IsFalse(LevelHandoff.TryTake(out _));
            Assert.IsNull(LevelHandoff.Delivered);
        }

        [Test]
        public void SentLevel_IsTakenOnce_AndRememberedAsDelivered()
        {
            LevelHandoff.Send(level);

            Assert.IsTrue(LevelHandoff.HasPending);
            Assert.IsTrue(LevelHandoff.TryTake(out var taken));
            Assert.AreSame(level, taken);
            Assert.AreSame(level, LevelHandoff.Delivered);

            Assert.IsFalse(LevelHandoff.HasPending);
            Assert.IsFalse(LevelHandoff.TryTake(out _), "a second renderer must not take it again");
        }

        [Test]
        public void CancelPending_HandsTheLevelBackWithoutDeliveringIt()
        {
            LevelHandoff.Send(level);

            Assert.AreSame(level, LevelHandoff.CancelPending());
            Assert.IsFalse(LevelHandoff.HasPending);
            Assert.IsNull(LevelHandoff.Delivered);
        }

        [Test]
        public void DestroyedLevel_DoesNotCountAsPending()
        {
            var doomed = TestLevels.NewLevel();
            LevelHandoff.Send(doomed);
            Object.DestroyImmediate(doomed);

            Assert.IsFalse(LevelHandoff.HasPending);
            Assert.IsFalse(LevelHandoff.TryTake(out _));
        }

        [Test]
        public void Renderer_OutsidePlayMode_LeavesTheHandoffAlone()
        {
            LevelHandoff.Send(level);
            var host = new GameObject("Renderer");
            try
            {
                host.AddComponent<LevelRenderer>();

                Assert.IsTrue(LevelHandoff.HasPending);
            }
            finally
            {
                Object.DestroyImmediate(host);
            }
        }
    }

    public class SimLauncherTests
    {
        [Test]
        public void BuildLevel_CopiesTheFloorPlan_AndUsesThePalette()
        {
            var snapshot = TestLevels.Snapshot("Flat", SampleLevels.PopulateApartment);
            var palette = ScriptableObject.CreateInstance<FloorPalette>();
            var level = SimLauncher.BuildLevel(snapshot, palette);
            try
            {
                Assert.AreEqual("Flat", level.name);
                Assert.AreSame(palette, level.Palette);
                Assert.AreEqual(4, level.Rooms.Count);
                Assert.AreEqual(3, level.Doorways.Count);
                Assert.AreEqual(snapshot.robotSpawn, level.RobotSpawn);
                Assert.IsTrue((level.hideFlags & HideFlags.DontUnloadUnusedAsset) != 0, "must survive the scene load");

                snapshot.rooms[0].outline[0] = new Vector2(99f, 99f);
                Assert.AreNotEqual(new Vector2(99f, 99f), level.Rooms[0].outline[0], "must not share the snapshot's lists");
            }
            finally
            {
                Object.DestroyImmediate(level);
                Object.DestroyImmediate(palette);
            }
        }

        [TestCase(4f, 3f)]
        [TestCase(20f, 2f)]
        [TestCase(2f, 20f)]
        public void FrameCamera_ShowsTheWholeLevel(float width, float height)
        {
            var host = new GameObject("Camera");
            try
            {
                var camera = host.AddComponent<Camera>();
                camera.aspect = 16f / 9f;
                var bounds = new Rect(3f - width * 0.5f, -1f - height * 0.5f, width, height);

                SimLauncher.FrameCamera(camera, bounds);

                Assert.IsTrue(camera.orthographic);
                Vector2 centre = camera.transform.position;
                float halfHeight = camera.orthographicSize;
                float halfWidth = halfHeight * camera.aspect;

                Assert.LessOrEqual(bounds.yMax, centre.y + halfHeight);
                Assert.GreaterOrEqual(bounds.yMin, centre.y - halfHeight);
                Assert.LessOrEqual(bounds.xMax, centre.x + halfWidth);
                Assert.GreaterOrEqual(bounds.xMin, centre.x - halfWidth);
                Assert.Less(camera.transform.position.z, 0f, "the camera must sit in front of the level");
            }
            finally
            {
                Object.DestroyImmediate(host);
            }
        }
    }
}
