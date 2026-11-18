"""Requirement 2.8 statistics and the import of campaign results into the simulator's Analysis page (2.18, 4.6, 4.9).

The simulator's side (BatchStats.cs, CampaignImport.cs) is C#; these tests pin the contract between the two: the same
statistics for the same input, the same CSV columns, and every field the importer reads present in what nodes write.
"""

import csv
import io
import math
import os
import re
import tempfile
import unittest

from vacuumctl import aws_ops, report, stats

ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..")
SCRIPTS = os.path.join(ROOT, "Robot vacuum simulator", "Assets", "Scripts")


def read(*parts):
    with open(os.path.join(*parts), encoding="utf-8-sig") as handle:
        return handle.read()


def cs_fields(source, class_name, end_marker):
    body = source[source.index(f"class {class_name}"):]
    body = body[:body.index(end_marker)] if end_marker in body else body
    return re.findall(r"public (?!static|const|readonly)[\w<>\[\]]+ (\w+)(?: = [^;]+)?;", body)


def campaign(values_a, values_b, failed_b=False):
    """A campaign_results.json with two algorithms on one condition."""
    runs = []
    for i, v in enumerate(values_a):
        runs.append({"runIndex": i, "floorPlan": "Two rooms", "configuration": "short", "algorithm": "BoustrophedonSweep",
                     "algorithmName": "Boustrophedon Sweep", "seed": i + 1, "succeeded": True, "coveragePercent": v,
                     "simulatedSeconds": 100.0 + i, "batteryUsed": 50.0 + 2 * i, "distanceMeters": 10.0, "revisitCount": 5,
                     "timeTo90": -1, "endReason": "TimeCap"})
    for i, v in enumerate(values_b):
        runs.append({"runIndex": 100 + i, "floorPlan": "Two rooms", "configuration": "short", "algorithm": "RandomBounce",
                     "algorithmName": "Random", "seed": i + 1, "succeeded": not (failed_b and i == 0), "coveragePercent": v,
                     "simulatedSeconds": 90.0, "batteryUsed": 40.0, "distanceMeters": 9.0, "revisitCount": 7,
                     "timeTo90": 80.0, "endReason": "TimeCap"})
    job = {"campaignId": "c-1", "campaignName": "test", "runs": [{"runIndex": r["runIndex"]} for r in runs]}
    return stats.aggregate(job, [{"shardIndex": 0, "runs": runs}])


class SharedStatisticsFixture(unittest.TestCase):
    """The same numbers are asserted in the simulator's CloudConnection/ExperimentTests (C#): [80, 82, 84, 86]."""

    def test_reference_values(self):
        s = stats.summarise([80, 82, 84, 86])
        self.assertAlmostEqual(s["mean"], 83.0)
        self.assertAlmostEqual(s["variance"], 6.666666666666667)
        self.assertAlmostEqual(s["stdDev"], 2.581988897471611)
        self.assertAlmostEqual(s["ci95HalfWidth"], 3.182 * math.sqrt(6.666666666666667 / 4))
        self.assertAlmostEqual(s["ci95Low"], 83.0 - 4.107944335877334, places=9)

    def test_csharp_test_uses_the_same_fixture(self):
        source = read(ROOT, "Robot vacuum simulator", "Assets", "Tests", "EditMode", "CampaignImportTests.cs")
        self.assertIn("80, 82, 84, 86", source)
        self.assertIn("6.666666666666667", source)
        self.assertIn("4.107944335877334", source)


