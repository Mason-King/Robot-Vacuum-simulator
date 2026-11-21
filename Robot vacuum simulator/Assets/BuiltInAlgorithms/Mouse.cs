using System;
using System.Collections.Generic;
using RobotVacuum.AlgorithmApi;

namespace RobotVacuum.Algorithms
{
    /// <summary>
    /// Mouse: an inward spiral, room by room, in one pass.
    ///
    /// Room by room (map analysis's rooms; the room the robot is in first, then the nearest by path). In a room:
    ///   1. LOOK (SLAM only): a room is spiraled only once its outline is closed. While a cell of the room borders
    ///      unexplored floor, Mouse drives there first, and plans the rings from the newer map it then has.
    ///   2. RINGS, outermost first. Ring 0 runs along the walls and around furniture: the cells where the body fits
    ///      with a 4-neighbour where it doesn't (as Boustrophedon Crawl's edge). Ring k (k ≥ 1) is the line where the
    ///      robot's center is BodyClearance + k × spacing from the nearest wall or furniture, so the rings follow the
    ///      room's shape and wrap any furniture. All of one ring's pieces are done (nearest first) before the next ring
    ///      in: where furniture splits a ring, every branch is cleaned before Mouse moves inward, so no floor is cut off.
    ///      Each cell's ring is fixed when it is first planned: an obstacle that appears later (the cat, moved furniture)
    ///      only blocks the ring points it covers, which are skipped, and never makes new rings around itself.
    ///   3. THE MIDDLE: when the room's deepest floor is more than <see cref="MiddleFraction"/> of a spacing past the last
    ///      ring, a last run along that ridge finishes it.
    ///   4. PATCHES (only when <see cref="PatchUp"/> is on, as in Crawl): whatever is still below the target.
    /// Every run is at sweep speed, one pass: the spacing is worked out per room as in Crawl (the widest whose worst
    /// point reaches the target at the controller's floor speed). Near obstacles (ring 0, along walls and around
    /// furniture), where turns and bumps already add cleaning, the pass aims at <see cref="ObstacleTargetFraction"/> of
    /// the target (<see cref="DriveRequests.SweepTargetFraction"/>); open floor at the full target.
    /// SLAM: when no room has work but the map has a frontier, explore the nearest. Done: drive.Finish().
    /// </summary>
    public sealed class Mouse : ICleaningAlgorithm
    {
        public const string Id = "Mouse";

        // ---- Tuning ----

        /// <summary>Near obstacles (ring 0), one pass aims at this share of the target.</summary>
        public const float ObstacleTargetFraction = 0.8f;

        /// <summary>A patch-up pass after the rings, as in Crawl. Off: one pass and done.</summary>
        public const bool PatchUp = false;

        /// <summary>The widest ring spacing, as a fraction of the brush width (as Crawl).</summary>
        public const float MaxSpacingFraction = 0.765f;

        /// <summary>The closest ring spacing, as a fraction of the brush width.</summary>
        public const float MinSpacingFraction = 0.25f;

        /// <summary>Spacings tried, from the widest down, in steps of this, meters.</summary>
        public const float SpacingStepMeters = 0.005f;

        /// <summary>A cell counts as clean at this fraction of what it is aimed at (the target, or the lowered target near obstacles).</summary>
        public const float CleanEnough = 0.95f;

        /// <summary>The middle gets its own run when the deepest floor is more than this share of a spacing past the last ring.</summary>
        public const float MiddleFraction = 0.4f;

        /// <summary>Ring 0 keeps the body this far (beyond its radius) from the wall as mapped (as Crawl's edge).</summary>
        public const float EdgeMarginMeters = 0.02f;

        /// <summary>The same, in a SLAM run.</summary>
        public const float SlamEdgeMarginMeters = 0.03f;

        /// <summary>Ring 0's strip: the brush is counted on up to this far short of its reach.</summary>
        public const float EdgeInsetMeters = 0.03f;

        /// <summary>A ring is abandoned after this many of its points in a row couldn't be reached.</summary>
        public const int RingFailuresAllowed = 3;

        /// <summary>Extra margin at ring 0's corners and ends, eased in and out over <see cref="CornerEaseMeters"/>.</summary>
        public const float CornerExtraMeters = 0.02f;
        public const float CornerEaseMeters = 0.1f;

        /// <summary>A waypoint is pass-through only where the ring bends less than this.</summary>
        public const float GentleTurnDegrees = 20f;

        /// <summary>The middle's ring number: after every ring.</summary>
        const int MiddleLevel = int.MaxValue / 2;

        /// <summary>A piece of ring shorter than this many cells is not worth a trip (the middle excepted).</summary>
        public const int MinRingCells = 8;

        /// <summary>Ring chains carry on across gaps up to this many cells.</summary>
        public const int RingGapCells = 2;

        /// <summary>Ring straightening tolerance (Ramer–Douglas–Peucker), meters.</summary>
        public const float RingTolerance = 0.02f;

        /// <summary>A ring leg that grazes the wall's cells is slid off it up to this far, a centimeter at a time.</summary>
        public const float SlideMeters = 0.04f;

        /// <summary>Patch-up (when on): a cell visited this many times without getting clean is given up on.</summary>
        public const int PatchVisits = 3;
        public const int PatchWaypoints = 80;
        public const float PatchTravelMeters = 0.5f;

