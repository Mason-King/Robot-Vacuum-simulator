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
