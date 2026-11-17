using System;
using System.Collections.Generic;
using RobotVacuum.AlgorithmApi;
using UnityEngine;
using K = RobotVacuum.Sim.Control.ControllerConstants;

namespace RobotVacuum.Sim.Control
{
    /// <summary>
    /// Plans a path for the robot's BODY across the believed map: A* over the map's cells, 8-connected, then
    /// shortened to a few straight legs.
    ///
    /// Clearance. Distances are measured to the EDGES of cells believed impassable (a mapped wall may fill its cell
    /// right up to the edge), so the clearance is a real gap. A cell is free for the body when its centre is more
    /// than the clearance (robot radius + <see cref="K.ClearanceMarginMeters"/>) from every such cell. Straight legs
    /// are checked exactly (segment against cell squares), so cutting a corner keeps the gap too. Two exceptions, so a robot that is
    /// already close to something can still move, and a goal close to a wall can still be reached:
    ///   * the START BASIN: cells reached from the start cell by never moving closer to an obstacle (the way out);
    ///   * the GOAL BASIN: the same from the goal cell (the way in).
    /// Basin cells cost <see cref="K.PlannerNearWallCostFactor"/> times more, so a path leaves them as soon as it can.
    /// A gap narrower than the body is closed unless the start or the goal is in it.
    ///
    /// Cost. Distance, divided by the believed traversability (harder floor costs more; floor at
    /// <see cref="K.PlannerMinTraversability"/>). Cells never written read as normal floor, so unexplored space in
    /// SLAM is planned through optimistically: a wrong guess is found by sensing and the path is re-planned.
    ///
    /// The clearance of each cell is cached for the map's current topology (which cells are blocked); the cache is
    /// rebuilt when a cell flips between blocked and passable, or a query needs a larger area.
    /// </summary>
    public sealed class PathPlanner
    {
        readonly InternalModelView view;
        readonly float clearance;
        readonly float cellSize;
        readonly int reachCells; // how far (cells) a blocked cell's influence on clearance reaches
        readonly float half;     // half a cell
        readonly float cap;      // clearance values are capped here: anything above the clearance is just "free"

        // Clearance field over a rectangle of cells.
        float[] field;
        int fieldMinRow, fieldMinCol, fieldRows, fieldCols;
        int fieldVersion = int.MinValue;

        // A* scratch, sized to the field.
        float[] cost;
        int[] parent;
        int[] seen;     // == search when visited this search
        int[] closed;   // == search when expanded this search
        int[] basin;    // == search when in the start or goal basin this search
        int search;
        readonly List<int> heapIndex = new List<int>();
        readonly List<float> heapKey = new List<float>();
        readonly Queue<int> flood = new Queue<int>();
        readonly List<Vector2> rawPath = new List<Vector2>();

        public PathPlanner(InternalModelView view, float clearanceMeters)
        {
            this.view = view ?? throw new ArgumentNullException(nameof(view));
            clearance = Mathf.Max(0f, clearanceMeters);
            cellSize = view.CellSizeMeters;
            half = cellSize * 0.5f;
            reachCells = Mathf.CeilToInt((clearance + half) / cellSize) + 1;
            cap = clearance + cellSize;
        }

        /// <summary>The distance the body is kept from anything believed impassable (radius + margin), metres.</summary>
        public float Clearance => clearance;

        /// <summary>How many A* searches have been run (reporting and tests).</summary>
        public int Searches { get; private set; }

        /// <summary>
        /// Plans from <paramref name="from"/> to <paramref name="to"/>. On success, <paramref name="into"/> (cleared
        /// first) holds the path's points, starting with <paramref name="from"/> and ending with
        /// <paramref name="to"/>, and <paramref name="length"/> its length in metres. False when the goal is in a cell
        /// believed impassable, or no path exists for the body.
        /// </summary>
        public bool FindPath(Vector2 from, Vector2 to, List<Vector2> into, out float length)
        {
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                return FindPathUntimed(from, to, into, out length);
            }
            finally
            {
                AddTime(started);
            }
        }

