using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using RobotVacuum.Level;
using RobotVacuum.LevelEditor;
using RobotVacuum.Sim.Control;
using RobotVacuum.Sim.Sensors;
using RobotVacuumSim.EnvironmentModel;
using UnityEngine;
using S = RobotVacuum.Sim.Control.LocalSlamConstants;

namespace RobotVacuum.Tests
{
    public class ProbabilityGridTests
    {
        const float Cell = 0.05f;

        static List<LidarRay> Ray(float bearing, float range, bool hit) =>
            new List<LidarRay> { new LidarRay { bearing = bearing, range = range, hit = hit } };

        [Test]
        public void AFirstHit_IsTheHitProbability_AndMoreHitsRaiseIt()
        {
            var grid = new ProbabilityGrid(Vector2.zero, Cell);
            var pose = new RobotPose(new Vector2(0.025f, 0.025f), 0f); // centre of cell (0, 0), facing +y
            var changed = new List<long>();

            grid.Insert(pose, Ray(0f, 1.0f, true), 5f, changed); // lands in cell (20, 0)
            Assert.AreEqual(S.HitProbability, grid.Probability(20, 0), 1e-6f);

            grid.Insert(pose, Ray(0f, 1.0f, true), 5f, changed);
            float odds = S.HitProbability / (1f - S.HitProbability);
            Assert.AreEqual(odds * odds / (1f + odds * odds), grid.Probability(20, 0), 1e-5f);

            for (int i = 0; i < 100; i++) grid.Insert(pose, Ray(0f, 1.0f, true), 5f, changed);
            Assert.AreEqual(S.MaxProbability, grid.Probability(20, 0), 1e-6f, "held within the limits");
        }

        [Test]
        public void ARay_ClearsTheCellsBeforeItsReturn_NotTheReturnCell()
        {
            var grid = new ProbabilityGrid(Vector2.zero, Cell);
            var pose = new RobotPose(new Vector2(0.025f, 0.025f), 0f);
            grid.Insert(pose, Ray(0f, 1.0f, true), 5f, new List<long>());

            for (int row = 0; row < 20; row++)
                Assert.AreEqual(S.MissProbability, grid.Probability(row, 0), 1e-6f, $"row {row}");
            Assert.AreEqual(S.HitProbability, grid.Probability(20, 0), 1e-6f);
            Assert.IsFalse(grid.IsKnown(21, 0), "nothing beyond the return");
        }

        [Test]
        public void ANoReturnRay_ClearsOnlyToTheMissingDataLength()
        {
            var grid = new ProbabilityGrid(Vector2.zero, Cell);
            var pose = new RobotPose(new Vector2(0.025f, 0.025f), 0f);
            grid.Insert(pose, Ray(0f, 8f, false), 5f, new List<long>());

            Assert.IsTrue(grid.IsKnown(99, 0));
            Assert.IsFalse(grid.IsKnown(101, 0));
        }

        [Test]
        public void ANoReturnRay_IsNotEnteredAtAll_InSlam()
        {
            // A lidar dropout on a wall must not clear floor through that wall.
            var grid = new ProbabilityGrid(Vector2.zero, Cell);
            var pose = new RobotPose(new Vector2(0.025f, 0.025f), 0f);
            grid.Insert(pose, Ray(0f, 8f, false), S.MissingDataRayLengthMeters, new List<long>());
            Assert.IsFalse(grid.IsKnown(1, 0), "nothing along a ray with no return");
            Assert.IsFalse(grid.IsKnown(20, 0));
        }

        [Test]
        public void OneScan_UpdatesACellOnce_AndNeverMissesACellItHit()
        {
            var grid = new ProbabilityGrid(Vector2.zero, Cell);
            var pose = new RobotPose(new Vector2(0.025f, 0.025f), 0f);
            var rays = new List<LidarRay>
            {
                new LidarRay { bearing = 0f, range = 1.0f, hit = true },  // hits (20, 0)
                new LidarRay { bearing = 0f, range = 1.0f, hit = true },  // the same cell again
                new LidarRay { bearing = 0f, range = 2.0f, hit = true },  // passes through (20, 0)
            };
            grid.Insert(pose, rays, 5f, new List<long>());
            Assert.AreEqual(S.HitProbability, grid.Probability(20, 0), 1e-6f);
            Assert.AreEqual(S.MissProbability, grid.Probability(10, 0), 1e-6f, "a cell two rays crossed is missed once");
        }

        [Test]
        public void Traversability_FollowsTheProbability()
        {
            Assert.AreEqual(1f, LocalSlam.Traversability(0f, 1f), "unknown: passable");
            Assert.AreEqual(0.8f, LocalSlam.Traversability(S.MissProbability, 0.8f), 1e-6f, "free: the floor's ease");
            Assert.AreEqual(0f, LocalSlam.Traversability(S.OccupiedProbability, 1f), "a wall");
            float half = LocalSlam.Traversability((S.FreeProbability + S.OccupiedProbability) / 2f, 1f);
            Assert.AreEqual(0.5f, half, 1e-5f);
        }
    }

    public class ScanMatcherTests
    {
        const float Cell = 0.05f;

        /// <summary>A submap built the way a SLAM run starts: noisy scans from the dock while spinning.</summary>
        static ProbabilityGrid DockSubmap(SlamWorld world, RandomProvider random, Vector2 dock)
        {
            var grid = new ProbabilityGrid(dock, Cell);
            for (int k = 0; k < 30; k++)
                grid.Insert(new RobotPose(dock, 12f * k), world.Scan(new RobotPose(dock, 12f * k), random).lidarRays, 5f, null);
            return grid;
        }

        static List<Vector2> Points(SensorFrame frame, int stride)
        {
            var list = new List<Vector2>();
            int seen = 0;
            foreach (var ray in frame.lidarRays)
                if (ray.hit && seen++ % stride == 0) list.Add(RobotPose.DirectionOf(ray.bearing) * ray.range);
            return list;
        }

        [Test]
        public void TheInterpolatedGradient_MatchesFiniteDifferences()
        {
            var world = SlamWorld.Apartment();
            var dock = new Vector2(-2f, -1f);
            var grid = DockSubmap(world, new RandomProvider(3), dock);
            var matcher = new ScanMatcher();
            matcher.Match(grid, new List<Vector2> { Vector2.up }, new RobotPose(dock, 0f), 0f, 0f); // binds the grid

            foreach (var p in new[] { new Vector2(-2.93f, -1.4f), new Vector2(-1.86f, 0.71f), new Vector2(0.43f, -1.02f) })
            {
                matcher.Interpolate(p.x, p.y, out double dx, out double dy);
                const double h = 1e-5;
                double fx = (matcher.Interpolate(p.x + h, p.y, out _, out _) - matcher.Interpolate(p.x - h, p.y, out _, out _)) / (2 * h);
                double fy = (matcher.Interpolate(p.x, p.y + h, out _, out _) - matcher.Interpolate(p.x, p.y - h, out _, out _)) / (2 * h);
                Assert.AreEqual(fx, dx, 1e-3 + 1e-3 * Math.Abs(fx), $"d/dx at {p}");
                Assert.AreEqual(fy, dy, 1e-3 + 1e-3 * Math.Abs(fy), $"d/dy at {p}");
            }
        }

        [Test]
        public void ANoisyScan_IsFittedCloserToTheTruth()
        {
            // Twelve scans near the dock, each started from a guess about 2 cm and 0.7° off. A Python copy of this
            // matcher gave 0.6 cm / 0.3° on average from 2.4 cm / 0.6° (worst 1.0 cm / 0.7°).
            var world = SlamWorld.Apartment();
            var random = new RandomProvider(5);
            var dock = new Vector2(-2f, -1f);
            var grid = DockSubmap(world, random, dock);
            var matcher = new ScanMatcher();
            var rng = new System.Random(5);

            double before = 0, after = 0, headingBefore = 0, headingAfter = 0;
            const int trials = 12;
            for (int t = 0; t < trials; t++)
            {
                var truth = new RobotPose(dock + new Vector2((float)(rng.NextDouble() * 0.3 - 0.15), (float)(rng.NextDouble() * 0.3 - 0.15)),
                    (float)(rng.NextDouble() * 360 - 180));
                var guess = new RobotPose(truth.position + new Vector2((float)(rng.NextDouble() * 0.04 - 0.02), (float)(rng.NextDouble() * 0.04 - 0.02)),
                    truth.heading + (float)(rng.NextDouble() * 1.4 - 0.7));
                var fitted = matcher.Match(grid, Points(world.Scan(truth, random), 2), guess, S.TranslationWeight, S.RotationWeight);

                before += Vector2.Distance(guess.position, truth.position);
                after += Vector2.Distance(fitted.position, truth.position);
                headingBefore += Mathf.Abs(Mathf.DeltaAngle(guess.heading, truth.heading));
                headingAfter += Mathf.Abs(Mathf.DeltaAngle(fitted.heading, truth.heading));
            }

            TestContext.WriteLine($"mean error: {before / trials * 100:F2} cm, {headingBefore / trials:F2}° before; " +
                                  $"{after / trials * 100:F2} cm, {headingAfter / trials:F2}° after");
            Assert.LessOrEqual(after / trials, 0.012, "position, mean over the trials");
            Assert.LessOrEqual(headingAfter / trials, 0.5, "heading, mean over the trials");
            Assert.Less(after, before);
        }

        [Test]
        public void TheFastFit_IsTheReferenceFit_BitForBit()
        {
            // The speed pass reuses each accepted trial's gradient and reads the map's 4 × 4 cells straight from the
            // array. Against the original fit: the same pose, iterations and mean probability, every time. Guesses up to
            // 10 cm and 5° off make the fit reject steps too, and some scans reach past the submap's edge.
            var world = SlamWorld.Apartment();
            var random = new RandomProvider(21);
            var dock = new Vector2(-2f, -1f);
            var grid = DockSubmap(world, random, dock);
            var fast = new ScanMatcher();
            var reference = new ScanMatcher();
            var rng = new System.Random(21);

            for (int t = 0; t < 40; t++)
            {
                var truth = new RobotPose(dock + new Vector2((float)(rng.NextDouble() * 0.6 - 0.3), (float)(rng.NextDouble() * 0.6 - 0.3)),
                    (float)(rng.NextDouble() * 360 - 180));
                float off = t < 20 ? 0.02f : 0.10f, turn = t < 20 ? 0.7f : 5f;
                var guess = new RobotPose(truth.position + new Vector2((float)(rng.NextDouble() * 2 * off - off), (float)(rng.NextDouble() * 2 * off - off)),
                    truth.heading + (float)(rng.NextDouble() * 2 * turn - turn));
                var points = Points(world.Scan(truth, random), 1 + t % 3);
                float occupied = t % 2 == 0 ? S.OccupiedSpaceWeight : 3f;

                var a = fast.Match(grid, points, guess, occupied, S.TranslationWeight, S.RotationWeight);
                var b = reference.MatchReference(grid, points, guess, occupied, S.TranslationWeight, S.RotationWeight);
                Assert.AreEqual(b.position.x, a.position.x, 0f, $"trial {t}: x");
                Assert.AreEqual(b.position.y, a.position.y, 0f, $"trial {t}: y");
                Assert.AreEqual(b.heading, a.heading, 0f, $"trial {t}: heading");
                Assert.AreEqual(reference.LastIterations, fast.LastIterations, $"trial {t}: iterations");
                Assert.AreEqual(reference.LastMeanProbability, fast.LastMeanProbability, 0f, $"trial {t}: mean probability");
            }

            // The interpolation alone, over and past the whole submap (the edge falls back to the reference path).
            float cell = grid.CellSizeMeters;
            for (float y = grid.AnchorWorld.y + (grid.minRow - 3) * cell; y < grid.AnchorWorld.y + (grid.minRow + grid.rows + 3) * cell; y += 0.73f * cell)
                for (float x = grid.AnchorWorld.x + (grid.minCol - 3) * cell; x < grid.AnchorWorld.x + (grid.minCol + grid.cols + 3) * cell; x += 0.81f * cell)
                {
                    double p = fast.Interpolate(x, y, out double dx, out double dy);
                    double q = fast.InterpolateReference(x, y, out double rx, out double ry);
                    Assert.AreEqual(q, p, 0.0, $"value at ({x}, {y})");
                    Assert.AreEqual(rx, dx, 0.0, $"d/dx at ({x}, {y})");
                    Assert.AreEqual(ry, dy, 0.0, $"d/dy at ({x}, {y})");
                }
        }

        [Test]
        public void TheSameInput_GivesTheSameFit()
        {
            var world = SlamWorld.Apartment();
            var dock = new Vector2(-2f, -1f);
            var grid = DockSubmap(world, new RandomProvider(9), dock);
            var points = Points(world.Scan(new RobotPose(dock + new Vector2(0.05f, 0.02f), 30f), new RandomProvider(10)), 2);
            var guess = new RobotPose(dock + new Vector2(0.06f, 0.01f), 30.4f);

            var a = new ScanMatcher().Match(grid, points, guess, S.TranslationWeight, S.RotationWeight);
            var b = new ScanMatcher().Match(grid, points, guess, S.TranslationWeight, S.RotationWeight);
            Assert.AreEqual(a.position, b.position);
            Assert.AreEqual(a.heading, b.heading);
        }
    }

    public class LocalSlamTests
    {
        [Test]
        public void Submaps_TakeTwiceTheScanCount_AndOverlap()
        {
            var world = SlamWorld.Apartment();
            var dock = new Vector2(-2f, -1f);
            var slam = new LocalSlam(dock, 0.05f, PoseCorrectionQuality.Balanced, 1f);
            slam.Begin(new RobotPose(dock, 0f));

            // Turning 2° per scan: every scan passes the motion filter.
            for (int k = 0; k < 2 * S.ScansPerSubmap + 1; k++)
            {
                var truth = new RobotPose(dock, 2f * k);
                slam.Track(world.Scan(truth, null), truth, 0.1f * k);
            }

            Assert.AreEqual(2 * S.ScansPerSubmap + 1, slam.ScansInserted);
            Assert.AreEqual(3, slam.Submaps.Count, "a new submap every 90 scans");
            Assert.IsTrue(slam.Submaps[0].Finished, "the first has its 180 scans");
            Assert.AreEqual(2 * S.ScansPerSubmap, slam.Submaps[0].grid.ScanCount);
            Assert.IsFalse(slam.Submaps[1].Finished);
        }

        [Test]
        public void TheMotionFilter_SkipsScansWhileStill()
        {
            var world = SlamWorld.Apartment();
            var dock = new Vector2(-2f, -1f);
            var slam = new LocalSlam(dock, 0.05f, PoseCorrectionQuality.Balanced, 1f);
            var pose = new RobotPose(dock, 0f);
            slam.Begin(pose);

            for (int k = 0; k < 20; k++) slam.Track(world.Scan(pose, null), pose, 0.1f * k);
            Assert.AreEqual(1, slam.ScansInserted, "only the first scan, until the robot moves");

            slam.Track(world.Scan(pose, null), pose, 6f);
            Assert.AreEqual(2, slam.ScansInserted, "and one after 5 s still");
        }

        [Test]
        public void ReplayingAScanIntoTheNewerSubmap_IsExactlyInsertingIt()
        {
            // The speed pass traces each scan once and replays its cells into the second active submap. Run the same
            // noisy scans through a LocalSlam doing that and one inserting into each submap separately: every pose,
            // every changed-cell list (in order) and every grid, bit for bit, must match.
            var world = SlamWorld.Apartment();
            var centre = new Vector2(-2f, -0.5f);
            var random = new RandomProvider(11);
            var replayed = new LocalSlam(centre, 0.05f, PoseCorrectionQuality.Balanced, 1f);
            var separate = new LocalSlam(centre, 0.05f, PoseCorrectionQuality.Balanced, 1f);
            var start = new RobotPose(centre + new Vector2(0.6f, 0f), 90f);
            LocalSlam.ScansPerSubmapOverride = 30; // several submap hand-overs
            try
            {
                replayed.Begin(start);
                separate.Begin(start);
                for (int k = 0; k < 200; k++)
                {
                    float angle = 6f * k;
                    var truth = new RobotPose(centre + RobotPose.DirectionOf(angle - 90f) * 0.6f, angle);
                    var frame = world.Scan(truth, random);

                    LocalSlam.ReplayInserts = true;
                    var a = replayed.Track(frame, truth, 0.1f * k);
                    LocalSlam.ReplayInserts = false;
                    var b = separate.Track(frame, truth, 0.1f * k);

                    Assert.AreEqual(b.position.x, a.position.x, 0f, $"scan {k}: x");
                    Assert.AreEqual(b.position.y, a.position.y, 0f, $"scan {k}: y");
                    Assert.AreEqual(b.heading, a.heading, 0f, $"scan {k}: heading");
                    CollectionAssert.AreEqual(separate.ChangedCells.ToArray(), replayed.ChangedCells.ToArray(), $"scan {k}: changed cells");
                }

                Assert.AreEqual(separate.Submaps.Count, replayed.Submaps.Count);
                Assert.Greater(replayed.Submaps.Count, 4, "the run crossed several submaps");
                for (int s = 0; s < replayed.Submaps.Count; s++)
                {
                    var x = replayed.Submaps[s].grid;
                    var y = separate.Submaps[s].grid;
                    Assert.AreEqual((y.minRow, y.minCol, y.rows, y.cols, y.ScanCount), (x.minRow, x.minCol, x.rows, x.cols, x.ScanCount), $"submap {s}: extent");
                    CollectionAssert.AreEqual(y.probability, x.probability, $"submap {s}: probabilities");
                }
            }
            finally
            {
                LocalSlam.ScansPerSubmapOverride = 0;
                LocalSlam.ReplayInserts = true;
            }
        }

