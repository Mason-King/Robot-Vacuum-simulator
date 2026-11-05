using System;
using System.Collections.Generic;
using UnityEngine;
using G = RobotVacuum.Sim.Control.GlobalSlamConstants;

namespace RobotVacuum.Sim.Control
{
    /// <summary>
    /// SLAM ONLY: finds where a scan sits in a finished submap (design doc, section 20; Cartographer's
    /// FastCorrelativeScanMatcher2D + its constraint builder's refinement).
    ///
    ///   1. EXHAUSTIVE SEARCH, exactly, by branch and bound: every pose in a window (cell steps in x and y; angle steps
    ///      small enough that the furthest point moves at most one cell) is scored by the mean occupancy probability of
    ///      the cells the scan's points land in. Max-pooled copies of the grid (each cell = the best of a 2^h × 2^h
    ///      block) give an upper bound for a whole block of poses at once, so blocks that can't beat the best so far
    ///      are skipped without scoring their poses. The answer is the same as scoring every pose.
    ///   2. Accepted only if that score is at least <see cref="G.MinScore"/>.
    ///   3. REFINED by least squares (<see cref="ScanMatcher"/>) from the best pose, to land between cells.
    /// </summary>
    public sealed class SubmapMatcher
    {
        const float Unknown = 0.1f; // as the scan matcher: unknown cells count as unlikely walls

        /// <summary>A finished submap's grid and its max-pooled copies (computed once, the first time it is searched).</summary>
        public sealed class Precomputed
        {
            internal int minRow, minCol, rows, cols;
            internal float[][] levels; // levels[h][r * cols + c] = max over [r, r + 2^h) × [c, c + 2^h)
            internal ProbabilityGrid grid;
        }

        readonly Dictionary<ProbabilityGrid, Precomputed> cache = new Dictionary<ProbabilityGrid, Precomputed>();
        readonly ScanMatcher refiner = new ScanMatcher();

        // Scratch: each angle's point cells.
        readonly List<int[]> angleRows = new List<int[]>();
        readonly List<int[]> angleCols = new List<int[]>();
        readonly List<float> angleValues = new List<float>();

        /// <summary>The last search: its best score and how many candidate poses were scored (vs the full window).</summary>
        public float LastScore { get; private set; }
        public int LastScored { get; private set; }
        public int LastWindowSize { get; private set; }

        public Precomputed Prepare(ProbabilityGrid grid)
        {
            if (cache.TryGetValue(grid, out var p)) return p;

            // Padded by the coarsest block size on every side, so a block that starts outside the grid but reaches into
            // it still gets a true upper bound (the padding is unknown, the lowest value there is).
            int pad = 1 << (G.BranchAndBoundDepth - 1);
            p = new Precomputed
            {
                grid = grid,
                minRow = grid.minRow - pad,
                minCol = grid.minCol - pad,
                rows = grid.rows + 2 * pad,
                cols = grid.cols + 2 * pad,
                levels = new float[G.BranchAndBoundDepth][],
            };
            int size = p.rows * p.cols;
            var base0 = new float[size];
            for (int i = 0; i < size; i++) base0[i] = Unknown;
            for (int r = 0; r < grid.rows; r++)
                for (int c = 0; c < grid.cols; c++)
                {
                    float v = grid.probability[r * grid.cols + c];
                    if (v > 0f) base0[(r + pad) * p.cols + c + pad] = v;
                }
            p.levels[0] = base0;

            for (int h = 1; h < G.BranchAndBoundDepth; h++)
            {
                var below = p.levels[h - 1];
                var level = new float[size];
                int half = 1 << (h - 1);
                for (int r = 0; r < p.rows; r++)
                {
                    for (int c = 0; c < p.cols; c++)
                    {
                        float v = below[r * p.cols + c];
                        if (c + half < p.cols) v = Mathf.Max(v, below[r * p.cols + c + half]);
                        if (r + half < p.rows)
                        {
                            v = Mathf.Max(v, below[(r + half) * p.cols + c]);
                            if (c + half < p.cols) v = Mathf.Max(v, below[(r + half) * p.cols + c + half]);
                        }
                        level[r * p.cols + c] = v;
                    }
                }
                p.levels[h] = level;
            }
            cache[grid] = p;
            return p;
        }

        /// <summary>Drops a submap's precomputed copies (not normally needed: finished submaps never change).</summary>
        public void Forget(ProbabilityGrid grid) => cache.Remove(grid);

        /// <summary>
        /// Searches for <paramref name="points"/> (robot-relative) in <paramref name="grid"/> around
        /// <paramref name="guess"/> (in the grid's frame). Returns true with the refined pose if a match scores at least
        /// <see cref="G.MinScore"/>.
        /// </summary>
        public bool Match(ProbabilityGrid grid, List<Vector2> points, RobotPose guess, out RobotPose found)
        {
            found = guess;
            if (!Search(grid, points, guess, G.SearchWindowMeters, G.SearchWindowDegrees, G.MinScore, exhaustive: false, out RobotPose best))
                return false;

            found = refiner.Match(grid, points, best, G.RefineOccupiedWeight, G.RefineTranslationWeight, G.RefineRotationWeight);
            return true;
        }

