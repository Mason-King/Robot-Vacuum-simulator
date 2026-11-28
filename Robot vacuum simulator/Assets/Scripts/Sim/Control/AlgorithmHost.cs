using System;
using System.Collections.Generic;
using System.Threading;
using RobotVacuum.AlgorithmApi;

namespace RobotVacuum.Sim.Control
{
    /// <summary>What went wrong with an algorithm call (requirement 2.11).</summary>
    public enum AlgorithmFaultKind
    {
        /// <summary>The algorithm threw.</summary>
        Exception,

        /// <summary>The algorithm did not return within its time budget and was stopped.</summary>
        Hang,

        /// <summary>The algorithm returned something the simulation can't use (a NaN waypoint, an out-of-range request).</summary>
        InvalidOutput,

        /// <summary>The algorithm can't be used in this run (not found, or it can't run with SLAM).</summary>
        Refused,

        /// <summary>
        /// Too many algorithm threads that would not stop are still running. This one is Terminal: the run ends,
        /// because another call could leave yet another thread running.
        /// </summary>
        GuardTripped,
    }

    /// <summary>Which algorithm call failed.</summary>
    public enum AlgorithmCall
    {
        /// <summary>Creating the algorithm (its constructor).</summary>
        Create,
        Initialize,
        NextGoal,
    }

    /// <summary>
    /// One algorithm failure, as captured: what kind, in which call, the exception (if any) and when. Saved in
    /// result.json (<see cref="RunFault"/>) and shown to the user.
    /// </summary>
    public sealed class AlgorithmFault
    {
        public AlgorithmFaultKind Kind;
        public AlgorithmCall Call;
        public string AlgorithmId;
        public string ExceptionType;
        public string Message;
        public string StackTrace;
        public float SimSeconds;

        /// <summary>True when the run must end rather than pause (see <see cref="AlgorithmFaultKind.GuardTripped"/>).</summary>
        public bool Terminal => Kind == AlgorithmFaultKind.GuardTripped;

        /// <summary>One line for people: the algorithm, what happened and where.</summary>
        public string Describe()
        {
            string name = AlgorithmCatalog.NameOf(AlgorithmId);
            switch (Kind)
            {
                case AlgorithmFaultKind.Exception:
                    return $"Algorithm '{name}' threw {ExceptionType} in {Call}: {Message}";
                case AlgorithmFaultKind.Hang:
                    return $"Algorithm '{name}' hung in {Call}: {Message}";
                case AlgorithmFaultKind.InvalidOutput:
                    return $"Algorithm '{name}' returned invalid output from {Call}: {Message}";
                case AlgorithmFaultKind.Refused:
                    return Message;
                case AlgorithmFaultKind.GuardTripped:
                    return $"Algorithm '{name}': {Message}";
                default:
                    return Message;
            }
        }

        public RunFault ToRecord() => new RunFault
        {
            kind = Kind.ToString(),
            call = Call.ToString(),
            algorithmId = AlgorithmId ?? "",
            algorithmName = AlgorithmCatalog.NameOf(AlgorithmId),
            exceptionType = ExceptionType ?? "",
            message = Message ?? "",
            stackTrace = StackTrace ?? "",
            simSeconds = SimSeconds,
            terminal = Terminal,
        };

        public static AlgorithmFault FromException(Exception exception, AlgorithmCall call, string algorithmId, float simSeconds) =>
            new AlgorithmFault
            {
                Kind = AlgorithmFaultKind.Exception,
                Call = call,
                AlgorithmId = algorithmId,
                ExceptionType = exception.GetType().FullName,
                Message = exception.Message,
                StackTrace = TrimStack(exception.StackTrace),
                SimSeconds = simSeconds,
            };

        /// <summary>The first <paramref name="maxFrames"/> lines of a stack trace (they can run to hundreds in a deep recursion).</summary>
        public static string TrimStack(string stack, int maxFrames = 20)
        {
            if (string.IsNullOrEmpty(stack)) return "";
            var lines = stack.Split('\n');
            if (lines.Length <= maxFrames) return stack.TrimEnd();
            return string.Join("\n", lines, 0, maxFrames).TrimEnd() + $"\n   ... {lines.Length - maxFrames} more frame(s)";
        }
    }