        /// <summary>A target that failed is skipped until the map has changed this many times, or this many questions have passed.</summary>
        public const int SkipForChanges = 40, SkipForQuestions = 120;

        public string Name => "Mouse";
        public bool SupportsSlam => true;

        // ---- State ----

        AlgorithmContext context;
        IMapAnalysis analysis;
        readonly List<Step> plan = new List<Step>();
        int planIndex;
        int ringFailures;
        int questions;
        readonly Dictionary<long, (int change, int question)> skipped = new Dictionary<long, (int, int)>();
        readonly HashSet<long> ringed = new HashSet<long>();  // ring cells driven (and their neighbors)
        readonly HashSet<long> looked = new HashSet<long>();  // frontier cells visited (SLAM)
        readonly Dictionary<long, int> plannedLevel = new Dictionary<long, int>(); // each cell's ring, fixed when first planned (-1: none)
        readonly Dictionary<long, int> patchVisits = new Dictionary<long, int>();
        readonly Dictionary<int, float> exposureAt = new Dictionary<int, float>();

        // Per decision.
        readonly Dictionary<int, float> roomSpacing = new Dictionary<int, float>();
        float[] roomDistance = new float[0];
        Vec2 anchor;
        Vec2? roomAt;
        string activity = "starting";
        int rings, middles, looks, stubs;

        struct Step
        {
            public Vec2 point;
            public DriveMode mode;
            public float exposure;
            public float targetFraction;
            public bool passThrough;
            public long skipKey;
            public bool ring;   // part of a ring run: if it can't be reached, go on to the next point
            public bool look;   // a frontier visit: mark the area looked at when reached
        }

        public string Activity => activity;
        public int RoomCount => analysis != null ? analysis.RoomCount : 0;

        public void Initialize(AlgorithmContext context)
        {
            this.context = context;
            analysis = context.analysis ?? throw new InvalidOperationException("Mouse needs the controller's map analysis (AlgorithmContext.analysis).");
            plan.Clear();
            planIndex = 0;
            ringFailures = 0;
            questions = 0;
            skipped.Clear();
            ringed.Clear();
            looked.Clear();
            plannedLevel.Clear();
            patchVisits.Clear();
            roomAt = null;
            activity = "starting";
            rings = middles = looks = stubs = 0;
        }

        public Vec2? NextGoal(IBeliefMap map, Pose2 pose, GoalStatus lastStatus)
        {
            questions++;
            if (context.drive.Finished) return null;

            if (planIndex < plan.Count)
            {
                if (lastStatus == GoalStatus.InProgress) return Issue(plan[planIndex]);
                if (lastStatus == GoalStatus.Reached)
                {
                    ringFailures = 0;
                    if (plan[planIndex].look) MarkLooked(plan[planIndex].point);
                    planIndex++;
                    if (planIndex < plan.Count) return Issue(plan[planIndex]);
                }
                else if (lastStatus == GoalStatus.Blocked || lastStatus == GoalStatus.Unreachable)
                {
                    var failed = plan[planIndex];
                    if (failed.ring && planIndex + 1 < plan.Count && ++ringFailures < RingFailuresAllowed)
                    {
                        // One point of a ring (a corner the body can't quite get into): carry on along the ring.
                        context.Log($"ring: waypoint ({failed.point.x:F2}, {failed.point.y:F2}) {lastStatus}; going on to the next");
                        planIndex++;
                        return Issue(plan[planIndex]);
                    }
                    context.Log($"{activity}: goal ({failed.point.x:F2}, {failed.point.y:F2}) {lastStatus}; skipping that piece of work");
                    if (failed.look) MarkLooked(failed.point);
                    Skip(map, failed.skipKey);
                    planIndex = plan.Count;
                }
            }

            plan.Clear();
            planIndex = 0;
            ringFailures = 0;
            if (!Choose(map, pose)) return null;
            return Issue(plan[0]);
        }

        Vec2 Issue(Step step)
        {
            var drive = context.drive;
            drive.Mode = step.mode;
            drive.SweepExposureMeters = step.exposure;
            drive.SweepTargetFraction = step.targetFraction > 0f ? step.targetFraction : 1f;
            drive.PassThrough = step.passThrough;
            drive.Hold = HoldMode.None;
            return step.point;
        }

        static Step Clean(Vec2 point, bool passThrough, long skipKey, float exposure, float targetFraction) => new Step
        {
            point = point,
            mode = DriveMode.Sweep,
            exposure = exposure,
            targetFraction = targetFraction,
            passThrough = passThrough,
            skipKey = skipKey,
        };

        static Step Travel(Vec2 point, long skipKey) => new Step { point = point, mode = DriveMode.Travel, targetFraction = 1f, skipKey = skipKey };

        // ---- Choosing ----------------------------------------------------------------------------------

        bool Choose(IBeliefMap map, Pose2 pose)
        {
            Vec2 here = pose.position;
            analysis.Update(here);
            BuildRoomCells();
            if (analysis.IsEmpty) return Finish();

            anchor = map.AnchorWorld;
            roomSpacing.Clear();
            MeasureRoomDistances();

            foreach (int room in RoomsInOrder(map, here))
            {
                if (context.slam && PlanLook(map, room)) return Begin("looking", room);
                int planned = PlanRing(map, room, out string what);
                if (planned > 0) return Begin(what, room);
                if (PatchUp && PlanPatch(map, room, here)) return Begin("patch", room);
            }

            if (context.slam && PlanFrontier(map)) return Begin("exploring", -1);
            return Finish();
        }

