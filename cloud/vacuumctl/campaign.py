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

# A campaign ID becomes an S3 prefix and an IAM resource name (compute.yaml CampaignScope). Same rule as HeadlessJob.cs.
CAMPAIGN_ID_PATTERN = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$")

# The built-in algorithms (AlgorithmCatalog in CleaningAlgorithm.cs): id -> (display name, supports SLAM).
BUILTIN_ALGORITHMS = {
    "RandomBounce": ("Random", True),
    "BoustrophedonSweep": ("Boustrophedon Sweep", True),
    "BoustrophedonCrawl": ("Boustrophedon Crawl", True),
    "Legacy.Spiral": ("Spiral", False),
    "Legacy.WallFollow": ("Wall follow", False),
    "Legacy.Lawnmower": ("Lawnmower", False),
}
# The old MovementPattern names still work and mean the legacy built-ins.
ALGORITHM_ALIASES = {"spiral": "Legacy.Spiral", "wallfollow": "Legacy.WallFollow", "lawnmower": "Legacy.Lawnmower"}
ALGORITHMS = list(BUILTIN_ALGORITHMS)  # kept for older callers

CORRECTION_QUALITIES = ["Performance", "Balanced", "Reliable"]
MAX_ALGORITHM_FILES = 64
MAX_ALGORITHM_FILE_BYTES = 1024 * 1024
MAX_ALGORITHM_TOTAL_BYTES = 4 * 1024 * 1024

