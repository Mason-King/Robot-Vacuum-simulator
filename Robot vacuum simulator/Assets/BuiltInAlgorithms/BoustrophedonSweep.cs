using System;
using System.Collections.Generic;
using RobotVacuum.AlgorithmApi;

namespace RobotVacuum.Algorithms
{
    /// <summary>
    /// Boustrophedon Sweep: cover all the floor as fast as possible, then go round again until it is clean.
    ///
    /// Parallel lanes driven back and forth at FULL speed, room by room, in PASSES. Lanes just overlap (10%), so one
    /// pass leaves no gaps with the fewest lanes. A pass covers every room before the next pass begins; each new pass
    /// turns the lanes 90° (crossing the last), and passes repeat until everything reachable is at the target (or after
    /// <see cref="MaxPasses"/>). A user who finds one pass enough can stop it. Then drive.Finish(), and the controller
    /// drives home to the dock. (Boustrophedon Crawl is the slow, thorough one.)
    ///
    /// Each decision starts from the controller's map analysis (<see cref="IMapAnalysis"/>):
    ///   * REACH: path distance from the robot over KNOWN floor. Reachable and nearest mean this.
    ///   * ROOMS: the floor cut at doorways.
    ///   * LANES per room, along that room's own walls (an angled room gets angled lanes).
    ///
    /// A pass over a room:
    ///   1. EDGES: along the walls where the strip by the wall is below the target, as close as the edge margin lets
    ///      the body go (<see cref="EdgeMarginMeters"/> from the wall as mapped). Traced as a chain of cells, bridged
    ///      over small gaps, straightened (Ramer–Douglas–Peucker), each corner moved onto the line that distance from
    ///      the wall; a leg that still grazes the wall's staircase of cells is slid off it a centimetre at a time.
    ///   2. LANES: a "cell" is swept monotonically, lane after lane in one direction, joined by U-turns driven without
    ///      stopping, from the room's end lane nearest the robot. The pass keeps its own record of the lane stretches it
    ///      has swept: driving across the room on the way somewhere does NOT count.
    ///   3. PATCHES: what the lanes missed (dirty, and not under the brush at all this pass), as connected clusters,
    ///      nearest first.
    /// SLAM: when no room has work in this pass but the map has a frontier, explore the nearest.
    /// </summary>
    public sealed class BoustrophedonSweep : ICleaningAlgorithm
    {
        public const string Id = "BoustrophedonSweep";

        // ---- Tuning ----

        /// <summary>Lanes just overlapping (10%): one pass leaves no gaps with the fewest lanes.</summary>
        public const float SpacingFraction = 0.9f;

        /// <summary>A cell counts as clean at this fraction of the target.</summary>
        public const float CleanEnough = 0.95f;

        /// <summary>Passes are not repeated forever (a spot the brush can't quite reach would never get clean).</summary>
        public const int MaxPasses = 8;

        /// <summary>Shorter to-do stretches are not worth a lane (patches pick them up).</summary>
        public const float MinRunMeters = 0.15f;

        /// <summary>At most this many points along a U-turn's semicircle (not counting its ends), and at least this far apart.</summary>
        public const int MaxArcPoints = 5;
        public const float ArcPointSpacingMeters = 0.08f;

        /// <summary>The edge run keeps the body this far (beyond its radius) from the wall as mapped. Paths elsewhere keep the planner's margin.</summary>
        public const float EdgeMarginMeters = 0.02f;

        /// <summary>The same, in a SLAM run: walls drawn from the lidar are less exact, and every bump disturbs the pose.</summary>
        public const float SlamEdgeMarginMeters = 0.03f;

        /// <summary>Extra margin at an edge run's corners and ends, where two walls' errors (and door jambs) add up.</summary>
        public const float CornerExtraMeters = 0.02f;

        /// <summary>The extra margin is eased in and out over this distance either side of a corner, so the walls between keep the edge line.</summary>
        public const float CornerEaseMeters = 0.1f;

