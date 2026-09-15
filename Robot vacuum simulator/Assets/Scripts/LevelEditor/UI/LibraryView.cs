using System;
using System.Collections.Generic;
using RobotVacuum.Level;
using UnityEngine;
using UnityEngine.UIElements;

namespace RobotVacuum.LevelEditor
{
    /// <summary>The home screen: every saved floor plan as a card with a live thumbnail.</summary>
    public sealed class LibraryView : VisualElement
    {
        readonly FloorPalette palette;
        readonly ScrollView content;
        readonly Label subtitle;
        readonly List<LevelLibrary.Entry> entries = new List<LevelLibrary.Entry>();

        public LibraryView(FloorPalette palette)
        {
            this.palette = palette;
            AddToClassList("le-screen");
            AddToClassList("le-library");

            var header = Ui.Div(this, "le-topbar le-library__header");
            Ui.IconButton(header, IconKind.Back, "Start screen", () => HomeRequested?.Invoke(), "le-btn--ghost");
            var brand = Ui.Div(header, "le-brand");
            var mark = new IconElement(IconKind.Logo);
            mark.AddToClassList("le-brand__mark");
            brand.Add(mark);
            var titles = Ui.Div(brand, "le-library__title-block");
            Ui.Text(titles, "Floor plans", "le-library__title");
            subtitle = Ui.Text(titles, string.Empty, "le-library__subtitle");

            Ui.Div(header, "le-topbar__spacer");
            VisualElement newButton = null;
            newButton = Ui.Button(header, "New floor plan", IconKind.Plus, () => NewRequested?.Invoke(newButton), "le-btn--primary");

            content = new ScrollView(ScrollViewMode.Vertical);
            content.AddToClassList("le-library__content");
            content.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            Add(content);
        }

        public event Action<string> OpenRequested;

        /// <summary>The back button was pressed.</summary>
        public event Action HomeRequested;

        /// <summary>The "New floor plan" button was pressed; the element is the anchor for a menu.</summary>
        public event Action<VisualElement> NewRequested;

        /// <summary>A card's overflow button was pressed.</summary>
        public event Action<LevelLibrary.Entry, VisualElement> CardMenuRequested;

        public IReadOnlyList<LevelLibrary.Entry> Entries => entries;

        public void Refresh()
        {
            entries.Clear();
            entries.AddRange(LevelLibrary.LoadAll());
            content.Clear();

            subtitle.text = entries.Count == 1 ? "1 floor plan" : $"{entries.Count} floor plans";

            if (entries.Count == 0)
            {
                BuildEmptyState();
                return;
            }

            var grid = Ui.Div(content, "le-card-grid");
            foreach (var entry in entries) grid.Add(BuildCard(entry));
        }

        void BuildEmptyState()
        {
            var empty = Ui.Div(content, "le-empty");
            var icon = new IconElement(IconKind.Logo);
            icon.AddToClassList("le-empty__icon");
            empty.Add(icon);

            Ui.Text(empty, "No floor plans yet", "le-empty__title");
            Ui.Text(empty, "Draw rooms, cut doorways, then drop the vacuum in and watch it clean.", "le-empty__text");

            VisualElement start = null;
            start = Ui.Button(Ui.Div(empty, "le-empty__actions"), "Create a floor plan", IconKind.Plus,
                () => NewRequested?.Invoke(start), "le-btn--primary");
        }

        VisualElement BuildCard(LevelLibrary.Entry entry)
        {
            var card = Ui.Div(null, "le-card");
            card.Add(new LevelThumbnail(entry.level, palette));

            var body = Ui.Div(card, "le-card__body");
            var text = Ui.Div(body, "le-card__text");
            Ui.Text(text, entry.level.name, "le-card__title");

            int rooms = entry.level.ValidRoomCount;
            string roomText = rooms == 1 ? "1 room" : $"{rooms} rooms";
            Ui.Text(text, $"{roomText} · {Ui.FormatArea(entry.level.TotalFloorArea())} · {RelativeTime(entry.modified)}", "le-card__meta");

            VisualElement more = null;
            more = Ui.IconButton(body, IconKind.More, "More", () => CardMenuRequested?.Invoke(entry, more), "le-btn--ghost le-btn--small le-card__menu");

            card.AddManipulator(new Clickable(() => OpenRequested?.Invoke(entry.path)));
            return card;
        }

