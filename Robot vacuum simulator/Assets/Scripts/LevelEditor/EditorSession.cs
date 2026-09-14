using System;
using System.Collections.Generic;
using RobotVacuum.Level;
using UnityEngine;

namespace RobotVacuum.LevelEditor
{
    public enum EditorTool { Select, Rect, Pen, Wall, Doorway, Spawn }

    public enum IssueSeverity { Warning, Error }

    public struct LevelIssue
    {
        public IssueSeverity severity;
        public string message;

        /// <summary>Room the issue is about, or -1.</summary>
        public int roomIndex;
    }

    /// <summary>
    /// One open floor plan: the live <see cref="LevelData"/>, what is selected, the active tool,
    /// snapping options, and undo history. Every change to the level goes through here so the
    /// canvas, inspector and top bar all hear about it the same way.
    /// </summary>
    public sealed class EditorSession
    {
        public const float HandlePixels = 7f;
        public const float MinDoorwayWidth = 0.3f;
        public const float MaxDoorwayWidth = 3f;
        public const float DefaultDoorwayWidth = 0.9f;

        public static readonly float[] SnapSteps = { 0.05f, 0.1f, 0.25f, 0.5f, 1f };

        readonly EditHistory history = new EditHistory();
        string savedState;
        string name;

        EditorTool tool = EditorTool.Select;
        int selectedRoom = -1;
        int selectedDoorway = -1;
        int paintFloor;
        bool snapToGrid = true;
        bool snapToVertices = true;
        bool showGrid = true;
        bool newRoomsHaveWalls = true;
        float snapIncrement = 0.25f;

        public EditorSession(LevelSnapshot snapshot, FloorPalette palette, string filePath)
        {
            Level = ScriptableObject.CreateInstance<LevelData>();
            Level.name = snapshot.name;
            Level.hideFlags = HideFlags.DontSave;
            Level.Palette = palette;

            snapshot.ApplyTo(Level);
            name = snapshot.name;
            FilePath = filePath;
            savedState = Serialize();
        }

        /// <summary>Level geometry or properties changed. The flag is true mid-drag, before the edit is committed.</summary>
        public event Action<bool> LevelChanged;
        public event Action SelectionChanged;
        public event Action ToolChanged;

        /// <summary>Name, dirty flag, or undo availability changed.</summary>
        public event Action DocumentChanged;

        /// <summary>Snapping, grid, or new-room defaults changed.</summary>
        public event Action OptionsChanged;

        /// <summary>A short message worth surfacing to the user, such as "No wall there".</summary>
        public event Action<string> Notice;

        public LevelData Level { get; }
        public FloorPalette Palette => Level.Palette;
        public string FilePath { get; }
        public string Name => name;
        public bool IsDirty { get; private set; }

        public bool CanUndo => history.CanUndo;
        public bool CanRedo => history.CanRedo;
        public string UndoLabel => history.UndoLabel;
        public string RedoLabel => history.RedoLabel;

        public void Dispose() => UnityEngine.Object.Destroy(Level);

        // ---------------------------------------------------------------- tool & options

        public EditorTool Tool
        {
            get => tool;
            set
            {
                if (tool == value) return;
                tool = value;
                ToolChanged?.Invoke();
            }
        }

        public int PaintFloor
        {
            get => paintFloor;
            set => SetOption(ref paintFloor, Mathf.Max(0, value));
        }

        public bool SnapToGrid { get => snapToGrid; set => SetOption(ref snapToGrid, value); }
        public bool SnapToVertices { get => snapToVertices; set => SetOption(ref snapToVertices, value); }
        public bool ShowGrid { get => showGrid; set => SetOption(ref showGrid, value); }
        public bool NewRoomsHaveWalls { get => newRoomsHaveWalls; set => SetOption(ref newRoomsHaveWalls, value); }

        public float SnapIncrement
        {
            get => snapIncrement;
            set => SetOption(ref snapIncrement, Mathf.Max(0.01f, value));
        }

        void SetOption<T>(ref T field, T value)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return;
            field = value;
            OptionsChanged?.Invoke();
        }

        // ---------------------------------------------------------------- selection

        public int SelectedRoom => selectedRoom;
        public int SelectedDoorway => selectedDoorway;
        public Room SelectedRoomData => Level.GetRoom(selectedRoom);
        public Doorway SelectedDoorwayData => GetDoorway(selectedDoorway);
        public bool HasSelection => selectedRoom >= 0 || selectedDoorway >= 0;

