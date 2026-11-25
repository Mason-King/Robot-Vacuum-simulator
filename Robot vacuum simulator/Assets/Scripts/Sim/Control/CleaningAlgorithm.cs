using System;
using System.Collections.Generic;
using System.Linq;
using RobotVacuum.AlgorithmApi;
using RobotVacuum.Algorithms;

namespace RobotVacuum.Sim.Control
{
    // The algorithm interface itself (ICleaningAlgorithm, AlgorithmContext, IBeliefMap and the types they use) lives
    // in the RobotVacuum.AlgorithmApi assembly, which has no Unity in it. This file is the simulator's side: the list
    // of algorithms a run can choose from.

    /// <summary>One algorithm a run can choose: built in, or found in the Algorithms folder.</summary>
    public sealed class AlgorithmEntry
    {
        readonly Func<ICleaningAlgorithm> factory;

        /// <summary>
        /// What config.json records and a run is started with. Built-ins have short ids ("RandomBounce"); a plugin's id is
        /// its class's full name (namespace + class), which the loader keeps unique.
        /// </summary>
        public string Id { get; }

        /// <summary>The name shown to the user.</summary>
        public string Name { get; }

        public bool SupportsSlam { get; }

        /// <summary>"built in", or the file or folder (relative to the Algorithms folder) it came from.</summary>
        public string Origin { get; }

        public bool BuiltIn => Origin == AlgorithmCatalog.BuiltInOrigin;

        public AlgorithmEntry(string id, string name, bool supportsSlam, string origin, Func<ICleaningAlgorithm> factory)
        {
            Id = id;
            Name = name;
            SupportsSlam = supportsSlam;
            Origin = origin;
            this.factory = factory;
        }

        /// <summary>A new instance of the algorithm.</summary>
        public ICleaningAlgorithm Create() => factory?.Invoke();

        public override string ToString() => $"{Name} [{Id}]";
    }

    /// <summary>
    /// Every algorithm a run can use: the built-ins, then whatever <see cref="AlgorithmLoader"/> found in the Algorithms
    /// folder. The folder is scanned the first time the list is needed, and again on <see cref="Rescan"/>, which is
    /// refused while a run is in progress: algorithms can be added while the simulator is open, but not mid-run.
    ///
    /// RandomBounce is the default and cannot be removed.
    /// </summary>
    public static class AlgorithmCatalog
    {
        public const string BuiltInOrigin = "built in";
        public const string RandomBounceId = "RandomBounce";

        static readonly AlgorithmEntry[] builtIns =
        {
            new AlgorithmEntry(RandomBounceId, "Random", true, BuiltInOrigin, () => new RandomBounceAlgorithm()),
            new AlgorithmEntry(BoustrophedonSweep.Id, "Boustrophedon Sweep", true, BuiltInOrigin, () => new BoustrophedonSweep()),
            new AlgorithmEntry(BoustrophedonCrawl.Id, "Boustrophedon Crawl", true, BuiltInOrigin, () => new BoustrophedonCrawl()),
            new AlgorithmEntry(Sheep.Id, "Sheep", true, BuiltInOrigin, () => new Sheep()),
            new AlgorithmEntry(Mouse.Id, "Mouse", true, BuiltInOrigin, () => new Mouse()),
        };

        static List<AlgorithmEntry> plugins = new List<AlgorithmEntry>();
        static AlgorithmLoadReport lastReport;
        static bool scanned;

        // Runs in progress, each with a check that it is still alive (a run destroyed without saying so must not lock the catalog forever).
        static readonly Dictionary<object, Func<bool>> runs = new Dictionary<object, Func<bool>>();

        /// <summary>The built-in algorithms, in order (RandomBounce first). Never scans the folder.</summary>
        public static IReadOnlyList<AlgorithmEntry> BuiltIns => builtIns;

        /// <summary>Every algorithm: the built-ins, then the folder's in file order. Scans the folder the first time.</summary>
        public static IReadOnlyList<AlgorithmEntry> Entries
        {
            get
            {
                EnsureScanned();
                return builtIns.Concat(plugins).ToList();
            }
        }

