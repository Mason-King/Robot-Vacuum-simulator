using UnityEngine;

namespace RobotVacuum.Sim.Control
{
    /// <summary>What an algorithm is told once, when a run starts (or restarts).</summary>
    public readonly struct AlgorithmContext
    {
        /// <summary>The run's random streams. An algorithm draws from <see cref="RandomProvider.Subsystem.RandomBounceAlgorithm"/> (or its own stream once one exists), never from Unity's Random.</summary>
        public readonly RandomProvider random;

        public readonly float robotRadius;

        /// <summary>Width of floor cleaned in one pass, metres.</summary>
        public readonly float cleaningWidth;

        /// <summary>The shared lattice's cell size, metres.</summary>
        public readonly float cellSizeMeters;

        /// <summary>True for a SLAM run: the map starts empty and fills in from sensing.</summary>
        public readonly bool slam;

        /// <summary>The lidar's maximum range, metres; 0 when the lidar is off.</summary>
        public readonly float lidarRangeMeters;

        public AlgorithmContext(RandomProvider random, float robotRadius, float cleaningWidth, float cellSizeMeters,
            bool slam, float lidarRangeMeters)
        {
            this.random = random;
            this.robotRadius = robotRadius;
            this.cleaningWidth = cleaningWidth;
            this.cellSizeMeters = cellSizeMeters;
            this.slam = slam;
            this.lidarRangeMeters = lidarRangeMeters;
        }
    }

    /// <summary>
    /// A pluggable cleaning algorithm (the upper of the two layers). It decides WHERE to go; the controller
    /// (<see cref="MovementBrain"/>) decides HOW: it drives to each waypoint, avoids and recovers locally, and
    /// reports back a <see cref="GoalStatus"/>.
    ///
    /// Rules for algorithm authors:
    ///   * Read the map only through the <see cref="InternalModelView"/> you are handed. It is the live model, so
    ///     there is no need to keep a copy; follow its change feed to keep a plan up to date.
    ///   * Plan from the believed pose you are handed, never the robot's true position.
    ///   * Return nearby waypoints (a few metres at most is a good habit); return null for "no goal right now".
    ///   * Draw randomness only from <see cref="AlgorithmContext.random"/>, so runs stay reproducible.
    ///   * An exception thrown here stops the run with an error, rather than crashing the simulator.
    /// </summary>
    public interface ICleaningAlgorithm
    {
        /// <summary>The name shown to the user.</summary>
        string Name { get; }

        /// <summary>
        /// True only if the algorithm works with no map at the start and a map that may be wrong (SLAM). A SLAM run
        /// with an algorithm that says false is refused at start with a clear error.
        /// </summary>
        bool SupportsSlam { get; }

        /// <summary>Called once at the start of every run, including a restart.</summary>
        void Initialize(AlgorithmContext context);

        /// <summary>
        /// Asked every <see cref="ControllerConstants.GoalPeriodSeconds"/> of simulated time, and at once when the
        /// current goal ends (Reached, Blocked or Unreachable). <paramref name="lastStatus"/> is how the current
        /// goal is going. Returning the same waypoint after Blocked makes the controller retry it (turn away,
        /// sidestep, try again); returning a different one starts afresh.
        /// </summary>
        Vector2? NextGoal(InternalModelView view, RobotPose believedPose, GoalStatus lastStatus);
    }

    /// <summary>
    /// Which algorithm each <see cref="MovementPattern"/> runs, and whether it supports SLAM. RandomBounce is the
    /// new goal-picking default. Spiral, Wall follow and Lawnmower are the OLD reactive patterns, kept working
    /// (through <see cref="LegacyPatternDriver"/>) until the four intelligent algorithms replace them. They steer
    /// by probing the TRUE world, so they cannot run with SLAM on.
    /// </summary>
    public static class AlgorithmCatalog
    {
        public static bool IsLegacyPattern(MovementPattern pattern) => pattern != MovementPattern.RandomBounce;

        public static bool SupportsSlam(MovementPattern pattern) =>
            !IsLegacyPattern(pattern) && CreateGoalAlgorithm(pattern).SupportsSlam;

        /// <summary>A new goal-picking algorithm for the pattern, or null for a legacy reactive pattern.</summary>
        public static ICleaningAlgorithm CreateGoalAlgorithm(MovementPattern pattern) => pattern switch
        {
            MovementPattern.RandomBounce => new RandomBounceAlgorithm(),
            _ => null,
        };

        public static string NameOf(MovementPattern pattern)
        {
            int index = (int)pattern;
            return index >= 0 && index < MovementBrain.Labels.Length ? MovementBrain.Labels[index] : pattern.ToString();
        }

        /// <summary>The error a SLAM run gets when its algorithm doesn't support SLAM, or null if it does.</summary>
        public static string SlamRefusal(MovementPattern pattern) => SupportsSlam(pattern)
            ? null
            : $"SLAM is on, but the '{NameOf(pattern)}' algorithm does not support SLAM. Turn SLAM off, or choose an algorithm that supports it (currently: Random).";
    }
}
