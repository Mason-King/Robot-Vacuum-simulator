using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using RobotVacuum.Sim.Control;
using RobotVacuum.Sim.Sensors;
using UnityEngine;

namespace RobotVacuum.Tests
{
    /// <summary>
    /// SLAM route 2 (raw-scan back end), stage 0: is matching one raw scan straight against another raw scan unbiased?
    /// No grid and no map in between. Point-to-line ICP: each return of the newer scan is pulled onto the line through
    /// the nearest returns of the older scan. Gate: scale bias within ±0.1% on every level (grid matching today: −0.3%
    /// to −1%). Test-only; nothing here runs in the simulation. See claude/SLAM_Status_2026-11-24.md.
    /// </summary>
    public class RawScanMatchTests
    {
        /// <summary>One scan as raw points in its own frame (heading 0 = +y, as RobotPose.DirectionOf), with line normals.</summary>
        sealed class RawScan
        {
            public RobotPose truth;
            public double[] x, y, range, nx, ny;
            public bool[] hasNormal;
            public bool[] edge;               // a return next to a gap or a range jump (where a wall ends in this scan)
            public double[] rayRange = new double[360]; // measured range per ray index (0 = no return)
            public readonly Dictionary<long, List<int>> cells = new Dictionary<long, List<int>>();
        }

        const double HashCell = 0.2;

        static long Key(int ix, int iy) => ((long)ix << 32) ^ (uint)iy;

        static RawScan Build(SensorFrame frame, RobotPose truth, float maxRange)
        {
            // Rays come in bearing order around the circle (0…180, then −179…−1), so neighbours by index are neighbours
            // by angle, with index 359 next to index 0.
            var rays = frame.lidarRays;
            int n = rays.Count;
            var index = new int[n];
            var px = new List<double>();
            var py = new List<double>();
            var pr = new List<double>();
            for (int i = 0; i < n; i++)
            {
                index[i] = -1;
                var ray = rays[i];
                if (!ray.hit || ray.range > maxRange) continue;
                Vector2 p = RobotPose.DirectionOf(ray.bearing) * ray.range;
                index[i] = px.Count;
                px.Add(p.x);
                py.Add(p.y);
                pr.Add(ray.range);
            }
            int m = px.Count;
            var scan = new RawScan
            {
                truth = truth, x = px.ToArray(), y = py.ToArray(), range = pr.ToArray(),
                nx = new double[m], ny = new double[m], hasNormal = new bool[m], edge = new bool[m],
            };
            for (int i = 0; i < n && i < 360; i++) scan.rayRange[i] = index[i] >= 0 ? scan.range[index[i]] : 0;
            for (int i = 0; i < n; i++)
            {
                int k = index[i];
                if (k < 0) continue;
                foreach (int d in new[] { -1, 1 })
                {
                    int j = index[((i + d) % n + n) % n];
                    if (j < 0 || Math.Abs(scan.range[j] - scan.range[k]) > Math.Max(0.1, 3 * scan.range[k] * Math.PI / 180.0))
                        scan.edge[k] = true;
                }
            }

            // Normal at each point: total least squares through the returns within ±3 rays that lie close by.
            for (int i = 0; i < n; i++)
            {
                int k = index[i];
                if (k < 0) continue;
                double reach = Math.Max(0.15, 4 * scan.range[k] * Math.PI / 180.0);
                double sx = 0, sy = 0;
                var near = new List<int>(7);
                for (int d = -3; d <= 3; d++)
                {
                    int j = index[((i + d) % n + n) % n];
                    if (j < 0) continue;
                    double dx = scan.x[j] - scan.x[k], dy = scan.y[j] - scan.y[k];
                    if (dx * dx + dy * dy > reach * reach) continue;
                    near.Add(j);
                    sx += scan.x[j];
                    sy += scan.y[j];
                }
                if (near.Count < 3) continue;
                double mx = sx / near.Count, my = sy / near.Count, cxx = 0, cxy = 0, cyy = 0;
                foreach (int j in near)
                {
                    double dx = scan.x[j] - mx, dy = scan.y[j] - my;
                    cxx += dx * dx;
                    cxy += dx * dy;
                    cyy += dy * dy;
                }
                double mean = (cxx + cyy) / 2, spread = Math.Sqrt((cxx - cyy) * (cxx - cyy) / 4 + cxy * cxy);
                double small = mean - spread, large = mean + spread;
                if (large <= 0 || small > 0.3 * large) continue; // not line-like
                // Eigenvector of the small eigenvalue = the line's normal.
                double vx, vy;
                if (Math.Abs(cxy) > 1e-15) { vx = small - cyy; vy = cxy; }
                else if (cxx < cyy) { vx = 1; vy = 0; }
                else { vx = 0; vy = 1; }
                double length = Math.Sqrt(vx * vx + vy * vy);
                if (length <= 0) continue;
                scan.nx[k] = vx / length;
                scan.ny[k] = vy / length;
                scan.hasNormal[k] = true;
            }

            for (int k = 0; k < m; k++)
            {
                long key = Key((int)Math.Floor(scan.x[k] / HashCell), (int)Math.Floor(scan.y[k] / HashCell));
                if (!scan.cells.TryGetValue(key, out var list)) scan.cells[key] = list = new List<int>(4);
                list.Add(k);
            }
            return scan;
        }