        [Test]
        public void Presets_BalancedIsTheDefault()
        {
            Assert.AreEqual(PoseCorrectionQuality.Balanced, default(PoseCorrectionQuality));
            Assert.AreEqual(PoseCorrectionQuality.Balanced, new RobotVacuum.Sim.RunSettings().slamCorrectionQuality);
            Assert.AreEqual(2, PoseCorrectionProfile.For(PoseCorrectionQuality.Balanced).rayStride);
            Assert.AreEqual(3, PoseCorrectionProfile.For(PoseCorrectionQuality.Performance).rayStride);
            Assert.AreEqual(1, PoseCorrectionProfile.For(PoseCorrectionQuality.Reliable).rayStride);
        }
    }

    /// <summary>
    /// Local SLAM end to end, on the same two-room apartment as before: noisy odometry (the real OdometrySensor), a
    /// noisy 360-ray lidar, local SLAM (the real LocalSlam) and the Internal Model (the real ObservationMapper). Only
    /// the robot's motion and the lidar's ray cast stand in for Unity physics. No loop closure yet (slice 2), so the
    /// limits are part 1's front-end limits; the numbers are printed for comparison.
    /// </summary>
    public class SlamPipelineTests
    {
        [TestCase(1)]
        [TestCase(2)]
        public void LocalSlam_KeepsDriftBounded(int seed)
        {
            var run = new SlamHarness(seed, loopClosure: false).Run(120f);
            Print(seed, run);

            Assert.LessOrEqual(run.Drift.MaxMeters, 0.15f);
            Assert.LessOrEqual(run.Drift.MaxDegrees, 4f);
            Assert.LessOrEqual(run.EndPositionError, 0.08f);
        }

        /// <summary>
        /// Loop closure on, 300 s. Provisional limits: no worse than local SLAM alone (slice 1's survey: worst 7.3–11.3 cm).
        /// The numbers are printed; the limits are tightened once the survey shows what loop closure achieves.
        /// </summary>
        [TestCase(1)]
        [TestCase(2)]
        public void WithLoopClosure_StaysBounded(int seed)
        {
            var run = new SlamHarness(seed).Run(300f);
            Print(seed, run);

            Assert.Greater(run.Global.LoopLinks, 0, "loop closure never closed a loop");
            Assert.LessOrEqual(run.Drift.MaxMeters, 0.12f);
            Assert.LessOrEqual(run.EndPositionError, 0.08f);
        }

        [Test]
        public void TheSameSeed_GivesTheSameRun()
        {
            var a = new SlamHarness(7).Run(60f);
            var b = new SlamHarness(7).Run(60f);
            Assert.AreEqual(a.EndBelieved.position, b.EndBelieved.position);
            Assert.AreEqual(a.EndBelieved.heading, b.EndBelieved.heading);
        }

        /// <summary>
        /// Diagnostics, not a check: eight seeds × 300 s with loop closure, one line each (plus loop closure and the
        /// aligned error). Run it on its own (select it, Run Selected).
        /// </summary>
        [Explicit, Test, Timeout(3_600_000)]
        public void Survey_EightSeeds()
        {
            for (int seed = 1; seed <= 8; seed++)
            {
                var run = new SlamHarness(seed).Run(300f);
                Print(seed, run);
                TestContext.WriteLine("    " + string.Join("; ", run.Checkpoints));
            }
        }

        /// <summary>The Enclosed Test world is the live level: spawn, wall faces seen from it, and open doorways.</summary>
        [Test]
        public void EnclosedTest_WorldMatchesTheLevel()
        {
            var world = SlamWorld.EnclosedTest();
            Vector2 spawn = world.Dock;
            Assert.AreEqual(5.2497f, spawn.x, 1e-3f);
            Assert.AreEqual(-1.6583f, spawn.y, 1e-3f);
            Assert.IsTrue(world.Inside(spawn) && world.IsFree(spawn, 0.2f), "spawn is in the corridor, clear of walls");
            // Wall faces from the spawn, worked out independently from the level file (12 cm walls centred on each edge).
            Assert.AreEqual(0.6453f, world.Cast(spawn, Vector2.right), 0.005f, "east end wall");
            Assert.AreEqual(0.3483f, world.Cast(spawn, Vector2.up), 0.005f, "corridor's inner wall");
            Assert.AreEqual(0.2629f, world.Cast(spawn, Vector2.down), 0.005f, "outer wall");
            foreach (var doorway in new[] { new Vector2(3.0506f, -1.1875f), new Vector2(-1.3716f, -1.0415f), new Vector2(-2.1062f, 0.1524f), new Vector2(2.2860f, 1.1521f) })
                Assert.IsTrue(world.IsFree(doorway, 0.2f), $"doorway at {doorway} is open");
            Assert.IsFalse(world.Inside(new Vector2(5.5f, 2.5f)), "outside every room");
        }

        /// <summary>One run scored against the agreed target (Nov 5): within 5 cm and 2° for 99% of the time, never beyond 8 cm or 3°.</summary>
        struct TargetScore
        {
            public float within, worstCm, worstDeg, p99Cm, rmsCm, rmsDeg;
            public bool Met => within >= 0.99f && worstCm <= 8f && worstDeg <= 3f;
            public override string ToString() =>
                $"{(Met ? "MEETS" : "misses")} target: {within * 100f:F1}% within 5 cm & 2°, worst {worstCm:F1} cm / {worstDeg:F2}°, p99 {p99Cm:F1} cm, rms {rmsCm:F1} cm / {rmsDeg:F2}°";
        }

        static TargetScore Score(SlamHarness harness, float seconds)
        {
            int steps = 0, within = 0;
            var before = harness.OnStep;
            harness.OnStep = (b, t, time) =>
            {
                before?.Invoke(b, t, time);
                steps++;
                if (Vector2.Distance(b.position, t.position) <= 0.05f && Mathf.Abs(Mathf.DeltaAngle(b.heading, t.heading)) <= 2f) within++;
            };
            harness.Run(seconds);
            harness.OnStep = before;
            var d = harness.Drift;
            return new TargetScore
            {
                within = steps > 0 ? within / (float)steps : 0f,
                worstCm = d.MaxMeters * 100f, worstDeg = d.MaxDegrees,
                p99Cm = d.PositionPercentile(0.99f) * 100f, rmsCm = d.RmsMeters * 100f, rmsDeg = d.RmsDegrees,
            };
        }

        /// <summary>
        /// Diagnostics, not a check: eight seeds × 300 s on the Enclosed Test level, live settings, loop closure on. Per
        /// seed: the survey line, the 60 s checkpoints, the whole-map scale and rotation fit, and the score against the
        /// target. Run it on its own.
        /// </summary>
        [Explicit, Test, Timeout(3_600_000)]
        public void Survey_Enclosed() => SurveyAgainstTarget("Enclosed Test", SlamWorld.EnclosedTest, verbose: true);

        /// <summary>
        /// Diagnostics, not a check: the target score on both levels, eight seeds each, one line per seed. This is the
        /// yardstick for every candidate fix. Run it on its own.
        /// </summary>
        [Explicit, Test, Timeout(3_600_000)]
        public void Survey_TargetBothLevels()
        {
            SurveyAgainstTarget("Apartment", SlamWorld.Apartment, verbose: false);
            SurveyAgainstTarget("Enclosed Test", SlamWorld.EnclosedTest, verbose: false);
        }

        static void SurveyAgainstTarget(string label, Func<SlamWorld> world, bool verbose, Action<SlamHarness> set = null)
        {
            const int seeds = 8;
            int met = 0;
            double within = 0, worst = 0, p99 = 0, rms = 0, scale = 0, rotation = 0, leftover = 0, msPerSimSecond = 0;
            for (int seed = 1; seed <= seeds; seed++)
            {
                var harness = new SlamHarness(seed, world());
                set?.Invoke(harness);
                var score = Score(harness, 300f);
                var fit = harness.AlignedWithScale();
                if (verbose)
                {
                    Print(seed, harness);
                    TestContext.WriteLine("    " + string.Join("; ", harness.Checkpoints));
                }
                TestContext.WriteLine($"{label} seed {seed}: {score} | map fit: scale {(fit.scale - 1) * 100:+0.00;-0.00}%, rotation {fit.rotationDegrees:+0.00;-0.00}°, " +
                                      $"left after shift/rotation/scale removed rms {fit.rms * 100:F1} cm");
                if (score.Met) met++;
                within += score.within / seeds;
                worst += score.worstCm / seeds;
                p99 += score.p99Cm / seeds;
                rms += score.rmsCm / seeds;
                scale += (fit.scale - 1) * 100 / seeds;
                rotation += fit.rotationDegrees / seeds;
                leftover += fit.rms * 100 / seeds;
                msPerSimSecond += harness.RealSeconds * 1000.0 / 300.0 / seeds;
            }
            TestContext.WriteLine($"{label}: {met}/{seeds} seeds meet the target | means: {within * 100:F1}% within, worst {worst:F1} cm, p99 {p99:F1} cm, rms {rms:F1} cm | " +
                                  $"map scale {scale:+0.00;-0.00}%, rotation {rotation:+0.00;-0.00}°, left over rms {leftover:F1} cm | {msPerSimSecond:F1} ms per sim second (whole harness)");
        }

        /// <summary>
        /// Diagnostics, not a check: where does the whole-map shrink live, when is it set, and can odometry see it early?
        /// Apartment and Enclosed Test, 8 seeds × 300 s each, live settings. Per seed: (1) WHERE: the scale of the whole
        /// run's track (global, after loop closure), of local SLAM's own track, and of local SLAM's track inside each
        /// submap's window (the matches while that submap was the newest). If the windows carry the shrink, it is drawn
        /// inside the submaps and moving submaps in the pose graph can't remove it. (2) WHEN: the global track's scale and
        /// rotation fitted on everything up to 10, 20, 30, 40, 60, 120 and 300 s. (3) ODOMETRY: the robot's own estimate of
        /// tracking's scale error, from chords of consecutive ≥ 2 m stretches of odometry path (tracked chord ÷ odometry
        /// chord − 1, summed), with odometry's honest ± (1.5 cm per √m of path), next to the true values. Measurement only.
        /// </summary>
        [Explicit, Test, Timeout(3_600_000)]
        public void Diagnose_EnclosedScale()
        {
            DiagnoseScaleOrigin("Apartment", SlamWorld.Apartment);
            DiagnoseScaleOrigin("Enclosed Test", SlamWorld.EnclosedTest);
        }

        /// <summary>Best fit believed ≈ scale · R(rotation) · true + shift. Spread = rms distance of the true points from their centroid.</summary>
        static (double scale, double rotation, double spread, int n) FitScale(IList<(Vector2 b, Vector2 t)> points)
        {
            int n = points.Count;
            if (n < 3) return (1, 0, 0, n);
            double mbx = 0, mby = 0, mtx = 0, mty = 0;
            foreach (var (b, t) in points) { mbx += b.x; mby += b.y; mtx += t.x; mty += t.y; }
            mbx /= n; mby /= n; mtx /= n; mty /= n;
            double dot = 0, cross = 0, tt = 0;
            foreach (var (b, t) in points)
            {
                double dtx = t.x - mtx, dty = t.y - mty, dbx = b.x - mbx, dby = b.y - mby;
                dot += dtx * dbx + dty * dby;
                cross += dtx * dby - dty * dbx;
                tt += dtx * dtx + dty * dty;
            }
            if (tt < 1e-12) return (1, 0, 0, n);
            return (System.Math.Sqrt(dot * dot + cross * cross) / tt, System.Math.Atan2(cross, dot) * 180 / System.Math.PI, System.Math.Sqrt(tt / n), n);
        }

        static void DiagnoseScaleOrigin(string label, Func<SlamWorld> world)
        {
            const int seeds = 8;
            const double stretchMeters = 2.0, odometrySigmaPerRootMeter = 0.015;
            float[] cutoffs = { 10f, 20f, 30f, 40f, 60f, 120f, 300f };
            int k = cutoffs.Length;
            double meanGlobal = 0, meanLocal = 0, meanWindows = 0;
            var meanSoFarScale = new double[k];
            var meanSoFarRotation = new double[k];
            var meanEstimate = new double[k];
            var meanTracked = new double[k];
            var meanOdometry = new double[k];
            var meanMiss = new double[k];
            var meanPlusMinus = new double[k];
            var detected = new int[k];

            for (int seed = 1; seed <= seeds; seed++)
            {
                var harness = new SlamHarness(seed, world());
                var matches = new List<(float t, RobotPose local, RobotPose truth, RobotPose odo, int submap, double odoPath)>();
                var globalTrack = new List<(float t, Vector2 b, Vector2 truth)>();
                double odoPath = 0;
                RobotPose? lastOdo = null;
                harness.OnMatch = (local, truth, t) =>
                {
                    var odo = harness.DeadReckoned;
                    if (lastOdo.HasValue) odoPath += Vector2.Distance(lastOdo.Value.position, odo.position);
                    lastOdo = odo;
                    matches.Add((t, local, truth, odo, harness.Slam.Submaps.Count - 1, odoPath));
                };
                int steps = 0;
                harness.OnStep = (b, truth, t) =>
                {
                    if (steps++ % 10 == 0) globalTrack.Add((t, b.position, truth.position));
                };
                harness.Run(300f);

                // (1) WHERE
                var globalFit = FitScale(globalTrack.Select(x => (x.b, x.truth)).ToList());
                var localFit = FitScale(matches.Select(x => (x.local.position, x.truth.position)).ToList());
                var windows = new List<string>();
                double weighted = 0, weights = 0;
                foreach (var group in matches.GroupBy(x => x.submap).OrderBy(g => g.Key))
                {
                    var fit = FitScale(group.Select(x => (x.local.position, x.truth.position)).ToList());
                    if (fit.n < 20) continue;
                    double w = fit.spread * fit.spread * fit.n;
                    weighted += (fit.scale - 1) * w;
                    weights += w;
                    windows.Add($"submap {group.Key}: {(fit.scale - 1) * 100:+0.00;-0.00}% / {fit.rotation:+0.00;-0.00}° " +
                                $"({group.First().t:F0}–{group.Last().t:F0} s, rms radius {fit.spread:F1} m)");
                }
                double windowScale = weights > 0 ? weighted / weights : 0;
                meanGlobal += (globalFit.scale - 1) / seeds;
                meanLocal += (localFit.scale - 1) / seeds;
                meanWindows += windowScale / seeds;
                TestContext.WriteLine($"{label} seed {seed}: WHERE whole run global {(globalFit.scale - 1) * 100:+0.00;-0.00}% / {globalFit.rotation:+0.00;-0.00}°, " +
                                      $"local SLAM frame {(localFit.scale - 1) * 100:+0.00;-0.00}% / {localFit.rotation:+0.00;-0.00}°, " +
                                      $"inside submap windows (weighted) {windowScale * 100:+0.00;-0.00}%");
                TestContext.WriteLine("    " + string.Join("; ", windows));

                // (2) WHEN and (3) ODOMETRY
                var when = new List<string>();
                var seen = new List<string>();
                for (int c = 0; c < k; c++)
                {
                    float cutoff = cutoffs[c];
                    var soFar = FitScale(globalTrack.Where(x => x.t <= cutoff).Select(x => (x.b, x.truth)).ToList());
                    meanSoFarScale[c] += (soFar.scale - 1) / seeds;
                    meanSoFarRotation[c] += soFar.rotation / seeds;
                    when.Add($"{cutoff:F0} s {(soFar.scale - 1) * 100:+0.00;-0.00}% / {soFar.rotation:+0.00;-0.00}°");

                    double tracked = 0, odometry = 0, real = 0, path = 0;
                    int i = 0;
                    while (i < matches.Count && matches[i].t <= cutoff)
                    {
                        int j = i;
                        while (j < matches.Count && matches[j].odoPath - matches[i].odoPath < stretchMeters) j++;
                        if (j >= matches.Count || matches[j].t > cutoff) break;
                        tracked += Vector2.Distance(matches[i].local.position, matches[j].local.position);
                        odometry += Vector2.Distance(matches[i].odo.position, matches[j].odo.position);
                        real += Vector2.Distance(matches[i].truth.position, matches[j].truth.position);
                        path += matches[j].odoPath - matches[i].odoPath;
                        i = j;
                    }
                    if (real <= 0 || odometry <= 0) { seen.Add($"{cutoff:F0} s: no stretch yet"); continue; }
                    double estimate = tracked / odometry - 1, trackedTrue = tracked / real - 1, odometryTrue = odometry / real - 1;
                    double plusMinus = odometrySigmaPerRootMeter * System.Math.Sqrt(path) / odometry;
                    meanEstimate[c] += estimate / seeds;
                    meanTracked[c] += trackedTrue / seeds;
                    meanOdometry[c] += odometryTrue / seeds;
                    meanMiss[c] += System.Math.Abs(estimate - trackedTrue) / seeds;
                    meanPlusMinus[c] += plusMinus / seeds;
                    if (System.Math.Abs(estimate) > 2 * plusMinus && System.Math.Sign(estimate) == System.Math.Sign(trackedTrue)) detected[c]++;
                    seen.Add($"{cutoff:F0} s: robot's estimate {estimate * 100:+0.00;-0.00}% ±{plusMinus * 100:F2} " +
                             $"(true: tracked {trackedTrue * 100:+0.00;-0.00}%, odometry {odometryTrue * 100:+0.00;-0.00}%; {path:F1} m)");
                }
                TestContext.WriteLine("    WHEN (global track so far): " + string.Join(", ", when));
                TestContext.WriteLine("    ODOMETRY: " + string.Join("; ", seen));
            }

            TestContext.WriteLine($"{label} MEANS: WHERE global {meanGlobal * 100:+0.00;-0.00}%, local SLAM frame {meanLocal * 100:+0.00;-0.00}%, " +
                                  $"inside submap windows {meanWindows * 100:+0.00;-0.00}%");
            TestContext.WriteLine($"{label} MEANS: WHEN " + string.Join(", ", Enumerable.Range(0, k).Select(c =>
                $"{cutoffs[c]:F0} s {meanSoFarScale[c] * 100:+0.00;-0.00}% / {meanSoFarRotation[c]:+0.00;-0.00}°")));
            for (int c = 0; c < k; c++)
                TestContext.WriteLine($"{label} MEANS: ODOMETRY by {cutoffs[c]:F0} s: estimate {meanEstimate[c] * 100:+0.00;-0.00}% ±{meanPlusMinus[c] * 100:F2}, " +
                                      $"true tracked {meanTracked[c] * 100:+0.00;-0.00}%, odometry itself {meanOdometry[c] * 100:+0.00;-0.00}%, " +
                                      $"estimate off by {meanMiss[c] * 100:F2}% on average; seen beyond 2± with the right sign on {detected[c]}/{seeds} seeds");
        }

