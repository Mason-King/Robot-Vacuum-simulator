using System;
using System.Collections.Concurrent;
using System.Threading;
using RobotVacuum.AlgorithmApi;

namespace RobotVacuum.Sim.Control
{
    /// <summary>What went wrong with an algorithm, when something did.</summary>
    public enum AlgorithmFault
    {
        None = 0,

        /// <summary>It threw an exception (including running out of memory).</summary>
        Crash = 1,

        /// <summary>A call did not return within its time budget: an infinite loop, a deadlock, or far too slow.</summary>
        Hang = 2,

        /// <summary>It returned something the controller cannot drive to: a non-finite or impossibly distant waypoint, bad drive requests.</summary>
        InvalidMove = 3,

        /// <summary>It kept running but the robot made no progress for a long time (the body's watchdog stopped it).</summary>
        Stalled = 4,
    }

    /// <summary>
    /// Runs an algorithm's code so that its failures stay inside the run. Every call goes to a dedicated worker
    /// thread while the simulation waits (so the run stays deterministic: the algorithm is never called concurrently).
    /// <list type="bullet">
    /// <item>An exception is caught and reported as a <see cref="AlgorithmFault.Crash"/>.</item>
    /// <item>A call that overruns its budget is abandoned as a <see cref="AlgorithmFault.Hang"/>: the simulation carries
    /// on, the worker is aborted where the runtime allows it (the Mono editor and player), and is otherwise left as
    /// a background thread that dies with the process. The algorithm instance is never called again.</item>
    /// <item>Once faulted, the sandbox refuses every further call, so the run can end cleanly.</item>
    /// </list>
    /// Plugins compile against the algorithm API and base .NET only (no UnityEngine), so running them off the main
    /// thread is safe. Not covered: a stack overflow, which .NET cannot recover from on any thread.
    /// </summary>
    public sealed class AlgorithmGuard : IDisposable
    {
        /// <summary>Real-time budget for constructing and initializing an algorithm, seconds.</summary>
        public static float InitializeBudgetSeconds = 5f;

        /// <summary>Real-time budget for one NextGoal, seconds. Generous: a normal call takes well under a millisecond.</summary>
        public static float NextGoalBudgetSeconds = 2f;

        /// <summary>Real-time budget for reading Name / SupportsSlam when the algorithm list is loaded, seconds.</summary>
        public static float ProbeBudgetSeconds = 2f;

        /// <summary>Waypoints farther than this from the believed pose are rejected as invalid moves.</summary>
        public const float MaxWaypointDistanceMeters = 200f;

        /// <summary>Stack for the worker: deep (but finite) recursion in an algorithm has room to work.</summary>
        const int WorkerStackBytes = 16 * 1024 * 1024;

        /// <summary>While a debugger is attached, calls get unlimited time (a breakpoint is not a hang). Tests switch this off.</summary>
        internal static bool UnlimitedUnderDebugger = true;

        static int abandoned;

        /// <summary>Hung workers that could not be aborted and are still running (each keeps a CPU core busy).</summary>
        public static int AbandonedThreads => Volatile.Read(ref abandoned);

        readonly string label;
        readonly AutoResetEvent work = new AutoResetEvent(false);
        readonly ManualResetEventSlim done = new ManualResetEventSlim(false);
        Thread worker;
        Func<object> job;
        object result;
        Exception error;
        bool disposed;

        public AlgorithmGuard(string label)
        {
            this.label = string.IsNullOrEmpty(label) ? "algorithm" : label;
        }

        public bool Faulted => Fault != AlgorithmFault.None;
        public AlgorithmFault Fault { get; private set; }
        public string FaultMessage { get; private set; }

        /// <summary>
        /// Runs <paramref name="call"/> on the worker, waiting at most <paramref name="budgetSeconds"/> of real time.
        /// False when it threw, overran, or the sandbox had already faulted; <see cref="Fault"/> says which.
        /// </summary>
        public bool TryRun<T>(string what, Func<T> call, float budgetSeconds, out T value)
        {
            value = default;
            if (disposed) throw new ObjectDisposedException(nameof(AlgorithmGuard));
            if (Faulted) return false;

            EnsureWorker();
            job = () => call();
            result = null;
            error = null;
            done.Reset();
            work.Set();

            // A debugger paused inside the algorithm is not a hang.
            bool finished = budgetSeconds <= 0f || (UnlimitedUnderDebugger && System.Diagnostics.Debugger.IsAttached)
                ? Wait(Timeout.Infinite)
                : Wait((int)Math.Ceiling(budgetSeconds * 1000f));

            if (!finished)
            {
                Abandon();
                SetFault(AlgorithmFault.Hang,
                    $"{what} did not return within {budgetSeconds:0.##} s (an infinite loop or a deadlock?). The run was stopped; the simulator carries on.");
                return false;
            }

            if (error != null)
            {
                SetFault(AlgorithmFault.Crash, $"{what} threw {error.GetType().Name}: {error.Message}");
                return false;
            }

            value = (T)result;
            return true;
        }

