"""The cloud connection (5.5, 4.8, C.12): settings file, validation, network parameters, admin policy, and the
connection test against fake AWS clients. No AWS account or network needed."""

import copy
import json
import os
import re
import tempfile
import unittest

from vacuumctl import __main__ as cli
from vacuumctl import backends, connection, policy

CLOUD = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CS_FILE = os.path.join(CLOUD, "..", "Robot vacuum simulator", "Assets", "Scripts", "LevelEditor", "Cloud", "CloudConnection.cs")


def read(path):
    with open(path, encoding="utf-8-sig") as handle:
        return handle.read()


def conn(**changes):
    c = copy.deepcopy(connection.DEFAULTS)
    for path, value in changes.items():
        section, _, key = path.partition("__")
        if key:
            c[section][key] = value
        else:
            c[section] = value
    return c


class ClientError(Exception):
    """Named like botocore's, which is what plain_error and error_code look at."""

    def __init__(self, code, message="denied"):
        super().__init__(message)
        self.response = {"Error": {"Code": code, "Message": message}}


class Fake:
    def __init__(self, **methods):
        self.methods = methods

    def __getattr__(self, name):
        value = self.methods[name]
        return value if callable(value) else (lambda **_: value)


class FakeSession:
    def __init__(self, **clients):
        self.clients = clients

    def client(self, name):
        return self.clients[name]


def simulate_like_aws(allowed=lambda action: True):
    def simulate(**request):
        for resource in request.get("ResourceArns", ["*"]):
            if resource != "*" and not resource.startswith("arn:"):
                raise ClientError("InvalidInput", f"ResourceNames are not in a valid ARN format: {resource}.")
        return {"EvaluationResults": [{"EvalActionName": a, "EvalDecision": "allowed" if allowed(a) else "implicitDeny"}
                                      for a in request["ActionNames"]]}
    return simulate


def healthy_clients(**overrides):
    clients = {
        "sts": Fake(get_caller_identity={"Account": "111122223333", "Arn": "arn:aws:iam::111122223333:user/vacuum-sim"}),
        "ec2": Fake(describe_availability_zones={"AvailabilityZones": [{"ZoneName": "us-east-2a"}, {"ZoneName": "us-east-2b"}]},
                    describe_vpcs={"Vpcs": [{}]}),
        "iam": Fake(simulate_principal_policy=simulate_like_aws()),
        "cloudformation": Fake(describe_stacks={"Stacks": [{"StackStatus": "CREATE_COMPLETE",
                                                            "Outputs": [{"OutputKey": "ResultsBucketName", "OutputValue": "b"}]}]}),
        "ecr": Fake(describe_images={"imageDetails": [{}]}),
    }
    clients.update(overrides)
    return clients


def run_test(c, **overrides):
    clients = healthy_clients(**overrides)
    return backends.test_aws_connection(c, session_factory=lambda profile, region: FakeSession(**clients))


def status(result, name):
    return next(item for item in result["checks"] if item["name"] == name)["status"]


class FileTests(unittest.TestCase):
    def test_missing_file_gives_defaults(self):
        with tempfile.TemporaryDirectory() as folder:
            c = connection.load(os.path.join(folder, "none.json"))
        self.assertFalse(c["_exists"])
        self.assertEqual(c["aws"]["resourcePrefix"], "robot-vacuum-sim")
        self.assertEqual(connection.validate(c), [])

    def test_round_trip_keeps_unknown_sections_and_never_holds_secrets(self):
        with tempfile.TemporaryDirectory() as folder:
            path = os.path.join(folder, "c.json")
            c = conn(aws__profile="vacuum")
            c["tool"]["python"] = "py"
            connection.save(c, path)
            again = connection.load(path)
            text = read(path)
        self.assertEqual(again["aws"]["profile"], "vacuum")
        self.assertEqual(again["tool"]["python"], "py")
        self.assertNotIn("_exists", text)
        for secret in ("aws_secret_access_key", "secretAccessKey", "sessionToken", "password"):
            self.assertNotIn(secret.lower(), text.lower())

    def test_default_path_follows_the_platform(self):
        self.assertEqual(connection.default_path({"VACUUM_CLOUD_CONNECTION": "x.json"}), "x.json")
        path = connection.default_path({"XDG_CONFIG_HOME": "/cfg", "APPDATA": "C:/AppData"})
        self.assertTrue(path.endswith(os.path.join("RobotVacuumSim", "cloud-connection.json")))

    def test_broken_file_is_a_plain_error(self):
        with tempfile.TemporaryDirectory() as folder:
            path = os.path.join(folder, "c.json")
            with open(path, "w") as handle:
                handle.write("{ nope")
            with self.assertRaises(ValueError):
                connection.load(path)