        static int Nearest(RawScan a, double x, double y, double maxDistance, bool needNormal)
        {
            int cx = (int)Math.Floor(x / HashCell), cy = (int)Math.Floor(y / HashCell);
            int best = -1;
            double bestSq = maxDistance * maxDistance;
            for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                {
                    if (!a.cells.TryGetValue(Key(cx + dx, cy + dy), out var list)) continue;
                    foreach (int k in list)
                    {
                        if (needNormal && !a.hasNormal[k]) continue;
                        double ex = a.x[k] - x, ey = a.y[k] - y, d2 = ex * ex + ey * ey;
                        if (d2 < bestSq) { bestSq = d2; best = k; }
                    }
                }
            return best;
        }

        /// <summary>
        /// Could scan A have seen point (x, y) of its own frame? Its ray at that bearing must have a return, at about that
        /// range: no return there means out of range or a dropout; a much nearer return means hidden behind something;
        /// a much farther one means the spot is open floor to A.
        /// </summary>
        static bool VisibleTo(RawScan a, double x, double y)
        {
            double range = Math.Sqrt(x * x + y * y);
            double bearing = Math.Atan2(-x, y) * 180.0 / Math.PI; // RobotPose.HeadingOf convention
            int ray = ((int)Math.Round(bearing) % 360 + 360) % 360;
            double tolerance = 3 * Sigma(range) + 0.05;
            for (int d = -1; d <= 1; d++)
            {
                double measured = a.rayRange[((ray + d) % 360 + 360) % 360];
                if (measured > 0 && Math.Abs(measured - range) <= tolerance) return true;
            }
            return false;
        }

        static double Sigma(double range) => 0.01 + 0.01 * range; // the simulated lidar's range σ

        /// <summary>Point-to-line ICP: B's pose in A's frame (x, y, θ radians CCW), from a guess.</summary>
        static bool Match(RawScan a, RawScan b, double gx, double gy, double gth, bool noiseWeights, bool visibility,
            out double tx, out double ty, out double th, out int used)
        {
            tx = gx; ty = gy; th = gth; used = 0;
            var H = new double[9];
            var g = new double[3];
            for (int iteration = 0; iteration < 40; iteration++)
            {
                double maxDistance = iteration < 10 ? 0.3 : iteration < 20 ? 0.15 : 0.1;
                Array.Clear(H, 0, 9);
                Array.Clear(g, 0, 3);
                double c = Math.Cos(th), s = Math.Sin(th);
                used = 0;
                for (int j = 0; j < b.x.Length; j++)
                {
                    double bx = b.x[j], by = b.y[j];
                    double px = tx + c * bx - s * by, py = ty + s * bx + c * by;
                    if (visibility && !VisibleTo(a, px, py)) continue;
                    int k = Nearest(a, px, py, maxDistance, true);
                    if (k < 0 || (visibility && a.edge[k])) continue;
                    double nx = a.nx[k], ny = a.ny[k];
                    double e = nx * (px - a.x[k]) + ny * (py - a.y[k]);
                    double sigmaSq = Sigma(a.range[k]) * Sigma(a.range[k]) + Sigma(b.range[j]) * Sigma(b.range[j]);
                    double w = noiseWeights ? 1.0 / sigmaSq : 1.0;
                    double huber = noiseWeights ? 2 * Math.Sqrt(sigmaSq) : 0.05;
                    if (Math.Abs(e) > huber) w *= huber / Math.Abs(e);
                    double j0 = nx, j1 = ny, j2 = nx * (-s * bx - c * by) + ny * (c * bx - s * by);
                    H[0] += w * j0 * j0; H[1] += w * j0 * j1; H[2] += w * j0 * j2;
                    H[4] += w * j1 * j1; H[5] += w * j1 * j2; H[8] += w * j2 * j2;
                    g[0] += w * j0 * e; g[1] += w * j1 * e; g[2] += w * j2 * e;
                    used++;
                }
                if (used < 30) return false;
                H[3] = H[1]; H[6] = H[2]; H[7] = H[5];
                for (int d = 0; d < 9; d += 4) H[d] += 1e-9;
                if (!Solve3(H, -g[0], -g[1], -g[2], out double dx, out double dy, out double dth)) return false;
                tx += dx;
                ty += dy;
                th += dth;
                if (iteration >= 20 && Math.Abs(dx) < 1e-6 && Math.Abs(dy) < 1e-6 && Math.Abs(dth) < 1e-7) break;
            }
            return true;
        }

