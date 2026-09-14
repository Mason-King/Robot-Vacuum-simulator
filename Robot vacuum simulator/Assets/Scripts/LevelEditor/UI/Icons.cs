using UnityEngine;
using UnityEngine.UIElements;

namespace RobotVacuum.LevelEditor
{
    public enum IconKind
    {
        Select, Rect, Pen, Wall, Doorway, Spawn,
        Undo, Redo, Play, Stop, Save, Check, Plus, Minus, Fit, Grid, Magnet, Points,
        Trash, Duplicate, Back, More, Warning, Close, ChevronDown, Logo, WallsOn, WallsOff,
    }

    /// <summary>
    /// A vector icon drawn with Painter2D on a 24-unit grid and tinted by the element's USS
    /// <c>color</c>, so the app needs no image assets and icons stay crisp at any UI scale.
    /// </summary>
    public sealed class IconElement : VisualElement
    {
        IconKind kind;

        public IconElement(IconKind kind)
        {
            this.kind = kind;
            AddToClassList("le-icon");
            pickingMode = PickingMode.Ignore;
            generateVisualContent += OnGenerateVisualContent;
        }

        public IconKind Kind
        {
            get => kind;
            set
            {
                if (kind == value) return;
                kind = value;
                MarkDirtyRepaint();
            }
        }

        /// <summary>
        /// Custom content is not regenerated when only an inherited colour changes (hover, checked),
        /// so anything that restyles an icon's parent calls this.
        /// </summary>
        public static void RepaintAll(VisualElement root) =>
            root?.Query<IconElement>().ForEach(icon => icon.MarkDirtyRepaint());

        void OnGenerateVisualContent(MeshGenerationContext context)
        {
            var rect = contentRect;
            if (rect.width < 1f || rect.height < 1f) return;

            float scale = Mathf.Min(rect.width, rect.height) / 24f;
            var origin = new Vector2(
                rect.x + (rect.width - 24f * scale) * 0.5f,
                rect.y + (rect.height - 24f * scale) * 0.5f);

            Draw(kind, new IconPen(context.painter2D, origin, scale, resolvedStyle.color));
        }

