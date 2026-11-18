using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using UnityEngine;

namespace RobotVacuum.Network
{
    /// <summary>A worker this PC is paired with. Not secret: the key is in the secret store (Windows Credential Manager).</summary>
    [Serializable]
    public sealed class PairedWorker
    {
        public string workerId;
        public string name;
        public string host;
        public int port = WorkerProtocol.DefaultPort;
        public string pairedUtc;

        public string Address => host + (port == WorkerProtocol.DefaultPort ? "" : ":" + port);
    }

    [Serializable]
    sealed class PairedWorkerList
    {
        public List<PairedWorker> workers = new List<PairedWorker>();
    }

    /// <summary>This PC's network settings: the worker's port and how many simulations run at once here. Not secret.</summary>
    [Serializable]
    public sealed class NetworkSettings
    {
        public int workerPort = WorkerProtocol.DefaultPort;

        /// <summary>Simulations at once on this PC, as a worker and for its own share of a network batch. 0 = CPU cores - 1.</summary>
        public int processes;

        public int Processes => processes > 0 ? processes : Math.Max(1, Environment.ProcessorCount - 1);
    }

    /// <summary>
    /// The researcher's side of the network workers (3.9): this PC's id, the workers it is paired with, their keys,
    /// and its settings. Files live in the simulator's data folder; keys only in the secret store (C.12).
    /// </summary>
    public static class WorkerRegistry
    {
        const string WorkersFile = "network-workers.json";
        const string SettingsFile = "network-settings.json";
        const string ClientIdFile = "network-client-id.txt";

        static readonly object gate = new object();
        static string dataFolder;
        static string appVersion;
        static string clientId;
        static ISecretStore secrets;

        /// <summary>Reads the main-thread-only Unity values once, so network threads can use the registry.</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        public static void Initialize()
        {
            lock (gate)
            {
                dataFolder = Application.persistentDataPath;
                appVersion = Application.version;
                secrets = null;
                clientId = null;
            }
        }

        /// <summary>For tests: keep everything in <paramref name="folder"/> and keys in memory.</summary>
        internal static void UseForTests(string folder, string version, ISecretStore store)
        {
            lock (gate)
            {
                dataFolder = folder;
                appVersion = version;
                secrets = store;
                clientId = null;
            }
        }

        static string Folder
        {
            get
            {
                if (dataFolder == null) Initialize();
                return dataFolder;
            }
        }

        public static string AppVersion
        {
            get
            {
                if (appVersion == null) Initialize();
                return appVersion;
            }
        }

        public static string ClientName => WorkerProtocol.CleanName(Environment.MachineName, "This PC");

        public static ISecretStore Secrets
        {
            get
            {
                lock (gate) return secrets ??= SecretStore.ForThisComputer(Path.Combine(Folder, "network-keys"));
            }
        }

        /// <summary>This PC's id as a client, made once and kept.</summary>
        public static string ClientId
        {
            get
            {
                lock (gate)
                {
                    if (clientId != null) return clientId;
                    string path = Path.Combine(Folder, ClientIdFile);
                    try
                    {
                        if (File.Exists(path))
                        {
                            string id = File.ReadAllText(path).Trim();
                            if (WorkerProtocol.IsValidId(id)) return clientId = id;
                        }
                    }
                    catch (Exception) { }
                    clientId = Guid.NewGuid().ToString("N");
                    Directory.CreateDirectory(Folder);
                    File.WriteAllText(path, clientId);
                    return clientId;
                }
            }
        }

        static string KeyName(string workerId) => "worker-" + workerId;

        // ---------------------------------------------------------------- paired workers

        public static List<PairedWorker> Workers()
        {
            lock (gate)
            {
                try
                {
                    string path = Path.Combine(Folder, WorkersFile);
                    if (!File.Exists(path)) return new List<PairedWorker>();
                    var list = JsonUtility.FromJson<PairedWorkerList>(File.ReadAllText(path));
                    return (list?.workers ?? new List<PairedWorker>()).Where(w => w != null && WorkerProtocol.IsValidId(w.workerId)).ToList();
                }
                catch (Exception)
                {
                    return new List<PairedWorker>();
                }
            }
        }

        static void SaveWorkers(List<PairedWorker> workers)
        {
            lock (gate)
            {
                Directory.CreateDirectory(Folder);
                WorkerFiles.Write(Path.Combine(Folder, WorkersFile), new PairedWorkerList { workers = workers });
            }
        }

        /// <summary>Pairs with the worker at <paramref name="address"/> using the code it shows. Blocking: call off the main thread.</summary>
        public static PairedWorker Pair(string address, string code)
        {
            if (!WorkerProtocol.TryParseAddress(address, out string host, out int port, out string problem))
                throw new WorkerException(problem);
            var result = WorkerClient.Pair(host, port, code, ClientId, ClientName, AppVersion);
            if (!Secrets.Write(KeyName(result.workerId), result.key, out string storeProblem))
                throw new WorkerException("Paired, but this PC could not save the key: " + storeProblem);
            Array.Clear(result.key, 0, result.key.Length);

            var worker = new PairedWorker
            {
                workerId = result.workerId,
                name = WorkerProtocol.CleanName(result.workerName, host),
                host = host,
                port = port,
                pairedUtc = DateTime.UtcNow.ToString("o"),
            };
            lock (gate)
            {
                var workers = Workers();
                workers.RemoveAll(w => w.workerId == worker.workerId);
                workers.Add(worker);
                SaveWorkers(workers);
            }
            return worker;
        }

