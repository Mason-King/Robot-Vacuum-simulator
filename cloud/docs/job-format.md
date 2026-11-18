# Headless job / campaign file format (schema version 1)

One JSON file describes a whole experiment campaign: the floor coverings, the floor plans, the vacuum and
simulation configurations, and which runs to do. The same file is:

- what you write (or the Setup Screen will eventually write),
- what `vacuumctl` checks, expands and uploads to S3,
- what the Unity build reads with `-configPath` in headless mode, on AWS or on your own PC.

Units are always metric: metres, m/s, degrees/s, simulated seconds, battery work units. (The GUI's
metric/imperial switch is display-only.) Any field you leave out takes the default below, which is the same
as the GUI's default. The Unity build re-validates everything before it runs (SDD 4.8).

## Top level

| Field | Default | Meaning |
|---|---|---|
| `schemaVersion` | `1` | Must be 1. |
| `campaignId` | generated | Unique ID: 1-128 letters, digits, `.`, `_` or `-`, starting with a letter or digit (it becomes an S3 prefix and an IAM resource name). `vacuumctl` generates `<name>-<UTC time>-<random>`. |
| `campaignName` | `"Campaign"` | Shown in results. |
| `description` | | Free text. |
| `floorTypes` | stock 6 | Floor coverings rooms can use. Empty = Hardwood, Tile, Laminate, Low Carpet, High Carpet, Area Rug with the app's values. Giving any replaces the whole list. |
| `floorPlans` | required | One or more floor plans in the format of `Robot vacuum simulator/Docs/floor-plan-format.md` (an exported floor plan file pasted in as-is). With `vacuumctl` you may instead write `{ "file": "floorplans/x.json" }` (path relative to the campaign file); it is inlined before upload. |
| `configurations` | one default | Named vacuum + battery + simulation settings. |
| `algorithmFiles` | | Custom algorithms to carry to the nodes: `.cs` files and folders, relative to the campaign file. `vacuumctl` inlines them into `algorithmSources`. See "Custom algorithms" below. |
| `algorithmSources` | | `[{ "path": "MyAlgo.cs", "source": "..." }]` - what `algorithmFiles` becomes. You can also write it by hand. |
| `sweep` | | The parameter sweep that generates `runs`. |
| `runs` | from sweep | Explicit list of runs. `vacuumctl` always fills this in. |
| `execution` | | How the nodes carry it out. |

## `floorTypes[]` (1.7.7, 1.7.8, 3.6, 3.13)

| Field | Default | Range | Meaning |
|---|---|---|---|
| `name` | required | unique | Matched against rooms' `floor` without regard to case. |
| `speedMultiplier` | 1 | 0.1-2 | Multiplies drive speed on this covering. |
| `energyCostMultiplier` | 1 | 0.1-4 | Multiplies battery work cost. |
| `cleaningEffort` | 1 | 0.1-4 | Suction-seconds per unit of dirt (higher = harder to clean). |
| `soilingRate` | 1 | 0-4 | How quickly dirt builds back up. |
| `stepHeight` | 0 | 0-0.1 m | Lip the vacuum climbs onto the covering. |

Stock values: see `STOCK_FLOOR_TYPES` in `vacuumctl/campaign.py` (copied from `FloorPalette.DefaultPresets`).

## `configurations[]`

