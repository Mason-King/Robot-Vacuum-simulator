using System;
using System.Collections.Generic;
using RobotVacuum.Level;
using UnityEngine.UIElements;

namespace RobotVacuum.LevelEditor
{
    /// <summary>
    /// The start screen's Start simulation page: every saved floor plan as a card. Picking one runs it in
    /// the simulator with the current <see cref="SimConfig"/>; each card also shows how earlier runs went.
    /// </summary>
    public sealed class SimulateView : VisualElement
    {
        readonly FloorPalette palette;
        readonly ScrollView content;
        readonly Label subtitle;

        public event Action BackRequested;
        public event Action ConfigureRequested;

        /// <summary>A floor plan should be opened in the editor; null asks for the editor's library.</summary>
        public event Action<string> EditRequested;

        public event Action<LevelLibrary.Entry> RunRequested;

        /// <summary>Set up a batch experiment on this floor plan.</summary>
        public event Action<LevelLibrary.Entry> BatchRequested;

        public SimulateView(FloorPalette palette)
        {
            this.palette = palette;
            AddToClassList("le-screen");

            var header = Ui.PageHeader(this, "Start simulation", "Start screen", () => BackRequested?.Invoke(), out subtitle);
            var configure = Ui.Button(header, "Configure", IconKind.Sliders, () => ConfigureRequested?.Invoke(), "le-btn--ghost");
            Ui.Tooltip(configure, "Change the movement algorithm and vacuum settings");
            Ui.Button(header, "Level editor", IconKind.Pen, () => EditRequested?.Invoke(null), "le-btn--ghost");

            content = new ScrollView(ScrollViewMode.Vertical);
            content.AddToClassList("le-library__content");
            content.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            Add(content);

            Refresh();
        }

        public void Refresh()
        {
            content.Clear();
            subtitle.text = $"Runs with {SimConfig.Summary}";

            var entries = LevelLibrary.LoadAll();
            if (entries.Count == 0)
            {
                var empty = Ui.Div(content, "le-empty");
                var icon = new IconElement(IconKind.Logo);
                icon.AddToClassList("le-empty__icon");
                empty.Add(icon);
                Ui.Text(empty, "No floor plans to run", "le-empty__title");
                Ui.Text(empty, "Draw a floor plan in the level editor first, then come back here to run it.", "le-empty__text");
                Ui.Button(Ui.Div(empty, "le-empty__actions"), "Open level editor", IconKind.Pen,
                    () => EditRequested?.Invoke(null), "le-btn--primary");
                return;
            }

            var runsByLevel = new Dictionary<string, List<RunReport>>();
            foreach (var run in RunHistory.LoadAll())
            {
                if (string.IsNullOrEmpty(run.levelPath)) continue;
                if (!runsByLevel.TryGetValue(run.levelPath, out var list)) runsByLevel[run.levelPath] = list = new List<RunReport>();
                list.Add(run);
            }

            var grid = Ui.Div(content, "le-card-grid");
            foreach (var entry in entries)
            {
                runsByLevel.TryGetValue(entry.path, out var runs);
                grid.Add(BuildCard(entry, runs));
            }
        }

        VisualElement BuildCard(LevelLibrary.Entry entry, List<RunReport> runs)
        {
            var card = Ui.Div(null, "le-card");
            var thumb = new LevelThumbnail(entry.level, palette);
            card.Add(thumb);

            var play = Ui.Div(thumb, "le-card__play");
            play.Add(new IconElement(IconKind.Play));

            var body = Ui.Div(card, "le-card__body");
            var text = Ui.Div(body, "le-card__text");
            Ui.Text(text, entry.level.name, "le-card__title");

            int rooms = entry.level.ValidRoomCount;
            string roomText = rooms == 1 ? "1 room" : $"{rooms} rooms";
            Ui.Text(text, $"{roomText} · {Ui.FormatArea(entry.level.TotalFloorArea())}", "le-card__meta");
            Ui.Text(text, RunsSummary(runs), "le-card__meta");

            Ui.IconButton(body, IconKind.Chart, "Batch experiment: run several algorithms over many seeds",
                () => BatchRequested?.Invoke(entry), "le-btn--ghost le-btn--small le-card__menu");
            Ui.IconButton(body, IconKind.Pen, "Edit floor plan", () => EditRequested?.Invoke(entry.path),
                "le-btn--ghost le-btn--small le-card__menu");

            card.AddManipulator(new Clickable(() => RunRequested?.Invoke(entry)));
            return card;
        }

        static string RunsSummary(List<RunReport> runs)
        {
            if (runs == null || runs.Count == 0) return "Not run yet";

            float best = 0f;
            foreach (var run in runs) best = Math.Max(best, run.coveragePercent);
            return $"{(runs.Count == 1 ? "1 run" : $"{runs.Count} runs")} · best {best:0.0}% clean";
        }
    }
}
