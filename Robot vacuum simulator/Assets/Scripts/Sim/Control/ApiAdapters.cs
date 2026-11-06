using System;
using System.Collections.Generic;
using RobotVacuum.AlgorithmApi;
using UnityEngine;

namespace RobotVacuum.Sim.Control
{
    /// <summary>Conversions between the algorithm API's types and Unity's. The values pass through unchanged.</summary>
    public static class ApiConversions
    {
        public static Vec2 ToApi(this Vector2 v) => new Vec2(v.x, v.y);
        public static Vector2 ToUnity(this Vec2 v) => new Vector2(v.x, v.y);
        public static Pose2 ToApi(this RobotPose pose) => new Pose2(pose.position.ToApi(), pose.heading);
        public static RobotPose ToUnity(this Pose2 pose) => new RobotPose(pose.position.ToUnity(), pose.heading);
    }

    /// <summary>
    /// The algorithm's random stream: the run's <see cref="RandomProvider"/>, algorithm subsystem. Every algorithm
    /// draws from that one stream (only one algorithm runs at a time), so RandomBounce's draws are what they were
    /// before the API existed, and every recorded seed still gives the same run.
    /// </summary>
    public sealed class ProviderRandom : IRandom
    {
        const RandomProvider.Subsystem Stream = RandomProvider.Subsystem.RandomBounceAlgorithm;
        readonly RandomProvider provider;

        public ProviderRandom(RandomProvider provider)
        {
            this.provider = provider ?? throw new ArgumentNullException(nameof(provider));
        }

        public double NextDouble() => provider.NextDouble(Stream);

        public float NextFloat(float min, float max) => provider.NextFloat(Stream, min, max);

        public int NextInt(int minInclusive, int maxExclusive)
        {
            if (maxExclusive <= minInclusive) return minInclusive;
            long span = (long)maxExclusive - minInclusive;
            long value = minInclusive + (long)Math.Floor(NextDouble() * span);
            return (int)Math.Min(value, maxExclusive - 1L);
        }

        public double NextGaussian(double mean, double standardDeviation) => provider.NextGaussian(Stream, mean, standardDeviation);
    }

    /// <summary>The robot's map as an algorithm sees it: <see cref="InternalModelView"/> behind the API's interface. Nothing is copied.</summary>
    public sealed class BeliefMapAdapter : IBeliefMap
    {
        readonly InternalModelView view;
        readonly PathPlanner planner;
        readonly List<Vector2> scratch = new List<Vector2>();

        /// <param name="planner">The body planner the algorithm's path queries use; null = one with no margin beyond the given clearance.</param>
        public BeliefMapAdapter(InternalModelView view, PathPlanner planner = null)
        {
            this.view = view ?? throw new ArgumentNullException(nameof(view));
            this.planner = planner;
        }

        /// <summary>The view behind it (simulator side only; an algorithm never gets this).</summary>
        public InternalModelView View => view;

        public float CellSizeMeters => view.CellSizeMeters;
        public Vec2 AnchorWorld => view.AnchorWorld.ToApi();
        public bool TryWorldToCell(Vec2 world, out CellIndex cell) => view.TryWorldToCell(world.ToUnity(), out cell);
        public Vec2 CellCenter(CellIndex cell) => view.CellCenter(cell).ToApi();
        public Vec2 CellCenter(int row, int col) => view.CellCenter(row, col).ToApi();

        public bool IsEmpty => view.IsEmpty;
        public int MinRow => view.MinRow;
        public int MinCol => view.MinCol;
        public int Rows => view.Rows;
        public int Cols => view.Cols;

        public float Traversability(int row, int col) => view.GetCell(row, col).traversabilityBelief;
        public float VacuumResistance(int row, int col) => view.GetCell(row, col).vacuumResistanceBelief;
        public float Dwell(int row, int col) => view.GetCell(row, col).dwellBelief;
        public float Cleanliness(int row, int col) => view.GetCell(row, col).cleanlinessBelief;
        public bool IsBlocked(int row, int col) => view.IsBlocked(row, col);
        public bool IsBlockedAt(Vec2 world) => view.IsBlockedAt(world.ToUnity());

        public int ReadRegion(int minRow, int minCol, int rows, int cols, int blockSize, BeliefField field, float[] buffer) =>
            view.ReadRegion(minRow, minCol, rows, cols, blockSize, field, buffer);

        public int ChangeCounter => view.ChangeCounter;
        public bool TakeChangedCells(List<CellIndex> into) => view.TakeChangedCells(into);

        public bool IsReachable(int row, int col) => view.IsReachable(row, col);
        public bool IsReachable(Vec2 world) => view.IsReachable(world.ToUnity());
        public bool HasLineOfSight(Vec2 from, Vec2 to) => view.HasLineOfSight(from.ToUnity(), to.ToUnity());
        public float FreeDistance(Vec2 from, Vec2 direction, float maxDistance) => view.FreeDistance(from.ToUnity(), direction.ToUnity(), maxDistance);

        public float BodyClearanceMeters => planner != null ? planner.Clearance : 0f;

        public bool FindPath(Vec2 from, Vec2 to, List<Vec2> into, out float lengthMeters)
        {
            if (into == null) throw new ArgumentNullException(nameof(into));
            into.Clear();
            lengthMeters = float.PositiveInfinity;
            if (planner == null || !planner.FindPath(from.ToUnity(), to.ToUnity(), scratch, out lengthMeters)) return false;
            for (int i = 0; i < scratch.Count; i++) into.Add(scratch[i].ToApi());
            return true;
        }

        public float PathLength(Vec2 from, Vec2 to) =>
            planner != null && planner.FindPath(from.ToUnity(), to.ToUnity(), scratch, out float length) ? length : float.PositiveInfinity;

        public float ObstacleDistance(Vec2 point) => planner != null ? planner.PointClearance(point.ToUnity()) : 0f;

        public bool BodyFitsLeg(Vec2 from, Vec2 to) => planner != null && planner.SegmentClear(from.ToUnity(), to.ToUnity());
    }
}
