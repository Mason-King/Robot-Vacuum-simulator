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

    def test_algorithm_names_become_catalog_ids(self):
        with tempfile.TemporaryDirectory() as folder:
            path = os.path.join(folder, "job.json")
            job = minimal()
            job["sweep"]["algorithms"] = ["randombounce", "spiral", "Boustrophedon Sweep", "BoustrophedonCrawl"]
            with open(path, "w") as handle:
                json.dump(job, handle)
            prepared = c.prepare(path)
            self.assertEqual(sorted({r["algorithm"] for r in prepared["runs"]}),
                             ["BoustrophedonCrawl", "BoustrophedonSweep", "Legacy.Spiral", "RandomBounce"])

    def test_slam_with_a_legacy_pattern_is_rejected(self):
        job = c.normalise(minimal(configurations=[{"name": "slam", "sensing": {"slam": True}}]))
        errors = c.validate(job)
        self.assertTrue(any("SLAM" in e and "Spiral" in e for e in errors), errors)
        self.assertEqual(job["configurations"][0]["sensing"]["lidarRayCount"], 360, "sensing defaults are filled")

    def test_vacuum_shape_defaults_to_round_and_is_checked(self):
        job = c.normalise(minimal())
        self.assertEqual(job["configurations"][0]["vacuum"]["shape"], "Round")
        self.assertEqual(c.validate(job), [])

        for name in ("Round", "DShaped", "Rectangular", "d", "D-shaped", "rectangle", "RECTANGULAR", ""):
            job = c.normalise(minimal(configurations=[{"vacuum": {"shape": name}}]))
            self.assertEqual(c.validate(job), [], name)

        job = c.normalise(minimal(configurations=[{"vacuum": {"shape": "triangle"}}]))
        self.assertTrue(any("vacuum.shape" in e for e in c.validate(job)))

    def test_vacuum_shapes_match_the_unity_source(self):
        root = os.path.join(HERE, "..", "..", "Robot vacuum simulator", "Assets", "Scripts", "Sim")
        source = open(os.path.join(root, "VacuumShape.cs"), encoding="utf-8-sig").read()
        for name in c.VACUUM_SHAPES:
            self.assertIn(f"        {name} = ", source)
        for alias in c._SHAPE_ALIASES:
            self.assertIn(f'case "{alias}":', source)
        self.assertIn("shape = VacuumShapeKind.Round", open(os.path.join(root, "VacuumSettings.cs"), encoding="utf-8-sig").read())

    def test_bad_sensing_values_are_reported(self):
        job = c.normalise(minimal(configurations=[{"sensing": {"noiseScale": 11, "lidarRayCount": 4, "slamCorrectionQuality": "Turbo"}}]))
        errors = c.validate(job)
        for field in ("noiseScale", "lidarRayCount", "slamCorrectionQuality"):
            self.assertTrue(any(field in e for e in errors), field)

    def test_algorithm_files_are_inlined_and_their_ids_known(self):
        source = ("using RobotVacuum.AlgorithmApi;\nnamespace Mine\n{\n    public sealed class Zig : ICleaningAlgorithm\n    {\n"
                  "        public string Name => \"Zig\";\n        public bool SupportsSlam => false;\n    }\n}\n")
        with tempfile.TemporaryDirectory() as folder:
            os.makedirs(os.path.join(folder, "algos", "Project", "Sub"))
            with open(os.path.join(folder, "algos", "Project", "Zig.cs"), "w") as handle:
                handle.write(source)
            with open(os.path.join(folder, "algos", "Project", "Sub", "Help.cs"), "w") as handle:
                handle.write("namespace Mine { static class Help { } }\n")
            job = minimal(algorithmFiles=["algos/Project"])
            job["sweep"]["algorithms"] = ["Mine.Zig"]
            path = os.path.join(folder, "job.json")
            with open(path, "w") as handle:
                json.dump(job, handle)
            prepared = c.prepare(path)
            self.assertNotIn("algorithmFiles", prepared)
            self.assertEqual(sorted(s["path"] for s in prepared["algorithmSources"]), ["Project/Sub/Help.cs", "Project/Zig.cs"])
            self.assertEqual({r["algorithm"] for r in prepared["runs"]}, {"Mine.Zig"})

            # The class says it can't do SLAM, so a SLAM configuration is refused before anything is uploaded.
            job["configurations"] = [{"sensing": {"slam": True}}]
            with open(path, "w") as handle:
                json.dump(job, handle)
            with self.assertRaises(c.CampaignError) as raised:
                c.prepare(path)
            self.assertTrue(any("SLAM" in e for e in raised.exception.errors))

    def test_algorithm_source_paths_must_stay_inside_the_folder(self):
        job = c.normalise(minimal(algorithmSources=[{"path": "../evil.cs", "source": "x"}, {"path": "notes.txt", "source": "x"}]))
        errors = c.validate(job)
        self.assertTrue(any("relative path" in e for e in errors))
        self.assertTrue(any(".cs" in e and "notes.txt" in e for e in errors))

    def test_slam_custom_example_is_valid(self):
        job = c.prepare(os.path.join(HERE, "..", "examples", "slam-custom.example.json"))
        self.assertEqual(len(job["runs"]), 2 * 3 * 5)
        self.assertEqual(job["algorithmSources"][0]["path"], "LongStraights.cs")

    def test_builtin_algorithms_match_the_unity_catalog(self):
        root = os.path.join(HERE, "..", "..", "Robot vacuum simulator", "Assets")
        catalog = open(os.path.join(root, "Scripts", "Sim", "Control", "CleaningAlgorithm.cs"), encoding="utf-8-sig").read()
        for algorithm_id, (label, slam) in c.BUILTIN_ALGORITHMS.items():
            if algorithm_id.startswith("Legacy."):
                self.assertIn(f'new AlgorithmEntry("{algorithm_id}", "{label}", {str(slam).lower()}', catalog)
            else:
                self.assertIn(f'"{label}", {str(slam).lower()}', catalog)

    def test_nonsense_files_give_campaign_errors_not_tracebacks(self):
        """A user's malformed file must come back as plain sentences (CampaignError), never a Python exception."""
        nonsense = [
            "not json {", "", "[1, 2]", "null", '"text"', "[" * 3000 + "]" * 3000,
            json.dumps(minimal(floorPlans="house")),
            json.dumps(minimal(configurations={"name": "x"})),
            json.dumps(minimal(configurations=[None, "fast", {"vacuum": "fast"}, {"sensing": "on"}])),
            json.dumps(minimal(sweep="all")),
            json.dumps(minimal(sweep={"algorithms": [{"id": 1}], "seeds": ["a"], "seedCount": "ten"})),
            json.dumps(minimal(runs={"runIndex": 0})),
            json.dumps(minimal(execution={"shardCount": "4"})),
            json.dumps(minimal(algorithmSources={"path": "a.cs"})),
            json.dumps(minimal(floorTypes=[{"name": 3}])),
            json.dumps(minimal(campaignName={"a": 1})),
            json.dumps(minimal(sweep={"algorithms": ["RandomBounce"], "seedCount": 10 ** 9})),
        ]
        with tempfile.TemporaryDirectory() as folder:
            path = os.path.join(folder, "job.json")
            for text in nonsense:
                with open(path, "w", encoding="utf-8") as handle:
                    handle.write(text)
                with self.assertRaises(c.CampaignError, msg=text[:60]) as raised:
                    c.prepare(path)
                self.assertTrue(raised.exception.errors and all(isinstance(e, str) for e in raised.exception.errors))
            with open(path, "w", encoding="utf-16") as handle:
                json.dump(minimal(), handle)
            self.assertEqual(len(c.prepare(path)["runs"]), 6, "a UTF-16 file (Windows Notepad) is read too")

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

        run_settings = open(os.path.join(root, "Sim", "RunSettings.cs"), encoding="utf-8-sig").read()
        sensing = defaults["sensing"]
        self.assertIn(f"public bool slam;", run_settings)
        self.assertFalse(sensing["slam"])
        self.assertIn(f"noiseScale = {sensing['noiseScale']:g}f", run_settings)
        self.assertIn(f"lidarHalfAngleDegrees = {sensing['lidarHalfAngleDegrees']:g}f", run_settings)
        self.assertIn(f"lidarRayCount = {sensing['lidarRayCount']}", run_settings)
        self.assertIn(f"slamCorrectionQuality = Control.PoseCorrectionQuality.{sensing['slamCorrectionQuality']}", run_settings)
        lidar = open(os.path.join(root, "Sim", "Sensors", "LidarSensor.cs"), encoding="utf-8-sig").read()
        self.assertIn(f"DefaultRangeMeters = {sensing['lidarRangeMeters']:g}f", lidar)


if __name__ == "__main__":
    unittest.main()
