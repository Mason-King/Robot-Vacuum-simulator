using System.Collections.Generic;
using RobotVacuum.Level;
using UnityEngine;

namespace RobotVacuum.Sim.Control
{
    /// <summary>
    /// SLAM OFF ONLY: fills the Internal Model with everything static in the Level Object before the run starts
    /// (design doc, section 7). For every lattice cell inside the level's bounds:
    ///
    ///   inside a wall's thickness (open doorways excluded)  → traversability 0, marked permanent (observations
    ///                                                          in a Normal run never clear it; see
    ///                                                          InternalModelWriter.MarkPermanent)
    ///   in a CLOSED doorway                                  → traversability 0, like wall
    ///   inside blocking furniture (never the cat)            → traversability 0
    ///   pass-under furniture (tables, beds)                  → unchanged: passable
    ///   floor inside a room                                  → traversability = the floor's speed multiplier,
    ///                                                          vacuum resistance = its cleaning effort,
    ///                                                          floor-ease memory = its speed multiplier
    ///
    /// Cells outside every room are NOT marked blocked: they stay default, like any unknown cell. They are
    /// off-limits only because the walls leave no route to them (the view's reachability finds them unconnected);
    /// if a real route exists (an open door to outside, a room edge without a wall) they are reachable on purpose.
    ///
    /// The cat is never preloaded: it moves, and would make the preload wrong almost at once.
    /// </summary>
    public static class NormalPreload
    {
        /// <summary>The lowest traversability a floor can preload as: a floor type with speed multiplier 0 is slow, not a wall.</summary>
        public const float MinFloorEase = 0.01f;

        /// <param name="levelRenderer">Places the level in the world; null means the level sits at the world origin (tests).</param>
        /// <returns>The number of cells preloaded as impassable.</returns>
        public static int Run(InternalModelWriter writer, LevelData level, LevelRenderer levelRenderer)
        {
            if (writer == null || level == null) return 0;

            var grid = writer.Grid;
            float cell = grid.CellSizeMeters;

            // The level's bounds in world space, padded by one wall thickness so outer walls are fully covered.
            Rect local = level.Bounds();
            float pad = level.WallThickness;
            Vector2 a = ToWorld(levelRenderer, new Vector2(local.xMin - pad, local.yMin - pad));
            Vector2 b = ToWorld(levelRenderer, new Vector2(local.xMax + pad, local.yMax + pad));
            Vector2 worldMin = Vector2.Min(a, b);
            Vector2 worldMax = Vector2.Max(a, b);

            if (!grid.TryWorldToGridIndex(worldMin, out int minRow, out int minCol)) return 0;
            if (!grid.TryWorldToGridIndex(worldMax, out int maxRow, out int maxCol)) return 0;
            writer.Reserve(minRow, minCol, maxRow, maxCol);

            // Blocking furniture, with corners and bounds worked out once (Obstacle.Contains rebuilds its corners per call).
            var blockers = new List<(List<Vector2> corners, Rect bounds)>();
            foreach (var obstacle in level.Obstacles)
            {
                if (obstacle == null || !obstacle.blocksVacuum || obstacle.kind == ObstacleKind.Cat) continue;
                var corners = obstacle.Corners();
                blockers.Add((corners, Poly2D.Bounds(corners)));
            }

            int blocked = 0;
            for (int row = minRow; row <= maxRow; row++)
            {
                for (int col = minCol; col <= maxCol; col++)
                {
                    Vector2 point = ToLevel(levelRenderer, grid.GridIndexToWorldCenter(row, col));

                    if (LevelGeometry.IsInsideWall(level, point))
                    {
                        writer.SetTraversability(row, col, 0f);
                        writer.MarkPermanent(row, col);
                        blocked++;
                        continue;
                    }

                    if (IsInsideClosedDoorway(level, point) || IsInsideAny(blockers, point))
                    {
                        writer.SetTraversability(row, col, 0f);
                        blocked++;
                        continue;
                    }

                    var room = level.RoomAt(point);
                    if (room == null) continue; // outside every room: left unknown (default)

                    var floor = level.FloorTypeOf(room);
                    float ease = floor != null ? Mathf.Max(MinFloorEase, floor.speedMultiplier) : 1f;
                    float resistance = floor != null && floor.cleaningEffort > 0f ? floor.cleaningEffort : 1f;

                    writer.Preload(row, col, ease, resistance);
                    writer.SetEase(row, col, ease);
                }
            }

            // The preload is the starting map, not a change: an algorithm reads the whole map once at the start.
            writer.DiscardPendingChanges();
            return blocked;
        }

        /// <summary>
        /// Whether a doorway is open. Closed doors don't exist in the Level Object yet, so every doorway is open;
        /// when they do, this is the one place to read the door's state.
        /// </summary>
        public static bool IsDoorwayOpen(Doorway doorway) => true;

        /// <summary>True when the point lies in the wall gap of a doorway that is closed (the gap is then solid, like wall).</summary>
        public static bool IsInsideClosedDoorway(LevelData level, Vector2 point)
        {
            float halfThickness = level.WallThickness * 0.5f;
            float snap = level.DoorwaySnapDistance;

            foreach (var doorway in level.Doorways)
            {
                if (doorway == null || IsDoorwayOpen(doorway)) continue;
                if (Vector2.Distance(point, doorway.center) > doorway.width * 0.5f + level.WallThickness) continue;

                // The gap is the part of a wall line the doorway cuts: within half a wall thickness of the line,
                // and within half the doorway's width of the doorway's centre along it.
                foreach (var room in level.Rooms)
                {
                    if (room == null || !room.IsValid) continue;
                    for (int edge = 0; edge < room.EdgeCount; edge++)
                    {
                        if (InGap(point, doorway, room.EdgeStart(edge), room.EdgeEnd(edge), halfThickness, snap)) return true;
                    }
                }
                foreach (var stroke in level.WallStrokes)
                    if (stroke != null && InGap(point, doorway, stroke.a, stroke.b, halfThickness, snap)) return true;
            }
            return false;
        }

        static bool InGap(Vector2 point, Doorway doorway, Vector2 start, Vector2 end, float halfThickness, float snap)
        {
            float length = Vector2.Distance(start, end);
            if (length < 1e-4f) return false;
            if (Poly2D.DistanceToSegment(doorway.center, start, end, out float doorT) > snap) return false;
            if (Poly2D.DistanceToSegment(point, start, end, out float pointT) > halfThickness) return false;
            return Mathf.Abs(pointT - doorT) <= doorway.width * 0.5f / length;
        }

        static bool IsInsideAny(List<(List<Vector2> corners, Rect bounds)> blockers, Vector2 point)
        {
            foreach (var (corners, bounds) in blockers)
                if (bounds.Contains(point) && Poly2D.ContainsPoint(corners, point)) return true;
            return false;
        }

        static Vector2 ToWorld(LevelRenderer renderer, Vector2 levelPoint) =>
            renderer != null ? (Vector2)renderer.LevelToWorld(levelPoint) : levelPoint;

        static Vector2 ToLevel(LevelRenderer renderer, Vector2 worldPoint) =>
            renderer != null ? renderer.WorldToLevel(worldPoint) : worldPoint;
    }
}
