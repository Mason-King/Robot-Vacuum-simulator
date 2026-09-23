using System;
using System.IO;
using UnityEngine;

namespace RobotVacuum.Sim
{
    // The file that crosses the process boundary going OUT. Numeric results only --
    // the coverage heatmap is a separate sibling .png (see heatmapImagePath), since
    // CoverageImage.Create needs a live ExternalModelGrid object, which only exists
    // inside the Secondary Boot process while it's still running.
    [Serializable]
    public class RunResult
    {
        public bool succeeded;
        public string errorMessage; // populated only when succeeded is false

        public string levelName;
        public int seed;
        public MovementPattern pattern;
        public PictureKind picture;

        public float coveragePercent;
        public float blockedAreaSquareMeters;
        public float simulatedSeconds;
        public float realSeconds;

        // Null/empty when this run had no heatmap to produce (e.g. it failed
        // before completing), or the coverage image genuinely wasn't built.
        public string heatmapImagePath;

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
