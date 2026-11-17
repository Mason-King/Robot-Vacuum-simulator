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
- **Randomness only from `context.random`**, so the same seed gives the same run.
- **Return nearby waypoints** (a few metres at most), or `null` for "no goal right now".
- `Name` must be non-empty and different from every other algorithm's. The class's full name (namespace + class) is its
  id, which `config.json` records; it must be unique too.
- An exception thrown by the algorithm stops the run with an error message; it does not crash the simulator.

## Errors

A file that does not compile is skipped, and the load report lists each error with its file, line and column, for
example `MyAlgorithm.cs(12,17): CS0029 Cannot implicitly convert type 'string' to 'int'`. The other files still load.

## DLLs

Build a class library targeting **.NET Standard 2.1**, referencing `RobotVacuum.AlgorithmApi.dll` (found in the
simulator's `..._Data/Managed` folder, or the project's `Library/ScriptAssemblies`). A DLL that references anything
else (UnityEngine, the simulator) is refused.

## Good to know

- The first compile after the simulator starts takes about a second (the compiler warming up); after that a small file
  takes a few milliseconds, and an unchanged file is not compiled again.
- .NET cannot unload code, so each changed version of a file stays in memory until the simulator closes. Harmless
  for normal use; restart after many edit-and-rescan cycles.

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