        /// <summary>
        /// Diagnostics, not a check: is local SLAM's drift biased? Seeds 1–6 × 300 s under variants that each remove one
        /// suspect, printing the signed end error averaged over the seeds. About 4 minutes. Run it on its own.
        /// </summary>
        [Explicit, Test, Timeout(3_600_000)]
        public void Diagnose_Bias()
        {
            // Found 2026-11-03 (rounds 1–5): the bias came from the lidar's RANGE noise, through both the map and the
            // matching; odometry, the start spin, bearing noise and dropouts were cleared. Fixed in the map by
            // LocalSlamConstants.FreeSpaceMarginSigmas and MaxInsertRangeMeters (worst 15.5 → 8.9 cm). Weighting
            // returns by their noise, or matching only near returns, made it worse. Kept as a regression check.
            var variants = new (string name, Action<SlamHarness> set)[]
            {
                ("as built", h => { }),
                ("exact odometry", h => h.OdometryNoise = 0f),
                ("no range noise", h => h.RangeNoise = false),
                ("exact lidar", h => h.LidarNoise = false),
            };
            foreach (var (name, set) in variants)
            {
                double ex = 0, ey = 0, eh = 0, worst = 0;
                var headings = new List<string>();
                const int seeds = 6;
                for (int seed = 1; seed <= seeds; seed++)
                {
                    var h = new SlamHarness(seed, loopClosure: false);
                    set(h);
                    h.Run(300f);
                    Vector2 e = (h.EndBelieved.position - h.EndTrue.position) * 100f;
                    float dh = Mathf.DeltaAngle(h.EndTrue.heading, h.EndBelieved.heading);
                    ex += e.x;
                    ey += e.y;
                    eh += dh;
                    worst += h.Drift.MaxMeters;
                    headings.Add($"{dh:+0.00;-0.00}");
                }
                TestContext.WriteLine($"{name}: mean end error ({ex / seeds:+0.0;-0.0}, {ey / seeds:+0.0;-0.0}) cm, heading {eh / seeds:+0.00;-0.00}° " +
                                      $"(per seed: {string.Join(" ", headings)}); mean worst {worst / seeds * 100:F1} cm");
            }
        }

        /// <summary>
        /// The fast redraw (a dense window, only changed cells written) leaves exactly the Internal Model the original
        /// straightforward redraw did, after every redraw of a 150 s run. Prints both redraws' times.
        /// </summary>
        [Test, Timeout(600_000)]
        public void Redraw_MatchesTheStraightforwardVersion()
        {
            // Small submaps, so loop closure (and with it redrawing) starts within the run; the live 360-scan submaps
            // finish their first submap only after about 720 scans.
            SlamHarness run;
            LocalSlam.ScansPerSubmapOverride = 90;
            try { run = new SlamHarness(1) { CompareRedraw = true }.Run(150f); }
            finally { LocalSlam.ScansPerSubmapOverride = 0; }
            TestContext.WriteLine($"{run.Redraws} redraws: fast mean {run.RedrawMsTotal / Mathf.Max(1, run.Redraws):F2} ms (worst {run.RedrawMsMax:F2}); " +
                                  $"original mean {run.ReferenceRedrawMsTotal / Mathf.Max(1, run.Redraws):F2} ms (worst {run.ReferenceRedrawMsMax:F2})");
            Assert.That(run.Redraws, Is.GreaterThan(0), "the run should have redrawn at least once");
            Assert.That(run.RedrawMismatches, Is.EqualTo(0), "cells that differ from the original redraw (summed over every redraw)");
        }

        /// <summary>
        /// Diagnostics, not a check: where does the one-directional error come from, the MAP or the MATCHING?
        ///
        /// Maps are built from scans taken at the TRUE poses (a 0.25 m lattice over the apartment, one scan each, inserted
        /// as local SLAM does), so no tracking error is involved:
        ///   exact      — exact scans, no free-space margin (the ideal map)
        ///   as built   — noisy scans, the 1σ margin (what local SLAM does)
        ///   no margin  — noisy scans, no margin
        ///
        /// 1. WALLS: where each map puts each wall surface (the first cell at the wall threshold met walking from the
        ///    room toward the wall, at its centre), averaged along the wall and over 8 lattice offsets, in cm INTO the
        ///    room (+) or behind the true surface (−).
        /// 2. MATCHING: at fixed true poses, 40 noisy scans each, every one fitted from a guess off by a zero-mean random
        ///    amount; the MEAN error of the fits (a bias), not their spread. Against each map, and against the as-built
        ///    map with only returns within 3 m. A real bias shows as a mean well above the spread ÷ √40.
        /// </summary>
        [Explicit, Test, Timeout(3_600_000)]
        public void Diagnose_BiasSource()
        {
            var world = SlamWorld.Apartment();
            const float cell = 0.0508f; // the live runs' cell size
            var mapPoses = BiasMapPoses(world);
            var variants = new (string name, bool noisy, float margin)[] { ("exact", false, 0f), ("as built", true, S.FreeSpaceMarginSigmas), ("no margin", true, 0f) };

            TestContext.WriteLine($"1. WALLS: where each map draws each wall surface, cm into the room (+) or behind it (−); {mapPoses.Count} mapping scans, {BiasOffsets} lattice offsets");
            var maps = new Dictionary<string, ProbabilityGrid>();
            var sums = new Dictionary<(string, int, float), double>();
            for (int k = 0; k < BiasOffsets; k++)
            {
                foreach (var (name, noisy, margin) in variants)
                {
                    var grid = BiasMap(world, mapPoses, BiasAnchor(k, cell), cell, noisy, margin, (uint)(100 + k));
                    if (k == 0) maps[name] = grid;
                    for (int w = 0; w < BiasWalls.Length; w++)
                        foreach (float threshold in new[] { S.OccupiedProbability, 0.5f })
                        {
                            var key = (name, w, threshold);
                            sums[key] = (sums.TryGetValue(key, out double v) ? v : 0) + WallOffset(world, grid, BiasWalls[w], threshold) / BiasOffsets;
                        }
                }
            }
            for (int w = 0; w < BiasWalls.Length; w++)
            {
                string line = $"    {BiasWalls[w].name}:";
                foreach (var (name, _, _) in variants)
                    line += $" {name} {sums[(name, w, S.OccupiedProbability)]:+0.00;-0.00} (at 0.5: {sums[(name, w, 0.5f)]:+0.00;-0.00}) |";
                TestContext.WriteLine(line.TrimEnd('|', ' '));
            }

            var conditions = new (string name, ProbabilityGrid map, float maxRange)[]
            {
                ("exact map", maps["exact"], float.MaxValue),
                ("as built", maps["as built"], float.MaxValue),
                ("as built, ≤3 m", maps["as built"], S.MaxInsertRangeMeters),
                ("no margin", maps["no margin"], float.MaxValue),
            };
            var testPoses = BiasTestPoses(world);
            var scans = BiasScans(world, testPoses);
            var summaries = new BiasSummary[conditions.Length];
            for (int k = 0; k < conditions.Length; k++) summaries[k] = new BiasSummary();

            TestContext.WriteLine($"2. MATCHING: mean fit error at {testPoses.Count} true poses ({BiasScansPerPose} noisy scans each; guesses off by σ 2 cm / 0.5°): " +
                                  "(x, y) cm and heading °, then [spread of single fits, cm]");
            for (int p = 0; p < testPoses.Count; p++)
            {
                string line = $"    ({testPoses[p].position.x:+0.00;-0.00}, {testPoses[p].position.y:+0.00;-0.00}) {testPoses[p].heading:+0;-0}°:";
                for (int k = 0; k < conditions.Length; k++)
                {
                    var (mean, heading, sd) = MatchBias(conditions[k].map, testPoses[p], scans[p], conditions[k].maxRange);
                    summaries[k].Add(mean, heading, sd, testPoses.Count);
                    line += $" {conditions[k].name} ({mean.x * 100:+0.00;-0.00}, {mean.y * 100:+0.00;-0.00}) {heading:+0.00;-0.00}° [{sd * 100:F1}] |";
                }
                TestContext.WriteLine(line.TrimEnd('|', ' '));
            }
            TestContext.WriteLine("   summary (bias = the mean error at a pose; noise in a mean ≈ spread ÷ 6.3):");
            for (int k = 0; k < conditions.Length; k++)
                TestContext.WriteLine($"    {conditions[k].name}: {summaries[k]}");
        }

        /// <summary>
        /// Eric's experiment: stand still at the dock first, adding every scan at the known dock pose (no fit, no motion
        /// filter), so the anchor submap is built from many sure readings, then spin, then rove. 8 seeds per setting, each
        /// seed printed (heading after the start, at 60 s and at the end, signed), then the means.
        /// </summary>
        [Explicit, Test, Timeout(3_600_000)]
        public void Experiment_StationaryStart()
        {
            var settings = new (string name, Action<SlamHarness> set)[]
            {
                ("as now (spin 120°/s, no hold)", h => { }),
                ("hold 2 s, then spin 120°/s", h => h.HoldSeconds = 2f),
                ("hold 5 s, then spin 120°/s", h => h.HoldSeconds = 5f),
                ("hold 10 s, then spin 120°/s", h => h.HoldSeconds = 10f),
                ("hold 5 s, then spin 60°/s", h => { h.HoldSeconds = 5f; h.SpinSpeed = 60f; }),
                ("hold 5 s, no spin", h => { h.HoldSeconds = 5f; h.SpinSign = 0f; }),
            };
            const int seeds = 8;
            foreach (var (name, set) in settings)
            {
                TestContext.WriteLine(name);
                float afterStart = 0, at60 = 0, end = 0, worstHeading = 0, worst = 0, worstMax = 0, p99 = 0, rms = 0;
                int starts = 0;
                for (int seed = 1; seed <= seeds; seed++)
                {
                    var harness = new SlamHarness(seed);
                    set(harness);
                    var run = harness.Run(300f);
                    float endHeading = Mathf.DeltaAngle(run.EndTrue.heading, run.EndBelieved.heading);
                    string start = float.IsNaN(run.HeadingAfterSpin) ? "n/a" : $"{run.HeadingAfterSpin:+0.00;-0.00}°";
                    TestContext.WriteLine($"    seed {seed}: heading after the start {start}, at 60 s {run.HeadingAt60:+0.00;-0.00}°, end {endHeading:+0.00;-0.00}°, " +
                                          $"worst {run.Drift.MaxDegrees:F2}° | position worst {run.Drift.MaxMeters * 100:F1} cm, p99 {run.Drift.PositionPercentile(0.99f) * 100:F1} cm, " +
                                          $"rms {run.Drift.RmsMeters * 100:F1} cm, end {run.EndPositionError * 100:F1} cm");
                    if (!float.IsNaN(run.HeadingAfterSpin))
                    {
                        afterStart += Mathf.Abs(run.HeadingAfterSpin);
                        starts++;
                    }
                    at60 += Mathf.Abs(run.HeadingAt60) / seeds;
                    end += Mathf.Abs(endHeading) / seeds;
                    worstHeading += run.Drift.MaxDegrees / seeds;
                    worst += run.Drift.MaxMeters / seeds;
                    worstMax = Mathf.Max(worstMax, run.Drift.MaxMeters);
                    p99 += run.Drift.PositionPercentile(0.99f) / seeds;
                    rms += run.Drift.RmsMeters / seeds;
                }
                TestContext.WriteLine($"  means: |heading| after the start {(starts > 0 ? (afterStart / starts).ToString("F2") + "°" : "n/a")}, at 60 s {at60:F2}°, end {end:F2}°, " +
                                      $"worst {worstHeading:F2}° | position worst {worst * 100:F1} cm (worst seed {worstMax * 100:F1}), p99 {p99 * 100:F1} cm, rms {rms * 100:F1} cm");
            }
        }

        /// <summary>
        /// The one clue that is not noise: with loop closure off, the believed track comes out rotated the SAME way
        /// (counter-clockwise) on all 8 seeds (Diagnose_ErrorSources, C0: +0.13° to +0.99°). Random drift would give mixed
        /// signs (8 of 8 alike by chance: 1 in 128). Which of these flips it says where it comes from:
        ///   start spin direction → the spin;   mirrored apartment → the room's shape;
        ///   full mirror image (mirrored apartment, mirrored goals, clockwise spin) NOT flipping → SLAM itself is
        ///   left/right-handed somewhere, i.e. a bug or an asymmetric step to find.
        /// 8 seeds × 300 s, loop closure off; per seed: best-fit rotation of the track, end heading error, worst.
        /// </summary>
        [Explicit, Test, Timeout(3_600_000)]
        public void Diagnose_Handedness()
        {
            var plain = SlamWorld.Apartment();
            var mirrored = plain.MirroredX();
            var settings = new (string name, SlamWorld world, Vector2 dock, bool mirrorGoals, float spin)[]
            {
                ("as now: counter-clockwise spin", plain, new Vector2(-2f, -1f), false, 1f),
                ("clockwise spin", plain, new Vector2(-2f, -1f), false, -1f),
                ("no spin", plain, new Vector2(-2f, -1f), false, 0f),
                ("mirrored apartment (and dock, goals), counter-clockwise spin", mirrored, new Vector2(2f, -1f), true, 1f),
                ("FULL MIRROR IMAGE: mirrored apartment, dock, goals, clockwise spin", mirrored, new Vector2(2f, -1f), true, -1f),
            };
            const int seeds = 8;
            foreach (var (name, world, dock, mirrorGoals, spin) in settings)
            {
                string rotations = "", headings = "";
                float rotationMean = 0, worst = 0, p99 = 0;
                int positive = 0;
                for (int seed = 1; seed <= seeds; seed++)
                {
                    var run = new SlamHarness(seed, world, false, dock) { MirrorGoals = mirrorGoals, SpinSign = spin }.Run(300f);
                    var aligned = run.Aligned();
                    float endHeading = Mathf.DeltaAngle(run.EndTrue.heading, run.EndBelieved.heading);
                    rotations += $" {aligned.rotationDegrees:+0.00;-0.00}";
                    headings += $" {endHeading:+0.00;-0.00}";
                    rotationMean += aligned.rotationDegrees / seeds;
                    if (aligned.rotationDegrees > 0) positive++;
                    worst += run.Drift.MaxMeters / seeds;
                    p99 += run.Drift.PositionPercentile(0.99f) / seeds;
                }
                TestContext.WriteLine($"{name}: track rotation{rotations} (mean {rotationMean:+0.00;-0.00}°, {positive} of {seeds} counter-clockwise) | " +
                                      $"end heading{headings} | worst {worst * 100:F1} cm, p99 {p99 * 100:F1} cm");
            }
        }