class ValidationTests(unittest.TestCase):
    def errors(self, **changes):
        return connection.validate(conn(**changes), check_files=False)

    def test_network_rules(self):
        self.assertTrue(self.errors(network__vpcCidr="10.0.0.0/8"))       # too big
        self.assertTrue(self.errors(network__vpcCidr="10.0.0.0/24"))      # too small for two /24 subnets
        self.assertTrue(self.errors(network__vpcCidr="8.8.0.0/16"))       # public range
        self.assertTrue(self.errors(network__vpcCidr="10.0.0.1/16"))      # host bits set
        self.assertFalse(self.errors(network__vpcCidr="172.20.0.0/20"))
        self.assertTrue(self.errors(network__mode="existingVpc"))         # no IDs
        self.assertFalse(self.errors(network__mode="existingVpc", network__existingVpcId="vpc-0abc12345",
                                     network__existingSubnetIds=["subnet-0abc12345", "subnet-0def67890"]))
        self.assertTrue(self.errors(network__mode="existingVpc", network__existingVpcId="vpc-0abc12345",
                                    network__existingSubnetIds=["subnet-0abc12345", "subnet-0abc12345"]))
        self.assertTrue(self.errors(network__httpsProxy="http://user:pw@proxy:8080"))  # no credentials in the file
        self.assertFalse(self.errors(network__httpsProxy="http://proxy.corp:8080"))

    def test_account_rules(self):
        self.assertTrue(self.errors(aws__region="Ohio"))
        self.assertTrue(self.errors(aws__resourcePrefix="Acme"))
        self.assertTrue(self.errors(aws__resourcePrefix="acme-"))
        self.assertTrue(self.errors(aws__profile="my profile"))
        self.assertTrue(self.errors(aws__signIn="credentialsFile"))
        self.assertTrue(self.errors(provider="azure"))
        self.assertFalse(self.errors(aws__region="us-gov-west-1", aws__resourcePrefix="acme-vacuum"))

    def test_compute_rules(self):
        self.assertTrue(self.errors(compute__maxVCpus=0))
        self.assertTrue(self.errors(compute__maxVCpus=True))
        self.assertTrue(self.errors(compute__nodeTimeoutSeconds=30))
        self.assertTrue(self.errors(compute__instanceTypes="c6i.large; rm -rf"))
        self.assertFalse(self.errors(compute__instanceTypes="c6i.large, c7i.xlarge"))


