using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

namespace RobotVacuum.LevelEditor.Cloud
{
    /// <summary>
    /// Runs the vacuumctl command-line tool (cloud/vacuumctl) for the Settings page, as its own process. The GUI never
    /// talks to AWS itself: the tool does, using the cloud connection file, so the simulator holds no AWS credentials
    /// and needs no AWS libraries (C.12, 4.8). Only the connection file's path is passed, never a credential.
    ///
    /// Output arrives on background threads and is queued; call <see cref="Drain"/> from the main thread (a UI
    /// Toolkit scheduler does this), so no Unity API is touched off the main thread.
    /// </summary>
    public sealed class VacuumctlProcess
    {
        readonly Process process;
        readonly ConcurrentQueue<string> lines = new ConcurrentQueue<string>();
        readonly StringBuilder stdout = new StringBuilder();
        readonly bool captureStdout;
        volatile bool exited;
        int exitCode = -1;

        /// <summary>A short name for what is running ("Test connection"), for the page.</summary>
        public string Title { get; }

        public bool HasExited => exited;
        public int ExitCode => exitCode;

        /// <summary>With captureStdout, standard output (the --json result). Read it after <see cref="HasExited"/>.</summary>
        public string StandardOutput { get { lock (stdout) return stdout.ToString(); } }

        VacuumctlProcess(Process process, string title, bool captureStdout)
        {
            this.process = process;
            Title = title;
            this.captureStdout = captureStdout;
        }

        /// <summary>
        /// Starts <c>python -m vacuumctl --connection &lt;file&gt; &lt;arguments&gt;</c> in the cloud folder. Null with
        /// <paramref name="problem"/> in plain words if it can't start.
        /// </summary>
        public static VacuumctlProcess Start(CloudConnection connection, string connectionPath, string title,
            IList<string> arguments, bool captureStdout, out string problem)
        {
            problem = null;
            string folder = FindCloudFolder(connection, out string folderProblem);
            if (folder == null)
            {
                problem = folderProblem;
                return null;
            }

            var all = new List<string> { "-m", "vacuumctl", "--connection", Path.GetFullPath(connectionPath) };
            all.AddRange(arguments);
            string python = PythonCommand(connection);

            var start = new ProcessStartInfo
            {
                FileName = python,
                Arguments = JoinArguments(all),
                WorkingDirectory = folder,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true, // closed at once, so a prompt can never hang the page
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            start.EnvironmentVariables["PYTHONUNBUFFERED"] = "1";
            start.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
            start.EnvironmentVariables["PYTHONDONTWRITEBYTECODE"] = "1";

            var process = new Process { StartInfo = start };
            var running = new VacuumctlProcess(process, title, captureStdout);
            process.OutputDataReceived += (_, e) => running.OnOutput(e.Data, isError: false);
            process.ErrorDataReceived += (_, e) => running.OnOutput(e.Data, isError: true);

            try
            {
                process.Start();
            }
            catch (Exception exception) when (exception is Win32Exception || exception is InvalidOperationException)
            {
                problem = $"Couldn't start Python (\"{python}\"): {exception.Message}. Install Python 3.10 or newer and run " +
                          "pip install -r cloud/vacuumctl/requirements.txt, or set the Python command under Command-line tool.";
                return null;
            }

            process.StandardInput.Close();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            Task.Run(() =>
            {
                // The no-argument WaitForExit also waits for the redirected output to be read to the end.
                try { process.WaitForExit(); running.exitCode = process.ExitCode; }
                catch (Exception) { running.exitCode = -1; }
                running.exited = true;
            });
            return running;
        }

        void OnOutput(string line, bool isError)
        {
            if (line == null) return;
            if (captureStdout && !isError)
            {
                lock (stdout) stdout.AppendLine(line);
                return;
            }
            lines.Enqueue(line);
        }

        /// <summary>Hands every queued output line to <paramref name="onLine"/>. Main thread only.</summary>
        public void Drain(Action<string> onLine)
        {
            while (lines.TryDequeue(out string line)) onLine(line);
        }

        public void Kill()
        {
            try
            {
                if (!process.HasExited) process.Kill();
            }
            catch (Exception exception)
            {
                UnityEngine.Debug.LogWarning($"Settings: could not stop vacuumctl: {exception.Message}");
            }
        }

        // ---------------------------------------------------------------- finding the tool

        /// <summary>The Python command: the one set on the Settings page, else py on Windows and python3 elsewhere.</summary>
        public static string PythonCommand(CloudConnection connection)
        {
            string set = connection.tool.python?.Trim();
            if (!string.IsNullOrEmpty(set)) return set;
            return Application.platform == RuntimePlatform.WindowsPlayer || Application.platform == RuntimePlatform.WindowsEditor
                ? "py" : "python3";
        }

        /// <summary>
        /// The repository's cloud folder (the one holding vacuumctl): the one set on the Settings page, else, in the
        /// Unity Editor, the cloud folder beside the Unity project, and in a build, a cloud folder next to the app.
        /// </summary>
        public static string FindCloudFolder(CloudConnection connection, out string problem)
        {
            problem = null;
            var candidates = new List<string>();
            string set = connection.tool.cloudFolder?.Trim();
            if (!string.IsNullOrEmpty(set)) candidates.Add(set);
            else
            {
                string project = Path.GetDirectoryName(Application.dataPath); // the Unity project, or the build folder
                candidates.Add(Path.Combine(project, "..", "cloud"));
                candidates.Add(Path.Combine(project, "cloud"));
            }

            foreach (string candidate in candidates)
            {
                string full = Path.GetFullPath(candidate);
                if (File.Exists(Path.Combine(full, "vacuumctl", "__main__.py"))) return full;
            }

            problem = string.IsNullOrEmpty(set)
                ? "The cloud tool (vacuumctl) wasn't found next to the app. Set the cloud folder under Command-line tool."
                : $"{set} doesn't contain vacuumctl. Choose the repository's cloud folder.";
            return null;
        }

        /// <summary>Quotes arguments the way Windows programs (and Mono on other systems) split them back apart.</summary>
        public static string JoinArguments(IEnumerable<string> arguments)
        {
            var text = new StringBuilder();
            foreach (string argument in arguments)
            {
                if (text.Length > 0) text.Append(' ');
                text.Append(Quote(argument ?? string.Empty));
            }
            return text.ToString();
        }

        static string Quote(string argument)
        {
            if (argument.Length > 0 && argument.IndexOfAny(new[] { ' ', '\t', '\n', '"' }) < 0) return argument;

            var quoted = new StringBuilder("\"");
            int backslashes = 0;
            foreach (char c in argument)
            {
                if (c == '\\') { backslashes++; continue; }
                if (c == '"') quoted.Append('\\', backslashes * 2 + 1);
                else quoted.Append('\\', backslashes);
                backslashes = 0;
                quoted.Append(c);
            }
            quoted.Append('\\', backslashes * 2);
            return quoted.Append('"').ToString();
        }
    }
}
