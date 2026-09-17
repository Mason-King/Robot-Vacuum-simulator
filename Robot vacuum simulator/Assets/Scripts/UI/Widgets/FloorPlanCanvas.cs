using System;
using System.Collections.Generic;
using RobotVacuum.Level;
using UnityEngine;
using UnityEngine.UIElements;

namespace RobotVacuum.LevelEditor
{
    /// <summary>
    /// The top-down drawing surface. Floor plans are painted with UI Toolkit's vector
    /// <see cref="Painter2D"/>, which fills arbitrary polygons with anti-aliasing and is clipped by
    /// the element, so no software rasteriser or texture upload is needed.
    /// <para>
    /// Level space is metres with y up; element space is pixels with y down. <see cref="ToLocal"/>
    /// and <see cref="ToLevel"/> convert between them.
    /// </para>
    /// </summary>
    public sealed class FloorPlanCanvas : VisualElement
    {
        enum Drag { None, Pan, Vertex, Room, Doorway, Spawn, RectCreate, WallCreate, Obstacle, ObstacleCreate, ObstacleHandle }

        public const float PixelsPerMetreAt100 = 50f;
        const float MinZoom = 4f;
        const float MaxZoom = 600f;
        const float DragThresholdPixels = 3f;
        const float SpawnRadius = 0.17f;

        static readonly Color Accent = new Color(0.357f, 0.553f, 0.937f);
        static readonly Color AccentSoft = new Color(0.357f, 0.553f, 0.937f, 0.14f);
        static readonly Color Danger = new Color(0.937f, 0.353f, 0.404f);
        static readonly Color Success = new Color(0.235f, 0.796f, 0.541f);
        static readonly Color Warning = new Color(0.949f, 0.702f, 0.298f);
        static readonly Color DoorColor = new Color(0.36f, 0.82f, 0.95f);
        static readonly Color HandleFill = new Color(0.97f, 0.98f, 1f);
        static readonly Color Shadow = new Color(0f, 0f, 0f, 0.35f);

        readonly EditorSession session;
        readonly VisualElement labelLayer;
        readonly Label measureLabel;
        readonly List<Label> roomLabels = new List<Label>();
        readonly List<Vector2> penPoints = new List<Vector2>();
        readonly List<Vector2> patternSegments = new List<Vector2>();

        float zoom = 60f;
        Vector2 viewCenter;
        bool framed;

        Vector2 cursorLocal;
        Vector2 cursorLevel;
        bool cursorInside;
        bool shiftHeld;
        bool spaceHeld;

        Drag drag = Drag.None;
        int capturedPointer = -1;
        bool dragMoved;
        bool dragRecorded;
        Vector2 pressLocal;
        Vector2 pressLevel;
        int dragRoom = -1;
        int dragVertex = -1;
        int pendingInsertEdge = -1;
        int dragDoorway = -1;
        int dragObstacle = -1;
        Vector2 dragAnchor;
        Vector2 dragObstacleOrigin;
        int dragHandle = -1;
        bool dragHandleIsSide;
        Vector2 dragOriginalSize;
        readonly List<Vector2> dragOriginalOutline = new List<Vector2>();
        readonly List<KeyValuePair<Doorway, Vector2>> dragOriginalDoorways = new List<KeyValuePair<Doorway, Vector2>>();
        Vector2 rectStart;
        Vector2 wallStart;

        // Hover state, recomputed on pointer move.
        int hoverRoom = -1;
        int hoverVertex = -1;
        int hoverMidpoint = -1;
        int hoverDoorway = -1;
        int hoverObstacle = -1;
        int hoverObstacleCorner = -1;
        int hoverObstacleSide = -1;
        bool hoverSpawn;
        int hoverEdgeRoom = -1;
        int hoverEdge = -1;
        int hoverStroke = -1;
        bool hoverPenClose;

        public FloorPlanCanvas(EditorSession session)
        {
            this.session = session;

            AddToClassList("le-canvas");
            focusable = true;
            generateVisualContent += OnGenerateVisualContent;

            labelLayer = Ui.Div(this, "le-canvas__labels");
            labelLayer.pickingMode = PickingMode.Ignore;

            measureLabel = Ui.Text(labelLayer, string.Empty, "le-measure");
            measureLabel.pickingMode = PickingMode.Ignore;

            RegisterCallback<PointerDownEvent>(OnPointerDown);
            RegisterCallback<PointerMoveEvent>(OnPointerMove);
            RegisterCallback<PointerUpEvent>(OnPointerUp);
            RegisterCallback<PointerCaptureOutEvent>(_ => CancelDrag());
            RegisterCallback<PointerLeaveEvent>(OnPointerLeave);
            RegisterCallback<WheelEvent>(OnWheel);
            RegisterCallback<GeometryChangedEvent>(OnGeometryChanged);

            session.LevelChanged += OnLevelChanged;
            session.SelectionChanged += Invalidate;
            session.OptionsChanged += Invalidate;
            session.ToolChanged += OnToolChanged;
        }

        /// <summary>Zoom, pan or cursor moved; the status bar listens to this.</summary>
        public event Action ViewChanged;

        /// <summary>Right-click: panel position and the level point under it.</summary>
        public event Action<Vector2, Vector2> ContextRequested;

        public float Zoom => zoom;
        public int ZoomPercent => Mathf.RoundToInt(zoom / PixelsPerMetreAt100 * 100f);
        public Vector2 CursorLevel => cursorLevel;
        public bool CursorInside => cursorInside;
        public int PenPointCount => penPoints.Count;

        public bool SpaceHeld
        {
            get => spaceHeld;
            set => spaceHeld = value;
        }

        public void Unbind()
        {
            session.LevelChanged -= OnLevelChanged;
            session.SelectionChanged -= Invalidate;
            session.OptionsChanged -= Invalidate;
            session.ToolChanged -= OnToolChanged;
        }

        void OnLevelChanged(bool live) => Invalidate();

        void OnToolChanged()
        {
            if (session.Tool != EditorTool.Pen) penPoints.Clear();
            ClearHover();
            Invalidate();
        }

        /// <summary>Repositions overlay labels and schedules a repaint.</summary>
        public void Invalidate()
        {
            UpdateRoomLabels();
            UpdateMeasureLabel();
            MarkDirtyRepaint();
        }

        // ---------------------------------------------------------------- view

        Vector2 Center => new Vector2(contentRect.width * 0.5f, contentRect.height * 0.5f);

        public Vector2 ToLocal(Vector2 level) => new Vector2(
            Center.x + (level.x - viewCenter.x) * zoom,
            Center.y - (level.y - viewCenter.y) * zoom);

        public Vector2 ToLevel(Vector2 local) => new Vector2(
            viewCenter.x + (local.x - Center.x) / zoom,
            viewCenter.y - (local.y - Center.y) / zoom);

        public void FrameAll()
        {
            var level = session.Level;
            FrameBounds(level.Rooms.Count > 0 ? level.Bounds() : new Rect(-3f, -3f, 6f, 6f));
        }

        public void FrameRoom(int index)
        {
            var room = session.Level.GetRoom(index);
            if (room != null && room.IsValid) FrameBounds(Poly2D.Bounds(room.outline));
        }

