using RobotVacuum.Level;
using RobotVacuum.Sim;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace RobotVacuum.LevelEditor
{
    /// <summary>The pages of the start screen, which other scenes can ask it to open on.</summary>
    public enum StartPage { Home, Configuration, Simulate, Analysis }

    /// <summary>Where a simulation was started from, and so where leaving it goes back to.</summary>
    public enum SimOrigin { Editor, StartScreen }

    /// <summary>
    /// Moves between the app's scenes: the start screen, the level editor, and the simulation scene a
    /// floor plan is played in. Every scene here must be in the build profile's scene list to load.
    /// </summary>
    public static class SimLauncher
    {
        public const string StartScenePath = "Assets/Scenes/StartScreen.unity";
        public const string EditorScenePath = "Assets/Scenes/LevelEditor.unity";
        public const string SimScenePath = "Assets/Scenes/PlayScene.unity";

        static FloorPalette fallbackPalette;
        static string returnPath;
        static StartPage startPage;
        static string focusedRunId;
        static BatchSpec pendingBatch;

        /// <summary>The movement algorithm, shared with every screen through <see cref="SimConfig"/>.</summary>
        public static MovementPattern MovementPattern
        {
            get => SimConfig.Pattern;
            set => SimConfig.Pattern = value;
        }

        /// <summary>What the Picture pattern draws, shared the same way.</summary>
        public static PictureKind Picture
        {
            get => SimConfig.Picture;
            set => SimConfig.Picture = value;
        }

        /// <summary>Where the running simulation was started from.</summary>
        public static SimOrigin Origin { get; private set; }

        /// <summary>The file of the floor plan playing in the simulator, or null.</summary>
        public static string SimulatedPath { get; private set; }

        public static bool OpenStartScreen(StartPage page = StartPage.Home)
        {
            startPage = page;
            return Load(StartScenePath);
        }

        /// <summary>Opens the Analysis page with <paramref name="runId"/> selected.</summary>
        public static bool OpenAnalysis(string runId)
        {
            focusedRunId = runId;
            return OpenStartScreen(StartPage.Analysis);
        }

        /// <summary>Opens the editor, reopening <paramref name="filePath"/> when given.</summary>
        public static bool OpenLevelEditor(string filePath = null)
        {
            returnPath = filePath;
            return Load(EditorScenePath);
        }

        /// <summary>Leaves the simulator for wherever it was started from.</summary>
        public static bool ReturnFromSimulation() => Origin == SimOrigin.StartScreen
            ? OpenStartScreen(StartPage.Simulate)
            : OpenLevelEditor(SimulatedPath);

        /// <summary>The start page asked for, handed out once.</summary>
        public static StartPage TakeStartPage()
        {
            var page = startPage;
            startPage = StartPage.Home;
            return page;
        }

        /// <summary>The run the Analysis page should select, handed out once.</summary>
        public static string TakeFocusedRun()
        {
            string id = focusedRunId;
            focusedRunId = null;
            return id;
        }

        /// <summary>
        /// Loads the simulation scene with <paramref name="snapshot"/> in place of its saved level.
        /// <paramref name="filePath"/> is reopened when the player returns to the editor.
        /// </summary>
        public static bool Simulate(LevelSnapshot snapshot, FloorPalette palette, string filePath,
            SimOrigin origin = SimOrigin.Editor)
        {
            if (snapshot == null || !CanLoad(SimScenePath)) return false;

            LevelHandoff.Send(BuildLevel(snapshot, palette != null ? palette : FallbackPalette()));
            SimulatedPath = filePath;
            Origin = origin;

            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneLoaded += OnSceneLoaded;
            SceneManager.LoadScene(SimScenePath);
            return true;
        }

        /// <summary>
        /// Loads the simulation scene and runs <paramref name="spec"/>'s batch on <paramref name="snapshot"/>
        /// instead of a single interactive run. Returns to the start screen's Analysis page when done.
        /// </summary>
        public static bool RunBatch(LevelSnapshot snapshot, FloorPalette palette, string filePath, BatchSpec spec)
        {
            if (spec == null || spec.patterns.Count == 0 || spec.trials < 1) return false;

            pendingBatch = spec;
            if (Simulate(snapshot, palette, filePath, SimOrigin.StartScreen)) return true;

            pendingBatch = null;
            return false;
        }

        /// <summary>The floor plan the editor should reopen, handed out once.</summary>
        public static string TakeReturnPath()
        {
            string path = returnPath;
            returnPath = null;
            return path;
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

        static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (scene.path != SimScenePath) return;
            SceneManager.sceneLoaded -= OnSceneLoaded;

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

            MovingCat.SpawnPlacedCats(renderer, renderer.transform);

            // From here on the engine steps everything in a fixed order; nothing steps itself.
            var engine = new GameObject("Simulation Engine").AddComponent<SimEngine>();
            var world = new SimWorld(engine, level, renderer);

            if (Camera.main != null) FrameCamera(Camera.main, WorldBounds(renderer, level));

            var batch = pendingBatch;
            pendingBatch = null;
            if (batch != null) new GameObject("Batch Runner").AddComponent<BatchRunner>().Run(world, batch);
            else new GameObject("Simulator HUD").AddComponent<SimHud>().Show(world);
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
            startPage = StartPage.Home;
            focusedRunId = null;
            pendingBatch = null;
            SimulatedPath = null;
            Origin = SimOrigin.Editor;
            fallbackPalette = null;
        }
    }
}
