"""vacuumctl command line.  Run from the cloud/ folder:  python -m vacuumctl <command> --help

Typical use
  python -m vacuumctl connection set --profile vacuum --region us-east-2 --prefix acme-vacuum
  python -m vacuumctl test-connection
  python -m vacuumctl validate examples/campaign.example.json
  python -m vacuumctl local    examples/campaign.example.json --exe "C:/.../Robot vacuum simulator.exe" --processes 4
  python -m vacuumctl run      examples/campaign.example.json --nodes 8      # provision, run, fetch, tear down

Account, region, resource prefix, network and compute limits come from the cloud connection file that the
simulator's Network Settings page edits (see `connection path`). A command-line flag overrides the file for one command.
"""

import argparse
import copy
import json
import os
import sys
import traceback

from . import __version__
from . import campaign as campaigns
from . import connection as connections
from .errors import plain_error

DEFAULT_PROJECT = "robot-vacuum-sim"


def main(argv=None):
    parser = argparse.ArgumentParser(prog="vacuumctl", description="Run Robot Vacuum Simulator campaigns headless on AWS Batch.")
    parser.add_argument("--version", action="version", version=__version__)
    parser.add_argument("--connection", help="cloud connection file (default: the one the simulator's Network Settings page uses)")
    parser.add_argument("--profile", help="AWS profile name (credentials stay in your AWS config)")
    parser.add_argument("--region")
    parser.add_argument("--project", help="prefix of every AWS resource name (the connection's resource prefix)")
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
    p.add_argument("--no-open", action="store_true", help="don't open the HTML report in the browser")

    p = sub.add_parser("compare", help="check two run folders hold identical results (AT-2.17-1)")
    p.add_argument("run_folder_a")
    p.add_argument("run_folder_b")

    # ---- the cloud connection (Network Settings page)
    p = sub.add_parser("connection", help="show or change the cloud connection settings")
    csub = p.add_subparsers(dest="action", required=True)
    q = csub.add_parser("show", help="print the settings in use")
    q.add_argument("--json", action="store_true")
    csub.add_parser("path", help="print where the connection file is")
    q = csub.add_parser("set", help="change settings (creates the file if needed)")
    q.add_argument("--provider", choices=["aws"])
    q.add_argument("--sign-in", choices=list(connections.SIGN_IN_METHODS), dest="sign_in")
    q.add_argument("--profile", dest="set_profile")
    q.add_argument("--credentials-file")
    q.add_argument("--region", dest="set_region")
    q.add_argument("--prefix", help="resource name prefix, e.g. acme-vacuum")
    network_args(q)
    q.add_argument("--https-proxy", help="proxy for this computer's calls to AWS; empty string = none")
    q.add_argument("--provisioning", choices=list(connections.PROVISIONING))
    q.add_argument("--max-vcpus", type=int)
    q.add_argument("--instance-types")
    q.add_argument("--node-timeout", type=int)

    p = sub.add_parser("test-connection", help="check sign-in, region, permissions, cloud resources and network")
    p.add_argument("--json", action="store_true", help="print one JSON result on stdout (for the simulator)")

    p = sub.add_parser("sso-login", help="sign in to an AWS SSO profile in the browser (needs the AWS CLI v2)")

    p = sub.add_parser("admin-policy", help="write the least-privilege IAM policy for your AWS admin to attach")
    p.add_argument("--out", help="file to write (default: print it)")
    p.add_argument("--account", help="AWS account ID (default: the signed-in account, else any)")

    p = sub.add_parser("backends", help="list where campaigns can run (available and planned)")
    p.add_argument("--json", action="store_true")

    # ---- AWS
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
        p.add_argument("--on-demand", action="store_true", default=None, help="on-demand EC2 instead of Spot")
        p.add_argument("--max-vcpus", type=int)
        p.add_argument("--instance-types", help="comma-separated, e.g. c6i.large,c7i.large")
        p.add_argument("--node-timeout", type=int, help="hard limit (s) per node attempt")
        network_args(p)

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
    p.add_argument("--no-open", action="store_true", help="don't open the HTML report in the browser")
    compute_args(p)

    for name, text in (("status", "progress of a campaign"), ("cancel", "stop a campaign"),
                       ("retry", "re-run only the nodes that didn't return all their runs")):
        p = sub.add_parser(name, help=text)
        p.add_argument("campaign_id")

    p = sub.add_parser("wait", help="watch a campaign until it ends")
    p.add_argument("campaign_id")
    p.add_argument("--poll", type=int, default=30)

    p = sub.add_parser("fetch", help="download results, compute the batch statistics, open the HTML report")
    p.add_argument("campaign_id")
    p.add_argument("--out", default="cloud-results")
    p.add_argument("--all-files", action="store_true")
    p.add_argument("--no-open", action="store_true")

    p = sub.add_parser("report", help="(re)build report.html from a campaign_results.json and open it (no AWS needed)")
    p.add_argument("results", help="campaign_results.json, or the folder holding it")
    p.add_argument("--no-open", action="store_true")

    p = sub.add_parser("logs", help="tail a node's log from CloudWatch")
    p.add_argument("campaign_id")
    p.add_argument("--shard", type=int, default=0)
    p.add_argument("--lines", type=int, default=100)

    sub.add_parser("list", help="list campaigns in the results bucket")

    p = sub.add_parser("destroy", help="delete EVERYTHING: compute, results bucket contents, registry, budget")
    p.add_argument("--confirm", metavar="PREFIX", help="the resource prefix, typed out (for scripts and the Network Settings page); "
                                                       "without it you are asked to type it")

    args = parser.parse_args(argv)

    try:
        return dispatch(args)
    except campaigns.CampaignError as error:
        print("The campaign file has problems:", file=sys.stderr)
        for line in error.errors:
            print(f"  - {line}", file=sys.stderr)
        return 2
    except ConnectionSettingsError as error:
        print("The cloud connection settings have problems:", file=sys.stderr)
        for line in error.errors:
            print(f"  - {line}", file=sys.stderr)
        print(f"Fix them in the simulator's Network Settings page or with `python -m vacuumctl connection set`.", file=sys.stderr)
        return 2
    except KeyboardInterrupt:
        print("\nInterrupted.", file=sys.stderr)
        return 130
    except SystemExit as error:
        # aws_ops stops with sys.exit("plain message"); keep that message in the campaign log too (SDD 8).
        if isinstance(error.code, str):
            _log_failure(error.code, with_traceback=False)
            return 1
        raise
    except Exception as error:  # AWS and network failures: plain message on screen, details in the log (C.2, SDD 8)
        from . import aws_ops
        if aws_ops.log_file() is None:
            raise
        _log_failure(plain_error(error, getattr(args, "profile", None)), with_traceback=True)
        print(f"Details: {aws_ops.log_file()}", file=sys.stderr)
        return 1


