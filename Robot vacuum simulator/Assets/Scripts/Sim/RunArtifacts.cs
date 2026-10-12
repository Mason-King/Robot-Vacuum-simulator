using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace RobotVacuum.Sim
{
    /// <summary>
    /// Everything a finished run leaves on disk, and how to read it back. One run = one folder under
    /// <c>Application.persistentDataPath/Runs/&lt;id&gt;/</c> holding:
    /// <list type="bullet">
    /// <item><c>external_model.json</c>: the saved External Model (<see cref="ExternalModelData"/>). The analysis screen draws from this.</item>
    /// <item>Three PNGs, one per <see cref="HeatmapMode"/>: export-only, for analysis outside the program.</item>
    /// <item><c>result.json</c>: the numbers (<see cref="RunResult"/>). Its existence is what marks a run as finished.</item>
    /// <item><c>config.json</c>: the run's inputs (only for runs started through Secondary Boot).</item>
    /// </list>
    /// The real Secondary Boot and the in-process TestSimStart both write through <see cref="Write"/>, so a
    /// test run looks exactly like a real one to anything reading the folder.
    /// </summary>
    public static class RunArtifacts
    {
        public const string ModelFileName = "external_model.json";
        public const string ResultFileName = "result.json";
        public const string ConfigFileName = "config.json";

        /// <summary>The folder every run folder lives in.</summary>
        public static string RunsRoot => Path.Combine(Application.persistentDataPath, "Runs");

        /// <summary>Makes a fresh, empty folder for a new run and returns its path.</summary>
        public static string NewRunFolder()
        {
            string folder = Path.Combine(RunsRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            return folder;
        }

        /// <summary>The PNG for one mode inside a run folder.</summary>
        public static string ImagePath(string runFolder, HeatmapMode mode)
        {
            switch (mode)
            {
                case HeatmapMode.FloorCoverage: return Path.Combine(runFolder, "heatmap_floor_coverage.png");
                case HeatmapMode.FloorOverCoverage: return Path.Combine(runFolder, "heatmap_floor_overcoverage.png");
                default: return Path.Combine(runFolder, "heatmap_floor_cleanliness.png");
            }
        }

        /// <summary>
        /// Saves the model and the three PNGs into <paramref name="runFolder"/> and records their paths, the
        /// target and the coverage figures on <paramref name="result"/>. Does NOT write result.json itself:
        /// the caller does that last, because the launcher treats its existence as "run finished".
        /// </summary>
        public static void Write(ExternalModelData data, string runFolder, RunResult result)
        {
            if (data == null || !data.IsValid || result == null) return;

            Directory.CreateDirectory(runFolder);

            // Compact JSON on purpose: pretty-printing puts every array element on its own line.
            string modelPath = Path.Combine(runFolder, ModelFileName);
            File.WriteAllText(modelPath, JsonUtility.ToJson(data, prettyPrint: false));
            result.externalModelPath = modelPath;

            result.targetMaxClean = data.targetMaxClean;
            result.coveragePercent = data.CoveragePercent();
            result.fullyCleanedPercent = data.FullyCleanedPercent();

            result.cleanlinessImagePath = WriteImage(data, runFolder, HeatmapMode.FloorCleanliness);
            result.coverageImagePath = WriteImage(data, runFolder, HeatmapMode.FloorCoverage);
            result.overCoverageImagePath = WriteImage(data, runFolder, HeatmapMode.FloorOverCoverage);

            // The older single-image field keeps pointing at the default view.
            result.heatmapImagePath = result.cleanlinessImagePath;
        }

        static string WriteImage(ExternalModelData data, string runFolder, HeatmapMode mode)
        {
            var texture = CoverageImage.Create(data, mode);
            if (texture == null) return null;

            string path = ImagePath(runFolder, mode);
            try
            {
                File.WriteAllBytes(path, texture.EncodeToPNG());
            }
            finally
            {
                if (Application.isPlaying) UnityEngine.Object.Destroy(texture);
                else UnityEngine.Object.DestroyImmediate(texture);
            }
            return path;
        }

        /// <summary>Reads a saved model back. Null if the file is missing or unreadable.</summary>
        public static ExternalModelData LoadModel(string modelPath)
        {
            try
            {
                if (!File.Exists(modelPath)) return null;
                var data = JsonUtility.FromJson<ExternalModelData>(File.ReadAllText(modelPath));
                return data != null && data.IsValid ? data : null;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"RunArtifacts: could not read the saved model at {modelPath}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// The analysis screen's load-and-draw call: reads the run's saved model and draws it in
        /// <paramref name="mode"/>. The caller owns the returned texture (destroy it when done). Null if the
        /// run has no readable model.
        /// </summary>
        public static Texture2D RenderFromFolder(string runFolder, HeatmapMode mode) =>
            CoverageImage.Create(LoadModel(Path.Combine(runFolder, ModelFileName)), mode);

        /// <summary>Folders of finished runs (those holding a result.json), newest first.</summary>
        public static List<string> FinishedRunFolders()
        {
            var folders = new List<string>();
            if (!Directory.Exists(RunsRoot)) return folders;

            foreach (string folder in Directory.GetDirectories(RunsRoot))
                if (File.Exists(Path.Combine(folder, ResultFileName))) folders.Add(folder);

            folders.Sort((a, b) =>
                File.GetLastWriteTimeUtc(Path.Combine(b, ResultFileName)).CompareTo(File.GetLastWriteTimeUtc(Path.Combine(a, ResultFileName))));
            return folders;
        }
    }
}
