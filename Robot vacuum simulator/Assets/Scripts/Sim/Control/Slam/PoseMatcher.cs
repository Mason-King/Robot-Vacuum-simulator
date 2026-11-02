using System.Collections.Generic;
using RobotVacuum.Sim.Sensors;
using UnityEngine;
using E = RobotVacuum.Sim.Control.EvidenceConstants;
using K = RobotVacuum.Sim.Control.PoseCorrectionConstants;

namespace RobotVacuum.Sim.Control
{
    /// <summary>
    /// The one user-facing knob for SLAM pose correction (Advanced settings): a coherent bundle of correction rate, rays
    /// used and wide-search window, so no combination can be set that breaks the matching. Balanced is 0 so an unset
    /// value means Balanced.
    /// </summary>
    public enum PoseCorrectionQuality
    {
        /// <summary>2.5 corrections/s, every 4th ray, wider window (more drift to absorb between corrections). Least CPU.</summary>
        Performance = 1,

        /// <summary>5 corrections/s, every 3rd ray. The default.</summary>
        Balanced = 0,

        /// <summary>10 corrections/s (every lidar scan), every 2nd ray. Most CPU.</summary>
        Reliable = 2,
    }

    /// <summary>The numbers a preset stands for.</summary>
    public readonly struct PoseCorrectionProfile
    {
        public readonly float rateHz;
        public readonly int rayStride;
        public readonly float coarseRangeMeters;
        public readonly float coarseRangeDegrees;

        public PoseCorrectionProfile(float rateHz, int rayStride, float coarseRangeMeters, float coarseRangeDegrees)
        {
            this.rateHz = rateHz;
            this.rayStride = rayStride;
            this.coarseRangeMeters = coarseRangeMeters;
            this.coarseRangeDegrees = coarseRangeDegrees;
        }

        public static PoseCorrectionProfile For(PoseCorrectionQuality quality) => quality switch
        {
            PoseCorrectionQuality.Performance => new PoseCorrectionProfile(2.5f, 4, 0.15f, 6f),
            PoseCorrectionQuality.Reliable => new PoseCorrectionProfile(10f, 2, 0.12f, 4f),
            _ => new PoseCorrectionProfile(5f, 3, 0.12f, 4f),
        };
    }

    /// <summary>How a correction attempt ended.</summary>
    public enum MatchOutcome
    {
        /// <summary>The pose was corrected in every direction.</summary>
        Applied,

        /// <summary>Corrected only in the directions the scan pins down (a corridor: across it, not along it).</summary>
        Partial,

        /// <summary>Too few rays hit anything: no correction.</summary>
        TooFewHits,

        /// <summary>Even the best pose matched poorly (the map isn't built enough yet): no correction.</summary>
        WeakMatch,
    }

    /// <summary>
    /// SLAM pose correction against the evidence map (design doc, section 17).
    ///
    ///   * SCORE: each hit point scores exp(−d²/2σ²), d = its distance to where hits in the nearest confirmed wall cell
    ///     (3×3 around it) actually landed; the pose's score is the mean over hits, minus the soft limit
    ///     <see cref="K.PriorWeight"/> × ((distance from the odometry guess / σpos)² + (turn / σhead)²).
    ///   * SEARCH: a fine grid around the odometry guess first; only if its best sits on the grid's edge (a real slip),
    ///     the preset's wide grid, then the fine grid around the wide winner.
    ///   * ESTIMATE: not the single best grid point but the score-weighted mean of the fine grid, so drift smaller than
    ///     one grid step is still corrected; the same weights give the spread in each direction.
    ///   * PARTIAL: a direction whose spread is wide (a corridor along its length) is left as odometry says.
    ///
    /// No random numbers; candidates in a fixed order; ties go to the candidate nearest the guess. The exponential uses a
    /// lookup table accurate to about one part in a million, so it is the same calculation, only faster.
    /// </summary>
    public sealed class PoseMatcher
    {
        // ---- exp(−x) lookup table, x in [0, TableMaxX] ----
        const int TableSize = 4096;
        const float TableMaxX = 8f;
        static readonly float[] Table = BuildTable();

        static float[] BuildTable()
        {
            var t = new float[TableSize + 1];
            for (int i = 0; i <= TableSize; i++) t[i] = Mathf.Exp(-i * TableMaxX / TableSize);
            return t;
        }

