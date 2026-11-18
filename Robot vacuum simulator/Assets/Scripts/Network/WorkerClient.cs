using System;
using System.IO;
using System.Net.Sockets;
using UnityEngine;

namespace RobotVacuum.Network
{
    /// <summary>A refusal or failure, in words the researcher can act on (C.2).</summary>
    public sealed class WorkerException : Exception
    {
        public string Code { get; }
        public WorkerException(string message, string code = null, Exception inner = null) : base(message, inner) => Code = code;
    }

    /// <summary>What pairing gives this PC: the worker's identity and the long-term key (keep it in the secret store).</summary>
    public sealed class PairingResult
    {
        public string workerId;
        public string workerName;
        public string appVersion;
        public string buildId;
        public byte[] key;
    }

    /// <summary>
    /// The researcher's side of the worker protocol (3.9): pair with a worker once, then connect with the saved key to
    /// send jobs, follow them and download their results. Not thread-safe: one request at a time per connection.
    /// </summary>
    public sealed class WorkerClient : IDisposable
    {
        readonly TcpClient tcp;
        readonly SecureChannel channel;

        public WorkerInfo Info { get; private set; }
        public Welcome Welcome { get; }
        public string Host { get; }
        public int Port { get; }

        WorkerClient(TcpClient tcp, SecureChannel channel, Welcome welcome, string host, int port)
        {
            this.tcp = tcp;
            this.channel = channel;
            Welcome = welcome;
            Host = host;
            Port = port;
        }

        public void Dispose()
        {
            try { tcp.Close(); }
            catch (Exception) { }
        }

        // ---------------------------------------------------------------- pairing and connecting

        /// <summary>Pairs this PC with a worker using the code shown on the worker. Throws <see cref="WorkerException"/>.</summary>
        public static PairingResult Pair(string host, int port, string pairingCode, string clientId, string clientName,
                                         string appVersion, int iterations = SecureChannel.PairingIterations)
        {
            string code = SecureChannel.NormalizePairingCode(pairingCode);
            if (code.Length != SecureChannel.PairingCodeLength)
                throw new WorkerException($"A pairing code has {SecureChannel.PairingCodeLength} letters and digits, like ABCDE-FGHJK. Copy it from the worker's Network Settings page or its window.");

            using (var tcp = Open(host, port))
            {
                var stream = tcp.GetStream();
                byte[] clientNonce = SecureChannel.RandomBytes(SecureChannel.NonceBytes);
                var welcome = Handshake(stream, host, port, WorkerProtocol.ModePair, clientId, clientName, clientNonce, appVersion);
                byte[] serverNonce = Convert.FromBase64String(welcome.serverNonce);
                var channel = SecureChannel.ForClient(stream, SecureChannel.PairingKey(code, clientNonce, serverNonce, iterations), clientNonce, serverNonce);
                try
                {
                    channel.Send(MessageType.Pair, new PairRequest { clientName = clientName });
                    var reply = channel.Receive(out _);
                    if (reply == null) throw new EndOfStreamException();
                    if (reply.type == MessageType.Error) throw Refusal(reply);
                    if (reply.type != MessageType.Paired) throw new WorkerException("The worker answered pairing with something unexpected.");
                    var paired = JsonUtility.FromJson<PairReply>(reply.body);
                    byte[] key = Convert.FromBase64String(paired.key ?? "");
                    if (key.Length != SecureChannel.KeyBytes) throw new WorkerException("The worker sent a key of the wrong size.");
                    return new PairingResult
                    {
                        workerId = paired.workerId, workerName = paired.workerName, appVersion = welcome.appVersion, buildId = welcome.buildId, key = key,
                    };
                }
                catch (Exception exception) when (exception is IOException || exception is ChannelAuthenticationException)
                {
                    // A worker that can't read our message (wrong code) just closes the connection.
                    throw new WorkerException("The pairing code was not accepted. Check the code shown on the worker: it changes each time pairing is opened, and five wrong tries close pairing.", "wrong-code", exception);
                }
            }
        }

