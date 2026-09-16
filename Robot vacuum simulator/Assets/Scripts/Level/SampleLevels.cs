using System.Collections.Generic;
using UnityEngine;

namespace RobotVacuum.Level
{
    /// <summary>Starter floor plans, shared by the in-app level editor and the Unity asset factory.</summary>
    public static class SampleLevels
    {
        /// <summary>A single 4 × 3 m room with the vacuum in the middle.</summary>
        public static void PopulateBlank(LevelData level)
        {
            level.Rooms.Clear();
            level.Doorways.Clear();
            level.WallStrokes.Clear();

            level.Rooms.Add(new Room
            {
                name = "Room 1",
                outline = LevelData.RectangleOutline(Vector2.zero, new Vector2(4f, 3f)),
                floorIndex = 0,
            });

            level.RobotSpawn = Vector2.zero;
        }

        /// <summary>
        /// An empty 6 × 6 m tiled room: the canvas for the Picture movement pattern, which cleans a picture
        /// into the coverage heatmap. Tile cleans quickly, so the shades come out true. The vacuum starts in
        /// the top-left corner, beside the first row.
        /// </summary>
        public static void PopulatePictureCanvas(LevelData level)
        {
            level.Rooms.Clear();
            level.Doorways.Clear();
            level.WallStrokes.Clear();
            level.Obstacles.Clear();

            level.Rooms.Add(new Room
            {
                name = "Canvas",
                outline = LevelData.RectangleOutline(Vector2.zero, new Vector2(6f, 6f)),
                floorIndex = 1,
            });

            level.RobotSpawn = new Vector2(-2.4f, 2.4f);
        }

        /// <summary>
        /// Four rooms, two of them deliberately off-axis, wired together with doorways.
        /// The bedroom shares an exact edge with the hall so its doorway cuts both walls.
        /// </summary>
        public static void PopulateApartment(LevelData level)
        {
            level.Rooms.Clear();
            level.Doorways.Clear();
            level.WallStrokes.Clear();

            level.Rooms.Add(new Room
            {
                name = "Living Room",
                floorIndex = 0,
                outline = new List<Vector2>
                {
                    new Vector2(-2.4f, -1.8f), new Vector2(1.6f, -1.8f),
                    new Vector2(1.6f, 1.4f), new Vector2(-2.4f, 1.4f),
                },
            });

            level.Rooms.Add(new Room
            {
                name = "Kitchen",
                floorIndex = 1,
                outline = new List<Vector2>
                {
                    new Vector2(1.6f, -1.8f), new Vector2(4.6f, -1.2f),
                    new Vector2(4.6f, 1.0f), new Vector2(1.6f, 1.4f),
                },
            });

            level.Rooms.Add(new Room
            {
                name = "Hall",
                floorIndex = 2,
                outline = new List<Vector2>
                {
                    new Vector2(-2.4f, -1.8f), new Vector2(1.6f, -1.8f),
                    new Vector2(1.2f, -3.2f), new Vector2(-2.0f, -3.4f),
                },
            });

            level.Rooms.Add(new Room
            {
                name = "Bedroom",
                floorIndex = 4,
                outline = new List<Vector2>
                {
                    new Vector2(-2.0f, -3.4f), new Vector2(-2.4f, -1.8f),
                    new Vector2(-4.923f, -2.431f), new Vector2(-4.523f, -4.031f),
                },
            });

            level.Doorways.Add(new Doorway { roomA = 0, roomB = 1, center = new Vector2(1.6f, 0.2f), width = 1.0f });
            level.Doorways.Add(new Doorway { roomA = 0, roomB = 2, center = new Vector2(-0.4f, -1.8f), width = 1.1f });
            level.Doorways.Add(new Doorway { roomA = 2, roomB = 3, center = new Vector2(-2.2f, -2.6f), width = 0.9f });

            level.RobotSpawn = new Vector2(-0.4f, -0.2f);
        }
    }
}
