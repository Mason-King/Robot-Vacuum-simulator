using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace RobotVacuum.Network
{
    /// <summary>
    /// The network worker's wire format (requirement 3.9: two independently executable components talking over a
    /// defined interface, across a network boundary). The full specification is cloud/docs/worker-protocol.md.
    ///
    /// Every message is a frame: a 4-byte big-endian length, then that many bytes. The first frame each way is a plain
    /// JSON handshake (<see cref="Hello"/>, <see cref="Welcome"/>) that carries only random nonces and public facts;
    /// everything after it goes through a <see cref="SecureChannel"/>, so it is encrypted and authenticated with a key
    /// that never crosses the network.
    /// </summary>
    public static class WorkerProtocol
    {
        public const int Version = 1;
        public const int DefaultPort = 47815;

        public const int MaxFrameBytes = 16 * 1024 * 1024;
        public const int MaxHandshakeBytes = 4096;
        public const int MaxChunkBytes = 4 * 1024 * 1024;
        public const int MaxJobBytes = 12 * 1024 * 1024;
        public const int MaxResumeBytes = 8 * 1024 * 1024;
        public const int MaxShardCount = 1024;
        public const int MaxConnections = 16;
        public const int IdleTimeoutMs = 120_000;
        public const int ConnectTimeoutMs = 8_000;

        /// <summary>How long a pairing window stays open, and how many wrong codes close it early.</summary>
        public const int PairingMinutes = 10;
        public const int MaxPairingFailures = 5;

        public const string ModePair = "pair";
        public const string ModeSession = "session";

        // Handshake errors (Welcome.error).
        public const string ErrorVersion = "version";
        public const string ErrorProtocol = "protocol";
        public const string ErrorPairingClosed = "pairing-closed";
        public const string ErrorNotPaired = "not-paired";
        public const string ErrorBusy = "busy";
        public const string ErrorBadHello = "bad-hello";

        static readonly Regex IdPattern = new Regex("^[A-Za-z0-9][A-Za-z0-9._-]{0,79}$", RegexOptions.CultureInvariant);

        /// <summary>Job and client ids become folder names on the worker, so only plain names are accepted (4.8).</summary>
        public static bool IsValidId(string id) => !string.IsNullOrEmpty(id) && IdPattern.IsMatch(id) && !id.Contains("..");

        public static void WriteFrame(Stream stream, byte[] data)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (data.Length > MaxFrameBytes) throw new InvalidDataException($"A message of {data.Length} bytes is over the {MaxFrameBytes}-byte limit.");
            var header = new byte[4];
            WriteInt32(header, 0, data.Length);
            stream.Write(header, 0, 4);
            stream.Write(data, 0, data.Length);
            stream.Flush();
        }

        /// <summary>Reads one frame, or returns null if the other side closed the connection cleanly between frames.</summary>
        public static byte[] ReadFrame(Stream stream, int maxBytes = MaxFrameBytes)
        {
            var header = new byte[4];
            int first = stream.Read(header, 0, 4);
            if (first == 0) return null;
            if (first < 4) ReadExactly(stream, header, first, 4 - first);
            int length = ReadInt32(header, 0);
            if (length < 0 || length > maxBytes) throw new InvalidDataException($"A message of {length} bytes is over the {maxBytes}-byte limit.");
            var data = new byte[length];
            ReadExactly(stream, data, 0, length);
            return data;
        }

        static void ReadExactly(Stream stream, byte[] buffer, int offset, int count)
        {
            while (count > 0)
            {
                int read = stream.Read(buffer, offset, count);
                if (read <= 0) throw new EndOfStreamException("The connection closed in the middle of a message.");
                offset += read;
                count -= read;
            }
        }

        public static void WriteInt32(byte[] buffer, int offset, int value)
        {
            buffer[offset] = (byte)(value >> 24);
            buffer[offset + 1] = (byte)(value >> 16);
            buffer[offset + 2] = (byte)(value >> 8);
            buffer[offset + 3] = (byte)value;
        }

        public static int ReadInt32(byte[] buffer, int offset) =>
            (buffer[offset] << 24) | (buffer[offset + 1] << 16) | (buffer[offset + 2] << 8) | buffer[offset + 3];

        public static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text ?? "");

        public static string Utf8(byte[] bytes) => bytes == null ? "" : Encoding.UTF8.GetString(bytes);

        /// <summary>A name shown to people (a PC or campaign name): printable, at most 64 characters.</summary>
        public static string CleanName(string name, string fallback)
        {
            var builder = new StringBuilder();
            foreach (char c in name ?? "")
            {
                if (builder.Length >= 64) break;
                if (!char.IsControl(c)) builder.Append(c);
            }
            string clean = builder.ToString().Trim();
            return clean.Length > 0 ? clean : fallback;
        }

        /// <summary>Parses "host", "host:port" or "[ipv6]:port". Returns false with a plain-language problem.</summary>
        public static bool TryParseAddress(string text, out string host, out int port, out string problem)
        {
            host = null;
            port = DefaultPort;
            problem = null;
            string value = (text ?? "").Trim();
            if (value.Length == 0)
            {
                problem = "Enter the worker's address, for example 192.168.1.20 or lab-pc-3.";
                return false;
            }

            string portText = null;
            if (value.StartsWith("[", StringComparison.Ordinal))
            {
                int close = value.IndexOf(']');
                if (close < 0) { problem = "An IPv6 address needs a closing ']'."; return false; }
                host = value.Substring(1, close - 1);
                if (close + 1 < value.Length)
                {
                    if (value[close + 1] != ':') { problem = "Write the port after the address as :47815."; return false; }
                    portText = value.Substring(close + 2);
                }
            }
            else
            {
                int colon = value.LastIndexOf(':');
                if (colon > 0 && value.IndexOf(':') == colon)
                {
                    host = value.Substring(0, colon);
                    portText = value.Substring(colon + 1);
                }
                else host = value;
            }

            if (portText != null && (!int.TryParse(portText, out port) || port < 1 || port > 65535))
            {
                problem = $"'{portText}' is not a port number (1-65535).";
                return false;
            }
            if (string.IsNullOrWhiteSpace(host) || host.IndexOfAny(new[] { ' ', '/', '\\' }) >= 0)
            {
                problem = $"'{host}' is not a computer name or IP address.";
                return false;
            }
            return true;
        }
    }

    /// <summary>Message type names inside the secure channel.</summary>
    public static class MessageType
    {
        public const string Auth = "auth";
        public const string Info = "info";
        public const string Pair = "pair";
        public const string Paired = "paired";
        public const string Submit = "submit";
        public const string Submitted = "submitted";
        public const string Status = "status";
        public const string StatusReply = "status-reply";
        public const string List = "list";
        public const string Files = "files";
        public const string Get = "get";
        public const string Chunk = "chunk";
        public const string Cancel = "cancel";
        public const string Release = "release";
        public const string Unpair = "unpair";
        public const string Ok = "ok";
        public const string Error = "error";
    }

    /// <summary>States a job and its shards go through on a worker.</summary>
    public static class WorkState
    {
        public const string Validating = "validating";
        public const string Queued = "queued";
        public const string Running = "running";
        public const string Finished = "finished";
        public const string Failed = "failed";
        public const string Rejected = "rejected";
        public const string Cancelled = "cancelled";

        public static bool IsFinal(string state) =>
            state == Finished || state == Failed || state == Rejected || state == Cancelled;
    }

    // ---------------------------------------------------------------- handshake (plain JSON)

    [Serializable]
    public sealed class Hello
    {
        public int protocol;
        public string mode;          // "pair" or "session"
        public string clientId;
        public string clientName;
        public string clientNonce;   // base64, 32 random bytes
        public string appVersion;
    }

    [Serializable]
    public sealed class Welcome
    {
        public int protocol;
        public string workerId;
        public string workerName;
        public string appVersion;
        public string buildId;
        public int jobSchemaVersion;
        public string serverNonce;   // base64, 32 random bytes
        public string error;         // empty, or one of WorkerProtocol.Error*
    }

    // ---------------------------------------------------------------- inside the secure channel

    /// <summary>Every message: its type and its body (the JSON of one of the classes below). Binary data follows it.</summary>
    [Serializable]
    public sealed class Envelope
    {
        public string type;
        public string body;
    }

    [Serializable]
    public sealed class WorkerInfo
    {
        public string workerId;
        public string workerName;
        public string appVersion;
        public string buildId;
        public string platform;
        public int processorCount;
        public int maxProcesses;
        public int runningProcesses;
        public int queuedShards;
    }

    [Serializable]
    public sealed class PairRequest
    {
        public string clientName;
    }

    [Serializable]
    public sealed class PairReply
    {
        public string workerId;
        public string workerName;
        public string key; // base64, 32 bytes: the long-term key for this client
    }

    /// <summary>Run some shards of a job. The job file itself (a headless job, docs/job-format.md) follows as UTF-8 bytes.</summary>
    [Serializable]
    public sealed class SubmitRequest
    {
        public string jobId;
        public string campaignName;
        public int shardCount;
        public List<int> shards = new List<int>();
        public List<ShardResume> resume = new List<ShardResume>();
    }

    /// <summary>The results.json a shard already produced elsewhere, so this worker continues it instead of starting over (4.5).</summary>
    [Serializable]
    public sealed class ShardResume
    {
        public int shardIndex;
        public string resultsJson;
    }

    [Serializable]
    public sealed class JobRef
    {
        public string jobId;
    }

    [Serializable]
    public sealed class StatusReply
    {
        public List<JobStatus> jobs = new List<JobStatus>();
    }

    [Serializable]
    public sealed class JobStatus
    {
        public string jobId;
        public string campaignName;
        public string state;
        public string submittedUtc;
        public List<string> errors = new List<string>();
        public List<ShardStatus> shards = new List<ShardStatus>();
    }

    [Serializable]
    public sealed class ShardStatus
    {
        public int shardIndex;
        public string state;
        public int totalRuns;
        public int completedRuns;
        public int failedRuns;
        public int exitCode = -1;
        public string message;
        public string updatedUtc;
    }

    [Serializable]
    public sealed class FileListRequest
    {
        public string jobId;
        public int shardIndex;
    }

    [Serializable]
    public sealed class FileList
    {
        public List<FileEntry> files = new List<FileEntry>();
    }

    [Serializable]
    public sealed class FileEntry
    {
        public string path;  // relative to the shard folder, '/' separated
        public long size;
    }

    [Serializable]
    public sealed class FileRequest
    {
        public string jobId;
        public int shardIndex;
        public string path;
        public long offset;
        public int length;
    }

    /// <summary>Part of a file; the bytes follow.</summary>
    [Serializable]
    public sealed class FileChunk
    {
        public string path;
        public long offset;
        public long totalSize;
    }

    [Serializable]
    public sealed class ErrorReply
    {
        public string code;
        public string message;
    }

    [Serializable]
    public sealed class Empty
    {
    }
}
