using System.Collections.Generic;
using RobotVacuum.AlgorithmApi;
using UnityEngine;

namespace RobotVacuum.Sim.Control
{
    /// <summary>
    /// The controller's blockage detection: is the robot boxed in or cut off by something that moved in (the cat), and
    /// where should it look, and is the way open again. The responses (the hold, the lookout drive, giving up) stay in
    /// <see cref="MovementBrain"/>. Designs: claude/Boxed_In_Controller_Design.md, claude/Boxed_In_Improvements_Design.md.
    ///
    ///   * The reach record (<see cref="Sample"/>, every BoxedInSampleSeconds): every cell the robot could reach this run,
    ///     and when each cell was last seen free. A cell blocked now that was free shortly before the blockage began is
    ///     RECENTLY blocked (a cat); one blocked for longer is treated as a wall (furniture the map learned late, such
    ///     as chair legs the Normal preload marks as floor). A cell that keeps flipping between blocked and clear
    ///     (<see cref="InternalModelView.Flickers"/>: chair legs the lidar half sees) is also treated as a wall: it is
    ///     always "recently" free, but nothing moved in.
    ///   * <see cref="BoxedIn"/>: cut-off floor or a blockage. It also keeps the cells to look at (for the lookout) and
    ///     the cells to watch (the blocker's footprint) while the robot holds.
    ///   * <see cref="WatchedCellCleared"/>: a cheap per-step read of the watched cells on the live map, so the hold can
    ///     run the full check only when something there changed.
    ///
    /// Plain C# (no Unity component), so it can be tested on hand-built maps. Buffers are kept between checks.
    /// </summary>
    public sealed class BlockageCheck
    {
        readonly InternalModelView view;
        readonly IBeliefMap map;
        MapAnalysis fallback;

        // The reach record, over a rectangle of the map that grows with it (a SLAM map grows).
        bool[] everReached = System.Array.Empty<bool>();
        float[] lastFree = System.Array.Empty<float>();
        int recRow0, recCol0, recRows, recCols;

        // From the last BoxedIn: what to drive to and look at.
        readonly List<(int row, int col)> lookAt = new List<(int row, int col)>();
        // The algorithm's last goal that ended Blocked or Unreachable, and when; and, once a blockage next to it has
        // been found, that goal kept as the place to watch until the algorithm finds work again.
        Vector2 lastFailedGoal;
        float lastFailedTime = float.NegativeInfinity;
        Vector2? focus;

        // What the last BoxedIn that found something was about (the cut-off floor, or the cells in the way), and how few
        // of them may stay out of reach for the way to count as open again (the trigger's own threshold).
        readonly List<(int row, int col)> heldCells = new List<(int row, int col)>();
        readonly HashSet<(int row, int col)> heldSet = new HashSet<(int row, int col)>();
        int heldThreshold;

        // The blocker's footprint while holding, with each cell's blocked state when it was last checked.
        readonly List<(int row, int col)> watched = new List<(int row, int col)>();
        readonly List<bool> watchedBlocked = new List<bool>();

        // Reused between checks (no garbage per check).
        bool[] seen = System.Array.Empty<bool>();
        readonly Queue<int> queue = new Queue<int>();
        readonly List<(int row, int col)> inWayCells = new List<(int row, int col)>();
        readonly List<(int row, int col)> gateCells = new List<(int row, int col)>();
        readonly List<(int row, int col)> recentMovedCells = new List<(int row, int col)>();
        readonly List<Vector2> moved = new List<Vector2>();
        readonly List<Vector2> recentMoved = new List<Vector2>();
        readonly List<Vector2> unknown = new List<Vector2>();

        public BlockageCheck(InternalModelView view, IBeliefMap map)
        {
            this.view = view;
            this.map = map;
        }

        // ---- Read-outs of the last check (log lines, the result) -----------------------------------------------------

        /// <summary>The last <see cref="BoxedIn"/> found a blockage (something sitting in the way), not cut-off floor.</summary>
        public bool LastByBlockage { get; private set; }

