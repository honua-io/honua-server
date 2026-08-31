import argparse
import importlib.util
import json
import tempfile
import unittest
from pathlib import Path

SCRIPT = Path(__file__).parents[1] / "build_protocol_certification_fragment.py"
SPEC = importlib.util.spec_from_file_location("cite_fragment", SCRIPT)
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)

SHA = "a" * 40
DIGEST = "sha256:" + "b" * 64


class FragmentTests(unittest.TestCase):
    def build(self, *, include_suite=True, image_digest=DIGEST,
              candidate_cut="2026-08-30T00:00:00Z",
              started_at="2026-08-30T00:01:00Z"):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            requirement = {
                "capability_key": "serve.wfs", "surface": "wfs-2-0", "operation": "serve.wfs",
                "canonical_client": "OGC CITE", "client_lane": "cite-wfs-2-0",
                "client_version": "WFS 2.0", "deployment_target": "local-docker",
                "scenario_facets": ["positive", "negative", "crs-axis", "media-schema"],
                "fixture_revision": "fixture@{source_sha}", "contract_revision": "contract-v1",
                "auth_policy_revision": "anonymous-public-v1",
            }
            summary = {"suites": ([{"id": "wfs20", "status": "passed", "totalTests": 167,
                                    "passed": 167, "failed": 0, "skipped": 0, "cantTell": 0}]
                                  if include_suite else [])}
            provenance = {"suites": {"wfs20": {"suite_version": "ets-wfs20@sha256:123",
                "team_engine_version": "6.0.0-RC2", "protocol_version": "2.0",
                "protocol_profile": "basic", "request_path": "/wfs"}}}
            paths = {}
            for name, value in (("requirements", {"requirements": [requirement]}),
                                ("summary", summary), ("provenance", provenance)):
                paths[name] = root / f"{name}.json"
                paths[name].write_text(json.dumps(value))
            args = argparse.Namespace(summary=paths["summary"], requirements=paths["requirements"],
                suite_provenance=paths["provenance"], source_sha=SHA, producer_source_sha=SHA,
                image_digest=image_digest, candidate_cut=candidate_cut,
                started_at=started_at, completed_at="2026-08-30T00:02:00Z",
                run_url="https://github.com/honua-io/honua-server/actions/runs/1",
                target_base_url="http://localhost:8080", output=root / "out.json")
            return MODULE.build(args)

    def test_pass_is_exact_candidate_and_content_addressed(self):
        fragment = self.build()
        observation = fragment["observations"][0]
        self.assertEqual("pass", observation["result"])
        self.assertEqual(SHA, observation["source_sha"])
        self.assertEqual(DIGEST, observation["image_digest"])
        self.assertEqual("OGC CITE", observation["performed_by"])
        self.assertEqual("http://localhost:8080/wfs", observation["request_url"])
        self.assertEqual("2026-08-30T00:00:00Z",
                         observation["evidence_receipt"]["identity"]["candidate_cut_at"])
        self.assertTrue(observation["evidence_uri"].endswith(observation["evidence_digest"][7:]))
        self.assertEqual(set(observation["facet_results"]), set(observation["exercised_capabilities"]))

    def test_missing_suite_is_explicit_skip_without_fabricated_evidence(self):
        observation = self.build(include_suite=False)["observations"][0]
        self.assertEqual("skip", observation["result"])
        self.assertIsNone(observation["request_url"])
        self.assertEqual([], observation["exercised_capabilities"])
        self.assertIsNone(observation["evidence_receipt"])
        self.assertIn("did not produce", observation["skip_reason"])

    def test_broad_unmapped_requirement_is_explicit_skip(self):
        self.assertNotIn("wfs", MODULE.SUITE_BY_SURFACE)

    def test_rejects_mutable_or_uppercase_candidate_identity(self):
        with self.assertRaisesRegex(ValueError, "image digest"):
            self.build(image_digest="latest")

    def test_rejects_pre_cut_execution(self):
        with self.assertRaisesRegex(ValueError, "at or after"):
            self.build(candidate_cut="2026-08-30T00:01:01Z")


if __name__ == "__main__":
    unittest.main()
