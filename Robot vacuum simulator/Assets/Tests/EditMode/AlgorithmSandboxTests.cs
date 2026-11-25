using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using RobotVacuum.Sim.Control;

namespace RobotVacuum.Tests
{
    /// <summary>
    /// The plugin sandbox (requirement 4.8; AlgorithmSandbox, AlgorithmSourceSandbox): what algorithm code may and
    /// may not reach, for source plugins and for DLL plugins, and that the two agree.
    /// </summary>
    public class AlgorithmSandboxTests
    {
        static AlgorithmCompiler.Result Compile(string source) =>
            AlgorithmCompiler.Compile("SandboxTest", new[] { new KeyValuePair<string, string>("Plugin.cs", source) });

        static string InClass(string members, string usings = "") =>
            usings + "\npublic static class Plugin\n{\n" + members + "\n}\n";

        static void AssertRefused(string source, string mustMention)
        {
            var result = Compile(source);
            string errors = string.Join("\n", result.errors);
            Assert.IsFalse(result.Succeeded, "should have been refused:\n" + source);
            Assert.That(result.errors.Any(e => e.Contains(AlgorithmSandbox.Code)), "a sandbox error, not a compile error:\n" + errors);
            Assert.That(errors.Contains(mustMention), $"the error names '{mustMention}':\n{errors}");
            Assert.That(result.errors.Last(), Is.EqualTo(AlgorithmSandbox.Summary), "the reason is stated once, at the end");
        }

        // ---- refused: each way out of the sandbox ------------------------------------------------------

        [Test] public void Files() => AssertRefused(InClass("public static string F() { return System.IO.File.ReadAllText(\"x\"); }"), "System.IO.File");
        [Test] public void Directories() => AssertRefused(InClass("public static string[] F() { return System.IO.Directory.GetFiles(\".\"); }"), "System.IO.Directory");
        [Test] public void Network() => AssertRefused(InClass("public static object F() { return new System.Net.Sockets.TcpClient(); }"), "System.Net.Sockets.TcpClient");
        [Test] public void WebRequests() => AssertRefused(InClass("public static object F() { return System.Net.WebRequest.Create(\"http://example.com\"); }"), "System.Net.WebRequest");
        [Test] public void Processes() => AssertRefused(InClass("public static void F() { System.Diagnostics.Process.Start(\"cmd\"); }"), "System.Diagnostics.Process");
        [Test] public void EnvironmentExit() => AssertRefused(InClass("public static void F() { System.Environment.Exit(0); }"), "System.Environment");
        [Test] public void GetTypeReflection() => AssertRefused(InClass("public static string F() { return new object().GetType().Assembly.FullName; }"), "GetType");
        [Test] public void TypeOf() => AssertRefused(InClass("public static object F() { return typeof(int); }"), "System.Type");
        [Test] public void TypeByName() => AssertRefused(InClass("public static object F() { return System.Type.GetType(\"System.IO.File\"); }"), "System.Type");
        [Test] public void Activator() => AssertRefused(InClass("public static object F() { return System.Activator.CreateInstance<System.Collections.Generic.List<int>>(); }"), "System.Activator");
        [Test] public void AssemblyReflection() => AssertRefused(InClass("public static object F() { return System.Reflection.Assembly.GetExecutingAssembly(); }"), "System.Reflection.Assembly");
        [Test] public void DelegateMethod() => AssertRefused(InClass("public static object F() { System.Func<int> f = () => 1; return f.Method; }"), "Method");
        [Test] public void Threads() => AssertRefused(InClass("public static void F() { new System.Threading.Thread(() => { }).Start(); }"), "System.Threading.Thread");
        [Test] public void Tasks() => AssertRefused(InClass("public static object F() { return System.Threading.Tasks.Task.Run(() => 1); }"), "System.Threading.Tasks.Task");
        [Test] public void Lock() => AssertRefused(InClass("static readonly object gate = new object(); public static void F() { lock (gate) { } }"), "'lock'");
        [Test] public void Console() => AssertRefused(InClass("public static void F() { System.Console.WriteLine(\"x\"); }"), "context.Log");
        [Test] public void Unseeded_Random() => AssertRefused(InClass("public static int F() { return new System.Random(1).Next(); }"), "context.random");
        [Test] public void Clock() => AssertRefused(InClass("public static long F() { return System.DateTime.Now.Ticks; }"), "System.DateTime");
        [Test] public void Stopwatch() => AssertRefused(InClass("public static object F() { return System.Diagnostics.Stopwatch.StartNew(); }"), "System.Diagnostics.Stopwatch");
        [Test] public void ExpressionTrees() => AssertRefused(InClass("public static object F() { System.Linq.Expressions.Expression<System.Func<int>> e = () => 1; return e; }"), "System.Linq.Expressions");
        [Test] public void NativeCode() => AssertRefused(InClass("[System.Runtime.InteropServices.DllImport(\"kernel32\")] static extern uint GetTickCount();"), "extern");
        [Test] public void Finalizer() => AssertRefused("public sealed class Holder { ~Holder() { } }", "Finalizers");
        [Test] public void BannedTypeAsGenericArgument() => AssertRefused(InClass("public static object F() { return new System.Collections.Generic.List<System.IO.FileInfo>(); }"), "System.IO.FileInfo");
        [Test] public void BannedBaseType() => AssertRefused("public sealed class Leaky : System.IO.MemoryStream { }", "System.IO.MemoryStream");
        [Test] public void UsingStatic() => AssertRefused("using static System.IO.File;\n" + InClass("public static bool F() { return Exists(\"x\"); }"), "System.IO.File");
        [Test] public void UsingAlias() => AssertRefused("using F = System.IO.File;\n" + InClass("public static bool G() { return F.Exists(\"x\"); }"), "System.IO.File");
        [Test] public void Var_StillChecked() => AssertRefused(InClass("public static void F() { var info = new System.IO.DirectoryInfo(\".\"); }"), "System.IO.DirectoryInfo");
        [Test] public void Concurrent() => AssertRefused(InClass("public static object F() { return new System.Collections.Concurrent.ConcurrentQueue<int>(); }"), "System.Collections.Concurrent");

