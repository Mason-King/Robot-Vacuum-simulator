using System;
using System.Collections.Generic;
using UnityEngine;

namespace RobotVacuum.Level
{
    /// <summary>
    /// A room outline. Points are in level-local 2D space and may be any simple polygon.
    /// The outline defines the <em>floor</em>; walls are tracked separately per edge so a
    /// single wall can be deleted without changing the floor's shape.
    /// </summary>
    [Serializable]
    public class Room
    {
        public string name = "Room";

        [Tooltip("Outline vertices. Any winding; concave is fine, self-intersecting is not.")]
        public List<Vector2> outline = new List<Vector2>();

        [Tooltip("Index into the level's floor palette.")]
        public int floorIndex;

        [Tooltip("One flag per outline edge. False means that wall has been deleted.")]
        public List<bool> wallEdges = new List<bool>();

        public bool IsValid => outline != null && outline.Count >= 3;
        public Vector2 Center => Poly2D.Centroid(outline);
        public float Area => Poly2D.Area(outline);
        public bool Contains(Vector2 point) => Poly2D.ContainsPoint(outline, point);

        public int EdgeCount => outline?.Count ?? 0;
        public Vector2 EdgeStart(int edge) => outline[edge];
        public Vector2 EdgeEnd(int edge) => outline[(edge + 1) % outline.Count];

        /// <summary>Keeps one wall flag per edge. New edges default to having a wall.</summary>
        public void SyncWallEdges()
        {
            if (wallEdges == null) wallEdges = new List<bool>();

            while (wallEdges.Count < EdgeCount) wallEdges.Add(true);
            while (wallEdges.Count > EdgeCount) wallEdges.RemoveAt(wallEdges.Count - 1);
        }

        public bool HasWall(int edge)
        {
            SyncWallEdges();
            return edge >= 0 && edge < wallEdges.Count && wallEdges[edge];
        }

        public void SetWall(int edge, bool present)
        {
            SyncWallEdges();
            if (edge >= 0 && edge < wallEdges.Count) wallEdges[edge] = present;
        }

        public void ToggleWall(int edge) => SetWall(edge, !HasWall(edge));

        public void SetAllWalls(bool present)
        {
            SyncWallEdges();
            for (int i = 0; i < wallEdges.Count; i++) wallEdges[i] = present;
        }

        /// <summary>
        /// Inserts a vertex after <paramref name="edge"/>. The split halves both inherit the
        /// original edge's wall flag, so deleting a wall then reshaping it stays deleted.
        /// </summary>
        public void InsertVertex(int edge, Vector2 point)
        {
            SyncWallEdges();
            if (edge < 0 || edge >= outline.Count) return;

            bool inherited = wallEdges[edge];
            outline.Insert(edge + 1, point);
            wallEdges.Insert(edge, inherited);
        }

        /// <summary>Removes a vertex, merging the two edges that met there.</summary>
        public void RemoveVertex(int index)
        {
            SyncWallEdges();
            if (index < 0 || index >= outline.Count || outline.Count <= 3) return;

            outline.RemoveAt(index);
            wallEdges.RemoveAt(Mathf.Min(index, wallEdges.Count - 1));
        }
    }

    /// <summary>
    /// A wall drawn on its own, not tied to a room outline. Lets you partition a space or
    /// add a stub wall without having to build a room around it.
    /// </summary>
    [Serializable]
    public class WallStroke
    {
        public Vector2 a;
        public Vector2 b;

        public float Length => Vector2.Distance(a, b);
    }

    /// <summary>
    /// A gap cut through wall geometry, linking two rooms. The gap is applied to any wall
    /// edge passing near <see cref="center"/>, so it works on non-aligned walls without
    /// the two rooms needing to share exact vertices.
    /// </summary>
    [Serializable]
    public class Doorway
    {
        public int roomA = -1;
        public int roomB = -1;
        public Vector2 center;
        public float width = 0.9f;

        public bool Links(int roomIndex) => roomA == roomIndex || roomB == roomIndex;
        public int Other(int roomIndex) => roomA == roomIndex ? roomB : roomA;
    }

    /// <summary>
    /// A 2D floor plan: arbitrary polygonal rooms, doorways connecting them, and the
    /// vacuum's starting point. Nothing here is grid-aligned.
    /// </summary>
    [CreateAssetMenu(menuName = "Robot Vacuum/Level", fileName = "NewLevel")]
    public class LevelData : ScriptableObject
    {
        [SerializeField] FloorPalette palette;
        [SerializeField] List<Room> rooms = new List<Room>();
        [SerializeField] List<Doorway> doorways = new List<Doorway>();
        [SerializeField] List<WallStroke> wallStrokes = new List<WallStroke>();

        [Tooltip("Wall thickness in metres. Walls are centred on room edges.")]
        [SerializeField] float wallThickness = 0.12f;

        [SerializeField] Color wallColor = new Color(0.22f, 0.24f, 0.29f);
        [SerializeField] Vector2 robotSpawn = Vector2.zero;

        public FloorPalette Palette { get => palette; set => palette = value; }
        public List<Room> Rooms => rooms;
        public List<Doorway> Doorways => doorways;
        public List<WallStroke> WallStrokes => wallStrokes;
        public Color WallColor { get => wallColor; set => wallColor = value; }
        public Vector2 RobotSpawn { get => robotSpawn; set => robotSpawn = value; }

        public float WallThickness
        {
            get => wallThickness;
            set => wallThickness = Mathf.Max(0.01f, value);
        }

        /// <summary>How far a doorway can sit from a wall edge and still cut it.</summary>
        public float DoorwaySnapDistance => Mathf.Max(wallThickness, 0.12f);

