#!/usr/bin/env python3
"""Verify external OGC suites run manually and on every release-candidate build."""
from pathlib import Path
import unittest
import yaml

ROOT = Path(__file__).resolve().parents[3]
WORKFLOWS = ROOT / ".github/workflows"


class UniqueKeyLoader(yaml.BaseLoader):
    """Reject duplicate keys that GitHub would reject rather than silently replacing."""

    def construct_mapping(self, node, deep=False):
        keys = [self.construct_object(key, deep=deep) for key, _ in node.value]
        if len(keys) != len(set(keys)):
            raise ValueError(f"Duplicate YAML key in {node.start_mark}")
        return super().construct_mapping(node, deep=deep)


def workflow(name):
    return yaml.load((WORKFLOWS / name).read_text(), Loader=UniqueKeyLoader)


class OgcReleaseCandidateTests(unittest.TestCase):
    def test_conformance_dispatchers_have_no_pr_push_or_schedule_trigger(self):
        names = set(path.name for path in WORKFLOWS.glob("cite-*-conformance.yml"))
        names.update(("cite-conformance.yml", "cite-evidence-report.yml",
                      "ogc-maps-conformance.yml", "ogc-api-building-block-conformance.yml"))
        calls = workflow("ogc-rc-conformance.yml")["jobs"]
        targets = {job["uses"].split("/")[-1] for job in calls.values()}
        self.assertEqual(names, targets)
        for name in names:
            with self.subTest(workflow=name):
                self.assertEqual(set(workflow(name)["on"]), {"workflow_dispatch", "workflow_call"})

    def test_every_release_candidate_waits_for_conformance(self):
        jobs = workflow("release-bundle.yml")["jobs"]
        call = jobs["ogc-conformance"]
        self.assertEqual(call["needs"], "rc-image")
        self.assertEqual(call["if"], "${{ github.event.inputs.channel == 'rc' }}")
        self.assertNotIn("run_integration", call["if"])
        self.assertEqual(call["with"]["server_image"], "${{ needs.rc-image.outputs.image_digest_ref }}")
        self.assertEqual(call["with"]["source_sha"], "${{ needs.rc-image.outputs.server_sha }}")
        self.assertIn("ogc-conformance", jobs["aggregate"]["needs"])
        self.assertIn("github.event.inputs.channel != 'rc' || needs.ogc-conformance.result == 'success'",
                      jobs["aggregate"]["if"])

    def test_reusable_calls_supply_required_inputs(self):
        for job in workflow("ogc-rc-conformance.yml")["jobs"].values():
            target = workflow(job["uses"].split("/")[-1])
            inputs = (target["on"]["workflow_call"] or {}).get("inputs", {})
            supplied = job.get("with", {})
            self.assertLessEqual(set(supplied), set(inputs))
            for key, definition in inputs.items():
                if definition.get("required") == "true":
                    self.assertIn(key, supplied)

    def test_digest_and_permissions_survive_reusable_call_chain(self):
        release = workflow("release-bundle.yml")["jobs"]
        self.assertIn("@${{ steps.build.outputs.digest }}", release["rc-image"]["outputs"]["image_digest_ref"])
        dispatcher = workflow("ogc-rc-conformance.yml")
        for job in dispatcher["jobs"].values():
            target = workflow(job["uses"].split("/")[-1])
            for permission, access in target.get("permissions", {}).items():
                if access == "write":
                    self.assertEqual(dispatcher["permissions"].get(permission), "write")
                    self.assertEqual(release["ogc-conformance"]["permissions"].get(permission), "write")

    def test_manual_dispatch_cannot_cancel_a_candidate_suite(self):
        for job in workflow("ogc-rc-conformance.yml")["jobs"].values():
            target = workflow(job["uses"].split("/")[-1])
            concurrency = target.get("concurrency", {})
            if concurrency.get("cancel-in-progress") != "false" and concurrency:
                self.assertIn("github.run_id", concurrency["group"])

    def test_duplicate_yaml_keys_are_rejected(self):
        with self.assertRaises(ValueError):
            yaml.load("with:\n  profile: basic\n  profile: full\n", Loader=UniqueKeyLoader)

    def test_independent_classic_profiles_keep_distinct_artifacts(self):
        jobs = workflow("cite-classic-conformance.yml")["jobs"]
        for job in jobs.values():
            self.assertEqual(job["with"]["artifact-suffix"], "-classic")
        steps = workflow("cite-conformance-common.yml")["jobs"]["cite"]["steps"]
        names = [step.get("with", {}).get("name", "") for step in steps]
        self.assertTrue(any("inputs.artifact-suffix" in name for name in names))


if __name__ == "__main__":
    unittest.main()