        /// <summary>Runs an action the same way as <see cref="TryRun{T}"/>.</summary>
        public bool TryRun(string what, Action call, float budgetSeconds) =>
            TryRun(what, () => { call(); return true; }, budgetSeconds, out bool _);

        /// <summary>Records a fault found outside a call (an invalid move); later calls are refused.</summary>
        public void SetFault(AlgorithmFault fault, string message)
        {
            if (Faulted || fault == AlgorithmFault.None) return;
            Fault = fault;
            FaultMessage = message;
        }

        bool Wait(int milliseconds) => done.Wait(milliseconds);

        void EnsureWorker()
        {
            if (worker != null) return;
            worker = new Thread(Loop, WorkerStackBytes)
            {
                IsBackground = true, // never keeps the process alive
                Name = $"Algorithm sandbox: {label}",
            };
            worker.Start();
        }

        void Loop()
        {
            while (true)
            {
                work.WaitOne();
                if (disposed) return;
                try
                {
                    result = job();
                }
                catch (ThreadAbortException)
                {
                    return; // abandoned after a hang; the runtime re-raises and the thread ends
                }
                catch (Exception exception)
                {
                    error = exception is System.Reflection.TargetInvocationException && exception.InnerException != null
                        ? exception.InnerException
                        : exception;
                }
                done.Set();
            }
        }

        void Abandon()
        {
            var hung = worker;
            worker = null;
            if (hung == null) return;

            bool stopped = false;
            try
            {
#pragma warning disable SYSLIB0006, CS0618 // Thread.Abort: supported by Mono (the editor and the Mono player), refused elsewhere
                hung.Abort();
#pragma warning restore SYSLIB0006, CS0618
                stopped = hung.Join(500);
            }
            catch (Exception)
            {
                // PlatformNotSupportedException (IL2CPP / .NET Core) and friends: leave it running in the background.
            }

            if (!stopped) Interlocked.Increment(ref abandoned);
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (worker != null) work.Set(); // an idle worker wakes, sees disposed and exits
            worker = null;
        }

        // ---------------------------------------------------------------- output checks

        /// <summary>
        /// Why <paramref name="goal"/> and the drive requests set with it cannot be driven, or null when they can.
        /// Null (no goal right now) is always allowed.
        /// </summary>
        public static string CheckMove(Vec2? goal, Pose2 believedPose, DriveRequests drive)
        {
            if (goal.HasValue)
            {
                var g = goal.Value;
                if (!IsFinite(g.x) || !IsFinite(g.y))
                    return $"NextGoal returned a non-finite waypoint ({g.x}, {g.y}).";

                float dx = g.x - believedPose.position.x, dy = g.y - believedPose.position.y;
                float distance = (float)Math.Sqrt(dx * dx + dy * dy);
                if (distance > MaxWaypointDistanceMeters)
                    return $"NextGoal returned a waypoint {distance:0} m away ({g.x:0.##}, {g.y:0.##}); waypoints more than {MaxWaypointDistanceMeters:0} m from the robot are outside any floor plan.";
            }

            if (drive != null)
            {
                if (!Enum.IsDefined(typeof(DriveMode), drive.Mode))
                    return $"DriveRequests.Mode was set to {(int)drive.Mode}, which is not a drive mode.";
                if (drive.Mode == DriveMode.Sweep && (!IsFinite(drive.SweepExposureMeters) || drive.SweepExposureMeters < 0f))
                    return $"DriveRequests.SweepExposureMeters was set to {drive.SweepExposureMeters}; it must be a finite length, 0 or more.";
            }
            return null;
        }

        static bool IsFinite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);
    }

    /// <summary>Hands log lines from the sandbox's worker to the main thread, which writes them after each call.</summary>
    public sealed class SandboxLog
    {
        const int MaxPending = 500;
        readonly ConcurrentQueue<string> pending = new ConcurrentQueue<string>();
        int dropped;

        public void Add(string message)
        {
            if (pending.Count >= MaxPending) { Interlocked.Increment(ref dropped); return; }
            pending.Enqueue(message);
        }

        public void Flush(Action<string> write)
        {
            while (pending.TryDequeue(out string message)) write(message);
            int lost = Interlocked.Exchange(ref dropped, 0);
            if (lost > 0) write($"({lost} more log line(s) dropped: the algorithm logged too much in one call)");
        }
    }
}
