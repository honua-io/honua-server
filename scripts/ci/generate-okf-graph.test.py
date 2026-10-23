#!/usr/bin/env python3
"""Small synthetic tests: reject unsafe composition and preserve public boundaries."""
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest

from gis_workflow import WorkflowError, validate

spec = importlib.util.spec_from_file_location("graph", Path(__file__).with_name("generate-okf-graph.py"))
graph = importlib.util.module_from_spec(spec)
spec.loader.exec_module(graph)


def workflow():
    return {"profile": "honua.gis-workflow/v1", "intent": "Inspect a dataset",
            "inputs": [{"name": "source", "concept": "/dataset.md", "required": True}],
            "outputs": [{"name": "result", "concept": "/dataset.md", "from": "$actions.inspect.result"}],
            "constraints": ["Source remains unchanged"],
            "actions": [{"id": "inspect", "verb": "inspect", "effect": "read", "effectTarget": "source_data",
                         "inputs": [{"name": "source", "concept": "/dataset.md", "from": "$inputs.source"}],
                         "outputs": [{"name": "result", "concept": "/dataset.md"}],
                         "preconditions": ["Source exists"], "authorization": ["Read source"],
                         "checks": ["Schema observed"], "recovery": ["Report failure"], "idempotency": "Read only"}]}


class Composition(unittest.TestCase):
    def test_typed_dependency_does_not_claim_execution(self):
        result = validate(workflow(), {"dataset", "map"})
        self.assertEqual(["inspect"], result["dependencyOrder"])
        self.assertFalse(result["executionVerified"])

    def test_no_implicit_dataset_to_map_coercion(self):
        value = workflow()
        value["outputs"][0]["concept"] = "/map.md"
        with self.assertRaisesRegex(WorkflowError, "type mismatch"):
            validate(value, {"dataset", "map"})

    def test_approval_never_authorizes_source_writes(self):
        for effect in ("create", "update", "delete", "execute"):
            value = workflow()
            value["actions"][0].update(effect=effect, authorization=["Human approved"])
            with self.subTest(effect=effect), self.assertRaisesRegex(WorkflowError, "ADR-0028"):
                validate(value, {"dataset"})

    def test_cycles_and_missing_results_refused(self):
        for reference, error in (("$actions.inspect.result", "cycle"), ("$actions.unknown.result", "undeclared")):
            value = workflow()
            value["actions"][0]["inputs"][0]["from"] = reference
            with self.subTest(reference=reference), self.assertRaisesRegex(WorkflowError, error):
                validate(value, {"dataset"})

    def test_configuration_execution_requires_recovery(self):
        value = workflow()
        value["actions"][0].update(effect="execute", effectTarget="deployment")
        with self.assertRaisesRegex(WorkflowError, "recovery contract"):
            validate(value, {"dataset"})


class Graph(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.repo = Path(self.directory.name)
        (self.repo / "scripts/ci").mkdir(parents=True)
        (self.repo / "docs/private").mkdir(parents=True)
        manifest = {"root": "docs", "spec": {"version": "0.2"}, "excludedFiles": [],
                    "excludedDirs": [{"path": "docs/private"}]}
        (self.repo / "scripts/ci/okf-bundle.v1.json").write_text(json.dumps(manifest), encoding="utf-8")
        self.write("dataset.md", "---\ntype: concept\ntitle: Dataset\naliases: [layer]\n---\nHuman explanation.\n[Private](private/answer.md)\n[Map](map.md)\n")
        self.write("map.md", "---\ntype: concept\ntitle: Map\naliases: [layer]\n---\nMap explanation.\n")
        self.write("private/answer.md", "SECRET EXPECTED ANSWER")

    def write(self, path, content):
        (self.repo / "docs" / path).write_text(content, encoding="utf-8", newline="\n")

    def test_public_projection_preserves_human_source(self):
        source = (self.repo / "docs/dataset.md").read_bytes()
        result = graph.build(self.repo)
        self.assertEqual(source, (self.repo / "docs/dataset.md").read_bytes())
        self.assertEqual({"dataset", "map"}, {n["id"] for n in result["nodes"]})
        self.assertNotIn("SECRET EXPECTED ANSWER", json.dumps(result))
        self.assertIn({"source": "dataset", "target": "private/answer"}, result["unresolvedLinks"])

    def test_alias_search_retains_ambiguity(self):
        result = graph.context(graph.build(self.repo), "layer")
        self.assertEqual({"dataset", "map"}, {n["id"] for n in result["nodes"]})
        self.assertFalse(result["executionAuthorized"])

    def test_content_changes_invalidate_bundle(self):
        before = graph.build(self.repo)["bundleDigest"]
        with (self.repo / "docs/map.md").open("a", encoding="utf-8") as stream:
            stream.write("Changed human semantics.\n")
        self.assertNotEqual(before, graph.build(self.repo)["bundleDigest"])

    def test_contract_link_does_not_claim_schema_validation(self):
        import yaml
        value = workflow()
        value["actions"][0]["interfaces"] = [{"kind": "mcp", "operation": "observed_tool", "contract": "/map.md"}]
        self.write("workflow.md", "---\n" + yaml.safe_dump({"type": "guide", "workflow": value}) + "---\nGuide.\n")
        binding = graph.build(self.repo)["interfaceBindings"][0]
        self.assertEqual("documentation_reference_only", binding["validation"])
        self.assertTrue(binding["runtimeDiscoveryRequired"])

    def test_http_operation_must_exist_in_linked_spec(self):
        import yaml
        value = workflow()
        value["actions"][0]["interfaces"] = [{"kind": "http", "operation": "read", "contract": "/api.json", "method": "GET", "path": "/items"}]
        self.write("workflow.md", "---\n" + yaml.safe_dump({"type": "guide", "workflow": value}) + "---\nGuide.\n")
        self.write("api.json", json.dumps({"paths": {"/items": {"post": {}}}}))
        with self.assertRaisesRegex(WorkflowError, "absent"):
            graph.build(self.repo)
        self.write("api.json", json.dumps({"paths": {"/items": {"get": {}}}}))
        before = graph.build(self.repo)
        self.assertEqual("openapi_method_and_path", before["interfaceBindings"][0]["validation"])
        self.write("api.json", json.dumps({"paths": {"/items": {"get": {"description": "Contract changed"}}}}))
        self.assertNotEqual(before["bundleDigest"], graph.build(self.repo)["bundleDigest"])

    def test_fenced_examples_do_not_create_edges(self):
        self.assertEqual(["map.md"], graph.body_links("```md\n[No](fake.md)\n```\n[Yes](map.md)"))


if __name__ == "__main__":
    unittest.main()
