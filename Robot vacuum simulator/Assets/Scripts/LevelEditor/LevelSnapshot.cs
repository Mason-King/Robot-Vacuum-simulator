using System;
using System.Collections.Generic;
using RobotVacuum.Level;
using UnityEngine;

namespace RobotVacuum.LevelEditor
{
    /// <summary>
    /// A plain, serialisable copy of a floor plan. It is both the on-disk level format and the
    /// unit the undo history stores, so anything that should survive a save or an undo lives here.
    /// The floor palette is deliberately left out: rooms keep an index into whichever palette the
    /// app ships with.
    /// </summary>
    [Serializable]
    public class LevelSnapshot
    {
        public const int CurrentVersion = 1;
        public const string DefaultName = "Untitled floor plan";

        public int version = CurrentVersion;
        public string name = DefaultName;
        public List<Room> rooms = new List<Room>();
        public List<Doorway> doorways = new List<Doorway>();
        public List<WallStroke> wallStrokes = new List<WallStroke>();
        public List<Obstacle> obstacles = new List<Obstacle>();
        public float wallThickness = 0.12f;
        public Color wallColor = new Color(0.22f, 0.24f, 0.29f);
        public Vector2 robotSpawn;

        /// <summary>Serialises a live level straight to JSON. Nothing in the result is shared with the level.</summary>
        public static string Serialize(LevelData level, string name, bool pretty = false)
        {
            var snapshot = new LevelSnapshot
            {
                name = name,
                rooms = level.Rooms,
                doorways = level.Doorways,
                wallStrokes = level.WallStrokes,
                obstacles = level.Obstacles,
                wallThickness = level.WallThickness,
                wallColor = level.WallColor,
                robotSpawn = level.RobotSpawn,
            };
            return JsonUtility.ToJson(snapshot, pretty);
        }

        public static LevelSnapshot FromLevel(LevelData level, string name) => Parse(Serialize(level, name));

        /// <summary>Builds a snapshot by running a populate method against a throwaway level.</summary>
        public static LevelSnapshot Create(string name, Action<LevelData> populate)
        {
            var scratch = ScriptableObject.CreateInstance<LevelData>();
            try
            {
                populate?.Invoke(scratch);
                return FromLevel(scratch, name);
            }
            finally
            {
                UnityEngine.Object.Destroy(scratch);
            }
        }

        /// <summary>Returns null for empty or unreadable input rather than throwing.</summary>
        public static LevelSnapshot Parse(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;

            LevelSnapshot snapshot;
            try
            {
                snapshot = JsonUtility.FromJson<LevelSnapshot>(json);
            }
            catch (ArgumentException)
            {
                return null;
            }

            snapshot?.Sanitise();
            return snapshot;
        }

        public string ToJson(bool pretty = true) => JsonUtility.ToJson(this, pretty);

        void Sanitise()
        {
            if (string.IsNullOrWhiteSpace(name)) name = DefaultName;
            rooms ??= new List<Room>();
            doorways ??= new List<Doorway>();
            wallStrokes ??= new List<WallStroke>();
            obstacles ??= new List<Obstacle>();
            wallThickness = Mathf.Max(0.01f, wallThickness);

            foreach (var room in rooms)
            {
                if (room == null) continue;
                room.outline ??= new List<Vector2>();
                room.SyncWallEdges();
            }

            foreach (var doorway in doorways)
                if (doorway != null) doorway.width = Mathf.Max(0.05f, doorway.width);

            foreach (var obstacle in obstacles)
            {
                if (obstacle == null) continue;
                obstacle.size = Vector2.Max(obstacle.size, Vector2.one * Obstacle.MinSize);
                if (string.IsNullOrWhiteSpace(obstacle.name)) obstacle.name = Obstacle.DefaultName(obstacle.kind);
            }
        }

        /// <summary>
        /// Replaces the contents of <paramref name="level"/>. The snapshot's rooms and doorways are
        /// handed over rather than copied, so do not keep editing the snapshot afterwards.
        /// </summary>
        public void ApplyTo(LevelData level)
        {
            level.Rooms.Clear();
            level.Rooms.AddRange(rooms);
            level.Doorways.Clear();
            level.Doorways.AddRange(doorways);
            level.WallStrokes.Clear();
            level.WallStrokes.AddRange(wallStrokes);
            level.Obstacles.Clear();
            level.Obstacles.AddRange(obstacles);
            level.WallThickness = wallThickness;
            level.WallColor = wallColor;
            level.RobotSpawn = robotSpawn;
        }

        public int ValidRoomCount
        {
            get
            {
                int count = 0;
                foreach (var room in rooms)
                    if (room != null && room.IsValid) count++;
                return count;
            }
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
    }
}