| Field | Default | Range | Requirement |
|---|---|---|---|
| `name` | `default` / `config-<i>` | unique | |
| `vacuum.driveSpeed` | 0.65 m/s | 0.01-5 | 1.3.5, 1.3.6 |
| `vacuum.reverseSpeed` | 0.35 m/s | 0.01-5 | |
| `vacuum.turnSpeed` | 320 deg/s | 1-2000 | |
| `vacuum.vacuumPower` | 1 | 0.01-100 | SDD 4.3 / 5.2 cleaning power |
| `battery.capacity` | 300 units | 1-10000 | 1.3.4 |
| `battery.distanceEnergyPerMeter` | 1 | 0-1000 | 3.6 |
| `battery.turnEnergyPerDegree` | 0.01 | 0-100 | 3.6 |
| `battery.operatingEnergyPerSecond` | 0.1 | 0-1000 | 1.3.2 |
| `battery.stalledEnergyPerSecond` | 0.03 | 0-1000 | |
| `simulation.cellSizeMeters` | 0.0508 (2 in) | 0.0127-0.1524 | 2.7 |
| `simulation.targetMaxClean` | 2 | 0.01-1000 | SDD 4.3 target_max_clean |
| `simulation.maxSimulatedSeconds` | 1800 | 1-86400 | Run ends as `TimeCap` if the battery outlasts it. |
| `simulation.telemetryInterval` | 0.5 s | 0.05-10 | 2.19 |
| `simulation.heatmapMode` | `FloorCleanliness` | `FloorCleanliness`, `FloorCoverage`, `FloorOverCoverage` | |
| `simulation.logAlgorithmDecisions` | `false` | | Write each algorithm decision to the log. Off by default here because it is very chatty and the log goes to CloudWatch (the GUI has it on). It only writes console messages: it changes no decision, random draw or result file, so GUI and headless runs of the same seed still match (2.17). |
| `sensing.slam` | `false` | | SLAM on: the robot starts with no map, sensors and odometry are noisy, and it builds its own map. Off (Normal): map preloaded, exact readings. |
| `sensing.noiseScale` | 1 | 0-10 | SLAM only. 0 = perfect, 1 = a realistic budget robot. |
| `sensing.lidarEnabled` | `true` | | Off = whisker only. |
| `sensing.lidarRangeMeters` | 8 | 0.2-50 | |
| `sensing.lidarHalfAngleDegrees` | 180 | 0-180 | Half the field of view from straight ahead; 180 = full circle. |
| `sensing.lidarRayCount` | 360 | 8-360 | Whole number. |
| `sensing.floorSensorEnabled` | `true` | | The floor-resistance sensor. |
| `sensing.slamCorrectionQuality` | `Balanced` | `Performance`, `Balanced`, `Reliable` | SLAM only: how many lidar returns each scan match uses. |
| `sensing.slamLoopClosure` | `true` | | SLAM only. |
| `floorEfforts[]` | none | `{ "floor": name, "effort": 0.1-4 }` | Cleaning-effort override per covering, as on the Configuration screen. |

Robot body geometry (radius, cleaning width) is model-intrinsic (SDD 5.1) and is not a run setting.

Every run setting in the GUI's `RunSettings` is covered above (B1). The one exception is `showBeliefOverlay`: it only
draws the robot's beliefs on screen, so it has no meaning without graphics and headless runs always have it off.
`randomSeed` is replaced by each run's explicit `seed`.

## `sweep`

| Field | Default | Meaning |
|---|---|---|
| `algorithms` | `["RandomBounce"]` | Algorithm ids or names, case-insensitive (see "Algorithms" below). |
| `seedStart`, `seedCount` | 1, 1 | Seeds `seedStart` ... `seedStart + seedCount - 1`. |
| `seeds` | | Explicit seeds; overrides the two above. |
| `floorPlans`, `configurations` | all | Names to include. |

Runs are generated in this order, numbered from 0: **floor plan → configuration → algorithm → seed**.

## Algorithms

A run's algorithm can be written as:

| Built-in id | Display name | SLAM | Also accepted |
|---|---|---|---|
| `RandomBounce` | Random | yes | |
| `BoustrophedonSweep` | Boustrophedon Sweep | yes | |
| `BoustrophedonCrawl` | Boustrophedon Crawl | yes | |
| `Legacy.Spiral` | Spiral | no | `Spiral` |
| `Legacy.WallFollow` | Wall follow | no | `WallFollow` |
| `Legacy.Lawnmower` | Lawnmower | no | `Lawnmower` |

or a custom algorithm's id, which is its `Namespace.ClassName` (or its display name). Both `vacuumctl` and the build
rewrite every run's algorithm to the catalog id, so results always say `Legacy.Spiral` even if you wrote `spiral`. A
configuration with `sensing.slam` on cannot use an algorithm that doesn't support SLAM (the legacy patterns don't); the
job is rejected before anything runs.

### Custom algorithms (3.8)

Every build ships the project's `Robot vacuum simulator/Algorithms` folder, so the algorithms in it can be used on any
node. To use one that isn't in the image, list it in `algorithmFiles`:

```json
"algorithmFiles": [ "algorithms/LongStraights.cs", "algorithms/MyProject" ]
```

- A file is carried as is; a folder is carried with its name, so all its `.cs` files are compiled together as one
  algorithm project, the same as a subfolder of the desktop Algorithms folder.
- Only `.cs` source is accepted (no DLLs): it is compiled on each node against the algorithm API and base .NET only, so
  it cannot reach Unity, the simulator, the file system through the simulator, or the true robot position (2.3, 4.8).
  C# 9 at most.
