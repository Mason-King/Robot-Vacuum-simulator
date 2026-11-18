using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using RobotVacuum.AlgorithmApi;

namespace RobotVacuum.Sim.Control
{
    /// <summary>
    /// The trust boundary around algorithm plugins (requirement 4.8, C.12; SDD 9.1). A plugin is code someone else
    /// wrote, and it may arrive from another computer (a network worker or a cloud node runs the algorithms a job
    /// carries). So what it can reach is fixed here, deny by default: the algorithm API, maths, strings, collections
    /// and LINQ, and nothing else. No files, network, other programs, reflection, threads, clocks or native code.
    ///
    /// The same list is enforced on both kinds of plugin, before any of its code is loaded:
    ///   * source (.cs): <see cref="AlgorithmSourceSandbox"/> checks every type and member the compiled code uses;
    ///   * a .dll: <see cref="CheckDll"/> reads its metadata (every external type and member it references, and any
    ///     native-code entry) without loading it.
    ///
    /// Clocks, <see cref="System.Random"/> and processor-dependent types are refused too, because they would make the
    /// same seed give different runs on different machines (2.1, 2.9); algorithms draw randomness from
    /// <c>context.random</c>.
    ///
    /// Not covered (disclosed, C.4): a hand-written .dll whose IL is deliberately unverifiable is not detected. DLLs
    /// are only ever loaded from the user's own Algorithms folder; jobs sent to other machines carry source only, so
    /// remote code always goes through the full source check. A plugin that loops forever or allocates without limit
    /// is a reliability problem, not a trust one: <see cref="AlgorithmGuard"/> handles it at run time in every run (a
    /// crash, a hang or an invalid move ends that run, not the simulator), backed by the run limits and process
    /// isolation of headless jobs (2.11).
    /// </summary>
    public static class AlgorithmSandbox
    {
        /// <summary>The code on every sandbox error, in the compiler's "file(line,col): CODE message" format.</summary>
        public const string Code = "RV0048";

        /// <summary>Added once after the individual errors, so the reason is stated without repeating it on every line.</summary>
        public const string Summary =
            "Algorithms run in a sandbox (requirement 4.8): they may use the algorithm API, maths, strings, collections " +
            "and LINQ, and nothing that reaches files, the network, other programs, reflection, threads or clocks. " +
            "See README.md in the Algorithms folder.";

        public static string ApiAssemblyName => typeof(ICleaningAlgorithm).Assembly.GetName().Name;

        const string TypeName = "System.Type";

        /// <summary>Namespaces whose every type is allowed (exact names: System.Collections.Concurrent is not included).</summary>
        static readonly HashSet<string> AllowedNamespaces = new HashSet<string>(StringComparer.Ordinal)
        {
            "System.Collections", "System.Collections.Generic", "System.Collections.ObjectModel", "System.Numerics",
        };

        /// <summary>Types refused even inside an allowed namespace.</summary>
        static readonly HashSet<string> DeniedTypes = new HashSet<string>(StringComparer.Ordinal)
        {
            "System.Numerics.Vector", "System.Numerics.Vector`1", // width depends on the processor (2.1)
        };

        /// <summary>Single types allowed outside those namespaces, by metadata name (generic arity after the backtick).</summary>
        static readonly HashSet<string> AllowedTypes = BuildAllowedTypes();

        /// <summary>Allowed types that may only be used through the members listed (compiler-emitted helpers).</summary>
        static readonly Dictionary<string, HashSet<string>> OnlyMembers = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
        {
            ["System.Runtime.CompilerServices.RuntimeHelpers"] = new HashSet<string>(StringComparer.Ordinal) { "InitializeArray", "GetSubArray" },
            // Iterators (yield return) read the current thread's id; nothing else of Environment is available.
            ["System.Environment"] = new HashSet<string>(StringComparer.Ordinal) { "CurrentManagedThreadId", "get_CurrentManagedThreadId" },
        };

