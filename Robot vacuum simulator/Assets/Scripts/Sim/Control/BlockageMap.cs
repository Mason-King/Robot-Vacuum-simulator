using System;
using System.Collections.Generic;
using UnityEngine;

namespace RobotVacuum.Sim.Control
{
    /// <summary>
    /// The map side of the boxed-in controller (<see cref="MovementBrain"/>'s waits for a blockage, such as the cat):
    /// the record of the floor the robot has reached this run, and the checks made against it. It only reads the map
    /// analysis; it never edits the map, so a blocked cell clears only when the lidar sees it clear. Plain C#, so the
    /// checks can be tested on small hand-built maps.
    ///
    /// Every check reads the analysis as it is: the caller updates it first.
    /// </summary>
    public sealed class BlockageMap
    {
        // Every floor cell the robot has been able to reach this run (sampled), over a rectangle of the map.
        bool[] everReached = Array.Empty<bool>();
        int everRow0, everCol0, everRows, everCols;

        /// <summary>The cells to look at from the last <see cref="BoxedIn"/> (cut off, or in the way): what <see cref="FindLookout"/> searches from.</summary>
        public readonly List<(int row, int col)> CutCells = new List<(int row, int col)>();

        /// <summary>True once something has been sampled (no check can find anything before).</summary>
        public bool HasRecord => everRows > 0;

        public void Clear()
        {
            everReached = Array.Empty<bool>();
            everRow0 = everCol0 = everRows = everCols = 0;
            CutCells.Clear();
        }

        public bool EverReached(int row, int col)
        {
            int r = row - everRow0, c = col - everCol0;
            return r >= 0 && r < everRows && c >= 0 && c < everCols && everReached[r * everCols + c];
        }

        static bool ReachableNow(MapAnalysis analysis, int row, int col) =>
            analysis.BodyFits(row, col) && !float.IsPositiveInfinity(analysis.ReachDistance(row, col));

        /// <summary>
        /// Marks the floor reachable in <paramref name="analysis"/> (no update of its own: one pass over the cells) as
        /// reached this run. MovementBrain calls it every BoxedInSampleSeconds with the algorithm's latest analysis.
        /// </summary>
        public void Sample(MapAnalysis analysis)
        {
            if (analysis == null || analysis.IsEmpty) return;

            // Grow the record to cover the analysis's rectangle (a SLAM map grows), keeping what it holds.
            int row0 = analysis.MinRow, col0 = analysis.MinCol, rows = analysis.Rows, cols = analysis.Cols;
            if (everRows == 0 || row0 < everRow0 || col0 < everCol0 ||
                row0 + rows > everRow0 + everRows || col0 + cols > everCol0 + everCols)
            {
                int newRow0 = everRows == 0 ? row0 : Math.Min(row0, everRow0);
                int newCol0 = everRows == 0 ? col0 : Math.Min(col0, everCol0);
                int newRowEnd = everRows == 0 ? row0 + rows : Math.Max(row0 + rows, everRow0 + everRows);
                int newColEnd = everRows == 0 ? col0 + cols : Math.Max(col0 + cols, everCol0 + everCols);
                int newRows = newRowEnd - newRow0, newCols = newColEnd - newCol0;
                var grown = new bool[newRows * newCols];
                for (int r = 0; r < everRows; r++)
                    Array.Copy(everReached, r * everCols, grown, (everRow0 + r - newRow0) * newCols + (everCol0 - newCol0), everCols);
                everReached = grown;
                everRow0 = newRow0;
                everCol0 = newCol0;
                everRows = newRows;
                everCols = newCols;
            }

            for (int row = row0; row < row0 + rows; row++)
            {
                int offset = (row - everRow0) * everCols - everCol0;
                for (int col = col0; col < col0 + cols; col++)
                    if (ReachableNow(analysis, row, col))
                        everReached[offset + col] = true;
            }
        }

