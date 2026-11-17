using System;
using System.Collections.Generic;
using L = RobotVacuum.Sim.Control.PoseGraphConstants;

namespace RobotVacuum.Sim.Control
{
    /// <summary>
    /// A 2D pose in double precision for the pose graph: x, y in metres, heading in degrees (0 = +y, counter-clockwise,
    /// the same convention as <see cref="RobotPose"/>). Relative poses use the standard rigid-transform algebra, which
    /// is consistent for any fixed heading convention.
    /// </summary>
    public struct GraphPose
    {
        public double x, y, heading;

        public GraphPose(double x, double y, double heading)
        {
            this.x = x;
            this.y = y;
            this.heading = heading;
        }

        public static GraphPose From(RobotPose p) => new GraphPose(p.position.x, p.position.y, p.heading);

        public RobotPose ToRobotPose() =>
            new RobotPose(new UnityEngine.Vector2((float)x, (float)y), UnityEngine.Mathf.DeltaAngle(0f, (float)heading));

        /// <summary><paramref name="b"/> as seen from <paramref name="a"/> (a⁻¹ ∘ b).</summary>
        public static GraphPose Relative(GraphPose a, GraphPose b)
        {
            double t = a.heading * Math.PI / 180.0, c = Math.Cos(t), s = Math.Sin(t);
            double dx = b.x - a.x, dy = b.y - a.y;
            return new GraphPose(c * dx + s * dy, -s * dx + c * dy, WrapDegrees(b.heading - a.heading));
        }

        /// <summary>a ∘ r: the pose <paramref name="r"/> (given relative to a) in world terms.</summary>
        public static GraphPose Compose(GraphPose a, GraphPose r)
        {
            double t = a.heading * Math.PI / 180.0, c = Math.Cos(t), s = Math.Sin(t);
            return new GraphPose(a.x + c * r.x - s * r.y, a.y + s * r.x + c * r.y, WrapDegrees(a.heading + r.heading));
        }

        /// <summary>An angle in degrees, wrapped to [-180, 180).</summary>
        public static double WrapDegrees(double degrees)
        {
            double w = (degrees + 180.0) % 360.0;
            if (w < 0) w += 360.0;
            return w - 180.0;
        }
    }

    /// <summary>A measured relative pose between two keyframes, with its uncertainty (σ).</summary>
    public readonly struct GraphLink
    {
        public readonly int from, to;
        public readonly GraphPose measured; // pose of 'to' as seen from 'from'
        public readonly double sigmaMeters, sigmaDegrees;
        public readonly bool revisit;

        public GraphLink(int from, int to, GraphPose measured, double sigmaMeters, double sigmaDegrees, bool revisit)
        {
            this.from = from;
            this.to = to;
            this.measured = measured;
            this.sigmaMeters = sigmaMeters;
            this.sigmaDegrees = sigmaDegrees;
            this.revisit = revisit;
        }
    }

    /// <summary>
    /// Least-squares pose graph (design doc, section 18): Gauss-Newton over every keyframe pose, the first held fixed
    /// (the dock is known exactly). Each iteration solves the normal equations H·dx = −b EXACTLY by sparse block
    /// elimination: the poses are eliminated in minimum-degree order (a keyframe chain with revisit links is sparse,
    /// so the elimination stays cheap), then back-substituted. Same answer as a dense solve, to rounding.
    /// </summary>
    public sealed class PoseGraph
    {
        // ---- Per-solve scratch (kept between calls to avoid garbage) ----
        double[][] diagonal = new double[0][];   // 3×3 row-major per pose
        double[][] rhs = new double[0][];        // 3 per pose
        Dictionary<int, double[]>[] off = new Dictionary<int, double[]>[0];
        int[] order = new int[0];
        double[] step = new double[0];

        /// <summary>Huber threshold for links marked revisit (loop closures), in σ; 0 = plain least squares.</summary>
        public double HuberSigmas { get; set; }

        /// <summary>Iterations used by the last <see cref="Optimise"/>.</summary>
        public int LastIterations { get; private set; }