        /// <summary>Members refused on otherwise allowed types: each is a way into reflection.</summary>
        static readonly Dictionary<string, HashSet<string>> DeniedMembers = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
        {
            ["System.Object"] = new HashSet<string>(StringComparer.Ordinal) { "GetType" },
            ["System.Delegate"] = new HashSet<string>(StringComparer.Ordinal) { "Method", "get_Method", "CreateDelegate" },
            ["System.MulticastDelegate"] = new HashSet<string>(StringComparer.Ordinal) { "Method", "get_Method", "CreateDelegate" },
            ["System.Array"] = new HashSet<string>(StringComparer.Ordinal) { "CreateInstance" },
        };

        static HashSet<string> BuildAllowedTypes()
        {
            var types = new HashSet<string>(StringComparer.Ordinal);
            void Add(string ns, params string[] names)
            {
                foreach (string name in names) types.Add(ns + "." + name);
            }

            Add("System",
                "Object", "String", "Boolean", "Char", "SByte", "Byte", "Int16", "UInt16", "Int32", "UInt32", "Int64",
                "UInt64", "Single", "Double", "Decimal", "Void", "Math", "MathF", "Array", "Nullable", "Nullable`1",
                "Enum", "ValueType", "Delegate", "MulticastDelegate", "TypeCode", "RuntimeFieldHandle", "Environment",
                "Attribute", "AttributeUsageAttribute", "AttributeTargets", "FlagsAttribute", "ObsoleteAttribute",
                "SerializableAttribute", "NonSerializedAttribute", "ParamArrayAttribute", "CLSCompliantAttribute",
                "IComparable", "IComparable`1", "IEquatable`1", "IDisposable", "IFormattable", "IFormatProvider",
                "ICloneable", "IConvertible",
                "TimeSpan", "StringComparison", "StringComparer", "StringSplitOptions", "MidpointRounding", "Convert",
                "BitConverter", "Lazy`1", "Span`1", "ReadOnlySpan`1", "Index", "Range",
                "Predicate`1", "Comparison`1", "Converter`2", "EventArgs", "EventHandler", "EventHandler`1",
                "Action", "Tuple", "ValueTuple",
                "Exception", "SystemException", "ArgumentException", "ArgumentNullException",
                "ArgumentOutOfRangeException", "ArithmeticException", "DivideByZeroException", "FormatException",
                "IndexOutOfRangeException", "InvalidCastException", "InvalidOperationException",
                "NotImplementedException", "NotSupportedException", "NullReferenceException",
                "ObjectDisposedException", "OverflowException");
            for (int arity = 1; arity <= 16; arity++) types.Add("System.Action`" + arity);
            for (int arity = 1; arity <= 17; arity++) types.Add("System.Func`" + arity);
            for (int arity = 1; arity <= 8; arity++)
            {
                types.Add("System.Tuple`" + arity);
                types.Add("System.ValueTuple`" + arity);
            }

            Add("System.Text", "StringBuilder");
            Add("System.Globalization", "CultureInfo", "NumberStyles", "NumberFormatInfo");
            Add("System.Linq", "Enumerable", "IGrouping`2", "ILookup`2", "Lookup`2", "IOrderedEnumerable`1");

            // What the C# compiler emits by itself (in a .dll's metadata), or attributes with no reach of their own.
            Add("System.Runtime.CompilerServices",
                "RuntimeHelpers", "CompilationRelaxationsAttribute", "RuntimeCompatibilityAttribute",
                "CompilerGeneratedAttribute", "ExtensionAttribute", "IteratorStateMachineAttribute",
                "IsReadOnlyAttribute", "IsByRefLikeAttribute", "IsUnmanagedAttribute", "TupleElementNamesAttribute",
                "MethodImplAttribute", "MethodImplOptions", "NullableAttribute", "NullableContextAttribute");
            Add("System.Diagnostics",
                "DebuggableAttribute", "DebuggerHiddenAttribute", "DebuggerStepThroughAttribute",
                "DebuggerBrowsableAttribute", "DebuggerBrowsableState", "DebuggerDisplayAttribute",
                "DebuggerNonUserCodeAttribute", "ConditionalAttribute");
            Add("System.Reflection",
                "DefaultMemberAttribute", "AssemblyTitleAttribute", "AssemblyDescriptionAttribute",
                "AssemblyConfigurationAttribute", "AssemblyCompanyAttribute", "AssemblyProductAttribute",
                "AssemblyCopyrightAttribute", "AssemblyTrademarkAttribute", "AssemblyCultureAttribute",
                "AssemblyVersionAttribute", "AssemblyFileVersionAttribute", "AssemblyInformationalVersionAttribute",
                "AssemblyMetadataAttribute");
            Add("System.Runtime.Versioning", "TargetFrameworkAttribute");
            Add("System.Threading", "Interlocked"); // the compiler uses it for events; it starts no thread
            return types;
        }

