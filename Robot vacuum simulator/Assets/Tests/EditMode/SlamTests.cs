using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using RobotVacuum.Sim.Control;
using RobotVacuum.Sim.Sensors;
using RobotVacuumSim.EnvironmentModel;
using UnityEngine;
using E = RobotVacuum.Sim.Control.EvidenceConstants;

namespace RobotVacuum.Tests
{
    public class EvidenceMapTests
    {
        const float Cell = 0.05f;

        [Test]
        public void TwoHits_ConfirmAWall_AndItsTraversabilityIsZero()
        {
            var map = new EvidenceMap(Vector2.zero, Cell);
            map.BeginScan();
            map.AddHit(2, 3, new Vector2(0.16f, 0.11f));
            Assert.IsFalse(map.IsConfirmed(2, 3), "one hit might be noise");
            Assert.Greater(EvidenceMap.Traversability(map.Evidence(2, 3), 1f), 0f);

            map.BeginScan();
            map.AddHit(2, 3, new Vector2(0.17f, 0.12f));
            Assert.IsTrue(map.IsConfirmed(2, 3));
            Assert.AreEqual(0f, EvidenceMap.Traversability(map.Evidence(2, 3), 1f));
        }

        [Test]
        public void AConfirmedWall_IsNeverErasedFromFarAway_ButClearsFastUpClose()
        {
            var map = new EvidenceMap(Vector2.zero, Cell);
            for (int i = 0; i < 3; i++)
            {
                map.BeginScan();
                map.AddHit(0, 0, new Vector2(0.02f, 0.02f));
            }

            for (int i = 0; i < 20; i++)
            {
                map.BeginScan();
                Assert.IsFalse(map.Clear(0, 0, 3f), "a far clear ray can't lower a confirmed wall");
            }
            Assert.IsTrue(map.IsConfirmed(0, 0));

            int scans = 0;
            while (map.IsConfirmed(0, 0) && scans < 10)
            {
                map.BeginScan();
                map.Clear(0, 0, 0.5f);
                scans++;
            }
            Assert.LessOrEqual(scans, 3, "within 1 m a moved cat vanishes in a few scans");
        }

        [Test]
        public void AnUnconfirmedCell_IsLoweredByFarClears()
        {
            var map = new EvidenceMap(Vector2.zero, Cell);
            map.BeginScan();
            map.AddHit(0, 0, Vector2.zero);
            map.BeginScan();
            Assert.IsTrue(map.Clear(0, 0, 4f));
            Assert.AreEqual(E.HitStep - E.ClearFar, map.Evidence(0, 0), 1e-5f);
        }

        [Test]
        public void EachScan_ClearsACellOnce_AndNeverACellItAlsoHit()
        {
            var map = new EvidenceMap(Vector2.zero, Cell);
            map.BeginScan();
            Assert.IsTrue(map.Clear(1, 1, 0.5f));
            Assert.IsFalse(map.Clear(1, 1, 0.5f), "a second ray in the same scan doesn't stack");
            Assert.AreEqual(-E.ClearNear, map.Evidence(1, 1), 1e-5f);

            map.BeginScan();
            map.MarkHit(4, 4);
            Assert.IsFalse(map.Clear(4, 4, 0.5f), "this scan hit it");
        }

        [Test]
        public void HitPoint_IsWhereTheHitsLanded_NotTheCellCentre()
        {
            var map = new EvidenceMap(Vector2.zero, Cell);
            map.BeginScan();
            map.AddHit(0, 0, new Vector2(0.001f, 0.004f));
            map.BeginScan();
            map.AddHit(0, 0, new Vector2(0.003f, 0.006f));

            Assert.IsTrue(map.TryGetHitPoint(0, 0, out var point));
            TestLevels.AssertNear(new Vector2(0.002f, 0.005f), point, 1e-5f);
        }

