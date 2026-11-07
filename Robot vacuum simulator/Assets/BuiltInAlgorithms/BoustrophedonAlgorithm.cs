using System;
using System.Collections.Generic;
using RobotVacuum.AlgorithmApi;

namespace RobotVacuum.Algorithms
{
    /// <summary>
    /// Boustrophedon ("ox-turning") coverage: parallel lanes driven back and forth, room by room, in PASSES.
    ///
    /// The run's <see cref="CleaningPriority"/> decides the order and the speed:
    ///   * Coverage: full speed. One whole pass over every room, then another pass (lanes turned 90°, crossing the
    ///     last), each pass complete before the next, until the floor reaches the target.
    ///   * Cleanliness: sweep speed (no slower than the controller's floor, ~0.3 m/s). Each room gets pass after pass
    ///     until it is at the target, then the next room.
    /// Done when everything reachable is at the target (or after <see cref="MaxPasses"/>): drive.Finish(), and the
    /// controller drives home to the dock.
    ///
    /// Each decision starts from a fresh look at the map:
    ///   * REACH (<see cref="ReachField"/>): path distance from the robot to every cell over KNOWN floor. Reachable and
    ///     nearest mean this; nothing is chosen by straight-line distance, and floor glimpsed through a gap in a
    ///     half-drawn wall is not reachable.
    ///   * ROOMS (<see cref="RoomUnits"/>): the floor cut at doorways, found as gaps between wall ends.
    ///   * LANES per room, along that room's own walls (an angled room gets angled lanes).
    ///
    /// A PASS over a room:
    ///   1. EDGES: run along the clearance line (19 cm from a wall face) where the strip by the wall is still below
    ///      the target. Traced as a chain of cells, bridged over small gaps, straightened (Ramer–Douglas–Peucker); a
    ///      straight leg that grazes the wall's staircase of cells is slid off it a centimetre at a time.
    ///   2. LANES: a "cell" is swept monotonically, lane after lane in one direction, joined by semicircular U-turns
    ///      driven without stopping, from the room's end lane nearest the robot. The pass keeps its own record of the
    ///      lane stretches it has swept: driving across the room on the way somewhere does NOT count, so it can't cut
    ///      the lanes in two.
    ///   3. PATCHES: what the lanes missed (dirty, and not under the brush at all this pass), as connected clusters,
    ///      nearest first, each cleaned completely (nearest cell next) before the next.
    /// SLAM: when no room has work in this pass but the map has a frontier, explore the nearest.
    /// </summary>
    public sealed class BoustrophedonAlgorithm : ICleaningAlgorithm
    {
        // ---- Tuning ----

        /// <summary>Cleanliness priority: lane spacing as a fraction of the brush width (0.765 → 26 cm for a 34 cm brush), the spacing that cleans fastest per pass.</summary>
        public const float LaneSpacingFraction = 0.765f;

        /// <summary>Coverage priority: lanes just overlapping (10%), so one pass leaves no gaps with the fewest lanes.</summary>
        public const float CoverageSpacingFraction = 0.9f;

        /// <summary>A cell counts as clean at this fraction of the target.</summary>
        public const float CleanEnough = 0.95f;

        /// <summary>Passes are not repeated forever (a spot the brush can't quite reach would never get clean).</summary>
        public const int MaxPasses = 8;

        /// <summary>Shorter to-do stretches are not worth a lane (patches pick them up).</summary>
        public const float MinRunMeters = 0.15f;

        /// <summary>Points along a U-turn arc (a semicircle), not counting its ends.</summary>
        public const int ArcPoints = 5;

        /// <summary>The edge pass cleans the strip up to this far short of the brush's reach.</summary>
        public const float EdgeInsetMeters = 0.03f;

        /// <summary>A stretch of edge shorter than this many cells is not worth a trip.</summary>
        public const int MinEdgeCells = 8;

        /// <summary>Edge chains carry on across gaps up to this many cells (door jambs, corners).</summary>
        public const int EdgeGapCells = 2;

        /// <summary>Edge straightening tolerance (Ramer–Douglas–Peucker), metres.</summary>
        public const float EdgeTolerance = 0.02f;

        /// <summary>A straightened edge leg that grazes the wall's cells is slid off it up to this far, a centimetre at a time.</summary>
        public const float EdgeSlideMeters = 0.04f;

        /// <summary>A patch cell visited this many times without getting clean is given up on.</summary>
        public const int PatchVisits = 3;

        /// <summary>At most this many waypoints to clean one patch cluster.</summary>
        public const int PatchWaypoints = 80;

        /// <summary>A target that failed is skipped until the map has changed this many times, or this many questions have passed.</summary>
        public const int SkipForChanges = 40, SkipForQuestions = 120;

        public string Name => "Boustrophedon";
        public bool SupportsSlam => true;

