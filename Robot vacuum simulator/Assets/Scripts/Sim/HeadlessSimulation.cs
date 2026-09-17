using System;
using RobotVacuum.Level;
using RobotVacuumSim.EnvironmentModel;
using UnityEngine;

namespace RobotVacuum.Sim
{
    /// <summary>What a run produced, once it has finished or been stopped.</summary>
    public readonly struct SimulationResults
    {
        public readonly string levelName;
        public readonly float simulatedSeconds;
        public readonly float realSeconds;
        public readonly float coveragePercent;
        public readonly float metresDriven;
        public readonly float blockedArea;
        public readonly bool batteryRanOut;

        /// <summary>The seed that produced this run. Running the same level and pattern again with it repeats the result.</summary>
        public readonly int seed;

        /// <summary>
        /// A heatmap of the floor as the run left it, taken before the grid was torn down. The receiver owns
        /// it and destroys it when done; null when there was no grid to draw.
        /// </summary>
        public readonly Texture2D coverage;

        public SimulationResults(string levelName, float simulatedSeconds, float realSeconds, float coveragePercent,
            float metresDriven, float blockedArea, bool batteryRanOut, int seed = 0, Texture2D coverage = null)
        {
            this.levelName = levelName;
            this.simulatedSeconds = simulatedSeconds;
            this.realSeconds = realSeconds;
            this.coveragePercent = coveragePercent;
            this.metresDriven = metresDriven;
            this.blockedArea = blockedArea;
            this.batteryRanOut = batteryRanOut;
            this.seed = seed;
            this.coverage = coverage;
        }

        /// <summary>Simulated seconds per real second, or 0 before enough time has passed to tell.</summary>
        public float SpeedUp => realSeconds > 0.01f ? simulatedSeconds / realSeconds : 0f;

        public static string FormatDuration(float seconds)
        {
            var span = TimeSpan.FromSeconds(Mathf.Max(0f, seconds));
            return $"{(int)span.TotalMinutes:00}:{span.Seconds:00}";
        }
    }