        // ---------------------------------------------------------------- the policy

        /// <summary>True if a type outside the plugin and the API, by metadata full name (outermost type for nested ones), may be used.</summary>
        public static bool IsTypeAllowed(string fullName)
        {
            if (string.IsNullOrEmpty(fullName) || DeniedTypes.Contains(fullName)) return false;
            if (AllowedTypes.Contains(fullName)) return true;
            int dot = fullName.LastIndexOf('.');
            return dot > 0 && AllowedNamespaces.Contains(fullName.Substring(0, dot));
        }

        /// <summary>True if <paramref name="member"/> of the allowed type <paramref name="typeFullName"/> may be used.</summary>
        public static bool IsMemberAllowed(string typeFullName, string member)
        {
            if (OnlyMembers.TryGetValue(typeFullName ?? "", out var only)) return only.Contains(member);
            return !(DeniedMembers.TryGetValue(typeFullName ?? "", out var denied) && denied.Contains(member));
        }

        /// <summary>One error line for something refused.</summary>
        public static string Refusal(string what, string fullName) =>
            $"{Code} '{what}' is not available to algorithms.{Hint(fullName ?? what)}";

        /// <summary>Why, and what to use instead, for the common cases.</summary>
        public static string Hint(string fullName)
        {
            string name = fullName ?? "";
            bool Under(string ns) => name == ns || name.StartsWith(ns + ".", StringComparison.Ordinal);

            if (name == "System.Random") return " Use context.random, so the same seed always gives the same run (2.9).";
            if (name == "System.DateTime" || name == "System.DateTimeOffset" || name == "System.Diagnostics.Stopwatch" ||
                Under("System.Environment") || name == "System.Guid" || name == "System.TimeZoneInfo")
                return " Clocks and machine details would make runs differ between machines (2.1).";
            if (name == "System.Console" || name == "System.Diagnostics.Debug" || name == "System.Diagnostics.Trace")
                return " Use context.Log(...) to write to the log.";
            if (name.StartsWith("System.Numerics.Vector", StringComparison.Ordinal))
                return " Its size depends on the processor, so results would differ between machines (2.1).";
            if (name == "System.Diagnostics.Process" || Under("System.Diagnostics.Process")) return " Algorithms cannot start other programs.";
            if (Under("System.Threading")) return " An algorithm runs on the simulation's thread: threads, tasks, timers and locks are not available.";
            if (Under("System.IO")) return " Algorithms cannot read or write files.";
            if (Under("System.Net")) return " Algorithms cannot use the network.";
            if (Under("System.Runtime.InteropServices")) return " Native code is not available to algorithms.";
            if (name == TypeName || name.StartsWith(TypeName + ".", StringComparison.Ordinal) || name == "System.Activator" || name == "System.AppDomain" || Under("System.Reflection") ||
                name.StartsWith("System.Object.GetType", StringComparison.Ordinal))
                return " Reflection could reach past the algorithm API.";
            return "";
        }

        // ---------------------------------------------------------------- DLL plugins

