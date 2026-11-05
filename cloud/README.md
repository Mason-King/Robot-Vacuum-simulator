# Cloud batch runs — headless simulator on AWS

Covers requirements **2.17** (headless mode), **3.9** (independently executable components across a network
boundary), **3.10** (CI producing a deployable artifact — cloud parts) and **5.5** (multi-node campaigns with
automated provisioning and teardown). SDD sections 2.1, 2.2, 8 and 12.

```
 Your PC (Windows)                                AWS (one region)
 ─────────────────                                ─────────────────────────────────────────────────────────
 campaign.json ──► vacuumctl ──── job.json ────►  S3 results bucket ◄──────────────┐
  (floor plans,     validate,     manifest.json   campaigns/<id>/                   │ results.json, heartbeat,
   configs, sweep)  submit ───── AWS Batch API ─► Batch job queue                   │ run folders (every ~15 s)
                    status  ◄─── heartbeats ────  └► array job, N children ──► EC2 nodes (Spot, scale 0→N→0)
                    fetch   ◄─── results ───────       one per shard              Docker: Unity Linux server
                    │                                                             build -batchmode -nographics
                    ▼                                                             logs ─► CloudWatch Logs
 campaign_results.json (per-run + mean/variance/95% CI)
```

| Piece | Where | What it does |
|---|---|---|
| Headless job mode | `Robot vacuum simulator/Assets/Scripts/Headless/` | `-batchmode -nographics -configPath job.json` → runs the job's simulations through the same `SimulationSession` the GUI uses, writes `results.json` + run folders, exits with a status code. |
| Linux server build | `Assets/Editor/Build/HeadlessBuild.cs` | Menu **Robot Vacuum › Build › Linux Headless Server**, or `-executeMethod RobotVacuum.EditorTools.HeadlessBuild.BuildLinuxServer`. |
| Container | `cloud/docker/` | Ubuntu + the Linux build + `entrypoint.py` (fetch job, run shard, stream results to S3, resume after retry). |
| Infrastructure | `cloud/infra/base.yaml`, `compute.yaml` | CloudFormation. *base*: ECR, results bucket, budget alert, GitHub OIDC role. *compute*: VPC, AWS Batch (EC2/Spot), job queue/definition, IAM, logs — created per campaign, deleted after. |
| Control CLI | `cloud/vacuumctl/` | `validate`, `local`, `compare`, `bootstrap`, `push-image`, `up`, `submit`, `status`, `logs`, `retry`, `fetch`, `run`, `down`, `destroy`. |
| CI | `.github/workflows/cloud-headless.yml` | Tests → Linux server build artifact → Docker image + 4-run smoke test in the container → push to ECR on `main`. |
| Job format | `cloud/docs/job-format.md` | Every configuration, vacuum, battery, floor-covering and floor-plan field, with defaults and ranges. |

## Why these choices

- **Linux Dedicated Server build in Docker (not Windows).** AWS Batch on EC2 runs Linux containers only;
  Windows containers in AWS Batch are Fargate-only (the `RuntimePlatform` OS family setting applies to Fargate
  jobs), with multi-GB Server Core images and slow cold starts. A Unity Linux server build is ~100 MB, needs
  no GPU, and starts in seconds. The Windows `.exe` keeps the same headless mode, so headless-vs-rendered
  checks (AT-2.17-1) can run on one Windows PC. **Open item:** SDD §10 asks for same-seed results across
  machines; Windows-vs-Linux physics results must be compared once (see "Determinism" below).
- **Docker** rather than alternatives (Podman, Apptainer, AMIs baked with Packer): it is what AWS Batch, ECR and
  GitHub Actions run natively, and what GameCI uses, so nothing extra to learn or install in the cloud.
- **AWS Batch on EC2 (Spot by default)** rather than hand-managed EC2: Batch itself provisions instances while
  jobs are queued and terminates them when the queue drains (MinvCpus 0), retries Spot interruptions, and
  enforces a hard per-node timeout — the automated provisioning/teardown 5.5 asks for. The compute stack is
  additionally deleted after each campaign, so nothing is left running or configured.
- **CloudFormation**: native to AWS, needs only boto3 (no Terraform install or state files).
- **Results through S3, not a socket**: nodes need no inbound ports (security group has no ingress), a dead
  node still returns what it finished, and retries resume from `results.json`.

## One-time setup (starting from an empty AWS account)

1. **Secure the account.** Sign in as root, turn on MFA, then in **IAM Identity Center** create a user with the
   `AdministratorAccess` permission set for setup (CloudFormation needs to create IAM roles). Don't use root keys.