        /// <summary>What the last scan found, problems included. Scans the folder the first time.</summary>
        public static AlgorithmLoadReport LastReport
        {
            get
            {
                EnsureScanned();
                return lastReport;
            }
        }

        /// <summary>Where plugins are looked for: beside the .exe in a build, beside Assets in the editor.</summary>
        public static string Folder => AlgorithmLoader.DefaultFolder;

        /// <summary>The algorithm with this id, or null. A built-in is found without scanning the folder.</summary>
        public static AlgorithmEntry Find(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            var builtIn = builtIns.FirstOrDefault(e => e.Id == id);
            if (builtIn != null) return builtIn;
            EnsureScanned();
            return plugins.FirstOrDefault(e => e.Id == id);
        }

        // ---- Scanning, and the run lock ------------------------------------------------------------

        /// <summary>True while any run is in progress: the list cannot change then.</summary>
        public static bool IsLocked
        {
            get
            {
                foreach (var dead in runs.Where(r => !SafeAlive(r.Value)).Select(r => r.Key).ToList()) runs.Remove(dead);
                return runs.Count > 0;
            }
        }

        /// <summary>Called when a run starts (<see cref="SimulationRunner.Start"/>). <paramref name="stillRunning"/> lets the lock expire if the run disappears without <see cref="EndRun"/>.</summary>
        public static void BeginRun(object run, Func<bool> stillRunning)
        {
            EnsureScanned(); // never scan in the middle of a run: do it now if it has not happened yet
            runs[run] = stillRunning ?? (() => true);
        }

        /// <summary>Called when a run is stopped.</summary>
        public static void EndRun(object run) => runs.Remove(run);

        /// <summary>
        /// Scans the Algorithms folder again, so new, changed or removed files take effect. Refused (the list is left
        /// as it was, and the report says why) while a run is in progress. The first compile of a session takes about a
        /// second (Roslyn warming up); after that a small file takes milliseconds, and an unchanged one is not recompiled.
        /// </summary>
        public static AlgorithmLoadReport Rescan() => RescanFolder(AlgorithmLoader.DefaultFolder);

        /// <summary>As <see cref="Rescan"/>, from another folder (tests and tools).</summary>
        public static AlgorithmLoadReport RescanFolder(string folder)
        {
            if (IsLocked)
            {
                var refusal = new AlgorithmLoadReport
                {
                    folder = folder,
                    refused = true,
                    refusedReason = "a run is in progress. Algorithms can be added or changed between runs.",
                };
                return refusal;
            }

            plugins = AlgorithmLoader.Load(folder, builtIns, out lastReport);
            scanned = true;
            return lastReport;
        }

        static void EnsureScanned()
        {
            if (!scanned) RescanFolder(AlgorithmLoader.DefaultFolder);
        }

        static bool SafeAlive(Func<bool> alive)
        {
            try
            {
                return alive();
            }
            catch
            {
                return false;
            }
        }

        // ---- Ids and SLAM ------------------------------------------------------------------------

        /// <summary>The algorithm a run uses when none was chosen (an empty or missing id).</summary>
        public const string DefaultId = RandomBounceId;

        /// <summary>The id itself, or <see cref="DefaultId"/> when it is null or empty.</summary>
        public static string OrDefault(string id) => string.IsNullOrEmpty(id) ? DefaultId : id;

        public static string NameOf(string id) => Find(id)?.Name ?? id;

        public static bool SupportsSlam(string id) => Find(id)?.SupportsSlam ?? false;

        /// <summary>Why a run with this algorithm cannot start, or null if it can.</summary>
        public static string Refusal(string id, bool slam)
        {
            var entry = Find(id);
            if (entry == null)
                return $"There is no algorithm '{id}'. It may have been removed from the Algorithms folder, or failed to compile (see the load report).";
            if (slam && !entry.SupportsSlam) return SlamRefusalText(entry.Name);
            return null;
        }

        static string SlamRefusalText(string name)
        {
            var capable = Entries.Where(e => e.SupportsSlam).Select(e => e.Name).ToList();
            return $"SLAM is on, but the '{name}' algorithm does not support SLAM. Turn SLAM off, or choose an algorithm that supports it (currently: {string.Join(", ", capable)}).";
        }
    }
}