        /// <summary>
        /// Moves <paramref name="poses"/> to best fit <paramref name="links"/> (pose 0 fixed). Returns the largest
        /// distance any pose moved, metres.
        /// </summary>
        public double Optimise(List<GraphPose> poses, List<GraphLink> links)
        {
            int n = poses.Count;
            LastIterations = 0;
            if (n < 2 || links.Count == 0) return 0;

            var start = poses.ToArray();
            Ensure(n);
            ComputeOrder(n, links);

            for (int iteration = 0; iteration < L.MaxIterations; iteration++)
            {
                LastIterations = iteration + 1;
                BuildSystem(poses, links, n);
                Eliminate(n);

                double largest = 0;
                for (int i = 0; i < n; i++)
                {
                    var p = poses[i];
                    p.x += step[3 * i];
                    p.y += step[3 * i + 1];
                    p.heading = GraphPose.WrapDegrees(p.heading + step[3 * i + 2] * 180.0 / Math.PI);
                    poses[i] = p;
                    for (int k = 0; k < 3; k++) largest = Math.Max(largest, Math.Abs(step[3 * i + k]));
                }
                if (largest < L.ConvergedStep) break;
            }

            double moved = 0;
            for (int i = 0; i < n; i++)
            {
                double dx = poses[i].x - start[i].x, dy = poses[i].y - start[i].y;
                moved = Math.Max(moved, Math.Sqrt(dx * dx + dy * dy));
            }
            return moved;
        }

        void Ensure(int n)
        {
            if (diagonal.Length >= n) return;
            int size = Math.Max(n, diagonal.Length * 2);
            Array.Resize(ref diagonal, size);
            Array.Resize(ref rhs, size);
            Array.Resize(ref off, size);
            Array.Resize(ref order, size);
            Array.Resize(ref step, 3 * size);
            for (int i = 0; i < size; i++)
            {
                if (diagonal[i] == null) diagonal[i] = new double[9];
                if (rhs[i] == null) rhs[i] = new double[3];
                if (off[i] == null) off[i] = new Dictionary<int, double[]>();
            }
        }

        // ---- Ordering: greedy minimum degree on the link graph (ties: lowest index) ----

        void ComputeOrder(int n, List<GraphLink> links)
        {
            var neighbours = new HashSet<int>[n];
            for (int i = 0; i < n; i++) neighbours[i] = new HashSet<int>();
            foreach (var link in links)
            {
                if (link.from == link.to) continue;
                neighbours[link.from].Add(link.to);
                neighbours[link.to].Add(link.from);
            }

            var done = new bool[n];
            var list = new List<int>();
            for (int k = 0; k < n; k++)
            {
                int best = -1, bestDegree = int.MaxValue;
                for (int i = 0; i < n; i++)
                {
                    if (done[i]) continue;
                    int degree = neighbours[i].Count;
                    if (degree < bestDegree)
                    {
                        best = i;
                        bestDegree = degree;
                    }
                }

                order[k] = best;
                done[best] = true;

                // Eliminating 'best' joins all its neighbours to each other (the fill).
                list.Clear();
                list.AddRange(neighbours[best]);
                foreach (int a in list)
                {
                    neighbours[a].Remove(best);
                    foreach (int b in list)
                        if (a != b) neighbours[a].Add(b);
                }
                neighbours[best].Clear();
            }
        }

        // ---- The normal equations ----

        void BuildSystem(List<GraphPose> poses, List<GraphLink> links, int n)
        {
            for (int i = 0; i < n; i++)
            {
                Array.Clear(diagonal[i], 0, 9);
                Array.Clear(rhs[i], 0, 3);
                off[i].Clear();
            }

            var A = new double[9];
            var B = new double[9];
            var e = new double[3];
            var omega = new double[3];

            foreach (var link in links)
            {
                int i = link.from, j = link.to;
                if (i == j) continue;
                LinkTerms(poses[i], poses[j], link, A, B, e);

                double sp = Math.Max(1e-9, link.sigmaMeters), sh = Math.Max(1e-9, link.sigmaDegrees * Math.PI / 180.0);
                omega[0] = omega[1] = 1.0 / (sp * sp);
                omega[2] = 1.0 / (sh * sh);

                // Huber loss on loop-closure links (re-weighted every iteration): beyond HuberSigmas of disagreement a
                // link's pull grows only linearly, so one wrong match can't drag the map far.
                if (link.revisit && HuberSigmas > 0)
                {
                    double whitened = Math.Sqrt(e[0] * e[0] * omega[0] + e[1] * e[1] * omega[1] + e[2] * e[2] * omega[2]);
                    if (whitened > HuberSigmas)
                    {
                        double w = HuberSigmas / whitened;
                        omega[0] *= w;
                        omega[1] *= w;
                        omega[2] *= w;
                    }
                }

                AddBlock(diagonal[i], A, A, omega);
                AddBlock(diagonal[j], B, B, omega);
                AddBlock(Off(i, j), A, B, omega);
                AddBlock(Off(j, i), B, A, omega);

                // b = Jᵀ Ω e; the system is H dx = −b, so rhs accumulates −b.
                for (int r = 0; r < 3; r++)
                {
                    double ai = 0, bj = 0;
                    for (int k = 0; k < 3; k++)
                    {
                        ai += A[k * 3 + r] * omega[k] * e[k];
                        bj += B[k * 3 + r] * omega[k] * e[k];
                    }
                    rhs[i][r] -= ai;
                    rhs[j][r] -= bj;
                }
            }

            // The dock is known exactly: pose 0 is held fixed.
            diagonal[0][0] += L.FixedPoseWeight;
            diagonal[0][4] += L.FixedPoseWeight;
            diagonal[0][8] += L.FixedPoseWeight;
        }