2. **Install on your PC:** Python 3.10+, the [AWS CLI v2](https://docs.aws.amazon.com/cli/latest/userguide/getting-started-install.html)
   (only for signing in), and Docker Desktop (only if you push images from your PC instead of CI).
3. **Sign in:** `aws configure sso --profile vacuum` and pick a region (e.g. `us-east-2`). Credentials stay in
   your AWS config — nothing is stored in this repo or the build (C.12).
4. **Python deps:** `cd cloud && pip install -r vacuumctl/requirements.txt`
5. **Create the long-lived resources:**
   ```
   python -m vacuumctl --profile vacuum --region us-east-2 bootstrap ^
       --budget-email you@example.com --monthly-budget 20 ^
       --github-repo Mason-King/Robot-Vacuum-simulator
   ```
   Confirm the budget email AWS sends. Note the printed `CiImagePushRoleArn`.
6. **Unity:** add the *Linux Dedicated Server Build Support* module to the 6000.3.x editor in Unity Hub.
7. **CI (GitHub › Settings › Secrets and variables › Actions):** secrets `UNITY_LICENSE`, `UNITY_EMAIL`,
   `UNITY_PASSWORD` ([GameCI activation](https://game.ci/docs/github/activation)); variables
   `UNITY_CI_ENABLED=true`, `AWS_REGION=us-east-2`, `AWS_ROLE_ARN=<CiImagePushRoleArn>`.
   Each push to `main` then publishes `robot-vacuum-sim-headless:latest`.
   *Without CI:* build via the menu, then
   `python -m vacuumctl push-image --build-dir "Robot vacuum simulator/Builds/LinuxServer"`.

## Running campaigns

```
cd cloud
python -m vacuumctl validate examples/smoke.json                      # no AWS needed
python -m vacuumctl local examples/smoke.json --exe "C:\path\Robot vacuum simulator.exe" --processes 4
python -m vacuumctl --profile vacuum run examples/smoke.json --nodes 2          # quick cloud check
python -m vacuumctl --profile vacuum run examples/campaign.example.json --nodes 8
```

`run` = `up` (provision) → `submit` → `wait` (live progress) → `fetch` (download + statistics) → `down`
(teardown, even on failure or Ctrl-C). Results land in `cloud-results/<campaignId>/campaign_results.json`;
`--all-files` also downloads every run folder. Step by step instead:

```
python -m vacuumctl up [--on-demand] [--max-vcpus 64]
python -m vacuumctl submit my_campaign.json --nodes 16
python -m vacuumctl status <campaignId>      # runs done / total, per-node state
python -m vacuumctl logs <campaignId> --shard 3
python -m vacuumctl retry <campaignId>       # only nodes missing runs; they resume
python -m vacuumctl fetch <campaignId> --all-files
python -m vacuumctl down
python -m vacuumctl destroy                  # removes everything, including stored results (asks first)
```

**Cost control:** nodes exist only while jobs run; the compute stack is deleted after `run`; Spot is the
default; every node attempt has a hard timeout (`--node-timeout`, default 6 h); `--deadline-minutes` cancels a
campaign that overruns; results expire from S3 after 90 days; the budget emails you at 50/80/100%.

## Demonstrating the acceptance tests

| Test | How |
|---|---|
| AT-2.17-1 | In the GUI, run a floor plan with a fixed seed (Random Seed off). Export that floor plan and put it, with the same settings and seed, in a campaign; run it with `vacuumctl local`. Set `simulation.maxSimulatedSeconds` to `86400` to match the GUI's single-run cap. Then `python -m vacuumctl compare <GUI run folder> <headless run folder>` — compares `result.json`, `summary.json` and the SHA-256 of `external_model.json`. GUI run folders are in `%USERPROFILE%\AppData\LocalLow\DefaultCompany\Robot vacuum simulator\Runs`. |
| AT-2.17-2 | 100 runs: `vacuumctl local` (prints wall-clock) vs the GUI's batch with drawing on. |
| AT-3.9 | The Windows PC (vacuumctl) and the AWS nodes are separate machines on different networks talking only through the job/results interface. `status` shows them communicating; `campaign_results.json` shows the outcome. |
| AT-3.10 | GitHub › Actions › *Cloud headless node*: a run per commit with test stages and the `linux-server-<sha>` artifact (and image in ECR on `main`). |
| AT-5.5 | `vacuumctl run examples/campaign.example.json --nodes 8`. While it runs, EC2 › Instances shows nodes tagged `robot-vacuum-sim-node`; afterwards they are gone and CloudFormation shows no `robot-vacuum-sim-compute` stack. |

## Determinism (SDD §10) — to check once

Run `examples/smoke.json` with a fixed seed three ways — GUI (Windows, rendered), `vacuumctl local` (Windows,
headless), and in the cloud (Linux, headless) — and `compare` the run folders. If Windows and Linux differ,
the options are: treat cloud results as their own population (compare only within one platform), or run the
cloud nodes on Windows (AWS Batch on Fargate with a Windows container, or Windows EC2 under ECS).

## Not done here / still open

- **Setup Screen integration** (agreed: CLI only for now). The GUI can call `vacuumctl` with `Process.Start`
  and read `campaign_results.json`; showing that file in the Data Screen covers 2.18.
- **Custom Logic plugin DLLs (3.8)** are not carried to nodes yet (SDD §14 open item). Planned approach: a
  `pluginDll` S3 reference in the job, validated before load (4.8).
- **GameCI and a project path with spaces** (`Robot vacuum simulator`) is untested; if the build step
  misbehaves, rename the folder or pass the path quoted. If the CI editor image lacks the Dedicated Server
  module, set `HEADLESS_SUBTARGET: Player` in the workflow (a normal Linux player run with `-nographics`).
- Unity creates `.meta` files for the new scripts the first time the project is opened — commit them.
