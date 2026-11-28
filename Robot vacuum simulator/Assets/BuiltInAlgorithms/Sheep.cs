using System;
using System.Collections.Generic;
using System.Diagnostics;
using RobotVacuum.AlgorithmApi;

namespace RobotVacuum.Algorithms
{
    /// <summary>
    /// Sheep: greedy. No pattern; each decision goes to the nearest patch of floor that is still below the target,
    /// preferring patches that keep the cleaned floor compact, and holds there until it is clean. The cleaned floor
    /// grows like an amoeba across the map.
    ///
    /// PATCHES are fixed tiles of <see cref="TileCells"/> × <see cref="TileCells"/> map cells on the lattice. A tile is
    /// a CANDIDATE when one of its cells is DIRTY (explored, passable, reachable by the brush, below
    /// <see cref="CleanEnough"/> × the target), it has a GOAL CELL (the cell nearest its centre where the body fits),
    /// the goal cell is reachable over known floor, and the tile is not skipped.
    ///
    /// A decision:
    ///   1. NEAREST: the candidate with the lowest TRIP COST: the path length (the map analysis's reach distance to its
    ///      goal cell) plus the turn toward it, as the metres the robot could have driven in the time the turn takes
    ///      (the brush is always on, so turning in place over finished floor is wasted cleaning, like driving is).
    ///   2. BAND: every candidate within <see cref="BandTiles"/> tile widths of that cost.
    ///   3. CONTACT: in the band, the highest share of the 8 surrounding tiles that Sheep HELD, out of those held or
    ///      still waiting; tiles finished incidentally by a neighbour's hold count in neither (see Contact).
    ///   4. Ties: the run's random stream.
    /// Then drive there at full speed and hold (<see cref="HoldMode.UntilClean"/>) until the tile is clean.
    ///
    /// FAILURES: a goal that ends Blocked or Unreachable, or a tile still dirty after its hold, is SKIPPED. A skipped
    /// tile is a candidate again once the robot has travelled <see cref="RetryAfterMeters"/> or
    /// <see cref="RetryAfterSeconds"/> have passed, whichever is first; after <see cref="MaxSkips"/> skips it is dropped.
    /// With nothing else to do, waiting skipped tiles are retried at once rather than waited for.
    ///
    /// SLAM: with no candidate on known floor, explore the nearest frontier. Nothing left: drive.Finish().
    /// </summary>
    public sealed class Sheep : ICleaningAlgorithm
    {
        public const string Id = "Sheep";

        // ---- Tuning ----

        /// <summary>
        /// A tile's side, in map cells (4 × 5.08 cm ≈ 0.20 m). The corner cells' centres are 0.108 m from the tile's
        /// centre, 0.148 m with the 0.04 m arrival tolerance: inside the 0.17 m brush. (5 would be 0.184 m: outside.)
        /// </summary>
        public const int TileCells = 4;

        /// <summary>A cell counts as clean at this fraction of the target (Boustrophedon uses 0.95).</summary>
        public const float CleanEnough = 0.80f;

        /// <summary>The band: candidates within this many tile widths of the nearest one's path distance.</summary>
        public const float BandTiles = 1f;

        /// <summary>A skipped tile may be tried again after the robot has travelled this far since the skip, metres...</summary>
        public const float RetryAfterMeters = 2f;

        /// <summary>... or after this many simulated seconds, whichever comes first.</summary>
        public const float RetryAfterSeconds = 30f;

        /// <summary>After this many skips a tile is dropped for the rest of the run.</summary>
        public const int MaxSkips = 3;

        /// <summary>
        /// How far inside the brush's edge a cell must be for Sheep to count on cleaning it, metres: the robot stops up
        /// to 0.04 m from its goal, and a cell right at the edge is then out from under the brush. Cells along walls and
        /// furniture that the brush only reaches with no margin are not counted as dirty.
        /// </summary>
        public const float ReachMargin = 0.05f;

        /// <summary>Speeds assumed when the context gives none (the robot's defaults), m/s and degrees per second.</summary>
        const float DefaultDriveSpeed = 0.65f, DefaultTurnSpeed = 320f;

        public string Name => "Sheep";
        public bool SupportsSlam => true;

        // ---- Statistics (the "done" log line, and tests) ----