        /// <summary>A lane carries straight on over floor already clean for up to this far, rather than splitting there.</summary>
        public const float BridgeMeters = 0.6f;

        /// <summary>The edge pass is for the strip up to this far short of the brush's reach.</summary>
        public const float EdgeInsetMeters = 0.03f;

        /// <summary>An edge waypoint is pass-through only where the run bends less than this.</summary>
        public const float GentleTurnDegrees = 20f;

        /// <summary>A stretch of edge shorter than this many cells is not worth a trip.</summary>
        public const int MinEdgeCells = 8;

        /// <summary>Edge chains carry on across gaps up to this many cells (door jambs, corners).</summary>
        public const int EdgeGapCells = 2;

        /// <summary>Edge straightening tolerance (Ramer–Douglas–Peucker), metres.</summary>
        public const float EdgeTolerance = 0.02f;

        /// <summary>An edge leg that grazes the wall's cells is slid off it up to this far, a centimetre at a time.</summary>
        public const float EdgeSlideMeters = 0.04f;

        /// <summary>A patch cell visited this many times without getting clean is given up on.</summary>
        public const int PatchVisits = 3;

        /// <summary>At most this many waypoints to clean one patch cluster.</summary>
        public const int PatchWaypoints = 80;

        /// <summary>A target that failed is skipped until the map has changed this many times, or this many questions have passed.</summary>
        public const int SkipForChanges = 40, SkipForQuestions = 120;

        public string Name => "Boustrophedon Sweep";
        public bool SupportsSlam => true;

        // ---- State ----

        AlgorithmContext context;
        IMapAnalysis analysis;
        readonly List<Step> plan = new List<Step>();
        int planIndex;
        readonly Dictionary<long, (int change, int question)> skipped = new Dictionary<long, (int, int)>();
        readonly HashSet<long> edged = new HashSet<long>();
        readonly Dictionary<long, int> patchVisits = new Dictionary<long, int>();
        int questions;

        float spacing;
        int pass;                                                                                   // 0 = the first pass
        readonly Dictionary<long, float> passStart = new Dictionary<long, float>();                // dwell when the pass began
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
            public bool passThrough;
            public long skipKey;
            public long sweptKey;   // != 0: reaching this point sweeps lane stretch sFrom..sTo
            public bool edge;       // part of an edge run: if it can't be reached, go on to the next one
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
        public int RoomCount => analysis != null ? analysis.RoomCount : 0;
        public IReadOnlyList<(Vec2 a, Vec2 b)> Doorways => analysis != null ? analysis.Doorways : Array.Empty<(Vec2, Vec2)>();
        public int Pass => pass;
        public string Activity => activity;

        public void Initialize(AlgorithmContext context)
        {
            this.context = context;
            analysis = context.analysis ?? throw new InvalidOperationException("Boustrophedon Sweep needs the controller's map analysis (AlgorithmContext.analysis).");
            spacing = context.cleaningWidth * SpacingFraction;
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
                    var failed = plan[planIndex].point;
                    if (plan[planIndex].edge && planIndex + 1 < plan.Count)
                    {
                        // One point of an edge run (a corner the body can't quite get into): carry on along the run.
                        context.Log($"edge: waypoint ({failed.x:F2}, {failed.y:F2}) {lastStatus}; going on to the next");
                        planIndex++;
                        return Issue(plan[planIndex]);
                    }
                    context.Log($"{activity}: goal ({failed.x:F2}, {failed.y:F2}) {lastStatus}; skipping that piece of work");
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
            context.drive.Mode = DriveMode.Travel; // Sweep always drives at full speed
            context.drive.PassThrough = step.passThrough;
            return step.point;
        }

        Step Clean(Vec2 point, bool passThrough, long skipKey) => new Step { point = point, passThrough = passThrough, skipKey = skipKey };

        // ---- Choosing ----------------------------------------------------------------------------------

