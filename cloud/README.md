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
| Control CLI | `cloud/vacuumctl/` | `validate`, `local`, `compare`, `connection`, `test-connection`, `sso-login`, `admin-policy`, `backends`, `bootstrap`, `push-image`, `up`, `submit`, `status`, `logs`, `retry`, `fetch`, `run`, `down`, `destroy`. |
| Network Settings page | `Assets/Scripts/Level/SettingsView.cs`, `Assets/Scripts/LevelEditor/Cloud/` | Start screen › **Network Settings**: the cloud connection (account, sign-in, region, prefix, network, compute limits), Test connection, Set up / Remove cloud resources, Save admin policy. Runs `vacuumctl`; holds no credentials. |
| Admin policy | `cloud/infra/vacuum-sim-user-policy.template.json` | Least-privilege IAM policy for the user or role that runs campaigns (`vacuumctl admin-policy` fills it in). |
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

## Cloud connection: bring your own cloud (5.5)

Campaigns run in **your organisation's own AWS account**. The templates set up the resources there, your data
stays in your account, and AWS bills you directly; there is no service of ours in between. Everything is set on
the start screen's **Network Settings** page, or with `python -m vacuumctl connection set ...`; both edit the same file:

| | |
|---|---|
| File | `%APPDATA%\RobotVacuumSim\cloud-connection.json` (Windows), `~/.config/RobotVacuumSim/...` elsewhere. `python -m vacuumctl connection path` prints it. Override with `--connection` or `VACUUM_CLOUD_CONNECTION`. |
| Provider | AWS. Microsoft Azure, Google Cloud and SSH clusters are planned (`vacuumctl backends`); see "Backends" below. |
| Sign-in | **AWS profile** (`aws configure --profile <name>`), **AWS SSO** (`aws configure sso`; the page's *Sign in (SSO)* button runs `aws sso login`), or a **credentials file** (only its path is saved). |
| Region, resource prefix | Every AWS resource name starts with the prefix (e.g. `acme-vacuum`), so teams can share one account. |
| Network | See below. |
| Compute | Spot or on-demand, maximum vCPUs at once, hard time limit per node, optional instance types. |

**No secrets are stored** (C.12): the file holds a profile *name* or a file *path*, never keys or passwords. Keys stay
where AWS keeps them: `~/.aws`, the SSO cache, or a `credential_process` (which is how a Windows Credential Manager
helper plugs in). The simulator itself never talks to AWS: the Network Settings page runs `vacuumctl`, and nothing touches
the network until a button there is pressed (1.1.1).

Precedence for each setting: command-line flag > connection file > environment (`AWS_PROFILE`, `AWS_REGION`) > default.
With no connection file, everything works as before (boto3's default credentials, prefix `robot-vacuum-sim`).

**Test connection** (`python -m vacuumctl test-connection`) changes nothing in the account. It checks the settings,
the sign-in (who you are, which account), the region, every permission in the admin policy (via the IAM policy
simulator), whether the cloud resources and an image exist, and the network: VPC count against the default limit
for new VPCs; for an existing VPC, that the subnets are in it and can reach S3, ECR, ECS and CloudWatch Logs.

**No admin rights?** *Save admin policy…* (or `python -m vacuumctl admin-policy --out policy.json --account <id>`)
writes a least-privilege IAM policy for your IT admin to attach to a user or role (e.g. `vacuum-sim`). Create it as
a **customer managed policy** (`aws iam create-policy`, then `attach-user-policy` or `attach-role-policy`): it is
about 5,000 characters, over the 2,048-character limit for a user's inline policies. It allows only
what setup and campaigns need, on resources named with your prefix, in your region. EC2 network actions can't be
narrowed to resource names, so they are limited to the region; an admin who wants more can add a tag condition on
`aws:ResourceTag/Project` (every stack tags its resources `Project=<prefix>`). It hasn't been tried in a locked-down
account yet: run Test connection after attaching it.

**Remove all cloud resources** asks you to type the prefix (C.9); the command-line equivalent is
`python -m vacuumctl destroy` (prompts) or `destroy --confirm <prefix>`.

### Network options

| Setting | What the nodes get | Use when |
|---|---|---|
| **New VPC, internet access** (default) | A VPC made for the campaign and deleted after it: public subnets, internet gateway, no inbound rules. | Simplest and cheapest. |
| **New VPC, private** (`--private-network`) | Private subnets, **no internet gateway**; S3 through a gateway endpoint, ECR, ECS and CloudWatch Logs through interface endpoints. | Custom algorithm code must have no route to the internet (4.8). AWS bills the 6 interface endpoints × 2 zones per hour while the campaign runs. |
| **Existing VPC** (`--network existing --vpc-id ... --subnet-ids ...`) | Your VPC and subnets; only the nodes' security group is created. Your routing decides what they reach. | Your organisation requires its own network. The subnets need a NAT gateway or VPC endpoints for S3, ECR, ECS and CloudWatch Logs. |

VPC address range: a private /16 to /20 (default `10.42.0.0/16`); two /24 subnets are cut from it. Pick one that
doesn't overlap a network you peer or VPN with. **HTTPS proxy:** for corporate networks, this computer's proxy for
`vacuumctl`'s calls to AWS (no user name or password in the file).

### Backends

`vacuumctl/backends.py` is the interface every place a campaign can run implements (test the connection, set up,
remove, run). Shipped: **local** (this computer) and **aws**. Azure, Google Cloud and SSH clusters are listed as
planned (C.4). Adding one is a new backend class plus its own settings section on the Network Settings page; campaign
files, results, statistics and reports stay the same.

## One-time setup (starting from an empty AWS account)

1. **Secure the account.** Sign in as root, turn on MFA, then in **IAM Identity Center** create a user with the
   `AdministratorAccess` permission set for setup (CloudFormation needs to create IAM roles). Don't use root keys.
2. **Install on your PC:** Python 3.10+, the [AWS CLI v2](https://docs.aws.amazon.com/cli/latest/userguide/getting-started-install.html)
   (only for signing in), and Docker Desktop (only if you push images from your PC instead of CI).
3. **Sign in:** `aws configure sso --profile vacuum` and pick a region (e.g. `us-east-2`). Credentials stay in
   your AWS config — nothing is stored in this repo or the build (C.12). Then set the connection, either on the
   simulator's **Network Settings** page or with
   `python -m vacuumctl connection set --sign-in sso --profile vacuum --region us-east-2`, and press **Test connection**
   (`python -m vacuumctl test-connection`).
4. **Python deps:** `cd cloud && pip install -r vacuumctl/requirements.txt`
5. **Create the long-lived resources:**
   ```
   python -m vacuumctl bootstrap ^
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
python -m vacuumctl run examples/smoke.json --nodes 2          # quick cloud check
python -m vacuumctl run examples/campaign.example.json --nodes 8
python -m vacuumctl run examples/slam-custom.example.json --nodes 4   # SLAM + a custom .cs algorithm
```

Algorithms are named by catalog id (`RandomBounce`, `BoustrophedonSweep`, `BoustrophedonCrawl`, `Legacy.Spiral`, ...,
or a custom algorithm's `Namespace.Class`); the old names `Spiral`, `WallFollow`, `Lawnmower` still work. Each
configuration can set `sensing` (SLAM, noise, lidar range/angle/rays, floor sensor). See `docs/job-format.md`.

`run` = `up` (provision) → `submit` → `wait` (live progress) → `fetch` (download + statistics) → `down`
(teardown, even on failure or Ctrl-C). Results land in `cloud-results/<campaignId>/campaign_results.json`;
`--all-files` also downloads every run folder.

**HTML report.** `run`, `fetch` and `local` also write `report.html` next to `campaign_results.json` and open it in
your browser: summary tiles, a coverage chart per floor plan × configuration (mean, 95% CI and every run),
the statistics table, each algorithm's median-run heatmap, the configurations used, problems, every run
(sortable) and the compute nodes. It is one self-contained file (light and dark mode) you can share or print.
Add `--no-open` to skip opening it; rebuild one any time with `python -m vacuumctl report <results folder>`.

Step by step instead:

```
python -m vacuumctl up [--on-demand] [--max-vcpus 64]
python -m vacuumctl submit my_campaign.json --nodes 16
python -m vacuumctl status <campaignId>      # runs done / total, per-node state
python -m vacuumctl logs <campaignId> --shard 3
python -m vacuumctl retry <campaignId>       # only nodes missing runs; they resume
python -m vacuumctl fetch <campaignId> --all-files
python -m vacuumctl down
python -m vacuumctl destroy                  # removes everything, including stored results (type the prefix to confirm)
```

**Cost control:** nodes exist only while jobs run; the compute stack is deleted after `run`; Spot is the
default; every node attempt has a hard timeout (`--node-timeout`, default 6 h); `--deadline-minutes` cancels a
campaign that overruns; results expire from S3 after 90 days; the budget emails you at 50/80/100%.

## Results in the simulator (Analysis) and the 2.8 statistics

Cloud and local headless campaigns show in the simulator's **Analysis** page through the same results interface as
batches run in the app (2.14, 2.18, 4.6, C.13):

1. Get the results onto this computer: `vacuumctl run` and `vacuumctl fetch <id>` write `cloud-results/<id>/`;
   `vacuumctl local` writes `local-results/<id>/`. `fetch` now also downloads each run's small files (summary,
   replay, CSVs; about 80 KB a run) so heat maps and replays work; `--all-files` adds `external_model.json` and the PNGs.
   Fetching again only downloads what's new.
2. In the simulator: **Analysis › Import results**, then pick the campaign. It lists the campaigns in the cloud
   folder's `cloud-results` and `local-results` (in the Unity Editor you can also browse to any folder).

Each campaign becomes **one batch per floor plan × configuration**, labelled *Cloud* or *Headless*, so statistics never
pool different conditions. Run ids come from the campaign and run index, so importing again replaces the earlier
import. The run files are copied into the app's own data folder (without `external_model.json`), so deleting a batch
in Analysis never touches the downloaded campaign. Import reads local files only: the simulator never contacts AWS
(1.1.1). A run whose files weren't downloaded is still imported from its numbers, so the statistics stay complete.
The run history keeps the newest 2000 runs; a larger import drops the oldest (the import says so).

**Statistics (2.8).** Every batch, in Analysis and in `report.html`, has a table with, per algorithm, the **mean,
sample variance (n − 1), standard deviation and 95% confidence interval** (Student's t) of **coverage, time and
battery used**, over the runs that succeeded; failed runs are listed and counted but left out (the app and the cloud
use the same rule). **Export CSV** on a batch in Analysis, and `campaign_runs.csv` beside every campaign, list each
run and the statistics in the same columns, for checking by hand.

## Demonstrating the acceptance tests

| Test | How |
|---|---|
| AT-2.17-1 | In the GUI, run a floor plan with a fixed seed (Random Seed off). Export that floor plan and put it, with the same settings and seed, in a campaign; run it with `vacuumctl local`. Set `simulation.maxSimulatedSeconds` to `86400` to match the GUI's single-run cap. Then `python -m vacuumctl compare <GUI run folder> <headless run folder>` — compares `result.json`, `summary.json` and the SHA-256 of `external_model.json`. GUI run folders are in `%USERPROFILE%\AppData\LocalLow\DefaultCompany\Robot vacuum simulator\Runs`. |
| AT-2.17-2 | 100 runs: `vacuumctl local` (prints wall-clock) vs the GUI's batch with drawing on. |
| AT-2.8-1 | A batch of 20+ seeds for one algorithm: in the app (Start simulation › batch, 20 seeds), or a campaign with `"sweep": {"algorithms": ["BoustrophedonSweep"], "seedStart": 1, "seedCount": 20}` run with `vacuumctl run`/`local` and imported. Analysis shows mean, variance, SD and 95% CI for coverage, time and battery. |
| AT-2.8-2 | Analysis › the batch › **Export CSV** (or `campaign_runs.csv`). In Excel, for each measure: `=AVERAGE(...)`, `=VAR.S(...)`, and the CI half-width `=T.INV.2T(0.05, n-1)*SQRT(VAR.S(...)/n)`; they match the table. |
| AT-2.18 | Import a cloud campaign: it is listed in Analysis as a batch, with the averaged heat map, mean coverage curve and statistics, the same view as a single run's results but marked *Cloud campaign*. |
| AT-3.9 | The Windows PC (vacuumctl) and the AWS nodes are separate machines on different networks talking only through the job/results interface. `status` shows them communicating; `campaign_results.json` shows the outcome. |
| AT-3.10 | GitHub › Actions › *Cloud headless node*: a run per commit with test stages and the `linux-server-<sha>` artifact (and image in ECR on `main`). |
| AT-5.5 | `vacuumctl run examples/campaign.example.json --nodes 8`. While it runs, EC2 › Instances shows nodes tagged `robot-vacuum-sim-node`; afterwards they are gone and CloudFormation shows no `robot-vacuum-sim-compute` stack. |

## Trust boundaries (4.8, C.12)

```
 Your computer                       AWS (your account)                     Inside one container (uid 1000, not root)
 ─────────────                       ──────────────────                     ──────────────────────────────────────────
 vacuumctl ── AWS profile ──▶  CloudFormation, Batch, S3 API               entrypoint.py  (holds the job role's credentials;
  (credentials stay in           │                                           │              non-dumpable, so /proc can't read them)
   ~/.aws, never in repo)        ▼                                           │ starts with AWS_*/ECS_* removed
                               Batch job ──▶ EC2 node (no inbound rules;     ▼
                                              private network: no internet)
                                              IMDS: hop limit 1  ✗──────  Unity build + custom algorithm code
                                              (containers can't reach it)   (no AWS credentials; reads job.json,
                                                                             writes its own shard folder)
                               Job role: s3 Get/Put on campaigns/<this campaign>/* only (vacuumctl run)
```

What is enforced:
- **No inbound access** to nodes (security group has no ingress rules).
- **The simulator process holds no AWS credentials.** Custom algorithms (carried as `.cs`) can use `System.IO`,
  `System.Net` and `System.Diagnostics.Process`: the plugin compiler limits what they can *reference* (no
  UnityEngine, no simulator internals) but is not a sandbox. So the entrypoint alone talks to S3, starts Unity
  with every `AWS_*`/`ECS_*` variable removed, and marks itself non-dumpable so Unity can't read its environment
  or memory through `/proc`. The instance metadata service is blocked for containers (IMDSv2, hop limit 1).
- **Least privilege:** the job role can only read/write `campaigns/<CampaignScope>/*`; `vacuumctl run` sets the
  scope to its own campaign (`vacuumctl up` on its own leaves it at `*`, any campaign).
- **Validated before running** (C# `HeadlessJob.Validate` and `vacuumctl/campaign.py`); runs as **non-root**.

- **Network:** with the **private network** option the nodes have no internet gateway, so custom algorithm code
  has no route to the internet; the VPC endpoints carry only S3, ECR, ECS and CloudWatch Logs traffic.

Known limits (disclose, C.4/C.5): with the default network (internet access), custom code can make **outbound
internet connections**; choose the private network to prevent it. Even then it could reach AWS service endpoints
(S3, ECR, ECS, Logs) but holds no credentials to use them. It can read the job file and write anything in its own
shard's output, and could kill the entrypoint (same user), which fails that node attempt. It cannot reach AWS
credentials, other campaigns' data or the instance role. With an existing VPC, your organisation's routing decides.

**AT-4.8 evidence:** `tests/test_security_and_logging.py` (credential variables stripped, entrypoint
non-dumpable, IMDS hop limit, per-campaign S3 scope). Cloud check: run a campaign with a custom algorithm whose
`Initialize` logs `Environment.GetEnvironmentVariable("AWS_CONTAINER_CREDENTIALS_RELATIVE_URI")` (empty),
`File.Exists("/proc/1/environ")` with a read attempt (`UnauthorizedAccessException`), and an HTTP GET to
`http://169.254.169.254/latest/meta-data/` (times out); the node log in CloudWatch shows all three.

## Determinism (SDD §10) — to check once

Run `examples/smoke.json` with a fixed seed three ways — GUI (Windows, rendered), `vacuumctl local` (Windows,
headless), and in the cloud (Linux, headless) — and `compare` the run folders. If Windows and Linux differ,
the options are: treat cloud results as their own population (compare only within one platform), or run the
cloud nodes on Windows (AWS Batch on Fargate with a Windows container, or Windows EC2 under ECS).

## Not done here / still open

- **Starting campaigns from the GUI.** The Network Settings page sets up and checks the connection, and Analysis
  imports the results; campaigns still start from `vacuumctl` (or the GUI's in-app batch). A "Run on: This computer /
  Cloud" choice in the batch dialog could call `vacuumctl run` the way the Network Settings page calls
  `test-connection`, then import the results automatically.
- **4.6 pairwise comparison.** Confidence intervals are shown for every measure. A paired comparison between
  algorithms on the same seeds (mean difference with CI, paired t-test, effect size) would strengthen AT-4.6-1.
- **Not yet tried in AWS:** the private-network and existing-VPC options, and the admin policy in a non-admin
  account. Test connection checks what it can without changing anything; do one `run examples/smoke.json` with each
  network option before relying on it.
- **Custom algorithms (3.8)** are carried to nodes as C# source (`algorithmFiles` in the campaign, compiled on each
  node against the algorithm API only; see `docs/job-format.md` › Custom algorithms). Prebuilt plugin DLLs are not
  carried: source can be checked by the same restricted compiler the desktop uses, a DLL can't (4.8).
- **GameCI and a project path with spaces** (`Robot vacuum simulator`) is untested; if the build step
  misbehaves, rename the folder or pass the path quoted. If the CI editor image lacks the Dedicated Server
  module, set `HEADLESS_SUBTARGET: Player` in the workflow (a normal Linux player run with `-nographics`).
- Unity creates `.meta` files for the new scripts the first time the project is opened — commit them.
- **CI runs only `HeadlessJobTests`, `CloudConnectionTests`, `CampaignImportTests` and `BatchStatsTests`** (`-testFilter` in `cloud-headless.yml`). Five other EditMode tests (level editor,
  LevelData, SimConfig) fail on `main` too; the team needs to fix or explain them before the presentation (C.14).
- **Per-cell telemetry in batches (2.19):** batch runs keep the final per-cell state (`external_model.json`) but not
  the per-cell time series. If the team reads 2.19 as needing it, add a job flag that tells `RunRecorder` to write
  `cell_events.csv` for batch runs too.
- **Windows Credential Manager** isn't read directly; use an AWS profile with a `credential_process` helper.