    /// <summary>
    /// Runs every call into a cleaning algorithm (its constructor, Initialize, NextGoal) on a thread of its own,
    /// so an algorithm that hangs can be stopped without freezing the simulation, the GUI or a headless job
    /// (requirement 2.11). An exception from the algorithm is caught and handed back the same way.
    ///
    /// The simulation still WAITS for each call: the physics step that asked does not finish until the call
    /// returns. Nothing runs alongside the algorithm, so a run is exactly as deterministic as before (2.17); the
    /// only change is that the wait has a limit. A call that runs past its budget (<see cref="NextGoalBudgetSeconds"/>,
    /// <see cref="InitializeBudgetSeconds"/>) is treated as a hang: the thread is aborted (Mono) and the call fails.
    /// The GUI is frozen for at most that budget plus <see cref="AbortWaitSeconds"/>.
    ///
    /// Thread guard rails. One worker thread at a time. After an abort the thread gets <see cref="AbortWaitSeconds"/>
    /// to end; one that won't (an algorithm looping inside a finally block, say) is abandoned as a "zombie" (it is a
    /// background thread, so it can't keep the program from closing). A run may leave at most
    /// <see cref="MaxZombiesPerRun"/> zombie; and while <see cref="MaxLiveZombies"/> zombies are still running
    /// anywhere in the program, no algorithm call is started at all. Either way the call fails with
    /// <see cref="AlgorithmFaultKind.GuardTripped"/>, which ends the run. Restarting the program clears them.
    ///
    /// While a debugger is attached, calls wait without a limit, so stopping at a breakpoint inside an algorithm
    /// doesn't count as a hang.
    ///
    /// Mono only: IL2CPP has no Thread.Abort, so there a hung thread is always abandoned as a zombie.
    /// </summary>
    public sealed class AlgorithmHost : IDisposable
    {
        public const float NextGoalBudgetSeconds = 5f;
        public const float InitializeBudgetSeconds = 15f;
        public const float ProbeBudgetSeconds = 5f;
        public const float AbortWaitSeconds = 2f;
        public const int MaxZombiesPerRun = 1;
        public const int MaxLiveZombies = 3;

        /// <summary>
        /// The worker's stack. Larger than the main thread's, so an algorithm that recursed deeply before (on the
        /// main thread) doesn't overflow now. A stack overflow can't be caught and ends the program.
        /// </summary>
        const int WorkerStackBytes = 16 * 1024 * 1024;

        // Threads abandoned by any host, program-wide. Pruned of those that have since ended.
        static readonly List<Thread> zombies = new List<Thread>();
        static readonly object zombieLock = new object();

        /// <summary>Zombie threads (see the class notes) still running, program-wide.</summary>
        public static int LiveZombies
        {
            get
            {
                lock (zombieLock)
                {
                    zombies.RemoveAll(t => !t.IsAlive);
                    return zombies.Count;
                }
            }
        }

        // Test hooks: a budget and an abort wait other than the real ones (the tests don't want to wait 5 s per hang).
        internal static float? BudgetOverrideSeconds;
        internal static float? AbortWaitOverrideSeconds;

        readonly object gate = new object();
        Thread worker;
        Func<object> job;     // the call handed to the worker; null while idle
        object jobResult;
        Exception jobError;
        bool jobDone;
        bool quitting;
        int generation;       // which worker a hand-back belongs to (an aborted one must not report)

        /// <summary>Zombies this host (this run) has left behind.</summary>
        public int RunZombies { get; private set; }

        /// <summary>The managed thread id of the current worker (tests), or -1.</summary>
        public int WorkerThreadId => worker != null ? worker.ManagedThreadId : -1;

        /// <summary>
        /// The outcome of one call: done (with the value), or the fault. The fault's AlgorithmId and SimSeconds are
        /// left for the caller to fill in.
        /// </summary>
        public readonly struct Outcome<T>
        {
            public readonly T Value;
            public readonly AlgorithmFault Fault;
            public bool Ok => Fault == null;

            public Outcome(T value, AlgorithmFault fault)
            {
                Value = value;
                Fault = fault;
            }
        }

        public Outcome<ICleaningAlgorithm> Create(AlgorithmEntry entry) =>
            Run(AlgorithmCall.Create, InitializeBudgetSeconds, () => entry.Create());

        public Outcome<bool> Initialize(ICleaningAlgorithm algorithm, AlgorithmContext context) =>
            Run(AlgorithmCall.Initialize, InitializeBudgetSeconds, () =>
            {
                algorithm.Initialize(context);
                return true;
            });

        public Outcome<Vec2?> NextGoal(ICleaningAlgorithm algorithm, IBeliefMap map, Pose2 pose, GoalStatus status) =>
            Run(AlgorithmCall.NextGoal, NextGoalBudgetSeconds, () => algorithm.NextGoal(map, pose, status));

        /// <summary>
        /// Loading the Algorithms folder: creates an algorithm and reads its Name and SupportsSlam, within
        /// <see cref="ProbeBudgetSeconds"/>. A constructor or property that loops forever then skips that algorithm
        /// instead of freezing the load.
        /// </summary>
        public Outcome<(string name, bool supportsSlam)> Probe(Func<(string, bool)> createAndRead) =>
            Run(AlgorithmCall.Create, ProbeBudgetSeconds, createAndRead);