def network_args(p):
    p.add_argument("--network", choices=["new", "existing"], help="a new VPC per campaign, or your existing VPC")
    p.add_argument("--vpc-cidr", help="new VPC address range, /16 to /20, e.g. 10.42.0.0/16")
    access = p.add_mutually_exclusive_group()
    access.add_argument("--private-network", dest="node_internet", action="store_false", default=None,
                        help="new VPC without internet access: nodes use VPC endpoints only (4.8)")
    access.add_argument("--node-internet", dest="node_internet", action="store_true",
                        help="new VPC with public subnets and an internet gateway (no inbound rules)")
    p.add_argument("--vpc-id", help="existing VPC ID, e.g. vpc-0abc1234")
    p.add_argument("--subnet-ids", help="existing subnets, comma-separated")


class ConnectionSettingsError(Exception):
    def __init__(self, errors):
        super().__init__("; ".join(errors))
        self.errors = errors



# ---------------------------------------------------------------------- settings resolution

def apply_overrides(conn, args):
    """A copy of the connection with this command's flags applied (flags win over the file)."""
    conn = copy.deepcopy(conn)
    aws, net, compute = conn["aws"], conn["network"], conn["compute"]
    if getattr(args, "provider", None):
        conn["provider"] = args.provider
    if getattr(args, "sign_in", None):
        aws["signIn"] = args.sign_in
    for attr, key in (("set_profile", "profile"), ("profile", "profile")):
        if getattr(args, attr, None):
            aws[key] = getattr(args, attr)
    if getattr(args, "credentials_file", None) is not None:
        aws["credentialsFile"] = args.credentials_file
    for attr in ("set_region", "region"):
        if getattr(args, attr, None):
            aws["region"] = getattr(args, attr)
    for attr in ("prefix", "project"):
        if getattr(args, attr, None):
            aws["resourcePrefix"] = getattr(args, attr)
    if getattr(args, "network", None):
        net["mode"] = "newVpc" if args.network == "new" else "existingVpc"
    if getattr(args, "vpc_cidr", None):
        net["vpcCidr"] = args.vpc_cidr
    if getattr(args, "node_internet", None) is not None:
        net["nodeInternetAccess"] = args.node_internet
    if getattr(args, "vpc_id", None):
        net["existingVpcId"] = args.vpc_id
        net["mode"] = "existingVpc" if not getattr(args, "network", None) else net["mode"]
    if getattr(args, "subnet_ids", None) is not None:
        net["existingSubnetIds"] = [s.strip() for s in args.subnet_ids.split(",") if s.strip()]
    if getattr(args, "https_proxy", None) is not None:
        net["httpsProxy"] = args.https_proxy
    if getattr(args, "provisioning", None):
        compute["provisioning"] = args.provisioning
    if getattr(args, "on_demand", None):
        compute["provisioning"] = "EC2"
    if getattr(args, "max_vcpus", None) is not None:
        compute["maxVCpus"] = args.max_vcpus
    if getattr(args, "instance_types", None) is not None:
        compute["instanceTypes"] = args.instance_types
    if getattr(args, "node_timeout", None) is not None:
        compute["nodeTimeoutSeconds"] = args.node_timeout
    return conn


