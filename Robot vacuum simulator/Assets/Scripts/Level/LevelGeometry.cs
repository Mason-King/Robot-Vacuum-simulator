using System.Collections.Generic;
using UnityEngine;

namespace RobotVacuum.Level
{
    /// <summary>A run of solid wall along one room edge, after doorway gaps have been removed.</summary>
    public struct WallSegment
    {
        public Vector2 a;
        public Vector2 b;
        public int roomIndex;

        public float Length => Vector2.Distance(a, b);
    }

    /// <summary>
    /// Derives wall runs from a floor plan. Shared by the scene renderer and the editor
    /// canvas so both show exactly the same doorway gaps.
    /// </summary>
    public static class LevelGeometry
    {
        /// <summary>True when a point lies within the solid width of a wall, excluding doorway gaps.</summary>
        public static bool IsInsideWall(LevelData level, Vector2 point)
        {
            if (level == null) return false;

            float halfThickness = level.WallThickness * 0.5f;
            float snapDistance = level.DoorwaySnapDistance;

            for (int roomIndex = 0; roomIndex < level.Rooms.Count; roomIndex++)
            {
                var room = level.Rooms[roomIndex];
                if (room == null || !room.IsValid) continue;

                for (int edge = 0; edge < room.EdgeCount; edge++)
                {
                    if (!room.HasWall(edge)) continue;
                    if (InsideSolidWall(point, room.EdgeStart(edge), room.EdgeEnd(edge), halfThickness, snapDistance, level.Doorways))
                        return true;
                }
            }

            foreach (var stroke in level.WallStrokes)
                if (stroke != null && InsideSolidWall(point, stroke.a, stroke.b, halfThickness, snapDistance, level.Doorways))
                    return true;

            return false;
        }

        /// <summary>True when a polygon footprint overlaps any solid wall, including its thickness.</summary>
        public static bool FootprintOverlapsWall(LevelData level, IList<Vector2> footprint)
        {
            if (level == null || footprint == null || footprint.Count < 3) return false;

            float clearance = Mathf.Max(0f, level.WallThickness * 0.5f - 1e-4f);
            foreach (var wall in CollectWallSegments(level))
            {
                if (Poly2D.ContainsPoint(footprint, wall.a) || Poly2D.ContainsPoint(footprint, wall.b))
                    return true;

                for (int i = 0; i < footprint.Count; i++)
                {
                    Vector2 a = footprint[i];
                    Vector2 b = footprint[(i + 1) % footprint.Count];
                    if (SegmentsIntersect(a, b, wall.a, wall.b)) return true;
                    if (Poly2D.DistanceToSegment(wall.a, a, b, out _) < clearance ||
                        Poly2D.DistanceToSegment(wall.b, a, b, out _) < clearance ||
                        Poly2D.DistanceToSegment(a, wall.a, wall.b, out _) < clearance ||
                        Poly2D.DistanceToSegment(b, wall.a, wall.b, out _) < clearance)
                        return true;
                }
            }

            return false;
        }

        /// <summary>True when any furniture footprint overlaps a solid wall.</summary>
        public static bool HasFurnitureWallCollision(LevelData level)
        {
            if (level == null) return false;

            foreach (var obstacle in level.Obstacles)
                if (obstacle != null && FootprintOverlapsWall(level, obstacle.Corners()))
                    return true;

            return false;
        }

        static bool SegmentsIntersect(Vector2 a, Vector2 b, Vector2 c, Vector2 d)
        {
            const float tolerance = 1e-6f;
            float abC = Poly2D.Cross(b - a, c - a);
            float abD = Poly2D.Cross(b - a, d - a);
            float cdA = Poly2D.Cross(d - c, a - c);
            float cdB = Poly2D.Cross(d - c, b - c);

            if (((abC > tolerance && abD < -tolerance) || (abC < -tolerance && abD > tolerance)) &&
                ((cdA > tolerance && cdB < -tolerance) || (cdA < -tolerance && cdB > tolerance)))
                return true;

            return Mathf.Abs(abC) <= tolerance && IsOnSegment(a, b, c, tolerance) ||
                   Mathf.Abs(abD) <= tolerance && IsOnSegment(a, b, d, tolerance) ||
                   Mathf.Abs(cdA) <= tolerance && IsOnSegment(c, d, a, tolerance) ||
                   Mathf.Abs(cdB) <= tolerance && IsOnSegment(c, d, b, tolerance);
        }

