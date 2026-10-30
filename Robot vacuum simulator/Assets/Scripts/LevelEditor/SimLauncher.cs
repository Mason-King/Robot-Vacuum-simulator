using RobotVacuum.Level;
using RobotVacuum.Sim;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace RobotVacuum.LevelEditor
{
    /// <summary>
    /// Moves between the app's scenes: the start screen, the level editor, and the simulation scene a
    /// floor plan is played in. Every scene here must be in the build profile's scene list to load.
    /// </summary>
    /// <summary>Where leaving the simulator goes back to.</summary>
    public enum ReturnTarget { Editor, RunSetup }

    public static class SimLauncher
    {
        public const string StartScenePath = "Assets/Scenes/StartScreen.unity";
        public const string EditorScenePath = "Assets/Scenes/LevelEditor.unity";
        public const string SimScenePath = "Assets/Scenes/PlayScene.unity";

        static FloorPalette fallbackPalette;
        static string returnPath;
        static ReturnTarget returnTarget;
        static RunMode pendingMode;

        /// <summary>How the last run was played, so its Run setup page reopens on the same choice.</summary>
        public static RunMode LastMode { get; private set; }

        /// <summary>The next run's movement algorithm. Changing it customizes the next run (see <see cref="RunPreferences"/>).</summary>
        public static MovementPattern MovementPattern
        {
            get => RunPreferences.Run.pattern;
            set
            {
                if (RunPreferences.Run.pattern == value) return;
                RunPreferences.Run.pattern = value;
                RunPreferences.MarkRunCustomized();
            }
        }

        public static bool OpenStartScreen() => Load(StartScenePath);

        /// <summary>The floor plan list, where every run starts. It lives in the editor scene.</summary>
        public static bool OpenFloorPlans()
        {
            returnPath = null;
            return Load(EditorScenePath);
        }

        public static bool OpenLevelEditor() => OpenFloorPlans();

        /// <summary>
        /// Leaves the simulator for where the run was started: the editor with the floor plan open, or that
        /// floor plan's Run setup page.
        /// </summary>
        public static bool ReturnToEditor() => Load(EditorScenePath);

        /// <summary>
        /// Loads the simulation scene with <paramref name="snapshot"/> in place of its saved level, played in
        /// <paramref name="mode"/> with <see cref="RunPreferences.Run"/>. Leaving the run goes back to
        /// <paramref name="filePath"/> in the editor, or on its Run setup page.
        /// </summary>
        public static bool Simulate(LevelSnapshot snapshot, FloorPalette palette, string filePath,
            RunMode mode = RunMode.Watch, ReturnTarget returnTo = ReturnTarget.Editor)
        {
            if (snapshot == null || !CanLoad(SimScenePath)) return false;

            var level = BuildLevel(snapshot, palette != null ? palette : FallbackPalette());
            if (LevelGeometry.HasFurnitureWallCollision(level))
            {
                Debug.LogWarning("Simulation cannot start because furniture overlaps a wall. Move the furniture clear of walls and try again.");
                Object.Destroy(level);
                return false;
            }

            LevelHandoff.Send(level);
            returnPath = filePath;
            returnTarget = returnTo;
            pendingMode = mode;
            LastMode = mode;

            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneLoaded += OnSceneLoaded;
            SceneManager.LoadScene(SimScenePath);
            return true;
        }

        /// <summary>The floor plan the editor should reopen, and where, handed out once.</summary>
        public static string TakeReturnPath(out ReturnTarget target)
        {
            string path = returnPath;
            target = returnTarget;
            returnPath = null;
            returnTarget = ReturnTarget.Editor;
            return path;
        }

        /// <summary>How the run being started should play, handed out once.</summary>
        public static RunMode TakeRunMode()
        {
            var mode = pendingMode;
            pendingMode = RunMode.Watch;
            return mode;
        }

        /// <summary>A live copy of a snapshot that shares nothing with it and survives the scene load.</summary>
        public static LevelData BuildLevel(LevelSnapshot snapshot, FloorPalette palette)
        {
            var level = ScriptableObject.CreateInstance<LevelData>();
            level.name = snapshot.name;
            level.hideFlags = HideFlags.DontUnloadUnusedAsset;
            level.Palette = palette;

            LevelSnapshot.Parse(snapshot.ToJson(false))?.ApplyTo(level);
            return level;
        }

        /// <summary>Fits an orthographic camera around <paramref name="bounds"/>, leaving room for the top bar.</summary>
        public static void FrameCamera(Camera camera, Rect bounds)
        {
            float aspect = Mathf.Max(0.1f, camera.aspect);

            camera.orthographic = true;
            camera.orthographicSize = Mathf.Max(bounds.height * 0.5f, bounds.width * 0.5f / aspect) * 1.2f + 0.6f;
            camera.transform.position = new Vector3(bounds.center.x, bounds.center.y - camera.orthographicSize * 0.06f, -10f);
            camera.transform.rotation = Quaternion.identity;
        }

        /// <summary>
        /// The palette runs fall back to when a level has none: the built-in default, created once and kept
        /// alive across scene loads.
        /// </summary>
        public static FloorPalette DefaultPalette() => FallbackPalette();

        static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (scene.path != SimScenePath) return;
            SceneManager.sceneLoaded -= OnSceneLoaded;

            // A scene with a SimTestDriver builds and runs the level itself: the driver has already taken the
            // handed-over level in its Awake, and it adds the camera framing and the HUD. Nothing to do here.
            if (Object.FindAnyObjectByType<SimTestDriver>() != null) return;

            // Runs after every Awake in the scene but before any Start, so the robot has not moved yet.
            var orphan = LevelHandoff.CancelPending();
            if (orphan != null)
            {
                Debug.LogError($"Simulator: {SimScenePath} has no LevelRenderer, so the floor plan couldn't be shown.");
                Object.Destroy(orphan);
                return;
            }

            var level = LevelHandoff.Delivered;
            var renderer = LevelRenderer.FindFor(level);
            if (renderer == null) return;

            foreach (var robot in Object.FindObjectsByType<VacuumRobot>(FindObjectsSortMode.None))
            {
                robot.Pattern = MovementPattern;
                robot.ResetToSpawn();
            }

            MovingCat.SpawnPlacedCats(renderer, renderer.transform);

            if (Camera.main != null) FrameCamera(Camera.main, WorldBounds(renderer, level));

            new GameObject("Simulator HUD").AddComponent<SimHud>().Show(level);
        }

        static Rect WorldBounds(LevelRenderer renderer, LevelData level)
        {
            var bounds = level.Bounds();
            Vector2 a = renderer.LevelToWorld(bounds.min);
            Vector2 b = renderer.LevelToWorld(bounds.max);
            return Rect.MinMaxRect(Mathf.Min(a.x, b.x), Mathf.Min(a.y, b.y), Mathf.Max(a.x, b.x), Mathf.Max(a.y, b.y));
        }

        static FloorPalette FallbackPalette()
        {
            if (fallbackPalette != null) return fallbackPalette;

            fallbackPalette = FloorPalette.CreateDefault();
            fallbackPalette.hideFlags = HideFlags.DontUnloadUnusedAsset;
            foreach (var floor in fallbackPalette.Entries) floor.hideFlags = HideFlags.DontUnloadUnusedAsset;
            return fallbackPalette;
        }

        static bool CanLoad(string path)
        {
            if (SceneUtility.GetBuildIndexByScenePath(path) >= 0) return true;

            Debug.LogError($"Can't open {path}: add it to the scene list in File > Build Profiles.");
            return false;
        }

        static bool Load(string path)
        {
            if (!CanLoad(path)) return false;

            SceneManager.LoadScene(path);
            return true;
        }

        // Statics survive entering Play Mode when domain reload is off.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            returnPath = null;
            returnTarget = ReturnTarget.Editor;
            pendingMode = RunMode.Watch;
            LastMode = RunMode.Watch;
            fallbackPalette = null;
        }
    }
}
