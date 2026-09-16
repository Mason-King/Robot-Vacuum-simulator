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
    public static class SimLauncher
    {
        public const string StartScenePath = "Assets/Scenes/StartScreen.unity";
        public const string EditorScenePath = "Assets/Scenes/LevelEditor.unity";
        public const string SimScenePath = "Assets/Scenes/PlayScene.unity";

        static FloorPalette fallbackPalette;
        static string returnPath;

        /// <summary>The movement algorithm chosen last, carried between the editor's run and the simulator.</summary>
        public static MovementPattern MovementPattern { get; set; }

        /// <summary>What the Picture pattern draws, carried the same way.</summary>
        public static PictureKind Picture { get; set; }

        public static bool OpenStartScreen() => Load(StartScenePath);

        public static bool OpenLevelEditor() => Load(EditorScenePath);

        /// <summary>Back to the editor, which reopens the floor plan that was playing.</summary>
        public static bool ReturnToEditor() => Load(EditorScenePath);

        /// <summary>
        /// Loads the simulation scene with <paramref name="snapshot"/> in place of its saved level.
        /// <paramref name="filePath"/> is reopened when the player returns to the editor.
        /// </summary>
        public static bool Simulate(LevelSnapshot snapshot, FloorPalette palette, string filePath)
        {
            if (snapshot == null || !CanLoad(SimScenePath)) return false;

            LevelHandoff.Send(BuildLevel(snapshot, palette != null ? palette : FallbackPalette()));
            returnPath = filePath;

            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneLoaded += OnSceneLoaded;
            SceneManager.LoadScene(SimScenePath);
            return true;
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

            foreach (var robot in Object.FindObjectsByType<VacuumRobot>(FindObjectsSortMode.None))
            {
                robot.Pattern = MovementPattern;
                robot.Picture = Picture;
                robot.ResetToSpawn();
            }

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
            fallbackPalette = null;
            MovementPattern = MovementPattern.RandomBounce;
            Picture = PictureKind.Heart;
        }
    }
}
