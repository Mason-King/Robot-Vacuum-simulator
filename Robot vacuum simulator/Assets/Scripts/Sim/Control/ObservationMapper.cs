using System.Collections.Generic;
using RobotVacuum.Sim.Sensors;
using RobotVacuum.AlgorithmApi;
using UnityEngine;
using C = RobotVacuum.Sim.Control.BeliefUpdateConstants;

namespace RobotVacuum.Sim.Control
{
    /// <summary>
    /// Turns one step's sensor readings into per-cell observations and applies them to the Internal Model
    /// (design doc, section 6). Every cell is placed using the BELIEVED pose: the sensors measured from where the
    /// robot really is, but the robot can only interpret them from where it thinks it is.
    ///
    ///   lidar hit          → the hit cell is observed impassable (0)
    ///   lidar clear path   → cells from the robot up to (not including) the hit cell, or out to max range on a
    ///                        miss, are observed passable at the floor ease the robot remembers for them (1 if unknown)
    ///   whisker bump       → the cell just beyond the contact is set to 0 directly, in both modes (a bump is certain)
    ///   whisker drag       → the cell under the robot: its floor ease
    ///   floor sensor       → the cell under the robot: its vacuum resistance
    ///   footprint          → cells under the body can't be obstacles; dwell and cleanliness beliefs grow there
    ///
    /// SLAM off: observations are ASSIGNED, except that a preloaded wall is never cleared (see
    /// <see cref="InternalModelWriter.MarkPermanent"/>).
    /// SLAM on: the lidar scan has already gone into <see cref="LocalSlam"/>'s submaps (design doc, section 19); each
    /// cell that scan changed takes its traversability from the submaps' occupancy probability. Floor ease and vacuum
    /// resistance keep the weighted rule in <see cref="BeliefRules"/>. Each branch tests the SLAM bool, never the
    /// noise value.
    /// </summary>
    public sealed class ObservationMapper
    {
        readonly InternalModelWriter writer;
        readonly bool slam;
        readonly float cellSize;
        readonly LocalSlam localSlam;
        readonly GlobalSlam globalSlam;

        /// <summary>SLAM: every Internal Model cell drawn from the submaps (so a redraw can clear the ones no longer drawn).</summary>
        HashSet<long> drawn = new HashSet<long>();
        HashSet<long> drawing = new HashSet<long>();

        /// <param name="localSlam">SLAM only: the submaps the lidar is mapped into (by <see cref="LocalSlam.Track"/>, before <see cref="Apply"/>).</param>
        /// <param name="globalSlam">SLAM with loop closure: where the submaps really are (null = local SLAM's frame is the global frame).</param>
        public ObservationMapper(InternalModelWriter writer, bool slam, LocalSlam localSlam = null, GlobalSlam globalSlam = null)
        {
            this.writer = writer;
            this.slam = slam;
            this.localSlam = localSlam;
            this.globalSlam = globalSlam;
            cellSize = writer.Grid.CellSizeMeters;
        }

        /// <summary>Applies everything in <paramref name="frame"/>, seen from <paramref name="pose"/>.</summary>
        public void Apply(SensorFrame frame, RobotPose pose)
        {
            if (frame.hasLidarScan) ApplyLidar(frame, pose);
            ApplyDrag(frame, pose);
            if (frame.hasVacuumResistance) ApplyVacuumResistance(frame, pose);
            if (frame.bumped) ApplyBump(frame, pose); // last, so a certain 0 is never overwritten by this step's clear views
        }

        // ---- Lidar ---------------------------------------------------------------------------------------

        void ApplyLidar(SensorFrame frame, RobotPose pose)
        {
            if (slam)
            {
                SyncFromSubmaps();
                return;
            }

            var grid = writer.Grid;
            if (!grid.TryWorldToGridIndex(pose.position, out int r0, out int c0)) return;

            float maxRange = frame.lidarRange;
            var rays = frame.lidarRays;

            for (int i = 0; i < rays.Count; i++)
            {
                var ray = rays[i];
                Vector2 end = pose.position + RobotPose.DirectionOf(pose.heading + ray.bearing) * ray.range;
                if (!grid.TryWorldToGridIndex(end, out int r1, out int c1)) continue;

                WalkRay(r0, c0, r1, c1, ray.range, maxRange, ray.hit);
            }
        }

