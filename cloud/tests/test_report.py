import os
import tempfile
import unittest

from vacuumctl import report, stats


class ReportTests(unittest.TestCase):
    def test_report_builds_and_escapes_names(self):
        job = {"campaignId": "c1", "campaignName": "<b>n</b>", "runs": [{"runIndex": i} for i in range(4)],
               "execution": {"shardCount": 2}, "configurations": [{"name": "x", "vacuum": {"driveSpeed": 0.65}}],
               "floorPlans": [{"name": "A", "rooms": [{"outline": [{"x": 0, "y": 0}, {"x": 2, "y": 0}, {"x": 2, "y": 2}]}]}]}
        runs = [{"runIndex": i, "floorPlan": "A", "configuration": "x", "algorithm": alg, "seed": i, "succeeded": True,
                 "coveragePercent": 50 + i, "simulatedSeconds": 100, "realSeconds": 1, "runFolder": f"runs/{i}"}
                for i, alg in enumerate(["Spiral", "Spiral", "Lawnmower", "Lawnmower"])]
        results = stats.aggregate(job, [{"shardIndex": 0, "runs": runs}])
        page = report.build_html(results, job, base_dir=tempfile.gettempdir())
        self.assertIn("&lt;b&gt;n&lt;/b&gt;", page)
        self.assertNotIn("<b>n</b>", page)
        self.assertIn("Lawnmower", page)
        self.assertIn("<svg", page)
        self.assertEqual(report.polygon_area(job["floorPlans"][0]["rooms"][0]["outline"]), 2.0)

    def test_algorithm_colour_is_fixed(self):
        self.assertEqual(report.slot_of("RandomBounce"), 1)
        self.assertEqual(report.slot_of("BoustrophedonSweep"), 2)
        self.assertEqual(report.slot_of("Legacy.Lawnmower"), 6)
        self.assertEqual(report.slot_of("Lawnmower"), 6, "results from older builds keep the same colour")
        self.assertIn(report.slot_of("Mine.Custom"), (7, 8), "custom algorithms never take a built-in's colour")


if __name__ == "__main__":
    unittest.main()
