#!/usr/bin/env python3
"""Container entrypoint for one headless node (one AWS Batch array child).

1. Fetch the job file (from S3, or a local path for `docker run` tests).
2. Fetch this shard's earlier results.json, if a previous attempt left one, so the run resumes.
3. Run the Unity build headless on this node's shard of the job.
4. While it runs, upload results.json / heartbeat.json / finished run folders every few seconds,
   so progress is visible and a dead node still returns what it finished (4.5, 5.5).
5. Upload everything once more at the end and exit with a code AWS Batch can act on.

Exit codes: 0 = shard done (individual run failures are recorded in results.json, not retried),
2 = the job file was rejected (do not retry), anything else = crash (AWS Batch may retry; the retry resumes).
"""

import argparse
import glob
import os
import shutil
import signal
import subprocess
import sys
import threading
import time
from urllib.parse import urlparse

UNITY_EXIT_OK = 0
UNITY_EXIT_INVALID = 2
UNITY_EXIT_SOME_RUNS_FAILED = 10


def log(message):
    print(f"[entrypoint] {message}", flush=True)


def parse_s3(uri):
    parsed = urlparse(uri)
    if parsed.scheme != "s3" or not parsed.netloc:
        raise ValueError(f"not an s3:// URI: {uri}")
    return parsed.netloc, parsed.path.lstrip("/")


def find_executable():
    explicit = os.environ.get("VACUUM_EXE")
    if explicit and os.path.isfile(explicit):
        return explicit
    home = os.environ.get("VACUUM_HOME", "/opt/vacuum")
    candidates = sorted(glob.glob(os.path.join(home, "*.x86_64")))
    if not candidates:
        raise FileNotFoundError(f"no Unity *.x86_64 executable found in {home}")
    return candidates[0]


