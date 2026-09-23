using System;
using System.IO;
using RobotVacuum.Level;
using RobotVacuum.LevelEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace RobotVacuum.Sim
{
    // Lives in the SAME player build as the normal GUI flow -- what makes this
    // process "Secondary Boot" isn't a different executable, it's THIS class
    // noticing -configPath on the command line and taking over before any
    // normal scene loads, combined with launching that build with
    // -batchmode -nographics (decided by SecondaryBootLauncher, not here).
    public static class SecondaryBootEntry
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void CheckForRunConfig()
        {
            string configPath = null;
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "-configPath") { configPath = args[i + 1]; break; }
            }

            if (configPath == null) return; // normal launch -- let the usual GUI scene flow proceed untouched

            RunConfig config;
            try
            {
                config = RunConfig.ReadFrom(configPath);
            }
            catch (Exception ex)
            {
                Debug.LogError($"SecondaryBootEntry: could not read config at {configPath}: {ex.Message}");
                Application.Quit(1);
                return;
            }

            if (config.headless)
            {
                // Headless never needs a scene or a camera at all -- build
                // straight away, same as before.
                var driverHost = new GameObject("Secondary Boot Driver");
                UnityEngine.Object.DontDestroyOnLoad(driverHost);
                driverHost.AddComponent<SecondaryBootDriver>().Begin(config);
                return;
            }

            // Graphics mode: load the REAL PlayScene rather than letting
            // whatever the default scene is (the title screen) load on its
            // own and trying to hijack/rebuild a camera on top of it.
            // PlayScene already has a correctly hand-placed Main Camera --
            // reusing it is simpler and more reliable than constructing one
            // from raw math and hoping it lines up.
            const string PlaySceneName = "PlayScene";
            SceneManager.sceneLoaded += OnPlaySceneLoaded;
            SceneManager.LoadScene(PlaySceneName);

            void OnPlaySceneLoaded(Scene scene, LoadSceneMode mode)
            {
                if (scene.name != PlaySceneName) return; // ignore the default scene finishing first, if it already had
                SceneManager.sceneLoaded -= OnPlaySceneLoaded;

                // PlayScene.unity, as a SAVED scene, has its own pre-placed
                // Vacuum Robot / level objects for the normal Level-Editor-
                // driven flow. SimulationRunner.Start builds a second, fresh
                // set from RunConfig -- without this, both exist at once:
                // two visible robots, only one actually wired to our grid,
                // and the scene's own leftover robot never gets told to stop
                // when the battery runs out, since nothing here controls it.
                //
                // DestroyImmediate, not Destroy: plain Destroy() only SCHEDULES
                // removal for the end of the current frame -- SimulationRunner.
                // Start (called a few lines below, same frame) searches for a
                // VacuumCleaningController to wire the heatmap to, and could
                // still find the old, soon-to-be-destroyed one at that exact
                // moment. That reference then goes dead once the scheduled
                // destroy actually happens, which is why the heatmap silently
                // stopped drawing.
                foreach (var oldRobot in UnityEngine.Object.FindObjectsByType<VacuumRobot>(FindObjectsSortMode.None))
                    UnityEngine.Object.DestroyImmediate(oldRobot.gameObject);
                foreach (var oldRenderer in UnityEngine.Object.FindObjectsByType<LevelRenderer>(FindObjectsSortMode.None))
                    UnityEngine.Object.DestroyImmediate(oldRenderer.gameObject);

                // Force windowed mode -- the default Player Settings fullscreen
                // behavior left no OS window chrome (no title bar, no close
                // button) for a window nobody expected to be fullscreen.
                Screen.fullScreenMode = FullScreenMode.Windowed;
                Screen.SetResolution(1280, 720, FullScreenMode.Windowed);

                var driverHost = new GameObject("Secondary Boot Driver");
                UnityEngine.Object.DontDestroyOnLoad(driverHost);
                driverHost.AddComponent<SecondaryBootDriver>().Begin(config);
            }
        }

        // Runs the actual simulation loop and writes the result. A real
        // MonoBehaviour (not static) because it needs FixedUpdate/Update, the
        // same per-tick pattern already used elsewhere in this codebase
        // (VacuumCleaningController, the old HeadlessSimulation).
        sealed class SecondaryBootDriver : MonoBehaviour
        {
            RunConfig config;
            LevelData level;
            SimulationRunner runner;
            float simulatedSeconds;
            float realSeconds;

            public void Begin(RunConfig config)
            {
                this.config = config;

                level = ScriptableObject.CreateInstance<LevelData>();
                config.level.ApplyTo(level);

                var options = config.headless
                    ? SimulationRunner.Options.Headless(config.pattern, config.picture, config.seed)
                    : SimulationRunner.Options.Visible(config.pattern, config.picture, config.seed);

                runner = SimulationRunner.Start(level, options);

                if (config.vacuumSettings != null && runner.Robot != null && runner.Battery != null)
                    ApplySettings(config.vacuumSettings, runner.Robot, runner.Battery);

                // Headless has nobody watching, so it runs fast -- same
                // acceleration mechanism the old same-process HeadlessSimulation
                // used. Graphics mode uses whatever playback speed the caller
                // asked for (default real-time) -- 100x would make a live
                // robot impossible to actually see moving.
                Time.timeScale = config.headless ? 100f : config.playbackSpeed;
            }

            void ApplySettings(VacuumSettings settings, VacuumRobot robot, Battery battery)
            {
                // VacuumSettings already knows how to read itself onto a robot
                // (VacuumSettings.From reads the other direction) -- if no
                // symmetric "apply" method exists yet, that is a small gap to
                // add there, not something to duplicate here.
                settings.ApplyTo(robot, battery);
            }

            void FixedUpdate()
            {
                simulatedSeconds += Time.fixedDeltaTime;

                bool batteryDead = runner.Battery != null && !runner.Battery.CanOperate;
                bool hitSafetyCap = simulatedSeconds >= config.simulatedSeconds;

                if (batteryDead || hitSafetyCap) Finish(success: true);
            }

            void Update()
            {
                realSeconds += Time.unscaledDeltaTime;
            }

            void Finish(bool success)
            {
                enabled = false;

                var result = new RunResult
                {
                    succeeded = success,
                    levelName = config.level.name,
                    seed = config.seed,
                    pattern = config.pattern,
                    picture = config.picture,
                    coveragePercent = runner.CoveragePercent,
                    blockedAreaSquareMeters = runner.BlockedArea,
                    simulatedSeconds = simulatedSeconds,
                    realSeconds = realSeconds,
                };

                // CoverageImage needs the LIVE grid -- this is the one moment
                // it still exists, so the heatmap has to be produced here, not
                // reconstructed later on the Primary side (see RunResult.cs).
                if (runner.Cleaning != null && runner.Cleaning.Grid != null)
                {
                    var texture = CoverageImage.Create(runner.Cleaning.Grid, runner.Level.Level, runner.Level);
                    if (texture != null)
                    {
                        string imagePath = Path.Combine(Path.GetDirectoryName(config.resultPath), "heatmap.png");
                        File.WriteAllBytes(imagePath, texture.EncodeToPNG());
                        result.heatmapImagePath = imagePath;
                    }
                }

                result.WriteTo(config.resultPath);

                if (config.headless)
                {
                    // Nothing left to look at -- safe to tear down and quit.
                    runner.Stop();
                    Application.Quit(0);
                }
                else
                {
                    // Graphics mode stays open so the final state (floor,
                    // robot, heatmap) is still visible -- runner.Stop() would
                    // destroy exactly what we want to keep showing, so it's
                    // skipped here. The robot still needs to be told to
                    // actually STOP, though, not just have this driver stop
                    // watching it -- VacuumRobot's own FixedUpdate keeps
                    // running independently of this component being disabled.
                    if (runner.Robot != null)
                    {
                        runner.Robot.enabled = false;
                        var body = runner.Robot.GetComponent<Rigidbody2D>();
                        if (body != null) body.linearVelocity = Vector2.zero;
                    }
                }
            }
        }
    }
}
