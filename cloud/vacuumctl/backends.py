"""Where campaigns run: the backend interface (5.5).

The simulator and this tool talk to a backend only through these methods, so another provider (Azure, Google
Cloud, an SSH cluster) is a new class here plus its own settings section in the simulator's Settings page;
dispatch, results and reports don't change. Shipped: Local (this computer) and AWS. The rest are planned (C.4).
"""

import os

from . import connection as connections
from .errors import error_code, plain_error

ENDPOINT_SERVICES = ("s3", "ecr.api", "ecr.dkr", "logs", "ecs", "ecs-agent", "ecs-telemetry")
DEFAULT_VPC_LIMIT = 5


class Backend:
    name = ""
    label = ""
    kind = "cloud"      # "local" or "cloud"
    available = True

    def describe(self):
        return {"name": self.name, "label": self.label, "kind": self.kind, "available": self.available}

    def test_connection(self, conn):
        raise NotImplementedError


class PlannedBackend(Backend):
    available = False

    def __init__(self, name, label):
        self.name, self.label = name, label

    def test_connection(self, conn):
        return report(False, self.name, [check("Provider", "fail", f"{self.label} support is planned but not built yet.")])


class LocalBackend(Backend):
    name, label, kind = "local", "This computer", "local"

    def test_connection(self, conn):
        return report(True, self.name, [check("Local runs", "ok", "Runs on this computer; no sign-in or network needed.")])

    def run(self, campaign_path, exe, out, processes, open_report=True):
        from .local import run_local
        return run_local(campaign_path, exe, out, processes, open_report=open_report)


class AwsBackend(Backend):
    name, label = "aws", "Amazon Web Services"

    def cloud(self, profile, region, project):
        from .aws_ops import Cloud
        return Cloud(profile=profile, region=region, project=project)

    def test_connection(self, conn, session_factory=None):
        return test_aws_connection(conn, session_factory)


BACKENDS = {b.name: b for b in (LocalBackend(), AwsBackend())}
PLANNED = [PlannedBackend("azure", "Microsoft Azure"), PlannedBackend("gcp", "Google Cloud"),
           PlannedBackend("ssh", "SSH / on-premises cluster")]


def get(name):
    if name in BACKENDS:
        return BACKENDS[name]
    for planned in PLANNED:
        if planned.name == name:
            return planned
    raise KeyError(name)


def cloud_provider_names():
    return [name for name, backend in BACKENDS.items() if backend.kind == "cloud"]


def planned_provider_labels():
    return [p.label for p in PLANNED]


def describe_all():
    return [b.describe() for b in BACKENDS.values()] + [p.describe() for p in PLANNED]


# ---------------------------------------------------------------------- reports

def check(name, status, message):
    return {"name": name, "status": status, "message": message}


def report(ok, provider, checks, **extra):
    result = {"ok": ok, "provider": provider, "checks": checks}
    result.update(extra)
    return result


def format_report(result):
    marks = {"ok": "OK  ", "warn": "WARN", "fail": "FAIL"}
    lines = [f"[{marks.get(c['status'], c['status'])}] {c['name']}: {c['message']}" for c in result["checks"]]
    lines.append("Connection OK." if result["ok"] else "Connection has problems (see FAIL above).")
    return "\n".join(lines)


# ---------------------------------------------------------------------- AWS checks

