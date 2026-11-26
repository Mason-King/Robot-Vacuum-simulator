using System;
using System.Collections.Generic;
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
        List<AlgorithmEntry> entries;
        readonly DropdownField algorithm;
        readonly RunSettings runSettings;
        readonly bool runActive;

        /// <param name="algorithmSwitched">Mid-run only: called with the old and new algorithm ids after the run's
        /// algorithm was switched (2.16), so the caller can log it.</param>
        /// <param name="reimport">When given, a "Reimport algorithms" button goes with the Algorithm list (beside it
        /// before a run, below it during one, where the menu is narrow). The caller rescans (and, mid-run, rebuilds the
        /// algorithm); the list is refreshed afterwards.</param>
        public RunSettingsPanel(RunSettings runSettings, SimulationSession session, Action<string> showWarning,
            Action settingsChanged = null, Action<string, string> algorithmSwitched = null, Action reimport = null)
        {
            if (runSettings == null) throw new ArgumentNullException(nameof(runSettings));
            if (showWarning == null) throw new ArgumentNullException(nameof(showWarning));
            AddToClassList("le-settings-panel");
            this.runSettings = runSettings;

            entries = AlgorithmCatalog.Entries.ToList();
            int algorithmIndex = entries.FindIndex(entry => entry.Id == runSettings.algorithmId);
            if (algorithmIndex < 0) algorithmIndex = 0;
            runSettings.algorithmId = entries[algorithmIndex].Id;

            runActive = session != null && session.Runner != null;
            algorithm = new DropdownField("Algorithm", entries.Select(entry => entry.Name).ToList(), algorithmIndex);
            // Mid-run (2.16) the robot switches to the new algorithm at once: it keeps its place and map, and the new
            // algorithm starts from Initialize. One that can't run here (SLAM) is refused and the choice put back.
            algorithm.RegisterValueChangedCallback(evt =>
            {
                int selectedIndex = algorithm.index;
                if (selectedIndex < 0 || selectedIndex >= entries.Count) return;
                string chosen = entries[selectedIndex].Id;

                if (runActive)
                {
                    string refusal = AlgorithmCatalog.Refusal(chosen, runSettings.slam);
                    if (!string.IsNullOrEmpty(refusal))
                    {
                        algorithm.SetValueWithoutNotify(evt.previousValue);
                        showWarning(refusal);
                        return;
                    }

                    var robot = session.Runner != null ? session.Runner.Robot : null;
                    string previous = robot != null ? robot.AlgorithmId : runSettings.algorithmId;
                    runSettings.algorithmId = chosen;
                    if (robot != null) robot.AlgorithmId = chosen;
                    if (previous != chosen)
                    {
                        session.RecordAlgorithmSwitch(previous, chosen); // result.json and summary.json keep the starting one
                        algorithmSwitched?.Invoke(previous, chosen);
                    }
                }
                else runSettings.algorithmId = chosen;

                settingsChanged?.Invoke();
            });

            if (reimport == null) Add(algorithm);
            else
            {
                var button = RobotVacuum.LevelEditor.Ui.Button(null, "Reimport algorithms", RobotVacuum.LevelEditor.IconKind.Undo, () =>
                {
                    reimport();
                    RefreshAlgorithms(showWarning, settingsChanged);
                }, "le-btn--ghost le-settings-panel__reimport");
                RobotVacuum.LevelEditor.Ui.Tooltip(button, runActive
                    ? "Reload the Algorithms folder now and rebuild this run's algorithm from it (its memory starts over; the robot keeps its place and map)."
                    : "Reload the Algorithms folder now, so new or changed algorithms show in this list.");

                if (runActive)
                {
                    Add(algorithm);
                    Add(button); // the in-run menu is narrow: below the list
                }
                else
                {
                    AddToClassList("le-settings-panel--reimport-beside"); // wider, to fit the button
                    var row = new VisualElement();
                    row.AddToClassList("le-settings-panel__algorithm-row");
                    algorithm.AddToClassList("le-settings-panel__algorithm");
                    row.Add(algorithm);
                    row.Add(button); // beside the list
                    Add(row);
                }
            }
            if (runActive)
            {
                var hint = new Label("Switching mid-run starts the new algorithm from where the robot is; it is logged.");
                hint.AddToClassList("le-empty-hint");
                hint.AddToClassList("le-settings-panel__hint");
                Add(hint);
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

        /// <summary>
        /// Reads the algorithm list again (after a rescan) and keeps the selection; if the selected one is gone, the
        /// first is selected and the user is told.
        /// </summary>
        void RefreshAlgorithms(Action<string> showWarning, Action settingsChanged)
        {
            entries = AlgorithmCatalog.Entries.ToList();
            if (entries.Count == 0) return;

            int index = entries.FindIndex(entry => entry.Id == runSettings.algorithmId);
            algorithm.choices = entries.Select(entry => entry.Name).ToList();
            if (index < 0 && runActive)
            {
                // Mid-run the robot keeps the algorithm it has; the caller holds the run if that no longer loads.
                algorithm.SetValueWithoutNotify(AlgorithmCatalog.NameOf(runSettings.algorithmId));
                return;
            }
            if (index < 0)
            {
                string gone = AlgorithmCatalog.NameOf(runSettings.algorithmId);
                index = 0;
                runSettings.algorithmId = entries[0].Id;
                algorithm.SetValueWithoutNotify(entries[0].Name);
                showWarning($"'{gone}' is no longer in the Algorithms folder (or no longer loads); {entries[0].Name} is selected instead.");
                settingsChanged?.Invoke();
            }
            else algorithm.SetValueWithoutNotify(entries[index].Name);
        }
    }
}