        bool Begin(string what, int room)
        {
            activity = what;
            Vec2 first = plan[0].point;
            if (room >= 0) roomAt = plan[plan.Count - 1].point;
            float distance = PathNear(first);
            string where = room >= 0 ? $" in room {room} of {analysis.RoomCount} (rings {RoomSpacing(room) * 100f:F1} cm apart)" : "";
            string how = float.IsPositiveInfinity(distance) ? "not on known floor" : distance.ToString("F1") + " m by known floor";
            context.Log($"{what}{where}: to ({first.x:F2}, {first.y:F2}), {how}, {plan.Count} waypoints");
            return true;
        }

        bool Finish()
        {
            activity = "done";
            context.Log($"done: {rings} ring runs, {middles} middles, {looks} looks, {stubs} stubs skipped. {Leftovers()}");
            context.drive.SweepTargetFraction = 1f;
            context.drive.Finish();
            return false;
        }

        List<int> RoomsInOrder(IBeliefMap map, Vec2 here)
        {
            var order = new List<int>();
            for (int r = 0; r < analysis.RoomCount; r++) if (!float.IsPositiveInfinity(roomDistance[r])) order.Add(r);
            order.Sort((a, b) => roomDistance[a].CompareTo(roomDistance[b]));
            int current = RoomOf(map, roomAt ?? here);
            if (current >= 0 && order.Remove(current)) order.Insert(0, current);
            return order;
        }

        int RoomOf(IBeliefMap map, Vec2 point)
        {
            if (!map.TryWorldToCell(point, out var center)) return -1;
            for (int radius = 0; radius <= 6; radius++)
                for (int r = center.row - radius; r <= center.row + radius; r++)
                    for (int c = center.col - radius; c <= center.col + radius; c++)
                    {
                        int label = analysis.RoomOf(r, c);
                        if (label >= 0) return label;
                    }
            return -1;
        }

        /// <summary>Each room's path distance from the robot: to its nearest dirty cell (or nearest cell, if all clean).</summary>
        void MeasureRoomDistances()
        {
            int count = analysis.RoomCount;
            if (roomDistance.Length < count) roomDistance = new float[count];
            var anyCell = new float[count];
            for (int r = 0; r < count; r++) roomDistance[r] = anyCell[r] = float.PositiveInfinity;
            ForCells((row, col, label) =>
            {
                float d = analysis.ReachDistance(row, col);
                if (d < anyCell[label]) anyCell[label] = d;
                if (Dirty(row, col) && d < roomDistance[label]) roomDistance[label] = d;
            });
            for (int r = 0; r < count; r++)
                if (float.IsPositiveInfinity(roomDistance[r])) roomDistance[r] = anyCell[r];
        }

        // ---- What is left to do -----------------------------------------------------------------------------

        /// <summary>
        /// Near an obstacle: within ring 0's half of a lane from the nearest wall or furniture (the strip a ring-0 pass
        /// aims at the lowered target). Measured from the clearance line, at the widest spacing.
        /// </summary>
        bool NearObstacle(int row, int col) =>
            !analysis.BodyFits(row, col) ||
            analysis.ObstacleDistance(row, col) < analysis.BodyClearance + 0.5f * context.cleaningWidth * MaxSpacingFraction;

        /// <summary>Below what the cell is aimed at: the target, or near obstacles the lowered target.</summary>
        bool Dirty(int row, int col) =>
            analysis.Cleanliness(row, col) <
            CleanEnough * context.targetCleanliness * (NearObstacle(row, col) ? ObstacleTargetFraction : 1f);

        /// <summary>Patch-up: dirty, not given up on.</summary>
        bool Wanted(int row, int col) =>
            Dirty(row, col) && (!patchVisits.TryGetValue(CellKey(row, col), out int visits) || visits < PatchVisits);

        /// <summary>
        /// Is any floor within the brush's reach of this ring cell dirty? Strip cells (the body doesn't fit) nearer the
        /// wall than the edge margin never get cleaned by a ring, so they don't count (as Crawl's edge).
        /// </summary>
        bool NeedsRing(int row, int col)
        {
            float margin = context.slam ? SlamEdgeMarginMeters : EdgeMarginMeters;
            float toFace = 0.5f * analysis.CellSize * ((float)Math.Sqrt(2.0) - 1f);
            float reachMeters = context.BrushRadius - EdgeInsetMeters;
            int cells = (int)Math.Ceiling(reachMeters / analysis.CellSize);
            float reachSq = reachMeters * reachMeters, size = analysis.CellSize;
            for (int dr = -cells; dr <= cells; dr++)
                for (int dc = -cells; dc <= cells; dc++)
                {
                    if ((dr * dr + dc * dc) * size * size > reachSq) continue;
                    int r = row + dr, c = col + dc;
                    if (analysis.IsBlocked(r, c) || !analysis.IsExplored(r, c)) continue;
                    if (!analysis.BodyFits(r, c) && analysis.ObstacleDistance(r, c) + toFace < margin - 0.001f) continue;
                    if (Dirty(r, c)) return true;
                }
            return false;
        }