def test_aws_connection(conn, session_factory=None):
    """Checks a connection end to end without changing anything in the account."""
    checks = []

    def done(**extra):
        return report(not any(c["status"] == "fail" for c in checks), "aws", checks, **extra)

    problems = connections.validate(conn)
    if problems:
        checks.append(check("Settings", "fail", " ".join(problems)))
        return done()
    checks.append(check("Settings", "ok", "Complete."))

    aws = conn["aws"]
    profile, region, project = aws["profile"], aws["region"], aws["resourcePrefix"]
    connections.apply_environment(conn)
    if conn["network"].get("httpsProxy"):
        checks.append(check("Proxy", "ok", f"AWS calls from this computer go through {conn['network']['httpsProxy']}."))

    if session_factory is None:
        try:
            import boto3
        except ImportError:
            checks.append(check("Tools", "fail", "boto3 isn't installed. Run: pip install -r cloud/vacuumctl/requirements.txt"))
            return done()

        def session_factory(profile_name, region_name):
            return boto3.Session(profile_name=profile_name, region_name=region_name)

    try:
        session = session_factory(profile, region)
        identity = session.client("sts").get_caller_identity()
    except Exception as error:  # every sign-in failure ends the test here
        checks.append(check("Sign-in", "fail", plain_error(error, profile)))
        return done()
    account, arn = identity["Account"], identity["Arn"]
    checks.append(check("Sign-in", "ok", f"Signed in as {arn} (account {account})."))

    ec2 = session.client("ec2")
    try:
        zones = [z["ZoneName"] for z in ec2.describe_availability_zones(
            Filters=[{"Name": "state", "Values": ["available"]}])["AvailabilityZones"]]
        if conn["network"]["mode"] == "newVpc" and len(zones) < 2:
            checks.append(check("Region", "fail", f"{region} has {len(zones)} available zone(s); a new VPC needs 2."))
        else:
            checks.append(check("Region", "ok", f"{region}: {len(zones)} availability zones."))
    except Exception as error:
        checks.append(check("Region", "fail", plain_error(error, profile)))
        return done(account=account, arn=arn)

    checks.append(_permission_check(session, account, arn, project, region))
    checks.extend(_resource_checks(session, project))
    checks.append(_network_check(ec2, conn))
    return done(account=account, arn=arn)


def _principal_arn(session, arn):
    """The IAM ARN the simulator needs: a user ARN as is; an assumed-role session becomes its role's ARN."""
    if ":assumed-role/" not in arn:
        return arn
    partition, account = arn.split(":")[1], arn.split(":")[4]
    role = arn.split(":assumed-role/")[1].split("/")[0]
    try:
        return session.client("iam").get_role(RoleName=role)["Role"]["Arn"]
    except Exception:
        return f"arn:{partition}:iam::{account}:role/{role}"


def _permission_check(session, account, arn, project, region):
    from . import policy

    if arn.endswith(":root"):
        return check("Permissions", "warn", "Signed in as the account's root user: every permission, but AWS advises "
                                            "against it. Use an IAM user or role with the admin policy file instead.")
    if ":federated-user/" in arn:
        return check("Permissions", "warn", "Federated users can't be checked ahead of time; setup will report any missing permission.")

    principal = _principal_arn(session, arn)
    username = arn.rsplit("/", 1)[-1]
    iam = session.client("iam")
    # Every real request carries these; without them the simulator treats conditions on them as unmet.
    base_context = [{"ContextKeyName": "aws:RequestedRegion", "ContextKeyValues": [region], "ContextKeyType": "string"},
                    {"ContextKeyName": "aws:SecureTransport", "ContextKeyValues": ["true"], "ContextKeyType": "boolean"}]
    denied, unsure = [], []  # (action, resource, explanation)
    try:
        for actions, resource, context in policy.probes(project, region, account, username, _bucket_name(session, project)):
            given = {entry["ContextKeyName"] for entry in context}
            request = {"PolicySourceArn": principal, "ActionNames": actions, "ResourceArns": [resource],
                       "ContextEntries": context + [e for e in base_context if e["ContextKeyName"] not in given]}
            while True:
                page = iam.simulate_principal_policy(**request)
                for result in page["EvaluationResults"]:
                    if result["EvalDecision"] == "allowed":
                        continue
                    entry = (result["EvalActionName"], resource, _explain(result))
                    if result["EvalDecision"] == "implicitDeny" and result.get("MissingContextValues"):
                        unsure.append(entry)  # depends on a request detail the check can't know
                    else:
                        denied.append(entry)
                if not page.get("IsTruncated"):
                    break
                request["Marker"] = page["Marker"]
    except Exception as error:
        if error_code(error) in ("AccessDenied", "AccessDeniedException"):
            return check("Permissions", "warn", "Couldn't check permissions ahead of time (this sign-in can't call "
                                                "iam:SimulatePrincipalPolicy). Setup will report anything missing.")
        return check("Permissions", "warn", f"Couldn't check permissions: {plain_error(error)}")

    details = [f"not allowed: {a} on {r}{why}" for a, r, why in sorted(set(denied))]
    details += [f"couldn't confirm: {a} on {r}{why}" for a, r, why in sorted(set(unsure))]
    if denied:
        actions = sorted({a for a, _, _ in denied})
        shown = ", ".join(actions[:8]) + (" ..." if len(actions) > 8 else "")
        result = check("Permissions", "fail", f"{len(actions)} needed action(s) aren't allowed: {shown}. The full list, with "
                                              "the reason for each, is in the log. If the admin policy was attached just now, "
                                              "wait a minute and test again (IAM changes take a moment to apply); otherwise "
                                              "ask your AWS admin to attach it (Settings > Save admin policy).")
    elif unsure:
        keys = sorted({k for _, _, why in unsure for k in why.split("needs ")[-1].rstrip(")").split(", ") if "needs " in why})
        result = check("Permissions", "warn", f"{len({a for a, _, _ in unsure})} action(s) couldn't be confirmed: your "
                                              f"policies depend on request details the check can't supply ({', '.join(keys)}). "
                                              "Real requests usually include them; setup will report anything missing.")
    else:
        return check("Permissions", "ok", "Everything setup and campaigns need is allowed.")
    result["details"] = details
    return result