        public int Decisions { get; private set; }
        public int TilesCleaned { get; private set; }
        public int Skips { get; private set; }
        public int Explorations { get; private set; }
        public double ScanMs { get; private set; }
        public double BandMs { get; private set; }
        public double FrontierMs { get; private set; }
        public string Activity { get; private set; } = "starting";

        // ---- State ----

        AlgorithmContext context;
        IMapAnalysis analysis;

        bool hasGoal;
        Vec2 goal;
        int goalTileRow, goalTileCol;
        bool goalIsFrontier;
        int goalRow2, goalCol2; // the issued stop, in half-cells

        readonly List<int> requiredCells = new List<int>(); // PlaceStop: row, col pairs

        bool hasLastPosition;
        Vec2 lastPosition;
        float odometer; // metres the believed position has moved this run

        float cleanLevel;   // CleanEnough × target
        float edgeReach;    // cells nearer a wall than this (to its edge) are out of the brush's reach, with the margin
        float bandMeters;

        // Goal search: the tile's centre point first, then cells as offsets from the tile's first row and column, nearest
        // the tile's centre first (ties by row, then column), out to the brush radius.
        int[] offsetRow = Array.Empty<int>();
        int[] offsetCol = Array.Empty<int>();

        struct Candidate
        {
            public int tileRow, tileCol;
            public Vec2 goal;
            public int goalRow2, goalCol2; // the goal in half-cells (2 × row, 2 × column of a cell centre)
            public float distance; // path length, metres
            public float cost;     // distance + the turn, as metres
        }

        readonly List<Candidate> candidates = new List<Candidate>();
        readonly List<Candidate> waiting = new List<Candidate>(); // skipped, not yet due for a retry, not dropped

        // Per-tile state, dense, over a rectangle of tiles that grows with the map (with padding).
        int tileRow0, tileCol0, tileRows, tileCols;
        byte[] skipCount = Array.Empty<byte>();
        bool[] held = Array.Empty<bool>();          // tiles Sheep held and finished (the route grows around these)
        float[] skipTime = Array.Empty<float>();
        float[] skipOdometer = Array.Empty<float>();
        short[] goalOffset = Array.Empty<short>(); // index into the offsets; -1 = no cell where the body fits
        int[] goalVersion = Array.Empty<int>();    // analysis.Version the goal offset was found at

        const int TilePadding = 8;
        const int NoVersion = int.MinValue;

        readonly Stopwatch stopwatch = new Stopwatch();

        public void Initialize(AlgorithmContext context)
        {
            this.context = context;
            analysis = context.analysis ?? throw new InvalidOperationException("Sheep needs the controller's map analysis (AlgorithmContext.analysis).");

            hasGoal = false;
            goalIsFrontier = false;
            hasLastPosition = false;
            odometer = 0f;
            Decisions = TilesCleaned = Skips = Explorations = 0;
            ScanMs = BandMs = FrontierMs = 0.0;
            Activity = "starting";
            candidates.Clear();
            waiting.Clear();

            tileRow0 = tileCol0 = tileRows = tileCols = 0;
            skipCount = Array.Empty<byte>();
            held = Array.Empty<bool>();
            skipTime = Array.Empty<float>();
            skipOdometer = Array.Empty<float>();
            goalOffset = Array.Empty<short>();
            goalVersion = Array.Empty<int>();

            analysis.SkipRooms = true; // Sheep never reads rooms
            cleanLevel = CleanEnough * context.targetCleanliness;
            bandMeters = BandTiles * TileCells * context.cellSizeMeters;
            BuildOffsets();
        }

