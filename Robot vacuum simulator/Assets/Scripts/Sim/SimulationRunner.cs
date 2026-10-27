using RobotVacuum.Level;
using UnityEngine;

namespace RobotVacuum.Sim
{
    /// <summary>
    /// Builds and owns a running simulation: the level with its colliders, the vacuum, and coverage
    /// tracking. Nothing here touches the UI, so a headless run can build one, step physics and read
    /// <see cref="CoveragePercent"/> without a single panel or camera. The editor's Run uses the same
    /// runner and adds its own camera and HUD on top.
    /// </summary>
    public sealed class SimulationRunner
    {
        /// <summary>What to build. The defaults are a full visible run.</summary>
        public struct Options
        {
            /// <summary>Floor, wall and furniture meshes, and the coverage heatmap. Off for headless runs.</summary>
            public bool visuals;

            public MovementPattern pattern;

            /// <summary>
            /// Seeds every random choice the vacuum makes. The same level, pattern and seed give the same
            /// run whether it is watched or headless, so results can be reproduced and compared.
            /// </summary>
            public int seed;

            /// <summary>
            /// target_max_clean for this run: the cleanliness each cell is aiming for. Zero or less means
            /// "use <see cref="HeatmapDefaults.TargetMaxClean"/>".
            /// </summary>
            public float targetMaxClean;

            /// <summary>Which view the on-screen heatmap starts in. Can be changed during the run.</summary>
            public HeatmapMode heatmapMode;

            /// <summary>Grid cell edge length in metres. Zero or less means <see cref="SimDefaults.CellSizeMeters"/>.</summary>
            public float cellSizeMeters;

            /// <summary>Cleanliness per second of dwell on an effort-1 floor. Zero or less means <see cref="HeatmapDefaults.BaseVacuumPower"/>.</summary>
            public float vacuumPower;

            /// <summary>The build options for a run described by already-resolved settings.</summary>
            public static Options From(RunSettings settings, bool headless) => new Options
            {
                visuals = !headless,
                pattern = settings.pattern,
                seed = settings.seed,
                targetMaxClean = settings.targetMaxClean,
                heatmapMode = settings.heatmapMode,
                cellSizeMeters = settings.cellSizeMeters,
                vacuumPower = settings.vacuumPower,
            };

            public static Options Visible(MovementPattern pattern = MovementPattern.RandomBounce, int seed = 0,
                float targetMaxClean = 0f, HeatmapMode heatmapMode = HeatmapMode.FloorCleanliness) =>
                new Options { visuals = true, pattern = pattern, seed = seed, targetMaxClean = targetMaxClean, heatmapMode = heatmapMode };

            /// <summary>Colliders and coverage only: no meshes, no heatmap.</summary>
            public static Options Headless(MovementPattern pattern = MovementPattern.RandomBounce, int seed = 0,
                float targetMaxClean = 0f) =>
                new Options { visuals = false, pattern = pattern, seed = seed, targetMaxClean = targetMaxClean };
        }

        SimulationRunner(GameObject root, LevelRenderer level, VacuumRobot robot, VacuumCleaningController cleaning, CoverageHeatmapRenderer heatmap, int seed)
        {
            Root = root;
            Level = level;
            Robot = robot;
            Cleaning = cleaning;
            Heatmap = heatmap;
            Seed = seed;
            Battery = robot != null ? robot.GetComponent<Battery>() : null;
        }

        public GameObject Root { get; }
        public LevelRenderer Level { get; }
        public VacuumRobot Robot { get; }
        public VacuumCleaningController Cleaning { get; }
        public Battery Battery { get; }

        /// <summary>The on-screen heatmap, or null for a headless run. Set its Mode to switch views mid-run.</summary>
        public CoverageHeatmapRenderer Heatmap { get; }

        /// <summary>The seed this run was built with.</summary>
        public int Seed { get; }

        /// <summary>How much of the cleanable floor is clean, 0 to 100.</summary>
        public float CoveragePercent => Cleaning != null ? Cleaning.CoveragePercent : 0f;

        /// <summary>Floor the vacuum can never reach because furniture blocks it, in square metres.</summary>
        public float BlockedArea => Cleaning != null && Cleaning.Grid != null ? Cleaning.Grid.NonCleanableAreaSquareMeters : 0f;

        /// <summary>
        /// Builds everything needed to run <paramref name="level"/> under one root object. The caller owns
        /// the result and ends it with <see cref="Stop"/>.
        /// </summary>
        public static SimulationRunner Start(LevelData level, Options options)
        {
            var root = new GameObject("Simulation");

            var levelObject = new GameObject("Level");
            levelObject.transform.SetParent(root.transform, false);
            var renderer = levelObject.AddComponent<LevelRenderer>();
            renderer.DrawMeshes = options.visuals; // colliders are built either way
            renderer.Level = level;

            // The robot and the coverage grid both read the level as they wake, so it exists first.
            var robotObject = new GameObject("Vacuum Robot");
            robotObject.transform.SetParent(root.transform, false);
            var robot = robotObject.AddComponent<VacuumRobot>();
            robot.Pattern = options.pattern;
            robot.Seed = options.seed;

            // ONE shared instance for the whole run -- see VacuumRobot.RandomSource's doc comment for why
            // this can't just be left to each consumer building its own from the same seed independently.
            // (int)->(uint) is a plain bit-reinterpreting cast: deterministic for any given seed, including
            // negative ones, so it doesn't break reproducibility.
            robot.RandomSource = new RandomProvider((uint)options.seed);

            var cleaning = robotObject.AddComponent<VacuumCleaningController>();
            cleaning.Initialize(options.targetMaxClean, options.cellSizeMeters, options.vacuumPower);

            CoverageHeatmapRenderer heatmapRenderer = null;
            if (options.visuals)
            {
                var heatmap = new GameObject("Coverage Heatmap");
                heatmap.transform.SetParent(root.transform, false);
                heatmapRenderer = heatmap.AddComponent<CoverageHeatmapRenderer>();
                heatmapRenderer.Mode = options.heatmapMode;
            }

            // Cats placed in the floor plan roam the run. Their random choices come from the same seed, so a
            // repeat of the run meets the same cats doing the same things.
            SimRandom.Reseed(options.seed);
            MovingCat.SpawnPlacedCats(renderer, root.transform);

            robot.ResetToSpawn();
            return new SimulationRunner(root, renderer, robot, cleaning, heatmapRenderer, options.seed);
        }

        /// <summary>Puts the vacuum back at its start point and forgets all coverage.</summary>
        public void Restart()
        {
            if (Robot != null) Robot.ResetToSpawn();
            if (Cleaning != null) Cleaning.ResetCoverage();
        }

        /// <summary>
        /// Destroys everything this run built. Pass <paramref name="immediate"/> when a new run is built in the
        /// same frame: a plain Destroy only happens at the end of the frame, and the new run's components find
        /// their level and controller with FindAnyObjectByType as they wake, so they could bind to the old,
        /// still-alive objects.
        /// </summary>
        public void Stop(bool immediate = false)
        {
            if (Root == null) return;

            if (Application.isPlaying && !immediate) Object.Destroy(Root);
            else Object.DestroyImmediate(Root);
        }
    }
}
