"""Running a campaign headless on this computer, and comparing runs (2.17).

`local` runs the same job file through the Windows build (or any build) with -batchmode -nographics,
split across N local processes, and aggregates exactly as the cloud path does. It is the quickest way to
test a campaign file, and it gives the "headless vs rendered" wall-clock comparison for AT-2.17-2.

`compare` checks two run folders (for example one rendered GUI run and one headless run with the same
configuration and seed) for identical result data (AT-2.17-1).
"""

import hashlib
import json
import os
import subprocess
import sys
import time

from . import campaign as campaigns
from . import stats


# Exit codes of the headless build (HeadlessBootstrap): finished, finished with failed runs, job rejected.
EXIT_DONE = (0, 10)
EXIT_REJECTED = 2
# Like the cloud's AWS Batch retry strategy: a shard that crashed or stopped responding is started again, up to
# this many times. Each attempt resumes after the runs already finished, and a run that keeps killing the
# simulator is skipped on the attempt after its second crash (current-run.json).
MAX_RETRIES = 2


def run_local(campaign_path, exe, out_dir, processes=1, open_report=True):
    if not os.path.isfile(exe):
        sys.exit(f"Simulator executable not found: {exe}")
    job = campaigns.prepare(campaign_path, shard_count=processes)
    count = job["execution"]["shardCount"]
    out_dir = os.path.abspath(os.path.join(out_dir, job["campaignId"]))
    job_path = os.path.join(out_dir, "job.json")
    campaigns.save(job, job_path)
    campaigns.save(campaigns.manifest(job), os.path.join(out_dir, "manifest.json"))

    print(f"Running {len(job['runs'])} run(s) headless in {count} local process(es). Output: {out_dir}", flush=True)
    started = time.monotonic()
    def launch(shard, shard_dir, attempt):
        log_name = "unity.log" if attempt == 0 else f"unity.attempt{attempt + 1}.log"
        command = [exe, "-batchmode", "-nographics", "-logFile", os.path.join(shard_dir, "logs", log_name),
                   "-configPath", job_path, "-outputPath", shard_dir,
                   "-shardIndex", str(shard), "-shardCount", str(count)]
        return subprocess.Popen(command)

    procs = []
    attempts = {}
    for shard in range(count):
        shard_dir = os.path.join(out_dir, "shards", f"shard-{shard:04d}")
        os.makedirs(os.path.join(shard_dir, "logs"), exist_ok=True)
        attempts[shard] = 0
        procs.append((shard, shard_dir, launch(shard, shard_dir, 0)))

    total = len(job["runs"])
    while any(p.poll() is None for _, _, p in procs) or _retry_crashed(procs, attempts, launch):
        time.sleep(2)
        done = 0
        for _, shard_dir, _ in procs:
            try:
                with open(os.path.join(shard_dir, "heartbeat.json"), encoding="utf-8") as handle:
                    done += json.load(handle).get("completedRuns", 0)
            except (OSError, ValueError):
                pass
        print(f"\r  {done}/{total} runs, {time.monotonic() - started:.0f} s", end="", flush=True)
    print()

    shard_results, shard_status = [], []
    for shard, shard_dir, proc in procs:
        data = _read_json(os.path.join(shard_dir, "results.json"))
        shard_results.append(data)
        shard_status.append({"shardIndex": shard, "exitCode": proc.returncode,
                             "status": (data or {}).get("status", "no results")})
        if proc.returncode not in EXIT_DONE:
            print(f"  shard {shard} exited with code {proc.returncode}; see {os.path.join(shard_dir, 'logs')}")

    results = stats.aggregate(job, shard_results, shard_status)
    results["wallClockSeconds"] = time.monotonic() - started
    path = os.path.join(out_dir, "campaign_results.json")
    campaigns.save(results, path)
    stats.write_runs_csv(results, os.path.join(out_dir, "campaign_runs.csv"))
    print(stats.format_table(results))
    print(f"Wall-clock time: {results['wallClockSeconds']:.1f} s. Results: {path}")
    print("To see it in the simulator: Analysis > Import results.")
    from .report import write_report
    write_report(path, open_browser=open_report, job=job)
    return results


def _retry_crashed(procs, attempts, launch):
    """Restart shards that crashed (not finished, not rejected) while they have retries left. True if any restarted."""
    restarted = False
    for i, (shard, shard_dir, proc) in enumerate(procs):
        code = proc.poll()
        if code is None or code in EXIT_DONE or code == EXIT_REJECTED or attempts[shard] >= MAX_RETRIES:
            continue
        attempts[shard] += 1
        print(f"\n  shard {shard} stopped with code {code}; starting it again to finish its remaining runs "
              f"(retry {attempts[shard]} of {MAX_RETRIES})", flush=True)
        procs[i] = (shard, shard_dir, launch(shard, shard_dir, attempts[shard]))
        restarted = True
    return restarted


# Fields of result.json that must match between two runs of the same configuration and seed.
RESULT_FIELDS = ["endReason", "seed", "pattern", "coveragePercent", "fullyCleanedPercent", "targetMaxClean",
                 "blockedAreaSquareMeters", "simulatedSeconds"]
SUMMARY_FIELDS = ["distanceMeters", "batteryUsed", "revisitCount", "timeTo50", "timeTo75", "timeTo90"]


def compare_runs(folder_a, folder_b):
    """True when both run folders hold identical result data. Prints every difference."""
    same = True
    for name, fields in (("result.json", RESULT_FIELDS), ("summary.json", SUMMARY_FIELDS)):
        a, b = _read_json(os.path.join(folder_a, name)), _read_json(os.path.join(folder_b, name))
        if a is None or b is None:
            print(f"  {name}: missing in {'first' if a is None else 'second'} folder - skipped")
            continue
        for field in fields:
            if a.get(field) != b.get(field):
                same = False
                print(f"  DIFF {name}:{field}: {a.get(field)} vs {b.get(field)}")
            else:
                print(f"  same {name}:{field} = {a.get(field)}")

    a_model = os.path.join(folder_a, "external_model.json")
    b_model = os.path.join(folder_b, "external_model.json")
    if os.path.isfile(a_model) and os.path.isfile(b_model):
        ha, hb = _sha256(a_model), _sha256(b_model)
        print(f"  {'same' if ha == hb else 'DIFF'} external_model.json sha256 {ha[:16]} vs {hb[:16]}")
        same = same and ha == hb

    config_a = (_read_json(os.path.join(folder_a, "config.json")) or {}).get("settings")
    config_b = (_read_json(os.path.join(folder_b, "config.json")) or {}).get("settings")
    if config_a is not None and config_b is not None and config_a != config_b:
        print("  note: the two runs' config.json settings differ, so their results are not expected to match:")
        for key in sorted(set(config_a) | set(config_b)):
            if config_a.get(key) != config_b.get(key):
                print(f"    {key}: {config_a.get(key)} vs {config_b.get(key)}")

    print("IDENTICAL" if same else "DIFFERENT")
    return same


def _sha256(path):
    digest = hashlib.sha256()
    with open(path, "rb") as handle:
        for block in iter(lambda: handle.read(1 << 20), b""):
            digest.update(block)
    return digest.hexdigest()


def _read_json(path):
    try:
        with open(path, encoding="utf-8-sig") as handle:
            return json.load(handle)
    except (OSError, ValueError):
        return None
