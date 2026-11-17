using System;
using System.IO;
using System.Text;
using UnityEngine;

namespace RobotVacuum.Sim
{
    /// <summary>
    /// Copies everything the Console receives during one run into <c>console_log.txt</c> in the run's folder: the
    /// algorithm's decision log, planner warnings, the finishing report, and any warnings or errors (errors with their
    /// stack traces). Started when the run's folder is made, stopped when its result is written. Lines are written as
    /// they arrive, so a run that dies part-way still leaves its log.
    /// </summary>
    public sealed class RunLog : IDisposable
    {
        public const string FileName = "console_log.txt";

        readonly object gate = new object();
        StreamWriter writer;

        public RunLog(string runFolder)
        {
            writer = new StreamWriter(Path.Combine(runFolder, FileName), false, new UTF8Encoding(false)) { AutoFlush = true };
            Application.logMessageReceivedThreaded += OnLog;
        }

        void OnLog(string message, string stackTrace, LogType type)
        {
            lock (gate)
            {
                if (writer == null) return;
                try
                {
                    if (type != LogType.Log) writer.Write("[" + type + "] ");
                    writer.WriteLine(message);
                    if ((type == LogType.Error || type == LogType.Exception || type == LogType.Assert) && !string.IsNullOrEmpty(stackTrace))
                        writer.WriteLine(stackTrace.TrimEnd());
                }
                catch (IOException)
                {
                    // The disk went away mid-run: stop logging rather than fail the run.
                    writer = null;
                }
            }
        }

        /// <summary>Stops capturing and closes the file. Safe to call more than once.</summary>
        public void Dispose()
        {
            Application.logMessageReceivedThreaded -= OnLog;
            lock (gate)
            {
                if (writer == null) return;
                try { writer.Dispose(); }
                catch (IOException) { }
                writer = null;
            }
        }
    }
}
