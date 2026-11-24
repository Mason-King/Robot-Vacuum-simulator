using System;
using System.Diagnostics;
using System.Threading;

namespace RobotVacuum.Sim
{
    /// <summary>
    /// Stops a movement algorithm that never returns from one call (an endless loop in a custom algorithm's
    /// NextGoal or Initialize), so the run fails with a plain message instead of freezing the GUI or hanging a
    /// headless job for good.
    ///
    /// <see cref="MovementBrain"/> brackets each call into the algorithm with <see cref="Enter"/> and
    /// <see cref="Exit"/>. A background thread checks every quarter second; once one call has gone on for
    /// <see cref="LimitSeconds"/> it interrupts the main thread with a <see cref="ThreadAbortException"/>, which
    /// lands inside the algorithm call. The brain's catch hands it to <see cref="ClaimTimeout"/>, which cancels the
    /// abort (the main thread carries on) and the run is failed like any other algorithm error.
    ///
    /// The check and the interrupt happen under one lock that <see cref="Exit"/> also takes, so the interrupt can
    /// only be sent while the main thread is still between Enter and Exit; once sent, it is delivered inside the
    /// same try block. This relies on Mono's Thread.Abort (the scripting backend used for every build of this
    /// project); where Abort is unsupported the watchdog does nothing and the headless stall guard is the backstop.
    /// </summary>
    public static class AlgorithmWatchdog
    {
        /// <summary>How long one call into an algorithm may take before it is stopped, in real seconds.</summary>
        public const float DefaultLimitSeconds = 20f;

        static readonly object Gate = new object();
        static float limitSeconds = DefaultLimitSeconds;
        static Thread watcher;
        static Thread caller;
        static long enteredAt;   // Stopwatch timestamp of the call in progress; 0 when none
        static int depth;
        static bool abortSent;
        static bool unsupported;

        /// <summary>How long one call may take before it is stopped. 0 or less turns the watchdog off.</summary>
        public static float LimitSeconds
        {
            get { lock (Gate) return limitSeconds; }
            set { lock (Gate) limitSeconds = value; }
        }

        /// <summary>True while a call into an algorithm is in progress on some thread.</summary>
        public static bool InsideAlgorithm
        {
            get { lock (Gate) return depth > 0; }
        }

        /// <summary>A call into the algorithm starts on this thread.</summary>
        public static void Enter()
        {
            lock (Gate)
            {
                if (depth++ == 0)
                {
                    abortSent = false; // an earlier stop that was never claimed must not switch the watchdog off
                    caller = Thread.CurrentThread;
                    enteredAt = Stopwatch.GetTimestamp();
                }
                EnsureWatcher();
            }
        }

        /// <summary>The call into the algorithm has returned (or thrown). Always call it from a finally.</summary>
        public static void Exit()
        {
            lock (Gate)
            {
                if (depth > 0 && --depth == 0)
                {
                    caller = null;
                    enteredAt = 0;
                }
            }
        }

        /// <summary>
        /// True when <paramref name="exception"/> is this watchdog stopping a call that took too long. The abort is
        /// cancelled, so the caller can record the failure and carry on. False for any other exception.
        /// </summary>
        public static bool ClaimTimeout(Exception exception)
        {
            if (!(exception is ThreadAbortException)) return false;
            lock (Gate)
            {
                if (!abortSent) return false;
                abortSent = false;
            }

            try { Thread.ResetAbort(); }
            catch (Exception) { /* not aborting after all, or not allowed: nothing more to undo */ }
            return true;
        }

        /// <summary>The message for a run stopped by the watchdog.</summary>
        public static string TimeoutMessage(string call) =>
            $"{call} did not return within {LimitSeconds:0} s, so the run was stopped. " +
            "The algorithm is probably stuck in an endless loop; check its loops and the conditions that end them.";

        static void EnsureWatcher()
        {
            if (watcher != null || unsupported) return;
            watcher = new Thread(Watch) { IsBackground = true, Name = "Algorithm watchdog", Priority = ThreadPriority.AboveNormal };
            watcher.Start();
        }

        static void Watch()
        {
            while (true)
            {
                Thread.Sleep(250);
                lock (Gate)
                {
                    if (depth == 0 || abortSent || caller == null || limitSeconds <= 0f) continue;
                    double seconds = (Stopwatch.GetTimestamp() - enteredAt) / (double)Stopwatch.Frequency;
                    if (seconds < limitSeconds) continue;

                    try
                    {
                        caller.Abort();
                        abortSent = true;
                    }
                    catch (Exception)
                    {
                        // Thread.Abort is not available on this runtime: stop watching.
                        unsupported = true;
                        watcher = null;
                        return;
                    }
                }
            }
        }
    }
}