        void FrameBounds(Rect bounds)
        {
            if (contentRect.width < 2f || contentRect.height < 2f)
            {
                framed = false;
                return;
            }

            framed = true;
            viewCenter = bounds.center;

            // Leave room for the floating tool rail and bottom bars.
            float width = Mathf.Max(64f, contentRect.width - 160f);
            float height = Mathf.Max(64f, contentRect.height - 140f);
            zoom = Mathf.Clamp(Mathf.Min(width / Mathf.Max(bounds.width, 0.5f), height / Mathf.Max(bounds.height, 0.5f)), MinZoom, MaxZoom);

            Invalidate();
            ViewChanged?.Invoke();
        }

        public void ZoomBy(float factor) => ZoomAround(Center, factor);

        void ZoomAround(Vector2 anchorLocal, float factor)
        {
            Vector2 before = ToLevel(anchorLocal);
            zoom = Mathf.Clamp(zoom * factor, MinZoom, MaxZoom);
            viewCenter += before - ToLevel(anchorLocal);

            Invalidate();
            ViewChanged?.Invoke();
        }

        void OnGeometryChanged(GeometryChangedEvent evt)
        {
            if (!framed) FrameAll();
            else Invalidate();
        }

        // ---------------------------------------------------------------- input

        static bool IsMac =>
            Application.platform == RuntimePlatform.OSXPlayer || Application.platform == RuntimePlatform.OSXEditor;

        void OnPointerDown(PointerDownEvent evt)
        {
            Focus();
            UpdateCursor(evt.position, evt.shiftKey);
            if (drag != Drag.None) return;

            bool contextClick = evt.button == 1 || (IsMac && evt.button == 0 && evt.ctrlKey && !evt.commandKey);
            bool panClick = evt.button == 2 || (evt.button == 0 && (evt.altKey || spaceHeld));

            if (panClick)
            {
                BeginDrag(Drag.Pan, evt.pointerId);
                evt.StopPropagation();
                return;
            }

            if (contextClick)
            {
                if (penPoints.Count > 0) penPoints.Clear();
                if (session.Tool == EditorTool.Select)
                {
                    int hit = RoomHit(cursorLevel);
                    if (hit >= 0) session.SelectRoom(hit);
                }

                Invalidate();
                ContextRequested?.Invoke(evt.position, cursorLevel);
                evt.StopPropagation();
                return;
            }

            if (evt.button != 0) return;

            pressLocal = cursorLocal;
            pressLevel = cursorLevel;

            switch (session.Tool)
            {
                case EditorTool.Select: BeginSelect(evt.clickCount, evt.pointerId); break;

                case EditorTool.Rect:
                    rectStart = session.Snap(cursorLevel, zoom);
                    BeginDrag(Drag.RectCreate, evt.pointerId);
                    break;

                case EditorTool.Obstacle:
                    rectStart = session.Snap(cursorLevel, zoom);
                    BeginDrag(Drag.ObstacleCreate, evt.pointerId);
                    break;

                case EditorTool.Pen: AddPenPoint(); break;

                case EditorTool.Wall: BeginWallAction(evt.pointerId); break;

                case EditorTool.Doorway: session.PlaceDoorway(cursorLevel); break;

                case EditorTool.Spawn:
                    session.SetSpawn(session.Snap(cursorLevel, zoom));
                    BeginDrag(Drag.Spawn, evt.pointerId);
                    dragRecorded = true;
                    break;
            }

            Invalidate();
            evt.StopPropagation();
        }

        void OnPointerMove(PointerMoveEvent evt)
        {
            Vector2 previous = cursorLocal;
            UpdateCursor(evt.position, evt.shiftKey);

            if (drag == Drag.Pan)
            {
                Vector2 delta = cursorLocal - previous;
                viewCenter -= new Vector2(delta.x, -delta.y) / zoom;
                Invalidate();
            }
            else if (drag != Drag.None)
            {
                ContinueDrag();
                Invalidate();
            }
            else
            {
                UpdateHover();
                Invalidate();
            }

            ViewChanged?.Invoke();
        }

        void OnPointerUp(PointerUpEvent evt)
        {
            if (drag == Drag.None || evt.pointerId != capturedPointer) return;

            UpdateCursor(evt.position, evt.shiftKey);
            var finished = drag;
            bool recorded = dragRecorded;
            EndDrag();

            switch (finished)
            {
                case Drag.RectCreate: CommitRect(); break;
                case Drag.WallCreate: CommitWallStroke(); break;
                case Drag.ObstacleCreate: CommitObstacle(); break;
                case Drag.Room:
                case Drag.Vertex:
                case Drag.Doorway:
                case Drag.Spawn:
                case Drag.Obstacle:
                case Drag.ObstacleHandle:
                    if (recorded) session.Commit();
                    break;
            }

            UpdateHover();
            Invalidate();
            evt.StopPropagation();
        }

        void OnPointerLeave(PointerLeaveEvent evt)
        {
            if (drag != Drag.None) return;
            cursorInside = false;
            ClearHover();
            Invalidate();
            ViewChanged?.Invoke();
        }

        void OnWheel(WheelEvent evt)
        {
            UpdateCursor(evt.mousePosition, evt.shiftKey);

            // Deltas range from ~1 per notch to ~120 depending on platform; clamp so no step jumps.
            float steps = Mathf.Clamp(evt.delta.y, -12f, 12f);
            ZoomAround(cursorLocal, Mathf.Exp(-steps * 0.05f));
            evt.StopPropagation();
        }

        void UpdateCursor(Vector2 panelPosition, bool shift)
        {
            cursorLocal = this.WorldToLocal(panelPosition);
            cursorLevel = ToLevel(cursorLocal);
            cursorInside = contentRect.Contains(cursorLocal);
            shiftHeld = shift;
        }

        void BeginDrag(Drag kind, int pointerId)
        {
            drag = kind;
            dragMoved = false;
            dragRecorded = false;
            capturedPointer = pointerId;
            this.CapturePointer(pointerId);
        }

        /// <summary>Clears drag state before releasing capture, so the capture-out callback is a no-op.</summary>
        void EndDrag()
        {
            int pointer = capturedPointer;
            drag = Drag.None;
            capturedPointer = -1;
            dragRoom = -1;
            dragVertex = -1;
            dragDoorway = -1;
            dragObstacle = -1;
            dragHandle = -1;
            pendingInsertEdge = -1;

            if (pointer >= 0 && this.HasPointerCapture(pointer)) this.ReleasePointer(pointer);
        }

        void CancelDrag()
        {
            if (drag == Drag.None) return;

            bool recorded = dragRecorded;
            EndDrag();
            if (recorded) session.Commit();
            Invalidate();
        }

        /// <summary>Called by the app on Escape. Returns true when something was cancelled.</summary>
        public bool CancelInteraction()
        {
            if (penPoints.Count > 0)
            {
                penPoints.Clear();
                Invalidate();
                return true;
            }

            if (drag == Drag.RectCreate || drag == Drag.WallCreate || drag == Drag.ObstacleCreate)
            {
                EndDrag();
                Invalidate();
                return true;
            }

            return false;
        }

        // ---------------------------------------------------------------- select tool