        static void Draw(IconKind kind, IconPen pen)
        {
            switch (kind)
            {
                case IconKind.Select:
                    pen.Closed(1.7f, 6f, 3.5f, 6f, 19f, 10f, 15.2f, 12.8f, 21f, 15.4f, 19.8f, 12.7f, 14.2f, 18.3f, 14f);
                    break;

                case IconKind.Rect:
                    pen.RoundRect(4.5f, 5.5f, 15f, 13f, 2f, 1.8f);
                    break;

                case IconKind.Pen:
                    pen.Closed(1.6f, 5f, 18f, 8f, 6f, 19f, 9f, 16f, 19f);
                    pen.Dot(5f, 18f, 2f);
                    pen.Dot(8f, 6f, 2f);
                    pen.Dot(19f, 9f, 2f);
                    pen.Dot(16f, 19f, 2f);
                    break;

                case IconKind.Wall:
                    pen.Line(3.4f, 5f, 19f, 19f, 5f);
                    break;

                case IconKind.Doorway:
                    pen.Line(1.8f, 3f, 20f, 7f, 20f);
                    pen.Line(1.8f, 18f, 20f, 21f, 20f);
                    pen.Line(1.8f, 7f, 20f, 7f, 9f);
                    pen.Arc(7f, 20f, 11f, -90f, 0f, 1.4f, ArcDirection.Clockwise);
                    break;

                case IconKind.Spawn:
                    pen.Ring(12f, 12f, 8f, 1.8f);
                    pen.Dot(12f, 7.6f, 2.1f);
                    break;

                case IconKind.Undo:
                    pen.Line(1.8f, 9f, 5f, 4f, 10f, 9f, 15f);
                    pen.Line(1.8f, 4f, 10f, 14f, 10f);
                    pen.Arc(14f, 15f, 5f, -90f, 90f, 1.8f, ArcDirection.Clockwise);
                    pen.Line(1.8f, 14f, 20f, 10f, 20f);
                    break;

                case IconKind.Redo:
                    pen.Line(1.8f, 15f, 5f, 20f, 10f, 15f, 15f);
                    pen.Line(1.8f, 20f, 10f, 10f, 10f);
                    pen.Arc(10f, 15f, 5f, 270f, 90f, 1.8f, ArcDirection.CounterClockwise);
                    pen.Line(1.8f, 10f, 20f, 14f, 20f);
                    break;

                case IconKind.Play:
                    pen.Fill(8f, 5f, 19f, 12f, 8f, 19f);
                    pen.Closed(1.6f, 8f, 5f, 19f, 12f, 8f, 19f);
                    break;

                case IconKind.Stop:
                    pen.Fill(7f, 7f, 17f, 7f, 17f, 17f, 7f, 17f);
                    pen.Closed(1.4f, 7f, 7f, 17f, 7f, 17f, 17f, 7f, 17f);
                    break;

                case IconKind.Save:
                    pen.RoundRect(4.5f, 4.5f, 15f, 15f, 2.5f, 1.7f);
                    pen.Line(1.7f, 8.5f, 4.5f, 8.5f, 9f, 15.5f, 9f, 15.5f, 4.5f);
                    pen.Ring(12f, 14.5f, 2f, 1.5f);
                    break;

                case IconKind.Check:
                    pen.Line(2f, 5f, 12.5f, 10f, 17.5f, 19f, 7f);
                    break;

                case IconKind.Plus:
                    pen.Line(1.8f, 12f, 5f, 12f, 19f);
                    pen.Line(1.8f, 5f, 12f, 19f, 12f);
                    break;

                case IconKind.Minus:
                    pen.Line(1.8f, 5f, 12f, 19f, 12f);
                    break;

                case IconKind.Fit:
                    pen.Line(1.8f, 4f, 9f, 4f, 4f, 9f, 4f);
                    pen.Line(1.8f, 15f, 4f, 20f, 4f, 20f, 9f);
                    pen.Line(1.8f, 20f, 15f, 20f, 20f, 15f, 20f);
                    pen.Line(1.8f, 9f, 20f, 4f, 20f, 4f, 15f);
                    break;

                case IconKind.Grid:
                    pen.RoundRect(4f, 4f, 16f, 16f, 2f, 1.6f);
                    pen.Line(1.3f, 9.33f, 4f, 9.33f, 20f);
                    pen.Line(1.3f, 14.67f, 4f, 14.67f, 20f);
                    pen.Line(1.3f, 4f, 9.33f, 20f, 9.33f);
                    pen.Line(1.3f, 4f, 14.67f, 20f, 14.67f);
                    break;

                case IconKind.Magnet:
                    pen.MagnetPath();
                    pen.Line(1.8f, 3.5f, 8.5f, 8.5f, 8.5f);
                    pen.Line(1.8f, 15.5f, 8.5f, 20.5f, 8.5f);
                    break;

                case IconKind.Points:
                    pen.Closed(1.3f, 6f, 18f, 12f, 6f, 18f, 18f);
                    pen.Dot(6f, 18f, 2.3f);
                    pen.Dot(12f, 6f, 2.3f);
                    pen.Dot(18f, 18f, 2.3f);
                    break;

                case IconKind.Trash:
                    pen.Line(1.8f, 4f, 7f, 20f, 7f);
                    pen.Line(1.8f, 9.5f, 7f, 9.5f, 4.5f, 14.5f, 4.5f, 14.5f, 7f);
                    pen.Line(1.8f, 6.5f, 7f, 7.5f, 20f, 16.5f, 20f, 17.5f, 7f);
                    break;

                case IconKind.Duplicate:
                    pen.RoundRect(8.5f, 8.5f, 11.5f, 11.5f, 2f, 1.6f);
                    pen.Line(1.6f, 15.5f, 5.5f, 15.5f, 4f, 4f, 4f, 4f, 15.5f, 5.5f, 15.5f);
                    break;

                case IconKind.Back:
                    pen.Line(2f, 15f, 5f, 8f, 12f, 15f, 19f);
                    break;

                case IconKind.More:
                    pen.Dot(6f, 12f, 1.8f);
                    pen.Dot(12f, 12f, 1.8f);
                    pen.Dot(18f, 12f, 1.8f);
                    break;

                case IconKind.Warning:
                    pen.Closed(1.7f, 12f, 4f, 21f, 19.5f, 3f, 19.5f);
                    pen.Line(1.8f, 12f, 10f, 12f, 14f);
                    pen.Dot(12f, 17f, 1.1f);
                    break;

                case IconKind.Close:
                    pen.Line(1.8f, 6.5f, 6.5f, 17.5f, 17.5f);
                    pen.Line(1.8f, 17.5f, 6.5f, 6.5f, 17.5f);
                    break;

                case IconKind.ChevronDown:
                    pen.Line(1.8f, 7f, 10f, 12f, 15f, 17f, 10f);
                    break;

                case IconKind.Logo:
                    pen.Ring(12f, 12f, 9f, 2.2f);
                    pen.Arc(12f, 12f, 5.5f, 200f, 340f, 1.8f, ArcDirection.Clockwise);
                    pen.Dot(12f, 13.5f, 2.4f);
                    break;

                case IconKind.WallsOn:
                    pen.RoundRect(4.5f, 4.5f, 15f, 15f, 1.5f, 2.6f);
                    break;

                case IconKind.WallsOff:
                    pen.Line(1.6f, 4.5f, 8.5f, 4.5f, 4.5f, 8.5f, 4.5f);
                    pen.Line(1.6f, 15.5f, 4.5f, 19.5f, 4.5f, 19.5f, 8.5f);
                    pen.Line(1.6f, 19.5f, 15.5f, 19.5f, 19.5f, 15.5f, 19.5f);
                    pen.Line(1.6f, 8.5f, 19.5f, 4.5f, 19.5f, 4.5f, 15.5f);
                    break;
            }
        }

