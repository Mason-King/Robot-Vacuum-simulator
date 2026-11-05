using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
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
        public void TheHitFieldGradient_MatchesFiniteDifferences()
        {
            var world = SlamWorld.Apartment();
            var dock = new Vector2(-2f, -1f);
            var grid = DockSubmap(world, new RandomProvider(3), dock);
            var matcher = new ScanMatcher();
            matcher.Match(grid, new List<Vector2> { Vector2.up }, new RobotPose(dock, 0f), 0f, 0f); // binds the grid

            int checkedPoints = 0;
            foreach (var p in new[] { new Vector2(-2.97f, -1.4f), new Vector2(-1.88f, 0.73f), new Vector2(0.43f, -1.02f), new Vector2(-2.5f, -1.98f) })
            {
                double e = matcher.HitResidual(p.x, p.y, out double dx, out double dy);
                const double h = 1e-5;
                double fx = (matcher.HitResidual(p.x + h, p.y, out _, out _) - matcher.HitResidual(p.x - h, p.y, out _, out _)) / (2 * h);
                double fy = (matcher.HitResidual(p.x, p.y + h, out _, out _) - matcher.HitResidual(p.x, p.y - h, out _, out _)) / (2 * h);
                Assert.AreEqual(fx, dx, 1e-3 + 1e-3 * Math.Abs(fx), $"d/dx at {p}");
                Assert.AreEqual(fy, dy, 1e-3 + 1e-3 * Math.Abs(fy), $"d/dy at {p}");
                if (e < 0.99) checkedPoints++;
            }
            Assert.That(checkedPoints, Is.GreaterThan(0), "at least one probe should be near hits");
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
        /// Diagnostics, not a check: how thick are the walls in the robot's map, and what does each free-space / wall
        /// rule cost in drift? Seeds 1–6 × 300 s on the apartment with diagonal walls. About 5 minutes. Run it on its own.
        /// </summary>
        [Explicit, Test, Timeout(3_600_000)]
        public void Diagnose_WallThickness()
        {
            var variants = new (string name, float sigmas, float fromRange, float occupied)[]
            {
                ("current: 2σ margin", -1f, 0f, 0f),
                ("1σ margin", 1f, 0f, 0f),
                ("no margin", 0f, 0f, 0f),
                ("2σ margin beyond 1.5 m only", -1f, 1.5f, 0f),
                ("2σ margin, wall at 0.75 (6 hits)", -1f, 0f, 0.75f),
                ("1σ margin, wall at 0.75", 1f, 0f, 0.75f),
            };
            try
            {
                foreach (var (name, sigmas, fromRange, occupied) in variants)
                {
                    LocalSlam.MarginSigmasOverride = sigmas;
                    LocalSlam.MarginFromRangeMeters = fromRange;
                    LocalSlam.OccupiedOverride = occupied;

                    const int seeds = 6;
                    double worst = 0, p99 = 0, heading = 0, falseNear = 0, ghosts = 0, missed = 0, thickness = 0;
                    for (int seed = 1; seed <= seeds; seed++)
                    {
                        var run = new SlamHarness(seed, SlamWorld.AngledApartment()).Run(300f);
                        var map = run.MapAccuracy();
                        worst += run.Drift.MaxMeters;
                        p99 += run.Drift.PositionPercentile(0.99f);
                        heading += Mathf.Abs(Mathf.DeltaAngle(run.EndTrue.heading, run.EndBelieved.heading));
                        falseNear += map.falseNear;
                        ghosts += map.ghosts;
                        missed += map.missed;
                        thickness += map.thickness;
                    }
                    TestContext.WriteLine($"{name}: worst {worst / seeds * 100:F1} cm, p99 {p99 / seeds * 100:F1} cm, end heading {heading / seeds:F2}° | " +
                                          $"false wall near walls {falseNear / seeds:F0} cells, ghosts {ghosts / seeds:F0}, walls missed {missed / seeds:F0}, " +
                                          $"thickness {thickness / seeds:F2}× (means over {seeds} seeds)");
                }
            }
            finally
            {
                LocalSlam.MarginSigmasOverride = -1f;
                LocalSlam.MarginFromRangeMeters = 0f;
                LocalSlam.OccupiedOverride = 0f;
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
        /// Diagnostics, not a check: the hit/miss balance, the free-space margin and the submap cell size, tried together.
        /// For each setting: the map bias (walls drawn into the room, cm; the mean over the six wall surfaces and 8
        /// lattice offsets, as in <see cref="Diagnose_BiasSource"/>), the matching bias against such a map, and a
        /// 4-seed × 300 s run of the real pipeline (loop closure on): drift, and how the Internal Model's walls compare
        /// with the truth (not available with finer submap cells, which the Internal Model can't be drawn from yet).
        /// </summary>
        [Explicit, Test, Timeout(3_600_000)]
        public void Experiment_HitMissAndCell()
        {
            var world = SlamWorld.Apartment();
            var mapPoses = BiasMapPoses(world);
            var testPoses = BiasTestPoses(world);
            var scans = BiasScans(world, testPoses);
            float m1 = S.FreeSpaceMarginSigmas;
            var settings = new (float miss, float margin, bool fine)[]
            {
                (S.MissProbability, m1, false), (S.MissProbability, 0f, false),
                (0.47f, m1, false), (0.47f, 0f, false),
                (0.45f, m1, false), (0.45f, 0f, false),
                (S.MissProbability, m1, true), (0.45f, m1, true), (0.45f, 0f, true),
            };
            const int seeds = 4;

            try
            {
                foreach (var (miss, margin, fine) in settings)
                {
                    ProbabilityGrid.MissProbabilityOverride = miss;
                    LocalSlam.MarginSigmasOverride = margin;
                    LocalSlam.SubmapCellOverride = fine ? 0.025f : 0f;
                    float cell = fine ? 0.0254f : 0.0508f;
                    string name = $"hit {S.HitProbability:F2} / miss {miss:F2}, margin {margin:F0}σ, {(fine ? "2.5" : "5")} cm cells" +
                                  (miss == S.MissProbability && margin == m1 && !fine ? " (now)" : "");
                    TestContext.WriteLine(name);

                    // Map and matching bias.
                    double into = 0, into05 = 0;
                    ProbabilityGrid first = null;
                    for (int k = 0; k < BiasOffsets; k++)
                    {
                        var grid = BiasMap(world, mapPoses, BiasAnchor(k, cell), cell, true, margin, (uint)(100 + k));
                        if (k == 0) first = grid;
                        foreach (var wall in BiasWalls)
                        {
                            into += WallOffset(world, grid, wall, S.OccupiedProbability) / (BiasOffsets * BiasWalls.Length);
                            into05 += WallOffset(world, grid, wall, 0.5f) / (BiasOffsets * BiasWalls.Length);
                        }
                    }
                    var summary = new BiasSummary();
                    for (int p = 0; p < testPoses.Count; p++)
                    {
                        var (mean, heading, sd) = MatchBias(first, testPoses[p], scans[p], float.MaxValue);
                        summary.Add(mean, heading, sd, testPoses.Count);
                    }
                    TestContext.WriteLine($"    walls drawn {into:+0.00;-0.00} cm into the room (at 0.5: {into05:+0.00;-0.00}); matching: {summary}");

                    // The real pipeline.
                    float worst = 0, worstMax = 0, p99 = 0, end = 0, heading2 = 0, aligned = 0;
                    float falseNear = 0, ghosts = 0, missed = 0, thickness = 0;
                    for (int seed = 1; seed <= seeds; seed++)
                    {
                        var run = new SlamHarness(seed).Run(300f);
                        worst += run.Drift.MaxMeters / seeds;
                        worstMax = Mathf.Max(worstMax, run.Drift.MaxMeters);
                        p99 += run.Drift.PositionPercentile(0.99f) / seeds;
                        end += run.EndPositionError / seeds;
                        heading2 += run.Drift.MaxDegrees / seeds;
                        aligned += run.Aligned().max / seeds;
                        if (!fine)
                        {
                            var map = run.MapAccuracy();
                            falseNear += map.falseNear / (float)seeds;
                            ghosts += map.ghosts / (float)seeds;
                            missed += map.missed / (float)seeds;
                            thickness += map.thickness / seeds;
                        }
                    }
                    TestContext.WriteLine($"    run ({seeds} seeds × 300 s, means): worst {worst * 100:F1} cm (worst seed {worstMax * 100:F1}), p99 {p99 * 100:F1} cm, " +
                                          $"end {end * 100:F1} cm, worst heading {heading2:F2}°, aligned worst {aligned * 100:F1} cm" +
                                          (fine ? "" : $"; walls: false near {falseNear:F0}, ghosts {ghosts:F0}, missed {missed:F0}, thickness {thickness:F2}×"));
                }
            }
            finally
            {
                ProbabilityGrid.MissProbabilityOverride = 0f;
                LocalSlam.MarginSigmasOverride = -1f;
                LocalSlam.SubmapCellOverride = 0f;
            }
        }

        /// <summary>
        /// Diagnostics, not a check: where does the HEADING error come from? Position error tracks heading error × distance
        /// from the dock (r = 0.74 over the survey's checkpoints). Each setting changes one thing; 4 seeds × 300 s each,
        /// loop closure on. Heading errors are |believed − true| in °, means over the seeds.
        /// </summary>
        [Explicit, Test, Timeout(3_600_000)]
        public void Diagnose_Heading()
        {
            var settings = new (string name, Action<SlamHarness> set, Action global)[]
            {
                ("as now", h => { }, null),
                ("start spin at 60°/s", h => h.SpinSpeed = 60f, null),
                ("no start spin", h => h.SpinSign = 0f, null),
                ("odometry: no turn noise", h => h.OdometryTurnNoise = false, null),
                ("odometry: no noise at all", h => h.OdometryNoise = 0f, null),
                ("lidar: no bearing noise", h => h.BearingNoise = false, null),
                ("lidar: no range noise", h => h.RangeNoise = false, null),
                ("rotation weight 10 (from 40)", h => { }, () => LocalSlam.RotationWeightOverride = 10f),
                ("rotation weight 160 (from 40)", h => { }, () => LocalSlam.RotationWeightOverride = 160f),
                ("consecutive links' heading σ 0.1° (from 0.4°)", h => { }, () => GlobalSlam.ConsecutiveSigmaDegreesOverride = 0.1f),
            };
            try
            {
                foreach (var (name, set, global) in settings)
                {
                    LocalSlam.RotationWeightOverride = 0f;
                    GlobalSlam.ConsecutiveSigmaDegreesOverride = 0f;
                    global?.Invoke();
                    HeadingRow(name, set);
                }
            }
            finally
            {
                LocalSlam.RotationWeightOverride = 0f;
                GlobalSlam.ConsecutiveSigmaDegreesOverride = 0f;
            }
        }

        /// <summary>
        /// Diagnostics, not a check: range noise drives the heading error (<see cref="Diagnose_Heading"/>). Candidate
        /// fixes in local SLAM's tracking, and two changes to the simulated noise that test the explanation. Same
        /// columns as <see cref="Diagnose_Heading"/>; 4 seeds × 300 s each, loop closure on.
        /// </summary>
        [Explicit, Test, Timeout(3_600_000)]
        public void Diagnose_RangeNoise()
        {
            var settings = new (string name, Action<SlamHarness> set, Action global)[]
            {
                ("as now", h => { }, null),
                ("track with returns within 3 m only", h => { }, () => LocalSlam.TrackRangeOverride = 3f),
                ("track with returns within 4 m only", h => { }, () => LocalSlam.TrackRangeOverride = 4f),
                ("points weighted by 1/σ(range)", h => { }, () => LocalSlam.PointWeightPower = 1),
                ("points weighted by 1/σ(range)²", h => { }, () => LocalSlam.PointWeightPower = 2),
                ("180 scans per submap (from 90)", h => { }, () => LocalSlam.ScansPerSubmapOverride = 180),
                ("SIM: range noise a flat 1 cm (no % of range)", h => h.FlatRangeNoise = true, null),
                ("SIM: range noise doubled", h => h.RangeNoiseScale = 2f, null),
            };
            try
            {
                foreach (var (name, set, global) in settings)
                {
                    LocalSlam.TrackRangeOverride = 0f;
                    LocalSlam.PointWeightPower = 0;
                    LocalSlam.ScansPerSubmapOverride = 0;
                    global?.Invoke();
                    HeadingRow(name, set);
                }
            }
            finally
            {
                LocalSlam.TrackRangeOverride = 0f;
                LocalSlam.PointWeightPower = 0;
                LocalSlam.ScansPerSubmapOverride = 0;
            }
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
        /// Where does the position error come from? Same driving, same seeds, five ways (8 seeds × 300 s):
        ///   A. perfect map: the map gets exact scans at the true poses; tracking fits the real noisy scans. The matcher's floor.
        ///   B. noisy map, no feedback: the map gets the real noisy scans, at the TRUE poses. Noisy walls, but no pose
        ///      error is ever written into the map.
        ///   C0. full SLAM, loop closure off.   C. full SLAM as now.   D. as now, loop links' σ × 3 (honest, looser).
        /// C0 − B is what the feedback loop (pose errors written into the map, then followed) costs; B − A what noisy walls
        /// cost. "Aligned" removes one best-fit shift and rotation of the whole run: the part lidar can't observe.
        /// </summary>
        [Explicit, Test, Timeout(3_600_000)]
        public void Diagnose_ErrorSources()
        {
            var settings = new (string name, bool loop, Action<SlamHarness> set, Action global)[]
            {
                ("A. perfect map (exact scans at true poses), loop off", false, h => { h.InsertAtTruth = true; h.InsertExactScans = true; }, null),
                ("B. noisy map at true poses (no feedback), loop off", false, h => h.InsertAtTruth = true, null),
                ("C0. full SLAM, loop off", false, h => { }, null),
                ("C. full SLAM as now (loop on)", true, h => { }, null),
                ("D. as now, loop links' σ × 3", true, h => { }, () => GlobalSlam.LoopSigmaScale = 3f),
            };
            const int seeds = 8;
            try
            {
                foreach (var (name, loop, set, global) in settings)
                {
                    GlobalSlam.LoopSigmaScale = 1f;
                    global?.Invoke();
                    TestContext.WriteLine(name);
                    float worst = 0, worstMax = 0, p99 = 0, rms = 0, end = 0, aWorst = 0, aRms = 0, heading = 0;
                    for (int seed = 1; seed <= seeds; seed++)
                    {
                        var harness = new SlamHarness(seed, null, loop);
                        set(harness);
                        var run = harness.Run(300f);
                        var aligned = run.Aligned();
                        TestContext.WriteLine($"    seed {seed}: worst {run.Drift.MaxMeters * 100:F1} cm, p99 {run.Drift.PositionPercentile(0.99f) * 100:F1}, " +
                                              $"rms {run.Drift.RmsMeters * 100:F1}, end {run.EndPositionError * 100:F1} | aligned worst {aligned.max * 100:F1}, " +
                                              $"rms {aligned.rms * 100:F1} (rotation {aligned.rotationDegrees:+0.00;-0.00}°) | heading worst {run.Drift.MaxDegrees:F2}°");
                        worst += run.Drift.MaxMeters / seeds;
                        worstMax = Mathf.Max(worstMax, run.Drift.MaxMeters);
                        p99 += run.Drift.PositionPercentile(0.99f) / seeds;
                        rms += run.Drift.RmsMeters / seeds;
                        end += run.EndPositionError / seeds;
                        aWorst += aligned.max / seeds;
                        aRms += aligned.rms / seeds;
                        heading += run.Drift.MaxDegrees / seeds;
                    }
                    TestContext.WriteLine($"  means: worst {worst * 100:F1} cm (worst seed {worstMax * 100:F1}), p99 {p99 * 100:F1}, rms {rms * 100:F1}, end {end * 100:F1} | " +
                                          $"aligned worst {aWorst * 100:F1}, rms {aRms * 100:F1} | heading worst {heading:F2}°");
                }
            }
            finally
            {
                GlobalSlam.LoopSigmaScale = 1f;
            }
        }

        /// <summary>
        /// SLAM_Option2_Design stage 1, go / no-go: the node-level graph with submaps rebuilt from their scans, against
        /// SLAM as now, on the same 8 seeds × 300 s. For reference (Diagnose_ErrorSources, same seeds): the map built at
        /// the TRUE poses gave p99 4.0 cm, rms 1.2; as now p99 8.0, rms 3.9. Node rows also report each node's pose
        /// against the truth (with the graph, and as local SLAM had it), submaps rebuilt, and global SLAM's time.
        /// </summary>
        [Explicit, Test, Timeout(3_600_000)]
        public void Experiment_NodeGraph()
        {
            var settings = new (string name, Action<SlamHarness> set)[]
            {
                ("as now (submap graph)", h => { }),
                ("node graph + rebuilt submaps", h => h.NodeGraph = true),
            };
            const int seeds = 8;
            foreach (var (name, set) in settings)
            {
                TestContext.WriteLine(name);
                float worst = 0, worstMax = 0, p99 = 0, rms = 0, end = 0, aWorst = 0, heading = 0;
                double ms = 0;
                for (int seed = 1; seed <= seeds; seed++)
                {
                    var harness = new SlamHarness(seed);
                    set(harness);
                    var run = harness.Run(300f);
                    var aligned = run.Aligned();
                    string nodes = "";
                    if (harness.NodeGraph)
                    {
                        var e = run.NodeErrors();
                        nodes = $" | nodes vs truth: graph mean {e.graphMean * 100:F1} cm (worst {e.graphMax * 100:F1}), local SLAM mean {e.localMean * 100:F1} " +
                                $"(worst {e.localMax * 100:F1}); {run.Global.Rebuilds} submap rebuilds, {run.Global.Optimisations} solves, {run.Global.LoopLinks} loop links";
                    }
                    TestContext.WriteLine($"    seed {seed}: worst {run.Drift.MaxMeters * 100:F1} cm, p99 {run.Drift.PositionPercentile(0.99f) * 100:F1}, " +
                                          $"rms {run.Drift.RmsMeters * 100:F1}, end {run.EndPositionError * 100:F1} | aligned worst {aligned.max * 100:F1} | " +
                                          $"heading worst {run.Drift.MaxDegrees:F2}° | global SLAM {run.GlobalMs / 1000:F1} s of {run.RealSeconds:F1} s{nodes}");
                    worst += run.Drift.MaxMeters / seeds;
                    worstMax = Mathf.Max(worstMax, run.Drift.MaxMeters);
                    p99 += run.Drift.PositionPercentile(0.99f) / seeds;
                    rms += run.Drift.RmsMeters / seeds;
                    end += run.EndPositionError / seeds;
                    aWorst += run.Aligned().max / seeds;
                    heading += run.Drift.MaxDegrees / seeds;
                    ms += run.GlobalMs / seeds;
                }
                TestContext.WriteLine($"  means: worst {worst * 100:F1} cm (worst seed {worstMax * 100:F1}), p99 {p99 * 100:F1}, rms {rms * 100:F1}, end {end * 100:F1} | " +
                                      $"aligned worst {aWorst * 100:F1} | heading worst {heading:F2}° | global SLAM {ms / 1000:F1} s per run");
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
        /// Does the error wander with submap generations? Scans per submap 90 (now), 180, 360, and never retired (one map
        /// tracked against all run). 8 seeds × 300 s, loop closure off, no other change. Stop rule agreed: if p99 does
        /// not improve by at least 1 cm from 90 to 360, the generation hypothesis is wrong.
        /// </summary>
        [Explicit, Test, Timeout(3_600_000)]
        public void Experiment_SubmapLifetime()
        {
            var settings = new (string name, int scans)[] { ("90 scans per submap (now)", 0), ("180", 180), ("360", 360), ("never retired (one map)", 1_000_000) };
            const int seeds = 8;
            try
            {
                foreach (var (name, scans) in settings)
                {
                    LocalSlam.ScansPerSubmapOverride = scans;
                    float worst = 0, worstMax = 0, p99 = 0, rms = 0, end = 0, aWorst = 0, aRms = 0, rotation = 0, heading = 0;
                    int ccw = 0;
                    string perSeed = "";
                    for (int seed = 1; seed <= seeds; seed++)
                    {
                        var run = new SlamHarness(seed, null, false).Run(300f);
                        var aligned = run.Aligned();
                        perSeed += $" {run.Drift.PositionPercentile(0.99f) * 100:F1}";
                        worst += run.Drift.MaxMeters / seeds;
                        worstMax = Mathf.Max(worstMax, run.Drift.MaxMeters);
                        p99 += run.Drift.PositionPercentile(0.99f) / seeds;
                        rms += run.Drift.RmsMeters / seeds;
                        end += run.EndPositionError / seeds;
                        aWorst += aligned.max / seeds;
                        aRms += aligned.rms / seeds;
                        rotation += aligned.rotationDegrees / seeds;
                        heading += run.Drift.MaxDegrees / seeds;
                        if (aligned.rotationDegrees > 0) ccw++;
                    }
                    TestContext.WriteLine($"{name}: worst {worst * 100:F1} cm (worst seed {worstMax * 100:F1}), p99 {p99 * 100:F1} (each seed:{perSeed}), rms {rms * 100:F1}, " +
                                          $"end {end * 100:F1} | aligned worst {aWorst * 100:F1}, rms {aRms * 100:F1} | track rotation mean {rotation:+0.00;-0.00}° " +
                                          $"({ccw} of {seeds} counter-clockwise) | heading worst {heading:F2}°");
                }
            }
            finally
            {
                LocalSlam.ScansPerSubmapOverride = 0;
            }
        }

        /// <summary>
        /// Confirmation before changing the live default: 90 vs 360 scans per submap, loop closure off vs on, in the test
        /// apartment and the angled apartment (8 seeds × 300 s each). Decision rule agreed: if 360 with loop closure on is
        /// at least as good as 360 with it off, in both worlds, the live ScansPerSubmap becomes 360.
        /// </summary>
        [Explicit, Test, Timeout(3_600_000)]
        public void Experiment_SubmapLifetimeConfirm()
        {
            var worlds = new (string name, SlamWorld world)[] { ("apartment", SlamWorld.Apartment()), ("angled apartment", SlamWorld.AngledApartment()) };
            const int seeds = 8;
            try
            {
                foreach (var (worldName, world) in worlds)
                    foreach (int scans in new[] { 90, 360 })
                        foreach (bool loop in new[] { false, true })
                        {
                            LocalSlam.ScansPerSubmapOverride = scans;
                            float worst = 0, worstMax = 0, p99 = 0, rms = 0, end = 0, aWorst = 0, aRms = 0, rotation = 0, heading = 0;
                            int ccw = 0, links = 0;
                            string perSeed = "";
                            for (int seed = 1; seed <= seeds; seed++)
                            {
                                var run = new SlamHarness(seed, world, loop).Run(300f);
                                var aligned = run.Aligned();
                                perSeed += $" {run.Drift.PositionPercentile(0.99f) * 100:F1}";
                                worst += run.Drift.MaxMeters / seeds;
                                worstMax = Mathf.Max(worstMax, run.Drift.MaxMeters);
                                p99 += run.Drift.PositionPercentile(0.99f) / seeds;
                                rms += run.Drift.RmsMeters / seeds;
                                end += run.EndPositionError / seeds;
                                aWorst += aligned.max / seeds;
                                aRms += aligned.rms / seeds;
                                rotation += aligned.rotationDegrees / seeds;
                                heading += run.Drift.MaxDegrees / seeds;
                                if (aligned.rotationDegrees > 0) ccw++;
                                if (run.Global != null) links += run.Global.LoopLinks;
                            }
                            TestContext.WriteLine($"{worldName}, {scans} scans per submap, loop closure {(loop ? "on" : "off")}: " +
                                                  $"worst {worst * 100:F1} cm (worst seed {worstMax * 100:F1}), p99 {p99 * 100:F1} (each seed:{perSeed}), rms {rms * 100:F1}, end {end * 100:F1} | " +
                                                  $"aligned worst {aWorst * 100:F1}, rms {aRms * 100:F1} | rotation {rotation:+0.00;-0.00}° ({ccw} of {seeds} ccw) | heading worst {heading:F2}°" +
                                                  (loop ? $" | loop links {links / seeds} per run" : ""));
                        }
            }
            finally
            {
                LocalSlam.ScansPerSubmapOverride = 0;
            }
        }

        /// <summary>
        /// Fix 1 on trial: scan points scored against where hits actually landed (hit field) instead of probabilities
        /// between cell centres. Stop rule agreed: the room-driven rotation (test apartment, loop closure on, now +0.26°,
        /// 7 of 8 seeds counter-clockwise) must fall below 0.15° with mixed signs, or the fix is dropped.
        ///   A. The matcher's own bias at fixed true poses (as Diagnose_BiasSource), exact and as-built maps.
        ///   B. The live pipeline (360-scan submaps, loop closure on), 8 seeds × 300 s, both apartments.
        /// </summary>
        [Explicit, Test, Timeout(3_600_000)]
        public void Experiment_HitField()
        {
            var settings = new (string name, int hits, float sigma)[] { ("as now (cell centres)", 0, 0f), ("hit field, σ 3 cm", 1, 0.03f), ("hit field, σ 5 cm", 1, 0.05f) };
            try
            {
                // A. Matching bias at fixed true poses.
                var apartment = SlamWorld.Apartment();
                var mapPoses = BiasMapPoses(apartment);
                var testPoses = BiasTestPoses(apartment);
                var scans = BiasScans(apartment, testPoses);
                const float cell = 0.0508f;
                var exact = BiasMap(apartment, mapPoses, BiasAnchor(0, cell), cell, false, 0f, 100);
                var built = BiasMap(apartment, mapPoses, BiasAnchor(0, cell), cell, true, S.FreeSpaceMarginSigmas, 100);
                TestContext.WriteLine($"A. MATCHING BIAS at {testPoses.Count} fixed true poses ({BiasScansPerPose} noisy scans each)");
                foreach (var (name, hits, sigma) in settings)
                {
                    ScanMatcher.HitFieldOverride = hits;
                    ScanMatcher.HitFieldSigmaOverride = sigma;
                    foreach (var (mapName, map) in new[] { ("exact map", exact), ("as-built map", built) })
                    {
                        var summary = new BiasSummary();
                        for (int p = 0; p < testPoses.Count; p++)
                        {
                            var (mean, heading, sd) = MatchBias(map, testPoses[p], scans[p], float.MaxValue);
                            summary.Add(mean, heading, sd, testPoses.Count);
                        }
                        TestContext.WriteLine($"    {name}, {mapName}: {summary}");
                    }
                }

                // B. The live pipeline.
                TestContext.WriteLine("B. LIVE PIPELINE (360-scan submaps, loop closure on), 8 seeds × 300 s");
                const int seeds = 8;
                foreach (var (worldName, world) in new[] { ("apartment", SlamWorld.Apartment()), ("angled apartment", SlamWorld.AngledApartment()) })
                {
                    foreach (var (name, hits, sigma) in settings)
                    {
                        ScanMatcher.HitFieldOverride = hits;
                        ScanMatcher.HitFieldSigmaOverride = sigma;
                        float worst = 0, worstMax = 0, p99 = 0, rms = 0, end = 0, aWorst = 0, aRms = 0, rotation = 0, heading = 0;
                        int ccw = 0;
                        string rotations = "";
                        for (int seed = 1; seed <= seeds; seed++)
                        {
                            var run = new SlamHarness(seed, world).Run(300f);
                            var aligned = run.Aligned();
                            rotations += $" {aligned.rotationDegrees:+0.00;-0.00}";
                            worst += run.Drift.MaxMeters / seeds;
                            worstMax = Mathf.Max(worstMax, run.Drift.MaxMeters);
                            p99 += run.Drift.PositionPercentile(0.99f) / seeds;
                            rms += run.Drift.RmsMeters / seeds;
                            end += run.EndPositionError / seeds;
                            aWorst += aligned.max / seeds;
                            aRms += aligned.rms / seeds;
                            rotation += aligned.rotationDegrees / seeds;
                            heading += run.Drift.MaxDegrees / seeds;
                            if (aligned.rotationDegrees > 0) ccw++;
                        }
                        TestContext.WriteLine($"    {worldName}, {name}: worst {worst * 100:F1} cm (worst seed {worstMax * 100:F1}), p99 {p99 * 100:F1}, rms {rms * 100:F1}, " +
                                              $"end {end * 100:F1} | aligned worst {aWorst * 100:F1}, rms {aRms * 100:F1} | rotation mean {rotation:+0.00;-0.00}° " +
                                              $"({ccw} of {seeds} ccw; each:{rotations}) | heading worst {heading:F2}°");
                    }
                }
            }
            finally
            {
                ScanMatcher.HitFieldOverride = -1;
                ScanMatcher.HitFieldSigmaOverride = 0f;
            }
        }

        /// <summary>
        /// Where does the remaining rotation come from? At the end of each run, the angle at which each long wall surface
        /// sits in the robot's map (from where hits landed, to well under a cell: <see cref="SlamHarness.WallFromHits"/>),
        /// against the track's best-fit rotation (8 seeds × 300 s, live settings, loop
        /// closure on, then off). If every wall shares the track's rotation, the whole map turned rigidly (heading drift
        /// while mapping). If walls differ from each other, the map-building step bends individual walls.
        /// </summary>
        [Explicit, Test, Timeout(3_600_000)]
        public void Diagnose_WallDirections()
        {
            var walls = new (string name, Vector2 start, Vector2 dir, float surface, float spanMin, float spanMax)[]
            {
                ("left outer", new Vector2(-2.5f, 0f), Vector2.left, -3.0f, -1.8f, 1.8f),
                ("right outer", new Vector2(2.5f, 0f), Vector2.right, 3.0f, -1.8f, 1.8f),
                ("top outer", new Vector2(0f, 1.5f), Vector2.up, 2.0f, -2.8f, 2.8f),
                ("bottom outer", new Vector2(0f, -1.5f), Vector2.down, -2.0f, -2.8f, 2.8f),
                ("middle, left face", new Vector2(0f, 0f), Vector2.right, 0.45f, -1.8f, 0.4f),
                ("middle, right face", new Vector2(1.0f, 0f), Vector2.left, 0.55f, -1.8f, 0.4f),
            };
            const int seeds = 8;
            foreach (bool loop in new[] { true, false })
            {
                TestContext.WriteLine(loop ? "LOOP CLOSURE ON (live)" : "LOOP CLOSURE OFF");
                var angleSum = new double[walls.Length];
                var offsetSum = new double[walls.Length];
                var counted = new int[walls.Length];
                double trackSum = 0;
                for (int seed = 1; seed <= seeds; seed++)
                {
                    var run = new SlamHarness(seed, null, loop).Run(300f);
                    var aligned = run.Aligned();
                    trackSum += aligned.rotationDegrees / seeds;
                    string line = $"    seed {seed}: track rotation {aligned.rotationDegrees:+0.00;-0.00}° | walls:";
                    for (int w = 0; w < walls.Length; w++)
                    {
                        bool alongX = Mathf.Abs(walls[w].dir.x) > 0.5f;
                        var (angle, offset, samples) = run.WallFromHits(alongX, walls[w].surface, walls[w].spanMin, walls[w].spanMax,
                            skipMiddleJoin: !alongX && walls[w].dir.y < 0f,
                            window: walls[w].name.StartsWith("middle") ? 0.045f : 0.15f); // the middle wall's faces are only 10 cm apart
                        line += $" {walls[w].name} {angle:+0.00;-0.00}° ({offset:+0.0;-0.0} cm)";
                        if (!float.IsNaN(angle))
                        {
                            angleSum[w] += angle;
                            offsetSum[w] += offset;
                            counted[w]++;
                        }
                    }
                    TestContext.WriteLine(line);
                }
                string means = $"  means: track rotation {trackSum:+0.00;-0.00}° | walls:";
                for (int w = 0; w < walls.Length; w++)
                    means += counted[w] > 0 ? $" {walls[w].name} {angleSum[w] / counted[w]:+0.00;-0.00}° ({offsetSum[w] / counted[w]:+0.0;-0.0} cm)" : $" {walls[w].name} n/a";
                TestContext.WriteLine(means);
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
        /// Is the robot's map shrunk? Best fit of the believed track to the true one allowing a scale (and separate x / y
        /// scales), live settings with loop closure on, 8 seeds × 300 s, five ways: as now; hit field on; no free-space
        /// margin; simulated range noise a flat 1 cm; no range noise. Measurement only.
        /// </summary>
        [Explicit, Test, Timeout(3_600_000)]
        public void Diagnose_Scale()
        {
            var settings = new (string name, Action<SlamHarness> set, Action global)[]
            {
                ("as now", h => { }, null),
                ("hit field on (σ 3 cm)", h => { }, () => { ScanMatcher.HitFieldOverride = 1; ScanMatcher.HitFieldSigmaOverride = 0.03f; }),
                ("no free-space margin", h => { }, () => LocalSlam.MarginSigmasOverride = 0f),
                ("SIM: range noise a flat 1 cm", h => h.FlatRangeNoise = true, null),
                ("SIM: no range noise", h => h.RangeNoise = false, null),
            };
            const int seeds = 8;
            try
            {
                foreach (var (name, set, global) in settings)
                {
                    ScanMatcher.HitFieldOverride = -1;
                    ScanMatcher.HitFieldSigmaOverride = 0f;
                    LocalSlam.MarginSigmasOverride = -1f;
                    global?.Invoke();
                    double scale = 0, sx = 0, sy = 0, rotation = 0, max = 0, rms = 0, worst = 0, p99 = 0;
                    string perSeed = "";
                    for (int seed = 1; seed <= seeds; seed++)
                    {
                        var harness = new SlamHarness(seed);
                        set(harness);
                        var run = harness.Run(300f);
                        var fit = run.AlignedWithScale();
                        perSeed += $" {(fit.scale - 1) * 100:+0.00;-0.00}";
                        scale += fit.scale / seeds;
                        sx += fit.scaleX / seeds;
                        sy += fit.scaleY / seeds;
                        rotation += fit.rotationDegrees / seeds;
                        max += fit.max / seeds;
                        rms += fit.rms / seeds;
                        worst += run.Drift.MaxMeters / seeds;
                        p99 += run.Drift.PositionPercentile(0.99f) / seeds;
                    }
                    TestContext.WriteLine($"{name}: scale {(scale - 1) * 100:+0.00;-0.00}% (each seed, %:{perSeed}) | x {(sx - 1) * 100:+0.00;-0.00}%, y {(sy - 1) * 100:+0.00;-0.00}% | " +
                                          $"rotation with scale allowed {rotation:+0.00;-0.00}° | after shift, rotation and scale removed: worst {max * 100:F1} cm, rms {rms * 100:F1} | " +
                                          $"raw worst {worst * 100:F1} cm, p99 {p99 * 100:F1}");
                }
            }
            finally
            {
                ScanMatcher.HitFieldOverride = -1;
                ScanMatcher.HitFieldSigmaOverride = 0f;
                LocalSlam.MarginSigmasOverride = -1f;
            }
        }

        /// <summary>
        /// Where does the ~1.1% shrink come from, and does weighting returns by their noise remove it? 8 seeds × 300 s each,
        /// scale reported on every row: as now; perfect map (exact scans at the true poses; tracking fits the real noisy
        /// scans; loop off); noisy map at the true poses (loop off); points weighted by 1/σ(range) and 1/σ(range)²;
        /// tracking with returns within 3 m only.
        /// </summary>
        [Explicit, Test, Timeout(3_600_000)]
        public void Diagnose_ScaleSource()
        {
            var settings = new (string name, bool loop, Action<SlamHarness> set, Action global)[]
            {
                ("as now", true, h => { }, null),
                ("perfect map at true poses (matcher alone), loop off", false, h => { h.InsertAtTruth = true; h.InsertExactScans = true; }, null),
                ("noisy map at true poses (no feedback), loop off", false, h => h.InsertAtTruth = true, null),
                ("points weighted by 1/σ(range)", true, h => { }, () => LocalSlam.PointWeightPower = 1),
                ("points weighted by 1/σ(range)²", true, h => { }, () => LocalSlam.PointWeightPower = 2),
                ("tracking with returns within 3 m only", true, h => { }, () => LocalSlam.TrackRangeOverride = 3f),
            };
            const int seeds = 8;
            try
            {
                foreach (var (name, loop, set, global) in settings)
                {
                    LocalSlam.PointWeightPower = 0;
                    LocalSlam.TrackRangeOverride = 0f;
                    global?.Invoke();
                    double scale = 0, sx = 0, sy = 0, rotation = 0, max = 0, rms = 0, worst = 0, p99 = 0;
                    string perSeed = "";
                    for (int seed = 1; seed <= seeds; seed++)
                    {
                        var harness = new SlamHarness(seed, null, loop);
                        set(harness);
                        var run = harness.Run(300f);
                        var fit = run.AlignedWithScale();
                        perSeed += $" {(fit.scale - 1) * 100:+0.00;-0.00}";
                        scale += fit.scale / seeds;
                        sx += fit.scaleX / seeds;
                        sy += fit.scaleY / seeds;
                        rotation += fit.rotationDegrees / seeds;
                        max += fit.max / seeds;
                        rms += fit.rms / seeds;
                        worst += run.Drift.MaxMeters / seeds;
                        p99 += run.Drift.PositionPercentile(0.99f) / seeds;
                    }
                    TestContext.WriteLine($"{name}: scale {(scale - 1) * 100:+0.00;-0.00}% (each seed, %:{perSeed}) | x {(sx - 1) * 100:+0.00;-0.00}%, y {(sy - 1) * 100:+0.00;-0.00}% | " +
                                          $"rotation with scale allowed {rotation:+0.00;-0.00}° | after shift, rotation and scale removed: worst {max * 100:F1} cm, rms {rms * 100:F1} | " +
                                          $"raw worst {worst * 100:F1} cm, p99 {p99 * 100:F1}");
                }
            }
            finally
            {
                LocalSlam.PointWeightPower = 0;
                LocalSlam.TrackRangeOverride = 0f;
            }
        }

        /// <summary>
        /// Can odometry pin the map's scale? Odometry's distance error is random, not systematic, so trusting its step more
        /// should stop the feedback loop's ~1.1% shrink. Translation weight (trust in odometry's step) 10 (now), 30, 100,
        /// 300; map insertion range 5 m instead of 3; and both. Live settings, 8 seeds × 300 s. Stop rule agreed: scale
        /// within ±0.3% AND p99 lower, or the setting is dropped.
        /// </summary>
        [Explicit, Test, Timeout(3_600_000)]
        public void Experiment_OdometryTrust()
        {
            var settings = new (string name, float translation, float insert)[]
            {
                ("as now (translation weight 10, insert 3 m)", 0f, 0f),
                ("translation weight 30", 30f, 0f),
                ("translation weight 100", 100f, 0f),
                ("translation weight 300", 300f, 0f),
                ("insert range 5 m", 0f, 5f),
                ("translation weight 100 + insert range 5 m", 100f, 5f),
            };
            const int seeds = 8;
            try
            {
                foreach (var (name, translation, insert) in settings)
                {
                    LocalSlam.TranslationWeightOverride = translation;
                    LocalSlam.InsertRangeOverride = insert;
                    double scale = 0, sx = 0, sy = 0, rotation = 0, max = 0, rms = 0, worst = 0, worstMax = 0, p99 = 0, rawRms = 0, heading = 0;
                    string perSeed = "";
                    for (int seed = 1; seed <= seeds; seed++)
                    {
                        var run = new SlamHarness(seed).Run(300f);
                        var fit = run.AlignedWithScale();
                        perSeed += $" {(fit.scale - 1) * 100:+0.00;-0.00}";
                        scale += fit.scale / seeds;
                        sx += fit.scaleX / seeds;
                        sy += fit.scaleY / seeds;
                        rotation += fit.rotationDegrees / seeds;
                        max += fit.max / seeds;
                        rms += fit.rms / seeds;
                        worst += run.Drift.MaxMeters / seeds;
                        worstMax = Math.Max(worstMax, run.Drift.MaxMeters);
                        p99 += run.Drift.PositionPercentile(0.99f) / seeds;
                        rawRms += run.Drift.RmsMeters / seeds;
                        heading += run.Drift.MaxDegrees / seeds;
                    }
                    TestContext.WriteLine($"{name}: scale {(scale - 1) * 100:+0.00;-0.00}% (each seed, %:{perSeed}) | x {(sx - 1) * 100:+0.00;-0.00}%, y {(sy - 1) * 100:+0.00;-0.00}% | " +
                                          $"rotation {rotation:+0.00;-0.00}° | raw worst {worst * 100:F1} cm (worst seed {worstMax * 100:F1}), p99 {p99 * 100:F1}, rms {rawRms * 100:F1} | " +
                                          $"after shift, rotation and scale removed: worst {max * 100:F1}, rms {rms * 100:F1} | heading worst {heading:F2}°");
                }
            }
            finally
            {
                LocalSlam.TranslationWeightOverride = 0f;
                LocalSlam.InsertRangeOverride = 0f;
            }
        }

        /// <summary>
        /// How far out should returns be written into the map? Insert range 3 (now), 4, 5, 6, 8 m, in both apartments, live
        /// settings, 8 seeds × 300 s. Decision rule agreed: adopt the range with the lowest p99 that is at least 1 cm better
        /// than 3 m in both worlds, provided wall thickness does not get noticeably worse. Scale reported, not a gate.
        /// </summary>
        [Explicit, Test, Timeout(3_600_000)]
        public void Experiment_InsertRange()
        {
            const int seeds = 8;
            try
            {
                foreach (var (worldName, world) in new[] { ("apartment", SlamWorld.Apartment()), ("angled apartment", SlamWorld.AngledApartment()) })
                {
                    foreach (float range in new[] { 3f, 4f, 5f, 6f, 8f })
                    {
                        LocalSlam.InsertRangeOverride = range;
                        double scale = 0, worst = 0, worstMax = 0, p99 = 0, rms = 0, heading = 0, falseNear = 0, ghosts = 0, missed = 0, thickness = 0;
                        string perSeed = "";
                        for (int seed = 1; seed <= seeds; seed++)
                        {
                            var run = new SlamHarness(seed, world).Run(300f);
                            var fit = run.AlignedWithScale();
                            var map = run.MapAccuracy();
                            perSeed += $" {run.Drift.PositionPercentile(0.99f) * 100:F1}";
                            scale += fit.scale / seeds;
                            worst += run.Drift.MaxMeters / seeds;
                            worstMax = Math.Max(worstMax, run.Drift.MaxMeters);
                            p99 += run.Drift.PositionPercentile(0.99f) / seeds;
                            rms += run.Drift.RmsMeters / seeds;
                            heading += run.Drift.MaxDegrees / seeds;
                            falseNear += map.falseNear / (double)seeds;
                            ghosts += map.ghosts / (double)seeds;
                            missed += map.missed / (double)seeds;
                            thickness += map.thickness / seeds;
                        }
                        TestContext.WriteLine($"{worldName}, insert range {range:F0} m: raw worst {worst * 100:F1} cm (worst seed {worstMax * 100:F1}), p99 {p99 * 100:F1} " +
                                              $"(each seed:{perSeed}), rms {rms * 100:F1} | scale {(scale - 1) * 100:+0.00;-0.00}% | heading worst {heading:F2}° | " +
                                              $"walls: false near {falseNear:F0}, ghosts {ghosts:F0}, missed {missed:F0}, thickness {thickness:F2}×");
                    }
                }
            }
            finally
            {
                LocalSlam.InsertRangeOverride = 0f;
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

        /// <summary>
        /// Diagnostics, not a check: how good are loop closure's matches? For every search (seeds 1–4 × 300 s): how far
        /// the guess and the match were from where the scan really sits in that submap, grouped by how many submaps
        /// back it was; and the submaps' final global poses against the truth, with and without the graph.
        /// </summary>
        [Explicit, Test, Timeout(3_600_000)]
        public void Diagnose_LoopClosure()
        {
            for (int seed = 1; seed <= 4; seed++)
            {
                var run = new SlamHarness(seed) { Diagnose = true }.Run(300f);
                Print(seed, run);

                foreach (var (label, lo, hi) in new[] { ("1–2 submaps back", 1, 2), ("3–5 back", 3, 5), ("6+ back", 6, 1000) })
                {
                    var group = run.Searches.Where(x => x.gap >= lo && x.gap <= hi).ToList();
                    if (group.Count == 0) continue;
                    var ok = group.Where(x => x.accepted).ToList();
                    string Mean(Func<(int gap, float guessCm, float foundCm, float guessDeg, float foundDeg, bool accepted, float score), float> f, List<(int, float, float, float, float, bool, float)> l) =>
                        l.Count > 0 ? l.Average(f).ToString("F2") : "-";
                    TestContext.WriteLine($"    {label}: {group.Count} searches, {ok.Count} accepted (score {Mean(x => x.score, group)}); " +
                                          $"guess off {Mean(x => x.guessCm, ok)} cm / {Mean(x => x.guessDeg, ok)}°, match off {Mean(x => x.foundCm, ok)} cm / {Mean(x => x.foundDeg, ok)}°; " +
                                          $"worst match {(ok.Count > 0 ? ok.Max(x => x.foundCm) : 0):F1} cm");
                }

                double graphMean = 0, graphMax = 0, localMean = 0, localMax = 0;
                int n = run.Slam.Submaps.Count;
                for (int i = 0; i < n; i++)
                {
                    var truthPose = run.TrueSubmapPose(i);
                    var g = run.Global.SubmapGlobalPoses[i];
                    var l = run.Slam.Submaps[i].LocalPose;
                    double eg = System.Math.Sqrt((g.x - truthPose.x) * (g.x - truthPose.x) + (g.y - truthPose.y) * (g.y - truthPose.y));
                    double el = System.Math.Sqrt((l.x - truthPose.x) * (l.x - truthPose.x) + (l.y - truthPose.y) * (l.y - truthPose.y));
                    graphMean += eg / n;
                    localMean += el / n;
                    graphMax = System.Math.Max(graphMax, eg);
                    localMax = System.Math.Max(localMax, el);
                }
                TestContext.WriteLine($"    submap poses vs the truth: with the graph mean {graphMean * 100:F1} cm (worst {graphMax * 100:F1}); " +
                                      $"local SLAM alone mean {localMean * 100:F1} cm (worst {localMax * 100:F1})");
            }
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
        public bool NodeGraph;                // SLAM_Option2_Design stage 1: node-level graph, submaps rebuilt from their scans
        public readonly List<RobotPose> NodeTruth = new List<RobotPose>(); // node graph: the true pose at each node
        public double GlobalMs;               // time spent in global SLAM (loop search, solves, rebuilds)

        // Diagnostics: one record per scan added to the map (Diagnose_ErrorGrowth).
        public bool RecordInsertions;
        RobotPose lastInsertTruth;

        /// <summary>Diagnostics: called every step with the believed and true pose and the time.</summary>
        public Action<RobotPose, RobotPose, float> OnStep;
        public readonly List<(float t, Vector2 error, float headingError, float trueMove, float trueTurn, float unseen, int submapScans)> Insertions =
            new List<(float, Vector2, float, float, float, float, int)>();         // with InsertAtTruth: the map gets an EXACT scan, i.e. a perfect map of what has been seen             // stand still at the dock this long first, adding every scan at the known dock pose
        public bool FlatRangeNoise;           // true = range noise σ is the fixed part only (no % of range)

        // Heading error (believed − true, °) after the start spin and at 60 s.
        public float HeadingAfterSpin = float.NaN, HeadingAt60 = float.NaN;

        public LocalSlam Slam => slam;

        /// <summary>Every 60 s: the believed − true position (x, y in cm) and heading (°), and how far the robot is from the dock.</summary>
        public readonly List<string> Checkpoints = new List<string>();

        /// <summary>Global SLAM (loop closure), or null when it is off.</summary>
        public GlobalSlam Global => globalSlam;

        // Diagnostics: local SLAM's frame → world at each submap's creation, and how each loop search did.
        public bool Diagnose;
        readonly List<GraphPose> frameAtCreation = new List<GraphPose>();
        public readonly List<(int gap, float guessCm, float foundCm, float guessDeg, float foundDeg, bool accepted, float score)> Searches =
            new List<(int, float, float, float, float, bool, float)>();

        // Diagnostics: run a second Internal Model redrawn by the original (reference) redraw, and compare after each redraw.
        public bool CompareRedraw;
        InternalModelWriter referenceWriter;
        ObservationMapper referenceMapper;
        public int Redraws, RedrawMismatches;
        public double RedrawMsTotal, RedrawMsMax, ReferenceRedrawMsTotal, ReferenceRedrawMsMax;

        /// <summary>World pose of submap i's frame, by local SLAM's frame → world at its creation.</summary>
        public GraphPose TrueSubmapPose(int i) => GraphPose.Compose(frameAtCreation[i], slam.Submaps[i].LocalPose);

        readonly Vector2 dockPosition;
        public bool MirrorGoals; // goals mirrored in x (with SlamWorld.MirroredX and a mirrored dock: the mirror image of a run)

        public SlamHarness(int seed, SlamWorld world = null, bool loopClosure = true, Vector2? dock = null)
        {
            dockPosition = dock ?? new Vector2(-2f, -1f);
            this.world = world ?? SlamWorld.Apartment();
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
            if (NodeGraph && globalSlam != null)
            {
                slam.KeepNodes = true;
                globalSlam.NodeMode = true;
            }
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

                if (holding) localPose = slam.TrackStationary(frame, dockPose, t);
                else if (InsertAtTruth && frame.hasLidarScan)
                    localPose = slam.Track(frame, localPose, t, truth, InsertExactScans ? world.Scan(truth, null) : frame);
                else localPose = slam.Track(frame, localPose, t);
                if (Diagnose)
                {
                    if (globalSlam != null) globalSlam.LogSearches = true;
                    // Local SLAM's frame → world right now: truth ∘ local⁻¹.
                    var localToWorld = GraphPose.Compose(GraphPose.From(truth), GraphPose.Relative(GraphPose.From(localPose), new GraphPose(0, 0, 0)));
                    while (frameAtCreation.Count < slam.Submaps.Count) frameAtCreation.Add(localToWorld);
                }
                if (NodeGraph && slam.InsertedThisStep) NodeTruth.Add(truth);
                globalClock.Restart();
                bool redraw = globalSlam != null && (NodeGraph ? globalSlam.AfterTrack(frame, deadReckoned) : globalSlam.AfterTrack(frame));
                GlobalMs += globalClock.Elapsed.TotalMilliseconds;
                if (Diagnose && globalSlam != null)
                {
                    foreach (var (submap, home, guess, found, accepted, score) in globalSlam.SearchLog)
                    {
                        // Where the scan really is in submap's grid frame: (frame at the submap's creation)⁻¹ ∘ truth.
                        var expected = GraphPose.Relative(frameAtCreation[submap], GraphPose.From(truth)).ToRobotPose();
                        Searches.Add((home - submap,
                            Vector2.Distance(guess.position, expected.position) * 100f, Vector2.Distance(found.position, expected.position) * 100f,
                            Mathf.Abs(Mathf.DeltaAngle(guess.heading, expected.heading)), Mathf.Abs(Mathf.DeltaAngle(found.heading, expected.heading)),
                            accepted, score));
                    }
                }
                believed = globalSlam != null ? globalSlam.ToGlobal(localPose) : localPose;
                if (RecordInsertions && slam.InsertedThisStep)
                {
                    float move = Vector2.Distance(truth.position, lastInsertTruth.position);
                    float turnSince = Mathf.Abs(Mathf.DeltaAngle(lastInsertTruth.heading, truth.heading));
                    int submapScans = slam.ActiveSubmaps.Count > 0 ? slam.ActiveSubmaps[slam.ActiveSubmaps.Count - 1].grid.ScanCount : 0;
                    Insertions.Add((t, believed.position - truth.position, Mathf.DeltaAngle(truth.heading, believed.heading), move, turnSince, unseen, submapScans));
                    lastInsertTruth = truth;
                }
                bool drawable = LocalSlam.SubmapCellOverride <= 0f; // the Internal Model is drawn from submaps on its own lattice
                if (redraw && drawable)
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
                if (frame.hasLidarScan && drawable)
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
        /// How one wall surface sits in the robot's map at the end of the run, to well under a cell: the mean hit position
        /// of every occupied submap cell (probability above 0.5) within <paramref name="window"/> of the true surface and along the given span,
        /// carried into the global frame, then a straight line through them weighted by hit count. Angle against the true
        /// wall (degrees, counter-clockwise +) and mean offset (cm, + = toward +x / +y). For walls along y, the span is y.
        /// </summary>
        public (float angleDegrees, float offsetCm, int cells) WallFromHits(bool alongX, float surface, float spanMin, float spanMax, bool skipMiddleJoin, float window = 0.15f)
        {
            double sw = 0, sS = 0, sC = 0, sSS = 0, sSC = 0;
            int cells = 0;
            foreach (var submap in slam.Submaps)
            {
                var g = submap.grid;
                for (int r = 0; r < g.rows; r++)
                {
                    for (int c = 0; c < g.cols; c++)
                    {
                        int i = r * g.cols + c;
                        int n = g.hitCount[i];
                        if (n == 0 || g.probability[i] <= 0.5f) continue;
                        var local = new Vector2((float)(g.AnchorWorld.x + g.hitSumX[i] / n), (float)(g.AnchorWorld.y + g.hitSumY[i] / n));
                        Vector2 p = globalSlam != null ? globalSlam.SubmapToGlobal(submap.index, local) : local;
                        float across = alongX ? p.x : p.y, along = alongX ? p.y : p.x;
                        if (Mathf.Abs(across - surface) > window || along < spanMin || along > spanMax) continue;
                        if (skipMiddleJoin && Mathf.Abs(along - 0.5f) < 0.25f) continue;
                        sw += n;
                        sS += n * along;
                        sC += n * across;
                        sSS += n * along * along;
                        sSC += n * along * across;
                        cells++;
                    }
                }
            }
            if (cells < 10) return (float.NaN, float.NaN, cells);
            double slope = (sw * sSC - sS * sC) / (sw * sSS - sS * sS);
            double angle = (alongX ? -Math.Atan(slope) : Math.Atan(slope)) * 180.0 / Math.PI;
            return ((float)angle, (float)((sC / sw - surface) * 100.0), cells);
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

        /// <summary>Node graph: each node's pose against the truth, with the graph and as local SLAM had it (mean, worst; metres).</summary>
        public (double graphMean, double graphMax, double localMean, double localMax) NodeErrors()
        {
            double gm = 0, gx = 0, lm = 0, lx = 0;
            int n = Mathf.Min(NodeTruth.Count, slam.Nodes.Count);
            for (int i = 0; i < n; i++)
            {
                var g = globalSlam.NodeGlobalPose(i);
                var t = NodeTruth[i];
                double eg = System.Math.Sqrt((g.x - t.position.x) * (g.x - t.position.x) + (g.y - t.position.y) * (g.y - t.position.y));
                double el = Vector2.Distance(slam.Nodes[i].pose.position, t.position);
                gm += eg / n;
                lm += el / n;
                gx = System.Math.Max(gx, eg);
                lx = System.Math.Max(lx, el);
            }
            return (gm, gx, lm, lx);
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

        /// <summary>
        /// How the robot's believed map compares with the true apartment, over every cell inside the outer walls: cells
        /// it believes are wall (traversability below 0.5) that are really floor, split into within 25 cm of a real wall
        /// (thick walls) and further out (ghosts); real wall-surface cells it believes passable (missed); and believed
        /// wall cells per real wall-surface cell (1 = as thin as the grid allows).
        /// </summary>
        public (int falseNear, int ghosts, int missed, float thickness) MapAccuracy()
        {
            const float cell = 0.05f, band = 0.25f;
            int falseNear = 0, ghosts = 0, missed = 0, believedWall = 0, surface = 0;
            var grid = writer.Grid;
            if (!grid.TryWorldToGridIndex(new Vector2(-3.1f, -2.1f), out int r0, out int c0)) return default;
            if (!grid.TryWorldToGridIndex(new Vector2(3.1f, 2.1f), out int r1, out int c1)) return default;
            for (int row = r0; row <= r1; row++)
            {
                for (int col = c0; col <= c1; col++)
                {
                    Vector2 p = grid.GridIndexToWorldCenter(row, col);
                    bool solid = !world.IsFree(p, 0f);
                    bool blocked = writer.Traversability(row, col) < 0.5f;
                    if (blocked) believedWall++;
                    if (solid)
                    {
                        bool isSurface = world.IsFree(p + new Vector2(cell, 0), 0f) || world.IsFree(p - new Vector2(cell, 0), 0f) ||
                                         world.IsFree(p + new Vector2(0, cell), 0f) || world.IsFree(p - new Vector2(0, cell), 0f);
                        if (isSurface)
                        {
                            surface++;
                            if (!blocked) missed++;
                        }
                    }
                    else if (blocked)
                    {
                        if (world.IsFree(p, band)) ghosts++;
                        else falseNear++;
                    }
                }
            }
            return (falseNear, ghosts, missed, surface > 0 ? (float)believedWall / surface : 0f);
        }

        static RobotPose Advance(RobotPose p, float forward, float turn) =>
            new RobotPose(p.position + RobotPose.DirectionOf(p.heading + turn * 0.5f) * forward, Mathf.DeltaAngle(0f, p.heading + turn));

        Vector2 PickGoal()
        {
            for (int tries = 0; tries < 1000; tries++)
            {
                var g = new Vector2((float)(goals.NextDouble() * 5.6 - 2.8), (float)(goals.NextDouble() * 3.6 - 1.8));
                if (MirrorGoals) g.x = -g.x;
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
