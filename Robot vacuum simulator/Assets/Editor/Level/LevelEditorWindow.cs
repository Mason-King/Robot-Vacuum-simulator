using System.Collections.Generic;
using RobotVacuum.Level;
using UnityEditor;
using UnityEngine;

namespace RobotVacuum.LevelEditor
{
    /// <summary>
    /// Floor-plan editor with its own top-down 2D canvas. Rooms are drawn directly — drag out
    /// a rectangle, or click points with the pen tool and close the path — then reshaped by
    /// dragging vertices. The scene view stays a live preview; all authoring happens here.
    /// </summary>
    public class LevelEditorWindow : EditorWindow
    {
        enum Tool { Select, Rect, Pen, Wall, Doorway, Spawn }
        enum Drag { None, Pan, Vertex, Room, Doorway, Spawn, RectCreate, WallCreate }

        const float SidebarWidth = 214f;
        const float MinZoom = 4f;
        const float MaxZoom = 400f;
        const float HandlePixels = 7f;

        static readonly int CanvasHint = "RobotVacuumLevelCanvas".GetHashCode();
        static readonly string[] ToolNames = { "Select", "Rect", "Pen", "Wall", "Doorway", "Spawn" };
        static readonly float[] SnapSteps = { 0.05f, 0.1f, 0.25f, 0.5f, 1f, 2f, 5f, 10f, 25f };

        [SerializeField] LevelData level;
        [SerializeField] Tool tool = Tool.Select;
        [SerializeField] int selectedRoom = -1;
        [SerializeField] int selectedDoorway = -1;
        [SerializeField] int paintFloor;
        [SerializeField] float snapIncrement = 0.25f;
        [SerializeField] bool snapToGrid = true;
        [SerializeField] bool snapToVertices = true;
        [SerializeField] bool showGrid = true;
        [SerializeField] bool liveRebuild = true;
        [SerializeField] bool newRoomsHaveWalls = true;
        [SerializeField] float zoom = 60f;
        [SerializeField] Vector2 viewCenter;
        [SerializeField] bool showSettings;
        [SerializeField] bool showValidation = true;

        [SerializeField] Vector2 sidebarScroll;

        CanvasRaster raster;
        bool rasterDirty = true;
        Rect canvasRect;
        Vector2 cursorLevel;

        Drag drag = Drag.None;
        int dragRoom = -1;
        int dragVertex = -1;
        int dragDoorway = -1;
        Vector2 dragAnchorLevel;
        Vector2 dragLastLevel;
        Vector2 rectStartLevel;
        Vector2 wallStartLevel;
        readonly List<Vector2> penPoints = new List<Vector2>();

        LevelRenderer cachedRenderer;
        LevelData cachedRendererLevel;
        GUIStyle roomLabelStyle;

        [MenuItem("Tools/Robot Vacuum/Level Editor %#l", false, 10)]
        public static LevelEditorWindow Open()
        {
            var window = GetWindow<LevelEditorWindow>("Level Editor");
            window.minSize = new Vector2(720f, 460f);
            return window;
        }

        public static void Open(LevelData target)
        {
            var window = Open();
            window.SetLevel(target);
        }

        void OnEnable()
        {
            wantsMouseMove = true;
            raster = new CanvasRaster();
            rasterDirty = true;
            Undo.undoRedoPerformed += OnUndoRedo;
        }

        void OnDisable()
        {
            Undo.undoRedoPerformed -= OnUndoRedo;
            raster?.Dispose();
            raster = null;
        }

        void OnUndoRedo()
        {
            rasterDirty = true;
            PushToScene();
            Repaint();
        }

        void SetLevel(LevelData target)
        {
            level = target;
            selectedRoom = -1;
            selectedDoorway = -1;
            penPoints.Clear();
            cachedRenderer = null;
            cachedRendererLevel = null;
            rasterDirty = true;
            FrameAll();
        }

        // ---------------------------------------------------------------- layout

        void OnGUI()
        {
            roomLabelStyle ??= new GUIStyle(EditorStyles.miniBoldLabel)
            {
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = new Color(1f, 1f, 1f, 0.9f) },
            };

            DrawToolbar();

            if (level == null)
            {
                DrawEmptyState();
                return;
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                DrawSidebar();

                var rect = GUILayoutUtility.GetRect(
                    0f, 0f, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));

                if (rect.width > 1f && rect.height > 1f)
                {
                    if (canvasRect.size != rect.size) rasterDirty = true;
                    canvasRect = rect;
                }

                if (canvasRect.width > 1f)
                {
                    HandleCanvasInput(canvasRect);
                    DrawCanvas(canvasRect);
                }
            }