        /// <summary>
        /// Bresenham from the robot's cell to the end cell, without allocating. Every cell before the end is seen
        /// clear; the end cell is the hit (if the ray hit) or seen clear too (a miss reaches max range).
        /// </summary>
        void WalkRay(int r0, int c0, int r1, int c1, float range, float maxRange, bool hit)
        {
            int dc = Mathf.Abs(c1 - c0), sc = c0 < c1 ? 1 : -1;
            int dr = -Mathf.Abs(r1 - r0), sr = r0 < r1 ? 1 : -1;
            int error = dc + dr;

            int steps = Mathf.Max(dc, -dr);
            float metersPerStep = steps > 0 ? range / steps : 0f;

            int r = r0, c = c0, step = 0;
            while (true)
            {
                bool last = r == r1 && c == c1;
                float distance = step * metersPerStep;

                if (last && hit) ObserveHit(r, c, distance, maxRange);
                else ObserveClear(r, c, distance, maxRange);

                if (last) return;

                int twice = 2 * error;
                if (twice >= dr)
                {
                    error += dr;
                    c += sc;
                }
                if (twice <= dc)
                {
                    error += dc;
                    r += sr;
                }
                step++;
            }
        }

        // Normal only (SLAM lidar goes through the submaps: SyncFromSubmaps).
        void ObserveHit(int row, int col, float range, float maxRange) => writer.SetTraversability(row, col, 0f);

        void ObserveClear(int row, int col, float range, float maxRange)
        {
            if (!writer.IsPermanent(row, col)) writer.SetTraversability(row, col, writer.EaseAt(row, col));
        }

        // ---- Lidar, SLAM: from the submaps ---------------------------------------------------------------------

        /// <summary>
        /// SLAM: every cell this step's scan changed in the submaps gets its traversability re-read from them, at the
        /// cell's place in the global frame (the matching submap's correction; the same cell without loop closure).
        /// </summary>
        void SyncFromSubmaps()
        {
            if (localSlam == null) return;
            var cells = localSlam.ChangedCells;
            bool corrected = globalSlam != null && globalSlam.SubmapGlobalPoses.Count > 0;
            float cos = 1f, sin = 0f;
            Vector2 localOrigin = Vector2.zero, globalOrigin = Vector2.zero;
            if (corrected) globalSlam.Transform(globalSlam.ReferenceSubmap, out cos, out sin, out localOrigin, out globalOrigin);

            for (int i = 0; i < cells.Count; i++)
            {
                int row = (int)(cells[i] >> 32), col = (int)cells[i];
                float p = localSlam.Probability(row, col);
                int gRow = row, gCol = col;
                if (corrected && !ToGlobalCell(row, col, cos, sin, localOrigin, globalOrigin, out gRow, out gCol)) continue;
                writer.SetTraversability(gRow, gCol, LocalSlam.Traversability(p, writer.EaseAt(gRow, gCol)));
                drawn.Add(CellIndex.Pack(gRow, gCol));
            }
        }

        bool ToGlobalCell(int row, int col, float cos, float sin, Vector2 localOrigin, Vector2 globalOrigin, out int gRow, out int gCol)
        {
            Vector2 centre = writer.Grid.GridIndexToWorldCenter(row, col) - localOrigin;
            Vector2 g = new Vector2(cos * centre.x - sin * centre.y, sin * centre.x + cos * centre.y) + globalOrigin;
            return writer.Grid.TryWorldToGridIndex(g, out gRow, out gCol);
        }