        /// <summary>
        /// Checks a plugin .dll against the sandbox WITHOUT loading it: every type it references from another
        /// assembly, every member it references on those types, and any native-code entry (P/Invoke, internal calls,
        /// module references). Adds one error per thing refused; adds nothing if the DLL may be loaded.
        /// </summary>
        public static void CheckDll(byte[] bytes, List<string> errors)
        {
            if (bytes == null || bytes.Length == 0)
            {
                errors.Add("Empty file.");
                return;
            }

            try
            {
                using (var pe = new PEReader(new MemoryStream(bytes, false), PEStreamOptions.PrefetchEntireImage))
                {
                    if (!pe.HasMetadata)
                    {
                        errors.Add("Not a .NET assembly.");
                        return;
                    }
                    var md = pe.GetMetadataReader();
                    string api = ApiAssemblyName;
                    var refused = new SortedSet<string>(StringComparer.Ordinal);

                    foreach (var handle in md.TypeReferences)
                    {
                        var outer = Outermost(md, handle);
                        string full = FullName(md, outer);
                        // The compiler names System.Type in attributes it adds by itself (yield return adds
                        // [IteratorStateMachine(typeof(...))]). A Type is harmless until one of its members is used,
                        // and every member of it is refused below, typeof (Type.GetTypeFromHandle) included.
                        if (full == TypeName && outer.ResolutionScope.Kind == HandleKind.AssemblyReference) continue;
                        var scope = outer.ResolutionScope;
                        if (scope.Kind == HandleKind.ModuleDefinition) continue; // its own types
                        if (scope.Kind == HandleKind.AssemblyReference)
                        {
                            string assembly = md.GetString(md.GetAssemblyReference((AssemblyReferenceHandle)scope).Name);
                            if (assembly == api) continue;
                            if (!IsTypeAllowed(full)) refused.Add(full);
                            continue;
                        }
                        refused.Add(full + " (from another module)");
                    }

                    foreach (var handle in md.MemberReferences)
                    {
                        var member = md.GetMemberReference(handle);
                        if (member.Parent.Kind != HandleKind.TypeReference) continue; // generic instances: their types are TypeReferences, checked above
                        var owner = md.GetTypeReference((TypeReferenceHandle)member.Parent);
                        if (owner.ResolutionScope.Kind == HandleKind.TypeReference) continue; // nested: no member rules apply
                        string ownerName = FullName(md, owner);
                        string name = md.GetString(member.Name);
                        if (ownerName == TypeName || !IsMemberAllowed(ownerName, name)) refused.Add(ownerName + "." + name);
                    }

                    bool native = md.GetTableRowCount(TableIndex.ModuleRef) > 0 || md.GetTableRowCount(TableIndex.ImplMap) > 0;
                    foreach (var handle in md.MethodDefinitions)
                    {
                        var method = md.GetMethodDefinition(handle);
                        if ((method.Attributes & MethodAttributes.PinvokeImpl) != 0 ||
                            (method.ImplAttributes & MethodImplAttributes.InternalCall) != 0)
                            native = true;
                    }

                    foreach (string name in refused)
                    {
                        string fullName = name.EndsWith(" (from another module)", StringComparison.Ordinal) ? "" : name;
                        errors.Add($"{Code} uses '{name}', which is not available to algorithms.{Hint(fullName)}");
                    }
                    if (native) errors.Add($"{Code} contains native code (P/Invoke, internal calls or module references), which is not available to algorithms.");
                    if (refused.Count > 0 || native) errors.Add(Summary);
                }
            }
            catch (BadImageFormatException)
            {
                errors.Add("Not a .NET assembly.");
            }
            catch (Exception exception)
            {
                errors.Add($"Could not read the DLL: {exception.GetType().Name}: {exception.Message}");
            }
        }

        static TypeReference Outermost(MetadataReader md, TypeReferenceHandle handle)
        {
            var type = md.GetTypeReference(handle);
            for (int depth = 0; depth < 64 && type.ResolutionScope.Kind == HandleKind.TypeReference; depth++)
                type = md.GetTypeReference((TypeReferenceHandle)type.ResolutionScope);
            return type;
        }

        static string FullName(MetadataReader md, TypeReference type)
        {
            string ns = md.GetString(type.Namespace);
            string name = md.GetString(type.Name);
            return string.IsNullOrEmpty(ns) ? name : ns + "." + name;
        }
    }
}