        /// <summary>
        /// Diagnostics, measurement only: how the error behaves at each scan added to the map (8 seeds × 300 s, loop
        /// closure off, then on).
        ///
        /// 1. GROWTH: the mean squared change of the error over L insertions, D(L), for several L, and the exponent
        ///    between neighbouring L. About 1 = a random walk (noise integrated), about 2 = a steady drift (bias
        ///    integrated), about 0 = bounded (something pulls it back). Plus each seed's heading drift rate.
        /// 2. ENTRY: the change of the error between consecutive insertions, grouped by what happened in between (turning
        ///    in place vs driving) and flagged when the scan saw mostly unmapped cells or its submap was new; each group's
        ///    share of the total squared change says where error gets in. (Every interval also carries the matcher's own
        ///    scan-to-scan noise, so groups are compared by their share and their size per insertion, not absolutely.)
        /// </summary>
        [Explicit, Test, Timeout(3_600_000)]
        public void Diagnose_ErrorGrowth()
        {
            const int seeds = 8;
            int[] lags = { 3, 10, 30, 100, 300 };
            foreach (bool loop in new[] { false, true })
            {
                TestContext.WriteLine(loop ? "LOOP CLOSURE ON (as now)" : "LOOP CLOSURE OFF (local SLAM alone: the pure integrator)");
                var dHeading = new double[lags.Length];
                var dPosition = new double[lags.Length];
                var counts = new int[lags.Length];
                string rates = "";
                int insertions = 0;

                // Entry groups: [0] turning in place, [1] driving, [2] other; flags: [3] mostly unseen (≥ 30%), [4] new submap (≤ 10 scans).
                string[] names = { "turning in place (moved < 5 cm, turned ≥ 1°)", "driving (moved ≥ 5 cm)", "other (time trigger, small motion)",
                                   "  flag: scan mostly unmapped (≥ 30% of returns in unseen cells)", "  flag: newest submap ≤ 10 scans old" };
                var n = new int[5];
                var sumH = new double[5];
                var sumH2 = new double[5];
                var sumP2 = new double[5];
                double totalH2 = 0, totalP2 = 0;

                for (int seed = 1; seed <= seeds; seed++)
                {
                    var run = new SlamHarness(seed, null, loop) { RecordInsertions = true }.Run(300f);
                    var rec = run.Insertions;
                    insertions += rec.Count;

                    for (int k = 0; k < lags.Length; k++)
                        for (int i = 0; i + lags[k] < rec.Count; i++)
                        {
                            double dh = Mathf.DeltaAngle(rec[i].headingError, rec[i + lags[k]].headingError);
                            dHeading[k] += dh * dh;
                            dPosition[k] += (rec[i + lags[k]].error - rec[i].error).sqrMagnitude;
                            counts[k]++;
                        }

                    // Heading drift rate: least-squares slope of heading error against insertion number.
                    double mx = 0, my = 0, sxy = 0, sxx = 0;
                    for (int i = 0; i < rec.Count; i++) { mx += i; my += rec[i].headingError; }
                    mx /= rec.Count;
                    my /= rec.Count;
                    for (int i = 0; i < rec.Count; i++) { sxy += (i - mx) * (rec[i].headingError - my); sxx += (i - mx) * (i - mx); }
                    rates += $" {sxy / sxx * 100:+0.00;-0.00}";

                    for (int i = 1; i < rec.Count; i++)
                    {
                        double dh = Mathf.DeltaAngle(rec[i - 1].headingError, rec[i].headingError);
                        double dp2 = (rec[i].error - rec[i - 1].error).sqrMagnitude;
                        int g = rec[i].trueMove >= 0.05f ? 1 : rec[i].trueTurn >= 1f ? 0 : 2;
                        void Add(int j) { n[j]++; sumH[j] += dh; sumH2[j] += dh * dh; sumP2[j] += dp2; }
                        Add(g);
                        if (rec[i].unseen >= 0.3f) Add(3);
                        if (rec[i].submapScans <= 10) Add(4);
                        totalH2 += dh * dh;
                        totalP2 += dp2;
                    }
                }

                TestContext.WriteLine($"  {insertions} insertions over {seeds} seeds");
                TestContext.WriteLine("  1. GROWTH of the error over L insertions (mean squared change; exponent to the next L: ~1 random walk, ~2 drift, ~0 bounded)");
                for (int k = 0; k < lags.Length; k++)
                {
                    double h = dHeading[k] / counts[k], pp = dPosition[k] / counts[k];
                    string slope = "";
                    if (k + 1 < lags.Length)
                    {
                        double h2 = dHeading[k + 1] / counts[k + 1], p2 = dPosition[k + 1] / counts[k + 1];
                        double lr = System.Math.Log((double)lags[k + 1] / lags[k]);
                        slope = $" | exponent to L={lags[k + 1]}: heading {System.Math.Log(h2 / h) / lr:F2}, position {System.Math.Log(p2 / pp) / lr:F2}";
                    }
                    TestContext.WriteLine($"    L={lags[k],3}: heading rms change {System.Math.Sqrt(h):F3}°, position rms change {System.Math.Sqrt(pp) * 100:F2} cm{slope}");
                }
                TestContext.WriteLine($"    heading drift rate per seed (° per 100 insertions):{rates}");

                TestContext.WriteLine("  2. ENTRY: change of the error between consecutive insertions, by what happened in between");
                for (int j = 0; j < 5; j++)
                {
                    if (n[j] == 0) continue;
                    TestContext.WriteLine($"    {names[j]}: {n[j]} ({100.0 * n[j] / Mathf.Max(1, insertions - seeds):F0}% of insertions) | heading: rms {System.Math.Sqrt(sumH2[j] / n[j]):F3}° per insertion, " +
                                          $"mean {sumH[j] / n[j]:+0.0000;-0.0000}°, share of total {100 * sumH2[j] / totalH2:F0}% | " +
                                          $"position: rms {System.Math.Sqrt(sumP2[j] / n[j]) * 100:F2} cm per insertion, share of total {100 * sumP2[j] / totalP2:F0}%");
                }
            }
        }

        /// <summary>
        /// Where is the right room's ~4 cm offset created? The pose error (believed − true) averaged by where the robot
        /// really is — left room (x &lt; 0.3), doorway (0.3–0.7, above the middle wall), right room (x &gt; 0.7) — and the
        /// change in error across each doorway crossing (last step before the doorway → first step after it). Live settings,
        /// 8 seeds × 300 s, loop closure on, then off. Measurement only.
        /// </summary>
        [Explicit, Test, Timeout(3_600_000)]
        public void Diagnose_Regions()
        {
            int Region(Vector2 p) => p.x < 0.3f ? 0 : p.x > 0.7f ? 2 : 1;
            string[] names = { "left room", "doorway", "right room" };
            const int seeds = 8;
            foreach (bool loop in new[] { true, false })
            {
                TestContext.WriteLine(loop ? "LOOP CLOSURE ON (live)" : "LOOP CLOSURE OFF");
                var sumX = new double[3]; var sumY = new double[3]; var sumH = new double[3];
                var sqX = new double[3]; var sqY = new double[3]; var steps = new int[3];
                // Crossings: [0] left → right, [1] right → left.
                var jumpX = new double[2]; var jumpY = new double[2]; var jumpH = new double[2]; var crossings = new int[2];
                var jumpSqX = new double[2];
                string firstEntries = "";

                for (int seed = 1; seed <= seeds; seed++)
                {
                    int lastSide = -1;            // last room (0 or 2) the robot was in
                    Vector2 lastSideError = Vector2.zero;
                    float lastSideHeading = 0f;
                    bool enteredRight = false;
                    var harness = new SlamHarness(seed, null, loop);
                    harness.OnStep = (believed, truth, t) =>
                    {
                        Vector2 e = believed.position - truth.position;
                        float h = Mathf.DeltaAngle(truth.heading, believed.heading);
                        int r = Region(truth.position);
                        sumX[r] += e.x; sumY[r] += e.y; sumH[r] += h;
                        sqX[r] += e.x * e.x; sqY[r] += e.y * e.y; steps[r]++;
                        if (r == 1) return;
                        if (lastSide >= 0 && r != lastSide)
                        {
                            int k = lastSide == 0 ? 0 : 1;
                            jumpX[k] += e.x - lastSideError.x;
                            jumpY[k] += e.y - lastSideError.y;
                            jumpH[k] += Mathf.DeltaAngle(lastSideHeading, h);
                            jumpSqX[k] += (e.x - lastSideError.x) * (e.x - lastSideError.x);
                            crossings[k]++;
                        }
                        if (r == 2 && !enteredRight)
                        {
                            enteredRight = true;
                            firstEntries += $" ({e.x * 100:+0.0;-0.0}, {e.y * 100:+0.0;-0.0}) cm at {t:F0} s;";
                        }
                        lastSide = r;
                        lastSideError = e;
                        lastSideHeading = h;
                    };
                    harness.Run(300f);
                }

                for (int r = 0; r < 3; r++)
                {
                    if (steps[r] == 0) continue;
                    double mx = sumX[r] / steps[r], my = sumY[r] / steps[r];
                    TestContext.WriteLine($"    {names[r]}: {100.0 * steps[r] / (steps[0] + steps[1] + steps[2]):F0}% of the time | mean error ({mx * 100:+0.00;-0.00}, {my * 100:+0.00;-0.00}) cm, " +
                                          $"heading {sumH[r] / steps[r]:+0.00;-0.00}° | spread x {System.Math.Sqrt(System.Math.Max(0, sqX[r] / steps[r] - mx * mx)) * 100:F2} cm, " +
                                          $"y {System.Math.Sqrt(System.Math.Max(0, sqY[r] / steps[r] - my * my)) * 100:F2} cm");
                }
                for (int k = 0; k < 2; k++)
                {
                    if (crossings[k] == 0) continue;
                    double mx = jumpX[k] / crossings[k];
                    TestContext.WriteLine($"    crossings {(k == 0 ? "left → right" : "right → left")}: {crossings[k]} | mean change in error ({mx * 100:+0.00;-0.00}, " +
                                          $"{jumpY[k] / crossings[k] * 100:+0.00;-0.00}) cm, heading {jumpH[k] / crossings[k]:+0.00;-0.00}° | " +
                                          $"spread of the x change {System.Math.Sqrt(System.Math.Max(0, jumpSqX[k] / crossings[k] - mx * mx)) * 100:F2} cm");
                }
                TestContext.WriteLine($"    error on first entering the right room, each seed:{firstEntries}");
            }
        }

        /// <summary>
        /// Keyframe stage 0, D1 (claude/SLAM_Certain_Anchor_Design.md): would keyframes help, and does a 10 s pause at
        /// the dock before the start spin? A keyframe is many scans averaged at one stop, so its range noise is ÷√N;
        /// it is added at the TRACKED pose (the robot doesn't know where it really is). Rows: as now; 10 s pause;
        /// exact scans inserted (the limit); range noise ÷ √10 inserted (1 s of averaging; bearing noise left full,
        /// so this is pessimistic); and the pause with each. Tracking always fits the ordinary noisy scan. The same
        /// report as Diagnose_Scale. GO if inserted-scan noise moves scale and rotation toward a map built at true
        /// poses (−0.15%, +0.04°); NO-GO if they stay near −0.57% / +0.23°. Measurement only.
        /// </summary>
        [Explicit, Test, Timeout(3_600_000)]
        public void Diagnose_KeyframeLimit()
        {
            const int seeds = 8;
            float averaged = 1f / Mathf.Sqrt(10f);
            var rows = new (string name, float hold, float insertNoise)[]
            {
                ("as now", 0f, -1f),
                ("10 s pause at the dock, then the spin", 10f, -1f),
                ("exact scans inserted at the tracked pose (keyframe limit)", 0f, 0f),
                ("range noise ÷ √10 inserted at the tracked pose (1 s keyframes)", 0f, averaged),
                ("10 s pause + exact scans inserted", 10f, 0f),
                ("10 s pause + range noise ÷ √10 inserted", 10f, averaged),
            };
            foreach (var row in rows)
            {
                double scale = 0, sx = 0, sy = 0, rotation = 0, plainRotation = 0, max = 0, rms = 0, worst = 0, p99 = 0, rawRms = 0, spinHeading = 0;
                string perSeed = "", perSeedRotation = "";
                for (int seed = 1; seed <= seeds; seed++)
                {
                    var run = new SlamHarness(seed) { HoldSeconds = row.hold, InsertRangeNoiseScale = row.insertNoise }.Run(300f);
                    var fit = run.AlignedWithScale();
                    var plain = run.Aligned();
                    perSeed += $" {(fit.scale - 1) * 100:+0.00;-0.00}";
                    perSeedRotation += $" {fit.rotationDegrees:+0.00;-0.00}";
                    scale += fit.scale / seeds;
                    sx += fit.scaleX / seeds;
                    sy += fit.scaleY / seeds;
                    rotation += fit.rotationDegrees / seeds;
                    plainRotation += plain.rotationDegrees / seeds;
                    max += fit.max / seeds;
                    rms += fit.rms / seeds;
                    worst += run.Drift.MaxMeters / seeds;
                    p99 += run.Drift.PositionPercentile(0.99f) / seeds;
                    rawRms += run.Drift.RmsMeters / seeds;
                    spinHeading += Mathf.Abs(run.HeadingAfterSpin) / seeds;
                }
                TestContext.WriteLine($"{row.name}: scale {(scale - 1) * 100:+0.00;-0.00}% (each seed, %:{perSeed}) | x {(sx - 1) * 100:+0.00;-0.00}%, y {(sy - 1) * 100:+0.00;-0.00}% | " +
                                      $"rotation with scale allowed {rotation:+0.00;-0.00}° (each seed:{perSeedRotation}); without scale {plainRotation:+0.00;-0.00}° | " +
                                      $"heading error after the spin {spinHeading:F3}° (mean size) | after shift, rotation and scale removed: worst {max * 100:F1} cm, rms {rms * 100:F1} | " +
                                      $"raw worst {worst * 100:F1} cm, p99 {p99 * 100:F1}, rms {rawRms * 100:F1}");
            }
        }