        int RoomHit(Vector2 point)
        {
            // The selected room wins overlaps so it stays draggable when something sits on top of it.
            var selected = session.SelectedRoomData;
            if (selected != null && selected.IsValid && selected.Contains(point)) return session.SelectedRoom;
            return session.Level.RoomIndexAt(point);
        }

        void BeginSelect(int clickCount, int pointerId)
        {
            float grab = EditorSession.HandlePixels / zoom;
            var point = cursorLevel;
            int selectedIndex = session.SelectedRoom;
            var selected = session.SelectedRoomData;

            if (selected != null && selected.IsValid)
            {
                int vertex = session.FindVertex(selectedIndex, point, grab);
                if (vertex >= 0)
                {
                    if (clickCount == 2)
                    {
                        if (selected.outline.Count > 3) session.RemoveVertex(selectedIndex, vertex);
                        return;
                    }

                    BeginDrag(Drag.Vertex, pointerId);
                    dragRoom = selectedIndex;
                    dragVertex = vertex;
                    return;
                }

                int midpoint = FindMidpoint(selected, point, grab * 1.3f);
                if (midpoint >= 0)
                {
                    BeginDrag(Drag.Vertex, pointerId);
                    dragRoom = selectedIndex;
                    pendingInsertEdge = midpoint;
                    return;
                }

                if (clickCount == 2 && EditorSession.TryFindEdge(selected, point, grab, out int edge, out Vector2 onEdge))
                {
                    session.InsertVertex(selectedIndex, edge, onEdge);
                    BeginDrag(Drag.Vertex, pointerId);
                    dragRoom = selectedIndex;
                    dragVertex = edge + 1;
                    dragRecorded = true;
                    return;
                }
            }

            if (TryFindObstacleHandle(point, grab, out int handle, out bool isSide))
            {
                var picked = session.SelectedObstacleData;
                BeginDrag(Drag.ObstacleHandle, pointerId);
                dragObstacle = session.SelectedObstacle;
                dragHandle = handle;
                dragHandleIsSide = isSide;
                dragObstacleOrigin = picked.center;
                dragOriginalSize = picked.size;
                return;
            }

            int doorway = session.FindDoorway(point, grab * 1.2f);
            if (doorway >= 0)
            {
                session.SelectDoorway(doorway);
                BeginDrag(Drag.Doorway, pointerId);
                dragDoorway = doorway;
                return;
            }

            if (Vector2.Distance(session.Level.RobotSpawn, point) <= Mathf.Max(grab * 1.5f, SpawnRadius))
            {
                BeginDrag(Drag.Spawn, pointerId);
                return;
            }

            // Furniture sits on top of rooms, so it wins the click.
            int obstacle = session.FindObstacle(point);
            if (obstacle >= 0)
            {
                session.SelectObstacle(obstacle);
                BeginDrag(Drag.Obstacle, pointerId);
                dragObstacle = obstacle;
                dragObstacleOrigin = session.Level.Obstacles[obstacle].center;
                return;
            }

            int hit = RoomHit(point);
            session.SelectRoom(hit);
            if (hit < 0) return;

            BeginDrag(Drag.Room, pointerId);
            dragRoom = hit;

            var room = session.Level.GetRoom(hit);
            dragOriginalOutline.Clear();
            dragOriginalOutline.AddRange(room.outline);

            // Snap using the corner nearest the grab point, so rooms click together at their corners.
            dragAnchor = room.outline[0];
            foreach (var corner in room.outline)
                if (Vector2.Distance(corner, point) < Vector2.Distance(dragAnchor, point)) dragAnchor = corner;

            dragOriginalDoorways.Clear();
            foreach (var door in session.Level.Doorways)
                if (door != null && door.Links(hit)) dragOriginalDoorways.Add(new KeyValuePair<Doorway, Vector2>(door, door.center));
        }

        static int FindMidpoint(Room room, Vector2 point, float tolerance)
        {
            int count = room.outline.Count;
            for (int e = 0; e < count; e++)
            {
                Vector2 mid = (room.outline[e] + room.outline[(e + 1) % count]) * 0.5f;
                if (Vector2.Distance(mid, point) <= tolerance) return e;
            }
            return -1;
        }

        void ContinueDrag()
        {
            if (!dragMoved)
            {
                if (Vector2.Distance(cursorLocal, pressLocal) < DragThresholdPixels) return;
                dragMoved = true;
            }

            switch (drag)
            {
                case Drag.Vertex: DragVertex(); break;
                case Drag.Room: DragRoom(); break;
                case Drag.Obstacle: DragObstacle(); break;
                case Drag.ObstacleHandle: DragObstacleHandle(); break;

                case Drag.Doorway:
                    RecordOnce("Move Doorway");
                    session.MoveDoorwayLive(dragDoorway, cursorLevel);
                    break;

                case Drag.Spawn:
                    RecordOnce("Move Vacuum Start");
                    session.Level.RobotSpawn = session.Snap(cursorLevel, zoom);
                    session.NotifyLiveChange();
                    break;
            }
        }

        void RecordOnce(string label)
        {
            if (dragRecorded) return;
            session.Checkpoint(label);
            dragRecorded = true;
        }

        void DragVertex()
        {
            var room = session.Level.GetRoom(dragRoom);
            if (room == null) return;

            if (pendingInsertEdge >= 0)
            {
                Vector2 mid = (room.outline[pendingInsertEdge] + room.outline[(pendingInsertEdge + 1) % room.outline.Count]) * 0.5f;
                session.InsertVertex(dragRoom, pendingInsertEdge, mid);
                dragVertex = pendingInsertEdge + 1;
                pendingInsertEdge = -1;
                dragRecorded = true;
            }

            if (dragVertex < 0 || dragVertex >= room.outline.Count) return;

            RecordOnce("Move Point");
            room.outline[dragVertex] = session.Snap(cursorLevel, zoom, dragRoom, dragVertex);
            session.NotifyLiveChange();
        }

        void DragRoom()
        {
            var room = session.Level.GetRoom(dragRoom);
            if (room == null || dragOriginalOutline.Count != room.outline.Count) return;

            Vector2 target = dragAnchor + (cursorLevel - pressLevel);
            Vector2 offset = session.Snap(target, zoom, dragRoom) - dragAnchor;

            RecordOnce("Move Room");
            for (int v = 0; v < room.outline.Count; v++) room.outline[v] = dragOriginalOutline[v] + offset;

            // Carry this room's doorways along so its connections survive the move.
            foreach (var pair in dragOriginalDoorways) pair.Key.center = pair.Value + offset;

            session.NotifyLiveChange();
        }

        void DragObstacle()
        {
            if (session.GetObstacle(dragObstacle) == null) return;

            // Grid snapping only: snapping a piece's centre to a room corner would park it on the wall.
            RecordOnce("Move Furniture");
            session.MoveObstacleLive(dragObstacle, session.SnapToGridStep(dragObstacleOrigin + (cursorLevel - pressLevel)));
        }