        // ---- Spacing and speed, per room ---------------------------------------------------------------------

        float RequiredExposure(float resistance) =>
            context.targetCleanliness * resistance * DriveRequests.SweepSpeedFloor * DriveRequests.SweepSpeedSafety /
            Math.Max(1e-3f, context.vacuumPower);

        float RoomResistance(int room)
        {
            var values = new List<float>();
            ForRoomCells(room, (row, col) => values.Add(analysis.VacuumResistance(row, col)));
            if (values.Count == 0) return 1f;
            values.Sort();
            return Math.Max(1e-3f, values[values.Count / 2]);
        }

        /// <summary>The widest ring spacing whose worst point reaches the target in one pass at the floor speed (as Crawl).</summary>
        float RoomSpacing(int room)
        {
            if (room < 0) return context.cleaningWidth * MaxSpacingFraction;
            if (roomSpacing.TryGetValue(room, out float cached)) return cached;
            float need = RequiredExposure(RoomResistance(room));
            float widest = context.cleaningWidth * MaxSpacingFraction, closest = context.cleaningWidth * MinSpacingFraction;
            float chosen = closest;
            for (float s = widest; s >= closest - 1e-4f; s -= SpacingStepMeters)
            {
                if (ExposureFor(s) < need) continue;
                chosen = s;
                break;
            }
            chosen = (float)Math.Round(chosen * 1000f) / 1000f;
            roomSpacing[room] = chosen;
            return chosen;
        }

        float ExposureFor(float spacingMeters)
        {
            int key = (int)Math.Round(spacingMeters * 1000f);
            if (exposureAt.TryGetValue(key, out float exposure)) return exposure;
            exposure = CoverageMath.LaneExposure(key / 1000f, context.BrushRadius);
            exposureAt[key] = exposure;
            return exposure;
        }

        // ---- 1. Looking (SLAM) -------------------------------------------------------------------------------

        /// <summary>
        /// The room's outline isn't closed while one of its cells (where the body fits) borders unexplored floor: go to the
        /// nearest such cell first. Each visit marks the area around it looked at, so a gap the lidar can never see into
        /// doesn't hold the room up for good.
        /// </summary>
        bool PlanLook(IBeliefMap map, int room)
        {
            float best = float.PositiveInfinity;
            int bestRow = 0, bestCol = 0;
            ForRoomCells(room, (row, col) =>
            {
                if (!analysis.BodyFits(row, col) || looked.Contains(CellKey(row, col))) return;
                bool open = !analysis.IsExplored(row + 1, col) || !analysis.IsExplored(row - 1, col) ||
                            !analysis.IsExplored(row, col + 1) || !analysis.IsExplored(row, col - 1);
                if (!open) return;
                float d = analysis.ReachDistance(row, col);
                if (d >= best || IsSkipped(map, PointKey(CellCenter(row, col)))) return;
                best = d;
                bestRow = row;
                bestCol = col;
            });
            if (float.IsPositiveInfinity(best)) return false;

            Vec2 target = CellCenter(bestRow, bestCol);
            var step = Travel(target, PointKey(target));
            step.look = true;
            plan.Add(step);
            looks++;
            return true;
        }

        void MarkLooked(Vec2 point)
        {
            if (!TryCell(point, out int row, out int col)) return;
            const int around = 4;
            for (int r = row - around; r <= row + around; r++)
                for (int c = col - around; c <= col + around; c++)
                    looked.Add(CellKey(r, c));
        }

        // ---- 2. Rings ---------------------------------------------------------------------------------------