        /// <summary>Connects with the key saved at pairing. Throws <see cref="WorkerException"/>.</summary>
        public static WorkerClient Connect(string host, int port, string clientId, byte[] key, string appVersion)
        {
            if (key == null) throw new WorkerException("This PC has no key for that worker. Pair with it again.", WorkerProtocol.ErrorNotPaired);
            TcpClient tcp = Open(host, port);
            try
            {
                var stream = tcp.GetStream();
                byte[] clientNonce = SecureChannel.RandomBytes(SecureChannel.NonceBytes);
                var welcome = Handshake(stream, host, port, WorkerProtocol.ModeSession, clientId, null, clientNonce, appVersion);
                byte[] serverNonce = Convert.FromBase64String(welcome.serverNonce);
                var channel = SecureChannel.ForClient(stream, key, clientNonce, serverNonce);
                var client = new WorkerClient(tcp, channel, welcome, host, port);
                try
                {
                    channel.Send(MessageType.Auth, new Empty());
                    var reply = channel.Receive(out _);
                    if (reply == null) throw new EndOfStreamException();
                    if (reply.type == MessageType.Error) throw Refusal(reply);
                    client.Info = JsonUtility.FromJson<WorkerInfo>(reply.body);
                }
                catch (Exception exception) when (exception is IOException || exception is ChannelAuthenticationException)
                {
                    throw new WorkerException("The worker did not accept this PC's key. Remove the worker here and pair with it again.", WorkerProtocol.ErrorNotPaired, exception);
                }
                return client;
            }
            catch
            {
                tcp.Close();
                throw;
            }
        }

        static TcpClient Open(string host, int port)
        {
            var tcp = new TcpClient { NoDelay = true, ReceiveTimeout = WorkerProtocol.IdleTimeoutMs, SendTimeout = 30_000 };
            try
            {
                var connecting = tcp.BeginConnect(host, port, null, null);
                if (!connecting.AsyncWaitHandle.WaitOne(WorkerProtocol.ConnectTimeoutMs))
                    throw new WorkerException($"{host}:{port} did not answer. Check the address, that the worker is switched on and on the same network, and that its firewall allows the simulator.", "unreachable");
                tcp.EndConnect(connecting);
                return tcp;
            }
            catch (WorkerException)
            {
                tcp.Close();
                throw;
            }
            catch (Exception exception)
            {
                tcp.Close();
                throw new WorkerException($"Could not reach {host}:{port} ({exception.Message}). Check the address, that the worker is switched on, and that its firewall allows the simulator.", "unreachable", exception);
            }
        }

        static Welcome Handshake(Stream stream, string host, int port, string mode, string clientId, string clientName, byte[] clientNonce, string appVersion)
        {
            var hello = new Hello
            {
                protocol = WorkerProtocol.Version,
                mode = mode,
                clientId = clientId,
                clientName = clientName,
                clientNonce = Convert.ToBase64String(clientNonce),
                appVersion = appVersion,
            };
            Welcome welcome;
            try
            {
                WorkerProtocol.WriteFrame(stream, WorkerProtocol.Utf8(JsonUtility.ToJson(hello)));
                byte[] reply = WorkerProtocol.ReadFrame(stream, WorkerProtocol.MaxHandshakeBytes);
                if (reply == null) throw new EndOfStreamException();
                welcome = JsonUtility.FromJson<Welcome>(WorkerProtocol.Utf8(reply));
            }
            catch (Exception exception) when (!(exception is WorkerException))
            {
                throw new WorkerException($"{host}:{port} is not a simulator worker, or it closed the connection.", "not-a-worker", exception);
            }

            switch (welcome?.error ?? "")
            {
                case "":
                    break;
                case WorkerProtocol.ErrorVersion:
                    throw new WorkerException($"The worker runs simulator version {welcome.appVersion} and this PC runs {appVersion}. Install the same version on both, so a run gives the same result wherever it runs (2.1).", welcome.error);
                case WorkerProtocol.ErrorPairingClosed:
                    throw new WorkerException("That worker isn't accepting new PCs right now. On the worker, choose 'Allow a new PC to pair', then enter the new code here within 10 minutes.", welcome.error);
                case WorkerProtocol.ErrorNotPaired:
                    throw new WorkerException("This PC is not paired with that worker (it may have been removed there). Pair with it again.", welcome.error);
                case WorkerProtocol.ErrorProtocol:
                    throw new WorkerException("The worker uses a different version of the worker protocol. Install the same simulator version on both.", welcome.error);
                default:
                    throw new WorkerException($"The worker refused the connection ({welcome.error}).", welcome.error);
            }
            if (string.IsNullOrEmpty(welcome.serverNonce)) throw new WorkerException("The worker's reply was incomplete.");
            return welcome;
        }

