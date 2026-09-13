using System.Collections.Generic;
using UnityEngine;

namespace RobotVacuum.Level
{
    /// <summary>
    /// Polygon helpers for arbitrary, non-axis-aligned room outlines: containment,
    /// ear-clipping triangulation, and the self-intersection check the editor uses to
    /// warn about shapes it cannot render.
    /// </summary>
    public static class Poly2D
    {
        /// <summary>Positive when the outline winds counter-clockwise.</summary>
        public static float SignedArea(IList<Vector2> points)
        {
            if (points == null || points.Count < 3) return 0f;

            float area = 0f;
            for (int i = 0, j = points.Count - 1; i < points.Count; j = i++)
                area += points[j].x * points[i].y - points[i].x * points[j].y;
            return area * 0.5f;
        }

        public static float Area(IList<Vector2> points) => Mathf.Abs(SignedArea(points));

        public static Vector2 Centroid(IList<Vector2> points)
        {
            if (points == null || points.Count == 0) return Vector2.zero;
            if (points.Count < 3) return Average(points);

            float area = SignedArea(points);
            if (Mathf.Abs(area) < 1e-6f) return Average(points);

            Vector2 sum = Vector2.zero;
            for (int i = 0, j = points.Count - 1; i < points.Count; j = i++)
            {
                float cross = points[j].x * points[i].y - points[i].x * points[j].y;
                sum += (points[j] + points[i]) * cross;
            }
            return sum / (6f * area);
        }

        static Vector2 Average(IList<Vector2> points)
        {
            Vector2 sum = Vector2.zero;
            for (int i = 0; i < points.Count; i++) sum += points[i];
            return sum / points.Count;
        }

        public static bool ContainsPoint(IList<Vector2> points, Vector2 p)
        {
            if (points == null || points.Count < 3) return false;

            bool inside = false;
            for (int i = 0, j = points.Count - 1; i < points.Count; j = i++)
            {
                if ((points[i].y > p.y) == (points[j].y > p.y)) continue;

                float dy = points[j].y - points[i].y;
                if (Mathf.Abs(dy) < 1e-9f) continue;

                float crossX = (points[j].x - points[i].x) * (p.y - points[i].y) / dy + points[i].x;
                if (p.x < crossX) inside = !inside;
            }
            return inside;
        }

        /// <summary>Distance from <paramref name="p"/> to segment ab, with the normalised position along it.</summary>
        public static float DistanceToSegment(Vector2 p, Vector2 a, Vector2 b, out float t)
        {
            Vector2 ab = b - a;
            float lengthSq = ab.sqrMagnitude;
            t = lengthSq < 1e-9f ? 0f : Mathf.Clamp01(Vector2.Dot(p - a, ab) / lengthSq);
            return Vector2.Distance(p, a + ab * t);
        }

        public static Rect Bounds(IList<Vector2> points)
        {
            if (points == null || points.Count == 0) return new Rect();

            Vector2 min = points[0], max = points[0];
            for (int i = 1; i < points.Count; i++)
            {
                min = Vector2.Min(min, points[i]);
                max = Vector2.Max(max, points[i]);
            }
            return new Rect(min, max - min);
        }

        /// <summary>
        /// Ear-clipping triangulation. Handles concave outlines; assumes the polygon does
        /// not self-intersect (see <see cref="IsSimple"/>). Output is counter-clockwise.
        /// </summary>
        public static int[] Triangulate(IList<Vector2> points)
        {
            int n = points?.Count ?? 0;
            if (n < 3) return System.Array.Empty<int>();

            // Work counter-clockwise so "convex" is a consistent test.
            var remaining = new List<int>(n);
            bool ccw = SignedArea(points) > 0f;
            for (int i = 0; i < n; i++) remaining.Add(ccw ? i : n - 1 - i);

            var triangles = new List<int>((n - 2) * 3);
            int guard = n * n;

            while (remaining.Count > 3 && guard-- > 0)
            {
                bool clipped = false;

                for (int i = 0; i < remaining.Count; i++)
                {
                    int count = remaining.Count;
                    int i0 = remaining[(i + count - 1) % count];
                    int i1 = remaining[i];
                    int i2 = remaining[(i + 1) % count];

                    Vector2 a = points[i0], b = points[i1], c = points[i2];
                    if (Cross(b - a, c - b) <= 0f) continue; // reflex corner, not an ear

                    bool blocked = false;
                    for (int k = 0; k < count; k++)
                    {
                        int index = remaining[k];
                        if (index == i0 || index == i1 || index == i2) continue;
                        if (PointInTriangle(points[index], a, b, c)) { blocked = true; break; }
                    }
                    if (blocked) continue;

                    triangles.Add(i0);
                    triangles.Add(i1);
                    triangles.Add(i2);
                    remaining.RemoveAt(i);
                    clipped = true;
                    break;
                }

                if (!clipped) break; // degenerate outline: fall through to a fan
            }

            if (remaining.Count == 3)
            {
                triangles.Add(remaining[0]);
                triangles.Add(remaining[1]);
                triangles.Add(remaining[2]);
            }
            else if (remaining.Count > 3)
            {
                for (int i = 1; i < remaining.Count - 1; i++)
                {
                    triangles.Add(remaining[0]);
                    triangles.Add(remaining[i]);
                    triangles.Add(remaining[i + 1]);
                }
            }

            return triangles.ToArray();
        }

        /// <summary>True when no two non-adjacent edges cross. Self-intersecting rooms triangulate badly.</summary>
        public static bool IsSimple(IList<Vector2> points)
        {
            int n = points?.Count ?? 0;
            if (n < 4) return n == 3;

            for (int i = 0; i < n; i++)
            {
                Vector2 a1 = points[i], a2 = points[(i + 1) % n];
                for (int j = i + 1; j < n; j++)
                {
                    // Skip shared-vertex neighbours, including the wrap-around pair.
                    if (j == i || (j + 1) % n == i || (i + 1) % n == j) continue;

                    Vector2 b1 = points[j], b2 = points[(j + 1) % n];
                    if (SegmentsIntersect(a1, a2, b1, b2)) return false;
                }
            }
            return true;
        }

        public static float Cross(Vector2 a, Vector2 b) => a.x * b.y - a.y * b.x;

        public static bool PointInTriangle(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
        {
            float d1 = Cross(b - a, p - a);
            float d2 = Cross(c - b, p - b);
            float d3 = Cross(a - c, p - c);

            bool anyNegative = d1 < 0f || d2 < 0f || d3 < 0f;
            bool anyPositive = d1 > 0f || d2 > 0f || d3 > 0f;
            return !(anyNegative && anyPositive);
        }

        static bool SegmentsIntersect(Vector2 a1, Vector2 a2, Vector2 b1, Vector2 b2)
        {
            float d1 = Cross(a2 - a1, b1 - a1);
            float d2 = Cross(a2 - a1, b2 - a1);
            float d3 = Cross(b2 - b1, a1 - b1);
            float d4 = Cross(b2 - b1, a2 - b1);

            return ((d1 > 0f) != (d2 > 0f)) && ((d3 > 0f) != (d4 > 0f));
        }
    }
}
