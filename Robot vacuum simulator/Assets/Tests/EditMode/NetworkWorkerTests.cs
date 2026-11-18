using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using NUnit.Framework;
using RobotVacuum.Headless;
using RobotVacuum.Network;
using UnityEngine;

namespace RobotVacuum.Tests
{
    /// <summary>
    /// The network worker (requirement 3.9) and its security (4.8, C.12): the secure channel, pairing, the protocol
    /// end to end over a real loopback TCP connection, per-client isolation, and the job lifecycle. Simulations are
    /// replaced by a fake launcher, so no Unity process is started.
    /// </summary>
    public class NetworkWorkerTests
    {
        const string Version = "test-1.0";
        const int FastIterations = 10; // the real 100,000 PBKDF2 rounds are only slow, not different

        // ---------------------------------------------------------------- the secure channel

        static (SecureChannel client, SecureChannel server) Pair(byte[] key = null)
        {
            key = key ?? SecureChannel.RandomBytes(32);
            byte[] cn = SecureChannel.RandomBytes(32), sn = SecureChannel.RandomBytes(32);
            return (SecureChannel.ForClient(Stream.Null, key, cn, sn), SecureChannel.ForServer(Stream.Null, key, cn, sn));
        }

        [Test]
        public void Channel_RoundTripsBothWays()
        {
            var (client, server) = Pair();
            byte[] message = Encoding.UTF8.GetBytes("hello worker");
            CollectionAssert.AreEqual(message, server.Open(client.Seal(message)));
            byte[] reply = Encoding.UTF8.GetBytes("hello client");
            CollectionAssert.AreEqual(reply, client.Open(server.Seal(reply)));
        }

        [Test]
        public void Channel_SameMessageTwice_LooksDifferent()
        {
            var (client, _) = Pair();
            byte[] message = Encoding.UTF8.GetBytes("same");
            CollectionAssert.AreNotEqual(client.Seal(message), client.Seal(message), "random IV and a counter in the MAC");
        }

        [Test]
        public void Channel_ATamperedMessage_IsRejected()
        {
            var (client, server) = Pair();
            byte[] frame = client.Seal(Encoding.UTF8.GetBytes("submit job"));
            frame[20] ^= 1;
            Assert.Throws<ChannelAuthenticationException>(() => server.Open(frame));
        }

        [Test]
        public void Channel_AReplayedMessage_IsRejected()
        {
            var (client, server) = Pair();
            byte[] frame = client.Seal(Encoding.UTF8.GetBytes("cancel"));
            server.Open(frame);
            Assert.Throws<ChannelAuthenticationException>(() => server.Open(frame));
        }

        [Test]
        public void Channel_MessagesOutOfOrder_AreRejected()
        {
            var (client, server) = Pair();
            byte[] first = client.Seal(Encoding.UTF8.GetBytes("1"));
            byte[] second = client.Seal(Encoding.UTF8.GetBytes("2"));
            Assert.Throws<ChannelAuthenticationException>(() => server.Open(second));
            Assert.DoesNotThrow(() => server.Open(first));
        }

        [Test]
        public void Channel_WrongKey_IsRejected()
        {
            byte[] cn = SecureChannel.RandomBytes(32), sn = SecureChannel.RandomBytes(32);
            var client = SecureChannel.ForClient(Stream.Null, SecureChannel.RandomBytes(32), cn, sn);
            var server = SecureChannel.ForServer(Stream.Null, SecureChannel.RandomBytes(32), cn, sn);
            Assert.Throws<ChannelAuthenticationException>(() => server.Open(client.Seal(new byte[] { 1, 2, 3 })));
        }

        [Test]
        public void Channel_AMessageReflectedToItsSender_IsRejected()
        {
            var (client, _) = Pair();
            Assert.Throws<ChannelAuthenticationException>(() => client.Open(client.Seal(new byte[] { 9 })), "each direction has its own keys");
        }