        public Vec2? NextGoal(IBeliefMap map, Pose2 believedPose, GoalStatus lastStatus)
        {
            if (context.drive.Finished) return null;

            Vec2 here = believedPose.position;
            if (hasLastPosition) odometer += Vec2.Distance(here, lastPosition);
            lastPosition = here;
            hasLastPosition = true;

            // Driving there, or holding over the tile: carry on. The hold request stays as it was set.
            if (hasGoal && lastStatus == GoalStatus.InProgress) return goal;

            Decisions++;
            analysis.Update(here);
            edgeReach = analysis.BodyClearance - context.BrushRadius + ReachMargin;

            if (hasGoal)
            {
                hasGoal = false;
                if (lastStatus == GoalStatus.Reached && !goalIsFrontier)
                {
                    if (TileDirtyWithin(goalTileRow, goalTileCol, goalRow2, goalCol2))
                    {
                        context.Log($"tile ({goalTileRow}, {goalTileCol}) still below the target after its hold; skipping it");
                        Skip(goalTileRow, goalTileCol);
                    }
                    else
                    {
                        TilesCleaned++;
                        held[TileIndex(goalTileRow, goalTileCol)] = true;
                    }
                }
                else if (lastStatus == GoalStatus.Blocked || lastStatus == GoalStatus.Unreachable)
                {
                    context.Log($"{(goalIsFrontier ? "frontier" : "tile")} ({goalTileRow}, {goalTileCol}) {lastStatus}; skipping it");
                    Skip(goalTileRow, goalTileCol);
                }
            }

            return Choose(map, believedPose);
        }

        // ---- Choosing ----------------------------------------------------------------------------------

        Vec2? Choose(IBeliefMap map, Pose2 pose)
        {
            if (analysis.IsEmpty) return Finish();

            // Metres of driving one degree of turning is worth, at the current speed settings.
            float drive = context.DriveSpeedMetersPerSecond, turnRate = context.TurnSpeedDegreesPerSecond;
            float metresPerDegree = (drive > 0f ? drive : DefaultDriveSpeed) / (turnRate > 0f ? turnRate : DefaultTurnSpeed);

            int rowMin = analysis.MinRow, rowMax = analysis.MinRow + analysis.Rows - 1;
            int colMin = analysis.MinCol, colMax = analysis.MinCol + analysis.Cols - 1;
            int tr0 = FloorDiv(rowMin, TileCells), tr1 = FloorDiv(rowMax, TileCells);
            int tc0 = FloorDiv(colMin, TileCells), tc1 = FloorDiv(colMax, TileCells);
            EnsureTiles(tr0, tc0, tr1, tc1);

            // 1. Candidates, and the nearest.
            stopwatch.Restart();
            candidates.Clear();
            waiting.Clear();
            float nearest = float.PositiveInfinity;
            int version = analysis.Version;
            for (int tr = tr0; tr <= tr1; tr++)
            {
                for (int tc = tc0; tc <= tc1; tc++)
                {
                    if (!TileDirty(tr, tc)) continue;

                    int t = TileIndex(tr, tc);
                    int skips = skipCount[t];
                    if (skips >= MaxSkips) continue;

                    if (goalVersion[t] != version)
                    {
                        goalOffset[t] = FindGoalOffset(tr, tc);
                        goalVersion[t] = version;
                    }
                    int k = goalOffset[t];
                    if (k < 0) continue;

                    float d = GoalReach(tr, tc, k);
                    if (float.IsPositiveInfinity(d)) continue;

                    GoalHalfCells(tr, tc, k, out int gr2, out int gc2);
                    if (!TileDirtyWithin(tr, tc, gr2, gc2)) continue; // only cells its goal can reach count

                    Vec2 at = GoalPoint(map, tr, tc, k);
                    float cost = d + Math.Abs(pose.BearingTo(at)) * metresPerDegree;
                    var candidate = new Candidate { tileRow = tr, tileCol = tc, goal = at, goalRow2 = gr2, goalCol2 = gc2, distance = d, cost = cost };
                    if (skips > 0 && !RetryDue(t))
                    {
                        waiting.Add(candidate);
                        continue;
                    }
                    candidates.Add(candidate);
                    if (cost < nearest) nearest = cost;
                }
            }
            ScanMs += stopwatch.Elapsed.TotalMilliseconds;

            if (candidates.Count == 0)
            {
                // SLAM: explore before retrying what failed.
                if (context.slam)
                {
                    stopwatch.Restart();
                    bool found = PlanFrontier(map);
                    FrontierMs += stopwatch.Elapsed.TotalMilliseconds;
                    if (found) return goal;
                }
                if (waiting.Count == 0) return Finish();

                // Nothing else to do: retry the waiting tiles now.
                candidates.AddRange(waiting);
                for (int i = 0; i < candidates.Count; i++) nearest = Math.Min(nearest, candidates[i].cost);
            }

            // 2–4. The band, the contact score, random ties.
            stopwatch.Restart();
            float limit = nearest + bandMeters;
            int chosen = -1, bestClean = -1, bestFloor = 1, ties = 0;
            for (int i = 0; i < candidates.Count; i++)
            {
                var c = candidates[i];
                if (c.cost > limit) continue;
                Contact(c.tileRow, c.tileCol, out int clean, out int floor);
                if (floor == 0)
                {
                    clean = 0;
                    floor = 1;
                }

                // clean / floor against bestClean / bestFloor, in whole numbers (exact).
                long mine = (long)clean * bestFloor, theirs = (long)bestClean * floor;
                if (chosen < 0 || mine > theirs)
                {
                    chosen = i;
                    bestClean = clean;
                    bestFloor = floor;
                    ties = 1;
                }
                else if (mine == theirs)
                {
                    ties++;
                    if (context.random.NextInt(0, ties) == 0) chosen = i; // each tied tile equally likely
                }
            }
            BandMs += stopwatch.Elapsed.TotalMilliseconds;

            return Issue(map, candidates[chosen], bestClean, bestFloor);
        }

