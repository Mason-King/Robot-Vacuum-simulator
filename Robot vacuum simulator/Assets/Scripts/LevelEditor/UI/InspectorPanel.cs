using System;
using System.Collections.Generic;
using RobotVacuum.Level;
using UnityEngine;
using UnityEngine.UIElements;

namespace RobotVacuum.LevelEditor
{
    /// <summary>
    /// The right-hand properties panel. It shows the level overview when nothing is selected, and
    /// room or doorway properties otherwise. Committed edits rebuild it; live drags only refresh
    /// the numbers, so text fields and sliders never lose focus mid-gesture.
    /// </summary>
    public sealed class InspectorPanel : VisualElement
    {
        static readonly Color[] WallColors =
        {
            new Color(0.22f, 0.24f, 0.29f),
            new Color(0.93f, 0.93f, 0.91f),
            new Color(0.55f, 0.57f, 0.60f),
            new Color(0.36f, 0.27f, 0.22f),
            new Color(0.20f, 0.30f, 0.40f),
            new Color(0.60f, 0.35f, 0.30f),
        };

        readonly EditorSession session;
        readonly ScrollView scroll;
        readonly List<Action> liveRefreshers = new List<Action>();
        bool continuousEdit;

        public InspectorPanel(EditorSession session)
        {
            this.session = session;
            AddToClassList("le-inspector");

            scroll = new ScrollView(ScrollViewMode.Vertical);
            scroll.AddToClassList("le-inspector__scroll");
            scroll.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            Add(scroll);

            session.LevelChanged += OnLevelChanged;
            session.SelectionChanged += Rebuild;
            session.OptionsChanged += OnOptionsChanged;

            Rebuild();
        }

        /// <summary>A room or doorway row was clicked and should be framed on the canvas.</summary>
        public event Action<int> FrameRoomRequested;

        public void Unbind()
        {
            session.LevelChanged -= OnLevelChanged;
            session.SelectionChanged -= Rebuild;
            session.OptionsChanged -= OnOptionsChanged;
        }

        void OnLevelChanged(bool live)
        {
            if (live || continuousEdit || Ui.IsTyping(panel))
            {
                foreach (var refresh in liveRefreshers) refresh();
                return;
            }

            Rebuild();
        }

        void OnOptionsChanged()
        {
            if (!continuousEdit) Rebuild();
        }

        public void Rebuild()
        {
            float scrollOffset = scroll.scrollOffset.y;
            scroll.Clear();
            liveRefreshers.Clear();

            if (session.SelectedRoomData != null) BuildRoom(session.SelectedRoom, session.SelectedRoomData);
            else if (session.SelectedDoorwayData != null) BuildDoorway(session.SelectedDoorway, session.SelectedDoorwayData);
            else BuildLevel();

            scroll.schedule.Execute(() => scroll.scrollOffset = new Vector2(0f, scrollOffset));
        }

        // ---------------------------------------------------------------- level overview

        void BuildLevel()
        {
            var level = session.Level;
            var header = Ui.Div(scroll, "le-inspector__header");
            Ui.Text(header, "Floor plan", "le-inspector__eyebrow");
            Ui.Text(header, session.Name, "le-inspector__title");

            var stats = Ui.Div(scroll, "le-stats");
            var rooms = Ui.Stat(stats, "Rooms", string.Empty);
            var doors = Ui.Stat(stats, "Doorways", string.Empty);
            var area = Ui.Stat(stats, "Floor area", string.Empty);
            Live(() =>
            {
                rooms.text = level.Rooms.Count.ToString();
                doors.text = level.Doorways.Count.ToString();
                area.text = Ui.FormatArea(level.TotalFloorArea());
            });

            BuildChecks();
            BuildRoomList();
            BuildDoorwayList();

            Ui.Section(scroll, "New rooms", out var newRooms);
            BuildFloorPicker(newRooms, session.PaintFloor, index => session.PaintFloor = index);
            var walls = new SwitchToggle("Include walls", session.NewRoomsHaveWalls);
            walls.ValueChanged += value => session.NewRoomsHaveWalls = value;
            newRooms.Add(walls);

            Ui.Section(scroll, "Walls", out var wallBody);
            Ui.Text(wallBody, "Thickness", "le-field-label");
            var thickness = new ValueSlider(0.02f, 0.5f, level.WallThickness, v => $"{v * 100f:0} cm");
            BindContinuous(thickness, "Wall Thickness", session.SetWallThicknessLive);
            wallBody.Add(thickness);

            Ui.Text(wallBody, "Colour in the simulation", "le-field-label");
            var colors = Ui.Div(wallBody, "le-color-row");
            foreach (var color in WallColors)
            {
                var captured = color;
                var chip = Ui.Swatch(colors, color, "le-color-chip");
                if (ApproximatelyEqual(level.WallColor, color)) chip.AddToClassList("le-color-chip--selected");
                chip.AddManipulator(new Clickable(() => session.SetWallColor(captured)));
            }
        }

