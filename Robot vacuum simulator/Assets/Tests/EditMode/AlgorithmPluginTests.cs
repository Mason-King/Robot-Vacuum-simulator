using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using RobotVacuum.AlgorithmApi;
using RobotVacuum.Sim;
using RobotVacuum.Sim.Control;
using RobotVacuumSim.EnvironmentModel;
using UnityEngine;

namespace RobotVacuum.Tests
{
    /// <summary>The algorithm API, the runtime compiler, the Algorithms folder loader and the catalog.</summary>
    public class AlgorithmPluginTests
    {
        const string GoodSource = @"using System;
using RobotVacuum.AlgorithmApi;

namespace PluginTests
{
    public sealed class ForwardOneMetre : ICleaningAlgorithm
    {
        AlgorithmContext context;
        public string Name => ""Forward one metre"";
        public bool SupportsSlam => true;
        public void Initialize(AlgorithmContext context) { this.context = context; }
        public Vec2? NextGoal(IBeliefMap map, Pose2 pose, GoalStatus lastStatus)
        {
            float free = map.FreeDistance(pose.position, pose.Forward, 1f);
            return pose.position + pose.Forward * Math.Max(0f, free - context.robotRadius);
        }
    }
}";

        // Errors on line 6 (string into int) and line 7 (unknown name).
        const string BrokenSource = @"using RobotVacuum.AlgorithmApi;
namespace PluginTests
{
    public static class Broken
    {
        static int y = ""text"";
        static int z = missing;
    }
}";

        string folder;

