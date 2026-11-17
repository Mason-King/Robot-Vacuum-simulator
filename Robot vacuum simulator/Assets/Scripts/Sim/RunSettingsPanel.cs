using System;
using System.Linq;
using UnityEngine.UIElements;
using RobotVacuum.Sim;
using RobotVacuum.Sim.Control;

namespace RobotVacuum.Sim.Control
{
    /// <summary>
    /// UI Toolkit controls for algorithm, display and SLAM settings. Display settings can be changed
    /// during a run; SLAM settings are enabled only while no runner is active.
    /// </summary>
    public sealed class RunSettingsPanel : VisualElement
    {
        public RunSettingsPanel(RunSettings runSettings, SimulationSession session, Action<string> showWarning,
            Action settingsChanged = null)
        {
            if (runSettings == null) throw new ArgumentNullException(nameof(runSettings));
            if (showWarning == null) throw new ArgumentNullException(nameof(showWarning));
            AddToClassList("le-settings-panel");

            var entries = AlgorithmCatalog.Entries.ToList();
            int algorithmIndex = entries.FindIndex(entry => entry.Id == runSettings.algorithmId);
            if (algorithmIndex < 0) algorithmIndex = 0;
            runSettings.algorithmId = entries[algorithmIndex].Id;

            bool runActive = session != null && session.Runner != null;
            var algorithm = new DropdownField("Algorithm", entries.Select(entry => entry.Name).ToList(), algorithmIndex);
            // The algorithm is fixed for the whole run; it can only be chosen before the run starts.
            algorithm.SetEnabled(!runActive);
            if (runActive) algorithm.AddToClassList("le-settings-panel__locked");
            algorithm.RegisterValueChangedCallback(evt =>
            {
                int selectedIndex = algorithm.index;
                if (selectedIndex >= 0 && selectedIndex < entries.Count)
                {
                    runSettings.algorithmId = entries[selectedIndex].Id;
                    var pattern = AlgorithmCatalog.PatternOf(runSettings.algorithmId);
                    if (pattern.HasValue) runSettings.pattern = pattern.Value;
                    settingsChanged?.Invoke();
                }
            });
            Add(algorithm);
            if (runActive)
            {
                var lockedHint = new Label("Set before the run starts. It can't change mid-run.");
                lockedHint.AddToClassList("le-empty-hint");
                lockedHint.AddToClassList("le-settings-panel__hint");
                Add(lockedHint);
            }

            var heatmapModes = Enum.GetValues(typeof(HeatmapMode)).Cast<HeatmapMode>().ToList();
            int heatmapIndex = heatmapModes.IndexOf(runSettings.heatmapMode);
            if (heatmapIndex < 0) heatmapIndex = 0;

            var heatmap = new DropdownField("Heatmap mode",
                heatmapModes.Select(HeatmapDefaults.DisplayName).ToList(), heatmapIndex);
            heatmap.RegisterValueChangedCallback(evt =>
            {
                int selectedIndex = heatmap.index;
                if (selectedIndex < 0 || selectedIndex >= heatmapModes.Count) return;

                runSettings.heatmapMode = heatmapModes[selectedIndex];
                var runner = session != null ? session.Runner : null;
                if (runner != null && runner.Heatmap != null)
                    runner.Heatmap.Mode = runSettings.heatmapMode;
                settingsChanged?.Invoke();
            });
            Add(heatmap);

            var beliefOverlay = new Toggle("Show belief map")
            {
                value = runSettings.showBeliefOverlay,
            };
            beliefOverlay.RegisterValueChangedCallback(evt =>
            {
                runSettings.showBeliefOverlay = evt.newValue;
                var runner = session != null ? session.Runner : null;
                if (runner != null && runner.BeliefOverlay != null)
                    runner.BeliefOverlay.Show = runSettings.showBeliefOverlay;
                settingsChanged?.Invoke();
            });
            Add(beliefOverlay);

            bool setupSettingsEnabled = session == null || session.Runner == null;
            var slam = new Toggle("SLAM")
            {
                value = runSettings.slam,
            };
            slam.SetEnabled(setupSettingsEnabled);
            slam.RegisterValueChangedCallback(evt =>
            {
                string refusal = AlgorithmCatalog.Refusal(runSettings.algorithmId, evt.newValue);
                if (!string.IsNullOrEmpty(refusal))
                {
                    slam.SetValueWithoutNotify(runSettings.slam);
                    showWarning(refusal);
                    return;
                }

                runSettings.slam = evt.newValue;
                settingsChanged?.Invoke();
            });
            Add(slam);

            var advanced = new Foldout
            {
                text = "Advanced",
                value = false,
            };

            var loopClosure = new Toggle("SLAM loop closure")
            {
                value = runSettings.slamLoopClosure,
            };
            loopClosure.SetEnabled(setupSettingsEnabled);
            loopClosure.RegisterValueChangedCallback(evt =>
            {
                runSettings.slamLoopClosure = evt.newValue;
                settingsChanged?.Invoke();
            });
            advanced.Add(loopClosure);

            var qualities = Enum.GetValues(typeof(PoseCorrectionQuality)).Cast<PoseCorrectionQuality>().ToList();
            int qualityIndex = qualities.IndexOf(runSettings.slamCorrectionQuality);
            if (qualityIndex < 0) qualityIndex = 0;

            var correctionQuality = new DropdownField("SLAM correction quality",
                qualities.Select(quality => quality.ToString()).ToList(), qualityIndex);
            correctionQuality.SetEnabled(setupSettingsEnabled);
            correctionQuality.RegisterValueChangedCallback(evt =>
            {
                int selectedIndex = correctionQuality.index;
                if (selectedIndex >= 0 && selectedIndex < qualities.Count)
                {
                    runSettings.slamCorrectionQuality = qualities[selectedIndex];
                    settingsChanged?.Invoke();
                }
            });
            advanced.Add(correctionQuality);
            Add(advanced);
        }
    }
}