        Vec2? Issue(IBeliefMap map, Candidate c, int clean, int floor)
        {
            int wasted = PlaceStop(map, ref c);
            goal = c.goal;
            goalRow2 = c.goalRow2;
            goalCol2 = c.goalCol2;
            goalTileRow = c.tileRow;
            goalTileCol = c.tileCol;
            goalIsFrontier = false;
            hasGoal = true;
            Activity = "cleaning";

            var drive = context.drive;
            drive.Mode = DriveMode.Travel;
            drive.PassThrough = false;
            drive.Hold = HoldMode.UntilClean;
            drive.HoldCleanliness = cleanLevel;
            drive.HoldRadiusMeters = HoldRadius(map, c);

            context.Log($"tile ({c.tileRow}, {c.tileCol}): {c.distance:F2} m by known floor, trip cost {c.cost:F2} m, contact {clean}/{floor}, " +
                        $"hold radius {drive.HoldRadiusMeters:F3} m, clean cells under the brush {wasted}");
            return goal;
        }

        /// <summary>The distance from the stop to the farthest dirty cell of the tile it reaches, plus half a cell; at most the brush radius.</summary>
        float HoldRadius(IBeliefMap map, Candidate c)
        {
            float reach2 = ReachHalfCellsSquared();
            int farthest2 = 0;
            int r0 = c.tileRow * TileCells, c0 = c.tileCol * TileCells;
            for (int row = r0; row < r0 + TileCells; row++)
                for (int col = c0; col < c0 + TileCells; col++)
                {
                    int dr = 2 * row - c.goalRow2, dc = 2 * col - c.goalCol2, d2 = dr * dr + dc * dc;
                    if (d2 <= reach2 && d2 > farthest2 && Dirty(row, col)) farthest2 = d2;
                }
            float cell = context.cellSizeMeters;
            return Math.Min(context.BrushRadius, (float)Math.Sqrt(farthest2) * 0.5f * cell + 0.5f * cell);
        }

        /// <summary>
        /// Moves the stop toward the dirty side. The tile's dirty cells its goal reaches must all stay within reach
        /// (brush radius less the margin); among the stops that keep them (the goal, then cells nearest the tile's centre
        /// where the body fits and that are reachable), the one with the fewest already-clean cells under the brush wins
        /// (ties: the earlier, nearer the centre). Returns that count.
        /// </summary>
        int PlaceStop(IBeliefMap map, ref Candidate c)
        {
            float reach2 = ReachHalfCellsSquared();
            int r0 = c.tileRow * TileCells, c0 = c.tileCol * TileCells;

            requiredCells.Clear();
            for (int row = r0; row < r0 + TileCells; row++)
                for (int col = c0; col < c0 + TileCells; col++)
                {
                    int dr = 2 * row - c.goalRow2, dc = 2 * col - c.goalCol2;
                    if (dr * dr + dc * dc <= reach2 && Dirty(row, col))
                    {
                        requiredCells.Add(row);
                        requiredCells.Add(col);
                    }
                }

            int best = CleanUnderBrush(c.goalRow2, c.goalCol2);
            for (int k = 0; k < offsetRow.Length && best > 0; k++)
            {
                int row = r0 + offsetRow[k], col = c0 + offsetCol[k];
                int row2 = 2 * row, col2 = 2 * col;
                if (row2 == c.goalRow2 && col2 == c.goalCol2) continue;
                if (!Covers(row2, col2, reach2)) continue;
                if (!analysis.BodyFits(row, col) || float.IsPositiveInfinity(analysis.ReachDistance(row, col))) continue;
                int wasted = CleanUnderBrush(row2, col2);
                if (wasted >= best) continue;
                best = wasted;
                c.goal = map.CellCenter(row, col);
                c.goalRow2 = row2;
                c.goalCol2 = col2;
            }
            return best;
        }