        /// <summary>
        /// Keyframe stage 0, D5: D4 left a +0.45° lean from a 10 s pause at the dock that lives in the HITS (not the
        /// grid size, not the margin; misses add ~0.3°), nearly the same on every seed. Which part of the simulated
        /// lidar's noise makes it? Each pair is no pause / 10 s pause, both WITHOUT the start spin: all noise; no lidar
        /// noise at all; no bearing noise; no range noise; no dropouts. If the lean survives with no noise, it is
        /// geometry or code (possibly the lidar simulation); if it vanishes with one source off, that source. The SLAM
        /// itself is unchanged (its free-space margin still assumes noise). 8 seeds × 300 s, live settings, loop on;
        /// the Diagnose_DockStart report. Measurement only.
        /// </summary>
        [Explicit, Test, Timeout(3_600_000)]
        public void Diagnose_PauseNoiseSource()
        {
            const int seeds = 8;
            var sources = new (string name, Action<SlamHarness> set)[]
            {
                ("all noise", h => { }),
                ("NO lidar noise", h => h.LidarNoise = false),
                ("no bearing noise", h => h.BearingNoise = false),
                ("no range noise", h => h.RangeNoise = false),
                ("no dropouts", h => h.Dropouts = false),
            };
            foreach (var source in sources)
            foreach (float hold in new[] { 0f, 10f })
            {
                double scale = 0, sx = 0, sy = 0, rotation = 0, max = 0, rms = 0, worst = 0, p99 = 0, rawRms = 0, heading60 = 0;
                string perSeedRotation = "";
                for (int seed = 1; seed <= seeds; seed++)
                {
                    var harness = new SlamHarness(seed) { SpinSign = 0f, HoldSeconds = hold };
                    source.set(harness);
                    var run = harness.Run(300f);
                    var fit = run.AlignedWithScale();
                    perSeedRotation += $" {fit.rotationDegrees:+0.00;-0.00}";
                    scale += fit.scale / seeds;
                    sx += fit.scaleX / seeds;
                    sy += fit.scaleY / seeds;
                    rotation += fit.rotationDegrees / seeds;
                    max += fit.max / seeds;
                    rms += fit.rms / seeds;
                    worst += run.Drift.MaxMeters / seeds;
                    p99 += run.Drift.PositionPercentile(0.99f) / seeds;
                    rawRms += run.Drift.RmsMeters / seeds;
                    heading60 += Mathf.Abs(run.HeadingAt60) / seeds;
                }
                TestContext.WriteLine($"{source.name}, {(hold > 0 ? "10 s pause" : "no pause")}: scale {(scale - 1) * 100:+0.00;-0.00}% (x {(sx - 1) * 100:+0.00;-0.00}%, y {(sy - 1) * 100:+0.00;-0.00}%) | " +
                                      $"rotation with scale allowed {rotation:+0.00;-0.00}° (each seed:{perSeedRotation}) | heading error at 60 s {heading60:F3}° (mean size) | " +
                                      $"after shift, rotation and scale removed: worst {max * 100:F1} cm, rms {rms * 100:F1} | raw worst {worst * 100:F1} cm, p99 {p99 * 100:F1}, rms {rawRms * 100:F1}");
            }
        }

        /// <summary>
        /// Keyframe stage 0, D2: is odometry (wheels + gyro) a trust signal from inside the robot that the map can't
        /// fool? Pairs of scan matches about 2 m of true travel apart, within 10 s, turning ≤ 30° in between (8 seeds ×
        /// 300 s, live settings). For each pair, the motion as tracked (local SLAM), as odometry measured it, and as it
        /// really was, each in the robot's own frame at the first match. Reports each one's scale error (distance
        /// error ÷ true distance) and heading-change error, mean and spread; whether tracking and odometry DISAGREE
        /// more than odometry's own spread, and how big tracking's true error is when they do and when they don't.
        /// GO if tracking's scale/heading bias stands clear of odometry's spread (odometry can see it), and flagged
        /// pairs carry clearly larger tracking error. Measurement only.
        /// </summary>
        [Explicit, Test, Timeout(3_600_000)]
        public void Diagnose_OdometryAgreement()
        {
            const int seeds = 8;
            const double pairMeters = 2.0, maxSeconds = 10.0, maxTurn = 30.0;
            var pairs = new List<(double trackScale, double odoScale, double trackTurn, double odoTurn, double disagree, double disagreeTurn, double trackError, double trackTurnError)>();
            for (int seed = 1; seed <= seeds; seed++)
            {
                var harness = new SlamHarness(seed);
                var log = new List<(float t, RobotPose local, RobotPose truth, RobotPose odo, double path, double turn)>();
                double path = 0, turn = 0;
                RobotPose? previous = null;
                harness.OnMatch = (local, truth, t) =>
                {
                    if (previous.HasValue)
                    {
                        path += Vector2.Distance(previous.Value.position, truth.position);
                        turn += Mathf.Abs(Mathf.DeltaAngle(previous.Value.heading, truth.heading));
                    }
                    previous = truth;
                    log.Add((t, local, truth, harness.DeadReckoned, path, turn));
                };
                harness.Run(300f);

                int j = 0;
                for (int i = 0; i < log.Count; i += 10)
                {
                    if (log[i].t < S.StartSpinDegrees / S.StartSpinDegreesPerSecond + 0.5f) continue; // after the start spin
                    if (j < i) j = i;
                    while (j < log.Count && log[j].path - log[i].path < pairMeters) j++;
                    if (j >= log.Count) break;
                    if (log[j].t - log[i].t > maxSeconds || log[j].turn - log[i].turn > maxTurn) continue;
                    var truthMove = GraphPose.Relative(GraphPose.From(log[i].truth), GraphPose.From(log[j].truth));
                    var trackMove = GraphPose.Relative(GraphPose.From(log[i].local), GraphPose.From(log[j].local));
                    var odoMove = GraphPose.Relative(GraphPose.From(log[i].odo), GraphPose.From(log[j].odo));
                    double trueDistance = System.Math.Sqrt(truthMove.x * truthMove.x + truthMove.y * truthMove.y);
                    if (trueDistance < 0.5) continue;
                    double trackDistance = System.Math.Sqrt(trackMove.x * trackMove.x + trackMove.y * trackMove.y);
                    double odoDistance = System.Math.Sqrt(odoMove.x * odoMove.x + odoMove.y * odoMove.y);
                    double dx = trackMove.x - odoMove.x, dy = trackMove.y - odoMove.y;
                    double ex = trackMove.x - truthMove.x, ey = trackMove.y - truthMove.y;
                    pairs.Add(((trackDistance - trueDistance) / trueDistance, (odoDistance - trueDistance) / trueDistance,
                        GraphPose.WrapDegrees(trackMove.heading - truthMove.heading), GraphPose.WrapDegrees(odoMove.heading - truthMove.heading),
                        System.Math.Sqrt(dx * dx + dy * dy), GraphPose.WrapDegrees(trackMove.heading - odoMove.heading),
                        System.Math.Sqrt(ex * ex + ey * ey), GraphPose.WrapDegrees(trackMove.heading - truthMove.heading)));
                }
            }

            double Mean(Func<(double trackScale, double odoScale, double trackTurn, double odoTurn, double disagree, double disagreeTurn, double trackError, double trackTurnError), double> f,
                IEnumerable<(double trackScale, double odoScale, double trackTurn, double odoTurn, double disagree, double disagreeTurn, double trackError, double trackTurnError)> g) => g.Average(f);
            double Spread(Func<(double trackScale, double odoScale, double trackTurn, double odoTurn, double disagree, double disagreeTurn, double trackError, double trackTurnError), double> f,
                IEnumerable<(double trackScale, double odoScale, double trackTurn, double odoTurn, double disagree, double disagreeTurn, double trackError, double trackTurnError)> g)
            {
                var list = g.Select(f).ToList();
                double m = list.Average();
                return System.Math.Sqrt(list.Average(v => (v - m) * (v - m)));
            }

            if (pairs.Count == 0) { TestContext.WriteLine("no pairs"); return; }
            TestContext.WriteLine($"{pairs.Count} pairs (~{pairMeters} m of true travel, ≤ {maxSeconds} s, ≤ {maxTurn}° turning)");
            TestContext.WriteLine($"  scale error: tracked mean {Mean(x => x.trackScale, pairs) * 100:+0.000;-0.000}% (spread {Spread(x => x.trackScale, pairs) * 100:F3}%), " +
                                  $"odometry mean {Mean(x => x.odoScale, pairs) * 100:+0.000;-0.000}% (spread {Spread(x => x.odoScale, pairs) * 100:F3}%)");
            TestContext.WriteLine($"  heading-change error: tracked mean {Mean(x => x.trackTurn, pairs):+0.0000;-0.0000}° (spread {Spread(x => x.trackTurn, pairs):F4}°), " +
                                  $"odometry mean {Mean(x => x.odoTurn, pairs):+0.0000;-0.0000}° (spread {Spread(x => x.odoTurn, pairs):F4}°)");
            int n = pairs.Count;
            double seScale = Spread(x => x.odoScale, pairs) / System.Math.Sqrt(n), seTurn = Spread(x => x.odoTurn, pairs) / System.Math.Sqrt(n);
            TestContext.WriteLine($"  averaged over all pairs, odometry's own uncertainty is ±{seScale * 100:F3}% in scale and ±{seTurn:F4}° in heading change " +
                                  $"(spread ÷ √pairs): a tracked bias larger than that is visible to odometry");

            // Per pair: does disagreement with odometry pick out pairs where tracking really went wrong?
            double odoSpreadMeters = Spread(x => x.odoScale, pairs) * pairMeters;
            double odoSpreadTurn = Spread(x => x.odoTurn, pairs);
            foreach (double k in new[] { 1.0, 2.0 })
            {
                var flagged = pairs.Where(x => x.disagree > k * odoSpreadMeters || System.Math.Abs(x.disagreeTurn) > k * odoSpreadTurn).ToList();
                var quiet = pairs.Where(x => !(x.disagree > k * odoSpreadMeters || System.Math.Abs(x.disagreeTurn) > k * odoSpreadTurn)).ToList();
                string Describe(List<(double trackScale, double odoScale, double trackTurn, double odoTurn, double disagree, double disagreeTurn, double trackError, double trackTurnError)> g) =>
                    g.Count == 0 ? "none" :
                    $"{g.Count} ({g.Count * 100.0 / n:F0}%): tracking's true error rms {System.Math.Sqrt(g.Average(x => x.trackError * x.trackError)) * 100:F2} cm, " +
                    $"heading-change rms {System.Math.Sqrt(g.Average(x => x.trackTurnError * x.trackTurnError)):F3}°, scale mean {g.Average(x => x.trackScale) * 100:+0.000;-0.000}%";
                TestContext.WriteLine($"  disagreeing by more than {k:F0}× odometry's spread: {Describe(flagged)}");
                TestContext.WriteLine($"  agreeing:                                    {Describe(quiet)}");
            }
        }

        /// <summary>One row of the heading diagnostics: 4 seeds × 300 s with <paramref name="set"/> applied to each harness.</summary>
        static void HeadingRow(string name, Action<SlamHarness> set)
        {
            const int seeds = 4;
            float afterSpin = 0, at60 = 0; int spins = 0; float end = 0, worst = 0, rms = 0, position = 0, positionMax = 0, p99 = 0;
            string signs = "";
            for (int seed = 1; seed <= seeds; seed++)
            {
                var harness = new SlamHarness(seed);
                set(harness);
                var run = harness.Run(300f);
                float endHeading = Mathf.DeltaAngle(run.EndTrue.heading, run.EndBelieved.heading);
                if (!float.IsNaN(run.HeadingAfterSpin))
                {
                    afterSpin += Mathf.Abs(run.HeadingAfterSpin);
                    spins++;
                }
                at60 += Mathf.Abs(run.HeadingAt60) / seeds;
                end += Mathf.Abs(endHeading) / seeds;
                worst += run.Drift.MaxDegrees / seeds;
                rms += run.Drift.RmsDegrees / seeds;
                position += run.Drift.MaxMeters / seeds;
                p99 += run.Drift.PositionPercentile(0.99f) / seeds;
                positionMax = Mathf.Max(positionMax, run.Drift.MaxMeters);
                signs += $" {endHeading:+0.00;-0.00}";
            }
            TestContext.WriteLine($"{name}: heading after spin {(spins > 0 ? (afterSpin / spins).ToString("F2") + "°" : "n/a")}, at 60 s {at60:F2}°, " +
                                  $"end {end:F2}° (each seed:{signs}), worst {worst:F2}°, rms {rms:F2}° | " +
                                  $"position worst {position * 100:F1} cm (worst seed {positionMax * 100:F1}), p99 {p99 * 100:F1} cm");
        }

        // ---- Bias diagnostics: shared pieces ----

        const int BiasOffsets = 8;
        const int BiasScansPerPose = 40;

        /// <summary>The wall surfaces: a point in the room to start from, the direction toward the wall, the true surface's coordinate on that axis, and the span along the wall to sample.</summary>
        static readonly (string name, Vector2 start, Vector2 dir, float surface, float spanMin, float spanMax)[] BiasWalls =
        {
            ("left outer (x = −3.0)", new Vector2(-2.5f, 0f), Vector2.left, -3.0f, -1.8f, 1.8f),
            ("right outer (x = +3.0)", new Vector2(2.5f, 0f), Vector2.right, 3.0f, -1.8f, 1.8f),
            ("top outer (y = +2.0)", new Vector2(0f, 1.5f), Vector2.up, 2.0f, -2.8f, 2.8f),
            ("bottom outer (y = −2.0)", new Vector2(0f, -1.5f), Vector2.down, -2.0f, -2.8f, 2.8f),
            ("middle wall, left face (x = +0.45)", new Vector2(0f, 0f), Vector2.right, 0.45f, -1.8f, 0.4f),
            ("middle wall, right face (x = +0.55)", new Vector2(1.0f, 0f), Vector2.left, 0.55f, -1.8f, 0.4f),
        };

        /// <summary>Lattice offset k of <see cref="BiasOffsets"/>: spread evenly across a cell in x and in y (y in a shuffled order), so rounding averages out.</summary>
        static Vector2 BiasAnchor(int k, float cell) =>
            new Vector2((k + 0.5f) / BiasOffsets * cell, ((k * 3) % BiasOffsets + 0.5f) / BiasOffsets * cell);

        /// <summary>The mapping poses: a 0.25 m lattice over the free floor, a random heading each.</summary>
        static List<RobotPose> BiasMapPoses(SlamWorld world)
        {
            var rng = new System.Random(7);
            var poses = new List<RobotPose>();
            for (float x = -2.85f; x <= 2.86f; x += 0.25f)
                for (float y = -1.85f; y <= 1.86f; y += 0.25f)
                    if (world.IsFree(new Vector2(x, y), 0.2f)) poses.Add(new RobotPose(new Vector2(x, y), (float)(rng.NextDouble() * 360 - 180)));
            return poses;
        }

        /// <summary>The fixed true poses the matching is tested at.</summary>
        static List<RobotPose> BiasTestPoses(SlamWorld world)
        {
            var rng = new System.Random(8);
            var poses = new List<RobotPose>();
            for (float x = -2.55f; x <= 2.6f; x += 0.85f)
                for (float y = -1.6f; y <= 1.65f; y += 0.8f)
                    if (world.IsFree(new Vector2(x, y), 0.25f)) poses.Add(new RobotPose(new Vector2(x, y), (float)(rng.NextDouble() * 360 - 180)));
            return poses;
        }

        /// <summary>A map built from one scan at each true pose, inserted as local SLAM does (3 m, the given margin).</summary>
        static ProbabilityGrid BiasMap(SlamWorld world, List<RobotPose> poses, Vector2 anchor, float cell, bool noisy, float margin, uint seed)
        {
            var grid = new ProbabilityGrid(anchor, cell);
            var random = noisy ? new RandomProvider(seed) : null;
            var changed = new List<long>();
            foreach (var pose in poses)
            {
                var frame = world.Scan(pose, random);
                changed.Clear();
                grid.Insert(pose, frame.lidarRays, S.MissingDataRayLengthMeters, S.MaxInsertRangeMeters, margin, changed);
            }
            return grid;
        }

        /// <summary>Cm into the room (+) of the first cell at <paramref name="threshold"/> met walking from the room toward the wall, at its centre; averaged along the wall.</summary>
        static float WallOffset(SlamWorld world, ProbabilityGrid grid, (string name, Vector2 start, Vector2 dir, float surface, float spanMin, float spanMax) w, float threshold)
        {
            float cell = grid.CellSizeMeters;
            bool alongX = Mathf.Abs(w.dir.x) > 0.5f;
            float sign = alongX ? Mathf.Sign(w.dir.x) : Mathf.Sign(w.dir.y);
            double sum = 0;
            int samples = 0;
            for (float s = w.spanMin; s <= w.spanMax + 1e-4f; s += 0.025f)
            {
                Vector2 from = alongX ? new Vector2(w.start.x, s) : new Vector2(s, w.start.y);
                if (!alongX && Mathf.Abs(s - 0.5f) < 0.25f && w.dir.y < 0f) continue; // where the middle wall meets the bottom wall
                for (float travelled = 0f; travelled < 1.2f; travelled += cell * 0.25f)
                {
                    Vector2 p = from + w.dir * travelled;
                    if (!world.IsFree(p, 0f) && Mathf.Abs((alongX ? p.x : p.y) - w.surface) > 0.15f) break; // something else in the way
                    if (grid.TryCell(p, out int row, out int col) && grid.Probability(row, col) >= threshold)
                    {
                        Vector2 centre = grid.AnchorWorld + new Vector2((col + 0.5f) * cell, (row + 0.5f) * cell);
                        sum += (w.surface - (alongX ? centre.x : centre.y)) * sign;
                        samples++;
                        break;
                    }
                }
            }
            return samples > 0 ? (float)(sum / samples * 100.0) : float.NaN;
        }

