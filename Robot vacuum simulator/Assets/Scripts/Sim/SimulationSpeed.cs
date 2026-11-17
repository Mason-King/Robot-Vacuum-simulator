using UnityEngine;

namespace RobotVacuum.Sim
{
    /// <summary>
    /// Simulation speed. <see cref="Apply"/> sets the requested multiplier. A frame budget then keeps the app usable
    /// when the simulation can't keep up: each frame runs only as many physics steps as fit in about
    /// <see cref="FrameBudgetSeconds"/> of real time, so a slow simulation runs slower than requested instead of
    /// freezing the UI. The physics step itself never changes, so a run's results are the same at any speed.
    /// </summary>
    public static class SimulationSpeed
    {
        public const float MaxPlaybackSpeed = 100f;

        /// <summary>Real time a frame may spend on physics before the budget cuts the steps per frame.</summary>
        public const float FrameBudgetSeconds = 0.05f;

        /// <summary>The speed last requested through <see cref="Apply"/>.</summary>
        public static float Requested { get; private set; } = 1f;

        /// <summary>Simulated seconds per real second, measured (smoothed). Below <see cref="Requested"/> when the simulation can't keep up.</summary>
        public static float Actual => SpeedGovernor.Actual;

        /// <summary>The most simulated time one frame may catch up, as <see cref="Apply"/> set it; the budget works below this.</summary>
        internal static float Ceiling { get; private set; } = 0.333f;

        public static float ClampPlaybackSpeed(float multiplier) =>
            Mathf.Clamp(multiplier, 0.01f, MaxPlaybackSpeed);

        public static void Apply(float multiplier, float baseMaximumDeltaTime)
        {
            float speed = ClampPlaybackSpeed(multiplier);
            Requested = speed;
            Time.timeScale = speed;
            Ceiling = Mathf.Max(Time.fixedDeltaTime, baseMaximumDeltaTime * Mathf.Max(1f, speed));
            Time.maximumDeltaTime = Ceiling;
            SpeedGovernor.Ensure();
        }
    }
}
