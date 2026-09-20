"""Fail-closed rules of the bounded-roster receipt emitter and cell kit.

Run: python3 -m unittest discover -s certification/bounded-roster/tests
"""
from __future__ import annotations

import importlib.util
import json
import os
import sys
import tempfile
import unittest
from pathlib import Path

HERE = Path(__file__).resolve().parent
LIB = HERE.parent / "lib"
REPO = HERE.parents[2]
REQUIREMENTS = REPO / "certification" / "client-protocol-requirements.v1.json"
sys.path.insert(0, str(LIB))


def load(name: str, path: Path):
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


emit = load("emit_receipts", LIB / "emit_receipts.py")
TEST_ID = "client-cert/pystac-client/stac/serve.stac"
SOURCE_SHA = "a" * 40
PRODUCER_SHA = "b" * 40
DIGEST = "sha256:" + "c" * 64


def observation(status: str = "pass", **overrides) -> dict:
    value = {
        "schema": "honua.bounded-roster-observation/v1", "test_case_id": TEST_ID,
        "canonical_client": "PySTAC-Client", "client_lane": "py-pystac",
        "client_version": "pystac=1.15.2;pystac-client=0.9.0", "client_version_detail": "pystac==1.15.2",
        "surface": "stac", "operation": "serve.stac", "protocol_version": "STAC API 1.0.0",
        "protocol_profile": "core", "started_at": "2026-09-16T21:00:00.000000Z",
        "finished_at": "2026-09-16T21:00:10.000000Z", "duration_ms": 10000, "status": status,
        "exercised_capabilities": ["positive", "negative", "auth", "pagination", "limit", "media-schema"],
        "summary": "all checks passed", "primary_request_url": "http://honua:5000/stac/search", "checks": [],
    }
    value.update(overrides)
    return value


def wire_line(at: str, url: str, status: int = 200) -> dict:
    return {"at": at, "completed_at": at, "client_address": "172.0.0.9", "method": "GET", "url": url,
            "user_agent": "python-requests", "credential": None, "range": None, "accept": "*/*",
            "request_content_type": None, "status": status, "content_type": "application/json",
            "content_range": None, "response_bytes": 10, "roster_asset": False}