            DrawStatusBar();
        }

        void DrawToolbar()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                EditorGUI.BeginChangeCheck();
                var picked = (LevelData)EditorGUILayout.ObjectField(
                    level, typeof(LevelData), false, GUILayout.Width(170f));
                if (EditorGUI.EndChangeCheck()) SetLevel(picked);

                if (GUILayout.Button("New…", EditorStyles.toolbarButton, GUILayout.Width(46f)))
                {
                    var created = LevelAssetFactory.CreateLevelAsset(true);
                    if (created != null) SetLevel(created);
                }

                using (new EditorGUI.DisabledScope(level == null))
                {
                    if (GUILayout.Button("Save", EditorStyles.toolbarButton, GUILayout.Width(42f)))
                        AssetDatabase.SaveAssets();

                    if (GUILayout.Button("Frame", EditorStyles.toolbarButton, GUILayout.Width(48f)))
                        FrameAll();

                    if (GUILayout.Button("Build Scene", EditorStyles.toolbarButton, GUILayout.Width(84f)))
                    {
                        Selection.activeObject = level;
                        LevelAssetFactory.SetUp2DScene();
                        cachedRenderer = null;
                    }
                }

                GUILayout.FlexibleSpace();

                EditorGUI.BeginChangeCheck();
                showGrid = GUILayout.Toggle(showGrid, "Grid", EditorStyles.toolbarButton, GUILayout.Width(40f));
                if (EditorGUI.EndChangeCheck()) rasterDirty = true;
            }
        }

        void DrawEmptyState()
        {
            GUILayout.FlexibleSpace();
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox, GUILayout.Width(340f)))
                {
                    EditorGUILayout.LabelField("No level open", EditorStyles.boldLabel);
                    EditorGUILayout.LabelField(
                        "Create one seeded with a sample apartment, or pick an existing level above.",
                        EditorStyles.wordWrappedLabel);

                    EditorGUILayout.Space(6f);
                    if (GUILayout.Button("Create Level…", GUILayout.Height(26f)))
                    {
                        var created = LevelAssetFactory.CreateLevelAsset(true);
                        if (created != null) SetLevel(created);
                    }
                }
                GUILayout.FlexibleSpace();
            }
            GUILayout.FlexibleSpace();
        }

        // ---------------------------------------------------------------- sidebar

        void DrawSidebar()
        {
            using (var scroll = new EditorGUILayout.ScrollViewScope(
                       sidebarScroll, GUILayout.Width(SidebarWidth), GUILayout.ExpandHeight(true)))
            {
                sidebarScroll = scroll.scrollPosition;

                DrawToolButtons();
                EditorGUILayout.Space(6f);
                DrawSnapControls();
                EditorGUILayout.Space(8f);
                DrawPalette();
                EditorGUILayout.Space(8f);
                DrawRoomList();
                EditorGUILayout.Space(8f);
                DrawDoorwayList();
                EditorGUILayout.Space(8f);

                showSettings = EditorGUILayout.Foldout(showSettings, "Level Settings", true);
                if (showSettings) DrawLevelSettings();

                EditorGUILayout.Space(4f);
                showValidation = EditorGUILayout.Foldout(showValidation, "Validation", true);
                if (showValidation) DrawValidation();
            }
        }

        void DrawToolButtons()
        {
            EditorGUILayout.LabelField("Tools", EditorStyles.boldLabel);

            EditorGUI.BeginChangeCheck();
            var next = (Tool)GUILayout.SelectionGrid((int)tool, ToolNames, 2, GUILayout.Height(44f));
            if (EditorGUI.EndChangeCheck()) SetTool(next);

            EditorGUILayout.LabelField(ToolHint(), EditorStyles.wordWrappedMiniLabel);

            newRoomsHaveWalls = EditorGUILayout.ToggleLeft(
                new GUIContent("New rooms include walls",
                    "Off means a new room is floor only, Sims style — draw the floor, then add walls with the Wall tool."),
                newRoomsHaveWalls);
        }

        string ToolHint() => tool switch
        {
            Tool.Select => "Drag a room to move it, drag its dots to reshape. Double-click an edge to add a point. Right-click for more.",
            Tool.Rect => "Drag out a rectangle to create a room. Hold Shift for a perfect square.",
            Tool.Pen => "Click to drop points. Click the first point again, or press Enter, to close the room. Backspace undoes a point, Esc cancels.",
            Tool.Wall => "Click a room edge to remove or restore that one wall. Drag in open space to draw a free-standing wall; Shift locks to 0/45/90°. Click a free-standing wall to delete it.",
            Tool.Doorway => "Click on a wall to cut a doorway. The two nearest rooms get linked.",
            Tool.Spawn => "Click to move the vacuum's starting point.",
            _ => string.Empty,
        };

        void DrawSnapControls()
        {
            EditorGUI.BeginChangeCheck();

            snapToVertices = EditorGUILayout.ToggleLeft(
                new GUIContent("Snap to points", "Pulls onto nearby room corners so rooms share exact edges."),
                snapToVertices);

            using (new EditorGUILayout.HorizontalScope())
            {
                snapToGrid = EditorGUILayout.ToggleLeft(
                    new GUIContent("Snap to", "Optional alignment aid. Rooms themselves are never grid-bound."),
                    snapToGrid, GUILayout.Width(70f));

                using (new EditorGUI.DisabledScope(!snapToGrid))
                {
                    snapIncrement = EditorGUILayout.FloatField(Mathf.Max(0.01f, snapIncrement));
                    GUILayout.Label("m", GUILayout.Width(14f));
                }
            }

            if (EditorGUI.EndChangeCheck()) Repaint();
        }

        void DrawPalette()
        {
            EditorGUILayout.LabelField("Floor Coverings", EditorStyles.boldLabel);

            if (level.Palette == null)
            {
                if (GUILayout.Button("Assign Default Palette"))
                {
                    Undo.RecordObject(level, "Assign Palette");
                    level.Palette = LevelAssetFactory.GetOrCreateDefaultPalette();
                    MarkLevelChanged();
                }
                return;
            }

            var selected = level.GetRoom(selectedRoom);

            for (int i = 0; i < level.Palette.Count; i++)
            {
                var row = EditorGUILayout.GetControlRect(false, 20f);
                bool isActive = selected != null ? selected.floorIndex == i : paintFloor == i;

                if (isActive) EditorGUI.DrawRect(row, new Color(0.24f, 0.48f, 0.90f, 0.35f));

                var swatch = new Rect(row.x + 2f, row.y + 3f, 15f, 14f);
                EditorGUI.DrawRect(swatch, level.Palette.ColorOf(i));

                GUI.Label(new Rect(row.x + 22f, row.y + 1f, row.width - 24f, 18f),
                    level.Palette.LabelOf(i));

                if (Event.current.type != EventType.MouseDown || !row.Contains(Event.current.mousePosition))
                    continue;

                paintFloor = i;
                if (selected != null)
                {
                    Undo.RecordObject(level, "Change Floor Covering");
                    selected.floorIndex = i;
                    MarkLevelChanged();
                }

                Event.current.Use();
                Repaint();
            }

            EditorGUILayout.LabelField(
                selected != null ? "Applies to the selected room." : "Used for new rooms.",
                EditorStyles.miniLabel);
        }

        void DrawRoomList()
        {
            EditorGUILayout.LabelField($"Rooms ({level.Rooms.Count})", EditorStyles.boldLabel);

            for (int i = 0; i < level.Rooms.Count; i++)
            {
                var room = level.Rooms[i];
                if (room == null) continue;

                using (new EditorGUILayout.HorizontalScope())
                {
                    var swatch = GUILayoutUtility.GetRect(13f, 13f, GUILayout.Width(13f));
                    swatch.y += 3f;
                    swatch.height = 12f;
                    EditorGUI.DrawRect(swatch, level.ColorOf(room));

                    var style = i == selectedRoom ? EditorStyles.boldLabel : EditorStyles.label;
                    if (GUILayout.Button(room.name, style))
                    {
                        selectedRoom = i;
                        selectedDoorway = -1;
                        rasterDirty = true;
                        FrameRoom(room);
                    }

                    GUILayout.Label($"{room.Area:0.0}", EditorStyles.miniLabel, GUILayout.Width(30f));

                    if (GUILayout.Button("×", EditorStyles.miniButton, GUILayout.Width(19f)))
                    {
                        DeleteRoom(i);
                        return;
                    }
                }
            }

            if (level.Rooms.Count == 0)
            {
                EditorGUILayout.LabelField("Draw one with the Rect or Pen tool.", EditorStyles.miniLabel);
                return;
            }

            var selected = level.GetRoom(selectedRoom);
            if (selected == null) return;

            EditorGUI.BeginChangeCheck();
            string name = EditorGUILayout.TextField("Name", selected.name);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(level, "Rename Room");
                selected.name = name;
                MarkLevelChanged();
            }
        }

        void DrawDoorwayList()
        {
            EditorGUILayout.LabelField($"Doorways ({level.Doorways.Count})", EditorStyles.boldLabel);

            for (int i = 0; i < level.Doorways.Count; i++)
            {
                var doorway = level.Doorways[i];
                if (doorway == null) continue;

                using (new EditorGUILayout.HorizontalScope())
                {
                    var style = i == selectedDoorway ? EditorStyles.boldLabel : EditorStyles.miniLabel;
                    if (GUILayout.Button($"{NameOfRoom(doorway.roomA)} ↔ {NameOfRoom(doorway.roomB)}", style))
                    {
                        selectedDoorway = i;
                        selectedRoom = -1;
                        rasterDirty = true;
                    }

                    EditorGUI.BeginChangeCheck();
                    float width = EditorGUILayout.Slider(doorway.width, 0.3f, 3f, GUILayout.Width(84f));
                    if (EditorGUI.EndChangeCheck())
                    {
                        Undo.RecordObject(level, "Resize Doorway");
                        doorway.width = width;
                        MarkLevelChanged();
                    }

                    if (GUILayout.Button("×", EditorStyles.miniButton, GUILayout.Width(19f)))
                    {
                        Undo.RecordObject(level, "Delete Doorway");
                        level.Doorways.RemoveAt(i);
                        selectedDoorway = -1;
                        MarkLevelChanged();
                        return;
                    }
                }
            }
        }

        void DrawLevelSettings()
        {
            EditorGUI.BeginChangeCheck();

            var palette = (FloorPalette)EditorGUILayout.ObjectField(
                "Palette", level.Palette, typeof(FloorPalette), false);
            float thickness = EditorGUILayout.Slider("Wall", level.WallThickness, 0.02f, 0.5f);
            var wallColor = EditorGUILayout.ColorField("Wall Colour", level.WallColor);

            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(level, "Edit Level Settings");
                level.Palette = palette;
                level.WallThickness = thickness;
                level.WallColor = wallColor;
                MarkLevelChanged();
            }

            liveRebuild = EditorGUILayout.ToggleLeft("Live scene rebuild", liveRebuild);
            EditorGUILayout.LabelField("Floor area", $"{level.TotalFloorArea():0.0} m²");
        }

        void DrawValidation()
        {
            bool clean = true;

            for (int i = 0; i < level.Rooms.Count; i++)
            {
                var room = level.Rooms[i];
                if (room == null) continue;

                if (!room.IsValid)
                {
                    EditorGUILayout.HelpBox($"'{room.name}' has fewer than 3 points.", MessageType.Error);
                    clean = false;
                }
                else if (!Poly2D.IsSimple(room.outline))
                {
                    EditorGUILayout.HelpBox($"'{room.name}' crosses itself.", MessageType.Error);
                    clean = false;
                }
            }

            foreach (var doorway in level.Doorways)
            {
                if (doorway == null) continue;
                if (DistanceToNearestWall(doorway.center) <= level.DoorwaySnapDistance) continue;

                EditorGUILayout.HelpBox("A doorway is not on a wall, so it cuts nothing.", MessageType.Warning);
                clean = false;
            }

            if (level.Rooms.Count > 0 && level.RoomIndexAt(level.RobotSpawn) < 0)
            {
                EditorGUILayout.HelpBox("The spawn point is outside every room.", MessageType.Warning);
                clean = false;
            }
            else
            {
                var orphans = level.UnreachableRooms();
                if (orphans.Count > 0)
                {
                    var names = new List<string>();
                    foreach (int index in orphans) names.Add(NameOfRoom(index));

                    EditorGUILayout.HelpBox("No doorway path to: " + string.Join(", ", names), MessageType.Warning);
                    clean = false;
                }
            }

            if (clean) EditorGUILayout.HelpBox("Floor plan looks good.", MessageType.Info);
        }

        void DrawStatusBar()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                GUILayout.Label($"x {cursorLevel.x:0.00}  y {cursorLevel.y:0.00} m", EditorStyles.miniLabel);
                GUILayout.Space(12f);

                var room = level.GetRoom(selectedRoom);
                if (room != null)
                {
                    GUILayout.Label(
                        $"{room.name} — {room.outline.Count} points, {room.Area:0.0} m²",
                        EditorStyles.miniLabel);
                }
                else if (penPoints.Count > 0)
                {
                    GUILayout.Label($"Pen: {penPoints.Count} point(s)", EditorStyles.miniLabel);
                }

                GUILayout.FlexibleSpace();
                GUILayout.Label($"{level.Rooms.Count} rooms · {level.TotalFloorArea():0.0} m²", EditorStyles.miniLabel);
                GUILayout.Space(10f);
                GUILayout.Label($"{zoom:0} px/m", EditorStyles.miniLabel);
            }
        }

        // ---------------------------------------------------------------- transforms

        Vector2 ToScreen(Vector2 levelPoint) => new Vector2(
            canvasRect.center.x + (levelPoint.x - viewCenter.x) * zoom,
            canvasRect.center.y - (levelPoint.y - viewCenter.y) * zoom);

        Vector2 ToLevel(Vector2 screenPoint) => new Vector2(
            viewCenter.x + (screenPoint.x - canvasRect.center.x) / zoom,
            viewCenter.y - (screenPoint.y - canvasRect.center.y) / zoom);

        Vector2 ToRaster(Vector2 levelPoint) => ToScreen(levelPoint) - canvasRect.position;

        void FrameAll()
        {
            if (level == null) return;

            var bounds = level.Rooms.Count > 0 ? level.Bounds() : new Rect(-3f, -3f, 6f, 6f);
            FrameBounds(bounds);
        }

        void FrameRoom(Room room)
        {
            if (room == null || !room.IsValid) return;
            FrameBounds(Poly2D.Bounds(room.outline));
        }

        void FrameBounds(Rect bounds)
        {
            viewCenter = bounds.center;

            float width = Mathf.Max(0.5f, canvasRect.width > 1f ? canvasRect.width : 800f);
            float height = Mathf.Max(0.5f, canvasRect.height > 1f ? canvasRect.height : 500f);

            zoom = Mathf.Clamp(
                Mathf.Min(width / (bounds.width + 1.5f), height / (bounds.height + 1.5f)),
                MinZoom, MaxZoom);

            rasterDirty = true;
            Repaint();
        }

        // ---------------------------------------------------------------- canvas input

        void HandleCanvasInput(Rect canvas)
        {
            var e = Event.current;
            int controlId = GUIUtility.GetControlID(CanvasHint, FocusType.Passive);
            bool inside = canvas.Contains(e.mousePosition);

            if (inside) cursorLevel = ToLevel(e.mousePosition);

            switch (e.type)
            {
                case EventType.ScrollWheel when inside:
                {
                    Vector2 anchor = ToLevel(e.mousePosition);
                    zoom = Mathf.Clamp(zoom * (1f - e.delta.y * 0.04f), MinZoom, MaxZoom);

                    // Keep the point under the cursor pinned while zooming.
                    Vector2 after = ToLevel(e.mousePosition);
                    viewCenter += anchor - after;

                    rasterDirty = true;
                    e.Use();
                    Repaint();
                    break;
                }

                case EventType.MouseDown when inside && IsPanGesture(e):
                    drag = Drag.Pan;
                    GUIUtility.hotControl = controlId;
                    e.Use();
                    break;

                case EventType.MouseDown when inside && e.button == 1:
                    ShowContextMenu(ToLevel(e.mousePosition));
                    e.Use();
                    break;

                case EventType.MouseDown when inside && e.button == 0:
                    GUIUtility.hotControl = controlId;
                    OnCanvasMouseDown(e);
                    e.Use();
                    Repaint();
                    break;

                case EventType.MouseDrag when GUIUtility.hotControl == controlId:
                    OnCanvasDrag(e);
                    e.Use();
                    Repaint();
                    break;

                case EventType.MouseUp when GUIUtility.hotControl == controlId:
                    OnCanvasMouseUp();
                    GUIUtility.hotControl = 0;
                    e.Use();
                    Repaint();
                    break;

                case EventType.MouseMove when inside:
                    if (tool == Tool.Pen && penPoints.Count > 0) rasterDirty = true;
                    Repaint();
                    break;

                // Only when no text or numeric field has focus, so typing a room name
                // does not fire tool shortcuts.
                case EventType.KeyDown when GUIUtility.keyboardControl == 0:
                    if (HandleShortcut(e)) { e.Use(); Repaint(); }
                    break;
            }
        }

        static bool IsPanGesture(Event e) => e.button == 2 || (e.button == 0 && e.alt);

        void OnCanvasMouseDown(Event e)
        {
            Vector2 point = ToLevel(e.mousePosition);

            switch (tool)
            {
                case Tool.Select: BeginSelectDrag(e, point); break;

                case Tool.Rect:
                    drag = Drag.RectCreate;
                    rectStartLevel = Snap(point);
                    break;

                case Tool.Pen: AddPenPoint(point); break;

                case Tool.Wall: BeginWallAction(point); break;

                case Tool.Doorway: PlaceDoorwayAt(point); break;

                case Tool.Spawn:
                    Undo.RecordObject(level, "Move Robot Spawn");
                    level.RobotSpawn = Snap(point);
                    MarkLevelChanged();
                    break;
            }
        }

        void BeginSelectDrag(Event e, Vector2 point)
        {
            float grab = HandlePixels / zoom;

            // Vertices of the selected room take priority, so points stay grabbable
            // even when they sit on top of another room.
            var room = level.GetRoom(selectedRoom);
            if (room != null && room.IsValid)
            {
                for (int v = 0; v < room.outline.Count; v++)
                {
                    if (Vector2.Distance(room.outline[v], point) > grab) continue;

                    if (e.clickCount == 2 && room.outline.Count > 3)
                    {
                        Undo.RecordObject(level, "Delete Point");
                        room.RemoveVertex(v);
                        MarkLevelChanged();
                        return;
                    }

                    drag = Drag.Vertex;
                    dragRoom = selectedRoom;
                    dragVertex = v;
                    return;
                }

                if (e.clickCount == 2 && TryFindEdge(room, point, grab, out int edgeIndex, out Vector2 onEdge))
                {
                    Undo.RecordObject(level, "Add Point");
                    room.InsertVertex(edgeIndex, onEdge);
                    drag = Drag.Vertex;
                    dragRoom = selectedRoom;
                    dragVertex = edgeIndex + 1;
                    MarkLevelChanged();
                    return;
                }
            }

            for (int i = 0; i < level.Doorways.Count; i++)
            {
                if (level.Doorways[i] == null) continue;
                if (Vector2.Distance(level.Doorways[i].center, point) > grab) continue;

                drag = Drag.Doorway;
                dragDoorway = i;
                selectedDoorway = i;
                selectedRoom = -1;
                rasterDirty = true;
                return;
            }

            if (Vector2.Distance(level.RobotSpawn, point) <= grab)
            {
                drag = Drag.Spawn;
                return;
            }

            int hit = level.RoomIndexAt(point);
            selectedRoom = hit;
            selectedDoorway = -1;
            rasterDirty = true;

            if (hit < 0) return;

            drag = Drag.Room;
            dragRoom = hit;
            dragAnchorLevel = point;
            dragLastLevel = point;
        }

        void OnCanvasDrag(Event e)
        {
            Vector2 point = ToLevel(e.mousePosition);

            switch (drag)
            {
                case Drag.Pan:
                    viewCenter -= new Vector2(e.delta.x, -e.delta.y) / zoom;
                    rasterDirty = true;
                    break;

                case Drag.Vertex:
                {
                    var room = level.GetRoom(dragRoom);
                    if (room == null || dragVertex < 0 || dragVertex >= room.outline.Count) break;

                    Undo.RecordObject(level, "Move Point");
                    room.outline[dragVertex] = Snap(point, dragRoom, dragVertex);
                    MarkLevelChanged();
                    break;
                }

                case Drag.Room:
                {
                    var room = level.GetRoom(dragRoom);
                    if (room == null) break;

                    Vector2 delta = point - dragLastLevel;
                    if (delta.sqrMagnitude < 1e-10f) break;

                    Undo.RecordObject(level, "Move Room");
                    for (int v = 0; v < room.outline.Count; v++) room.outline[v] += delta;

                    // Carry this room's doorways along so connections survive the move.
                    foreach (var doorway in level.Doorways)
                        if (doorway != null && doorway.Links(dragRoom)) doorway.center += delta;

                    dragLastLevel = point;
                    MarkLevelChanged();
                    break;
                }

                case Drag.Doorway:
                {
                    var doorway = level.Doorways[dragDoorway];
                    if (doorway == null) break;

                    Undo.RecordObject(level, "Move Doorway");
                    doorway.center = SnapToNearestWall(point, out int roomA, out int roomB);
                    if (roomA >= 0) doorway.roomA = roomA;
                    if (roomB >= 0) doorway.roomB = roomB;
                    MarkLevelChanged();
                    break;
                }

                case Drag.Spawn:
                    Undo.RecordObject(level, "Move Robot Spawn");
                    level.RobotSpawn = Snap(point);
                    MarkLevelChanged();
                    break;

                case Drag.RectCreate:
                case Drag.WallCreate:
                    rasterDirty = true;
                    break;
            }
        }

        void OnCanvasMouseUp()
        {
            if (drag == Drag.RectCreate) CommitRectRoom();
            if (drag == Drag.WallCreate) CommitWallStroke();

            drag = Drag.None;
            dragRoom = -1;
            dragVertex = -1;
            dragDoorway = -1;

            PushToScene();
        }

        void CommitRectRoom()
        {
            Vector2 end = Snap(cursorLevel);
            Vector2 min = Vector2.Min(rectStartLevel, end);
            Vector2 max = Vector2.Max(rectStartLevel, end);

            if (Event.current != null && Event.current.shift)
            {
                float side = Mathf.Max(max.x - min.x, max.y - min.y);
                max = min + new Vector2(side, side);
            }

            if (max.x - min.x < 0.15f || max.y - min.y < 0.15f)
            {
                rasterDirty = true;
                return;
            }

            AddRoom(new List<Vector2>
            {
                new Vector2(min.x, min.y),
                new Vector2(max.x, min.y),
                new Vector2(max.x, max.y),
                new Vector2(min.x, max.y),
            });
        }

        void AddPenPoint(Vector2 point)
        {
            Vector2 snapped = Snap(point);

            if (penPoints.Count >= 3 &&
                Vector2.Distance(snapped, penPoints[0]) <= HandlePixels * 1.6f / zoom)
            {
                ClosePenPolygon();
                return;
            }

            penPoints.Add(snapped);
            rasterDirty = true;
        }

        void ClosePenPolygon()
        {
            if (penPoints.Count >= 3) AddRoom(new List<Vector2>(penPoints));

            penPoints.Clear();
            rasterDirty = true;
        }

        // ---------------------------------------------------------------- walls

        /// <summary>
        /// Wall tool click: on a room edge it toggles that single wall, on a free-standing
        /// wall it deletes it, and anywhere else it starts drawing a new one.
        /// </summary>
        void BeginWallAction(Vector2 point)
        {
            float grab = HandlePixels * 1.6f / zoom;

            int strokeIndex = FindWallStroke(point, grab);
            if (strokeIndex >= 0)
            {
                Undo.RecordObject(level, "Delete Wall");
                level.WallStrokes.RemoveAt(strokeIndex);
                MarkLevelChanged();
                return;
            }

            if (TryFindAnyEdge(point, grab, out int roomIndex, out int edgeIndex, out _))
            {
                var room = level.Rooms[roomIndex];
                bool present = room.HasWall(edgeIndex);

                Undo.RecordObject(level, present ? "Delete Wall" : "Restore Wall");
                room.SetWall(edgeIndex, !present);

                selectedRoom = roomIndex;
                MarkLevelChanged();
                return;
            }

            drag = Drag.WallCreate;
            wallStartLevel = Snap(point);
        }

        void CommitWallStroke()
        {
            Vector2 end = WallStrokeEnd();
            if (Vector2.Distance(wallStartLevel, end) < 0.1f)
            {
                rasterDirty = true;
                return;
            }

            Undo.RecordObject(level, "Draw Wall");
            level.WallStrokes.Add(new WallStroke { a = wallStartLevel, b = end });
            MarkLevelChanged();
        }

        Vector2 WallStrokeEnd()
        {
            Vector2 end = Snap(cursorLevel);
            if (Event.current == null || !Event.current.shift) return end;

            // Shift locks the wall to the nearest 45°.
            Vector2 delta = end - wallStartLevel;
            float angle = Mathf.Round(Mathf.Atan2(delta.y, delta.x) / (Mathf.PI / 4f)) * (Mathf.PI / 4f);
            float length = delta.magnitude;

            return wallStartLevel + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * length;
        }

        int FindWallStroke(Vector2 point, float tolerance)
        {
            for (int i = 0; i < level.WallStrokes.Count; i++)
            {
                var stroke = level.WallStrokes[i];
                if (stroke == null) continue;

                if (Poly2D.DistanceToSegment(point, stroke.a, stroke.b, out _) <= tolerance) return i;
            }
            return -1;
        }

        /// <summary>Nearest room edge to a point, across every room.</summary>
        bool TryFindAnyEdge(Vector2 point, float tolerance, out int roomIndex, out int edgeIndex, out Vector2 onEdge)
        {
            roomIndex = -1;
            edgeIndex = -1;
            onEdge = point;

            float best = tolerance;

            for (int r = 0; r < level.Rooms.Count; r++)
            {
                var room = level.Rooms[r];
                if (room == null || !room.IsValid) continue;

                int count = room.outline.Count;
                for (int e = 0; e < count; e++)
                {
                    Vector2 a = room.outline[e];
                    Vector2 b = room.outline[(e + 1) % count];

                    float distance = Poly2D.DistanceToSegment(point, a, b, out float t);
                    if (distance > best) continue;

                    best = distance;
                    roomIndex = r;
                    edgeIndex = e;
                    onEdge = Vector2.Lerp(a, b, t);
                }
            }

            return roomIndex >= 0;
        }

        bool HandleShortcut(Event e)
        {
            switch (e.keyCode)
            {
                case KeyCode.V: SetTool(Tool.Select); return true;
                case KeyCode.R: SetTool(Tool.Rect); return true;
                case KeyCode.P: SetTool(Tool.Pen); return true;
                case KeyCode.W: SetTool(Tool.Wall); return true;
                case KeyCode.D: SetTool(Tool.Doorway); return true;
                case KeyCode.S: SetTool(Tool.Spawn); return true;
                case KeyCode.F: FrameAll(); return true;

                case KeyCode.Return:
                case KeyCode.KeypadEnter:
                    if (penPoints.Count >= 3) { ClosePenPolygon(); return true; }
                    return false;

                case KeyCode.Escape:
                    if (penPoints.Count > 0) { penPoints.Clear(); rasterDirty = true; return true; }
                    if (selectedRoom >= 0) { selectedRoom = -1; rasterDirty = true; return true; }
                    return false;

                case KeyCode.Backspace when penPoints.Count > 0:
                    penPoints.RemoveAt(penPoints.Count - 1);
                    rasterDirty = true;
                    return true;

                case KeyCode.Delete:
                case KeyCode.Backspace:
                    if (selectedRoom >= 0) { DeleteRoom(selectedRoom); return true; }
                    return false;

                default:
                    return false;
            }
        }

        void SetTool(Tool next)
        {
            if (next != Tool.Pen && penPoints.Count > 0) penPoints.Clear();
            tool = next;
            rasterDirty = true;
            Repaint();
        }

        void ShowContextMenu(Vector2 point)
        {
            var menu = new GenericMenu();
            float grab = HandlePixels / zoom;
            int roomIndex = level.RoomIndexAt(point);
            var room = level.GetRoom(roomIndex);

            if (room != null)
            {
                int vertexIndex = -1;
                for (int v = 0; v < room.outline.Count; v++)
                    if (Vector2.Distance(room.outline[v], point) <= grab) { vertexIndex = v; break; }

                if (vertexIndex >= 0 && room.outline.Count > 3)
                {
                    int captured = vertexIndex;
                    menu.AddItem(new GUIContent("Delete Point"), false, () =>
                    {
                        Undo.RecordObject(level, "Delete Point");
                        room.RemoveVertex(captured);
                        MarkLevelChanged();
                    });
                }
                else if (TryFindEdge(room, point, grab * 2f, out int edgeIndex, out Vector2 onEdge))
                {
                    menu.AddItem(new GUIContent("Insert Point Here"), false, () =>
                    {
                        Undo.RecordObject(level, "Add Point");
                        room.InsertVertex(edgeIndex, onEdge);
                        MarkLevelChanged();
                    });
                }

                menu.AddSeparator(string.Empty);

                if (level.Palette != null)
                {
                    for (int i = 0; i < level.Palette.Count; i++)
                    {
                        int captured = i;
                        menu.AddItem(
                            new GUIContent("Floor Covering/" + level.Palette.LabelOf(i)),
                            room.floorIndex == i,
                            () =>
                            {
                                Undo.RecordObject(level, "Change Floor Covering");
                                room.floorIndex = captured;
                                MarkLevelChanged();
                            });
                    }
                }

                if (TryFindAnyEdge(point, grab * 3f, out int wallRoom, out int wallEdge, out _))
                {
                    bool present = level.Rooms[wallRoom].HasWall(wallEdge);
                    menu.AddItem(new GUIContent(present ? "Delete This Wall" : "Restore This Wall"), false, () =>
                    {
                        Undo.RecordObject(level, present ? "Delete Wall" : "Restore Wall");
                        level.Rooms[wallRoom].SetWall(wallEdge, !present);
                        MarkLevelChanged();
                    });
                }

                menu.AddItem(new GUIContent("Walls/Remove All"), false, () =>
                {
                    Undo.RecordObject(level, "Remove Walls");
                    room.SetAllWalls(false);
                    MarkLevelChanged();
                });
                menu.AddItem(new GUIContent("Walls/Restore All"), false, () =>
                {
                    Undo.RecordObject(level, "Restore Walls");
                    room.SetAllWalls(true);
                    MarkLevelChanged();
                });

                menu.AddItem(new GUIContent("Duplicate Room"), false, () => DuplicateRoom(roomIndex));
                menu.AddItem(new GUIContent("Delete Room"), false, () => DeleteRoom(roomIndex));
            }
            else
            {
                menu.AddDisabledItem(new GUIContent("No room here"));
                menu.AddSeparator(string.Empty);
                menu.AddItem(new GUIContent("Frame All"), false, FrameAll);
            }

            menu.ShowAsContext();
        }

        // ---------------------------------------------------------------- canvas drawing

        void DrawCanvas(Rect canvas)
        {
            if (Event.current.type != EventType.Repaint)
            {
                DrawCanvasOverlay(canvas);
                return;
            }

            if (raster.Resize(Mathf.FloorToInt(canvas.width), Mathf.FloorToInt(canvas.height)))
                rasterDirty = true;

            if (rasterDirty)
            {
                RebuildRaster();
                rasterDirty = false;
            }

            GUI.DrawTexture(canvas, raster.Texture, ScaleMode.StretchToFill, false);
            DrawCanvasOverlay(canvas);
        }

        void RebuildRaster()
        {
            raster.Clear(new Color32(28, 30, 35, 255));

            if (showGrid) DrawGridInto();
            DrawRoomsInto();
            DrawGhostWallsInto();
            DrawWallsInto();
            DrawDoorwaysInto();
            DrawSpawnInto();
            DrawToolPreviewInto();

            raster.Apply();
        }

        void DrawGridInto()
        {
            float step = SnapSteps[SnapSteps.Length - 1];
            for (int i = 0; i < SnapSteps.Length; i++)
            {
                if (SnapSteps[i] * zoom < 48f) continue;
                step = SnapSteps[i];
                break;
            }

            Vector2 topLeft = ToLevel(canvasRect.position);
            Vector2 bottomRight = ToLevel(canvasRect.position + canvasRect.size);

            var minor = new Color32(255, 255, 255, 14);
            var major = new Color32(255, 255, 255, 30);
            var axis = new Color32(120, 180, 255, 90);

            int firstX = Mathf.FloorToInt(topLeft.x / step);
            int lastX = Mathf.CeilToInt(bottomRight.x / step);
            if (lastX - firstX <= 600)
            {
                for (int i = firstX; i <= lastX; i++)
                {
                    float x = ToRaster(new Vector2(i * step, 0f)).x;
                    var color = i == 0 ? axis : (i % 5 == 0 ? major : minor);
                    raster.FillRect(x, 0f, 1f, raster.Height, color);
                }
            }

            int firstY = Mathf.FloorToInt(bottomRight.y / step);
            int lastY = Mathf.CeilToInt(topLeft.y / step);
            if (lastY - firstY <= 600)
            {
                for (int i = firstY; i <= lastY; i++)
                {
                    float y = ToRaster(new Vector2(0f, i * step)).y;
                    var color = i == 0 ? axis : (i % 5 == 0 ? major : minor);
                    raster.FillRect(0f, y, raster.Width, 1f, color);
                }
            }
        }

        void DrawRoomsInto()
        {
            for (int i = 0; i < level.Rooms.Count; i++)
            {
                var room = level.Rooms[i];
                if (room == null || !room.IsValid) continue;

                var screen = new Vector2[room.outline.Count];
                for (int v = 0; v < room.outline.Count; v++) screen[v] = ToRaster(room.outline[v]);

                var fill = level.ColorOf(room);
                bool selected = i == selectedRoom;

                raster.FillPolygon(screen, Poly2D.Triangulate(room.outline), fill);

                var outline = selected
                    ? new Color(1f, 0.84f, 0.3f)
                    : new Color(fill.r * 0.55f, fill.g * 0.55f, fill.b * 0.55f);

                raster.DrawPolyline(screen, selected ? 2.5f : 1.5f, outline, true);
            }
        }

        void DrawWallsInto()
        {
            float thickness = Mathf.Max(2f, level.WallThickness * zoom);
            Color wall = level.WallColor;

            foreach (var segment in LevelGeometry.CollectWallSegments(level))
                raster.DrawSegment(ToRaster(segment.a), ToRaster(segment.b), thickness, wall);
        }

        /// <summary>Faint dashes along edges whose wall was deleted, so the opening reads as deliberate.</summary>
        void DrawGhostWallsInto()
        {
            var ghost = new Color(1f, 1f, 1f, 0.16f);

            foreach (var room in level.Rooms)
            {
                if (room == null || !room.IsValid) continue;

                int count = room.outline.Count;
                for (int e = 0; e < count; e++)
                {
                    if (room.HasWall(e)) continue;

                    Vector2 a = room.outline[e];
                    Vector2 b = room.outline[(e + 1) % count];

                    float length = Vector2.Distance(a, b);
                    int dashes = Mathf.Clamp(Mathf.RoundToInt(length * 4f), 2, 80);

                    for (int d = 0; d < dashes; d += 2)
                    {
                        Vector2 from = Vector2.Lerp(a, b, d / (float)dashes);
                        Vector2 to = Vector2.Lerp(a, b, (d + 1) / (float)dashes);
                        raster.DrawSegment(ToRaster(from), ToRaster(to), 2f, ghost);
                    }
                }
            }
        }

        void DrawDoorwaysInto()
        {
            for (int i = 0; i < level.Doorways.Count; i++)
            {
                var doorway = level.Doorways[i];
                if (doorway == null) continue;

                var center = ToRaster(doorway.center);
                var color = i == selectedDoorway
                    ? new Color(0.4f, 0.95f, 1f)
                    : new Color(0.3f, 0.72f, 0.88f);

                raster.DrawCircle(center, doorway.width * 0.5f * zoom, 1.5f, color);
                raster.FillDisc(center, 3.5f, color);
            }
        }

        void DrawSpawnInto()
        {
            var center = ToRaster(level.RobotSpawn);
            var color = new Color(0.25f, 1f, 0.5f);

            raster.DrawCircle(center, Mathf.Max(6f, 0.17f * zoom), 2f, color);
            raster.FillDisc(center, 3f, color);
        }

        void DrawToolPreviewInto()
        {
            if (tool == Tool.Rect && drag == Drag.RectCreate)
            {
                Vector2 end = Snap(cursorLevel);
                Vector2 min = Vector2.Min(rectStartLevel, end);
                Vector2 max = Vector2.Max(rectStartLevel, end);

                if (Event.current != null && Event.current.shift)
                {
                    float side = Mathf.Max(max.x - min.x, max.y - min.y);
                    max = min + new Vector2(side, side);
                }

                var corners = new[]
                {
                    ToRaster(new Vector2(min.x, min.y)), ToRaster(new Vector2(max.x, min.y)),
                    ToRaster(new Vector2(max.x, max.y)), ToRaster(new Vector2(min.x, max.y)),
                };

                var preview = level.Palette != null ? level.Palette.ColorOf(paintFloor) : Color.gray;
                preview.a = 0.45f;

                raster.FillPolygon(corners, new[] { 0, 1, 2, 0, 2, 3 }, preview);
                raster.DrawPolyline(corners, 1.5f, new Color(1f, 0.84f, 0.3f), true);
            }

            if (tool == Tool.Wall && drag == Drag.WallCreate)
            {
                float thickness = Mathf.Max(2f, level.WallThickness * zoom);
                raster.DrawSegment(
                    ToRaster(wallStartLevel), ToRaster(WallStrokeEnd()), thickness,
                    new Color(1f, 0.84f, 0.3f));
            }

            if (tool != Tool.Pen || penPoints.Count == 0) return;

            var penColor = new Color(1f, 0.84f, 0.3f);
            var screen = new Vector2[penPoints.Count];
            for (int i = 0; i < penPoints.Count; i++) screen[i] = ToRaster(penPoints[i]);

            if (penPoints.Count >= 3)
            {
                var fill = level.Palette != null ? level.Palette.ColorOf(paintFloor) : Color.gray;
                fill.a = 0.35f;
                raster.FillPolygon(screen, Poly2D.Triangulate(penPoints), fill);
            }

            raster.DrawPolyline(screen, 2f, penColor, false);

            // Rubber-band from the last point to the cursor.
            var cursor = ToRaster(cursorLevel);
            raster.DrawSegment(screen[screen.Length - 1], cursor, 1.5f, new Color(1f, 0.84f, 0.3f, 0.7f));

            foreach (var point in screen) raster.FillDisc(point, 3.5f, penColor);

            // Highlight the first point once the path can be closed.
            if (penPoints.Count >= 3)
                raster.DrawCircle(screen[0], 7f, 2f, new Color(0.3f, 1f, 0.5f));
        }

        void DrawCanvasOverlay(Rect canvas)
        {
            GUI.BeginClip(canvas);
            var origin = canvasRect.position;
            canvasRect.position = Vector2.zero;

            for (int i = 0; i < level.Rooms.Count; i++)
            {
                var room = level.Rooms[i];
                if (room == null || !room.IsValid) continue;

                var center = ToScreen(room.Center);
                GUI.Label(new Rect(center.x - 60f, center.y - 8f, 120f, 16f), room.name, roomLabelStyle);
            }

            var selected = level.GetRoom(selectedRoom);
            if (selected != null && selected.IsValid)
            {
                foreach (var vertex in selected.outline)
                {
                    var p = ToScreen(vertex);
                    var box = new Rect(p.x - 4f, p.y - 4f, 8f, 8f);

                    EditorGUI.DrawRect(box, new Color(0.1f, 0.1f, 0.12f));
                    EditorGUI.DrawRect(
                        new Rect(box.x + 1f, box.y + 1f, box.width - 2f, box.height - 2f),
                        new Color(1f, 0.87f, 0.35f));
                }
            }

            canvasRect.position = origin;
            GUI.EndClip();
        }

        // ---------------------------------------------------------------- helpers

        Vector2 Snap(Vector2 point, int skipRoom = -1, int skipVertex = -1)
        {
            if (snapToVertices)
            {
                float threshold = HandlePixels * 1.4f / zoom;
                float best = threshold;
                Vector2 result = point;
                bool found = false;

                for (int r = 0; r < level.Rooms.Count; r++)
                {
                    var room = level.Rooms[r];
                    if (room == null) continue;

                    for (int v = 0; v < room.outline.Count; v++)
                    {
                        if (r == skipRoom && v == skipVertex) continue;

                        float distance = Vector2.Distance(room.outline[v], point);
                        if (distance > best) continue;

                        best = distance;
                        result = room.outline[v];
                        found = true;
                    }
                }

                if (found) return result;
            }

            if (!snapToGrid || snapIncrement <= 0f) return point;

            return new Vector2(
                Mathf.Round(point.x / snapIncrement) * snapIncrement,
                Mathf.Round(point.y / snapIncrement) * snapIncrement);
        }

        static bool TryFindEdge(Room room, Vector2 point, float tolerance, out int edgeIndex, out Vector2 onEdge)
        {
            edgeIndex = -1;
            onEdge = point;

            float best = tolerance;
            int count = room.outline.Count;

            for (int e = 0; e < count; e++)
            {
                Vector2 a = room.outline[e];
                Vector2 b = room.outline[(e + 1) % count];

                float distance = Poly2D.DistanceToSegment(point, a, b, out float t);
                if (distance > best) continue;

                best = distance;
                edgeIndex = e;
                onEdge = Vector2.Lerp(a, b, t);
            }

            return edgeIndex >= 0;
        }

        float DistanceToNearestWall(Vector2 point)
        {
            float best = float.MaxValue;

            foreach (var room in level.Rooms)
            {
                if (room == null || !room.IsValid) continue;

                int count = room.outline.Count;
                for (int e = 0; e < count; e++)
                {
                    float distance = Poly2D.DistanceToSegment(
                        point, room.outline[e], room.outline[(e + 1) % count], out _);
                    if (distance < best) best = distance;
                }
            }

            return best;
        }

        /// <summary>
        /// Pulls a point onto the closest room edge and reports the two closest rooms, which
        /// become the doorway's endpoints. Room B stays -1 for a wall onto the outside.
        /// </summary>
        Vector2 SnapToNearestWall(Vector2 point, out int roomA, out int roomB)
        {
            roomA = -1;
            roomB = -1;

            Vector2 bestPoint = point;
            float bestDistance = float.MaxValue;
            var perRoom = new List<KeyValuePair<int, float>>();

            for (int i = 0; i < level.Rooms.Count; i++)
            {
                var room = level.Rooms[i];
                if (room == null || !room.IsValid) continue;

                float roomBest = float.MaxValue;
                int count = room.outline.Count;

                for (int e = 0; e < count; e++)
                {
                    Vector2 a = room.outline[e];
                    Vector2 b = room.outline[(e + 1) % count];

                    float distance = Poly2D.DistanceToSegment(point, a, b, out float t);
                    if (distance < roomBest) roomBest = distance;

                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        bestPoint = Vector2.Lerp(a, b, t);
                    }
                }

                perRoom.Add(new KeyValuePair<int, float>(i, roomBest));
            }

            if (bestDistance > 1f) return point;

            perRoom.Sort((x, y) => x.Value.CompareTo(y.Value));
            float tolerance = level.DoorwaySnapDistance;

            if (perRoom.Count > 0 && perRoom[0].Value <= tolerance) roomA = perRoom[0].Key;
            if (perRoom.Count > 1 && perRoom[1].Value <= tolerance) roomB = perRoom[1].Key;

            return bestPoint;
        }

        void PlaceDoorwayAt(Vector2 point)
        {
            Vector2 snapped = SnapToNearestWall(point, out int roomA, out int roomB);
            if (roomA < 0)
            {
                ShowNotification(new GUIContent("No wall there"));
                return;
            }

            Undo.RecordObject(level, "Place Doorway");
            level.Doorways.Add(new Doorway { roomA = roomA, roomB = roomB, center = snapped, width = 0.9f });
            selectedDoorway = level.Doorways.Count - 1;
            MarkLevelChanged();
        }

        string NameOfRoom(int index)
        {
            var room = level.GetRoom(index);
            return room != null ? room.name : "Outside";
        }

        void AddRoom(List<Vector2> outline)
        {
            Undo.RecordObject(level, "Add Room");

            var room = new Room
            {
                name = $"Room {level.Rooms.Count + 1}",
                outline = outline,
                floorIndex = paintFloor,
            };
            room.SetAllWalls(newRoomsHaveWalls);

            selectedRoom = level.AddRoom(room);

            tool = Tool.Select;
            MarkLevelChanged();
        }

        void DuplicateRoom(int index)
        {
            var source = level.GetRoom(index);
            if (source == null) return;

            Undo.RecordObject(level, "Duplicate Room");
            var copy = new Room
            {
                name = source.name + " Copy",
                floorIndex = source.floorIndex,
                outline = new List<Vector2>(source.outline),
            };

            var offset = new Vector2(Poly2D.Bounds(source.outline).width + 0.4f, 0f);
            for (int v = 0; v < copy.outline.Count; v++) copy.outline[v] += offset;

            selectedRoom = level.AddRoom(copy);
            MarkLevelChanged();
        }

        void DeleteRoom(int index)
        {
            Undo.RecordObject(level, "Delete Room");
            level.RemoveRoom(index);
            selectedRoom = -1;
            selectedDoorway = -1;
            MarkLevelChanged();
        }

        void MarkLevelChanged()
        {
            rasterDirty = true;
            EditorUtility.SetDirty(level);

            if (drag == Drag.None) PushToScene();
            Repaint();
        }

        void PushToScene()
        {
            if (!liveRebuild) return;

            var renderer = ResolveSceneRenderer();
            if (renderer != null) renderer.Rebuild();
            SceneView.RepaintAll();
        }

        LevelRenderer ResolveSceneRenderer()
        {
            if (cachedRenderer != null && cachedRendererLevel == level) return cachedRenderer;

            cachedRenderer = LevelRenderer.FindFor(level);
            cachedRendererLevel = level;
            return cachedRenderer;
        }
    }
}