        [Test]
        public void ErasingAWall_ForgetsWhereItsHitsLanded()
        {
            var map = new EvidenceMap(Vector2.zero, Cell);
            for (int i = 0; i < 2; i++)
            {
                map.BeginScan();
                map.AddHit(0, 0, new Vector2(0.001f, 0.001f));
            }
            for (int i = 0; i < 3; i++)
            {
                map.BeginScan();
                map.Clear(0, 0, 0.2f);
            }
            Assert.AreEqual(0, map.HitCount(0, 0));
            Assert.IsFalse(map.TryGetHitPoint(0, 0, out _));
        }
    }

    public class PoseMatcherTests
    {
        const float Cell = 0.05f;

        [Test]
        public void AWrongGuess_IsPulledBackToTheTruth_InARoom()
        {
            var world = SlamWorld.Apartment();
            var map = SlamWorld.MapFrom(world, new[] { new RobotPose(new Vector2(-2f, -1f), 0f), new RobotPose(new Vector2(-1f, 0.5f), 90f) });
            var truth = new RobotPose(new Vector2(-1.5f, -0.5f), 20f);
            var guess = new RobotPose(truth.position + new Vector2(0.015f, -0.012f), truth.heading + 0.6f);

            var matcher = new PoseMatcher();
            var scan = world.Scan(truth, null);
            var profile = PoseCorrectionProfile.For(PoseCorrectionQuality.Balanced);

            // The estimate between grid steps damps each correction (it recovers roughly 40% of the error per attempt
            // here; the Python prototype gives the same 1.92 -> 1.22 cm), so repeated corrections converge without
            // overshooting. Check both: the first attempt clearly shrinks the error, and five in a row converge.
            var outcome = matcher.Match(map, scan, guess, profile, 0.02f, 1f, out var corrected);
            Assert.That(outcome, Is.EqualTo(MatchOutcome.Applied).Or.EqualTo(MatchOutcome.Partial));
            float before = Vector2.Distance(guess.position, truth.position);
            float first = Vector2.Distance(corrected.position, truth.position);
            Assert.LessOrEqual(first, before * 0.75f, $"first attempt: corrected {corrected.position}, {first * 100f:F2} cm off");

            for (int i = 1; i < 5; i++) matcher.Match(map, scan, corrected, profile, 0.02f, 1f, out corrected);
            float last = Vector2.Distance(corrected.position, truth.position);
            float heading = Mathf.Abs(Mathf.DeltaAngle(corrected.heading, truth.heading));
            Assert.LessOrEqual(last, 0.004f, $"after 5 attempts: {last * 100f:F2} cm off (prototype: 0.18 cm)");
            Assert.LessOrEqual(heading, 0.15f, $"after 5 attempts: {heading:F2}° off (prototype: 0.04°)");
        }

        [Test]
        public void ABigSlip_UsesTheWideSearch()
        {
            var world = SlamWorld.Apartment();
            var map = SlamWorld.MapFrom(world, new[] { new RobotPose(new Vector2(-2f, -1f), 0f), new RobotPose(new Vector2(-1f, 0.5f), 90f) });
            var truth = new RobotPose(new Vector2(-1.5f, -0.5f), 20f);
            var guess = new RobotPose(truth.position + new Vector2(0.07f, 0.05f), truth.heading + 2.5f);
            var matcher = new PoseMatcher();

            matcher.Match(map, world.Scan(truth, null), guess, PoseCorrectionProfile.For(PoseCorrectionQuality.Balanced),
                0.2f, 5f, out var corrected);

            Assert.IsTrue(matcher.UsedWideSearch);
            Assert.LessOrEqual(Vector2.Distance(corrected.position, truth.position), 0.02f, $"corrected {corrected.position}");
        }

