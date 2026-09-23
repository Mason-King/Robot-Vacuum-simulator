using System;
using System.Diagnostics;
using System.IO;
using RobotVacuum.Level;
using RobotVacuum.LevelEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace RobotVacuum.Sim
{
    // Called from the Setup Screen's "Start Run" button. Owns everything about how
    // a run actually executes -- the UI side never touches a file path or a
    // Process object directly.
    public static class SecondaryBootLauncher
    {
        // PLACEHOLDER -- no build pipeline exists yet (confirmed, nobody on the
        // team has produced an .exe as of this writing). This MUST be set to the
        // real built player's path before Launch can do anything. Left as a
        // public field rather than a hardcoded guess, since the actual path is
        // whatever the team's build output ends up being -- a natural fit for
        // the Settings Screen's existing "project directory" concept once that
        // exists, rather than a constant baked in here.
        public static string SecondaryBootExecutablePath;

        // Safety cap only, not a designed stopping condition -- the real
        // terminal condition per SDD 1.3.3 is the vacuum's own battery reaching
        // zero. This just prevents a genuinely stuck run (a bugged algorithm
        // that never drains battery) from hanging forever.
        const float MaxSimulatedSecondsSafetyCap = 3600f;

        public static bool IsRunning { get; private set; }

        // Same problem LevelHandoff/SimLauncher already guard against: static
        // fields don't reset just because Play mode stopped. If a previous
        // session's poller GameObject got torn down (scene teardown on Stop)
        // before it detected the process finishing, IsRunning would stay stuck
        // true forever, blocking every future launch in that Editor session.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetOnPlayModeStart()
        {
            IsRunning = false;
        }

        // playbackSpeed is a new OPTIONAL trailing parameter, added after the
        // fact -- existing callers (the test trigger, and whatever the Setup
        // Screen button ends up calling) keep working unchanged with no
        // playback-speed argument at all, defaulting to real-time. This
        // preserves the exact signature already shown to the teammate
        // building that button, rather than breaking it.
        public static void Launch(LevelData level, VacuumSettings settings,
            MovementPattern pattern, PictureKind picture, int seed,
            bool headless, Action<RunResult> onComplete, float playbackSpeed = 1f)
        {
            if (IsRunning)
            {
                onComplete?.Invoke(RunResult.Failure("A run is already in progress."));
                return;
            }

            if (string.IsNullOrEmpty(SecondaryBootExecutablePath) || !File.Exists(SecondaryBootExecutablePath))
            {
                onComplete?.Invoke(RunResult.Failure(
                    "SecondaryBootExecutablePath is not set to a real build. " +
                    "Build the project (File > Build Settings > Build) and set this path first."));
                return;
            }

            string runFolder = Path.Combine(Application.persistentDataPath, "Runs", Guid.NewGuid().ToString("N"));
            string configPath = Path.Combine(runFolder, "config.json");
            string resultPath = Path.Combine(runFolder, "result.json");

            var config = new RunConfig
            {
                level = LevelSnapshot.FromLevel(level, level.name),
                vacuumSettings = settings,
                pattern = pattern,
                picture = picture,
                seed = seed,
                simulatedSeconds = MaxSimulatedSecondsSafetyCap,
                headless = headless,
                playbackSpeed = playbackSpeed,
                resultPath = resultPath,
            };
            config.WriteTo(configPath);

            var startInfo = new ProcessStartInfo
            {
                FileName = SecondaryBootExecutablePath,
                Arguments = $"-configPath \"{configPath}\"" + (headless ? " -batchmode -nographics" : ""),
                UseShellExecute = false,
            };

            Process process;
            try
            {
                process = Process.Start(startInfo);
            }
            catch (Exception ex)
            {
                onComplete?.Invoke(RunResult.Failure($"Could not start the Secondary Boot process: {ex.Message}"));
                return;
            }

            IsRunning = true;

            // Process.Start returns immediately -- completion has to be
            // discovered by polling, same per-frame-check idiom this codebase
            // already uses elsewhere (HeadlessSimulation's Update loop), rather
            // than Process.Exited's background-thread event, which is unsafe to
            // touch Unity APIs from directly.
            var pollerHost = new GameObject("Secondary Boot Poller") { hideFlags = HideFlags.HideInHierarchy };
            var poller = pollerHost.AddComponent<ProcessCompletionPoller>();
            poller.Begin(process, resultPath, result =>
            {
                IsRunning = false;
                UnityEngine.Object.Destroy(pollerHost);
                onComplete?.Invoke(result);
            });
        }

        // Small private driver, invisible to callers -- exists purely to give
        // the per-frame poll somewhere to run, since Launch() itself is a
        // one-shot static call with no ongoing Update of its own.
        sealed class ProcessCompletionPoller : MonoBehaviour
        {
            Process process;
            string resultPath;
            Action<RunResult> onDone;

            public void Begin(Process process, string resultPath, Action<RunResult> onDone)
            {
                this.process = process;
                this.resultPath = resultPath;
                this.onDone = onDone;
            }

            void Update()
            {
                // The result file existing means the run finished -- checked
                // FIRST and independently of whether the process itself has
                // exited, since graphics-mode runs deliberately stay open
                // after finishing (see SecondaryBootEntry.Finish) so someone
                // watching can see the final state. Process exit alone is no
                // longer a reliable "done" signal for graphics mode.
                if (File.Exists(resultPath))
                {
                    RunResult result;
                    try
                    {
                        result = RunResult.ReadFrom(resultPath);
                    }
                    catch (Exception ex)
                    {
                        result = RunResult.Failure($"Could not read the result file: {ex.Message}");
                    }

                    enabled = false; // stop polling before the callback, in case it starts a new run
                    onDone(result);
                    return;
                }

                // No result file yet, but the process is gone -- that's a
                // genuine crash/failure, not just "still running." Still
                // needed for headless, and as a safety net for graphics mode
                // if the window gets closed manually before finishing.
                if (process != null && process.HasExited)
                {
                    enabled = false;
                    onDone(RunResult.Failure("Secondary Boot exited without producing a result file (it likely crashed)."));
                }
            }
        }
    }
}
