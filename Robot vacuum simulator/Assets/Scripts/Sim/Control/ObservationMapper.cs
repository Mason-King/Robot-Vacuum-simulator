using RobotVacuum.Sim.Sensors;
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
    ///   footprint          → cells under the body can't be obstacles; dwell and cleanliness beliefs grow there
    ///
    /// SLAM off: observations are ASSIGNED, except that a preloaded wall is never cleared (see
    /// <see cref="InternalModelWriter.MarkPermanent"/>). SLAM on: they go through the weighted rule in <see cref="BeliefRules"/>.
    /// Each branch tests the SLAM bool, never the noise value.
    /// </summary>
    public sealed class ObservationMapper
    {
        readonly InternalModelWriter writer;
        readonly bool slam;
        readonly float cellSize;

        public ObservationMapper(InternalModelWriter writer, bool slam)
        {
            this.writer = writer;
            this.slam = slam;
            cellSize = writer.Grid.CellSizeMeters;
        }

        /// <summary>Applies everything in <paramref name="frame"/>, seen from <paramref name="pose"/>.</summary>
        public void Apply(SensorFrame frame, RobotPose pose)
        {
            if (frame.hasLidarScan) ApplyLidar(frame, pose);
            ApplyDrag(frame, pose);
            if (frame.bumped) ApplyBump(frame, pose); // last, so a certain 0 is never overwritten by this step's clear views
        }

        // ---- Lidar ---------------------------------------------------------------------------------------

        void ApplyLidar(SensorFrame frame, RobotPose pose)
        {
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

        void ObserveHit(int row, int col, float range, float maxRange)
        {
            if (!slam)
            {
                writer.SetTraversability(row, col, 0f);
                return;
            }

            float belief = writer.Traversability(row, col);
            writer.SetTraversability(row, col, BeliefRules.Step(belief, 0f, C.HitAlpha, range, maxRange, snapToZero: true));
        }

        void ObserveClear(int row, int col, float range, float maxRange)
        {
            float observed = writer.EaseAt(row, col);

            if (!slam)
            {
                if (!writer.IsPermanent(row, col)) writer.SetTraversability(row, col, observed);
                return;
            }

            float belief = writer.Traversability(row, col);
            writer.SetTraversability(row, col, BeliefRules.Step(belief, observed, C.ClearAlpha, range, maxRange, snapToZero: true));
        }

        // ---- Whisker -------------------------------------------------------------------------------------

        void ApplyBump(SensorFrame frame, RobotPose pose)
        {
            // Nudge a quarter cell past the contact point so rounding lands on the obstacle's side.
            Vector2 direction = RobotPose.DirectionOf(pose.heading + frame.bumpBearing);
            Vector2 contact = pose.position + direction * (frame.bumpDistance + cellSize * 0.25f);

            if (writer.Grid.TryWorldToGridIndex(contact, out int row, out int col))
                writer.SetTraversability(row, col, 0f); // certain: set directly in both modes
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

                    if (distanceSq <= solidSq && writer.Traversability(row, col) <= 0f && !writer.IsPermanent(row, col))
                        writer.SetTraversability(row, col, writer.EaseAt(row, col));

                    float resistance = Mathf.Max(0.01f, writer.Get(row, col).vacuumResistanceBelief);
                    writer.AddDwellAndCleanliness(row, col, dt, dt * vacuumPower / resistance);
                }
            }
        }
    }
}
