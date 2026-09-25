"""Mock harness proofs; these do not qualify a deployed candidate."""
import base64
import copy
import json
import math
import os
import tempfile
import unittest
from datetime import timedelta
from pathlib import Path
from unittest.mock import patch

import gp_canary as canary
import gp_canary_streak as streak


def output(process):
    if process == canary.PROCESSES[0]:
        ring = [[1000 + 100 * math.cos(i * math.pi / 16), 2000 + 100 * math.sin(i * math.pi / 16)] for i in range(32)]
        ring.append(ring[0])
        return {"type": "Feature", "properties": {"processId": process, "inputSrid": 3857, "bufferDistance": 100},
                "geometry": {"type": "Polygon", "coordinates": [ring]}}
    features = []
    for group in range(4):
        x = group * 100
        features.append({"type": "Feature", "properties": {"processId": process, "inputSrid": 3857, "groupKey": str(group)},
                         "geometry": {"type": "Polygon", "coordinates": [[[x, 0], [x + 33, 0], [x + 33, 33], [x, 33], [x, 0]]]}})
    return {"type": "FeatureCollection", "processId": process, "inputSrid": 3857, "inputCount": 4096,
            "groupCount": 4, "features": features}


class NumericalOracleTests(unittest.TestCase):
    def test_frozen_workloads_and_analytical_outputs(self):
        for process in canary.PROCESSES:
            self.assertTrue(canary.oracle(process, output(process))["features"])
        dissolve = canary.payload(canary.PROCESSES[1])["inputs"]
        self.assertEqual(4096, len(dissolve["wkbs"]))
        self.assertEqual({str(i): 1024 for i in range(4)}, {key: dissolve["groupKeys"].count(key) for key in set(dissolve["groupKeys"])})
        self.assertEqual(93, len(base64.b64decode(dissolve["wkbs"][0])))

    def test_buffer_valid_json_wrong_geometry_crs_and_distance_rejected(self):
        for mutation in ("coordinate", "crs", "distance", "topology", "empty"):
            with self.subTest(mutation=mutation):
                document = output(canary.PROCESSES[0])
                if mutation == "coordinate":
                    document["geometry"]["coordinates"][0][1][0] += 0.1
                elif mutation == "crs":
                    document["properties"]["inputSrid"] = 4326
                elif mutation == "distance":
                    document["properties"]["bufferDistance"] = 1
                elif mutation == "topology":
                    document["geometry"]["coordinates"] = [[[900, 1900], [1100, 2100], [1100, 1900], [900, 2100], [900, 1900]]]
                else:
                    document = {"message": "nonempty JSON is not an oracle"}
                with self.assertRaises(ValueError):
                    canary.oracle(canary.PROCESSES[0], document)

    def test_dissolve_missing_group_wrong_count_and_shape_rejected(self):
        for mutation in ("group", "count", "crs", "shape", "hole"):
            with self.subTest(mutation=mutation):
                document = output(canary.PROCESSES[1])
                if mutation == "group":
                    document["features"][1]["properties"]["groupKey"] = "0"
                elif mutation == "count":
                    document["inputCount"] = 4
                elif mutation == "crs":
                    document["features"][0]["properties"]["inputSrid"] = 4326
                elif mutation == "shape":
                    document["features"][0]["geometry"]["coordinates"][0][1][0] = 32
                else:
                    document["features"][0]["geometry"]["coordinates"].append([[1, 1], [1, 2], [2, 2], [2, 1], [1, 1]])
                with self.assertRaises(ValueError):
                    canary.oracle(canary.PROCESSES[1], document)

    def test_geometry_ordering_does_not_change_semantics(self):
        document = output(canary.PROCESSES[1])
        document["features"].reverse()
        for feature in document["features"]:
            feature["geometry"]["coordinates"][0].reverse()
        self.assertEqual(canary.oracle(canary.PROCESSES[1], output(canary.PROCESSES[1])), canary.oracle(canary.PROCESSES[1], document))

    def test_missing_configuration_still_writes_failure_receipt(self):
        with tempfile.TemporaryDirectory() as directory, patch.dict(os.environ, {"HONUA_GP_CANARY_RECEIPT": str(Path(directory) / "receipt.json")}, clear=True):
            self.assertEqual(1, canary.main())
            receipt = json.loads((Path(directory) / "receipt.json").read_text())
            self.assertEqual("fail", receipt["outcome"])
            self.assertIn("URL", receipt["finding"])
            self.assertEqual([], receipt["operations"])

    def test_live_identity_must_be_exact_image_digest(self):
        client = canary.Client("https://candidate.example", "secret")
        pin = {"server_digest": "sha256:" + "a" * 64}
        for revision, source, passes in ((pin["server_digest"], "image-digest", True),
                                         ("sha256:" + "b" * 64, "image-digest", False),
                                         ("a" * 40, "commit-sha", False), (None, None, False)):
            with self.subTest(revision=revision), patch.object(client, "request", return_value={"server": {"deploymentRevision": revision, "deploymentRevisionSource": source}}):
                if passes:
                    self.assertEqual(revision, client.identity(pin)["deploymentRevision"])
                else:
                    with self.assertRaises(ValueError):
                        client.identity(pin)

    def test_operation_failure_keeps_id_and_does_not_prevent_second_receipt(self):
        class FakeClient:
            endpoint = "https://candidate.example"
            def request(self, path, body=None, expected=200):
                if body:
                    self.process = "geometry.buffer" if "buffer" in path else "geometry.dissolve"
                    return {"jobID": self.process.replace(".", "-")}
                if path.endswith("/results"):
                    return {"value": {"value": output(self.process)}}
                return {"status": "failed" if self.process == "geometry.buffer" else "successful"}
        with tempfile.TemporaryDirectory() as directory:
            receipt = {"operations": []}
            client = FakeClient()
            for process in canary.PROCESSES:
                canary.run_operation(client, process, Path(directory), receipt)
            self.assertEqual(["fail", "pass"], [item["outcome"] for item in receipt["operations"]])
            self.assertTrue(all(item.get("operation_id") for item in receipt["operations"]))

    def test_cross_origin_artifact_is_rejected(self):
        client = canary.Client("https://candidate.example", "secret")
        for href in ("https://evil.example/output", "//evil.example/output", "http://candidate.example/output"):
            with self.subTest(href=href), self.assertRaises(ValueError):
                canary.resolve_output(client, {"result": {"href": href}})


class ScheduledStreakTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.runs = []
        for index in range(7):
            created = canary.timestamp("2026-09-25T18:18:00Z") - timedelta(hours=6 * index)
            run = {"id": 100 - index, "event": "schedule", "run_attempt": 1,
                   "created_at": created.isoformat(), "head_sha": "a" * 40,
                   "html_url": f"https://github.com/example/actions/runs/{100-index}",
                   "status": "completed", "conclusion": "success"}
            self.runs.append(run)
            folder = self.root / str(run["id"])
            candidate = {"source_sha": "b" * 40, "server_digest": "sha256:" + "c" * 64,
                         "server_image": "ghcr.io/honua-io/honua-server@sha256:" + "c" * 64,
                         "manifest_sha256": "d" * 64, "release_ref": "e" * 40,
                         "oracle_sha256": "f" * 64, "harness_sha": run["head_sha"]}
            identity = {"deploymentRevision": candidate["server_digest"], "deploymentRevisionSource": "image-digest"}
            receipt = {"schema": canary.SCHEMA, "fixture": canary.FIXTURE, "outcome": "pass",
                       "candidate": candidate, "endpoint": "https://candidate.example",
                       "identity_before": identity, "identity_after": identity,
                       "github": {key: run[key] for key in ("id", "run_attempt", "event", "created_at", "head_sha", "html_url")},
                       "scheduled_slot": canary.scheduled_slot(run["created_at"]),
                       "started_at": created.isoformat(), "completed_at": (created + timedelta(minutes=1)).isoformat(), "operations": []}
            for process in canary.PROCESSES:
                operation = {"process": process, "fixture": canary.FIXTURE, "outcome": "pass", "operation_id": f"job-{index}-{process}",
                             "latency_seconds": 1, "submit_http_status": 201, "transitions": [{"status": "successful"}],
                             "metrics": canary.oracle(process, output(process))}
                for kind, document in (("input", canary.payload(process)), ("output", output(process))):
                    path = folder / f"{process.replace('.', '-')}-{kind}.json"
                    canary.write_json(path, document)
                    operation[kind] = {"file": path.name, "sha256": canary.sha(path.read_bytes())}
                receipt["operations"].append(operation)
            canary.write_json(folder / "receipt.json", receipt)

    def build(self, event="schedule"):
        return streak.build_streak(self.runs[0], self.runs, lambda run: self.root / str(run["id"]), self.root / "bundle" / "streak.json", event)

    def mutate(self, index, change):
        path = self.root / str(self.runs[index]["id"]) / "receipt.json"
        receipt = json.loads(path.read_text())
        change(receipt)
        canary.write_json(path, receipt)

    def test_seven_original_complete_intervals_produce_retained_bundle(self):
        result = self.build()
        self.assertTrue(result["ready"])
        self.assertEqual(7, result["consecutive_green"])
        self.assertEqual(35, len(list((self.root / "bundle" / "intervals").glob("*/*.json"))))

    def test_missing_current_receipt_resets_streak(self):
        (self.root / "100" / "receipt.json").unlink()
        result = self.build()
        self.assertFalse(result["ready"])
        self.assertEqual(0, result["consecutive_green"])

    def test_missing_slot_cannot_be_replaced_by_an_older_green(self):
        self.runs[3]["created_at"] = "2026-09-20T00:18:00Z"
        result = self.build()
        self.assertFalse(result["ready"])
        self.assertEqual(3, result["consecutive_green"])

    def test_skipped_cancelled_failed_and_retried_runs_break_streak(self):
        for conclusion in ("skipped", "cancelled", "failure"):
            with self.subTest(conclusion=conclusion):
                self.runs[1]["conclusion"] = conclusion
                self.assertEqual(1, self.build()["consecutive_green"])
        self.runs[1]["conclusion"] = "success"
        self.runs[1]["run_attempt"] = 2
        self.assertEqual(1, self.build()["consecutive_green"])

    def test_manual_or_current_retry_never_advances(self):
        self.assertFalse(self.build("workflow_dispatch")["ready"])
        self.runs[0]["run_attempt"] = 2
        self.assertFalse(self.build()["ready"])

    def test_candidate_or_oracle_change_breaks_streak(self):
        for field in ("server_digest", "source_sha", "manifest_sha256", "release_ref", "oracle_sha256"):
            with self.subTest(field=field):
                path = self.root / "99" / "receipt.json"
                original = path.read_bytes()
                self.mutate(1, lambda receipt: receipt["candidate"].update({field: "different"}))
                self.assertEqual(1, self.build()["consecutive_green"])
                path.write_bytes(original)

    def test_green_summary_cannot_hide_missing_metrics_or_mutated_artifacts(self):
        self.mutate(1, lambda receipt: receipt["operations"][0].pop("metrics"))
        self.assertEqual(1, self.build()["consecutive_green"])
        (self.root / "100" / "geometry-buffer-output.json").write_text('{"error":"still valid JSON"}')
        self.assertEqual(0, self.build()["consecutive_green"])

    def test_stale_receipt_and_duplicate_interval_cannot_pass(self):
        self.mutate(2, lambda receipt: receipt.update(completed_at="2026-09-26T00:00:00Z"))
        self.assertEqual(2, self.build()["consecutive_green"])
        duplicate = copy.deepcopy(self.runs[1])
        duplicate["id"] = 999
        self.runs.append(duplicate)
        self.assertEqual(1, self.build()["consecutive_green"])

    def test_late_schedule_cannot_substitute_for_missed_slot(self):
        with self.assertRaises(ValueError):
            canary.scheduled_slot("2026-09-25T03:00:00Z")


if __name__ == "__main__":
    unittest.main()