def _explain(result):
    """Why the simulator said no, from its own answer: an explicit deny, a boundary, an SCP, or a missing request detail."""
    reasons = []
    if result["EvalDecision"] == "explicitDeny":
        reasons.append("explicitly denied")
    if (result.get("PermissionsBoundaryDecisionDetail") or {}).get("AllowedByPermissionsBoundary") is False:
        reasons.append("blocked by the user's permissions boundary")
    if (result.get("OrganizationsDecisionDetail") or {}).get("AllowedByOrganizations") is False:
        reasons.append("blocked by an AWS Organizations policy (SCP)")
    policies = sorted({m.get("SourcePolicyId") for m in result.get("MatchedStatements") or [] if m.get("SourcePolicyId")})
    if policies:
        reasons.append("matched " + ", ".join(policies))
    if result.get("MissingContextValues"):
        reasons.append("needs " + ", ".join(result["MissingContextValues"]))
    return f" ({'; '.join(reasons)})" if reasons else " (no policy allows it)"


def _bucket_name(session, project):
    """The real results bucket's name, if the base stack exists, so the check uses it rather than a sample name."""
    try:
        stack = session.client("cloudformation").describe_stacks(StackName=f"{project}-base")["Stacks"][0]
        return next((o["OutputValue"] for o in stack.get("Outputs", []) if o["OutputKey"] == "ResultsBucketName"), None)
    except Exception:
        return None


def _resource_checks(session, project):
    checks = []
    stack = f"{project}-base"
    try:
        found = session.client("cloudformation").describe_stacks(StackName=stack)["Stacks"][0]
    except Exception as error:
        if error_code(error) == "ValidationError":  # "Stack ... does not exist"
            checks.append(check("Cloud resources", "warn", f"Not set up yet in this region (no {stack} stack). "
                                                           "Press \"Set up cloud resources\"."))
            return checks
        checks.append(check("Cloud resources", "warn", f"Couldn't look: {plain_error(error)}"))
        return checks
    status = found["StackStatus"]
    if not status.endswith("_COMPLETE") or "ROLLBACK" in status:
        checks.append(check("Cloud resources", "fail", f"{stack} is in state {status}. Remove and set up again."))
        return checks
    outputs = {o["OutputKey"]: o["OutputValue"] for o in found.get("Outputs", [])}
    checks.append(check("Cloud resources", "ok", f"Set up: results bucket {outputs.get('ResultsBucketName', '?')}."))
    try:
        session.client("ecr").describe_images(repositoryName=f"{project}-headless", imageIds=[{"imageTag": "latest"}])
        checks.append(check("Simulator image", "ok", "An image tagged latest is in the registry."))
    except Exception as error:
        checks.append(check("Simulator image", "warn", "No image tagged latest yet. Push one (CI on main, or "
                                                       "`python -m vacuumctl push-image`)." if error_code(error) in
                            ("ImageNotFoundException", "RepositoryNotFoundException") else plain_error(error)))
    return checks