        /// <summary>
        /// The next ring run in the room: the lowest ring with cells still needing it, from its cell nearest by path; all
        /// of a ring's pieces before the next ring in. Returns 1 = planned, -1 = no ring left (stubs are marked done and
        /// looked past).
        /// </summary>
        int PlanRing(IBeliefMap map, int room, out string what)
        {
            what = "ring";
            float spacing = RoomSpacing(room);
            float half = 0.5f * analysis.CellSize;

            // Each room cell's ring: 0 for the cells along walls and furniture, k for those within half a cell of the
            // line k spacings in, the middle for the ridge when it is far enough past the last ring. A cell's ring is
            // FIXED the first time it is planned (normally when Mouse starts the room; in SLAM once its outline is
            // closed): an obstacle that appears later (the cat, moved furniture) only blocks the ring points it covers,
            // which are skipped, and never makes new rings around itself.
            var byLevel = new SortedDictionary<int, List<(int row, int col)>>();
            float deepest = 0f;
            ForRoomCells(room, (row, col) =>
            {
                if (analysis.BodyFits(row, col)) deepest = Math.Max(deepest, analysis.ObstacleDistance(row, col));
            });
            float clearance = analysis.BodyClearance;
            int lastRing = spacing > 0f ? (int)Math.Floor((deepest - clearance) / spacing) : 0;
            float lastRingAt = clearance + lastRing * spacing;
            bool middle = deepest - lastRingAt > MiddleFraction * spacing;

            ForRoomCells(room, (row, col) =>
            {
                long key = CellKey(row, col);
                if (ringed.Contains(key)) return;
                bool fits = analysis.BodyFits(row, col);
                if (!plannedLevel.TryGetValue(key, out int level))
                {
                    if (!fits) return; // not floor the body can use yet: planned when it is
                    level = -1;
                    bool edge = !analysis.BodyFits(row + 1, col) || !analysis.BodyFits(row - 1, col) ||
                                !analysis.BodyFits(row, col + 1) || !analysis.BodyFits(row, col - 1);
                    if (edge) level = 0;
                    else
                    {
                        float d = analysis.ObstacleDistance(row, col);
                        int k = (int)Math.Round((d - clearance) / spacing);
                        if (k >= 1 && Math.Abs(d - (clearance + k * spacing)) <= half) level = k;
                        else if (middle && d >= deepest - 1.5f * half) level = MiddleLevel;
                    }
                    plannedLevel[key] = level;
                }
                if (level < 0 || !fits) return; // no ring here, or blocked right now (skipped; the ring carries on)
                if (!byLevel.TryGetValue(level, out var list)) byLevel[level] = list = new List<(int, int)>();
                list.Add((row, col));
            });

            foreach (var entry in byLevel)
            {
                int level = entry.Key;
                bool isMiddle = level == MiddleLevel;
                for (int attempt = 0; attempt < 50; attempt++)
                {
                    var chain = RingChain(map, entry.Value, isMiddle, out int status);
                    if (status < 0) break;      // nothing needing a ring at this level: the next level in
                    if (chain == null) continue; // a stub, marked done: look again at this level

                    PlanRun(map, room, chain, level, isMiddle);
                    if (isMiddle)
                    {
                        middles++;
                        what = "middle";
                    }
                    else
                    {
                        rings++;
                        what = $"ring {level}";
                    }
                    return 1;
                }
            }
            return -1;
        }

        /// <summary>
        /// A chain of ring cells from the cell needing a ring nearest by path, along the level's other cells (keeping
        /// straight, bridging gaps up to <see cref="RingGapCells"/>). The chain's cells and their neighbors are marked
        /// ringed. Status: 1 = chain, 0 = a stub (marked, null), -1 = no cell at this level needs a ring.
        /// </summary>
        List<Vec2> RingChain(IBeliefMap map, List<(int row, int col)> levelCells, bool isMiddle, out int status)
        {
            status = -1;
            var open = new HashSet<long>();
            (int row, int col) start = (0, 0);
            float best = float.PositiveInfinity;
            foreach (var c in levelCells)
            {
                long key = CellKey(c.row, c.col);
                if (ringed.Contains(key)) continue;
                open.Add(key);
                float d = analysis.ReachDistance(c.row, c.col);
                if (d >= best || IsSkipped(map, PointKey(CellCenter(c.row, c.col))) || !NeedsRing(c.row, c.col)) continue;
                best = d;
                start = c;
            }
            if (float.IsPositiveInfinity(best)) return null;

            var chain = new List<(int row, int col)> { start };
            open.Remove(CellKey(start.row, start.col));
            float dirR = 0f, dirC = 0f;
            while (chain.Count < 4000)
            {
                var (row, col) = chain[chain.Count - 1];
                (int row, int col)? next = null;
                float bestScore = float.PositiveInfinity;
                for (int ring = 1; ring <= 1 + RingGapCells && next == null; ring++)
                    for (int nr = -ring; nr <= ring; nr++)
                        for (int nc = -ring; nc <= ring; nc++)
                        {
                            if (Math.Max(Math.Abs(nr), Math.Abs(nc)) != ring || !open.Contains(CellKey(row + nr, col + nc))) continue;
                            float length = (float)Math.Sqrt(nr * nr + nc * nc);
                            float score = (1f - (nr * dirR + nc * dirC) / length) + 0.1f * length; // turning least, nearest
                            if (score < bestScore)
                            {
                                bestScore = score;
                                next = (row + nr, col + nc);
                            }
                        }
                if (next == null) break;
                if (!map.BodyFitsLeg(CellCenter(row, col), CellCenter(next.Value.row, next.Value.col))) break;

                float stepR = next.Value.row - row, stepC = next.Value.col - col;
                float stepLength = (float)Math.Sqrt(stepR * stepR + stepC * stepC);
                dirR = stepR / stepLength;
                dirC = stepC / stepLength;
                chain.Add(next.Value);
                open.Remove(CellKey(next.Value.row, next.Value.col));
            }

            foreach (var (row, col) in chain)
                for (int r = row - 1; r <= row + 1; r++)
                    for (int c = col - 1; c <= col + 1; c++)
                        ringed.Add(CellKey(r, c));

            if (chain.Count < MinRingCells && !isMiddle)
            {
                stubs++;
                status = 0;
                return null;
            }

            var points = new List<Vec2>(chain.Count);
            foreach (var (row, col) in chain) points.Add(CellCenter(row, col));
            status = 1;
            return points;
        }