        double[] Off(int a, int b)
        {
            if (!off[a].TryGetValue(b, out var block))
            {
                block = new double[9];
                off[a][b] = block;
            }
            return block;
        }

        /// <summary>target += Xᵀ diag(ω) Y (3×3 row-major).</summary>
        static void AddBlock(double[] target, double[] X, double[] Y, double[] omega)
        {
            for (int r = 0; r < 3; r++)
                for (int c = 0; c < 3; c++)
                {
                    double sum = 0;
                    for (int k = 0; k < 3; k++) sum += X[k * 3 + r] * omega[k] * Y[k * 3 + c];
                    target[r * 3 + c] += sum;
                }
        }

        /// <summary>
        /// The link's error e (3) and its Jacobians A = ∂e/∂pose_from, B = ∂e/∂pose_to (3×3 row-major), with the heading
        /// part in radians: e_xy = Rzᵀ (Riᵀ (tj − ti) − z_xy), e_θ = wrap(θj − θi − zθ).
        /// </summary>
        internal static void LinkTerms(GraphPose pi, GraphPose pj, GraphLink link, double[] A, double[] B, double[] e)
        {
            double ti = pi.heading * Math.PI / 180.0, ci = Math.Cos(ti), si = Math.Sin(ti);
            double tz = link.measured.heading * Math.PI / 180.0, cz = Math.Cos(tz), sz = Math.Sin(tz);
            double dx = pj.x - pi.x, dy = pj.y - pi.y;

            // Riᵀ dt, and its derivative with respect to θi.
            double qx = ci * dx + si * dy, qy = -si * dx + ci * dy;
            double dqx = -si * dx + ci * dy, dqy = -ci * dx - si * dy;

            // Rzᵀ v = (cz vx + sz vy, −sz vx + cz vy)
            double ux = qx - link.measured.x, uy = qy - link.measured.y;
            e[0] = cz * ux + sz * uy;
            e[1] = -sz * ux + cz * uy;
            e[2] = GraphPose.WrapDegrees(pj.heading - pi.heading - link.measured.heading) * Math.PI / 180.0;

            // M = Rzᵀ Riᵀ
            double m00 = cz * ci - sz * si, m01 = cz * si + sz * ci;
            double m10 = -sz * ci - cz * si, m11 = -sz * si + cz * ci;

            A[0] = -m00; A[1] = -m01; A[2] = cz * dqx + sz * dqy;
            A[3] = -m10; A[4] = -m11; A[5] = -sz * dqx + cz * dqy;
            A[6] = 0; A[7] = 0; A[8] = -1;

            B[0] = m00; B[1] = m01; B[2] = 0;
            B[3] = m10; B[4] = m11; B[5] = 0;
            B[6] = 0; B[7] = 0; B[8] = 1;
        }

        // ---- Exact sparse block elimination ----

        readonly List<int> scratchNeighbours = new List<int>();
        readonly List<Elimination> eliminations = new List<Elimination>();

        sealed class Elimination
        {
            public int pose;
            public readonly double[] inverse = new double[9];
            public readonly double[] rhs = new double[3];
            public readonly List<int> later = new List<int>();          // neighbours eliminated after this pose
            public readonly List<double[]> coupling = new List<double[]>(); // H[pose, later[k]]
        }