        bool Choose(IBeliefMap map, Pose2 pose)
        {
            Vec2 here = pose.position;
            analysis.Update(here);
            if (analysis.IsEmpty) return Finish();

            laneAnchor = map.AnchorWorld;
            roomWalls.Clear();
            roomRuns.Clear();
            MeasureRoomDistances();

            // At most one new pass per decision: if a fresh pass still finds nothing to do, nothing can be done.
            for (int attempt = 0; attempt < 2; attempt++)
            {
                // The room we are in first, while it has work in this pass; then the others, nearest first. A room whose
                // work can't be planned (edge stubs, everything skipped) doesn't stop the others being tried.
                foreach (int room in RoomsInOrder(map, here))
                {
                    if (!HasPassWork(room)) continue;
                    if (PlanEdge(map, room)) return Begin("edge", room);
                    if (PlanSweep(map, room)) return Begin("lanes", room);
                    if (PlanPatch(map, room, here)) return Begin("patch", room);
                }

                // Nothing left in this pass where it applies. SLAM: explore before starting another pass.
                if (context.slam && PlanFrontier(map, here)) return Begin("exploring", -1);

                if (!AnyDirty() || pass + 1 >= MaxPasses) break;
                StartPass();
            }
            return Finish();
        }

        bool Begin(string what, int room)
        {
            activity = what;
            Vec2 first = plan[0].point;
            if (room >= 0) roomAt = plan[plan.Count - 1].point;
            float distance = PathNear(first);
            string where = room >= 0 ? " in room " + room + " of " + analysis.RoomCount : "";
            string how = float.IsPositiveInfinity(distance) ? "not on known floor" : distance.ToString("F1") + " m by known floor";
            context.Log($"pass {pass + 1}, {what}{where}: to ({first.x:F2}, {first.y:F2}), {how}, {plan.Count} waypoints");
            return true;
        }

        bool Finish()
        {
            activity = "done";
            string passes = pass == 0 ? "1 pass" : (pass + 1) + " passes";
            context.Log($"done after {passes}. {Leftovers()}");
            context.drive.Finish();
            return false;
        }

        /// <summary>The rooms the robot can reach: the one it is in first, then the others nearest (by path) first.</summary>
        List<int> RoomsInOrder(IBeliefMap map, Vec2 here)
        {
            var order = new List<int>();
            for (int r = 0; r < analysis.RoomCount; r++) if (!float.IsPositiveInfinity(roomDistance[r])) order.Add(r);
            order.Sort((a, b) => roomDistance[a].CompareTo(roomDistance[b]));
            int current = RoomOf(map, roomAt ?? here);
            if (current >= 0 && order.Remove(current)) order.Insert(0, current);
            return order;
        }

        /// <summary>A new pass: lanes turn 90°; swept lanes, edges and patches start over; "missed" counts from now.</summary>
        void StartPass()
        {
            pass++;
            passStart.Clear();
            for (int row = analysis.MinRow; row < analysis.MinRow + analysis.Rows; row++)
                for (int col = analysis.MinCol; col < analysis.MinCol + analysis.Cols; col++)
                {
                    float dwell = analysis.Dwell(row, col);
                    if (dwell > 0f) passStart[CellKey(row, col)] = dwell;
                }
            swept.Clear();
            edged.Clear();
            patchVisits.Clear();
            skipped.Clear();
            roomRuns.Clear();
            context.Log($"pass {pass + 1} begins (lanes turned 90°)");
        }