        static string RelativeTime(DateTime time)
        {
            var span = DateTime.Now - time;
            if (span.TotalMinutes < 1) return "just now";
            if (span.TotalHours < 1) return $"{(int)span.TotalMinutes} min ago";
            if (span.TotalDays < 1) return $"{(int)span.TotalHours} h ago";
            if (span.TotalDays < 2) return "yesterday";
            if (span.TotalDays < 7) return $"{(int)span.TotalDays} days ago";
            return time.ToString("d MMM yyyy");
        }
    }

    /// <summary>A static, fitted drawing of a floor plan for library cards.</summary>
    public sealed class LevelThumbnail : VisualElement
    {
        readonly LevelSnapshot level;
        readonly FloorPalette palette;
        readonly List<WallSegment> walls;

        public LevelThumbnail(LevelSnapshot level, FloorPalette palette)
        {
            this.level = level;
            this.palette = palette;
            AddToClassList("le-card__thumb");
            pickingMode = PickingMode.Ignore;

            // Wall runs need a LevelData; build them once rather than on every repaint.
            var scratch = ScriptableObject.CreateInstance<LevelData>();
            LevelSnapshot.Parse(level.ToJson(false))?.ApplyTo(scratch);
            walls = LevelGeometry.CollectWallSegments(scratch);
            UnityEngine.Object.Destroy(scratch);

            generateVisualContent += OnGenerateVisualContent;
        }

        void OnGenerateVisualContent(MeshGenerationContext context)
        {
            var rect = contentRect;
            if (rect.width < 4f || rect.height < 4f || level.ValidRoomCount == 0) return;

            var bounds = level.Bounds();
            const float padding = 18f;
            float scale = Mathf.Min(
                (rect.width - padding * 2f) / Mathf.Max(bounds.width, 0.5f),
                (rect.height - padding * 2f) / Mathf.Max(bounds.height, 0.5f));

            Vector2 ToLocal(Vector2 p) => new Vector2(
                rect.center.x + (p.x - bounds.center.x) * scale,
                rect.center.y - (p.y - bounds.center.y) * scale);

            var painter = context.painter2D;
            painter.lineJoin = LineJoin.Round;

            foreach (var room in level.rooms)
            {
                if (room == null || !room.IsValid) continue;

                painter.BeginPath();
                painter.MoveTo(ToLocal(room.outline[0]));
                for (int i = 1; i < room.outline.Count; i++) painter.LineTo(ToLocal(room.outline[i]));
                painter.ClosePath();

                var floor = palette != null && palette.Get(room.floorIndex) != null ? palette.ColorOf(room.floorIndex) : Color.gray;
                floor.a = 1f;
                painter.fillColor = floor;
                painter.Fill(FillRule.NonZero);
            }

            foreach (var obstacle in level.obstacles)
            {
                if (obstacle == null) continue;

                var corners = obstacle.Corners();
                painter.BeginPath();
                painter.MoveTo(ToLocal(corners[0]));
                for (int i = 1; i < corners.Count; i++) painter.LineTo(ToLocal(corners[i]));
                painter.ClosePath();

                var color = Obstacle.ColorOf(obstacle.kind);
                color.a = obstacle.blocksVacuum ? 1f : 0.45f;
                painter.fillColor = color;
                painter.Fill(FillRule.NonZero);
            }

            painter.lineWidth = Mathf.Clamp(level.wallThickness * scale, 1.5f, 4f);
            painter.lineCap = LineCap.Round;
            painter.strokeColor = new Color(0.93f, 0.94f, 0.96f);
            painter.BeginPath();
            foreach (var segment in walls)
            {
                painter.MoveTo(ToLocal(segment.a));
                painter.LineTo(ToLocal(segment.b));
            }
            painter.Stroke();
        }
    }
}