        /// <summary>For each test pose: <see cref="BiasScansPerPose"/> noisy scans and a guess for each (off by σ 2 cm / 0.5°).</summary>
        static List<(List<SensorFrame> frames, List<RobotPose> guesses)> BiasScans(SlamWorld world, List<RobotPose> poses)
        {
            var scanRandom = new RandomProvider(31);
            var guessRng = new System.Random(11);
            var all = new List<(List<SensorFrame>, List<RobotPose>)>();
            foreach (var truth in poses)
            {
                var frames = new List<SensorFrame>();
                var guesses = new List<RobotPose>();
                for (int n = 0; n < BiasScansPerPose; n++)
                {
                    frames.Add(world.Scan(truth, scanRandom));
                    guesses.Add(new RobotPose(truth.position + new Vector2(Gauss(guessRng, 0.02f), Gauss(guessRng, 0.02f)), truth.heading + Gauss(guessRng, 0.5f)));
                }
                all.Add((frames, guesses));
            }
            return all;
        }

        /// <summary>The mean fit error at one true pose (position, heading °) and the spread of single fits (m).</summary>
        static (Vector2 mean, double heading, double sd) MatchBias(ProbabilityGrid map, RobotPose truth,
            (List<SensorFrame> frames, List<RobotPose> guesses) scans, float maxRange)
        {
            var matcher = new ScanMatcher();
            int n = scans.frames.Count;
            Vector2 mean = Vector2.zero;
            double heading = 0, sq = 0;
            var errors = new List<Vector2>();
            for (int s = 0; s < n; s++)
            {
                var points = new List<Vector2>();
                var rays = scans.frames[s].lidarRays;
                int seen = 0;
                for (int i = 0; i < rays.Count; i++)
                {
                    if (!rays[i].hit || rays[i].range > maxRange) continue;
                    if (seen++ % 2 != 0) continue; // the Balanced quality's ray stride
                    points.Add(RobotPose.DirectionOf(rays[i].bearing) * rays[i].range);
                }
                var fit = matcher.Match(map, points, scans.guesses[s], S.TranslationWeight, S.RotationWeight);
                Vector2 e = fit.position - truth.position;
                errors.Add(e);
                mean += e / n;
                heading += Mathf.DeltaAngle(truth.heading, fit.heading) / n;
            }
            foreach (var e in errors) sq += (e - mean).sqrMagnitude;
            return (mean, heading, System.Math.Sqrt(sq / (n - 1)));
        }

        /// <summary>Matching bias over many poses: the mean size of each pose's bias, the worst, the part common to all poses, and the spread of single fits.</summary>
        sealed class BiasSummary
        {
            Vector2 common;
            double size, worst, heading, spread;

            public void Add(Vector2 mean, double meanHeading, double sd, int poses)
            {
                common += mean / poses;
                size += mean.magnitude / poses;
                worst = System.Math.Max(worst, mean.magnitude);
                heading += System.Math.Abs(meanHeading) / poses;
                spread += sd / poses;
            }

            public override string ToString() =>
                $"bias mean {size * 100:F2} cm (worst {worst * 100:F2}), heading {heading:F2}°; common to all poses ({common.x * 100:+0.00;-0.00}, {common.y * 100:+0.00;-0.00}) cm; spread of single fits {spread * 100:F2} cm";
        }

        static float Gauss(System.Random rng, float sigma)
        {
            double u1 = 1.0 - rng.NextDouble(), u2 = rng.NextDouble();
            return (float)(sigma * System.Math.Sqrt(-2.0 * System.Math.Log(u1)) * System.Math.Cos(2.0 * System.Math.PI * u2));
        }

        static void Print(int seed, SlamHarness run)
        {
            var d = run.Drift;
            TestContext.WriteLine(
                $"seed {seed}: worst {d.MaxMeters * 100f:F1} cm, {d.MaxDegrees:F2}°; " +
                $"p99 {d.PositionPercentile(0.99f) * 100f:F1} cm, {d.HeadingPercentile(0.99f):F2}°; " +
                $"p95 {d.PositionPercentile(0.95f) * 100f:F1} cm; rms {d.RmsMeters * 100f:F1} cm, {d.RmsDegrees:F2}°; " +
                $"end {run.EndPositionError * 100f:F1} cm; odometry alone would be {run.OdometryOnlyEndError * 100f:F1} cm; " +
                $"scans inserted {run.Slam.ScansInserted}, submaps {run.Slam.Submaps.Count}; {run.RealSeconds:F1} s real" +
                (run.Global != null
                    ? $"\n    loop closure: {run.Global.Searches} searches, {run.Global.LoopLinks} links, {run.Global.Optimisations} solves; " +
                      $"aligned (best-fit shift and rotation removed): worst {run.Aligned().max * 100f:F1} cm, rms {run.Aligned().rms * 100f:F1} cm, rotation {run.Aligned().rotationDegrees:+0.00;-0.00}°"
                    : ""));
        }
    }

    public class SubmapMatcherTests
    {
        static ProbabilityGrid DockSubmap(SlamWorld world, RandomProvider random, Vector2 dock)
        {
            var grid = new ProbabilityGrid(dock, 0.05f);
            for (int k = 0; k < 30; k++)
                grid.Insert(new RobotPose(dock, 12f * k), world.Scan(new RobotPose(dock, 12f * k), random).lidarRays, 5f,
                    LocalSlamConstants.LoopClosurePointRangeMeters, 1f, null);
            return grid;
        }

        static List<Vector2> Points(SensorFrame frame)
        {
            var list = new List<Vector2>();
            foreach (var ray in frame.lidarRays)
                if (ray.hit && ray.range <= LocalSlamConstants.LoopClosurePointRangeMeters) list.Add(RobotPose.DirectionOf(ray.bearing) * ray.range);
            return list;
        }

        [Test]
        public void BranchAndBound_FindsExactlyWhatScoringEveryPoseFinds()
        {
            var world = SlamWorld.Apartment();
            var dock = new Vector2(-2f, -1f);
            var grid = DockSubmap(world, new RandomProvider(4), dock);
            var rng = new System.Random(4);
            var matcher = new SubmapMatcher();

            for (int t = 0; t < 8; t++)
            {
                var truth = new RobotPose(dock + new Vector2((float)(rng.NextDouble() * 0.6 - 0.3), (float)(rng.NextDouble() * 0.6 - 0.3)),
                    (float)(rng.NextDouble() * 360 - 180));
                var guess = new RobotPose(truth.position + new Vector2((float)(rng.NextDouble() * 0.4 - 0.2), (float)(rng.NextDouble() * 0.4 - 0.2)),
                    truth.heading + (float)(rng.NextDouble() * 10 - 5));
                var points = Points(world.Scan(truth, new RandomProvider((uint)(100 + t))));

                bool fast = matcher.Search(grid, points, guess, GlobalSlamConstants.SearchWindowMeters, GlobalSlamConstants.SearchWindowDegrees,
                    0.3f, exhaustive: false, out var a);
                float fastScore = matcher.LastScore;
                int scored = matcher.LastScored, window = matcher.LastWindowSize;
                bool slow = matcher.Search(grid, points, guess, GlobalSlamConstants.SearchWindowMeters, GlobalSlamConstants.SearchWindowDegrees,
                    0.3f, exhaustive: true, out var b);

                Assert.AreEqual(slow, fast);
                Assert.AreEqual(matcher.LastScore, fastScore, 1e-5f, $"trial {t}: the same best score");
                TestContext.WriteLine($"trial {t}: score {fastScore:F3}; branch and bound scored {scored} of {window} poses; " +
                                      $"found {Vector2.Distance(a.position, truth.position) * 100f:F1} cm, {Mathf.DeltaAngle(a.heading, truth.heading):+0.0;-0.0}° from the truth");
            }
        }

        [Test]
        public void ARevisit_IsFoundAndRefinedCloseToTheTruth()
        {
            var world = SlamWorld.Apartment();
            var dock = new Vector2(-2f, -1f);
            var grid = DockSubmap(world, new RandomProvider(6), dock);
            var truth = new RobotPose(dock + new Vector2(0.25f, -0.1f), 63f);
            var guess = new RobotPose(truth.position + new Vector2(-0.18f, 0.12f), truth.heading - 4f); // 22 cm, 4° off
            var points = Points(world.Scan(truth, new RandomProvider(7)));

            var matcher = new SubmapMatcher();
            Assert.IsTrue(matcher.Match(grid, points, guess, out var found), $"score {matcher.LastScore:F3}");
            float off = Vector2.Distance(found.position, truth.position), turn = Mathf.Abs(Mathf.DeltaAngle(found.heading, truth.heading));
            TestContext.WriteLine($"score {matcher.LastScore:F3}; {off * 100f:F2} cm, {turn:F2}° from the truth");
            Assert.LessOrEqual(off, 0.02f);
            Assert.LessOrEqual(turn, 0.6f);
        }

        [Test]
        public void ABlankPlace_IsNotMatched()
        {
            var world = SlamWorld.Apartment();
            var dock = new Vector2(-2f, -1f);
            var grid = DockSubmap(world, new RandomProvider(8), dock);
            // A scan from a different world: nothing in this submap looks like it.
            var points = Points(SlamWorld.Corridor().Scan(new RobotPose(Vector2.zero, 0f), null));
            Assert.IsFalse(new SubmapMatcher().Match(grid, points, new RobotPose(dock, 0f), out _));
        }
    }

    public class PoseGraphTests
    {
        [Test]
        public void TwoPoses_TheSecondMovesToWhereTheLinkSays()
        {
            var poses = new List<GraphPose> { new GraphPose(0, 0, 0), new GraphPose(0.5, 0.1, 10) };
            var links = new List<GraphLink> { new GraphLink(0, 1, new GraphPose(1, 0, 30), 0.01, 0.5, false) };

            new PoseGraph().Optimise(poses, links);

            Assert.AreEqual(0.0, poses[0].x, 1e-6, "pose 0 is fixed");
            Assert.AreEqual(1.0, poses[1].x, 1e-6);
            Assert.AreEqual(0.0, poses[1].y, 1e-6);
            Assert.AreEqual(30.0, poses[1].heading, 1e-5);
        }

        [Test]
        public void AWrongLoopLink_IsLargelyIgnored_WithTheHuberLoss()
        {
            // A straight chain of 6 poses 1 m apart, with right loop links from pose 0 to each, and one wrong one
            // claiming pose 5 is 30 cm to the side.
            var truth = new List<GraphPose>();
            for (int i = 0; i < 6; i++) truth.Add(new GraphPose(i, 0, 0));
            var links = new List<GraphLink>();
            for (int i = 1; i < 6; i++) links.Add(new GraphLink(i - 1, i, GraphPose.Relative(truth[i - 1], truth[i]), 0.02, 0.4, false));
            for (int i = 2; i < 6; i++) links.Add(new GraphLink(0, i, GraphPose.Relative(truth[0], truth[i]), 0.01, 0.3, true));
            links.Add(new GraphLink(0, 5, new GraphPose(5, 0.3, 0), 0.01, 0.3, true));

            var plain = new List<GraphPose>(truth);
            new PoseGraph().Optimise(plain, links);
            var robust = new List<GraphPose>(truth);
            new PoseGraph { HuberSigmas = GlobalSlamConstants.HuberSigmas }.Optimise(robust, links);

            TestContext.WriteLine($"pose 5 pulled sideways: {plain[5].y * 100:F1} cm plain, {robust[5].y * 100:F1} cm with the Huber loss");
            Assert.Less(System.Math.Abs(robust[5].y), System.Math.Abs(plain[5].y) * 0.5);
        }

        [Test]
        public void RelativeAndCompose_AreInverses()
        {
            var a = new GraphPose(1.2, -0.7, 135);
            var b = new GraphPose(-0.4, 2.1, -100);
            var back = GraphPose.Compose(a, GraphPose.Relative(a, b));
            Assert.AreEqual(b.x, back.x, 1e-9);
            Assert.AreEqual(b.y, back.y, 1e-9);
            Assert.AreEqual(0.0, GraphPose.WrapDegrees(b.heading - back.heading), 1e-9);
        }

        [Test]
        public void ASquareLoop_TheClosureRemovesTheDrift()
        {
            // 12 poses round a 1 m square; the odometry links each turn 1° too far; one link says the last pose is
            // back at the first.
            var truth = new List<GraphPose>();
            for (int i = 0; i < 12; i++)
            {
                int side = i / 3, along = i % 3;
                double x = side switch { 0 => along / 3.0, 1 => 1, 2 => 1 - along / 3.0, _ => 0 };
                double y = side switch { 0 => 0, 1 => along / 3.0, 2 => 1, _ => 1 - along / 3.0 };
                truth.Add(new GraphPose(x, y, side * 90));
            }

            var links = new List<GraphLink>();
            var poses = new List<GraphPose> { truth[0] };
            for (int i = 1; i < 12; i++)
            {
                var z = GraphPose.Relative(truth[i - 1], truth[i]);
                z.heading += 1.0;
                links.Add(new GraphLink(i - 1, i, z, 0.01, 0.5, false));
                poses.Add(GraphPose.Compose(poses[i - 1], z));
            }
            double before = Distance(poses[11], truth[11]);

            links.Add(new GraphLink(0, 11, GraphPose.Relative(truth[0], truth[11]), 0.01, 0.3, true));
            new PoseGraph().Optimise(poses, links);

            double after = Distance(poses[11], truth[11]);
            TestContext.WriteLine($"last pose: {before * 100:F1} cm off before, {after * 100:F2} cm after");
            Assert.Less(after, before * 0.2);
        }

        [Test]
        public void TheSparseSolve_MatchesADenseSolve()
        {
            // A chain with random revisit links, solved by PoseGraph and by plain dense Gauss-Newton.
            var random = new System.Random(5);
            int n = 40;
            var poses = new List<GraphPose>();
            var links = new List<GraphLink>();
            for (int i = 0; i < n; i++)
                poses.Add(new GraphPose(random.NextDouble() * 3, random.NextDouble() * 2, random.NextDouble() * 360 - 180));
            for (int i = 1; i < n; i++)
                links.Add(new GraphLink(i - 1, i, Jitter(GraphPose.Relative(poses[i - 1], poses[i]), random), 0.02, 0.8, false));
            for (int k = 0; k < 15; k++)
            {
                int a = random.Next(n), b = random.Next(n);
                if (a == b) continue;
                links.Add(new GraphLink(Math.Min(a, b), Math.Max(a, b),
                    Jitter(GraphPose.Relative(poses[Math.Min(a, b)], poses[Math.Max(a, b)]), random), 0.02, 0.6, true));
            }

            var sparse = new List<GraphPose>(poses);
            var dense = new List<GraphPose>(poses);
            new PoseGraph().Optimise(sparse, links);
            DenseGaussNewton(dense, links);

            for (int i = 0; i < n; i++)
            {
                Assert.AreEqual(dense[i].x, sparse[i].x, 1e-6, $"pose {i} x");
                Assert.AreEqual(dense[i].y, sparse[i].y, 1e-6, $"pose {i} y");
                Assert.AreEqual(0.0, GraphPose.WrapDegrees(dense[i].heading - sparse[i].heading), 1e-5, $"pose {i} heading");
            }
        }

        static GraphPose Jitter(GraphPose p, System.Random random) =>
            new GraphPose(p.x + (random.NextDouble() - 0.5) * 0.04, p.y + (random.NextDouble() - 0.5) * 0.04,
                p.heading + (random.NextDouble() - 0.5) * 2);

        static double Distance(GraphPose a, GraphPose b) => Math.Sqrt((a.x - b.x) * (a.x - b.x) + (a.y - b.y) * (a.y - b.y));