        [Test]
        public void InACorridor_OnlyTheAcrossDirectionIsCorrected()
        {
            var world = SlamWorld.Corridor();
            var truth = new RobotPose(Vector2.zero, 0f);
            var map = SlamWorld.MapFrom(world, new[] { truth, new RobotPose(new Vector2(1f, 0f), 0f), new RobotPose(new Vector2(-1f, 0f), 0f) });
            var guess = new RobotPose(new Vector2(0.015f, 0.012f), 0f); // off along (x) and across (y)

            var outcome = new PoseMatcher().Match(map, world.Scan(truth, null), guess,
                PoseCorrectionProfile.For(PoseCorrectionQuality.Balanced), 0.05f, 1f, out var corrected);

            // Along the corridor every shift scores about the same, so the score-weighted estimate stays at the guess;
            // across it the score is sharp, so the estimate moves to the truth.
            Assert.That(outcome, Is.EqualTo(MatchOutcome.Applied).Or.EqualTo(MatchOutcome.Partial));
            Assert.AreEqual(guess.position.x, corrected.position.x, 0.005f, "along the corridor: left about where odometry says");
            Assert.AreEqual(0f, corrected.position.y, 0.007f, "across the corridor: corrected");
        }

        [Test]
        public void TheSameInput_GivesTheSameCorrection()
        {
            var world = SlamWorld.Apartment();
            var map = SlamWorld.MapFrom(world, new[] { new RobotPose(new Vector2(-2f, -1f), 0f) });
            var truth = new RobotPose(new Vector2(-1.8f, -0.8f), 10f);
            var guess = new RobotPose(truth.position + new Vector2(0.01f, 0.01f), 10.5f);
            var scan = world.Scan(truth, null);
            var profile = PoseCorrectionProfile.For(PoseCorrectionQuality.Balanced);

            new PoseMatcher().Match(map, scan, guess, profile, 0.02f, 1f, out var a);
            new PoseMatcher().Match(map, scan, guess, profile, 0.02f, 1f, out var b);
            Assert.AreEqual(a.position, b.position);
            Assert.AreEqual(a.heading, b.heading);
        }

        [Test]
        public void AnEmptyMap_IsAWeakMatch_AndNothingInRange_IsTooFewHits()
        {
            var profile = PoseCorrectionProfile.For(PoseCorrectionQuality.Balanced);
            var pose = new RobotPose(Vector2.zero, 0f);

            Assert.AreEqual(MatchOutcome.WeakMatch,
                new PoseMatcher().Match(new EvidenceMap(Vector2.zero, Cell), SlamWorld.Apartment().Scan(new RobotPose(new Vector2(-2f, -1f), 0f), null),
                    pose, profile, 0.02f, 1f, out _));
            Assert.AreEqual(MatchOutcome.TooFewHits,
                new PoseMatcher().Match(new EvidenceMap(Vector2.zero, Cell), new SlamWorld(new Rect[0]).Scan(pose, null), pose, profile, 0.02f, 1f, out _));
        }

        [Test]
        public void Presets_BalancedIsTheDefault()
        {
            Assert.AreEqual(PoseCorrectionQuality.Balanced, default(PoseCorrectionQuality));
            Assert.AreEqual(PoseCorrectionQuality.Balanced, new RobotVacuum.Sim.RunSettings().slamCorrectionQuality);
            Assert.AreEqual(5f, PoseCorrectionProfile.For(PoseCorrectionQuality.Balanced).rateHz);
            Assert.AreEqual(2.5f, PoseCorrectionProfile.For(PoseCorrectionQuality.Performance).rateHz);
            Assert.AreEqual(10f, PoseCorrectionProfile.For(PoseCorrectionQuality.Reliable).rateHz);
        }
    }

