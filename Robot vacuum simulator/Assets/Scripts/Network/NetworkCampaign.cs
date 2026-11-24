using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using RobotVacuum.Headless;
using RobotVacuum.LevelEditor;
using UnityEngine;

namespace RobotVacuum.Network
{
    /// <summary>Where a network batch may run: a paired worker, or (worker == null) this PC.</summary>
    public sealed class CampaignTarget
    {
        public PairedWorker worker;
        public string Label => worker == null ? "This PC" : worker.name;
    }

    /// <summary>One computer's part of a running network batch, for the progress display.</summary>
    public sealed class TargetProgress
    {
        public string label;
        public string state;
        public string message;
        public int shards;
        public int completedRuns;
        public int totalRuns;
    }

    public sealed class CampaignProgress
    {
        public string phase;
        public string problem;
        public int totalRuns;
        public int completedRuns;
        public int failedRuns;
        public List<TargetProgress> targets = new List<TargetProgress>();
    }

    /// <summary>
    /// A batch split across this PC and paired network workers (3.9; 4.5; 5.5 "local cluster"), run as one campaign:
    /// <list type="number">
    /// <item>connect to each chosen worker and ask how many simulations it runs at once;</item>
    /// <item>split the runs into that many shards (ShardPlan), send each worker its shards, start this PC's own as
    ///   headless processes;</item>
    /// <item>every couple of seconds: follow each shard and download what finished (results in steps, SDD 8);</item>
    /// <item>a computer that stops answering for a minute, or a shard that crashes, has its shards moved to another
    ///   computer, which continues from the runs already done (up to 3 tries per shard; 4.5 failure recovery);</item>
    /// <item>merge the shards into campaign_results.json and import the campaign into Analysis (2.18, C.13).</item>
    /// </list>
    /// The campaign folder (NetworkCampaigns/&lt;id&gt;) has the same layout as a cloud campaign, plus network.log and
    /// every shard's logs/unity.log from every computer: all the batch's logs in one place (3.12).
    /// One network batch runs at a time. The work happens on a background thread; the import on the main thread.
    /// </summary>
    public sealed class NetworkCampaign
    {
        public const string Transport = "network";
        public const string PhaseStarting = "starting";
        public const string PhaseRunning = "running";
        public const string PhaseCollecting = "collecting";
        public const string PhaseImporting = "importing";
        public const string PhaseFinished = "finished";
        public const string PhaseCancelled = "cancelled";
        public const string PhaseFailed = "failed";

        const int MaxAttempts = 3;

        /// <summary>A worker silent this long is treated as gone and its shards move. Shorter in tests.</summary>
        internal static TimeSpan LostAfter = TimeSpan.FromSeconds(60);
        internal static TimeSpan PollInterval = TimeSpan.FromSeconds(2);

        /// <summary>Tests import into their own history instead of the app's.</summary>
        internal string historyPathForTests;
        internal string dataRootForTests;

        public static NetworkCampaign Current { get; private set; }

        /// <summary>Raised on the main thread once a campaign has finished (and been imported), been cancelled or failed.</summary>
        public static event Action<NetworkCampaign> Ended;

        sealed class Target
        {
            public PairedWorker worker;
            public string label;
            public int slots;
            public WorkerClient client;
            public DateTime lastContact = DateTime.UtcNow;
            public string state = "connecting";
            public string message;
            public bool dead;
            public bool IsLocal => worker == null;
        }

        sealed class Shard
        {
            public int index;
            public Target target;
            public string state = WorkState.Queued;
            public int attempts;
            public IShardProcess local;
            public int totalRuns;
            public int completedRuns;
            public int failedRuns;
            public bool done;      // final, and its files are here
            public string message;
        }

        [Serializable]
        sealed class Manifest
        {
            public string transport = Transport;
            public string campaignId;
            public string campaignName;
            public string createdUtc;
            public int shardCount;
            public List<ManifestTarget> targets = new List<ManifestTarget>();
        }

        [Serializable]
        sealed class ManifestTarget
        {
            public string label;
            public string workerId;
            public string address;
            public List<int> shards = new List<int>();
        }

        readonly object gate = new object();
        readonly HeadlessJob job;
        readonly string executable;
        readonly int localSlots;
        readonly List<Target> targets = new List<Target>();
        readonly List<Shard> shards = new List<Shard>();
        readonly string logPath;
        byte[] jobBytes;
        int shardCount;
        volatile bool cancelRequested;
        volatile string phase = PhaseStarting;
        string problem;
        bool endRaised;

