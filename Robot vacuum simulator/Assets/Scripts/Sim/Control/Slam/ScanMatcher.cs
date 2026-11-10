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

        /// <summary>Diagnostics (tests): hit-field matching on (1) or off (0) regardless of the constant; −1 = the constant.</summary>
        internal static int HitFieldOverride = -1;

        /// <summary>Diagnostics (tests): the hit kernel's σ instead of the constant (0 = the constant).</summary>
        internal static float HitFieldSigmaOverride = 0f;

        static bool UseHits => HitFieldOverride >= 0 ? HitFieldOverride == 1 : S.MatchAgainstHits;

        /// <summary>Diagnostics (tests): a weight per point for the fit (null = all 1).</summary>
        internal List<float> Weights;
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
                double scale = Weights != null ? occupiedScale * Weights[i] : occupiedScale;
                double r, dpdx, dpdy;
                if (UseHits)
                {
                    // r = scale · e, with e = exp(−D) falling as the point nears where hits landed: ∂r/∂w = scale · ∂e/∂w,
                    // written below as −scale · (−∂e/∂w) so the shared Jacobian code applies.
                    double e = HitResidual(wx, wy, out double dedx, out double dedy);
                    r = scale * e;
                    dpdx = -dedx;
                    dpdy = -dedy;
                }
                else
                {
                    double p = Interpolate(wx, wy, out dpdx, out dpdy);
                    r = scale * (1.0 - p);
                }
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
        /// <summary>
        /// Hit field: D = Σ over occupied cells whose mean hit lies within R = 2 cells of the point of
        /// w · exp(−d² / 2σ²) · (1 − d²/R²)², with d the distance to the cell's mean hit and w = min(hits, cap) / cap.
        /// The taper takes each cell's contribution smoothly to zero at R, and a 7 × 7 window always holds every cell
        /// that close, so the field and its gradient are continuous everywhere (a plain 5 × 5 window jumped as the point
        /// crossed a cell line). Returns e = exp(−D) (1 far from any hits, falling toward 0 where hits are dense) and its
        /// gradient. Hits gather symmetrically about a real surface, so D peaks there whatever the occupancy band's
        /// thickness or the lattice's position.
        /// </summary>
        internal double HitResidual(double wx, double wy, out double dedx, out double dedy)
        {
            double sigma = HitFieldSigmaOverride > 0f ? HitFieldSigmaOverride : S.HitFieldSigmaMeters;
            double inverseTwoSigmaSq = 1.0 / (2 * sigma * sigma), inverseSigmaSq = 1.0 / (sigma * sigma);
            double cell = 1.0 / inverseCell, radiusSq = 4 * cell * cell, inverseRadiusSq = 1.0 / radiusSq;
            int col0 = (int)Math.Floor((wx - ax) * inverseCell), row0 = (int)Math.Floor((wy - ay) * inverseCell);
            double D = 0, dDdx = 0, dDdy = 0;
            for (int dr = -3; dr <= 3; dr++)
            {
                for (int dc = -3; dc <= 3; dc++)
                {
                    if (!grid.TryIndex(row0 + dr, col0 + dc, out int index)) continue;
                    int n = grid.hitCount[index];
                    if (n == 0 || grid.probability[index] <= S.FreeProbability) continue;
                    double mx = ax + grid.hitSumX[index] / n, my = ay + grid.hitSumY[index] / n;
                    double dx = wx - mx, dy = wy - my, d2 = dx * dx + dy * dy;
                    if (d2 >= radiusSq) continue;
                    double w = Math.Min(n, S.HitFieldCountCap) / (double)S.HitFieldCountCap;
                    double g = Math.Exp(-d2 * inverseTwoSigmaSq);
                    double t = 1 - d2 * inverseRadiusSq, taper = t * t;
                    double k = w * g * taper;
                    D += k;
                    // d(k)/dx = w · [g' · taper + g · taper'], g' = −g·dx/σ², taper' = 2t · (−2dx/R²)
                    double dk = w * (-g * inverseSigmaSq * taper - g * 4 * t * inverseRadiusSq);
                    dDdx += dk * dx;
                    dDdy += dk * dy;
                }
            }
            double e = Math.Exp(-D);
            dedx = -e * dDdx;
            dedy = -e * dDdy;
            return e;
        }

        internal double Interpolate(double wx, double wy, out double dpdx, out double dpdy)
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
