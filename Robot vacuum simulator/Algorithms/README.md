# Algorithms folder

Cleaning algorithms the simulator loads at runtime. This folder sits next to the simulator: beside the `.exe` in an
installed build, and in the project folder (beside `Assets`) when running in the Unity editor. Everything in the
project's copy is shipped with every build.

## Adding an algorithm

1. Write a public class with a public parameterless constructor that implements `RobotVacuum.AlgorithmApi.ICleaningAlgorithm`.
   `_Template.cs` is a working example to copy.
2. Put it here:
   - a single `.cs` file is compiled on its own;
   - a **subfolder** is one algorithm project: every `.cs` file in it (and below) is compiled together, so helpers can be split across files;
   - a `.dll` built elsewhere is loaded as it is (see "DLLs" below).
3. Rescan: the folder is read when the simulator first needs the algorithm list, and again on a rescan
   (in the editor: **Tools > Algorithms > Rescan**, which logs the load report). New and changed files take effect then.

Anything whose name starts with `_` or `.` is skipped.

Algorithms can be added or changed while the simulator is open, but **not during a run**: a rescan is refused while a
run is in progress, and the list stays as it was until the run is stopped.

## The rules

- **C# 9 at most** (the compiler is Roslyn 3.11): no file-scoped namespaces (`namespace X;`) and no global usings.
- **Only the algorithm API and base .NET.** The code is compiled against `RobotVacuum.AlgorithmApi` and the standard
  library only. `using UnityEngine;` does not compile, and the simulator's own classes cannot be reached. Use the API's
  `Vec2`, `Pose2`, `IBeliefMap` and `CellIndex`.
- **Read the map only through the `IBeliefMap` you are handed**, and plan from the believed pose you are handed. The
  robot's true position is not available, by design.
- **Randomness only from `context.random`**, so the same seed gives the same run. `System.Random` and the clock are
  refused (see the sandbox below).
- **Return nearby waypoints** (a few metres at most), or `null` for "no goal right now".
- `Name` must be non-empty and different from every other algorithm's. The class's full name (namespace + class) is its
  id, which `config.json` records; it must be unique too.
- **Failures stop the run, not the simulator.** While it runs, algorithm code is under a run guard (`AlgorithmGuard`:
  a worker thread the simulation waits on, so runs stay deterministic). The run ends with an error naming the kind
  of failure:
  - **Crash:** any exception (including out-of-memory).
  - **Hang:** a call that does not return in time (constructor and `Initialize`: 5 s; `NextGoal`: 2 s; `Name` /
    `SupportsSlam` while loading: 2 s). That's an infinite loop or a deadlock. The worker is aborted where the
    runtime allows it, and the algorithm is never called again. Time limits are lifted while a debugger is attached.
  - **InvalidMove:** a non-finite waypoint, one more than 200 m from the robot, or invalid `DriveRequests`
    (an undefined mode, or a sweep exposure that is negative or not finite).
  - **Stalled:** the robot made no progress for a long time.

  The one exception is a stack overflow (unbounded recursion), which .NET cannot recover from on any thread.
  To try failures out, see `_FaultInjection/README.md`.

## The sandbox (requirement 4.8)

An algorithm may come from someone else, and a job can carry it to another computer (a network worker or a cloud
node), so what algorithm code can reach is fixed and checked **before any of it runs**, for `.cs` files and for DLLs
alike. Everything not listed here is refused.

| Allowed | Refused |
|---|---|
| The algorithm API (`RobotVacuum.AlgorithmApi`) and your own types | Files and folders (`System.IO`) |
| Numbers and `Math`, strings, `StringBuilder`, `CultureInfo`, enums, tuples, `Span<T>`, `Lazy<T>`, delegates (`Func`, `Action`) | The network (`System.Net`) and other programs (`Process`) |
| Collections (`System.Collections`, `.Generic`, `.ObjectModel`) and LINQ (`Enumerable`) | Reflection: `GetType()`, `typeof`, `Type`, `Activator`, `System.Reflection`, `dynamic`, expression trees |
| `System.Numerics` (`Vector2`, `BigInteger`, `Complex` ...) | Threads, tasks, timers, `lock`, concurrent collections, finalizers |
| Exceptions (`throw`, `try`/`catch`) | Clocks and machine state (`DateTime.Now`, `Stopwatch`, `Environment`, `Guid`) and `System.Random`: they would make the same seed give different runs (2.1, 2.9) |
| Iterators (`yield return`), events | `Console` (use `context.Log`), native code (`extern`, `DllImport`), `System.Numerics.Vector<T>` (its size depends on the processor) |

A refused use is reported like a compile error, with the file, line and the code `RV0048`, for example
`MyAlgorithm.cs(14,20): RV0048 'System.IO.File' is not available to algorithms. Algorithms cannot read or write files.`

What the sandbox checks is what code can *reach*, not how it *behaves*. Behaviour is the run guard's job (see
"Failures stop the run" above): an algorithm that crashes, loops forever or makes an invalid move ends its run, in
every kind of run. Memory use is not capped by either; that is left to the run limits (each run's time limit, 2.11)
and, for headless and network jobs, to running each job in its own process.

## Errors

A file that does not compile is skipped, and the load report lists each error with its file, line and column, for
example `MyAlgorithm.cs(12,17): CS0029 Cannot implicitly convert type 'string' to 'int'`. The other files still load.

## DLLs

Build a class library targeting **.NET Standard 2.1**, referencing `RobotVacuum.AlgorithmApi.dll` (found in the
simulator's `..._Data/Managed` folder, or the project's `Library/ScriptAssemblies`). A DLL that references anything
else (UnityEngine, the simulator) is refused, and the sandbox above applies: the DLL's metadata (every type and member
it uses from outside, and any native code) is checked before it is loaded.

DLLs are loaded only from this folder on your own computer. Jobs sent to other computers carry `.cs` source only, so
remote code always goes through the full source check. A DLL whose IL was written by hand to be unverifiable is not
detected (disclosed limitation).

## Good to know

- The first compile after the simulator starts takes about a second (the compiler warming up); after that a small file
  takes a few milliseconds, and an unchanged file is not compiled again.
- .NET cannot unload code, so each changed version of a file stays in memory until the simulator closes. Harmless
  for normal use; restart after many edit-and-rescan cycles.
- The body can be Round (the default), D-shaped or Rectangular (Configuration › Vacuum › Body shape, or `vacuum.shape`
  in a job file). Every shape is 34 cm wide, so `robotRadius` (half the width) and `cleaningWidth` are the same for all
  three and lane spacing needs no change. The D and the rectangle need more room to turn in place (about 46 and 51 cm
  across, against 34 cm), and their corners can catch a wall mid-turn; the controller handles that (it backs off and
  re-routes), so an algorithm doesn't have to.

## Map analysis

`context.analysis` (`IMapAnalysis`) is the controller's analysis of the robot's map, the same one the built-in
Boustrophedon algorithms use. Call `context.analysis.Update(pose.position)` when you make a decision, then read:

- `BodyFits(row, col)`, `ObstacleDistance(row, col)`: where the robot's body fits, and how far each cell is from the
  nearest wall.
- `ReachDistance(row, col)`, `Reachable(row, col)`: how far the robot would drive to a cell over floor it has
  actually sensed (unknown floor never connects anything).
- `RoomCount`, `RoomOf(row, col)`, `Doorways`: the floor split into rooms at its doorways.
- Copies of `IsExplored`, `IsBlocked`, `Dwell`, `Cleanliness` and `VacuumResistance`, consistent with the above.

`context.Log("...")` writes a line to the Console when the run's "Log Algorithm Decisions" setting is on.