        public Doorway GetDoorway(int index) =>
            index >= 0 && index < Level.Doorways.Count ? Level.Doorways[index] : null;

        public void SelectRoom(int index)
        {
            if (Level.GetRoom(index) == null) index = -1;
            if (selectedRoom == index && selectedDoorway < 0) return;

            selectedRoom = index;
            selectedDoorway = -1;
            SelectionChanged?.Invoke();
        }

        public void SelectDoorway(int index)
        {
            if (GetDoorway(index) == null) index = -1;
            if (selectedDoorway == index && selectedRoom < 0) return;

            selectedDoorway = index;
            selectedRoom = -1;
            SelectionChanged?.Invoke();
        }

        public void ClearSelection()
        {
            if (!HasSelection) return;
            selectedRoom = -1;
            selectedDoorway = -1;
            SelectionChanged?.Invoke();
        }

        /// <summary>Drops selections that no longer point at anything, e.g. after an undo.</summary>
        bool ClampSelection()
        {
            bool changed = false;
            if (selectedRoom >= 0 && Level.GetRoom(selectedRoom) == null) { selectedRoom = -1; changed = true; }
            if (selectedDoorway >= 0 && GetDoorway(selectedDoorway) == null) { selectedDoorway = -1; changed = true; }
            return changed;
        }

        // ---------------------------------------------------------------- history & persistence

        public string Serialize(bool pretty = false) => LevelSnapshot.Serialize(Level, name, pretty);

        /// <summary>
        /// Records the current state for undo. Call once before a discrete edit, or once when a drag
        /// first moves; follow with <see cref="Commit"/> when the edit is finished.
        /// </summary>
        public void Checkpoint(string label) => history.Push(label, Serialize());

        /// <summary>Announces a change made mid-drag. Cheap: no dirty check, no history.</summary>
        public void NotifyLiveChange() => LevelChanged?.Invoke(true);

        /// <summary>Finishes an edit started with <see cref="Checkpoint"/>.</summary>
        public void Commit()
        {
            bool selectionDropped = ClampSelection();
            string current = Serialize();
            history.DiscardIfUnchanged(current);
            IsDirty = current != savedState;

            LevelChanged?.Invoke(false);
            if (selectionDropped) SelectionChanged?.Invoke();
            DocumentChanged?.Invoke();
        }

        void Edit(string label, Action change)
        {
            Checkpoint(label);
            change();
            Commit();
        }

        public bool Undo()
        {
            if (!history.TryUndo(Serialize(), out string state, out string label)) return false;
            Restore(state);
            Notice?.Invoke($"Undo {label}");
            return true;
        }

        public bool Redo()
        {
            if (!history.TryRedo(Serialize(), out string state, out string label)) return false;
            Restore(state);
            Notice?.Invoke($"Redo {label}");
            return true;
        }

        void Restore(string state)
        {
            var snapshot = LevelSnapshot.Parse(state);
            if (snapshot == null) return;

            snapshot.ApplyTo(Level);
            name = snapshot.name;
            Level.name = name;

            ClampSelection();
            IsDirty = Serialize() != savedState;

            LevelChanged?.Invoke(false);
            SelectionChanged?.Invoke();
            DocumentChanged?.Invoke();
        }

        /// <summary>Writes the level to its file. Throws on IO failure so the caller can report it.</summary>
        public void Save()
        {
            LevelLibrary.Write(FilePath, Serialize(true));
            savedState = Serialize();
            IsDirty = false;
            DocumentChanged?.Invoke();
        }

        // ---------------------------------------------------------------- level-wide edits

        public void Rename(string next)
        {
            next = next?.Trim();
            if (string.IsNullOrEmpty(next) || next == name) return;

            Edit("Rename", () =>
            {
                name = next;
                Level.name = next;
            });
        }

        public void SetSpawn(Vector2 point) => Edit("Move Vacuum Start", () => Level.RobotSpawn = point);

        public void SetWallColor(Color color)
        {
            if (Level.WallColor == color) return;
            Edit("Wall Colour", () => Level.WallColor = color);
        }

        /// <summary>For sliders: call <see cref="Checkpoint"/> when the drag starts and <see cref="Commit"/> when it ends.</summary>
        public void SetWallThicknessLive(float value)
        {
            Level.WallThickness = value;
            NotifyLiveChange();
        }

