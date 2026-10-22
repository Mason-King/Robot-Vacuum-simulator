using System;
using System.IO;
using RobotVacuum.LevelEditor;
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
        // Same safety cap the real launcher uses (one source: SimDefaults).
        const float SafetyCapSeconds = SimDefaults.MaxSimulatedSeconds;

        public static void Run(TestSimRunRequest request, Action<RunResult> onComplete)
        {
            var options = request.headless
                ? SimulationRunner.Options.Headless(request.pattern, request.seed, request.targetMaxClean)
                : SimulationRunner.Options.Visible(request.pattern, request.seed, request.targetMaxClean);

            var runner = SimulationRunner.Start(request.level, options);

            if (runner.Robot != null && runner.Battery != null)
                request.vacuumSettings.ApplyTo(runner.Robot, runner.Battery);

            // Same run folder layout as the real Secondary Boot, with the same
            // config.json: the inputs are saved up front, exactly as the
            // launcher does, so a test run is complete and reproducible too.
            string runFolder = RunArtifacts.NewRunFolder();
            var config = new RunConfig
            {
                level = LevelSnapshot.FromLevel(request.level, request.level.name),
                vacuumSettings = request.vacuumSettings,
                pattern = request.pattern,
                seed = request.seed,
                simulatedSeconds = SafetyCapSeconds,
                headless = request.headless,
                playbackSpeed = request.headless ? 100f : SimulationSpeed.ClampPlaybackSpeed(request.playbackSpeed),
                targetMaxClean = runner.Cleaning != null ? runner.Cleaning.TargetMaxClean : HeatmapDefaults.TargetMaxClean,
                resultPath = Path.Combine(runFolder, RunArtifacts.ResultFileName),
            };
            config.WriteTo(Path.Combine(runFolder, RunArtifacts.ConfigFileName));

            var driverHost = new GameObject("Test Sim Driver");
            driverHost.AddComponent<InProcessDriver>().Begin(request, runner, runFolder, onComplete);
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
            string runFolder;
            Action<RunResult> onComplete;
            float simulatedSeconds;
            float realSeconds;
            float originalTimeScale;
            float originalMaximumDeltaTime;

            public void Begin(TestSimRunRequest request, SimulationRunner runner, string runFolder, Action<RunResult> onComplete)
            {
                this.request = request;
                this.runner = runner;
                this.runFolder = runFolder;
                this.onComplete = onComplete;

                originalTimeScale = Time.timeScale;
                originalMaximumDeltaTime = Time.maximumDeltaTime;
                SimulationSpeed.Apply(request.headless ? 100f : request.playbackSpeed, originalMaximumDeltaTime);
            }

            void FixedUpdate()
            {
                simulatedSeconds += Time.fixedDeltaTime;

                bool batteryDead = runner.Battery != null && !runner.Battery.CanOperate;
                bool hitSafetyCap = simulatedSeconds >= SafetyCapSeconds;

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
                Time.maximumDeltaTime = originalMaximumDeltaTime;

                var result = new RunResult
                {
                    succeeded = true,
                    levelName = request.level.name,
                    seed = request.seed,
                    pattern = request.pattern,
                    coveragePercent = runner.CoveragePercent,
                    blockedAreaSquareMeters = runner.BlockedArea,
                    simulatedSeconds = simulatedSeconds,
                    realSeconds = realSeconds,
                };

                // Same artifacts as the real Secondary Boot, in the run folder
                // made at the start (config.json is already there), so a test
                // run shows up for the analysis side too.
                if (runner.Cleaning != null && runner.Cleaning.Grid != null)
                {
                    RunArtifacts.Write(runner.Cleaning.Grid.ToData(), runFolder, result);
                    result.WriteTo(Path.Combine(runFolder, RunArtifacts.ResultFileName));
                }

                onComplete(result);
                UnityEngine.Object.Destroy(gameObject);
            }
        }
    }
}
