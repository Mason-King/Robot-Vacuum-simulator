"""The cloud connection: which cloud account and network campaigns use (5.5, 4.8, C.12).

One small JSON file, shared by the simulator's Network Settings page (CloudConnection.cs) and this tool, so both always
use the same account, region, resource prefix and network. It holds **no secrets**: only the *name* of an AWS
profile or the *path* of a credentials file. The credentials themselves stay where AWS keeps them
(~/.aws/credentials, the AWS SSO cache, or a credential_process such as a Windows Credential Manager helper).

Default location (same as Environment.SpecialFolder.ApplicationData in the simulator):
  Windows   %APPDATA%\\RobotVacuumSim\\cloud-connection.json
  elsewhere $XDG_CONFIG_HOME/RobotVacuumSim/cloud-connection.json  (~/.config/... when unset)
Override with --connection <file> or VACUUM_CLOUD_CONNECTION.

Precedence for every setting: command-line flag > this file > environment (AWS_PROFILE, AWS_REGION) > default.
"""

import copy
import ipaddress
import json
import os
import re

SCHEMA_VERSION = 1
FILE_NAME = "cloud-connection.json"
APP_FOLDER = "RobotVacuumSim"

SIGN_IN_METHODS = ("profile", "sso", "credentialsFile")
NETWORK_MODES = ("newVpc", "existingVpc")
PROVISIONING = ("SPOT", "EC2")

# Keep these patterns identical to CloudConnection.cs (tests/test_connection.py checks they match).
REGION_PATTERN = r"^[a-z]{2}(-gov)?-[a-z]+-\d$"
PREFIX_PATTERN = r"^[a-z][a-z0-9-]{1,30}[a-z0-9]$"
VPC_ID_PATTERN = r"^vpc-[0-9a-f]{8,17}$"
SUBNET_ID_PATTERN = r"^subnet-[0-9a-f]{8,17}$"
PROXY_PATTERN = r"^https?://[^\s/:]+(:\d{1,5})?/?$"
INSTANCE_TYPES_PATTERN = r"^[a-z0-9]+(\.[a-z0-9]+)?(,[a-z0-9]+(\.[a-z0-9]+)?)*$"
MIN_VPC_PREFIX, MAX_VPC_PREFIX = 16, 20
PRIVATE_RANGES = ("10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16")  # RFC 1918, as in CloudConnection.cs

DEFAULTS = {
    "schemaVersion": SCHEMA_VERSION,
    "provider": "aws",
    "aws": {
        "signIn": "profile",
        "profile": "default",
        "credentialsFile": "",
        "region": "us-east-2",
        "resourcePrefix": "robot-vacuum-sim",
    },
    "network": {
        "mode": "newVpc",
        "vpcCidr": "10.42.0.0/16",
        "nodeInternetAccess": True,
        "existingVpcId": "",
        "existingSubnetIds": [],
        "httpsProxy": "",
    },
    "compute": {
        "provisioning": "SPOT",
        "maxVCpus": 32,
        "instanceTypes": "",
        "nodeTimeoutSeconds": 21600,
    },
    # Used only by the simulator, to start this tool. Kept here so the file round-trips unchanged.
    "tool": {"python": "", "cloudFolder": ""},
}


def default_path(environ=None):
    environ = os.environ if environ is None else environ
    if environ.get("VACUUM_CLOUD_CONNECTION"):
        return environ["VACUUM_CLOUD_CONNECTION"]
    if os.name == "nt" and environ.get("APPDATA"):
        base = environ["APPDATA"]
    else:
        base = environ.get("XDG_CONFIG_HOME") or os.path.join(os.path.expanduser("~"), ".config")
    return os.path.join(base, APP_FOLDER, FILE_NAME)


def _merge(defaults, given):
    result = copy.deepcopy(defaults)
    for key, value in (given or {}).items():
        if isinstance(result.get(key), dict) and isinstance(value, dict):
            result[key] = _merge(result[key], value)
        else:
            result[key] = value
    return result