        /// <summary>Resizes from a corner or side handle, keeping the opposite corner or side where it was.</summary>
        void DragObstacleHandle()
        {
            var obstacle = session.GetObstacle(dragObstacle);
            if (obstacle == null) return;

            RecordOnce("Resize Furniture");

            Vector2 target = session.Snap(cursorLevel, zoom);
            Vector2 center, size;
            if (dragHandleIsSide)
                Obstacle.ResizeFromSide(dragObstacleOrigin, dragOriginalSize, obstacle.rotation, dragHandle, target, out center, out size);
            else
                Obstacle.ResizeFromCorner(dragObstacleOrigin, dragOriginalSize, obstacle.rotation, dragHandle, target, out center, out size);

            session.SetObstacleBoundsLive(dragObstacle, center, size);
        }

        /// <summary>A corner or side handle of the selected furniture under the point, as on rooms.</summary>
        bool TryFindObstacleHandle(Vector2 point, float grab, out int handle, out bool isSide)
        {
            handle = -1;
            isSide = false;

            var obstacle = session.SelectedObstacleData;
            if (obstacle == null || session.Tool != EditorTool.Select) return false;

            var corners = obstacle.Corners();
            for (int c = 0; c < 4; c++)
            {
                if (Vector2.Distance(corners[c], point) > grab) continue;
                handle = c;
                return true;
            }

            for (int s = 0; s < 4; s++)
            {
                Vector2 a = corners[s], b = corners[(s + 1) % 4];
                if (Vector2.Distance(a, b) * zoom < 28f) continue;
                if (Vector2.Distance((a + b) * 0.5f, point) > grab * 1.3f) continue;

                handle = s;
                isSide = true;
                return true;
            }

            return false;
        }

        // ---------------------------------------------------------------- drawing tools

        Vector2 RectEnd(out Vector2 min, out Vector2 max)
        {
            Vector2 end = session.Snap(cursorLevel, zoom);
            min = Vector2.Min(rectStart, end);
            max = Vector2.Max(rectStart, end);

            if (shiftHeld)
            {
                float side = Mathf.Max(max.x - min.x, max.y - min.y);
                max = min + new Vector2(side, side);
            }

            return end;
        }

        void CommitRect()
        {
            RectEnd(out Vector2 min, out Vector2 max);
            if (max.x - min.x < 0.15f || max.y - min.y < 0.15f) return;

            session.AddRoom(new List<Vector2>
            {
                new Vector2(min.x, min.y),
                new Vector2(max.x, min.y),
                new Vector2(max.x, max.y),
                new Vector2(min.x, max.y),
            });
            session.Tool = EditorTool.Select;
        }

        void CommitObstacle()
        {
            RectEnd(out Vector2 min, out Vector2 max);
            Vector2 size = max - min;

            // A click without a real drag drops the kind's usual footprint where it was pressed.
            if (size.x < Obstacle.MinSize || size.y < Obstacle.MinSize)
                session.AddObstacle(rectStart, Obstacle.DefaultSize(session.ObstacleKind));
            else
                session.AddObstacle((min + max) * 0.5f, size);

            session.Tool = EditorTool.Select;
        }

        void AddPenPoint()
        {
            Vector2 snapped = session.Snap(cursorLevel, zoom);

            if (penPoints.Count >= 3 && Vector2.Distance(snapped, penPoints[0]) <= EditorSession.HandlePixels * 1.6f / zoom)
            {
                ClosePen();
                return;
            }

            if (penPoints.Count > 0 && Vector2.Distance(snapped, penPoints[penPoints.Count - 1]) < 1e-4f) return;
            penPoints.Add(snapped);
        }

        /// <summary>Finishes the pen path as a room. Returns false when there are too few points.</summary>
        public bool ClosePen()
        {
            if (penPoints.Count < 3) return false;

            var outline = new List<Vector2>(penPoints);
            penPoints.Clear();
            session.AddRoom(outline);
            session.Tool = EditorTool.Select;
            return true;
        }

        public bool RemoveLastPenPoint()
        {
            if (penPoints.Count == 0) return false;
            penPoints.RemoveAt(penPoints.Count - 1);
            Invalidate();
            return true;
        }

        void BeginWallAction(int pointerId)
        {
            float grab = EditorSession.HandlePixels * 1.6f / zoom;

            int stroke = session.FindWallStroke(cursorLevel, grab);
            if (stroke >= 0)
            {
                session.DeleteWallStroke(stroke);
                return;
            }

            if (session.TryFindAnyEdge(cursorLevel, grab, out int roomIndex, out int edge, out _))
            {
                var room = session.Level.GetRoom(roomIndex);
                session.SetWall(roomIndex, edge, !room.HasWall(edge));
                return;
            }

            wallStart = session.Snap(cursorLevel, zoom);
            BeginDrag(Drag.WallCreate, pointerId);
        }

        Vector2 WallEnd()
        {
            Vector2 end = session.Snap(cursorLevel, zoom);
            if (!shiftHeld) return end;

            // Shift locks to the nearest 45°.
            Vector2 delta = end - wallStart;
            float step = Mathf.PI / 4f;
            float angle = Mathf.Round(Mathf.Atan2(delta.y, delta.x) / step) * step;
            return wallStart + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * delta.magnitude;
        }

        void CommitWallStroke()
        {
            Vector2 end = WallEnd();
            if (Vector2.Distance(wallStart, end) < 0.1f) return;
            session.AddWallStroke(wallStart, end);
        }

        // ---------------------------------------------------------------- hover

        void ClearHover()
        {
            hoverRoom = hoverVertex = hoverMidpoint = hoverDoorway = hoverObstacle = hoverEdgeRoom = hoverEdge = hoverStroke = -1;
            hoverObstacleCorner = hoverObstacleSide = -1;
            hoverSpawn = false;
            hoverPenClose = false;
        }

        void UpdateHover()
        {
            ClearHover();
            if (!cursorInside) return;

            float grab = EditorSession.HandlePixels / zoom;
            var point = cursorLevel;

            switch (session.Tool)
            {
                case EditorTool.Select:
                {
                    var selected = session.SelectedRoomData;
                    if (selected != null && selected.IsValid)
                    {
                        hoverVertex = session.FindVertex(session.SelectedRoom, point, grab);
                        if (hoverVertex < 0) hoverMidpoint = FindMidpoint(selected, point, grab * 1.3f);
                        if (hoverVertex >= 0 || hoverMidpoint >= 0) return;
                    }

                    if (TryFindObstacleHandle(point, grab, out int handle, out bool isSide))
                    {
                        if (isSide) hoverObstacleSide = handle;
                        else hoverObstacleCorner = handle;
                        return;
                    }

                    hoverDoorway = session.FindDoorway(point, grab * 1.2f);
                    if (hoverDoorway >= 0) return;

                    hoverSpawn = Vector2.Distance(session.Level.RobotSpawn, point) <= Mathf.Max(grab * 1.5f, SpawnRadius);
                    if (hoverSpawn) return;

                    hoverObstacle = session.FindObstacle(point);
                    if (hoverObstacle >= 0) return;

                    hoverRoom = RoomHit(point);
                    break;
                }

                case EditorTool.Wall:
                {
                    float wallGrab = grab * 1.6f;
                    hoverStroke = session.FindWallStroke(point, wallGrab);
                    if (hoverStroke < 0) session.TryFindAnyEdge(point, wallGrab, out hoverEdgeRoom, out hoverEdge, out _);
                    break;
                }

                case EditorTool.Pen:
                    hoverPenClose = penPoints.Count >= 3 &&
                        Vector2.Distance(session.Snap(point, zoom), penPoints[0]) <= EditorSession.HandlePixels * 1.6f / zoom;
                    break;
            }
        }