        void Eliminate(int n)
        {
            while (eliminations.Count < n) eliminations.Add(new Elimination());
            var temp = new double[9];
            var product = new double[9];
            var v = new double[3];

            for (int k = 0; k < n; k++)
            {
                int p = order[k];
                var record = eliminations[k];
                record.pose = p;
                record.later.Clear();
                record.coupling.Clear();

                Invert3(diagonal[p], record.inverse);
                Array.Copy(rhs[p], record.rhs, 3);

                scratchNeighbours.Clear();
                scratchNeighbours.AddRange(off[p].Keys);
                scratchNeighbours.Sort(); // a fixed order, so results never depend on dictionary order

                foreach (int a in scratchNeighbours)
                {
                    record.later.Add(a);
                    record.coupling.Add((double[])off[p][a].Clone());
                }

                // v = D⁻¹ rhs_p
                Multiply3x1(record.inverse, record.rhs, v);

                for (int ia = 0; ia < scratchNeighbours.Count; ia++)
                {
                    int a = scratchNeighbours[ia];
                    var hap = off[a][p];

                    // temp = H_ap D⁻¹
                    Multiply3x3(hap, record.inverse, temp);

                    // rhs_a −= H_ap D⁻¹ rhs_p
                    for (int r = 0; r < 3; r++)
                        rhs[a][r] -= hap[r * 3] * v[0] + hap[r * 3 + 1] * v[1] + hap[r * 3 + 2] * v[2];

                    for (int ib = 0; ib < scratchNeighbours.Count; ib++)
                    {
                        int b = scratchNeighbours[ib];
                        Multiply3x3(temp, off[p][b], product); // H_ap D⁻¹ H_pb
                        var target = a == b ? diagonal[a] : Off(a, b);
                        for (int q = 0; q < 9; q++) target[q] -= product[q];
                    }
                }

                foreach (int a in scratchNeighbours) off[a].Remove(p);
                off[p].Clear();
            }

            // Back-substitution, last eliminated first: x_p = D⁻¹ (rhs_p − Σ H_pa x_a).
            for (int k = n - 1; k >= 0; k--)
            {
                var record = eliminations[k];
                v[0] = record.rhs[0];
                v[1] = record.rhs[1];
                v[2] = record.rhs[2];
                for (int m = 0; m < record.later.Count; m++)
                {
                    int a = record.later[m];
                    var h = record.coupling[m];
                    double x0 = step[3 * a], x1 = step[3 * a + 1], x2 = step[3 * a + 2];
                    v[0] -= h[0] * x0 + h[1] * x1 + h[2] * x2;
                    v[1] -= h[3] * x0 + h[4] * x1 + h[5] * x2;
                    v[2] -= h[6] * x0 + h[7] * x1 + h[8] * x2;
                }
                var inv = record.inverse;
                int p = record.pose;
                step[3 * p] = inv[0] * v[0] + inv[1] * v[1] + inv[2] * v[2];
                step[3 * p + 1] = inv[3] * v[0] + inv[4] * v[1] + inv[5] * v[2];
                step[3 * p + 2] = inv[6] * v[0] + inv[7] * v[1] + inv[8] * v[2];
            }
        }

        static void Multiply3x3(double[] a, double[] b, double[] result)
        {
            for (int r = 0; r < 3; r++)
                for (int c = 0; c < 3; c++)
                    result[r * 3 + c] = a[r * 3] * b[c] + a[r * 3 + 1] * b[3 + c] + a[r * 3 + 2] * b[6 + c];
        }

        static void Multiply3x1(double[] a, double[] x, double[] result)
        {
            for (int r = 0; r < 3; r++) result[r] = a[r * 3] * x[0] + a[r * 3 + 1] * x[1] + a[r * 3 + 2] * x[2];
        }

        static void Invert3(double[] m, double[] inv)
        {
            double a = m[0], b = m[1], c = m[2], d = m[3], e = m[4], f = m[5], g = m[6], h = m[7], i = m[8];
            double A = e * i - f * h, B = -(d * i - f * g), C = d * h - e * g;
            double det = a * A + b * B + c * C;
            if (Math.Abs(det) < 1e-300) det = det < 0 ? -1e-300 : 1e-300; // a pose no link touches: no move
            double s = 1.0 / det;
            inv[0] = A * s;
            inv[1] = -(b * i - c * h) * s;
            inv[2] = (b * f - c * e) * s;
            inv[3] = B * s;
            inv[4] = (a * i - c * g) * s;
            inv[5] = -(a * f - c * d) * s;
            inv[6] = C * s;
            inv[7] = -(a * h - b * g) * s;
            inv[8] = (a * e - b * d) * s;
        }
    }
}