- Paths must stay inside the Algorithms folder (no `..`, drive letters or leading slash). Names starting with `_` or
  `.` are refused because the loader skips them. Limits: 64 files, 1 MB each, 4 MB in all.
- The node writes a copy of the shipped folder plus your files to a temporary folder and loads that. A carried file
  with the same path as a shipped one replaces it (the log says so). A carried file that does not compile rejects the
  whole job (exit code 2) with the compiler's errors.
- `vacuumctl validate` finds the ids in your files by pattern, to catch typos and SLAM mismatches before upload; the
  node's compiler has the final say.

See `examples/slam-custom.example.json` and `examples/algorithms/LongStraights.cs`.

## `runs[]`

`{ "runIndex": 0, "floorPlan": 0, "configuration": 0, "algorithm": "Legacy.Spiral", "seed": 7 }` — `floorPlan` and
`configuration` are indices into the lists above. `runIndex` values must be unique.

## `execution`

| Field | Default | Meaning |
|---|---|---|
| `shardCount` | 1 | Nodes to split across. Run *i* goes to node *i* mod `shardCount`. `vacuumctl --nodes` overrides it and never uses more nodes than runs. |
| `reportEveryNRuns` | 1 | A node rewrites `results.json` after every N finished runs (SDD 8 "return in steps"). |
| `saveExternalModel` | true | Keep each run's `external_model.json` (2-20 MB each). Coverage figures are computed either way. |
| `saveHeatmapImages` | true | Keep each run's three heatmap PNGs. |
| `maxRealSecondsPerRun` | 3600 | Wall-clock limit per run; a run over it is stopped and recorded as an error (2.11). 0 = none. |

## Command line of the headless build

```
<exe> -batchmode -nographics -configPath job.json [-outputPath dir] [-shardIndex i] [-shardCount n]
      [-validateOnly] [-noResume] [-logFile <file or ->]
```

`-configPath` without `-batchmode -nographics` is refused (SDD 2.1). `-shardIndex` falls back to
`AWS_BATCH_JOB_ARRAY_INDEX`, `-shardCount` to `VACUUM_SHARD_COUNT`, then `execution.shardCount`.

Exit codes: `0` all runs succeeded · `10` finished, some runs failed (see results) · `2` job rejected · `1` crashed.

## What a node writes

```
<outputPath>/
  results.json        ShardResults - status, host, every finished run's outcome
  heartbeat.json      progress, refreshed every ~2 s
  logs/unity.log      (container only)
  runs/<guid>/        one folder per run, same files as a GUI run:
                      config.json, result.json, summary.json, telemetry.csv, replay.json, parameters.csv,
                      rooms.csv, external_model.json, heatmap_*.png
```

Each entry of `results.json` → `runs[]` has: `runIndex, floorPlan(Index), configuration(Index), algorithm (catalog id),
algorithmName, seed, slam, succeeded, error, endReason, coveragePercent, fullyCleanedPercent, simulatedSeconds, realSeconds,
batteryCapacity, batteryUsed, batteryEmptied, distanceMeters, revisitCount, timeTo50/75/90 (-1 = never),
floorAreaSquareMeters, blockedAreaSquareMeters, targetMaxClean, cellSizeMeters, runFolder, rooms[], warnings[]`.

Per-cell output (2.19): for batch runs `RunRecorder` writes the final per-cell state in `external_model.json` and
skips the per-cell time series (`cell_events.csv`, `cells_final.csv`), which the GUI writes only for single runs.
`telemetry.csv` samples position, heading, velocity and battery every `telemetryInterval`. Whether that meets 2.19's
"per-cell state" telemetry is still a team decision (see `README.md` › Not done here).

On your own computer, every AWS command also appends to **`cloud-results/<campaignId>/vacuumctl.log`** (commands with
no campaign: `cloud-results/vacuumctl.log`): stack changes, uploads, status polls and every AWS or network error with
its traceback. This is the dedicated network and infrastructure error log (SDD 8).

`vacuumctl fetch` (and `local`) downloads each run's small files and merges every node's file into **`campaign_results.json`**: totals (planned / returned /
failed / missing run indices), one group per floor plan × configuration × algorithm with count, mean,
sample variance, standard deviation and 95% confidence interval (Student's t) for each metric, and every
individual run so the statistics can be recomputed by hand (AT-2.8-2). **`campaign_runs.csv`** has the same in spreadsheet form, in the same columns as the
simulator's Analysis › Export CSV. The simulator shows the campaign with Analysis › Import results.