        /// <summary>What the last <see cref="BoxedIn"/> found, for the log and the caller's messages.</summary>
        public struct Check
        {
            /// <summary>Cut-off floor still to clean; for a blockage, the cells in the way.</summary>
            public int CutOff;
            /// <summary>Unmapped cells bordering floor reachable only through the blockage.</summary>
            public int UnmappedEdge;
            /// <summary>Found by the blockage rules (something sitting in the way), not by cut-off floor.</summary>
            public bool ByBlockage;
            public int InWay, DirtyInWay;
            /// <summary>Cells blocked now that the robot reached earlier and that don't flicker: something moved in.</summary>
            public int Moved;
            /// <summary>Cells blocked now that the robot reached earlier but that flicker (a thin static obstacle).</summary>
            public int Flickering;
            public float MovedSpan;
            public int Hidden;
        }

        /// <summary>
        /// Is the robot boxed in? First: floor is cut off now: at least BoxedInCutOffCells cells of mapped floor the
        /// body fits on, still to clean, can't be reached from here but could be if everywhere the robot has reached
        /// this run were still open. That takes in floor the robot never got to (the far end of a hallway the cat sits
        /// in) as well as floor it did, while floor that only walls separate (outside the house, seen through a
        /// doorway) never counts: no path of reached cells leads to it. Whatever is in the way (the cat) is not itself
        /// counted: its cells and the clearance around them aren't floor the body fits on.
        ///
        /// Else: a blockage near <paramref name="focus"/> (see the rules at the end). <paramref name="focusIsFailure"/>
        /// says the focus is where the algorithm's last goal failed (or a blockage found before), not just the robot.
        /// <paramref name="flickers"/> (may be null) tells which cells keep flipping between blocked and clear.
        ///
        /// Leaves the cells to look at in <see cref="CutCells"/>.
        /// </summary>
        public bool BoxedIn(MapAnalysis analysis, Func<int, int, Vector2> cellCenter, float dirtyBelow, Vector2 focus,
            bool focusIsFailure, Func<int, int, bool> flickers, out Check check)
        {
            check = default;
            CutCells.Clear();
            if (analysis == null || analysis.IsEmpty || everRows == 0) return false;
            int row0 = analysis.MinRow, col0 = analysis.MinCol, rows = analysis.Rows, cols = analysis.Cols;

            bool Dirty(int row, int col) => analysis.Cleanliness(row, col) < dirtyBelow;

            // Spread out from the floor reachable now, through anywhere reached before (even if blocked now) and any
            // mapped floor the body fits on. What it finds that can't be reached now is cut off.
            var seen = new bool[rows * cols];
            var queue = new Queue<int>();
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < cols; c++)
                    if (ReachableNow(analysis, row0 + r, col0 + c))
                    {
                        seen[r * cols + c] = true;
                        queue.Enqueue(r * cols + c);
                    }
            int cutOff = 0, unmappedEdge = 0;
            while (queue.Count > 0)
            {
                int index = queue.Dequeue();
                int r = index / cols, c = index % cols;
                bool cutSide = !ReachableNow(analysis, row0 + r, col0 + c); // reached only through the blockage
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
                        CutCells.Add((row0 + r, col0 + c));
                        continue;
                    }
                    bool floor = analysis.IsExplored(row, col) && analysis.BodyFits(row, col);
                    if (!floor && !EverReached(row, col)) continue;
                    seen[nr * cols + nc] = true;
                    queue.Enqueue(nr * cols + nc);
                    if (floor) // can't be reachable now: every reachable cell was a starting cell
                    {
                        CutCells.Add((row, col));
                        if (Dirty(row, col)) cutOff++; // only floor still to clean counts
                    }
                }
            }
            check.CutOff = cutOff;
            check.UnmappedEdge = unmappedEdge;
            // Unmapped edge alone is not enough: in SLAM the map's edges are redrawn as the pose is corrected, which leaves
            // unmapped seams next to floor reached earlier with nothing cut off at all (a false alarm cost a 3-minute
            // hold). It only adds to floor that is really cut off and still to clean.
            if (cutOff >= ControllerConstants.BoxedInCutOffCells ||
                cutOff > 0 && unmappedEdge >= ControllerConstants.BoxedInUnmappedEdgeCells)
                return true;

            // Nothing measurable cut off (what lies behind may never have been mapped: a cat in a narrow hallway hides
            // it from the lidar, or it sits on the floor that is left). Look at the blockage itself instead, near the
            // focus:
            //  - in the way: cells reachable earlier that the body doesn't fit on now; those still to clean matter;
            //  - moved: cells mapped blocked now that the robot could reach earlier (walls and furniture never were):
            //    something that moves, such as the cat, is sitting there. Cells that keep flickering between blocked
            //    and clear are counted apart: a thin static obstacle (a chair's legs), not something that moved in.
            float radius = ControllerConstants.BoxedInBlockageRadiusMeters;
            float hideRadius = ControllerConstants.BoxedInHiddenRadiusMeters;
            int inWay = 0, dirtyInWay = 0, flickering = 0;
            var inWayCells = new List<(int row, int col)>();
            var moved = new List<Vector2>();
            var unknown = new List<Vector2>();
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < cols; c++)
                {
                    int row = row0 + r, col = col0 + c;
                    Vector2 at = cellCenter(row, col);
                    float d2 = (at - focus).sqrMagnitude;
                    if (d2 > (radius + hideRadius) * (radius + hideRadius)) continue;
                    if (!analysis.IsExplored(row, col)) { unknown.Add(at); continue; }
                    if (d2 > radius * radius || !EverReached(row, col) || analysis.BodyFits(row, col)) continue;
                    inWay++;
                    if (Dirty(row, col)) dirtyInWay++;
                    inWayCells.Add((row, col));
                    if (!analysis.IsBlocked(row, col)) continue;
                    if (flickers != null && flickers(row, col)) flickering++;
                    else moved.Add(at);
                }
            float movedSpan = Span(moved);
            int hidden = 0;
            foreach (var u in unknown)
                foreach (var m in moved)
                    if ((u - m).sqrMagnitude <= hideRadius * hideRadius) { hidden++; break; }

            check.InWay = inWay;
            check.DirtyInWay = dirtyInWay;
            check.Moved = moved.Count;
            check.Flickering = flickering;
            check.MovedSpan = movedSpan;
            check.Hidden = hidden;

            // A blockage: something sitting on floor the robot could reach before that still needs cleaning (the cat at
            // the end of a hallway, on the floor that is left). A cat napping on floor already cleaned doesn't count.
            // The moved cells must also be compact, cat-sized: in SLAM a correction of the pose redraws a stretch of
            // wall a few centimeters over, which also turns floor reached earlier into blocked cells, but in a long
            // thin line (a false alarm near the end of a run, when the drift is largest, cost a 3-minute hold).
            // Where flickering cells are among the blocked ones, the in-way rules also need real moved cells: the cells
            // in the way may be nothing but the clearance around a chair's legs.
            bool compact = movedSpan <= ControllerConstants.BoxedInMovedMaxSpanMeters;
            bool movedEvidence = flickering == 0 || moved.Count >= ControllerConstants.BoxedInMovedMinCells;
            bool blockage = compact && movedEvidence && dirtyInWay >= ControllerConstants.BoxedInDirtyInWayCells
                            || focusIsFailure && movedEvidence && inWay >= ControllerConstants.BoxedInBlockageCells && cutOff > 0
                            || compact && moved.Count >= ControllerConstants.BoxedInMovedCells && hidden >= ControllerConstants.BoxedInHiddenCells;
            if (!blockage) return false;
            check.ByBlockage = true;
            check.CutOff = inWay;
            CutCells.Clear();
            CutCells.AddRange(inWayCells);
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
        /// Cells blocked now that the robot reached earlier and that don't flicker, within <paramref name="radius"/> of
        /// <paramref name="a"/> or <paramref name="b"/>: something that moved in there (the cat), as opposed to a way
        /// home that was never open (a closed door, a level without a gap).
        /// </summary>
        public int MovedNear(MapAnalysis analysis, Func<int, int, Vector2> cellCenter, Vector2 a, Vector2? b, float radius,
            Func<int, int, bool> flickers)
        {
            if (analysis == null || analysis.IsEmpty || everRows == 0) return 0;
            float r2 = radius * radius;
            int count = 0;
            for (int row = analysis.MinRow; row < analysis.MinRow + analysis.Rows; row++)
                for (int col = analysis.MinCol; col < analysis.MinCol + analysis.Cols; col++)
                {
                    if (!analysis.IsBlocked(row, col) || !EverReached(row, col)) continue;
                    Vector2 at = cellCenter(row, col);
                    if ((at - a).sqrMagnitude > r2 && (!b.HasValue || (at - b.Value).sqrMagnitude > r2)) continue;
                    if (flickers != null && flickers(row, col)) continue;
                    count++;
                }
            return count;
        }

        /// <summary>
        /// The reachable floor nearest the cut-off floor: a search spreading out from every cell in <see cref="CutCells"/>
        /// across the map (through the blockage) to the first cell the robot can reach.
        /// </summary>
        public Vector2? FindLookout(MapAnalysis analysis, Func<int, int, Vector2> cellCenter)
        {
            if (analysis == null || analysis.IsEmpty || CutCells.Count == 0) return null;
            int row0 = analysis.MinRow, col0 = analysis.MinCol, rows = analysis.Rows, cols = analysis.Cols;
            var seen = new bool[rows * cols];
            var queue = new Queue<int>();
            foreach (var (row, col) in CutCells)
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
                    if (ReachableNow(analysis, row, col)) return cellCenter(row, col);
                    queue.Enqueue(nr * cols + nc);
                }
            }
            return null;
        }

        /// <summary>
        /// On the way home: the reachable floor nearest the dock along the way. A search spreads out from the dock
        /// through any mapped free cell or place reached earlier (a path a point could take, through the blockage) to
        /// the first cell the robot can reach: next to whatever blocks the way. Null when no such path exists.
        /// </summary>
        public Vector2? FindHomeLookout(MapAnalysis analysis, Func<int, int, Vector2> cellCenter, int dockRow, int dockCol)
        {
            if (analysis == null || analysis.IsEmpty) return null;
            int row0 = analysis.MinRow, col0 = analysis.MinCol, rows = analysis.Rows, cols = analysis.Cols;
            int dr = dockRow - row0, dc = dockCol - col0;
            if (dr < 0 || dr >= rows || dc < 0 || dc >= cols) return null;
            var seen = new bool[rows * cols];
            var queue = new Queue<int>();
            seen[dr * cols + dc] = true;
            queue.Enqueue(dr * cols + dc);
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
                    if (ReachableNow(analysis, row, col)) return cellCenter(row, col);
                    bool free = analysis.IsExplored(row, col) && !analysis.IsBlocked(row, col);
                    if (free || EverReached(row, col)) queue.Enqueue(nr * cols + nc);
                }
            }
            return null;
        }

        /// <summary>The dock can be reached: some cell within <paramref name="reachCells"/> of it that the body fits on is reachable now.</summary>
        public static bool DockReachable(MapAnalysis analysis, int dockRow, int dockCol, int reachCells)
        {
            if (analysis == null || analysis.IsEmpty) return false;
            for (int row = dockRow - reachCells; row <= dockRow + reachCells; row++)
                for (int col = dockCol - reachCells; col <= dockCol + reachCells; col++)
                    if (analysis.Contains(row, col) && ReachableNow(analysis, row, col))
                        return true;
            return false;
        }
    }
}