        /// <summary>
        /// Drives a chain at sweep speed, once. Ring 0 runs on the edge line (as Crawl's edge) at the lowered target; the
        /// rings inside, and the middle, on their own cells' line at the full target.
        /// </summary>
        void PlanRun(IBeliefMap map, int room, List<Vec2> points, int level, bool isMiddle)
        {
            var run = level == 0 ? EdgeRun(map, points) : StraightRun(map, points);
            float exposure = level == 0
                ? CoverageMath.EdgeExposure(context.BrushRadius, EdgeInsetMeters)
                : ExposureFor(RoomSpacing(room));
            float fraction = level == 0 ? ObstacleTargetFraction : 1f;

            int firstStep = plan.Count;
            if (run.Count == 1) plan.Add(Clean(run[0], false, PointKey(run[0]), exposure, fraction)); // a middle that is a single point
            else plan.Add(Travel(run[0], PointKey(run[0])));
            for (int i = 1; i < run.Count; i++)
            {
                bool through = i < run.Count - 1 && Gentle(run[i - 1], run[i], run[i + 1]);
                plan.Add(Clean(run[i], through, PointKey(run[i]), exposure, fraction));
            }
            EndWithStop();
            for (int i = firstStep; i < plan.Count; i++)
            {
                var step = plan[i];
                step.ring = true;
                plan[i] = step;
            }
        }

        /// <summary>A chain straightened (within <see cref="RingTolerance"/>), legs that graze slid off, stretches that won't straighten driven cell by cell (every 3rd).</summary>
        List<Vec2> StraightRun(IBeliefMap map, List<Vec2> points)
        {
            if (points.Count == 1) return new List<Vec2> { points[0] };
            var keep = new List<int>();
            Simplify(points, 0, points.Count - 1, keep);
            keep.Add(points.Count - 1);
            keep.Sort();

            var run = new List<Vec2> { points[keep[0]] };
            Vec2 previous = run[0];
            for (int k = 1; k < keep.Count; k++)
            {
                Vec2 target = points[keep[k]];
                if (!map.BodyFitsLeg(previous, target) && !TrySlide(map, previous, ref target))
                {
                    for (int i = keep[k - 1] + 3; i < keep[k]; i += 3)
                    {
                        run.Add(points[i]);
                        previous = points[i];
                    }
                    target = points[keep[k]];
                }
                run.Add(target);
                previous = target;
            }
            return run;
        }

        /// <summary>Ring 0: straightened, each kept corner moved onto the edge line, legs that still graze slid off, corners eased (as Crawl's edge).</summary>
        List<Vec2> EdgeRun(IBeliefMap map, List<Vec2> points)
        {
            var keep = new List<int>();
            Simplify(points, 0, points.Count - 1, keep);
            keep.Add(points.Count - 1);
            keep.Sort();

            var moved = new List<Vec2>(keep.Count);
            for (int k = 0; k < keep.Count; k++)
            {
                Vec2 before = points[keep[Math.Max(0, k - 1)]], here = points[keep[k]], after = points[keep[Math.Min(keep.Count - 1, k + 1)]];
                moved.Add(OntoEdgeLine(map, here, Unit(here - before) + Unit(after - here), EdgeClearance));
            }

            var run = new List<Vec2> { moved[0] };
            Vec2 previous = moved[0];
            for (int k = 1; k < keep.Count; k++)
            {
                Vec2 target = moved[k];
                if (!map.BodyFitsLeg(previous, target) && !TrySlide(map, previous, ref target))
                {
                    for (int i = keep[k - 1] + 3; i < keep[k]; i += 3)
                    {
                        run.Add(points[i]);
                        previous = points[i];
                    }
                    target = map.BodyFitsLeg(previous, moved[k]) ? moved[k] : points[keep[k]];
                }
                run.Add(target);
                previous = target;
            }
            return EaseCorners(map, run);
        }

        static bool Gentle(Vec2 a, Vec2 b, Vec2 c)
        {
            Vec2 u = b - a, v = c - b;
            float lu = u.Length, lv = v.Length;
            if (lu < 1e-4f || lv < 1e-4f) return true;
            return Vec2.Dot(u, v) / (lu * lv) >= (float)Math.Cos(GentleTurnDegrees * Math.PI / 180.0);
        }

        List<Vec2> EaseCorners(IBeliefMap map, List<Vec2> run)
        {
            if (run.Count < 2) return run;
            var eased = new List<Vec2>(run.Count * 3);
            for (int i = 0; i < run.Count; i++)
            {
                Vec2 here = run[i];
                bool first = i == 0, last = i == run.Count - 1;
                if (!first && !last && Gentle(run[i - 1], here, run[i + 1]))
                {
                    eased.Add(here);
                    continue;
                }

                Vec2 before = first ? here : run[i - 1], after = last ? here : run[i + 1];
                Vec2 tangent = Unit(here - before) + Unit(after - here);
                Vec2 corner = OntoEdgeLine(map, here, tangent, EdgeClearance + CornerExtraMeters);

                if (!first)
                {
                    Vec2 incoming = here - before;
                    float length = incoming.Length;
                    float ease = Math.Min(CornerEaseMeters, length * 0.4f);
                    if (ease > 0.02f) eased.Add(here - incoming * (ease / length));
                }
                eased.Add(map.BodyFitsLeg(eased.Count > 0 ? eased[eased.Count - 1] : corner, corner) ? corner : here);
                if (!last)
                {
                    Vec2 outgoing = after - here;
                    float length = outgoing.Length;
                    float ease = Math.Min(CornerEaseMeters, length * 0.4f);
                    if (ease > 0.02f) eased.Add(here + outgoing * (ease / length));
                }
            }
            return eased;
        }

