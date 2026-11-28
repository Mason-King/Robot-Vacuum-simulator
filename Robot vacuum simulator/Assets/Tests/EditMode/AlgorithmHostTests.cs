using System;
using System.Threading;
using NUnit.Framework;
using RobotVacuum.AlgorithmApi;
using RobotVacuum.Sim;
using RobotVacuum.Sim.Control;
using UnityEngine;
using UnityEngine.TestTools;
using System.Text.RegularExpressions;

namespace RobotVacuum.Tests
{
    /// <summary>
    /// Requirement 2.11 / AT-2.11: an algorithm that throws, hangs or won't stop is caught by
    /// <see cref="AlgorithmHost"/> and reported, and the caller carries on. These run the host directly, so they need
    /// no scene or Play mode; the whole run (session, result.json, headless jobs) is covered by the manual and
    /// command-line checks in Docs/AT-2.11.
    /// </summary>
    public class AlgorithmHostTests
    {
        /// <summary>An algorithm whose calls do whatever the test says.</summary>
        sealed class Scripted : ICleaningAlgorithm
        {
            public Action OnInitialize;
            public Func<Vec2?> OnNextGoal;

            public string Name => "Scripted (test)";
            public bool SupportsSlam => true;
            public void Initialize(AlgorithmContext context) => OnInitialize?.Invoke();
            public Vec2? NextGoal(IBeliefMap map, Pose2 believedPose, GoalStatus lastStatus) => OnNextGoal != null ? OnNextGoal() : null;
        }

        // Released at the end of each test, so a deliberately stubborn thread ends instead of spinning on.
        static volatile bool release;

        /// <summary>Hangs inside a finally block, where an abort waits until the block ends: a thread that won't stop.</summary>
        static Vec2? HangStubbornly()
        {
            try { }
            finally
            {
                while (!release) Thread.Sleep(5);
            }
            return null;
        }

        /// <summary>Hangs in an ordinary loop (with calls in it, so an abort can land).</summary>
        static Vec2? HangPlainly()
        {
            var list = new System.Collections.Generic.List<int>();
            while (true)
            {
                list.Add(1);
                if (list.Count > 1000) list.Clear();
            }
        }

        AlgorithmHost host;

        [SetUp]
        public void SetUp()
        {
            release = false;
            AlgorithmHost.BudgetOverrideSeconds = 0.3f;
            AlgorithmHost.AbortWaitOverrideSeconds = 0.3f;
            host = new AlgorithmHost();
        }

        [TearDown]
        public void TearDown()
        {
            release = true;
            host.Dispose();
            AlgorithmHost.BudgetOverrideSeconds = null;
            AlgorithmHost.AbortWaitOverrideSeconds = null;

            // Let released zombies end, so they don't count against the next test's program-wide limit.
            var until = DateTime.UtcNow.AddSeconds(2);
            while (AlgorithmHost.LiveZombies > 0 && DateTime.UtcNow < until) Thread.Sleep(10);
        }

        static Pose2 Pose => new Pose2(new Vec2(0f, 0f), 0f);

        /// <summary>An abandoned thread is logged as an error, on purpose: tell the test runner to expect it.</summary>
        static void ExpectAbandoned(int times)
        {
            for (int i = 0; i < times; i++)
                LogAssert.Expect(LogType.Error, new Regex("would not stop after it hung and was abandoned"));
        }

        [Test]
        public void NextGoal_ReturnsTheAlgorithmsAnswer_FromAnotherThread()
        {
            int callerThread = Thread.CurrentThread.ManagedThreadId;
            int calledOn = -1;
            var algorithm = new Scripted
            {
                OnNextGoal = () =>
                {
                    calledOn = Thread.CurrentThread.ManagedThreadId;
                    return new Vec2(1.5f, -2f);
                },
            };

            var outcome = host.NextGoal(algorithm, null, Pose, GoalStatus.None);

            Assert.IsTrue(outcome.Ok);
            Assert.AreEqual(1.5f, outcome.Value.Value.x, 1e-6f);
            Assert.AreEqual(-2f, outcome.Value.Value.y, 1e-6f);
            Assert.AreNotEqual(callerThread, calledOn, "the algorithm runs on the host's thread, not the caller's");
        }

        [Test]
        public void NextGoal_Throwing_IsAnExceptionFault_WithTypeMessageAndStack()
        {
            var algorithm = new Scripted { OnNextGoal = () => throw new InvalidOperationException("deliberate test failure") };

            var outcome = host.NextGoal(algorithm, null, Pose, GoalStatus.None);

            Assert.IsFalse(outcome.Ok);
            Assert.AreEqual(AlgorithmFaultKind.Exception, outcome.Fault.Kind);
            Assert.AreEqual(AlgorithmCall.NextGoal, outcome.Fault.Call);
            Assert.AreEqual(typeof(InvalidOperationException).FullName, outcome.Fault.ExceptionType);
            Assert.AreEqual("deliberate test failure", outcome.Fault.Message);
            StringAssert.Contains("Scripted", outcome.Fault.StackTrace, "the stack names where it was thrown");
            Assert.IsFalse(outcome.Fault.Terminal);
        }