        // ---------------------------------------------------------------- overlay labels

        void UpdateRoomLabels()
        {
            var level = session.Level;
            int used = 0;

            for (int i = 0; i < level.Rooms.Count; i++)
            {
                var room = level.Rooms[i];
                if (room == null || !room.IsValid) continue;

                var bounds = Poly2D.Bounds(room.outline);
                float screenWidth = bounds.width * zoom;
                float screenHeight = bounds.height * zoom;
                if (screenWidth < 56f || screenHeight < 26f) continue;

                var label = NextLabel(used++);
                var center = ToLocal(room.Center);
                label.style.left = center.x;
                label.style.top = center.y;
                label.style.display = DisplayStyle.Flex;

                bool showArea = screenWidth > 110f && screenHeight > 60f;
                label.text = showArea
                    ? $"{room.name}  <color=#FFFFFF8C>{room.Area:0.0} m²</color>"
                    : room.name;
                label.EnableInClassList("le-room-label--selected", i == session.SelectedRoom);
                label.EnableInClassList("le-room-label--obstacle", false);
            }

            for (int i = 0; i < level.Obstacles.Count; i++)
            {
                var obstacle = level.Obstacles[i];
                if (obstacle == null || obstacle.size.x * zoom < 48f || obstacle.size.y * zoom < 18f) continue;

                var label = NextLabel(used++);
                var center = ToLocal(obstacle.center);
                label.style.left = center.x;
                label.style.top = center.y;
                label.style.display = DisplayStyle.Flex;
                label.text = obstacle.name;
                label.EnableInClassList("le-room-label--selected", i == session.SelectedObstacle);
                label.EnableInClassList("le-room-label--obstacle", true);
            }

            for (int i = used; i < roomLabels.Count; i++) roomLabels[i].style.display = DisplayStyle.None;
            measureLabel.BringToFront();
        }

        Label NextLabel(int index)
        {
            if (index == roomLabels.Count)
            {
                var created = Ui.Text(labelLayer, string.Empty, "le-room-label");
                created.pickingMode = PickingMode.Ignore;
                created.enableRichText = true;
                roomLabels.Add(created);
            }

            return roomLabels[index];
        }

        void UpdateMeasureLabel()
        {
            string text = null;

            if (drag == Drag.RectCreate || drag == Drag.ObstacleCreate)
            {
                RectEnd(out Vector2 min, out Vector2 max);
                text = $"{max.x - min.x:0.00} × {max.y - min.y:0.00} m";
            }
            else if (drag == Drag.WallCreate)
            {
                text = Ui.FormatMetres(Vector2.Distance(wallStart, WallEnd()));
            }
            else if (session.Tool == EditorTool.Pen && penPoints.Count > 0 && cursorInside)
            {
                text = Ui.FormatMetres(Vector2.Distance(penPoints[penPoints.Count - 1], session.Snap(cursorLevel, zoom)));
            }
            else if (drag == Drag.ObstacleHandle && dragMoved && session.GetObstacle(dragObstacle) != null)
            {
                var size = session.GetObstacle(dragObstacle).size;
                text = $"{size.x:0.00} × {size.y:0.00} m";
            }
            else if (drag == Drag.Vertex && dragMoved && dragVertex >= 0)
            {
                var room = session.Level.GetRoom(dragRoom);
                if (room != null && dragVertex < room.outline.Count)
                {
                    var p = room.outline[dragVertex];
                    text = $"{p.x:0.00}, {p.y:0.00}";
                }
            }

            if (text == null)
            {
                measureLabel.style.display = DisplayStyle.None;
                return;
            }

            measureLabel.text = text;
            measureLabel.style.left = cursorLocal.x + 16f;
            measureLabel.style.top = cursorLocal.y + 18f;
            measureLabel.style.display = DisplayStyle.Flex;
        }

        // ---------------------------------------------------------------- painting

        void OnGenerateVisualContent(MeshGenerationContext context)
        {
            if (contentRect.width < 2f || contentRect.height < 2f) return;

            var painter = context.painter2D;
            painter.lineJoin = LineJoin.Round;

            if (session.ShowGrid) PaintGrid(painter);
            PaintRooms(painter);
            PaintRemovedWalls(painter);
            PaintObstacles(painter);
            PaintWalls(painter);
            PaintDoorways(painter);
            PaintSpawn(painter);
            PaintSelection(painter);
            PaintToolPreview(painter);
        }

        void PaintGrid(Painter2D painter)
        {
            // Pick the smallest "nice" step that still leaves breathing room between lines.
            float[] steps = { 0.05f, 0.1f, 0.25f, 0.5f, 1f, 2f, 5f, 10f, 25f, 50f };
            float step = steps[steps.Length - 1];
            foreach (float candidate in steps)
            {
                if (candidate * zoom < 22f) continue;
                step = candidate;
                break;
            }

            Vector2 topLeft = ToLevel(Vector2.zero);
            Vector2 bottomRight = ToLevel(contentRect.size);

            int firstX = Mathf.FloorToInt(topLeft.x / step), lastX = Mathf.CeilToInt(bottomRight.x / step);
            int firstY = Mathf.FloorToInt(bottomRight.y / step), lastY = Mathf.CeilToInt(topLeft.y / step);
            if (lastX - firstX > 400 || lastY - firstY > 400) return;

            for (int pass = 0; pass < 2; pass++)
            {
                bool major = pass == 1;
                painter.lineWidth = 1f;
                painter.strokeColor = major ? new Color(1f, 1f, 1f, 0.075f) : new Color(1f, 1f, 1f, 0.035f);
                painter.BeginPath();

                for (int i = firstX; i <= lastX; i++)
                {
                    if ((i % 5 == 0) != major || i == 0) continue;
                    float x = ToLocal(new Vector2(i * step, 0f)).x;
                    painter.MoveTo(new Vector2(x, 0f));
                    painter.LineTo(new Vector2(x, contentRect.height));
                }

                for (int i = firstY; i <= lastY; i++)
                {
                    if ((i % 5 == 0) != major || i == 0) continue;
                    float y = ToLocal(new Vector2(0f, i * step)).y;
                    painter.MoveTo(new Vector2(0f, y));
                    painter.LineTo(new Vector2(contentRect.width, y));
                }

                painter.Stroke();
            }

            var origin = ToLocal(Vector2.zero);
            painter.strokeColor = new Color(Accent.r, Accent.g, Accent.b, 0.28f);
            painter.BeginPath();
            painter.MoveTo(new Vector2(origin.x, 0f));
            painter.LineTo(new Vector2(origin.x, contentRect.height));
            painter.MoveTo(new Vector2(0f, origin.y));
            painter.LineTo(new Vector2(contentRect.width, origin.y));
            painter.Stroke();
        }

        void PolygonPath(Painter2D painter, IList<Vector2> points)
        {
            painter.BeginPath();
            painter.MoveTo(ToLocal(points[0]));
            for (int i = 1; i < points.Count; i++) painter.LineTo(ToLocal(points[i]));
            painter.ClosePath();
        }