        /// <summary>The same Gauss-Newton as PoseGraph, with the normal equations solved densely (partial pivoting).</summary>
        static void DenseGaussNewton(List<GraphPose> poses, List<GraphLink> links)
        {
            int n = poses.Count, m = 3 * n;
            var A = new double[9];
            var B = new double[9];
            var e = new double[3];
            for (int iteration = 0; iteration < PoseGraphConstants.MaxIterations; iteration++)
            {
                var H = new double[m, m];
                var rhs = new double[m];
                foreach (var link in links)
                {
                    int i = link.from, j = link.to;
                    PoseGraph.LinkTerms(poses[i], poses[j], link, A, B, e);
                    double sh = link.sigmaDegrees * Math.PI / 180.0;
                    var omega = new[] { 1 / (link.sigmaMeters * link.sigmaMeters), 1 / (link.sigmaMeters * link.sigmaMeters), 1 / (sh * sh) };
                    var J = new[] { A, B };
                    var at = new[] { i, j };
                    for (int u = 0; u < 2; u++)
                        for (int v = 0; v < 2; v++)
                            for (int r = 0; r < 3; r++)
                                for (int c = 0; c < 3; c++)
                                {
                                    double sum = 0;
                                    for (int k = 0; k < 3; k++) sum += J[u][k * 3 + r] * omega[k] * J[v][k * 3 + c];
                                    H[3 * at[u] + r, 3 * at[v] + c] += sum;
                                }
                    for (int u = 0; u < 2; u++)
                        for (int r = 0; r < 3; r++)
                        {
                            double sum = 0;
                            for (int k = 0; k < 3; k++) sum += J[u][k * 3 + r] * omega[k] * e[k];
                            rhs[3 * at[u] + r] -= sum;
                        }
                }
                for (int d = 0; d < 3; d++) H[d, d] += PoseGraphConstants.FixedPoseWeight;

                var x = SolveDense(H, rhs);
                double largest = 0;
                for (int i = 0; i < n; i++)
                {
                    var p = poses[i];
                    p.x += x[3 * i];
                    p.y += x[3 * i + 1];
                    p.heading = GraphPose.WrapDegrees(p.heading + x[3 * i + 2] * 180.0 / Math.PI);
                    poses[i] = p;
                    for (int k = 0; k < 3; k++) largest = Math.Max(largest, Math.Abs(x[3 * i + k]));
                }
                if (largest < PoseGraphConstants.ConvergedStep) break;
            }
        }

        static double[] SolveDense(double[,] H, double[] b)
        {
            int m = b.Length;
            var a = (double[,])H.Clone();
            var x = (double[])b.Clone();
            for (int col = 0; col < m; col++)
            {
                int pivot = col;
                for (int r = col + 1; r < m; r++)
                    if (Math.Abs(a[r, col]) > Math.Abs(a[pivot, col])) pivot = r;
                if (pivot != col)
                {
                    for (int c = 0; c < m; c++) (a[col, c], a[pivot, c]) = (a[pivot, c], a[col, c]);
                    (x[col], x[pivot]) = (x[pivot], x[col]);
                }
                for (int r = col + 1; r < m; r++)
                {
                    double f = a[r, col] / a[col, col];
                    if (f == 0) continue;
                    for (int c = col; c < m; c++) a[r, c] -= f * a[col, c];
                    x[r] -= f * x[col];
                }
            }
            for (int r = m - 1; r >= 0; r--)
            {
                double sum = x[r];
                for (int c = r + 1; c < m; c++) sum -= a[r, c] * x[c];
                x[r] = sum / a[r, r];
            }
            return x;
        }
    }

    /// <summary>A world of axis-aligned solid rectangles, with an exact lidar ray cast.</summary>
    sealed class SlamWorld
    {
        public readonly Rect[] solids;

        /// <summary>Rotated walls: centre, half size, angle (degrees, counter-clockwise).</summary>
        public readonly (Vector2 centre, Vector2 half, float angle)[] slanted;

        // Where the harness starts and where it may send the robot. The defaults are the apartment's (kept as doubles so
        // the goal sequence, and so every earlier result, is unchanged).
        public Vector2 Dock = new Vector2(-2f, -1f);
        public double GoalMinX = -2.8, GoalWidth = 5.6, GoalMinY = -1.8, GoalHeight = 3.6;
        public float GoalMargin = 0.3f, PathMargin = 0.2f;
        public Func<Vector2, bool> Inside; // null = anywhere free counts as inside the home

        public SlamWorld(Rect[] solids) : this(solids, new (Vector2, Vector2, float)[0]) { }

        public SlamWorld(Rect[] solids, (Vector2 centre, Vector2 half, float angle)[] slanted)
        {
            this.solids = solids;
            this.slanted = slanted;
        }

        /// <summary>
        /// The apartment with diagonal walls added (as in the Sample apartment level): a 45° wall across the left room's
        /// top-left corner and a 30° wall in the right room.
        /// </summary>
        public static SlamWorld AngledApartment()
        {
            var a = Apartment();
            return new SlamWorld(a.solids, new[]
            {
                (new Vector2(-2.6f, 1.6f), new Vector2(0.6f, 0.05f), 45f),
                (new Vector2(1.9f, 1.45f), new Vector2(0.7f, 0.05f), -30f),
            });
        }

        static Vector2 ToLocal(Vector2 p, Vector2 centre, float angle)
        {
            float r = -angle * Mathf.Deg2Rad, c = Mathf.Cos(r), s = Mathf.Sin(r);
            Vector2 d = p - centre;
            return new Vector2(c * d.x - s * d.y, s * d.x + c * d.y);
        }

        static Vector2 DirToLocal(Vector2 d, float angle)
        {
            float r = -angle * Mathf.Deg2Rad, c = Mathf.Cos(r), s = Mathf.Sin(r);
            return new Vector2(c * d.x - s * d.y, s * d.x + c * d.y);
        }

        /// <summary>This world mirrored left to right (x → −x).</summary>
        public SlamWorld MirroredX()
        {
            var m = new Rect[solids.Length];
            for (int i = 0; i < solids.Length; i++) m[i] = Rect.MinMaxRect(-solids[i].xMax, solids[i].yMin, -solids[i].xMin, solids[i].yMax);
            var sl = new (Vector2 centre, Vector2 half, float angle)[slanted.Length];
            for (int i = 0; i < slanted.Length; i++) sl[i] = (new Vector2(-slanted[i].centre.x, slanted[i].centre.y), slanted[i].half, -slanted[i].angle);
            return new SlamWorld(m, sl);
        }

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

        /// <summary>Folder of level files the harness can load (Assets/Tests/EditMode/Levels).</summary>
        public static string LevelFolder => Path.Combine(Application.dataPath, "Tests", "EditMode", "Levels");

        /// <summary>Eric's "Enclosed Test" level (Levels/EnclosedTest.json): narrow corridor start, four rooms, four doorways.</summary>
        public static SlamWorld EnclosedTest() => FromLevelFile("EnclosedTest.json");

        /// <summary>Eric's "My Room 01" level (Levels/MyRoom01.json): one open 9 m room with side rooms, no corridor.</summary>
        public static SlamWorld MyRoom01() => FromLevelFile("MyRoom01.json");

        public static SlamWorld FromLevelFile(string fileName)
        {
            string path = Path.Combine(LevelFolder, fileName);
            Assert.IsTrue(File.Exists(path), $"level file missing: {path}");
            return FromLevelJson(File.ReadAllText(path));
        }

        /// <summary>
        /// A world built the way the live sim builds its colliders (LevelRenderer): every wall run from
        /// LevelGeometry.CollectWallSegments (doorways already cut) as a box of the level's wall thickness centred on the
        /// edge, furniture that blocks the vacuum as a solid box, pass-under furniture as its four corner legs, cats left
        /// out. The dock is the level's robot spawn; goals are drawn inside the rooms' outlines.
        /// </summary>
        public static SlamWorld FromLevelJson(string json)
        {
            var snapshot = LevelSnapshot.Parse(json);
            Assert.IsNotNull(snapshot, "the level file did not parse");
            var level = ScriptableObject.CreateInstance<LevelData>();
            level.hideFlags = HideFlags.DontSave;
            try
            {
                snapshot.ApplyTo(level);
                var boxes = new List<(Vector2 centre, Vector2 half, float angle)>();
                float half = level.WallThickness * 0.5f;
                foreach (var wall in LevelGeometry.CollectWallSegments(level))
                {
                    Vector2 d = wall.b - wall.a;
                    float length = d.magnitude;
                    if (length < 1e-4f) continue;
                    boxes.Add(((wall.a + wall.b) * 0.5f, new Vector2(length * 0.5f, half), Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg));
                }

                const float PassableLegSize = 0.08f; // LevelRenderer.PassableLegSize
                foreach (var o in level.Obstacles)
                {
                    if (o == null || o.kind == ObstacleKind.Cat) continue;
                    if (o.blocksVacuum)
                    {
                        boxes.Add((o.center, o.size * 0.5f, o.rotation));
                        continue;
                    }
                    float leg = Mathf.Min(PassableLegSize, o.size.x * 0.5f, o.size.y * 0.5f);
                    float hx = o.size.x * 0.5f - leg * 0.5f, hy = o.size.y * 0.5f - leg * 0.5f;
                    float r = o.rotation * Mathf.Deg2Rad, c = Mathf.Cos(r), s = Mathf.Sin(r);
                    for (int xs = -1; xs <= 1; xs += 2)
                        for (int ys = -1; ys <= 1; ys += 2)
                        {
                            var l = new Vector2(xs * hx, ys * hy);
                            boxes.Add((o.center + new Vector2(c * l.x - s * l.y, s * l.x + c * l.y), Vector2.one * (leg * 0.5f), o.rotation));
                        }
                }

                var outlines = level.Rooms.Where(room => room != null && room.IsValid).Select(room => new List<Vector2>(room.outline)).ToList();
                float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
                foreach (var outline in outlines)
                    foreach (var p in outline)
                    {
                        minX = Mathf.Min(minX, p.x); maxX = Mathf.Max(maxX, p.x);
                        minY = Mathf.Min(minY, p.y); maxY = Mathf.Max(maxY, p.y);
                    }

                return new SlamWorld(new Rect[0], boxes.ToArray())
                {
                    Dock = level.RobotSpawn,
                    GoalMinX = minX, GoalWidth = maxX - minX, GoalMinY = minY, GoalHeight = maxY - minY,
                    // The start corridor is 0.68 m clear between wall faces: the apartment's 0.3 m goal margin would leave
                    // an 8 cm band. Robot radius is about 0.17 m.
                    GoalMargin = 0.22f, PathMargin = 0.18f,
                    Inside = p => outlines.Any(outline => Poly2D.ContainsPoint(outline, p)),
                };
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(level);
            }
        }

