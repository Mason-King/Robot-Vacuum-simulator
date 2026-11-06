using System.IO;
using RobotVacuum.Sim.Control;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace RobotVacuum.EditorTools
{
    /// <summary>
    /// The Algorithms folder, editor side.
    ///   * Tools → Algorithms → Rescan: scan the folder again and log the load report (refused during a run).
    ///   * Tools → Algorithms → Open Folder: show the folder in Explorer (created if missing).
    ///   * After every player build, the project's Algorithms folder is copied next to the built .exe, so the
    ///     algorithms that ship with the simulator are in every install.
    /// </summary>
    public sealed class AlgorithmsFolderEditor : IPostprocessBuildWithReport
    {
        public int callbackOrder => 0;

        [MenuItem("Tools/Algorithms/Rescan")]
        static void Rescan()
        {
            var report = AlgorithmCatalog.Rescan();
            if (report.refused || report.FailedUnits > 0) Debug.LogWarning(report.Text());
            else Debug.Log(report.Text());
        }

        [MenuItem("Tools/Algorithms/Open Folder")]
        static void OpenFolder()
        {
            string folder = AlgorithmLoader.DefaultFolder;
            Directory.CreateDirectory(folder);
            EditorUtility.RevealInFinder(folder);
        }

        public void OnPostprocessBuild(BuildReport report)
        {
            string source = AlgorithmLoader.DefaultFolder; // <project>/Algorithms
            if (!Directory.Exists(source))
            {
                Debug.LogWarning($"No Algorithms folder at {source}; the build has only the built-in algorithms.");
                return;
            }

            // outputPath is the .exe on Windows (a folder on some platforms).
            string output = report.summary.outputPath;
            string buildFolder = Directory.Exists(output) ? output : Path.GetDirectoryName(output);
            string target = Path.Combine(buildFolder ?? ".", AlgorithmLoader.FolderName);

            if (Directory.Exists(target)) Directory.Delete(target, true); // a clean copy: no stale algorithms from an old build
            Copy(source, target);
            Debug.Log($"Copied the Algorithms folder to {target}.");
        }

        static void Copy(string from, string to)
        {
            Directory.CreateDirectory(to);
            foreach (string file in Directory.GetFiles(from))
                File.Copy(file, Path.Combine(to, Path.GetFileName(file)), true);
            foreach (string directory in Directory.GetDirectories(from))
                Copy(directory, Path.Combine(to, Path.GetFileName(directory)));
        }
    }
}
