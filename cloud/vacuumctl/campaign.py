"""Campaign (job) files: load, fill defaults, expand the sweep, validate, shard.

This mirrors RobotVacuum.Headless.HeadlessJob in the Unity project. The Unity build validates the file
again before it runs anything, so a mistake here can never run unchecked; this copy exists so problems are
reported on your machine, before anything is uploaded or paid for. Keep the two in step (the defaults
and ranges are listed in cloud/docs/job-format.md).
"""

import copy
import datetime
import json
import os
import re
import uuid

SCHEMA_VERSION = 1

ALGORITHMS = ["RandomBounce", "Spiral", "WallFollow", "Lawnmower"]
HEATMAP_MODES = ["FloorCleanliness", "FloorCoverage", "FloorOverCoverage"]

# The app's stock floor coverings (FloorPalette.DefaultPresets), in palette order.
STOCK_FLOOR_TYPES = [
    {"name": "Hardwood", "speedMultiplier": 1.00, "energyCostMultiplier": 1.00, "cleaningEffort": 0.8, "soilingRate": 1.0, "stepHeight": 0.000},
    {"name": "Tile", "speedMultiplier": 1.00, "energyCostMultiplier": 0.90, "cleaningEffort": 0.7, "soilingRate": 1.2, "stepHeight": 0.000},
    {"name": "Laminate", "speedMultiplier": 0.95, "energyCostMultiplier": 1.05, "cleaningEffort": 0.8, "soilingRate": 1.0, "stepHeight": 0.000},
    {"name": "Low Carpet", "speedMultiplier": 0.85, "energyCostMultiplier": 1.35, "cleaningEffort": 1.6, "soilingRate": 1.4, "stepHeight": 0.008},
    {"name": "High Carpet", "speedMultiplier": 0.60, "energyCostMultiplier": 1.80, "cleaningEffort": 2.4, "soilingRate": 1.6, "stepHeight": 0.020},
    {"name": "Area Rug", "speedMultiplier": 0.75, "energyCostMultiplier": 1.55, "cleaningEffort": 2.0, "soilingRate": 1.3, "stepHeight": 0.015},
]

FLOOR_TYPE_DEFAULTS = {"speedMultiplier": 1.0, "energyCostMultiplier": 1.0, "cleaningEffort": 1.0, "soilingRate": 1.0, "stepHeight": 0.0}

# Same defaults as the GUI (VacuumSettings.Defaults, Battery, SimDefaults, HeatmapDefaults, SimConfig).
CONFIGURATION_DEFAULTS = {
    "name": "default",
    "vacuum": {"driveSpeed": 0.65, "reverseSpeed": 0.35, "turnSpeed": 320.0, "vacuumPower": 1.0},
    "battery": {
        "capacity": 300.0,
        "distanceEnergyPerMeter": 1.0,
        "turnEnergyPerDegree": 0.01,
        "operatingEnergyPerSecond": 0.1,
        "stalledEnergyPerSecond": 0.03,
    },
    "simulation": {
        "cellSizeMeters": 0.0508,
        "targetMaxClean": 2.0,
        "maxSimulatedSeconds": 1800.0,
        "telemetryInterval": 0.5,
        "heatmapMode": "FloorCleanliness",
    },
    "floorEfforts": [],
}

SWEEP_DEFAULTS = {"algorithms": [], "seedStart": 1, "seedCount": 1, "seeds": [], "floorPlans": [], "configurations": []}

EXECUTION_DEFAULTS = {
    "shardCount": 1,
    "reportEveryNRuns": 1,
    "saveExternalModel": True,
    "saveHeatmapImages": True,
    "maxRealSecondsPerRun": 3600.0,
}