class NetworkParameterTests(unittest.TestCase):
    def test_new_vpc_with_and_without_internet(self):
        self.assertEqual(connection.network_parameters(conn()),
                         {"NetworkMode": "NewVpc", "VpcCidr": "10.42.0.0/16", "NodeInternetAccess": "true"})
        self.assertEqual(connection.network_parameters(conn(network__nodeInternetAccess=False))["NodeInternetAccess"], "false")

    def test_existing_vpc(self):
        params = connection.network_parameters(conn(network__mode="existingVpc", network__existingVpcId="vpc-0abc12345",
                                                    network__existingSubnetIds=["subnet-0abc12345", "subnet-0def67890"]))
        self.assertEqual(params, {"NetworkMode": "ExistingVpc", "ExistingVpcId": "vpc-0abc12345",
                                  "ExistingSubnetIds": "subnet-0abc12345,subnet-0def67890"})

    def test_parameters_exist_in_the_template(self):
        template = read(os.path.join(CLOUD, "infra", "compute.yaml"))
        for key in ("NetworkMode", "VpcCidr", "NodeInternetAccess", "ExistingVpcId", "ExistingSubnetIds"):
            self.assertIn(f"\n  {key}:\n", template)
        # Private network: no internet gateway and every endpoint the nodes need.
        for service in backends.ENDPOINT_SERVICES:
            self.assertIn(f'.{service}"', template)
        self.assertRegex(template, r"InternetGateway:\n    Type: AWS::EC2::InternetGateway\n    Condition: PublicNodes")

    def test_flags_override_the_file(self):
        args = cli.main.__globals__["argparse"].Namespace(network="existing", vpc_id="vpc-0abc12345",
                                                          subnet_ids="subnet-0abc12345, subnet-0def67890", node_internet=None)
        c = cli.apply_overrides(conn(), args)
        self.assertEqual(c["network"]["mode"], "existingVpc")
        self.assertEqual(c["network"]["existingSubnetIds"], ["subnet-0abc12345", "subnet-0def67890"])


class PolicyTests(unittest.TestCase):
    def test_placeholders_filled_and_scoped_to_the_prefix(self):
        doc = policy.render("acme-vacuum", "us-east-2", "111122223333")
        text = json.dumps(doc)
        self.assertNotRegex(text, r"\$\{(Project|Region|AccountId|Partition)\}")
        self.assertIn("${aws:username}", text)  # IAM's own variable stays
        for statement in doc["Statement"]:
            for resource in statement["Resource"] if isinstance(statement["Resource"], list) else [statement["Resource"]]:
                if resource != "*":
                    self.assertTrue("acme-vacuum" in resource or "aws-service-role" in resource or "oidc-provider" in resource
                                    or "vacuum-sim" in resource or "${aws:username}" in resource or "aws:policy" in resource,
                                    resource)
            if statement["Resource"] == "*" and statement["Sid"] not in ("ValidateTemplates", "RegistrySignIn", "ListLogGroups"):
                self.assertEqual(statement["Condition"]["StringEquals"]["aws:RequestedRegion"], "us-east-2")

    def test_partitions(self):
        self.assertIn("arn:aws-us-gov:", json.dumps(policy.render("p-1", "us-gov-west-1")))
        self.assertIn("arn:aws-cn:", json.dumps(policy.render("p-1", "cn-north-1")))

    def test_fits_the_managed_policy_size_limit(self):
        text = json.dumps(policy.render("a" * 32, "us-gov-west-1", "111122223333"))
        self.assertLess(len(re.sub(r"\s", "", text)), 6144)


