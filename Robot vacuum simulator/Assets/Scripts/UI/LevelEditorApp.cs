using System;
using System.Collections.Generic;
using System.IO;
using RobotVacuum.Level;
using RobotVacuum.Sim;
using UnityEngine;
using UnityEngine.UIElements;

namespace RobotVacuum.LevelEditor
{
    /// <summary>
    /// The in-game level editor. Drop this on an empty GameObject and press Play: it builds its own
    /// UI Toolkit panel, shows the floor-plan library, and lets the player draw, save and run levels.
    /// Nothing here depends on the Unity Editor, so it ships in player builds as-is.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class LevelEditorApp : MonoBehaviour
    {
        [Tooltip("Floor coverings rooms can use. Left empty, the built-in six-covering palette is used.")]
        [SerializeField] FloorPalette palette;

        [Tooltip("Optional level assets offered as starting points under New floor plan.")]
        [SerializeField] LevelData[] templates = Array.Empty<LevelData>();

        [Tooltip("Multiplies the UI size on top of the screen's DPI scaling.")]
        [SerializeField, Range(0.6f, 2f)] float uiScale = 1f;

        static readonly (EditorTool tool, IconKind icon, string name, string key)[] Tools =
        {
            (EditorTool.Select, IconKind.Select, "Select", "V"),
            (EditorTool.Rect, IconKind.Rect, "Rectangle room", "R"),
            (EditorTool.Pen, IconKind.Pen, "Pen room", "P"),
            (EditorTool.Wall, IconKind.Wall, "Walls", "W"),
            (EditorTool.Doorway, IconKind.Doorway, "Doorway", "D"),
            (EditorTool.Spawn, IconKind.Spawn, "Vacuum start", "S"),
            (EditorTool.Obstacle, IconKind.Obstacle, "Furniture", "O"),
        };

        VisualElement root;
        PanelSettings panelSettings;
        FloorPalette runtimePalette;
        VisualElement app;
        OverlayLayer overlay;
        LibraryView library;

        // Editor screen, rebuilt per session.
        EditorSession session;
        VisualElement editorScreen;
        FloorPlanCanvas canvas;
        InspectorPanel inspector;
        TextField nameField;
        VisualElement statusChip;
        Label statusLabel;
        VisualElement issuesChip;
        VisualElement undoButton;
        VisualElement redoButton;
        Label hintLabel;
        Label coordsLabel;
        Label zoomLabel;
        VisualElement gridToggle;
        VisualElement snapToggle;
        VisualElement pointsToggle;
        Label snapLabel;
        readonly Dictionary<EditorTool, VisualElement> toolButtons = new Dictionary<EditorTool, VisualElement>();

        LevelRun run;
        HeadlessSimulation fastRun;
        Texture2D fastRunImage;
        int fastRunSeed;
        int fastRunMinutesIndex = 1;
        bool closingFastRun;

        // Captured from the first run's robot, then applied to every later one.
        VacuumSettings vacuumSettings;

        FloorPalette Palette => palette != null ? palette : runtimePalette;

        // ---------------------------------------------------------------- lifecycle

        void Start()
        {
            if (palette == null) runtimePalette = FloorPalette.CreateDefault();

            CreatePanel();

            // Coming back from the simulator reopens the floor plan that was playing.
            string returning = SimLauncher.TakeReturnPath();
            if (!string.IsNullOrEmpty(returning) && File.Exists(returning)) OpenLevel(returning);
            else ShowLibrary();
        }

        void OnDestroy()
        {
            run?.Stop();
            CloseSession();

            if (runtimePalette != null)
            {
                foreach (var floor in runtimePalette.Entries) Destroy(floor);
                Destroy(runtimePalette);
            }

            if (panelSettings != null) Destroy(panelSettings);
        }

        void OnApplicationQuit()
        {
            // Losing work to a closed window is worse than an unexpected save.
            if (session != null && session.IsDirty) TrySave(false);
        }

        void CreatePanel()
        {
            root = UiPanel.Create(transform, "Level Editor UI", uiScale, out panelSettings);

            app = Ui.Div(root, "le-app");
            overlay = new OverlayLayer();

            library = new LibraryView(Palette);
            library.OpenRequested += OpenLevel;
            library.NewRequested += ShowNewLevelMenu;
            library.CardMenuRequested += ShowCardMenu;
            library.HomeRequested += () => SimLauncher.OpenStartScreen();

            // Keyboard events go to the focused element, or the panel root when nothing has focus,
            // so listen on the panel root during trickle-down to see both.
            root.RegisterCallback<AttachToPanelEvent>(_ => HookKeyboard(root));
            if (root.panel != null) HookKeyboard(root);
        }

        bool keyboardHooked;

        void HookKeyboard(VisualElement root)
        {
            if (keyboardHooked || root.panel == null) return;
            keyboardHooked = true;

            root.panel.visualTree.RegisterCallback<KeyDownEvent>(OnKeyDown, TrickleDown.TrickleDown);
            root.panel.visualTree.RegisterCallback<KeyUpEvent>(OnKeyUp, TrickleDown.TrickleDown);
        }

        void ShowScreen(VisualElement screen)
        {
            app.Clear();
            if (screen != null) app.Add(screen);
            app.Add(overlay);
        }

        // ---------------------------------------------------------------- library

        void ShowLibrary()
        {
            library.Refresh();
            ShowScreen(library);
        }

        void ShowNewLevelMenu(VisualElement anchor)
        {
            var entries = new List<MenuEntry>
            {
                MenuEntry.Item("Blank room", () => CreateLevel(LevelSnapshot.DefaultName, SampleLevels.PopulateBlank), IconKind.Rect),
                MenuEntry.Item("Sample apartment", () => CreateLevel("Sample apartment", SampleLevels.PopulateApartment), IconKind.Grid),
                MenuEntry.Item("Picture canvas", () => CreateLevel("Picture canvas", SampleLevels.PopulatePictureCanvas), IconKind.Pen),
            };

            bool headed = false;
            foreach (var template in templates)
            {
                if (template == null) continue;
                if (!headed)
                {
                    entries.Add(MenuEntry.Separator());
                    entries.Add(MenuEntry.Heading("TEMPLATES"));
                    headed = true;
                }

                var captured = template;
                entries.Add(MenuEntry.Item(captured.name, () => CreateLevel(captured.name, level =>
                    LevelSnapshot.FromLevel(captured, captured.name).ApplyTo(level)), IconKind.Duplicate));
            }

            overlay.ShowMenuBelow(anchor, entries);
        }

        void CreateLevel(string wantedName, Action<LevelData> populate)
        {
            var snapshot = LevelSnapshot.Create(UniqueLevelName(wantedName), populate);

            try
            {
                OpenLevel(LevelLibrary.Create(snapshot));
            }
            catch (Exception exception)
            {
                ReportError("Couldn't create the floor plan", exception);
            }
        }

        string UniqueLevelName(string wanted)
        {
            var taken = new List<string>();
            foreach (var entry in library.Entries) taken.Add(entry.level.name);
            return LevelLibrary.UniqueName(wanted, taken);
        }

        void ShowCardMenu(LevelLibrary.Entry entry, VisualElement anchor)
        {
            overlay.ShowMenuBelow(anchor, new List<MenuEntry>
            {
                MenuEntry.Item("Open", () => OpenLevel(entry.path), IconKind.Select),
                MenuEntry.Item("Simulate", () => SimulateLevel(entry.level, entry.path), IconKind.Play),
                MenuEntry.Item("Duplicate", () => DuplicateLevel(entry), IconKind.Duplicate),
                MenuEntry.Separator(),
                MenuEntry.Item("Delete…", () => ConfirmDeleteLevel(entry), IconKind.Trash, danger: true),
            });
        }

        void DuplicateLevel(LevelLibrary.Entry entry)
        {
            try
            {
                var copy = LevelSnapshot.Parse(entry.level.ToJson(false));
                copy.name = UniqueLevelName(entry.level.name + " copy");
                LevelLibrary.Create(copy);
                library.Refresh();
            }
            catch (Exception exception)
            {
                ReportError("Couldn't duplicate the floor plan", exception);
            }
        }

        void ConfirmDeleteLevel(LevelLibrary.Entry entry)
        {
            overlay.ShowDialog(
                $"Delete “{entry.level.name}”?",
                "This removes the floor plan from this device. It can't be undone.",
                null,
                new DialogButton("Cancel", ButtonStyle.Ghost, null),
                new DialogButton("Delete", ButtonStyle.Danger, () =>
                {
                    try
                    {
                        LevelLibrary.Delete(entry.path);
                        library.Refresh();
                    }
                    catch (Exception exception)
                    {
                        ReportError("Couldn't delete the floor plan", exception);
                    }
                }));
        }

        // ---------------------------------------------------------------- session

        void OpenLevel(string path)
        {
            LevelSnapshot snapshot;
            try
            {
                snapshot = LevelLibrary.Read(path);
            }
            catch (Exception exception)
            {
                ReportError("Couldn't open the floor plan", exception);
                return;
            }

            if (snapshot == null)
            {
                overlay.Toast("That file isn't a readable floor plan.");
                return;
            }

            CloseSession();
            session = new EditorSession(snapshot, Palette, path);
            session.DocumentChanged += RefreshDocumentState;
            session.LevelChanged += OnLevelChanged;
            session.ToolChanged += RefreshTools;
            session.OptionsChanged += RefreshViewBar;
            session.Notice += message => overlay.Toast(message);

            BuildEditorScreen();
            ShowScreen(editorScreen);
            canvas.Focus();
        }

        void CloseSession()
        {
            if (session == null) return;

            canvas?.Unbind();
            inspector?.Unbind();
            session.Dispose();

            session = null;
            canvas = null;
            inspector = null;
            editorScreen = null;
            toolButtons.Clear();
        }

        /// <summary>Back to the library, offering to save first when there are unsaved edits.</summary>
        void RequestCloseEditor()
        {
            if (session == null) return;

            if (!session.IsDirty)
            {
                CloseSession();
                ShowLibrary();
                return;
            }

            overlay.ShowDialog(
                "Save changes?",
                $"“{session.Name}” has edits that haven't been saved.",
                null,
                new DialogButton("Cancel", ButtonStyle.Ghost, null),
                new DialogButton("Don't save", ButtonStyle.Ghost, () =>
                {
                    CloseSession();
                    ShowLibrary();
                }),
                new DialogButton("Save", ButtonStyle.Primary, () =>
                {
                    if (!TrySave(false)) return;
                    CloseSession();
                    ShowLibrary();
                }));
        }

        bool TrySave(bool announce)
        {
            if (session == null) return false;

            try
            {
                session.Save();
                if (announce) overlay.Toast("Saved");
                return true;
            }
            catch (Exception exception)
            {
                ReportError("Couldn't save", exception);
                return false;
            }
        }

        void ReportError(string title, Exception exception)
        {
            Debug.LogException(exception);
            string detail = exception is IOException || exception is UnauthorizedAccessException
                ? exception.Message
                : "Something went wrong. Details are in the log.";
            overlay.ShowDialog(title, detail, null, new DialogButton("OK", ButtonStyle.Primary, null));
        }

        // ---------------------------------------------------------------- editor screen

        void BuildEditorScreen()
        {
            editorScreen = Ui.Div(null, "le-screen");
            BuildTopBar(Ui.Div(editorScreen, "le-topbar"));

            var body = Ui.Div(editorScreen, "le-body");
            var stage = Ui.Div(body, "le-stage");

            canvas = new FloorPlanCanvas(session);
            canvas.ViewChanged += RefreshViewReadouts;
            canvas.ContextRequested += ShowCanvasMenu;
            stage.Add(canvas);

            BuildToolRail(Ui.Div(stage, "le-float le-toolrail"));

            var hint = Ui.Div(stage, "le-float le-hint");
            hint.pickingMode = PickingMode.Ignore;
            hintLabel = Ui.Text(hint, string.Empty);
            hintLabel.pickingMode = PickingMode.Ignore;

            BuildViewBar(Ui.Div(stage, "le-float le-bar le-viewbar"));
            BuildZoomBar(Ui.Div(stage, "le-float le-bar le-zoombar"));

            inspector = new InspectorPanel(session);
            inspector.FrameRoomRequested += index => canvas.FrameRoom(index);
            body.Add(inspector);

            RefreshDocumentState();
            RefreshTools();
            RefreshViewBar();
            RefreshViewReadouts();
        }

        void BuildTopBar(VisualElement bar)
        {
            Ui.IconButton(bar, IconKind.Back, "All floor plans", RequestCloseEditor, "le-btn--ghost");

            nameField = Ui.TextInput(bar, session.Name, value =>
            {
                if (string.IsNullOrWhiteSpace(value)) nameField.SetValueWithoutNotify(session.Name);
                else session.Rename(value);
            }, "le-name-input");
            nameField.RegisterCallback<KeyDownEvent>(evt =>
            {
                if (evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter) canvas.Focus();
            });

            statusChip = Ui.Div(bar, "le-status-chip");
            Ui.Div(statusChip, "le-status-chip__dot");
            statusLabel = Ui.Text(statusChip, string.Empty);

            Ui.Div(bar, "le-topbar__spacer");

            issuesChip = Ui.Button(bar, string.Empty, IconKind.Check, () =>
            {
                session.ClearSelection();
                inspector.Rebuild();
            }, "le-issues-chip");
            Ui.Tooltip(issuesChip, "Show level checks");

            Ui.Div(bar, "le-divider");
            undoButton = Ui.IconButton(bar, IconKind.Undo, $"Undo ({Shortcut("Z")})", () => session.Undo(), "le-btn--ghost");
            redoButton = Ui.IconButton(bar, IconKind.Redo, $"Redo ({Shortcut("⇧Z")})", () => session.Redo(), "le-btn--ghost");
            Ui.Div(bar, "le-divider");

            Ui.Button(bar, "Save", IconKind.Save, () => TrySave(true), "le-btn--ghost");

            // The editor edits; every way of running the plan lives behind this one button.
            VisualElement runButton = null;
            runButton = Ui.Button(bar, "Run", IconKind.Play, () => ShowRunMenu(runButton), "le-btn--primary");
            Ui.Tooltip(runButton, "Run this floor plan, with or without visuals");
        }

        void BuildToolRail(VisualElement rail)
        {
            foreach (var entry in Tools)
            {
                var tool = entry.tool;
                var button = Ui.IconButton(rail, entry.icon, $"{entry.name}   {entry.key}", () => session.Tool = tool,
                    "le-tool", TooltipSide.Right);
                toolButtons[tool] = button;
            }
        }

        void BuildViewBar(VisualElement bar)
        {
            gridToggle = Ui.IconButton(bar, IconKind.Grid, "Show grid   G", () => session.ShowGrid = !session.ShowGrid,
                "le-toggle", TooltipSide.Above);
            Ui.Div(bar, "le-bar__divider");

            snapToggle = Ui.IconButton(bar, IconKind.Magnet, "Snap to grid", () => session.SnapToGrid = !session.SnapToGrid,
                "le-toggle", TooltipSide.Above);

            VisualElement stepButton = null;
            stepButton = Ui.Button(bar, string.Empty, null, () => ShowSnapStepMenu(stepButton), "le-toggle");
            snapLabel = stepButton.Q<Label>() ?? Ui.Text(stepButton, string.Empty, "le-btn__label");
            stepButton.RemoveFromClassList("le-btn--icon");
            Ui.Tooltip(stepButton, "Grid step", TooltipSide.Above);

            pointsToggle = Ui.IconButton(bar, IconKind.Points, "Snap to room corners", () => session.SnapToVertices = !session.SnapToVertices,
                "le-toggle", TooltipSide.Above);
        }

        void ShowSnapStepMenu(VisualElement anchor)
        {
            var entries = new List<MenuEntry> { MenuEntry.Heading("GRID STEP") };
            foreach (float step in EditorSession.SnapSteps)
            {
                float captured = step;
                var item = MenuEntry.Item(FormatStep(step), () =>
                {
                    session.SnapIncrement = captured;
                    session.SnapToGrid = true;
                });
                item.isChecked = Mathf.Approximately(step, session.SnapIncrement);
                entries.Add(item);
            }

            var bounds = anchor.worldBound;
            overlay.ShowMenu(new Vector2(bounds.xMin, bounds.yMin - 6f - 34f * (entries.Count + 0.5f)), entries);
        }

        static string FormatStep(float step) => step < 1f ? $"{step * 100f:0} cm" : $"{step:0} m";

        void BuildZoomBar(VisualElement bar)
        {
            coordsLabel = Ui.Text(bar, string.Empty, "le-coords");
            Ui.Div(bar, "le-bar__divider");
            Ui.IconButton(bar, IconKind.Minus, "Zoom out   −", () => canvas.ZoomBy(1f / 1.25f), null, TooltipSide.Above);

            var zoomButton = Ui.Button(bar, "100%", null, () => canvas.FrameAll(), "le-zoom-label");
            zoomLabel = zoomButton.Q<Label>();
            Ui.Tooltip(zoomButton, "Fit floor plan   F", TooltipSide.Above);

            Ui.IconButton(bar, IconKind.Plus, "Zoom in   +", () => canvas.ZoomBy(1.25f), null, TooltipSide.Above);
            Ui.IconButton(bar, IconKind.Fit, "Fit floor plan   F", () => canvas.FrameAll(), null, TooltipSide.Above);
        }

        // ---------------------------------------------------------------- refresh

        void OnLevelChanged(bool live)
        {
            if (!live) RefreshIssues();
        }

        void RefreshDocumentState()
        {
            if (session == null || editorScreen == null) return;

            if (nameField.focusController?.focusedElement != nameField) nameField.SetValueWithoutNotify(session.Name);

            statusChip.EnableInClassList("le-status-chip--dirty", session.IsDirty);
            statusLabel.text = session.IsDirty ? "Unsaved changes" : "Saved";

            undoButton.SetEnabled(session.CanUndo);
            redoButton.SetEnabled(session.CanRedo);
            RefreshIssues();
        }

        void RefreshIssues()
        {
            if (session == null || issuesChip == null) return;

            int count = session.Validate().Count;
            var icon = issuesChip.Q<IconElement>();
            var label = issuesChip.Q<Label>();

            if (label == null)
            {
                label = Ui.Text(issuesChip, string.Empty, "le-btn__label");
                issuesChip.RemoveFromClassList("le-btn--icon");
            }

            icon.Kind = count == 0 ? IconKind.Check : IconKind.Warning;
            label.text = count == 0 ? "Ready to run" : count == 1 ? "1 issue" : $"{count} issues";
            Ui.SetClass(issuesChip, "le-issues-chip--ok", count == 0);
            Ui.SetClass(issuesChip, "le-issues-chip--warn", count > 0);
        }

        void RefreshTools()
        {
            if (session == null) return;

            foreach (var pair in toolButtons)
                Ui.SetClass(pair.Value, "le-tool--active", pair.Key == session.Tool);

            RefreshHint();
        }

        void RefreshHint()
        {
            if (hintLabel == null) return;

            hintLabel.text = session.Tool switch
            {
                EditorTool.Select => "Drag rooms to move · drag corners to reshape · drag a midpoint to add a corner",
                EditorTool.Rect => "Drag to draw a room · hold Shift for a square",
                EditorTool.Pen => canvas != null && canvas.PenPointCount >= 3
                    ? "Click the first point or press Enter to finish · Backspace removes a point"
                    : "Click to place corners",
                EditorTool.Wall => "Click a side to remove or restore its wall · drag in open space to draw a wall",
                EditorTool.Doorway => "Click on a wall to cut a doorway",
                EditorTool.Spawn => "Click to set where the vacuum starts",
                EditorTool.Obstacle => $"Drag to size a {Obstacle.DefaultName(session.ObstacleKind).ToLowerInvariant()}, or click to drop one · change the kind in the panel",
                _ => string.Empty,
            };
        }

        void RefreshViewBar()
        {
            if (session == null || gridToggle == null) return;

            Ui.SetClass(gridToggle, "le-toggle--on", session.ShowGrid);
            Ui.SetClass(snapToggle, "le-toggle--on", session.SnapToGrid);
            Ui.SetClass(pointsToggle, "le-toggle--on", session.SnapToVertices);
            snapLabel.text = FormatStep(session.SnapIncrement);
            snapLabel.parent.SetEnabled(session.SnapToGrid);
            RefreshHint(); // the furniture hint names the kind, which is an option
        }

        void RefreshViewReadouts()
        {
            if (canvas == null) return;

            zoomLabel.text = $"{canvas.ZoomPercent}%";
            var p = canvas.CursorLevel;
            coordsLabel.text = canvas.CursorInside ? $"x {p.x:0.00}   y {p.y:0.00}" : string.Empty;

            if (session.Tool == EditorTool.Pen) RefreshHint();
        }

        // ---------------------------------------------------------------- context menu

        void ShowCanvasMenu(Vector2 panelPosition, Vector2 point)
        {
            var level = session.Level;
            float grab = EditorSession.HandlePixels / canvas.Zoom;
            var entries = new List<MenuEntry>();

            int obstacleIndex = level.ObstacleIndexAt(point);
            if (obstacleIndex >= 0)
            {
                var obstacle = level.Obstacles[obstacleIndex];
                session.SelectObstacle(obstacleIndex);

                entries.Add(MenuEntry.Heading(obstacle.name.ToUpperInvariant()));
                var passItem = MenuEntry.Item("Vacuum can pass under", () => session.SetObstacleBlocks(obstacleIndex, !obstacle.blocksVacuum));
                passItem.isChecked = !obstacle.blocksVacuum;
                entries.Add(passItem);
                entries.Add(MenuEntry.Separator());
                entries.Add(MenuEntry.Item("Duplicate", () => session.DuplicateObstacle(obstacleIndex), IconKind.Duplicate, Shortcut("D")));
                entries.Add(MenuEntry.Item("Delete furniture", () => session.DeleteObstacle(obstacleIndex), IconKind.Trash, "⌫", danger: true));

                overlay.ShowMenu(panelPosition, entries);
                return;
            }

            int roomIndex = session.SelectedRoomData != null && session.SelectedRoomData.Contains(point)
                ? session.SelectedRoom
                : level.RoomIndexAt(point);
            var room = level.GetRoom(roomIndex);

            if (room != null)
            {
                entries.Add(MenuEntry.Heading(room.name.ToUpperInvariant()));

                int vertex = session.FindVertex(roomIndex, point, grab * 1.5f);
                if (vertex >= 0)
                {
                    entries.Add(MenuEntry.Item("Delete corner", () => session.RemoveVertex(roomIndex, vertex), IconKind.Minus,
                        enabled: room.outline.Count > 3));
                }
                else if (EditorSession.TryFindEdge(room, point, grab * 2f, out int edge, out Vector2 onEdge))
                {
                    entries.Add(MenuEntry.Item("Add corner here", () => session.InsertVertex(roomIndex, edge, onEdge), IconKind.Plus));
                }

                if (session.TryFindAnyEdge(point, grab * 3f, out int wallRoom, out int wallEdge, out _))
                {
                    bool present = level.Rooms[wallRoom].HasWall(wallEdge);
                    entries.Add(MenuEntry.Item(present ? "Remove this wall" : "Restore this wall",
                        () => session.SetWall(wallRoom, wallEdge, !present), present ? IconKind.WallsOff : IconKind.WallsOn));
                }

                entries.Add(MenuEntry.Item("Place furniture here",
                    () => session.AddObstacle(session.Snap(point, canvas.Zoom), Obstacle.DefaultSize(session.ObstacleKind)), IconKind.Obstacle));

                entries.Add(MenuEntry.Separator());
                entries.Add(MenuEntry.Heading("FLOOR"));
                entries.Add(MenuEntry.Custom(close => BuildSwatchRow(roomIndex, room.floorIndex, close)));
                entries.Add(MenuEntry.Separator());

                int walls = session.WallCount(room);
                entries.Add(MenuEntry.Item("Add all walls", () => session.SetAllWalls(roomIndex, true), IconKind.WallsOn,
                    enabled: walls < room.EdgeCount));
                entries.Add(MenuEntry.Item("Remove all walls", () => session.SetAllWalls(roomIndex, false), IconKind.WallsOff,
                    enabled: walls > 0));
                entries.Add(MenuEntry.Separator());
                entries.Add(MenuEntry.Item("Duplicate", () => session.DuplicateRoom(roomIndex), IconKind.Duplicate, Shortcut("D")));
                entries.Add(MenuEntry.Item("Delete room", () => session.DeleteRoom(roomIndex), IconKind.Trash, "⌫", danger: true));
            }
            else
            {
                entries.Add(MenuEntry.Item("Draw rectangle room", () => session.Tool = EditorTool.Rect, IconKind.Rect, "R"));
                entries.Add(MenuEntry.Item("Draw pen room", () => session.Tool = EditorTool.Pen, IconKind.Pen, "P"));
                entries.Add(MenuEntry.Item("Start vacuum here", () => session.SetSpawn(session.Snap(point, canvas.Zoom)), IconKind.Spawn));
                entries.Add(MenuEntry.Item("Place furniture here",
                    () => session.AddObstacle(session.Snap(point, canvas.Zoom), Obstacle.DefaultSize(session.ObstacleKind)), IconKind.Obstacle));
                entries.Add(MenuEntry.Separator());
                entries.Add(MenuEntry.Item("Fit floor plan", canvas.FrameAll, IconKind.Fit, "F"));
            }

            overlay.ShowMenu(panelPosition, entries);
        }

        VisualElement BuildSwatchRow(int roomIndex, int current, Action close)
        {
            var row = Ui.Div(null, "le-menu__swatches");
            if (Palette == null) return row;

            for (int i = 0; i < Palette.Count; i++)
            {
                int index = i;
                var swatch = Ui.Swatch(row, Palette.ColorOf(i), i == current ? "le-menu__swatch le-menu__swatch--selected" : "le-menu__swatch");
                Ui.Tooltip(swatch, Palette.LabelOf(i), TooltipSide.Above);
                swatch.AddManipulator(new Clickable(() =>
                {
                    close();
                    session.SetRoomFloor(roomIndex, index);
                }));
            }

            return row;
        }

        // ---------------------------------------------------------------- keyboard

        static bool IsMac =>
            Application.platform == RuntimePlatform.OSXPlayer || Application.platform == RuntimePlatform.OSXEditor;

        static string Shortcut(string key) => IsMac ? $"⌘{key}" : $"Ctrl+{key.Replace("⇧", "Shift+")}";

        void OnKeyDown(KeyDownEvent evt)
        {
            if (evt.keyCode == KeyCode.None) return;

            if (evt.keyCode == KeyCode.Escape && overlay.Dismiss())
            {
                evt.StopPropagation();
                return;
            }

            if (overlay.HasPopup) return;

            if (run != null)
            {
                if (evt.keyCode == KeyCode.Escape) { StopRun(); evt.StopPropagation(); }
                return;
            }

            if (session == null || editorScreen == null) return;

            bool typing = Ui.IsTyping(root.panel);
            if (HandleCommandShortcut(evt, typing) || (!typing && HandleEditorKey(evt)))
                evt.StopPropagation();
        }

        void OnKeyUp(KeyUpEvent evt)
        {
            if (evt.keyCode == KeyCode.Space && canvas != null) canvas.SpaceHeld = false;
        }

        bool HandleCommandShortcut(KeyDownEvent evt, bool typing)
        {
            if (!evt.actionKey) return false;

            switch (evt.keyCode)
            {
                case KeyCode.S:
                    if (typing) canvas.Focus();
                    TrySave(true);
                    return true;

                case KeyCode.Return:
                case KeyCode.KeypadEnter:
                    StartRun();
                    return true;
            }

            if (typing) return false;

            switch (evt.keyCode)
            {
                case KeyCode.Z when evt.shiftKey:
                case KeyCode.Y:
                    session.Redo();
                    return true;

                case KeyCode.Z:
                    session.Undo();
                    return true;

                case KeyCode.D:
                    if (session.SelectedRoom >= 0) session.DuplicateRoom(session.SelectedRoom);
                    else if (session.SelectedObstacle >= 0) session.DuplicateObstacle(session.SelectedObstacle);
                    return true;

                default:
                    return false;
            }
        }

        bool HandleEditorKey(KeyDownEvent evt)
        {
            if (evt.ctrlKey || evt.commandKey || evt.altKey) return false;

            switch (evt.keyCode)
            {
                case KeyCode.V: session.Tool = EditorTool.Select; return true;
                case KeyCode.R: session.Tool = EditorTool.Rect; return true;
                case KeyCode.P: session.Tool = EditorTool.Pen; return true;
                case KeyCode.W: session.Tool = EditorTool.Wall; return true;
                case KeyCode.D: session.Tool = EditorTool.Doorway; return true;
                case KeyCode.S: session.Tool = EditorTool.Spawn; return true;
                case KeyCode.O: session.Tool = EditorTool.Obstacle; return true;
                case KeyCode.G: session.ShowGrid = !session.ShowGrid; return true;
                case KeyCode.F: canvas.FrameAll(); return true;

                case KeyCode.Equals:
                case KeyCode.KeypadPlus:
                    canvas.ZoomBy(1.25f);
                    return true;

                case KeyCode.Minus:
                case KeyCode.KeypadMinus:
                    canvas.ZoomBy(1f / 1.25f);
                    return true;

                case KeyCode.Space:
                    canvas.SpaceHeld = true;
                    return true;

                case KeyCode.Return:
                case KeyCode.KeypadEnter:
                    return canvas.ClosePen();

                case KeyCode.Escape:
                    if (canvas.CancelInteraction()) return true;
                    if (session.HasSelection) { session.ClearSelection(); return true; }
                    if (session.Tool != EditorTool.Select) { session.Tool = EditorTool.Select; return true; }
                    return false;

                case KeyCode.Backspace:
                case KeyCode.Delete:
                    if (canvas.RemoveLastPenPoint()) return true;
                    if (!session.HasSelection) return false;
                    session.DeleteSelection();
                    return true;

                default:
                    return false;
            }
        }

        // ---------------------------------------------------------------- simulator

        /// <summary>Saves the open floor plan, then plays it in the simulation scene.</summary>
        void Simulate()
        {
            if (session == null) return;

            var snapshot = LevelSnapshot.Parse(session.Serialize());
            if (!HasRooms(snapshot)) return;

            // Loading the simulator tears down this scene, and the session with it.
            if (session.IsDirty && !TrySave(false)) return;

            SimulateLevel(snapshot, session.FilePath);
        }

        void SimulateLevel(LevelSnapshot snapshot, string path)
        {
            if (!HasRooms(snapshot)) return;

            // Only the serialized palette asset outlives this scene; the built-in one is destroyed with it,
            // so leave it out and let the launcher supply its own.
            if (!SimLauncher.Simulate(snapshot, palette, path))
                overlay.Toast("The simulator scene isn't in the build's scene list.");
        }

        bool HasRooms(LevelSnapshot snapshot)
        {
            if (snapshot != null && snapshot.ValidRoomCount > 0) return true;

            overlay.Toast("Draw a room before running the vacuum.");
            return false;
        }

        // ---------------------------------------------------------------- run mode

        void StartRun()
        {
            if (session == null || run != null) return;

            if (session.Level.Rooms.Count == 0)
            {
                overlay.Toast("Draw a room before running the vacuum.");
                return;
            }

            int startRoom = session.Level.RoomIndexAt(session.Level.RobotSpawn);
            if (startRoom < 0)
            {
                overlay.ShowDialog(
                    "Vacuum starts outside",
                    "The vacuum cannot reach any room from its starting position. Proceeding will leave all rooms at 0% coverage.",
                    null,
                    new DialogButton("Cancel", ButtonStyle.Ghost, null),
                    new DialogButton("Proceed anyway", ButtonStyle.Primary, BeginRun));
                return;
            }

            var unreachable = session.Level.UnreachableRooms();
            if (unreachable.Count > 0)
            {
                var names = new List<string>();
                foreach (int index in unreachable)
                {
                    var room = session.Level.GetRoom(index);
                    names.Add(room != null ? room.name : "Room");
                }

                overlay.ShowDialog(
                    "Unreachable rooms",
                    "No doorway path reaches " + string.Join(", ", names) + ". Proceeding will leave those rooms at 0% coverage.",
                    null,
                    new DialogButton("Cancel", ButtonStyle.Ghost, null),
                    new DialogButton("Proceed anyway", ButtonStyle.Primary, BeginRun));
                return;
            }

            BeginRun();
        }

        void BeginRun()
        {
            overlay.CloseMenu();
            run = new LevelRun(session.Level);

            if (vacuumSettings == null) vacuumSettings = VacuumSettings.From(run.Robot, run.Battery);
            else vacuumSettings.ApplyTo(run.Robot, run.Battery);

            ShowScreen(BuildRunHud());
            app.AddToClassList("le-app--running");
        }

        void StopRun()
        {
            // Escape and the Stop button reach both kinds of run; a fast run is the one showing if it is.
            if (fastRun != null || fastRunImage != null)
            {
                StopFastRun();
                return;
            }

            if (run == null) return;

            run.Stop();
            run = null;
            app.RemoveFromClassList("le-app--running");

            ShowScreen(editorScreen);
            canvas.Focus();
        }

        // ---------------------------------------------------------------- fast run

        static readonly float[] FastRunMinutes = { 1f, 5f, 15f };
        static readonly string[] FastRunMinuteLabels = { "1 min", "5 min", "15 min" };

        /// <summary>
        /// The one way from editing into running. Headless is the quick answer — no visuals, many times
        /// faster than real time, with the coverage previewed as it fills in — while the watched runs are
        /// for seeing the vacuum actually drive, either over the editor or in the simulator scene.
        /// </summary>
        void ShowRunMenu(VisualElement anchor)
        {
            overlay.ShowMenuBelow(anchor, new List<MenuEntry>
            {
                MenuEntry.Heading("RUN"),
                MenuEntry.Item("Headless — fast, no visuals", StartFastRun, IconKind.Fit),
                MenuEntry.Custom(_ => BuildFastRunLength()),
                MenuEntry.Separator(),
                MenuEntry.Item("Watch here", StartRun, IconKind.Spawn, Shortcut("↩")),
                MenuEntry.Item("Watch in simulator", Simulate, IconKind.Play),
            });
        }

        /// <summary>
        /// How long a headless run simulates. It sits in the menu rather than in the run itself, so a length
        /// can be picked and the run started without leaving the menu.
        /// </summary>
        VisualElement BuildFastRunLength()
        {
            var row = Ui.Div(null, "le-run-menu__length");
            Ui.Text(row, "Length", "le-field-label");

            var minutes = new Segmented(FastRunMinuteLabels, fastRunMinutesIndex);
            minutes.SelectionChanged += index => fastRunMinutesIndex = index;
            row.Add(minutes);

            return row;
        }

        /// <summary>
        /// Runs the open floor plan with no visuals, many times faster than real time, previewing the
        /// coverage grid as it fills in. The seed is kept, so Watch it replays the same run properly.
        /// </summary>
        void StartFastRun()
        {
            if (session == null || fastRun != null) return;

            if (session.Level.Rooms.Count == 0)
            {
                overlay.Toast("Draw a room before running the vacuum.");
                return;
            }

            if (run != null) StopRun(); // the two would fight over the time scale

            overlay.CloseMenu();
            fastRunSeed = Rng.NewSeed();
            SimLauncher.Seed = fastRunSeed;

            // The editor goes on using this level, so the run is told not to take ownership of it.
            fastRun = HeadlessSimulation.Start(session.Level, session.Name, FastRunMinutes[fastRunMinutesIndex] * 60f,
                SimLauncher.MovementPattern, SimLauncher.Picture, fastRunSeed, HeadlessSimulation.DefaultSpeed, false);
            fastRun.Finished += OnFastRunFinished;

            ShowScreen(BuildFastRunHud());
            app.AddToClassList("le-app--running");
        }

        void StopFastRun()
        {
            closingFastRun = true;
            fastRun?.Stop(); // reports through OnFastRunFinished, which hands over the final image
            closingFastRun = false;
            fastRun = null;

            ClearFastRunImage();
            app.RemoveFromClassList("le-app--running");
            ShowScreen(editorScreen);
            canvas.Focus();
        }

        void ClearFastRunImage()
        {
            if (fastRunImage == null) return;

            Destroy(fastRunImage);
            fastRunImage = null;
        }

        void OnFastRunFinished(SimulationResults results)
        {
            fastRun = null;

            ClearFastRunImage();
            fastRunImage = results.coverage;

            if (closingFastRun) return; // the run was stopped on the way out of the screen
            ShowScreen(BuildFastRunResults(results));
        }

        VisualElement BuildFastRunHud()
        {
            var screen = Ui.Div(null, "le-screen le-fastrun-screen");

            var panel = Ui.Div(screen, "le-float le-fastrun");
            Ui.Text(panel, $"Fast run · {session.Name}", "le-fastrun__title");

            var heatmap = new Image { scaleMode = ScaleMode.ScaleToFit };
            heatmap.AddToClassList("le-heatmap");
            Ui.Tooltip(heatmap, "Green is clean, red barely touched, grey blocked by furniture");
            panel.Add(heatmap);

            var bar = Ui.Div(panel, "le-progress");
            var fill = Ui.Div(bar, "le-progress__fill");

            var stats = Ui.Div(panel, "le-fastrun__stats");
            var cleaned = Ui.RunStat(stats, "Cleaned");
            var simulated = Ui.RunStat(stats, "Simulated");
            var real = Ui.RunStat(stats, "Real time");
            var speed = Ui.RunStat(stats, "Speed");

            var actions = Ui.Div(panel, "le-fastrun__actions");
            Ui.Button(actions, "Stop", IconKind.Stop, StopFastRun, "le-btn--ghost");

            screen.schedule.Execute(() =>
            {
                if (fastRun == null || !fastRun.Running) return;

                // Repaint in place while the grid keeps its shape; rebuild only if it ever changes.
                if (fastRunImage == null || !fastRun.RepaintImage(fastRunImage))
                {
                    ClearFastRunImage();
                    fastRunImage = fastRun.CreateImage();
                }

                heatmap.image = fastRunImage;

                fill.style.width = Length.Percent(fastRun.Progress * 100f);
                cleaned.text = $"{fastRun.CoveragePercent:0.0}%";
                simulated.text = SimulationResults.FormatDuration(fastRun.SimulatedSeconds);
                real.text = SimulationResults.FormatDuration(fastRun.RealSeconds);
                speed.text = fastRun.RealSeconds > 0.5f ? $"{fastRun.SimulatedSeconds / fastRun.RealSeconds:0}×" : "—";
            }).Every(200);

            return screen;
        }

        VisualElement BuildFastRunResults(SimulationResults results)
        {
            var screen = Ui.Div(null, "le-screen le-fastrun-screen");

            var panel = Ui.Div(screen, "le-float le-fastrun");
            Ui.Text(panel, $"{results.levelName} · {results.coveragePercent:0.0}% cleaned", "le-fastrun__title");

            if (fastRunImage != null)
            {
                var heatmap = new Image { image = fastRunImage, scaleMode = ScaleMode.ScaleToFit };
                heatmap.AddToClassList("le-heatmap");
                Ui.Tooltip(heatmap, "Green is clean, red barely touched, grey blocked by furniture");
                panel.Add(heatmap);
            }

            var stats = Ui.Div(panel, "le-fastrun__stats");
            Ui.RunStat(stats, "Cleaned").text = $"{results.coveragePercent:0.0}%";
            Ui.RunStat(stats, "Distance").text = Ui.FormatMetres(results.metresDriven);
            Ui.RunStat(stats, "Blocked").text = Ui.FormatArea(results.blockedArea);
            Ui.RunStat(stats, "Simulated").text = SimulationResults.FormatDuration(results.simulatedSeconds);
            Ui.RunStat(stats, "Real time").text = SimulationResults.FormatDuration(results.realSeconds);

            Ui.Text(panel, results.batteryRanOut
                ? $"The battery ran out. Seed {results.seed}."
                : $"About {results.SpeedUp:0}× real time. Seed {results.seed}.", "le-fastrun__note");

            var actions = Ui.Div(panel, "le-fastrun__actions");

            var minutes = new Segmented(FastRunMinuteLabels, fastRunMinutesIndex);
            minutes.SelectionChanged += index => fastRunMinutesIndex = index;
            Ui.Tooltip(minutes, "How long the next fast run simulates");
            actions.Add(minutes);

            Ui.Button(actions, "Run again", IconKind.Redo, () => { StopFastRun(); StartFastRun(); }, "le-btn--ghost");

            var watch = Ui.Button(actions, "Watch it", IconKind.Play, () => { StopFastRun(); StartRun(); }, "le-btn--ghost");
            Ui.Tooltip(watch, "Replay this exact run in the editor, at normal speed");

            Ui.Button(actions, "Close", IconKind.Close, StopFastRun, "le-btn--primary");

            return screen;
        }

        VisualElement BuildRunHud()
        {
            var screen = Ui.Div(null, "le-screen");
            screen.pickingMode = PickingMode.Ignore;

            var bar = Ui.Div(screen, "le-float le-run-bar");
            Ui.Button(bar, "Stop", IconKind.Stop, StopRun, "le-btn--ghost");
            Ui.IconButton(bar, IconKind.Undo, "Restart", () => run?.Restart(), "le-btn--ghost");

            var simSpeed = new Segmented(new[] { "1×", "2×", "4×", "8×" }, 0);
            simSpeed.SelectionChanged += index => run?.SetSpeed(Mathf.Pow(2f, index));
            Ui.Tooltip(simSpeed, "Simulation speed");
            bar.Add(simSpeed);

            VisualElement settingsButton = null;
            settingsButton = Ui.Button(bar, "Settings", IconKind.Spawn, () => ShowVacuumSettings(settingsButton), "le-btn--ghost");
            Ui.Tooltip(settingsButton, "Vacuum speed and battery");

            var picture = new Segmented(Pictures.Names, (int)SimLauncher.Picture);
            picture.SelectionChanged += index =>
            {
                if ((PictureKind)index == PictureKind.Image && !ImageFilePicker.TryLoad(out string problem))
                {
                    picture.SetSelectedWithoutNotify((int)SimLauncher.Picture);
                    if (problem != null) overlay.Toast(problem);
                    return;
                }

                SimLauncher.Picture = (PictureKind)index;
                if (run == null) return;

                if (run.Robot != null) run.Robot.Picture = SimLauncher.Picture;
                run.Restart(); // start drawing from the top on a clean floor
            };
            Ui.Tooltip(picture, "Picture to draw into the heatmap · Image… loads a PNG or JPG");
            Ui.SetVisible(picture, SimLauncher.MovementPattern == MovementPattern.Picture);

            var movement = new Segmented(MovementBrain.Labels, (int)SimLauncher.MovementPattern);
            movement.SelectionChanged += index =>
            {
                SimLauncher.MovementPattern = (MovementPattern)index;
                if (run != null && run.Robot != null) run.Robot.Pattern = SimLauncher.MovementPattern;
                Ui.SetVisible(picture, SimLauncher.MovementPattern == MovementPattern.Picture);
            };
            Ui.Tooltip(movement, "Movement algorithm");
            bar.Add(movement);
            bar.Add(picture);

            var live = Ui.Div(bar, "le-run-live");
            Ui.Div(live, "le-run-live__dot");
            Ui.Text(live, session.Name);

            var runtime = Ui.RunStat(bar, "Runtime");
            var speed = Ui.RunStat(bar, "Speed");
            var battery = Ui.RunStat(bar, "Battery");
            var distance = Ui.RunStat(bar, "Distance");
            var surface = Ui.RunStat(bar, "Surface");
            var blocked = Ui.RunStat(bar, "Blocked");
            blocked.text = Ui.FormatArea(session.Level.BlockedFloorArea());
            var cleaned = Ui.RunStat(bar, "Cleaned");

            screen.schedule.Execute(() =>
            {
                if (run == null) return;
                var robot = run.Robot;

                runtime.text = VacuumSettings.FormatDuration(run.Elapsed);
                speed.text = vacuumSettings.FormatSpeed(robot != null ? robot.SpeedMetersPerSecond : 0f);

                if (run.Battery != null)
                {
                    battery.text = VacuumSettings.FormatBattery(run.Battery.CurrentLifeSeconds, run.Battery.BatteryLifeSeconds);
                    Ui.SetClass(battery, "le-run-stat__value--warn",
                        VacuumSettings.IsBatteryLow(run.Battery.CurrentLifeSeconds, run.Battery.BatteryLifeSeconds));
                }

                distance.text = Ui.FormatMetres(robot != null ? robot.DistanceTravelled : 0f);
                var floor = robot != null ? robot.CurrentFloor : null;
                surface.text = floor != null ? floor.Label : "—";
                cleaned.text = run.Cleaning != null && run.Cleaning.Grid != null ? $"{run.Cleaning.CoveragePercent:0.0}%" : "—";
            }).Every(150);

            return screen;
        }

        void ShowVacuumSettings(VisualElement anchor)
        {
            if (run == null) return;

            overlay.ShowMenuBelow(anchor, new List<MenuEntry>
            {
                MenuEntry.Heading("VACUUM"),
                MenuEntry.Custom(_ => BuildVacuumSettings(anchor)),
            });
        }

        VisualElement BuildVacuumSettings(VisualElement anchor)
        {
            var settings = vacuumSettings;
            var panel = Ui.Div(null, "le-run-settings");

            var units = new Segmented(new[] { "m/s", "ft/s" }, settings.useFeet ? 1 : 0);
            units.SelectionChanged += index =>
            {
                settings.useFeet = index == 1;
                ShowVacuumSettings(anchor); // rebuild so the slider read-outs switch unit too
            };
            panel.Add(units);

            AddVacuumSetting(panel, "Drive speed", 0.05f, 2f, settings.driveSpeed, settings.FormatSpeed, v => settings.driveSpeed = v);
            AddVacuumSetting(panel, "Reverse speed", 0.05f, 1.5f, settings.reverseSpeed, settings.FormatSpeed, v => settings.reverseSpeed = v);
            AddVacuumSetting(panel, "Turn speed", 30f, 720f, settings.turnSpeed, v => $"{v:0} °/s", v => settings.turnSpeed = v);
            AddVacuumSetting(panel, "Battery life", 10f, 1800f, settings.batteryLifeSeconds, VacuumSettings.FormatDuration,
                v => settings.batteryLifeSeconds = v);
            Ui.Text(panel, "Changing battery life recharges the vacuum.", "le-empty-hint");

            return panel;
        }

        void AddVacuumSetting(VisualElement parent, string label, float min, float max, float value,
            Func<float, string> format, Action<float> store)
        {
            Ui.Text(parent, label, "le-field-label");

            var slider = new ValueSlider(min, max, value, format, true);
            slider.ValueChanged += next =>
            {
                store(next);
                if (run != null) vacuumSettings.ApplyTo(run.Robot, run.Battery);
            };
            parent.Add(slider);
        }
    }