# (section, field, min, max) - identical to HeadlessJob.Validate.
RANGES = [
    ("vacuum", "driveSpeed", 0.01, 5.0),
    ("vacuum", "reverseSpeed", 0.01, 5.0),
    ("vacuum", "turnSpeed", 1.0, 2000.0),
    ("vacuum", "vacuumPower", 0.01, 100.0),
    ("battery", "capacity", 1.0, 10000.0),
    ("battery", "distanceEnergyPerMeter", 0.0, 1000.0),
    ("battery", "turnEnergyPerDegree", 0.0, 100.0),
    ("battery", "operatingEnergyPerSecond", 0.0, 1000.0),
    ("battery", "stalledEnergyPerSecond", 0.0, 1000.0),
    ("simulation", "cellSizeMeters", 0.0127, 0.1524),
    ("simulation", "targetMaxClean", 0.01, 1000.0),
    ("simulation", "maxSimulatedSeconds", 1.0, 86400.0),
    ("simulation", "telemetryInterval", 0.05, 10.0),
]

FLOOR_RANGES = [
    ("speedMultiplier", 0.1, 2.0),
    ("energyCostMultiplier", 0.1, 4.0),
    ("cleaningEffort", 0.1, 4.0),
    ("soilingRate", 0.0, 4.0),
    ("stepHeight", 0.0, 0.1),
]
EFFORT_RANGE = (0.1, 4.0)


class CampaignError(Exception):
    """The campaign file can't be used; .errors lists every reason."""

    def __init__(self, errors):
        super().__init__("\n".join(errors))
        self.errors = errors


def _merge(defaults, given):
    """defaults overlaid with given, recursively for dicts. Lists and scalars in given win."""
    merged = copy.deepcopy(defaults)
    for key, value in (given or {}).items():
        if isinstance(value, dict) and isinstance(merged.get(key), dict):
            merged[key] = _merge(merged[key], value)
        else:
            merged[key] = value
    return merged


def new_campaign_id(name):
    slug = re.sub(r"[^a-z0-9]+", "-", (name or "campaign").lower()).strip("-")[:32] or "campaign"
    stamp = datetime.datetime.now(datetime.timezone.utc).strftime("%Y%m%d-%H%M%S")
    return f"{slug}-{stamp}-{uuid.uuid4().hex[:4]}"


def load(path):
    """Reads a campaign file, inlining floor plans given as {"file": "..."} (relative to the campaign file)."""
    with open(path, encoding="utf-8-sig") as handle:
        job = json.load(handle)

    base = os.path.dirname(os.path.abspath(path))
    plans = []
    for i, plan in enumerate(job.get("floorPlans") or []):
        if isinstance(plan, dict) and "file" in plan and "rooms" not in plan:
            plan_path = os.path.join(base, plan["file"])
            try:
                with open(plan_path, encoding="utf-8-sig") as handle:
                    loaded = json.load(handle)
            except (OSError, ValueError) as error:
                raise CampaignError([f"floorPlans[{i}]: could not read {plan_path}: {error}"])
            if plan.get("name"):
                loaded["name"] = plan["name"]
            plans.append(loaded)
        else:
            plans.append(plan)
    job["floorPlans"] = plans
    return job


def normalise(job):
    """A copy with every default filled in, a campaign ID, and the sweep expanded into explicit runs."""
    job = copy.deepcopy(job)
    job.setdefault("schemaVersion", SCHEMA_VERSION)
    job["campaignName"] = job.get("campaignName") or "Campaign"
    job["campaignId"] = job.get("campaignId") or new_campaign_id(job["campaignName"])
    job["floorTypes"] = [_merge(FLOOR_TYPE_DEFAULTS, f) for f in (job.get("floorTypes") or [])]
    job["floorPlans"] = job.get("floorPlans") or []

    configs = job.get("configurations") or [{}]
    job["configurations"] = []
    for i, config in enumerate(configs):
        merged = _merge(CONFIGURATION_DEFAULTS, config)
        if not config.get("name"):
            merged["name"] = "default" if len(configs) == 1 else f"config-{i}"
        job["configurations"].append(merged)

    job["sweep"] = _merge(SWEEP_DEFAULTS, job.get("sweep"))
    job["execution"] = _merge(EXECUTION_DEFAULTS, job.get("execution"))
    if not job.get("runs"):
        job["runs"], _ = expand_sweep(job)
    return job


def _select(names, items, what, errors):
    if not names:
        return list(range(len(items)))
    picked = []
    for name in names:
        found = next((i for i, item in enumerate(items) if (item or {}).get("name", "").lower() == str(name).lower()), None)
        if found is None:
            errors.append(f'sweep: no {what} named "{name}".')
        else:
            picked.append(found)
    return picked


