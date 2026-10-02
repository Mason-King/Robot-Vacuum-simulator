using System;
using RobotVacuum.Level;
using RobotVacuum.LevelEditor;
using UnityEngine;
using UnityEngine.UI;

namespace RobotVacuum.Sim
{
    // Test-side counterpart to SimRunRequest -- deliberately a SEPARATE type
    // (not shared with the production pair), so this whole file plus
    // TestSimStart.cs can be deleted together with zero risk of breaking
    // SimStartController.cs/SimBootStart.cs, which never reference it.
    public struct TestSimRunRequest
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
    // TestSimStartController.cs
    //
    // ***** TESTING ONLY -- DELETE THIS FILE (and TestSimStart.cs) BEFORE  *****
    // ***** FINAL SUBMISSION. NEITHER FILE IS REFERENCED BY ANY PRODUCTION *****
    // ***** SCRIPT -- deleting this pair breaks nothing else in the project.*****
    //
    // A separate GameObject/button from the real one -- attach this to a
    // throwaway test button, never to the real Run button (that one gets
    // SimStartController instead). Runs the sim in THIS process, no build
    // required, so the team isn't rebuilding for every small change. Does
    // NOT exercise the real Secondary Boot mechanism (no config file, no
    // Process.Start, no result file) -- it proves the SIMULATION works, not
    // that the BOOT mechanism works.
    // ============================================================================
    public sealed class TestSimStartController : MonoBehaviour
    {
        [Header("UI fields")]
        [SerializeField] Dropdown patternDropdown;
        [SerializeField] Dropdown pictureDropdown;
        [SerializeField] Toggle headlessToggle;
        [SerializeField] Slider speedSlider;
        [SerializeField] InputField seedField;
        [SerializeField] Text statusText;

        [Header("Level source")]
        [SerializeField] EditorSession editorSession;

        [Header("Vacuum settings source")]
        [SerializeField] VacuumSettings vacuumSettingsOverride;

        public void OnRunButtonClicked()
        {
            if (editorSession == null || editorSession.Level == null)
            {
                SetStatus("No level to run -- draw or open one first.");
                return;
            }

            var settings = vacuumSettingsOverride ?? new VacuumSettings();

            var request = new TestSimRunRequest
            {
                level = editorSession.Level,
                vacuumSettings = settings,
                pattern = (MovementPattern)patternDropdown.value,
                picture = (PictureKind)pictureDropdown.value,
                seed = int.TryParse(seedField.text, out int parsedSeed) ? parsedSeed : Environment.TickCount,
                headless = headlessToggle.isOn,
                playbackSpeed = speedSlider.value,
            };

            SetStatus("Running (test mode)...");
            TestSimStart.Run(request, ReportResult);
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
