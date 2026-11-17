namespace RobotVacuum.AlgorithmApi
{
    /// <summary>How the controller is getting on with the current waypoint. Reported to the algorithm when it is next asked.</summary>
    public enum GoalStatus
    {
        /// <summary>No waypoint yet (the first question of a run), or the algorithm returned none.</summary>
        None,

        /// <summary>The robot's centre came within the arrival tolerance.</summary>
        Reached,

        /// <summary>Still driving toward it.</summary>
        InProgress,

        /// <summary>Contact ahead, a believed obstacle ahead, or no progress within the blocked timeout.</summary>
        Blocked,

        /// <summary>The waypoint lies in a cell believed impassable (0).</summary>
        Unreachable,
    }
}
