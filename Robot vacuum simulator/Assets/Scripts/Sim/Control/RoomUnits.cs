using System;
using System.Collections.Generic;
using RobotVacuum.AlgorithmApi;

namespace RobotVacuum.Sim.Control
{
    /// <summary>
    /// Splits the floor the robot can reach into ROOMS by finding DOORWAYS as gaps between wall ends.
    ///
    /// Width alone can't tell a doorway from a room: in the sample apartment the hall (about 1.3 m inside) is barely
    /// wider than its own 1.1 m door. What marks a doorway is that a wall stops and, across a short gap, starts again
    /// in line. So:
    ///   1. WALL ENDS. Blocked cells mostly surrounded by free floor (at least 5 of 8 neighbours) are candidates. For
    ///      each, look at the blocked cells within <see cref="WallSampleMeters"/>: their principal direction (PCA) is
    ///      the wall's direction there, and their mean offset says which side the wall body is on.
    ///   2. PAIRS. Two candidates A and B, <see cref="MinGap"/>..<see cref="MaxGap"/> apart, are a doorway when both
    ///      walls run along AB (|direction · AB| above <see cref="InLine"/>), each wall's body lies BEHIND its end
    ///      (away from the other end), and only free floor lies between them. Facing walls of a corridor fail the
    ///      first test (their direction is across AB); two cells along one wall fail the second and third.
    ///   3. ROOMS. The doorway segments are cut out of the floor; rooms are the connected (4-connected) pieces left.
    ///      Pieces under <see cref="MinRoomArea"/>, and the cut cells themselves, are then given to the room beside them.
    ///
    /// In SLAM a doorway appears once both its jambs have been seen; until then its floor belongs to the room the robot
    /// is in, which is what should happen. An opening with no wall stubs (open plan) stays one room.
    /// Labels are per snapshot cell: -1 = not floor the robot can use; 0.. = room number (renumbered every build).
    /// </summary>
    public sealed class RoomUnits
    {
        public const float MinGap = 0.5f, MaxGap = 1.4f;
        public const float WallSampleMeters = 0.3f;
        public const float InLine = 0.8f;
        public const float MinRoomArea = 0.5f;

        int[] label = new int[0];
        bool[] cut = new bool[0];
        readonly Queue<int> queue = new Queue<int>();
        readonly List<int> component = new List<int>();

        public int Count { get; private set; }

        /// <summary>The doorways found (end points), for tests and the overlay.</summary>
        public readonly List<(Vec2 a, Vec2 b)> Doorways = new List<(Vec2, Vec2)>();

        public int LabelOf(MapSnapshot snapshot, int row, int col)
        {
            int index = snapshot.IndexOf(row, col);
            return index < 0 ? -1 : label[index];
        }

        /// <param name="wallsChanged">
        /// True when the blocked cells (or the snapshot's extent) changed since the last build: the doorways are found
        /// again. False reuses the last build's doorways (they depend only on the walls).
        /// </param>
        public void Build(MapSnapshot snapshot, IBeliefMap map, ReachField reach, bool wallsChanged = true)
        {
            int rows = snapshot.Rows, cols = snapshot.Cols, n = rows * cols;
            if (label.Length < n)
            {
                label = new int[n];
                cut = new bool[n];
                wallsChanged = true;
            }
            Count = 0;
            for (int i = 0; i < n; i++)
            {
                int row = snapshot.MinRow + i / cols, col = snapshot.MinCol + i % cols;
                label[i] = reach.Reachable(row, col) ? -2 : -1; // -2 = usable, not yet labelled
            }

            if (wallsChanged)
            {
                Doorways.Clear();
                for (int i = 0; i < n; i++) cut[i] = false;
                FindDoorways(snapshot, map);
            }

            // Rooms: components of usable floor not crossing a doorway cut.
            int minCells = (int)(MinRoomArea / (snapshot.CellSize * snapshot.CellSize));
            for (int i = 0; i < n; i++)
            {
                if (label[i] != -2 || cut[i]) continue;
                component.Clear();
                label[i] = -3;
                component.Add(i);
                for (int k = 0; k < component.Count; k++)
                    foreach (int j in Neighbours(component[k], rows, cols))
                        if (label[j] == -2 && !cut[j])
                        {
                            label[j] = -3;
                            component.Add(j);
                        }

                if (component.Count < minCells)
                {
                    foreach (int j in component) label[j] = -4; // a sliver: joins a neighbour below
                    continue;
                }
                int room = Count++;
                foreach (int j in component) label[j] = room;
            }

            // Cut cells and slivers join the room beside them.
            for (int i = 0; i < n; i++) if (label[i] == -4) label[i] = -2;
            for (int i = 0; i < n; i++) if (label[i] >= 0) queue.Enqueue(i);
            while (queue.Count > 0)
            {
                int i = queue.Dequeue();
                foreach (int j in Neighbours(i, rows, cols))
                {
                    if (label[j] != -2) continue;
                    label[j] = label[i];
                    queue.Enqueue(j);
                }
            }

            // Usable floor no room reached (no room big enough anywhere near): one room of its own.
            for (int i = 0; i < n; i++)
            {
                if (label[i] != -2) continue;
                label[i] = Count++;
                queue.Enqueue(i);
                while (queue.Count > 0)
                {
                    int k = queue.Dequeue();
                    foreach (int j in Neighbours(k, rows, cols))
                    {
                        if (label[j] != -2) continue;
                        label[j] = label[k];
                        queue.Enqueue(j);
                    }
                }
            }
        }