def load(path=None):
    """The connection with every default filled in, and whether the file exists. A broken file raises ValueError."""
    path = path or default_path()
    if not os.path.isfile(path):
        conn = copy.deepcopy(DEFAULTS)
        conn["_exists"] = False
        conn["_path"] = path
        return conn
    try:
        with open(path, encoding="utf-8-sig") as handle:
            given = json.load(handle)
    except (OSError, json.JSONDecodeError) as error:
        raise ValueError(f"The cloud connection file {path} can't be read: {error}") from error
    if not isinstance(given, dict):
        raise ValueError(f"The cloud connection file {path} isn't a JSON object.")
    conn = _merge(DEFAULTS, given)
    conn["_exists"] = True
    conn["_path"] = path
    return conn


def save(conn, path=None):
    """Writes atomically (temp file + replace), so the simulator never reads half a file."""
    path = path or conn.get("_path") or default_path()
    os.makedirs(os.path.dirname(os.path.abspath(path)), exist_ok=True)
    data = {k: v for k, v in conn.items() if not k.startswith("_")}
    temp = path + ".tmp"
    with open(temp, "w", encoding="utf-8") as handle:
        json.dump(data, handle, indent=2)
        handle.write("\n")
    os.replace(temp, path)
    return path


def _int(value):
    if isinstance(value, bool):
        return None
    try:
        number = float(value)
    except (TypeError, ValueError):
        return None
    return int(number) if number == int(number) else None


def validate(conn, check_files=True):
    """Every problem with the connection, as plain sentences (C.2). Empty = usable."""
    errors = []
    if conn.get("schemaVersion") != SCHEMA_VERSION:
        errors.append(f"schemaVersion {conn.get('schemaVersion')} isn't supported (expected {SCHEMA_VERSION}).")

    from . import backends  # here to avoid an import cycle
    provider = conn.get("provider")
    if provider not in backends.cloud_provider_names():
        planned = backends.planned_provider_labels()
        errors.append(f'Provider "{provider}" isn\'t available. Available: AWS.'
                      + (f" Planned: {', '.join(planned)}." if planned else ""))

    aws = conn.get("aws") or {}
    sign_in = aws.get("signIn")
    if sign_in not in SIGN_IN_METHODS:
        errors.append(f'Sign-in method "{sign_in}" must be one of: AWS profile, AWS SSO, credentials file.')
    profile = str(aws.get("profile") or "")
    if not profile.strip() or any(c.isspace() for c in profile):
        errors.append("AWS profile name: enter the profile's name (no spaces), e.g. default or vacuum.")
    if sign_in == "credentialsFile":
        path = str(aws.get("credentialsFile") or "")
        if not path:
            errors.append("Credentials file: choose the file that holds the AWS keys.")
        elif check_files and not os.path.isfile(os.path.expanduser(path)):
            errors.append(f"Credentials file: {path} doesn't exist.")
    if not re.match(REGION_PATTERN, str(aws.get("region") or "")):
        errors.append(f'Region "{aws.get("region")}" doesn\'t look like an AWS region, e.g. us-east-2.')
    if not re.match(PREFIX_PATTERN, str(aws.get("resourcePrefix") or "")):
        errors.append(f'Resource name prefix "{aws.get("resourcePrefix")}": use 3-32 lowercase letters, digits or '
                      "hyphens, starting with a letter and not ending with a hyphen (e.g. acme-vacuum).")

    net = conn.get("network") or {}
    mode = net.get("mode")
    if mode not in NETWORK_MODES:
        errors.append(f'Network mode "{mode}" must be "newVpc" or "existingVpc".')
    if mode == "newVpc":
        cidr = str(net.get("vpcCidr") or "")
        try:
            network = ipaddress.IPv4Network(cidr, strict=True)
            if not MIN_VPC_PREFIX <= network.prefixlen <= MAX_VPC_PREFIX:
                errors.append(f"VPC address range {cidr}: use a /{MIN_VPC_PREFIX} to /{MAX_VPC_PREFIX} range.")
            elif not any(network.subnet_of(ipaddress.IPv4Network(r)) for r in PRIVATE_RANGES):
                errors.append(f"VPC address range {cidr}: use a private range (10.x, 172.16-31.x or 192.168.x).")
        except ValueError:
            errors.append(f'VPC address range "{cidr}" isn\'t a network address, e.g. 10.42.0.0/16.')
        if not isinstance(net.get("nodeInternetAccess"), bool):
            errors.append("Node internet access must be true or false.")
    elif mode == "existingVpc":
        if not re.match(VPC_ID_PATTERN, str(net.get("existingVpcId") or "")):
            errors.append(f'Existing VPC ID "{net.get("existingVpcId")}" should look like vpc-0abc1234.')
        subnets = net.get("existingSubnetIds") or []
        if not isinstance(subnets, list) or not subnets:
            errors.append("Existing subnets: list at least one subnet ID (two in different zones is better).")
        else:
            bad = [s for s in subnets if not re.match(SUBNET_ID_PATTERN, str(s))]
            if bad:
                errors.append(f"Existing subnets: {', '.join(map(str, bad))} don't look like subnet-0abc1234.")
            if len(set(subnets)) != len(subnets):
                errors.append("Existing subnets: a subnet is listed twice.")
    proxy = str(net.get("httpsProxy") or "")
    if proxy and not re.match(PROXY_PATTERN, proxy):
        errors.append(f'HTTPS proxy "{proxy}" should look like http://proxy.example.com:8080 (no user name or password).')

    compute = conn.get("compute") or {}
    if compute.get("provisioning") not in PROVISIONING:
        errors.append('Provisioning must be "SPOT" or "EC2" (on-demand).')
    vcpus = _int(compute.get("maxVCpus"))
    if vcpus is None or not 1 <= vcpus <= 4096:
        errors.append("Maximum vCPUs must be a whole number from 1 to 4096.")
    timeout = _int(compute.get("nodeTimeoutSeconds"))
    if timeout is None or not 60 <= timeout <= 1209600:
        errors.append("Node time limit must be 60 seconds to 14 days.")
    types = str(compute.get("instanceTypes") or "").replace(" ", "")
    if types and not re.match(INSTANCE_TYPES_PATTERN, types):
        errors.append(f'Instance types "{compute.get("instanceTypes")}": comma-separated, e.g. c6i.large,c7i.large.')
    return errors


