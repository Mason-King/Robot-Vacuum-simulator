using System.Collections.Generic;
using NUnit.Framework;
using RobotVacuum.Sim;
using UnityEngine;
using Object = UnityEngine.Object;

namespace RobotVacuum.Tests
{
    public class RngTests
    {
        [Test]
        public void SameSeed_GivesTheSameSequence()
        {
            var first = new Rng(12345);
            var second = new Rng(12345);

            for (int i = 0; i < 20; i++)
                Assert.AreEqual(first.Value, second.Value, 0f, $"value {i}");
        }

        [Test]
        public void DifferentSeeds_Diverge()
        {
            var first = new Rng(1);
            var second = new Rng(2);

            bool differs = false;
            for (int i = 0; i < 20 && !differs; i++)
                differs = !Mathf.Approximately(first.Value, second.Value);

            Assert.IsTrue(differs);
        }

        [Test]
        public void Reseeding_RewindsTheSequence()
        {
            var rng = new Rng(7);
            var first = new List<float>();
            for (int i = 0; i < 5; i++) first.Add(rng.Value);

            rng.Seed = 7;
            for (int i = 0; i < 5; i++) Assert.AreEqual(first[i], rng.Value, 0f, $"value {i}");
        }

        [Test]
        public void Values_StayInRange()
        {
            var rng = new Rng(99);

            for (int i = 0; i < 200; i++)
            {
                float value = rng.Value;
                Assert.GreaterOrEqual(value, 0f);
                Assert.Less(value, 1f);
            }
        }

        [Test]
        public void ZeroSeed_StillProducesVariety()
        {
            var rng = new Rng(0);
            float first = rng.Value;

            bool differs = false;
            for (int i = 0; i < 20 && !differs; i++) differs = !Mathf.Approximately(first, rng.Value);

            Assert.IsTrue(differs, "a zero seed must not stall the generator");
        }
    }

    /// <summary>
    /// A run has to come out the same whether it is watched or run headless, which means nothing the brain
    /// decides may depend on frame timing or on random state shared with the rest of the app.
    /// </summary>
    public class BrainDeterminismTests
    {
        GameObject host;
        VacuumRobot robot;

        [SetUp]
        public void SetUp()
        {
            host = new GameObject("Robot");
            robot = host.AddComponent<VacuumRobot>();
        }

        [TearDown]
        public void TearDown()
        {
            if (host != null) Object.DestroyImmediate(host);
        }

        List<float> TurnsAfterBumps(MovementPattern pattern, int seed, int count = 12)
        {
            var brain = MovementBrain.Create(pattern, PictureKind.Heart, seed);
            var turns = new List<float>();
            for (int i = 0; i < count; i++) turns.Add(brain.AfterBump(robot).turn);
            return turns;
        }

        [TestCase(MovementPattern.RandomBounce)]
        [TestCase(MovementPattern.James)]
        [TestCase(MovementPattern.Spiral)]
        public void SameSeed_TurnsTheSameWayEveryTime(MovementPattern pattern)
        {
            CollectionAssert.AreEqual(TurnsAfterBumps(pattern, 4242), TurnsAfterBumps(pattern, 4242));
        }

        [Test]
        public void DifferentSeeds_TurnDifferently()
        {
            CollectionAssert.AreNotEqual(TurnsAfterBumps(MovementPattern.RandomBounce, 1),
                TurnsAfterBumps(MovementPattern.RandomBounce, 2));
        }

        [Test]
        public void Reseeding_StartsTheSameRunAgain()
        {
            var brain = MovementBrain.Create(MovementPattern.RandomBounce, PictureKind.Heart, 77);
            var first = new List<float>();
            for (int i = 0; i < 8; i++) first.Add(brain.AfterBump(robot).turn);

            brain.Seed(77);
            for (int i = 0; i < 8; i++)
                Assert.AreEqual(first[i], brain.AfterBump(robot).turn, 0f, $"turn {i}");
        }

        [Test]
        public void Steering_DependsOnTheRobotsClock_NotWallClockTime()
        {
            var first = MovementBrain.Create(MovementPattern.RandomBounce, PictureKind.Heart, 5);
            var second = MovementBrain.Create(MovementPattern.RandomBounce, PictureKind.Heart, 5);

            // Same seed and the same (reset) robot clock: the drift has to match, whenever it is asked.
            Assert.AreEqual(first.Steer(robot, 0.02f), second.Steer(robot, 0.02f), 0f);
        }

        [Test]
        public void Steering_DiffersBetweenSeeds()
        {
            var first = MovementBrain.Create(MovementPattern.RandomBounce, PictureKind.Heart, 11);
            var second = MovementBrain.Create(MovementPattern.RandomBounce, PictureKind.Heart, 12);

            Assert.AreNotEqual(first.Steer(robot, 0.02f), second.Steer(robot, 0.02f));
        }

        [Test]
        public void Lawnmower_IgnoresTheSeed_AndStaysRepeatable()
        {
            // It alternates deterministically, so seeds must not change it at all.
            CollectionAssert.AreEqual(TurnsAfterBumps(MovementPattern.Lawnmower, 1),
                TurnsAfterBumps(MovementPattern.Lawnmower, 999));
        }
    }
}
