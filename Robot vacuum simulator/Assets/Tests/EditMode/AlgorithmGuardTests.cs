using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using NUnit.Framework;
using RobotVacuum.AlgorithmApi;
using RobotVacuum.Sim.Control;

namespace RobotVacuum.Tests
{
    /// <summary>
    /// Runtime failures in an algorithm (a crash, an infinite loop, an invalid move) are detected, classified and
    /// contained: the call returns, the algorithm is cut off, and the host (here, the test runner) carries on.
    /// </summary>
    public class AlgorithmGuardTests
    {
        const float Budget = 0.3f;

        // Released in TearDown, in case the runtime cannot abort a hung worker: it then exits on its own.
        static volatile bool release;

        float savedInitialize, savedNextGoal, savedProbe;

        sealed class Scripted : ICleaningAlgorithm
        {
            public Action onInitialize;
            public Func<Vec2?> onNextGoal;
            public int calls;
            public string Name => "Scripted";
            public bool SupportsSlam => true;
            public void Initialize(AlgorithmContext context) => onInitialize?.Invoke();

            public Vec2? NextGoal(IBeliefMap map, Pose2 believedPose, GoalStatus lastStatus)
            {
                calls++;
                return onNextGoal != null ? onNextGoal() : new Vec2(1f, 0f);
            }
        }

        static void Spin()
        {
            while (!release) { } // an infinite loop, as far as the sandbox can tell
        }

        [SetUp]
        public void SetUp()
        {
            release = false;
            savedInitialize = AlgorithmGuard.InitializeBudgetSeconds;
            savedNextGoal = AlgorithmGuard.NextGoalBudgetSeconds;
            savedProbe = AlgorithmGuard.ProbeBudgetSeconds;
            AlgorithmGuard.InitializeBudgetSeconds = AlgorithmGuard.NextGoalBudgetSeconds = AlgorithmGuard.ProbeBudgetSeconds = Budget;
            AlgorithmGuard.UnlimitedUnderDebugger = false;
        }

        [TearDown]
        public void TearDown()
        {
            release = true;
            AlgorithmGuard.InitializeBudgetSeconds = savedInitialize;
            AlgorithmGuard.NextGoalBudgetSeconds = savedNextGoal;
            AlgorithmGuard.ProbeBudgetSeconds = savedProbe;
            AlgorithmGuard.UnlimitedUnderDebugger = true;
        }

        static Vec2? Ask(AlgorithmGuard sandbox, ICleaningAlgorithm algorithm, out bool answered)
        {
            answered = sandbox.TryRun("NextGoal", () => algorithm.NextGoal(null, new Pose2(Vec2.Zero, 0f), GoalStatus.None),
                AlgorithmGuard.NextGoalBudgetSeconds, out Vec2? goal);
            return goal;
        }

        // ---- crash ------------------------------------------------------------------------------------------

        [Test]
        public void Crash_IsCaught_AndClassified()
        {
            var algorithm = new Scripted { onNextGoal = () => throw new InvalidOperationException("boom") };
            using var sandbox = new AlgorithmGuard("crash test");

            Ask(sandbox, algorithm, out bool answered);

            Assert.IsFalse(answered);
            Assert.AreEqual(AlgorithmFault.Crash, sandbox.Fault);
            StringAssert.Contains("InvalidOperationException", sandbox.FaultMessage);
            StringAssert.Contains("boom", sandbox.FaultMessage);
        }

        [Test]
        public void Crash_InInitialize_IsCaught()
        {
            var algorithm = new Scripted { onInitialize = () => throw new NullReferenceException() };
            using var sandbox = new AlgorithmGuard("init crash");

            bool ok = sandbox.TryRun("Initialize", () => algorithm.Initialize(default), AlgorithmGuard.InitializeBudgetSeconds);

            Assert.IsFalse(ok);
            Assert.AreEqual(AlgorithmFault.Crash, sandbox.Fault);
            StringAssert.StartsWith("Initialize threw NullReferenceException", sandbox.FaultMessage);
        }