        public string CampaignId => job.campaignId;
        public string CampaignName => job.campaignName;
        public string Folder { get; }
        public int TotalRuns { get; }
        public string Phase => phase;
        public bool IsOver => phase == PhaseFinished || phase == PhaseCancelled || phase == PhaseFailed;
        public CampaignImport.Result Imported { get; private set; }

        public string Problem
        {
            get { lock (gate) return problem; }
        }

        public static string CampaignsRoot => Path.Combine(Application.persistentDataPath, "NetworkCampaigns");

        NetworkCampaign(HeadlessJob job, IEnumerable<CampaignTarget> chosen, string executable, int localSlots, string root)
        {
            this.job = job;
            this.executable = executable;
            this.localSlots = Math.Max(1, localSlots);
            TotalRuns = job.runs.Count;
            Folder = Path.Combine(root, job.campaignId);
            Directory.CreateDirectory(Folder);
            logPath = Path.Combine(Folder, "network.log");
            foreach (var choice in chosen)
                targets.Add(new Target { worker = choice.worker, label = choice.Label });
        }

        // ---------------------------------------------------------------- starting and stopping (main thread)

        /// <summary>Checks the job and starts the batch on the chosen computers. Returns null with a plain-language problem.</summary>
        public static NetworkCampaign TryStart(HeadlessJob job, List<CampaignTarget> chosen, out string problem)
        {
            problem = null;
            if (Current != null && !Current.IsOver)
            {
                problem = "A network batch is already running. Wait for it to finish, or cancel it, first.";
                return null;
            }
            if (chosen == null || chosen.Count == 0)
            {
                problem = "Choose at least one computer to run the batch on.";
                return null;
            }

            var errors = job.Validate(); // the same checks a worker makes, so a bad batch is caught here (4.8)
            if (errors.Count > 0)
            {
                problem = "The batch can't be sent: " + string.Join(" ", errors);
                return null;
            }

            string executable = null;
            if (chosen.Any(c => c.worker == null))
            {
                executable = SimulatorExecutable.Find(out problem);
                if (executable == null) return null;
            }

            return StartInternal(job, chosen, CampaignsRoot, executable, WorkerRegistry.Settings().Processes, withRunner: true);
        }

        /// <summary>Starts the batch thread. Tests call it directly, with their own folder and no runner (they call <see cref="MainThreadStep"/>).</summary>
        internal static NetworkCampaign StartInternal(HeadlessJob job, List<CampaignTarget> chosen, string root, string executable, int localSlots,
                                                      bool withRunner, string historyPath = null, string dataRoot = null)
        {
            var campaign = new NetworkCampaign(job, chosen, executable, localSlots, root)
            {
                historyPathForTests = historyPath,
                dataRootForTests = dataRoot,
            };
            Current = campaign;
            if (withRunner) NetworkCampaignRunner.Ensure();
            new Thread(campaign.Run) { IsBackground = true, Name = "Network batch" }.Start();
            return campaign;
        }

        /// <summary>Stops every shard everywhere. What already came back stays in the campaign folder and can be imported.</summary>
        public void Cancel() => cancelRequested = true;

        public CampaignProgress Progress()
        {
            var progress = new CampaignProgress { phase = phase, totalRuns = TotalRuns };
            lock (gate)
            {
                progress.problem = problem;
                progress.completedRuns = shards.Sum(s => s.completedRuns);
                progress.failedRuns = shards.Sum(s => s.failedRuns);
                foreach (var target in targets)
                {
                    var mine = shards.Where(s => s.target == target).ToList();
                    progress.targets.Add(new TargetProgress
                    {
                        label = target.label,
                        state = target.state,
                        message = target.message,
                        shards = mine.Count,
                        completedRuns = mine.Sum(s => s.completedRuns),
                        totalRuns = mine.Sum(s => s.totalRuns),
                    });
                }
            }
            return progress;
        }

        // ---------------------------------------------------------------- the batch (background thread)

