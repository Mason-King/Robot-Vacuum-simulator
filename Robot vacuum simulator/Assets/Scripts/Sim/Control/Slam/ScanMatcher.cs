using System;
using System.Collections.Generic;
using UnityEngine;
using S = RobotVacuum.Sim.Control.LocalSlamConstants;

namespace RobotVacuum.Sim.Control
{
    /// <summary>
    /// SLAM ONLY: fits a scan to a submap by least squares (design doc, section 19; Cartographer's CeresScanMatcher2D).
    /// Minimises, over the pose (x, y, θ):
    ///
    ///   Σ_points (w_occ / √N · (1 − M(pose · point)))²  +  (w_t · |t − t₀|)²  +  (w_r · (θ − θ₀))²
    ///
    /// where M is the submap's occupancy probability, smoothly interpolated (bicubic, as Cartographer's Ceres grid
    /// interpolator: Catmull-Rom splines through the cell centres; unknown cells count as 0.1), and (t₀, θ₀) is the
    /// odometry guess. Solved with Levenberg–Marquardt on the 3 pose values. The fit lands between cells (no grid of
    /// candidates, so nothing pulls the answer toward a grid point), but it needs a guess within about a cell.
    /// </summary>
    public sealed class ScanMatcher
    {
        const float UnknownProbability = 0.1f; // Cartographer: kMaxCorrespondenceCost 0.9

        ProbabilityGrid grid;
        float inverseCell;
        double ax, ay; // anchor

        /// <summary>The last fit's iterations and its mean fit to the map (mean M over the points, 0–1).</summary>
        public int LastIterations { get; private set; }

        public float LastMeanProbability { get; private set; }

        /// <summary>
        /// Fits <paramref name="points"/> (robot-relative, metres) to <paramref name="map"/>, starting from and held
        /// toward <paramref name="guess"/>. Returns the fitted pose.
        /// </summary>
        public RobotPose Match(ProbabilityGrid map, List<Vector2> points, RobotPose guess, float translationWeight,
            float rotationWeight) => Match(map, points, guess, S.OccupiedSpaceWeight, translationWeight, rotationWeight);

        /// <param name="occupiedWeight">The fit's weight (loop closure's refinement uses its own).</param>
        public RobotPose Match(ProbabilityGrid map, List<Vector2> points, RobotPose guess, float occupiedWeight,
            float translationWeight, float rotationWeight)
        {
            grid = map;
            inverseCell = 1f / map.CellSizeMeters;
            ax = map.AnchorWorld.x;
            ay = map.AnchorWorld.y;

            int n = points.Count;
            double occupiedScale = occupiedWeight / Math.Sqrt(Math.Max(1, n));
            double tx0 = guess.position.x, ty0 = guess.position.y, th0 = guess.heading * Math.PI / 180.0;
            double tx = tx0, ty = ty0, th = th0;

            // Levenberg–Marquardt. Each trial step is evaluated with its gradient (JᵀJ, Jᵀr), so when the step is
            // accepted the next iteration already has them: the trial pose IS the next pose, and evaluating it again
            // (as the reference version below does) gives the same numbers. Same iterations, same result, about half
            // the evaluations.
            double lambda = 1e-3;
            double cost = Cost(points, tx, ty, th, tx0, ty0, th0, occupiedScale, translationWeight, rotationWeight, H, g);

            int iteration = 0;
            for (; iteration < S.MaxIterations; iteration++)
            {
                // (H + λ·diag(H)) δ = −g
                A[0] = H[0] * (1 + lambda); A[1] = H[1]; A[2] = H[2];
                A[3] = H[3]; A[4] = H[4] * (1 + lambda); A[5] = H[5];
                A[6] = H[6]; A[7] = H[7]; A[8] = H[8] * (1 + lambda);
                if (!Solve3(A, -g[0], -g[1], -g[2], out double dx, out double dy, out double dth)) break;

                double nextCost = Cost(points, tx + dx, ty + dy, th + dth, tx0, ty0, th0, occupiedScale, translationWeight, rotationWeight, trialH, trialG);
                if (nextCost < cost)
                {
                    tx += dx;
                    ty += dy;
                    th += dth;
                    cost = nextCost;
                    (H, trialH) = (trialH, H);
                    (g, trialG) = (trialG, g);
                    lambda = Math.Max(1e-9, lambda * 0.1);
                    if (Math.Abs(dx) < S.ConvergedStep && Math.Abs(dy) < S.ConvergedStep && Math.Abs(dth) < S.ConvergedStep) break;
                }
                else
                {
                    lambda *= 10;
                    if (lambda > 1e6) break;
                }
            }

            LastIterations = iteration;
            LastMeanProbability = MeanProbability(points, tx, ty, th);

            return new RobotPose(new Vector2((float)tx, (float)ty), Mathf.DeltaAngle(0f, (float)(th * 180.0 / Math.PI)));
        }