        [Test]
        public void OutOfMemory_IsACrash_NotAHostFailure()
        {
            var algorithm = new Scripted { onNextGoal = () => throw new OutOfMemoryException("simulated") };
            using var sandbox = new AlgorithmGuard("oom");
            Ask(sandbox, algorithm, out bool answered);
            Assert.IsFalse(answered);
            Assert.AreEqual(AlgorithmFault.Crash, sandbox.Fault);
        }

        // ---- infinite loop -------------------------------------------------------------------------------------

        [Test]
        public void InfiniteLoop_InNextGoal_IsCutOffWithinTheBudget()
        {
            var algorithm = new Scripted { onNextGoal = () => { Spin(); return null; } };
            using var sandbox = new AlgorithmGuard("hang test");

            var clock = Stopwatch.StartNew();
            Ask(sandbox, algorithm, out bool answered);
            clock.Stop();

            Assert.IsFalse(answered);
            Assert.AreEqual(AlgorithmFault.Hang, sandbox.Fault);
            StringAssert.Contains("did not return", sandbox.FaultMessage);
            Assert.Less(clock.Elapsed.TotalSeconds, Budget + 1.5, "the host got control back");
        }

        [Test]
        public void InfiniteLoop_InInitialize_IsCutOff()
        {
            var algorithm = new Scripted { onInitialize = Spin };
            using var sandbox = new AlgorithmGuard("init hang");

            bool ok = sandbox.TryRun("Initialize", () => algorithm.Initialize(default), AlgorithmGuard.InitializeBudgetSeconds);

            Assert.IsFalse(ok);
            Assert.AreEqual(AlgorithmFault.Hang, sandbox.Fault);
        }

        [Test]
        public void AfterAFault_TheAlgorithmIsNeverCalledAgain()
        {
            int attempts = 0;
            var algorithm = new Scripted { onNextGoal = () => { attempts++; throw new Exception("first call fails"); } };
            using var sandbox = new AlgorithmGuard("quarantine");

            Ask(sandbox, algorithm, out _);
            var clock = Stopwatch.StartNew();
            Ask(sandbox, algorithm, out bool second);

            Assert.IsFalse(second);
            Assert.AreEqual(1, attempts, "isolated: the faulted instance is not run again");
            Assert.Less(clock.Elapsed.TotalSeconds, 0.05, "refused at once, no waiting");
        }

        [Test]
        public void HostCarriesOn_AfterAHang()
        {
            using (var hung = new AlgorithmGuard("hung"))
                Ask(hung, new Scripted { onNextGoal = () => { Spin(); return null; } }, out _);

            // A fresh run (a new sandbox, as the next trial of a batch gets) works normally.
            var healthy = new Scripted();
            using var sandbox = new AlgorithmGuard("next trial");
            var goal = Ask(sandbox, healthy, out bool answered);

            Assert.IsTrue(answered, sandbox.FaultMessage);
            Assert.AreEqual(AlgorithmFault.None, sandbox.Fault);
            Assert.AreEqual(1f, goal.Value.x);
        }

        [Test]
        public void HealthyCalls_PassResultsThrough_InOrder()
        {
            int next = 0;
            var algorithm = new Scripted { onNextGoal = () => new Vec2(next++, 0f) };
            using var sandbox = new AlgorithmGuard("healthy");

            for (int i = 0; i < 200; i++)
            {
                var goal = Ask(sandbox, algorithm, out bool answered);
                Assert.IsTrue(answered);
                Assert.AreEqual(i, goal.Value.x);
            }
            Assert.AreEqual(200, algorithm.calls);
        }

        // ---- invalid move ----------------------------------------------------------------------------------

        static readonly Pose2 Origin = new Pose2(Vec2.Zero, 0f);

