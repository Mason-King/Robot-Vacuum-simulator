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

        public bool Update(Vec2 robot)
        {
            bool changed = snapshot.Take(map);
            if (changed) Version++;
            reach.Build(snapshot, map, robot);
            rooms.Build(snapshot, map, reach, changed);
            return changed;
        }

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

        public int RoomCount => rooms.Count;
        public int RoomOf(int row, int col) => rooms.LabelOf(snapshot, row, col);
        public IReadOnlyList<(Vec2 a, Vec2 b)> Doorways => rooms.Doorways;
    }
}
