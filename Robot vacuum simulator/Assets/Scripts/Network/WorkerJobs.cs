using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using RobotVacuum.Headless;
using UnityEngine;

namespace RobotVacuum.Network
{
    /// <summary>What to start: one shard of a job, or a validation of the whole job.</summary>
    public sealed class ShardLaunch
    {
        public string jobPath;
        public string outputFolder;
        public string logFile;
        public int shardIndex;
        public int shardCount;
        public bool validateOnly;
    }

    public interface IShardProcess
    {
        bool HasExited { get; }
        int ExitCode { get; }
        void Kill();
    }

    /// <summary>Starts shard processes. The real one runs this simulator's headless mode; tests use a fake.</summary>
    public interface IShardLauncher
    {
        IShardProcess Start(ShardLaunch launch, out string problem);
    }

    /// <summary>
    /// Runs each shard as its own headless process of this simulator: <c>-batchmode -nographics -configPath job.json
    /// -outputPath shard -shardIndex i -shardCount n</c>, exactly as a cloud node does (SDD 2.1). The worker's own
    /// process never compiles or runs a job's algorithm code; only these children do, behind the plugin sandbox (4.8).
    /// </summary>
    public sealed class ProcessShardLauncher : IShardLauncher
    {
        readonly string executable;

        public ProcessShardLauncher(string executable) => this.executable = executable;

        public IShardProcess Start(ShardLaunch launch, out string problem)
        {
            problem = null;
            string arguments = $"-batchmode -nographics -logFile \"{launch.logFile}\" -configPath \"{launch.jobPath}\" " +
                               $"-outputPath \"{launch.outputFolder}\"";
            arguments += launch.validateOnly ? " -validateOnly" : $" -shardIndex {launch.shardIndex} -shardCount {launch.shardCount}";
            try
            {
                var process = Process.Start(new ProcessStartInfo(executable, arguments)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = launch.outputFolder,
                });
                if (process == null)
                {
                    problem = "The simulator could not be started.";
                    return null;
                }
                try { process.PriorityClass = ProcessPriorityClass.BelowNormal; } // keep the worker PC usable (C.6)
                catch (Exception) { }
                return new Wrapped(process);
            }
            catch (Exception exception)
            {
                problem = $"The simulator could not be started: {exception.Message}";
                return null;
            }
        }

        sealed class Wrapped : IShardProcess
        {
            readonly Process process;
            public Wrapped(Process process) => this.process = process;

            public bool HasExited
            {
                get
                {
                    try { return process.HasExited; }
                    catch (Exception) { return true; }
                }
            }

            public int ExitCode
            {
                get
                {
                    try { return process.ExitCode; }
                    catch (Exception) { return HeadlessBootstrap.ExitCrashed; }
                }
            }