        /// <summary>
        /// SLAM with loop closure, after the submaps moved: the Internal Model is redrawn from every submap at its global
        /// pose. Each cell takes its probability from the newest submap that has seen it (the active ones first, as for
        /// live updates); cells no longer drawn by any submap go back to unknown (the floor's ease).
        ///
        /// Done in a dense window over every submap's global footprint (no per-cell hashing), and only cells whose value
        /// actually changes are written. Gives exactly the cells and values of <see cref="RedrawFromSubmapsReference"/>.
        /// </summary>
        public void RedrawFromSubmaps()
        {
            if (localSlam == null || globalSlam == null) return;
            var submaps = localSlam.Submaps;
            var active = localSlam.ActiveSubmaps;

            // 1. The window: every drawable submap's corner cells in the global frame, plus a cell all round.
            int minRow = int.MaxValue, minCol = int.MaxValue, maxRow = int.MinValue, maxCol = int.MinValue;
            for (int i = 0; i < submaps.Count; i++)
            {
                var grid = submaps[i].grid;
                if (submaps[i].index >= globalSlam.SubmapGlobalPoses.Count || grid.rows == 0 || grid.cols == 0) continue;
                globalSlam.Transform(submaps[i].index, out float cos, out float sin, out Vector2 localOrigin, out Vector2 globalOrigin);
                for (int corner = 0; corner < 4; corner++)
                {
                    int row = grid.minRow + ((corner & 1) == 0 ? 0 : grid.rows - 1);
                    int col = grid.minCol + ((corner & 2) == 0 ? 0 : grid.cols - 1);
                    if (!ToGlobalCell(row, col, cos, sin, localOrigin, globalOrigin, out int gRow, out int gCol)) continue;
                    minRow = Mathf.Min(minRow, gRow);
                    maxRow = Mathf.Max(maxRow, gRow);
                    minCol = Mathf.Min(minCol, gCol);
                    maxCol = Mathf.Max(maxCol, gCol);
                }
            }

            if (minRow <= maxRow)
            {
                windowMinRow = minRow - 1;
                windowMinCol = minCol - 1;
                windowRows = maxRow - minRow + 3;
                windowCols = maxCol - minCol + 3;
            }
            else
            {
                windowMinRow = windowMinCol = windowRows = windowCols = 0;
            }
            int total = windowRows * windowCols;
            if (window.Length < total) window = new float[total];
            else System.Array.Clear(window, 0, total);

            // 2. Newest first: the active submaps, then the finished ones newest to oldest. A cell keeps the first value.
            for (int a = 0; a < active.Count; a++) DrawIntoWindow(active[a]);
            for (int i = submaps.Count - 1; i >= 0; i--)
                if (submaps[i].Finished) DrawIntoWindow(submaps[i]);

            // 3. Cells drawn before but not now go back to unknown.
            foreach (long key in drawn)
            {
                int row = (int)(key >> 32), col = (int)key;
                if (WindowValue(row, col) > 0f) continue;
                writer.SetTraversability(row, col, writer.EaseAt(row, col));
            }

            // 4. Write the window (the writer skips cells already equal) and remember what is drawn.
            drawn.Clear();
            for (int r = 0; r < windowRows; r++)
            {
                int row = windowMinRow + r;
                for (int c = 0; c < windowCols; c++)
                {
                    float p = window[r * windowCols + c];
                    if (p <= 0f) continue;
                    int col = windowMinCol + c;
                    writer.SetTraversability(row, col, LocalSlam.Traversability(p, writer.EaseAt(row, col)));
                    drawn.Add(CellIndex.Pack(row, col));
                }
            }
        }

        // The redraw window: the newest probability for each global cell (0 = not drawn).
        float[] window = new float[0];
        int windowMinRow, windowMinCol, windowRows, windowCols;

        float WindowValue(int row, int col)
        {
            int r = row - windowMinRow, c = col - windowMinCol;
            if (r < 0 || c < 0 || r >= windowRows || c >= windowCols) return 0f;
            return window[r * windowCols + c];
        }

        void DrawIntoWindow(Submap submap)
        {
            if (submap.index >= globalSlam.SubmapGlobalPoses.Count) return;
            globalSlam.Transform(submap.index, out float cos, out float sin, out Vector2 localOrigin, out Vector2 globalOrigin);
            var grid = submap.grid;
            var probability = grid.probability;
            for (int r = 0; r < grid.rows; r++)
            {
                int rowStart = r * grid.cols;
                for (int c = 0; c < grid.cols; c++)
                {
                    float p = probability[rowStart + c];
                    if (p <= 0f) continue;
                    if (!ToGlobalCell(grid.minRow + r, grid.minCol + c, cos, sin, localOrigin, globalOrigin, out int gRow, out int gCol)) continue;
                    int wr = gRow - windowMinRow, wc = gCol - windowMinCol;
                    if (wr < 0 || wc < 0 || wr >= windowRows || wc >= windowCols) continue; // cannot happen: the window holds every corner
                    int index = wr * windowCols + wc;
                    if (window[index] <= 0f) window[index] = p;
                }
            }
        }

        /// <summary>
        /// Tests only: the original, straightforward redraw (a hash set per cell, every cell rewritten), kept so the
        /// fast <see cref="RedrawFromSubmaps"/> can be checked against it cell for cell.
        /// </summary>
        internal void RedrawFromSubmapsReference()
        {
            if (localSlam == null || globalSlam == null) return;
            drawing.Clear();

            var submaps = localSlam.Submaps;
            var active = localSlam.ActiveSubmaps;
            for (int a = 0; a < active.Count; a++) DrawSubmapReference(active[a]);
            for (int i = submaps.Count - 1; i >= 0; i--)
                if (submaps[i].Finished) DrawSubmapReference(submaps[i]);

            foreach (long key in drawn)
            {
                if (drawing.Contains(key)) continue;
                int row = (int)(key >> 32), col = (int)key;
                writer.SetTraversability(row, col, writer.EaseAt(row, col));
            }
            (drawn, drawing) = (drawing, drawn);
        }