        [Test]
        public void InvalidMoves_AreRejected_WithAReason()
        {
            Assert.IsNotNull(AlgorithmGuard.CheckMove(new Vec2(float.NaN, 0f), Origin, null), "NaN");
            Assert.IsNotNull(AlgorithmGuard.CheckMove(new Vec2(0f, float.PositiveInfinity), Origin, null), "infinity");
            StringAssert.Contains("m away", AlgorithmGuard.CheckMove(new Vec2(5000f, 0f), Origin, null));

            var badMode = new DriveRequests { Mode = (DriveMode)42 };
            StringAssert.Contains("not a drive mode", AlgorithmGuard.CheckMove(new Vec2(1f, 0f), Origin, badMode));

            var badExposure = new DriveRequests { Mode = DriveMode.Sweep, SweepExposureMeters = -1f };
            StringAssert.Contains("SweepExposureMeters", AlgorithmGuard.CheckMove(new Vec2(1f, 0f), Origin, badExposure));
        }

        [Test]
        public void ValidMoves_AndNoGoal_AreAllowed()
        {
            Assert.IsNull(AlgorithmGuard.CheckMove(null, Origin, new DriveRequests()));
            Assert.IsNull(AlgorithmGuard.CheckMove(new Vec2(3f, -2f), Origin, new DriveRequests { Mode = DriveMode.Sweep, SweepExposureMeters = 0.3f }));
            Assert.IsNull(AlgorithmGuard.CheckMove(new Vec2(150f, 0f), new Pose2(new Vec2(100f, 0f), 0f), null), "far from the origin but near the robot");
        }

        // ---- logging -----------------------------------------------------------------------------------------

        [Test]
        public void Log_IsHandedToTheMainThread_AndCapped()
        {
            var log = new SandboxLog();
            using var sandbox = new AlgorithmGuard("log");
            sandbox.TryRun("chatty", () => { for (int i = 0; i < 10000; i++) log.Add($"line {i}"); }, Budget);

            var written = new List<string>();
            log.Flush(written.Add);

            Assert.AreEqual("line 0", written[0]);
            Assert.LessOrEqual(written.Count, 501);
            StringAssert.Contains("dropped", written[written.Count - 1]);
        }

        // ---- loading the Algorithms folder ------------------------------------------------------------------------

        [Test]
        public void Loader_SkipsAnAlgorithmWhoseConstructorHangs_AndLoadsTheRest()
        {
            string folder = Path.Combine(Path.GetTempPath(), "RobotVacuumSandboxTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                File.WriteAllText(Path.Combine(folder, "Hangs.cs"), @"using RobotVacuum.AlgorithmApi;
namespace SandboxTests
{
    public sealed class HangsOnCreate : ICleaningAlgorithm
    {
        public HangsOnCreate() { while (true) { } }
        public string Name => ""Hangs on create"";
        public bool SupportsSlam => true;
        public void Initialize(AlgorithmContext context) { }
        public Vec2? NextGoal(IBeliefMap map, Pose2 pose, GoalStatus lastStatus) => null;
    }
}");
                File.WriteAllText(Path.Combine(folder, "Fine.cs"), @"using RobotVacuum.AlgorithmApi;
namespace SandboxTests
{
    public sealed class Fine : ICleaningAlgorithm
    {
        public string Name => ""Fine"";
        public bool SupportsSlam => true;
        public void Initialize(AlgorithmContext context) { }
        public Vec2? NextGoal(IBeliefMap map, Pose2 pose, GoalStatus lastStatus) => null;
    }
}");

                var clock = Stopwatch.StartNew();
                var entries = AlgorithmLoader.Load(folder, AlgorithmCatalog.BuiltIns, out var report);
                clock.Stop();

                Assert.IsTrue(entries.Any(e => e.Name == "Fine"), report.Text());
                Assert.IsFalse(entries.Any(e => e.Name == "Hangs on create"));
                StringAssert.Contains("did not return", report.Text());
                Assert.Less(clock.Elapsed.TotalSeconds, 30, "the load finished (the first compile warms the compiler up)");
            }
            finally
            {
                try { Directory.Delete(folder, true); } catch (IOException) { }
            }
        }
    }
}