        static float Gauss(float x)
        {
            if (x >= TableMaxX) return 0f;
            float f = x * (TableSize / TableMaxX);
            int i = (int)f;
            return Table[i] + (Table[i + 1] - Table[i]) * (f - i);
        }

        readonly List<float> bearings = new List<float>(360);
        readonly List<float> ranges = new List<float>(360);
        float[] offsetX = new float[0];
        float[] offsetY = new float[0];

        // The last fine grid searched (for the estimate between grid steps).
        readonly List<Vector3> fineCandidates = new List<Vector3>(729); // x, y, heading
        readonly List<float> fineScores = new List<float>(729);

        EvidenceMap map;
        RobotPose guess;
        float inverseTwoSigmaSq, priorPos2, priorHead2;
        int hits;

        /// <summary>True if the last attempt needed the wide search.</summary>
        public bool UsedWideSearch { get; private set; }

        /// <summary>The last attempt's best score (mean per hit, after the soft limit).</summary>
        public float LastScore { get; private set; }

        /// <summary>The last attempt's estimated spread: position (largest direction, metres) and heading (degrees).</summary>
        public float LastSpreadMeters { get; private set; }
        public float LastSpreadDegrees { get; private set; }

        /// <param name="sigmaPosMeters">How far odometry could plausibly have drifted since the last correction (soft limit).</param>
        /// <param name="sigmaHeadDegrees">The same, for heading.</param>
        public MatchOutcome Match(EvidenceMap evidence, SensorFrame frame, RobotPose odometryGuess, in PoseCorrectionProfile profile,
            float sigmaPosMeters, float sigmaHeadDegrees, out RobotPose corrected)
        {
            corrected = odometryGuess;
            map = evidence;
            guess = odometryGuess;
            UsedWideSearch = false;
            LastScore = 0f;
            LastSpreadMeters = 0f;
            LastSpreadDegrees = 0f;

            // The rays used, and of those, the ones that hit something.
            int stride = Mathf.Max(1, profile.rayStride);
            int used = 0;
            bearings.Clear();
            ranges.Clear();
            var rays = frame.lidarRays;
            for (int i = 0; i < rays.Count; i += stride)
            {
                used++;
                if (!rays[i].hit) continue;
                bearings.Add(rays[i].bearing);
                ranges.Add(rays[i].range);
            }

            hits = bearings.Count;
            if (hits < Mathf.Max(K.MinHitRaysFloor, Mathf.CeilToInt(used * K.MinHitFraction))) return MatchOutcome.TooFewHits;

            if (offsetX.Length < hits)
            {
                offsetX = new float[hits];
                offsetY = new float[hits];
            }

            inverseTwoSigmaSq = 1f / (2f * K.ScoreSigmaMeters * K.ScoreSigmaMeters);
            float sp = Mathf.Max(1e-4f, sigmaPosMeters), sh = Mathf.Max(1e-3f, sigmaHeadDegrees);
            priorPos2 = 1f / (sp * sp);
            priorHead2 = 1f / (sh * sh);

            int nFineXY = Mathf.RoundToInt(K.FineRangeMeters / K.FineStepMeters);
            int nFineA = Mathf.RoundToInt(K.FineRangeDegrees / K.FineStepDegrees);

            // ---- Fine search around the odometry guess ----
            bool onEdge = Search(odometryGuess, nFineXY, K.FineStepMeters, nFineA, K.FineStepDegrees, record: true,
                out RobotPose best, out float bestScore);

            if (onEdge)
            {
                // ---- A real slip: the wide search, then the fine search around its winner ----
                UsedWideSearch = true;
                int nCoarseXY = Mathf.RoundToInt(profile.coarseRangeMeters / K.CoarseStepMeters);
                int nCoarseA = Mathf.RoundToInt(profile.coarseRangeDegrees / K.CoarseStepDegrees);
                Search(odometryGuess, nCoarseXY, K.CoarseStepMeters, nCoarseA, K.CoarseStepDegrees, record: false,
                    out RobotPose wide, out _);
                Search(wide, nFineXY, K.FineStepMeters, nFineA, K.FineStepDegrees, record: true, out best, out bestScore);
            }

            LastScore = bestScore;
            if (bestScore < K.MinMeanScore) return MatchOutcome.WeakMatch;

            // ---- Estimate between grid steps: score-weighted mean and spread of the last fine grid ----
            double wSum = 0, mx = 0, my = 0, mh = 0;
            float centreHeading = fineCandidates[0].z;
            for (int i = 0; i < fineCandidates.Count; i++)
            {
                double w = Mathf.Exp((fineScores[i] - bestScore) / K.SoftTemperature);
                var c = fineCandidates[i];
                wSum += w;
                mx += w * c.x;
                my += w * c.y;
                mh += w * Mathf.DeltaAngle(centreHeading, c.z);
            }
            mx /= wSum;
            my /= wSum;
            mh /= wSum;

            double cxx = 0, cxy = 0, cyy = 0, chh = 0;
            for (int i = 0; i < fineCandidates.Count; i++)
            {
                double w = Mathf.Exp((fineScores[i] - bestScore) / K.SoftTemperature) / wSum;
                var c = fineCandidates[i];
                double dx = c.x - mx, dy = c.y - my, dh = Mathf.DeltaAngle(centreHeading, c.z) - mh;
                cxx += w * dx * dx;
                cxy += w * dx * dy;
                cyy += w * dy * dy;
                chh += w * dh * dh;
            }

            // ---- Partial correction: only the directions the scan pins down ----
            var outcome = MatchOutcome.Applied;
            Vector2 position = odometryGuess.position;
            float heading = odometryGuess.heading;

            float spreadHead = (float)System.Math.Sqrt(chh);
            float targetHeading = centreHeading + (float)mh;
            if (spreadHead < K.LooseDegrees) heading += Mathf.DeltaAngle(odometryGuess.heading, targetHeading);
            else outcome = MatchOutcome.Partial;

            Vector2 delta = new Vector2((float)mx, (float)my) - odometryGuess.position;
            Eigen2(cxx, cxy, cyy, out double ev1, out Vector2 v1, out double ev2, out Vector2 v2);
            float spread1 = (float)System.Math.Sqrt(System.Math.Max(ev1, 0)), spread2 = (float)System.Math.Sqrt(System.Math.Max(ev2, 0));
            if (spread1 < K.LooseMeters) position += v1 * Vector2.Dot(v1, delta); else outcome = MatchOutcome.Partial;
            if (spread2 < K.LooseMeters) position += v2 * Vector2.Dot(v2, delta); else outcome = MatchOutcome.Partial;

            LastSpreadMeters = Mathf.Max(spread1, spread2);
            LastSpreadDegrees = spreadHead;

            corrected = new RobotPose(position, Mathf.DeltaAngle(0f, heading));
            return outcome;
        }