        [Test]
        public void Dynamic_IsRefused()
        {
            // May fail to compile at all (no binder referenced); either way it must not load.
            var result = Compile(InClass("public static object F() { dynamic d = 1; object o = d; return o; }"));
            Assert.IsFalse(result.Succeeded, string.Join("\n", result.errors));
        }

        [Test]
        public void Errors_HaveFileLineAndCode()
        {
            var result = Compile("public static class Plugin\n{\n    public static string F() { return System.IO.File.ReadAllText(\"x\"); }\n}\n");
            Assert.That(result.errors.Any(e => e.StartsWith("Plugin.cs(3,") && e.Contains(AlgorithmSandbox.Code)), string.Join("\n", result.errors));
        }

        // ---- allowed: ordinary algorithm code ------------------------------------------------------------

        const string Ordinary = @"using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using RobotVacuum.AlgorithmApi;

namespace SandboxTests
{
    public enum Phase { Explore, Sweep }

    public struct Leg : IEquatable<Leg>
    {
        public Vec2 from, to;
        public bool Equals(Leg other) => from.x == other.from.x && to.y == other.to.y;
    }

    public sealed class Ordinary : ICleaningAlgorithm
    {
        readonly Dictionary<int, List<Vec2>> rows = new Dictionary<int, List<Vec2>>();
        readonly Queue<Leg> legs = new Queue<Leg>();
        readonly HashSet<(int, int)> seen = new HashSet<(int, int)>();
        Lazy<string> label = new Lazy<string>(() => ""ordinary"");
        AlgorithmContext context;
        Phase phase;

        public string Name => ""Ordinary (sandbox test)"";
        public bool SupportsSlam => true;
        public event EventHandler Changed;