    /// <summary>
    /// Runs a floor plan with no visuals, no heatmap and no camera, stepping the engine as fast as it will
    /// go, and reports what happened. Nothing here needs a panel: the UI only watches the numbers, so a
    /// batch-mode run can call <see cref="Start"/> the same way.
    ///
    /// The run is stepped entirely on the physics clock, so its result depends on the seed and not on how
    /// fast the machine managed to step it: the same seed watched in the simulator gives the same numbers.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class HeadlessSimulation : MonoBehaviour
    {
        /// <summary>Default simulated seconds per real second.</summary>
        public const float DefaultSpeed = 25f;

        /// <summary>
        /// The longest slice of time the engine will simulate in one frame. Raising it is what actually
        /// makes a fast run fast: it lets many physics steps run per frame instead of the usual few.
        /// Dropping steps under load only slows the run down; it never changes the route the vacuum takes.
        /// </summary>
        public const float MaxFrameSeconds = 1f;

        SimulationRunner runner;
        LevelData level;
        bool ownsLevel;
        string levelName;
        float targetSeconds;
        float savedTimeScale;
        float savedMaxDeltaTime;
        bool finished;

        public bool Running => runner != null && !finished;
        public float SimulatedSeconds { get; private set; }
        public float RealSeconds { get; private set; }
        public float TargetSeconds => targetSeconds;
        public float Progress => targetSeconds > 0f ? Mathf.Clamp01(SimulatedSeconds / targetSeconds) : 0f;
        public float CoveragePercent => runner != null ? runner.CoveragePercent : 0f;

        /// <summary>The coverage grid while the run is going, for previews. Null once it has ended.</summary>
        public ExternalModelGrid Grid => runner != null && runner.Cleaning != null ? runner.Cleaning.Grid : null;

        /// <summary>A heatmap of the floor as it stands, or null once the run has ended. The caller owns it.</summary>
        public Texture2D CreateImage() => CoverageImage.Create(Grid, LevelOfRun, runner != null ? runner.Level : null);

        /// <summary>Refreshes an image from <see cref="CreateImage"/>. False when it no longer fits the grid.</summary>
        public bool RepaintImage(Texture2D texture) =>
            CoverageImage.Repaint(texture, Grid, LevelOfRun, runner != null ? runner.Level : null);

        /// <summary>The level being run, read back from the renderer so it is right whoever owns it.</summary>
        LevelData LevelOfRun => runner != null && runner.Level != null ? runner.Level.Level : null;

        /// <summary>The seed this run was started with.</summary>
        public int Seed { get; private set; }

        /// <summary>Filled in when the run ends.</summary>
        public SimulationResults Results { get; private set; }

        /// <summary>The run ended, either by reaching its time or because the battery died.</summary>
        public event Action<SimulationResults> Finished;

        /// <summary>
        /// Starts a run of <paramref name="simulatedSeconds"/>, ending early if the battery goes flat. With
        /// <paramref name="ownsLevel"/> the run destroys <paramref name="level"/> when it ends; pass false
        /// to run a level something else is still using, such as the one open in the editor.
        /// </summary>
        public static HeadlessSimulation Start(LevelData level, string levelName, float simulatedSeconds,
            MovementPattern pattern, PictureKind picture, int seed = 0, float speed = DefaultSpeed, bool ownsLevel = true)
        {
            var host = new GameObject("Headless Simulation");
            var simulation = host.AddComponent<HeadlessSimulation>();
            simulation.Begin(level, levelName, simulatedSeconds, pattern, picture, seed, speed, ownsLevel);
            return simulation;
        }

        void Begin(LevelData levelData, string name, float simulatedSeconds, MovementPattern pattern,
            PictureKind picture, int seed, float speed, bool owns)
        {
            level = levelData;
            ownsLevel = owns;
            levelName = name;
            Seed = seed;
            targetSeconds = Mathf.Max(1f, simulatedSeconds);

            savedTimeScale = Time.timeScale;
            savedMaxDeltaTime = Time.maximumDeltaTime;
            Time.timeScale = Mathf.Max(1f, speed);
            Time.maximumDeltaTime = MaxFrameSeconds;

            runner = SimulationRunner.Start(level, SimulationRunner.Options.Headless(pattern, picture, seed));
        }

        void FixedUpdate()
        {
            if (!Running) return;

            SimulatedSeconds += Time.fixedDeltaTime;

            bool flat = runner.Battery != null && !runner.Battery.CanOperate;
            if (flat || SimulatedSeconds >= targetSeconds) Finish(flat);
        }

        void Update()
        {
            if (Running) RealSeconds += Time.unscaledDeltaTime;
        }

        /// <summary>Ends the run now and reports what it managed in the time it had.</summary>
        public void Stop()
        {
            if (!finished) Finish(false);
        }

        void Finish(bool batteryRanOut)
        {
            finished = true;

            // Drawn before the grid goes with the runner, so the results carry a picture of the floor.
            var coverage = CreateImage();

            Results = new SimulationResults(levelName, SimulatedSeconds, RealSeconds, runner.CoveragePercent,
                runner.Robot != null ? runner.Robot.DistanceTravelled : 0f, runner.BlockedArea, batteryRanOut,
                Seed, coverage);

            Time.timeScale = savedTimeScale;
            Time.maximumDeltaTime = savedMaxDeltaTime;

            runner.Stop();
            runner = null;

            if (ownsLevel) DestroyNow(level);
            level = null;

            // Reported before this object goes, so a listener can read Results.
            Finished?.Invoke(Results);
            DestroyNow(gameObject);
        }

        /// <summary>Tidies up from a play-mode run or an edit-mode test alike.</summary>
        static void DestroyNow(UnityEngine.Object target)
        {
            if (target == null) return;

            if (Application.isPlaying) UnityEngine.Object.Destroy(target);
            else UnityEngine.Object.DestroyImmediate(target);
        }
    }
}
