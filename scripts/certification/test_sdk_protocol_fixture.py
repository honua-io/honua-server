"""Offline controls for the source-bound SDK protocol fixture."""

from __future__ import annotations

import importlib.util
import json
import shutil
import tempfile
import unittest
from pathlib import Path
from unittest import mock

HERE = Path(__file__).resolve().parent


def load(name: str, filename: str):
    spec = importlib.util.spec_from_file_location(name, HERE / filename)
    assert spec and spec.loader
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


VERIFY = load("sdk_protocol_verify", "verify-sdk-protocol-fixture.py")
SEED = load("sdk_protocol_seed", "seed-sdk-protocol-fixture.py")


class SdkProtocolFixtureTests(unittest.TestCase):
    def setUp(self) -> None:
        self.manifest = VERIFY.verify_source()

    def test_source_inputs_are_byte_bound_and_core_stays_frozen(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            source = VERIFY.ROOT
            paths = [
                "tests/seed/sdk-protocol-v1/manifest.json",
                self.manifest["coreFixture"]["path"],
                *(item["path"] for item in self.manifest["inputs"]),
            ]
            for relative in paths:
                target = root / relative
                target.parent.mkdir(parents=True, exist_ok=True)
                shutil.copy2(source / relative, target)
            VERIFY.verify_source(root)
            with (root / "tests/seed/sdk-protocol-v1/geocoder.csv").open("a", encoding="utf-8") as stream:
                stream.write("extra,row\n")
            with self.assertRaisesRegex(ValueError, "fixture source drift"):
                VERIFY.verify_source(root)

    def test_duplicate_and_missing_input_refuse(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            manifest_path = root / "tests/seed/sdk-protocol-v1/manifest.json"
            manifest_path.parent.mkdir(parents=True)
            duplicate = dict(self.manifest)
            duplicate["inputs"] = [*self.manifest["inputs"], self.manifest["inputs"][0]]
            manifest_path.write_text(json.dumps(duplicate), encoding="utf-8")
            with self.assertRaisesRegex(ValueError, "duplicate fixture input"):
                VERIFY.verify_source(root)
            duplicate["inputs"].pop()
            manifest_path.write_text(json.dumps(duplicate), encoding="utf-8")
            with self.assertRaisesRegex(ValueError, "fixture source drift"):
                VERIFY.verify_source(root)

    def test_all_six_sdk_routing_locations_are_explicit(self) -> None:
        environment = VERIFY.client_environment(self.manifest)
        for prefix in (
            "ROUTE_ORIGIN", "ROUTE_DESTINATION", "SERVICE_AREA_CENTER",
            "ROUTE_INCIDENT", "ROUTE_FACILITY_A", "ROUTE_FACILITY_B",
        ):
            self.assertIn(f"HONUA_PROTOCOL_{prefix}_LONGITUDE", environment)
            self.assertIn(f"HONUA_PROTOCOL_{prefix}_LATITUDE", environment)
        self.assertEqual("380 New York St Redlands", environment["HONUA_PROTOCOL_GEOCODE_TEXT"])
        self.assertNotIn("HONUA_PROTOCOL_API_KEY", environment)

    def test_topology_accepts_only_the_exact_lattice_and_extensions(self) -> None:
        versions = [["pgrouting", "3.7.3"], ["postgis", "3.5"]]
        vertices = [[str(id), longitude, latitude] for id, longitude, latitude in SEED.EXPECTED_VERTICES]
        edges = [[str(gid), str(source), str(target), "1", "1", "t"] for gid, source, target in SEED.EXPECTED_EDGES]
        with mock.patch.object(SEED, "_query", side_effect=[versions, vertices, edges, [["12"]]]):
            self.assertEqual({"vertices": 9, "edges": 12, "pgroutingVersion": "3.7.3", "postgisVersion": "3.5"}, SEED._verify_topology())
        altered = [row.copy() for row in edges]
        altered[0][1] = "9"
        with mock.patch.object(SEED, "_query", side_effect=[versions, vertices, altered, [["12"]]]):
            with self.assertRaisesRegex(RuntimeError, "routing edges differ"):
                SEED._verify_topology()
        with mock.patch.object(SEED, "_query", side_effect=[versions, vertices, edges, [["13"]]]):
            with self.assertRaisesRegex(RuntimeError, "routing edges differ"):
                SEED._verify_topology()
        with mock.patch.object(SEED, "_query", return_value=[]):
            with self.assertRaisesRegex(RuntimeError, "lacks PostGIS or pgRouting"):
                SEED._verify_topology()

    def test_http_probe_requires_authored_geocoder_row_and_four_edge_route(self) -> None:
        good_forward = {"candidates": [{"address": "380 New York St, Redlands, CA 92373", "location": {"x": -117.1956, "y": 34.0566}}]}
        good_route = {"routes": {"features": [{"attributes": {"Total_TravelTime": 4}, "geometry": {"paths": [[[0, 0], [0.01, 0], [0.01, 0.01], [0.02, 0.01], [0.02, 0.02]]]}}]}}
        with mock.patch.object(SEED, "_json_request", side_effect=[good_forward, good_route]):
            SEED._probe_http("http://localhost:8080", self.manifest)
        bad_route = {"routes": {"features": [{"attributes": {"Total_TravelTime": 1}, "geometry": {"paths": [[[0, 0], [0.02, 0.02]]]}}]}}
        with mock.patch.object(SEED, "_json_request", side_effect=[good_forward, bad_route]):
            with self.assertRaisesRegex(RuntimeError, "four-edge reviewed lattice"):
                SEED._probe_http("http://localhost:8080", self.manifest)
        wrong_path = {"routes": {"features": [{"attributes": {"Total_TravelTime": 4}, "geometry": {"paths": [[[0, 0], [0.01, 0], [0.02, 0.02], [0.02, 0.01], [0.02, 0.02]]]}}]}}
        with mock.patch.object(SEED, "_json_request", side_effect=[good_forward, wrong_path]):
            with self.assertRaisesRegex(RuntimeError, "four-edge reviewed lattice"):
                SEED._probe_http("http://localhost:8080", self.manifest)
        wrong_endpoint = {"routes": {"features": [{"attributes": {"Total_TravelTime": 4}, "geometry": {"paths": [[[0, 0], [0.01, 0], [0.01, 0.01], [0.02, 0.01], [0.01, 0.01]]]}}]}}
        with mock.patch.object(SEED, "_json_request", side_effect=[good_forward, wrong_endpoint]):
            with self.assertRaisesRegex(RuntimeError, "four-edge reviewed lattice"):
                SEED._probe_http("http://localhost:8080", self.manifest)
        nonfinite_path = {"routes": {"features": [{"attributes": {"Total_TravelTime": 4}, "geometry": {"paths": [[[0, 0], [0.01, 0], [float("nan"), 0.01], [0.02, 0.01], [0.02, 0.02]]]}}]}}
        with mock.patch.object(SEED, "_json_request", side_effect=[good_forward, nonfinite_path]):
            with self.assertRaisesRegex(RuntimeError, "four-edge reviewed lattice"):
                SEED._probe_http("http://localhost:8080", self.manifest)


if __name__ == "__main__":
    unittest.main()
