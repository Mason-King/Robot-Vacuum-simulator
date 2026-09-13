using UnityEngine;

namespace RobotVacuum.LevelEditor
{
    /// <summary>
    /// A small software rasteriser backing the level editor's 2D canvas.
    /// <para>
    /// IMGUI can only draw axis-aligned rectangles, and GL drawing is not clipped by the
    /// surrounding layout, so arbitrary polygons are rasterised into a texture and blitted
    /// with a single <c>GUI.DrawTexture</c>. That keeps rooms at any angle clipped to the
    /// canvas and free of the seams you get from stitching AA triangles together.
    /// </para>
    /// Coordinates are canvas pixels with the origin at the <em>top left</em> and y pointing
    /// down, matching <c>Event.current.mousePosition</c>; the flip into texture space is
    /// handled internally.
    /// </summary>
    public class CanvasRaster
    {
        public const int MaxDimension = 4096;

        Texture2D texture;
        Color32[] pixels;

        public int Width { get; private set; }
        public int Height { get; private set; }
        public Texture2D Texture => texture;

        /// <summary>Raw buffer, row 0 at the bottom. Exposed for drawing verification.</summary>
        public Color32[] Pixels => pixels;

        /// <summary>
        /// Allocates the pixel buffer only. Split from <see cref="Resize"/> so the drawing
        /// routines can be exercised without a live graphics device.
        /// </summary>
        public bool ResizeBuffer(int width, int height)
        {
            width = Mathf.Clamp(width, 1, MaxDimension);
            height = Mathf.Clamp(height, 1, MaxDimension);

            if (pixels != null && Width == width && Height == height) return false;

            Width = width;
            Height = height;
            pixels = new Color32[width * height];
            return true;
        }

        /// <summary>Reallocates if the size changed. Returns true when the buffer was replaced.</summary>
        public bool Resize(int width, int height)
        {
            bool bufferChanged = ResizeBuffer(width, height);
            if (texture != null && !bufferChanged) return false;

            if (texture != null) Object.DestroyImmediate(texture);

            texture = new Texture2D(Width, Height, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave,
            };
            return true;
        }

        public void Dispose()
        {
            if (texture != null) Object.DestroyImmediate(texture);
            texture = null;
            pixels = null;
            Width = Height = 0;
        }

        public void Apply()
        {
            if (texture == null) return;
            texture.SetPixels32(pixels);
            texture.Apply(false);
        }

        public void Clear(Color32 color)
        {
            if (pixels == null) return;

            // Fill one row, then block-copy it down the buffer.
            for (int x = 0; x < Width; x++) pixels[x] = color;
            for (int y = 1; y < Height; y++) System.Array.Copy(pixels, 0, pixels, y * Width, Width);
        }

        // ---------------------------------------------------------------- primitives

        /// <summary>Blends one pixel, flipping y so texture row 0 is the bottom of the canvas.</summary>
        public void BlendPixel(int x, int y, Color32 source)
        {
            if (pixels == null || x < 0 || y < 0 || x >= Width || y >= Height) return;
            if (source.a == 0) return;

            int index = (Height - 1 - y) * Width + x;

            if (source.a == 255)
            {
                pixels[index] = source;
                return;
            }

            var destination = pixels[index];
            int alpha = source.a;
            int inverse = 255 - alpha;

            pixels[index] = new Color32(
                (byte)((source.r * alpha + destination.r * inverse) / 255),
                (byte)((source.g * alpha + destination.g * inverse) / 255),
                (byte)((source.b * alpha + destination.b * inverse) / 255),
                255);
        }

        public void FillRect(float x, float y, float width, float height, Color32 color)
        {
            int minX = Mathf.Max(0, Mathf.FloorToInt(x));
            int minY = Mathf.Max(0, Mathf.FloorToInt(y));
            int maxX = Mathf.Min(Width - 1, Mathf.CeilToInt(x + width) - 1);
            int maxY = Mathf.Min(Height - 1, Mathf.CeilToInt(y + height) - 1);

            for (int py = minY; py <= maxY; py++)
                for (int px = minX; px <= maxX; px++)
                    BlendPixel(px, py, color);
        }