            public void Kill()
            {
                try { if (!process.HasExited) process.Kill(); }
                catch (Exception) { }
            }
        }
    }

    /// <summary>
    /// The worker's jobs. Each paired client has its own folder, and every request is answered only from the asking
    /// client's folder, so one researcher can never list, read, cancel or delete another's work (4.8):
    /// <code>
    ///   clients/&lt;clientId&gt;/jobs/&lt;jobId&gt;/
    ///     job.json                the headless job (docs/job-format.md), exactly as received
    ///     state.json              this worker's bookkeeping (survives a restart)
    ///     validate/               the validation run's results.json and log
    ///     shards/shard-NNNN/      what the headless shard writes: results.json, heartbeat.json, logs/, runs/
    /// </code>
    /// A job is first checked by a validation process (the same checks a node makes, with the plugin sandbox), then its
    /// shards are queued and run, at most <c>maxProcesses</c> at a time across all clients, oldest job first.
    /// If the worker stops, unfinished shards are queued again when it restarts and resume from their results.json.
    /// </summary>
    public sealed class WorkerJobs : IDisposable
    {
        public const string StateFileName = "state.json";
        public static readonly TimeSpan Retention = TimeSpan.FromDays(7);

        readonly string root;
        readonly IShardLauncher launcher;
        readonly Action<string> log;
        readonly object gate = new object();
        readonly List<Job> jobs = new List<Job>();
        readonly Thread scheduler;
        volatile bool stopping;

        public int MaxProcesses { get; }

        [Serializable]
        sealed class JobState
        {
            public string clientId;
            public string jobId;
            public string campaignName;
            public int shardCount;
            public string jobSha256;
            public string submittedUtc;
            public string state;
            public List<string> errors = new List<string>();
            public List<ShardState> shards = new List<ShardState>();
        }

        [Serializable]
        sealed class ShardState
        {
            public int shardIndex;
            public string state;
            public int exitCode = -1;
            public string message;
        }

        sealed class Job
        {
            public JobState data;
            public string folder;
            public IShardProcess validator;
            public readonly Dictionary<int, IShardProcess> processes = new Dictionary<int, IShardProcess>();
            public DateTime submitted;

            public string JobPath => Path.Combine(folder, "job.json");
            public string ValidateFolder => Path.Combine(folder, "validate");
            public string ShardFolder(int index) => Path.Combine(folder, "shards", $"shard-{index:D4}");
            public ShardState Shard(int index) => data.shards.FirstOrDefault(s => s.shardIndex == index);
        }

        public WorkerJobs(string root, IShardLauncher launcher, int maxProcesses, Action<string> log, bool startScheduler = true)
        {
            this.root = root;
            this.launcher = launcher;
            this.log = log ?? (_ => { });
            MaxProcesses = Math.Max(1, maxProcesses);
            Directory.CreateDirectory(root);
            LoadExisting();
            if (!startScheduler) return;
            scheduler = new Thread(SchedulerLoop) { IsBackground = true, Name = "Worker scheduler" };
            scheduler.Start();
        }

        public void Dispose()
        {
            stopping = true;
            scheduler?.Join(2000);
            lock (gate)
            {
                foreach (var job in jobs)
                {
                    job.validator?.Kill();
                    foreach (var process in job.processes.Values) process.Kill();
                }
            }
        }

        public (int running, int queued) Load()
        {
            lock (gate)
            {
                int running = jobs.Sum(j => j.processes.Count + (j.validator != null ? 1 : 0));
                int queued = jobs.Sum(j => j.data.shards.Count(s => s.state == WorkState.Queued));
                return (running, queued);
            }
        }

        // ---------------------------------------------------------------- requests (one client's own jobs only)

        /// <summary>Accepts shards of a job. Returns null, or a plain-language reason it was refused.</summary>
        public string Submit(string clientId, SubmitRequest request, byte[] jobBytes, string clientName = null)
        {
            if (!WorkerProtocol.IsValidId(clientId)) return "The client id is not valid.";
            if (request == null || !WorkerProtocol.IsValidId(request.jobId)) return "The job id must be letters, digits, '.', '_' or '-' (at most 80).";
            if (jobBytes == null || jobBytes.Length == 0) return "The job file is empty.";
            if (jobBytes.Length > WorkerProtocol.MaxJobBytes) return $"The job file is over the {WorkerProtocol.MaxJobBytes / (1024 * 1024)} MB limit.";
            if (request.shardCount < 1 || request.shardCount > WorkerProtocol.MaxShardCount)
                return $"A job can be split into 1 to {WorkerProtocol.MaxShardCount} shards.";
            var shards = (request.shards ?? new List<int>()).Distinct().ToList();
            if (shards.Count == 0) return "No shards were given to run.";
            if (shards.Any(s => s < 0 || s >= request.shardCount)) return "A shard number is outside the job's shards.";
            long resumeBytes = (request.resume ?? new List<ShardResume>()).Sum(r => (long)(r?.resultsJson?.Length ?? 0));
            if (resumeBytes > WorkerProtocol.MaxResumeBytes) return "The results to resume from are too large.";

            string sha = Sha256(jobBytes);
            lock (gate)
            {
                var job = Find(clientId, request.jobId);
                if (job != null && job.data.jobSha256 != sha) return "A different job with this id is already on this worker.";
                if (job != null && job.data.shardCount != request.shardCount) return "The job's shard count does not match the copy already on this worker.";
                if (job != null && (job.data.state == WorkState.Cancelled || job.data.state == WorkState.Rejected))
                    return $"This job was {job.data.state} on this worker; start it again with a new id.";

                if (job == null)
                {
                    string folder = Path.Combine(root, clientId, "jobs", request.jobId);
                    Directory.CreateDirectory(folder);
                    job = new Job
                    {
                        folder = folder,
                        submitted = DateTime.UtcNow,
                        data = new JobState
                        {
                            clientId = clientId,
                            jobId = request.jobId,
                            campaignName = WorkerProtocol.CleanName(request.campaignName, request.jobId),
                            shardCount = request.shardCount,
                            jobSha256 = sha,
                            submittedUtc = DateTime.UtcNow.ToString("o"),
                            state = WorkState.Validating,
                        },
                    };
                    File.WriteAllBytes(job.JobPath, jobBytes);
                    jobs.Add(job);
                    log($"Job {request.jobId} ({job.data.campaignName}) received from {WorkerProtocol.CleanName(clientName, clientId)}: shards {string.Join(", ", shards)} of {request.shardCount}.");
                }
                else log($"Job {request.jobId}: shards {string.Join(", ", shards)} added.");

                foreach (int index in shards)
                {
                    var shard = job.Shard(index);
                    if (shard != null && !WorkState.IsFinal(shard.state)) continue; // already here and not finished
                    if (shard != null && shard.state == WorkState.Finished) continue;
                    if (shard == null)
                    {
                        shard = new ShardState { shardIndex = index };
                        job.data.shards.Add(shard);
                    }
                    shard.state = job.data.state == WorkState.Validating ? WorkState.Validating : WorkState.Queued;
                    shard.exitCode = -1;
                    shard.message = null;

                    string shardFolder = job.ShardFolder(index);
                    Directory.CreateDirectory(shardFolder);
                    var resume = request.resume?.FirstOrDefault(r => r != null && r.shardIndex == index && !string.IsNullOrEmpty(r.resultsJson));
                    if (resume != null) File.WriteAllText(Path.Combine(shardFolder, ShardResults.FileName), resume.resultsJson);
                }
                if (job.data.state == WorkState.Finished || job.data.state == WorkState.Failed) job.data.state = WorkState.Queued;
                Save(job);
            }
            return null;
        }

        public StatusReply Status(string clientId, string jobId)
        {
            var reply = new StatusReply();
            lock (gate)
            {
                foreach (var job in jobs.Where(j => j.data.clientId == clientId && (string.IsNullOrEmpty(jobId) || j.data.jobId == jobId)))
                {
                    var status = new JobStatus
                    {
                        jobId = job.data.jobId,
                        campaignName = job.data.campaignName,
                        state = job.data.state,
                        submittedUtc = job.data.submittedUtc,
                        errors = new List<string>(job.data.errors),
                    };
                    foreach (var shard in job.data.shards.OrderBy(s => s.shardIndex))
                    {
                        var line = new ShardStatus
                        {
                            shardIndex = shard.shardIndex,
                            state = shard.state,
                            exitCode = shard.exitCode,
                            message = shard.message,
                        };
                        string folder = job.ShardFolder(shard.shardIndex);
                        var results = ReadJson<ShardResults>(Path.Combine(folder, ShardResults.FileName));
                        if (results != null)
                        {
                            line.totalRuns = results.totalRuns;
                            line.completedRuns = results.completedRuns;
                            line.failedRuns = results.failedRuns;
                            line.updatedUtc = results.updatedUtc;
                        }
                        var heartbeat = ReadJson<Heartbeat>(Path.Combine(folder, Heartbeat.FileName));
                        if (heartbeat != null)
                        {
                            line.completedRuns = Math.Max(line.completedRuns, heartbeat.completedRuns);
                            if (heartbeat.totalRuns > 0) line.totalRuns = heartbeat.totalRuns;
                            line.updatedUtc = heartbeat.updatedUtc ?? line.updatedUtc;
                        }
                        status.shards.Add(line);
                    }
                    reply.jobs.Add(status);
                }
            }
            return reply;
        }

        public FileList ListFiles(string clientId, string jobId, int shardIndex, out string error)
        {
            error = null;
            var list = new FileList();
            string folder;
            lock (gate)
            {
                var job = Find(clientId, jobId);
                if (job == null || job.Shard(shardIndex) == null)
                {
                    error = "There is no such job or shard on this worker.";
                    return list;
                }
                folder = job.ShardFolder(shardIndex);
            }
            if (!Directory.Exists(folder)) return list;

            string prefix = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            foreach (string file in Directory.GetFiles(folder, "*", SearchOption.AllDirectories))
            {
                if (file.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;
                string full = Path.GetFullPath(file);
                if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                long size;
                try { size = new FileInfo(full).Length; }
                catch (Exception) { continue; }
                list.files.Add(new FileEntry { path = full.Substring(prefix.Length).Replace('\\', '/'), size = size });
            }
            list.files.Sort((a, b) => string.CompareOrdinal(a.path, b.path));
            return list;
        }

        /// <summary>Reads part of a file in one of the client's own shard folders. Paths outside it are refused (4.8).</summary>
        public byte[] ReadChunk(string clientId, FileRequest request, out FileChunk chunk, out string error)
        {
            chunk = null;
            error = null;
            string folder;
            lock (gate)
            {
                var job = request != null ? Find(clientId, request.jobId) : null;
                if (job == null || job.Shard(request.shardIndex) == null)
                {
                    error = "There is no such job or shard on this worker.";
                    return null;
                }
                folder = job.ShardFolder(request.shardIndex);
            }

            if (!TryResolveInside(folder, request.path, out string full))
            {
                error = "That file is not part of this shard.";
                return null;
            }
            try
            {
                using (var file = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    long total = file.Length;
                    long offset = Math.Max(0, Math.Min(request.offset, total));
                    int length = (int)Math.Min(Math.Max(0, Math.Min(request.length, WorkerProtocol.MaxChunkBytes)), total - offset);
                    var data = new byte[length];
                    file.Seek(offset, SeekOrigin.Begin);
                    int read = 0;
                    while (read < length)
                    {
                        int n = file.Read(data, read, length - read);
                        if (n <= 0) break;
                        read += n;
                    }
                    if (read < length) Array.Resize(ref data, read);
                    chunk = new FileChunk { path = request.path, offset = offset, totalSize = total };
                    return data;
                }
            }
            catch (Exception exception)
            {
                error = $"The file could not be read: {exception.Message}";
                return null;
            }
        }

        public string Cancel(string clientId, string jobId)
        {
            lock (gate)
            {
                var job = Find(clientId, jobId);
                if (job == null) return "There is no such job on this worker.";
                CancelLocked(job, "Cancelled by the PC that sent it.");
                Save(job);
                log($"Job {jobId} cancelled.");
                return null;
            }
        }

        /// <summary>Cancels the job if needed and deletes its folder: the client has everything it wants.</summary>
        public string Release(string clientId, string jobId)
        {
            Job job;
            lock (gate)
            {
                job = Find(clientId, jobId);
                if (job == null) return null; // already gone
                CancelLocked(job, "Released.");
                jobs.Remove(job);
            }
            DeleteFolder(job.folder);
            log($"Job {jobId} released and deleted.");
            return null;
        }

        /// <summary>Stops and deletes everything a client sent (when it unpairs or is removed).</summary>
        public void ForgetClient(string clientId)
        {
            List<Job> theirs;
            lock (gate)
            {
                theirs = jobs.Where(j => j.data.clientId == clientId).ToList();
                foreach (var job in theirs)
                {
                    CancelLocked(job, "The PC that sent it was removed.");
                    jobs.Remove(job);
                }
            }
            if (WorkerProtocol.IsValidId(clientId)) DeleteFolder(Path.Combine(root, clientId));
            if (theirs.Count > 0) log($"Removed {theirs.Count} job(s) from a client that was unpaired.");
        }

        /// <summary>A summary of every job, for the worker's own status display.</summary>
        public List<(string clientId, string campaign, string state, int completed, int total)> Summary()
        {
            var lines = new List<(string, string, string, int, int)>();
            List<string> clients;
            lock (gate) clients = jobs.Select(j => j.data.clientId).Distinct().ToList();
            foreach (string client in clients)
            {
                foreach (var status in Status(client, null).jobs)
                    lines.Add((client, status.campaignName, status.state, status.shards.Sum(s => s.completedRuns), status.shards.Sum(s => s.totalRuns)));
            }
            return lines;
        }

        // ---------------------------------------------------------------- scheduling

        void SchedulerLoop()
        {
            while (!stopping)
            {
                try { Tick(); }
                catch (Exception exception) { log($"Scheduler error: {exception.Message}"); }
                Thread.Sleep(500);
            }
        }

        /// <summary>One scheduling step: collect finished processes, start new ones. Public for tests.</summary>
        public void Tick()
        {
            lock (gate)
            {
                foreach (var job in jobs) Collect(job);
                StartWork();
            }
        }

        void Collect(Job job)
        {
            bool changed = false;
            if (job.validator != null && job.validator.HasExited)
            {
                int code = job.validator.ExitCode;
                job.validator = null;
                changed = true;
                if (job.data.state == WorkState.Cancelled) { }
                else if (code == HeadlessBootstrap.ExitOk)
                {
                    job.data.state = WorkState.Queued;
                    foreach (var shard in job.data.shards.Where(s => s.state == WorkState.Validating)) shard.state = WorkState.Queued;
                    log($"Job {job.data.jobId} is valid; its shards are queued.");
                }
                else
                {
                    var results = ReadJson<ShardResults>(Path.Combine(job.ValidateFolder, ShardResults.FileName));
                    job.data.errors = results?.errors != null && results.errors.Count > 0
                        ? results.errors
                        : new List<string> { $"The job could not be checked (exit code {code}); see validate/unity.log on the worker." };
                    job.data.state = code == HeadlessBootstrap.ExitInvalid ? WorkState.Rejected : WorkState.Failed;
                    foreach (var shard in job.data.shards.Where(s => !WorkState.IsFinal(s.state)))
                    {
                        shard.state = job.data.state;
                        shard.message = job.data.errors.FirstOrDefault();
                    }
                    log($"Job {job.data.jobId} was {job.data.state}: {string.Join(" | ", job.data.errors)}");
                }
            }

            foreach (var pair in job.processes.ToList())
            {
                if (!pair.Value.HasExited) continue;
                int code = pair.Value.ExitCode;
                job.processes.Remove(pair.Key);
                changed = true;
                var shard = job.Shard(pair.Key);
                if (shard == null || shard.state == WorkState.Cancelled) continue;
                shard.exitCode = code;
                if (code == HeadlessBootstrap.ExitOk || code == HeadlessBootstrap.ExitSomeRunsFailed)
                {
                    shard.state = WorkState.Finished;
                    shard.message = code == HeadlessBootstrap.ExitSomeRunsFailed ? "Finished; some runs failed (see results)." : null;
                }
                else if (code == HeadlessBootstrap.ExitInvalid)
                {
                    shard.state = WorkState.Rejected;
                    shard.message = "The job was rejected; see logs/unity.log.";
                }
                else
                {
                    shard.state = WorkState.Failed;
                    shard.message = $"The shard stopped with exit code {code}; see logs/unity.log.";
                }
                log($"Job {job.data.jobId} shard {pair.Key}: {shard.state} (exit code {code}).");
            }

            if (job.data.shards.Count > 0 && job.data.state != WorkState.Validating && job.data.state != WorkState.Cancelled &&
                job.data.state != WorkState.Rejected)
            {
                string before = job.data.state;
                if (job.data.shards.All(s => WorkState.IsFinal(s.state)))
                    job.data.state = job.data.shards.All(s => s.state == WorkState.Finished) ? WorkState.Finished : WorkState.Failed;
                else
                    job.data.state = job.processes.Count > 0 ? WorkState.Running : WorkState.Queued;
                changed |= before != job.data.state;
            }
            if (changed) Save(job);
        }

        void StartWork()
        {
            int busy = jobs.Sum(j => j.processes.Count + (j.validator != null ? 1 : 0));
            foreach (var job in jobs.OrderBy(j => j.submitted))
            {
                if (busy >= MaxProcesses) return;
                if (job.data.state != WorkState.Validating || job.validator != null) continue;
                Directory.CreateDirectory(job.ValidateFolder);
                job.validator = launcher.Start(new ShardLaunch
                {
                    jobPath = job.JobPath,
                    outputFolder = job.ValidateFolder,
                    logFile = Path.Combine(job.ValidateFolder, "unity.log"),
                    shardCount = job.data.shardCount,
                    validateOnly = true,
                }, out string problem);
                if (job.validator == null)
                {
                    job.data.state = WorkState.Failed;
                    job.data.errors = new List<string> { problem };
                    foreach (var shard in job.data.shards) { shard.state = WorkState.Failed; shard.message = problem; }
                    Save(job);
                    log($"Job {job.data.jobId}: {problem}");
                    continue;
                }
                busy++;
            }

            foreach (var job in jobs.OrderBy(j => j.submitted))
            {
                if (job.data.state == WorkState.Validating || job.data.state == WorkState.Cancelled || job.data.state == WorkState.Rejected) continue;
                foreach (var shard in job.data.shards.Where(s => s.state == WorkState.Queued).OrderBy(s => s.shardIndex))
                {
                    if (busy >= MaxProcesses) return;
                    string folder = job.ShardFolder(shard.shardIndex);
                    Directory.CreateDirectory(Path.Combine(folder, "logs"));
                    var process = launcher.Start(new ShardLaunch
                    {
                        jobPath = job.JobPath,
                        outputFolder = folder,
                        logFile = Path.Combine(folder, "logs", "unity.log"),
                        shardIndex = shard.shardIndex,
                        shardCount = job.data.shardCount,
                    }, out string problem);
                    if (process == null)
                    {
                        shard.state = WorkState.Failed;
                        shard.message = problem;
                        log($"Job {job.data.jobId} shard {shard.shardIndex}: {problem}");
                        continue;
                    }
                    job.processes[shard.shardIndex] = process;
                    shard.state = WorkState.Running;
                    job.data.state = WorkState.Running;
                    busy++;
                    log($"Job {job.data.jobId} shard {shard.shardIndex} started.");
                    Save(job);
                }
            }
        }

        void CancelLocked(Job job, string message)
        {
            job.validator?.Kill();
            job.validator = null;
            foreach (var process in job.processes.Values) process.Kill();
            job.processes.Clear();
            foreach (var shard in job.data.shards.Where(s => !WorkState.IsFinal(s.state)))
            {
                shard.state = WorkState.Cancelled;
                shard.message = message;
            }
            if (!WorkState.IsFinal(job.data.state)) job.data.state = WorkState.Cancelled;
        }

        // ---------------------------------------------------------------- storage

        void LoadExisting()
        {
            if (!Directory.Exists(root)) return;
            foreach (string clientFolder in Directory.GetDirectories(root))
            {
                string jobsFolder = Path.Combine(clientFolder, "jobs");
                if (!Directory.Exists(jobsFolder)) continue;
                foreach (string folder in Directory.GetDirectories(jobsFolder))
                {
                    var data = ReadJson<JobState>(Path.Combine(folder, StateFileName));
                    if (data == null || !WorkerProtocol.IsValidId(data.clientId) || !WorkerProtocol.IsValidId(data.jobId) ||
                        !File.Exists(Path.Combine(folder, "job.json")))
                        continue;

                    DateTime.TryParse(data.submittedUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out DateTime submitted);
                    var job = new Job { data = data, folder = folder, submitted = submitted };
                    if (WorkState.IsFinal(data.state) && DateTime.UtcNow - submitted > Retention)
                    {
                        DeleteFolder(folder);
                        log($"Deleted job {data.jobId}: finished more than {Retention.TotalDays:0} days ago and never collected.");
                        continue;
                    }

                    // Interrupted by a restart: validate again, or queue again (a shard resumes from its results.json).
                    foreach (var shard in data.shards.Where(s => s.state == WorkState.Running)) shard.state = WorkState.Queued;
                    if (data.state == WorkState.Running) data.state = WorkState.Queued;
                    jobs.Add(job);
                }
            }
            if (jobs.Count > 0) log($"Picked up {jobs.Count} job(s) from before the worker last stopped.");
        }

        void Save(Job job)
        {
            try
            {
                string path = Path.Combine(job.folder, StateFileName);
                File.WriteAllText(path + ".tmp", JsonUtility.ToJson(job.data, true));
                if (File.Exists(path)) File.Delete(path);
                File.Move(path + ".tmp", path);
            }
            catch (Exception exception)
            {
                log($"Could not save the state of job {job.data.jobId}: {exception.Message}");
            }
        }

        Job Find(string clientId, string jobId) =>
            jobs.FirstOrDefault(j => j.data.clientId == clientId && j.data.jobId == jobId);

        static T ReadJson<T>(string path) where T : class
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

        /// <summary>True if <paramref name="relative"/> names an existing file inside <paramref name="folder"/>.</summary>
        public static bool TryResolveInside(string folder, string relative, out string full) =>
            TryResolveInsideOrNew(folder, relative, out full) && File.Exists(full);

        /// <summary>
        /// True if <paramref name="relative"/> is a plain relative path that stays inside <paramref name="folder"/> (no
        /// "..", no absolute path, no drive letter), whether or not the file exists yet. Both ends use it: the worker
        /// for what it reads, the client for where it writes what it downloads (4.8).
        /// </summary>
        public static bool TryResolveInsideOrNew(string folder, string relative, out string full)
        {
            full = null;
            if (string.IsNullOrEmpty(relative) || relative.Length > 400 || Path.IsPathRooted(relative)) return false;
            if (relative.IndexOf(':') >= 0) return false;
            foreach (string part in relative.Split('/', '\\'))
                if (part.Length == 0 || part == ".." || part == ".") return false;

            string prefix = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string candidate = Path.GetFullPath(Path.Combine(folder, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
            full = candidate;
            return true;
        }

        static void DeleteFolder(string folder)
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    if (Directory.Exists(folder)) Directory.Delete(folder, true);
                    return;
                }
                catch (Exception)
                {
                    Thread.Sleep(200); // a just-killed process may still hold a file
                }
            }
        }

        static string Sha256(byte[] bytes)
        {
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }
    }
}