        // ---- State ----

        AlgorithmContext context;
        readonly MapSnapshot snapshot = new MapSnapshot();
        readonly ReachField reach = new ReachField();
        readonly RoomUnits rooms = new RoomUnits();
        readonly List<Step> plan = new List<Step>();
        int planIndex;
        readonly Dictionary<long, (int change, int question)> skipped = new Dictionary<long, (int, int)>();
        readonly HashSet<long> edged = new HashSet<long>();
        readonly Dictionary<long, int> patchVisits = new Dictionary<long, int>();
        int questions;

        float spacing;
        float laneExposure;
        int pass;                                                            // 0 = the first pass
        readonly Dictionary<long, float> passStart = new Dictionary<long, float>();              // dwell when the pass began
        readonly Dictionary<long, List<(float s0, float s1)>> swept = new Dictionary<long, List<(float, float)>>(); // this pass

        // Per decision.
        readonly Dictionary<int, Vec2> roomWalls = new Dictionary<int, Vec2>();
        readonly Dictionary<int, List<Run>> roomRuns = new Dictionary<int, List<Run>>();
        float[] roomDistance = new float[0];

        // The lane frame in use: lanes along `along`, at across-offsets lane × spacing from the map's anchor.
        Vec2 laneAnchor, along = new Vec2(1f, 0f), across = new Vec2(0f, 1f);
        int frameKey;
        Vec2? roomAt;
        string activity = "starting";

        struct Step
        {
            public Vec2 point;
            public DriveMode mode;
            public bool passThrough;
            public float exposure;
            public long skipKey;
            public long sweptKey;   // != 0: reaching this point sweeps lane stretch sFrom..sTo
            public float sFrom, sTo;
        }

        struct Run
        {
            public int lane;
            public float s0, s1;
        }

        // For tests and the overlay.
        public float LaneSpacing => spacing;
        public Vec2 LaneDirection => along;
        public int RoomCount => rooms.Count;
        public IReadOnlyList<(Vec2 a, Vec2 b)> Doorways => rooms.Doorways;
        public int Pass => pass;
        public string Activity => activity;

        public void Initialize(AlgorithmContext context)
        {
            this.context = context;
            spacing = context.cleaningWidth *
                      (context.priority == CleaningPriority.Coverage ? CoverageSpacingFraction : LaneSpacingFraction);
            laneExposure = CoverageMath.LaneExposure(spacing, context.BrushRadius);
            pass = 0;
            passStart.Clear();
            swept.Clear();
            plan.Clear();
            planIndex = 0;
            skipped.Clear();
            edged.Clear();
            patchVisits.Clear();
            questions = 0;
            roomAt = null;
            activity = "starting";
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
                    var done = plan[planIndex];
                    if (done.sweptKey != 0) MarkSwept(done.sweptKey, done.sFrom, done.sTo);
                    planIndex++;
                    if (planIndex < plan.Count) return Issue(plan[planIndex]);
                }
                else if (lastStatus == GoalStatus.Blocked || lastStatus == GoalStatus.Unreachable)
                {
                    Skip(map, plan[planIndex].skipKey);
                    planIndex = plan.Count; // abandon the rest of this piece of work
                }
            }