        void PaintRooms(Painter2D painter)
        {
            var level = session.Level;
            bool selectTool = session.Tool == EditorTool.Select;

            for (int i = 0; i < level.Rooms.Count; i++)
            {
                var room = level.Rooms[i];
                if (room == null || !room.IsValid) continue;

                var fill = level.ColorOf(room);
                fill.a = 1f;
                bool selected = i == session.SelectedRoom;
                bool hovered = selectTool && i == hoverRoom && !selected && drag == Drag.None;

                PolygonPath(painter, room.outline);
                painter.fillColor = fill;
                painter.Fill(FillRule.NonZero);

                // The pattern replaces the current path, so the outline is traced again afterwards.
                PaintFloorPattern(painter, room, level.FloorTypeOf(room), fill);
                PolygonPath(painter, room.outline);

                if (selected || hovered)
                {
                    painter.fillColor = selected ? AccentSoft : new Color(1f, 1f, 1f, 0.05f);
                    painter.Fill(FillRule.NonZero);
                }

                painter.lineWidth = 1f;
                painter.strokeColor = new Color(fill.r * 0.55f, fill.g * 0.55f, fill.b * 0.55f, 1f);
                painter.Stroke();
            }
        }

        /// <summary>Strokes the floor's pattern over its fill, fading it out before it gets too dense to read.</summary>
        void PaintFloorPattern(Painter2D painter, Room room, FloorType floor, Color fill)
        {
            if (floor == null || floor.pattern == FloorPattern.Plain) return;

            float spacingPixels = FloorPatterns.Spacing(floor.pattern) * zoom;
            if (spacingPixels < 4f) return;

            patternSegments.Clear();
            FloorPatterns.Collect(floor.pattern, room.outline, patternSegments);
            if (patternSegments.Count == 0) return;

            float fade = Mathf.InverseLerp(4f, 12f, spacingPixels);
            bool hazard = floor.pattern == FloorPattern.Hazard;

            // Dark lines on light floors and light lines on dark ones, so the pattern shows on any colour.
            float luminance = fill.r * 0.299f + fill.g * 0.587f + fill.b * 0.114f;
            painter.strokeColor = hazard ? new Color(Warning.r, Warning.g, Warning.b, 0.85f * fade)
                : luminance > 0.35f ? new Color(0f, 0f, 0f, 0.16f * fade)
                : new Color(1f, 1f, 1f, 0.14f * fade);
            painter.lineWidth = hazard ? Mathf.Max(2f, spacingPixels * 0.35f) : 1f;
            painter.lineCap = LineCap.Butt;

            painter.BeginPath();
            for (int i = 0; i + 1 < patternSegments.Count; i += 2)
            {
                painter.MoveTo(ToLocal(patternSegments[i]));
                painter.LineTo(ToLocal(patternSegments[i + 1]));
            }
            painter.Stroke();
        }

        /// <summary>Solid furniture blocks the vacuum; translucent furniture with a dashed edge lets it pass under.</summary>
        void PaintObstacles(Painter2D painter)
        {
            var level = session.Level;

            for (int i = 0; i < level.Obstacles.Count; i++)
            {
                var obstacle = level.Obstacles[i];
                if (obstacle == null) continue;

                var corners = obstacle.Corners();
                var color = Obstacle.ColorOf(obstacle.kind);
                bool selected = i == session.SelectedObstacle;
                bool hovered = i == hoverObstacle && !selected && drag == Drag.None;

                PolygonPath(painter, corners);
                painter.fillColor = new Color(color.r, color.g, color.b, obstacle.blocksVacuum ? 1f : 0.4f);
                painter.Fill(FillRule.NonZero);

                if (hovered)
                {
                    painter.fillColor = new Color(1f, 1f, 1f, 0.08f);
                    painter.Fill(FillRule.NonZero);
                }

                var edge = selected
                    ? Accent
                    : new Color(Mathf.Min(1f, color.r * 1.35f), Mathf.Min(1f, color.g * 1.35f), Mathf.Min(1f, color.b * 1.35f), 1f);
                float width = selected ? 2f : 1.5f;

                if (obstacle.blocksVacuum)
                {
                    painter.lineWidth = width;
                    painter.strokeColor = edge;
                    painter.Stroke();
                }
                else
                {
                    DashedPolygon(painter, corners, width, edge);
                }
            }
        }

        void PaintObstacleGhost(Painter2D painter, IList<Vector2> corners, float alpha)
        {
            var color = Obstacle.ColorOf(session.ObstacleKind);

            PolygonPath(painter, corners);
            painter.fillColor = new Color(color.r, color.g, color.b, alpha);
            painter.Fill(FillRule.NonZero);
            painter.lineWidth = 1.5f;
            painter.strokeColor = Accent;
            painter.Stroke();
        }

        void DashedPolygon(Painter2D painter, IList<Vector2> points, float width, Color color)
        {
            painter.lineWidth = width;
            painter.lineCap = LineCap.Butt;
            painter.strokeColor = color;
            painter.BeginPath();

            float dash = 5f / zoom;
            for (int e = 0; e < points.Count; e++)
            {
                Vector2 a = points[e];
                Vector2 b = points[(e + 1) % points.Count];
                int dashes = Mathf.Clamp(Mathf.FloorToInt(Vector2.Distance(a, b) / dash), 2, 400);

                for (int d = 0; d < dashes; d += 2)
                {
                    painter.MoveTo(ToLocal(Vector2.Lerp(a, b, d / (float)dashes)));
                    painter.LineTo(ToLocal(Vector2.Lerp(a, b, (d + 1) / (float)dashes)));
                }
            }

            painter.Stroke();
        }

        void PaintRemovedWalls(Painter2D painter)
        {
            painter.lineWidth = 1.5f;
            painter.lineCap = LineCap.Butt;
            painter.strokeColor = new Color(1f, 1f, 1f, 0.22f);
            painter.BeginPath();

            float dash = 6f / zoom;

            foreach (var room in session.Level.Rooms)
            {
                if (room == null || !room.IsValid) continue;

                int count = room.outline.Count;
                for (int e = 0; e < count; e++)
                {
                    if (room.HasWall(e)) continue;

                    Vector2 a = room.outline[e];
                    Vector2 b = room.outline[(e + 1) % count];
                    float length = Vector2.Distance(a, b);
                    int dashes = Mathf.Clamp(Mathf.FloorToInt(length / dash), 2, 400);

                    for (int d = 0; d < dashes; d += 2)
                    {
                        painter.MoveTo(ToLocal(Vector2.Lerp(a, b, d / (float)dashes)));
                        painter.LineTo(ToLocal(Vector2.Lerp(a, b, (d + 1) / (float)dashes)));
                    }
                }
            }

            painter.Stroke();
        }

