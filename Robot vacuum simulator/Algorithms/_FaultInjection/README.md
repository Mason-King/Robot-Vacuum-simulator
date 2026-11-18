# Fault injection algorithms

Deliberately broken algorithms for checking that algorithm failures are detected and isolated: the run ends
with a clear error, and the simulator (and the rest of a batch) carries on.

The folder name starts with `_`, so the simulator **skips it** and these never appear in the algorithm list by
accident.

## Turning them on

1. Rename this folder from `_FaultInjection` to `FaultInjection`.
2. Rescan: **Tools > Algorithms > Rescan** in the editor (or restart the simulator).
3. The algorithm list now has the `Fault: …` entries below.

Rename it back to `_FaultInjection` and rescan when you're done.

| Algorithm | What it does (after 20 goals unless noted) | Expected failure |
|---|---|---|
| Fault: crash | indexes past the end of an array | **Crash**: `NextGoal threw IndexOutOfRangeException …` |
| Fault: infinite loop | `while (true)` inside NextGoal | **Hang**: `NextGoal did not return within 2 s …` |
| Fault: hang in Initialize | `while (true)` inside Initialize, at the start | **Hang**: `Initialize did not return within 5 s …` |
| Fault: NaN waypoint | returns a NaN waypoint | **InvalidMove**: `non-finite waypoint` |
| Fault: waypoint 10 km away | returns a waypoint 10 km away | **InvalidMove**: `waypoint 10000 m away` |
| Fault: bad drive request | asks for a sweep with negative exposure | **InvalidMove**: `SweepExposureMeters …` |
| Fault: none (control) | never fails | runs normally |

## What to check

- **Single run:** pick a `Fault: …` algorithm and start a simulation. The robot wanders, then stops. The end-of-run
  panel names the failure kind and reason, the Console has a matching error, and you can start another run
  straight away. For the hangs, the editor freezes for at most the time budget (2 s or 5 s), not indefinitely.
- **Batch:** run a batch with every `Fault: …` algorithm and a few seeds. Every trial runs. The faulty ones end
  early with "algorithm failed (Crash / Hang / InvalidMove)" in the trial list. The control's trials are
  unaffected, and the batch report's anomaly section lists each failure.
- **Load time:** a constructor or `Name` that never returns is also caught. The load report then shows
  `… did not return within 2 s` for that class, and the other algorithms still load.

The automated equivalents are the EditMode tests in `Assets/Tests/EditMode/AlgorithmGuardTests.cs`.