        bool Covers(int row2, int col2, float reach2)
        {
            for (int i = 0; i < requiredCells.Count; i += 2)
            {
                int dr = 2 * requiredCells[i] - row2, dc = 2 * requiredCells[i + 1] - col2;
                if (dr * dr + dc * dc > reach2) return false;
            }
            return true;
        }

        /// <summary>Explored, passable cells under the whole brush at (row2, col2) that are already at the level: the floor a hold there over-cleans.</summary>
        int CleanUnderBrush(int row2, int col2)
        {
            float half = 0.5f * context.cellSizeMeters;
            float rf = half > 0f ? context.BrushRadius / half : 0f, rr = rf * rf;
            int r2 = (int)Math.Floor(rf);
            int rowLo = (row2 - r2 + 1) >> 1, rowHi = (row2 + r2) >> 1;
            int colLo = (col2 - r2 + 1) >> 1, colHi = (col2 + r2) >> 1;
            int count = 0;
            for (int row = rowLo; row <= rowHi; row++)
            {
                int dr = 2 * row - row2;
                for (int col = colLo; col <= colHi; col++)
                {
                    int dc = 2 * col - col2;
                    if (dr * dr + dc * dc > rr) continue;
                    if (analysis.Cleanliness(row, col) >= cleanLevel && analysis.IsExplored(row, col) && !analysis.IsBlocked(row, col))
                        count++;
                }
            }
            return count;
        }

        /// <summary>(Brush radius − margin)², in half-cells² (the same value ReachesTile uses).</summary>
        float ReachHalfCellsSquared()
        {
            float cell = context.cellSizeMeters;
            if (cell <= 0f) return 0f;
            float r = (context.BrushRadius - ReachMargin) * 2f / cell;
            return r * r;
        }

        Vec2? Finish()
        {
            Activity = "done";
            hasGoal = false;
            context.Log($"done: {Decisions} decisions, {TilesCleaned} tiles cleaned, {Skips} skips, {Explorations} explorations; " +
                        $"scan {ScanMs:F1} ms, band {BandMs:F1} ms, frontier {FrontierMs:F1} ms");
            context.drive.Hold = HoldMode.None;
            context.drive.Finish();
            return null;
        }

        // ---- Tiles and cells --------------------------------------------------------------------------------

        /// <summary>Below the target, and floor the brush can clean: explored, passable, not too near a wall.</summary>
        bool Dirty(int row, int col) =>
            analysis.Cleanliness(row, col) < cleanLevel && analysis.IsExplored(row, col) && !analysis.IsBlocked(row, col) &&
            analysis.ObstacleDistance(row, col) >= edgeReach;

        /// <summary>The tile has a dirty cell within reach (brush radius less the margin) of the point (row2, col2), in half-cells.</summary>
        bool TileDirtyWithin(int tileRow, int tileCol, int row2, int col2)
        {
            float reach2 = ReachHalfCellsSquared();
            int r0 = tileRow * TileCells, c0 = tileCol * TileCells;
            for (int row = r0; row < r0 + TileCells; row++)
                for (int col = c0; col < c0 + TileCells; col++)
                {
                    int dr = 2 * row - row2, dc = 2 * col - col2;
                    if (dr * dr + dc * dc <= reach2 && Dirty(row, col)) return true;
                }
            return false;
        }

        /// <summary>The goal for index k (see FindGoalOffset) in half-cells.</summary>
        void GoalHalfCells(int tileRow, int tileCol, int k, out int row2, out int col2)
        {
            int r0 = tileRow * TileCells, c0 = tileCol * TileCells;
            if (k > 0)
            {
                row2 = 2 * (r0 + offsetRow[k - 1]);
                col2 = 2 * (c0 + offsetCol[k - 1]);
            }
            else
            {
                row2 = 2 * r0 + TileCells - 1;
                col2 = 2 * c0 + TileCells - 1;
            }
        }