        public bool IsFree(Vector2 p, float margin)
        {
            foreach (var s in solids)
                if (p.x > s.xMin - margin && p.x < s.xMax + margin && p.y > s.yMin - margin && p.y < s.yMax + margin) return false;
            foreach (var (centre, half, angle) in slanted)
            {
                Vector2 l = ToLocal(p, centre, angle);
                if (Mathf.Abs(l.x) < half.x + margin && Mathf.Abs(l.y) < half.y + margin) return false;
            }
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
            foreach (var (centre, half, angle) in slanted)
            {
                Vector2 o = ToLocal(origin, centre, angle), d = DirToLocal(direction, angle);
                float tMin = 0f, tMax = float.MaxValue;
                if (!Slab(o.x, d.x, -half.x, half.x, ref tMin, ref tMax)) continue;
                if (!Slab(o.y, d.y, -half.y, half.y, ref tMin, ref tMax)) continue;
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
        public SensorFrame Scan(RobotPose pose, RandomProvider random, float range = 8f,
            bool bearingNoise = true, bool rangeNoise = true, bool dropouts = true, float rangeNoiseScale = 1f, bool flatRangeNoise = false)
        {
            var frame = new SensorFrame();
            frame.Clear();
            frame.hasLidarScan = true;
            frame.lidarRange = range;

            for (int i = 0; i < 360; i++)
            {
                float bearing = i <= 180 ? i : i - 360;
                float beamNoise = random != null ? Gauss(random, SensorNoiseProfile.LidarBearingSigmaDegrees) : 0f;
                float beam = bearingNoise ? bearing + beamNoise : bearing;
                float trueRange = Mathf.Min(range, Cast(pose.position, RobotPose.DirectionOf(pose.heading + beam)));
                bool hit = trueRange < range;
                float measured = trueRange;

                if (random != null && hit)
                {
                    float miss = Mathf.Min(SensorNoiseProfile.LidarMissChanceCap,
                        SensorNoiseProfile.LidarMissChanceAtOneMeter + SensorNoiseProfile.LidarMissChancePerMeter * Mathf.Max(0f, trueRange - 1f));
                    if (random.NextDouble(RandomProvider.Subsystem.SensorNoise) < miss && dropouts)
                    {
                        hit = false;
                        measured = range;
                    }
                    else
                    {
                        float sigma = SensorNoiseProfile.LidarRangeSigmaMeters + (flatRangeNoise ? 0f : SensorNoiseProfile.LidarRangeSigmaFraction * trueRange);
                        float rangeError = Gauss(random, sigma * rangeNoiseScale);
                        measured = Mathf.Clamp(trueRange + (rangeNoise ? rangeError : 0f), 0f, range);
                    }
                }
                frame.lidarRays.Add(new LidarRay { bearing = bearing, range = measured, hit = hit });
            }
            return frame;
        }

        static float Gauss(RandomProvider random, float sigma) =>
            (float)random.NextGaussian(RandomProvider.Subsystem.SensorNoise, 0.0, sigma);

    }

    /// <summary>Drives a robot around <see cref="SlamWorld.Apartment"/> and runs the real SLAM pipeline on it.</summary>
    sealed class SlamHarness
    {
        const float Dt = 0.02f;
        const float DriveSpeed = 0.65f;
        const float TurnSpeed = 320f;

        readonly SlamWorld world;
        readonly InternalModelWriter writer;
        readonly RandomProvider random;
        readonly System.Random goals;
        readonly OdometrySensor odometry = new OdometrySensor();
        readonly LocalSlam slam;
        readonly GlobalSlam globalSlam;
        readonly ObservationMapper mapper;
        RobotPose localPose;
        readonly List<(Vector2 believed, Vector2 truth)> track = new List<(Vector2, Vector2)>();

        RobotPose truth, believed, deadReckoned;

        public readonly DriftStats Drift = new DriftStats();
        public float EndPositionError, OdometryOnlyEndError, RealSeconds;
        public RobotPose EndBelieved, EndTrue;

        // Diagnostic switches (defaults = a normal run).
        public float SpinSign = 1f;       // +1 counter-clockwise start spin, −1 clockwise, 0 none
        public float OdometryNoise = 1f;  // odometry noise scale
        public bool LidarNoise = true;    // false = exact lidar
        public bool BearingNoise = true, RangeNoise = true, Dropouts = true; // each part of the lidar noise
        public float SpinSpeed = S.StartSpinDegreesPerSecond; // the start spin's speed (°/s)
        public bool OdometryTurnNoise = true; // false = odometry reports the true turn (distance noise unchanged)
        public float RangeNoiseScale = 1f;    // the lidar range noise × this
        public float HoldSeconds;
        public bool InsertAtTruth;            // the map gets each scan at the TRUE pose (tracking still fitted): no pose errors written into the map
        public bool InsertExactScans;
        public double GlobalMs;               // time spent in global SLAM (loop search, solves, rebuilds)

        // Diagnostics: one record per scan added to the map (Diagnose_ErrorGrowth).
        public bool RecordInsertions;
        RobotPose lastInsertTruth;

        /// <summary>Diagnostics: called every step with the believed and true pose and the time.</summary>
        public Action<RobotPose, RobotPose, float> OnStep;

        /// <summary>Diagnostics: called after every scan match with local SLAM's fitted pose and the true pose, and the time.</summary>
        public Action<RobotPose, RobotPose, float> OnMatch;
        public readonly List<(float t, Vector2 error, float headingError, float trueMove, float trueTurn, float unseen, int submapScans)> Insertions =
            new List<(float, Vector2, float, float, float, float, int)>();         // with InsertAtTruth: the map gets an EXACT scan, i.e. a perfect map of what has been seen             // stand still at the dock this long first, adding every scan at the known dock pose
        public bool FlatRangeNoise;           // true = range noise σ is the fixed part only (no % of range)

        // Keyframe diagnostics (claude/SLAM_Certain_Anchor_Design.md, keyframe stage 0): < 0 = off (as now). ≥ 0 = the
        // scan added to the map is a second scan of the same true pose with its range noise × this (0 = exact),
        // added at the TRACKED pose; tracking still fits the ordinary scan. Stands in for an averaged keyframe.
        public float InsertRangeNoiseScale = -1f;


        /// <summary>Diagnostics: odometry alone (dead reckoning from the dock), this step.</summary>
        public RobotPose DeadReckoned => deadReckoned;

        // Heading error (believed − true, °) after the start spin and at 60 s.
        public float HeadingAfterSpin = float.NaN, HeadingAt60 = float.NaN;

        public LocalSlam Slam => slam;

        /// <summary>Every 60 s: the believed − true position (x, y in cm) and heading (°), and how far the robot is from the dock.</summary>
        public readonly List<string> Checkpoints = new List<string>();

        /// <summary>Global SLAM (loop closure), or null when it is off.</summary>
        public GlobalSlam Global => globalSlam;

        // Diagnostics: run a second Internal Model redrawn by the original (reference) redraw, and compare after each redraw.
        public bool CompareRedraw;
        InternalModelWriter referenceWriter;
        ObservationMapper referenceMapper;
        public int Redraws, RedrawMismatches;
        public double RedrawMsTotal, RedrawMsMax, ReferenceRedrawMsTotal, ReferenceRedrawMsMax;

        readonly Vector2 dockPosition;
        public bool MirrorGoals; // goals mirrored in x (with SlamWorld.MirroredX and a mirrored dock: the mirror image of a run)

        public SlamHarness(int seed, SlamWorld world = null, bool loopClosure = true, Vector2? dock = null)
        {
            this.world = world ?? SlamWorld.Apartment();
            dockPosition = dock ?? this.world.Dock;
            random = new RandomProvider((uint)seed);
            goals = new System.Random(seed);
            truth = believed = deadReckoned = new RobotPose(dockPosition, 0f);

            writer = new InternalModelWriter(new InternalModelGrid(truth.position, 0.05f));
            slam = new LocalSlam(truth.position, 0.05f, PoseCorrectionQuality.Balanced, 1f);
            globalSlam = loopClosure ? new GlobalSlam(slam) : null;
            mapper = new ObservationMapper(writer, slam: true, slam, globalSlam);
            slam.Begin(believed);
            globalSlam?.Begin();
            localPose = believed;
            odometry.Reset(truth.position, truth.heading);
        }

        public SlamHarness Run(float seconds)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var redrawClock = new System.Diagnostics.Stopwatch();
            if (CompareRedraw && globalSlam != null && referenceMapper == null)
            {
                referenceWriter = new InternalModelWriter(new InternalModelGrid(dockPosition, 0.05f));
                referenceMapper = new ObservationMapper(referenceWriter, slam: true, slam, globalSlam);
            }
            float t = 0f;
            int step = 0;
            float spinLeft = SpinSign != 0f ? S.StartSpinDegrees : 0f;
            Vector2? goal = null;
            float previousTrueHeading = truth.heading;
            RobotPose dockPose = localPose;
            lastInsertTruth = truth;
            var globalClock = new System.Diagnostics.Stopwatch();
            bool spinning = spinLeft > 0f;

            while (t < seconds)
            {
                t += Dt;

                // ---- the true motion ----
                bool holding = t <= HoldSeconds;
                if (holding)
                {
                    // standing still at the dock
                }
                else if (spinLeft > 0f)
                {
                    float turn = SpinSpeed * Dt;
                    truth = new RobotPose(truth.position, truth.heading + SpinSign * turn);
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
                odometry.Measure(truth.position, truth.heading, 1f, slam: true, OdometryNoise, random, out float forward, out float turned);
                if (!OdometryTurnNoise) turned = Mathf.DeltaAngle(previousTrueHeading, truth.heading);
                previousTrueHeading = truth.heading;
                localPose = Advance(localPose, forward, turned);
                deadReckoned = Advance(deadReckoned, forward, turned);

                var frame = step % 5 == 0 ? world.Scan(truth, LidarNoise ? random : null, 8f, BearingNoise, RangeNoise, Dropouts, RangeNoiseScale, FlatRangeNoise) : NoScan;
                // Diagnostics: the share of this scan's mapped returns (≤ 3 m) landing in cells the active submaps haven't seen.
                float unseen = 0f;
                if (RecordInsertions && frame.hasLidarScan && slam.ActiveSubmaps.Count > 0)
                {
                    int total = 0, fresh = 0;
                    var grid = slam.ActiveSubmaps[0].grid;
                    foreach (var ray in frame.lidarRays)
                    {
                        if (!ray.hit || ray.range > S.MaxInsertRangeMeters) continue;
                        Vector2 end = localPose.position + RobotPose.DirectionOf(localPose.heading + ray.bearing) * ray.range;
                        total++;
                        if (!grid.TryCell(end, out int r, out int c) || slam.Probability(r, c) <= 0f) fresh++;
                    }
                    unseen = total > 0 ? fresh / (float)total : 1f;
                }
                else if (RecordInsertions) unseen = 1f;

                int matchesBefore = slam.Matches;
                if (holding) localPose = slam.TrackStationary(frame, dockPose, t);
                else if (InsertAtTruth && frame.hasLidarScan)
                    localPose = slam.Track(frame, localPose, t, truth, InsertExactScans ? world.Scan(truth, null) : frame);
                else if (InsertRangeNoiseScale >= 0f && frame.hasLidarScan)
                    localPose = slam.Track(frame, localPose, t, null,
                        InsertRangeNoiseScale == 0f ? world.Scan(truth, null)
                                                    : world.Scan(truth, random, 8f, BearingNoise, RangeNoise, Dropouts, RangeNoiseScale * InsertRangeNoiseScale, FlatRangeNoise));
                else localPose = slam.Track(frame, localPose, t);
                if (OnMatch != null && slam.Matches > matchesBefore) OnMatch(localPose, truth, t);
                globalClock.Restart();
                bool redraw = globalSlam != null && globalSlam.AfterTrack(frame);
                GlobalMs += globalClock.Elapsed.TotalMilliseconds;
                believed = globalSlam != null ? globalSlam.ToGlobal(localPose) : localPose;
                if (RecordInsertions && slam.InsertedThisStep)
                {
                    float move = Vector2.Distance(truth.position, lastInsertTruth.position);
                    float turnSince = Mathf.Abs(Mathf.DeltaAngle(lastInsertTruth.heading, truth.heading));
                    int submapScans = slam.ActiveSubmaps.Count > 0 ? slam.ActiveSubmaps[slam.ActiveSubmaps.Count - 1].grid.ScanCount : 0;
                    Insertions.Add((t, believed.position - truth.position, Mathf.DeltaAngle(truth.heading, believed.heading), move, turnSince, unseen, submapScans));
                    lastInsertTruth = truth;
                }
                if (redraw)
                {
                    redrawClock.Restart();
                    mapper.RedrawFromSubmaps();
                    double ms = redrawClock.Elapsed.TotalMilliseconds;
                    Redraws++;
                    RedrawMsTotal += ms;
                    RedrawMsMax = System.Math.Max(RedrawMsMax, ms);
                    if (referenceMapper != null)
                    {
                        redrawClock.Restart();
                        referenceMapper.RedrawFromSubmapsReference();
                        ms = redrawClock.Elapsed.TotalMilliseconds;
                        ReferenceRedrawMsTotal += ms;
                        ReferenceRedrawMsMax = System.Math.Max(ReferenceRedrawMsMax, ms);
                        RedrawMismatches += CountDifferences(writer.Grid, referenceWriter.Grid);
                    }
                }
                if (frame.hasLidarScan)
                {
                    mapper.Apply(frame, believed);
                    referenceMapper?.Apply(frame, believed);
                }
                step++;
                if (step % 10 == 0) track.Add((believed.position, truth.position));

                Drift.Add(Vector2.Distance(believed.position, truth.position), Mathf.Abs(Mathf.DeltaAngle(believed.heading, truth.heading)));
                OnStep?.Invoke(believed, truth, t);
                if (spinning && !holding && spinLeft <= 0f)
                {
                    spinning = false;
                    HeadingAfterSpin = Mathf.DeltaAngle(truth.heading, believed.heading);
                }
                if (step == 3000) HeadingAt60 = Mathf.DeltaAngle(truth.heading, believed.heading);
                if (step % 3000 == 0)
                {
                    Vector2 e = (believed.position - truth.position) * 100f;
                    Checkpoints.Add($"t {t:F0}: ({e.x:+0.0;-0.0}, {e.y:+0.0;-0.0}) cm, {Mathf.DeltaAngle(truth.heading, believed.heading):+0.00;-0.00}°, " +
                                    $"{Vector2.Distance(truth.position, dockPosition):F1} m from the dock");
                }
            }

            EndPositionError = Vector2.Distance(believed.position, truth.position);
            OdometryOnlyEndError = Vector2.Distance(deadReckoned.position, truth.position);
            EndBelieved = believed;
            EndTrue = truth;
            RealSeconds = (float)clock.Elapsed.TotalSeconds;
            return this;
        }

        static readonly SensorFrame NoScan = new SensorFrame();

        /// <summary>
        /// As <see cref="Aligned"/>, but the best fit may also scale the track: believed ≈ scale · R(rotation) · true + shift
        /// (least squares). Also the separate x and y scales of a general linear fit (believed ≈ A · true + shift), to show a
        /// shrink along one axis. Scale below 1 = the robot's map is smaller than the real home.
        /// </summary>
        public (float scale, float rotationDegrees, float max, float rms, float scaleX, float scaleY) AlignedWithScale()
        {
            if (track.Count < 3) return default;
            Vector2 mb = Vector2.zero, mt = Vector2.zero;
            foreach (var (b, t) in track) { mb += b; mt += t; }
            mb /= track.Count;
            mt /= track.Count;
            double dot = 0, cross = 0, tt2 = 0, sxx = 0, sxy = 0, syy = 0, bxtx = 0, bxty = 0, bytx = 0, byty = 0;
            foreach (var (b, t) in track)
            {
                Vector2 db = b - mb, dt = t - mt;
                dot += dt.x * db.x + dt.y * db.y;
                cross += dt.x * db.y - dt.y * db.x;
                tt2 += dt.x * dt.x + dt.y * dt.y;
                sxx += dt.x * dt.x; sxy += dt.x * dt.y; syy += dt.y * dt.y;
                bxtx += db.x * dt.x; bxty += db.x * dt.y; bytx += db.y * dt.x; byty += db.y * dt.y;
            }
            double angle = Math.Atan2(cross, dot), scale = Math.Sqrt(dot * dot + cross * cross) / tt2;
            double c = Math.Cos(angle) * scale, sn = Math.Sin(angle) * scale;
            double max = 0, sumSq = 0;
            foreach (var (b, t) in track)
            {
                Vector2 dt = t - mt;
                double fx = c * dt.x - sn * dt.y + mb.x, fy = sn * dt.x + c * dt.y + mb.y;
                double e = Math.Sqrt((b.x - fx) * (b.x - fx) + (b.y - fy) * (b.y - fy));
                max = Math.Max(max, e);
                sumSq += e * e;
            }
            // General linear fit A = (Σ db dtᵀ)(Σ dt dtᵀ)⁻¹: its diagonal gives the x and y scales.
            double det = sxx * syy - sxy * sxy;
            double a11 = (bxtx * syy - bxty * sxy) / det, a22 = (byty * sxx - bytx * sxy) / det;
            return ((float)scale, (float)(angle * 180 / Math.PI), (float)max, (float)Math.Sqrt(sumSq / track.Count), (float)a11, (float)a22);
        }

        /// <summary>
        /// How one wall surface is drawn in the Internal Model at the end of the run: walking from <paramref name="start"/>
        /// toward the wall (<paramref name="dir"/>, along x or y) at points spread along it, the first cell believed blocked
        /// (traversability below 0.5), at its centre. A straight line through those points gives the drawn wall's angle
        /// against the true one (degrees, counter-clockwise +) and its mean offset from the true surface (cm, along
        /// <paramref name="dir"/>'s axis, + = toward +x / +y).
        /// </summary>
        public (float angleDegrees, float offsetCm, int samples) DrawnWall(Vector2 start, Vector2 dir, float surface, float spanMin, float spanMax)
        {
            bool alongX = Mathf.Abs(dir.x) > 0.5f;
            var grid = writer.Grid;
            double n = 0, sumS = 0, sumC = 0, sumSS = 0, sumSC = 0;
            for (float s = spanMin; s <= spanMax + 1e-4f; s += 0.025f)
            {
                if (!alongX && dir.y < 0f && Mathf.Abs(s - 0.5f) < 0.25f) continue; // where the middle wall meets the bottom wall
                Vector2 from = alongX ? new Vector2(start.x, s) : new Vector2(s, start.y);
                for (float travelled = 0f; travelled < 1.2f; travelled += 0.0125f)
                {
                    Vector2 p = from + dir * travelled;
                    if (!world.IsFree(p, 0f) && Mathf.Abs((alongX ? p.x : p.y) - surface) > 0.15f) break; // something else in the way
                    if (!grid.TryWorldToGridIndex(p, out int row, out int col) || writer.Traversability(row, col) >= 0.5f) continue;
                    Vector2 centre = grid.GridIndexToWorldCenter(row, col);
                    double c = alongX ? centre.x : centre.y;
                    n++;
                    sumS += s;
                    sumC += c;
                    sumSS += s * s;
                    sumSC += s * c;
                    break;
                }
            }
            if (n < 10) return (float.NaN, float.NaN, (int)n);
            double slope = (n * sumSC - sumS * sumC) / (n * sumSS - sumS * sumS);
            // A counter-clockwise turn θ makes a wall along y lean by dx/dy = −tan θ, and a wall along x by dy/dx = +tan θ.
            double angle = (alongX ? -Math.Atan(slope) : Math.Atan(slope)) * 180.0 / Math.PI;
            return ((float)angle, (float)((sumC / n - surface) * 100.0), (int)n);
        }

        /// <summary>Cells whose traversability differs between two Internal Models (over both their extents).</summary>
        static int CountDifferences(InternalModelGrid a, InternalModelGrid b)
        {
            int minRow = Mathf.Min(a.MinRow, b.MinRow), minCol = Mathf.Min(a.MinCol, b.MinCol);
            int maxRow = Mathf.Max(a.MinRow + a.Rows, b.MinRow + b.Rows), maxCol = Mathf.Max(a.MinCol + a.Cols, b.MinCol + b.Cols);
            int differences = 0;
            for (int row = minRow; row < maxRow; row++)
                for (int col = minCol; col < maxCol; col++)
                    if (a.GetCell(row, col).traversabilityBelief != b.GetCell(row, col).traversabilityBelief) differences++;
            return differences;
        }

        /// <summary>
        /// The error left after removing one best-fit shift and rotation of the whole run (believed vs true positions,
        /// every 10 steps): how published SLAM results score themselves, since the global frame is arbitrary. For
        /// information only; pass/fail stays on the raw world-frame error.
        /// </summary>
        public (float max, float rms, float rotationDegrees) Aligned()
        {
            if (track.Count < 2) return default;
            Vector2 mb = Vector2.zero, mt = Vector2.zero;
            foreach (var (b, t) in track)
            {
                mb += b;
                mt += t;
            }
            mb /= track.Count;
            mt /= track.Count;
            double cross = 0, dot = 0;
            foreach (var (b, t) in track)
            {
                Vector2 tb = b - mb, tt = t - mt;
                cross += tt.x * tb.y - tt.y * tb.x;
                dot += tt.x * tb.x + tt.y * tb.y;
            }
            double angle = System.Math.Atan2(cross, dot), c = System.Math.Cos(angle), s = System.Math.Sin(angle);
            double max = 0, sumSq = 0;
            foreach (var (b, t) in track)
            {
                Vector2 tt = t - mt;
                double fx = c * tt.x - s * tt.y + mb.x, fy = s * tt.x + c * tt.y + mb.y;
                double e = System.Math.Sqrt((b.x - fx) * (b.x - fx) + (b.y - fy) * (b.y - fy));
                max = System.Math.Max(max, e);
                sumSq += e * e;
            }
            return ((float)max, (float)System.Math.Sqrt(sumSq / track.Count), (float)(angle * 180 / System.Math.PI));
        }

        static RobotPose Advance(RobotPose p, float forward, float turn) =>
            new RobotPose(p.position + RobotPose.DirectionOf(p.heading + turn * 0.5f) * forward, Mathf.DeltaAngle(0f, p.heading + turn));

        Vector2 PickGoal()
        {
            for (int tries = 0; tries < 1000; tries++)
            {
                var g = new Vector2((float)(goals.NextDouble() * world.GoalWidth + world.GoalMinX), (float)(goals.NextDouble() * world.GoalHeight + world.GoalMinY));
                if (MirrorGoals) g.x = -g.x;
                if ((world.Inside == null || world.Inside(g)) && world.IsFree(g, world.GoalMargin) && LineFree(truth.position, g)) return g;
            }
            return truth.position;
        }

        bool LineFree(Vector2 a, Vector2 b)
        {
            int n = Mathf.CeilToInt(Vector2.Distance(a, b) / 0.025f) + 1;
            for (int i = 0; i <= n; i++)
                if (!world.IsFree(Vector2.Lerp(a, b, (float)i / n), world.PathMargin)) return false;
            return true;
        }
    }
}
