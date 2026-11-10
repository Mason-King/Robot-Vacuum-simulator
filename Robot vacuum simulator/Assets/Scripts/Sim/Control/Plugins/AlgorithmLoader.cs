using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using RobotVacuum.AlgorithmApi;
using UnityEngine;

namespace RobotVacuum.Sim.Control
{
    /// <summary>What one scan of the Algorithms folder found.</summary>
    public sealed class AlgorithmLoadReport
    {
        public string folder;
        public DateTime when = DateTime.Now;

        /// <summary>True when the scan was refused because a run is in progress; the catalog was left as it was.</summary>
        public bool refused;
        public string refusedReason;

        /// <summary>A problem with the folder itself (missing, unreadable), or null.</summary>
        public string folderProblem;

        public readonly List<UnitReport> units = new List<UnitReport>();

        public int AlgorithmCount => units.Sum(u => u.algorithms.Count);
        public int FailedUnits => units.Count(u => u.errors.Count > 0);

        /// <summary>The whole report as text, one line per finding, for a log, a panel or a file.</summary>
        public string Text()
        {
            var text = new StringBuilder();
            text.AppendLine($"Algorithms folder: {folder}");
            if (refused) text.AppendLine($"Not rescanned: {refusedReason}");
            if (folderProblem != null) text.AppendLine(folderProblem);
            foreach (var unit in units)
            {
                string state = unit.errors.Count > 0 ? "FAILED" : "ok";
                string cache = unit.fromCache ? ", unchanged" : unit.milliseconds > 0 ? $", {unit.milliseconds:F0} ms" : "";
                text.AppendLine($"  {unit.name} ({unit.kind}{cache}): {state}");
                foreach (var name in unit.algorithms) text.AppendLine($"    + {name}");
                foreach (var error in unit.errors) text.AppendLine($"    error: {error}");
                foreach (var warning in unit.warnings) text.AppendLine($"    warning: {warning}");
            }
            text.AppendLine($"{AlgorithmCount} algorithm(s) loaded from the folder; {FailedUnits} file(s) or folder(s) failed.");
            return text.ToString();
        }
    }

    /// <summary>One file or folder in the Algorithms folder.</summary>
    public sealed class UnitReport
    {
        /// <summary>Its path relative to the Algorithms folder.</summary>
        public string name;

        /// <summary>"source" (one .cs file), "folder" (all .cs files in a subfolder, compiled together) or "dll".</summary>
        public string kind;

        public bool fromCache;
        public double milliseconds;

        /// <summary>The names of the algorithms it added to the catalog.</summary>
        public readonly List<string> algorithms = new List<string>();
        public readonly List<string> errors = new List<string>();
        public readonly List<string> warnings = new List<string>();
    }

    /// <summary>
    /// Finds algorithms in the Algorithms folder, compiles or loads them, and checks them.
    ///
    /// The folder sits next to the simulator: beside the .exe in a build, and in the project folder (beside Assets)
    /// in the editor. In it:
    ///   * each .cs file is compiled on its own;
    ///   * each subfolder is one algorithm project: all the .cs files in it (and below) are compiled together;
    ///   * each .dll is loaded as it is (it may reference only the API and base .NET);
    ///   * anything whose name starts with '_' or '.' is skipped (the template is _Template.cs for this reason).
    ///
    /// A scan happens only between runs, never during one (see <see cref="AlgorithmCatalog.Rescan"/>). Each unit
    /// is hashed; an unchanged one reuses what was loaded before, because Mono cannot unload an assembly.
    /// </summary>
    public static class AlgorithmLoader
    {
        public const string FolderName = "Algorithms";

        /// <summary>The Algorithms folder: beside the .exe in a build, beside Assets in the editor.</summary>
        public static string DefaultFolder => Path.Combine(Path.GetDirectoryName(Application.dataPath) ?? ".", FolderName);

        sealed class Loaded
        {
            public Assembly assembly;
            public List<string> errors;
            public List<string> warnings;
            public double milliseconds;
        }

        static readonly Dictionary<string, Loaded> cache = new Dictionary<string, Loaded>();