def expand_sweep(job):
    """Floor plan, then configuration, then algorithm, then seed - the order HeadlessJob.ExpandSweep uses."""
    errors = []
    sweep = job.get("sweep") or SWEEP_DEFAULTS
    plans = _select(sweep.get("floorPlans"), job.get("floorPlans") or [], "floor plan", errors)
    configs = _select(sweep.get("configurations"), job.get("configurations") or [], "configuration", errors)
    algorithms = sweep.get("algorithms") or ["RandomBounce"]
    seeds = list(sweep.get("seeds") or [])
    if not seeds:
        start = int(sweep.get("seedStart", 1))
        seeds = [start + s for s in range(max(1, int(sweep.get("seedCount", 1))))]

    runs = []
    for plan in plans:
        for config in configs:
            for algorithm in algorithms:
                for seed in seeds:
                    runs.append({"runIndex": len(runs), "floorPlan": plan, "configuration": config,
                                 "algorithm": algorithm, "seed": int(seed)})
    return runs, errors


def _number(value):
    return isinstance(value, (int, float)) and not isinstance(value, bool) and value == value


def _check(errors, label, value, low, high):
    if not _number(value) or value < low or value > high:
        errors.append(f"{label} = {value} is outside {low}-{high}.")


def _parse_algorithm(name):
    for known in ALGORITHMS:
        if str(name).strip().lower() == known.lower():
            return known
    return None


def validate(job):
    """Every problem with a normalised job, as readable sentences. Empty list = good to go."""
    errors = []
    if job.get("schemaVersion") != SCHEMA_VERSION:
        errors.append(f"schemaVersion {job.get('schemaVersion')} is not supported (expected {SCHEMA_VERSION}).")

    floor_names = []
    for i, floor in enumerate(job.get("floorTypes") or []):
        name = (floor or {}).get("name")
        if not name:
            errors.append(f"floorTypes[{i}]: needs a name.")
            continue
        if name.lower() in (n.lower() for n in floor_names):
            errors.append(f'floorTypes[{i}]: the name "{name}" is used twice.')
        floor_names.append(name)
        for field, low, high in FLOOR_RANGES:
            _check(errors, f"floorTypes[{i}].{field}", floor.get(field), low, high)
    if not floor_names:
        floor_names = [f["name"] for f in STOCK_FLOOR_TYPES]
    known_floors = {n.lower() for n in floor_names}

    plans = job.get("floorPlans") or []
    if not plans:
        errors.append("floorPlans: at least one floor plan is needed.")
    for i, plan in enumerate(plans):
        rooms = (plan or {}).get("rooms") or []
        valid = [r for r in rooms if r and len(r.get("outline") or []) >= 3]
        if not valid:
            errors.append(f"floorPlans[{i}]: needs at least one room with 3 or more corners.")
            continue
        for room in rooms:
            if not room:
                continue
            floor = room.get("floor")
            if floor:
                if floor.strip().lower() not in known_floors:
                    errors.append(f'floorPlans[{i}] "{plan.get("name")}" room "{room.get("name")}": unknown floor "{floor}". '
                                  f"Known: {', '.join(floor_names)}.")
            else:
                index = room.get("floorIndex", 0)
                if not isinstance(index, int) or index < 0 or index >= len(floor_names):
                    errors.append(f'floorPlans[{i}] "{plan.get("name")}" room "{room.get("name")}": floorIndex {index} is out of range.')
        for doorway in plan.get("doorways") or []:
            if doorway and (doorway.get("roomA", -1) >= len(rooms) or doorway.get("roomB", -1) >= len(rooms)):
                errors.append(f'floorPlans[{i}] "{plan.get("name")}": a doorway links a room index that doesn\'t exist.')

    config_names = []
    for i, config in enumerate(job.get("configurations") or []):
        at = f'configurations[{i}] "{config.get("name")}"'
        if str(config.get("name")).lower() in (n.lower() for n in config_names):
            errors.append(f"{at}: the name is used twice.")
        config_names.append(str(config.get("name")))
        for section, field, low, high in RANGES:
            _check(errors, f"{at}.{section}.{field}", (config.get(section) or {}).get(field), low, high)
        mode = (config.get("simulation") or {}).get("heatmapMode")
        if str(mode).lower() not in (m.lower() for m in HEATMAP_MODES):
            errors.append(f'{at}.simulation.heatmapMode: "{mode}" is not one of {", ".join(HEATMAP_MODES)}.')
        for effort in config.get("floorEfforts") or []:
            name = (effort or {}).get("floor")
            if not name:
                errors.append(f"{at}.floorEfforts: every entry needs a floor name.")
                continue
            if name.lower() not in known_floors:
                errors.append(f'{at}.floorEfforts: unknown floor "{name}".')
            _check(errors, f"{at}.floorEfforts[{name}]", effort.get("effort"), *EFFORT_RANGE)

    _, sweep_errors = expand_sweep(job)
    errors.extend(sweep_errors)

    runs = job.get("runs") or []
    if not runs:
        errors.append("runs: nothing to run (no runs listed and the sweep is empty).")
    seen = set()
    for run in runs:
        index = run.get("runIndex")
        if index in seen:
            errors.append(f"runs: runIndex {index} appears twice.")
        seen.add(index)
        if not isinstance(index, int) or index < 0:
            errors.append(f"runs: runIndex {index} must be a whole number of 0 or more.")
        if not isinstance(run.get("floorPlan"), int) or not 0 <= run["floorPlan"] < len(plans):
            errors.append(f"run {index}: floorPlan {run.get('floorPlan')} is out of range.")
        if not isinstance(run.get("configuration"), int) or not 0 <= run["configuration"] < len(job.get("configurations") or []):
            errors.append(f"run {index}: configuration {run.get('configuration')} is out of range.")
        if _parse_algorithm(run.get("algorithm")) is None:
            errors.append(f'run {index}: unknown algorithm "{run.get("algorithm")}". Known: {", ".join(ALGORITHMS)}.')
        if not isinstance(run.get("seed"), int):
            errors.append(f"run {index}: seed must be a whole number.")

    execution = job.get("execution") or {}
    if not isinstance(execution.get("shardCount"), int) or not 1 <= execution["shardCount"] <= 10000:
        errors.append("execution.shardCount must be a whole number 1-10000.")
    if not isinstance(execution.get("reportEveryNRuns"), int) or execution["reportEveryNRuns"] < 1:
        errors.append("execution.reportEveryNRuns must be at least 1.")
    if not _number(execution.get("maxRealSecondsPerRun")) or execution["maxRealSecondsPerRun"] < 0:
        errors.append("execution.maxRealSecondsPerRun can't be negative (0 = no limit).")
    return errors