        [Test]
        public void Pbkdf2_MatchesTheStandardTestVectors()
        {
            byte[] password = Encoding.ASCII.GetBytes("password"), salt = Encoding.ASCII.GetBytes("salt");
            Assert.AreEqual("120fb6cffcf8b32c43e7225256c4f837a86548c92ccc35480805987cb70be17b", Hex(SecureChannel.Pbkdf2(password, salt, 1, 32)));
            Assert.AreEqual("ae4d0c95af6b46d32d0adff928f06dd02a303f8ef3c251dfd6e2d85a95474c43", Hex(SecureChannel.Pbkdf2(password, salt, 2, 32)));
            Assert.AreEqual("c5e478d59288c841aa530db6845c4c8d962893a001ce4e11a4963873aa98134a", Hex(SecureChannel.Pbkdf2(password, salt, 4096, 32)));
        }

        static string Hex(byte[] bytes) => BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();

        [Test]
        public void PairingCodes_AreRandom_Readable_AndForgivingToType()
        {
            var codes = Enumerable.Range(0, 50).Select(_ => SecureChannel.NewPairingCode()).ToList();
            Assert.AreEqual(50, codes.Distinct().Count());
            foreach (string code in codes)
            {
                Assert.AreEqual(SecureChannel.PairingCodeLength, code.Length);
                Assert.That(code.All(c => SecureChannel.PairingAlphabet.IndexOf(c) >= 0), code);
            }
            string formatted = SecureChannel.FormatPairingCode(codes[0]);
            Assert.AreEqual(11, formatted.Length);
            Assert.AreEqual('-', formatted[5]);
            Assert.AreEqual(codes[0], SecureChannel.NormalizePairingCode(" " + formatted.ToLowerInvariant().Replace("-", " - ") + " "));
        }

        // ---------------------------------------------------------------- small rules

        [TestCase("192.168.1.20", "192.168.1.20", WorkerProtocol.DefaultPort)]
        [TestCase("lab-pc-3:5000", "lab-pc-3", 5000)]
        [TestCase(" [::1]:47816 ", "::1", 47816)]
        [TestCase("[fe80::1]", "fe80::1", WorkerProtocol.DefaultPort)]
        public void Addresses_Parse(string text, string host, int port)
        {
            Assert.IsTrue(WorkerProtocol.TryParseAddress(text, out string h, out int p, out string problem), problem);
            Assert.AreEqual(host, h);
            Assert.AreEqual(port, p);
        }

        [TestCase("")]
        [TestCase("lab-pc:99999")]
        [TestCase("lab-pc:port")]
        [TestCase("lab pc")]
        [TestCase("http://lab-pc")]
        public void Addresses_BadOnesExplainWhy(string text)
        {
            Assert.IsFalse(WorkerProtocol.TryParseAddress(text, out _, out _, out string problem));
            Assert.IsNotEmpty(problem);
        }

        [Test]
        public void Ids_OnlyPlainNames()
        {
            Assert.IsTrue(WorkerProtocol.IsValidId("campaign-2026.11_18"));
            foreach (string bad in new[] { "", "..", "../x", "a/b", "a\\b", "C:x", ".hidden", new string('a', 81), "a..b" })
                Assert.IsFalse(WorkerProtocol.IsValidId(bad), bad);
        }

        [Test]
        public void FilePaths_StayInsideTheShardFolder()
        {
            string folder = NewFolder();
            Directory.CreateDirectory(Path.Combine(folder, "runs", "abc"));
            File.WriteAllText(Path.Combine(folder, "runs", "abc", "result.json"), "{}");
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(folder), "secret.txt"), "no");