        /// <summary>The last <see cref="BoxedIn"/>: unmapped cells bordering the cut-off side.</summary>
        public int LastUnmappedEdge { get; private set; }

        /// <summary>The last <see cref="BoxedIn"/>'s numbers, for the "[Blockage check]" log line (no time prefix).</summary>
        public string LastLine { get; private set; }

        /// <summary>The cells the hold watches (the blocker's footprint), for tests and diagnostics.</summary>
        public IReadOnlyList<(int row, int col)> Watched => watched;

        /// <summary>A blockage has been found next to a failed goal and is being watched until the algorithm finds work again.</summary>
        public bool HasFocus => focus.HasValue;

        /// <summary>
        /// The map analysis to use: the algorithm's own when it has one (kept current by the algorithm), else one of
        /// this check's own over the same map.
        /// </summary>
        public MapAnalysis AnalysisOr(MapAnalysis algorithms) => algorithms ?? (fallback ??= new MapAnalysis(map));

        // ---- Run state ---------------------------------------------------------------------------------------------

        public void Reset()
        {
            everReached = System.Array.Empty<bool>();
            lastFree = System.Array.Empty<float>();
            recRow0 = recCol0 = recRows = recCols = 0;
            lookAt.Clear();
            lastFailedTime = float.NegativeInfinity;
            focus = null;
            watched.Clear();
            watchedBlocked.Clear();
            heldCells.Clear();
            heldThreshold = 0;
            LastByBlockage = false;
            LastUnmappedEdge = 0;
            LastLine = null;
        }

        /// <summary>The algorithm's goal ended Blocked or Unreachable here, at this time.</summary>
        public void NoteFailedGoal(Vector2 goal, float now)
        {
            lastFailedGoal = goal;
            lastFailedTime = now;
        }

        /// <summary>The algorithm found work again: a later blockage is looked for afresh.</summary>
        public void ClearFocus() => focus = null;

        // ---- The reach record ----------------------------------------------------------------------------------------

        /// <summary>
        /// Every BoxedInSampleSeconds: marks the floor reachable in <paramref name="analysis"/> (no update of its own:
        /// one pass over the cells) as reached this run, and stamps every explored cell that isn't blocked as free now.
        /// </summary>
        public void Sample(MapAnalysis analysis, float now)
        {
            if (analysis == null || analysis.IsEmpty) return;
            int row0 = analysis.MinRow, col0 = analysis.MinCol, rows = analysis.Rows, cols = analysis.Cols;
            Grow(row0, col0, rows, cols);

            for (int row = row0; row < row0 + rows; row++)
            {
                int offset = (row - recRow0) * recCols - recCol0;
                for (int col = col0; col < col0 + cols; col++)
                {
                    if (analysis.BodyFits(row, col) && !float.IsPositiveInfinity(analysis.ReachDistance(row, col)))
                        everReached[offset + col] = true;
                    if (analysis.IsExplored(row, col) && !analysis.IsBlocked(row, col))
                        lastFree[offset + col] = now;
                }
            }
        }

        /// <summary>Grows the record to cover this rectangle, keeping what it holds.</summary>
        void Grow(int row0, int col0, int rows, int cols)
        {
            if (recRows != 0 && row0 >= recRow0 && col0 >= recCol0 &&
                row0 + rows <= recRow0 + recRows && col0 + cols <= recCol0 + recCols)
                return;

            int newRow0 = recRows == 0 ? row0 : System.Math.Min(row0, recRow0);
            int newCol0 = recRows == 0 ? col0 : System.Math.Min(col0, recCol0);
            int newRowEnd = recRows == 0 ? row0 + rows : System.Math.Max(row0 + rows, recRow0 + recRows);
            int newColEnd = recRows == 0 ? col0 + cols : System.Math.Max(col0 + cols, recCol0 + recCols);
            int newRows = newRowEnd - newRow0, newCols = newColEnd - newCol0;

            var grownReached = new bool[newRows * newCols];
            var grownFree = new float[newRows * newCols];
            for (int i = 0; i < grownFree.Length; i++) grownFree[i] = float.NegativeInfinity; // never seen free
            for (int r = 0; r < recRows; r++)
            {
                int to = (recRow0 + r - newRow0) * newCols + (recCol0 - newCol0);
                System.Array.Copy(everReached, r * recCols, grownReached, to, recCols);
                System.Array.Copy(lastFree, r * recCols, grownFree, to, recCols);
            }
            everReached = grownReached;
            lastFree = grownFree;
            recRow0 = newRow0;
            recCol0 = newCol0;
            recRows = newRows;
            recCols = newCols;
        }