        [Test]
        public void Initialize_Throwing_IsAnExceptionFault()
        {
            var algorithm = new Scripted { OnInitialize = () => throw new ArgumentException("bad setup") };

            var outcome = host.Initialize(algorithm, default);

            Assert.IsFalse(outcome.Ok);
            Assert.AreEqual(AlgorithmFaultKind.Exception, outcome.Fault.Kind);
            Assert.AreEqual(AlgorithmCall.Initialize, outcome.Fault.Call);
        }

        [Test]
        public void Create_Throwing_IsAnExceptionFault()
        {
            var entry = new AlgorithmEntry("Test.Throws", "Throws in its constructor", true, "test",
                () => throw new NotSupportedException("constructor failed"));

            var outcome = host.Create(entry);

            Assert.IsFalse(outcome.Ok);
            Assert.AreEqual(AlgorithmCall.Create, outcome.Fault.Call);
            Assert.AreEqual(typeof(NotSupportedException).FullName, outcome.Fault.ExceptionType);
        }

        [Test]
        public void Hang_IsStopped_WithinTheBudgetPlusTheAbortWait()
        {
            var algorithm = new Scripted { OnNextGoal = HangPlainly };
            var started = DateTime.UtcNow;

            var outcome = host.NextGoal(algorithm, null, Pose, GoalStatus.None);

            double seconds = (DateTime.UtcNow - started).TotalSeconds;
            Assert.IsFalse(outcome.Ok);
            Assert.AreEqual(AlgorithmFaultKind.Hang, outcome.Fault.Kind);
            Assert.AreEqual(AlgorithmCall.NextGoal, outcome.Fault.Call);
            Assert.Less(seconds, 0.3 + 0.3 + 1.0, "the caller got control back after the budget and the abort wait");
        }

        [Test]
        public void AfterAHang_TheNextCallRunsOnAFreshThread()
        {
            var hangs = new Scripted { OnNextGoal = HangPlainly };
            var first = host.NextGoal(hangs, null, Pose, GoalStatus.None);
            Assume.That(first.Fault != null && first.Fault.Kind == AlgorithmFaultKind.Hang);
            Assume.That(host.RunZombies == 0, "this check needs the hung thread to have stopped");

            var fine = new Scripted { OnNextGoal = () => new Vec2(3f, 4f) };
            var second = host.NextGoal(fine, null, Pose, GoalStatus.None);

            Assert.IsTrue(second.Ok, second.Fault != null ? second.Fault.Describe() : "");
            Assert.AreEqual(3f, second.Value.Value.x, 1e-6f);
        }

        [Test]
        public void AThreadThatWontStop_IsAbandoned_ThenASecondOneTripsTheGuard()
        {
            var stubborn = new Scripted { OnNextGoal = HangStubbornly };
            ExpectAbandoned(2);

            var first = host.NextGoal(stubborn, null, Pose, GoalStatus.None);
            Assert.AreEqual(AlgorithmFaultKind.Hang, first.Fault.Kind, "the first one is an ordinary hang (Recoverable)");
            Assert.AreEqual(1, host.RunZombies);
            Assert.IsFalse(first.Fault.Terminal);

            var second = host.NextGoal(stubborn, null, Pose, GoalStatus.None);
            Assert.AreEqual(AlgorithmFaultKind.GuardTripped, second.Fault.Kind, "the second one in the run ends it");
            Assert.IsTrue(second.Fault.Terminal);

            int calls = 0;
            var fine = new Scripted { OnNextGoal = () => { calls++; return null; } };
            var third = host.NextGoal(fine, null, Pose, GoalStatus.None);
            Assert.AreEqual(AlgorithmFaultKind.GuardTripped, third.Fault.Kind, "no more calls are started in this run");
            Assert.AreEqual(0, calls);
        }

