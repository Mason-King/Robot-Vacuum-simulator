"""vacuumctl - send Robot Vacuum Simulator campaigns to AWS Batch and bring the results back.

This is the control-side component of requirement 3.9: it talks to the headless simulation nodes across
the network boundary only through the job file (in) and results files (out) in S3, plus the AWS Batch API.
"""

__version__ = "1.0.0"