    /// <summary>
    /// The whole SLAM pose pipeline, end to end, on the same two-room apartment as the Python prototype: noisy
    /// odometry (the real OdometrySensor), a noisy 360-ray lidar, the evidence map (the real ObservationMapper) and
    /// pose correction (the real SlamFrontEnd). Only the robot's motion and the lidar's ray cast are stand-ins for
    /// Unity physics.
    ///
    /// Part 1 (front end, no loop closure) thresholds, from the prototype's front-end runs (worst 6.4–10.9 cm,
    /// 1.3–2.9°, end 3.0–4.7 cm over 150 s) plus margin. Part 2 (loop closure) tightens these to the target.
    /// </summary>
    public class SlamPipelineTests
    {
        [TestCase(1)]
        [TestCase(2)]
        public void FrontEnd_KeepsDriftBounded(int seed)
        {
            var run = new SlamHarness(seed).Run(120f);

            TestContext.WriteLine($"seed {seed}: worst {run.MaxPositionError * 100f:F1} cm, {run.MaxHeadingError:F2}°; " +
                                  $"end {run.EndPositionError * 100f:F1} cm; p99 {run.P99PositionError * 100f:F1} cm; " +
                                  $"odometry alone would be {run.OdometryOnlyEndError * 100f:F1} cm");

            Assert.LessOrEqual(run.MaxPositionError, 0.15f);
            Assert.LessOrEqual(run.MaxHeadingError, 4f);
            Assert.LessOrEqual(run.EndPositionError, 0.08f);
        }

        [Test]
        public void TheSameSeed_GivesTheSameRun()
        {
            var a = new SlamHarness(7).Run(20f);
            var b = new SlamHarness(7).Run(20f);
            Assert.AreEqual(a.EndBelieved.position, b.EndBelieved.position);
            Assert.AreEqual(a.EndBelieved.heading, b.EndBelieved.heading);
        }
    }

    /// <summary>A world of axis-aligned solid rectangles, with an exact lidar ray cast.</summary>
    sealed class SlamWorld
    {
        public readonly Rect[] solids;

        public SlamWorld(Rect[] solids) => this.solids = solids;

        /// <summary>The prototype's apartment: 6 × 4 m, an interior wall with a doorway above it, a sofa and a box.</summary>
        public static SlamWorld Apartment() => new SlamWorld(new[]
        {
            Rect.MinMaxRect(-3.1f, 2.0f, 3.1f, 2.1f), Rect.MinMaxRect(-3.1f, -2.1f, 3.1f, -2.0f),
            Rect.MinMaxRect(3.0f, -2.1f, 3.1f, 2.1f), Rect.MinMaxRect(-3.1f, -2.1f, -3.0f, 2.1f),
            Rect.MinMaxRect(0.45f, -2.0f, 0.55f, 0.6f),
            Rect.MinMaxRect(-1.9f, 0.75f, -1.3f, 1.25f),
            Rect.MinMaxRect(1.8f, -1.4f, 2.2f, -1.0f),
        });

        /// <summary>Two long parallel walls: sliding along them changes nothing a lidar can see.</summary>
        public static SlamWorld Corridor() => new SlamWorld(new[]
        {
            Rect.MinMaxRect(-30f, 0.5f, 30f, 0.6f), Rect.MinMaxRect(-30f, -0.6f, 30f, -0.5f),
        });

        public bool IsFree(Vector2 p, float margin)
        {
            foreach (var s in solids)
                if (p.x > s.xMin - margin && p.x < s.xMax + margin && p.y > s.yMin - margin && p.y < s.yMax + margin) return false;
            return true;
        }

        /// <summary>Distance along a ray to the nearest solid (slab test), or float.MaxValue.</summary>
        public float Cast(Vector2 origin, Vector2 direction)
        {
            float best = float.MaxValue;
            foreach (var s in solids)
            {
                float tMin = 0f, tMax = float.MaxValue;
                if (!Slab(origin.x, direction.x, s.xMin, s.xMax, ref tMin, ref tMax)) continue;
                if (!Slab(origin.y, direction.y, s.yMin, s.yMax, ref tMin, ref tMax)) continue;
                if (tMin < best) best = tMin;
            }
            return best;
        }