        void OnValidate()
        {
            wallThickness = Mathf.Max(0.01f, wallThickness);
            if (rooms == null) rooms = new List<Room>();
            if (doorways == null) doorways = new List<Doorway>();
            if (wallStrokes == null) wallStrokes = new List<WallStroke>();

            foreach (var room in rooms) room?.SyncWallEdges();

            for (int i = doorways.Count - 1; i >= 0; i--)
                doorways[i].width = Mathf.Max(0.05f, doorways[i].width);
        }

        public Room GetRoom(int index) =>
            index >= 0 && index < rooms.Count ? rooms[index] : null;

        public FloorType FloorTypeOf(Room room) =>
            room != null && palette != null ? palette.Get(room.floorIndex) : null;

        public Color ColorOf(Room room)
        {
            var floor = FloorTypeOf(room);
            return floor != null ? floor.color : new Color(0.6f, 0.6f, 0.6f);
        }

        /// <summary>Index of the room containing <paramref name="point"/>, or -1. Later rooms win overlaps.</summary>
        public int RoomIndexAt(Vector2 point)
        {
            for (int i = rooms.Count - 1; i >= 0; i--)
                if (rooms[i] != null && rooms[i].IsValid && rooms[i].Contains(point))
                    return i;
            return -1;
        }

        public Room RoomAt(Vector2 point) => GetRoom(RoomIndexAt(point));

        /// <summary>Floor covering under a point, or null when the point is outside every room.</summary>
        public FloorType FloorTypeAt(Vector2 point) => FloorTypeOf(RoomAt(point));

        public IEnumerable<int> NeighboursOf(int roomIndex)
        {
            foreach (var doorway in doorways)
            {
                if (!doorway.Links(roomIndex)) continue;

                int other = doorway.Other(roomIndex);
                if (other >= 0 && other < rooms.Count) yield return other;
            }
        }

        /// <summary>Rooms reachable from <paramref name="startRoom"/> by walking through doorways.</summary>
        public HashSet<int> ReachableFrom(int startRoom)
        {
            var seen = new HashSet<int>();
            if (GetRoom(startRoom) == null) return seen;

            var queue = new Queue<int>();
            queue.Enqueue(startRoom);
            seen.Add(startRoom);

            while (queue.Count > 0)
            {
                foreach (int neighbour in NeighboursOf(queue.Dequeue()))
                    if (seen.Add(neighbour)) queue.Enqueue(neighbour);
            }
            return seen;
        }

        /// <summary>Rooms the vacuum cannot reach from its spawn point, for editor warnings.</summary>
        public List<int> UnreachableRooms()
        {
            var orphans = new List<int>();
            int start = RoomIndexAt(robotSpawn);
            if (start < 0) return orphans;

            var reachable = ReachableFrom(start);
            for (int i = 0; i < rooms.Count; i++)
                if (rooms[i] != null && rooms[i].IsValid && !reachable.Contains(i))
                    orphans.Add(i);
            return orphans;
        }

        public float TotalFloorArea()
        {
            float total = 0f;
            foreach (var room in rooms)
                if (room != null && room.IsValid) total += room.Area;
            return total;
        }

        public Rect Bounds()
        {
            bool any = false;
            Vector2 min = Vector2.zero, max = Vector2.zero;

            foreach (var room in rooms)
            {
                if (room == null || !room.IsValid) continue;
                foreach (var point in room.outline)
                {
                    if (!any) { min = max = point; any = true; continue; }
                    min = Vector2.Min(min, point);
                    max = Vector2.Max(max, point);
                }
            }

            return any ? new Rect(min, max - min) : new Rect(-5f, -5f, 10f, 10f);
        }

        // ---------------------------------------------------------------- authoring

        public int AddRoom(Room room)
        {
            rooms.Add(room);
            return rooms.Count - 1;
        }

        /// <summary>Removes a room and repairs the room indices stored on doorways.</summary>
        public void RemoveRoom(int index)
        {
            if (index < 0 || index >= rooms.Count) return;
            rooms.RemoveAt(index);

            for (int i = doorways.Count - 1; i >= 0; i--)
            {
                var doorway = doorways[i];
                if (doorway.roomA == index || doorway.roomB == index)
                {
                    doorways.RemoveAt(i);
                    continue;
                }

                if (doorway.roomA > index) doorway.roomA--;
                if (doorway.roomB > index) doorway.roomB--;
            }
        }

        /// <summary>
        /// Builds a regular polygon outline, used for newly created rooms.
        /// </summary>
        public static List<Vector2> RegularOutline(Vector2 center, float radius, int sides, float rotationDegrees = 0f)
        {
            sides = Mathf.Max(3, sides);
            var points = new List<Vector2>(sides);

            for (int i = 0; i < sides; i++)
            {
                float angle = rotationDegrees * Mathf.Deg2Rad + i * Mathf.PI * 2f / sides;
                points.Add(center + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * radius);
            }
            return points;
        }

        public static List<Vector2> RectangleOutline(Vector2 center, Vector2 size, float rotationDegrees = 0f)
        {
            var half = size * 0.5f;
            var corners = new[]
            {
                new Vector2(-half.x, -half.y),
                new Vector2(half.x, -half.y),
                new Vector2(half.x, half.y),
                new Vector2(-half.x, half.y),
            };

            float radians = rotationDegrees * Mathf.Deg2Rad;
            float cos = Mathf.Cos(radians), sin = Mathf.Sin(radians);

            var points = new List<Vector2>(4);
            foreach (var corner in corners)
                points.Add(center + new Vector2(corner.x * cos - corner.y * sin, corner.x * sin + corner.y * cos));
            return points;
        }
    }
}