        static Vec2 Unit(Vec2 v)
        {
            float length = v.Length;
            return length > 1e-6f ? v * (1f / length) : Vec2.Zero;
        }

        float EdgeClearance => context.robotRadius + (context.slam ? SlamEdgeMarginMeters : EdgeMarginMeters);

        Vec2 OntoEdgeLine(IBeliefMap map, Vec2 point, Vec2 tangent, float clearance)
        {
            float length = tangent.Length;
            if (length < 1e-4f) return point;
            Vec2 normal = new Vec2(-tangent.y / length, tangent.x / length);
            float plus = map.ObstacleDistance(point + normal * 0.03f), minus = map.ObstacleDistance(point - normal * 0.03f);
            if (Math.Abs(plus - minus) < 1e-4f) return point;
            if (plus < minus) normal = -normal;

            Vec2 q = point;
            for (int i = 0; i < 4; i++)
            {
                float move = map.ObstacleDistance(q) - clearance;
                if (Math.Abs(move) < 0.002f) break;
                q = q - normal * move;
            }
            if (map.ObstacleDistance(q) >= clearance - 0.003f) return q;

            Vec2 good = point, bad = q;
            for (int i = 0; i < 8; i++)
            {
                Vec2 middle = (good + bad) * 0.5f;
                if (map.ObstacleDistance(middle) >= clearance - 0.003f) good = middle;
                else bad = middle;
            }
            return good;
        }

        bool TrySlide(IBeliefMap map, Vec2 from, ref Vec2 target)
        {
            Vec2 leg = target - from;
            float length = leg.Length;
            if (length < 1e-4f) return false;
            Vec2 normal = new Vec2(-leg.y / length, leg.x / length);
            Vec2 middle = (from + target) * 0.5f;
            if (map.ObstacleDistance(middle - normal * 0.03f) > map.ObstacleDistance(middle + normal * 0.03f)) normal = -normal;

            for (float slide = 0.01f; slide <= SlideMeters + 1e-4f; slide += 0.01f)
            {
                Vec2 moved = target + normal * slide;
                if (!map.BodyFitsLeg(from, moved)) continue;
                target = moved;
                return true;
            }
            return false;
        }

        static void Simplify(List<Vec2> points, int first, int last, List<int> keep)
        {
            keep.Add(first);
            if (last - first < 2) return;

            Vec2 a = points[first], b = points[last];
            Vec2 ab = b - a;
            float length = ab.Length;
            int farthest = -1;
            float worst = RingTolerance;
            for (int i = first + 1; i < last; i++)
            {
                float d = length > 1e-6f ? Math.Abs(Vec2.Cross(ab, points[i] - a)) / length : Vec2.Distance(points[i], a);
                if (d <= worst) continue;
                worst = d;
                farthest = i;
            }
            if (farthest < 0) return;

            keep.RemoveAt(keep.Count - 1);
            Simplify(points, first, farthest, keep);
            Simplify(points, farthest, last, keep);
        }

        // ---- 3. Patches (only when PatchUp is on) ----------------------------------------------------------------

        bool PlanPatch(IBeliefMap map, int room, Vec2 here)
        {
            var wanted = new HashSet<long>();
            ForRoomCells(room, (row, col) =>
            {
                if (Wanted(row, col) && !IsSkipped(map, PointKey(CellCenter(row, col)))) wanted.Add(CellKey(row, col));
            });
            if (wanted.Count == 0) return false;

            long seed = 0;
            float best = float.PositiveInfinity;
            foreach (long key in wanted)
            {
                float d = analysis.ReachDistance((int)(key >> 32), (int)key);
                if (d < best)
                {
                    best = d;
                    seed = key;
                }
            }
            if (float.IsPositiveInfinity(best)) return false;

            var cluster = new List<long> { seed };
            wanted.Remove(seed);
            for (int k = 0; k < cluster.Count; k++)
            {
                int row = (int)(cluster[k] >> 32), col = (int)cluster[k];
                for (int dr = -1; dr <= 1; dr++)
                    for (int dc = -1; dc <= 1; dc++)
                    {
                        long key = CellKey(row + dr, col + dc);
                        if (!wanted.Remove(key)) continue;
                        cluster.Add(key);
                    }
            }

            foreach (long key in cluster) patchVisits[key] = patchVisits.TryGetValue(key, out int visits) ? visits + 1 : 1;

            float exposure = ExposureFor(RoomSpacing(room));
            var uncovered = new HashSet<long>(cluster);
            Vec2 at = here;
            float cover = context.BrushRadius * 0.9f;
            for (int count = 0; uncovered.Count > 0 && count < PatchWaypoints; count++)
            {
                long nearest = 0;
                float nearestDistance = float.PositiveInfinity;
                foreach (long key in uncovered)
                {
                    float d = Vec2.Distance(CellCenter((int)(key >> 32), (int)key), at);
                    if (d < nearestDistance)
                    {
                        nearestDistance = d;
                        nearest = key;
                    }
                }
                Vec2 target = CellCenter((int)(nearest >> 32), (int)nearest);
                plan.Add(count == 0 && nearestDistance > PatchTravelMeters
                    ? Travel(target, PointKey(target))
                    : Clean(target, map.BodyFitsLeg(at, target), PointKey(target), exposure, 1f));
                uncovered.RemoveWhere(key => Vec2.Distance(CellCenter((int)(key >> 32), (int)key), target) <= cover);
                at = target;
            }
            EndWithStop();
            return true;
        }

