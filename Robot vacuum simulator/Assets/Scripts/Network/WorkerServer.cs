using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using UnityEngine;

namespace RobotVacuum.Network
{
    public sealed class WorkerServerOptions
    {
        public int port = WorkerProtocol.DefaultPort;
        public IPAddress bindAddress = IPAddress.Any;
        public string dataFolder;
        public string workerName;
        public string appVersion;
        public string buildId;
        public string platform;
        public int jobSchemaVersion;
        public ISecretStore secrets;
        public WorkerJobs jobs;
        public Action<string> log;
        public int pairingIterations = SecureChannel.PairingIterations;
    }

    /// <summary>A PC allowed to send this worker jobs. Not secret: its key is in the secret store.</summary>
    [Serializable]
    public sealed class PairedClient
    {
        public string clientId;
        public string name;
        public string pairedUtc;
        public string lastSeenUtc;
        public string lastAddress;
    }

    [Serializable]
    sealed class PairedClientList
    {
        public List<PairedClient> clients = new List<PairedClient>();
    }

    /// <summary>
    /// The listening half of the network worker (3.9). Accepts TCP connections from paired PCs, runs the
    /// handshake, then answers requests through a <see cref="SecureChannel"/>; the jobs themselves are in
    /// <see cref="WorkerJobs"/>.
    ///
    /// Pairing: while a pairing window is open (10 minutes, opened by the person at the worker), a PC that knows the
    /// pairing code shown on the worker can pair. The code is never sent; both sides derive a key from it, and only a
    /// side with the right code can produce a message the other accepts. The worker then gives that PC a random
    /// long-term key, kept in the secret store on both sides (Windows Credential Manager). Five wrong codes close the
    /// window. Anyone without a key gets nothing but the public handshake (4.8, C.12).
    /// </summary>
    public sealed class WorkerServer : IDisposable
    {
        public const string PairedFileName = "paired-clients.json";
        public const string IdFileName = "worker-id.txt";

        readonly WorkerServerOptions options;
        readonly object gate = new object();
        readonly List<PairedClient> paired;
        readonly Action<string> log;
        TcpListener listener;
        Thread acceptThread;
        readonly List<TcpClient> open = new List<TcpClient>();
        volatile bool stopping;
        int connections;
        string pairingCode;
        DateTime pairingUntil;
        int pairingFailures;

        public string WorkerId { get; }
        public int Port { get; private set; }

        public WorkerServer(WorkerServerOptions options)
        {
            if (options?.jobs == null || options.secrets == null || string.IsNullOrEmpty(options.dataFolder))
                throw new ArgumentException("The worker needs a data folder, a job manager and a secret store.");
            this.options = options;
            log = options.log ?? (_ => { });
            Directory.CreateDirectory(options.dataFolder);
            WorkerId = LoadOrCreateId();
            paired = LoadPaired();
        }

        public void Start()
        {
            listener = new TcpListener(options.bindAddress, options.port);
            listener.Start(WorkerProtocol.MaxConnections);
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "Worker listener" };
            acceptThread.Start();
            log($"Listening on port {Port} ({string.Join(", ", LocalAddresses())}).");
        }

        /// <summary>Stops listening and closes every open connection: a stopped worker answers nobody.</summary>
        public void Dispose()
        {
            stopping = true;
            try { listener?.Stop(); }
            catch (Exception) { }
            acceptThread?.Join(1000);
            List<TcpClient> connected;
            lock (open) connected = open.ToList();
            foreach (var tcp in connected)
            {
                try { tcp.Close(); }
                catch (Exception) { }
            }
        }

        // ---------------------------------------------------------------- pairing window and paired PCs

        /// <summary>Opens a pairing window with a fresh code (any earlier code stops working) and returns the code.</summary>
        public string OpenPairing(TimeSpan duration)
        {
            string code = SecureChannel.NewPairingCode();
            lock (gate)
            {
                pairingCode = code;
                pairingUntil = DateTime.UtcNow + duration;
                pairingFailures = 0;
            }
            log($"Pairing is open for {duration.TotalMinutes:0} minutes.");
            return code;
        }

        public void ClosePairing()
        {
            lock (gate) pairingCode = null;
        }

        public bool TryGetPairing(out string code, out DateTime until)
        {
            lock (gate)
            {
                if (pairingCode != null && DateTime.UtcNow > pairingUntil) pairingCode = null;
                code = pairingCode;
                until = pairingUntil;
                return code != null;
            }
        }

        public List<PairedClient> PairedClients()
        {
            lock (gate) return paired.Select(p => new PairedClient
            {
                clientId = p.clientId, name = p.name, pairedUtc = p.pairedUtc, lastSeenUtc = p.lastSeenUtc, lastAddress = p.lastAddress,
            }).ToList();
        }

