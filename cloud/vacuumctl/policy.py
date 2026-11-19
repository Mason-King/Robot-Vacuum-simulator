"""The least-privilege IAM policy an organisation's IT admin attaches to the user or role that runs campaigns (C.12).

The template is cloud/infra/vacuum-sim-user-policy.template.json; ${Project}, ${Region}, ${AccountId} and
${Partition} are filled in here. ${aws:username} is an IAM policy variable and is left as it is.

`probes` turns the policy into the permission checks `test-connection` runs: every action, against the real
resource names vacuumctl and the templates use (the two stacks, the results bucket, the roles CloudFormation names
after the stacks, the log group), so an account whose own policy names those exact resources passes too.
"""

import json
import os

TEMPLATE = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "infra",
                        "vacuum-sim-user-policy.template.json")

# What the wildcard statements stand for: the actions vacuumctl and CloudFormation actually call on them.
WILDCARD_ACTIONS = {
    "s3:*": ["s3:CreateBucket", "s3:DeleteBucket", "s3:ListBucket", "s3:ListBucketVersions", "s3:PutBucketPolicy",
             "s3:DeleteBucketPolicy", "s3:PutEncryptionConfiguration", "s3:PutLifecycleConfiguration",
             "s3:PutBucketPublicAccessBlock", "s3:PutBucketOwnershipControls", "s3:PutBucketTagging",
             "s3:GetObject", "s3:PutObject", "s3:DeleteObject", "s3:DeleteObjectVersion"],
    "ecr:*": ["ecr:CreateRepository", "ecr:DeleteRepository", "ecr:DescribeRepositories", "ecr:DescribeImages",
              "ecr:PutLifecyclePolicy", "ecr:PutImageScanningConfiguration", "ecr:TagResource",
              "ecr:BatchCheckLayerAvailability", "ecr:InitiateLayerUpload", "ecr:UploadLayerPart",
              "ecr:CompleteLayerUpload", "ecr:PutImage", "ecr:BatchGetImage", "ecr:BatchDeleteImage"],
}
S3_OBJECT_ACTIONS = {"s3:GetObject", "s3:PutObject", "s3:DeleteObject", "s3:DeleteObjectVersion"}


def partition_of(region):
    if region.startswith("cn-"):
        return "aws-cn"
    if region.startswith("us-gov-"):
        return "aws-us-gov"
    return "aws"


def render(project, region, account="*", template_path=TEMPLATE):
    with open(template_path, encoding="utf-8") as handle:
        text = handle.read()
    for key, value in (("${Project}", project), ("${Region}", region), ("${AccountId}", account or "*"),
                       ("${Partition}", partition_of(region))):
        text = text.replace(key, value)
    return json.loads(text)


def probe_resource(resource, username="probe"):
    """A concrete ARN for a policy resource pattern. A bare "*" stays "*" (the simulator reads it as any resource)."""
    if resource == "*":
        return "*"
    return resource.replace("${aws:username}", username).replace("*", "probe")


def probes(project, region, account, username="probe", bucket=None):
    """[(actions, resource ARN, context entries)]: what to simulate so every action in the policy is checked
    against a resource it will really be used on."""
    arn = f"arn:{partition_of(region)}"
    bucket = bucket or f"{project}-base-resultsbucket-probe"
    real = {
        "StacksNamedForThisProject": [f"{arn}:cloudformation:{region}:{account}:stack/{project}-base/id",
                                      f"{arn}:cloudformation:{region}:{account}:stack/{project}-compute/id"],
        "ResultsBucket": [f"{arn}:s3:::{bucket}", f"{arn}:s3:::{bucket}/campaigns/probe/job.json"],
        "ImageRegistry": [f"{arn}:ecr:{region}:{account}:repository/{project}-headless"],
        "RolesForThisProject": [f"{arn}:iam::{account}:role/{project}-compute-JobRole-probe",
                                f"{arn}:iam::{account}:instance-profile/{project}-compute-InstanceProfile-probe"],
        "OnlyTheEcsInstancePolicyMayBeAttached": [f"{arn}:iam::{account}:role/{project}-compute-InstanceRole-probe"],
        "ServiceLinkedRolesBatchNeeds": [f"{arn}:iam::{account}:role/aws-service-role/batch.amazonaws.com/AWSServiceRoleForBatch"],
        "NodeLogs": [f"{arn}:logs:{region}:{account}:log-group:/{project}/batch",
                     f"{arn}:logs:{region}:{account}:log-group:/{project}/batch:log-stream:node/headless/probe"],
    }
    result = []
    for statement in render(project, region, account)["Statement"]:
        sid = statement["Sid"]
        if sid == "TestConnectionPermissionCheck":
            continue  # already proven: the check itself is running
        actions = []
        for action in statement["Action"]:
            actions += WILDCARD_ACTIONS.get(action, [action])
        patterns = statement["Resource"] if isinstance(statement["Resource"], list) else [statement["Resource"]]
        resources = real.get(sid) or [probe_resource(patterns[0], username)]
        context = [{"ContextKeyName": key, "ContextKeyValues": values if isinstance(values, list) else [values],
                    "ContextKeyType": "string"}
                   for test in (statement.get("Condition") or {}).values() for key, values in test.items()]
        context = [dict(entry, ContextKeyValues=entry["ContextKeyValues"][:1]) for entry in context]

        groups = {}
        for action in actions:
            groups.setdefault(_resource_for(action, resources), []).append(action)
        for resource, grouped in groups.items():
            result.append((grouped, resource, context))
    return result


def _resource_for(action, resources):
    """The probe resource of the right type for an action (an object vs. a bucket, an instance profile vs. a role)."""
    if action in S3_OBJECT_ACTIONS:
        return next((r for r in resources if r.count("/") >= 1 and ":s3:::" in r), resources[0])
    if action.startswith("s3:"):
        return next((r for r in resources if ":s3:::" in r and "/" not in r), resources[0])
    if action == "logs:GetLogEvents":  # acts on a log stream, not the group
        return next((r for r in resources if ":log-stream:" in r), resources[0])
    if action.startswith("logs:"):
        return next((r for r in resources if ":log-stream:" not in r), resources[0])
    if action.startswith("iam:") and "InstanceProfile" in action:
        return next((r for r in resources if ":instance-profile/" in r), resources[0])
    if action.startswith("iam:"):
        return next((r for r in resources if ":role/" in r), resources[0])
    return resources[0]