        /// <summary>Connects to a paired worker. Blocking: call off the main thread. Throws <see cref="WorkerException"/>.</summary>
        public static WorkerClient Connect(PairedWorker worker) =>
            WorkerClient.Connect(worker.host, worker.port, ClientId, Secrets.Read(KeyName(worker.workerId)), AppVersion);

        /// <summary>Forgets a worker here (key and entry) and, if it can be reached, asks it to forget this PC too. Blocking.</summary>
        public static string Remove(PairedWorker worker)
        {
            string note = null;
            try
            {
                using (var client = Connect(worker)) client.Unpair();
            }
            catch (WorkerException exception)
            {
                note = $"{worker.name} could not be told ({exception.Message}); it can remove this PC from its own Settings.";
            }
            Secrets.Delete(KeyName(worker.workerId));
            lock (gate)
            {
                var workers = Workers();
                workers.RemoveAll(w => w.workerId == worker.workerId);
                SaveWorkers(workers);
            }
            return note;
        }

        // ---------------------------------------------------------------- settings

        public static NetworkSettings Settings()
        {
            try
            {
                string path = Path.Combine(Folder, SettingsFile);
                var settings = File.Exists(path) ? JsonUtility.FromJson<NetworkSettings>(File.ReadAllText(path)) : null;
                settings ??= new NetworkSettings();
                if (settings.workerPort < 1 || settings.workerPort > 65535) settings.workerPort = WorkerProtocol.DefaultPort;
                settings.processes = Mathf.Clamp(settings.processes, 0, 256);
                return settings;
            }
            catch (Exception)
            {
                return new NetworkSettings();
            }
        }

        public static void SaveSettings(NetworkSettings settings) => WorkerFiles.Write(Path.Combine(Folder, SettingsFile), settings);
    }

    /// <summary>
    /// The worker on THIS computer, as Settings sees it: started as a separate headless process of this
    /// simulator (so it keeps the GUI responsive and is the same component as a worker started by hand), followed
    /// through Worker/worker-status.json and told what to do through Worker/worker-control.json.
    /// </summary>
    public static class LocalWorker
    {
        /// <summary>The worker's status, or null if no worker is running on this computer.</summary>
        public static WorkerStatusFile Status()
        {
            var status = WorkerFiles.Read<WorkerStatusFile>(WorkerFiles.StatusPath);
            if (status == null || status.state == "stopped") return status;
            bool fresh = DateTime.TryParse(status.updatedUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out DateTime updated) &&
                         (DateTime.UtcNow - updated).TotalSeconds < 6;
            if (status.state == "error") return status;
            if (!fresh || !Alive(status.pid)) status.state = "stopped";
            return status;
        }

        public static bool IsRunning
        {
            get
            {
                var status = Status();
                return status != null && (status.state == "running" || status.state == "starting");
            }
        }

        static bool Alive(int pid)
        {
            if (pid <= 0) return false;
            try { return !Process.GetProcessById(pid).HasExited; }
            catch (Exception) { return false; }
        }

        /// <summary>Starts the worker. Main thread. Returns false with a plain-language problem.</summary>
        public static bool Start(NetworkSettings settings, out string problem)
        {
            if (IsRunning)
            {
                problem = null;
                return true;
            }
            string executable = SimulatorExecutable.Find(out problem);
            if (executable == null) return false;

            Directory.CreateDirectory(WorkerFiles.DataFolder);
            string unityLog = Path.Combine(WorkerFiles.DataFolder, "worker-unity.log");
            string arguments = $"-batchmode -nographics -worker -workerPort {settings.workerPort} -workerProcesses {settings.Processes} " +
                               $"-workerPairMinutes 0 -workerParentPid {Process.GetCurrentProcess().Id} -logFile \"{unityLog}\"";
            try
            {
                // Clear the last status, so "starting" isn't confused with an old "error".
                WorkerFiles.Write(WorkerFiles.StatusPath, new WorkerStatusFile { state = "starting", updatedUtc = DateTime.UtcNow.ToString("o") });
                var process = Process.Start(new ProcessStartInfo(executable, arguments) { UseShellExecute = false, CreateNoWindow = true });
                if (process == null)
                {
                    problem = "The worker could not be started.";
                    return false;
                }
                return true;
            }
            catch (Exception exception)
            {
                problem = $"The worker could not be started: {exception.Message}";
                return false;
            }
        }

        /// <summary>Asks the running worker to do something: "open-pairing", "close-pairing", "unpair" or "stop".</summary>
        public static void Send(string command, string clientId = null, int minutes = 0)
        {
            int sequence = WorkerFiles.Read<WorkerControlFile>(WorkerFiles.ControlPath)?.sequence ?? 0;
            WorkerFiles.Write(WorkerFiles.ControlPath, new WorkerControlFile { sequence = sequence + 1, command = command, clientId = clientId, minutes = minutes });
        }

        /// <summary>Stops the worker: asks it first; if it is still there after a few seconds, ends its process.</summary>
        public static void Stop()
        {
            var status = Status();
            Send("stop");
            if (status == null || status.pid <= 0) return;
            int pid = status.pid;
            new System.Threading.Thread(() =>
            {
                System.Threading.Thread.Sleep(6000);
                try
                {
                    var process = Process.GetProcessById(pid);
                    if (!process.HasExited) process.Kill();
                }
                catch (Exception) { }
            }) { IsBackground = true }.Start();
        }
    }
}
