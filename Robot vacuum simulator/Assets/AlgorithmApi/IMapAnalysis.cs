using System.Collections.Generic;

namespace RobotVacuum.AlgorithmApi
{
    /// <summary>
    /// The controller's analysis of the robot's map, shared by every algorithm: where the robot's BODY fits, how far
    /// the robot would have to drive to every cell over floor it knows, and how that floor divides into ROOMS at its
    /// doorways. In <see cref="AlgorithmContext.analysis"/>.
    ///
    /// Call <see cref="Update"/> once when you make a decision, then read as much as you like. It copies the written
    /// part of the map, so reads are cheap and consistent with each other until the next update. The expensive parts
    /// (the distance field, the doorways) are only redone when the map's walls or explored area actually changed.
    ///
    /// Everything is per cell of the map (row, col), the same cells as <see cref="IBeliefMap"/>. Outside the copied
    /// rectangle a cell reads as unexplored, not blocked, nothing done, and unreachable.
    /// </summary>
    public interface IMapAnalysis
    {
        /// <summary>
        /// Brings the analysis up to date with the map and the robot's believed position (reach is measured from
        /// there). Returns true when the walls or the explored area changed since the last update.
        /// </summary>
        bool Update(Vec2 robot);

        /// <summary>Goes up every time the walls or explored area are found to have changed (distances and doorways redone).</summary>
        int Version { get; }

        // ---- The copied rectangle ----

        bool IsEmpty { get; }
        int MinRow { get; }
        int MinCol { get; }
        int Rows { get; }
        int Cols { get; }
        float CellSize { get; }

        /// <summary>The planner's body clearance: how far the robot's centre stays from anything believed impassable (radius plus a margin), metres.</summary>
        float BodyClearance { get; }

        bool Contains(int row, int col);

        // ---- The map, as copied ----

        /// <summary>Believed impassable (<see cref="IBeliefMap.IsBlocked"/>: traversability 0, or a SLAM wall shaded below half its floor's ease).</summary>
        bool IsBlocked(int row, int col);

        /// <summary>Sensed by the robot (in a Normal run, everything is).</summary>
        bool IsExplored(int row, int col);

        /// <summary>The robot believes it has been over the cell (dwell above 0).</summary>
        bool IsVisited(int row, int col);

        float Dwell(int row, int col);
        float Cleanliness(int row, int col);
        float VacuumResistance(int row, int col);

        // ---- Where the body fits ----

        /// <summary>
        /// Distance from the cell's centre to the nearest believed-impassable cell's EDGE, metres; positive infinity
        /// when nothing blocked is in the map. Exact along a row or column, at most about 1 cm short on a diagonal.
        /// </summary>
        float ObstacleDistance(int row, int col);

        /// <summary>The body fits with its centre here: explored, and at least <see cref="BodyClearance"/> from anything blocked.</summary>
        bool BodyFits(int row, int col);

        // ---- Reach ----

        /// <summary>
        /// How far the robot would drive to the cell, metres, over floor it KNOWS (explored, the body fits). Positive
        /// infinity when it can't get there that way. Unexplored floor never connects anything, so floor glimpsed
        /// through a gap in a half-drawn wall is unreachable.
        /// </summary>
        float ReachDistance(int row, int col);

        bool Reachable(int row, int col);

        // ---- Rooms ----

        /// <summary>The number of rooms found. Room numbers are 0 .. RoomCount - 1 and may change from one update to the next.</summary>
        int RoomCount { get; }

        /// <summary>The cell's room, or -1 when it is not reachable floor.</summary>
        int RoomOf(int row, int col);

        /// <summary>
        /// The doorways found, as the two wall ends either side. A doorway is a gap of 0.5 to 1.4 m between two wall
        /// ends in line with each other; an opening with no wall stubs (open plan) is not one.
        /// </summary>
        IReadOnlyList<(Vec2 a, Vec2 b)> Doorways { get; }
    }
}
