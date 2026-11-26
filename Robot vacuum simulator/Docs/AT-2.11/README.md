# AT-2.11: deliberately broken algorithms

Acceptance test 2.11: *run an algorithm deliberately modified to throw an exception or hang, and confirm the
application reports the failure and remains usable afterward, rather than crashing.*

This folder holds five broken test algorithms. They are **not** loaded unless you copy them into the `Algorithms`
folder (next to the program's `.exe` in a build, or next to `Assets` in the Unity editor). Remove them again afterwards.

| File | What it does | Expected report |
|---|---|---|
| `AT211_ThrowsInNextGoal.cs` | Wanders for 30 sim s, then throws | Algorithm failed: threw InvalidOperationException in NextGoal |
| `AT211_ThrowsInInitialize.cs` | Throws as the run starts | Algorithm failed: threw ... in Initialize, at 0 s; the robot never moves |
| `AT211_Hangs.cs` | Wanders for 30 sim s, then loops forever | About 5 s frozen, then: hung in NextGoal, no reply within 5 s; it was stopped |
| `AT211_HangsStubbornly.cs` | Same, but in a way that can't be stopped | About 7 s frozen, then: hung ... it would not stop and was abandoned |
| `AT211_BadOutput.cs` | Wanders for 30 sim s, then returns a NaN waypoint | Algorithm failed: returned invalid output from NextGoal |

## A. From the program (no command line)

1. Copy the five `.cs` files into the `Algorithms` folder, then start the program.
2. Open the algorithm list (Settings). The five "AT-2.11 ..." algorithms should appear.
3. Batch setup: pick a small floor plan; tick **Boustrophedon Sweep** and **AT-2.11 Hangs**; 2 seeds; turn
   **Headless** on; run.
   - The program may pause for a few seconds at a time, but never for good.
   - The batch finishes. Analysis shows 4 runs: the Hangs runs say the algorithm failed (hung in NextGoal), the
     Sweep runs look normal.
4. Repeat step 3 with **Throws in NextGoal**, then **Throws in Initialize**, then **Bad output**. Each run of the
   broken one is reported with its reason; the Sweep runs are normal.
5. Repeat with **Hangs stubbornly**. The batch still finishes; those runs are reported as hung (thread abandoned).
6. **Without restarting the program**, do one normal single run (graphics) with Boustrophedon Sweep. It must work
   normally. This is the heart of AT-2.11: the program is still usable after the failures.
7. In Analysis, open one broken run's folder and open `result.json`. It has `"hasFault": true`, a `fault` section
   (kind, call, exceptionType, message, stackTrace, simSeconds) and a `stopEvents` list.
8. Close the program normally. It must exit fully (no leftover "Robot Vacuum" process in Task Manager), even
   after step 5.

Also try one of them as a single graphics run: the run ends with the end panel naming the failure, and **Run again**
and the rest of the program still work.

Guard rail to know about: each "Hangs stubbornly" run leaves one thread running that can't be stopped. After
**three** of those in one session, the program refuses to start any more algorithm calls (every run ends at once
with "GuardTripped") until it is restarted. That is intended.

## B. Command line (optional)

`run_headless.bat`: double-click it, drag the simulator's `.exe` into the window, press Enter. It runs
`job_faults.json` (one Sweep run plus one run of each broken algorithm, all carried inside the job file) headless.

Expected: **exit code 10** ("finished, some runs failed"), and `out\results.json` lists the five AT-2.11 runs as
`"succeeded": false` with `"endReason": "AlgorithmFault"` and their reasons, and the Sweep run as succeeded.

## Report

For each step: pass or fail. For a failure, a screenshot and the run's folder (or `out\` for the command line).
