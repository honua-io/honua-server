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
        def inspect(name, ancestors=()):
            self.assertNotIn(name, ancestors)
            for job in workflow(name)["jobs"].values():
                target_path = job.get("uses", "")
                if not target_path.startswith("./.github/workflows/"):
                    continue
                target_name = target_path.split("/")[-1]
                target = workflow(target_name)
                inputs = (target["on"]["workflow_call"] or {}).get("inputs", {})
                supplied = job.get("with", {})
                self.assertLessEqual(set(supplied), set(inputs), target_name)
                for key, definition in inputs.items():
                    if definition.get("required") == "true":
                        self.assertIn(key, supplied, target_name)
                self.assertNotEqual(job.get("secrets"), "inherit", target_name)
                inspect(target_name, (*ancestors, name))
        inspect("ogc-rc-conformance.yml")
        self.assertNotIn("secrets", workflow("release-bundle.yml")["jobs"]["ogc-conformance"])

    def test_permissions_survive_every_reusable_call(self):
        rank = {"none": 0, "read": 1, "write": 2}
        def check(child, parent):
            for permission, access in child.items():
                self.assertGreaterEqual(rank[parent.get(permission, "none")], rank[access], permission)
        def inspect(name, caller_permissions):
            doc = workflow(name)
            declared = doc.get("permissions", caller_permissions)
            check(declared, caller_permissions)
            for job in doc["jobs"].values():
                permissions = job.get("permissions", declared)
                check(permissions, caller_permissions)
                if job.get("uses", "").startswith("./.github/workflows/"):
                    inspect(job["uses"].split("/")[-1], permissions)
        caller = workflow("release-bundle.yml")["jobs"]["ogc-conformance"]["permissions"]
        inspect("ogc-rc-conformance.yml", caller)

    def test_manual_dispatch_cannot_cancel_a_candidate_suite(self):
        dispatcher = workflow("ogc-rc-conformance.yml")
        self.assertNotIn("concurrency", dispatcher)
        for job in dispatcher["jobs"].values():
            target = workflow(job["uses"].split("/")[-1])
            self.assertNotIn("concurrency", target)
        pages = workflow("cite-evidence-report.yml")["jobs"]["deploy-pages"]["concurrency"]
        self.assertEqual(pages, {"group": "cite-evidence-report-pages",
                                 "cancel-in-progress": "false", "queue": "max"})

    def test_source_identity_uses_image_only_when_supplied(self):
        for job in workflow("ogc-rc-conformance.yml")["jobs"].values():
            name = job["uses"].split("/")[-1]
            doc = workflow(name)
            image = "honua_image" if name == "cite-conformance.yml" else "server_image"
            text = (WORKFLOWS / name).read_text()
            if image in (doc["on"]["workflow_call"] or {}).get("inputs", {}) and "inputs.source_sha || github.sha" in text:
                self.assertIn(f"inputs.{image} != '' && inputs.source_sha || github.sha", text, name)

    def test_duplicate_yaml_keys_are_rejected(self):
        with self.assertRaises(ValueError):
            yaml.load("with:\n  profile: basic\n  profile: full\n", Loader=UniqueKeyLoader)

    def test_classic_suites_have_distinct_artifact_identities(self):
        jobs = workflow("cite-classic-conformance.yml")["jobs"]
        suites = [job["with"]["suite"] for job in jobs.values()]
        self.assertEqual(len(suites), len(set(suites)))
        self.assertNotIn("artifact-suffix", workflow("cite-conformance-common.yml")["on"]["workflow_call"]["inputs"])

    def test_building_blocks_never_skip_release_candidate_work(self):
        job = workflow("ogc-api-building-block-conformance.yml")["jobs"]["conformance"]
        self.assertNotIn("if", job)
        self.assertFalse(any(step.get("id") == "generated-only" for step in job["steps"]))
        text = (WORKFLOWS / "ogc-api-building-block-conformance.yml").read_text()
        self.assertNotIn("pull_request", text)


if __name__ == "__main__":
    unittest.main()