        bool FindPathUntimed(Vector2 from, Vector2 to, List<Vector2> into, out float length)
        {
            into.Clear();
            length = float.PositiveInfinity;
            if (!view.TryWorldToCell(from, out var start) || !view.TryWorldToCell(to, out var goal)) return false;
            if (view.IsBlocked(goal.row, goal.col)) return false;

            Searches++;
            EnsureField(start, goal);

            // In plain view (the usual case for a goal picked along a free line): one straight leg, no search. Not across
            // unexplored floor, though: that would be a guess, and a known way round is preferred (below).
            if ((start.row == goal.row && start.col == goal.col) || (Clear(from, to) && Known(start, goal)))
            {
                into.Add(from);
                into.Add(to);
                length = Vector2.Distance(from, to);
                return true;
            }

            search++;
            int startIndex = Index(start.row, start.col);
            int goalIndex = Index(goal.row, goal.col);
            MarkBasin(startIndex);
            MarkBasin(goalIndex);

            if (!Search(startIndex, goalIndex)) return false;

            // Cells back from the goal, then shortened to straight legs.
            rawPath.Clear();
            for (int index = goalIndex; index != startIndex; index = parent[index]) rawPath.Add(CenterOf(index));
            rawPath.Add(from);
            rawPath.Reverse();
            rawPath[rawPath.Count - 1] = to;

            Shorten(rawPath, into);
            length = 0f;
            for (int i = 1; i < into.Count; i++) length += Vector2.Distance(into[i - 1], into[i]);
            return true;
        }