def apply_environment(conn, environ=None):
    """Points boto3 at the chosen credentials file and proxy, for this process only. Nothing is written to disk."""
    environ = os.environ if environ is None else environ
    aws = conn.get("aws") or {}
    if aws.get("signIn") == "credentialsFile" and aws.get("credentialsFile"):
        environ["AWS_SHARED_CREDENTIALS_FILE"] = os.path.expanduser(aws["credentialsFile"])
    proxy = (conn.get("network") or {}).get("httpsProxy")
    if proxy:
        environ["HTTPS_PROXY"] = proxy
        environ["https_proxy"] = proxy


def network_parameters(conn):
    """compute.yaml parameters for the connection's network settings."""
    net = conn.get("network") or DEFAULTS["network"]
    if net.get("mode") == "existingVpc":
        return {
            "NetworkMode": "ExistingVpc",
            "ExistingVpcId": net["existingVpcId"],
            "ExistingSubnetIds": ",".join(net["existingSubnetIds"]),
        }
    return {
        "NetworkMode": "NewVpc",
        "VpcCidr": net.get("vpcCidr") or DEFAULTS["network"]["vpcCidr"],
        "NodeInternetAccess": "true" if net.get("nodeInternetAccess", True) else "false",
    }


def describe_network(conn):
    net = conn.get("network") or DEFAULTS["network"]
    if net.get("mode") == "existingVpc":
        return f"existing VPC {net.get('existingVpcId')} ({len(net.get('existingSubnetIds') or [])} subnet(s))"
    access = "with internet access" if net.get("nodeInternetAccess", True) else "private, VPC endpoints only"
    return f"new VPC {net.get('vpcCidr')} per campaign, {access}"