        bool TileDirty(int tileRow, int tileCol)
        {
            int r0 = tileRow * TileCells, c0 = tileCol * TileCells;
            for (int row = r0; row < r0 + TileCells; row++)
                for (int col = c0; col < c0 + TileCells; col++)
                    if (Dirty(row, col)) return true;
            return false;
        }

        /// <summary>
        /// Where to stop for a tile, as an index: 0 = the tile's centre point (every cell touching it has room for the
        /// body); k ≥ 1 = the cell at offset k - 1. The first, nearest the tile's centre, from which the brush reaches
        /// every cleanable cell of the tile with the margin to spare; failing that, the first where the body fits at
        /// all; -1 if none. Depends only on the map's walls and explored floor (cached per analysis version).
        /// </summary>
        short FindGoalOffset(int tileRow, int tileCol)
        {
            int r0 = tileRow * TileCells, c0 = tileCol * TileCells;
            short fallback = -1;
            if (CentreFits(r0, c0))
            {
                if (ReachesTile(r0, c0, 2 * r0 + TileCells - 1, 2 * c0 + TileCells - 1)) return 0;
                fallback = 0;
            }
            for (int k = 0; k < offsetRow.Length; k++)
            {
                int row = r0 + offsetRow[k], col = c0 + offsetCol[k];
                if (!analysis.BodyFits(row, col)) continue;
                if (ReachesTile(r0, c0, 2 * row, 2 * col)) return (short)(k + 1);
                if (fallback < 0) fallback = (short)(k + 1);
            }
            return fallback;
        }

        /// <summary>The body fits at every cell whose centre is within half a cell (each way) of the tile's centre point.</summary>
        bool CentreFits(int r0, int c0)
        {
            int lo = (TileCells - 1) / 2, hi = TileCells / 2; // 1 and 2 for 4 cells; both 1 for 3
            for (int row = r0 + lo; row <= r0 + hi; row++)
                for (int col = c0 + lo; col <= c0 + hi; col++)
                    if (!analysis.BodyFits(row, col)) return false;
            return true;
        }

        /// <summary>
        /// True when every cleanable cell of the tile (explored, passable, within the brush's reach of a wall) lies within
        /// the brush radius less the margin of the point (row2 / 2, col2 / 2), given in half-cells.
        /// </summary>
        bool ReachesTile(int r0, int c0, int row2, int col2)
        {
            float cell = context.cellSizeMeters, reach = context.BrushRadius - ReachMargin;
            float reachHalfCellsSq = reach * 2f / cell;
            reachHalfCellsSq *= reachHalfCellsSq;
            for (int row = r0; row < r0 + TileCells; row++)
                for (int col = c0; col < c0 + TileCells; col++)
                {
                    if (!analysis.IsExplored(row, col) || analysis.IsBlocked(row, col) ||
                        analysis.ObstacleDistance(row, col) < edgeReach) continue;
                    int dr = 2 * row - row2, dc = 2 * col - col2;
                    if (dr * dr + dc * dc > reachHalfCellsSq) return false;
                }
            return true;
        }

        /// <summary>The goal's distance by known floor (for the centre point: the nearest of the cells around it).</summary>
        float GoalReach(int tileRow, int tileCol, int k)
        {
            int r0 = tileRow * TileCells, c0 = tileCol * TileCells;
            if (k > 0) return analysis.ReachDistance(r0 + offsetRow[k - 1], c0 + offsetCol[k - 1]);
            int lo = (TileCells - 1) / 2, hi = TileCells / 2;
            float best = float.PositiveInfinity;
            for (int row = r0 + lo; row <= r0 + hi; row++)
                for (int col = c0 + lo; col <= c0 + hi; col++)
                    best = Math.Min(best, analysis.ReachDistance(row, col));
            return best;
        }