        bool EverReached(int row, int col)
        {
            int r = row - recRow0, c = col - recCol0;
            return r >= 0 && r < recRows && c >= 0 && c < recCols && everReached[r * recCols + c];
        }

        /// <summary>
        /// Free at some sample since <paramref name="since"/>: blocked (if it is) only recently. A cell never seen free
        /// was never floor the robot knew.
        /// </summary>
        bool FreeSince(int row, int col, float since)
        {
            int r = row - recRow0, c = col - recCol0;
            return r >= 0 && r < recRows && c >= 0 && c < recCols && lastFree[r * recCols + c] >= since;
        }

        /// <summary>
        /// The time a cell must have been free at or after to count as recently blocked: BoxedInRecentBlockSeconds
        /// before the episode began (or before now, when no episode has begun), so a hold in progress doesn't age the
        /// blocker out.
        /// </summary>
        public static float RecentSince(float now, float episodeStart) =>
            (episodeStart >= 0f ? episodeStart : now) - ControllerConstants.BoxedInRecentBlockSeconds;

        // ---- Boxed in / cut off ------------------------------------------------------------------------------------

        /// <summary>
        /// True when floor is cut off, or something recently moved in is blocking floor still to clean.
        ///
        /// Cut off: at least BoxedInCutOffCells cells of mapped floor the body fits on, still to clean, can't be
        /// reached from here, but could be if the places the robot reached earlier and that are blocked only recently
        /// were open; at least one such recently blocked (and not flickering) cell must lie on the way. Floor that only walls separate (outside the house), or that something blocked long ago separates
        /// (furniture), never counts. Unmapped cells bordering the cut-off side add to floor that is really cut off.
        ///
        /// Blockage (nothing measurable cut off): near the algorithm's last failed goal if that was just before, else
        /// near the robot, cells reached earlier that the body doesn't fit on now ("in the way"), and among them the
        /// blocked ones ("moved"). Only moved cells that are recently blocked and don't flicker count as something that
        /// moved in; at least one is needed, and the compact-span and hidden-cell tests use only those.
        ///
        /// Leaves the cells to look at for <see cref="FindLookout"/> and the cells to watch for
        /// <see cref="WatchedCellCleared"/>.
        /// </summary>
        /// <param name="dirtyBelow">Floor counts as still to clean below this cleanliness (the robot's belief).</param>
        /// <param name="episodeStart">When the current blockage episode began, or negative if none has.</param>
        public bool BoxedIn(MapAnalysis analysis, Vector2 robot, float now, float episodeStart, float dirtyBelow, out int cutOff)
        {
            analysis.Update(robot.ToApi());
            cutOff = 0;
            lookAt.Clear();
            gateCells.Clear();
            LastByBlockage = false;
            LastUnmappedEdge = 0;
            LastLine = null;
            if (analysis.IsEmpty || recRows == 0) return false;
            int row0 = analysis.MinRow, col0 = analysis.MinCol, rows = analysis.Rows, cols = analysis.Cols;
            float since = RecentSince(now, episodeStart);

            bool Dirty(int row, int col) => analysis.Cleanliness(row, col) < dirtyBelow;
            bool Reachable(int row, int col) =>
                analysis.BodyFits(row, col) && !float.IsPositiveInfinity(analysis.ReachDistance(row, col));

            // Spread out from the floor reachable now, through mapped floor the body fits on and through places reached
            // before that are open, or blocked only recently. What it finds that can't be reached now is cut off.
            ClearSeen(rows * cols);
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < cols; c++)
                    if (Reachable(row0 + r, col0 + c))
                    {
                        seen[r * cols + c] = true;
                        queue.Enqueue(r * cols + c);
                    }
            int unmappedEdge = 0;
            while (queue.Count > 0)
            {
                int index = queue.Dequeue();
                int r = index / cols, c = index % cols;
                bool cutSide = !Reachable(row0 + r, col0 + c); // reached only through the blockage
                for (int k = 0; k < 4; k++)
                {
                    int nr = r + (k == 0 ? 1 : k == 1 ? -1 : 0), nc = c + (k == 2 ? 1 : k == 3 ? -1 : 0);
                    if (nr < 0 || nr >= rows || nc < 0 || nc >= cols || seen[nr * cols + nc]) continue;
                    int row = row0 + nr, col = col0 + nc;
                    // Unmapped space bordering the cut-off side: what lies past the blockage that the robot never saw
                    // (a room behind a doorway the cat sits in). Counted, not entered.
                    if (cutSide && !analysis.IsExplored(row, col))
                    {
                        seen[nr * cols + nc] = true;
                        unmappedEdge++;
                        lookAt.Add((row0 + r, col0 + c));
                        continue;
                    }
                    bool floor = analysis.IsExplored(row, col) && analysis.BodyFits(row, col);
                    if (!floor)
                    {
                        if (!EverReached(row, col)) continue;
                        bool blocked = analysis.IsBlocked(row, col);
                        // Blocked long ago, or flickering (a thin static obstacle): a wall to this check.
                        if (blocked && (view.Flickers(row, col) || !FreeSince(row, col, since))) continue;
                        if (blocked && gateCells.Count < ControllerConstants.BoxedInWatchedCellsMax) gateCells.Add((row, col));
                    }
                    seen[nr * cols + nc] = true;
                    queue.Enqueue(nr * cols + nc);
                    if (floor) // can't be reachable now: every reachable cell was a starting cell
                    {
                        lookAt.Add((row, col));
                        if (Dirty(row, col)) cutOff++; // only floor still to clean counts
                    }
                }
            }
            LastUnmappedEdge = unmappedEdge;
            // Unmapped edge alone is not enough: in SLAM the map's edges are redrawn as the pose is corrected, which leaves
            // unmapped seams next to floor reached earlier with nothing cut off at all. It only adds to floor that is
            // really cut off and still to clean. And something must have moved in: the flood has to have crossed at
            // least one recently blocked cell that doesn't flicker. Floor cut off only because the body no longer fits
            // past static furniture the map learned late (the clearance around chair legs) is not waited for.
            if (gateCells.Count > 0 &&
                (cutOff >= ControllerConstants.BoxedInCutOffCells ||
                 cutOff > 0 && unmappedEdge >= ControllerConstants.BoxedInUnmappedEdgeCells))
            {
                LastLine = $"cut-off floor {cutOff}, unmapped edge past it {unmappedEdge}, recently blocked in the way {gateCells.Count}.";
                Watch(gateCells);
                Hold(lookAt, ControllerConstants.BoxedInCutOffCells, analysis, floorOnly: true); // the cut-off floor itself
                return true;
            }

