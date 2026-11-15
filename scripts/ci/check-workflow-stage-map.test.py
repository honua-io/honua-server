#!/usr/bin/env python3
"""Adversarial checks for stage vocabulary and source contract drift."""
import fnmatch
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest

import yaml

SPEC = importlib.util.spec_from_file_location(
    "stage_map", Path(__file__).with_name("check-workflow-stage-map.py"))
checker = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(checker)


class StageMapTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)
        action = {"id": "read", "verb": "inspect_layer", "inputs": [
            {"concept": "/concepts/domain/layer.md"}], "outputs": [],
            "interfaces": [{"kind": "http", "method": "GET", "path": "/layers",
                            "contract": "/contract.json"}]}
        self.write("docs/guides/workflows/read.md", self.frontmatter(
            {"workflow": {"actions": [action]}}))
        self.write("docs/concepts/domain/layer.md", self.frontmatter({"title": "Layer"}))
        self.write("docs/concepts/domain/README.md", "| [Layer](layer.md) | Served data |\n")
        self.write("docs/contract.json", json.dumps({"paths": {"/layers": {"get": {}}}}))
        self.write("docs/sdk.md", "```python\nclient.query()\n```\n`honua layers`\n`honua_list_layers`\n")
        self.write("skills/read/SKILL.md", self.frontmatter({"name": "honua-read"}) + "Inspect the Layer.")
        sources = {"rest": ("GET /layers", "docs/contract.json"),
                   "sdk": ("client.query", "docs/sdk.md"),
                   "cli": ("honua layers", "docs/sdk.md"),
                   "mcp": ("honua_list_layers", "docs/sdk.md"),
                   "skill": ("honua-read", "skills/read/SKILL.md")}
        row = {"okf": "/guides/workflows/read.md#read", "verb": "inspect_layer",
               "objects": [{"concept": "/concepts/domain/layer.md", "terms": ["Layer"]}],
               "surfaces": {}}
        for surface, (operation, source) in sources.items():
            binding = {"verb": row["verb"], "operation": operation, "source": source}
            if surface == "skill":
                binding["evidence"] = "Inspect the Layer."
            row["surfaces"][surface] = {"bindings": [binding]}
        self.document = {"schemaVersion": 1, "stages": [row]}
        self.row = row

    @staticmethod
    def frontmatter(metadata):
        return "---\n" + json.dumps(metadata) + "\n---\n"

    def write(self, path, text):
        target = self.root / path
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text(text)

    def errors(self):
        return checker.check(self.root, self.document)

    def test_source_backed_bindings_pass(self):
        self.assertEqual([], self.errors())

    def test_missing_duplicate_and_unknown_stages_fail(self):
        for rows in ([], [self.row, self.row], [dict(self.row, okf="/unknown#read")]):
            with self.subTest(rows=rows):
                self.assertTrue(checker.check(self.root, {"schemaVersion": 1, "stages": rows}))

    def test_changed_stage_and_surface_verbs_fail(self):
        self.row["verb"] = "different_verb"
        self.assertTrue(self.errors())
        self.row["verb"] = "inspect_layer"
        self.row["surfaces"]["sdk"]["bindings"][0]["verb"] = "query_layer"
        self.assertTrue(self.errors())

    def test_retired_or_noncanonical_object_and_missing_concept_fail(self):
        for term in ("Published layer", "workspace", "Dataset"):
            self.row["objects"][0]["terms"] = [term]
            self.assertTrue(self.errors())
        self.row["objects"] = []
        self.assertTrue(self.errors())

    def test_changed_rest_route_or_method_fail(self):
        binding = self.row["surfaces"]["rest"]["bindings"][0]
        for operation in ("POST /layers", "GET /unknown"):
            binding["operation"] = operation
            self.assertTrue(self.errors())

    def test_removed_sdk_operation_and_partial_symbol_fail(self):
        self.write("docs/sdk.md", "client.queryAll()\n`honua layers`\n`honua_list_layers`\n")
        self.assertTrue(self.errors())

    def test_skill_name_and_evidence_drift_fail(self):
        self.write("skills/read/SKILL.md", self.frontmatter({"name": "honua-other"}) + "Inspect the Layer.")
        self.assertTrue(self.errors())
        self.write("skills/read/SKILL.md", self.frontmatter({"name": "honua-read"}) + "No instructions.")
        self.assertTrue(self.errors())

    def test_gap_is_explicit_and_cannot_hide_known_interface(self):
        self.row["surfaces"]["sdk"] = {"bindings": [], "gap": "Not recorded."}
        self.assertEqual([], self.errors())
        self.row["surfaces"]["rest"] = {"bindings": [], "gap": "Not recorded."}
        self.assertTrue(self.errors())

    def test_missing_surface_and_unexplained_empty_surface_fail(self):
        self.row["surfaces"]["sdk"] = {"bindings": []}
        self.assertTrue(self.errors())
        del self.row["surfaces"]["sdk"]
        self.assertTrue(self.errors())

    def test_external_source_is_rejected(self):
        self.row["surfaces"]["sdk"]["bindings"][0]["source"] = "../outside.md"
        self.assertTrue(self.errors())

    def test_retired_wire_spelling_requires_explicit_exception(self):
        binding = self.row["surfaces"]["sdk"]["bindings"][0]
        binding["operation"] = "read_workspace"
        self.write("docs/sdk.md", "read_workspace()\n`honua layers`\n`honua_list_layers`\n")
        self.assertTrue(self.errors())
        binding["literalReason"] = "Exact unreplaced wire identifier, not a user object name."
        self.assertEqual([], self.errors())

    def test_docs_gate_triggers_for_every_recorded_source(self):
        root = checker.REPO
        workflow = yaml.load((root / ".github/workflows/docs-link-gate.yml").read_text(),
                             Loader=yaml.BaseLoader)
        patterns = workflow["on"]["pull_request"]["paths"]
        document = json.loads((root / checker.MAP).read_text())
        sources = {binding["source"] for row in document["stages"]
                   for entry in row["surfaces"].values() for binding in entry["bindings"]}
        for source in sources:
            with self.subTest(source=source):
                self.assertTrue(any(fnmatch.fnmatchcase(source, pattern) for pattern in patterns))

    def test_repository_map_passes(self):
        self.assertEqual([], checker.check(checker.REPO, json.loads(
            (checker.REPO / checker.MAP).read_text())))


if __name__ == "__main__":
    unittest.main()
