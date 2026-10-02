using System;
using RobotVacuum.Level;
using RobotVacuum.LevelEditor;
using UnityEngine;
using UnityEngine.UI;

namespace RobotVacuum.Sim
{
    // The in-memory, un-serialized counterpart to RunConfig -- carries
    // gathered UI values to SimBootStart.Run. Lives here (not a separate
    // file) so the production pair (this file + SimBootStart.cs) has zero
    // dependency on anything outside itself.
    public struct SimRunRequest
    {
        public LevelData level;
        public VacuumSettings vacuumSettings;
        public MovementPattern pattern;
        public PictureKind picture;
        public int seed;
        public bool headless;
        public float playbackSpeed;
    }

    // ============================================================================
    // SimStartController.cs
    //
    // PRODUCTION -- this ships. Attach to the real Run button's GameObject.
    // Contains nothing test-related, references nothing from TestSimStart.cs
    // or TestSimStartController.cs. Calls the real Secondary Boot mechanism
    // (SimBootStart -> SecondaryBootLauncher) only.
    // ============================================================================
    public sealed class SimStartController : MonoBehaviour
    {
        [Header("UI fields")]
        [SerializeField] Dropdown patternDropdown;
        [SerializeField] Dropdown pictureDropdown;
        [SerializeField] Toggle headlessToggle;
        [SerializeField] Slider speedSlider;
        [SerializeField] InputField seedField;
        [SerializeField] Text statusText;

        [Header("Level source")]
        [Tooltip("Whatever GameObject owns the active EditorSession, if launching straight from the currently-open floor plan.")]
        [SerializeField] EditorSession editorSession;

        [Header("Vacuum settings source")]
        [Tooltip("PENDING: no persistent 'current settings' instance was found anywhere in the project. Wire this to wherever that ends up living. Left unset, the robot gets zero drive speed and will silently not move -- not a crash, easy to mistake for a bug.")]
        [SerializeField] VacuumSettings vacuumSettingsOverride;

        public void OnRunButtonClicked()
        {
            if (editorSession == null || editorSession.Level == null)
            {
                SetStatus("No level to run -- draw or open one first.");
                return;
            }

            var settings = vacuumSettingsOverride ?? new VacuumSettings();

            var request = new SimRunRequest
            {
                level = editorSession.Level,
                vacuumSettings = settings,
                pattern = (MovementPattern)patternDropdown.value,
                picture = (PictureKind)pictureDropdown.value,
                seed = int.TryParse(seedField.text, out int parsedSeed) ? parsedSeed : Environment.TickCount,
                headless = headlessToggle.isOn,
                playbackSpeed = speedSlider.value,
            };

            SetStatus("Running...");
            SimBootStart.Run(request, ReportResult);
        }

        void ReportResult(RunResult result)
        {
            SetStatus(result.succeeded
                ? $"Done -- {result.coveragePercent:0.0}% coverage in {result.simulatedSeconds:0.0}s"
                : $"Failed -- {result.errorMessage}");
        }

        void SetStatus(string message)
        {
            if (statusText != null) statusText.text = message;
        }
    }
}
