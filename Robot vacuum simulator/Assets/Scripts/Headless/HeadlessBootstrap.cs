using System;
using System.Globalization;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace RobotVacuum.Headless
{
    /// <summary>
    /// The headless entry hook (SDD 2.1, 2.5; requirement 2.17). Launched normally, the executable boots the
    /// GUI and this does nothing. Launched with <c>-configPath &lt;job.json&gt;</c> it switches the process into a
    /// headless job: the GUI scene is switched off before any of its Start methods run, a
    /// <see cref="HeadlessJobRunner"/> runs the job's simulations, writes the results, and the process exits.
    ///
    /// Command line:
    /// <code>
    ///   RobotVacuum -batchmode -nographics -configPath job.json [-outputPath dir]
    ///               [-shardIndex i] [-shardCount n] [-validateOnly] [-noResume] [-logFile -]
    /// </code>
    /// -shardIndex falls back to AWS_BATCH_JOB_ARRAY_INDEX, -shardCount to VACUUM_SHARD_COUNT and then the job
    /// file's execution.shardCount. A Linux Dedicated Server build is always batchmode with no graphics device,
    /// so it qualifies without the two flags.
    ///
    /// Exit codes: 0 every run succeeded, 10 the job finished but some runs failed (see results.json),
    /// 2 the job file or arguments were rejected, 1 the job crashed.
    /// </summary>
    public static class HeadlessBootstrap
    {
        public const int ExitOk = 0;
        public const int ExitCrashed = 1;
        public const int ExitInvalid = 2;
        public const int ExitSomeRunsFailed = 10;

        /// <summary>True when this process was launched as a headless job. GUI code can check it to stay out of the way.</summary>
        public static bool IsHeadlessJob { get; private set; }

        public static HeadlessArgs Args { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Boot()
        {
            var args = HeadlessArgs.Parse(Environment.GetCommandLineArgs());
            if (string.IsNullOrEmpty(args.configPath)) return; // a normal GUI launch

            IsHeadlessJob = true;
            Args = args;

            // Keep the log readable in CloudWatch: stack traces only for real problems.
            Application.SetStackTraceLogType(LogType.Log, StackTraceLogType.None);
            Application.SetStackTraceLogType(LogType.Warning, StackTraceLogType.None);

            SceneManager.sceneLoaded += OnFirstSceneLoaded;
        }

        // Runs after the boot scene's Awake/OnEnable but before any Start, which is where the GUI builds itself.
        static void OnFirstSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            SceneManager.sceneLoaded -= OnFirstSceneLoaded;
            foreach (var root in scene.GetRootGameObjects()) root.SetActive(false);

            if (!IsHeadlessProcess())
            {
                // SDD 2.1: refuse -configPath without -batchmode -nographics, so a job can never quietly run
                // with rendering on and be mistaken for a headless result.
                Debug.LogError("[headless] -configPath was given without -batchmode -nographics. Refusing to run the job. " +
                               "Launch as: <exe> -batchmode -nographics -configPath <job.json>");
                Application.Quit(ExitInvalid);
                return;
            }

            var host = new GameObject("Headless Job");
            UnityEngine.Object.DontDestroyOnLoad(host);
            host.AddComponent<HeadlessJobRunner>().Begin(Args);
        }

        static bool IsHeadlessProcess()
        {
            if (!Application.isBatchMode) return false;
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) return true;
            foreach (string arg in Environment.GetCommandLineArgs())
                if (string.Equals(arg, "-nographics", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // Statics survive entering Play Mode when domain reload is off.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            SceneManager.sceneLoaded -= OnFirstSceneLoaded;
            IsHeadlessJob = false;
            Args = null;
        }
    }

    /// <summary>The headless job's command-line arguments.</summary>
    public sealed class HeadlessArgs
    {
        public string configPath;
        public string outputPath;
        public int shardIndex = -1;
        public int shardCount;
        public bool validateOnly;
        public bool resume = true;

        public static HeadlessArgs Parse(string[] argv)
        {
            var args = new HeadlessArgs();
            for (int i = 0; i < argv.Length; i++)
            {
                string flag = argv[i];
                string value = i + 1 < argv.Length ? argv[i + 1] : null;

                if (Is(flag, "-configPath") && value != null) { args.configPath = value; i++; }
                else if (Is(flag, "-outputPath") && value != null) { args.outputPath = value; i++; }
                else if (Is(flag, "-shardIndex") && TryInt(value, out int index)) { args.shardIndex = index; i++; }
                else if (Is(flag, "-shardCount") && TryInt(value, out int count)) { args.shardCount = count; i++; }
                else if (Is(flag, "-validateOnly")) args.validateOnly = true;
                else if (Is(flag, "-noResume")) args.resume = false;
            }

            if (args.shardIndex < 0 && TryInt(Environment.GetEnvironmentVariable("AWS_BATCH_JOB_ARRAY_INDEX"), out int envIndex))
                args.shardIndex = envIndex;
            if (args.shardCount <= 0 && TryInt(Environment.GetEnvironmentVariable("VACUUM_SHARD_COUNT"), out int envCount))
                args.shardCount = envCount;

            return args;
        }

        static bool Is(string arg, string flag) => string.Equals(arg, flag, StringComparison.OrdinalIgnoreCase);

        static bool TryInt(string text, out int value) =>
            int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }
}
