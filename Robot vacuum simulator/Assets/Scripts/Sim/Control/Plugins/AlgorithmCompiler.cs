using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using RobotVacuum.AlgorithmApi;

namespace RobotVacuum.Sim.Control
{
    /// <summary>
    /// Compiles algorithm source text (C#) into an assembly in memory, with Roslyn, and loads it.
    ///
    /// The source is compiled against the algorithm API and the base .NET library ONLY (<see cref="AllowedReferences"/>):
    /// not UnityEngine, not the simulator. So a plugin cannot reach into the simulation, only use what the API hands
    /// it (requirement 2.3), and `using UnityEngine;` is a compile error that says so.
    ///
    /// Roslyn comes from the com.unity.pipeline package (Microsoft.CodeAnalysis 3.11), which ships in player builds.
    /// 3.11 means C# 9 at most: no file-scoped namespaces or global usings.
    ///
    /// Mono cannot unload an assembly, so every compile that succeeds stays in memory until the simulator closes.
    /// <see cref="AlgorithmLoader"/> caches by content, so an unchanged file is never compiled twice.
    /// </summary>
    public static class AlgorithmCompiler
    {
        /// <summary>The newest C# Roslyn 3.11 understands.</summary>
        public const string LanguageVersionName = "C# 9";

        /// <summary>The assemblies a plugin may reference, besides the API itself (base .NET only).</summary>
        public static readonly string[] AllowedReferences =
        {
            "mscorlib", "netstandard", "System", "System.Core", "System.Runtime", "System.Collections",
            "System.Linq", "System.Numerics", "System.Runtime.Extensions",
        };

        /// <summary>The result of one compile.</summary>
        public sealed class Result
        {
            /// <summary>The loaded assembly, or null if it did not compile.</summary>
            public Assembly assembly;

            /// <summary>Errors, one line each: "file(line,column): CS0000 message".</summary>
            public readonly List<string> errors = new List<string>();

            /// <summary>Warnings, same format.</summary>
            public readonly List<string> warnings = new List<string>();

            /// <summary>Real milliseconds the compile took (the first one of a session includes about a second of Roslyn warm-up).</summary>
            public double milliseconds;

            public bool Succeeded => assembly != null;
        }

        static List<object> references; // MetadataReference, typed as object so this class loads even without Roslyn
        static string referenceProblem;

        /// <summary>
        /// Compiles <paramref name="sources"/> (file name → text) as one assembly. Never throws: a missing compiler or
        /// a broken source both come back as errors in the result.
        /// </summary>
        public static Result Compile(string unitName, IReadOnlyList<KeyValuePair<string, string>> sources)
        {
            var result = new Result();
            var clock = Stopwatch.StartNew();
            try
            {
                CompileWithRoslyn(unitName, sources, result); // its own method, so a missing Roslyn DLL is caught here
            }
            catch (Exception exception) when (exception is FileNotFoundException || exception is TypeLoadException ||
                                              exception is FileLoadException || exception is MissingMethodException)
            {
                result.assembly = null;
                result.errors.Add($"The C# compiler (Roslyn) could not be loaded: {exception.GetType().Name}: {exception.Message}");
            }
            catch (Exception exception)
            {
                result.assembly = null;
                result.errors.Add($"Compiling failed: {exception.GetType().Name}: {exception.Message}");
            }
            result.milliseconds = clock.Elapsed.TotalMilliseconds;
            return result;
        }

