import math
import unittest

from vacuumctl import stats


class StatsTests(unittest.TestCase):
    def test_summary_matches_hand_calculation(self):
        values = [80.0, 82.0, 84.0, 86.0]
        s = stats.summarise(values)
        self.assertAlmostEqual(s["mean"], 83.0)
        self.assertAlmostEqual(s["variance"], 20.0 / 3.0)            # sample variance
        half = 3.182 * math.sqrt((20.0 / 3.0) / 4)                    # t(0.975, 3) = 3.182
        self.assertAlmostEqual(s["ci95HalfWidth"], half)
        self.assertAlmostEqual(s["ci95Low"], 83.0 - half)

    def test_single_value_and_empty(self):
        self.assertEqual(stats.summarise([5])["variance"], 0.0)
        self.assertIsNone(stats.summarise([])["mean"])

    def test_aggregate_groups_and_missing_runs(self):
        job = {"campaignId": "c", "campaignName": "n", "runs": [{"runIndex": i} for i in range(5)]}
        def run(i, alg, cov, ok=True):
            return {"runIndex": i, "floorPlan": "A", "configuration": "x", "algorithm": alg, "seed": i,
                    "succeeded": ok, "coveragePercent": cov, "timeTo90": -1}
        shards = [{"shardIndex": 0, "runs": [run(0, "Spiral", 50), run(2, "Spiral", 70)]},
                  {"shardIndex": 1, "runs": [run(1, "Lawnmower", 90), run(3, "Lawnmower", 0, ok=False)]}]
        result = stats.aggregate(job, shards)
        self.assertEqual(result["totals"]["missingRuns"], [4])
        self.assertEqual(result["totals"]["failedRuns"], 1)
        spiral = next(g for g in result["groups"] if g["algorithm"] == "Spiral")
        self.assertAlmostEqual(spiral["metrics"]["coveragePercent"]["mean"], 60.0)
        self.assertEqual(spiral["metrics"]["timeTo90"]["count"], 0, "never-reached (-1) values are excluded")
        lawn = next(g for g in result["groups"] if g["algorithm"] == "Lawnmower")
        self.assertEqual(lawn["metrics"]["coveragePercent"]["count"], 1, "failed runs are excluded from statistics")
        self.assertIn("Spiral", stats.format_table(result))

    def test_t_table_matches_unity(self):
        import os
        path = os.path.join(os.path.dirname(__file__), "..", "..", "Robot vacuum simulator", "Assets", "Scripts", "LevelEditor", "BatchStats.cs")
        source = open(path, encoding="utf-8-sig").read()
        for value in stats.T_TABLE:
            self.assertIn(f"{value:.3f}", source)


if __name__ == "__main__":
    unittest.main()