# The project's own Algorithms folder: shipped next to every build, so its algorithms exist on every node.
SHIPPED_ALGORITHMS_FOLDER = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..",
                                                          "Robot vacuum simulator", "Algorithms"))
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
        "logAlgorithmDecisions": False,
    },
    # RunSettings' Sensing and Advanced sections.
    "sensing": {
        "slam": False,
        "noiseScale": 1.0,
        "lidarEnabled": True,
        "lidarRangeMeters": 8.0,
        "lidarHalfAngleDegrees": 180.0,
        "lidarRayCount": 360,
        "floorSensorEnabled": True,
        "slamCorrectionQuality": "Balanced",
        "slamLoopClosure": True,
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
    ("sensing", "noiseScale", 0.0, 10.0),
    ("sensing", "lidarRangeMeters", 0.2, 50.0),
    ("sensing", "lidarHalfAngleDegrees", 0.0, 180.0),
    ("sensing", "lidarRayCount", 8, 360),
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

    # Custom algorithms: "algorithmFiles" lists .cs files and folders (relative to the campaign file); each is inlined
    # into "algorithmSources" as {path, source}, path relative to the Algorithms folder. A folder keeps its name, so it
    # stays one multi-file algorithm project on the node, as it is on the desktop.
    sources = list(job.get("algorithmSources") or [])
    for i, entry in enumerate(job.pop("algorithmFiles", None) or []):
        target = os.path.join(base, str(entry))
        if os.path.isdir(target):
            top = os.path.basename(os.path.normpath(target))
            found = []
            for folder, _, files in os.walk(target):
                for name in sorted(files):
                    if name.lower().endswith(".cs"):
                        full = os.path.join(folder, name)
                        rel = os.path.relpath(full, target).replace(os.sep, "/")
                        found.append((f"{top}/{rel}", full))
            if not found:
                raise CampaignError([f"algorithmFiles[{i}]: no .cs files in {target}."])
        elif os.path.isfile(target):
            found = [(os.path.basename(target), target)]
        else:
            raise CampaignError([f"algorithmFiles[{i}]: {target} does not exist."])
        for rel, full in sorted(found):
            try:
                with open(full, encoding="utf-8-sig") as handle:
                    sources.append({"path": rel, "source": handle.read()})
            except (OSError, UnicodeDecodeError) as error:
                raise CampaignError([f"algorithmFiles[{i}]: could not read {full}: {error}"])
    if sources:
        job["algorithmSources"] = sources
    return job


# ---------------------------------------------------------------------------------------------- algorithms

_NAMESPACE = re.compile(r"^\s*namespace\s+([A-Za-z_][\w.]*)", re.M)
_CLASS = re.compile(r"\bpublic\s+(?:sealed\s+|partial\s+)*class\s+([A-Za-z_]\w*)[^{;]*?:\s*[^{;]*\bICleaningAlgorithm\b")
_NAME = re.compile(r"\bName\s*=>\s*\"((?:[^\"\\]|\\.)*)\"")
_SLAM = re.compile(r"\bSupportsSlam\s*=>\s*(true|false)\b")


def _plugins_in(path, text):
    """Algorithms one .cs file declares, found by pattern (the build's compiler has the final say):
    [{"id": "Namespace.Class", "name": ..., "slam": bool or None, "file": path}]."""
    found = []
    namespace = _NAMESPACE.search(text)
    for match in _CLASS.finditer(text):
        body = text[match.end():]
        name = _NAME.search(body)
        slam = _SLAM.search(body)
        cls = match.group(1)
        found.append({"id": f"{namespace.group(1)}.{cls}" if namespace else cls,
                      "name": name.group(1) if name else None,
                      "slam": (slam.group(1) == "true") if slam else None,
                      "file": path})
    return found


def shipped_algorithm_sources(folder=SHIPPED_ALGORITHMS_FOLDER):
    """{path: text} for the project's Algorithms folder, skipping names starting with '_' or '.' as the loader does."""
    out = {}
    if not os.path.isdir(folder):
        return out
    for root, dirs, files in os.walk(folder):
        dirs[:] = [d for d in dirs if not d.startswith(("_", "."))]
        for name in files:
            if name.lower().endswith(".cs") and not name.startswith(("_", ".")):
                full = os.path.join(root, name)
                try:
                    with open(full, encoding="utf-8-sig") as handle:
                        out[os.path.relpath(full, folder).replace(os.sep, "/")] = handle.read()
                except (OSError, UnicodeDecodeError):
                    pass
    return out


def known_plugins(job, shipped=None):
    """Plugin algorithms a node will have: the shipped folder, with the job's carried files replacing same-named ones."""
    files = dict(shipped_algorithm_sources() if shipped is None else shipped)
    for source in job.get("algorithmSources") or []:
        files[str((source or {}).get("path", "")).replace("\\", "/")] = (source or {}).get("source") or ""
    plugins = []
    for path, text in sorted(files.items()):
        plugins.extend(_plugins_in(path, text))
    return plugins


def resolve_algorithm(name, plugins=()):
    """(catalog id, display name, supports SLAM or None) for an algorithm name, or None if it is unknown. Accepts a
    catalog id, an old pattern name (Spiral, WallFollow, Lawnmower) or a display name, in any case - the same rules
    as HeadlessJob.ResolveAlgorithmId."""
    text = str(name or "").strip()
    if not text:
        return None
    low = text.lower()
    for algorithm_id, (label, slam) in BUILTIN_ALGORITHMS.items():
        if algorithm_id.lower() == low:
            return algorithm_id, label, slam
    for plugin in plugins:
        if plugin["id"].lower() == low:
            return plugin["id"], plugin["name"] or plugin["id"], plugin["slam"]
    if low in ALGORITHM_ALIASES:
        algorithm_id = ALGORITHM_ALIASES[low]
        return algorithm_id, BUILTIN_ALGORITHMS[algorithm_id][0], BUILTIN_ALGORITHMS[algorithm_id][1]
    if low == "randombounce":
        return "RandomBounce", "Random", True
    for algorithm_id, (label, slam) in BUILTIN_ALGORITHMS.items():
        if label.lower() == low:
            return algorithm_id, label, slam
    for plugin in plugins:
        if (plugin["name"] or "").lower() == low:
            return plugin["id"], plugin["name"], plugin["slam"]
    return None


def _validate_sources(job, errors):
    sources = job.get("algorithmSources") or []
    if len(sources) > MAX_ALGORITHM_FILES:
        errors.append(f"algorithmSources: {len(sources)} files is more than the {MAX_ALGORITHM_FILES} allowed.")
    total = 0
    seen = set()
    for i, source in enumerate(sources):
        path = str((source or {}).get("path") or "").strip()
        if not path:
            errors.append(f"algorithmSources[{i}]: needs a path.")
            continue
        at = f'algorithmSources[{i}] "{path}"'
        parts = path.replace("\\", "/").split("/")
        if os.path.isabs(path) or ":" in path or path.startswith(("/", "\\")) or any(p in ("", ".", "..") for p in parts):
            errors.append(f"{at}: must be a plain relative path inside the Algorithms folder (no drive, leading slash or '..').")
        elif not path.lower().endswith(".cs"):
            errors.append(f"{at}: only C# source files (.cs) can be carried in a job.")
        elif any(p.startswith(("_", ".")) for p in parts):
            errors.append(f"{at}: names starting with '_' or '.' are skipped by the loader, so this file would never be used.")
        key = "/".join(parts).lower()
        if key in seen:
            errors.append(f"{at}: the same path is given twice.")
        seen.add(key)
        size = len(((source or {}).get("source") or "").encode("utf-8"))
        if size == 0:
            errors.append(f"{at}: the source is empty.")
        if size > MAX_ALGORITHM_FILE_BYTES:
            errors.append(f"{at}: {size} bytes is more than the {MAX_ALGORITHM_FILE_BYTES} allowed per file.")
        total += size
    if total > MAX_ALGORITHM_TOTAL_BYTES:
        errors.append(f"algorithmSources: {total} bytes in all is more than the {MAX_ALGORITHM_TOTAL_BYTES} allowed.")


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


def _parse_algorithm(name, plugins=()):
    resolved = resolve_algorithm(name, plugins)
    return resolved[0] if resolved else None


def validate(job):
    """Every problem with a normalised job, as readable sentences. Empty list = good to go."""
    errors = []
    if job.get("schemaVersion") != SCHEMA_VERSION:
        errors.append(f"schemaVersion {job.get('schemaVersion')} is not supported (expected {SCHEMA_VERSION}).")
    campaign_id = str(job.get("campaignId") or "")
    if not CAMPAIGN_ID_PATTERN.match(campaign_id):
        errors.append(f'campaignId "{campaign_id}": use 1-128 letters, digits, ".", "_" or "-", starting with a letter or digit.')

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
        quality = (config.get("sensing") or {}).get("slamCorrectionQuality")
        if str(quality).lower() not in (q.lower() for q in CORRECTION_QUALITIES):
            errors.append(f'{at}.sensing.slamCorrectionQuality: "{quality}" is not one of {", ".join(CORRECTION_QUALITIES)}.')
        if not isinstance((config.get("sensing") or {}).get("lidarRayCount"), int):
            errors.append(f"{at}.sensing.lidarRayCount must be a whole number.")
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

    _validate_sources(job, errors)
    plugins = known_plugins(job)
    configs = job.get("configurations") or []
    reported = set()

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
        resolved = resolve_algorithm(run.get("algorithm"), plugins)
        if resolved is None:
            if ("?", run.get("algorithm")) not in reported:
                reported.add(("?", run.get("algorithm")))
                known = list(BUILTIN_ALGORITHMS) + [p["id"] for p in plugins]
                errors.append(f'run {index}: unknown algorithm "{run.get("algorithm")}". Known: {", ".join(known)} '
                              f"(Spiral, WallFollow and Lawnmower also work). A custom algorithm must be in the project's "
                              f"Algorithms folder or listed in algorithmFiles.")
        elif isinstance(run.get("configuration"), int) and 0 <= run["configuration"] < len(configs):
            config = configs[run["configuration"]]
            if (config.get("sensing") or {}).get("slam") and resolved[2] is False and (config.get("name"), resolved[0]) not in reported:
                reported.add((config.get("name"), resolved[0]))
                capable = [label for label, slam in BUILTIN_ALGORITHMS.values() if slam] + [p["name"] or p["id"] for p in plugins if p["slam"]]
                errors.append(f'run {index} (configuration "{config.get("name")}"): SLAM is on, but the "{resolved[1]}" algorithm '
                              f"does not support SLAM. Turn SLAM off, or choose one that does ({', '.join(capable)}).")
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
    # Record every run by catalog id, as the node will ("spiral" -> "Legacy.Spiral"), so results and manifest agree.
    plugins = known_plugins(job)
    for run in job["runs"]:
        run["algorithm"] = _parse_algorithm(run["algorithm"], plugins)
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
