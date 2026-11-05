"""Everything that talks to AWS: CloudFormation stacks, S3, AWS Batch, CloudWatch Logs and ECR.

Credentials are never stored by this tool: boto3 reads them from your AWS profile (~/.aws/credentials or
AWS SSO), named with --profile (C.12, SDD 8 "credential reference to an external file").
"""

import base64
import datetime
import json
import os
import shutil
import subprocess
import sys
import time

from . import campaign as campaigns
from . import stats

CLOUD_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
INFRA_DIR = os.path.join(CLOUD_DIR, "infra")
DOCKER_DIR = os.path.join(CLOUD_DIR, "docker")

TERMINAL = {"SUCCEEDED", "FAILED"}


def log(message):
    print(message, flush=True)


class Cloud:
    def __init__(self, profile=None, region=None, project="robot-vacuum-sim"):
        try:
            import boto3
        except ImportError:
            sys.exit("boto3 is not installed. Run: pip install -r cloud/vacuumctl/requirements.txt")

        self.session = boto3.Session(profile_name=profile, region_name=region)
        if not self.session.region_name:
            sys.exit("No AWS region set. Pass --region (e.g. --region us-east-2) or set one in your AWS profile.")
        self.project = project
        self.region = self.session.region_name
        self.cfn = self.session.client("cloudformation")
        self.s3 = self.session.client("s3")
        self.batch = self.session.client("batch")
        self.logs = self.session.client("logs")
        self.ecr = self.session.client("ecr")

    @property
    def base_stack(self):
        return f"{self.project}-base"

    @property
    def compute_stack(self):
        return f"{self.project}-compute"

    # ------------------------------------------------------------------ stacks

    def outputs(self, stack):
        from botocore.exceptions import ClientError

        try:
            found = self.cfn.describe_stacks(StackName=stack)["Stacks"][0]
        except ClientError:
            return None
        if not found["StackStatus"].endswith("_COMPLETE") or "ROLLBACK" in found["StackStatus"]:
            return None
        return {o["OutputKey"]: o["OutputValue"] for o in found.get("Outputs", [])}

    def base_outputs(self):
        outputs = self.outputs(self.base_stack)
        if not outputs:
            sys.exit(f"The base stack '{self.base_stack}' isn't set up in {self.region}. Run: python -m vacuumctl bootstrap")
        return outputs

    def _stack_status(self, stack):
        from botocore.exceptions import ClientError

        try:
            return self.cfn.describe_stacks(StackName=stack)["Stacks"][0]["StackStatus"]
        except ClientError:
            return None

    def deploy(self, stack, template_name, parameters):
        from botocore.exceptions import ClientError, WaiterError

        with open(os.path.join(INFRA_DIR, template_name), encoding="utf-8") as handle:
            body = handle.read()
        params = [{"ParameterKey": k, "ParameterValue": str(v)} for k, v in parameters.items()]
        tags = [{"Key": "Project", "Value": self.project}]

        status = self._stack_status(stack)
        if status in ("ROLLBACK_COMPLETE", "ROLLBACK_FAILED", "DELETE_FAILED"):
            log(f"{stack} is in {status}; deleting it before creating it again.")
            self.delete(stack)
            status = None

        try:
            if status is None:
                log(f"Creating {stack} ... (a few minutes)")
                self.cfn.create_stack(StackName=stack, TemplateBody=body, Parameters=params, Tags=tags,
                                      Capabilities=["CAPABILITY_NAMED_IAM"], OnFailure="ROLLBACK")
                self.cfn.get_waiter("stack_create_complete").wait(StackName=stack, WaiterConfig={"Delay": 10, "MaxAttempts": 180})
            else:
                log(f"Updating {stack} ...")
                try:
                    self.cfn.update_stack(StackName=stack, TemplateBody=body, Parameters=params, Tags=tags,
                                          Capabilities=["CAPABILITY_NAMED_IAM"])
                except ClientError as error:
                    if "No updates are to be performed" in str(error):
                        log(f"{stack} is already up to date.")
                        return self.outputs(stack)
                    raise
                self.cfn.get_waiter("stack_update_complete").wait(StackName=stack, WaiterConfig={"Delay": 10, "MaxAttempts": 180})
        except WaiterError:
            self._print_failure(stack)
            sys.exit(f"{stack} could not be deployed (see the reasons above).")
        log(f"{stack} is ready.")
        return self.outputs(stack)

    def _print_failure(self, stack):
        try:
            events = self.cfn.describe_stack_events(StackName=stack)["StackEvents"]
        except Exception:
            return
        for event in events[:25]:
            if "FAILED" in event.get("ResourceStatus", ""):
                log(f"  {event['LogicalResourceId']}: {event.get('ResourceStatusReason', '')}")

    def delete(self, stack):
        if self._stack_status(stack) is None:
            log(f"{stack} does not exist; nothing to delete.")
            return
        log(f"Deleting {stack} ...")
        self.cfn.delete_stack(StackName=stack)
        self.cfn.get_waiter("stack_delete_complete").wait(StackName=stack, WaiterConfig={"Delay": 10, "MaxAttempts": 180})
        log(f"{stack} deleted.")

    # ------------------------------------------------------------------ one-time setup

    def bootstrap(self, budget_email="", monthly_budget=20, github_repo="", create_oidc=True, retention_days=90):
        outputs = self.deploy(self.base_stack, "base.yaml", {
            "ProjectName": self.project,
            "BudgetEmail": budget_email or "",
            "MonthlyBudgetUSD": monthly_budget,
            "GitHubRepository": github_repo or "",
            "CreateGitHubOidcProvider": "true" if create_oidc else "false",
            "ResultsRetentionDays": retention_days,
        })
        for key, value in (outputs or {}).items():
            log(f"  {key}: {value}")
        return outputs

    def push_image(self, build_dir=None, tag="latest", git_sha=None):
        repository = self.base_outputs()["ImageRepositoryUri"]
        if build_dir:
            target = os.path.join(DOCKER_DIR, "build")
            for entry in os.listdir(target) if os.path.isdir(target) else []:
                if entry != ".gitkeep":
                    path = os.path.join(target, entry)
                    shutil.rmtree(path) if os.path.isdir(path) else os.remove(path)
            shutil.copytree(build_dir, target, dirs_exist_ok=True)
        if not any(name.endswith(".x86_64") for name in os.listdir(os.path.join(DOCKER_DIR, "build"))):
            sys.exit("cloud/docker/build has no Linux server build. Build it (Robot Vacuum > Build > Linux Headless Server) "
                     "and pass --build-dir <folder with RobotVacuumServer.x86_64>.")

        token = self.ecr.get_authorization_token()["authorizationData"][0]
        user, password = base64.b64decode(token["authorizationToken"]).decode().split(":", 1)
        registry = token["proxyEndpoint"]
        subprocess.run(["docker", "login", "--username", user, "--password-stdin", registry],
                       input=password, text=True, check=True)
        sha = git_sha or "local"
        tags = [f"{repository}:{tag}", f"{repository}:{sha}"]
        build = ["docker", "build", "--platform", "linux/amd64", "--build-arg", f"GIT_SHA={sha}"]
        for t in tags:
            build += ["-t", t]
        subprocess.run(build + [DOCKER_DIR], check=True)
        for t in tags:
            subprocess.run(["docker", "push", t], check=True)
        log(f"Pushed {', '.join(tags)}")

    def image_exists(self, tag):
        from botocore.exceptions import ClientError

        repo = f"{self.project}-headless"
        try:
            self.ecr.describe_images(repositoryName=repo, imageIds=[{"imageTag": tag}])
            return True
        except ClientError:
            return False

    # ------------------------------------------------------------------ compute (5.5 provisioning / teardown)

    def up(self, image_tag="latest", provisioning="SPOT", max_vcpus=32, instance_types=None, timeout_seconds=21600):
        base = self.base_outputs()
        if not self.image_exists(image_tag):
            sys.exit(f"No image tagged '{image_tag}' in ECR yet. Push one first (CI on main, or: python -m vacuumctl push-image).")
        params = {
            "ProjectName": self.project,
            "ImageUri": f"{base['ImageRepositoryUri']}:{image_tag}",
            "ResultsBucketName": base["ResultsBucketName"],
            "ProvisioningModel": provisioning,
            "MaxVCpus": max_vcpus,
            "JobTimeoutSeconds": timeout_seconds,
        }
        if instance_types:
            params["InstanceTypes"] = instance_types
        return self.deploy(self.compute_stack, "compute.yaml", params)

    def down(self):
        self.delete(self.compute_stack)

    def compute_outputs(self):
        outputs = self.outputs(self.compute_stack)
        if not outputs:
            sys.exit(f"The compute stack '{self.compute_stack}' isn't up. Run: python -m vacuumctl up  (or use `run`, which does it for you)")
        return outputs

    # ------------------------------------------------------------------ campaigns

    def _key(self, campaign_id, *parts):
        return "/".join(["campaigns", campaign_id, *parts])

    def _put_json(self, bucket, key, data):
        self.s3.put_object(Bucket=bucket, Key=key, Body=json.dumps(data, indent=2).encode("utf-8"),
                           ContentType="application/json")

    def _get_json(self, bucket, key):
        from botocore.exceptions import ClientError

        try:
            return json.loads(self.s3.get_object(Bucket=bucket, Key=key)["Body"].read())
        except ClientError:
            return None

    def submit(self, job):
        bucket = self.base_outputs()["ResultsBucketName"]
        compute = self.compute_outputs()
        cid = job["campaignId"]
        shards = job["execution"]["shardCount"]

        self._put_json(bucket, self._key(cid, "job.json"), job)
        manifest = campaigns.manifest(job)

        request = {
            "jobName": f"{self.project}-{cid}"[:128],
            "jobQueue": compute["JobQueueArn"],
            "jobDefinition": compute["JobDefinitionArn"],
            "parameters": {
                "jobUri": f"s3://{bucket}/{self._key(cid, 'job.json')}",
                "outputUri": f"s3://{bucket}/{self._key(cid, 'shards')}",
            },
            "containerOverrides": {"environment": [{"name": "VACUUM_SHARD_COUNT", "value": str(shards)}]},
            "tags": {"Project": self.project, "Campaign": cid},
            "propagateTags": True,
        }
        if shards > 1:
            request["arrayProperties"] = {"size": shards}
        response = self.batch.submit_job(**request)

        manifest["bucket"] = bucket
        manifest["region"] = self.region
        manifest["batchJobs"] = [{"jobId": response["jobId"], "kind": "array" if shards > 1 else "single",
                                  "shards": list(range(shards)), "submittedUtc": _now()}]
        self._put_json(bucket, self._key(cid, "manifest.json"), manifest)
        log(f"Submitted campaign {cid}: {manifest['totalRuns']} run(s) on {shards} node(s). AWS Batch job {response['jobId']}")
        return manifest

    def manifest(self, campaign_id):
        bucket = self.base_outputs()["ResultsBucketName"]
        manifest = self._get_json(bucket, self._key(campaign_id, "manifest.json"))
        if not manifest:
            sys.exit(f"No campaign '{campaign_id}' in s3://{bucket}/campaigns/. See: python -m vacuumctl list")
        return manifest

    def job_file(self, campaign_id):
        return self._get_json(self.base_outputs()["ResultsBucketName"], self._key(campaign_id, "job.json"))

    def status(self, campaign_id, quiet=False):
        """Progress across every node: AWS Batch states plus each node's heartbeat (4.5 live progress)."""
        manifest = self.manifest(campaign_id)
        bucket = manifest["bucket"]

        job_ids = [j["jobId"] for j in manifest["batchJobs"]]
        described = {}
        for start in range(0, len(job_ids), 100):
            for job in self.batch.describe_jobs(jobs=job_ids[start:start + 100])["jobs"]:
                described[job["jobId"]] = job

        states = []
        for entry in manifest["batchJobs"]:
            job = described.get(entry["jobId"], {})
            states.append({"jobId": entry["jobId"], "status": job.get("status", "UNKNOWN"),
                           "summary": (job.get("arrayProperties") or {}).get("statusSummary"),
                           "reason": job.get("statusReason")})

        shards, completed, failed = [], 0, 0
        for shard in manifest["shards"]:
            prefix = self._key(campaign_id, "shards", f"shard-{shard['shardIndex']:04d}")
            beat = self._get_json(bucket, prefix + "/heartbeat.json") or {}
            done = beat.get("completedRuns", 0)
            completed += done
            shards.append({"shardIndex": shard["shardIndex"], "planned": len(shard["runIndices"]),
                           "completed": done, "status": beat.get("status", "waiting"),
                           "updatedUtc": beat.get("updatedUtc"), "currentRunIndex": beat.get("currentRunIndex")})

        latest = states[-1]["status"] if states else "UNKNOWN"
        finished = all(s["status"] in TERMINAL for s in states)
        result = {"campaignId": campaign_id, "totalRuns": manifest["totalRuns"], "completedRuns": completed,
                  "batch": states, "shards": shards, "finished": finished, "latestStatus": latest}
        if not quiet:
            pct = 100.0 * completed / max(1, manifest["totalRuns"])
            log(f"[{_now(short=True)}] {campaign_id}: {completed}/{manifest['totalRuns']} runs ({pct:.0f}%) - "
                + ", ".join(f"{s['status']}" + (f" {s['summary']}" if s["summary"] else "") for s in states))
        return result

    def wait(self, campaign_id, poll_seconds=30, deadline_minutes=0):
        started = time.monotonic()
        while True:
            state = self.status(campaign_id)
            if state["finished"]:
                return state
            if deadline_minutes and time.monotonic() - started > deadline_minutes * 60:
                log(f"Deadline of {deadline_minutes} min reached; cancelling the remaining work.")
                self.cancel(campaign_id)
                return self.status(campaign_id)
            time.sleep(poll_seconds)

    def cancel(self, campaign_id, reason="Cancelled by vacuumctl"):
        for entry in self.manifest(campaign_id)["batchJobs"]:
            self.batch.terminate_job(jobId=entry["jobId"], reason=reason)
            log(f"Terminated {entry['jobId']}")

    def fetch(self, campaign_id, out_dir, everything=False):
        """Downloads every node's results.json, merges them, writes campaign_results.json (locally and to S3)."""
        manifest = self.manifest(campaign_id)
        bucket = manifest["bucket"]
        job = self.job_file(campaign_id)
        os.makedirs(out_dir, exist_ok=True)

        shard_results, shard_status = [], []
        for shard in manifest["shards"]:
            name = f"shard-{shard['shardIndex']:04d}"
            data = self._get_json(bucket, self._key(campaign_id, "shards", name, "results.json"))
            shard_results.append(data)
            shard_status.append({"shardIndex": shard["shardIndex"], "planned": len(shard["runIndices"]),
                                 "returned": len((data or {}).get("runs") or []),
                                 "status": (data or {}).get("status", "no results")})
            if data:
                campaigns.save(data, os.path.join(out_dir, "shards", name, "results.json"))

        if everything:
            self.download_prefix(bucket, self._key(campaign_id, ""), out_dir)

        results = stats.aggregate(job, shard_results, shard_status)
        campaigns.save(job, os.path.join(out_dir, "job.json"))
        campaigns.save(manifest, os.path.join(out_dir, "manifest.json"))
        path = os.path.join(out_dir, "campaign_results.json")
        campaigns.save(results, path)
        self._put_json(bucket, self._key(campaign_id, "campaign_results.json"), results)
        log(stats.format_table(results))
        log(f"Results: {path}")
        return results

    def download_prefix(self, bucket, prefix, out_dir):
        paginator = self.s3.get_paginator("list_objects_v2")
        count = 0
        for page in paginator.paginate(Bucket=bucket, Prefix=prefix):
            for item in page.get("Contents", []):
                relative = item["Key"][len(prefix):]
                target = os.path.join(out_dir, *relative.split("/"))
                os.makedirs(os.path.dirname(target), exist_ok=True)
                self.s3.download_file(bucket, item["Key"], target)
                count += 1
        log(f"Downloaded {count} file(s) to {out_dir}")

    def retry(self, campaign_id):
        """Re-runs only the nodes whose runs aren't all back. Each picks up after its last finished run."""
        manifest = self.manifest(campaign_id)
        bucket = manifest["bucket"]
        compute = self.compute_outputs()
        shards = manifest["shardCount"]
        submitted = []
        for shard in manifest["shards"]:
            name = f"shard-{shard['shardIndex']:04d}"
            data = self._get_json(bucket, self._key(campaign_id, "shards", name, "results.json")) or {}
            returned = {r["runIndex"] for r in data.get("runs") or []}
            if set(shard["runIndices"]) <= returned:
                continue
            response = self.batch.submit_job(
                jobName=f"{self.project}-{campaign_id}-retry-{shard['shardIndex']}"[:128],
                jobQueue=compute["JobQueueArn"],
                jobDefinition=compute["JobDefinitionArn"],
                containerOverrides={"command": [
                    "--job-uri", f"s3://{bucket}/{self._key(campaign_id, 'job.json')}",
                    "--output-uri", f"s3://{bucket}/{self._key(campaign_id, 'shards')}",
                    "--shard-index", str(shard["shardIndex"]), "--shard-count", str(shards)]},
                tags={"Project": self.project, "Campaign": campaign_id}, propagateTags=True)
            manifest["batchJobs"].append({"jobId": response["jobId"], "kind": "retry",
                                          "shards": [shard["shardIndex"]], "submittedUtc": _now()})
            submitted.append(shard["shardIndex"])
        self._put_json(bucket, self._key(campaign_id, "manifest.json"), manifest)
        log(f"Resubmitted shard(s): {submitted}" if submitted else "Every shard has returned all its runs; nothing to retry.")
        return submitted

    def list_campaigns(self):
        bucket = self.base_outputs()["ResultsBucketName"]
        response = self.s3.list_objects_v2(Bucket=bucket, Prefix="campaigns/", Delimiter="/")
        for prefix in response.get("CommonPrefixes", []):
            log(prefix["Prefix"].split("/")[1])

    def tail_logs(self, campaign_id, shard_index=0, lines=100):
        """The last lines of a node's log in CloudWatch (3.12 observability)."""
        manifest = self.manifest(campaign_id)
        job_id = None
        for entry in reversed(manifest["batchJobs"]):
            if shard_index in entry["shards"]:
                job_id = entry["jobId"] + (f":{shard_index}" if entry["kind"] == "array" else "")
                break
        if not job_id:
            sys.exit(f"No AWS Batch job for shard {shard_index}.")
        job = self.batch.describe_jobs(jobs=[job_id])["jobs"]
        stream = job[0].get("container", {}).get("logStreamName") if job else None
        if not stream:
            sys.exit(f"Shard {shard_index} ({job_id}) has no log yet (status {job[0]['status'] if job else 'unknown'}).")
        group = self.compute_outputs()["LogGroupName"]
        events = self.logs.get_log_events(logGroupName=group, logStreamName=stream, limit=lines, startFromHead=False)["events"]
        for event in events:
            log(event["message"])

    # ------------------------------------------------------------------ full teardown

    def destroy(self):
        self.down()
        base = self.outputs(self.base_stack)
        if base and base.get("ResultsBucketName"):
            bucket = base["ResultsBucketName"]
            log(f"Emptying s3://{bucket} ...")
            paginator = self.s3.get_paginator("list_object_versions")
            for page in paginator.paginate(Bucket=bucket):
                objects = [{"Key": v["Key"], "VersionId": v["VersionId"]}
                           for v in page.get("Versions", []) + page.get("DeleteMarkers", [])]
                for start in range(0, len(objects), 1000):
                    self.s3.delete_objects(Bucket=bucket, Delete={"Objects": objects[start:start + 1000]})
        self.delete(self.base_stack)


def _now(short=False):
    now = datetime.datetime.now(datetime.timezone.utc)
    return now.strftime("%H:%M:%S") if short else now.isoformat()
