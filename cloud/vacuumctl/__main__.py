"""vacuumctl command line.  Run from the cloud/ folder:  python -m vacuumctl <command> --help

Typical use
  python -m vacuumctl validate examples/campaign.example.json
  python -m vacuumctl local    examples/campaign.example.json --exe "C:/.../Robot vacuum simulator.exe" --processes 4
  python -m vacuumctl run      examples/campaign.example.json --nodes 8      # provision, run, fetch, tear down
"""

import argparse
import os
import sys

from . import __version__
from . import campaign as campaigns


def main(argv=None):
    parser = argparse.ArgumentParser(prog="vacuumctl", description="Run Robot Vacuum Simulator campaigns headless on AWS Batch.")
    parser.add_argument("--version", action="version", version=__version__)
    parser.add_argument("--profile", default=os.environ.get("AWS_PROFILE"), help="AWS profile name (credentials stay in your AWS config)")
    parser.add_argument("--region", default=os.environ.get("AWS_REGION") or os.environ.get("AWS_DEFAULT_REGION"))
    parser.add_argument("--project", default="robot-vacuum-sim", help="prefix of every AWS resource name")
    sub = parser.add_subparsers(dest="command", required=True)

    p = sub.add_parser("validate", help="check a campaign file and show what it expands to (no AWS needed)")
    p.add_argument("campaign")
    p.add_argument("--nodes", type=int)
    p.add_argument("--write", help="also save the expanded job file here")

    p = sub.add_parser("local", help="run a campaign headless on this computer")
    p.add_argument("campaign")
    p.add_argument("--exe", required=True, help="the simulator executable (Windows .exe or Linux build)")
    p.add_argument("--out", default="local-results")
    p.add_argument("--processes", type=int, default=1, help="parallel simulator processes")

    p = sub.add_parser("compare", help="check two run folders hold identical results (AT-2.17-1)")
    p.add_argument("run_folder_a")
    p.add_argument("run_folder_b")

    p = sub.add_parser("bootstrap", help="one-time: create the registry, results bucket, budget alert and CI role")
    p.add_argument("--budget-email", default="")
    p.add_argument("--monthly-budget", type=float, default=20)
    p.add_argument("--github-repo", default="", help="owner/repo allowed to push images from GitHub Actions")
    p.add_argument("--no-oidc-provider", action="store_true", help="the account already has GitHub's OIDC provider")
    p.add_argument("--retention-days", type=int, default=90)

    p = sub.add_parser("push-image", help="build the Docker image from a Linux server build and push it to ECR")
    p.add_argument("--build-dir", help="folder containing RobotVacuumServer.x86_64 and its _Data folder")
    p.add_argument("--tag", default="latest")
    p.add_argument("--git-sha")

    def compute_args(p):
        p.add_argument("--image-tag", default="latest")
        p.add_argument("--on-demand", action="store_true", help="on-demand EC2 instead of Spot")
        p.add_argument("--max-vcpus", type=int, default=32)
        p.add_argument("--instance-types", help="comma-separated, e.g. c6i.large,c7i.large")
        p.add_argument("--node-timeout", type=int, default=21600, help="hard limit (s) per node attempt")

    p = sub.add_parser("up", help="provision the AWS Batch compute stack")
    compute_args(p)
    sub.add_parser("down", help="tear down the compute stack")

    p = sub.add_parser("submit", help="upload a campaign and start it (compute stack must be up)")
    p.add_argument("campaign")
    p.add_argument("--nodes", type=int, help="split across this many nodes (default: execution.shardCount)")
    p.add_argument("--wait", action="store_true")

    p = sub.add_parser("run", help="end to end: provision, submit, watch, fetch results, tear down (5.5)")
    p.add_argument("campaign")
    p.add_argument("--nodes", type=int)
    p.add_argument("--out", default="cloud-results")
    p.add_argument("--all-files", action="store_true", help="also download every run folder")
    p.add_argument("--keep-infra", action="store_true", help="leave the compute stack up afterwards")
    p.add_argument("--deadline-minutes", type=float, default=0, help="cancel whatever is still running after this long")
    p.add_argument("--poll", type=int, default=30)
    compute_args(p)

    for name, text in (("status", "progress of a campaign"), ("cancel", "stop a campaign"),
                       ("retry", "re-run only the nodes that didn't return all their runs")):
        p = sub.add_parser(name, help=text)
        p.add_argument("campaign_id")

    p = sub.add_parser("wait", help="watch a campaign until it ends")
    p.add_argument("campaign_id")
    p.add_argument("--poll", type=int, default=30)

    p = sub.add_parser("fetch", help="download results and compute the batch statistics")
    p.add_argument("campaign_id")
    p.add_argument("--out", default="cloud-results")
    p.add_argument("--all-files", action="store_true")

    p = sub.add_parser("logs", help="tail a node's log from CloudWatch")
    p.add_argument("campaign_id")
    p.add_argument("--shard", type=int, default=0)
    p.add_argument("--lines", type=int, default=100)

    sub.add_parser("list", help="list campaigns in the results bucket")

    p = sub.add_parser("destroy", help="delete EVERYTHING: compute, results bucket contents, registry, budget")
    p.add_argument("--yes", action="store_true", help="skip the confirmation prompt")

    args = parser.parse_args(argv)

    try:
        return dispatch(args)
    except campaigns.CampaignError as error:
        print("The campaign file has problems:", file=sys.stderr)
        for line in error.errors:
            print(f"  - {line}", file=sys.stderr)
        return 2
    except KeyboardInterrupt:
        print("\nInterrupted.", file=sys.stderr)
        return 130