        void Run()
        {
            try
            {
                Log($"Network batch \"{CampaignName}\" ({CampaignId}): {TotalRuns} run(s) on {string.Join(", ", targets.Select(t => t.label))}.");
                Connect();
                var live = targets.Where(t => !t.dead).ToList();
                if (live.Count == 0)
                {
                    Fail("None of the chosen computers could be reached: " + string.Join(" ", targets.Select(t => $"{t.label}: {t.message}")));
                    return;
                }

                Plan(live);
                phase = PhaseRunning;
                foreach (var target in live) Dispatch(target, ShardsOf(target));

                while (!AllDone())
                {
                    if (cancelRequested)
                    {
                        CancelEverywhere();
                        return;
                    }
                    Poll();
                    Recover();
                    if (phase == PhaseFailed) return;
                    Thread.Sleep(PollInterval);
                }

                phase = PhaseCollecting;
                foreach (var target in targets.Where(t => !t.IsLocal && !t.dead && t.client != null))
                {
                    TryWorker(target, client => client.Release(CampaignId));
                    target.client?.Dispose();
                    target.client = null;
                }
                WriteResults();
                Log("All shards are back. Importing into Analysis.");
                phase = PhaseImporting; // NetworkCampaignRunner imports on the main thread
            }
            catch (Exception exception)
            {
                Fail($"The network batch stopped: {exception.Message}");
                Log(exception.ToString());
            }
        }

        void Connect()
        {
            foreach (var target in targets)
            {
                if (target.IsLocal)
                {
                    target.slots = localSlots;
                    target.state = "ready";
                    continue;
                }
                try
                {
                    target.client = WorkerRegistry.Connect(target.worker);
                    target.slots = Math.Max(1, target.client.Info.maxProcesses);
                    target.state = "ready";
                    target.lastContact = DateTime.UtcNow;
                    Log($"{target.label} ({target.worker.Address}): connected, {target.slots} simulation(s) at a time.");
                }
                catch (WorkerException exception)
                {
                    target.dead = true;
                    target.state = "unreachable";
                    target.message = exception.Message;
                    Log($"{target.label}: {exception.Message}");
                }
            }
        }

        void Plan(List<Target> live)
        {
            var assignment = ShardPlan.Assign(live.Select(t => t.slots).ToList(), TotalRuns, out shardCount);
            job.execution.shardCount = shardCount;
            jobBytes = WorkerProtocol.Utf8(job.ToJson(false));
            File.WriteAllBytes(Path.Combine(Folder, "job.json"), jobBytes);

            var manifest = new Manifest
            {
                campaignId = CampaignId, campaignName = CampaignName, createdUtc = DateTime.UtcNow.ToString("o"), shardCount = shardCount,
            };
            lock (gate)
            {
                for (int t = 0; t < live.Count; t++)
                {
                    foreach (int index in assignment[t])
                        shards.Add(new Shard { index = index, target = live[t], totalRuns = ShardPlan.RunsIn(index, shardCount, TotalRuns) });
                    manifest.targets.Add(new ManifestTarget
                    {
                        label = live[t].label, workerId = live[t].worker?.workerId, address = live[t].worker?.Address, shards = assignment[t],
                    });
                }
            }
            WorkerFiles.Write(Path.Combine(Folder, "manifest.json"), manifest);
            Log($"Split into {shardCount} shard(s): " + string.Join(", ", live.Select((t, i) => $"{t.label} {assignment[i].Count}")) + ".");
        }

        List<Shard> ShardsOf(Target target)
        {
            lock (gate) return shards.Where(s => s.target == target && !s.done).ToList();
        }

        string ShardFolder(int index) => Path.Combine(Folder, "shards", $"shard-{index:D4}");