        void DrawSubmapReference(Submap submap)
        {
            if (submap.index >= globalSlam.SubmapGlobalPoses.Count) return;
            globalSlam.Transform(submap.index, out float cos, out float sin, out Vector2 localOrigin, out Vector2 globalOrigin);
            var grid = submap.grid;
            var probability = grid.probability;
            for (int r = 0; r < grid.rows; r++)
            {
                for (int c = 0; c < grid.cols; c++)
                {
                    float p = probability[r * grid.cols + c];
                    if (p <= 0f) continue;
                    if (!ToGlobalCell(grid.minRow + r, grid.minCol + c, cos, sin, localOrigin, globalOrigin, out int gRow, out int gCol)) continue;
                    if (!drawing.Add(CellIndex.Pack(gRow, gCol))) continue; // a newer submap already drew it
                    writer.SetTraversability(gRow, gCol, LocalSlam.Traversability(p, writer.EaseAt(gRow, gCol)));
                }
            }
        }

        // ---- Whisker -------------------------------------------------------------------------------------

        void ApplyBump(SensorFrame frame, RobotPose pose)
        {
            // Nudge a quarter cell past the contact point so rounding lands on the obstacle's side.
            Vector2 direction = RobotPose.DirectionOf(pose.heading + frame.bumpBearing);
            Vector2 contact = pose.position + direction * (frame.bumpDistance + cellSize * 0.25f);

            if (writer.Grid.TryWorldToGridIndex(contact, out int row, out int col))
            {
                if (slam) localSlam?.MarkCertain(globalSlam != null ? globalSlam.ToLocal(contact) : contact);
                writer.SetTraversability(row, col, 0f); // certain: set directly in both modes
            }
        }

        void ApplyDrag(SensorFrame frame, RobotPose pose)
        {
            if (!writer.Grid.TryWorldToGridIndex(pose.position, out int row, out int col)) return;

            float ease = slam
                ? BeliefRules.Step(writer.EaseAt(row, col), frame.floorEase, C.DragAlpha, 0f, 0f, snapToZero: false)
                : frame.floorEase;

            writer.SetEase(row, col, ease);
            if (!writer.IsPermanent(row, col))
                writer.SetTraversability(row, col, ease); // the robot is standing on it, so it is passable at that ease
        }

        // ---- Vacuum resistance (floor) sensor --------------------------------------------------------------

        void ApplyVacuumResistance(SensorFrame frame, RobotPose pose)
        {
            if (!writer.Grid.TryWorldToGridIndex(pose.position, out int row, out int col)) return;

            float value = slam
                ? BeliefRules.Step(writer.Get(row, col).vacuumResistanceBelief, frame.vacuumResistance,
                    C.FloorSensorAlpha, 0f, 0f, snapToZero: false)
                : frame.vacuumResistance;

            writer.SetVacuumResistance(row, col, value);
        }

        // ---- Footprint -----------------------------------------------------------------------------------

        /// <summary>
        /// Every step, for each cell well inside the believed footprint: the robot is standing there, so a cell
        /// believed blocked is corrected to its floor ease; and the robot's own dwell and cleanliness estimates grow
        /// (cleanliness at vacuum power ÷ the believed vacuum resistance).
        /// </summary>
        public void ApplyFootprint(RobotPose pose, float radius, float dt, float vacuumPower)
        {
            var grid = writer.Grid;
            Vector2 p = pose.position;

            if (!grid.TryWorldToGridIndex(p - new Vector2(radius, radius), out int minRow, out int minCol)) return;
            if (!grid.TryWorldToGridIndex(p + new Vector2(radius, radius), out int maxRow, out int maxCol)) return;

            float coverRadiusSq = radius * radius;
            float solid = Mathf.Max(0f, radius - cellSize);
            float solidSq = solid * solid;

            for (int row = minRow; row <= maxRow; row++)
            {
                for (int col = minCol; col <= maxCol; col++)
                {
                    float distanceSq = (grid.GridIndexToWorldCenter(row, col) - p).sqrMagnitude;
                    if (distanceSq > coverRadiusSq) continue;

                    if (distanceSq <= solidSq)
                    {
                        if (writer.Traversability(row, col) <= 0f && !writer.IsPermanent(row, col))
                            writer.SetTraversability(row, col, writer.EaseAt(row, col));
                    }

                    float resistance = Mathf.Max(0.01f, writer.Get(row, col).vacuumResistanceBelief);
                    writer.AddDwellAndCleanliness(row, col, dt, dt * vacuumPower / resistance);
                }
            }
        }
    }
}