        static bool Solve3(double[] A, double b0, double b1, double b2, out double x0, out double x1, out double x2)
        {
            double det = A[0] * (A[4] * A[8] - A[5] * A[7]) - A[1] * (A[3] * A[8] - A[5] * A[6]) + A[2] * (A[3] * A[7] - A[4] * A[6]);
            x0 = x1 = x2 = 0;
            if (Math.Abs(det) < 1e-30) return false;
            x0 = (b0 * (A[4] * A[8] - A[5] * A[7]) - A[1] * (b1 * A[8] - A[5] * b2) + A[2] * (b1 * A[7] - A[4] * b2)) / det;
            x1 = (A[0] * (b1 * A[8] - A[5] * b2) - b0 * (A[3] * A[8] - A[5] * A[6]) + A[2] * (A[3] * b2 - b1 * A[6])) / det;
            x2 = (A[0] * (A[4] * b2 - b1 * A[7]) - A[1] * (A[3] * b2 - b1 * A[6]) + b0 * (A[3] * A[7] - A[4] * A[6])) / det;
            return true;
        }

        /// <summary>B's true pose in A's frame.</summary>
        static void TrueRelative(RobotPose a, RobotPose b, out double x, out double y, out double th)
        {
            double h = a.heading * Math.PI / 180.0, c = Math.Cos(h), s = Math.Sin(h);
            double dx = b.position.x - a.position.x, dy = b.position.y - a.position.y;
            x = c * dx + s * dy;
            y = -s * dx + c * dy;
            th = Mathf.DeltaAngle(a.heading, b.heading) * Math.PI / 180.0;
        }

        /// <summary>Share of B's returns that land (at the true relative pose) within 10 cm of one of A's returns.</summary>
        static double Overlap(RawScan a, RawScan b, double x, double y, double th)
        {
            if (b.x.Length == 0) return 0;
            double c = Math.Cos(th), s = Math.Sin(th);
            int hits = 0;
            for (int j = 0; j < b.x.Length; j++)
                if (Nearest(a, x + c * b.x[j] - s * b.y[j], y + s * b.x[j] + c * b.y[j], 0.1, false) >= 0) hits++;
            return hits / (double)b.x.Length;
        }

        static double Gauss(System.Random r, double sigma) =>
            sigma * Math.Sqrt(-2.0 * Math.Log(1.0 - r.NextDouble())) * Math.Cos(2.0 * Math.PI * r.NextDouble());

        sealed class Stats
        {
            readonly List<double> v = new List<double>();
            public void Add(double x) => v.Add(x);
            public int Count => v.Count;
            public double Mean => v.Count > 0 ? v.Average() : double.NaN;
            public double Rms => v.Count > 0 ? Math.Sqrt(v.Average(x => x * x)) : double.NaN;
            public double StandardError
            {
                get
                {
                    if (v.Count < 2) return double.NaN;
                    double m = Mean;
                    return Math.Sqrt(v.Sum(x => (x - m) * (x - m)) / (v.Count - 1) / v.Count);
                }
            }
        }