        int RoomOf(IBeliefMap map, Vec2 point)
        {
            if (!map.TryWorldToCell(point, out var centre)) return -1;
            for (int radius = 0; radius <= 6; radius++)
                for (int r = centre.row - radius; r <= centre.row + radius; r++)
                    for (int c = centre.col - radius; c <= centre.col + radius; c++)
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

        /// <summary>For the log: what is still below the target, by room, and in the strips by the walls.</summary>
        string Leftovers()
        {
            int count = analysis.RoomCount;
            var below = new int[count];
            var cells = new int[count];
            int strip = 0, stripBelow = 0;
            for (int row = analysis.MinRow; row < analysis.MinRow + analysis.Rows; row++)
                for (int col = analysis.MinCol; col < analysis.MinCol + analysis.Cols; col++)
                {
                    int label = analysis.RoomOf(row, col);
                    if (label >= 0)
                    {
                        cells[label]++;
                        if (Dirty(row, col)) below[label]++;
                    }
                    else if (analysis.IsExplored(row, col) && !analysis.IsBlocked(row, col) && NextToRoom(row, col))
                    {
                        strip++;
                        if (Dirty(row, col)) stripBelow++;
                    }
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
            return $"Below target: {totalBelow} of {total} room cells ({totalBelow * area:F2} m²) [{string.Join(", ", parts)}]; " +
                   $"wall strips {stripBelow} of {strip} ({stripBelow * area:F2} m²).";
        }

        /// <summary>A cell within the brush's reach of a room cell (the strip by a wall the body can't stand on).</summary>
        bool NextToRoom(int row, int col)
        {
            int cells = (int)Math.Ceiling(context.BrushRadius / analysis.CellSize);
            for (int dr = -cells; dr <= cells; dr++)
                for (int dc = -cells; dc <= cells; dc++)
                    if (analysis.RoomOf(row + dr, col + dc) >= 0) return true;
            return false;
        }

        // ---- What is left to do -----------------------------------------------------------------------------

        /// <summary>Below the target.</summary>
        bool Dirty(int row, int col) => analysis.Cleanliness(row, col) < CleanEnough * context.targetCleanliness;

        /// <summary>Not under the brush at all since this pass began (its dwell hasn't grown).</summary>
        bool UntouchedThisPass(int row, int col)
        {
            float before = passStart.TryGetValue(CellKey(row, col), out float d) ? d : 0f;
            return analysis.Dwell(row, col) <= before + 1e-4f;
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

        /// <summary>Anything below the target in any room.</summary>
        bool AnyDirty()
        {
            bool found = false;
            ForCells((row, col, label) =>
            {
                if (!found && Dirty(row, col)) found = true;
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
            float low = analysis.BodyClearance, high = analysis.BodyClearance + 3f * analysis.CellSize;
            ForRoomCells(room, (row, col) =>
            {
                float d = analysis.ObstacleDistance(row, col);
                if (d < low || d > high) return;
                float right = analysis.ObstacleDistance(row, col + 1), left = analysis.ObstacleDistance(row, col - 1);
                float up = analysis.ObstacleDistance(row + 1, col), down = analysis.ObstacleDistance(row - 1, col);
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
            float tolerance = analysis.CellSize;
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

            float step = analysis.CellSize;
            int firstLane = (int)Math.Ceiling(tMin / spacing), lastLane = (int)Math.Floor(tMax / spacing);
            for (int lane = firstLane; lane <= lastLane; lane++)
            {
                bool inRun = false;
                float runStart = 0f, lastTodo = 0f;
                for (float s = sMin; s <= sMax + step * 0.5f; s += step)
                {
                    bool inRoom = TryCell(PointOn(lane, s), out int row, out int col) && analysis.RoomOf(row, col) == room;
                    bool sweptHere = IsSwept(lane, s);
                    bool todo = inRoom && !sweptHere && Dirty(row, col);
                    if (todo)
                    {
                        if (!inRun)
                        {
                            inRun = true;
                            runStart = s;
                        }
                        lastTodo = s;
                    }
                    else if (inRun && !(inRoom && !sweptHere && s - lastTodo <= BridgeMeters))
                    {
                        // Not floor of this room, already swept, or clean for too long: the run ends at its last dirty sample.
                        // (A short clean stretch, like the spot where the robot turned on the dock, is carried over.)
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
                bool edge = !analysis.BodyFits(row + 1, col) || !analysis.BodyFits(row - 1, col) ||
                            !analysis.BodyFits(row, col + 1) || !analysis.BodyFits(row, col - 1);
                if (edge && StripDirty(row, col)) cells.Add((row, col));
            });
            return cells;
        }

        /// <summary>Is any of the strip the body can't stand on (explored, not blocked, the body doesn't fit) dirty, within the brush's reach of this edge cell?</summary>
        bool StripDirty(int row, int col)
        {
            float reachMeters = context.BrushRadius - EdgeInsetMeters;
            int cells = (int)Math.Ceiling(reachMeters / analysis.CellSize);
            float reachSq = reachMeters * reachMeters, size = analysis.CellSize;
            for (int dr = -cells; dr <= cells; dr++)
                for (int dc = -cells; dc <= cells; dc++)
                {
                    if ((dr * dr + dc * dc) * size * size > reachSq) continue;
                    int r = row + dr, c = col + dc;
                    if (analysis.BodyFits(r, c) || analysis.IsBlocked(r, c) || !analysis.IsExplored(r, c)) continue;
                    if (Dirty(r, c)) return true;
                }
            return false;
        }

        bool PlanEdge(IBeliefMap map, int room)
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
        /// bridging gaps up to <see cref="EdgeGapCells"/>), straighten it, move its corners onto the edge line, and drive
        /// it as pass-through waypoints. Returns 1 = planned; 0 = only a stub (marked done); -1 = no edge left.
        /// </summary>
        int TryPlanEdge(IBeliefMap map, int room)
        {
            var points = EdgeChain(map, room, out int status);
            if (points == null) return status;

            var run = EdgeRun(map, points);
            int firstStep = plan.Count;
            plan.Add(new Step { point = run[0], skipKey = PointKey(run[0]) }); // getting there: a stop, not pass-through
            for (int i = 1; i < run.Count; i++)
            {
                bool through = i < run.Count - 1 && Gentle(run[i - 1], run[i], run[i + 1]);
                plan.Add(Clean(run[i], through, PointKey(run[i])));
            }
            EndWithStop();
            for (int i = firstStep; i < plan.Count; i++)
            {
                var step = plan[i];
                step.edge = true;
                plan[i] = step;
            }
            return 1;
        }

        /// <summary>
        /// The chain of edge cell centres from the nearest edge cell, or null with <paramref name="status"/> 0 (a stub,
        /// marked done) or -1 (no edge left). The chain's cells and their neighbours are marked done for this pass.
        /// </summary>
        List<Vec2> EdgeChain(IBeliefMap map, int room, out int status)
        {
            status = -1;
            var cells = EdgeCells(room);
            if (cells.Count == 0) return null;

            var open = new HashSet<long>();
            foreach (var c in cells) open.Add(CellKey(c.row, c.col));

            (int row, int col) start = (0, 0);
            float best = float.PositiveInfinity;
            foreach (var c in cells)
            {
                float d = analysis.ReachDistance(c.row, c.col);
                if (d < best && !IsSkipped(map, PointKey(CellCentre(c.row, c.col))))
                {
                    best = d;
                    start = c;
                }
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
            if (chain.Count < MinEdgeCells)
            {
                status = 0;
                return null;
            }

            var points = new List<Vec2>(chain.Count);
            foreach (var (row, col) in chain) points.Add(CellCentre(row, col));
            status = 1;
            return points;
        }

        /// <summary>
        /// The waypoints of an edge run along a chain of edge cell centres: straightened, each kept corner moved onto the
        /// edge line (<see cref="EdgeClearance"/> from the wall), legs that still graze slid off, stretches that won't
        /// straighten driven cell by cell (every 3rd).
        /// </summary>
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
                    // Still grazing: drive this stretch cell by cell (every 3rd), on the cells' own centres.
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

        /// <summary>
        /// True when the run bends less than <see cref="GentleTurnDegrees"/> at <paramref name="b"/>. Only such points are
        /// pass-through: a pass-through goal counts as reached 5 cm early, so at a real corner the robot would start the
        /// next stretch 5 cm off the line (and miss the strip by the wall for much of it).
        /// </summary>
        static bool Gentle(Vec2 a, Vec2 b, Vec2 c)
        {
            Vec2 u = b - a, v = c - b;
            float lu = u.Length, lv = v.Length;
            if (lu < 1e-4f || lv < 1e-4f) return true;
            return Vec2.Dot(u, v) / (lu * lv) >= (float)Math.Cos(GentleTurnDegrees * Math.PI / 180.0);
        }

        /// <summary>
        /// At each sharp corner of an edge run, and at its two ends, the robot has to stop on the point, and there the
        /// errors of two walls (or a door jamb) add up: so that point is moved <see cref="CornerExtraMeters"/> further out,
        /// with a point on the edge line <see cref="CornerEaseMeters"/> before and after it, so the stretches of wall
        /// between corners still run on the edge line.
        /// </summary>
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
                Vec2 tangent = Unit(here - before) + Unit(after - here); // a corner moves along its bisector, toward both walls
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

        /// <summary>The unit vector along <paramref name="v"/>, or zero.</summary>
        static Vec2 Unit(Vec2 v)
        {
            float length = v.Length;
            return length > 1e-6f ? v * (1f / length) : Vec2.Zero;
        }

        /// <summary>How far from the wall (as mapped) the edge run keeps the robot's centre: its radius plus the edge margin.</summary>
        float EdgeClearance => context.robotRadius + (context.slam ? SlamEdgeMarginMeters : EdgeMarginMeters);

        /// <summary>
        /// Moves a point of an edge run straight toward the wall, along the normal to <paramref name="tangent"/>, until it
        /// is <paramref name="clearance"/> from the nearest blocked cell. Measured, not assumed: a few steps of "move by the
        /// difference", then backed off if a corner of the wall came closer than that.
        /// </summary>
        Vec2 OntoEdgeLine(IBeliefMap map, Vec2 point, Vec2 tangent, float clearance)
        {
            float length = tangent.Length;
            if (length < 1e-4f) return point;
            Vec2 normal = new Vec2(-tangent.y / length, tangent.x / length);
            float plus = map.ObstacleDistance(point + normal * 0.03f), minus = map.ObstacleDistance(point - normal * 0.03f);
            if (Math.Abs(plus - minus) < 1e-4f) return point; // no wall near enough to tell which side it is on
            if (plus < minus) normal = -normal;                // the normal points away from the wall

            float edge = clearance;
            Vec2 q = point;
            for (int i = 0; i < 4; i++)
            {
                float move = map.ObstacleDistance(q) - edge;
                if (Math.Abs(move) < 0.002f) break;
                q = q - normal * move;
            }
            if (map.ObstacleDistance(q) >= edge - 0.003f) return q;

            // A corner of the wall is closer than the line: go back toward where we started until it isn't.
            Vec2 good = point, bad = q;
            for (int i = 0; i < 8; i++)
            {
                Vec2 middle = (good + bad) * 0.5f;
                if (map.ObstacleDistance(middle) >= edge - 0.003f) good = middle;
                else bad = middle;
            }
            return good;
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
        bool PlanSweep(IBeliefMap map, int room)
        {
            var mine = Runs(room).FindAll(r => !IsSkipped(map, RunKey(r)));
            if (mine.Count == 0) return false;

            // Start from the nearest END of a block of runs: a run with no overlapping run in the lane on one side. For a whole
            // room that is its first or last lane; for leftover fragments, the nearest fragment, swept on through its
            // neighbours, instead of jumping between the room's far sides.
            Run current = mine[0];
            int direction = 1;
            float nearest = float.PositiveInfinity;
            foreach (var r in mine)
            {
                bool below = HasNeighbour(mine, r, -1), above = HasNeighbour(mine, r, 1);
                if (below && above) continue;
                float d = Math.Min(PathTo(PointOn(r.lane, r.s0)), PathTo(PointOn(r.lane, r.s1)));
                if (d >= nearest) continue;
                nearest = d;
                current = r;
                direction = below ? -1 : 1; // sweep away from the open side
            }

            // Get to the first run's nearer end (by path).
            bool startAtS0 = PathTo(PointOn(current.lane, current.s0)) <= PathTo(PointOn(current.lane, current.s1));
            float sNow = startAtS0 ? current.s0 : current.s1;
            int sign = startAtS0 ? 1 : -1;
            plan.Add(new Step { point = PointOn(current.lane, sNow), skipKey = RunKey(current) });

            float radius = spacing * 0.5f;
            int arcPoints = Math.Max(1, Math.Min(MaxArcPoints, (int)(Math.PI * radius / ArcPointSpacingMeters) - 1));
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
                    for (int i = 1; i <= arcPoints; i++)
                    {
                        double theta = Math.PI * i / (arcPoints + 1);
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

        float PathTo(Vec2 point) => TryCell(point, out int row, out int col) ? analysis.ReachDistance(row, col) : float.PositiveInfinity;

        /// <summary>For the log: the path distance to the point, or to the nearest reachable cell within 4 cells of it (edge points lie closer to the wall than the planner's clearance).</summary>
        float PathNear(Vec2 point)
        {
            if (!TryCell(point, out int row, out int col)) return float.PositiveInfinity;
            float best = float.PositiveInfinity;
            for (int dr = -4; dr <= 4; dr++)
                for (int dc = -4; dc <= 4; dc++)
                    best = Math.Min(best, analysis.ReachDistance(row + dr, col + dc));
            return best;
        }

        /// <summary>Is there a run in the lane on that side (<paramref name="side"/> ±1) overlapping this one along the lanes?</summary>
        bool HasNeighbour(List<Run> list, Run run, int side)
        {
            foreach (var r in list)
            {
                if (r.lane != run.lane + side) continue;
                if (Math.Min(r.s1, run.s1) - Math.Max(r.s0, run.s0) >= spacing) return true;
            }
            return false;
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
                bool frontier = !analysis.IsExplored(row + 1, col) || !analysis.IsExplored(row - 1, col) ||
                                !analysis.IsExplored(row, col + 1) || !analysis.IsExplored(row, col - 1);
                if (!frontier) return;
                float d = analysis.ReachDistance(row, col);
                Vec2 p = CellCentre(row, col);
                if (d >= best || IsSkipped(map, PointKey(p))) return;
                best = d;
                target = p;
            });
            if (float.IsPositiveInfinity(best)) return false;
            plan.Add(new Step { point = target, skipKey = PointKey(target) });
            return true;
        }

        // ---- Helpers ----------------------------------------------------------------------------------------

        /// <summary>A cell's centre: the map's anchor (a cell corner) plus (col + ½, row + ½) cells.</summary>
        Vec2 CellCentre(int row, int col) =>
            laneAnchor + new Vec2((col + 0.5f) * analysis.CellSize, (row + 0.5f) * analysis.CellSize);

        bool TryCell(Vec2 point, out int row, out int col)
        {
            col = (int)Math.Floor((point.x - laneAnchor.x) / analysis.CellSize);
            row = (int)Math.Floor((point.y - laneAnchor.y) / analysis.CellSize);
            return analysis.Contains(row, col);
        }

        /// <summary>Every cell that belongs to a room, with its room.</summary>
        void ForCells(Action<int, int, int> visit)
        {
            for (int row = analysis.MinRow; row < analysis.MinRow + analysis.Rows; row++)
                for (int col = analysis.MinCol; col < analysis.MinCol + analysis.Cols; col++)
                {
                    int label = analysis.RoomOf(row, col);
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
