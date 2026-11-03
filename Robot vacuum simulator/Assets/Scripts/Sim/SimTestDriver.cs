using System.IO;
using RobotVacuum.Level;
using RobotVacuum.LevelEditor;
using UnityEngine;

namespace RobotVacuum.Sim
{
    /// <summary>
    /// The one-step way to test a simulation: put this on an object in PlayScene, press Play, and a full,
    /// recorded run starts (config.json, model, PNGs, result.json in a new run folder).
    ///
    /// It is a caller like any other: it picks a level, starts a <see cref="SimulationSession"/> with the
    /// settings below, frames the camera, shows the run's HUD, and shows the end-of-run panel when the run
    /// ends. The run itself is built and recorded by the session, exactly as the Setup screen and the
    /// headless job will build and record theirs.
    ///
    /// Level, in priority order: a level handed over by the Level Editor's Simulate button; the Level Asset
    /// below; the Library File below; the built-in sample apartment.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class SimTestDriver : MonoBehaviour
    {
        [Header("Level (first one set wins; all empty = sample apartment)")]
        [Tooltip("A floor plan asset from the Project window. The run uses a copy, so the asset is never changed.")]
        [SerializeField] LevelData levelAsset;

        [Tooltip("The file name of a floor plan saved by the Level Editor, e.g. \"Kitchen.json\". Looked up in the editor's library folder (LevelLibrary.Folder). Used when Level Asset is empty.")]
        [SerializeField] string libraryFileName;

        [Tooltip("Floor types for the level. Empty = the level's own palette, or the built-in default.")]
        [SerializeField] FloorPalette palette;

        [Header("Run settings (0 or blank = default)")]
        [SerializeField] RunSettings settings = new RunSettings();

        SimulationSession session;
        LevelData runLevel;
        LevelData handedLevel;
        SimHud hud;
        RunEndPanel endPanel;

        void Awake()
        {
            // The Level Editor's Simulate button hands its level over across the scene load. Take it now, in
            // Awake, before SimLauncher's scene-loaded callback would discard it as unclaimed.
            if (LevelHandoff.TryTake(out var handed)) handedLevel = handed;
        }

        void Start()
        {
            runLevel = ResolveLevel(out string source);
            if (runLevel == null) return;

            if (handedLevel != null)
            {
                // A batch experiment asked for from the start screen runs instead of a single run.
                var batch = SimLauncher.TakePendingBatch();
                if (batch != null)
                {
                    new GameObject("Batch Runner").AddComponent<BatchRunner>().Run(runLevel, batch);
                    runLevel = null; // the batch runner owns it now
                    return;
                }

                // A level from the app runs with the configuration chosen there: algorithm, seed, grid, vacuum.
                settings = SimConfig.ToRunSettings();
            }

            if (!SimulationSession.TryStart(runLevel, settings, headless: false, out session, out string error))
            {
                Debug.LogError($"SimTestDriver: {error}", this);
                return;
            }

            session.Finished += OnRunFinished;
            session.Closed += OnSessionClosed;
            Debug.Log($"SimTestDriver: running {source}, seed {session.Settings.seed}. Run folder: {session.RunFolder}", this);

            FrameCamera();

            endPanel = RunEndPanel.Create(transform);

            hud = new GameObject("Simulator HUD").AddComponent<SimHud>();
            hud.RestartRequested = RunAgain;
            hud.LeaveRequested = LeaveToEditor;
            hud.Show(runLevel); // the HUD destroys runLevel when it goes away
        }

        void OnDestroy()
        {
            if (session != null)
            {
                session.Finished -= OnRunFinished;
                session.Closed -= OnSessionClosed;
                session.Close(); // records the run as stopped by user if it was still going
                session = null;
            }

            if (hud == null && runLevel != null) Destroy(runLevel);
        }

        // ---------------------------------------------------------------- level

        LevelData ResolveLevel(out string source)
        {
            if (handedLevel != null)
            {
                source = $"\"{handedLevel.name}\" from the Level Editor";
                return handedLevel; // already a private runtime copy (SimLauncher.BuildLevel)
            }

            if (levelAsset != null)
            {
                source = $"level asset \"{levelAsset.name}\"";
                var snapshot = LevelSnapshot.FromLevel(levelAsset, levelAsset.name);
                return SimLauncher.BuildLevel(snapshot, PaletteFor(levelAsset.Palette));
            }

            if (!string.IsNullOrWhiteSpace(libraryFileName))
            {
                string path = Path.Combine(LevelLibrary.Folder, libraryFileName.Trim());
                source = $"library file \"{path}\"";
                if (!File.Exists(path))
                {
                    Debug.LogError($"SimTestDriver: no floor plan at {path}. Check Library File Name, or clear it to use the sample apartment.", this);
                    return null;
                }

                var snapshot = LevelLibrary.Read(path);
                if (snapshot == null)
                {
                    Debug.LogError($"SimTestDriver: {path} could not be read as a floor plan.", this);
                    return null;
                }

                return SimLauncher.BuildLevel(snapshot, PaletteFor(null));
            }

            source = "the sample apartment";
            var sample = ScriptableObject.CreateInstance<LevelData>();
            sample.name = "Sample apartment";
            sample.hideFlags = HideFlags.DontSave;
            sample.Palette = PaletteFor(null);
            SampleLevels.PopulateApartment(sample);
            return sample;
        }

        FloorPalette PaletteFor(FloorPalette levelsOwn)
        {
            if (palette != null) return palette;
            if (levelsOwn != null) return levelsOwn;
            return SimLauncher.DefaultPalette();
        }

        // ---------------------------------------------------------------- run end

        void OnRunFinished(SimulationSession finished)
        {
            var result = finished.Result;
            string detail = result == null
                ? "No result was recorded."
                : $"Coverage {result.coveragePercent:0.0}%  ·  {FormatTime(result.simulatedSeconds)} simulated  ·  seed {result.seed}";

            if (result != null && !string.IsNullOrEmpty(result.errorMessage))
                detail += "\n" + result.errorMessage;

            endPanel.Show($"Run finished: {Describe(result != null ? result.endReason : RunEndReason.None)}",
                detail, RunAgain, CloseRun);
        }

        void RunAgain()
        {
            if (session == null) return;

            if (endPanel != null) endPanel.Hide();

            if (!session.Restart(out string error))
            {
                Debug.LogError($"SimTestDriver: {error}", this);
                return;
            }

            Debug.Log($"SimTestDriver: new run, seed {session.Settings.seed}. Run folder: {session.RunFolder}", this);
            if (hud != null) hud.Rebind();
        }

        void CloseRun()
        {
            if (session != null) session.Close();
        }

        void LeaveToEditor()
        {
            CloseRun();
            SimLauncher.ReturnToEditor();
        }

        void OnSessionClosed(SimulationSession closed)
        {
            if (endPanel != null) endPanel.Hide();

            // With the run gone the HUD has nothing to show. Destroying it also puts the time scale back and
            // releases the run's level.
            if (hud != null) Destroy(hud.gameObject);
            hud = null;
            runLevel = null;
            session = null;
        }

        // ---------------------------------------------------------------- camera

        void FrameCamera()
        {
            var camera = Camera.main;
            if (camera == null)
            {
                Debug.LogWarning("SimTestDriver: no Main Camera in the scene, so the run isn't framed.", this);
                return;
            }

            var renderer = session.Runner.Level;
            var bounds = runLevel.Bounds();
            Vector2 a = renderer.LevelToWorld(bounds.min);
            Vector2 b = renderer.LevelToWorld(bounds.max);
            var world = Rect.MinMaxRect(Mathf.Min(a.x, b.x), Mathf.Min(a.y, b.y), Mathf.Max(a.x, b.x), Mathf.Max(a.y, b.y));

            SimLauncher.FrameCamera(camera, world);
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.063f, 0.075f, 0.094f);
        }

        // ---------------------------------------------------------------- text

        static string Describe(RunEndReason reason)
        {
            switch (reason)
            {
                case RunEndReason.Completed: return "completed";
                case RunEndReason.BatteryDepleted: return "battery empty";
                case RunEndReason.TimeCap: return "time limit reached";
                case RunEndReason.StoppedByUser: return "stopped";
                case RunEndReason.Error: return "error";
                case RunEndReason.Degenerate: return "no progress";
                default: return "unknown";
            }
        }

        static string FormatTime(float seconds)
        {
            var span = System.TimeSpan.FromSeconds(Mathf.Max(0f, seconds));
            return span.TotalHours >= 1
                ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}"
                : $"{span.Minutes:00}:{span.Seconds:00}";
        }
    }
}