        public void DeleteSelection()
        {
            if (selectedRoom >= 0) DeleteRoom(selectedRoom);
            else if (selectedDoorway >= 0) DeleteDoorway(selectedDoorway);
        }

        // ---------------------------------------------------------------- rooms

        public void AddRoom(List<Vector2> outline)
        {
            Edit("Add Room", () =>
            {
                var room = new Room
                {
                    name = NextRoomName(),
                    outline = outline,
                    floorIndex = paintFloor,
                };
                room.SetAllWalls(newRoomsHaveWalls);

                selectedRoom = Level.AddRoom(room);
                selectedDoorway = -1;
            });

            SelectionChanged?.Invoke();
        }

        string NextRoomName()
        {
            var taken = new List<string>();
            foreach (var room in Level.Rooms)
                if (room != null) taken.Add(room.name);

            for (int n = Level.Rooms.Count + 1; ; n++)
            {
                string candidate = $"Room {n}";
                if (!taken.Contains(candidate)) return candidate;
            }
        }

        public void DeleteRoom(int index)
        {
            var room = Level.GetRoom(index);
            if (room == null) return;

            Edit("Delete Room", () =>
            {
                Level.RemoveRoom(index);
                selectedRoom = -1;
                selectedDoorway = -1;
            });

            SelectionChanged?.Invoke();
            Notice?.Invoke($"Deleted {room.name}");
        }

        public void DuplicateRoom(int index)
        {
            var source = Level.GetRoom(index);
            if (source == null || !source.IsValid) return;

            Edit("Duplicate Room", () =>
            {
                var copy = new Room
                {
                    name = source.name + " copy",
                    floorIndex = source.floorIndex,
                    outline = new List<Vector2>(source.outline),
                    wallEdges = new List<bool>(source.wallEdges ?? new List<bool>()),
                };

                var offset = new Vector2(Poly2D.Bounds(source.outline).width + 0.4f, 0f);
                for (int v = 0; v < copy.outline.Count; v++) copy.outline[v] += offset;
                copy.SyncWallEdges();

                selectedRoom = Level.AddRoom(copy);
                selectedDoorway = -1;
            });

            SelectionChanged?.Invoke();
        }

        public void RenameRoom(int index, string next)
        {
            var room = Level.GetRoom(index);
            next = next?.Trim();
            if (room == null || string.IsNullOrEmpty(next) || room.name == next) return;

            Edit("Rename Room", () => room.name = next);
        }

        /// <summary>Changes a room's covering and remembers it as the covering for the next new room.</summary>
        public void SetRoomFloor(int index, int floor)
        {
            PaintFloor = floor;

            var room = Level.GetRoom(index);
            if (room == null || room.floorIndex == floor) return;

            Edit("Floor Covering", () => room.floorIndex = floor);
        }

        public void SetWall(int roomIndex, int edge, bool present)
        {
            var room = Level.GetRoom(roomIndex);
            if (room == null || room.HasWall(edge) == present) return;

            Edit(present ? "Restore Wall" : "Remove Wall", () => room.SetWall(edge, present));
        }

        public void SetAllWalls(int roomIndex, bool present)
        {
            var room = Level.GetRoom(roomIndex);
            if (room == null) return;

            Edit(present ? "Add All Walls" : "Remove All Walls", () => room.SetAllWalls(present));
        }

        public int WallCount(Room room)
        {
            if (room == null) return 0;

            int count = 0;
            for (int e = 0; e < room.EdgeCount; e++)
                if (room.HasWall(e)) count++;
            return count;
        }

        public void InsertVertex(int roomIndex, int edge, Vector2 point)
        {
            var room = Level.GetRoom(roomIndex);
            if (room == null) return;

            Edit("Add Point", () => room.InsertVertex(edge, point));
        }

        public void RemoveVertex(int roomIndex, int vertex)
        {
            var room = Level.GetRoom(roomIndex);
            if (room == null || room.outline.Count <= 3) return;

            Edit("Delete Point", () => room.RemoveVertex(vertex));
        }

        // ---------------------------------------------------------------- doorways & walls

        public bool PlaceDoorway(Vector2 point)
        {
            Vector2 snapped = SnapToNearestWall(point, out int roomA, out int roomB);
            if (roomA < 0)
            {
                Notice?.Invoke("Click on a wall to place a doorway");
                return false;
            }

            Edit("Add Doorway", () =>
            {
                Level.Doorways.Add(new Doorway
                {
                    roomA = roomA,
                    roomB = roomB,
                    center = snapped,
                    width = DefaultDoorwayWidth,
                });

                selectedDoorway = Level.Doorways.Count - 1;
                selectedRoom = -1;
            });

            SelectionChanged?.Invoke();
            return true;
        }