        /// <summary>Removes a PC: its key, and every job it sent (stopped and deleted).</summary>
        public bool Unpair(string clientId)
        {
            PairedClient removed;
            lock (gate)
            {
                removed = paired.FirstOrDefault(p => p.clientId == clientId);
                if (removed == null) return false;
                paired.Remove(removed);
                SavePaired();
            }
            options.secrets.Delete(KeyName(clientId));
            options.jobs.ForgetClient(clientId);
            log($"Removed {removed.name}.");
            return true;
        }

        static string KeyName(string clientId) => "worker-client-" + clientId;

        bool IsPaired(string clientId)
        {
            lock (gate) return paired.Any(p => p.clientId == clientId);
        }

        string NameOf(string clientId)
        {
            lock (gate) return paired.FirstOrDefault(p => p.clientId == clientId)?.name ?? clientId;
        }

        // ---------------------------------------------------------------- connections

        void AcceptLoop()
        {
            while (!stopping)
            {
                TcpClient tcp;
                try
                {
                    tcp = listener.AcceptTcpClient();
                }
                catch (Exception exception)
                {
                    if (stopping) break;
                    log($"Listener error: {exception.Message}");
                    Thread.Sleep(250);
                    continue;
                }

                if (Interlocked.Increment(ref connections) > WorkerProtocol.MaxConnections)
                {
                    Interlocked.Decrement(ref connections);
                    try { tcp.Close(); }
                    catch (Exception) { }
                    continue;
                }

                lock (open) open.Add(tcp);
                var thread = new Thread(() =>
                {
                    try { Serve(tcp); }
                    finally
                    {
                        lock (open) open.Remove(tcp);
                        Interlocked.Decrement(ref connections);
                        try { tcp.Close(); }
                        catch (Exception) { }
                    }
                }) { IsBackground = true, Name = "Worker connection" };
                thread.Start();
            }
        }