        /// <summary>
        /// True if the straight leg from <paramref name="a"/> to <paramref name="b"/> keeps the body clear: every point
        /// of it is at least the clearance from every cell believed impassable, or, when an end is already closer than
        /// that, at least as far as the closer end is. So a leg that starts or ends near a wall is fine, but one that
        /// runs into something, or cuts a corner too fine, is not. A cell the start or end point lies inside (a
        /// wrongly blocked cell under the robot) is ignored.
        /// </summary>
        public bool SegmentClear(Vector2 a, Vector2 b)
        {
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                return Clear(a, b);
            }
            finally
            {
                AddTime(started);
            }
        }

        /// <summary>Real milliseconds spent planning and checking legs so far (reporting).</summary>
        public double Milliseconds { get; private set; }

        void AddTime(long started) =>
            Milliseconds += (System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

        /// <summary>
        /// <see cref="SegmentClear"/>, untimed. Walks every cell the leg passes through (Amanatides–Woo). Every point of
        /// the leg in a cell is within half the cell's diagonal of its centre, so a cell whose cached clearance is at
        /// least the required gap plus that is clear without further work; only cells closer than that are checked
        /// exactly, segment against each nearby blocked square.
        /// </summary>
        bool Clear(Vector2 a, Vector2 b)
        {
            if (!view.TryWorldToCell(a, out var ca) || !view.TryWorldToCell(b, out var cb)) return false;
            float required = Mathf.Min(clearance, Mathf.Min(PointClearance(a), PointClearance(b))) - 1e-4f;
            float halfDiagonal = half * 1.41421356f;
            float safe = required + halfDiagonal;

            Vector2 d = b - a;
            int row = ca.row, col = ca.col;
            int stepCol = d.x > 0f ? 1 : -1, stepRow = d.y > 0f ? 1 : -1;
            Vector2 first = view.CellCenter(row, col);
            float tMaxX = Mathf.Abs(d.x) > 1e-9f ? (first.x + stepCol * half - a.x) / d.x : float.PositiveInfinity;
            float tMaxY = Mathf.Abs(d.y) > 1e-9f ? (first.y + stepRow * half - a.y) / d.y : float.PositiveInfinity;
            float tDeltaX = Mathf.Abs(d.x) > 1e-9f ? cellSize / Mathf.Abs(d.x) : float.PositiveInfinity;
            float tDeltaY = Mathf.Abs(d.y) > 1e-9f ? cellSize / Mathf.Abs(d.y) : float.PositiveInfinity;
            int guard = Math.Abs(cb.row - ca.row) + Math.Abs(cb.col - ca.col) + 2;

            while (true)
            {
                if (ClearanceOf(row, col) < safe && !CellClear(row, col, a, b, required, safe)) return false;
                if ((row == cb.row && col == cb.col) || guard-- <= 0) return true;

                if (tMaxX < tMaxY)
                {
                    col += stepCol;
                    tMaxX += tDeltaX;
                }
                else
                {
                    row += stepRow;
                    tMaxY += tDeltaY;
                }
            }
        }

        /// <summary>The exact check for the part of leg a-b in one cell: no blocked square near the cell comes within <paramref name="required"/>.</summary>
        bool CellClear(int row, int col, Vector2 a, Vector2 b, float required, float safe)
        {
            Vector2 here = view.CellCenter(row, col);
            int reach = Mathf.CeilToInt((safe + half) / cellSize);
            for (int r = row - reach; r <= row + reach; r++)
                for (int c = col - reach; c <= col + reach; c++)
                {
                    if (!view.IsBlocked(r, c)) continue;
                    Vector2 centre = view.CellCenter(r, c);
                    if (PointToSquare(here, centre) >= safe) continue; // too far from this cell to matter
                    if (Inside(a, centre) || Inside(b, centre)) continue;
                    if (SegmentToSquare(a, b, centre) < required) return false;
                }
            return true;
        }

        /// <summary>The distance from a point to the nearest cell believed impassable (to its edge), capped just above the clearance.</summary>
        public float PointClearance(Vector2 point)
        {
            if (!view.TryWorldToCell(point, out var cell)) return 0f;
            float best = cap;
            for (int r = cell.row - reachCells; r <= cell.row + reachCells; r++)
                for (int c = cell.col - reachCells; c <= cell.col + reachCells; c++)
                {
                    if (!view.IsBlocked(r, c)) continue;
                    float d = PointToSquare(point, view.CellCenter(r, c));
                    if (d < best) best = d;
                }
            return best;
        }

        bool Inside(Vector2 p, Vector2 centre) => Mathf.Abs(p.x - centre.x) <= half && Mathf.Abs(p.y - centre.y) <= half;

        float PointToSquare(Vector2 p, Vector2 centre)
        {
            float dx = Mathf.Max(Mathf.Abs(p.x - centre.x) - half, 0f);
            float dy = Mathf.Max(Mathf.Abs(p.y - centre.y) - half, 0f);
            return Mathf.Sqrt(dx * dx + dy * dy);
        }

        /// <summary>The distance between segment a-b and the cell square at <paramref name="centre"/> (0 if they meet).</summary>
        float SegmentToSquare(Vector2 a, Vector2 b, Vector2 centre)
        {
            Vector2 min = centre - new Vector2(half, half), max = centre + new Vector2(half, half);
            if (SegmentMeetsBox(a, b, min, max)) return 0f;

            // Apart: the closest pair includes an end of the segment or a corner of the square.
            float best = Mathf.Min(PointToSquare(a, centre), PointToSquare(b, centre));
            best = Mathf.Min(best, PointToSegment(min, a, b));
            best = Mathf.Min(best, PointToSegment(max, a, b));
            best = Mathf.Min(best, PointToSegment(new Vector2(min.x, max.y), a, b));
            best = Mathf.Min(best, PointToSegment(new Vector2(max.x, min.y), a, b));
            return best;
        }

        static float PointToSegment(Vector2 p, Vector2 a, Vector2 b)
        {
            Vector2 ab = b - a;
            float lengthSq = ab.sqrMagnitude;
            float t = lengthSq > 1e-12f ? Mathf.Clamp01(Vector2.Dot(p - a, ab) / lengthSq) : 0f;
            return Vector2.Distance(p, a + ab * t);
        }

        /// <summary>Liang–Barsky: does the segment touch the box?</summary>
        static bool SegmentMeetsBox(Vector2 a, Vector2 b, Vector2 min, Vector2 max)
        {
            float t0 = 0f, t1 = 1f;
            Vector2 d = b - a;
            return Clip(-d.x, a.x - min.x, ref t0, ref t1) && Clip(d.x, max.x - a.x, ref t0, ref t1) &&
                   Clip(-d.y, a.y - min.y, ref t0, ref t1) && Clip(d.y, max.y - a.y, ref t0, ref t1);
        }

        static bool Clip(float p, float q, ref float t0, ref float t1)
        {
            if (Mathf.Abs(p) < 1e-12f) return q >= 0f;
            float t = q / p;
            if (p < 0f)
            {
                if (t > t1) return false;
                if (t > t0) t0 = t;
            }
            else
            {
                if (t < t0) return false;
                if (t < t1) t1 = t;
            }
            return true;
        }

        /// <summary>A shortening leg: clear for the body, and not cutting across unexplored floor the search went round.</summary>
        bool ClearAndKnown(Vector2 a, Vector2 b) =>
            Clear(a, b) && view.TryWorldToCell(a, out var ca) && view.TryWorldToCell(b, out var cb) &&
            (Known(ca, cb) || !AnyKnown(ca, cb));

        /// <summary>True if any cell on the line has been sensed (a leg wholly in unknown floor is as the search found it).</summary>
        bool AnyKnown(CellIndex a, CellIndex b)
        {
            foreach (var cell in GridLine.Cells(a.row, a.col, b.row, b.col))
                if (view.IsExplored(cell.row, cell.col)) return true;
            return false;
        }

        /// <summary>True if every cell on the straight line between two cells has been sensed (always, in a Normal run).</summary>
        bool Known(CellIndex a, CellIndex b)
        {
            foreach (var cell in GridLine.Cells(a.row, a.col, b.row, b.col))
                if (!view.IsExplored(cell.row, cell.col)) return false;
            return true;
        }

        /// <summary>The distance from the cell's centre to the nearest believed-impassable cell's edge (0 for such a cell), capped just above the clearance.</summary>
        public float ClearanceOf(int row, int col)
        {
            if (fieldVersion == view.TopologyVersion && InField(row, col)) return field[(row - fieldMinRow) * fieldCols + (col - fieldMinCol)];
            return DirectClearance(row, col);
        }

        // ---- The clearance field ------------------------------------------------------------------------

        void EnsureField(CellIndex start, CellIndex goal)
        {
            int pad = reachCells + Mathf.CeilToInt(K.PlannerPadMeters / cellSize);
            int minRow = Math.Min(start.row, goal.row), maxRow = Math.Max(start.row, goal.row);
            int minCol = Math.Min(start.col, goal.col), maxCol = Math.Max(start.col, goal.col);
            if (!view.IsEmpty)
            {
                minRow = Math.Min(minRow, view.MinRow);
                minCol = Math.Min(minCol, view.MinCol);
                maxRow = Math.Max(maxRow, view.MinRow + view.Rows - 1);
                maxCol = Math.Max(maxCol, view.MinCol + view.Cols - 1);
            }
            minRow -= pad;
            minCol -= pad;
            maxRow += pad;
            maxCol += pad;

            bool covers = field != null && minRow >= fieldMinRow && minCol >= fieldMinCol &&
                          maxRow < fieldMinRow + fieldRows && maxCol < fieldMinCol + fieldCols;
            if (covers && fieldVersion == view.TopologyVersion) return;

            fieldMinRow = minRow;
            fieldMinCol = minCol;
            fieldRows = maxRow - minRow + 1;
            fieldCols = maxCol - minCol + 1;
            int total = fieldRows * fieldCols;

            if (field == null || field.Length < total)
            {
                field = new float[total];
                cost = new float[total];
                parent = new int[total];
                seen = new int[total];
                closed = new int[total];
                basin = new int[total];
                search = 0;
            }
            for (int i = 0; i < total; i++) field[i] = cap;

            // Every blocked cell lowers the clearance of the cells around it.
            for (int r = 0; r < fieldRows; r++)
            {
                for (int c = 0; c < fieldCols; c++)
                {
                    if (!view.IsBlocked(r + fieldMinRow, c + fieldMinCol)) continue;
                    for (int dr = -reachCells; dr <= reachCells; dr++)
                    {
                        int rr = r + dr;
                        if (rr < 0 || rr >= fieldRows) continue;
                        for (int dc = -reachCells; dc <= reachCells; dc++)
                        {
                            int cc = c + dc;
                            if (cc < 0 || cc >= fieldCols) continue;
                            float d = OffsetDistance(dr, dc);
                            int index = rr * fieldCols + cc;
                            if (d < field[index]) field[index] = d;
                        }
                    }
                }
            }
            fieldVersion = view.TopologyVersion;
        }

        float DirectClearance(int row, int col)
        {
            float best = cap;
            for (int dr = -reachCells; dr <= reachCells; dr++)
                for (int dc = -reachCells; dc <= reachCells; dc++)
                {
                    if (!view.IsBlocked(row + dr, col + dc)) continue;
                    float d = OffsetDistance(dr, dc);
                    if (d < best) best = d;
                }
            return best;
        }

        /// <summary>From a cell's centre to the edge of the cell (dr, dc) away: 0 for the cell itself.</summary>
        float OffsetDistance(int dr, int dc)
        {
            if (dr == 0 && dc == 0) return 0f;
            float dx = Mathf.Max(Math.Abs(dc) * cellSize - half, 0f);
            float dy = Mathf.Max(Math.Abs(dr) * cellSize - half, 0f);
            return Mathf.Sqrt(dx * dx + dy * dy);
        }

        bool InField(int row, int col) =>
            field != null && row >= fieldMinRow && col >= fieldMinCol && row < fieldMinRow + fieldRows && col < fieldMinCol + fieldCols;

        int Index(int row, int col) => (row - fieldMinRow) * fieldCols + (col - fieldMinCol);

        Vector2 CenterOf(int index)
        {
            int r = index / fieldCols;
            return view.CellCenter(r + fieldMinRow, index - r * fieldCols + fieldMinCol);
        }

        bool Free(int index) => field[index] > clearance;

        // ---- Basins: the way out from the start, the way in to the goal ---------------------------------

        void MarkBasin(int from)
        {
            basin[from] = search;
            if (Free(from)) return;

            flood.Clear();
            flood.Enqueue(from);
            while (flood.Count > 0)
            {
                int index = flood.Dequeue();
                int r = index / fieldCols, c = index - r * fieldCols;
                float here = field[index];
                for (int dr = -1; dr <= 1; dr++)
                    for (int dc = -1; dc <= 1; dc++)
                    {
                        if (dr == 0 && dc == 0) continue;
                        int rr = r + dr, cc = c + dc;
                        if (rr < 0 || cc < 0 || rr >= fieldRows || cc >= fieldCols) continue;
                        int next = rr * fieldCols + cc;
                        if (basin[next] == search || field[next] <= 0f || field[next] < here) continue;
                        basin[next] = search;
                        if (!Free(next)) flood.Enqueue(next); // stop at free cells: they are open anyway
                    }
            }
        }

        bool Open(int index) => field[index] > 0f && (Free(index) || basin[index] == search);

        // ---- A* -----------------------------------------------------------------------------------------

        bool Search(int startIndex, int goalIndex)
        {
            heapIndex.Clear();
            heapKey.Clear();

            int goalRow = goalIndex / fieldCols, goalCol = goalIndex - goalRow * fieldCols;
            cost[startIndex] = 0f;
            seen[startIndex] = search;
            Push(startIndex, Heuristic(startIndex, goalRow, goalCol));

            int expansions = 0;
            while (heapIndex.Count > 0)
            {
                int index = Pop();
                if (closed[index] == search) continue;
                closed[index] = search;
                if (index == goalIndex) return true;
                if (++expansions > K.PlannerMaxExpansions) return false;

                int r = index / fieldCols, c = index - r * fieldCols;
                for (int dr = -1; dr <= 1; dr++)
                    for (int dc = -1; dc <= 1; dc++)
                    {
                        if (dr == 0 && dc == 0) continue;
                        int rr = r + dr, cc = c + dc;
                        if (rr < 0 || cc < 0 || rr >= fieldRows || cc >= fieldCols) continue;
                        int next = rr * fieldCols + cc;
                        if (closed[next] == search || !Open(next)) continue;

                        // No cutting a corner past a closed cell.
                        if (dr != 0 && dc != 0 && (!Open(index + dr * fieldCols) || !Open(index + dc))) continue;

                        float step = (dr != 0 && dc != 0 ? 1.41421356f : 1f) * cellSize;
                        float traversability = Mathf.Clamp(view.Traversability(rr + fieldMinRow, cc + fieldMinCol), K.PlannerMinTraversability, 1f);
                        float weight = 1f / traversability;
                        if (!Free(next)) weight *= K.PlannerNearWallCostFactor;
                        if (!view.IsExplored(rr + fieldMinRow, cc + fieldMinCol)) weight *= K.PlannerUnknownCostFactor;

                        float candidate = cost[index] + step * weight;
                        if (seen[next] == search && candidate >= cost[next]) continue;
                        seen[next] = search;
                        cost[next] = candidate;
                        parent[next] = index;
                        Push(next, candidate + Heuristic(next, goalRow, goalCol));
                    }
            }
            return false;
        }

        float Heuristic(int index, int goalRow, int goalCol)
        {
            int r = index / fieldCols, c = index - r * fieldCols;
            int dr = Math.Abs(r - goalRow), dc = Math.Abs(c - goalCol);
            int diagonal = Math.Min(dr, dc), straight = Math.Max(dr, dc) - diagonal;
            return (diagonal * 1.41421356f + straight) * cellSize;
        }

        void Push(int index, float key)
        {
            heapIndex.Add(index);
            heapKey.Add(key);
            int i = heapIndex.Count - 1;
            while (i > 0)
            {
                int up = (i - 1) / 2;
                if (heapKey[up] <= heapKey[i]) break;
                Swap(i, up);
                i = up;
            }
        }

        int Pop()
        {
            int top = heapIndex[0];
            int last = heapIndex.Count - 1;
            heapIndex[0] = heapIndex[last];
            heapKey[0] = heapKey[last];
            heapIndex.RemoveAt(last);
            heapKey.RemoveAt(last);

            int i = 0;
            while (true)
            {
                int left = 2 * i + 1, right = left + 1, smallest = i;
                if (left < heapIndex.Count && heapKey[left] < heapKey[smallest]) smallest = left;
                if (right < heapIndex.Count && heapKey[right] < heapKey[smallest]) smallest = right;
                if (smallest == i) break;
                Swap(i, smallest);
                i = smallest;
            }
            return top;
        }

        void Swap(int a, int b)
        {
            (heapIndex[a], heapIndex[b]) = (heapIndex[b], heapIndex[a]);
            (heapKey[a], heapKey[b]) = (heapKey[b], heapKey[a]);
        }

        // ---- Shortening -----------------------------------------------------------------------------------

        /// <summary>
        /// Greedy string-pulling: from each kept point, jump as far along the cell path as a clear straight leg reaches
        /// (found by galloping, then halving), and keep that point.
        /// </summary>
        void Shorten(List<Vector2> points, List<Vector2> into)
        {
            into.Add(points[0]);
            int anchor = 0;
            while (anchor < points.Count - 1)
            {
                // Gallop ahead in doubling steps while the leg stays clear, then narrow down.
                int next = anchor + 1;
                int step = 1;
                while (next + step < points.Count && ClearAndKnown(points[anchor], points[next + step]))
                {
                    next += step;
                    step *= 2;
                }
                for (step /= 2; step >= 1; step /= 2)
                    if (next + step < points.Count && ClearAndKnown(points[anchor], points[next + step])) next += step;

                into.Add(points[next]);
                anchor = next;
            }
        }
    }
}