            // Nothing measurable cut off (what lies behind may never have been mapped: a cat in a narrow hallway hides
            // it from the lidar, or it sits on the floor that is left). Look at the blockage itself instead, near the
            // algorithm's last failed goal if that was just before the finish, else near the robot.
            bool fromFailure = false;
            Vector2? at0 = focus;
            if (!at0.HasValue)
            {
                fromFailure = now - lastFailedTime <= ControllerConstants.BoxedInRecentFailSeconds;
                at0 = fromFailure ? lastFailedGoal : robot;
            }
            Vector2 at = at0.Value;
            float radius = ControllerConstants.BoxedInBlockageRadiusMeters;
            float hideRadius = ControllerConstants.BoxedInHiddenRadiusMeters;
            int inWay = 0, dirtyInWay = 0, flickering = 0;
            inWayCells.Clear();
            recentMovedCells.Clear();
            moved.Clear();
            recentMoved.Clear();
            unknown.Clear();
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < cols; c++)
                {
                    int row = row0 + r, col = col0 + c;
                    Vector2 cell = view.CellCenter(row, col);
                    float d2 = (cell - at).sqrMagnitude;
                    if (d2 > (radius + hideRadius) * (radius + hideRadius)) continue;
                    if (!analysis.IsExplored(row, col)) { unknown.Add(cell); continue; }
                    if (d2 > radius * radius || !EverReached(row, col) || analysis.BodyFits(row, col)) continue;
                    inWay++;
                    if (Dirty(row, col)) dirtyInWay++;
                    inWayCells.Add((row, col));
                    if (!analysis.IsBlocked(row, col)) continue;
                    moved.Add(cell);
                    if (view.Flickers(row, col)) { flickering++; continue; }
                    if (!FreeSince(row, col, since)) continue;
                    recentMoved.Add(cell);
                    recentMovedCells.Add((row, col));
                }
            float span = Span(recentMoved);
            int hidden = 0;
            foreach (var u in unknown)
                foreach (var m in recentMoved)
                    if ((u - m).sqrMagnitude <= hideRadius * hideRadius) { hidden++; break; }