        // The current pose's JᵀJ and Jᵀr, the trial pose's, and the damped system (kept, so a fit allocates nothing).
        double[] H = new double[9], g = new double[3], trialH = new double[9], trialG = new double[3];
        readonly double[] A = new double[9];

        /// <summary>
        /// Tests only: the original fit, which evaluates the current pose again at the start of every iteration (and
        /// reads the map through <see cref="InterpolateReference"/>), kept so <see cref="Match(ProbabilityGrid, List{Vector2}, RobotPose, float, float, float)"/>
        /// can be checked against it bit for bit.
        /// </summary>
        internal RobotPose MatchReference(ProbabilityGrid map, List<Vector2> points, RobotPose guess, float occupiedWeight,
            float translationWeight, float rotationWeight)
        {
            grid = map;
            inverseCell = 1f / map.CellSizeMeters;
            ax = map.AnchorWorld.x;
            ay = map.AnchorWorld.y;

            int n = points.Count;
            double occupiedScale = occupiedWeight / Math.Sqrt(Math.Max(1, n));
            double tx0 = guess.position.x, ty0 = guess.position.y, th0 = guess.heading * Math.PI / 180.0;
            double tx = tx0, ty = ty0, th = th0;

            referenceInterpolation = true;
            double lambda = 1e-3;
            double cost = Cost(points, tx, ty, th, tx0, ty0, th0, occupiedScale, translationWeight, rotationWeight, null, null);
            var H = new double[9];
            var g = new double[3];

            int iteration = 0;
            for (; iteration < S.MaxIterations; iteration++)
            {
                Cost(points, tx, ty, th, tx0, ty0, th0, occupiedScale, translationWeight, rotationWeight, H, g);

                // (H + λ·diag(H)) δ = −g
                double[] A =
                {
                    H[0] * (1 + lambda), H[1], H[2],
                    H[3], H[4] * (1 + lambda), H[5],
                    H[6], H[7], H[8] * (1 + lambda),
                };
                if (!Solve3(A, -g[0], -g[1], -g[2], out double dx, out double dy, out double dth)) break;

                double nextCost = Cost(points, tx + dx, ty + dy, th + dth, tx0, ty0, th0, occupiedScale, translationWeight, rotationWeight, null, null);
                if (nextCost < cost)
                {
                    tx += dx;
                    ty += dy;
                    th += dth;
                    cost = nextCost;
                    lambda = Math.Max(1e-9, lambda * 0.1);
                    if (Math.Abs(dx) < S.ConvergedStep && Math.Abs(dy) < S.ConvergedStep && Math.Abs(dth) < S.ConvergedStep) break;
                }
                else
                {
                    lambda *= 10;
                    if (lambda > 1e6) break;
                }
            }

            referenceInterpolation = false;
            LastIterations = iteration;
            LastMeanProbability = MeanProbability(points, tx, ty, th);
            return new RobotPose(new Vector2((float)tx, (float)ty), Mathf.DeltaAngle(0f, (float)(th * 180.0 / Math.PI)));
        }