class ConnectionTestTests(unittest.TestCase):
    def test_every_simulated_resource_is_an_arn_or_star(self):
        for actions, resource, _ in policy.probes("acme-vacuum", "us-east-2", "111122223333", "kristie-cli", "real-bucket"):
            self.assertTrue(resource == "*" or resource.startswith("arn:"), resource)
            self.assertFalse(any(a.endswith(":*") for a in actions), actions)  # wildcards are expanded

    def test_probes_cover_every_action_on_the_real_resource_names(self):
        probes = policy.probes("acme-vacuum", "us-east-2", "111122223333", "kristie-cli", "acme-vacuum-base-resultsbucket-x1")
        probed = {a for actions, _, _ in probes for a in actions}
        for statement in policy.render("acme-vacuum", "us-east-2", "111122223333")["Statement"]:
            if statement["Sid"] == "TestConnectionPermissionCheck":
                continue
            for action in statement["Action"]:
                expected = policy.WILDCARD_ACTIONS.get(action, [action])
                self.assertTrue(set(expected) <= probed, action)
        by_action = {a: r for actions, r, _ in probes for a in actions}
        self.assertTrue(by_action["cloudformation:DescribeStacks"].endswith(":stack/acme-vacuum-base/id"))
        self.assertEqual(by_action["s3:ListBucket"], "arn:aws:s3:::acme-vacuum-base-resultsbucket-x1")
        self.assertTrue(by_action["s3:PutObject"].startswith("arn:aws:s3:::acme-vacuum-base-resultsbucket-x1/campaigns/"))
        self.assertIn(":instance-profile/", by_action["iam:CreateInstanceProfile"])
        self.assertIn(":role/", by_action["iam:CreateRole"])
        self.assertIn(":log-group:/acme-vacuum/batch:log-stream:", by_action["logs:GetLogEvents"])
        self.assertNotIn(":log-stream:", by_action["logs:CreateLogGroup"])
        self.assertNotIn("cloudformation:ValidateTemplate", probed)  # vacuumctl never calls it

    def test_conditions_are_passed_as_context(self):
        probes = policy.probes("acme-vacuum", "us-east-2", "111122223333")
        context = {a: c for actions, _, c in probes for a in actions}
        self.assertEqual(context["iam:AttachRolePolicy"][0]["ContextKeyName"], "iam:PolicyARN")
        self.assertEqual(context["ec2:CreateVpc"][0]["ContextKeyValues"], ["us-east-2"])

    def test_denied_details_name_action_and_resource(self):
        iam = Fake(simulate_principal_policy=simulate_like_aws(lambda a: a != "s3:PutObject"))
        result = run_test(conn(), iam=iam)
        permissions = next(c for c in result["checks"] if c["name"] == "Permissions")
        self.assertEqual(permissions["details"],
                         ["not allowed: s3:PutObject on arn:aws:s3:::b/campaigns/probe/job.json (no policy allows it)"])

    def test_requests_carry_region_and_https_context(self):
        seen = []
        iam = Fake(simulate_principal_policy=lambda **r: seen.append(r["ContextEntries"]) or simulate_like_aws()(**r))
        run_test(conn(), iam=iam)
        for entries in seen:
            names = [e["ContextKeyName"] for e in entries]
            self.assertIn("aws:SecureTransport", names)
            self.assertEqual(names.count("aws:RequestedRegion"), 1)

    def test_missing_context_is_a_note_not_a_failure(self):
        def simulate(**request):
            return {"EvaluationResults": [{"EvalActionName": a, "EvalDecision": "implicitDeny",
                                           "MissingContextValues": ["aws:SourceIp"]} if a.startswith("ecr:") else
                                          {"EvalActionName": a, "EvalDecision": "allowed"} for a in request["ActionNames"]]}
        result = run_test(conn(), iam=Fake(simulate_principal_policy=simulate))
        self.assertTrue(result["ok"])
        permissions = next(c for c in result["checks"] if c["name"] == "Permissions")
        self.assertEqual(permissions["status"], "warn")
        self.assertIn("aws:SourceIp", permissions["message"])

    def test_explicit_deny_names_the_policy(self):
        def simulate(**request):
            return {"EvaluationResults": [{"EvalActionName": a, "EvalDecision": "explicitDeny",
                                           "MatchedStatements": [{"SourcePolicyId": "DenyWithoutMfa"}]}
                                          for a in request["ActionNames"]]}
        result = run_test(conn(), iam=Fake(simulate_principal_policy=simulate))
        permissions = next(c for c in result["checks"] if c["name"] == "Permissions")
        self.assertIn("explicitly denied; matched DenyWithoutMfa", permissions["details"][0])

    def test_healthy_connection(self):
        result = run_test(conn())
        self.assertTrue(result["ok"], result)
        self.assertEqual(result["account"], "111122223333")
        self.assertEqual({c["status"] for c in result["checks"]}, {"ok"})

    def test_bad_settings_stop_before_any_aws_call(self):
        result = backends.test_aws_connection(conn(aws__region="nowhere"), session_factory=lambda *a: self.fail("called AWS"))
        self.assertFalse(result["ok"])
        self.assertEqual(status(result, "Settings"), "fail")

    def test_expired_sso_sign_in_is_explained(self):
        UnauthorizedSSOTokenError = type("UnauthorizedSSOTokenError", (Exception,), {})

        def fail(**_):
            raise UnauthorizedSSOTokenError("expired")
        result = run_test(conn(aws__signIn="sso"), sts=Fake(get_caller_identity=fail))
        self.assertEqual(status(result, "Sign-in"), "fail")
        self.assertIn("Sign in (SSO)", result["checks"][-1]["message"])

    def test_missing_permissions_are_listed(self):
        iam = Fake(simulate_principal_policy=simulate_like_aws(lambda a: not a.startswith("batch:")))
        result = run_test(conn(), iam=iam)
        self.assertFalse(result["ok"])
        message = next(c for c in result["checks"] if c["name"] == "Permissions")["message"]
        self.assertIn("batch:", message)
        self.assertIn("admin policy", message)

    def test_permission_check_that_cant_run_is_only_a_warning(self):
        def deny(**_):
            raise ClientError("AccessDenied")
        result = run_test(conn(), iam=Fake(simulate_principal_policy=deny))
        self.assertTrue(result["ok"])
        self.assertEqual(status(result, "Permissions"), "warn")

    def test_assumed_role_is_checked_as_its_role(self):
        seen = []
        iam = Fake(get_role=lambda **r: {"Role": {"Arn": f"arn:aws:iam::111122223333:role/team/{r['RoleName']}"}},
                   simulate_principal_policy=lambda **r: seen.append(r["PolicySourceArn"]) or {"EvaluationResults": []})
        sts = Fake(get_caller_identity={"Account": "111122223333",
                                        "Arn": "arn:aws:sts::111122223333:assumed-role/vacuum-sim/kristie"})
        run_test(conn(), iam=iam, sts=sts)
        self.assertEqual(set(seen), {"arn:aws:iam::111122223333:role/team/vacuum-sim"})

    def test_not_set_up_yet_is_a_warning(self):
        def missing(**_):
            raise ClientError("ValidationError", "Stack with id robot-vacuum-sim-base does not exist")
        result = run_test(conn(), cloudformation=Fake(describe_stacks=missing))
        self.assertTrue(result["ok"])
        self.assertEqual(status(result, "Cloud resources"), "warn")

    def test_vpc_limit_warning_for_new_vpcs(self):
        result = run_test(conn(), ec2=Fake(describe_availability_zones={"AvailabilityZones": [{"ZoneName": "a"}, {"ZoneName": "b"}]},
                                           describe_vpcs={"Vpcs": [{}] * 5}))
        self.assertEqual(status(result, "Network"), "warn")

    def existing(self, subnets, route_tables, endpoints=()):
        c = conn(network__mode="existingVpc", network__existingVpcId="vpc-0abc12345",
                 network__existingSubnetIds=[s["SubnetId"] for s in subnets])
        ec2 = Fake(describe_availability_zones={"AvailabilityZones": [{"ZoneName": "a"}, {"ZoneName": "b"}]},
                   describe_vpcs={"Vpcs": [{}]},
                   describe_subnets={"Subnets": subnets},
                   describe_vpc_endpoints={"VpcEndpoints": [{"ServiceName": f"com.amazonaws.us-east-2.{s}"} for s in endpoints]},
                   describe_route_tables=lambda **r: {"RouteTables": route_tables.get(r["Filters"][0]["Values"][0], [])})
        return run_test(c, ec2=ec2)

    def test_existing_vpc_with_nat_is_fine(self):
        subnets = [{"SubnetId": "subnet-0aaa11111", "VpcId": "vpc-0abc12345", "AvailabilityZone": "a"},
                   {"SubnetId": "subnet-0bbb22222", "VpcId": "vpc-0abc12345", "AvailabilityZone": "b"}]
        nat = [{"Routes": [{"DestinationCidrBlock": "0.0.0.0/0", "NatGatewayId": "nat-1"}]}]
        result = self.existing(subnets, {"subnet-0aaa11111": nat, "subnet-0bbb22222": nat})
        self.assertEqual(status(result, "Network"), "ok", result)

    def test_existing_private_subnet_without_endpoints_fails(self):
        subnets = [{"SubnetId": "subnet-0aaa11111", "VpcId": "vpc-0abc12345", "AvailabilityZone": "a"}]
        result = self.existing(subnets, {"subnet-0aaa11111": [{"Routes": [{"DestinationCidrBlock": "10.0.0.0/16"}]}]},
                               endpoints=("s3", "ecr.api"))
        self.assertEqual(status(result, "Network"), "fail")
        self.assertIn("ecs-agent", next(c for c in result["checks"] if c["name"] == "Network")["message"])

    def test_existing_private_subnet_with_all_endpoints_passes(self):
        subnets = [{"SubnetId": "subnet-0aaa11111", "VpcId": "vpc-0abc12345", "AvailabilityZone": "a"},
                   {"SubnetId": "subnet-0bbb22222", "VpcId": "vpc-0abc12345", "AvailabilityZone": "b"}]
        local = [{"Routes": [{"DestinationCidrBlock": "10.0.0.0/16"}]}]
        result = self.existing(subnets, {"subnet-0aaa11111": local, "subnet-0bbb22222": local},
                               endpoints=backends.ENDPOINT_SERVICES)
        self.assertEqual(status(result, "Network"), "ok", result)

    def test_igw_subnet_without_public_ips_fails(self):
        subnets = [{"SubnetId": "subnet-0aaa11111", "VpcId": "vpc-0abc12345", "AvailabilityZone": "a",
                    "MapPublicIpOnLaunch": False}]
        result = self.existing(subnets, {"subnet-0aaa11111": [{"Routes": [{"DestinationCidrBlock": "0.0.0.0/0",
                                                                           "GatewayId": "igw-1"}]}]})
        self.assertEqual(status(result, "Network"), "fail")