            LastLine = $"near {(focus.HasValue ? "the blockage" : fromFailure ? "the failed goal" : "the robot")} ({at.x:F2}, {at.y:F2}): " +
                       $"cut-off floor {cutOff}, unmapped edge past it {unmappedEdge}, in the way {inWay} ({dirtyInWay} still to clean), " +
                       $"moved {moved.Count} (recently blocked {recentMoved.Count}, flickering {flickering}, span {span:F2} m), unmapped next to them {hidden}.";

            // A blockage: something that moved in recently, sitting on floor the robot could reach before that still
            // needs cleaning (the cat at the end of a hallway, on the floor that is left). A cat napping on floor already
            // cleaned doesn't count, nor does furniture the map learned long ago. The moved cells must also be compact,
            // cat-sized: in SLAM a correction of the pose redraws a stretch of wall a few centimeters over, in a long
            // thin line.
            bool compact = span <= ControllerConstants.BoxedInMovedMaxSpanMeters;
            // Next to flickering cells (chair legs), a cell or two not yet flagged is more likely the same furniture than
            // a cat, so it takes BoxedInMovedMinCells; otherwise one recently blocked cell is enough.
            int minMoved = flickering > 0 ? ControllerConstants.BoxedInMovedMinCells : 1;
            bool blockage = recentMoved.Count >= minMoved &&
                            (compact && dirtyInWay >= ControllerConstants.BoxedInDirtyInWayCells
                             || (focus.HasValue || fromFailure) && inWay >= ControllerConstants.BoxedInBlockageCells && cutOff > 0
                             || compact && recentMoved.Count >= ControllerConstants.BoxedInMovedCells && hidden >= ControllerConstants.BoxedInHiddenCells);
            if (!blockage) return false;
            focus = at;
            LastByBlockage = true;
            cutOff = inWay;
            lookAt.Clear();
            lookAt.AddRange(inWayCells);
            Watch(recentMovedCells);
            Hold(inWayCells, ControllerConstants.BoxedInDirtyInWayCells, analysis, floorOnly: false); // the body doesn't fit there now
            return true;
        }

        static float Span(List<Vector2> points)
        {
            if (points.Count == 0) return 0f;
            float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
            foreach (var p in points)
            {
                minX = Mathf.Min(minX, p.x); maxX = Mathf.Max(maxX, p.x);
                minY = Mathf.Min(minY, p.y); maxY = Mathf.Max(maxY, p.y);
            }
            return Mathf.Max(maxX - minX, maxY - minY);
        }

        /// <summary>
        /// While holding at the finish: has the floor the hold is about become reachable again? True when fewer than
        /// the trigger's own threshold (or half, for a small set) of the cells the last positive <see cref="BoxedIn"/>
        /// found (the cut-off floor, or the cells in the way) are still out of the body's reach. This, not "<see cref="BoxedIn"/> no longer fires",
        /// is what "open" means: when the blocker shifts, the recently blocked cells can drop below the trigger while
        /// cells it sat on earlier still block the way. Null when there is nothing remembered (use the full check).
        /// Updates <paramref name="analysis"/> first.
        /// </summary>
        public bool? HeldFloorReachable(MapAnalysis analysis, Vector2 robot)
        {
            if (heldCells.Count == 0) return null;
            analysis.Update(robot.ToApi());
            if (analysis.IsEmpty) return false;
            int stillOut = 0;
            foreach (var (row, col) in heldCells)
                if (!analysis.Contains(row, col) || !analysis.BodyFits(row, col) ||
                    float.IsPositiveInfinity(analysis.ReachDistance(row, col)))
                    stillOut++;
            // A small held set (the trigger fired on the unmapped edge, or on hidden floor) would count as open at once
            // against the full threshold: then at least half of it has to be reachable.
            return stillOut < Mathf.Min(heldThreshold, (heldCells.Count + 1) / 2);
        }

        void Hold(List<(int row, int col)> cells, int threshold, MapAnalysis analysis, bool floorOnly)
        {
            heldCells.Clear();
            heldSet.Clear();
            foreach (var cell in cells)
                if ((!floorOnly || analysis.BodyFits(cell.row, cell.col)) && heldSet.Add(cell)) heldCells.Add(cell);
            heldThreshold = threshold;
        }

        /// <summary>The cells the hold is about (for tests and diagnostics).</summary>
        public int HeldCellCount => heldCells.Count;

        // ---- Way home ----------------------------------------------------------------------------------------------

        /// <summary>
        /// The dock can be reached: any cell within <paramref name="robotRadius"/> of it that the body fits on and the
        /// robot can reach. Updates <paramref name="analysis"/> first.
        /// </summary>
        public bool DockReachable(MapAnalysis analysis, Vector2 robot, Vector2 dock, float robotRadius)
        {
            analysis.Update(robot.ToApi());
            if (analysis.IsEmpty || !view.TryWorldToCell(dock, out var cell)) return false;
            int reachCells = Mathf.CeilToInt(robotRadius / analysis.CellSize);
            for (int row = cell.row - reachCells; row <= cell.row + reachCells; row++)
                for (int col = cell.col - reachCells; col <= cell.col + reachCells; col++)
                    if (analysis.Contains(row, col) && analysis.BodyFits(row, col) &&
                        !float.IsPositiveInfinity(analysis.ReachDistance(row, col)))
                        return true;
            return false;
        }

        /// <summary>
        /// On the way home: the reachable floor nearest the dock along the way. A search spreads out from the dock
        /// through any mapped free cell or place reached earlier (a path a point could take, through the blockage) to
        /// the first cell the robot can reach: next to whatever blocks the way. The blocked cells it passes through
        /// (the blocker on the way, or on the dock) become the watched cells. Uses the analysis as last updated.
        /// </summary>
        public Vector2? FindHomeLookout(MapAnalysis analysis, Vector2 dock)
        {
            gateCells.Clear();
            Vector2? found = null;
            if (analysis != null && !analysis.IsEmpty && view.TryWorldToCell(dock, out var start))
            {
                int row0 = analysis.MinRow, col0 = analysis.MinCol, rows = analysis.Rows, cols = analysis.Cols;
                int dr = start.row - row0, dc = start.col - col0;
                if (dr >= 0 && dr < rows && dc >= 0 && dc < cols)
                {
                    ClearSeen(rows * cols);
                    seen[dr * cols + dc] = true;
                    queue.Enqueue(dr * cols + dc);
                    if (analysis.IsBlocked(start.row, start.col)) gateCells.Add((start.row, start.col)); // something on the dock
                    while (queue.Count > 0 && !found.HasValue)
                    {
                        int index = queue.Dequeue();
                        int r = index / cols, c = index % cols;
                        for (int k = 0; k < 4; k++)
                        {
                            int nr = r + (k == 0 ? 1 : k == 1 ? -1 : 0), nc = c + (k == 2 ? 1 : k == 3 ? -1 : 0);
                            if (nr < 0 || nr >= rows || nc < 0 || nc >= cols || seen[nr * cols + nc]) continue;
                            seen[nr * cols + nc] = true;
                            int row = row0 + nr, col = col0 + nc;
                            if (analysis.BodyFits(row, col) && !float.IsPositiveInfinity(analysis.ReachDistance(row, col)))
                            {
                                found = view.CellCenter(row, col);
                                break;
                            }
                            bool free = analysis.IsExplored(row, col) && !analysis.IsBlocked(row, col);
                            if (!free && !EverReached(row, col)) continue;
                            // Flickering cells (chair legs) are not watched: they would clear and trigger the full check every few seconds.
                            if (!free && !view.Flickers(row, col) && gateCells.Count < ControllerConstants.BoxedInWatchedCellsMax) gateCells.Add((row, col));
                            queue.Enqueue(nr * cols + nc);
                        }
                    }
                    queue.Clear();
                }
            }
            Watch(gateCells);
            return found;
        }

        /// <summary>
        /// The reachable floor nearest what the last <see cref="BoxedIn"/> found (cut-off floor, or the cells in the
        /// way): a search spreading out from those cells across the map (through the blockage) to the first cell the
        /// robot can reach. Uses the analysis that check just updated.
        /// </summary>
        public Vector2? FindLookout(MapAnalysis analysis)
        {
            if (analysis == null || analysis.IsEmpty || lookAt.Count == 0) return null;
            int row0 = analysis.MinRow, col0 = analysis.MinCol, rows = analysis.Rows, cols = analysis.Cols;
            ClearSeen(rows * cols);
            foreach (var (row, col) in lookAt)
            {
                int r = row - row0, c = col - col0;
                if (r < 0 || r >= rows || c < 0 || c >= cols || seen[r * cols + c]) continue;
                seen[r * cols + c] = true;
                queue.Enqueue(r * cols + c);
            }
            while (queue.Count > 0)
            {
                int index = queue.Dequeue();
                int r = index / cols, c = index % cols;
                for (int k = 0; k < 4; k++)
                {
                    int nr = r + (k == 0 ? 1 : k == 1 ? -1 : 0), nc = c + (k == 2 ? 1 : k == 3 ? -1 : 0);
                    if (nr < 0 || nr >= rows || nc < 0 || nc >= cols || seen[nr * cols + nc]) continue;
                    seen[nr * cols + nc] = true;
                    int row = row0 + nr, col = col0 + nc;
                    if (analysis.BodyFits(row, col) && !float.IsPositiveInfinity(analysis.ReachDistance(row, col)))
                    {
                        queue.Clear();
                        return view.CellCenter(row, col);
                    }
                    queue.Enqueue(nr * cols + nc);
                }
            }
            return null;
        }

        // ---- Watching the blocker while holding --------------------------------------------------------------------

        void Watch(List<(int row, int col)> cells)
        {
            watched.Clear();
            watchedBlocked.Clear();
            foreach (var cell in cells)
            {
                watched.Add(cell);
                watchedBlocked.Add(view.IsBlocked(cell.row, cell.col));
            }
        }

        /// <summary>
        /// True when a watched cell that was blocked at the last check reads clear on the live map now (a few dozen
        /// single-cell reads; no analysis update). The caller then runs the full check, and calls
        /// <see cref="RefreshWatched"/> if the way is still closed, so only a new change triggers the next one.
        /// </summary>
        public bool WatchedCellCleared()
        {
            for (int i = 0; i < watched.Count; i++)
                if (watchedBlocked[i] && !view.IsBlocked(watched[i].row, watched[i].col))
                    return true;
            return false;
        }

        /// <summary>Takes the watched cells' blocked states as they are now.</summary>
        public void RefreshWatched()
        {
            for (int i = 0; i < watched.Count; i++)
                watchedBlocked[i] = view.IsBlocked(watched[i].row, watched[i].col);
        }

        void ClearSeen(int count)
        {
            if (seen.Length < count) seen = new bool[count];
            else System.Array.Clear(seen, 0, count);
            queue.Clear();
        }
    }
}
