using System;

namespace RobotVacuum.Sim
{
    // ============================================================================
    // SimBootStart.cs
    //
    // OFFICIAL PATH -- the real Secondary Boot mechanism: writes a config file,
    // spawns a second process (SecondaryBootExecutablePath must be set to a
    // real build first), and reads the result back once that process finishes.
    // This is what actually ships.
    //
    // A plain static class, not a component -- SimStartController calls
    // Run(...) directly when its mode field is set to RealBoot. Never attached
    // to anything itself.
    // ============================================================================
    public static class SimBootStart
    {
        public static void Run(SimRunRequest request, Action<RunResult> onComplete)
        {
            SecondaryBootLauncher.Launch(
                request.level,
                request.vacuumSettings,
                request.pattern,
                request.picture,
                request.seed,
                request.headless,
                onComplete,
                request.playbackSpeed);
        }
    }
}