def resolve(args):
    """Loads the connection file and works out the profile, region and project this command uses.

    Precedence: flag > connection file > environment (AWS_PROFILE, AWS_REGION) > default. With no connection file
    and no flags, boto3's own default credential chain is used, as before.
    """
    conn = apply_overrides(connections.load(args.connection), args)
    exists = conn["_exists"]
    aws = conn["aws"]
    args.profile = args.profile or (aws["profile"] if exists else None) or os.environ.get("AWS_PROFILE")
    args.region = (args.region or (aws["region"] if exists else None)
                   or os.environ.get("AWS_REGION") or os.environ.get("AWS_DEFAULT_REGION"))
    args.project = args.project or (aws["resourcePrefix"] if exists else None) or DEFAULT_PROJECT
    if exists:
        connections.apply_environment(conn)
    return conn


def compute_settings(conn):
    compute = conn["compute"]
    types = (compute.get("instanceTypes") or "").replace(" ", "") or None
    return ("EC2" if compute["provisioning"] == "EC2" else "SPOT", int(compute["maxVCpus"]), types,
            int(compute["nodeTimeoutSeconds"]))


def check_network(conn):
    """Only the network and compute settings matter to `up`/`run`; the rest may come from flags or the environment."""
    errors = [e for e in connections.validate(conn, check_files=False)
              if e.startswith(("VPC address", "Network mode", "Existing", "Node internet", "Provisioning",
                               "Maximum vCPUs", "Node time limit", "Instance types"))]
    if errors:
        raise ConnectionSettingsError(errors)


# ---------------------------------------------------------------------- logging helpers

def _log_failure(message, with_traceback):
    from . import aws_ops
    aws_ops.log(message, error=True)
    path = aws_ops.log_file()
    if with_traceback and path:
        try:
            with open(path, "a", encoding="utf-8") as handle:
                traceback.print_exc(file=handle)
        except OSError:
            pass


def log_path(args, campaign_id=None):
    """cloud-results/<campaign>/vacuumctl.log for campaign commands, cloud-results/vacuumctl.log for the rest."""
    out = getattr(args, "out", None) or "cloud-results"
    return os.path.join(out, campaign_id, "vacuumctl.log") if campaign_id else os.path.join(out, "vacuumctl.log")