        static WorkerException Refusal(Envelope reply)
        {
            var error = JsonUtility.FromJson<ErrorReply>(reply.body);
            return new WorkerException(string.IsNullOrEmpty(error?.message) ? "The worker refused the request." : error.message, error?.code);
        }

        // ---------------------------------------------------------------- requests

        T Request<T>(string type, object body, string expected, out byte[] payload, byte[] send = null)
        {
            try
            {
                channel.Send(type, body, send);
                var reply = channel.Receive(out payload);
                if (reply == null) throw new WorkerException("The worker closed the connection.", "closed");
                if (reply.type == MessageType.Error) throw Refusal(reply);
                if (reply.type != expected) throw new WorkerException($"The worker answered '{reply.type}' to '{type}'.");
                return JsonUtility.FromJson<T>(reply.body);
            }
            catch (Exception exception) when (exception is IOException || exception is SocketException || exception is ObjectDisposedException)
            {
                throw new WorkerException($"The connection to the worker was lost ({exception.Message}).", "connection", exception);
            }
        }

        public WorkerInfo RefreshInfo() => Info = Request<WorkerInfo>(MessageType.Info, new Empty(), MessageType.Info, out _);

        /// <summary>Sends shards of a job; the job file goes as UTF-8 bytes.</summary>
        public void Submit(SubmitRequest request, byte[] jobBytes) =>
            Request<JobRef>(MessageType.Submit, request, MessageType.Submitted, out _, jobBytes);

        public StatusReply Status(string jobId = null) =>
            Request<StatusReply>(MessageType.Status, new JobRef { jobId = jobId }, MessageType.StatusReply, out _);

        public FileList ListFiles(string jobId, int shardIndex) =>
            Request<FileList>(MessageType.List, new FileListRequest { jobId = jobId, shardIndex = shardIndex }, MessageType.Files, out _);

        public byte[] ReadChunk(string jobId, int shardIndex, string path, long offset, int length, out long totalSize)
        {
            var chunk = Request<FileChunk>(MessageType.Get,
                new FileRequest { jobId = jobId, shardIndex = shardIndex, path = path, offset = offset, length = length },
                MessageType.Chunk, out byte[] data);
            totalSize = chunk.totalSize;
            return data ?? Array.Empty<byte>();
        }

        /// <summary>Downloads one whole file of a shard to <paramref name="localPath"/> (written to a temp file, then moved).</summary>
        public void Download(string jobId, int shardIndex, string path, string localPath)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(localPath) ?? ".");
            string temp = localPath + ".part";
            using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write))
            {
                long offset = 0, total;
                do
                {
                    byte[] data = ReadChunk(jobId, shardIndex, path, offset, WorkerProtocol.MaxChunkBytes, out total);
                    if (data.Length == 0 && offset < total) throw new WorkerException($"{path} stopped short on the worker.");
                    file.Write(data, 0, data.Length);
                    offset += data.Length;
                } while (offset < total);
            }
            if (File.Exists(localPath)) File.Delete(localPath);
            File.Move(temp, localPath);
        }

        public void Cancel(string jobId) => Request<Empty>(MessageType.Cancel, new JobRef { jobId = jobId }, MessageType.Ok, out _);

        public void Release(string jobId) => Request<Empty>(MessageType.Release, new JobRef { jobId = jobId }, MessageType.Ok, out _);

        /// <summary>Asks the worker to forget this PC (and delete what it sent). The caller deletes the saved key.</summary>
        public void Unpair() => Request<Empty>(MessageType.Unpair, new Empty(), MessageType.Ok, out _);
    }
}