def prepare(path, shard_count=None, campaign_id=None):
    """load + normalise + validate. Raises CampaignError listing every problem."""
    job = load(path)
    if campaign_id:
        job["campaignId"] = campaign_id
    job = normalise(job)
    if shard_count is not None:
        job["execution"]["shardCount"] = int(shard_count)
    # Never send more nodes than runs: an empty node still costs a start-up.
    job["execution"]["shardCount"] = max(1, min(job["execution"]["shardCount"], len(job["runs"]) or 1))
    errors = validate(job)
    if errors:
        raise CampaignError(errors)
    return job


def shard_of(run_index, shard_count):
    """Run i goes to node i mod shardCount (HeadlessJob.RunsForShard)."""
    return run_index % max(1, shard_count)


def manifest(job):
    """Which runs each node owns - used to spot missing runs when results come back (5.5 partial recovery)."""
    count = job["execution"]["shardCount"]
    shards = [[] for _ in range(count)]
    for run in job["runs"]:
        shards[shard_of(run["runIndex"], count)].append(run["runIndex"])
    return {
        "campaignId": job["campaignId"],
        "campaignName": job["campaignName"],
        "createdUtc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
        "totalRuns": len(job["runs"]),
        "shardCount": count,
        "shards": [{"shardIndex": i, "runIndices": runs} for i, runs in enumerate(shards)],
    }


def save(job, path):
    os.makedirs(os.path.dirname(os.path.abspath(path)), exist_ok=True)
    with open(path, "w", encoding="utf-8") as handle:
        json.dump(job, handle, indent=2)