class SettingsPageContractTests(unittest.TestCase):
    """The simulator's Settings page (CloudConnection.cs) and this tool must agree on the file (4.9-style contract)."""

    @classmethod
    def setUpClass(cls):
        if not os.path.isfile(CS_FILE):
            raise unittest.SkipTest("CloudConnection.cs not found")
        cls.source = read(CS_FILE)

    def cs_constant(self, name):
        match = re.search(name + r'\s*=\s*@"([^"]*)"', self.source)
        self.assertIsNotNone(match, name)
        return match.group(1)

    def test_validation_patterns_match(self):
        for cs_name, py_value in (("RegionPattern", connection.REGION_PATTERN), ("PrefixPattern", connection.PREFIX_PATTERN),
                                  ("VpcIdPattern", connection.VPC_ID_PATTERN), ("SubnetIdPattern", connection.SUBNET_ID_PATTERN),
                                  ("ProxyPattern", connection.PROXY_PATTERN),
                                  ("InstanceTypesPattern", connection.INSTANCE_TYPES_PATTERN)):
            self.assertEqual(self.cs_constant(cs_name), py_value, cs_name)

    def test_field_names_match(self):
        def keys(d):
            for k, v in d.items():
                yield k
                if isinstance(v, dict):
                    yield from keys(v)
        for key in keys(connection.DEFAULTS):
            self.assertRegex(self.source, rf"public [\w<>\[\]]+ {key}\b", key)

    def test_defaults_and_file_location_match(self):
        self.assertIn(f'"{connection.FILE_NAME}"', self.source)
        self.assertIn(f'"{connection.APP_FOLDER}"', self.source)
        self.assertIn(f'"{connection.DEFAULTS["network"]["vpcCidr"]}"', self.source)
        self.assertIn(f'"{connection.DEFAULTS["aws"]["resourcePrefix"]}"', self.source)


if __name__ == "__main__":
    unittest.main()