        static bool Slab(float o, float d, float min, float max, ref float tMin, ref float tMax)
        {
            if (Mathf.Abs(d) < 1e-9f) return o >= min && o <= max;
            float t1 = (min - o) / d, t2 = (max - o) / d;
            if (t1 > t2) (t1, t2) = (t2, t1);
            tMin = Mathf.Max(tMin, t1);
            tMax = Mathf.Min(tMax, t2);
            return tMin <= tMax;
        }

        /// <summary>A 360-ray scan from <paramref name="pose"/>; noisy like LidarSensor at noise 1 when <paramref name="random"/> is given, exact when null.</summary>
        public SensorFrame Scan(RobotPose pose, RandomProvider random, float range = 8f)
        {
            var frame = new SensorFrame();
            frame.Clear();
            frame.hasLidarScan = true;
            frame.lidarRange = range;

            for (int i = 0; i < 360; i++)
            {
                float bearing = i <= 180 ? i : i - 360;
                float beam = random != null ? bearing + Gauss(random, SensorNoiseProfile.LidarBearingSigmaDegrees) : bearing;
                float trueRange = Mathf.Min(range, Cast(pose.position, RobotPose.DirectionOf(pose.heading + beam)));
                bool hit = trueRange < range;
                float measured = trueRange;

                if (random != null && hit)
                {
                    float miss = Mathf.Min(SensorNoiseProfile.LidarMissChanceCap,
                        SensorNoiseProfile.LidarMissChanceAtOneMeter + SensorNoiseProfile.LidarMissChancePerMeter * Mathf.Max(0f, trueRange - 1f));
                    if (random.NextDouble(RandomProvider.Subsystem.SensorNoise) < miss)
                    {
                        hit = false;
                        measured = range;
                    }
                    else
                    {
                        measured = Mathf.Clamp(trueRange + Gauss(random, SensorNoiseProfile.LidarRangeSigmaMeters + SensorNoiseProfile.LidarRangeSigmaFraction * trueRange), 0f, range);
                    }
                }
                frame.lidarRays.Add(new LidarRay { bearing = bearing, range = measured, hit = hit });
            }
            return frame;
        }

        static float Gauss(RandomProvider random, float sigma) =>
            (float)random.NextGaussian(RandomProvider.Subsystem.SensorNoise, 0.0, sigma);

        /// <summary>An evidence map built from exact scans at the given poses (two scans each, so walls are confirmed).</summary>
        public static EvidenceMap MapFrom(SlamWorld world, IEnumerable<RobotPose> poses)
        {
            var writer = new InternalModelWriter(new InternalModelGrid(Vector2.zero, 0.05f));
            var mapper = new ObservationMapper(writer, slam: true);
            foreach (var pose in poses)
            {
                var scan = world.Scan(pose, null);
                mapper.Apply(scan, pose);
                mapper.Apply(scan, pose);
            }
            return mapper.Evidence;
        }
    }

    /// <summary>Drives a robot around <see cref="SlamWorld.Apartment"/> and runs the real SLAM pose pipeline on it.</summary>
    sealed class SlamHarness
    {
        const float Dt = 0.02f;
        const float DriveSpeed = 0.65f;
        const float TurnSpeed = 320f;

        readonly SlamWorld world = SlamWorld.Apartment();
        readonly RandomProvider random;
        readonly System.Random goals;
        readonly OdometrySensor odometry = new OdometrySensor();
        readonly SlamFrontEnd frontEnd;
        readonly ObservationMapper mapper;

        RobotPose truth, believed, deadReckoned;
        readonly List<float> errors = new List<float>();

        public float MaxPositionError, MaxHeadingError, EndPositionError, P99PositionError, OdometryOnlyEndError;
        public RobotPose EndBelieved;