class CsvTests(unittest.TestCase):
    def test_columns_match_the_simulators_export(self):
        source = read(SCRIPTS, "LevelEditor", "BatchStats.cs")
        self.assertIn('"' + ",".join(stats.RUN_COLUMNS) + '"', source)
        self.assertIn('"' + ",".join(stats.SUMMARY_COLUMNS) + '"', source)

    def test_rows_and_statistics(self):
        results = campaign([80, 82, 84, 86], [50, 60, 70], failed_b=True)
        with tempfile.TemporaryDirectory() as folder:
            path = stats.write_runs_csv(results, os.path.join(folder, "campaign_runs.csv"))
            with open(path, encoding="utf-8", newline="") as handle:
                rows = list(csv.reader(handle))
        runs = [r for r in rows if r and r[0] == "run"]
        summaries = [dict(zip(stats.SUMMARY_COLUMNS, r)) for r in rows if r and r[0] == "summary"]
        self.assertEqual(len(runs), 7)
        self.assertEqual(sum(1 for r in runs if r[3] == "false"), 1)
        sweep = next(s for s in summaries if s["algorithm"] == "Boustrophedon Sweep" and s["measure"] == "coverage_percent")
        self.assertEqual(sweep["n"], "4")
        self.assertAlmostEqual(float(sweep["variance"]), 6.666666666666667)
        self.assertAlmostEqual(float(sweep["t_critical"]), 3.182)
        random_cov = next(s for s in summaries if s["algorithm"] == "Random" and s["measure"] == "coverage_percent")
        self.assertEqual(random_cov["n"], "2", "the failed run is left out of the statistics")
        self.assertEqual({s["measure"] for s in summaries}, {"coverage_percent", "time_s", "battery_used"})


class ReportTests(unittest.TestCase):
    def test_statistics_table_has_mean_variance_sd_and_ci_for_the_three_measures(self):
        results = campaign([80, 82, 84, 86], [50, 60, 70])
        html = report.summary_statistics_table(results["groups"])
        for heading in ("Mean", "Variance", "SD", "95% CI", "Coverage (%)", "Time (s)", "Battery used"):
            self.assertIn(heading, html)
        self.assertIn("6.67", html)          # the sweep's coverage variance, 2 decimals
        self.assertIn("78.89 – 87.11", html)  # 83 ± 4.108

    def test_full_report_includes_it(self):
        html = report.build_html(campaign([80, 82, 84, 86], [50, 60, 70]))
        self.assertIn("<h3>Statistics</h3>", html)
        self.assertIn("campaign_runs.csv", html)


class FetchFilesTests(unittest.TestCase):
    def test_only_the_small_per_run_files(self):
        self.assertTrue(aws_ops.is_app_run_file("shards/shard-0001/runs/abc123/summary.json"))
        self.assertTrue(aws_ops.is_app_run_file("shards/shard-0000/runs/abc123/replay.json"))
        self.assertFalse(aws_ops.is_app_run_file("shards/shard-0000/runs/abc123/external_model.json"))
        self.assertFalse(aws_ops.is_app_run_file("shards/shard-0000/runs/abc123/heatmap_floor_coverage.png"))
        self.assertFalse(aws_ops.is_app_run_file("shards/shard-0000/results.json"))
        self.assertFalse(aws_ops.is_app_run_file("job.json"))


class ImportContractTests(unittest.TestCase):
    """What CampaignImport.cs reads must exist in what the nodes (HeadlessResults.cs) and vacuumctl (stats.py) write."""

    @classmethod
    def setUpClass(cls):
        cls.importer = read(SCRIPTS, "LevelEditor", "CampaignImport.cs")
        cls.node = read(SCRIPTS, "Headless", "HeadlessResults.cs")

    def test_outcome_fields_come_from_the_nodes_run_outcome(self):
        read_fields = cs_fields(self.importer, "Outcome", "/// <summary>A campaign folder")
        written = cs_fields(self.node, "RunOutcome", "[Serializable]\n    public sealed class RoomOutcome")
        self.assertTrue(read_fields)
        self.assertEqual([f for f in read_fields if f not in written], [])

    def test_results_file_fields_come_from_vacuumctl(self):
        read_fields = cs_fields(self.importer, "ResultsFile", "/// <summary>One run's outcome")
        written = campaign([1, 2], [3])
        self.assertEqual([f for f in read_fields if f not in written], [])
        self.assertEqual(written["schemaVersion"], 1)
        self.assertIn("schemaVersion != 1", self.importer)


if __name__ == "__main__":
    unittest.main()
