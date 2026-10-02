using System;
using UnityEngine;

namespace RobotVacuum.Sim
{
    // ============================================================================
    // TestSimStart.cs
    //
    // TESTING PATH -- runs the simulation in THIS process, no build/second .exe
    // required. Fast iteration for the team during day-to-day development.
    //
    // This does NOT exercise the real Secondary Boot mechanism at all -- no
    // config file, no Process.Start, no result file, no real process boundary.
    // It proves the SIMULATION logic works; it does not prove the BOOT
    // mechanism works. Before any milestone/demo that depends on Secondary
    // Boot actually functioning, verify with SimBootStart, not this.
    //
    // A plain static class, not a component -- SimStartController calls
    // Run(...) directly when its mode field is set to Test. Never attached to
    // anything itself.
    // ============================================================================
    public static class TestSimStart
    {
        public static void Run(TestSimRunRequest request, Action<RunResult> onComplete)
        {
            var options = request.headless
                ? SimulationRunner.Options.Headless(request.pattern, request.picture, request.seed)
                : SimulationRunner.Options.Visible(request.pattern, request.picture, request.seed);

            var runner = SimulationRunner.Start(request.level, options);

            if (runner.Robot != null && runner.Battery != null)
                request.vacuumSettings.ApplyTo(runner.Robot, runner.Battery);

            var driverHost = new GameObject("Test Sim Driver");
            driverHost.AddComponent<InProcessDriver>().Begin(request, runner, onComplete);
        }

        // Mirrors SecondaryBootEntry's driver loop, but deliberately NOT
        // shared code with it -- the two have genuinely different lifecycles.
        // SecondaryBootEntry's driver lives in a throwaway process that exits
        // when done; this one lives in the ONGOING host process and must
        // never quit it or leave Time.timeScale altered behind it.
        sealed class InProcessDriver : MonoBehaviour
        {
            TestSimRunRequest request;
            SimulationRunner runner;
            Action<RunResult> onComplete;
            float simulatedSeconds;
            float realSeconds;
            float originalTimeScale;

            public void Begin(TestSimRunRequest request, SimulationRunner runner, Action<RunResult> onComplete)
            {
                this.request = request;
                this.runner = runner;
                this.onComplete = onComplete;

                originalTimeScale = Time.timeScale;
                Time.timeScale = request.headless ? 100f : Mathf.Max(request.playbackSpeed, 0.01f);
            }

            void FixedUpdate()
            {
                simulatedSeconds += Time.fixedDeltaTime;

                bool batteryDead = runner.Battery != null && !runner.Battery.CanOperate;
                bool hitSafetyCap = simulatedSeconds >= 3600f;

                if (batteryDead || hitSafetyCap) Finish();
            }

            void Update()
            {
                realSeconds += Time.unscaledDeltaTime;
            }

            void Finish()
            {
                enabled = false;
                Time.timeScale = originalTimeScale;

                var result = new RunResult
                {
                    succeeded = true,
                    levelName = request.level.name,
                    seed = request.seed,
                    pattern = request.pattern,
                    picture = request.picture,
                    coveragePercent = runner.CoveragePercent,
                    blockedAreaSquareMeters = runner.BlockedArea,
                    simulatedSeconds = simulatedSeconds,
                    realSeconds = realSeconds,
                };

                onComplete(result);
                UnityEngine.Object.Destroy(gameObject);
            }
        }
    }
}
