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
            public PictureKind picture;

            /// <summary>
            /// Seeds every random choice the vacuum makes. The same level, pattern and seed give the same
            /// run whether it is watched or headless, so results can be reproduced and compared.
            /// </summary>
            public int seed;

            public static Options Visible(MovementPattern pattern = MovementPattern.RandomBounce,
                PictureKind picture = PictureKind.Heart, int seed = 0) =>
                new Options { visuals = true, pattern = pattern, picture = picture, seed = seed };

            /// <summary>Colliders and coverage only: no meshes, no heatmap.</summary>
            public static Options Headless(MovementPattern pattern = MovementPattern.RandomBounce,
                PictureKind picture = PictureKind.Heart, int seed = 0) =>
                new Options { visuals = false, pattern = pattern, picture = picture, seed = seed };
        }

        SimulationRunner(GameObject root, LevelRenderer level, VacuumRobot robot, VacuumCleaningController cleaning, int seed)
        {
            Root = root;
            Level = level;
            Robot = robot;
            Cleaning = cleaning;
            Seed = seed;
            Battery = robot != null ? robot.GetComponent<Battery>() : null;
        }

        public GameObject Root { get; }
        public LevelRenderer Level { get; }
        public VacuumRobot Robot { get; }
        public VacuumCleaningController Cleaning { get; }
        public Battery Battery { get; }

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
            robot.Picture = options.picture;
            robot.Seed = options.seed;

            var cleaning = robotObject.AddComponent<VacuumCleaningController>();

            if (options.visuals)
            {
                var heatmap = new GameObject("Coverage Heatmap");
                heatmap.transform.SetParent(root.transform, false);
                heatmap.AddComponent<CoverageHeatmapRenderer>();
            }

            robot.ResetToSpawn();
            return new SimulationRunner(root, renderer, robot, cleaning, options.seed);
        }

        /// <summary>Puts the vacuum back at its start point and forgets all coverage.</summary>
        public void Restart()
        {
            if (Robot != null) Robot.ResetToSpawn();
            if (Cleaning != null) Cleaning.ResetCoverage();
        }

        public void Stop()
        {
            if (Root == null) return;

            if (Application.isPlaying) Object.Destroy(Root);
            else Object.DestroyImmediate(Root);
        }
    }
}