        [SetUp]
        public void SetUp()
        {
            folder = Path.Combine(Path.GetTempPath(), "RobotVacuumAlgorithmTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
        }

        [TearDown]
        public void TearDown()
        {
            try { Directory.Delete(folder, true); }
            catch (IOException) { }

            AlgorithmCatalog.Rescan(); // back to the real Algorithms folder for the other tests
        }

        static AlgorithmCompiler.Result Compile(string source, string file = "Test.cs") =>
            AlgorithmCompiler.Compile("Test", new[] { new KeyValuePair<string, string>(file, source) });

        // ---- The API's own types match Unity's to the bit ---------------------------------------------

        [Test]
        public void Pose2_MatchesRobotPose_AndDeltaAngleMatchesMathf()
        {
            foreach (float heading in new[] { 0f, 1f, 37.5f, 90f, 179.9f, 180f, -45f, 271f, 720.25f })
            {
                var api = Pose2.DirectionOf(heading);
                var unity = RobotPose.DirectionOf(heading);
                Assert.AreEqual(unity.x, api.x, 1e-6f, $"x at {heading}");
                Assert.AreEqual(unity.y, api.y, 1e-6f, $"y at {heading}");
                Assert.AreEqual(RobotPose.HeadingOf(unity), Pose2.HeadingOf(api), 1e-4f, $"heading of {heading}");
            }

            foreach (float from in new[] { 0f, 10f, -170f, 359f })
                foreach (float to in new[] { 0f, 180f, -180f, 190f, 540f, -10f })
                    Assert.AreEqual(Mathf.DeltaAngle(from, to), Pose2.DeltaAngle(from, to), 1e-4f, $"{from} -> {to}");
        }

        [Test]
        public void ProviderRandom_DrawsTheSameStreamAsTheProvider()
        {
            var direct = new RandomProvider(42u);
            var wrapped = new ProviderRandom(new RandomProvider(42u));
            for (int i = 0; i < 20; i++)
                Assert.AreEqual(direct.NextFloat(RandomProvider.Subsystem.RandomBounceAlgorithm, 0f, 360f), wrapped.NextFloat(0f, 360f));

            var ints = new ProviderRandom(new RandomProvider(7u));
            for (int i = 0; i < 200; i++)
            {
                int value = ints.NextInt(-3, 4);
                Assert.That(value, Is.InRange(-3, 3));
            }
        }

        // ---- Compiling text ---------------------------------------------------------------------------

        [Test]
        public void Compiler_GoodSource_CompilesToAWorkingAlgorithm()
        {
            var result = Compile(GoodSource);
            Assert.IsTrue(result.Succeeded, string.Join("\n", result.errors));

            var type = result.assembly.GetType("PluginTests.ForwardOneMetre");
            var algorithm = (ICleaningAlgorithm)Activator.CreateInstance(type);
            Assert.AreEqual("Forward one metre", algorithm.Name);

            // Run it against a real (empty) map through the adapter.
            var writer = new InternalModelWriter(new InternalModelGrid(Vector2.zero, 0.05f));
            var map = new BeliefMapAdapter(new InternalModelView(writer));
            algorithm.Initialize(new AlgorithmContext(new ProviderRandom(new RandomProvider(1u)), 0.17f, 0.34f, 0.05f, true, 8f));
            var goal = algorithm.NextGoal(map, new Pose2(Vec2.Zero, 0f), GoalStatus.None);
            Assert.IsTrue(goal.HasValue);
            Assert.AreEqual(0f, goal.Value.x, 1e-4f);
            Assert.AreEqual(0.83f, goal.Value.y, 1e-3f, "one metre forward (+y) less the radius");
        }

        [Test]
        public void Compiler_BrokenSource_ReportsFileLineAndCode()
        {
            var result = Compile(BrokenSource, "Broken.cs");
            Assert.IsFalse(result.Succeeded);
            Assert.That(result.errors.Any(e => e.StartsWith("Broken.cs(6,") && e.Contains("CS0029")), string.Join("\n", result.errors));
            Assert.That(result.errors.Any(e => e.StartsWith("Broken.cs(7,") && e.Contains("CS0103")), string.Join("\n", result.errors));
        }

        [Test]
        public void Compiler_RefusesUnityEngine_AndTheSimulator()
        {
            var unity = Compile("using UnityEngine;\npublic static class UsesUnity { public static float F() { return Mathf.PI; } }");
            Assert.IsFalse(unity.Succeeded);
            Assert.That(unity.errors.Any(e => e.Contains("UnityEngine")), string.Join("\n", unity.errors));

            var simulator = Compile("public static class UsesSim { public static object F() { return typeof(RobotVacuum.Sim.Control.AlgorithmCatalog); } }");
            Assert.IsFalse(simulator.Succeeded, "the simulator's own assembly is not referenced");
        }

        [Test]
        public void Compiler_AllowsTheBaseLibrary()
        {
            var result = Compile(@"using System.Collections.Generic; using System.Linq;
public static class UsesBase { public static int F() { var set = new HashSet<int> { 1, 2, 3 }; var q = new Queue<int>(set); return q.Where(x => x > 1).Sum(); } }");
            Assert.IsTrue(result.Succeeded, string.Join("\n", result.errors));
        }

        // ---- The folder ---------------------------------------------------------------------------------

        void Write(string relativePath, string text)
        {
            string path = Path.Combine(folder, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, text);
        }

        static string Algorithm(string ns, string cls, string name, bool slam = true, string ctor = "") => $@"using RobotVacuum.AlgorithmApi;
namespace {ns}
{{
    public sealed class {cls} : ICleaningAlgorithm
    {{
        {ctor}
        public string Name => ""{name}"";
        public bool SupportsSlam => {(slam ? "true" : "false")};
        public void Initialize(AlgorithmContext context) {{ }}
        public Vec2? NextGoal(IBeliefMap map, Pose2 pose, GoalStatus lastStatus) {{ return null; }}
    }}
}}";

        [Test]
        public void Loader_FilesFoldersAndProblems_EachReported()
        {
            Write("Good.cs", Algorithm("FolderTest", "Good", "Folder good"));
            Write("Broken.cs", BrokenSource);
            Write("_Skipped.cs", Algorithm("FolderTest", "Skipped", "Skipped"));
            Write("NoCtor.cs", Algorithm("FolderTest", "NoCtor", "No ctor", ctor: "public NoCtor(int x) { }"));
            Write("SameName.cs", Algorithm("FolderTest", "SameName", "Folder good"));
            Write("ClashesWithBuiltIn.cs", Algorithm("FolderTest", "Clash", "Random"));
            Write("NotAnAlgorithm.cs", "public static class Nothing { }");
            Write("notes.txt", "not code");

            // A subfolder is one project: the algorithm uses a helper from another file.
            Write("Project/Helper.cs", "namespace FolderTest { public static class Helper { public const string Label = \"Project algorithm\"; } }");
            Write("Project/Main.cs", @"using RobotVacuum.AlgorithmApi;
namespace FolderTest
{
    public sealed class ProjectAlgorithm : ICleaningAlgorithm
    {
        public string Name => Helper.Label;
        public bool SupportsSlam => false;
        public void Initialize(AlgorithmContext context) { }
        public Vec2? NextGoal(IBeliefMap map, Pose2 pose, GoalStatus lastStatus) { return null; }
    }
}");

            var entries = AlgorithmLoader.Load(folder, AlgorithmCatalog.BuiltIns, out var report);
            string text = report.Text();

            CollectionAssert.AreEquivalent(new[] { "FolderTest.Good", "FolderTest.ProjectAlgorithm" }, entries.Select(e => e.Id).ToArray(), text);
            Assert.IsTrue(entries.Single(e => e.Id == "FolderTest.Good").SupportsSlam);
            Assert.IsFalse(entries.Single(e => e.Id == "FolderTest.ProjectAlgorithm").SupportsSlam);
            Assert.AreEqual("Good.cs", entries.Single(e => e.Id == "FolderTest.Good").Origin);

            UnitReport Unit(string name) => report.units.Single(u => u.name == name);
            Assert.IsFalse(report.units.Any(u => u.name.StartsWith("_")), "underscore files are skipped");
            Assert.IsFalse(report.units.Any(u => u.name == "notes.txt"));
            Assert.That(Unit("Broken.cs").errors.Any(e => e.Contains("(6,")), text);
            Assert.That(Unit("NoCtor.cs").errors.Single(), Does.Contain("constructor"), text);
            Assert.That(Unit("SameName.cs").errors.Single(), Does.Contain("already taken"), text);
            Assert.That(Unit("ClashesWithBuiltIn.cs").errors.Single(), Does.Contain("already taken"), text);
            Assert.That(Unit("NotAnAlgorithm.cs").warnings.Any(w => w.Contains("No public class")), text);
            Assert.AreEqual("folder", Unit("Project/").kind);
        }

        [Test]
        public void Loader_UnchangedFile_IsNotCompiledAgain_AChangedOneIs()
        {
            Write("Cached.cs", Algorithm("CacheTest", "Cached", "Cached one"));
            AlgorithmLoader.Load(folder, AlgorithmCatalog.BuiltIns, out var first);
            AlgorithmLoader.Load(folder, AlgorithmCatalog.BuiltIns, out var second);
            Assert.IsTrue(second.units.Single().fromCache, second.Text());

            Write("Cached.cs", Algorithm("CacheTest", "Cached", "Cached one, edited"));
            var entries = AlgorithmLoader.Load(folder, AlgorithmCatalog.BuiltIns, out var third);
            Assert.IsFalse(third.units.Single().fromCache);
            Assert.AreEqual("Cached one, edited", entries.Single().Name);
        }

        [Test]
        public void Loader_MissingFolder_LeavesOnlyTheBuiltIns()
        {
            var entries = AlgorithmLoader.Load(Path.Combine(folder, "does not exist"), AlgorithmCatalog.BuiltIns, out var report);
            Assert.IsEmpty(entries);
            Assert.IsNotNull(report.folderProblem);
        }

        // ---- The catalog --------------------------------------------------------------------------------

        [Test]
        public void Catalog_RandomBounceIsTheFirstBuiltIn_AndPatternsMapToIds()
        {
            Assert.AreEqual(AlgorithmCatalog.RandomBounceId, AlgorithmCatalog.BuiltIns[0].Id);
            Assert.IsInstanceOf<RandomBounceAlgorithm>(AlgorithmCatalog.Find(AlgorithmCatalog.RandomBounceId).Create());

            foreach (MovementPattern pattern in Enum.GetValues(typeof(MovementPattern)))
            {
                string id = AlgorithmCatalog.IdOf(pattern);
                Assert.AreEqual(pattern, AlgorithmCatalog.PatternOf(id));
                Assert.AreEqual(MovementBrain.Labels[(int)pattern], AlgorithmCatalog.NameOf(id));
            }
        }

        [Test]
        public void Catalog_RescanAddsPlugins_ButIsRefusedDuringARun()
        {
            Write("Scanned.cs", Algorithm("CatalogTest", "Scanned", "Catalog scanned"));
            var report = AlgorithmCatalog.RescanFolder(folder);
            Assert.IsFalse(report.refused);
            Assert.IsNotNull(AlgorithmCatalog.Find("CatalogTest.Scanned"), report.Text());

            var run = new object();
            AlgorithmCatalog.BeginRun(run, () => true);
            try
            {
                Write("Later.cs", Algorithm("CatalogTest", "Later", "Catalog later"));
                var refused = AlgorithmCatalog.RescanFolder(folder);
                Assert.IsTrue(refused.refused);
                Assert.IsNull(AlgorithmCatalog.Find("CatalogTest.Later"), "nothing new mid-run");
                Assert.IsNotNull(AlgorithmCatalog.Find("CatalogTest.Scanned"), "and nothing taken away");
            }
            finally
            {
                AlgorithmCatalog.EndRun(run);
            }

            AlgorithmCatalog.RescanFolder(folder);
            Assert.IsNotNull(AlgorithmCatalog.Find("CatalogTest.Later"), "between runs it is picked up");
        }

        [Test]
        public void Catalog_ARunThatVanishes_DoesNotLockItForever()
        {
            bool alive = true;
            var run = new object();
            AlgorithmCatalog.BeginRun(run, () => alive);
            Assert.IsTrue(AlgorithmCatalog.IsLocked);
            alive = false;
            Assert.IsFalse(AlgorithmCatalog.IsLocked);
        }

        [Test]
        public void Catalog_UnknownOrNonSlamPlugin_IsRefusedAtStart()
        {
            Write("NoSlam.cs", Algorithm("CatalogTest", "NoSlam", "Catalog no SLAM", slam: false));
            AlgorithmCatalog.RescanFolder(folder);

            Assert.IsNull(AlgorithmCatalog.Refusal("CatalogTest.NoSlam", slam: false));
            Assert.That(AlgorithmCatalog.Refusal("CatalogTest.NoSlam", slam: true), Does.Contain("does not support SLAM"));
            Assert.That(AlgorithmCatalog.Refusal("No.Such.Algorithm", slam: false), Does.Contain("no algorithm"));

            var options = SimulationRunner.Options.Headless();
            options.algorithmId = "No.Such.Algorithm";
            Assert.Throws<InvalidOperationException>(() => SimulationRunner.Start(null, options), "refused before anything is built");
        }

        [Test]
        public void RunSettings_RecordTheAlgorithmById_AndKeepBuiltInsInStepWithThePattern()
        {
            var fromPattern = new RunSettings { randomSeed = false, pattern = MovementPattern.Spiral }.Resolved();
            Assert.AreEqual("Legacy.Spiral", fromPattern.algorithmId);

            var fromId = new RunSettings { randomSeed = false, algorithmId = "Legacy.WallFollow" }.Resolved();
            Assert.AreEqual(MovementPattern.WallFollow, fromId.pattern);

            var plugin = new RunSettings { randomSeed = false, algorithmId = "Someone.Else" }.Resolved();
            Assert.AreEqual("Someone.Else", plugin.algorithmId, "a plugin id is kept as given");
        }
    }
}