        /// <summary>The goal as a point: the tile's centre (midway between its middle cells' centres), or the cell's centre.</summary>
        Vec2 GoalPoint(IBeliefMap map, int tileRow, int tileCol, int k)
        {
            int r0 = tileRow * TileCells, c0 = tileCol * TileCells;
            if (k > 0) return map.CellCenter(r0 + offsetRow[k - 1], c0 + offsetCol[k - 1]);
            int lo = (TileCells - 1) / 2, hi = TileCells / 2;
            Vec2 a = map.CellCenter(r0 + lo, c0 + lo), b = map.CellCenter(r0 + hi, c0 + hi);
            return new Vec2(0.5f * (a.x + b.x), 0.5f * (a.y + b.y));
        }

        /// <summary>
        /// Contact, by tiles and history: of the 8 tiles around this one, how many Sheep HELD and finished (clean), out
        /// of those that are held or still waiting (dirty or unexplored floor). Tiles finished incidentally by a
        /// neighbour's hold, and tiles with no floor, count in neither, so floor pre-cleaned ahead of the robot does
        /// not pull the route forward: the route grows around what Sheep has held.
        /// </summary>
        void Contact(int tileRow, int tileCol, out int clean, out int floor)
        {
            clean = floor = 0;
            for (int dr = -1; dr <= 1; dr++)
                for (int dc = -1; dc <= 1; dc++)
                {
                    if (dr == 0 && dc == 0) continue;
                    int tr = tileRow + dr, tc = tileCol + dc;
                    if (tr >= tileRow0 && tr < tileRow0 + tileRows && tc >= tileCol0 && tc < tileCol0 + tileCols &&
                        held[TileIndex(tr, tc)])
                    {
                        clean++;
                        floor++;
                    }
                    else if (TileWaiting(tr, tc)) floor++;
                }
        }

        /// <summary>The tile still has work: a dirty cell, or unexplored floor (not known blocked).</summary>
        bool TileWaiting(int tileRow, int tileCol)
        {
            int r0 = tileRow * TileCells, c0 = tileCol * TileCells;
            for (int row = r0; row < r0 + TileCells; row++)
                for (int col = c0; col < c0 + TileCells; col++)
                {
                    if (!analysis.IsExplored(row, col)) { if (!analysis.IsBlocked(row, col)) return true; continue; }
                    if (Dirty(row, col)) return true;
                }
            return false;
        }

        // ---- Exploring (SLAM) ------------------------------------------------------------------------------

        /// <summary>The frontier nearest by path: explored floor where the body fits, next to unexplored, reachable over known floor, its tile not skipped.</summary>
        bool PlanFrontier(IBeliefMap map)
        {
            float best = float.PositiveInfinity;
            int bestRow = 0, bestCol = 0;
            int rowEnd = analysis.MinRow + analysis.Rows, colEnd = analysis.MinCol + analysis.Cols;
            for (int row = analysis.MinRow; row < rowEnd; row++)
            {
                for (int col = analysis.MinCol; col < colEnd; col++)
                {
                    if (!analysis.BodyFits(row, col)) continue;
                    bool frontier = !analysis.IsExplored(row + 1, col) || !analysis.IsExplored(row - 1, col) ||
                                    !analysis.IsExplored(row, col + 1) || !analysis.IsExplored(row, col - 1);
                    if (!frontier) continue;
                    float d = analysis.ReachDistance(row, col);
                    if (d >= best) continue;
                    int t = TileIndex(FloorDiv(row, TileCells), FloorDiv(col, TileCells));
                    if (skipCount[t] >= MaxSkips || (skipCount[t] > 0 && !RetryDue(t))) continue;
                    best = d;
                    bestRow = row;
                    bestCol = col;
                }
            }
            if (float.IsPositiveInfinity(best)) return false;

            goal = map.CellCenter(bestRow, bestCol);
            goalTileRow = FloorDiv(bestRow, TileCells);
            goalTileCol = FloorDiv(bestCol, TileCells);
            goalIsFrontier = true;
            hasGoal = true;
            Activity = "exploring";
            Explorations++;

            var drive = context.drive;
            drive.Mode = DriveMode.Travel;
            drive.PassThrough = false;
            drive.Hold = HoldMode.None;
            context.Log($"exploring: frontier ({goal.x:F2}, {goal.y:F2}), {best:F2} m by known floor");
            return true;
        }

        // ---- Skips ------------------------------------------------------------------------------------------

