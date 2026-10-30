using System.Collections.Generic;
using RobotVacuum.Level;
using UnityEngine;
using UnityEngine.UIElements;

namespace RobotVacuum.LevelEditor
{
    /// <summary>
    /// A fitted drawing of a floor plan with an optional coverage heatmap over it and, for replays, the
    /// vacuum's path up to a moment and the vacuum itself. Used by the Analysis page for single-run
    /// replays and for batch-averaged heatmaps.
    /// </summary>
    public sealed class FloorMapView : VisualElement
    {
        const float Padding = 14f;

        // Sequential single hue: clean floor shows as the accent colour, dirty floor stays dark.
        static readonly Color Clean = new Color(0.357f, 0.553f, 0.937f, 0.92f);
        static readonly Color Dirty = new Color(0.937f, 0.353f, 0.404f, 0.55f);

        readonly LevelSnapshot level;
        readonly FloorPalette palette;
        readonly List<WallSegment> walls;
        readonly VisualElement heatmap;
        readonly VisualElement overlay;
        Texture2D texture;
        Rect mapRect;
        List<Vector2> path;
        int pathCount;
        Vector2 robot;
        float robotHeading;
        float robotRadius = 0.17f;
        bool showRobot;

        public FloorMapView(LevelSnapshot level, FloorPalette palette)
        {
            this.level = level;
            this.palette = palette;
            AddToClassList("le-floor-map");

            var scratch = ScriptableObject.CreateInstance<LevelData>();
            LevelSnapshot.Parse(level.ToJson(false))?.ApplyTo(scratch);
            walls = LevelGeometry.CollectWallSegments(scratch);
            Object.Destroy(scratch);

            heatmap = Ui.Div(this, "le-floor-map__heat");
            heatmap.pickingMode = PickingMode.Ignore;
            overlay = Ui.Div(this, "le-floor-map__overlay");
            overlay.pickingMode = PickingMode.Ignore;

            generateVisualContent += DrawFloor;
            overlay.generateVisualContent += DrawPath;
            RegisterCallback<GeometryChangedEvent>(_ => PlaceHeatmap());
            RegisterCallback<DetachFromPanelEvent>(_ => ReleaseTexture());
        }

        /// <summary>Shows a coverage map: cleanliness 0–1 per block, -1 for no floor, covering <paramref name="rect"/>.</summary>
        public void SetCoverage(float[] values, int width, int height, Rect rect)
        {
            if (values == null || width <= 0 || height <= 0 || values.Length != width * height)
            {
                Ui.SetVisible(heatmap, false);
                return;
            }

            if (texture == null || texture.width != width || texture.height != height)
            {
                ReleaseTexture();
                texture = new Texture2D(width, height, TextureFormat.RGBA32, false)
                {
                    filterMode = FilterMode.Point,
                    wrapMode = TextureWrapMode.Clamp,
                    hideFlags = HideFlags.DontSave,
                };
            }

            var pixels = new Color32[values.Length];
            for (int i = 0; i < values.Length; i++)
                pixels[i] = values[i] < 0f ? new Color32(0, 0, 0, 0) : (Color32)Color.Lerp(Dirty, Clean, values[i]);
            texture.SetPixels32(pixels);
            texture.Apply(false);

            mapRect = rect;
            heatmap.style.backgroundImage = new StyleBackground(texture);
            Ui.SetVisible(heatmap, true);
            PlaceHeatmap();
        }

        /// <summary>Shows the vacuum's path through its first <paramref name="count"/> points, and the vacuum at the last.</summary>
        public void SetPath(List<Vector2> points, int count, float heading, float radius)
        {
            path = points;
            pathCount = Mathf.Clamp(count, 0, points?.Count ?? 0);
            showRobot = pathCount > 0;
            if (showRobot) robot = points[pathCount - 1];
            robotHeading = heading;
            robotRadius = radius;
            overlay.MarkDirtyRepaint();
        }

        bool TryFit(out Rect bounds, out float scale, out Rect rect)
        {
            rect = contentRect;
            bounds = level.Bounds();
            scale = 0f;
            if (rect.width < 4f || rect.height < 4f || level.ValidRoomCount == 0) return false;

            scale = Mathf.Min(
                (rect.width - Padding * 2f) / Mathf.Max(bounds.width, 0.5f),
                (rect.height - Padding * 2f) / Mathf.Max(bounds.height, 0.5f));
            return scale > 0f;
        }

