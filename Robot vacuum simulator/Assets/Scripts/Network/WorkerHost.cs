using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using RobotVacuum.Headless;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace RobotVacuum.Network
{
    /// <summary>worker-status.json: what the worker is doing, for the Network Settings page of the same computer.</summary>
    [Serializable]
    public sealed class WorkerStatusFile
    {
        public string state;            // "starting", "running", "error", "stopped"
        public string error;
        public int pid;
        public int port;
        public string workerName;
        public List<string> addresses = new List<string>();
        public string pairingCode;      // shown only on this computer, and only while pairing is open
        public string pairingExpiresUtc;
        public List<PairedClient> clients = new List<PairedClient>();
        public int maxProcesses;
        public int runningProcesses;
        public int queuedShards;
        public List<WorkerJobLine> jobs = new List<WorkerJobLine>();
        public string secretStore;
        public string updatedUtc;
    }

    [Serializable]
    public sealed class WorkerJobLine
    {
        public string client;
        public string campaign;
        public string state;
        public int completedRuns;
        public int totalRuns;
    }

    /// <summary>worker-control.json: a command from the Network Settings page to the worker on the same computer.</summary>
    [Serializable]
    public sealed class WorkerControlFile
    {
        public int sequence;
        public string command;          // "open-pairing", "close-pairing", "unpair", "stop"
        public string clientId;
        public int minutes;
    }

    /// <summary>The worker's files, in this simulator's data folder on this computer.</summary>
    public static class WorkerFiles
    {
        public static string DataFolder => Path.Combine(Application.persistentDataPath, "Worker");
        public static string StatusPath => Path.Combine(DataFolder, "worker-status.json");
        public static string ControlPath => Path.Combine(DataFolder, "worker-control.json");
        public static string LogPath => Path.Combine(DataFolder, "worker.log");

        public static T Read<T>(string path) where T : class
        {
            try
            {
                if (!File.Exists(path)) return null;
                using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(file))
                    return JsonUtility.FromJson<T>(reader.ReadToEnd());
            }
            catch (Exception)
            {
                return null;
            }
        }

        public static void Write(string path, object value)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
            string temp = path + "." + Process.GetCurrentProcess().Id + ".tmp";
            File.WriteAllText(temp, JsonUtility.ToJson(value, true));
            if (File.Exists(path)) File.Delete(path);
            File.Move(temp, path);
        }
    }

    /// <summary>
    /// Worker mode (requirement 3.9): <c>RobotVacuum -batchmode -nographics -worker [-workerPort 47815]
    /// [-workerProcesses N] [-workerPairMinutes 10]</c>. The process shows no window; it listens for paired PCs, runs
    /// their jobs as headless child processes, and keeps running until it is stopped (Ctrl+C, closing its window,
    /// or Stop on the Network Settings page). Started from Network Settings, it also stops when that simulator closes.
    /// Everything it does is logged to Worker/worker.log (the dedicated network log, SDD 8; 3.12).
    /// </summary>
    public sealed class WorkerHost : MonoBehaviour
    {
        const int DefaultPairingMinutes = WorkerProtocol.PairingMinutes;

        readonly object logGate = new object();
        string logPath; // cached: Application.persistentDataPath may only be read on the main thread, and the log is written from network threads
        WorkerServer server;
        WorkerJobs jobs;
        string secretStore;
        int parentPid;
        int lastControl;
        float nextTick;
        bool stopped;

        public void Begin(HeadlessArgs args)
        {
            Application.runInBackground = true;
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = 10; // it only waits and checks files; no need to spin

            Directory.CreateDirectory(WorkerFiles.DataFolder);
            logPath = WorkerFiles.LogPath;
            parentPid = args.workerParentPid;
            lastControl = WorkerFiles.Read<WorkerControlFile>(WorkerFiles.ControlPath)?.sequence ?? 0; // ignore old commands
            WriteStatus("starting");

            string executable = SimulatorExecutable.Find(out string problem);
            if (executable == null)
            {
                Fail(problem);
                return;
            }

            int processes = args.workerProcesses > 0 ? args.workerProcesses : Math.Max(1, SystemInfo.processorCount - 1);
            int port = args.workerPort > 0 ? args.workerPort : WorkerProtocol.DefaultPort;
            var secrets = SecretStore.ForThisComputer(Path.Combine(WorkerFiles.DataFolder, "secrets"));
            secretStore = secrets.Describe();
            try
            {
                jobs = new WorkerJobs(Path.Combine(WorkerFiles.DataFolder, "clients"), new ProcessShardLauncher(executable), processes, Log);
                server = new WorkerServer(new WorkerServerOptions
                {
                    port = port,
                    dataFolder = WorkerFiles.DataFolder,
                    workerName = WorkerProtocol.CleanName(Environment.MachineName, "Worker"),
                    appVersion = Application.version,
                    buildId = Application.buildGUID,
                    platform = Application.platform.ToString(),
                    jobSchemaVersion = HeadlessJob.CurrentSchemaVersion,
                    secrets = secrets,
                    jobs = jobs,
                    log = Log,
                });
                server.Start();
            }
            catch (SocketException exception)
            {
                Fail($"Port {port} can't be used ({exception.Message}). Another worker may already be running on this computer, or another program uses the port; choose another port.");
                return;
            }
            catch (Exception exception)
            {
                Fail($"The worker could not start: {exception.Message}");
                return;
            }

            int pairMinutes = args.workerPairMinutes >= 0 ? args.workerPairMinutes : (parentPid > 0 ? 0 : DefaultPairingMinutes);
            if (pairMinutes > 0)
            {
                string code = server.OpenPairing(TimeSpan.FromMinutes(pairMinutes));
                Log($"Pairing code: {SecureChannel.FormatPairingCode(code)}. On the researcher's PC: Network Settings > Network workers > Add worker, within {pairMinutes} minutes.");
            }

            var addresses = WorkerServer.LocalAddresses();
            Log($"Worker ready: {Environment.MachineName}, port {server.Port}, up to {processes} simulation(s) at a time, keys in {secretStore}. " +
                $"Address to enter on the researcher's PC: {(addresses.Count > 0 ? string.Join(" or ", addresses.Select(a => a + ":" + server.Port)) : "(no network connection found)")}.");
            WriteStatus("running");
        }

        void Update()
        {
            if (stopped || server == null || Time.unscaledTime < nextTick) return;
            nextTick = Time.unscaledTime + 1f;
            HandleControl();
            if (stopped) return;
            WriteStatus("running");
            CheckParent();
        }

        void HandleControl()
        {
            var control = WorkerFiles.Read<WorkerControlFile>(WorkerFiles.ControlPath);
            if (control == null || control.sequence <= lastControl) return;
            lastControl = control.sequence;
            switch (control.command)
            {
                case "open-pairing":
                    server.OpenPairing(TimeSpan.FromMinutes(control.minutes > 0 ? control.minutes : DefaultPairingMinutes));
                    break;
                case "close-pairing":
                    server.ClosePairing();
                    Log("Pairing closed.");
                    break;
                case "unpair":
                    if (!string.IsNullOrEmpty(control.clientId)) server.Unpair(control.clientId);
                    break;
                case "stop":
                    Stop("Stopped from Network Settings.");
                    break;
            }
        }

        void CheckParent()
        {
            if (parentPid <= 0) return;
            bool gone;
            try { gone = Process.GetProcessById(parentPid).HasExited; }
            catch (Exception) { gone = true; }
            if (gone) Stop("The simulator that started this worker has closed, so the worker stops too.");
        }

        void Stop(string reason)
        {
            Log(reason);
            Shutdown();
            WriteStatus("stopped");
            Application.Quit(HeadlessBootstrap.ExitOk);
        }

        void Fail(string message)
        {
            Log(message);
            Debug.LogError("[worker] " + message);
            Shutdown();
            WriteStatus("error", message);
            Application.Quit(HeadlessBootstrap.ExitInvalid);
        }

        void OnApplicationQuit() => Shutdown();

        void OnDestroy() => Shutdown();

        void Shutdown()
        {
            if (stopped) return;
            stopped = true;
            server?.Dispose();
            jobs?.Dispose(); // stops this worker's simulations; unfinished shards resume when it starts again
            server = null;
            jobs = null;
        }

        void WriteStatus(string state, string error = null)
        {
            try
            {
                var status = new WorkerStatusFile
                {
                    state = state,
                    error = error,
                    pid = Process.GetCurrentProcess().Id,
                    port = server?.Port ?? 0,
                    workerName = Environment.MachineName,
                    addresses = WorkerServer.LocalAddresses(),
                    secretStore = secretStore,
                    updatedUtc = DateTime.UtcNow.ToString("o"),
                };
                if (server != null && !stopped)
                {
                    if (server.TryGetPairing(out string code, out DateTime until))
                    {
                        status.pairingCode = SecureChannel.FormatPairingCode(code);
                        status.pairingExpiresUtc = until.ToString("o");
                    }
                    status.clients = server.PairedClients();
                    var names = status.clients.ToDictionary(c => c.clientId, c => c.name);
                    var (running, queued) = jobs.Load();
                    status.maxProcesses = jobs.MaxProcesses;
                    status.runningProcesses = running;
                    status.queuedShards = queued;
                    foreach (var line in jobs.Summary())
                        status.jobs.Add(new WorkerJobLine
                        {
                            client = names.TryGetValue(line.clientId, out string name) ? name : line.clientId,
                            campaign = line.campaign,
                            state = line.state,
                            completedRuns = line.completed,
                            totalRuns = line.total,
                        });
                }
                WorkerFiles.Write(WorkerFiles.StatusPath, status);
            }
            catch (Exception exception)
            {
                Debug.LogWarning("[worker] Could not write the status file: " + exception.Message);
            }
        }

        void Log(string message)
        {
            string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}";
            Debug.Log("[worker] " + message);
            lock (logGate)
            {
                try { if (logPath != null) File.AppendAllText(logPath, line + Environment.NewLine); }
                catch (Exception) { }
            }
        }
    }

    /// <summary>The simulator program to start headless processes with (worker shards, and this PC's own share of a network batch).</summary>
    public static class SimulatorExecutable
    {
        /// <summary>
        /// In a build: this program itself. In the Unity editor: the newest build in the project's Builds folder (a
        /// headless process needs a built copy). Returns null with a plain-language problem. Main thread only.
        /// </summary>
        public static string Find(out string problem)
        {
            problem = null;
            if (!Application.isEditor)
            {
                try
                {
                    string path = Process.GetCurrentProcess().MainModule?.FileName;
                    if (!string.IsNullOrEmpty(path) && File.Exists(path)) return path;
                }
                catch (Exception) { }
                string first = Environment.GetCommandLineArgs().FirstOrDefault();
                if (!string.IsNullOrEmpty(first) && File.Exists(first)) return Path.GetFullPath(first);
                problem = "Could not find this simulator's own program file to start simulations with.";
                return null;
            }

            string builds = Path.Combine(Path.GetDirectoryName(Application.dataPath) ?? ".", "Builds");
            bool windows = Application.platform == RuntimePlatform.WindowsEditor;
            string best = null;
            DateTime newest = DateTime.MinValue;
            if (Directory.Exists(builds))
            {
                foreach (string data in Directory.GetDirectories(builds, "*_Data", SearchOption.AllDirectories))
                {
                    string stem = data.Substring(0, data.Length - "_Data".Length);
                    var candidates = windows ? new[] { stem + ".exe" } : new[] { stem + ".x86_64", stem };
                    foreach (string candidate in candidates)
                    {
                        if (!File.Exists(candidate)) continue;
                        DateTime written = File.GetLastWriteTimeUtc(candidate);
                        if (written > newest)
                        {
                            newest = written;
                            best = candidate;
                        }
                    }
                }
            }
            if (best != null) return best;
            problem = windows
                ? "Running from the Unity editor: build the simulator for Windows into the project's Builds folder first (File > Build Profiles). Workers and this PC's headless share run that build."
                : "Running from the Unity editor: build the simulator into the project's Builds folder first. Workers and this PC's headless share run that build.";
            return null;
        }
    }
}