    /// <summary>
    /// The editor's view of a run: a <see cref="SimulationRunner"/> does the simulation itself, and this
    /// adds the camera the player watches it through. <see cref="Stop"/> puts everything back.
    /// </summary>
    public sealed class LevelRun
    {
        readonly LevelData level;
        readonly SimulationRunner simulation;
        readonly Camera camera;
        readonly bool createdCamera;
        readonly CameraState savedCamera;
        readonly float savedTimeScale;

        struct CameraState
        {
            public bool orthographic;
            public float orthographicSize;
            public Vector3 position;
            public Quaternion rotation;
            public CameraClearFlags clearFlags;
            public Color background;
        }

        public LevelRun(LevelData level)
        {
            this.level = level;
            savedTimeScale = Time.timeScale;

            simulation = SimulationRunner.Start(level,
                SimulationRunner.Options.Visible(SimLauncher.MovementPattern, SimLauncher.Picture, SimLauncher.Seed));

            camera = Camera.main;
            if (camera == null)
            {
                camera = new GameObject("Run Camera").AddComponent<Camera>();
                camera.transform.SetParent(simulation.Root.transform, false);
                createdCamera = true;
            }
            else
            {
                savedCamera = new CameraState
                {
                    orthographic = camera.orthographic,
                    orthographicSize = camera.orthographicSize,
                    position = camera.transform.position,
                    rotation = camera.transform.rotation,
                    clearFlags = camera.clearFlags,
                    background = camera.backgroundColor,
                };
            }

            FrameCamera();
        }

        public VacuumRobot Robot => simulation.Robot;
        public VacuumCleaningController Cleaning => simulation.Cleaning;
        public Battery Battery => simulation.Battery;
        /// <summary>
        /// Simulated seconds this run has covered. It counts physics steps rather than wall-clock time, so
        /// it means the same thing at any run speed and matches what a headless run reports.
        /// </summary>
        public float Elapsed => simulation.Robot != null ? simulation.Robot.SimTime : 0f;

        void FrameCamera()
        {
            SimLauncher.FrameCamera(camera, level.Bounds());
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.063f, 0.075f, 0.094f);
        }

        public void Restart() => simulation.Restart();

        public void SetSpeed(float multiplier) => Time.timeScale = multiplier;

        public void Stop()
        {
            Time.timeScale = savedTimeScale;

            if (!createdCamera && camera != null)
            {
                camera.orthographic = savedCamera.orthographic;
                camera.orthographicSize = savedCamera.orthographicSize;
                camera.transform.SetPositionAndRotation(savedCamera.position, savedCamera.rotation);
                camera.clearFlags = savedCamera.clearFlags;
                camera.backgroundColor = savedCamera.background;
            }

            simulation.Stop();
        }
    }
}