        /// <summary>
        /// Scans <paramref name="folder"/> and returns the algorithms found, each checked and given a unique id.
        /// <paramref name="taken"/> are the entries already in the catalog (the built-ins): a plugin may not reuse
        /// their ids or names. Never throws; every problem is in the report.
        /// </summary>
        public static List<AlgorithmEntry> Load(string folder, IEnumerable<AlgorithmEntry> taken, out AlgorithmLoadReport report)
        {
            report = new AlgorithmLoadReport { folder = folder };
            var found = new List<AlgorithmEntry>();
            var ids = new HashSet<string>(taken.Select(e => e.Id), StringComparer.Ordinal);
            var names = new HashSet<string>(taken.Select(e => e.Name), StringComparer.OrdinalIgnoreCase);

            if (!Directory.Exists(folder))
            {
                report.folderProblem = "The folder does not exist, so only the built-in algorithms are available.";
                return found;
            }

            List<(string name, string kind, List<string> files)> units;
            try
            {
                units = FindUnits(folder);
            }
            catch (Exception exception)
            {
                report.folderProblem = $"The folder could not be read: {exception.Message}";
                return found;
            }

            foreach (var (name, kind, files) in units)
            {
                var unit = new UnitReport { name = name, kind = kind };
                report.units.Add(unit);
                try
                {
                    var assembly = LoadUnit(folder, name, kind, files, unit);
                    if (assembly != null) Register(assembly, name, unit, ids, names, found);
                }
                catch (Exception exception)
                {
                    unit.errors.Add($"{exception.GetType().Name}: {exception.Message}");
                }
            }
            return found;
        }

        static List<(string, string, List<string>)> FindUnits(string folder)
        {
            var units = new List<(string, string, List<string>)>();

            foreach (var file in Directory.GetFiles(folder).OrderBy(f => f, StringComparer.Ordinal))
            {
                string fileName = Path.GetFileName(file);
                if (Skipped(fileName)) continue;
                string extension = Path.GetExtension(file).ToLowerInvariant();
                if (extension == ".cs") units.Add((fileName, "source", new List<string> { file }));
                else if (extension == ".dll") units.Add((fileName, "dll", new List<string> { file }));
            }

            foreach (var directory in Directory.GetDirectories(folder).OrderBy(d => d, StringComparer.Ordinal))
            {
                string directoryName = Path.GetFileName(directory);
                if (Skipped(directoryName)) continue;
                var files = Directory.GetFiles(directory, "*.cs", SearchOption.AllDirectories)
                    .Where(f => !RelativeParts(directory, f).Any(Skipped))
                    .OrderBy(f => f, StringComparer.Ordinal)
                    .ToList();
                units.Add((directoryName + "/", "folder", files));
            }
            return units;
        }

        static bool Skipped(string name) => name.StartsWith("_") || name.StartsWith(".");

        static IEnumerable<string> RelativeParts(string root, string path) =>
            path.Substring(root.Length).Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);

        static Assembly LoadUnit(string folder, string name, string kind, List<string> files, UnitReport unit)
        {
            if (files.Count == 0)
            {
                unit.warnings.Add("No .cs files in this folder.");
                return null;
            }

            var bytes = files.Select(File.ReadAllBytes).ToList();
            string hash = Hash(kind, files.Select(f => Relative(folder, f)).ToList(), bytes);

            if (cache.TryGetValue(hash, out var loaded))
            {
                unit.fromCache = true;
            }
            else
            {
                loaded = kind == "dll" ? LoadDll(bytes[0]) : CompileSources(folder, name, files, bytes);
                cache[hash] = loaded;
            }

            unit.errors.AddRange(loaded.errors);
            unit.warnings.AddRange(loaded.warnings);
            unit.milliseconds = unit.fromCache ? 0 : loaded.milliseconds;
            return loaded.errors.Count == 0 ? loaded.assembly : null;
        }

        static Loaded CompileSources(string folder, string name, List<string> files, List<byte[]> bytes)
        {
            var sources = new List<KeyValuePair<string, string>>();
            for (int i = 0; i < files.Count; i++)
                sources.Add(new KeyValuePair<string, string>(Relative(folder, files[i]), DecodeText(bytes[i])));

            var result = AlgorithmCompiler.Compile(name, sources);
            return new Loaded { assembly = result.assembly, errors = result.errors, warnings = result.warnings, milliseconds = result.milliseconds };
        }