class S3Sync:
    """Uploads files under a local folder to an S3 prefix, only the ones that changed since last time."""

    def __init__(self, local_root, s3_uri):
        import boto3  # imported here so local-only runs don't need boto3

        self.local_root = local_root
        self.bucket, prefix = parse_s3(s3_uri)
        self.prefix = prefix.rstrip("/") + "/" if prefix else ""
        self.s3 = boto3.client("s3")
        self.seen = {}
        self.lock = threading.Lock()

    def download_if_exists(self, relative, local_path):
        from botocore.exceptions import ClientError

        try:
            self.s3.download_file(self.bucket, self.prefix + relative, local_path)
            return True
        except ClientError:
            return False

    def sync(self, final=False):
        with self.lock:
            uploaded = 0
            for directory, _, files in os.walk(self.local_root):
                for name in files:
                    if name.endswith(".tmp"):
                        continue
                    path = os.path.join(directory, name)
                    try:
                        stat = os.stat(path)
                    except FileNotFoundError:
                        continue
                    signature = (stat.st_size, stat.st_mtime_ns)
                    if self.seen.get(path) == signature:
                        continue
                    # results.json goes last (below) so readers never see results ahead of their run folders.
                    if name == "results.json" and os.path.dirname(path) == self.local_root:
                        continue
                    key = self.prefix + os.path.relpath(path, self.local_root).replace(os.sep, "/")
                    try:
                        self.s3.upload_file(path, self.bucket, key)
                        self.seen[path] = signature
                        uploaded += 1
                    except Exception as error:  # keep going; the next sync retries
                        log(f"upload of {key} failed: {error}")

            results = os.path.join(self.local_root, "results.json")
            if os.path.isfile(results):
                stat = os.stat(results)
                signature = (stat.st_size, stat.st_mtime_ns)
                if self.seen.get(results) != signature:
                    try:
                        self.s3.upload_file(results, self.bucket, self.prefix + "results.json")
                        self.seen[results] = signature
                        uploaded += 1
                    except Exception as error:
                        log(f"upload of results.json failed: {error}")
            if final:
                log(f"final upload done ({uploaded} file(s)) to s3://{self.bucket}/{self.prefix}")


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    source = parser.add_mutually_exclusive_group(required=True)
    source.add_argument("--job-uri", help="s3://bucket/key of the job file")
    source.add_argument("--job-file", help="local job file (for docker run tests)")
    target = parser.add_mutually_exclusive_group(required=True)
    target.add_argument("--output-uri", help="s3://bucket/prefix that shard folders go under")
    target.add_argument("--output-dir", help="local folder that shard folders go under (for docker run tests)")
    parser.add_argument("--shard-index", type=int, default=None)
    parser.add_argument("--shard-count", type=int, default=None)
    parser.add_argument("--sync-interval", type=float, default=float(os.environ.get("VACUUM_SYNC_SECONDS", "15")))
    args = parser.parse_args()

    shard_index = args.shard_index
    if shard_index is None:
        shard_index = int(os.environ.get("AWS_BATCH_JOB_ARRAY_INDEX", "0"))
    shard_count = args.shard_count or int(os.environ.get("VACUUM_SHARD_COUNT", "1"))
    shard_name = f"shard-{shard_index:04d}"

    work = os.environ.get("VACUUM_WORK", "/work")
    os.makedirs(work, exist_ok=True)
    job_path = os.path.join(work, "job.json")
    out_dir = os.path.join(args.output_dir, shard_name) if args.output_dir else os.path.join(work, shard_name)
    os.makedirs(os.path.join(out_dir, "logs"), exist_ok=True)

    log(f"shard {shard_index + 1} of {shard_count}, AWS Batch job {os.environ.get('AWS_BATCH_JOB_ID', '-')}, "
        f"attempt {os.environ.get('AWS_BATCH_JOB_ATTEMPT', '1')}, image {os.environ.get('VACUUM_IMAGE_REVISION', '?')}")

    sync = None
    if args.job_uri:
        import boto3

        bucket, key = parse_s3(args.job_uri)
        boto3.client("s3").download_file(bucket, key, job_path)
        log(f"job file fetched from {args.job_uri}")
    else:
        shutil.copyfile(args.job_file, job_path)

    if args.output_uri:
        sync = S3Sync(out_dir, args.output_uri.rstrip("/") + "/" + shard_name)
        if sync.download_if_exists("results.json", os.path.join(out_dir, "results.json")):
            log("found results from an earlier attempt; finished runs will be skipped")

    exe = find_executable()
    command = [
        exe, "-batchmode", "-nographics", "-logFile", "-",
        "-configPath", job_path, "-outputPath", out_dir,
        "-shardIndex", str(shard_index), "-shardCount", str(shard_count),
    ]
    log("starting: " + " ".join(command))

    unity_log = open(os.path.join(out_dir, "logs", "unity.log"), "w", encoding="utf-8", errors="replace")
    process = subprocess.Popen(command, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True,
                               encoding="utf-8", errors="replace", bufsize=1)

    def forward_output():
        for line in process.stdout:
            sys.stdout.write(line)  # -> CloudWatch Logs (3.12)
            unity_log.write(line)
        unity_log.flush()

    reader = threading.Thread(target=forward_output, daemon=True)
    reader.start()

    # AWS Batch sends SIGTERM on Spot interruption, timeout or cancel: pass it on, then upload what exists.
    def on_term(signum, _frame):
        log(f"signal {signum} received; stopping the simulator and uploading partial results")
        if process.poll() is None:
            process.terminate()

    signal.signal(signal.SIGTERM, on_term)
    signal.signal(signal.SIGINT, on_term)

    next_sync = time.monotonic() + args.sync_interval
    while process.poll() is None:
        time.sleep(1)
        if sync and time.monotonic() >= next_sync:
            sync.sync()
            next_sync = time.monotonic() + args.sync_interval

    reader.join(timeout=10)
    unity_log.close()
    code = process.returncode
    log(f"simulator exited with code {code}")

    if sync:
        sync.sync(final=True)

    if code in (UNITY_EXIT_OK, UNITY_EXIT_SOME_RUNS_FAILED):
        return 0
    if code == UNITY_EXIT_INVALID:
        return 2
    return code if code and code > 0 else 1


if __name__ == "__main__":
    sys.exit(main())