        void BuildChecks()
        {
            var issues = session.Validate();
            var card = Ui.Div(scroll, issues.Count == 0 ? "le-checks le-checks--ok" : "le-checks");

            if (issues.Count == 0)
            {
                var row = Ui.Div(card, "le-check");
                row.Add(new IconElement(IconKind.Check));
                Ui.Text(row, "Ready to run", "le-check__text");
                return;
            }

            foreach (var issue in issues)
            {
                var row = Ui.Div(card, issue.severity == IssueSeverity.Error ? "le-check le-check--error" : "le-check le-check--warning");
                row.Add(new IconElement(IconKind.Warning));
                Ui.Text(row, issue.message, "le-check__text");

                if (issue.roomIndex < 0) continue;

                int roomIndex = issue.roomIndex;
                row.AddToClassList("le-check--link");
                row.AddManipulator(new Clickable(() =>
                {
                    session.SelectRoom(roomIndex);
                    FrameRoomRequested?.Invoke(roomIndex);
                }));
            }
        }

        void BuildRoomList()
        {
            var level = session.Level;
            Ui.Section(scroll, $"Rooms · {level.Rooms.Count}", out var body);

            if (level.Rooms.Count == 0)
            {
                Ui.Text(body, "Draw one with the Rectangle (R) or Pen (P) tool.", "le-empty-hint");
                return;
            }

            for (int i = 0; i < level.Rooms.Count; i++)
            {
                var room = level.Rooms[i];
                if (room == null) continue;

                int index = i;
                var row = Ui.Div(body, "le-list-row");
                Ui.Swatch(row, level.ColorOf(room), "le-swatch le-swatch--round");
                Ui.Text(row, room.name, "le-list-row__label");
                Ui.Text(row, room.IsValid ? Ui.FormatArea(room.Area) : "—", "le-list-row__meta");
                Ui.IconButton(row, IconKind.Trash, "Delete room", () => session.DeleteRoom(index), "le-list-row__action");

                row.AddManipulator(new Clickable(() =>
                {
                    session.SelectRoom(index);
                    FrameRoomRequested?.Invoke(index);
                }));
            }
        }

        void BuildDoorwayList()
        {
            var level = session.Level;
            if (level.Doorways.Count == 0) return;

            Ui.Section(scroll, $"Doorways · {level.Doorways.Count}", out var body);

            for (int i = 0; i < level.Doorways.Count; i++)
            {
                var doorway = level.Doorways[i];
                if (doorway == null) continue;

                int index = i;
                var row = Ui.Div(body, "le-list-row");
                var icon = new IconElement(IconKind.Doorway);
                icon.AddToClassList("le-list-row__icon");
                row.Add(icon);
                Ui.Text(row, session.DoorwayTitle(doorway), "le-list-row__label");
                Ui.Text(row, $"{doorway.width:0.0} m", "le-list-row__meta");
                Ui.IconButton(row, IconKind.Trash, "Delete doorway", () => session.DeleteDoorway(index), "le-list-row__action");

                row.AddManipulator(new Clickable(() => session.SelectDoorway(index)));
            }
        }

        // ---------------------------------------------------------------- room

        void BuildRoom(int index, Room room)
        {
            var level = session.Level;

            var header = Ui.Div(scroll, "le-inspector__header le-inspector__header--row");
            var titleBlock = Ui.Div(header, "le-inspector__header-text");
            Ui.Text(titleBlock, "Room", "le-inspector__eyebrow");
            Ui.IconButton(header, IconKind.Close, "Deselect (Esc)", session.ClearSelection, "le-btn--ghost le-btn--small");

            var nameField = Ui.TextInput(scroll, room.name, value => session.RenameRoom(index, value), "le-input--title");
            nameField.RegisterCallback<KeyDownEvent>(evt =>
            {
                if (evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter || evt.keyCode == KeyCode.Escape)
                    nameField.Blur();
            });

            var stats = Ui.Div(scroll, "le-stats");
            var area = Ui.Stat(stats, "Area", string.Empty);
            var perimeter = Ui.Stat(stats, "Perimeter", string.Empty);
            var corners = Ui.Stat(stats, "Corners", string.Empty);
            Live(() =>
            {
                if (!room.IsValid) return;
                area.text = Ui.FormatArea(room.Area);
                perimeter.text = Ui.FormatMetres(Perimeter(room));
                corners.text = room.outline.Count.ToString();
            });

            Ui.Section(scroll, "Floor covering", out var floorBody);
            BuildFloorPicker(floorBody, room.floorIndex, floor => session.SetRoomFloor(index, floor));

            Ui.Section(scroll, "Walls", out var wallBody);
            int walls = session.WallCount(room);
            Ui.Text(wallBody, walls == room.EdgeCount
                ? $"All {walls} sides have walls."
                : $"{walls} of {room.EdgeCount} sides have walls.", "le-body-text");

            var wallButtons = Ui.Div(wallBody, "le-button-row");
            Ui.Button(wallButtons, "Add all", IconKind.WallsOn, () => session.SetAllWalls(index, true), "le-btn--ghost le-btn--grow")
                .SetEnabled(walls < room.EdgeCount);
            Ui.Button(wallButtons, "Remove all", IconKind.WallsOff, () => session.SetAllWalls(index, false), "le-btn--ghost le-btn--grow")
                .SetEnabled(walls > 0);
            Ui.Text(wallBody, "Use the Wall tool (W) to toggle a single side.", "le-empty-hint");

            var connections = new List<int>();
            for (int i = 0; i < level.Doorways.Count; i++)
                if (level.Doorways[i] != null && level.Doorways[i].Links(index)) connections.Add(i);

            if (connections.Count > 0)
            {
                Ui.Section(scroll, "Connects to", out var linkBody);
                foreach (int doorIndex in connections)
                {
                    int captured = doorIndex;
                    var doorway = level.Doorways[doorIndex];
                    var row = Ui.Div(linkBody, "le-list-row");
                    var icon = new IconElement(IconKind.Doorway);
                    icon.AddToClassList("le-list-row__icon");
                    row.Add(icon);
                    Ui.Text(row, session.NameOfRoom(doorway.Other(index)), "le-list-row__label");
                    Ui.Text(row, $"{doorway.width:0.0} m", "le-list-row__meta");
                    row.AddManipulator(new Clickable(() => session.SelectDoorway(captured)));
                }
            }

            var actions = Ui.Div(scroll, "le-button-row le-inspector__actions");
            Ui.Button(actions, "Duplicate", IconKind.Duplicate, () => session.DuplicateRoom(index), "le-btn--ghost le-btn--grow");
            Ui.Button(actions, "Delete", IconKind.Trash, () => session.DeleteRoom(index), "le-btn--danger-ghost le-btn--grow");
        }