class EmitterTests(unittest.TestCase):
    def build(self, observations: list[dict], wire: list[dict]) -> tuple[dict, dict]:
        with tempfile.TemporaryDirectory() as directory:
            run = Path(directory)
            (run / "observations" / "lanes").mkdir(parents=True)
            (run / "wire").mkdir()
            for index, value in enumerate(observations):
                (run / "observations" / f"{index}.json").write_text(json.dumps(value), encoding="utf-8")
            (run / "wire" / "wire.jsonl").write_text("".join(json.dumps(line) + "\n" for line in wire), encoding="utf-8")
            (run / "candidate.json").write_text(json.dumps({
                "run_id": "roster-test", "release": "2026.1", "source_sha": SOURCE_SHA, "index_digest": DIGEST,
                "durable_uri_base": "https://example.invalid/receipts/"}), encoding="utf-8")
            (run / "fixture.json").write_text("{}", encoding="utf-8")
            (run / "lanes.json").write_text(json.dumps({"by_client_lane": {}}), encoding="utf-8")
            requirements = json.loads(REQUIREMENTS.read_text(encoding="utf-8"))
            return emit.build(run, requirements, PRODUCER_SHA)

    def only_result(self, receipts: dict) -> tuple[dict, dict]:
        self.assertEqual(1, len(receipts))
        envelope = next(iter(receipts.values()))
        self.assertEqual(1, len(envelope["results"]))
        return envelope, envelope["results"][0]

    def test_a_pass_backed_by_wire_exchanges_is_emitted_with_the_governed_join_keys(self):
        receipts, joins = self.build([observation()], [
            wire_line("2026-09-16T21:00:01+00:00", "http://honua:5000/stac"),
            wire_line("2026-09-16T21:00:02+00:00", "http://honua:5000/stac/search?limit=3")])
        envelope, result = self.only_result(receipts)
        self.assertEqual("pass", result["status"])
        self.assertEqual("http://honua:5000/stac/search?limit=3", result["request_url"])
        self.assertEqual(SOURCE_SHA, envelope["server_commit"])
        self.assertEqual(DIGEST, envelope["image_digest"])
        self.assertEqual(PRODUCER_SHA, envelope["producer_source_sha"])
        self.assertEqual("py-pystac", envelope["runner_lane"])
        self.assertEqual(f"docker/cng/seed.sql@{SOURCE_SHA}", envelope["fixture_revision"])
        self.assertEqual("local-docker", envelope["deployment_target"])
        self.assertEqual(2, joins[TEST_ID]["exchanges"])

    def test_a_claimed_pass_with_no_exchange_in_its_window_is_rewritten_to_fail(self):
        receipts, _ = self.build([observation()], [
            wire_line("2026-09-16T20:00:00+00:00", "http://honua:5000/stac/search")])
        _, result = self.only_result(receipts)
        self.assertEqual("fail", result["status"])
        self.assertIn("no candidate-bound exchange", result["notes"])

    def test_credential_bearing_urls_are_never_chosen_and_a_pass_without_a_clean_url_fails(self):
        receipts, _ = self.build([observation()], [
            wire_line("2026-09-16T21:00:01+00:00", "http://honua:5000/stac/search?token=%3Credacted%3E")])
        _, result = self.only_result(receipts)
        self.assertEqual("fail", result["status"])
        self.assertIn("no publishable request URL", result["notes"])

    def test_roster_asset_exchanges_do_not_count_as_client_evidence(self):
        asset = wire_line("2026-09-16T21:00:01+00:00", "http://honua:5000/__roster/index.html")
        asset["roster_asset"] = True
        receipts, _ = self.build([observation()], [asset])
        _, result = self.only_result(receipts)
        self.assertEqual("fail", result["status"])

    def test_a_failing_cell_stays_failed_even_with_exchanges(self):
        receipts, _ = self.build([observation("fail", summary="failed checks auth")], [
            wire_line("2026-09-16T21:00:01+00:00", "http://honua:5000/stac/search")])
        _, result = self.only_result(receipts)
        self.assertEqual("fail", result["status"])

    def test_gdal_and_gdal_ogr_cells_on_one_lane_get_separate_envelopes(self):
        base = dict(client_lane="gdal", client_version="3.8.4", surface="ogc", client_version_detail="GDAL 3.8.4",
                    protocol_version="1", protocol_profile="p", primary_request_url="http://honua:5000/ogc/features",
                    exercised_capabilities=["positive"])
        receipts, _ = self.build([
            observation(test_case_id="client-cert/gdal-ogr/ogc/OGC-OP-WFS-2-0", canonical_client="GDAL/OGR", **base),
            observation(test_case_id="client-cert/gdal/ogc/OGC-OP-WCS-2-0-COVERAGE", canonical_client="GDAL", **base),
        ], [wire_line("2026-09-16T21:00:01+00:00", "http://honua:5000/ogc/features")])
        clients = sorted(envelope["client_id"] for envelope in receipts.values())
        self.assertEqual(["GDAL", "GDAL/OGR"], clients)
        for envelope in receipts.values():
            self.assertEqual({envelope["client_id"]}, {result["performed_by"] for result in envelope["results"]})

    def test_an_ungoverned_test_id_is_refused(self):
        with self.assertRaises(SystemExit):
            self.build([observation(test_case_id="client-cert/pystac-client/stac/not-governed")], [])


class CellKitTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        os.environ["ROSTER_REQUIREMENTS"] = str(REQUIREMENTS)
        os.environ["ROSTER_OBSERVATIONS"] = self.directory.name
        self.cellkit = load("cellkit", LIB / "cellkit.py")

    def tearDown(self):
        self.directory.cleanup()

    def written(self, cell) -> dict:
        return json.loads(cell.write().read_text(encoding="utf-8"))

    def test_a_governed_facet_without_a_check_fails_the_cell(self):
        cell = self.cellkit.Cell(TEST_ID, client_version_detail="x", protocol_version="1", protocol_profile="p")
        with cell.check("positive", "ok"):
            pass
        result = self.written(cell)
        self.assertEqual("fail", result["status"])
        self.assertIn("governed facets not exercised", result["summary"])
        self.assertEqual(["positive"], result["exercised_capabilities"])

    def test_an_exception_fails_only_its_check_and_the_cell(self):
        cell = self.cellkit.Cell(TEST_ID, client_version_detail="x", protocol_version="1", protocol_profile="p")
        for facet in cell.requirement["scenario_facets"]:
            with cell.check(facet, facet):
                if facet == "auth":
                    raise RuntimeError("401 was not returned")
        result = self.written(cell)
        self.assertEqual("fail", result["status"])
        self.assertEqual(["auth:auth"], [f"{c['facet']}:{c['name']}" for c in result["checks"] if c["outcome"] == "fail"])

    def test_every_governed_facet_passing_is_a_pass(self):
        cell = self.cellkit.Cell(TEST_ID, client_version_detail="x", protocol_version="1", protocol_profile="p")
        for facet in cell.requirement["scenario_facets"]:
            with cell.check(facet, facet):
                pass
        self.assertEqual("pass", self.written(cell)["status"])

    def test_an_ungoverned_facet_is_a_harness_error(self):
        cell = self.cellkit.Cell(TEST_ID, client_version_detail="x", protocol_version="1", protocol_profile="p")
        with self.assertRaises(ValueError):
            with cell.check("range-efficiency", "not governed for STAC"):
                pass


if __name__ == "__main__":
    unittest.main()