        readonly struct IconPen
        {
            readonly Painter2D painter;
            readonly Vector2 origin;
            readonly float scale;
            readonly Color color;

            public IconPen(Painter2D painter, Vector2 origin, float scale, Color color)
            {
                this.painter = painter;
                this.origin = origin;
                this.scale = scale;
                this.color = color;
            }

            Vector2 P(float x, float y) => origin + new Vector2(x, y) * scale;

            void BeginStroke(float width)
            {
                painter.lineWidth = width * scale;
                painter.strokeColor = color;
                painter.lineCap = LineCap.Round;
                painter.lineJoin = LineJoin.Round;
                painter.BeginPath();
            }

            void Path(float[] xy)
            {
                painter.MoveTo(P(xy[0], xy[1]));
                for (int i = 2; i + 1 < xy.Length; i += 2) painter.LineTo(P(xy[i], xy[i + 1]));
            }

            public void Line(float width, params float[] xy)
            {
                BeginStroke(width);
                Path(xy);
                painter.Stroke();
            }

            public void Closed(float width, params float[] xy)
            {
                BeginStroke(width);
                Path(xy);
                painter.ClosePath();
                painter.Stroke();
            }

            public void Fill(params float[] xy)
            {
                painter.fillColor = color;
                painter.BeginPath();
                Path(xy);
                painter.ClosePath();
                painter.Fill(FillRule.NonZero);
            }

            public void Dot(float x, float y, float radius)
            {
                painter.fillColor = color;
                painter.BeginPath();
                painter.Arc(P(x, y), radius * scale, Angle.Degrees(0f), Angle.Degrees(360f), ArcDirection.Clockwise);
                painter.ClosePath();
                painter.Fill(FillRule.NonZero);
            }

            public void Ring(float x, float y, float radius, float width)
            {
                BeginStroke(width);
                painter.Arc(P(x, y), radius * scale, Angle.Degrees(0f), Angle.Degrees(360f), ArcDirection.Clockwise);
                painter.ClosePath();
                painter.Stroke();
            }

            public void Arc(float x, float y, float radius, float from, float to, float width, ArcDirection direction)
            {
                BeginStroke(width);
                painter.Arc(P(x, y), radius * scale, Angle.Degrees(from), Angle.Degrees(to), direction);
                painter.Stroke();
            }

            public void RoundRect(float x, float y, float w, float h, float r, float width)
            {
                float radius = r * scale;
                BeginStroke(width);
                painter.MoveTo(P(x + r, y));
                painter.ArcTo(P(x + w, y), P(x + w, y + h), radius);
                painter.ArcTo(P(x + w, y + h), P(x, y + h), radius);
                painter.ArcTo(P(x, y + h), P(x, y), radius);
                painter.ArcTo(P(x, y), P(x + w, y), radius);
                painter.ClosePath();
                painter.Stroke();
            }

            public void MagnetPath()
            {
                BeginStroke(1.8f);
                painter.MoveTo(P(6f, 4.5f));
                painter.LineTo(P(6f, 12f));
                painter.Arc(P(12f, 12f), 6f * scale, Angle.Degrees(180f), Angle.Degrees(0f), ArcDirection.CounterClockwise);
                painter.LineTo(P(18f, 4.5f));
                painter.Stroke();
            }
        }
    }
}