def dispatch(args):
    if args.command == "validate":
        job = campaigns.prepare(args.campaign, shard_count=args.nodes)
        print(f"OK: \"{job['campaignName']}\" - {len(job['floorPlans'])} floor plan(s), {len(job['configurations'])} "
              f"configuration(s), {len(job['runs'])} run(s) on {job['execution']['shardCount']} node(s).")
        if args.write:
            campaigns.save(job, args.write)
            print(f"Expanded job file written to {args.write}")
        return 0

    if args.command == "local":
        from .local import run_local
        results = run_local(args.campaign, args.exe, args.out, args.processes)
        return 0 if not results["totals"]["missingRuns"] else 1

    if args.command == "compare":
        from .local import compare_runs
        return 0 if compare_runs(args.run_folder_a, args.run_folder_b) else 1

    from .aws_ops import Cloud
    cloud = Cloud(profile=args.profile, region=args.region, project=args.project)

    if args.command == "bootstrap":
        cloud.bootstrap(args.budget_email, args.monthly_budget, args.github_repo, not args.no_oidc_provider, args.retention_days)
    elif args.command == "push-image":
        cloud.push_image(args.build_dir, args.tag, args.git_sha)
    elif args.command == "up":
        cloud.up(args.image_tag, "EC2" if args.on_demand else "SPOT", args.max_vcpus, args.instance_types, args.node_timeout)
    elif args.command == "down":
        cloud.down()
    elif args.command == "submit":
        job = campaigns.prepare(args.campaign, shard_count=args.nodes)
        manifest = cloud.submit(job)
        if args.wait:
            cloud.wait(manifest["campaignId"])
    elif args.command == "run":
        job = campaigns.prepare(args.campaign, shard_count=args.nodes)
        print(f"Campaign {job['campaignId']}: {len(job['runs'])} run(s) on {job['execution']['shardCount']} node(s).")
        cloud.up(args.image_tag, "EC2" if args.on_demand else "SPOT", args.max_vcpus, args.instance_types, args.node_timeout)
        try:
            cloud.submit(job)
            cloud.wait(job["campaignId"], args.poll, args.deadline_minutes)
            results = cloud.fetch(job["campaignId"], os.path.join(args.out, job["campaignId"]), args.all_files)
        finally:
            if args.keep_infra:
                print("Leaving the compute stack up (--keep-infra). Nodes still scale to zero when idle. Tear down with: python -m vacuumctl down")
            else:
                cloud.down()
        return 0 if not results["totals"]["missingRuns"] else 1
    elif args.command == "status":
        cloud.status(args.campaign_id)
    elif args.command == "wait":
        cloud.wait(args.campaign_id, args.poll)
    elif args.command == "fetch":
        cloud.fetch(args.campaign_id, os.path.join(args.out, args.campaign_id), args.all_files)
    elif args.command == "cancel":
        cloud.cancel(args.campaign_id)
    elif args.command == "retry":
        cloud.retry(args.campaign_id)
    elif args.command == "logs":
        cloud.tail_logs(args.campaign_id, args.shard, args.lines)
    elif args.command == "list":
        cloud.list_campaigns()
    elif args.command == "destroy":
        if not args.yes:
            answer = input(f"This deletes every '{args.project}' AWS resource and ALL stored results in {cloud.region}. "
                           "Type the project name to confirm: ")
            if answer.strip() != args.project:
                print("Not confirmed; nothing deleted.")
                return 1
        cloud.destroy()
    return 0


if __name__ == "__main__":
    sys.exit(main())