        /// <summary>Moves a doorway onto the nearest wall and relinks it to the rooms it now joins. No history.</summary>
        public void MoveDoorwayLive(int index, Vector2 point)
        {
            var doorway = GetDoorway(index);
            if (doorway == null) return;

            doorway.center = SnapToNearestWall(point, out int roomA, out int roomB);
            if (roomA >= 0)
            {
                doorway.roomA = roomA;
                doorway.roomB = roomB;
            }

            NotifyLiveChange();
        }

        public void SetDoorwayWidthLive(int index, float width)
        {
            var doorway = GetDoorway(index);
            if (doorway == null) return;

            doorway.width = Mathf.Clamp(width, MinDoorwayWidth, MaxDoorwayWidth);
            NotifyLiveChange();
        }

        public void DeleteDoorway(int index)
        {
            if (GetDoorway(index) == null) return;

            Edit("Delete Doorway", () =>
            {
                Level.Doorways.RemoveAt(index);
                selectedDoorway = -1;
            });

            SelectionChanged?.Invoke();
        }

        public void AddWallStroke(Vector2 a, Vector2 b) =>
            Edit("Draw Wall", () => Level.WallStrokes.Add(new WallStroke { a = a, b = b }));

        public void DeleteWallStroke(int index)
        {
            if (index < 0 || index >= Level.WallStrokes.Count) return;
            Edit("Delete Wall", () => Level.WallStrokes.RemoveAt(index));
        }

        // ---------------------------------------------------------------- queries

        public string NameOfRoom(int index)
        {
            var room = Level.GetRoom(index);
            return room != null ? room.name : "Outside";
        }

        public string DoorwayTitle(Doorway doorway) =>
            doorway == null ? string.Empty : $"{NameOfRoom(doorway.roomA)} ↔ {NameOfRoom(doorway.roomB)}";