            plan.Clear();
            planIndex = 0;
            if (!Choose(map, pose)) return null;
            return Issue(plan[0]);
        }

        Vec2 Issue(Step step)
        {
            context.drive.Mode = step.mode;
            context.drive.SweepExposureMeters = step.exposure;
            context.drive.PassThrough = step.passThrough;
            return step.point;
        }

        /// <summary>A cleaning step: full speed (Coverage) or sweep speed (Cleanliness).</summary>
        Step Clean(Vec2 point, bool passThrough, long skipKey) => new Step
        {
            point = point,
            mode = context.priority == CleaningPriority.Coverage ? DriveMode.Travel : DriveMode.Sweep,
            exposure = laneExposure,
            passThrough = passThrough,
            skipKey = skipKey,
        };

        // ---- Choosing ----------------------------------------------------------------------------------

        bool Choose(IBeliefMap map, Pose2 pose)
        {
            snapshot.Take(map);
            if (snapshot.IsEmpty) return Finish();

            Vec2 here = pose.position;
            reach.Build(snapshot, map, here);
            rooms.Build(snapshot, map, reach);
            laneAnchor = map.AnchorWorld;
            roomWalls.Clear();
            roomRuns.Clear();
            MeasureRoomDistances();

            // At most one new pass per decision: if a fresh pass still finds nothing to do, nothing can be done.
            for (int attempt = 0; attempt < 2; attempt++)
            {
                int room = PickRoom(map, out bool work, here);
                if (room >= 0 && work)
                {
                    if (PlanEdge(map, room, here)) return Begin("edge", room);
                    if (PlanSweep(map, room, here)) return Begin("lanes", room);
                    if (PlanPatch(map, room, here)) return Begin("patch", room);
                }

                // Nothing left in this pass where it applies. SLAM: explore before starting another pass.
                if (context.slam && PlanFrontier(map, here)) return Begin("exploring", -1);

                bool coverage = context.priority == CleaningPriority.Coverage;
                bool dirty = coverage ? AnyDirty(-1) : room >= 0 && AnyDirty(room);
                if (!dirty || pass + 1 >= MaxPasses * (coverage ? 1 : Math.Max(1, rooms.Count))) break;
                StartPass();
            }
            return Finish();
        }

        bool Begin(string what, int room)
        {
            activity = what;
            if (room >= 0) roomAt = plan[plan.Count - 1].point;
            return true;
        }

        bool Finish()
        {
            activity = "done";
            context.drive.Finish();
            return false;
        }

        /// <summary>
        /// Coverage: the room we are in while it has work in this pass, else the nearest (by path) that has. Cleanliness:
        /// the room we are in while it is dirty (another pass comes before leaving it), else the nearest dirty room.
        /// <paramref name="work"/> says whether that room has work left in THIS pass (if not, a new pass is due).
        /// </summary>
        int PickRoom(IBeliefMap map, out bool work, Vec2 here)
        {
            bool coverage = context.priority == CleaningPriority.Coverage;
            bool Wants(int r) => coverage ? HasPassWork(r) : HasPassWork(r) || AnyDirty(r);

            int current = RoomOf(map, roomAt ?? here);
            int chosen = -1;
            if (current >= 0 && Wants(current))
            {
                chosen = current;
            }
            else
            {
                var order = new List<int>();
                for (int r = 0; r < rooms.Count; r++) if (!float.IsPositiveInfinity(roomDistance[r])) order.Add(r);
                order.Sort((a, b) => roomDistance[a].CompareTo(roomDistance[b]));
                foreach (int r in order)
                {
                    if (!Wants(r)) continue;
                    chosen = r;
                    break;
                }
            }
            work = chosen >= 0 && HasPassWork(chosen);
            return chosen;
        }

        /// <summary>A new pass: lanes turn 90°; swept lanes, edges and patches start over; "missed" counts from now.</summary>
        void StartPass()
        {
            pass++;
            passStart.Clear();
            for (int row = snapshot.MinRow; row < snapshot.MinRow + snapshot.Rows; row++)
                for (int col = snapshot.MinCol; col < snapshot.MinCol + snapshot.Cols; col++)
                {
                    float dwell = snapshot.Dwell(row, col);
                    if (dwell > 0f) passStart[CellKey(row, col)] = dwell;
                }
            swept.Clear();
            edged.Clear();
            patchVisits.Clear();
            skipped.Clear();
            roomRuns.Clear();
            activity = $"pass {pass + 1}";
        }

        int RoomOf(IBeliefMap map, Vec2 point)
        {
            if (!map.TryWorldToCell(point, out var centre)) return -1;
            for (int radius = 0; radius <= 6; radius++)
                for (int r = centre.row - radius; r <= centre.row + radius; r++)
                    for (int c = centre.col - radius; c <= centre.col + radius; c++)
                    {
                        int label = rooms.LabelOf(snapshot, r, c);
                        if (label >= 0) return label;
                    }
            return -1;
        }

        /// <summary>Each room's path distance from the robot: to its nearest dirty cell (or nearest cell, if all clean).</summary>
        void MeasureRoomDistances()
        {
            if (roomDistance.Length < rooms.Count) roomDistance = new float[rooms.Count];
            var anyCell = new float[rooms.Count];
            for (int r = 0; r < rooms.Count; r++) roomDistance[r] = anyCell[r] = float.PositiveInfinity;
            ForCells((row, col, label) =>
            {
                float d = reach.Distance(row, col);
                if (d < anyCell[label]) anyCell[label] = d;
                if (Dirty(row, col) && d < roomDistance[label]) roomDistance[label] = d;
            });
            for (int r = 0; r < rooms.Count; r++)
                if (float.IsPositiveInfinity(roomDistance[r])) roomDistance[r] = anyCell[r];
        }

        // ---- What is left to do -----------------------------------------------------------------------------

        /// <summary>Below the target.</summary>
        bool Dirty(int row, int col) => snapshot.Cleanliness(row, col) < CleanEnough * context.targetCleanliness;

        /// <summary>Not under the brush at all since this pass began (its dwell hasn't grown).</summary>
        bool UntouchedThisPass(int row, int col)
        {
            float before = passStart.TryGetValue(CellKey(row, col), out float d) ? d : 0f;
            return snapshot.Dwell(row, col) <= before + 1e-4f;
        }

        /// <summary>A cell patches go for: dirty, missed by this pass, not given up on.</summary>
        bool Wanted(int row, int col) =>
            Dirty(row, col) && UntouchedThisPass(row, col) &&
            (!patchVisits.TryGetValue(CellKey(row, col), out int visits) || visits < PatchVisits);

        bool HasPassWork(int room)
        {
            if (room < 0) return false;
            if (Runs(room).Count > 0 || EdgeCells(room).Count > 0) return true;
            bool found = false;
            ForRoomCells(room, (row, col) =>
            {
                if (!found && Wanted(row, col)) found = true;
            });
            return found;
        }

        /// <summary>Anything below the target in the room (-1 = in any room).</summary>
        bool AnyDirty(int room)
        {
            bool found = false;
            ForCells((row, col, label) =>
            {
                if (!found && (room < 0 || label == room) && Dirty(row, col)) found = true;
            });
            return found;
        }

        // ---- Lane frames, per room -----------------------------------------------------------------------

        /// <summary>
        /// The room's wall direction. Near a wall the distance transform rises straight away from it, so the gradient's
        /// angle φ is the wall's normal; sampled on the room's floor in a band just inside the clearance line. Rooms are
        /// mostly rectangles, so φ matters modulo 90°: average (cos 4φ, sin 4φ), and the orientation is atan2/4. Lanes
        /// then run along whichever of the two directions gives the longer lanes across the room.
        /// </summary>
        Vec2 RoomWall(int room)
        {
            if (roomWalls.TryGetValue(room, out var cached)) return cached;

            double sumCos = 0, sumSin = 0;
            float low = snapshot.Clearance, high = snapshot.Clearance + 3f * snapshot.CellSize;
            ForRoomCells(room, (row, col) =>
            {
                float d = snapshot.ObstacleDistance(row, col);
                if (d < low || d > high) return;
                float right = snapshot.ObstacleDistance(row, col + 1), left = snapshot.ObstacleDistance(row, col - 1);
                float up = snapshot.ObstacleDistance(row + 1, col), down = snapshot.ObstacleDistance(row - 1, col);
                if (float.IsInfinity(right) || float.IsInfinity(left) || float.IsInfinity(up) || float.IsInfinity(down)) return;
                float gx = right - left, gy = up - down;
                if (gx * gx + gy * gy < 1e-8f) return;
                double phi = Math.Atan2(gy, gx);
                sumCos += Math.Cos(4 * phi);
                sumSin += Math.Sin(4 * phi);
            });

            double angle = sumCos == 0 && sumSin == 0 ? 0.0 : Math.Atan2(sumSin, sumCos) / 4.0;
            var a = new Vec2((float)Math.Cos(angle), (float)Math.Sin(angle));
            var b = new Vec2(-a.y, a.x);
            var wall = Extent(room, a) >= Extent(room, b) ? a : b;
            roomWalls[room] = wall;
            return wall;
        }

        float Extent(int room, Vec2 axis)
        {
            float min = float.PositiveInfinity, max = float.NegativeInfinity;
            ForRoomCells(room, (row, col) =>
            {
                if (((row ^ col) & 1) != 0) return; // every other cell is plenty
                float s = Vec2.Dot(CellCentre(row, col) - laneAnchor, axis);
                min = Math.Min(min, s);
                max = Math.Max(max, s);
            });
            return max > min ? max - min : 0f;
        }

        /// <summary>Use the room's lane frame: along its walls on even passes, across them on odd ones (cross-hatching).</summary>
        void UseFrame(int room)
        {
            var wall = RoomWall(room);
            along = pass % 2 == 0 ? wall : new Vec2(-wall.y, wall.x);
            across = new Vec2(-along.y, along.x);

            // The frame's identity, for the swept-lane record: the lane angle mod 180°, in 2° steps.
            double degrees = Math.Atan2(along.y, along.x) * 180.0 / Math.PI;
            degrees = ((degrees % 180.0) + 180.0) % 180.0;
            frameKey = (int)Math.Round(degrees / 2.0) % 90;
        }

        long SweptKey(int lane) => ((long)(frameKey + 1) << 40) ^ (uint)lane;

        void MarkSwept(long key, float s0, float s1)
        {
            if (!swept.TryGetValue(key, out var list)) swept[key] = list = new List<(float, float)>();
            list.Add((Math.Min(s0, s1), Math.Max(s0, s1)));
        }

        bool IsSwept(int lane, float s)
        {
            if (!swept.TryGetValue(SweptKey(lane), out var list)) return false;
            float tolerance = snapshot.CellSize;
            foreach (var (s0, s1) in list)
                if (s >= s0 - tolerance && s <= s1 + tolerance) return true;
            return false;
        }

        /// <summary>
        /// The room's to-do runs in its frame: lanes sampled every cell; a sample is to do where it is in the room, dirty,
        /// and not on a stretch of lane this pass has swept. Runs shorter than <see cref="MinRunMeters"/> are left to patches.
        /// </summary>
        List<Run> Runs(int room)
        {
            UseFrame(room);
            if (roomRuns.TryGetValue(room, out var cached)) return cached;
            var runs = new List<Run>();
            roomRuns[room] = runs;

            float sMin = float.PositiveInfinity, sMax = float.NegativeInfinity, tMin = float.PositiveInfinity, tMax = float.NegativeInfinity;
            ForRoomCells(room, (row, col) =>
            {
                Vec2 p = CellCentre(row, col) - laneAnchor;
                float s = Vec2.Dot(p, along), t = Vec2.Dot(p, across);
                sMin = Math.Min(sMin, s); sMax = Math.Max(sMax, s);
                tMin = Math.Min(tMin, t); tMax = Math.Max(tMax, t);
            });
            if (float.IsPositiveInfinity(sMin)) return runs;

            float step = snapshot.CellSize;
            int firstLane = (int)Math.Ceiling(tMin / spacing), lastLane = (int)Math.Floor(tMax / spacing);
            for (int lane = firstLane; lane <= lastLane; lane++)
            {
                bool inRun = false;
                float runStart = 0f, lastTodo = 0f;
                for (float s = sMin; s <= sMax + step * 0.5f; s += step)
                {
                    bool todo = TryCell(PointOn(lane, s), out int row, out int col) &&
                                rooms.LabelOf(snapshot, row, col) == room && Dirty(row, col) && !IsSwept(lane, s);
                    if (todo)
                    {
                        if (!inRun)
                        {
                            inRun = true;
                            runStart = s;
                        }
                        lastTodo = s;
                    }
                    else if (inRun)
                    {
                        if (lastTodo - runStart >= MinRunMeters) runs.Add(new Run { lane = lane, s0 = runStart, s1 = lastTodo });
                        inRun = false;
                    }
                }
                if (inRun && lastTodo - runStart >= MinRunMeters) runs.Add(new Run { lane = lane, s0 = runStart, s1 = lastTodo });
            }
            return runs;
        }

        Vec2 PointOn(int lane, float s) => laneAnchor + across * (lane * spacing) + along * s;

        // ---- 1. Edges ----------------------------------------------------------------------------------

        /// <summary>
        /// The room's edge cells still to run along in this pass: cells where the body fits with a 4-neighbour where it
        /// doesn't (a wall's clearance line; across a doorway the next room's floor fits too, so doorways aren't edges),
        /// with dirty strip by the wall within the brush's reach.
        /// </summary>
        List<(int row, int col)> EdgeCells(int room)
        {
            var cells = new List<(int, int)>();
            ForRoomCells(room, (row, col) =>
            {
                if (edged.Contains(CellKey(row, col))) return;
                bool edge = !snapshot.BodyFits(row + 1, col) || !snapshot.BodyFits(row - 1, col) ||
                            !snapshot.BodyFits(row, col + 1) || !snapshot.BodyFits(row, col - 1);
                if (edge && StripDirty(row, col)) cells.Add((row, col));
            });
            return cells;
        }

        /// <summary>Is any of the strip the body can't reach (explored, not blocked, the body doesn't fit) dirty, within the brush's reach of this edge cell?</summary>
        bool StripDirty(int row, int col)
        {
            float reachMeters = context.BrushRadius - EdgeInsetMeters;
            int cells = (int)Math.Ceiling(reachMeters / snapshot.CellSize);
            float reachSq = reachMeters * reachMeters, size = snapshot.CellSize;
            for (int dr = -cells; dr <= cells; dr++)
                for (int dc = -cells; dc <= cells; dc++)
                {
                    if ((dr * dr + dc * dc) * size * size > reachSq) continue;
                    int r = row + dr, c = col + dc;
                    if (snapshot.BodyFits(r, c) || snapshot.IsBlocked(r, c) || !snapshot.IsExplored(r, c)) continue;
                    if (Dirty(r, c)) return true;
                }
            return false;
        }

        bool PlanEdge(IBeliefMap map, int room, Vec2 here)
        {
            // Stubs of edge (a few cells at a door jamb or a corner) are not worth a trip: mark them and look again.
            for (int attempt = 0; attempt < 50; attempt++)
            {
                int planned = TryPlanEdge(map, room);
                if (planned > 0) return true;
                if (planned < 0) return false;
            }
            return false;
        }

        /// <summary>
        /// One run along the edge, from the edge cell nearest by path: follow the chain of edge cells (keeping straight,
        /// bridging gaps up to <see cref="EdgeGapCells"/>), straighten it, and drive it as pass-through waypoints.
        /// Returns 1 = planned; 0 = only a stub (marked done); -1 = no edge left.
        /// </summary>
        int TryPlanEdge(IBeliefMap map, int room)
        {
            var cells = EdgeCells(room);
            if (cells.Count == 0) return -1;

            var open = new HashSet<long>();
            foreach (var c in cells) open.Add(CellKey(c.row, c.col));

            (int row, int col) start = (0, 0);
            float best = float.PositiveInfinity;
            foreach (var c in cells)
            {
                float d = reach.Distance(c.row, c.col);
                if (d < best && !IsSkipped(map, PointKey(CellCentre(c.row, c.col))))
                {
                    best = d;
                    start = c;
                }
            }
            if (float.IsPositiveInfinity(best)) return -1;

            var chain = new List<(int row, int col)> { start };
            open.Remove(CellKey(start.row, start.col));
            float dirR = 0f, dirC = 0f;
            while (chain.Count < 4000)
            {
                var (row, col) = chain[chain.Count - 1];
                (int row, int col)? next = null;
                float bestScore = float.PositiveInfinity;
                for (int ring = 1; ring <= 1 + EdgeGapCells && next == null; ring++)
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
                if (!map.BodyFitsLeg(CellCentre(row, col), CellCentre(next.Value.row, next.Value.col))) break;

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
                        edged.Add(CellKey(r, c));
            if (chain.Count < MinEdgeCells) return 0;

            var points = new List<Vec2>(chain.Count);
            foreach (var (row, col) in chain) points.Add(CellCentre(row, col));
            var keep = new List<int>();
            Simplify(points, 0, points.Count - 1, keep);
            keep.Add(points.Count - 1);
            keep.Sort();

            plan.Add(new Step { point = points[0], mode = DriveMode.Travel, skipKey = PointKey(points[0]) });
            Vec2 previous = points[0];
            for (int k = 1; k < keep.Count; k++)
            {
                Vec2 target = points[keep[k]];
                if (!map.BodyFitsLeg(previous, target) && !TrySlide(map, previous, ref target))
                {
                    // Still grazing: drive this stretch cell by cell (every 3rd).
                    for (int i = keep[k - 1] + 3; i < keep[k]; i += 3)
                    {
                        plan.Add(Clean(points[i], true, PointKey(points[i])));
                        previous = points[i];
                    }
                    target = points[keep[k]];
                }
                plan.Add(Clean(target, true, PointKey(target)));
                previous = target;
            }
            EndWithStop();
            return 1;
        }

        /// <summary>
        /// A straightened leg along a wall drawn in cells grazes the inside corners of the cells' staircase. Slide the
        /// leg's end away from the wall (toward more room) a centimetre at a time until the leg fits.
        /// </summary>
        bool TrySlide(IBeliefMap map, Vec2 from, ref Vec2 target)
        {
            Vec2 leg = target - from;
            float length = leg.Length;
            if (length < 1e-4f) return false;
            Vec2 normal = new Vec2(-leg.y / length, leg.x / length);
            Vec2 middle = (from + target) * 0.5f;
            if (map.ObstacleDistance(middle - normal * 0.03f) > map.ObstacleDistance(middle + normal * 0.03f)) normal = -normal;

            for (float slide = 0.01f; slide <= EdgeSlideMeters + 1e-4f; slide += 0.01f)
            {
                Vec2 moved = target + normal * slide;
                if (!map.BodyFitsLeg(from, moved)) continue;
                target = moved;
                return true;
            }
            return false;
        }

        /// <summary>Ramer–Douglas–Peucker: the indices (besides the last) of the points that keep the line within the tolerance.</summary>
        static void Simplify(List<Vec2> points, int first, int last, List<int> keep)
        {
            keep.Add(first);
            if (last - first < 2) return;

            Vec2 a = points[first], b = points[last];
            Vec2 ab = b - a;
            float length = ab.Length;
            int farthest = -1;
            float worst = EdgeTolerance;
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

        // ---- 2. Lanes ------------------------------------------------------------------------------------

        /// <summary>
        /// One monotone sweep over a cell of the room, in the room's frame. It starts from the run in the room's first or
        /// last lane, whichever end is nearer by path, and moves lane by lane toward the other end: each next run must
        /// lie beside the one before (overlapping along the lanes), reached by a semicircular U-turn of radius half the
        /// spacing, pulled back so its apex stays on the floor. All pass-through, so the robot never stops between lanes.
        /// Each lane stretch is recorded as swept when it is driven.
        /// </summary>
        bool PlanSweep(IBeliefMap map, int room, Vec2 here)
        {
            var mine = Runs(room).FindAll(r => !IsSkipped(map, RunKey(r)));
            if (mine.Count == 0) return false;

            int minLane = int.MaxValue, maxLane = int.MinValue;
            foreach (var r in mine)
            {
                minLane = Math.Min(minLane, r.lane);
                maxLane = Math.Max(maxLane, r.lane);
            }

            Run? fromLow = NearestRunIn(mine, minLane, out float dLow), fromHigh = NearestRunIn(mine, maxLane, out float dHigh);
            if (fromLow == null && fromHigh == null) return false;
            bool low = fromHigh == null || (fromLow != null && dLow <= dHigh);
            Run current = low ? fromLow.Value : fromHigh.Value;
            int direction = low ? 1 : -1;

            // Get to the first run's nearer end (by path).
            bool startAtS0 = PathTo(PointOn(current.lane, current.s0)) <= PathTo(PointOn(current.lane, current.s1));
            float sNow = startAtS0 ? current.s0 : current.s1;
            int sign = startAtS0 ? 1 : -1;
            plan.Add(new Step { point = PointOn(current.lane, sNow), mode = DriveMode.Travel, skipKey = RunKey(current) });

            float radius = spacing * 0.5f;
            for (int lanes = 0; lanes < 200; lanes++)
            {
                float exitS = sign > 0 ? current.s1 : current.s0;
                Run? next = NextRun(mine, current, direction, exitS);

                if (next != null)
                {
                    var n = next.Value;
                    float apex = sign > 0 ? Math.Min(exitS, n.s1) : Math.Max(exitS, n.s0);
                    float turnS = apex - sign * radius;
                    Vec2 a = PointOn(current.lane, turnS), b = PointOn(n.lane, turnS);

                    var arc = new List<Vec2> { a };
                    Vec2 centre = (a + b) * 0.5f;
                    Vec2 outward = along * sign, sideways = across * direction;
                    for (int i = 1; i <= ArcPoints; i++)
                    {
                        double theta = Math.PI * i / (ArcPoints + 1);
                        arc.Add(centre - sideways * (radius * (float)Math.Cos(theta)) + outward * (radius * (float)Math.Sin(theta)));
                    }
                    arc.Add(b);

                    bool fits = (turnS - sNow) * sign > 0f;
                    for (int i = 1; i < arc.Count && fits; i++) fits = map.BodyFitsLeg(arc[i - 1], arc[i]);
                    if (fits)
                    {
                        var leg = Clean(a, true, RunKey(current));
                        leg.sweptKey = SweptKey(current.lane);
                        leg.sFrom = sNow;
                        leg.sTo = turnS;
                        plan.Add(leg);
                        for (int i = 1; i < arc.Count; i++) plan.Add(Clean(arc[i], true, RunKey(n)));
                        current = n;
                        sNow = turnS;
                        sign = -sign;
                        continue;
                    }
                }

                // The end of this cell: sweep to the lane's end and stop.
                var last = Clean(PointOn(current.lane, exitS), false, RunKey(current));
                last.sweptKey = SweptKey(current.lane);
                last.sFrom = sNow;
                last.sTo = exitS;
                plan.Add(last);
                break;
            }
            return true;
        }

        float PathTo(Vec2 point) => TryCell(point, out int row, out int col) ? reach.Distance(row, col) : float.PositiveInfinity;

        Run? NearestRunIn(List<Run> list, int lane, out float distance)
        {
            Run? best = null;
            distance = float.PositiveInfinity;
            foreach (var r in list)
            {
                if (r.lane != lane) continue;
                float d = Math.Min(PathTo(PointOn(r.lane, r.s0)), PathTo(PointOn(r.lane, r.s1)));
                if (d < distance)
                {
                    distance = d;
                    best = r;
                }
            }
            return best;
        }

        /// <summary>The run in the next lane that overlaps this one along the lanes, nearest the exit.</summary>
        Run? NextRun(List<Run> list, Run current, int direction, float exitS)
        {
            Run? best = null;
            float bestDistance = float.PositiveInfinity;
            foreach (var r in list)
            {
                if (r.lane != current.lane + direction) continue;
                float overlap = Math.Min(r.s1, current.s1) - Math.Max(r.s0, current.s0);
                if (overlap < spacing) continue;
                float d = exitS < r.s0 ? r.s0 - exitS : exitS > r.s1 ? exitS - r.s1 : 0f;
                if (d < bestDistance)
                {
                    bestDistance = d;
                    best = r;
                }
            }
            return best;
        }

        // ---- 3. Patches ----------------------------------------------------------------------------------

        /// <summary>
        /// The nearest (by path) cluster of cells this pass missed, cleaned completely: from where the robot is, always on
        /// to the nearest cell of the cluster the brush hasn't covered yet, as pass-through waypoints.
        /// </summary>
        bool PlanPatch(IBeliefMap map, int room, Vec2 here)
        {
            var wanted = new HashSet<long>();
            ForRoomCells(room, (row, col) =>
            {
                if (Wanted(row, col) && !IsSkipped(map, PointKey(CellCentre(row, col)))) wanted.Add(CellKey(row, col));
            });
            if (wanted.Count == 0) return false;

            // The cluster (8-connected) containing the wanted cell nearest by path.
            long seed = 0;
            float best = float.PositiveInfinity;
            foreach (long key in wanted)
            {
                float d = reach.Distance((int)(key >> 32), (int)key);
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

            // Nearest uncovered cell next, until the brush has been over all of them.
            var uncovered = new HashSet<long>(cluster);
            Vec2 at = here;
            float cover = context.BrushRadius * 0.9f;
            for (int count = 0; uncovered.Count > 0 && count < PatchWaypoints; count++)
            {
                long nearest = 0;
                float nearestDistance = float.PositiveInfinity;
                foreach (long key in uncovered)
                {
                    float d = Vec2.Distance(CellCentre((int)(key >> 32), (int)key), at);
                    if (d < nearestDistance)
                    {
                        nearestDistance = d;
                        nearest = key;
                    }
                }
                Vec2 target = CellCentre((int)(nearest >> 32), (int)nearest);
                plan.Add(Clean(target, map.BodyFitsLeg(at, target), PointKey(target)));
                uncovered.RemoveWhere(key => Vec2.Distance(CellCentre((int)(key >> 32), (int)key), target) <= cover);
                at = target;
            }
            EndWithStop();
            return true;
        }

        // ---- Exploring (SLAM) ------------------------------------------------------------------------------

        /// <summary>The frontier nearest by path: explored floor where the body fits, next to unexplored, that the robot can reach over known floor.</summary>
        bool PlanFrontier(IBeliefMap map, Vec2 here)
        {
            float best = float.PositiveInfinity;
            Vec2 target = here;
            ForCells((row, col, label) =>
            {
                bool frontier = !snapshot.IsExplored(row + 1, col) || !snapshot.IsExplored(row - 1, col) ||
                                !snapshot.IsExplored(row, col + 1) || !snapshot.IsExplored(row, col - 1);
                if (!frontier) return;
                float d = reach.Distance(row, col);
                Vec2 p = CellCentre(row, col);
                if (d >= best || IsSkipped(map, PointKey(p))) return;
                best = d;
                target = p;
            });
            if (float.IsPositiveInfinity(best)) return false;
            plan.Add(new Step { point = target, mode = DriveMode.Travel, skipKey = PointKey(target) });
            return true;
        }

        // ---- Helpers ----------------------------------------------------------------------------------------

        /// <summary>A cell's centre: the map's anchor (a cell corner) plus (col + ½, row + ½) cells.</summary>
        Vec2 CellCentre(int row, int col) =>
            laneAnchor + new Vec2((col + 0.5f) * snapshot.CellSize, (row + 0.5f) * snapshot.CellSize);

        bool TryCell(Vec2 point, out int row, out int col)
        {
            col = (int)Math.Floor((point.x - laneAnchor.x) / snapshot.CellSize);
            row = (int)Math.Floor((point.y - laneAnchor.y) / snapshot.CellSize);
            return snapshot.Contains(row, col);
        }

        /// <summary>Every cell that belongs to a room, with its room.</summary>
        void ForCells(Action<int, int, int> visit)
        {
            for (int row = snapshot.MinRow; row < snapshot.MinRow + snapshot.Rows; row++)
                for (int col = snapshot.MinCol; col < snapshot.MinCol + snapshot.Cols; col++)
                {
                    int label = rooms.LabelOf(snapshot, row, col);
                    if (label >= 0) visit(row, col, label);
                }
        }

        void ForRoomCells(int room, Action<int, int> visit) =>
            ForCells((row, col, label) =>
            {
                if (label == room) visit(row, col);
            });

        void EndWithStop()
        {
            var last = plan[plan.Count - 1];
            last.passThrough = false;
            plan[plan.Count - 1] = last;
        }

        static long CellKey(int row, int col) => ((long)row << 32) | (uint)col;

        static long PointKey(Vec2 p) => (1L << 62) ^ ((long)(int)Math.Floor(p.x / 0.25f) << 24) ^ (long)(int)Math.Floor(p.y / 0.25f);

        long RunKey(Run r) => (2L << 61) ^ ((long)frameKey << 48) ^ ((long)r.lane << 24) ^ (long)(int)Math.Floor(r.s0 / 0.25f);

        void Skip(IBeliefMap map, long key) => skipped[key] = (map.ChangeCounter, questions);

        bool IsSkipped(IBeliefMap map, long key) =>
            skipped.TryGetValue(key, out var at) &&
            map.ChangeCounter - at.change < SkipForChanges && questions - at.question < SkipForQuestions;
    }
}