        Vector2 ToLocal(Vector2 p, Rect bounds, float scale, Rect rect) => new Vector2(
            rect.center.x + (p.x - bounds.center.x) * scale,
            rect.center.y - (p.y - bounds.center.y) * scale);

        void PlaceHeatmap()
        {
            if (texture == null || !TryFit(out var bounds, out float scale, out var rect)) return;

            // A texture's first row draws at the bottom, which is the map's lowest y: no flip needed.
            Vector2 topLeft = ToLocal(new Vector2(mapRect.xMin, mapRect.yMax), bounds, scale, rect);
            heatmap.style.left = topLeft.x;
            heatmap.style.top = topLeft.y;
            heatmap.style.width = mapRect.width * scale;
            heatmap.style.height = mapRect.height * scale;
            overlay.MarkDirtyRepaint();
        }

        void DrawFloor(MeshGenerationContext context)
        {
            if (!TryFit(out var bounds, out float scale, out var rect)) return;
            var painter = context.painter2D;
            painter.lineJoin = LineJoin.Round;

            foreach (var room in level.rooms)
            {
                if (room == null || !room.IsValid) continue;

                painter.BeginPath();
                painter.MoveTo(ToLocal(room.outline[0], bounds, scale, rect));
                for (int i = 1; i < room.outline.Count; i++) painter.LineTo(ToLocal(room.outline[i], bounds, scale, rect));
                painter.ClosePath();

                var floor = palette != null && palette.Get(room.floorIndex) != null ? palette.ColorOf(room.floorIndex) : Color.gray;
                floor = Color.Lerp(floor, new Color(0.08f, 0.09f, 0.11f), 0.55f);
                floor.a = 1f;
                painter.fillColor = floor;
                painter.Fill(FillRule.NonZero);
            }
        }

        void DrawPath(MeshGenerationContext context)
        {
            if (!TryFit(out var bounds, out float scale, out var rect)) return;
            var painter = context.painter2D;
            painter.lineJoin = LineJoin.Round;
            painter.lineCap = LineCap.Round;

            foreach (var obstacle in level.obstacles)
            {
                if (obstacle == null || obstacle.kind == ObstacleKind.Cat) continue;

                var corners = obstacle.Corners();
                painter.BeginPath();
                painter.MoveTo(ToLocal(corners[0], bounds, scale, rect));
                for (int i = 1; i < corners.Count; i++) painter.LineTo(ToLocal(corners[i], bounds, scale, rect));
                painter.ClosePath();
                var color = Obstacle.ColorOf(obstacle.kind);
                color.a = obstacle.blocksVacuum ? 0.9f : 0.35f;
                painter.fillColor = color;
                painter.Fill(FillRule.NonZero);
            }

            painter.lineWidth = Mathf.Clamp(level.wallThickness * scale, 1.5f, 4f);
            painter.strokeColor = new Color(0.93f, 0.94f, 0.96f);
            painter.BeginPath();
            foreach (var segment in walls)
            {
                painter.MoveTo(ToLocal(segment.a, bounds, scale, rect));
                painter.LineTo(ToLocal(segment.b, bounds, scale, rect));
            }
            painter.Stroke();

            if (path == null || pathCount < 2) return;

            painter.lineWidth = 1.5f;
            painter.strokeColor = new Color(1f, 1f, 1f, 0.55f);
            painter.BeginPath();
            painter.MoveTo(ToLocal(path[0], bounds, scale, rect));
            for (int i = 1; i < pathCount; i++) painter.LineTo(ToLocal(path[i], bounds, scale, rect));
            painter.Stroke();

            if (!showRobot) return;

            Vector2 centre = ToLocal(robot, bounds, scale, rect);
            float radius = Mathf.Max(4f, robotRadius * scale);
            painter.fillColor = new Color(0.93f, 0.94f, 0.96f);
            painter.BeginPath();
            painter.Arc(centre, radius, 0f, 360f);
            painter.Fill();

            float radians = robotHeading * Mathf.Deg2Rad;
            Vector2 nose = new Vector2(-Mathf.Sin(radians), -Mathf.Cos(radians)); // +y is up in level space, down in UI
            painter.lineWidth = 2f;
            painter.strokeColor = new Color(0.2f, 0.72f, 0.95f);
            painter.BeginPath();
            painter.MoveTo(centre);
            painter.LineTo(centre + nose * radius);
            painter.Stroke();
        }

        void ReleaseTexture()
        {
            if (texture == null) return;
            Object.Destroy(texture);
            texture = null;
        }
    }
}
