using System.Collections.Generic;

namespace RobotVacuum.AlgorithmApi
{
    /// <summary>
    /// What an algorithm sees of the robot's map (its Internal Model): the live grid, read-only.
    ///
    ///   * Single-cell reads and world/cell conversion; the extent of what has been written so far.
    ///   * Batch reads of a region at a block size you choose, into a buffer you own and reuse.
    ///   * A change feed: the cells whose traversability or vacuum resistance changed since the last call. Read the
    ///     whole map once at the start (a Normal run's preloaded map is NOT in the feed), then follow the feed.
    ///   * Reachability from the robot's believed position (cached flood fill over passable cells).
    ///   * Believed line of sight and free distance along a ray.
    ///
    /// Cells never written read as passable, normal floor, nothing done. In a SLAM run the map starts empty.
    /// </summary>
    public interface IBeliefMap
    {
        // ---- Geometry ----

        /// <summary>Edge length of one cell, metres.</summary>
        float CellSizeMeters { get; }

        /// <summary>The world point the lattice is anchored to (a cell corner).</summary>
        Vec2 AnchorWorld { get; }

        /// <summary>The cell containing a world point. False only for points too far away to index.</summary>
        bool TryWorldToCell(Vec2 world, out CellIndex cell);

        Vec2 CellCenter(CellIndex cell);
        Vec2 CellCenter(int row, int col);

        // ---- Extent: the rectangle of cells written so far ----

        bool IsEmpty { get; }
        int MinRow { get; }
        int MinCol { get; }
        int Rows { get; }
        int Cols { get; }

        // ---- Single cells ----

        /// <summary>How easily the robot believes it can cross the cell: 1 = normal floor, lower = harder, EXACTLY 0 = impassable.</summary>
        float Traversability(int row, int col);

        /// <summary>How hard the robot believes the floor is to clean: 1 = normal, higher = harder. Never 0.</summary>
        float VacuumResistance(int row, int col);

        /// <summary>Seconds the robot believes it has spent over the cell. Above 0 means "I have been here".</summary>
        float Dwell(int row, int col);

        /// <summary>Cleanliness the robot believes it has achieved in the cell.</summary>
        float Cleanliness(int row, int col);

        /// <summary>
        /// True if the robot has sensed this cell, free or blocked. In a SLAM run, unexplored cells still READ as open
        /// floor (traversability 1), so use this to tell "known free" from "unknown". In a Normal run (the map is
        /// given) every cell counts as explored.
        /// </summary>
        bool IsExplored(int row, int col);

        /// <summary>True if the cell is believed impassable (traversability 0).</summary>
        bool IsBlocked(int row, int col);

        /// <summary>True if the cell containing the point is believed impassable.</summary>
        bool IsBlockedAt(Vec2 world);

        // ---- Batch reads ----

        /// <summary>
        /// Reads a rectangle of cells, <paramref name="blockSize"/> × <paramref name="blockSize"/> cells per value,
        /// into <paramref name="buffer"/> row by row (lowest row first). Traversability gives each block's MINIMUM (one
        /// blocked cell makes the block blocked); the other fields give the block's mean. Returns the number of values
        /// written. The buffer must be at least <see cref="BeliefRegion.BufferLength"/> long; reuse it between calls.
        /// </summary>
        int ReadRegion(int minRow, int minCol, int rows, int cols, int blockSize, BeliefField field, float[] buffer);

        // ---- Change feed ----

        /// <summary>Goes up whenever a reported change happens. Compare with the last value seen to know if anything changed.</summary>
        int ChangeCounter { get; }

        /// <summary>
        /// Fills <paramref name="into"/> (cleared first) with the cells whose traversability or vacuum resistance
        /// changed since the last call. Returns false when too much changed to list: rescan the whole map instead.
        /// </summary>
        bool TakeChangedCells(List<CellIndex> into);

        // ---- Queries ----

        /// <summary>
        /// True if a path of passable cells (4-connected) leads from the robot's believed cell to this one. Cells
        /// outside the written map count as reachable only if the passable region runs off the map's edge. This is
        /// CELL connectivity: a gap narrower than the robot still connects.
        /// </summary>
        bool IsReachable(int row, int col);
        bool IsReachable(Vec2 world);

        /// <summary>True if no cell believed impassable lies on the grid line between the two points (the start cell is ignored, so a robot standing in a wrongly-blocked cell can still see out).</summary>
        bool HasLineOfSight(Vec2 from, Vec2 to);

        /// <summary>
        /// How far from <paramref name="from"/> along <paramref name="direction"/> (unit length) the line runs before
        /// the first cell believed impassable, up to <paramref name="maxDistance"/>. The start cell is ignored.
        /// </summary>
        float FreeDistance(Vec2 from, Vec2 direction, float maxDistance);

        // ---- Paths for the robot's body ----

        /// <summary>How far planned paths keep the robot's centre from anything believed impassable: its radius plus a small margin, metres.</summary>
        float BodyClearanceMeters { get; }

        /// <summary>
        /// The shortest path the robot's body can take from <paramref name="from"/> to <paramref name="to"/> through the
        /// believed map (the same planner the controller drives with). <paramref name="into"/> (cleared first) gets the
        /// path's points: <paramref name="from"/>, the corners, then <paramref name="to"/>. False when the goal is in a
        /// cell believed impassable or no path exists. Unexplored cells count as open. A search over a big map takes a
        /// few milliseconds, so don't run hundreds per question.
        /// </summary>
        bool FindPath(Vec2 from, Vec2 to, List<Vec2> into, out float lengthMeters);

        /// <summary>The length of <see cref="FindPath"/>'s path, or positive infinity when there is none.</summary>
        float PathLength(Vec2 from, Vec2 to);

        /// <summary>
        /// Distance from <paramref name="point"/> to the nearest cell believed impassable, measured to the cell's edge
        /// (a mapped wall may fill its cell). Values at or above <see cref="BodyClearanceMeters"/> all mean "the body
        /// fits here"; the result is capped a little above it. Use it to keep waypoints off walls.
        /// </summary>
        float ObstacleDistance(Vec2 point);

        /// <summary>True if the body can drive the straight leg from <paramref name="from"/> to <paramref name="to"/> clear of believed obstacles.</summary>
        bool BodyFitsLeg(Vec2 from, Vec2 to);
    }
}
