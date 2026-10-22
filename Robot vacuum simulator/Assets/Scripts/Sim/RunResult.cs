using System;
using System.IO;
using UnityEngine;

namespace RobotVacuum.Sim
{
    // The file that crosses the process boundary going OUT. Numbers and the paths
    // of the other files the run saved next to it (the External Model state and
    // the three heatmap PNGs -- see RunArtifacts). The images are drawn inside the
    // Secondary Boot process, but the saved model is plain data, so anything can
    // redraw them later from external_model.json.
    [Serializable]
    public class RunResult
    {
        public bool succeeded;
        public string errorMessage; // populated only when succeeded is false

        public string levelName;
        public int seed;
        public MovementPattern pattern;

        // Graded average against the target (see ExternalModelData.CoveragePercent).
        public float coveragePercent;

        // Share of cleanable cells that reached the target.
        public float fullyCleanedPercent;

        // target_max_clean this run was measured against.
        public float targetMaxClean;
        public float blockedAreaSquareMeters;
        public float simulatedSeconds;
        public float realSeconds;

        // Null/empty when a file wasn't produced (e.g. the run failed before
        // completing). heatmapImagePath is the older single-image field and
        // points at the Floor Cleanliness PNG.
        public string heatmapImagePath;
        public string externalModelPath;
        public string cleanlinessImagePath;
        public string coverageImagePath;
        public string overCoverageImagePath;

        public static RunResult Failure(string message) =>
            new RunResult { succeeded = false, errorMessage = message };

        public void WriteTo(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, JsonUtility.ToJson(this, prettyPrint: true));
        }

        public static RunResult ReadFrom(string path) =>
            JsonUtility.FromJson<RunResult>(File.ReadAllText(path));
    }
}