        public SlamHarness(int seed)
        {
            random = new RandomProvider((uint)seed);
            goals = new System.Random(seed);
            truth = believed = deadReckoned = new RobotPose(new Vector2(-2f, -1f), 0f);

            var writer = new InternalModelWriter(new InternalModelGrid(truth.position, 0.05f));
            frontEnd = new SlamFrontEnd(truth.position, 0.05f, PoseCorrectionQuality.Balanced, 1f);
            mapper = new ObservationMapper(writer, slam: true, frontEnd.Map);
            frontEnd.Begin(believed);
            odometry.Reset(truth.position, truth.heading);
        }

        public SlamHarness Run(float seconds)
        {
            float t = 0f;
            int step = 0;
            float spinLeft = PoseCorrectionConstants.StartSpinDegrees;
            Vector2? goal = null;

            while (t < seconds)
            {
                t += Dt;

                // ---- the true motion ----
                if (spinLeft > 0f)
                {
                    float turn = PoseCorrectionConstants.StartSpinDegreesPerSecond * Dt;
                    truth = new RobotPose(truth.position, truth.heading + turn);
                    spinLeft -= turn;
                }
                else
                {
                    if (!goal.HasValue || Vector2.Distance(goal.Value, truth.position) < 0.05f) goal = PickGoal();
                    float error = truth.BearingTo(goal.Value);
                    if (Mathf.Abs(error) > 1f)
                    {
                        truth = new RobotPose(truth.position, truth.heading + Mathf.Clamp(error, -TurnSpeed * Dt, TurnSpeed * Dt));
                    }
                    else
                    {
                        float d = Mathf.Min(DriveSpeed * Dt, Vector2.Distance(goal.Value, truth.position));
                        truth = new RobotPose(truth.position + truth.Forward * d, truth.heading);
                    }
                }

                // ---- the controller's pose pipeline ----
                odometry.Measure(truth.position, truth.heading, 1f, slam: true, 1f, random, out float forward, out float turned);
                believed = Advance(believed, forward, turned);
                deadReckoned = Advance(deadReckoned, forward, turned);
                frontEnd.AddOdometry(forward, turned);

                if (step % 5 == 0)
                {
                    var frame = world.Scan(truth, random);
                    believed = frontEnd.Correct(frame, believed, t, Dt);
                    mapper.Apply(frame, believed);
                }
                step++;

                float e = Vector2.Distance(believed.position, truth.position);
                errors.Add(e);
                MaxPositionError = Mathf.Max(MaxPositionError, e);
                MaxHeadingError = Mathf.Max(MaxHeadingError, Mathf.Abs(Mathf.DeltaAngle(believed.heading, truth.heading)));
            }

            EndPositionError = Vector2.Distance(believed.position, truth.position);
            OdometryOnlyEndError = Vector2.Distance(deadReckoned.position, truth.position);
            EndBelieved = believed;
            var sorted = errors.OrderBy(x => x).ToList();
            P99PositionError = sorted[Mathf.Clamp((int)(sorted.Count * 0.99f), 0, sorted.Count - 1)];
            return this;
        }

        static RobotPose Advance(RobotPose p, float forward, float turn) =>
            new RobotPose(p.position + RobotPose.DirectionOf(p.heading + turn * 0.5f) * forward, Mathf.DeltaAngle(0f, p.heading + turn));

        Vector2 PickGoal()
        {
            for (int tries = 0; tries < 1000; tries++)
            {
                var g = new Vector2((float)(goals.NextDouble() * 5.6 - 2.8), (float)(goals.NextDouble() * 3.6 - 1.8));
                if (world.IsFree(g, 0.3f) && LineFree(truth.position, g)) return g;
            }
            return truth.position;
        }

        bool LineFree(Vector2 a, Vector2 b)
        {
            int n = Mathf.CeilToInt(Vector2.Distance(a, b) / 0.025f) + 1;
            for (int i = 0; i <= n; i++)
                if (!world.IsFree(Vector2.Lerp(a, b, (float)i / n), 0.2f)) return false;
            return true;
        }
    }
}
