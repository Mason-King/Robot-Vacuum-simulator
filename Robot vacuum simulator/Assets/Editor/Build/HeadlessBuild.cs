using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace RobotVacuum.EditorTools
{
    /// <summary>
    /// Builds the headless cloud node (2.17, 3.10, 5.5): the same project and scenes as the Windows client, for
    /// Linux x86-64, as a Dedicated Server (no renderer, always batchmode). The Docker image in cloud/docker
    /// packages this build.
    ///
    /// Menu: Robot Vacuum > Build > Linux Headless Server.
    /// CI:   Unity -batchmode -quit -projectPath "Robot vacuum simulator"
    ///             -executeMethod RobotVacuum.EditorTools.HeadlessBuild.BuildLinuxServer
    ///             [-customBuildPath Builds/LinuxServer/RobotVacuumServer.x86_64]
    /// Set HEADLESS_SUBTARGET=Player to make a normal Linux player instead, if the Unity install (or CI
    /// image) lacks the Linux Dedicated Server module. It runs headless the same way with -batchmode -nographics.
    /// </summary>
    public static class HeadlessBuild
    {
        public const string DefaultPath = "Builds/LinuxServer/RobotVacuumServer.x86_64";

        [MenuItem("Robot Vacuum/Build/Linux Headless Server")]
        static void BuildFromMenu()
        {
            var report = Build(DefaultPath);
            if (report.summary.result == BuildResult.Succeeded)
                EditorUtility.RevealInFinder(Path.GetFullPath(DefaultPath));
        }

        /// <summary>Entry point for -executeMethod. Exits the editor with 0 on success, 1 on failure.</summary>
        public static void BuildLinuxServer()
        {
            string path = ArgValue("-customBuildPath") ?? Environment.GetEnvironmentVariable("HEADLESS_BUILD_PATH") ?? DefaultPath;
            // The container entrypoint looks for *.x86_64, so make sure the executable has that name.
            if (!path.EndsWith(".x86_64", StringComparison.OrdinalIgnoreCase)) path += ".x86_64";
            // GameCI passes a path relative to the repository root; the editor resolves relative paths against the project.
            string workspace = Environment.GetEnvironmentVariable("GITHUB_WORKSPACE");
            if (!Path.IsPathRooted(path) && !string.IsNullOrEmpty(workspace)) path = Path.Combine(workspace, path);
            var report = Build(path);
            if (Application.isBatchMode) EditorApplication.Exit(report.summary.result == BuildResult.Succeeded ? 0 : 1);
        }

        static BuildReport Build(string path)
        {
            var scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray();
            bool server = !string.Equals(Environment.GetEnvironmentVariable("HEADLESS_SUBTARGET"), "Player", StringComparison.OrdinalIgnoreCase);

            var options = new BuildPlayerOptions
            {
                scenes = scenes,
                locationPathName = path,
                target = BuildTarget.StandaloneLinux64,
                subtarget = (int)(server ? StandaloneBuildSubtarget.Server : StandaloneBuildSubtarget.Player),
                options = BuildOptions.None,
            };

            Debug.Log($"[HeadlessBuild] Building Linux {(server ? "Dedicated Server" : "player")} to {path} with {scenes.Length} scene(s).");
            var report = BuildPipeline.BuildPlayer(options);
            Debug.Log($"[HeadlessBuild] {report.summary.result}: {report.summary.totalErrors} error(s), {report.summary.totalSize / (1024 * 1024)} MB.");
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
