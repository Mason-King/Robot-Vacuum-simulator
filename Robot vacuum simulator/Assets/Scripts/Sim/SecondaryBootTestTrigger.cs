using RobotVacuum.Level;
using RobotVacuum.LevelEditor;
using UnityEngine;

namespace RobotVacuum.Sim
{
    // ============================================================================
    // SecondaryBootTestTrigger.cs
    //
    // THROWAWAY -- for manually proving SecondaryBootLauncher actually works,
    // before wiring it to the real Setup Screen button. Delete this once that's
    // confirmed and the real button exists. Never show this to a teammate as
    // "the" way to launch a run.
    //
    // Usage: drop this on any empty GameObject in any scene (a fresh empty scene
    // is fine -- it doesn't need a level loaded in the scene itself). Fill in
    // the two fields in the Inspector, enter Play mode, then right-click this
    // component's header (or the gear icon) and choose "Run Test Launch" from
    // the context menu.
    // ============================================================================
    public class SecondaryBootTestTrigger : MonoBehaviour
    {
        [Tooltip("Any real floor plan asset -- drag one in from your project.")]
        [SerializeField] LevelData testLevel;

        [Tooltip("Full path to the built player .exe, e.g. C:/Builds/RobotVacuumSimulator.exe")]
        [SerializeField] string executablePath;

        [SerializeField] bool headless = true;
        [SerializeField] int seed = 1;

        [Tooltip("Time.timeScale while watching a Graphics-mode run. Ignored in headless (that always runs fast).")]
        [SerializeField] float playbackSpeed = 1f;

        [Header("Vacuum settings for this test run")]
        [SerializeField] float driveSpeed = 0.3f;
        [SerializeField] float reverseSpeed = 0.2f;
        [SerializeField] float turnSpeed = 90f;
        [SerializeField] float batteryLifeSeconds = 30f;

        [ContextMenu("Run Test Launch")]
        void RunTestLaunch()
        {
            if (testLevel == null)
            {
                Debug.LogError("SecondaryBootTestTrigger: assign a LevelData in the Inspector first.");
                return;
            }
            if (string.IsNullOrEmpty(executablePath))
            {
                Debug.LogError("SecondaryBootTestTrigger: assign the built .exe's path in the Inspector first.");
                return;
            }

            SecondaryBootLauncher.SecondaryBootExecutablePath = executablePath;

            // Now driven by the Inspector fields above, not hardcoded -- so
            // changing battery life (or speeds) between test runs actually
            // does something.
            var settings = new VacuumSettings
            {
                driveSpeed = driveSpeed,
                reverseSpeed = reverseSpeed,
                turnSpeed = turnSpeed,
                batteryLifeSeconds = batteryLifeSeconds,
                useFeet = false,
            };

            Debug.Log("SecondaryBootTestTrigger: launching...");

            SecondaryBootLauncher.Launch(testLevel, settings, MovementPattern.RandomBounce,
                PictureKind.Heart, seed, headless, OnComplete, playbackSpeed);
        }

        void OnComplete(RunResult result)
        {
            if (result.succeeded)
            {
                Debug.Log($"SecondaryBootTestTrigger: SUCCESS -- coverage={result.coveragePercent:F1}%, " +
                          $"simulatedSeconds={result.simulatedSeconds:F1}, realSeconds={result.realSeconds:F1}, " +
                          $"heatmap={result.heatmapImagePath}");
            }
            else
            {
                Debug.LogError($"SecondaryBootTestTrigger: FAILED -- {result.errorMessage}");
            }
        }
    }
}