        /// <summary>
        /// Snaps onto nearby room corners first (so rooms can share exact edges), then onto the
        /// optional alignment grid. <paramref name="zoom"/> is canvas pixels per metre.
        /// A <paramref name="skipRoom"/> with <paramref name="skipVertex"/> of -1 ignores that whole room.
        /// </summary>
        public Vector2 Snap(Vector2 point, float zoom, int skipRoom = -1, int skipVertex = -1)
        {
            if (snapToVertices)
            {
                float best = HandlePixels * 1.4f / zoom;
                Vector2 result = point;
                bool found = false;

                for (int r = 0; r < Level.Rooms.Count; r++)
                {
                    var room = Level.Rooms[r];
                    if (room == null) continue;
                    if (r == skipRoom && skipVertex < 0) continue;

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

        public int FindVertex(int roomIndex, Vector2 point, float tolerance)
        {
            var room = Level.GetRoom(roomIndex);
            if (room == null) return -1;

            int best = -1;
            float bestDistance = tolerance;
            for (int v = 0; v < room.outline.Count; v++)
            {
                float distance = Vector2.Distance(room.outline[v], point);
                if (distance > bestDistance) continue;
                bestDistance = distance;
                best = v;
            }
            return best;
        }

        public int FindDoorway(Vector2 point, float tolerance)
        {
            for (int i = 0; i < Level.Doorways.Count; i++)
            {
                var doorway = Level.Doorways[i];
                if (doorway != null && Vector2.Distance(doorway.center, point) <= tolerance) return i;
            }
            return -1;
        }

        public int FindWallStroke(Vector2 point, float tolerance)
        {
            for (int i = 0; i < Level.WallStrokes.Count; i++)
            {
                var stroke = Level.WallStrokes[i];
                if (stroke == null) continue;
                if (Poly2D.DistanceToSegment(point, stroke.a, stroke.b, out _) <= tolerance) return i;
            }
            return -1;
        }

        public static bool TryFindEdge(Room room, Vector2 point, float tolerance, out int edgeIndex, out Vector2 onEdge)
        {
            edgeIndex = -1;
            onEdge = point;
            if (room == null || !room.IsValid) return false;

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

        /// <summary>Nearest room edge to a point, across every room.</summary>
        public bool TryFindAnyEdge(Vector2 point, float tolerance, out int roomIndex, out int edgeIndex, out Vector2 onEdge)
        {
            roomIndex = -1;
            edgeIndex = -1;
            onEdge = point;

            float best = tolerance;

            for (int r = 0; r < Level.Rooms.Count; r++)
            {
                var room = Level.Rooms[r];
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

        /// <summary>Distance to the closest room edge, and that edge's direction.</summary>
        public float NearestWall(Vector2 point, out Vector2 direction)
        {
            float best = float.MaxValue;
            direction = Vector2.right;

            foreach (var room in Level.Rooms)
            {
                if (room == null || !room.IsValid) continue;

                int count = room.outline.Count;
                for (int e = 0; e < count; e++)
                {
                    Vector2 a = room.outline[e];
                    Vector2 b = room.outline[(e + 1) % count];

                    float distance = Poly2D.DistanceToSegment(point, a, b, out _);
                    if (distance >= best || (b - a).sqrMagnitude < 1e-8f) continue;

                    best = distance;
                    direction = (b - a).normalized;
                }
            }

            return best;
        }

        /// <summary>
        /// Pulls a point onto the closest room edge and reports the two closest rooms, which
        /// become the doorway's endpoints. Room B stays -1 for a wall onto the outside.
        /// </summary>
        public Vector2 SnapToNearestWall(Vector2 point, out int roomA, out int roomB)
        {
            roomA = -1;
            roomB = -1;

            if (!TryFindAnyEdge(point, 1f, out _, out _, out Vector2 bestPoint)) return point;

            // Distances are measured from the snapped point, so both rooms sharing a wall qualify.
            var perRoom = new List<KeyValuePair<int, float>>();
            for (int i = 0; i < Level.Rooms.Count; i++)
            {
                var room = Level.Rooms[i];
                if (room == null || !room.IsValid) continue;

                float roomBest = float.MaxValue;
                int count = room.outline.Count;
                for (int e = 0; e < count; e++)
                {
                    float distance = Poly2D.DistanceToSegment(
                        bestPoint, room.outline[e], room.outline[(e + 1) % count], out _);
                    if (distance < roomBest) roomBest = distance;
                }

                perRoom.Add(new KeyValuePair<int, float>(i, roomBest));
            }

            perRoom.Sort((x, y) => x.Value.CompareTo(y.Value));
            float tolerance = Level.DoorwaySnapDistance;

            if (perRoom.Count > 0 && perRoom[0].Value <= tolerance) roomA = perRoom[0].Key;
            if (perRoom.Count > 1 && perRoom[1].Value <= tolerance) roomB = perRoom[1].Key;

            return bestPoint;
        }

        /// <summary>Everything that would stop the level playing the way it looks.</summary>
        public List<LevelIssue> Validate()
        {
            var issues = new List<LevelIssue>();

            for (int i = 0; i < Level.Rooms.Count; i++)
            {
                var room = Level.Rooms[i];
                if (room == null) continue;

                if (!room.IsValid)
                    issues.Add(new LevelIssue { severity = IssueSeverity.Error, roomIndex = i, message = $"{room.name} has fewer than 3 corners." });
                else if (!Poly2D.IsSimple(room.outline))
                    issues.Add(new LevelIssue { severity = IssueSeverity.Error, roomIndex = i, message = $"{room.name}'s outline crosses itself." });
            }

            foreach (var doorway in Level.Doorways)
            {
                if (doorway == null) continue;
                if (NearestWall(doorway.center, out _) <= Level.DoorwaySnapDistance) continue;

                issues.Add(new LevelIssue { severity = IssueSeverity.Warning, roomIndex = -1, message = "A doorway isn't on a wall, so it opens nothing." });
            }

            if (Level.Rooms.Count == 0)
            {
                issues.Add(new LevelIssue { severity = IssueSeverity.Warning, roomIndex = -1, message = "Draw a room to get started." });
            }
            else if (Level.RoomIndexAt(Level.RobotSpawn) < 0)
            {
                issues.Add(new LevelIssue { severity = IssueSeverity.Warning, roomIndex = -1, message = "The vacuum starts outside every room." });
            }
            else
            {
                foreach (int index in Level.UnreachableRooms())
                    issues.Add(new LevelIssue { severity = IssueSeverity.Warning, roomIndex = index, message = $"No doorway path to {NameOfRoom(index)}." });
            }

            return issues;
        }
    }
}