        // ---- Exploring (SLAM) ------------------------------------------------------------------------------

        bool PlanFrontier(IBeliefMap map)
        {
            float best = float.PositiveInfinity;
            Vec2 target = Vec2.Zero;
            ForCells((row, col, label) =>
            {
                if (!analysis.BodyFits(row, col) || looked.Contains(CellKey(row, col))) return;
                bool frontier = !analysis.IsExplored(row + 1, col) || !analysis.IsExplored(row - 1, col) ||
                                !analysis.IsExplored(row, col + 1) || !analysis.IsExplored(row, col - 1);
                if (!frontier) return;
                float d = analysis.ReachDistance(row, col);
                Vec2 p = CellCenter(row, col);
                if (d >= best || IsSkipped(map, PointKey(p))) return;
                best = d;
                target = p;
            });
            if (float.IsPositiveInfinity(best)) return false;
            var step = Travel(target, PointKey(target));
            step.look = true;
            plan.Add(step);
            return true;
        }

        // ---- Helpers ----------------------------------------------------------------------------------------

        string Leftovers()
        {
            int count = analysis.RoomCount;
            var below = new int[count];
            var cells = new int[count];
            for (int i = 0; i < roomCells.Count; i++)
            {
                var c = roomCells[i];
                if (c.label >= count) continue;
                cells[c.label]++;
                if (Dirty(c.row, c.col)) below[c.label]++;
            }
            float area = analysis.CellSize * analysis.CellSize;
            var parts = new List<string>();
            int total = 0, totalBelow = 0;
            for (int r = 0; r < count; r++)
            {
                total += cells[r];
                totalBelow += below[r];
                parts.Add($"room {r}: {below[r]}/{cells[r]}");
            }
            return $"Below what they were aimed at: {totalBelow} of {total} room cells ({totalBelow * area:F2} m²) [{string.Join(", ", parts)}].";
        }

        float PathNear(Vec2 point)
        {
            if (!TryCell(point, out int row, out int col)) return float.PositiveInfinity;
            float best = float.PositiveInfinity;
            for (int dr = -4; dr <= 4; dr++)
                for (int dc = -4; dc <= 4; dc++)
                    best = Math.Min(best, analysis.ReachDistance(row + dr, col + dc));
            return best;
        }

        Vec2 CellCenter(int row, int col) =>
            anchor + new Vec2((col + 0.5f) * analysis.CellSize, (row + 0.5f) * analysis.CellSize);

        bool TryCell(Vec2 point, out int row, out int col)
        {
            col = (int)Math.Floor((point.x - anchor.x) / analysis.CellSize);
            row = (int)Math.Floor((point.y - anchor.y) / analysis.CellSize);
            return analysis.Contains(row, col);
        }

        // Every room cell, and each room's cells, row by row, listed once per decision right after analysis.Update
        // (as Crawl).
        readonly List<(int row, int col, int label)> roomCells = new List<(int, int, int)>();
        readonly List<List<(int row, int col)>> cellsByRoom = new List<List<(int, int)>>();

        void BuildRoomCells()
        {
            roomCells.Clear();
            for (int i = 0; i < cellsByRoom.Count; i++) cellsByRoom[i].Clear();
            for (int row = analysis.MinRow; row < analysis.MinRow + analysis.Rows; row++)
                for (int col = analysis.MinCol; col < analysis.MinCol + analysis.Cols; col++)
                {
                    int label = analysis.RoomOf(row, col);
                    if (label < 0) continue;
                    roomCells.Add((row, col, label));
                    while (cellsByRoom.Count <= label) cellsByRoom.Add(new List<(int, int)>());
                    cellsByRoom[label].Add((row, col));
                }
        }

        void ForCells(Action<int, int, int> visit)
        {
            for (int i = 0; i < roomCells.Count; i++)
            {
                var cell = roomCells[i];
                visit(cell.row, cell.col, cell.label);
            }
        }

        void ForRoomCells(int room, Action<int, int> visit)
        {
            if (room < 0 || room >= cellsByRoom.Count) return;
            var cells = cellsByRoom[room];
            for (int i = 0; i < cells.Count; i++) visit(cells[i].row, cells[i].col);
        }

        void EndWithStop()
        {
            var last = plan[plan.Count - 1];
            last.passThrough = false;
            plan[plan.Count - 1] = last;
        }

        static long CellKey(int row, int col) => ((long)row << 32) | (uint)col;

        static long PointKey(Vec2 p) => (1L << 62) ^ ((long)(int)Math.Floor(p.x / 0.25f) << 24) ^ (long)(int)Math.Floor(p.y / 0.25f);

        void Skip(IBeliefMap map, long key) => skipped[key] = (map.ChangeCounter, questions);

        bool IsSkipped(IBeliefMap map, long key) =>
            skipped.TryGetValue(key, out var at) &&
            map.ChangeCounter - at.change < SkipForChanges && questions - at.question < SkipForQuestions;
    }
}
