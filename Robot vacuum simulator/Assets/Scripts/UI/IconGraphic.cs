using System.Collections.Generic;
using RobotVacuum.LevelEditor;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.UIElements;

namespace RobotVacuum.UI
{
    /// <summary>
    /// One of the app's vector icons as a uGUI graphic, tinted by its colour. It draws the same definitions
    /// as the UI Toolkit icons (<see cref="IconElement.Draw"/>), tessellated into a mesh, so it needs no
    /// image assets and stays crisp at any size.
    /// </summary>
    [RequireComponent(typeof(CanvasRenderer))]
    public sealed class IconGraphic : MaskableGraphic
    {
        [SerializeField] IconKind kind = IconKind.Logo;

        public IconKind Kind
        {
            get => kind;
            set
            {
                if (kind == value) return;
                kind = value;
                SetVerticesDirty();
            }
        }

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            var r = GetPixelAdjustedRect();
            if (r.width < 1f || r.height < 1f) return;

            float scale = Mathf.Min(r.width, r.height) / 24f;
            var origin = new Vector2(r.x + (r.width - 24f * scale) * 0.5f, r.yMax - (r.height - 24f * scale) * 0.5f);
            IconElement.Draw(kind, new MeshPen(vh, origin, scale, color));
        }

        /// <summary>
        /// Tessellates the icon commands: strokes become a quad per segment with a round dot at every point
        /// (round caps and joins), fills become triangle fans (every filled icon shape is convex).
        /// </summary>
        sealed class MeshPen : IIconPen
        {
            const int CircleSegments = 14;

            readonly VertexHelper vh;
            readonly Vector2 origin;
            readonly float scale;
            readonly Color32 color;
            readonly List<Vector2> points = new List<Vector2>();

            public MeshPen(VertexHelper vh, Vector2 origin, float scale, Color color)
            {
                this.vh = vh;
                this.origin = origin;
                this.scale = scale;
                this.color = color;
            }

            // Icon space runs y down from the top-left; uGUI runs y up.
            Vector2 P(float x, float y) => new Vector2(origin.x + x * scale, origin.y - y * scale);

            static Vector2 OnCircle(float x, float y, float radius, float degrees)
            {
                float a = degrees * Mathf.Deg2Rad;
                return new Vector2(x + Mathf.Cos(a) * radius, y + Mathf.Sin(a) * radius);
            }

            void Points(float[] xy)
            {
                points.Clear();
                for (int i = 0; i + 1 < xy.Length; i += 2) points.Add(P(xy[i], xy[i + 1]));
            }

            public void Line(float width, params float[] xy)
            {
                Points(xy);
                StrokePoints(width, false);
            }

            public void Closed(float width, params float[] xy)
            {
                Points(xy);
                StrokePoints(width, true);
            }

            public void Fill(params float[] xy)
            {
                Points(xy);
                FillPoints();
            }

            public void Dot(float x, float y, float radius) => Disc(P(x, y), radius * scale);

            public void Ring(float x, float y, float radius, float width)
            {
                points.Clear();
                for (int i = 0; i < 40; i++) points.Add(PointOnArc(x, y, radius, i * 9f));
                StrokePoints(width, true);
            }

            public void Arc(float x, float y, float radius, float from, float to, float width, ArcDirection direction)
            {
                // Clockwise in icon space (y down) means the angle increases.
                if (direction == ArcDirection.Clockwise) while (to < from) to += 360f;
                else while (to > from) to -= 360f;

                points.Clear();
                AddArc(x, y, radius, from, to);
                StrokePoints(width, false);
            }

            public void RoundRect(float x, float y, float w, float h, float r, float width)
            {
                points.Clear();
                AddArc(x + w - r, y + r, r, 270f, 360f);
                AddArc(x + w - r, y + h - r, r, 0f, 90f);
                AddArc(x + r, y + h - r, r, 90f, 180f);
                AddArc(x + r, y + r, r, 180f, 270f);
                StrokePoints(width, true);
            }

            public void MagnetPath()
            {
                points.Clear();
                points.Add(P(6f, 4.5f));
                AddArc(12f, 12f, 6f, 180f, 0f); // through the bottom of the U
                points.Add(P(18f, 4.5f));
                StrokePoints(1.8f, false);
            }

            Vector2 PointOnArc(float x, float y, float radius, float degrees)
            {
                var p = OnCircle(x, y, radius, degrees);
                return P(p.x, p.y);
            }

            void AddArc(float x, float y, float radius, float from, float to)
            {
                int steps = Mathf.Max(2, Mathf.CeilToInt(Mathf.Abs(to - from) / 12f));
                for (int i = 0; i <= steps; i++) points.Add(PointOnArc(x, y, radius, Mathf.Lerp(from, to, i / (float)steps)));
            }

            void StrokePoints(float width, bool closed)
            {
                float half = width * scale * 0.5f;
                int count = points.Count;
                if (count == 0) return;

                int segments = closed ? count : count - 1;
                for (int i = 0; i < segments; i++)
                {
                    Vector2 a = points[i];
                    Vector2 b = points[(i + 1) % count];
                    Vector2 along = b - a;
                    if (along.sqrMagnitude < 1e-6f) continue;

                    Vector2 normal = new Vector2(-along.y, along.x).normalized * half;
                    Quad(a + normal, b + normal, b - normal, a - normal);
                }

                foreach (var p in points) Disc(p, half);
            }

            void FillPoints()
            {
                if (points.Count < 3) return;
                int start = vh.currentVertCount;
                foreach (var p in points) Vertex(p);
                for (int i = 1; i + 1 < points.Count; i++) vh.AddTriangle(start, start + i, start + i + 1);
            }

            void Disc(Vector2 center, float radius)
            {
                if (radius <= 0f) return;
                int start = vh.currentVertCount;
                Vertex(center);
                for (int i = 0; i < CircleSegments; i++)
                {
                    float a = i * Mathf.PI * 2f / CircleSegments;
                    Vertex(center + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * radius);
                }
                for (int i = 0; i < CircleSegments; i++)
                    vh.AddTriangle(start, start + 1 + i, start + 1 + (i + 1) % CircleSegments);
            }

            void Quad(Vector2 a, Vector2 b, Vector2 c, Vector2 d)
            {
                int start = vh.currentVertCount;
                Vertex(a);
                Vertex(b);
                Vertex(c);
                Vertex(d);
                vh.AddTriangle(start, start + 1, start + 2);
                vh.AddTriangle(start, start + 2, start + 3);
            }

            void Vertex(Vector2 p) => vh.AddVert(new Vector3(p.x, p.y), color, Vector2.zero);
        }
    }
}