def _network_check(ec2, conn):
    net = conn["network"]
    try:
        if net["mode"] == "newVpc":
            count = len(ec2.describe_vpcs()["Vpcs"])
            extra = "" if net["nodeInternetAccess"] else (
                " Private network: 6 interface endpoints in 2 zones, billed by AWS per endpoint-hour while a campaign runs.")
            if count >= DEFAULT_VPC_LIMIT:
                return check("Network", "warn", f"The region already has {count} VPCs; the default limit is "
                                                f"{DEFAULT_VPC_LIMIT}, so creating one per campaign may fail.{extra}")
            return check("Network", "ok", f"A new VPC ({net['vpcCidr']}) is made for each campaign.{extra}")
        return _existing_vpc_check(ec2, net)
    except Exception as error:
        code = error_code(error)
        if code.startswith("InvalidVpcID") or code.startswith("InvalidSubnetID"):
            return check("Network", "fail", f"{plain_error(error)} Check the IDs and the region.")
        return check("Network", "warn", f"Couldn't check the network: {plain_error(error)}")


def _existing_vpc_check(ec2, net):
    vpc_id, subnet_ids = net["existingVpcId"], net["existingSubnetIds"]
    ec2.describe_vpcs(VpcIds=[vpc_id])
    subnets = ec2.describe_subnets(SubnetIds=subnet_ids)["Subnets"]
    elsewhere = [s["SubnetId"] for s in subnets if s["VpcId"] != vpc_id]
    if elsewhere:
        return check("Network", "fail", f"Subnet(s) {', '.join(elsewhere)} aren't in {vpc_id}.")

    # Service names are com.amazonaws.<region>.<service>, e.g. ...us-east-2.ecr.api
    endpoints = {".".join(e["ServiceName"].split(".")[3:])
                 for e in ec2.describe_vpc_endpoints(Filters=[{"Name": "vpc-id", "Values": [vpc_id]}])["VpcEndpoints"]
                 if e.get("State", "available") in ("available", "pending")}
    main = ec2.describe_route_tables(Filters=[{"Name": "vpc-id", "Values": [vpc_id]},
                                              {"Name": "association.main", "Values": ["true"]}])["RouteTables"]
    problems = []
    for subnet in subnets:
        tables = ec2.describe_route_tables(Filters=[{"Name": "association.subnet-id", "Values": [subnet["SubnetId"]]}])["RouteTables"] or main
        routes = [r for t in tables for r in t.get("Routes", []) if r.get("DestinationCidrBlock") == "0.0.0.0/0"
                  and r.get("State", "active") == "active"]
        via_igw = any(str(r.get("GatewayId", "")).startswith("igw-") for r in routes)
        via_other = any(r.get("NatGatewayId") or r.get("TransitGatewayId") or r.get("NetworkInterfaceId") or r.get("InstanceId")
                        for r in routes)
        if via_igw and not via_other and not subnet.get("MapPublicIpOnLaunch"):
            problems.append(f"{subnet['SubnetId']} routes to an internet gateway but doesn't give instances public IPs")
        elif not routes:
            missing = [s for s in ENDPOINT_SERVICES if s not in endpoints]
            if missing:
                problems.append(f"{subnet['SubnetId']} has no route out and the VPC lacks endpoints for {', '.join(missing)}")
    if problems:
        return check("Network", "fail", "Nodes couldn't reach AWS: " + "; ".join(problems) + ".")
    zones = {s["AvailabilityZone"] for s in subnets}
    note = "" if len(zones) > 1 else " Only one availability zone: Spot capacity may be harder to get."
    return check("Network", "warn" if len(zones) == 1 else "ok",
                 f"{vpc_id} with {len(subnets)} subnet(s) in {len(zones)} zone(s); your network's routing applies.{note}")