        /// <summary>
        /// Scores a grid centred on <paramref name="centre"/>: ±n steps in x and y, ±nA steps of heading, in a fixed order.
        /// A strictly higher score wins; an equal score wins only if nearer the odometry guess. Returns true if the winner
        /// is on the grid's outer edge.
        /// </summary>
        bool Search(RobotPose centre, int nXY, float stepXY, int nA, float stepA, bool record, out RobotPose best, out float bestScore)
        {
            if (record)
            {
                fineCandidates.Clear();
                fineScores.Clear();
            }

            best = centre;
            bestScore = float.NegativeInfinity;
            float bestTie = float.PositiveInfinity;
            bool onEdge = false;

            for (int a = -nA; a <= nA; a++)
            {
                float heading = centre.heading + a * stepA;
                Rotate(heading);

                for (int iy = -nXY; iy <= nXY; iy++)
                {
                    for (int ix = -nXY; ix <= nXY; ix++)
                    {
                        Vector2 position = centre.position + new Vector2(ix * stepXY, iy * stepXY);
                        float score = ScoreRotated(position) - Penalty(position, heading);

                        if (record)
                        {
                            fineCandidates.Add(new Vector3(position.x, position.y, heading));
                            fineScores.Add(score);
                        }

                        float tie = TieDistance(position, heading);
                        if (score > bestScore + 1e-6f || (Mathf.Abs(score - bestScore) <= 1e-6f && tie < bestTie))
                        {
                            bestScore = score;
                            bestTie = tie;
                            best = new RobotPose(position, heading);
                            onEdge = Mathf.Abs(ix) == nXY || Mathf.Abs(iy) == nXY || (nA > 0 && Mathf.Abs(a) == nA);
                        }
                    }
                }
            }
            return onEdge;
        }