        /// <summary>
        /// The search alone: the best pose on the search lattice with score ≥ <paramref name="minScore"/>. With
        /// <paramref name="exhaustive"/> every candidate is scored (tests compare the two).
        /// </summary>
        public bool Search(ProbabilityGrid grid, List<Vector2> points, RobotPose guess, float windowMeters, float windowDegrees,
            float minScore, bool exhaustive, out RobotPose best)
        {
            best = guess;
            LastScore = 0f;
            LastScored = 0;
            LastWindowSize = 0;
            if (points.Count == 0) return false;

            var p = Prepare(grid);
            float cell = grid.CellSizeMeters;
            Vector2 anchor = grid.AnchorWorld;

            // Angle step: the furthest point moves at most one cell (Cartographer's rule).
            float maxRange = 0f;
            foreach (var q in points) maxRange = Mathf.Max(maxRange, q.magnitude);
            float stepDegrees = maxRange > cell
                ? Mathf.Acos(1f - cell * cell / (2f * maxRange * maxRange)) * Mathf.Rad2Deg
                : windowDegrees;
            int angles = Mathf.Max(0, Mathf.CeilToInt(windowDegrees / stepDegrees));
            int window = Mathf.CeilToInt(windowMeters / cell);

            // Each angle's point cells, at offset (0, 0).
            angleRows.Clear();
            angleCols.Clear();
            angleValues.Clear();
            for (int a = -angles; a <= angles; a++)
            {
                float heading = guess.heading + a * stepDegrees;
                var rr = new int[points.Count];
                var cc = new int[points.Count];
                float rad = heading * Mathf.Deg2Rad, cs = Mathf.Cos(rad), sn = Mathf.Sin(rad);
                for (int i = 0; i < points.Count; i++)
                {
                    Vector2 w = guess.position + new Vector2(cs * points[i].x - sn * points[i].y, sn * points[i].x + cs * points[i].y);
                    cc[i] = Mathf.FloorToInt((w.x - anchor.x) / cell) - p.minCol;
                    rr[i] = Mathf.FloorToInt((w.y - anchor.y) / cell) - p.minRow;
                }
                angleRows.Add(rr);
                angleCols.Add(cc);
                angleValues.Add(heading);
            }
            LastWindowSize = angleValues.Count * (2 * window + 1) * (2 * window + 1);

            float bestScore = minScore;
            int bestA = -1, bestDr = 0, bestDc = 0;

            if (exhaustive)
            {
                for (int a = 0; a < angleValues.Count; a++)
                    for (int dr = -window; dr <= window; dr++)
                        for (int dc = -window; dc <= window; dc++)
                        {
                            float s = Score(p, 0, a, dr, dc);
                            if (s > bestScore)
                            {
                                bestScore = s;
                                bestA = a;
                                bestDr = dr;
                                bestDc = dc;
                            }
                        }
            }
            else
            {
                int top = G.BranchAndBoundDepth - 1;
                int stride = 1 << top;
                var candidates = new List<(float score, int a, int dr, int dc)>();
                for (int a = 0; a < angleValues.Count; a++)
                    for (int dr = -window; dr <= window; dr += stride)
                        for (int dc = -window; dc <= window; dc += stride)
                            candidates.Add((Score(p, top, a, dr, dc), a, dr, dc));
                Branch(p, candidates, top, window, ref bestScore, ref bestA, ref bestDr, ref bestDc);
            }

            LastScore = bestA >= 0 ? bestScore : 0f;
            if (bestA < 0) return false;
            best = new RobotPose(guess.position + new Vector2(bestDc * cell, bestDr * cell), angleValues[bestA]);
            return true;
        }

        /// <summary>Depth first, best first: a block is explored only if its upper bound beats the best leaf so far.</summary>
        void Branch(Precomputed p, List<(float score, int a, int dr, int dc)> candidates, int h, int window,
            ref float bestScore, ref int bestA, ref int bestDr, ref int bestDc)
        {
            // Highest bound first; equal bounds in a fixed order (angle, then rows, then columns).
            candidates.Sort((x, y) =>
            {
                int byScore = y.score.CompareTo(x.score);
                if (byScore != 0) return byScore;
                if (x.a != y.a) return x.a.CompareTo(y.a);
                if (x.dr != y.dr) return x.dr.CompareTo(y.dr);
                return x.dc.CompareTo(y.dc);
            });

            foreach (var c in candidates)
            {
                if (c.score <= bestScore) break; // sorted: nothing after this can beat it either
                if (h == 0)
                {
                    bestScore = c.score;
                    bestA = c.a;
                    bestDr = c.dr;
                    bestDc = c.dc;
                    continue;
                }

                int half = 1 << (h - 1);
                var children = new List<(float score, int a, int dr, int dc)>(4);
                for (int i = 0; i < 2; i++)
                {
                    int dr = c.dr + i * half;
                    if (dr > window) break;
                    for (int j = 0; j < 2; j++)
                    {
                        int dc = c.dc + j * half;
                        if (dc > window) break;
                        children.Add((Score(p, h - 1, c.a, dr, dc), c.a, dr, dc));
                    }
                }
                Branch(p, children, h - 1, window, ref bestScore, ref bestA, ref bestDr, ref bestDc);
            }
        }

        /// <summary>Mean over points of level <paramref name="h"/> at their cells shifted by (dr, dc).</summary>
        float Score(Precomputed p, int h, int a, int dr, int dc)
        {
            if (h == 0) LastScored++;
            var level = p.levels[h];
            var rr = angleRows[a];
            var cc = angleCols[a];
            float sum = 0f;
            for (int i = 0; i < rr.Length; i++)
            {
                int r = rr[i] + dr, c = cc[i] + dc;
                sum += r >= 0 && c >= 0 && r < p.rows && c < p.cols ? level[r * p.cols + c] : Unknown;
            }
            return sum / rr.Length;
        }
    }
}