        /// <summary>Ends the worker thread (a fresh one is started by the next call). Safe to call twice.</summary>
        public void Dispose()
        {
            lock (gate)
            {
                quitting = true;
                generation++;
                Monitor.PulseAll(gate);
            }
            worker = null;
        }

        Outcome<T> Run<T>(AlgorithmCall call, float budgetSeconds, Func<T> work)
        {
            if (RunZombies >= MaxZombiesPerRun + 1 || LiveZombies >= MaxLiveZombies)
                return new Outcome<T>(default, GuardFault(call));

            EnsureWorker();

            lock (gate)
            {
                job = () => work();
                jobResult = null;
                jobError = null;
                jobDone = false;
                Monitor.PulseAll(gate);

                float budget = BudgetOverrideSeconds ?? budgetSeconds;
                bool unlimited = System.Diagnostics.Debugger.IsAttached;
                var deadline = DateTime.UtcNow.AddSeconds(budget);
                while (!jobDone)
                {
                    if (unlimited)
                    {
                        Monitor.Wait(gate);
                        continue;
                    }
                    var left = deadline - DateTime.UtcNow;
                    if (left <= TimeSpan.Zero || !Monitor.Wait(gate, left)) break; // out of time (jobDone is checked below)
                }

                if (jobDone)
                {
                    job = null;
                    if (jobError != null)
                        return new Outcome<T>(default, AlgorithmFault.FromException(jobError, call, null, 0f));
                    return new Outcome<T>((T)jobResult, null);
                }

                // Over budget: this worker is finished with, whatever it does next.
                job = null;
                generation++;
            }

            bool stopped = StopHungWorker();
            var fault = new AlgorithmFault
            {
                Kind = AlgorithmFaultKind.Hang,
                Call = call,
                Message = $"no reply within {(BudgetOverrideSeconds ?? budgetSeconds):0.#} s" +
                          (stopped ? "; it was stopped" : "; it would not stop and was abandoned"),
            };

            // A second thread that would not stop in the same run: end the run (see the class notes).
            if (!stopped && RunZombies > MaxZombiesPerRun)
            {
                fault.Kind = AlgorithmFaultKind.GuardTripped;
                fault.Message = $"hung in {call}, and its thread would not stop for the second time this run. " +
                                "The run is ended so no more threads are left running.";
            }
            return new Outcome<T>(default, fault);
        }

        AlgorithmFault GuardFault(AlgorithmCall call) => new AlgorithmFault
        {
            Kind = AlgorithmFaultKind.GuardTripped,
            Call = call,
            Message = RunZombies > MaxZombiesPerRun
                ? "an earlier call in this run left a thread that would not stop; no more calls are made."
                : $"{LiveZombies} algorithm threads that would not stop are still running in this program. " +
                  "No more algorithm calls are started until the program is restarted.",
        };

        void EnsureWorker()
        {
            if (worker != null && worker.IsAlive) return;

            int mine;
            lock (gate)
            {
                quitting = false;
                mine = generation;
            }

            worker = new Thread(() => WorkerLoop(mine), WorkerStackBytes)
            {
                IsBackground = true,
                Name = "Cleaning algorithm",
            };
            worker.Start();
        }

        void WorkerLoop(int mine)
        {
            while (true)
            {
                Func<object> work;
                lock (gate)
                {
                    while (job == null && !quitting && generation == mine) Monitor.Wait(gate);
                    if (quitting || generation != mine) return;
                    work = job;
                }

                object result = null;
                Exception error = null;
                try
                {
                    result = work();
                }
                catch (ThreadAbortException)
                {
                    throw; // stopped as hung: the main thread has already moved on
                }
                catch (Exception exception)
                {
                    error = exception;
                }

                lock (gate)
                {
                    if (generation != mine) return; // given up on while running: don't report
                    jobResult = result;
                    jobError = error;
                    jobDone = true;
                    job = null;
                    Monitor.PulseAll(gate);
                }
            }
        }

        /// <summary>Aborts the hung worker and waits briefly for it to end. False if it wouldn't (it is now a zombie).</summary>
        bool StopHungWorker()
        {
            var hung = worker;
            worker = null;
            if (hung == null) return true;

            try
            {
                hung.Abort(); // supported on Mono, which this project builds with
            }
            catch (PlatformNotSupportedException)
            {
                // IL2CPP / .NET Core: no abort. The thread can only be abandoned.
            }
            catch (ThreadStateException)
            {
                // Suspended or not started: nothing more to do.
            }

            if (hung.Join(TimeSpan.FromSeconds(AbortWaitOverrideSeconds ?? AbortWaitSeconds))) return true;

            RunZombies++;
            lock (zombieLock) zombies.Add(hung);
            UnityEngine.Debug.LogError($"An algorithm thread would not stop after it hung and was abandoned " +
                                       $"({RunZombies} this run, {LiveZombies} in the program). It keeps using a CPU core until the program closes.");
            return false;
        }
    }
}
