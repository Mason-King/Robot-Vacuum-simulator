namespace RobotVacuum.AlgorithmApi
{
    /// <summary>
    /// A cleaning algorithm (the upper of the two layers). It decides WHERE to go; the controller decides HOW: it
    /// drives to each waypoint, avoids and recovers locally, and reports back a <see cref="GoalStatus"/>.
    ///
    /// To add one, write a public class with a public parameterless constructor that implements this interface,
    /// and put its .cs file in the Algorithms folder (see the README there). The simulator compiles it.
    ///
    /// Rules:
    ///   * Read the map only through the <see cref="IBeliefMap"/> you are handed. It is the live map, so there is no
    ///     need to keep a copy; follow its change feed to keep a plan up to date.
    ///   * Plan from the believed pose you are handed. The robot's true position is not available, by design.
    ///   * Return nearby waypoints (a few metres at most is a good habit); return null for "no goal right now".
    ///   * Draw randomness only from <see cref="AlgorithmContext.random"/>.
    ///   * An exception thrown here stops the run with an error, rather than crashing the simulator.
    /// </summary>
    public interface ICleaningAlgorithm
    {
        /// <summary>The name shown to the user. Must be non-empty and different from every other algorithm's.</summary>
        string Name { get; }

        /// <summary>
        /// True only if the algorithm works with no map at the start and a map that may be wrong (SLAM). A SLAM run
        /// with an algorithm that says false is refused at start with a clear error.
        /// </summary>
        bool SupportsSlam { get; }

        /// <summary>Called once at the start of every run, including a restart, and when the user switches to it mid-run.</summary>
        void Initialize(AlgorithmContext context);

        /// <summary>
        /// Asked every half second of simulated time, and at once when the current goal ends (Reached, Blocked or
        /// Unreachable). <paramref name="lastStatus"/> is how the current goal is going. Returning the same waypoint
        /// after Blocked makes the controller retry it (turn away, sidestep, try again); returning a different one
        /// starts afresh.
        /// </summary>
        Vec2? NextGoal(IBeliefMap map, Pose2 believedPose, GoalStatus lastStatus);
    }
}