            Assert.IsTrue(WorkerJobs.TryResolveInside(folder, "runs/abc/result.json", out _));
            foreach (string bad in new[] { "../secret.txt", "runs/../../secret.txt", "/etc/passwd", "C:\\Windows\\win.ini", "runs\\..\\..\\secret.txt", "runs/missing.json", "" })
                Assert.IsFalse(WorkerJobs.TryResolveInside(folder, bad, out _), bad);
        }

        [Test]
        public void HeadlessArgs_ReadTheWorkerFlags()
        {
            var args = HeadlessArgs.Parse(new[] { "sim.exe", "-batchmode", "-nographics", "-worker", "-workerPort", "5000", "-workerProcesses", "3", "-workerPairMinutes", "0", "-workerParentPid", "1234" });
            Assert.IsTrue(args.worker);
            Assert.AreEqual(5000, args.workerPort);
            Assert.AreEqual(3, args.workerProcesses);
            Assert.AreEqual(0, args.workerPairMinutes);
            Assert.AreEqual(1234, args.workerParentPid);
            Assert.AreEqual(-1, HeadlessArgs.Parse(new[] { "sim.exe", "-worker" }).workerPairMinutes, "by hand: pairing opens by default");
        }

        [Test]
        public void SecretStores_RoundTrip()
        {
            var stores = new List<ISecretStore> { new MemorySecretStore(), new PrivateFileStore(Path.Combine(NewFolder(), "secrets")) };
            if (Environment.OSVersion.Platform == PlatformID.Win32NT) stores.Add(new WindowsCredentialStore());
            foreach (var store in stores)
            {
                string name = "test-" + Guid.NewGuid().ToString("N");
                byte[] secret = SecureChannel.RandomBytes(32);
                Assert.IsTrue(store.Write(name, secret, out string problem), store.Describe() + ": " + problem);
                CollectionAssert.AreEqual(secret, store.Read(name), store.Describe());
                store.Delete(name);
                Assert.IsNull(store.Read(name), store.Describe());
            }
        }

        // ---------------------------------------------------------------- end to end over loopback

        sealed class FakeProcess : IShardProcess
        {
            public bool HasExited { get; set; }
            public int ExitCode { get; set; }
            public bool Killed;
            public void Kill() { Killed = true; HasExited = true; ExitCode = 1; }
        }

        /// <summary>Stands in for the headless simulator: writes what a shard would, and exits when told.</summary>
        sealed class FakeLauncher : IShardLauncher
        {
            public readonly List<(ShardLaunch launch, FakeProcess process)> started = new List<(ShardLaunch, FakeProcess)>();
            public List<string> rejectWith;

            public IShardProcess Start(ShardLaunch launch, out string problem)
            {
                problem = null;
                var process = new FakeProcess();
                started.Add((launch, process));
                if (launch.validateOnly && rejectWith != null)
                    new ShardResults { status = "invalid", errors = rejectWith }.WriteTo(launch.outputFolder);
                if (!launch.validateOnly)
                {
                    var existing = ShardResults.TryRead(launch.outputFolder);
                    string run = Path.Combine(launch.outputFolder, "runs", "run-" + launch.shardIndex);
                    Directory.CreateDirectory(run);
                    File.WriteAllText(Path.Combine(run, "result.json"), "{\"coveragePercent\": 50}");
                    new ShardResults
                    {
                        campaignId = "c1", shardIndex = launch.shardIndex, shardCount = launch.shardCount, status = "completed",
                        totalRuns = 2, completedRuns = existing != null ? existing.completedRuns + 1 : 2,
                    }.WriteTo(launch.outputFolder);
                }
                return process;
            }

            public void FinishAll(int code = 0)
            {
                foreach (var entry in started.Where(s => !s.process.HasExited))
                {
                    entry.process.ExitCode = code;
                    entry.process.HasExited = true;
                }
            }
        }

        string root;
        FakeLauncher launcher;
        WorkerJobs jobs;
        WorkerServer server;
        readonly List<string> log = new List<string>();

        static string NewFolder()
        {
            string folder = Path.Combine(Path.GetTempPath(), "RobotVacuumWorkerTests_" + Guid.NewGuid().ToString("N"), "data");
            Directory.CreateDirectory(folder);
            return folder;
        }

        [SetUp]
        public void SetUp()
        {
            root = NewFolder();
            launcher = new FakeLauncher();
            jobs = new WorkerJobs(Path.Combine(root, "clients"), launcher, 4, line => { lock (log) log.Add(line); }, startScheduler: false);
            server = new WorkerServer(new WorkerServerOptions
            {
                port = 0,
                bindAddress = IPAddress.Loopback,
                dataFolder = root,
                workerName = "test-worker",
                appVersion = Version,
                jobSchemaVersion = 1,
                secrets = new MemorySecretStore(),
                jobs = jobs,
                log = line => { lock (log) log.Add(line); },
                pairingIterations = FastIterations,
            });
            server.Start();
        }

        [TearDown]
        public void TearDown()
        {
            server?.Dispose();
            jobs?.Dispose();
            try { Directory.Delete(Path.GetDirectoryName(root), true); }
            catch (Exception) { }
        }

        PairingResult PairClient(string clientId, string code = null) =>
            WorkerClient.Pair("127.0.0.1", server.Port, code ?? server.OpenPairing(TimeSpan.FromMinutes(5)), clientId, "PC " + clientId, Version, FastIterations);

        WorkerClient ConnectClient(string clientId, byte[] key) => WorkerClient.Connect("127.0.0.1", server.Port, clientId, key, Version);

        static byte[] JobBytes(string text = "{\"schemaVersion\":1,\"campaignId\":\"c1\"}") => Encoding.UTF8.GetBytes(text);

        static SubmitRequest Submit(string jobId, int shardCount, params int[] shards) =>
            new SubmitRequest { jobId = jobId, campaignName = "Test campaign", shardCount = shardCount, shards = shards.ToList() };

        [Test]
        public void Pairing_WithTheRightCode_GivesAKeyThatConnects()
        {
            var paired = PairClient("client-a");
            Assert.AreEqual(32, paired.key.Length);
            Assert.AreEqual(server.WorkerId, paired.workerId);
            Assert.IsFalse(server.TryGetPairing(out _, out _), "one PC per code");

            using (var client = ConnectClient("client-a", paired.key))
            {
                Assert.AreEqual("test-worker", client.Info.workerName);
                Assert.AreEqual(4, client.Info.maxProcesses);
            }
            Assert.AreEqual("PC client-a", server.PairedClients().Single().name);
        }

        [Test]
        public void Pairing_WrongCodes_AreRefused_AndFiveCloseThePairingWindow()
        {
            string code = server.OpenPairing(TimeSpan.FromMinutes(5));
            string wrong = code.Substring(0, 9) + (code[9] == 'A' ? 'B' : 'A');
            for (int i = 0; i < WorkerProtocol.MaxPairingFailures; i++)
            {
                var error = Assert.Throws<WorkerException>(() => PairClient("client-x", wrong));
                Assert.AreEqual("wrong-code", error.Code, error.Message);
            }
            var closed = Assert.Throws<WorkerException>(() => PairClient("client-x", code));
            Assert.AreEqual(WorkerProtocol.ErrorPairingClosed, closed.Code, "even the right code is refused once the window closed");
            Assert.IsEmpty(server.PairedClients());
        }

        [Test]
        public void Pairing_WhenClosed_IsRefusedWithAnExplanation()
        {
            var error = Assert.Throws<WorkerException>(() => WorkerClient.Pair("127.0.0.1", server.Port, "ABCDE-FGHJK", "client-a", "PC", Version, FastIterations));
            Assert.AreEqual(WorkerProtocol.ErrorPairingClosed, error.Code);
            StringAssert.Contains("Allow a new PC to pair", error.Message);
        }

        [Test]
        public void Connecting_WithAWrongKeyOrNoPairing_IsRefused()
        {
            PairClient("client-a");
            var wrongKey = Assert.Throws<WorkerException>(() => ConnectClient("client-a", SecureChannel.RandomBytes(32)));
            Assert.AreEqual(WorkerProtocol.ErrorNotPaired, wrongKey.Code);
            var stranger = Assert.Throws<WorkerException>(() => ConnectClient("client-b", SecureChannel.RandomBytes(32)));
            Assert.AreEqual(WorkerProtocol.ErrorNotPaired, stranger.Code);
        }

        [Test]
        public void ADifferentVersion_IsRefused_SoResultsStayIdentical()
        {
            var paired = PairClient("client-a");
            var error = Assert.Throws<WorkerException>(() => WorkerClient.Connect("127.0.0.1", server.Port, "client-a", paired.key, "other-version"));
            Assert.AreEqual(WorkerProtocol.ErrorVersion, error.Code);
            StringAssert.Contains(Version, error.Message);
        }

        [Test]
        public void AJob_IsValidated_Run_Reported_AndDownloaded()
        {
            var paired = PairClient("client-a");
            using (var client = ConnectClient("client-a", paired.key))
            {
                client.Submit(Submit("job-1", 3, 0, 2), JobBytes());
                Assert.AreEqual(WorkState.Validating, client.Status("job-1").jobs.Single().state);

                jobs.Tick();
                Assert.AreEqual(1, launcher.started.Count);
                Assert.IsTrue(launcher.started[0].launch.validateOnly, "validated in its own process before any shard runs");
                launcher.FinishAll();
                jobs.Tick(); // validation passed: shards queued and started
                Assert.AreEqual(3, launcher.started.Count);
                var shardLaunches = launcher.started.Skip(1).Select(s => s.launch).ToList();
                CollectionAssert.AreEquivalent(new[] { 0, 2 }, shardLaunches.Select(l => l.shardIndex));
                Assert.That(shardLaunches.All(l => l.shardCount == 3));

                var running = client.Status("job-1").jobs.Single();
                Assert.AreEqual(WorkState.Running, running.state);
                Assert.AreEqual(2, running.shards.Single(s => s.shardIndex == 0).completedRuns);

                launcher.FinishAll();
                jobs.Tick();
                var finished = client.Status("job-1").jobs.Single();
                Assert.AreEqual(WorkState.Finished, finished.state);
                Assert.That(finished.shards.All(s => s.state == WorkState.Finished && s.exitCode == 0));

                var files = client.ListFiles("job-1", 2).files.Select(f => f.path).ToList();
                CollectionAssert.Contains(files, "results.json");
                CollectionAssert.Contains(files, "runs/run-2/result.json");

                string local = Path.Combine(root, "downloaded", "result.json");
                client.Download("job-1", 2, "runs/run-2/result.json", local);
                Assert.AreEqual("{\"coveragePercent\": 50}", File.ReadAllText(local));

                client.Release("job-1");
                Assert.IsEmpty(client.Status().jobs);
            }
        }

        [Test]
        public void AnInvalidJob_IsRejected_WithTheValidatorsReasons()
        {
            launcher.rejectWith = new List<string> { "configurations[0]: battery capacity must be positive." };
            var paired = PairClient("client-a");
            using (var client = ConnectClient("client-a", paired.key))
            {
                client.Submit(Submit("bad", 1, 0), JobBytes());
                jobs.Tick();
                launcher.FinishAll(HeadlessBootstrap.ExitInvalid);
                jobs.Tick();
                var status = client.Status("bad").jobs.Single();
                Assert.AreEqual(WorkState.Rejected, status.state);
                CollectionAssert.Contains(status.errors, "configurations[0]: battery capacity must be positive.");
                Assert.AreEqual(1, launcher.started.Count, "no shard ran");
            }
        }

        [Test]
        public void OneClient_CannotSeeOrTouch_AnothersJobs()
        {
            var a = PairClient("client-a");
            var b = PairClient("client-b");
            using (var alice = ConnectClient("client-a", a.key))
            using (var bob = ConnectClient("client-b", b.key))
            {
                alice.Submit(Submit("private", 1, 0), JobBytes());
                Assert.IsEmpty(bob.Status().jobs);
                Assert.Throws<WorkerException>(() => bob.ListFiles("private", 0));
                Assert.Throws<WorkerException>(() => bob.ReadChunk("private", 0, "results.json", 0, 100, out _));
                Assert.Throws<WorkerException>(() => bob.Cancel("private"));
                Assert.AreEqual(1, alice.Status().jobs.Count, "still there for its owner");
            }
        }

        [Test]
        public void Files_OutsideTheShard_CannotBeRead()
        {
            var paired = PairClient("client-a");
            using (var client = ConnectClient("client-a", paired.key))
            {
                client.Submit(Submit("job", 1, 0), JobBytes());
                foreach (string path in new[] { "../../job.json", "../../state.json", "..\\..\\job.json", "/etc/passwd", "C:\\Windows\\win.ini" })
                    Assert.Throws<WorkerException>(() => client.ReadChunk("job", 0, path, 0, 100, out _), path);
            }
        }

        [Test]
        public void Submissions_AreChecked()
        {
            var paired = PairClient("client-a");
            using (var client = ConnectClient("client-a", paired.key))
            {
                Assert.Throws<WorkerException>(() => client.Submit(Submit("../escape", 1, 0), JobBytes()));
                Assert.Throws<WorkerException>(() => client.Submit(Submit("job", 2, 5), JobBytes()), "shard outside the job");
                Assert.Throws<WorkerException>(() => client.Submit(Submit("job", 0, 0), JobBytes()), "no shards");
                Assert.Throws<WorkerException>(() => client.Submit(Submit("job", 1, 0), new byte[0]), "empty job");
                client.Submit(Submit("job", 2, 0), JobBytes());
                Assert.Throws<WorkerException>(() => client.Submit(Submit("job", 2, 1), JobBytes("{\"different\":true}")), "same id, different job");
                client.Submit(Submit("job", 2, 1), JobBytes()); // more shards of the same job: accepted
                Assert.AreEqual(2, client.Status("job").jobs.Single().shards.Count);
            }
        }

        [Test]
        public void AShardTakenOverFromAnotherWorker_ResumesFromItsResults()
        {
            var paired = PairClient("client-a");
            string partial = JsonUtility.ToJson(new ShardResults { campaignId = "c1", shardIndex = 1, shardCount = 2, totalRuns = 2, completedRuns = 1 });
            using (var client = ConnectClient("client-a", paired.key))
            {
                var request = Submit("job", 2, 1);
                request.resume.Add(new ShardResume { shardIndex = 1, resultsJson = partial });
                client.Submit(request, JobBytes());
                jobs.Tick();
                launcher.FinishAll();
                jobs.Tick();
                // The fake shard adds one run to whatever results.json it finds, as the real one resumes.
                Assert.AreEqual(2, client.Status("job").jobs.Single().shards.Single().completedRuns);
            }
        }

        [Test]
        public void Cancel_StopsTheShards()
        {
            var paired = PairClient("client-a");
            using (var client = ConnectClient("client-a", paired.key))
            {
                client.Submit(Submit("job", 1, 0), JobBytes());
                jobs.Tick();
                launcher.FinishAll();
                jobs.Tick();
                var shard = launcher.started.Last().process;
                client.Cancel("job");
                Assert.IsTrue(shard.Killed);
                Assert.AreEqual(WorkState.Cancelled, client.Status("job").jobs.Single().state);
            }
        }

        [Test]
        public void Unpairing_RemovesTheKeyAndTheJobs()
        {
            var paired = PairClient("client-a");
            string jobFolder = Path.Combine(root, "clients", "client-a");
            using (var client = ConnectClient("client-a", paired.key))
            {
                client.Submit(Submit("job", 1, 0), JobBytes());
                Assert.IsTrue(Directory.Exists(jobFolder));
                client.Unpair();
            }
            Assert.IsFalse(Directory.Exists(jobFolder));
            var error = Assert.Throws<WorkerException>(() => ConnectClient("client-a", paired.key));
            Assert.AreEqual(WorkerProtocol.ErrorNotPaired, error.Code);
        }

        [Test]
        public void ARestartedWorker_PicksUpUnfinishedShards()
        {
            var paired = PairClient("client-a");
            using (var client = ConnectClient("client-a", paired.key))
            {
                client.Submit(Submit("job", 1, 0), JobBytes());
                jobs.Tick();
                launcher.FinishAll();
                jobs.Tick(); // shard running
            }
            jobs.Dispose();

            var restartedLauncher = new FakeLauncher();
            using (var restarted = new WorkerJobs(Path.Combine(root, "clients"), restartedLauncher, 4, null, startScheduler: false))
            {
                Assert.AreEqual(WorkState.Queued, restarted.Status("client-a", "job").jobs.Single().shards.Single().state);
                restarted.Tick();
                Assert.AreEqual(1, restartedLauncher.started.Count);
            }
            jobs = null;
        }

        [Test]
        public void Processes_NeverExceedTheLimit()
        {
            var paired = PairClient("client-a");
            using (var client = ConnectClient("client-a", paired.key))
            {
                client.Submit(Submit("big", 10, Enumerable.Range(0, 10).ToArray()), JobBytes());
                jobs.Tick();
                launcher.FinishAll();
                jobs.Tick();
                Assert.AreEqual(4, launcher.started.Count(s => !s.launch.validateOnly), "4 = the worker's limit");
                Assert.AreEqual(6, jobs.Load().queued);
            }
        }
    }
}
