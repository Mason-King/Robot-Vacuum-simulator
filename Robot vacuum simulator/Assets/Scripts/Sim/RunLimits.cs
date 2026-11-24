using System.Collections.Generic;
using RobotVacuum.Level;
using UnityEngine;

namespace RobotVacuum.Sim
{
    /// <summary>
    /// Floor plans that cannot be simulated at all, caught before a run is built, with a sentence saying why and what to
    /// change. Checked by <see cref="SimulationSession"/> for every caller (the GUI, a batch, a headless or cloud job),
    /// so a nonsense plan gets the same plain message everywhere instead of an exception part-way into building the run.
    ///
    /// These are limits of the simulation, not opinions about the plan: coordinates far enough out that single-precision
    /// physics and grid indexing break down, a plan with no floor to clean, and a grid too large for memory or for a
    /// heatmap texture. Softer problems (an unreachable room, a start outside every room) stay warnings
    /// (<see cref="LevelEditor.RunChecks"/>): those runs still go ahead.
    /// </summary>
    public static class RunLimits
    {
        /// <summary>No coordinate or size may be further out than this, in metres (10 km). Past it floats lose centimetres.</summary>
        public const float MaxCoordinateMeters = 10000f;

        /// <summary>The smallest total floor area a run makes sense on (10 cm × 10 cm).</summary>
        public const float MinFloorAreaSquareMeters = 0.01f;

        /// <summary>The most grid cells one model may have. About 35 million cells (a 300 m square at 2 in) runs in 3 GB.</summary>
        public const long MaxGridCells = 50_000_000;

        /// <summary>The most cells along one side: the heatmap is one texture, and GPUs top out at 16384 pixels.</summary>
        public const int MaxGridSide = 16000;

        /// <summary>Why this floor plan can't be simulated at this cell size, or null when it can.</summary>
        public static string Problem(LevelData level, float cellSizeMeters)
        {
            if (level == null) return "There is no floor plan to run.";

            string far = FarthestBadValue(level);
            if (far != null)
                return $"The floor plan has a position or size that is not a usable number of metres ({far}). Every coordinate and " +
                       $"size must be within {MaxCoordinateMeters:0} m of the origin. Check the floor plan file for a typo.";

            float area = level.TotalFloorArea();
            if (!(area >= MinFloorAreaSquareMeters))
                return "The floor plan has no floor to clean: every room has (almost) no area. A room needs at least 3 corners " +
                       "that are not all on one line, and an outline that doesn't cross itself.";

            if (cellSizeMeters > 0f)
            {
                Rect bounds = level.Bounds();
                double across = System.Math.Ceiling(bounds.width / cellSizeMeters) + 2;
                double down = System.Math.Ceiling(bounds.height / cellSizeMeters) + 2;
                double cells = across * down;
                if (cells > MaxGridCells || across > MaxGridSide || down > MaxGridSide)
                    return $"The floor plan is {bounds.width:0.#} m × {bounds.height:0.#} m. At a cell size of {cellSizeMeters * 100f:0.##} cm that is " +
                           $"{across:0} × {down:0} cells, more than the simulator can hold (at most {MaxGridSide} cells along a side and " +
                           $"{MaxGridCells / 1_000_000} million in all). Use a larger cell size or a smaller floor plan.";
            }

            return null;
        }

        // The first value that is not finite or is beyond MaxCoordinateMeters, described for the message; null if none.
        static string FarthestBadValue(LevelData level)
        {
            for (int r = 0; r < level.Rooms.Count; r++)
            {
                var room = level.Rooms[r];
                if (room?.outline == null) continue;
                foreach (var point in room.outline)
                    if (Bad(point)) return $"room \"{room.name}\" has a corner at {Describe(point)}";
            }

            foreach (var obstacle in level.Obstacles)
            {
                if (obstacle == null) continue;
                if (Bad(obstacle.center)) return $"furniture \"{obstacle.name}\" is centred at {Describe(obstacle.center)}";
                if (Bad(obstacle.size)) return $"furniture \"{obstacle.name}\" is {Describe(obstacle.size)} in size";
                if (!IsFinite(obstacle.rotation)) return $"furniture \"{obstacle.name}\" has a rotation of {obstacle.rotation}";
            }

            foreach (var doorway in level.Doorways)
                if (doorway != null && (Bad(doorway.center) || !IsFinite(doorway.width) || doorway.width > MaxCoordinateMeters))
                    return $"a doorway at {Describe(doorway.center)} is {doorway.width} m wide";

            foreach (var stroke in level.WallStrokes)
                if (stroke != null && (Bad(stroke.a) || Bad(stroke.b)))
                    return $"a wall runs from {Describe(stroke.a)} to {Describe(stroke.b)}";

            if (!IsFinite(level.WallThickness) || level.WallThickness > MaxCoordinateMeters)
                return $"the walls are {level.WallThickness} m thick";

            if (Bad(level.RobotSpawn)) return $"the vacuum starts at {Describe(level.RobotSpawn)}";
            return null;
        }

        static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        static bool Bad(Vector2 v) =>
            !IsFinite(v.x) || !IsFinite(v.y) || Mathf.Abs(v.x) > MaxCoordinateMeters || Mathf.Abs(v.y) > MaxCoordinateMeters;

        static string Describe(Vector2 v) => $"({v.x:G4}, {v.y:G4})";
    }
}
