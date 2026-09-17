using System.Collections.Generic;
using UnityEngine;

namespace RobotVacuum.Level
{
    /// <summary>
    /// Line work for each <see cref="FloorPattern"/>, in level metres, clipped to a room outline. Lines
    /// are anchored to the level origin so a pattern runs on continuously across neighbouring rooms.
    /// The editor strokes these; the simulation draws the same patterns from <see cref="FloorTextures"/>.
    /// </summary>
    public static class FloorPatterns
    {
        public const float PlankWidth = 0.2f;
        public const float PlankLength = 1.2f;
        public const float TileSize = 0.4f;

        const float CarpetSpacing = 0.15f;
        const float ShagSpacing = 0.08f;
        const float RugSpacing = 0.2121f;    // matches the rug texture's diamond lattice
        const float HazardSpacing = 0.3536f; // matches the hazard texture's stripe period
        const int MaxLines = 2000;

        static readonly List<float> crossings = new List<float>();

        /// <summary>Gap between neighbouring lines, so callers can skip a pattern too dense to read.</summary>
        public static float Spacing(FloorPattern pattern) => pattern switch
        {
            FloorPattern.Planks => PlankWidth,
            FloorPattern.Tiles => TileSize,
            FloorPattern.Carpet => CarpetSpacing,
            FloorPattern.Shag => ShagSpacing,
            FloorPattern.Rug => RugSpacing,
            FloorPattern.Hazard => HazardSpacing,
            _ => 0f,
        };

        /// <summary>Appends the pattern's segments inside <paramref name="outline"/> as consecutive point pairs.</summary>
        public static void Collect(FloorPattern pattern, IList<Vector2> outline, List<Vector2> segments)
        {
            if (outline == null || outline.Count < 3) return;

            var bounds = Poly2D.Bounds(outline);
            switch (pattern)
            {
                case FloorPattern.Planks:
                    Hatch(outline, bounds, 0f, PlankWidth, segments);
                    PlankJoints(outline, bounds, segments);
                    break;

                case FloorPattern.Tiles:
                    Hatch(outline, bounds, 0f, TileSize, segments);
                    Hatch(outline, bounds, 90f, TileSize, segments);
                    break;

                case FloorPattern.Carpet:
                    Hatch(outline, bounds, 45f, CarpetSpacing, segments);
                    Hatch(outline, bounds, -45f, CarpetSpacing, segments);
                    break;

                case FloorPattern.Shag:
                    Hatch(outline, bounds, 60f, ShagSpacing, segments);
                    break;

                case FloorPattern.Rug:
                    Hatch(outline, bounds, 45f, RugSpacing, segments);
                    Hatch(outline, bounds, -45f, RugSpacing, segments);
                    break;

                case FloorPattern.Hazard:
                    Hatch(outline, bounds, -45f, HazardSpacing, segments);
                    break;
            }
        }

        /// <summary>Appends the parts of segment ab that lie inside the polygon, as point pairs.</summary>
        public static void ClipSegment(Vector2 a, Vector2 b, IList<Vector2> outline, List<Vector2> output)
        {
            Vector2 ab = b - a;
            crossings.Clear();
            crossings.Add(0f);
            crossings.Add(1f);

            int count = outline.Count;
            for (int i = 0; i < count; i++)
            {
                Vector2 p = outline[i];
                Vector2 pq = outline[(i + 1) % count] - p;

                float denominator = Poly2D.Cross(ab, pq);
                if (Mathf.Abs(denominator) < 1e-9f) continue;

                Vector2 ap = p - a;
                float t = Poly2D.Cross(ap, pq) / denominator;
                float u = Poly2D.Cross(ap, ab) / denominator;
                if (t > 0f && t < 1f && u >= 0f && u <= 1f) crossings.Add(t);
            }

            crossings.Sort();

            for (int i = 0; i + 1 < crossings.Count; i++)
            {
                float t0 = crossings[i], t1 = crossings[i + 1];
                if (t1 - t0 < 1e-6f) continue;
                if (!Poly2D.ContainsPoint(outline, a + ab * ((t0 + t1) * 0.5f))) continue;

                output.Add(a + ab * t0);
                output.Add(a + ab * t1);
            }
        }

        /// <summary>Parallel lines at <paramref name="angleDegrees"/>, <paramref name="spacing"/> apart.</summary>
        static void Hatch(IList<Vector2> outline, Rect bounds, float angleDegrees, float spacing, List<Vector2> output)
        {
            float radians = angleDegrees * Mathf.Deg2Rad;
            var along = new Vector2(Mathf.Cos(radians), Mathf.Sin(radians));
            var across = new Vector2(-along.y, along.x);

            float minAcross = float.MaxValue, maxAcross = float.MinValue;
            float minAlong = float.MaxValue, maxAlong = float.MinValue;
            foreach (var corner in new[] { bounds.min, bounds.max, new Vector2(bounds.xMin, bounds.yMax), new Vector2(bounds.xMax, bounds.yMin) })
            {
                minAcross = Mathf.Min(minAcross, Vector2.Dot(corner, across));
                maxAcross = Mathf.Max(maxAcross, Vector2.Dot(corner, across));
                minAlong = Mathf.Min(minAlong, Vector2.Dot(corner, along));
                maxAlong = Mathf.Max(maxAlong, Vector2.Dot(corner, along));
            }

            int first = Mathf.FloorToInt(minAcross / spacing);
            int last = Mathf.CeilToInt(maxAcross / spacing);
            if (last - first > MaxLines) return;

            for (int k = first; k <= last; k++)
            {
                Vector2 origin = across * (k * spacing);
                ClipSegment(origin + along * (minAlong - 1f), origin + along * (maxAlong + 1f), outline, output);
            }
        }

        /// <summary>The short end joints between planks, staggered by half a plank on alternate rows.</summary>
        static void PlankJoints(IList<Vector2> outline, Rect bounds, List<Vector2> output)
        {
            int firstRow = Mathf.FloorToInt(bounds.yMin / PlankWidth);
            int lastRow = Mathf.CeilToInt(bounds.yMax / PlankWidth);
            if (lastRow - firstRow > MaxLines) return;

            for (int row = firstRow; row < lastRow; row++)
            {
                float y0 = row * PlankWidth;
                float shift = (row & 1) == 0 ? 0f : PlankLength * 0.5f;

                int first = Mathf.FloorToInt((bounds.xMin - shift) / PlankLength);
                int last = Mathf.CeilToInt((bounds.xMax - shift) / PlankLength);

                for (int j = first; j <= last; j++)
                {
                    float x = j * PlankLength + shift;
                    ClipSegment(new Vector2(x, y0), new Vector2(x, y0 + PlankWidth), outline, output);
                }
            }
        }
    }
}
