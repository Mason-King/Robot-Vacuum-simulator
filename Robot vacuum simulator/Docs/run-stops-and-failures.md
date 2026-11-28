# How runs stop, and what happens when an algorithm fails

Requirements 2.11 (an algorithm's failure is caught and the program stays usable) and 2.16 (parameters can be changed
while a run is going or paused).

## Stops

A run **stops** when one of these happens:

| Stop | Shown as | Saved end reason |
|---|---|---|
| The algorithm says it's done and the robot docks (also when it gave up on a blockage) | Cleaning finished | `Completed` |
| The battery ran low and the robot docked | Returned to the dock: battery low | `ReturnedLowBattery` |
| The battery ran out | Battery empty | `BatteryDepleted` |
| The algorithm threw, hung (no answer in 5 s; 15 s to start) or returned something unusable | The algorithm failed | `AlgorithmFault` |
| No progress for 120 s | No progress | `Stuck` |
| The robot finished or ran low but the dock can't be reached | The dock can't be reached | `Degenerate` |
| The run's time limit (if switched on) | Time limit reached | `TimeLimit` |

**In a normal (graphics) run** the simulation pauses and a card says what happened, with:

- **Run Again:** the same floor plan with exactly the same seed, from the start.
- **Change Parameters:** closes the card so the Settings and Furniture menus can be used. A **Resume** button stays on
  the bar (the pause button does the same).
- **Reimport Algorithms** (when the algorithm failed): see below.
- **Resume:** carries on from where the robot is, after you confirm. The no-progress timer starts again. If the cause is
  still there (an empty battery, an algorithm with nothing left to do) it simply stops again. Resuming after the time
  limit lifts the limit for the rest of the run.
- **Finish:** ends the run and opens its analysis.

**Back** and **Finish** on the bar are always available.

**In a batch or a headless job** a stop ends that run at once. It is recorded, and the next run starts.

## Ends that can't be carried on from

These end the run at once and go straight to its analysis, which shows what happened:

- **The simulation's own code failed** (not the algorithm): `Error`.
- **An algorithm left threads that would not stop.** A hung algorithm is stopped; if its thread refuses, it is abandoned
  and keeps using a CPU core until the program closes. A second one in the same run ends the run (`Error`). With three
  such threads running in the program, no algorithm call is started until the program is restarted.
- **The program's 24 h safety cap on simulated time:** `TimeCap`.

If a run's results can't even be written, the program says so and goes back to the setup.

## Changing things mid-run

From the run's **Settings** menu, while it is going, paused or stopped:

- **Vacuum speeds and battery capacity:** as before. Selecting the battery capacity field at all, even without changing
  it, recharges the battery (the field shows capacity, not the charge left).
- **Refill battery.**
- **Algorithm:** switch to another one. The robot keeps its place and its map; the new algorithm starts from `Initialize`.
- **Reimport algorithms** (directly below the Algorithm list; on the Configuration screen it sits beside the list and
  just refreshes it): pauses the run, rescans the `Algorithms` folder, rebuilds the run's algorithm (always: its
  memory starts over) and carries on. **After dropping an updated algorithm into the Algorithms folder, press Reimport
  algorithms to apply it to the run in progress.** If the algorithm no longer loads (removed, renamed, doesn't compile,
  can't run with SLAM, throws or hangs as it starts), the run stops until it is fixed and reimported, another algorithm
  is chosen, or the run is finished. The old version is never kept running.

**Furniture:** change or remove pieces as before.

Every change is logged with the simulated time it happened. That includes each reimport, with the algorithm's
fingerprint before and after, so a change of code mid-run shows in the results. Nothing is recorded while the run is
paused or stopped.

## Time limits

All limits count **simulated** time.

- **Run time limit** (Configuration). In simulated seconds: default 3600 (1:00:00), from 1 to 86,400 (24 h).
  - Every batch trial ends there at the latest.
  - A single run uses it only when **Stop single runs at the time limit** is switched on (off by default).
  - Headless jobs set their own `simulation.maxSimulatedSeconds` (default 3600; 1 to 86,400).
- **Safety cap:** 24 h, always.

## Algorithms and saved runs

A run records its algorithm's **name, id and date**, not its code; keeping versions of algorithms is up to you. The
Analysis screen marks a run whose algorithm is **no longer installed** under that id. An algorithm added later under
the same name may not be the same code.

## result.json (in each run's folder)

| Field | Meaning |
|---|---|
| `endReason`, `endReasonName` | How the run ended (number and name; see the tables above; 4 = `StoppedByUser`). |
| `succeeded` | False only for `Error` and `AlgorithmFault`. |
| `errorMessage` | The failure, or the problem behind the end (stuck, dock unreachable, blockage). |
| `hasFault`, `fault` | The run's latest algorithm or simulation failure, even one that was resumed past. `fault` holds `kind` (Exception, Hang, InvalidOutput, Refused, GuardTripped, Simulation), `call` (Create, Initialize, NextGoal), `algorithmId`, `algorithmName`, `exceptionType`, `message`, `stackTrace`, `simSeconds`, `terminal`. |
| `stopEvents` | Every stop, in order: `simSeconds`, `reason`, `message`, `action` (Resumed, RunAgain, Finished, Back, Terminated). |

Mid-run changes, including reimports, algorithm switches and refills, are in the run's `parameters.csv`, as before.