        static void CompileWithRoslyn(string unitName, IReadOnlyList<KeyValuePair<string, string>> sources, Result result)
        {
            var refs = References();
            if (referenceProblem != null) result.warnings.Add(referenceProblem);

            var parse = new Microsoft.CodeAnalysis.CSharp.CSharpParseOptions(Microsoft.CodeAnalysis.CSharp.LanguageVersion.CSharp9);
            var trees = new List<Microsoft.CodeAnalysis.SyntaxTree>();
            foreach (var source in sources)
                trees.Add(Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(source.Value ?? "", parse, source.Key, Encoding.UTF8));

            var options = new Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(
                Microsoft.CodeAnalysis.OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: Microsoft.CodeAnalysis.OptimizationLevel.Release,
                allowUnsafe: false)
                // Assembly-version unification notes (mscorlib vs netstandard): noise to an algorithm author.
                .WithSpecificDiagnosticOptions(new Dictionary<string, Microsoft.CodeAnalysis.ReportDiagnostic>
                {
                    { "CS1701", Microsoft.CodeAnalysis.ReportDiagnostic.Suppress },
                    { "CS1702", Microsoft.CodeAnalysis.ReportDiagnostic.Suppress },
                });

            // A fresh name every compile: Mono keeps every loaded assembly, and two with one name confuse type lookup.
            string assemblyName = "Algorithm_" + Sanitize(unitName) + "_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var compilation = Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create(assemblyName, trees,
                refs.Cast<Microsoft.CodeAnalysis.MetadataReference>(), options);

            using (var stream = new MemoryStream())
            {
                var emitted = compilation.Emit(stream);
                foreach (var diagnostic in emitted.Diagnostics)
                {
                    if (diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error) result.errors.Add(Describe(diagnostic));
                    else if (diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Warning) result.warnings.Add(Describe(diagnostic));
                }
                if (!emitted.Success)
                {
                    if (result.errors.Count == 0) result.errors.Add("Did not compile (no error was reported).");
                    if (result.errors.Any(e => e.Contains("UnityEngine")))
                        result.errors.Add("Algorithms cannot use UnityEngine: use the types in RobotVacuum.AlgorithmApi (Vec2, Pose2, IBeliefMap).");
                    return;
                }
                result.assembly = Assembly.Load(stream.ToArray());
            }
        }

        static string Describe(Microsoft.CodeAnalysis.Diagnostic diagnostic)
        {
            var span = diagnostic.Location.GetLineSpan();
            string file = string.IsNullOrEmpty(span.Path) ? "source" : span.Path;
            if (!diagnostic.Location.IsInSource) return $"{diagnostic.Id} {diagnostic.GetMessage()}";
            return $"{file}({span.StartLinePosition.Line + 1},{span.StartLinePosition.Character + 1}): {diagnostic.Id} {diagnostic.GetMessage()}";
        }

        /// <summary>The API assembly plus whichever of the allowed base assemblies can be found. Built once.</summary>
        static List<object> References()
        {
            if (references != null) return references;

            var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                string name = assembly.GetName().Name;
                if (!AllowedReferences.Contains(name) || paths.ContainsKey(name)) continue;
                string location = LocationOf(assembly);
                if (location != null) paths[name] = location;
            }

            // Not loaded yet: look next to mscorlib, and in its Facades folder.
            string coreFolder = Path.GetDirectoryName(LocationOf(typeof(object).Assembly) ?? "");
            foreach (string name in AllowedReferences)
            {
                if (paths.ContainsKey(name) || string.IsNullOrEmpty(coreFolder)) continue;
                foreach (string candidate in new[] { Path.Combine(coreFolder, name + ".dll"), Path.Combine(coreFolder, "Facades", name + ".dll") })
                {
                    if (!File.Exists(candidate)) continue;
                    paths[name] = candidate;
                    break;
                }
            }

            var list = new List<object>();
            string api = LocationOf(typeof(ICleaningAlgorithm).Assembly);
            if (api == null) throw new FileNotFoundException("The algorithm API assembly (RobotVacuum.AlgorithmApi.dll) has no file to compile against.");
            list.Add(Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(api));
            foreach (var path in paths.Values) list.Add(Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(path));

            if (!paths.ContainsKey("mscorlib")) referenceProblem = "mscorlib was not found; most code will not compile.";
            references = list;
            return references;
        }

        static string LocationOf(Assembly assembly)
        {
            try
            {
                if (assembly.IsDynamic) return null;
                string location = assembly.Location;
                return !string.IsNullOrEmpty(location) && File.Exists(location) ? location : null;
            }
            catch
            {
                return null;
            }
        }

        static string Sanitize(string name)
        {
            var builder = new StringBuilder();
            foreach (char c in name ?? "") builder.Append(char.IsLetterOrDigit(c) ? c : '_');
            return builder.Length > 0 ? builder.ToString() : "unit";
        }
    }
}
