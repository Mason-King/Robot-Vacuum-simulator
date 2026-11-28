using NUnit.Framework;
using RobotVacuum.Sim.Control;
using UnityEngine;

namespace RobotVacuum.Tests
{
    public class DriftStatsTests
    {
        /// <summary>A track whose believed copy is the true one shrunk 1%, turned 0.5° and shifted: all of it is map drift.</summary>
        [Test]
        public void Aligned_SeparatesMapDrift_FromLocalAccuracy()
        {
            var drift = new DriftStats();
            const float scale = 0.99f, turn = 0.5f;
            float r = turn * Mathf.Deg2Rad, c = Mathf.Cos(r) * scale, s = Mathf.Sin(r) * scale;
            var shift = new Vector2(0.03f, -0.02f);
            for (int i = 0; i < 4000; i++)
            {
                float t = i * 0.01f;
                var truePosition = new Vector2(3f * Mathf.Cos(t), 2f * Mathf.Sin(1.3f * t));
                var believed = new Vector2(c * truePosition.x - s * truePosition.y, s * truePosition.x + c * truePosition.y) + shift;
                float heading = t * 40f;
                drift.Add(new RobotPose(believed, heading + turn), new RobotPose(truePosition, heading));
            }

            var aligned = drift.Aligned();
            Assert.AreEqual(-1f, aligned.scalePercent, 0.01f);
            Assert.AreEqual(turn, aligned.rotationDegrees, 0.01f);
            Assert.Less(aligned.maxMeters, 0.001f, "nothing local is left");
            Assert.Less(aligned.maxDegrees, 0.01f, "the heading error is all map rotation");
            Assert.Greater(drift.MaxMeters, 0.02f, "the raw drift still shows it");
        }

        [Test]
        public void AddingPoses_GivesTheSameRawDrift_AsAddingDistances()
        {
            var a = new DriftStats();
            var b = new DriftStats();
            for (int i = 0; i < 500; i++)
            {
                var truth = new RobotPose(new Vector2(i * 0.01f, Mathf.Sin(i * 0.05f)), i * 0.7f);
                var believed = new RobotPose(truth.position + new Vector2(0.01f * Mathf.Cos(i), 0.02f * Mathf.Sin(i)), truth.heading + Mathf.Sin(i * 0.3f));
                a.Add(believed, truth);
                b.Add(Vector2.Distance(believed.position, truth.position), Mathf.Abs(Mathf.DeltaAngle(truth.heading, believed.heading)));
            }
            Assert.AreEqual(b.MaxMeters, a.MaxMeters);
            Assert.AreEqual(b.RmsMeters, a.RmsMeters);
            Assert.AreEqual(b.PositionPercentile(0.99f), a.PositionPercentile(0.99f));
            Assert.AreEqual(b.HeadingPercentile(0.99f), a.HeadingPercentile(0.99f));
        }

        [Test]
        public void Aligned_IsZero_BeforeTheRobotMoves()
        {
            var drift = new DriftStats();
            for (int i = 0; i < 100; i++) drift.Add(new RobotPose(Vector2.one, 0f), new RobotPose(Vector2.one, 0f));
            var aligned = drift.Aligned();
            Assert.AreEqual(0f, aligned.scalePercent);
            Assert.AreEqual(0f, aligned.maxMeters);
        }
    }
}
