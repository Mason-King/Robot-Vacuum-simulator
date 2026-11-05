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
| `campaignId` | generated | Unique ID. `vacuumctl` generates `<name>-<UTC time>-<random>`. |
| `campaignName` | `"Campaign"` | Shown in results. |
| `description` | | Free text. |
| `floorTypes` | stock 6 | Floor coverings rooms can use. Empty = Hardwood, Tile, Laminate, Low Carpet, High Carpet, Area Rug with the app's values. Giving any replaces the whole list. |
| `floorPlans` | required | One or more floor plans in the format of `Robot vacuum simulator/Docs/floor-plan-format.md` (an exported floor plan file pasted in as-is). With `vacuumctl` you may instead write `{ "file": "floorplans/x.json" }` (path relative to the campaign file); it is inlined before upload. |
| `configurations` | one default | Named vacuum + battery + simulation settings. |
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
| `floorEfforts[]` | none | `{ "floor": name, "effort": 0.1-4 }` | Cleaning-effort override per covering, as on the Configuration screen. |

Robot body geometry (radius, cleaning width) is model-intrinsic (SDD 5.1) and is not a run setting.

## `sweep`

| Field | Default | Meaning |
|---|---|---|
| `algorithms` | `["RandomBounce"]` | Any of `RandomBounce`, `Spiral`, `WallFollow`, `Lawnmower` (case-insensitive). |
| `seedStart`, `seedCount` | 1, 1 | Seeds `seedStart` ... `seedStart + seedCount - 1`. |
| `seeds` | | Explicit seeds; overrides the two above. |
| `floorPlans`, `configurations` | all | Names to include. |

Runs are generated in this order, numbered from 0: **floor plan → configuration → algorithm → seed**.

## `runs[]`

`{ "runIndex": 0, "floorPlan": 0, "configuration": 0, "algorithm": "Spiral", "seed": 7 }` — `floorPlan` and
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

Each entry of `results.json` → `runs[]` has: `runIndex, floorPlan(Index), configuration(Index), algorithm,
seed, succeeded, error, endReason, coveragePercent, fullyCleanedPercent, simulatedSeconds, realSeconds,
batteryCapacity, batteryUsed, batteryEmptied, distanceMeters, revisitCount, timeTo50/75/90 (-1 = never),
floorAreaSquareMeters, blockedAreaSquareMeters, targetMaxClean, cellSizeMeters, runFolder, rooms[], warnings[]`.

`vacuumctl fetch` merges every node's file into **`campaign_results.json`**: totals (planned / returned /
failed / missing run indices), one group per floor plan × configuration × algorithm with count, mean,
sample variance, standard deviation and 95% confidence interval (Student's t) for each metric, and every
individual run so the statistics can be recomputed by hand (AT-2.8-2).
