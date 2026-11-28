using System.Collections.Generic;
using RobotVacuum.Level;
using UnityEngine;

namespace RobotVacuum.LevelEditor
{
    /// <summary>
    /// What changed between two versions of a floor plan, as lines a person can read: rooms added, removed, moved or
    /// reshaped, floor coverings, walls, doorways, furniture, the vacuum's start. Rooms and furniture are matched by
    /// name (in order, for repeated names), doorways by the rooms they join.
    /// </summary>
    public static class LevelDiff
    {
        /// <summary>Positions closer than this, metres, count as the same.</summary>
        const float Same = 0.001f;

        public static List<string> Compare(LevelSnapshot before, LevelSnapshot after, FloorPalette palette = null)
        {
            var lines = new List<string>();
            if (before == null || after == null) return lines;

            if (before.name != after.name) lines.Add($"Renamed “{before.name}” → “{after.name}”");

            float areaBefore = before.TotalFloorArea(), areaAfter = after.TotalFloorArea();
            if (Mathf.Abs(areaBefore - areaAfter) > 0.005f)
                lines.Add($"Floor area {Ui.FormatArea(areaBefore)} → {Ui.FormatArea(areaAfter)}");

            CompareRooms(before, after, palette, lines);
            CompareDoorways(before, after, lines);
            CompareObstacles(before, after, lines);

            int strokesBefore = before.wallStrokes.Count, strokesAfter = after.wallStrokes.Count;
            if (strokesAfter > strokesBefore) lines.Add($"{Count(strokesAfter - strokesBefore, "free wall")} drawn");
            else if (strokesAfter < strokesBefore) lines.Add($"{Count(strokesBefore - strokesAfter, "free wall")} removed");
            else if (!SameStrokes(before.wallStrokes, after.wallStrokes)) lines.Add("Free walls moved");

            if (Vector2.Distance(before.robotSpawn, after.robotSpawn) > Same)
                lines.Add($"Vacuum start moved {Ui.FormatLength(Vector2.Distance(before.robotSpawn, after.robotSpawn))}");
            if (Mathf.Abs(before.wallThickness - after.wallThickness) > Same)
                lines.Add($"Wall thickness {Ui.FormatSmallLength(before.wallThickness)} → {Ui.FormatSmallLength(after.wallThickness)}");
            if (before.wallColor != after.wallColor) lines.Add("Wall colour changed");

            return lines;
        }

        // ---- rooms --------------------------------------------------------------------------------------------

        static void CompareRooms(LevelSnapshot before, LevelSnapshot after, FloorPalette palette, List<string> lines)
        {
            var pairs = Match(before.rooms, after.rooms, room => room?.name);
            foreach (var (a, b) in pairs)
            {
                if (a == null) { lines.Add($"Room “{b.name}” added ({Ui.FormatArea(b.IsValid ? b.Area : 0f)})"); continue; }
                if (b == null) { lines.Add($"Room “{a.name}” removed ({Ui.FormatArea(a.IsValid ? a.Area : 0f)})"); continue; }

                string shape = CompareOutline(a.outline, b.outline);
                if (shape != null)
                {
                    float areaA = a.IsValid ? a.Area : 0f, areaB = b.IsValid ? b.Area : 0f;
                    lines.Add(Mathf.Abs(areaA - areaB) > 0.005f
                        ? $"Room “{b.name}” {shape}, {Ui.FormatArea(areaA)} → {Ui.FormatArea(areaB)}"
                        : $"Room “{b.name}” {shape}");
                }
                if (a.floorIndex != b.floorIndex)
                    lines.Add($"Room “{b.name}” floor {FloorName(palette, a.floorIndex)} → {FloorName(palette, b.floorIndex)}");

                int wallsA = Walls(a), wallsB = Walls(b);
                if (wallsA != wallsB)
                    lines.Add(wallsB < wallsA
                        ? $"Room “{b.name}”: {Count(wallsA - wallsB, "wall")} removed"
                        : $"Room “{b.name}”: {Count(wallsB - wallsA, "wall")} added");
            }
        }

        /// <summary>Null when the outlines are the same; else "moved 0.50 m" (same shape, shifted) or "reshaped".</summary>
        static string CompareOutline(List<Vector2> a, List<Vector2> b)
        {
            a ??= new List<Vector2>();
            b ??= new List<Vector2>();
            if (a.Count == b.Count)
            {
                bool same = true;
                for (int i = 0; i < a.Count && same; i++) same = Vector2.Distance(a[i], b[i]) <= Same;
                if (same) return null;

                if (a.Count > 0)
                {
                    Vector2 shift = b[0] - a[0];
                    bool shifted = true;
                    for (int i = 1; i < a.Count && shifted; i++) shifted = Vector2.Distance(a[i] + shift, b[i]) <= Same;
                    if (shifted) return $"moved {Ui.FormatLength(shift.magnitude)}";
                }
            }
            return "reshaped";
        }