# ---------------------------------------------------------------------- commands

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
        results = run_local(args.campaign, args.exe, args.out, args.processes, open_report=not args.no_open)
        return 0 if not results["totals"]["missingRuns"] else 1

    if args.command == "report":
        from .report import write_report
        write_report(args.results, open_browser=not args.no_open)
        return 0

    if args.command == "compare":
        from .local import compare_runs
        return 0 if compare_runs(args.run_folder_a, args.run_folder_b) else 1

    if args.command == "backends":
        from . import backends
        entries = backends.describe_all()
        if args.json:
            print(json.dumps(entries, indent=2))
        else:
            for entry in entries:
                print(f"{entry['name']:6} {entry['label']:28} {'available' if entry['available'] else 'planned'}")
        return 0

    if args.command == "connection":
        return connection_command(args)

    conn = resolve(args)
    from . import backends
    from .aws_ops import log, set_log_file, set_machine_output

    if args.command == "test-connection":
        set_machine_output(args.json)
        set_log_file(log_path(args))
        log(f"--- vacuumctl test-connection ({connections.describe_network(conn)})")
        result = backends.get(conn["provider"]).test_connection(conn)
        for item in result["checks"]:
            log(f"{item['status'].upper()} {item['name']}: {item['message']}", error=item["status"] == "fail", console=False)
            for detail in item.get("details", []):
                log(f"    {detail}", console=False)
        print(json.dumps(result, indent=2) if args.json else backends.format_report(result))
        return 0 if result["ok"] else 1

    if args.command == "admin-policy":
        from . import policy
        account = args.account or _account_or_none(args)
        document = json.dumps(policy.render(args.project, args.region or connections.DEFAULTS["aws"]["region"], account), indent=2)
        if args.out:
            with open(args.out, "w", encoding="utf-8") as handle:
                handle.write(document + "\n")
            print(f"Admin policy written to {args.out} (account {account or 'any'}, region {args.region}, prefix {args.project}).")
            print("Your AWS admin creates it as a customer managed policy (it is too big for an inline policy, which is "
                  "limited to 2,048 characters) and attaches it to the IAM user or role you sign in with:")
            print(f"  aws iam create-policy --policy-name {args.project} --policy-document file://{args.out}")
            print(f"  aws iam attach-user-policy --user-name <your IAM user> --policy-arn "
                  f"arn:{policy.partition_of(args.region or 'us-east-1')}:iam::{account or '<account id>'}:policy/{args.project}")
        else:
            print(document)
        return 0

    if args.command == "sso-login":
        from .aws_ops import sso_login
        if not args.profile:
            raise ConnectionSettingsError(["AWS profile name: set the SSO profile's name first."])
        return sso_login(args.profile)

    # Every AWS command keeps a dedicated network/infrastructure log (SDD 8), per campaign where there is one.
    job = None
    if args.command in ("submit", "run"):
        job = campaigns.prepare(args.campaign, shard_count=args.nodes)
        set_log_file(log_path(args, job["campaignId"]))
    else:
        set_log_file(log_path(args, getattr(args, "campaign_id", None)))
    log(f"--- vacuumctl {args.command} (profile {args.profile or 'default'}, region {args.region or 'from profile'}, "
        f"prefix {args.project})")

    if args.command in ("up", "run"):
        check_network(conn)

    cloud = backends.get("aws").cloud(args.profile, args.region, args.project)

    if args.command == "bootstrap":
        cloud.bootstrap(args.budget_email, args.monthly_budget, args.github_repo, not args.no_oidc_provider, args.retention_days)
    elif args.command == "push-image":
        cloud.push_image(args.build_dir, args.tag, args.git_sha)
    elif args.command == "up":
        provisioning, max_vcpus, types, timeout = compute_settings(conn)
        cloud.up(args.image_tag, provisioning, max_vcpus, types, timeout, network=connections.network_parameters(conn))
    elif args.command == "down":
        cloud.down()
    elif args.command == "submit":
        manifest = cloud.submit(job)
        if args.wait:
            cloud.wait(manifest["campaignId"])
    elif args.command == "run":
        log(f"Campaign {job['campaignId']}: {len(job['runs'])} run(s) on {job['execution']['shardCount']} node(s); "
            f"network: {connections.describe_network(conn)}.")
        provisioning, max_vcpus, types, timeout = compute_settings(conn)
        # The nodes' role is limited to this campaign's S3 prefix (4.8): custom algorithms can't touch other campaigns.
        cloud.up(args.image_tag, provisioning, max_vcpus, types, timeout, campaign_scope=job["campaignId"],
                 network=connections.network_parameters(conn))
        try:
            cloud.submit(job)
            cloud.wait(job["campaignId"], args.poll, args.deadline_minutes)
            results = cloud.fetch(job["campaignId"], os.path.join(args.out, job["campaignId"]), args.all_files,
                                  open_report=not args.no_open)
        finally:
            if args.keep_infra:
                log("Leaving the compute stack up (--keep-infra). Nodes still scale to zero when idle, and can only reach "
                    "this campaign's data; `python -m vacuumctl up` reopens it to any campaign. Tear down with: python -m vacuumctl down")
            else:
                cloud.down()
        return 0 if not results["totals"]["missingRuns"] else 1
    elif args.command == "status":
        cloud.status(args.campaign_id)
    elif args.command == "wait":
        cloud.wait(args.campaign_id, args.poll)
    elif args.command == "fetch":
        cloud.fetch(args.campaign_id, os.path.join(args.out, args.campaign_id), args.all_files, open_report=not args.no_open)
    elif args.command == "cancel":
        cloud.cancel(args.campaign_id)
    elif args.command == "retry":
        cloud.retry(args.campaign_id)
    elif args.command == "logs":
        cloud.tail_logs(args.campaign_id, args.shard, args.lines)
    elif args.command == "list":
        cloud.list_campaigns()
    elif args.command == "destroy":
        # C.9: the prefix must always be typed, here or (for the Network Settings page and scripts) in --confirm.
        answer = args.confirm
        if answer is None:
            answer = input(f"This deletes every '{args.project}' AWS resource and ALL stored results in {cloud.region}. "
                           "Type the resource prefix to confirm: ")
        if answer.strip() != args.project:
            log("Not confirmed (the prefix didn't match); nothing deleted.", error=True)
            return 1
        cloud.destroy()
    return 0