        float Penalty(Vector2 position, float heading)
        {
            float d2 = (position - guess.position).sqrMagnitude;
            float dh = Mathf.DeltaAngle(guess.heading, heading);
            return K.PriorWeight * (d2 * priorPos2 + dh * dh * priorHead2);
        }

        float TieDistance(Vector2 position, float heading)
        {
            float dh = Mathf.DeltaAngle(heading, guess.heading) * 0.01f;
            return (position - guess.position).sqrMagnitude + dh * dh;
        }

        /// <summary>The hit points' offsets from the robot for one heading (trig once per heading, not per candidate).</summary>
        void Rotate(float heading)
        {
            for (int i = 0; i < hits; i++)
            {
                Vector2 d = RobotPose.DirectionOf(heading + bearings[i]) * ranges[i];
                offsetX[i] = d.x;
                offsetY[i] = d.y;
            }
        }

        /// <summary>Mean over hits of exp(−d²/2σ²) to the nearest confirmed wall's hit position (3×3 cells around the point).</summary>
        float ScoreRotated(Vector2 position)
        {
            Vector2 anchor = map.AnchorWorld;
            float inverseCell = 1f / map.CellSizeMeters;
            float cell = map.CellSizeMeters;
            var evidence = map.evidence;
            var meanX = map.meanX;
            var meanY = map.meanY;
            var count = map.hitCount;
            int minRow = map.minRow, minCol = map.minCol, rows = map.rows, cols = map.cols;

            float sum = 0f;
            for (int i = 0; i < hits; i++)
            {
                // Relative to the anchor, like the stored hit positions.
                float px = position.x + offsetX[i] - anchor.x;
                float py = position.y + offsetY[i] - anchor.y;
                int col = Mathf.FloorToInt(px * inverseCell) - minCol;
                int row = Mathf.FloorToInt(py * inverseCell) - minRow;

                float bestHere = 0f;
                for (int dr = -1; dr <= 1; dr++)
                {
                    int r = row + dr;
                    if (r < 0 || r >= rows) continue;
                    for (int dc = -1; dc <= 1; dc++)
                    {
                        int c = col + dc;
                        if (c < 0 || c >= cols) continue;
                        int index = r * cols + c;
                        if (evidence[index] < E.Confirmed) continue;

                        float hx, hy;
                        if (count[index] > 0)
                        {
                            hx = meanX[index];
                            hy = meanY[index];
                        }
                        else
                        {
                            hx = (c + minCol + 0.5f) * cell;
                            hy = (r + minRow + 0.5f) * cell;
                        }

                        float dx = px - hx, dy = py - hy;
                        float v = Gauss((dx * dx + dy * dy) * inverseTwoSigmaSq);
                        if (v > bestHere) bestHere = v;
                    }
                }
                sum += bestHere;
            }
            return sum / hits;
        }

        /// <summary>Eigen-decomposition of the symmetric 2×2 matrix [[a, b], [b, d]].</summary>
        static void Eigen2(double a, double b, double d, out double ev1, out Vector2 v1, out double ev2, out Vector2 v2)
        {
            double mean = 0.5 * (a + d);
            double r = System.Math.Sqrt(0.25 * (a - d) * (a - d) + b * b);
            ev1 = mean + r;
            ev2 = mean - r;

            // Normalised in double by hand: these are covariances in m² (about 1e-5 for a few millimetres), and Unity's
            // Vector2.normalized returns ZERO below a length of 1e-5, which silently disabled the position correction.
            // Of the two equivalent eigenvector forms, the longer one is used for accuracy.
            double x1 = ev1 - d, y1 = b;
            double x2 = b, y2 = ev1 - a;
            double n1 = x1 * x1 + y1 * y1, n2 = x2 * x2 + y2 * y2;
            if (n2 > n1) { x1 = x2; y1 = y2; n1 = n2; }

            if (n1 > 1e-30)
            {
                double inv = 1.0 / System.Math.Sqrt(n1);
                v1 = new Vector2((float)(x1 * inv), (float)(y1 * inv));
            }
            else
            {
                v1 = a >= d ? Vector2.right : Vector2.up; // isotropic: any pair of perpendicular axes
            }
            v2 = new Vector2(-v1.y, v1.x);
        }
    }
}