        static int Walls(Room room)
        {
            int walls = 0;
            for (int edge = 0; edge < room.EdgeCount; edge++)
                if (room.HasWall(edge)) walls++;
            return walls;
        }

        static string FloorName(FloorPalette palette, int index) =>
            palette != null ? palette.LabelOf(index) : $"#{index}";

        // ---- doorways -----------------------------------------------------------------------------------------

        static void CompareDoorways(LevelSnapshot before, LevelSnapshot after, List<string> lines)
        {
            string Key(LevelSnapshot level, Doorway d)
            {
                string a = RoomName(level, d.roomA), b = RoomName(level, d.roomB);
                return string.CompareOrdinal(a, b) <= 0 ? $"{a} ↔ {b}" : $"{b} ↔ {a}";
            }

            var pairs = Match(before.doorways, after.doorways, d => d != null ? Key(before.doorways.Contains(d) ? before : after, d) : null);
            foreach (var (a, b) in pairs)
            {
                if (a == null) { lines.Add($"Doorway {Key(after, b)} added"); continue; }
                if (b == null) { lines.Add($"Doorway {Key(before, a)} removed"); continue; }
                float moved = Vector2.Distance(a.center, b.center);
                if (moved > Same) lines.Add($"Doorway {Key(after, b)} moved {Ui.FormatLength(moved)}");
                if (Mathf.Abs(a.width - b.width) > Same)
                    lines.Add($"Doorway {Key(after, b)} width {Ui.FormatLength(a.width)} → {Ui.FormatLength(b.width)}");
            }
        }

        static string RoomName(LevelSnapshot level, int index) =>
            index >= 0 && index < level.rooms.Count && level.rooms[index] != null ? level.rooms[index].name : "outside";

        // ---- furniture ----------------------------------------------------------------------------------------

        static void CompareObstacles(LevelSnapshot before, LevelSnapshot after, List<string> lines)
        {
            var pairs = Match(before.obstacles, after.obstacles, o => o != null ? $"{o.kind}:{o.name}" : null);
            foreach (var (a, b) in pairs)
            {
                if (a == null) { lines.Add($"“{b.name}” added"); continue; }
                if (b == null) { lines.Add($"“{a.name}” removed"); continue; }
                float moved = Vector2.Distance(a.center, b.center);
                if (moved > Same) lines.Add($"“{b.name}” moved {Ui.FormatLength(moved)}");
                if (Vector2.Distance(a.size, b.size) > Same)
                    lines.Add($"“{b.name}” resized {Ui.FormatSize(a.size.x, a.size.y)} → {Ui.FormatSize(b.size.x, b.size.y)}");
                if (Mathf.Abs(Mathf.DeltaAngle(a.rotation, b.rotation)) > 0.05f) lines.Add($"“{b.name}” turned {a.rotation:0}° → {b.rotation:0}°");
                if (a.blocksVacuum != b.blocksVacuum)
                    lines.Add(b.blocksVacuum ? $"“{b.name}” now blocks the vacuum" : $"“{b.name}” now lets the vacuum pass under");
            }
        }

        static bool SameStrokes(List<WallStroke> a, List<WallStroke> b)
        {
            for (int i = 0; i < a.Count; i++)
                if (a[i] == null || b[i] == null || Vector2.Distance(a[i].a, b[i].a) > Same || Vector2.Distance(a[i].b, b[i].b) > Same)
                    return false;
            return true;
        }

        // ---- matching -----------------------------------------------------------------------------------------

        /// <summary>
        /// Pairs items of <paramref name="before"/> with items of <paramref name="after"/> that have the same key, in
        /// order (so the second "Room" pairs with the second "Room"). Unpaired items come back with null on the other
        /// side: (null, added) or (removed, null). Order: the after list, then what was removed.
        /// </summary>
        public static List<(T before, T after)> Match<T>(List<T> before, List<T> after, System.Func<T, string> key) where T : class
        {
            var pairs = new List<(T, T)>();
            var used = new bool[before.Count];
            foreach (var b in after)
            {
                if (b == null) continue;
                string k = key(b);
                int found = -1;
                for (int i = 0; i < before.Count && found < 0; i++)
                    if (!used[i] && before[i] != null && key(before[i]) == k) found = i;
                if (found >= 0)
                {
                    used[found] = true;
                    pairs.Add((before[found], b));
                }
                else pairs.Add((null, b));
            }
            for (int i = 0; i < before.Count; i++)
                if (!used[i] && before[i] != null) pairs.Add((before[i], null));
            return pairs;
        }

        static string Count(int n, string thing) => n == 1 ? $"1 {thing}" : $"{n} {thing}s";
    }
}