        void Dispatch(Target target, List<Shard> list)
        {
            if (list.Count == 0) return;
            if (target.IsLocal)
            {
                var launcher = new ProcessShardLauncher(executable);
                foreach (var shard in list)
                {
                    string folder = ShardFolder(shard.index);
                    Directory.CreateDirectory(Path.Combine(folder, "logs"));
                    shard.local = launcher.Start(new ShardLaunch
                    {
                        jobPath = Path.Combine(Folder, "job.json"),
                        outputFolder = folder,
                        logFile = Path.Combine(folder, "logs", "unity.log"),
                        shardIndex = shard.index,
                        shardCount = shardCount,
                    }, out string launchProblem);
                    lock (gate)
                    {
                        shard.state = shard.local != null ? WorkState.Running : WorkState.Failed;
                        shard.message = launchProblem;
                    }
                    Log(shard.local != null ? $"This PC: shard {shard.index} started." : $"This PC: shard {shard.index} could not start: {launchProblem}");
                }
                return;
            }

            var request = new SubmitRequest { jobId = CampaignId, campaignName = CampaignName, shardCount = shardCount };
            foreach (var shard in list)
            {
                request.shards.Add(shard.index);
                var previous = ShardResults.TryRead(ShardFolder(shard.index));
                if (previous != null && previous.runs != null && previous.runs.Count > 0)
                    request.resume.Add(new ShardResume { shardIndex = shard.index, resultsJson = File.ReadAllText(Path.Combine(ShardFolder(shard.index), ShardResults.FileName)) });
            }
            bool sent = TryWorker(target, client => client.Submit(request, jobBytes));
            lock (gate)
                foreach (var shard in list)
                    shard.state = sent ? WorkState.Validating : WorkState.Failed;
            Log(sent ? $"{target.label}: sent shard(s) {string.Join(", ", request.shards)}." : $"{target.label}: could not send shard(s) {string.Join(", ", request.shards)}: {target.message}");
        }