        [Test]
        public void TooManyLiveZombiesInTheProgram_RefuseNewCallsInEveryHost()
        {
            var stubborn = new Scripted { OnNextGoal = HangStubbornly };
            var hosts = new System.Collections.Generic.List<AlgorithmHost>();
            ExpectAbandoned(AlgorithmHost.MaxLiveZombies);
            try
            {
                for (int i = 0; i < AlgorithmHost.MaxLiveZombies; i++)
                {
                    var h = new AlgorithmHost();
                    hosts.Add(h);
                    h.NextGoal(stubborn, null, Pose, GoalStatus.None);
                }
                Assert.GreaterOrEqual(AlgorithmHost.LiveZombies, AlgorithmHost.MaxLiveZombies);

                int calls = 0;
                var fine = new Scripted { OnNextGoal = () => { calls++; return null; } };
                var refused = host.NextGoal(fine, null, Pose, GoalStatus.None);

                Assert.AreEqual(AlgorithmFaultKind.GuardTripped, refused.Fault.Kind);
                Assert.AreEqual(0, calls);
            }
            finally
            {
                foreach (var h in hosts) h.Dispose();
            }
        }

        [Test]
        public void Calls_RunOneAtATime_InOrder()
        {
            int step = 0;
            var order = new System.Collections.Generic.List<int>();
            var algorithm = new Scripted
            {
                OnNextGoal = () =>
                {
                    order.Add(++step);
                    return null;
                },
            };

            for (int i = 0; i < 50; i++)
                Assert.IsTrue(host.NextGoal(algorithm, null, Pose, GoalStatus.None).Ok);

            Assert.AreEqual(50, order.Count);
            for (int i = 0; i < 50; i++) Assert.AreEqual(i + 1, order[i]);
        }

        [Test]
        public void Fault_Record_RoundTripsThroughResultJson()
        {
            var fault = AlgorithmFault.FromException(new InvalidOperationException("boom"), AlgorithmCall.NextGoal, "Sheep", 12.5f);
            var result = new RunResult
            {
                succeeded = false,
                endReason = RunEndReason.AlgorithmFault,
                endReasonName = RunEndReason.AlgorithmFault.ToString(),
                hasFault = true,
                fault = fault.ToRecord(),
            };
            result.stopEvents.Add(new RunStopEvent { simSeconds = 12.5f, reason = "AlgorithmFault", message = "boom", action = "Terminated" });

            var read = JsonUtility.FromJson<RunResult>(JsonUtility.ToJson(result));

            Assert.AreEqual(RunEndReason.AlgorithmFault, read.endReason);
            Assert.IsTrue(read.hasFault);
            Assert.AreEqual("Exception", read.fault.kind);
            Assert.AreEqual("NextGoal", read.fault.call);
            Assert.AreEqual("boom", read.fault.message);
            Assert.AreEqual(12.5f, read.fault.simSeconds, 1e-5f);
            Assert.AreEqual(1, read.stopEvents.Count);
            Assert.AreEqual("Terminated", read.stopEvents[0].action);
        }

        [Test]
        public void EndReasonNumbers_AreUnchanged_AndNewOnesAreAppended()
        {
            // result.json stores the number: these must never move.
            Assert.AreEqual(1, (int)RunEndReason.Completed);
            Assert.AreEqual(2, (int)RunEndReason.BatteryDepleted);
            Assert.AreEqual(3, (int)RunEndReason.TimeCap);
            Assert.AreEqual(4, (int)RunEndReason.StoppedByUser);
            Assert.AreEqual(5, (int)RunEndReason.Error);
            Assert.AreEqual(6, (int)RunEndReason.Degenerate);
            Assert.AreEqual(7, (int)RunEndReason.ReturnedLowBattery);
            Assert.AreEqual(8, (int)RunEndReason.TimeLimit);
            Assert.AreEqual(9, (int)RunEndReason.AlgorithmFault);
            Assert.AreEqual(10, (int)RunEndReason.Stuck);
        }

        [Test]
        public void Fingerprint_OfABuiltIn_IsBuiltIn_AndOfAMissingOne_IsNotLoaded()
        {
            Assert.AreEqual("built-in", AlgorithmCatalog.Fingerprint(AlgorithmCatalog.RandomBounceId));
            Assert.AreEqual(AlgorithmCatalog.NotLoaded, AlgorithmCatalog.Fingerprint("No.Such.Algorithm"));
        }

        [Test]
        public void HeadlessJob_DefaultTimeLimit_IsOneSimulatedHour()
        {
            Assert.AreEqual(3600f, new Headless.SimulationSpec().maxSimulatedSeconds, 1e-3f);
        }

        [Test]
        public void RunSettings_UnsetTimeLimit_ResolvesToNone()
        {
            var resolved = new RunSettings { randomSeed = false, seed = 1 }.Resolved();
            Assert.AreEqual(0f, resolved.maxSimulatedSeconds, 1e-6f, "no limit of its own: only the 24 h safety cap");

            var capped = new RunSettings { randomSeed = false, seed = 1, maxSimulatedSeconds = 1e9f }.Resolved();
            Assert.AreEqual(SimDefaults.MaxSimulatedSeconds, capped.maxSimulatedSeconds, 1e-3f, "never above the safety cap");
        }
    }
}