        void Serve(TcpClient tcp)
        {
            string address = (tcp.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "unknown address";
            try
            {
                tcp.NoDelay = true;
                tcp.ReceiveTimeout = WorkerProtocol.IdleTimeoutMs;
                tcp.SendTimeout = 30_000;
                var stream = tcp.GetStream();

                byte[] helloBytes = WorkerProtocol.ReadFrame(stream, WorkerProtocol.MaxHandshakeBytes);
                if (helloBytes == null) return;
                Hello hello = null;
                try { hello = JsonUtility.FromJson<Hello>(WorkerProtocol.Utf8(helloBytes)); }
                catch (Exception) { }

                byte[] clientNonce = FromBase64(hello?.clientNonce);
                byte[] serverNonce = SecureChannel.RandomBytes(SecureChannel.NonceBytes);
                var welcome = new Welcome
                {
                    protocol = WorkerProtocol.Version,
                    workerId = WorkerId,
                    workerName = options.workerName,
                    appVersion = options.appVersion,
                    buildId = options.buildId,
                    jobSchemaVersion = options.jobSchemaVersion,
                    serverNonce = Convert.ToBase64String(serverNonce),
                };

                string code = null;
                byte[] secret = null;
                if (hello == null || clientNonce == null || clientNonce.Length != SecureChannel.NonceBytes || !WorkerProtocol.IsValidId(hello.clientId))
                    welcome.error = WorkerProtocol.ErrorBadHello;
                else if (hello.protocol != WorkerProtocol.Version)
                    welcome.error = WorkerProtocol.ErrorProtocol;
                else if (hello.appVersion != options.appVersion)
                    welcome.error = WorkerProtocol.ErrorVersion; // different builds could give different results (2.1)
                else if (hello.mode == WorkerProtocol.ModePair)
                {
                    if (!TryGetPairing(out code, out _)) welcome.error = WorkerProtocol.ErrorPairingClosed;
                }
                else if (hello.mode == WorkerProtocol.ModeSession)
                {
                    secret = IsPaired(hello.clientId) ? options.secrets.Read(KeyName(hello.clientId)) : null;
                    if (secret == null) welcome.error = WorkerProtocol.ErrorNotPaired;
                }
                else welcome.error = WorkerProtocol.ErrorBadHello;

                WorkerProtocol.WriteFrame(stream, WorkerProtocol.Utf8(JsonUtility.ToJson(welcome)));
                if (!string.IsNullOrEmpty(welcome.error))
                {
                    log($"Refused a connection from {address}: {welcome.error}.");
                    return;
                }

                if (hello.mode == WorkerProtocol.ModePair) Pair(stream, hello, code, clientNonce, serverNonce, address);
                else Session(stream, hello, secret, clientNonce, serverNonce, address);
            }
            catch (ChannelAuthenticationException exception)
            {
                log($"Closed a connection from {address}: {exception.Message}");
            }
            catch (Exception exception) when (exception is IOException || exception is SocketException ||
                                              exception is ObjectDisposedException || exception is InvalidDataException)
            {
                // Dropped, timed out or malformed: nothing to answer.
            }
            catch (Exception exception)
            {
                log($"Error while serving {address}: {exception.GetType().Name}: {exception.Message}");
            }
        }

        void Pair(Stream stream, Hello hello, string code, byte[] clientNonce, byte[] serverNonce, string address)
        {
            byte[] key = SecureChannel.PairingKey(code, clientNonce, serverNonce, options.pairingIterations);
            var channel = SecureChannel.ForServer(stream, key, clientNonce, serverNonce);
            Envelope request;
            try
            {
                request = channel.Receive(out _);
            }
            catch (ChannelAuthenticationException)
            {
                RecordPairingFailure(address);
                return;
            }
            if (request == null || request.type != MessageType.Pair) return;

            lock (gate)
            {
                if (pairingCode != code)
                {
                    channel.Send(MessageType.Error, new ErrorReply { code = WorkerProtocol.ErrorPairingClosed, message = "Pairing closed while this PC was connecting. Open it again on the worker." });
                    return;
                }
                pairingCode = null; // one PC per code
            }

            var body = Body<PairRequest>(request);
            string name = WorkerProtocol.CleanName(body.clientName ?? hello.clientName, "A PC");
            byte[] longKey = SecureChannel.RandomBytes(SecureChannel.KeyBytes);
            if (!options.secrets.Write(KeyName(hello.clientId), longKey, out string problem))
            {
                channel.Send(MessageType.Error, new ErrorReply { code = "store", message = "The worker could not save the pairing key: " + problem });
                log($"Pairing with {name} failed: {problem}");
                return;
            }

            lock (gate)
            {
                paired.RemoveAll(p => p.clientId == hello.clientId);
                paired.Add(new PairedClient
                {
                    clientId = hello.clientId,
                    name = name,
                    pairedUtc = DateTime.UtcNow.ToString("o"),
                    lastSeenUtc = DateTime.UtcNow.ToString("o"),
                    lastAddress = address,
                });
                SavePaired();
            }
            channel.Send(MessageType.Paired, new PairReply { workerId = WorkerId, workerName = options.workerName, key = Convert.ToBase64String(longKey) });
            log($"Paired with {name} ({address}).");
        }

        void RecordPairingFailure(string address)
        {
            lock (gate)
            {
                pairingFailures++;
                log($"A wrong pairing code was tried from {address} ({pairingFailures} of {WorkerProtocol.MaxPairingFailures}).");
                if (pairingFailures < WorkerProtocol.MaxPairingFailures || pairingCode == null) return;
                pairingCode = null;
                log("Pairing closed after too many wrong codes. Open it again to pair a PC.");
            }
        }

        void Session(Stream stream, Hello hello, byte[] secret, byte[] clientNonce, byte[] serverNonce, string address)
        {
            var channel = SecureChannel.ForServer(stream, secret, clientNonce, serverNonce);
            var first = channel.Receive(out _);
            if (first == null || first.type != MessageType.Auth) return;

            Touch(hello.clientId, address);
            string clientName = NameOf(hello.clientId);
            channel.Send(MessageType.Info, BuildInfo());

            while (!stopping)
            {
                var request = channel.Receive(out byte[] payload);
                if (request == null) return;
                if (!IsPaired(hello.clientId))
                {
                    channel.Send(MessageType.Error, new ErrorReply { code = WorkerProtocol.ErrorNotPaired, message = "This PC was removed from the worker." });
                    return;
                }
                if (!Handle(channel, hello.clientId, clientName, request, payload)) return;
            }
        }

        /// <summary>Answers one request. Every request is answered from the asking client's own jobs only.</summary>
        bool Handle(SecureChannel channel, string clientId, string clientName, Envelope request, byte[] payload)
        {
            var jobs = options.jobs;
            switch (request.type)
            {
                case MessageType.Info:
                    channel.Send(MessageType.Info, BuildInfo());
                    return true;

                case MessageType.Submit:
                {
                    var submit = Body<SubmitRequest>(request);
                    string error = jobs.Submit(clientId, submit, payload, clientName);
                    if (error != null) SendError(channel, "refused", error);
                    else channel.Send(MessageType.Submitted, new JobRef { jobId = submit.jobId });
                    return true;
                }

                case MessageType.Status:
                    channel.Send(MessageType.StatusReply, jobs.Status(clientId, Body<JobRef>(request).jobId));
                    return true;

                case MessageType.List:
                {
                    var list = Body<FileListRequest>(request);
                    var files = jobs.ListFiles(clientId, list.jobId, list.shardIndex, out string error);
                    if (error != null) SendError(channel, "not-found", error);
                    else channel.Send(MessageType.Files, files);
                    return true;
                }

                case MessageType.Get:
                {
                    byte[] data = jobs.ReadChunk(clientId, Body<FileRequest>(request), out FileChunk chunk, out string error);
                    if (error != null) SendError(channel, "not-found", error);
                    else channel.Send(MessageType.Chunk, chunk, data);
                    return true;
                }

                case MessageType.Cancel:
                {
                    string error = jobs.Cancel(clientId, Body<JobRef>(request).jobId);
                    if (error != null) SendError(channel, "not-found", error);
                    else channel.Send(MessageType.Ok, new Empty());
                    return true;
                }

                case MessageType.Release:
                {
                    string error = jobs.Release(clientId, Body<JobRef>(request).jobId);
                    if (error != null) SendError(channel, "not-found", error);
                    else channel.Send(MessageType.Ok, new Empty());
                    return true;
                }

                case MessageType.Unpair:
                    Unpair(clientId);
                    channel.Send(MessageType.Ok, new Empty());
                    return false;

                default:
                    SendError(channel, "unknown", $"Unknown request '{request.type}'.");
                    return true;
            }
        }

        static void SendError(SecureChannel channel, string code, string message) =>
            channel.Send(MessageType.Error, new ErrorReply { code = code, message = message });

        static T Body<T>(Envelope envelope) where T : class, new()
        {
            try { return JsonUtility.FromJson<T>(envelope.body ?? "{}") ?? new T(); }
            catch (Exception) { return new T(); }
        }

        WorkerInfo BuildInfo()
        {
            var (running, queued) = options.jobs.Load();
            return new WorkerInfo
            {
                workerId = WorkerId,
                workerName = options.workerName,
                appVersion = options.appVersion,
                buildId = options.buildId,
                platform = options.platform,
                processorCount = Environment.ProcessorCount,
                maxProcesses = options.jobs.MaxProcesses,
                runningProcesses = running,
                queuedShards = queued,
            };
        }

        void Touch(string clientId, string address)
        {
            lock (gate)
            {
                var client = paired.FirstOrDefault(p => p.clientId == clientId);
                if (client == null) return;
                client.lastSeenUtc = DateTime.UtcNow.ToString("o");
                client.lastAddress = address;
                SavePaired();
            }
        }

        // ---------------------------------------------------------------- storage

        string LoadOrCreateId()
        {
            string path = Path.Combine(options.dataFolder, IdFileName);
            try
            {
                if (File.Exists(path))
                {
                    string id = File.ReadAllText(path).Trim();
                    if (WorkerProtocol.IsValidId(id)) return id;
                }
            }
            catch (Exception) { }
            string fresh = Guid.NewGuid().ToString("N");
            File.WriteAllText(path, fresh);
            return fresh;
        }

        List<PairedClient> LoadPaired()
        {
            try
            {
                string path = Path.Combine(options.dataFolder, PairedFileName);
                if (!File.Exists(path)) return new List<PairedClient>();
                var list = JsonUtility.FromJson<PairedClientList>(File.ReadAllText(path));
                return (list?.clients ?? new List<PairedClient>()).Where(c => c != null && WorkerProtocol.IsValidId(c.clientId)).ToList();
            }
            catch (Exception exception)
            {
                log($"Could not read the list of paired PCs: {exception.Message}");
                return new List<PairedClient>();
            }
        }

        void SavePaired()
        {
            try
            {
                string path = Path.Combine(options.dataFolder, PairedFileName);
                File.WriteAllText(path + ".tmp", JsonUtility.ToJson(new PairedClientList { clients = paired }, true));
                if (File.Exists(path)) File.Delete(path);
                File.Move(path + ".tmp", path);
            }
            catch (Exception exception)
            {
                log($"Could not save the list of paired PCs: {exception.Message}");
            }
        }

        static byte[] FromBase64(string text)
        {
            if (string.IsNullOrEmpty(text) || text.Length > 128) return null;
            try { return Convert.FromBase64String(text); }
            catch (Exception) { return null; }
        }

        /// <summary>This computer's IPv4 addresses on the networks it is connected to, for typing into another PC.</summary>
        public static List<string> LocalAddresses()
        {
            var addresses = new List<string>();
            try
            {
                foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (adapter.OperationalStatus != OperationalStatus.Up || adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    foreach (var unicast in adapter.GetIPProperties().UnicastAddresses)
                    {
                        var ip = unicast.Address;
                        if (ip.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(ip)) continue;
                        string text = ip.ToString();
                        if (text.StartsWith("169.254.", StringComparison.Ordinal)) continue; // no network (self-assigned)
                        if (!addresses.Contains(text)) addresses.Add(text);
                    }
                }
            }
            catch (Exception) { }
            return addresses;
        }
    }
}