        /// <summary>Runs a request on a worker, reconnecting once. False (and the reason in target.message) if it failed.</summary>
        bool TryWorker(Target target, Action<WorkerClient> request)
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    if (target.client == null) target.client = WorkerRegistry.Connect(target.worker);
                    request(target.client);
                    target.lastContact = DateTime.UtcNow;
                    if (target.state == "reconnecting" || target.state == "ready") target.state = "working";
                    target.message = null;
                    return true;
                }
                catch (WorkerException exception)
                {
                    target.client?.Dispose();
                    target.client = null;
                    target.message = exception.Message;
                    if (exception.Code == "refused" || exception.Code == WorkerProtocol.ErrorVersion || exception.Code == WorkerProtocol.ErrorNotPaired)
                    {
                        MarkDead(target, exception.Message);
                        return false;
                    }
                }
            }
            if (DateTime.UtcNow - target.lastContact > LostAfter) MarkDead(target, "It stopped answering: " + target.message);
            else target.state = "reconnecting";
            return false;
        }

        void MarkDead(Target target, string why)
        {
            if (target.dead) return;
            target.dead = true;
            target.state = "lost";
            target.message = why;
            Log($"{target.label} dropped out: {why}");
        }

        void Poll()
        {
            foreach (var target in targets.Where(t => !t.dead))
            {
                var mine = ShardsOf(target);
                if (mine.Count == 0) continue;
                if (target.IsLocal) PollLocal(mine);
                else PollWorker(target, mine);
            }
        }

        void PollLocal(List<Shard> mine)
        {
            foreach (var shard in mine)
            {
                var results = ShardResults.TryRead(ShardFolder(shard.index));
                lock (gate)
                {
                    if (results != null)
                    {
                        shard.completedRuns = results.completedRuns;
                        shard.failedRuns = results.failedRuns;
                    }
                    if (shard.local == null || !shard.local.HasExited) continue;
                    int code = shard.local.ExitCode;
                    shard.local = null;
                    bool ok = code == HeadlessBootstrap.ExitOk || code == HeadlessBootstrap.ExitSomeRunsFailed;
                    shard.state = ok ? WorkState.Finished : code == HeadlessBootstrap.ExitInvalid ? WorkState.Rejected : WorkState.Failed;
                    shard.message = ok ? null : $"exit code {code}; see shards/shard-{shard.index:D4}/logs/unity.log";
                    shard.done = ok || shard.state == WorkState.Rejected;
                }
                Log($"This PC: shard {shard.index} {shard.state}{(shard.message != null ? " (" + shard.message + ")" : "")}.");
                if (shard.state == WorkState.Rejected) Fail("The batch was rejected: see " + Path.Combine("shards", $"shard-{shard.index:D4}", "logs", "unity.log") + " in the campaign folder.");
            }
        }

        void PollWorker(Target target, List<Shard> mine)
        {
            StatusReply status = null;
            if (!TryWorker(target, client => status = client.Status(CampaignId)) || status == null) return;
            var remote = status.jobs.FirstOrDefault();
            if (remote == null)
            {
                MarkDead(target, "It no longer has this batch.");
                return;
            }
            if (remote.state == WorkState.Rejected)
            {
                Fail($"{target.label} rejected the batch: {string.Join(" ", remote.errors)}");
                return;
            }

            foreach (var shard in mine)
            {
                var line = remote.shards.FirstOrDefault(s => s.shardIndex == shard.index);
                if (line == null) continue;
                lock (gate)
                {
                    shard.state = line.state;
                    shard.message = line.message;
                    shard.completedRuns = Math.Max(shard.completedRuns, line.completedRuns);
                    shard.failedRuns = line.failedRuns;
                    if (line.totalRuns > 0) shard.totalRuns = line.totalRuns;
                }
                bool final = WorkState.IsFinal(line.state);
                if (!Sync(target, shard, final)) continue;
                if (final && line.state == WorkState.Finished)
                {
                    lock (gate) shard.done = true;
                    Log($"{target.label}: shard {shard.index} finished and collected ({line.completedRuns} run(s)).");
                }
            }
        }

        /// <summary>
        /// Downloads what is new in a worker's shard folder: progress files, the growing log, and every run folder whose
        /// result.json exists (written last, so the run is complete). True if the listing could be read.
        /// </summary>
        bool Sync(Target target, Shard shard, bool final)
        {
            FileList list = null;
            if (!TryWorker(target, client => list = client.ListFiles(CampaignId, shard.index)) || list == null) return false;
            var finishedRuns = new HashSet<string>(list.files
                .Where(f => f.path.StartsWith("runs/", StringComparison.Ordinal) && f.path.EndsWith("/result.json", StringComparison.Ordinal))
                .Select(f => f.path.Substring(0, f.path.Length - "/result.json".Length)));

            string folder = ShardFolder(shard.index);
            foreach (var file in list.files)
            {
                if (file.path.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) || file.path.EndsWith(".part", StringComparison.OrdinalIgnoreCase)) continue;
                if (!WorkerJobs.TryResolveInsideOrNew(folder, file.path, out string local)) continue; // never write outside the campaign (4.8)

                long have = File.Exists(local) ? new FileInfo(local).Length : -1;
                bool isRun = file.path.StartsWith("runs/", StringComparison.Ordinal);
                if (isRun)
                {
                    int slash = file.path.IndexOf('/', "runs/".Length);
                    string run = slash > 0 ? file.path.Substring(0, slash) : file.path;
                    if (!finishedRuns.Contains(run) || have == file.size) continue;
                }
                else if (file.path.EndsWith(".log", StringComparison.OrdinalIgnoreCase))
                {
                    if (have >= file.size) continue;
                    if (!TryWorker(target, client => AppendFrom(client, shard.index, file.path, local, Math.Max(0, have)))) return false;
                    continue;
                }
                else if (have == file.size && !final) continue;

                if (!TryWorker(target, client => client.Download(CampaignId, shard.index, file.path, local))) return false;
            }
            return true;
        }

        void AppendFrom(WorkerClient client, int shard, string path, string local, long offset)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(local) ?? ".");
            using (var file = new FileStream(local, FileMode.Append, FileAccess.Write))
            {
                long total;
                do
                {
                    byte[] data = client.ReadChunk(CampaignId, shard, path, offset, WorkerProtocol.MaxChunkBytes, out total);
                    if (data.Length == 0) break;
                    file.Write(data, 0, data.Length);
                    offset += data.Length;
                } while (offset < total);
            }
        }

        /// <summary>Moves shards off computers that dropped out, and retries crashed ones elsewhere (4.5).</summary>
        void Recover()
        {
            List<Shard> stranded;
            lock (gate)
                stranded = shards.Where(s => !s.done && (s.target.dead || s.state == WorkState.Failed || s.state == WorkState.Cancelled)).ToList();
            if (stranded.Count == 0) return;

            foreach (var group in stranded.GroupBy(s => s.target))
            {
                foreach (var shard in group)
                {
                    shard.attempts++;
                    if (shard.attempts >= MaxAttempts)
                    {
                        lock (gate)
                        {
                            shard.done = true;
                            shard.state = WorkState.Failed;
                            shard.message = $"Gave up after {MaxAttempts} tries: {shard.message}";
                        }
                        Log($"Shard {shard.index}: {shard.message}");
                        continue;
                    }

                    var next = PickTarget(exclude: shard.target);
                    if (next == null)
                    {
                        Fail("Every computer in the batch dropped out. The runs that finished are kept: import them from Analysis > Import results.");
                        return;
                    }
                    string from = shard.target.label;
                    lock (gate)
                    {
                        shard.target = next;
                        shard.state = WorkState.Queued;
                    }
                    Log($"Shard {shard.index}: moved from {from} to {next.label} (try {shard.attempts + 1} of {MaxAttempts}); it continues from the runs already done.");
                    Dispatch(next, new List<Shard> { shard });
                }
            }
        }

        Target PickTarget(Target exclude)
        {
            lock (gate)
            {
                var live = targets.Where(t => !t.dead).ToList();
                if (live.Count == 0) return null;
                var others = live.Where(t => t != exclude).ToList();
                var pool = others.Count > 0 ? others : live;
                return pool.OrderBy(t => shards.Count(s => s.target == t && !s.done) / (double)Math.Max(1, t.slots)).First();
            }
        }

        bool AllDone()
        {
            lock (gate) return shards.Count > 0 && shards.All(s => s.done);
        }

        void CancelEverywhere()
        {
            Log("Cancelled. Stopping every shard.");
            List<Shard> all;
            lock (gate) all = shards.ToList();
            foreach (var shard in all) shard.local?.Kill();
            foreach (var target in targets.Where(t => !t.IsLocal && !t.dead))
            {
                TryWorker(target, client =>
                {
                    client.Cancel(CampaignId);
                    client.Release(CampaignId);
                });
                target.client?.Dispose();
                target.client = null;
            }
            WriteResults();
            phase = PhaseCancelled;
        }

        void Fail(string why)
        {
            lock (gate) problem = why;
            Log("Failed: " + why);
            List<Shard> all;
            lock (gate) all = shards.ToList();
            foreach (var shard in all) shard.local?.Kill();
            foreach (var target in targets.Where(t => !t.IsLocal && !t.dead && t.client != null))
            {
                TryWorker(target, client => client.Cancel(CampaignId));
                target.client?.Dispose();
                target.client = null;
            }
            try { WriteResults(); }
            catch (Exception) { }
            phase = PhaseFailed;
        }

        void WriteResults()
        {
            var file = NetworkResultsFile.FromShards(Folder, CampaignId, CampaignName, TotalRuns);
            WorkerFiles.Write(Path.Combine(Folder, CampaignImport.ResultsFileName), file);
            Log($"{file.returnedRuns} of {TotalRuns} run(s) came back ({file.failedRuns} failed).");
        }

        void Log(string message)
        {
            lock (gate)
            {
                try { File.AppendAllText(logPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}"); }
                catch (Exception) { }
            }
        }

        /// <summary>Main thread, from the runner: import a finished campaign and tell listeners. Kills this PC's shards when the app quits.</summary>
        internal void MainThreadStep()
        {
            if (phase == PhaseImporting)
            {
                try
                {
                    Imported = CampaignImport.Import(Folder, historyPathForTests, dataRootForTests);
                    Log($"Imported {Imported.imported} run(s) into Analysis.");
                    phase = PhaseFinished;
                }
                catch (Exception exception)
                {
                    lock (gate) problem = $"The results came back but could not be imported: {exception.Message}. Import them from Analysis > Import results.";
                    phase = PhaseFailed;
                }
            }
            if (IsOver && !endRaised)
            {
                endRaised = true;
                Ended?.Invoke(this);
            }
        }

        internal void OnApplicationQuit()
        {
            if (IsOver) return;
            cancelRequested = true;
            List<Shard> all;
            lock (gate) all = shards.ToList();
            foreach (var shard in all) shard.local?.Kill();
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            Current = null;
            Ended = null;
        }
    }

    /// <summary>Lives across scenes while a network batch runs, to do its main-thread work.</summary>
    public sealed class NetworkCampaignRunner : MonoBehaviour
    {
        static NetworkCampaignRunner instance;

        public static void Ensure()
        {
            if (instance != null) return;
            var host = new GameObject("Network batch");
            DontDestroyOnLoad(host);
            instance = host.AddComponent<NetworkCampaignRunner>();
        }

        void Update() => NetworkCampaign.Current?.MainThreadStep();

        void OnApplicationQuit() => NetworkCampaign.Current?.OnApplicationQuit();
    }
}