        static float Perimeter(Room room)
        {
            float total = 0f;
            for (int e = 0; e < room.EdgeCount; e++) total += Vector2.Distance(room.EdgeStart(e), room.EdgeEnd(e));
            return total;
        }

        void BuildFloorPicker(VisualElement parent, int selected, Action<int> onPick)
        {
            var palette = session.Palette;
            if (palette == null || palette.Count == 0)
            {
                Ui.Text(parent, "No floor palette is assigned.", "le-empty-hint");
                return;
            }

            var grid = Ui.Div(parent, "le-floor-grid");
            for (int i = 0; i < palette.Count; i++)
            {
                int index = i;
                var floor = palette.Get(i);
                var card = Ui.Div(grid, i == selected ? "le-floor-card le-floor-card--selected" : "le-floor-card");
                Ui.Swatch(card, palette.ColorOf(i), "le-floor-card__swatch");

                var text = Ui.Div(card, "le-floor-card__text");
                Ui.Text(text, palette.LabelOf(i), "le-floor-card__name");
                if (floor != null) Ui.Text(text, $"{floor.speedMultiplier:0.##}× speed", "le-floor-card__meta");

                card.AddManipulator(new Clickable(() => onPick(index)));
            }
        }

        // ---------------------------------------------------------------- doorway

        void BuildDoorway(int index, Doorway doorway)
        {
            var header = Ui.Div(scroll, "le-inspector__header le-inspector__header--row");
            var titleBlock = Ui.Div(header, "le-inspector__header-text");
            Ui.Text(titleBlock, "Doorway", "le-inspector__eyebrow");
            var title = Ui.Text(titleBlock, session.DoorwayTitle(doorway), "le-inspector__title");
            Ui.IconButton(header, IconKind.Close, "Deselect (Esc)", session.ClearSelection, "le-btn--ghost le-btn--small");
            Live(() => title.text = session.DoorwayTitle(doorway));

            Ui.Section(scroll, "Width", out var body);
            var width = new ValueSlider(EditorSession.MinDoorwayWidth, EditorSession.MaxDoorwayWidth, doorway.width, v => $"{v:0.00} m");
            BindContinuous(width, "Doorway Width", value => session.SetDoorwayWidthLive(index, value));
            body.Add(width);
            Ui.Text(body, "Drag the doorway along any wall with the Select tool. It links whichever rooms it sits between.", "le-empty-hint");

            var actions = Ui.Div(scroll, "le-button-row le-inspector__actions");
            Ui.Button(actions, "Delete doorway", IconKind.Trash, () => session.DeleteDoorway(index), "le-btn--danger-ghost le-btn--grow");
        }

        // ---------------------------------------------------------------- helpers

        void Live(Action refresh)
        {
            refresh();
            liveRefreshers.Add(refresh);
        }

        /// <summary>One undo step per slider drag: checkpoint on grab, live updates, commit on release.</summary>
        void BindContinuous(ValueSlider slider, string label, Action<float> apply)
        {
            slider.DragStarted += () =>
            {
                continuousEdit = true;
                session.Checkpoint(label);
            };
            slider.ValueChanged += apply;
            slider.DragEnded += () =>
            {
                continuousEdit = false;
                session.Commit();
            };
        }

        static bool ApproximatelyEqual(Color a, Color b) =>
            Mathf.Abs(a.r - b.r) < 0.01f && Mathf.Abs(a.g - b.g) < 0.01f && Mathf.Abs(a.b - b.b) < 0.01f;
    }
}