        void PaintWalls(Painter2D painter)
        {
            var level = session.Level;
            float half = level.WallThickness * 0.5f;

            painter.lineWidth = Mathf.Max(2f, level.WallThickness * zoom);
            painter.lineCap = LineCap.Butt;
            painter.strokeColor = WallColorOnCanvas(level.WallColor);
            painter.BeginPath();

            foreach (var segment in LevelGeometry.CollectWallSegments(level))
            {
                Vector2 direction = segment.b - segment.a;
                if (direction.sqrMagnitude < 1e-8f) continue;
                direction.Normalize();

                // Extend ends that sit on a corner so walls meet squarely; doorway jambs stay flush.
                Vector2 a = IsCorner(segment.a, segment.roomIndex) ? segment.a - direction * half : segment.a;
                Vector2 b = IsCorner(segment.b, segment.roomIndex) ? segment.b + direction * half : segment.b;

                painter.MoveTo(ToLocal(a));
                painter.LineTo(ToLocal(b));
            }

            painter.Stroke();

            if (session.Tool != EditorTool.Wall) return;

            // Wall tool hover: red for "click removes", blue for "click restores".
            if (hoverStroke >= 0 && hoverStroke < level.WallStrokes.Count)
            {
                var stroke = level.WallStrokes[hoverStroke];
                StrokeLine(painter, stroke.a, stroke.b, Mathf.Max(3f, level.WallThickness * zoom), Danger);
            }
            else if (hoverEdgeRoom >= 0)
            {
                var room = level.GetRoom(hoverEdgeRoom);
                if (room != null && hoverEdge < room.EdgeCount)
                {
                    bool present = room.HasWall(hoverEdge);
                    StrokeLine(painter, room.EdgeStart(hoverEdge), room.EdgeEnd(hoverEdge),
                        Mathf.Max(3f, level.WallThickness * zoom), present ? Danger : Accent);
                }
            }
        }

        /// <summary>The canvas is dark, so a wall colour chosen for the lit scene is lifted to stay readable.</summary>
        static Color WallColorOnCanvas(Color wall)
        {
            Color.RGBToHSV(wall, out float h, out float s, out float v);
            return Color.HSVToRGB(h, s * 0.6f, Mathf.Max(v, 0.82f));
        }

        bool IsCorner(Vector2 point, int roomIndex)
        {
            var level = session.Level;

            if (roomIndex >= 0)
            {
                var room = level.GetRoom(roomIndex);
                if (room == null) return false;
                foreach (var corner in room.outline)
                    if ((corner - point).sqrMagnitude < 1e-8f) return true;
                return false;
            }

            foreach (var stroke in level.WallStrokes)
            {
                if (stroke == null) continue;
                if ((stroke.a - point).sqrMagnitude < 1e-8f || (stroke.b - point).sqrMagnitude < 1e-8f) return true;
            }
            return false;
        }

        void PaintDoorways(Painter2D painter)
        {
            var level = session.Level;

            for (int i = 0; i < level.Doorways.Count; i++)
            {
                var doorway = level.Doorways[i];
                if (doorway == null) continue;

                bool selected = i == session.SelectedDoorway;
                bool hovered = i == hoverDoorway;
                bool onWall = session.NearestWall(doorway.center, out Vector2 direction) <= level.DoorwaySnapDistance;
                var color = !onWall ? Warning : selected ? Accent : DoorColor;

                PaintDoorGlyph(painter, doorway.center, direction, doorway.width, color, selected || hovered ? 1f : 0.9f);

                var center = ToLocal(doorway.center);
                FillCircle(painter, center, selected || hovered ? 6f : 4.5f, Shadow);
                FillCircle(painter, center, selected || hovered ? 5f : 3.5f, color);
            }

            if (session.Tool == EditorTool.Doorway && cursorInside && drag == Drag.None)
            {
                Vector2 snapped = session.SnapToNearestWall(cursorLevel, out int roomA, out _);
                if (roomA >= 0)
                {
                    session.NearestWall(snapped, out Vector2 direction);
                    PaintDoorGlyph(painter, snapped, direction, EditorSession.DefaultDoorwayWidth, DoorColor, 0.55f);
                }
            }
        }

        /// <summary>Two jamb ticks across the wall at the gap's edges, joined by a faint threshold line.</summary>
        void PaintDoorGlyph(Painter2D painter, Vector2 center, Vector2 direction, float width, Color color, float alpha)
        {
            float jamb = Mathf.Max(session.Level.WallThickness * 1.8f, 10f / zoom);
            Vector2 normal = new Vector2(-direction.y, direction.x);
            Vector2 a = center - direction * (width * 0.5f);
            Vector2 b = center + direction * (width * 0.5f);

            var faint = new Color(color.r, color.g, color.b, 0.35f * alpha);
            StrokeLine(painter, a, b, 1.5f, faint);

            var solid = new Color(color.r, color.g, color.b, alpha);
            painter.lineCap = LineCap.Round;
            StrokeLine(painter, a - normal * (jamb * 0.5f), a + normal * (jamb * 0.5f), 2.5f, solid);
            StrokeLine(painter, b - normal * (jamb * 0.5f), b + normal * (jamb * 0.5f), 2.5f, solid);
            painter.lineCap = LineCap.Butt;
        }

        void PaintSpawn(Painter2D painter)
        {
            var spawn = session.Level.RobotSpawn;
            bool active = hoverSpawn || drag == Drag.Spawn;
            PaintRobot(painter, spawn, active ? 1f : 0.95f);

            if (active)
            {
                float radius = Mathf.Max(8f, SpawnRadius * zoom) + 5f;
                StrokeCircle(painter, ToLocal(spawn), radius, 2f, new Color(Success.r, Success.g, Success.b, 0.7f));
            }
        }

        void PaintRobot(Painter2D painter, Vector2 levelPoint, float alpha)
        {
            var center = ToLocal(levelPoint);
            float radius = Mathf.Max(8f, SpawnRadius * zoom);

            FillCircle(painter, center + new Vector2(0f, 1.5f), radius + 1.5f, new Color(0f, 0f, 0f, 0.4f * alpha));
            FillCircle(painter, center, radius, new Color(0.95f, 0.96f, 0.98f, alpha));
            StrokeCircle(painter, center, radius * 0.62f, Mathf.Max(1f, radius * 0.08f), new Color(0.75f, 0.78f, 0.84f, alpha));
            FillCircle(painter, center + new Vector2(0f, -radius * 0.58f), Mathf.Max(2f, radius * 0.2f), new Color(Accent.r, Accent.g, Accent.b, alpha));
        }

        void PaintObstacleHandles(Painter2D painter)
        {
            var obstacle = session.SelectedObstacleData;
            if (obstacle == null || session.Tool != EditorTool.Select || (drag == Drag.Obstacle && dragMoved)) return;

            var corners = obstacle.Corners();
            bool resizing = drag == Drag.ObstacleHandle && dragMoved;

            for (int s = 0; s < 4; s++)
            {
                Vector2 a = corners[s], b = corners[(s + 1) % 4];
                if (Vector2.Distance(a, b) * zoom < 28f) continue;

                bool hot = s == hoverObstacleSide || (resizing && dragHandleIsSide && s == dragHandle);
                PaintMidpointHandle(painter, ToLocal((a + b) * 0.5f), hot);
            }

            for (int c = 0; c < 4; c++)
            {
                bool hot = c == hoverObstacleCorner || (resizing && !dragHandleIsSide && c == dragHandle);
                PaintCornerHandle(painter, ToLocal(corners[c]), hot);
            }
        }

