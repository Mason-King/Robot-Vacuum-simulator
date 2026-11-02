using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using RobotVacuum.Sim;
using RobotVacuum.Sim.Sensors;

namespace RobotVacuum.Tests
{
    public class LidarRayLayoutTests
    {
        static float[] Layout(int count, float halfAngle)
        {
            float[] bearings = null;
            int n = LidarRayLayout.Compute(count, halfAngle, ref bearings);
            return n == 0 ? new float[0] : bearings.Take(n).ToArray();
        }

        [Test]
        public void FullView_UsesExactCount_ForwardFirst_EvenlySpaced_NoDuplicates()
        {
            foreach (int count in new[] { 8, 9, 90, 359, 360 })
            {
                var rays = Layout(count, 180f);

                Assert.AreEqual(count, rays.Length, $"count {count}");
                Assert.AreEqual(0f, rays[0], 1e-6f, "the first ray points straight ahead");
                Assert.AreEqual(count, rays.Select(b => System.Math.Round(b, 3)).Distinct().Count(), $"no duplicate bearings at count {count}");
                Assert.IsTrue(rays.All(b => b > -180f && b <= 180f), "bearings stay in (-180, 180]");

                var sorted = rays.OrderBy(b => b).ToList();
                float step = 360f / count;
                for (int i = 1; i < sorted.Count; i++)
                    Assert.AreEqual(step, sorted[i] - sorted[i - 1], 1e-3f, $"even spacing at count {count}");
            }
        }

        [Test]
        public void FullView_EvenCountHasOneRearRay_OddCountHasNone()
        {
            Assert.AreEqual(1, Layout(8, 180f).Count(b => System.Math.Abs(b - 180f) < 1e-4f));
            Assert.AreEqual(0, Layout(9, 180f).Count(b => System.Math.Abs(System.Math.Abs(b) - 180f) < 1e-4f));
        }

        [Test]
        public void PartialView_OddCount_IsSymmetricAndIncludesBothEdges()
        {
            var rays = Layout(9, 60f);

            Assert.AreEqual(9, rays.Length);
            Assert.AreEqual(0f, rays[0], 1e-6f);
            Assert.IsTrue(rays.Any(b => System.Math.Abs(b - 60f) < 1e-4f), "left edge included");
            Assert.IsTrue(rays.Any(b => System.Math.Abs(b + 60f) < 1e-4f), "right edge included");
            Assert.AreEqual(4, rays.Count(b => b > 0f));
            Assert.AreEqual(4, rays.Count(b => b < 0f));
        }

        [Test]
        public void PartialView_EvenCount_UsesExactCount_ExtraRayOnTheLeft()
        {
            var rays = Layout(10, 60f);

            Assert.AreEqual(10, rays.Length, "the count is used exactly, never reduced");
            Assert.AreEqual(0f, rays[0], 1e-6f);
            Assert.AreEqual(5, rays.Count(b => b > 0f), "left (counter-clockwise) gets the extra ray");
            Assert.AreEqual(4, rays.Count(b => b < 0f));
            Assert.IsTrue(rays.Any(b => System.Math.Abs(b - 60f) < 1e-4f));
            Assert.IsTrue(rays.Any(b => System.Math.Abs(b + 60f) < 1e-4f));
        }

        [Test]
        public void HalfAngleZero_GivesNoRays()
        {
            Assert.AreEqual(0, Layout(360, 0f).Length);
        }

        [Test]
        public void CountAndHalfAngle_AreClamped()
        {
            Assert.AreEqual(8, Layout(3, 180f).Length);
            Assert.AreEqual(360, Layout(1000, 180f).Length);
            Assert.AreEqual(0, Layout(360, -30f).Length, "negative half-angles clamp to 0");
            Assert.AreEqual(360, Layout(360, 500f).Length, "half-angles above 180 clamp to a full circle");
        }

        [Test]
        public void OverlapWarning_OnlyWhenRaysAreCloserThanACellAtRange()
        {
            Assert.IsNull(LidarRayLayout.OverlapWarning(360, 180f, 0.0508f, 8f), "1° spacing is wider than a 2-inch cell at 8 m");
            Assert.IsNotNull(LidarRayLayout.OverlapWarning(360, 10f, 0.0508f, 8f), "360 rays over 20° overlap");
        }

        [Test]
        public void RunSettings_NormalRun_NeverChangesTheNoiseScale()
        {
            var resolved = new RunSettings { randomSeed = false, slam = false, noiseScale = 999f }.Resolved();
            Assert.AreEqual(999f, resolved.noiseScale, "SLAM off: the noise scale is neither read nor changed");

            var slam = new RunSettings { randomSeed = false, slam = true, noiseScale = 999f }.Resolved();
            Assert.AreEqual(RunSettings.MaxNoiseScale, slam.noiseScale, "SLAM on: clamped to the maximum");
        }
    }
}
