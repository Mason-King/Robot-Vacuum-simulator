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

    /// <summary>A stretch of room edge with no wall and no floor beyond it: a way out into empty space.</summary>
    public struct WallOpening
    {
        public int roomIndex;
        public int edge;
        public Vector2 a;
        public Vector2 b;

        public float Width => Vector2.Distance(a, b);
        public Vector2 Center => (a + b) * 0.5f;
    }

    /// <summary>
    /// Derives wall runs from a floor plan. Shared by the scene renderer and the editor
    /// canvas so both show exactly the same doorway gaps.
    /// </summary>
    public static class LevelGeometry
    {
        /// <summary>The stock vacuum's diameter. Narrower gaps can't let it through.</summary>
        public const float VacuumWidth = 0.34f;

        const float OpeningSampleStep = 0.04f;
        // How far past an unwalled edge to look for floor. Several distances, so a doorway between rooms
        // drawn a few centimetres apart still finds the neighbouring room instead of the sliver between.
        static readonly float[] OpeningProbeDistances = { 0.08f, 0.2f, 0.35f };

        /// <summary>
        /// Every stretch of room edge at least <paramref name="minWidth"/> long where the wall is missing (deleted,
        /// or cut by a doorway) and nothing but empty space lies beyond: no other room's floor and no wall.
        /// The vacuum can drive out through these and keep going forever.
        /// </summary>
        public static List<WallOpening> FindOpenings(LevelData level, float minWidth = VacuumWidth)
        {
            var openings = new List<WallOpening>();
            if (level == null) return openings;

            for (int roomIndex = 0; roomIndex < level.Rooms.Count; roomIndex++)
            {
                var room = level.Rooms[roomIndex];
                if (room == null || !room.IsValid) continue;

                for (int edge = 0; edge < room.EdgeCount; edge++)
                {
                    Vector2 start = room.EdgeStart(edge);
                    Vector2 end = room.EdgeEnd(edge);
                    float length = Vector2.Distance(start, end);
                    if (length < minWidth) continue;

                    Vector2 normal = new Vector2(end.y - start.y, start.x - end.x) / length;
                    int samples = Mathf.CeilToInt(length / OpeningSampleStep);
                    int runStart = -1;

                    for (int i = 0; i <= samples + 1; i++)
                    {
                        bool open = i <= samples && LeaksAt(level, room, Vector2.Lerp(start, end, (float)i / samples), normal);
                        if (open && runStart < 0) runStart = i;
                        if (open || runStart < 0) continue;

                        // The run covers samples runStart..i-1; give it half a step either side.
                        float t0 = Mathf.Clamp01((runStart - 0.5f) / samples);
                        float t1 = Mathf.Clamp01((i - 0.5f) / samples);
                        if ((t1 - t0) * length >= minWidth)
                            openings.Add(new WallOpening
                            {
                                roomIndex = roomIndex,
                                edge = edge,
                                a = Vector2.Lerp(start, end, t0),
                                b = Vector2.Lerp(start, end, t1),
                            });
                        runStart = -1;
                    }
                }
            }

            return openings;
        }

        /// <summary>True when <paramref name="point"/> is past every room's floor and every wall: the vacuum has escaped.</summary>
        public static bool IsOffFloor(LevelData level, Vector2 point) =>
            level != null && level.RoomIndexAt(point) < 0 && !IsInsideWall(level, point);

        /// <summary>Whether the edge point at <paramref name="point"/> is unwalled with nothing on its far side.</summary>
        static bool LeaksAt(LevelData level, Room room, Vector2 point, Vector2 normal)
        {
            if (IsInsideWall(level, point)) return false;

            float first = OpeningProbeDistances[0];
            bool positiveInside = room.Contains(point + normal * first);

            // Both or neither inside happens right at a sharp corner; the neighbouring samples decide.
            if (positiveInside == room.Contains(point - normal * first)) return false;

            Vector2 outward = positiveInside ? -normal : normal;
            foreach (float distance in OpeningProbeDistances)
                if (!IsOffFloor(level, point + outward * distance)) return false;
            return true;
        }

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