        void PaintSelection(Painter2D painter)
        {
            PaintObstacleHandles(painter);

            var room = session.SelectedRoomData;
            if (room == null || !room.IsValid) return;

            PolygonPath(painter, room.outline);
            painter.lineWidth = 2f;
            painter.strokeColor = Accent;
            painter.Stroke();

            if (session.Tool != EditorTool.Select) return;

            int count = room.outline.Count;
            bool draggingVertex = drag == Drag.Vertex && dragMoved;

            if (!draggingVertex && drag != Drag.Room)
            {
                for (int e = 0; e < count; e++)
                {
                    // Only offer a midpoint handle where the edge is long enough to grab it separately.
                    Vector2 a = room.outline[e], b = room.outline[(e + 1) % count];
                    if (Vector2.Distance(a, b) * zoom < 36f) continue;

                    PaintMidpointHandle(painter, ToLocal((a + b) * 0.5f), e == hoverMidpoint);
                }
            }

            for (int v = 0; v < count; v++)
            {
                PaintCornerHandle(painter, ToLocal(room.outline[v]), v == hoverVertex || (draggingVertex && v == dragVertex));
            }
        }

        void PaintToolPreview(Painter2D painter)
        {
            var level = session.Level;
            var floor = level.Palette != null ? level.Palette.ColorOf(session.PaintFloor) : Color.gray;

            switch (session.Tool)
            {
                case EditorTool.Rect when drag == Drag.RectCreate:
                {
                    RectEnd(out Vector2 min, out Vector2 max);
                    var corners = new[] { min, new Vector2(max.x, min.y), max, new Vector2(min.x, max.y) };

                    PolygonPath(painter, corners);
                    painter.fillColor = new Color(floor.r, floor.g, floor.b, 0.5f);
                    painter.Fill(FillRule.NonZero);
                    painter.lineWidth = 1.5f;
                    painter.strokeColor = Accent;
                    painter.Stroke();
                    break;
                }

                case EditorTool.Obstacle when drag == Drag.ObstacleCreate:
                {
                    RectEnd(out Vector2 min, out Vector2 max);
                    PaintObstacleGhost(painter, new[] { min, new Vector2(max.x, min.y), max, new Vector2(min.x, max.y) }, 0.55f);
                    break;
                }

                case EditorTool.Obstacle when cursorInside && drag == Drag.None:
                    PaintObstacleGhost(painter,
                        LevelData.RectangleOutline(session.Snap(cursorLevel, zoom), Obstacle.DefaultSize(session.ObstacleKind)), 0.3f);
                    break;

                case EditorTool.Wall when drag == Drag.WallCreate:
                    painter.lineCap = LineCap.Round;
                    StrokeLine(painter, wallStart, WallEnd(), Mathf.Max(3f, level.WallThickness * zoom), Accent);
                    painter.lineCap = LineCap.Butt;
                    break;

                case EditorTool.Pen:
                    PaintPenPreview(painter, floor);
                    break;

                case EditorTool.Spawn when cursorInside && drag == Drag.None:
                    PaintRobot(painter, session.Snap(cursorLevel, zoom), 0.45f);
                    break;
            }

            bool drawing = session.Tool == EditorTool.Rect || session.Tool == EditorTool.Pen || session.Tool == EditorTool.Wall
                || session.Tool == EditorTool.Obstacle;
            if (drawing && cursorInside && drag != Drag.Pan)
            {
                // A small reticle shows exactly where the next point will land after snapping.
                Vector2 snapped = ToLocal(drag == Drag.WallCreate ? WallEnd() : session.Snap(cursorLevel, zoom));
                StrokeCircle(painter, snapped, 4.5f, 1.5f, Accent);
                FillCircle(painter, snapped, 1.5f, Accent);
            }
        }

        void PaintPenPreview(Painter2D painter, Color floor)
        {
            if (penPoints.Count == 0) return;

            if (penPoints.Count >= 3)
            {
                PolygonPath(painter, penPoints);
                painter.fillColor = new Color(floor.r, floor.g, floor.b, 0.35f);
                painter.Fill(FillRule.NonZero);
            }

            painter.lineWidth = 2f;
            painter.strokeColor = Accent;
            painter.lineCap = LineCap.Round;
            painter.BeginPath();
            painter.MoveTo(ToLocal(penPoints[0]));
            for (int i = 1; i < penPoints.Count; i++) painter.LineTo(ToLocal(penPoints[i]));
            painter.Stroke();

            if (cursorInside)
            {
                Vector2 target = hoverPenClose ? penPoints[0] : session.Snap(cursorLevel, zoom);
                StrokeLine(painter, penPoints[penPoints.Count - 1], target, 1.5f, new Color(Accent.r, Accent.g, Accent.b, 0.6f));
            }
            painter.lineCap = LineCap.Butt;

            for (int i = 0; i < penPoints.Count; i++)
            {
                var p = ToLocal(penPoints[i]);
                FillCircle(painter, p, 4f, HandleFill);
                StrokeCircle(painter, p, 4f, 1.5f, Accent);
            }

            if (penPoints.Count >= 3)
                StrokeCircle(painter, ToLocal(penPoints[0]), hoverPenClose ? 9f : 7f, 2f, Success);
        }

        void StrokeLine(Painter2D painter, Vector2 levelA, Vector2 levelB, float width, Color color)
        {
            painter.lineWidth = width;
            painter.strokeColor = color;
            painter.BeginPath();
            painter.MoveTo(ToLocal(levelA));
            painter.LineTo(ToLocal(levelB));
            painter.Stroke();
        }

        /// <summary>The square corner handle shared by rooms and furniture.</summary>
        static void PaintCornerHandle(Painter2D painter, Vector2 local, bool hot)
        {
            float half = hot ? 5.5f : 4f;

            painter.BeginPath();
            painter.MoveTo(new Vector2(local.x - half, local.y - half));
            painter.LineTo(new Vector2(local.x + half, local.y - half));
            painter.LineTo(new Vector2(local.x + half, local.y + half));
            painter.LineTo(new Vector2(local.x - half, local.y + half));
            painter.ClosePath();
            painter.fillColor = hot ? Accent : HandleFill;
            painter.Fill(FillRule.NonZero);
            painter.lineWidth = 1.5f;
            painter.strokeColor = Accent;
            painter.Stroke();
        }

        /// <summary>The round side handle shared by rooms and furniture.</summary>
        static void PaintMidpointHandle(Painter2D painter, Vector2 local, bool hot)
        {
            FillCircle(painter, local, hot ? 5f : 3.5f, hot ? Accent : new Color(0.09f, 0.1f, 0.13f, 0.9f));
            StrokeCircle(painter, local, hot ? 5f : 3.5f, 1.5f, Accent);
        }

        static void FillCircle(Painter2D painter, Vector2 localCenter, float radius, Color color)
        {
            painter.fillColor = color;
            painter.BeginPath();
            painter.Arc(localCenter, radius, Angle.Degrees(0f), Angle.Degrees(360f), ArcDirection.Clockwise);
            painter.ClosePath();
            painter.Fill(FillRule.NonZero);
        }

        static void StrokeCircle(Painter2D painter, Vector2 localCenter, float radius, float width, Color color)
        {
            painter.lineWidth = width;
            painter.strokeColor = color;
            painter.BeginPath();
            painter.Arc(localCenter, radius, Angle.Degrees(0f), Angle.Degrees(360f), ArcDirection.Clockwise);
            painter.ClosePath();
            painter.Stroke();
        }
    }
}
