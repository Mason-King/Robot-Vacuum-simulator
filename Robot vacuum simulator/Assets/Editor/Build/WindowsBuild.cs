using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace RobotVacuum.EditorTools
{
    /// <summary>
    /// Builds the Windows client (1.1.1): the GUI, which is also the network worker (-worker) and the headless job runner
    /// (-configPath), into Builds/Windows. The Settings page and network batches started from the editor run this
    /// build, because a worker or headless shard needs a built copy of the simulator.
    ///
    /// Always a release build (never Development): a Development build would also start the Unity Pipeline package's
    /// local HTTP command server, which must not ship (4.8). Mono scripting backend, as the runtime plugin compiler needs.
    ///
    /// Menu: Robot Vacuum > Build > Windows.
    /// CI:   Unity -batchmode -quit -projectPath "Robot vacuum simulator" -executeMethod RobotVacuum.EditorTools.WindowsBuild.BuildWindows
    ///             [-customBuildPath Builds/Windows/RobotVacuum.exe]
    /// </summary>
    public static class WindowsBuild
    {
        public const string DefaultPath = "Builds/Windows/RobotVacuum.exe";

        [MenuItem("Robot Vacuum/Build/Windows")]
        static void BuildFromMenu()
        {
            var report = Build(DefaultPath);
            if (report.summary.result == BuildResult.Succeeded)
                EditorUtility.RevealInFinder(Path.GetFullPath(DefaultPath));
            else
                EditorUtility.DisplayDialog("Windows build failed", $"{report.summary.totalErrors} error(s). See the Console for details.", "OK");
        }

        /// <summary>Entry point for -executeMethod. Exits the editor with 0 on success, 1 on failure.</summary>
        public static void BuildWindows()
        {
            string path = ArgValue("-customBuildPath") ?? DefaultPath;
            if (!path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) path += ".exe";
            var report = Build(path);
            if (Application.isBatchMode) EditorApplication.Exit(report.summary.result == BuildResult.Succeeded ? 0 : 1);
        }

        static BuildReport Build(string path)
        {
            WindowDefaults.Apply(); // opens in a resizable window
            var scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray();
            var options = new BuildPlayerOptions
            {
                scenes = scenes,
                locationPathName = path,
                target = BuildTarget.StandaloneWindows64,
                subtarget = (int)StandaloneBuildSubtarget.Player,
                options = BuildOptions.None, // release, not Development (see above)
            };

            Debug.Log($"[WindowsBuild] Building the Windows client to {path} with {scenes.Length} scene(s), version {PlayerSettings.bundleVersion}.");
            var report = BuildPipeline.BuildPlayer(options);
            Debug.Log($"[WindowsBuild] {report.summary.result}: {report.summary.totalErrors} error(s), {report.summary.totalSize / (1024 * 1024)} MB, {report.summary.totalTime.TotalSeconds:0} s.");
            return report;
        }

        static string ArgValue(string flag)
        {
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
            return null;
        }
    }
}