        static Loaded LoadDll(byte[] bytes)
        {
            var loaded = new Loaded { errors = new List<string>(), warnings = new List<string>() };
            var clock = System.Diagnostics.Stopwatch.StartNew();
            Assembly assembly;
            try
            {
                assembly = Assembly.Load(bytes);
            }
            catch (BadImageFormatException)
            {
                loaded.errors.Add("Not a .NET assembly.");
                return loaded;
            }

            // A DLL is built elsewhere, so check what it reaches for: the API and base .NET only, as for source.
            string api = typeof(ICleaningAlgorithm).Assembly.GetName().Name;
            foreach (var reference in assembly.GetReferencedAssemblies())
            {
                if (reference.Name == api || AlgorithmCompiler.AllowedReferences.Contains(reference.Name)) continue;
                if (reference.Name.StartsWith("System.", StringComparison.Ordinal) && !reference.Name.StartsWith("System.Windows", StringComparison.Ordinal)) continue;
                loaded.errors.Add($"References {reference.Name}, which algorithms may not use (only {api} and base .NET).");
            }
            loaded.assembly = loaded.errors.Count == 0 ? assembly : null;
            loaded.milliseconds = clock.Elapsed.TotalMilliseconds;
            return loaded;
        }

        /// <summary>Checks every class in the assembly that implements the interface, and adds the good ones.</summary>
        static void Register(Assembly assembly, string unitName, UnitReport unit, HashSet<string> ids, HashSet<string> names, List<AlgorithmEntry> found)
        {
            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException exception)
            {
                unit.errors.Add("Some types could not be loaded: " + string.Join("; ", exception.LoaderExceptions.Where(e => e != null).Select(e => e.Message).Distinct()));
                return;
            }

            int before = found.Count;
            foreach (var type in types.Where(t => typeof(ICleaningAlgorithm).IsAssignableFrom(t)).OrderBy(t => t.FullName, StringComparer.Ordinal))
            {
                if (type.IsInterface || type.IsAbstract) continue; // a base class for others, not an algorithm itself
                string label = type.FullName;

                if (!type.IsVisible)
                {
                    unit.errors.Add($"{label}: must be public.");
                    continue;
                }
                if (type.ContainsGenericParameters)
                {
                    unit.errors.Add($"{label}: must not be generic.");
                    continue;
                }
                var constructor = type.GetConstructor(Type.EmptyTypes);
                if (constructor == null)
                {
                    unit.errors.Add($"{label}: needs a public constructor with no parameters.");
                    continue;
                }

                string name;
                bool supportsSlam;
                try
                {
                    var probe = (ICleaningAlgorithm)constructor.Invoke(null);
                    name = probe.Name?.Trim();
                    supportsSlam = probe.SupportsSlam;
                }
                catch (Exception exception)
                {
                    var inner = exception is TargetInvocationException && exception.InnerException != null ? exception.InnerException : exception;
                    unit.errors.Add($"{label}: creating it failed: {inner.GetType().Name}: {inner.Message}");
                    continue;
                }

                if (string.IsNullOrEmpty(name))
                {
                    unit.errors.Add($"{label}: Name is empty.");
                    continue;
                }
                if (!ids.Add(label))
                {
                    unit.errors.Add($"{label}: another algorithm already uses this class name (namespace + class).");
                    continue;
                }
                if (!names.Add(name))
                {
                    unit.errors.Add($"{label}: the name '{name}' is already taken by another algorithm.");
                    continue;
                }

                var algorithmType = type;
                found.Add(new AlgorithmEntry(label, name, supportsSlam, unitName, null,
                    () => (ICleaningAlgorithm)Activator.CreateInstance(algorithmType)));
                unit.algorithms.Add(name + (supportsSlam ? " (SLAM)" : ""));
            }

            if (found.Count == before && unit.errors.Count == 0)
                unit.warnings.Add("No public class implements ICleaningAlgorithm.");
        }

        static string Relative(string folder, string path) =>
            path.Substring(folder.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Replace('\\', '/');

        /// <summary>Text from a file: UTF-8 (with or without a byte-order mark), or UTF-16 with one.</summary>
        static string DecodeText(byte[] bytes)
        {
            using (var reader = new StreamReader(new MemoryStream(bytes), new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true))
                return reader.ReadToEnd();
        }

        static string Hash(string kind, List<string> names, List<byte[]> contents)
        {
            using (var sha = SHA256.Create())
            {
                var buffer = new MemoryStream();
                void Write(byte[] data) => buffer.Write(data, 0, data.Length);
                Write(Encoding.UTF8.GetBytes(kind + "\n"));
                for (int i = 0; i < names.Count; i++)
                {
                    Write(Encoding.UTF8.GetBytes(names[i] + "\n" + contents[i].Length + "\n"));
                    Write(contents[i]);
                }
                return BitConverter.ToString(sha.ComputeHash(buffer.ToArray())).Replace("-", "");
            }
        }
    }
}