def _account_or_none(args):
    try:
        import boto3
        return boto3.Session(profile_name=args.profile, region_name=args.region).client("sts").get_caller_identity()["Account"]
    except Exception:
        return None


def connection_command(args):
    path = args.connection or connections.default_path()
    if args.action == "path":
        print(path)
        return 0
    conn = connections.load(path)
    if args.action == "show":
        problems = connections.validate(conn)
        if args.json:
            data = {k: v for k, v in conn.items() if not k.startswith("_")}
            print(json.dumps({"path": path, "exists": conn["_exists"], "problems": problems, "connection": data}, indent=2))
        else:
            aws = conn["aws"]
            print(f"File:     {path}{'' if conn['_exists'] else '  (not created yet; showing defaults)'}")
            print(f"Provider: {conn['provider']}   sign-in: {aws['signIn']}   profile: {aws['profile']}"
                  + (f"   credentials file: {aws['credentialsFile']}" if aws["signIn"] == "credentialsFile" else ""))
            print(f"Region:   {aws['region']}   resource prefix: {aws['resourcePrefix']}")
            print(f"Network:  {connections.describe_network(conn)}"
                  + (f"   proxy: {conn['network']['httpsProxy']}" if conn["network"]["httpsProxy"] else ""))
            c = conn["compute"]
            print(f"Compute:  {c['provisioning']}, up to {c['maxVCpus']} vCPUs, node limit {c['nodeTimeoutSeconds']} s"
                  + (f", types {c['instanceTypes']}" if c["instanceTypes"] else ""))
            for problem in problems:
                print(f"  ! {problem}")
        return 0 if not problems else 1
    # set
    updated = apply_overrides(conn, args)
    problems = connections.validate(updated)
    if problems:
        raise ConnectionSettingsError(problems)
    connections.save(updated, path)
    print(f"Saved {path}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