        /// <summary>
        /// Stage 0 measurement. Per level (apartment, Enclosed Test, My Room 01): 4 seeds × 300 s of harness driving for
        /// realistic poses; at each pose a fresh noisy scan (the simulated lidar's model). Pairs 1–2, 2–3 and 3–5 m apart
        /// (true distance), sharing at least 30% of their returns, matched from a guess 3 cm / 1° off. Reports the scale
        /// error (error along the true motion ÷ its length), lateral and heading error, per distance band, for four ways:
        /// v1 nearest-point pairing (noisy and exact) for reference; then pairing only points the older scan could have
        /// seen, away from where its walls end (noise-weighted, exact, unweighted). Round 1 (2026-11-24): v1 leaned
        /// −0.47 to −0.65% even on exact scans (pairs reaching into ground the older scan never saw). GATE: the
        /// visibility-checked, noise-weighted scale bias within ±0.1% on every level.
        /// </summary>
        [Explicit, Test, Timeout(7_200_000)]
        public void Stage0_RawScanMatchBias()
        {
            var rows = new (string name, bool weights, float maxRange, bool exact, bool visibility)[]
            {
                ("v1 nearest-point pairing, noise-weighted", true, 8f, false, false),
                ("v1 nearest-point pairing, exact scans", true, 8f, true, false),
                ("VISIBILITY-CHECKED pairing, noise-weighted", true, 8f, false, true),
                ("visibility-checked, exact scans (method's own bias)", true, 8f, true, true),
                ("visibility-checked, unweighted", false, 8f, false, true),
            };
            var bands = new (double lo, double hi)[] { (1, 2), (2, 3), (3, 5) };
            var levels = new (string, Func<SlamWorld>)[] { ("Apartment", SlamWorld.Apartment), ("Enclosed Test", SlamWorld.EnclosedTest), ("My Room 01", SlamWorld.MyRoom01) };

            foreach (var (label, makeWorld) in levels)
            {
                // Poses: where the harness robot really was at each scan.
                var poses = new List<List<RobotPose>>();
                for (int seed = 1; seed <= 4; seed++)
                {
                    var harness = new SlamHarness(seed, makeWorld(), loopClosure: false);
                    var list = new List<RobotPose>();
                    int steps = 0;
                    harness.OnStep = (b, t, time) => { if (steps++ % 5 == 0) list.Add(t); };
                    harness.Run(300f);
                    poses.Add(list);
                }
                var world = makeWorld();

                foreach (var (name, weights, maxRange, exact, visibility) in rows)
                {
                    var scale = bands.Select(_ => new Stats()).ToArray();
                    var all = new Stats();
                    var lateral = new Stats();
                    var heading = new Stats();
                    var along = new Stats();
                    int tried = 0, failed = 0, gross = 0;
                    for (int seed = 1; seed <= 4; seed++)
                    {
                        var random = new RandomProvider((uint)(seed * 7919 + 13));
                        var guessNoise = new System.Random(seed * 104729 + 7);
                        var truths = poses[seed - 1];
                        var scans = truths.Select(t => Build(world.Scan(t, exact ? null : random), t, maxRange)).ToList();
                        for (int i = 0; i < scans.Count; i += 15)
                        {
                            for (int band = 0; band < bands.Length; band++)
                            {
                                for (int j = i + 1; j < Math.Min(scans.Count, i + 600); j++)
                                {
                                    double d = Vector2.Distance(truths[i].position, truths[j].position);
                                    if (d < bands[band].lo || d >= bands[band].hi) continue;
                                    TrueRelative(truths[i], truths[j], out double rx, out double ry, out double rth);
                                    if (Overlap(scans[i], scans[j], rx, ry, rth) < 0.3) continue;
                                    tried++;
                                    bool ok = Match(scans[i], scans[j], rx + Gauss(guessNoise, 0.03), ry + Gauss(guessNoise, 0.03),
                                        rth + Gauss(guessNoise, Math.PI / 180.0), weights, visibility, out double ex, out double ey, out double eth, out _);
                                    if (!ok) { failed++; break; }
                                    double ux = rx / d, uy = ry / d, errX = ex - rx, errY = ey - ry;
                                    if (Math.Sqrt(errX * errX + errY * errY) > 0.10) { gross++; break; }
                                    double a = errX * ux + errY * uy;
                                    scale[band].Add(a / d);
                                    all.Add(a / d);
                                    along.Add(a);
                                    lateral.Add(errX * -uy + errY * ux);
                                    heading.Add((eth - rth) * 180.0 / Math.PI);
                                    break; // one partner per band per anchor
                                }
                            }
                        }
                    }
                    string bandText = string.Join(", ", bands.Select((b, k) =>
                        $"{b.lo:F0}–{b.hi:F0} m {scale[k].Mean * 100:+0.000;-0.000}% ±{scale[k].StandardError * 100:F3} (n {scale[k].Count})"));
                    bool pass = Math.Abs(all.Mean) <= 0.001;
                    TestContext.WriteLine($"{label} | {name}: SCALE {all.Mean * 100:+0.000;-0.000}% ±{all.StandardError * 100:F3} [{bandText}] | " +
                                          $"along rms {along.Rms * 100:F2} cm, lateral mean {lateral.Mean * 100:+0.00;-0.00} cm, heading mean {heading.Mean:+0.000;-0.000}° rms {heading.Rms:F3}° | " +
                                          $"{tried} pairs, {failed} no fit, {gross} off > 10 cm" + (weights && !exact && visibility ? $" | GATE {(pass ? "PASS" : "FAIL")}" : ""));
                }
            }
        }
    }
}
