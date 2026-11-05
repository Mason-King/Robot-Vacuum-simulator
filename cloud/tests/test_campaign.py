"""Campaign file handling. Run from cloud/:  python -m unittest discover -s tests -v"""

import copy
import json
import os
import tempfile
import unittest

from vacuumctl import campaign as c

HERE = os.path.dirname(os.path.abspath(__file__))
EXAMPLE = os.path.join(HERE, "..", "examples", "campaign.example.json")

ROOM = {"name": "Box", "floor": "Hardwood", "outline": [{"x": 0, "y": 0}, {"x": 4, "y": 0}, {"x": 4, "y": 3}, {"x": 0, "y": 3}]}


def minimal(**extra):
    job = {"schemaVersion": 1, "campaignName": "t",
           "floorPlans": [{"name": "A", "rooms": [copy.deepcopy(ROOM)], "robotSpawn": {"x": 1, "y": 1}}],
           "sweep": {"algorithms": ["RandomBounce", "spiral"], "seedStart": 5, "seedCount": 3}}
    job.update(extra)
    return job


class CampaignTests(unittest.TestCase):
    def test_sweep_expands_in_documented_order(self):
        job = c.normalise(minimal())
        self.assertEqual(c.validate(job), [])
        self.assertEqual([r["seed"] for r in job["runs"]], [5, 6, 7, 5, 6, 7])
        self.assertEqual([r["algorithm"] for r in job["runs"]], ["RandomBounce"] * 3 + ["spiral"] * 3)
        self.assertEqual([r["runIndex"] for r in job["runs"]], list(range(6)))

    def test_defaults_are_filled(self):
        job = c.normalise(minimal(configurations=[{"name": "x", "vacuum": {"driveSpeed": 1.0}}]))
        config = job["configurations"][0]
        self.assertEqual(config["vacuum"]["driveSpeed"], 1.0)
        self.assertEqual(config["vacuum"]["reverseSpeed"], 0.35)
        self.assertEqual(config["battery"]["capacity"], 300.0)
        self.assertEqual(config["simulation"]["cellSizeMeters"], 0.0508)

    def test_bad_values_are_reported(self):
        job = c.normalise(minimal(configurations=[{"simulation": {"cellSizeMeters": 5}, "battery": {"capacity": 999999}}]))
        errors = c.validate(job)
        self.assertTrue(any("cellSizeMeters" in e for e in errors))
        self.assertTrue(any("battery.capacity" in e for e in errors))

    def test_unknown_algorithm_and_floor(self):
        job = minimal()
        job["sweep"]["algorithms"] = ["Teleport"]
        job["floorPlans"][0]["rooms"][0]["floor"] = "Lava"
        errors = c.validate(c.normalise(job))
        self.assertTrue(any("Teleport" in e for e in errors))
        self.assertTrue(any("Lava" in e for e in errors))

    def test_custom_floor_types(self):
        job = minimal(floorTypes=[{"name": "Lava", "cleaningEffort": 3}])
        job["floorPlans"][0]["rooms"][0]["floor"] = "lava"
        job = c.normalise(job)
        self.assertEqual(c.validate(job), [])
        self.assertEqual(job["floorTypes"][0]["speedMultiplier"], 1.0)

    def test_shards_cover_every_run_once(self):
        job = c.normalise(minimal())
        job["execution"]["shardCount"] = 4
        manifest = c.manifest(job)
        indices = sorted(i for shard in manifest["shards"] for i in shard["runIndices"])
        self.assertEqual(indices, list(range(len(job["runs"]))))

    def test_prepare_caps_nodes_at_run_count(self):
        with tempfile.TemporaryDirectory() as folder:
            path = os.path.join(folder, "job.json")
            with open(path, "w") as handle:
                json.dump(minimal(), handle)
            job = c.prepare(path, shard_count=50)
            self.assertEqual(job["execution"]["shardCount"], 6)

    def test_example_campaign_is_valid(self):
        job = c.prepare(EXAMPLE)
        self.assertEqual(len(job["runs"]), 2 * 2 * 4 * 20)
        self.assertEqual(job["floorPlans"][0]["name"], "Two rooms", "file references are inlined")

    def test_defaults_match_the_unity_source(self):
        """Guards against the Python and C# defaults drifting apart."""
        root = os.path.join(HERE, "..", "..", "Robot vacuum simulator", "Assets", "Scripts")
        settings = open(os.path.join(root, "Sim", "VacuumSettings.cs"), encoding="utf-8-sig").read()
        battery = open(os.path.join(root, "Bot", "Battery.cs"), encoding="utf-8-sig").read()
        defaults = c.CONFIGURATION_DEFAULTS
        self.assertIn(f"driveSpeed = {defaults['vacuum']['driveSpeed']}f", settings)
        self.assertIn(f"reverseSpeed = {defaults['vacuum']['reverseSpeed']}f", settings)
        self.assertIn(f"turnSpeed = {defaults['vacuum']['turnSpeed']:g}f", settings)
        self.assertIn(f"batteryCapacity = {defaults['battery']['capacity']:g}f", settings)
        self.assertIn(f"distanceEnergyPerMeter = {defaults['battery']['distanceEnergyPerMeter']:g}f", battery)
        self.assertIn(f"turnEnergyPerDegree = {defaults['battery']['turnEnergyPerDegree']:g}f", battery)
        self.assertIn(f"operatingEnergyPerSecond = {defaults['battery']['operatingEnergyPerSecond']:g}f", battery)
        self.assertIn(f"stalledEnergyPerSecond = {defaults['battery']['stalledEnergyPerSecond']:g}f", battery)


if __name__ == "__main__":
    unittest.main()
