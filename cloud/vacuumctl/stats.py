"""Merging node results and the batch statistics (requirement 2.8).

The statistics match the GUI's BatchStats: sample variance (n - 1) and a two-sided 95% confidence
interval for the mean from Student's t. Every per-run value is kept in campaign_results.json, so the
figures can be recomputed by hand (AT-2.8-2).
"""

import datetime
import math

# Two-sided 95% critical values of Student's t for 1-30 degrees of freedom (BatchStats.TTable).
T_TABLE = [
    12.706, 4.303, 3.182, 2.776, 2.571, 2.447, 2.365, 2.306, 2.262, 2.228,
    2.201, 2.179, 2.160, 2.145, 2.131, 2.120, 2.110, 2.101, 2.093, 2.086,
    2.080, 2.074, 2.069, 2.064, 2.060, 2.056, 2.052, 2.048, 2.045, 2.042,
]

# Metrics summarised per group. timeTo* only count runs that reached the level (-1 = never).
METRICS = [
    "coveragePercent",
    "fullyCleanedPercent",
    "simulatedSeconds",
    "batteryUsed",
    "distanceMeters",
    "revisitCount",
    "timeTo50",
    "timeTo75",
    "timeTo90",
    "realSeconds",
]
REACHED_ONLY = {"timeTo50", "timeTo75", "timeTo90"}


def t_critical_95(degrees_of_freedom):
    if degrees_of_freedom < 1:
        return 0.0
    if degrees_of_freedom <= len(T_TABLE):
        return T_TABLE[degrees_of_freedom - 1]
    if degrees_of_freedom <= 60:
        return 2.000
    if degrees_of_freedom <= 120:
        return 1.980
    return 1.960


def summarise(values):
    """count, mean, sample variance, standard deviation and the 95% CI of the mean."""
    values = [float(v) for v in values]
    n = len(values)
    if n == 0:
        return {"count": 0, "mean": None, "variance": None, "stdDev": None, "ci95HalfWidth": None,
                "ci95Low": None, "ci95High": None, "min": None, "max": None}
    mean = sum(values) / n
    variance = sum((v - mean) ** 2 for v in values) / (n - 1) if n > 1 else 0.0
    half = t_critical_95(n - 1) * math.sqrt(variance / n) if n > 1 else 0.0
    return {
        "count": n,
        "mean": mean,
        "variance": variance,
        "stdDev": math.sqrt(variance),
        "ci95HalfWidth": half,
        "ci95Low": mean - half,
        "ci95High": mean + half,
        "min": min(values),
        "max": max(values),
    }


def merge_shards(shard_results):
    """All run outcomes from every node, one per runIndex (a later attempt replaces an earlier one)."""
    by_index = {}
    hosts, problems = [], []
    for shard in shard_results:
        if not shard:
            continue
        if shard.get("host"):
            hosts.append({"shardIndex": shard.get("shardIndex"), **shard["host"]})
        for error in shard.get("errors") or []:
            problems.append(f"shard {shard.get('shardIndex')}: {error}")
        for run in shard.get("runs") or []:
            by_index[run["runIndex"]] = run
    return [by_index[i] for i in sorted(by_index)], hosts, problems


def aggregate(job, shard_results, shard_status=None):
    """campaign_results.json: totals, per-group statistics and every run, ready for analysis (2.8, 4.5)."""
    runs, hosts, problems = merge_shards(shard_results)
    planned = {r["runIndex"] for r in job["runs"]}
    returned = {r["runIndex"] for r in runs}

    groups = {}
    for run in runs:
        key = (run.get("floorPlan"), run.get("configuration"), run.get("algorithm"))
        groups.setdefault(key, []).append(run)

    summaries = []
    for (plan, config, algorithm), members in sorted(groups.items(), key=lambda kv: tuple(str(k) for k in kv[0])):
        good = [r for r in members if r.get("succeeded")]
        metrics = {}
        for metric in METRICS:
            values = [r.get(metric) for r in good if r.get(metric) is not None]
            if metric in REACHED_ONLY:
                values = [v for v in values if v >= 0]
            metrics[metric] = summarise(values)
        summaries.append({
            "floorPlan": plan,
            "configuration": config,
            "algorithm": algorithm,
            "runs": len(members),
            "succeeded": len(good),
            "failed": len(members) - len(good),
            "seeds": sorted(r.get("seed") for r in good),
            "metrics": metrics,
        })

    # Best mean coverage first within each floor plan / configuration, as the GUI's comparison lists it.
    summaries.sort(key=lambda g: (str(g["floorPlan"]), str(g["configuration"]), -(g["metrics"]["coveragePercent"]["mean"] or -1)))

    return {
        "schemaVersion": 1,
        "campaignId": job["campaignId"],
        "campaignName": job.get("campaignName"),
        "generatedUtc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
        "totals": {
            "plannedRuns": len(planned),
            "returnedRuns": len(returned & planned),
            "succeededRuns": sum(1 for r in runs if r.get("succeeded")),
            "failedRuns": sum(1 for r in runs if not r.get("succeeded")),
            "missingRuns": sorted(planned - returned),
        },
        "statistics": {"varianceKind": "sample (n-1)", "confidenceInterval": "95%, Student's t, two-sided"},
        "groups": summaries,
        "shards": shard_status or [],
        "hosts": hosts,
        "problems": problems,
        "runs": runs,
    }


def format_table(results):
    """A readable summary for the terminal."""
    lines = []
    totals = results["totals"]
    lines.append(f"Campaign {results['campaignName']} ({results['campaignId']})")
    lines.append(f"  runs: {totals['returnedRuns']}/{totals['plannedRuns']} returned, "
                 f"{totals['succeededRuns']} succeeded, {totals['failedRuns']} failed, {len(totals['missingRuns'])} missing")
    header = f"  {'floor plan':<18} {'config':<12} {'algorithm':<13} {'n':>4}  {'coverage % (95% CI)':<24} {'sim time s':>11} {'battery used':>13}"
    lines.append(header)
    lines.append("  " + "-" * (len(header) - 2))
    for group in results["groups"]:
        cov = group["metrics"]["coveragePercent"]
        sec = group["metrics"]["simulatedSeconds"]
        bat = group["metrics"]["batteryUsed"]
        if cov["count"]:
            coverage = f"{cov['mean']:.2f} ({cov['ci95Low']:.2f}-{cov['ci95High']:.2f})"
        else:
            coverage = "-"
        lines.append(f"  {str(group['floorPlan'])[:18]:<18} {str(group['configuration'])[:12]:<12} {str(group['algorithm'])[:13]:<13} "
                     f"{group['succeeded']:>4}  {coverage:<24} {_fmt(sec['mean']):>11} {_fmt(bat['mean']):>13}")
    for problem in results.get("problems") or []:
        lines.append(f"  ! {problem}")
    return "\n".join(lines)


def _fmt(value):
    return "-" if value is None else f"{value:.1f}"