        struct End
        {
            public int row, col;
            public Vec2 at, direction, behind; // wall direction (unit), mean offset of the wall body from here
        }

        void FindDoorways(MapSnapshot snapshot, IBeliefMap map)
        {
            float cell = snapshot.CellSize;
            int sample = (int)Math.Ceiling(WallSampleMeters / cell);
            var ends = new List<End>();

            for (int row = snapshot.MinRow; row < snapshot.MinRow + snapshot.Rows; row++)
                for (int col = snapshot.MinCol; col < snapshot.MinCol + snapshot.Cols; col++)
                {
                    if (!snapshot.IsBlocked(row, col)) continue;
                    int free = 0;
                    for (int dr = -1; dr <= 1; dr++)
                        for (int dc = -1; dc <= 1; dc++)
                            if ((dr != 0 || dc != 0) && !snapshot.IsBlocked(row + dr, col + dc)) free++;
                    if (free < 5) continue;

                    // The wall around this cell: principal direction and where its body lies.
                    double sx = 0, sy = 0, sxx = 0, syy = 0, sxy = 0;
                    int count = 0;
                    for (int dr = -sample; dr <= sample; dr++)
                        for (int dc = -sample; dc <= sample; dc++)
                        {
                            if ((dr * dr + dc * dc) > sample * sample || !snapshot.IsBlocked(row + dr, col + dc)) continue;
                            double x = dc * cell, y = dr * cell;
                            sx += x; sy += y; sxx += x * x; syy += y * y; sxy += x * y;
                            count++;
                        }
                    if (count < 3) continue; // a speck, not a wall
                    double mx = sx / count, my = sy / count;
                    if (mx * mx + my * my < 0.05 * 0.05) continue; // wall on both sides: the middle of a wall, not an end
                    double cxx = sxx / count - mx * mx, cyy = syy / count - my * my, cxy = sxy / count - mx * my;
                    double angle = 0.5 * Math.Atan2(2 * cxy, cxx - cyy);
                    ends.Add(new End
                    {
                        row = row,
                        col = col,
                        at = map.CellCenter(row, col),
                        direction = new Vec2((float)Math.Cos(angle), (float)Math.Sin(angle)),
                        behind = new Vec2((float)mx, (float)my),
                    });
                }

            for (int i = 0; i < ends.Count; i++)
            {
                var a = ends[i];
                int bestJ = -1;
                float bestGap = float.PositiveInfinity;
                for (int j = 0; j < ends.Count; j++)
                {
                    if (j == i) continue;
                    var b = ends[j];
                    Vec2 ab = b.at - a.at;
                    float gap = ab.Length;
                    if (gap < MinGap || gap > MaxGap || gap >= bestGap) continue;
                    Vec2 u = ab / gap;
                    if (Math.Abs(Vec2.Dot(a.direction, u)) < InLine || Math.Abs(Vec2.Dot(b.direction, u)) < InLine) continue;
                    if (Vec2.Dot(a.behind, u) > -0.05f || Vec2.Dot(b.behind, u) < 0.05f) continue; // each wall's body behind its own end
                    if (!GapIsFree(snapshot, a, b)) continue;
                    bestGap = gap;
                    bestJ = j;
                }
                if (bestJ < 0) continue;

                var end = ends[bestJ];
                Doorways.Add((a.at, end.at));
                foreach (var c in GridLine.Cells(a.row, a.col, end.row, end.col))
                {
                    int index = snapshot.IndexOf(c.row, c.col);
                    if (index >= 0) cut[index] = true;
                }
            }
        }

        /// <summary>Nothing blocked on the line between two wall ends, apart from the ends' own walls.</summary>
        static bool GapIsFree(MapSnapshot snapshot, End a, End b)
        {
            int skip = 2; // cells at each end belonging to the jambs themselves
            var cells = new List<CellIndex>(GridLine.Cells(a.row, a.col, b.row, b.col));
            for (int k = skip; k < cells.Count - skip; k++)
                if (snapshot.IsBlocked(cells[k].row, cells[k].col)) return false;
            return cells.Count > 2 * skip;
        }

        static IEnumerable<int> Neighbours(int i, int rows, int cols)
        {
            int r = i / cols, c = i % cols;
            if (r > 0) yield return i - cols;
            if (r < rows - 1) yield return i + cols;
            if (c > 0) yield return i - 1;
            if (c < cols - 1) yield return i + 1;
        }
    }
}
