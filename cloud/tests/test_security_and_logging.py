"""Trust boundary (4.8, C.12) and the network/infrastructure log (SDD 8). No AWS or Docker needed."""

import importlib.util
import os
import subprocess
import sys
import tempfile
import unittest

from vacuumctl import __main__ as cli
from vacuumctl import aws_ops
from vacuumctl import campaign

CLOUD = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def load_entrypoint():
    spec = importlib.util.spec_from_file_location("entrypoint", os.path.join(CLOUD, "docker", "entrypoint.py"))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


class SimulatorEnvironmentTests(unittest.TestCase):
    """AT-4.8 (container part): the simulator, which may run custom algorithm code, gets no AWS credentials."""

    def test_credential_and_metadata_variables_are_removed(self):
        entrypoint = load_entrypoint()
        env = entrypoint.simulator_environment({
            "PATH": "/usr/bin",
            "HOME": "/home/vacuum",
            "AWS_CONTAINER_CREDENTIALS_RELATIVE_URI": "/v2/credentials/abc",
            "AWS_CONTAINER_CREDENTIALS_FULL_URI": "http://169.254.170.2/x",
            "AWS_CONTAINER_AUTHORIZATION_TOKEN": "t",
            "AWS_ACCESS_KEY_ID": "AKIA", "AWS_SECRET_ACCESS_KEY": "s", "AWS_SESSION_TOKEN": "t",
            "AWS_PROFILE": "vacuum", "AWS_DEFAULT_REGION": "us-east-2",
            "ECS_CONTAINER_METADATA_URI_V4": "http://169.254.170.2/v4/x",
            "ECS_CONTAINER_METADATA_URI": "http://169.254.170.2/v3/x",
            "AWS_BATCH_JOB_ID": "job-1", "AWS_BATCH_JOB_ARRAY_INDEX": "3",
            "VACUUM_SHARD_COUNT": "4",
        })
        self.assertEqual(env, {"PATH": "/usr/bin", "HOME": "/home/vacuum", "AWS_BATCH_JOB_ID": "job-1",
                               "AWS_BATCH_JOB_ARRAY_INDEX": "3", "VACUUM_SHARD_COUNT": "4"})

    def test_lower_case_variants_are_removed_too(self):
        env = load_entrypoint().simulator_environment({"aws_secret_access_key": "s", "Ecs_Container_Metadata_Uri": "u"})
        self.assertEqual(env, {})

    @unittest.skipUnless(sys.platform.startswith("linux"), "prctl is Linux-only (the container is Linux)")
    def test_entrypoint_hides_itself_from_proc(self):
        # In a child process, so this test process stays dumpable.
        code = ("import ctypes, importlib.util, sys; spec = importlib.util.spec_from_file_location('e', sys.argv[1]); "
                "m = importlib.util.module_from_spec(spec); spec.loader.exec_module(m); ok = m.make_non_dumpable(); "
                "print(ok, ctypes.CDLL(None).prctl(3, 0, 0, 0, 0))")
        out = subprocess.run([sys.executable, "-c", code, os.path.join(CLOUD, "docker", "entrypoint.py")],
                             capture_output=True, text=True, check=True).stdout.split()
        self.assertEqual(out, ["True", "0"])  # PR_GET_DUMPABLE = 0


class ComputeTemplateTests(unittest.TestCase):
    """Checked as text: CloudFormation's !Sub/!Ref tags need a CFN-aware YAML loader."""

    @classmethod
    def setUpClass(cls):
        with open(os.path.join(CLOUD, "infra", "compute.yaml"), encoding="utf-8") as handle:
            cls.template = handle.read()

    def test_containers_cannot_reach_instance_metadata(self):
        self.assertIn("HttpTokens: required", self.template)
        self.assertRegex(self.template, r"HttpPutResponseHopLimit:\s*1\b")
        self.assertIn("LaunchTemplateId: !Ref NodeLaunchTemplate", self.template)

    def test_job_role_is_scoped_to_one_campaign(self):
        self.assertIn("/campaigns/${CampaignScope}/*", self.template)
        self.assertNotRegex(self.template, r"\$\{ResultsBucketName\}/campaigns/\*\"")


class CampaignIdTests(unittest.TestCase):
    def job(self, campaign_id):
        job = campaign.normalise({"campaignId": campaign_id, "floorPlans": [
            {"name": "box", "rooms": [{"name": "r", "outline": [{"x": 0, "y": 0}, {"x": 3, "y": 0}, {"x": 3, "y": 3}]}]}]})
        return [e for e in campaign.validate(job) if e.startswith("campaignId")]

    def test_generated_ids_are_accepted(self):
        self.assertTrue(campaign.CAMPAIGN_ID_PATTERN.match(campaign.new_campaign_id("Baseline vs fast!")))

    def test_ids_that_would_escape_the_prefix_are_rejected(self):
        for bad in ("../other", "a/b", "*", "-lead", "x" * 129, "has space"):
            self.assertTrue(self.job(bad), bad)
        self.assertFalse(self.job("my-campaign_1.2"))


class NetworkLogTests(unittest.TestCase):
    def tearDown(self):
        aws_ops.set_log_file(None)

    def test_messages_go_to_the_campaign_log_file(self):
        with tempfile.TemporaryDirectory() as folder:
            path = os.path.join(folder, "c1", "vacuumctl.log")
            aws_ops.set_log_file(path)
            aws_ops.log("Creating stack ...")
            aws_ops.log("upload failed", error=True)
            with open(path, encoding="utf-8") as handle:
                lines = handle.read().splitlines()
        self.assertEqual(len(lines), 2)
        self.assertRegex(lines[0], r"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ Creating stack \.\.\.$")
        self.assertIn(" ERROR upload failed", lines[1])

    def test_log_path_per_campaign(self):
        class Args:
            out = "cloud-results"
        self.assertEqual(cli.log_path(Args(), "c1"), os.path.join("cloud-results", "c1", "vacuumctl.log"))
        self.assertEqual(cli.log_path(object()), os.path.join("cloud-results", "vacuumctl.log"))

    def test_plain_errors(self):
        NoCredentialsError = type("NoCredentialsError", (Exception,), {})
        EndpointConnectionError = type("EndpointConnectionError", (Exception,), {})
        ClientError = type("ClientError", (Exception,), {})
        refused = ClientError("x")
        refused.response = {"Error": {"Code": "AccessDenied", "Message": "nope"}}
        self.assertIn("aws sso login --profile vacuum", cli.plain_error(NoCredentialsError(), "vacuum"))
        self.assertIn("network problem", cli.plain_error(EndpointConnectionError("down")))
        self.assertEqual(cli.plain_error(refused), "AWS refused the request (AccessDenied): nope")


if __name__ == "__main__":
    unittest.main()