        void Skip(int tileRow, int tileCol)
        {
            int t = TileIndex(tileRow, tileCol);
            if (skipCount[t] < byte.MaxValue) skipCount[t]++;
            skipTime[t] = context.SimSeconds;
            skipOdometer[t] = odometer;
            Skips++;
        }

        bool RetryDue(int t) =>
            odometer - skipOdometer[t] >= RetryAfterMeters || context.SimSeconds - skipTime[t] >= RetryAfterSeconds;

        // ---- Dense per-tile arrays --------------------------------------------------------------------------

        int TileIndex(int tileRow, int tileCol) => (tileRow - tileRow0) * tileCols + (tileCol - tileCol0);

        /// <summary>Grows the tile arrays (with padding) to cover tiles tr0..tr1 × tc0..tc1, keeping what they hold.</summary>
        void EnsureTiles(int tr0, int tc0, int tr1, int tc1)
        {
            if (tileRows > 0 && tr0 >= tileRow0 && tc0 >= tileCol0 &&
                tr1 < tileRow0 + tileRows && tc1 < tileCol0 + tileCols) return;

            int newRow0 = tileRows > 0 ? Math.Min(tileRow0, tr0 - TilePadding) : tr0 - TilePadding;
            int newCol0 = tileRows > 0 ? Math.Min(tileCol0, tc0 - TilePadding) : tc0 - TilePadding;
            int newRowEnd = tileRows > 0 ? Math.Max(tileRow0 + tileRows, tr1 + 1 + TilePadding) : tr1 + 1 + TilePadding;
            int newColEnd = tileRows > 0 ? Math.Max(tileCol0 + tileCols, tc1 + 1 + TilePadding) : tc1 + 1 + TilePadding;
            int rows = newRowEnd - newRow0, cols = newColEnd - newCol0, n = rows * cols;

            var count = new byte[n];
            var heldNew = new bool[n];
            var time = new float[n];
            var odo = new float[n];
            var offset = new short[n];
            var ver = new int[n];
            for (int i = 0; i < n; i++) ver[i] = NoVersion;

            for (int r = 0; r < tileRows; r++)
            {
                for (int c = 0; c < tileCols; c++)
                {
                    int from = r * tileCols + c;
                    int to = (tileRow0 + r - newRow0) * cols + (tileCol0 + c - newCol0);
                    count[to] = skipCount[from];
                    heldNew[to] = held[from];
                    time[to] = skipTime[from];
                    odo[to] = skipOdometer[from];
                    offset[to] = goalOffset[from];
                    ver[to] = goalVersion[from];
                }
            }

            skipCount = count;
            held = heldNew;
            skipTime = time;
            skipOdometer = odo;
            goalOffset = offset;
            goalVersion = ver;
            tileRow0 = newRow0;
            tileCol0 = newCol0;
            tileRows = rows;
            tileCols = cols;
        }

        /// <summary>
        /// Cell offsets from a tile's first row and column whose centres lie within the brush radius of the tile's centre
        /// point, nearest first; equal distances by row, then column. Worked in half-cells so even tile sizes are exact.
        /// </summary>
        void BuildOffsets()
        {
            float cell = context.cellSizeMeters;
            int centre2 = TileCells - 1; // the tile's centre, in half-cells from its first cell's centre
            int reach2 = cell > 0f ? (int)Math.Floor(2f * context.BrushRadius / cell) : 0;
            int span = reach2 / 2 + TileCells;
            var list = new List<(int dr, int dc, int d2)>();
            for (int dr = -span; dr <= span; dr++)
                for (int dc = -span; dc <= span; dc++)
                {
                    int er = 2 * dr - centre2, ec = 2 * dc - centre2;
                    int d2 = er * er + ec * ec;
                    if (d2 <= reach2 * reach2) list.Add((dr, dc, d2));
                }
            list.Sort((a, b) => a.d2 != b.d2 ? a.d2.CompareTo(b.d2) : a.dr != b.dr ? a.dr.CompareTo(b.dr) : a.dc.CompareTo(b.dc));
            offsetRow = new int[list.Count];
            offsetCol = new int[list.Count];
            for (int i = 0; i < list.Count; i++)
            {
                offsetRow[i] = list[i].dr;
                offsetCol[i] = list[i].dc;
            }
        }

        static int FloorDiv(int a, int b) => a >= 0 ? a / b : -((-a + b - 1) / b);
    }
}