        /// <summary>The cost; if <paramref name="H"/> is given, also JᵀJ (3×3) and Jᵀr (3) at this pose.</summary>
        double Cost(List<Vector2> points, double tx, double ty, double th, double tx0, double ty0, double th0,
            double occupiedScale, double translationWeight, double rotationWeight, double[] H, double[] g)
        {
            double c = Math.Cos(th), s = Math.Sin(th);
            double sum = 0;
            if (H != null)
            {
                Array.Clear(H, 0, 9);
                Array.Clear(g, 0, 3);
            }

            for (int i = 0; i < points.Count; i++)
            {
                double px = points[i].x, py = points[i].y;
                double wx = tx + c * px - s * py, wy = ty + s * px + c * py;
                double scale = occupiedScale;
                double p = Interpolate(wx, wy, out double dpdx, out double dpdy);
                double r = scale * (1.0 - p);
                sum += r * r;

                if (H != null)
                {
                    // dr/d(tx, ty, θ) = −scale · ∇M · d(world point)/d(tx, ty, θ)
                    double jx = -scale * dpdx;
                    double jy = -scale * dpdy;
                    double jt = -scale * (dpdx * (-s * px - c * py) + dpdy * (c * px - s * py));
                    Accumulate(H, g, jx, jy, jt, r);
                }
            }

            double rx = translationWeight * (tx - tx0), ry = translationWeight * (ty - ty0), rt = rotationWeight * (th - th0);
            sum += rx * rx + ry * ry + rt * rt;
            if (H != null)
            {
                Accumulate(H, g, translationWeight, 0, 0, rx);
                Accumulate(H, g, 0, translationWeight, 0, ry);
                Accumulate(H, g, 0, 0, rotationWeight, rt);
            }
            return sum;
        }

        static void Accumulate(double[] H, double[] g, double j0, double j1, double j2, double r)
        {
            H[0] += j0 * j0; H[1] += j0 * j1; H[2] += j0 * j2;
            H[3] += j1 * j0; H[4] += j1 * j1; H[5] += j1 * j2;
            H[6] += j2 * j0; H[7] += j2 * j1; H[8] += j2 * j2;
            g[0] += j0 * r;
            g[1] += j1 * r;
            g[2] += j2 * r;
        }

        float MeanProbability(List<Vector2> points, double tx, double ty, double th)
        {
            if (points.Count == 0) return 0f;
            double c = Math.Cos(th), s = Math.Sin(th), sum = 0;
            for (int i = 0; i < points.Count; i++)
            {
                double px = points[i].x, py = points[i].y;
                sum += Interpolate(tx + c * px - s * py, ty + s * px + c * py, out _, out _);
            }
            return (float)(sum / points.Count);
        }

        // ---- Bicubic interpolation over cell centres -----------------------------------------------------

        /// <summary>The occupancy probability at a world point, and its gradient (per metre).</summary>
        internal double Interpolate(double wx, double wy, out double dpdx, out double dpdy)
        {
            if (referenceInterpolation) return InterpolateReference(wx, wy, out dpdx, out dpdy);

            // Continuous index with cell centres at whole numbers.
            double u = (wx - ax) * inverseCell - 0.5, v = (wy - ay) * inverseCell - 0.5;
            int c0 = (int)Math.Floor(u), r0 = (int)Math.Floor(v);
            double fu = u - c0, fv = v - r0;

            // The 4 × 4 cells around the point, read straight from the grid's array when they all lie inside it (nearly
            // always: the grid is padded); otherwise as the reference does. The same values in the same splines.
            int top = r0 - 1 - grid.minRow, left = c0 - 1 - grid.minCol;
            if (top < 0 || left < 0 || top + 4 > grid.rows || left + 4 > grid.cols)
                return InterpolateReference(wx, wy, out dpdx, out dpdy);

            var p = grid.probability;
            int cols = grid.cols, i = top * cols + left;
            Spline(Known(p[i]), Known(p[i + 1]), Known(p[i + 2]), Known(p[i + 3]), fu, out double f0, out double d0);
            i += cols;
            Spline(Known(p[i]), Known(p[i + 1]), Known(p[i + 2]), Known(p[i + 3]), fu, out double f1, out double d1);
            i += cols;
            Spline(Known(p[i]), Known(p[i + 1]), Known(p[i + 2]), Known(p[i + 3]), fu, out double f2, out double d2);
            i += cols;
            Spline(Known(p[i]), Known(p[i + 1]), Known(p[i + 2]), Known(p[i + 3]), fu, out double f3, out double d3);

            Spline(f0, f1, f2, f3, fv, out double value, out double dv);
            Spline(d0, d1, d2, d3, fv, out double du, out _);
            dpdx = du * inverseCell;
            dpdy = dv * inverseCell;
            return value;
        }