        public void Initialize(AlgorithmContext context)
        {
            this.context = context;
            phase = Phase.Explore;
            int[] primes = { 2, 3, 5, 7, 11 };
            var scratch = new int[4];
            scratch[0] = primes.Sum();
            var builder = new StringBuilder();
            builder.Append(string.Format(CultureInfo.InvariantCulture, ""{0:F2}"", Math.Sqrt(2.0)));
            builder.Append($"" {Math.PI:0.00} {phase} {label.Value}"");
            foreach (var group in primes.GroupBy(p => p % 3).OrderBy(g => g.Key)) builder.Append(group.Count());
            var ordered = new SortedDictionary<string, double> { [""a""] = 1, [""b""] = 2 };
            builder.Append(ordered.Values.Max().ToString(CultureInfo.InvariantCulture));
            try { throw new InvalidOperationException(""test""); }
            catch (Exception exception) when (exception.Message.Length > 0) { builder.Append(exception.Message); }
            Changed?.Invoke(this, EventArgs.Empty);
            if (Enum.TryParse(""Sweep"", out Phase parsed)) phase = parsed;
            context.Log(builder.ToString());
        }

        IEnumerable<int> Countdown(int from)
        {
            for (int i = from; i >= 0; i--) yield return i;
        }

        public Vec2? NextGoal(IBeliefMap map, Pose2 believedPose, GoalStatus lastStatus)
        {
            int steps = Countdown(3).Count();
            float heading = context.random.NextFloat(0f, 360f);
            Vec2 direction = Pose2.DirectionOf(heading);
            float length = Math.Min(map.FreeDistance(believedPose.position, direction, 2f), steps);
            var (a, b) = (length, heading);
            seen.Add(((int)a, (int)b));
            legs.Enqueue(new Leg { from = believedPose.position, to = believedPose.position + direction * a });
            return legs.Count > 0 ? legs.Peek().to : (Vec2?)null;
        }
    }
}";

        [Test]
        public void OrdinaryAlgorithmCode_IsAllowed()
        {
            var result = Compile(Ordinary);
            Assert.IsTrue(result.Succeeded, string.Join("\n", result.errors));
        }

        [Test]
        public void ShippedAlgorithms_AllPassTheSandbox()
        {
            string folder = AlgorithmLoader.DefaultFolder;
            Assume.That(Directory.Exists(folder), "no Algorithms folder at " + folder);

            var units = new List<(string name, List<string> files)>();
            foreach (string file in Directory.GetFiles(folder, "*.cs")) units.Add((Path.GetFileName(file), new List<string> { file }));
            foreach (string sub in Directory.GetDirectories(folder))
                units.Add((Path.GetFileName(sub), Directory.GetFiles(sub, "*.cs", SearchOption.AllDirectories).ToList()));

            foreach (var unit in units.Where(u => u.files.Count > 0))
            {
                var sources = unit.files.Select(f => new KeyValuePair<string, string>(Path.GetFileName(f), File.ReadAllText(f))).ToList();
                var result = AlgorithmCompiler.Compile(unit.name, sources);
                Assert.IsTrue(result.Succeeded, unit.name + ":\n" + string.Join("\n", result.errors));
            }
        }

        // ---- DLL plugins: the same verdicts, read from metadata without loading ----------------------------

        static byte[] Dll(string source)
        {
            var errors = new List<string>();
            byte[] image = AlgorithmCompiler.EmitUnchecked("SandboxDll", new[] { new KeyValuePair<string, string>("Plugin.cs", source) }, errors);
            Assert.IsNotNull(image, "the test plugin should compile:\n" + string.Join("\n", errors));
            return image;
        }

        static List<string> CheckDll(byte[] image)
        {
            var errors = new List<string>();
            AlgorithmSandbox.CheckDll(image, errors);
            return errors;
        }

        [Test]
        public void Dll_OrdinaryCode_IsAllowed()
        {
            var errors = CheckDll(Dll(Ordinary));
            Assert.IsEmpty(errors, string.Join("\n", errors));
        }