        static bool IsOnSegment(Vector2 a, Vector2 b, Vector2 point, float tolerance) =>
            point.x >= Mathf.Min(a.x, b.x) - tolerance &&
            point.x <= Mathf.Max(a.x, b.x) + tolerance &&
            point.y >= Mathf.Min(a.y, b.y) - tolerance &&
            point.y <= Mathf.Max(a.y, b.y) + tolerance;

        static bool InsideSolidWall(
            Vector2 point, Vector2 start, Vector2 end, float halfThickness, float snapDistance, List<Doorway> doorways)
        {
            float length = Vector2.Distance(start, end);
            if (length < 1e-4f) return false;

            float distance = Poly2D.DistanceToSegment(point, start, end, out float pointT);
            if (distance > halfThickness) return false;

            foreach (var doorway in doorways)
            {
                if (doorway == null) continue;

                float doorwayDistance = Poly2D.DistanceToSegment(doorway.center, start, end, out float doorwayT);
                if (doorwayDistance > snapDistance) continue;

                float halfGap = doorway.width * 0.5f / length;
                if (Mathf.Abs(pointT - doorwayT) <= halfGap) return false;
            }

            return true;
        }

        /// <summary>
        /// Every room edge, split around any doorway sitting on it. A doorway near an edge
        /// shared by two rooms cuts both walls, which is what opens the connection.
        /// </summary>
        public static List<WallSegment> CollectWallSegments(LevelData level)
        {
            var segments = new List<WallSegment>();
            if (level == null) return segments;

            var gaps = new List<Vector2>();

            for (int roomIndex = 0; roomIndex < level.Rooms.Count; roomIndex++)
            {
                var room = level.Rooms[roomIndex];
                if (room == null || !room.IsValid) continue;

                int count = room.outline.Count;
                for (int e = 0; e < count; e++)
                {
                    // A deleted wall contributes nothing, but the floor edge stays put.
                    if (!room.HasWall(e)) continue;

                    CutLine(level, room.outline[e], room.outline[(e + 1) % count], roomIndex, gaps, segments);
                }
            }

            foreach (var stroke in level.WallStrokes)
                if (stroke != null) CutLine(level, stroke.a, stroke.b, -1, gaps, segments);

            return segments;
        }

        /// <summary>Emits the solid runs of one wall line after every nearby doorway has cut into it.</summary>
        static void CutLine(
            LevelData level, Vector2 p0, Vector2 p1, int roomIndex,
            List<Vector2> gapScratch, List<WallSegment> output)
        {
            float length = Vector2.Distance(p0, p1);
            if (length < 1e-4f) return;

            float snap = level.DoorwaySnapDistance;
            gapScratch.Clear();

            foreach (var doorway in level.Doorways)
            {
                if (doorway == null) continue;

                float distance = Poly2D.DistanceToSegment(doorway.center, p0, p1, out float t);
                if (distance > snap) continue;

                float halfSpan = doorway.width * 0.5f / length;
                gapScratch.Add(new Vector2(t - halfSpan, t + halfSpan));
            }

            foreach (var span in Complement(gapScratch))
            {
                output.Add(new WallSegment
                {
                    a = Vector2.LerpUnclamped(p0, p1, span.x),
                    b = Vector2.LerpUnclamped(p0, p1, span.y),
                    roomIndex = roomIndex,
                });
            }
        }

        /// <summary>Merges the gap spans and returns the parts of [0,1] that stay solid.</summary>
        public static List<Vector2> Complement(List<Vector2> gaps)
        {
            var solid = new List<Vector2>();

            if (gaps == null || gaps.Count == 0)
            {
                solid.Add(new Vector2(0f, 1f));
                return solid;
            }

            gaps.Sort((x, y) => x.x.CompareTo(y.x));

            float cursor = 0f;
            foreach (var gap in gaps)
            {
                float start = Mathf.Clamp01(gap.x);
                float end = Mathf.Clamp01(gap.y);
                if (end <= cursor) continue;

                if (start > cursor + 1e-4f) solid.Add(new Vector2(cursor, start));
                cursor = Mathf.Max(cursor, end);
            }

            if (cursor < 1f - 1e-4f) solid.Add(new Vector2(cursor, 1f));
            return solid;
        }
    }
}
