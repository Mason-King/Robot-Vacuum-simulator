using System;
using System.Collections.Generic;
using RobotVacuum.AlgorithmApi;

namespace RobotVacuum.Sim.Control
{
    /// <summary>
    /// The controller's <see cref="IMapAnalysis"/>: a <see cref="MapSnapshot"/> of the belief map (with its distance
    /// transform), a <see cref="ReachField"/> from the robot, and <see cref="RoomUnits"/>. One per run, handed to the
    /// algorithm in its context.
    ///
    /// Each <see cref="Update"/> copies the map (a few batch reads) and refloods reach from the robot. The distance
    /// transform and the doorway search are only redone when the blocked or explored cells changed, which in a Normal
    /// run is almost never and in a SLAM run is while the map is still filling in.
    /// </summary>
    public sealed class MapAnalysis : IMapAnalysis
    {
        readonly IBeliefMap map;
        readonly MapSnapshot snapshot = new MapSnapshot();
        readonly ReachField reach = new ReachField();
        readonly RoomUnits rooms = new RoomUnits();

        public MapAnalysis(IBeliefMap map)
        {
            this.map = map ?? throw new ArgumentNullException(nameof(map));
        }

        /// <summary>The snapshot behind it (simulator side and tests).</summary>
        public MapSnapshot Snapshot => snapshot;

        public int Version { get; private set; }

        public bool SkipRooms { get; set; }
        bool roomsStale; // rooms were skipped since they were last found
        bool roomsBuilt;
        bool[] roomsReachable; // the reachable cells the room split was last built from

        public bool Update(Vec2 robot)
        {
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            bool changed = snapshot.Take(map);
            if (changed) Version++;
            long taken = System.Diagnostics.Stopwatch.GetTimestamp();
            reach.Build(snapshot, map, robot);
            long reached = System.Diagnostics.Stopwatch.GetTimestamp();
            if (SkipRooms) roomsStale = true;
            else
            {
                // The room split reads only the walls (changed) and which cells are reachable. When neither moved, the
                // last split stands; a robot that ends up on floor cut off from where it was changes the reachable set.
                bool reachChanged = reach.CopyReachable(ref roomsReachable);
                if (changed || roomsStale || reachChanged || !roomsBuilt)
                {
                    rooms.Build(snapshot, map, reach, changed || roomsStale || !roomsBuilt);
                    roomsBuilt = true;
                    RoomBuilds++;
                }
                roomsStale = false;
            }
            long done = System.Diagnostics.Stopwatch.GetTimestamp();

            Updates++;
            if (changed) ChangedUpdates++;
            SnapshotMs += Ms(started, taken);
            ReachMs += Ms(taken, reached);
            RoomsMs += Ms(reached, done);
            return changed;
        }

        // ---- Cost read-outs (the lag log; reporting only, never read by the simulation) ---------------------

        /// <summary>Update calls, and how many found the walls or explored cells changed (redoing the distance transform and doorway search).</summary>
        public int Updates { get; private set; }
        public int ChangedUpdates { get; private set; }

        /// <summary>Updates that redid the room split (the rest reused it: walls and reachable cells unchanged).</summary>
        public int RoomBuilds { get; private set; }

        /// <summary>Real ms in Update's three parts: copying the map (and, when changed, its distance transform), the reach flood, the rooms.</summary>
        public double SnapshotMs { get; private set; }
        public double ReachMs { get; private set; }
        public double RoomsMs { get; private set; }

        static double Ms(long from, long to) => (to - from) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

        public bool IsEmpty => snapshot.IsEmpty;
        public int MinRow => snapshot.MinRow;
        public int MinCol => snapshot.MinCol;
        public int Rows => snapshot.Rows;
        public int Cols => snapshot.Cols;
        public float CellSize => snapshot.CellSize;
        public float BodyClearance => snapshot.Clearance;

        public bool Contains(int row, int col) => snapshot.Contains(row, col);
        public bool IsBlocked(int row, int col) => snapshot.IsBlocked(row, col);
        public bool IsExplored(int row, int col) => snapshot.IsExplored(row, col);
        public bool IsVisited(int row, int col) => snapshot.IsVisited(row, col);
        public float Dwell(int row, int col) => snapshot.Dwell(row, col);
        public float Cleanliness(int row, int col) => snapshot.Cleanliness(row, col);
        public float VacuumResistance(int row, int col) => snapshot.VacuumResistance(row, col);

        public float ObstacleDistance(int row, int col) => snapshot.ObstacleDistance(row, col);
        public bool BodyFits(int row, int col) => snapshot.BodyFits(row, col);

        public float ReachDistance(int row, int col) => reach.Distance(row, col);
        public bool Reachable(int row, int col) => reach.Reachable(row, col);

        public int RoomCount => SkipRooms ? 0 : rooms.Count;
        public int RoomOf(int row, int col) => SkipRooms ? -1 : rooms.LabelOf(snapshot, row, col);
        public IReadOnlyList<(Vec2 a, Vec2 b)> Doorways => SkipRooms ? NoDoorways : rooms.Doorways;
        static readonly (Vec2 a, Vec2 b)[] NoDoorways = new (Vec2 a, Vec2 b)[0];
    }
}