        [TestCase("public static string F() { return System.IO.File.ReadAllText(\"x\"); }", "System.IO.File")]
        [TestCase("public static object F() { return new System.Net.Sockets.TcpClient(); }", "System.Net.Sockets.TcpClient")]
        [TestCase("public static void F() { System.Diagnostics.Process.Start(\"cmd\"); }", "System.Diagnostics.Process")]
        [TestCase("public static string F() { return new object().GetType().Name; }", "System.Object.GetType")]
        [TestCase("public static object F() { return typeof(int); }", "System.Type")]
        [TestCase("public static int F() { return new System.Random(1).Next(); }", "System.Random")]
        [TestCase("public static long F() { return System.DateTime.Now.Ticks; }", "System.DateTime")]
        [TestCase("public static void F() { new System.Threading.Thread(() => { }).Start(); }", "System.Threading.Thread")]
        [TestCase("static readonly object gate = new object(); public static void F() { lock (gate) { } }", "System.Threading.Monitor")]
        public void Dll_RefusedCode_IsRefusedWithoutLoading(string members, string mustMention)
        {
            var errors = CheckDll(Dll(InClass(members)));
            string all = string.Join("\n", errors);
            Assert.That(errors.Any(e => e.Contains(AlgorithmSandbox.Code) && e.Contains(mustMention)), all);
            Assert.AreEqual(AlgorithmSandbox.Summary, errors.Last(), all);
        }

        [Test]
        public void Dll_NativeCode_IsRefused()
        {
            var errors = CheckDll(Dll(InClass("[System.Runtime.InteropServices.DllImport(\"kernel32\")] public static extern uint GetTickCount();")));
            Assert.That(errors.Any(e => e.Contains("native code")), string.Join("\n", errors));
        }

        [Test]
        public void Dll_TheSimulatorItself_IsRefused()
        {
            string path = typeof(AlgorithmCatalog).Assembly.Location;
            Assume.That(File.Exists(path), "the simulator assembly has no file here");
            var errors = CheckDll(File.ReadAllBytes(path));
            Assert.IsNotEmpty(errors, "the simulator reaches files, Unity and more; it must not pass as a plugin");
        }

        [Test]
        public void Dll_NotAnAssembly_IsReportedPlainly()
        {
            var errors = CheckDll(new byte[] { 1, 2, 3, 4, 5 });
            Assert.That(errors.Any(e => e.Contains("Not a .NET assembly") || e.Contains("Could not read")), string.Join("\n", errors));
        }

        // ---- the policy itself --------------------------------------------------------------------------

        [Test]
        public void Policy_ExactNamespaces_NotPrefixes()
        {
            Assert.IsTrue(AlgorithmSandbox.IsTypeAllowed("System.Collections.Generic.List`1"));
            Assert.IsFalse(AlgorithmSandbox.IsTypeAllowed("System.Collections.Concurrent.ConcurrentBag`1"));
            Assert.IsFalse(AlgorithmSandbox.IsTypeAllowed("System.Collections.Generic.Evil.Thing"));
            Assert.IsFalse(AlgorithmSandbox.IsTypeAllowed("System.Numerics.Vector`1"));
            Assert.IsTrue(AlgorithmSandbox.IsTypeAllowed("System.Numerics.Vector2"));
            Assert.IsFalse(AlgorithmSandbox.IsTypeAllowed(""));
        }

        [Test]
        public void Policy_CompilerHelpers_OnlyTheirSafeMembers()
        {
            Assert.IsTrue(AlgorithmSandbox.IsMemberAllowed("System.Runtime.CompilerServices.RuntimeHelpers", "InitializeArray"));
            Assert.IsFalse(AlgorithmSandbox.IsMemberAllowed("System.Runtime.CompilerServices.RuntimeHelpers", "RunClassConstructor"));
            Assert.IsFalse(AlgorithmSandbox.IsMemberAllowed("System.Object", "GetType"));
            Assert.IsTrue(AlgorithmSandbox.IsMemberAllowed("System.Object", "ToString"));
        }
    }
}