        /// <summary>Half-space triangle fill. Winding-agnostic, so room orientation does not matter.</summary>
        public void FillTriangle(Vector2 a, Vector2 b, Vector2 c, Color32 color)
        {
            int minX = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(a.x, Mathf.Min(b.x, c.x))));
            int maxX = Mathf.Min(Width - 1, Mathf.CeilToInt(Mathf.Max(a.x, Mathf.Max(b.x, c.x))));
            int minY = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(a.y, Mathf.Min(b.y, c.y))));
            int maxY = Mathf.Min(Height - 1, Mathf.CeilToInt(Mathf.Max(a.y, Mathf.Max(b.y, c.y))));
            if (minX > maxX || minY > maxY) return;

            for (int py = minY; py <= maxY; py++)
            {
                for (int px = minX; px <= maxX; px++)
                {
                    var p = new Vector2(px + 0.5f, py + 0.5f);

                    float w0 = Edge(a, b, p);
                    float w1 = Edge(b, c, p);
                    float w2 = Edge(c, a, p);

                    bool negative = w0 < 0f || w1 < 0f || w2 < 0f;
                    bool positive = w0 > 0f || w1 > 0f || w2 > 0f;
                    if (negative && positive) continue;

                    BlendPixel(px, py, color);
                }
            }
        }

        static float Edge(Vector2 a, Vector2 b, Vector2 p) =>
            (b.x - a.x) * (p.y - a.y) - (b.y - a.y) * (p.x - a.x);

        public void FillPolygon(Vector2[] points, int[] triangles, Color32 color)
        {
            if (points == null || triangles == null) return;

            for (int i = 0; i + 2 < triangles.Length; i += 3)
                FillTriangle(points[triangles[i]], points[triangles[i + 1]], points[triangles[i + 2]], color);
        }

        public void FillDisc(Vector2 center, float radius, Color32 color)
        {
            if (radius <= 0f) return;

            int minX = Mathf.Max(0, Mathf.FloorToInt(center.x - radius));
            int maxX = Mathf.Min(Width - 1, Mathf.CeilToInt(center.x + radius));
            int minY = Mathf.Max(0, Mathf.FloorToInt(center.y - radius));
            int maxY = Mathf.Min(Height - 1, Mathf.CeilToInt(center.y + radius));

            float radiusSq = radius * radius;

            for (int py = minY; py <= maxY; py++)
            {
                for (int px = minX; px <= maxX; px++)
                {
                    float dx = px + 0.5f - center.x;
                    float dy = py + 0.5f - center.y;
                    if (dx * dx + dy * dy <= radiusSq) BlendPixel(px, py, color);
                }
            }
        }

        /// <summary>
        /// A thick line drawn as a quad plus round caps, so chained segments meet cleanly at
        /// corners. Use opaque colours: the caps overlap the quad and would double-blend.
        /// </summary>
        public void DrawSegment(Vector2 a, Vector2 b, float thickness, Color32 color)
        {
            float half = Mathf.Max(0.5f, thickness * 0.5f);
            Vector2 delta = b - a;

            if (delta.sqrMagnitude < 1e-8f)
            {
                FillDisc(a, half, color);
                return;
            }

            Vector2 normal = new Vector2(-delta.y, delta.x).normalized * half;

            FillTriangle(a + normal, b + normal, b - normal, color);
            FillTriangle(a + normal, b - normal, a - normal, color);

            if (thickness > 2f)
            {
                FillDisc(a, half, color);
                FillDisc(b, half, color);
            }
        }

        public void DrawPolyline(Vector2[] points, float thickness, Color32 color, bool closed)
        {
            if (points == null || points.Length < 2) return;

            int last = closed ? points.Length : points.Length - 1;
            for (int i = 0; i < last; i++)
                DrawSegment(points[i], points[(i + 1) % points.Length], thickness, color);
        }

        public void DrawCircle(Vector2 center, float radius, float thickness, Color32 color, int segments = 40)
        {
            if (radius <= 0f) return;

            var previous = center + new Vector2(radius, 0f);
            for (int i = 1; i <= segments; i++)
            {
                float angle = i * Mathf.PI * 2f / segments;
                var next = center + new Vector2(Mathf.Cos(angle) * radius, Mathf.Sin(angle) * radius);
                DrawSegment(previous, next, thickness, color);
                previous = next;
            }
        }
    }
}