        /// <summary>A cell's probability as the interpolation reads it (unknown = <see cref="UnknownProbability"/>), as <see cref="Value"/> does.</summary>
        static double Known(float p) => p > 0f ? p : UnknownProbability;

        bool referenceInterpolation; // MatchReference: read the map the original way throughout

        /// <summary>The original interpolation, a cell at a time through <see cref="ProbabilityGrid.Probability"/> (and for points at the grid's edge).</summary>
        internal double InterpolateReference(double wx, double wy, out double dpdx, out double dpdy)
        {
            // Continuous index with cell centres at whole numbers.
            double u = (wx - ax) * inverseCell - 0.5, v = (wy - ay) * inverseCell - 0.5;
            int c0 = (int)Math.Floor(u), r0 = (int)Math.Floor(v);
            double fu = u - c0, fv = v - r0;

            double f0 = 0, f1 = 0, f2 = 0, f3 = 0, d0 = 0, d1 = 0, d2 = 0, d3 = 0;
            for (int k = 0; k < 4; k++)
            {
                int row = r0 - 1 + k;
                Spline(Value(row, c0 - 1), Value(row, c0), Value(row, c0 + 1), Value(row, c0 + 2), fu, out double f, out double df);
                switch (k)
                {
                    case 0: f0 = f; d0 = df; break;
                    case 1: f1 = f; d1 = df; break;
                    case 2: f2 = f; d2 = df; break;
                    default: f3 = f; d3 = df; break;
                }
            }

            Spline(f0, f1, f2, f3, fv, out double value, out double dv);
            Spline(d0, d1, d2, d3, fv, out double du, out _);
            dpdx = du * inverseCell;
            dpdy = dv * inverseCell;
            return value;
        }

        double Value(int row, int col)
        {
            float p = grid.Probability(row, col);
            return p > 0f ? p : UnknownProbability;
        }

        /// <summary>Catmull-Rom (cubic Hermite with central differences), as Ceres' CubicHermiteSpline: value and derivative at x ∈ [0, 1] between p1 and p2.</summary>
        static void Spline(double p0, double p1, double p2, double p3, double x, out double f, out double dfdx)
        {
            double a = 0.5 * (-p0 + 3.0 * p1 - 3.0 * p2 + p3);
            double b = 0.5 * (2.0 * p0 - 5.0 * p1 + 4.0 * p2 - p3);
            double c = 0.5 * (-p0 + p2);
            f = p1 + x * (c + x * (b + x * a));
            dfdx = c + x * (2.0 * b + 3.0 * a * x);
        }

        static bool Solve3(double[] m, double b0, double b1, double b2, out double x0, out double x1, out double x2)
        {
            double a = m[0], b = m[1], c = m[2], d = m[3], e = m[4], f = m[5], g = m[6], h = m[7], i = m[8];
            double A = e * i - f * h, B = -(d * i - f * g), C = d * h - e * g;
            double det = a * A + b * B + c * C;
            if (Math.Abs(det) < 1e-300)
            {
                x0 = x1 = x2 = 0;
                return false;
            }
            double inv = 1.0 / det;
            x0 = (A * b0 - (b * i - c * h) * b1 + (b * f - c * e) * b2) * inv;
            x1 = (B * b0 + (a * i - c * g) * b1 - (a * f - c * d) * b2) * inv;
            x2 = (C * b0 - (a * h - b * g) * b1 + (a * e - b * d) * b2) * inv;
            return true;
        }
    }
}
