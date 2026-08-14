"""Regression coverage for the observe-only native-image impact selector."""

from __future__ import annotations

import importlib.util
import json
import tempfile
import unittest
from argparse import Namespace
from pathlib import Path


ROOT = Path(__file__).resolve().parents[3]
SCRIPT = ROOT / "scripts" / "ci" / "native-image-impact.py"
SPEC = importlib.util.spec_from_file_location("native_image_impact", SCRIPT)
assert SPEC and SPEC.loader
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)
POLICY = MODULE.load_policy(ROOT / ".github" / "native-image-impact.json")


def report(*paths: str):
    return MODULE.evaluate(ROOT, POLICY, paths, base_sha="base", head_sha="head")


class NativeImageImpactTests(unittest.TestCase):
    def test_server_project_change_selects_all_serving_variants_only(self) -> None:
        result = report("src/Honua.Protocols.OData/Features/Query.cs")
        self.assertTrue(result["candidate"]["risk_classes"]["server_aot_compile"])
        self.assertEqual(
            result["candidate"]["serving_variants"],
            {"generic": True, "lambda": True, "functions": True},
        )
        self.assertFalse(result["candidate"]["worker_build"])

    def test_shared_core_change_selects_serving_and_worker(self) -> None:
        result = report("src/Honua.Core/Models/Resource.cs")
        self.assertTrue(result["candidate"]["risk_classes"]["server_aot_compile"])
        self.assertTrue(result["candidate"]["risk_classes"]["worker_managed_graph"])
        self.assertTrue(result["candidate"]["worker_build"])

    def test_analyzer_change_is_a_global_managed_input(self) -> None:
        result = report("src/Honua.Analyzers/Rules/Rule.cs")
        self.assertTrue(result["candidate"]["risk_classes"]["server_aot_compile"])
        self.assertTrue(result["candidate"]["risk_classes"]["worker_managed_graph"])

    def test_variant_dockerfiles_remain_independent(self) -> None:
        generic = report("docker/Dockerfile.aot")
        self.assertEqual(
            generic["candidate"]["serving_variants"],
            {"generic": True, "lambda": False, "functions": False},
        )
        functions = report("docker/cloud/azure-functions/host.json")
        self.assertEqual(
            functions["candidate"]["serving_variants"],
            {"generic": False, "lambda": False, "functions": True},
        )

    def test_worker_rootfs_change_does_not_select_serving(self) -> None:
        result = report("docker/worker-gdal/Dockerfile")
        self.assertFalse(any(result["candidate"]["serving_variants"].values()))
        self.assertFalse(result["candidate"]["risk_classes"]["worker_managed_graph"])
        self.assertTrue(result["candidate"]["risk_classes"]["worker_native_rootfs"])
        self.assertTrue(result["candidate"]["risk_classes"]["worker_vulnerability"])

    def test_vulnerability_policy_change_closes_legacy_trigger_gap(self) -> None:
        result = report(".trivyignore")
        self.assertFalse(result["legacy"]["worker_trigger"])
        self.assertTrue(result["candidate"]["worker_build"])
        self.assertTrue(result["comparison"]["worker_candidate_only"])

    def test_solution_and_test_fixture_changes_expose_legacy_waste(self) -> None:
        solution = report("Honua.sln")
        self.assertTrue(solution["legacy"]["serving_trigger"])
        self.assertTrue(solution["legacy"]["worker_trigger"])
        self.assertFalse(any(solution["candidate"]["serving_variants"].values()))
        self.assertFalse(solution["candidate"]["worker_build"])
        fixture = report("tests/fixtures/ai-builder/example.json")
        self.assertTrue(fixture["comparison"]["serving_legacy_only"])

    def test_embedded_catalog_is_discovered_as_serving_input(self) -> None:
        result = report("docs/gis/data/feature-catalog.json")
        self.assertTrue(result["candidate"]["risk_classes"]["server_aot_compile"])
        self.assertTrue(all(result["candidate"]["serving_variants"].values()))

    def test_exact_embedded_ai_fixture_is_not_confused_with_fixture_directory(self) -> None:
        embedded = report("tests/fixtures/ai-builder/spatial-query-contract-v1.json")
        unrelated = report("tests/fixtures/ai-builder/new-test-only-fixture.json")
        self.assertTrue(embedded["candidate"]["risk_classes"]["server_aot_compile"])
        self.assertTrue(unrelated["comparison"]["serving_legacy_only"])

    def test_pack_only_readme_does_not_select_runtime_images(self) -> None:
        result = report("README.md")
        self.assertFalse(any(result["candidate"]["risk_classes"].values()))

    def test_unrelated_documentation_selects_no_image(self) -> None:
        result = report("docs/internal/ci/unrelated.md")
        self.assertFalse(any(result["candidate"]["risk_classes"].values()))
        self.assertFalse(result["legacy"]["serving_trigger"])
        self.assertFalse(result["legacy"]["worker_trigger"])

    def test_project_graph_is_transitive_and_conservative_about_conditions(self) -> None:
        result = report("src/Honua.Db/Oracle/OracleStore.cs")
        projects = result["graphs"]["serving"]["projects"]
        self.assertIn("src/Honua.Db/Oracle/Honua.Oracle.csproj", projects)
        self.assertIn("src/Honua.Core.Abstractions/Honua.Core.Abstractions.csproj", projects)
        self.assertTrue(result["candidate"]["risk_classes"]["server_aot_compile"])

    def test_invalid_or_escaping_project_reference_fails_closed(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            repo = Path(directory)
            project = repo / "src" / "App" / "App.csproj"
            project.parent.mkdir(parents=True)
            project.write_text(
                '<Project><ItemGroup><ProjectReference Include="../../../outside.csproj" />'
                '</ItemGroup></Project>',
                encoding="utf-8",
            )
            with self.assertRaises(MODULE.PolicyError):
                MODULE.project_closure(repo, "src/App/App.csproj", [])

    def test_checked_in_policy_matches_authoritative_workflow_triggers(self) -> None:
        MODULE.validate_policy(ROOT, POLICY)

    def test_observer_permissions_reject_semantic_write_variants(self) -> None:
        workflow = (
            ROOT / ".github" / "workflows" / "native-image-impact-observe.yml"
        ).read_text(encoding="utf-8")
        MODULE.validate_observer_permissions(workflow)
        canonical = """permissions:
  actions: read
  checks: read
  contents: read
  pull-requests: read
"""
        unsafe = (
            workflow.replace("  actions: read", '  actions: "write" # required', 1),
            workflow.replace(canonical, "permissions: write-all\n", 1),
            workflow.replace(
                "  observe:\n    name:",
                "  observe:\n    permissions:\n      contents: write\n    name:",
                1,
            ),
        )
        for source in unsafe:
            with self.subTest(source=source[:100]), self.assertRaises(MODULE.PolicyError):
                MODULE.validate_observer_permissions(source)

    def test_report_is_explicitly_non_mutating(self) -> None:
        result = report("src/Honua.Core/Models/Resource.cs")
        encoded = json.dumps(result)
        self.assertEqual(result["mode"], "observe")
        self.assertEqual(result["mutation"], "none")
        self.assertNotIn("cancel", encoded)

    def test_changed_path_parser_is_nul_delimited_and_fail_closed(self) -> None:
        self.assertEqual(
            MODULE.parse_changed_path_output(b"docs/a.md\0src/App.cs\0"),
            ["docs/a.md", "src/App.cs"],
        )
        unsafe = [
            b"unterminated",
            b"line\nbreak\0",
            b"back\\slash\0",
            b"../escape\0",
            b"/absolute\0",
            b"duplicate\0duplicate\0",
            b"invalid-\xff\0",
        ]
        for raw in unsafe:
            with self.subTest(raw=raw), self.assertRaises(MODULE.PolicyError):
                MODULE.parse_changed_path_output(raw)

    def test_trusted_identity_is_bound_to_v2_report(self) -> None:
        result = MODULE.evaluate(
            ROOT,
            POLICY,
            ["docs/internal/ci/observer.md"],
            base_sha="a" * 40,
            head_sha="b" * 40,
            repository="honua-io/honua-server",
            pull_request=3204,
            policy_sha="c" * 40,
            policy_blob_sha="d" * 40,
            routing_policy_blob_sha="e" * 40,
            resolver_blob_sha="f" * 40,
            observer_workflow_blob_sha="1" * 40,
            policy_inputs_sha256="2" * 64,
            trusted_execution=MODULE.TRUSTED_EXECUTION,
            gate_workflow_path=".github/workflows/pr-gate.yml",
            gate_run_id=123,
            gate_run_attempt=2,
            gate_run_conclusion="success",
        )
        self.assertEqual(result["schema"], "honua.ci.native-image-impact-observation/v2")
        self.assertEqual(result["gate_run_head_sha"], "b" * 40)
        self.assertEqual(result["policy_sha"], "c" * 40)
        self.assertEqual(result["routing_policy_blob_sha"], "e" * 40)
        self.assertEqual(result["policy_inputs_sha256"], "2" * 64)
        self.assertEqual(len(result["changed_paths_sha256"]), 64)

    def test_observation_identity_requires_complete_content_addressing(self) -> None:
        args = Namespace(
            base="a" * 40,
            head="b" * 40,
            repository="honua-io/honua-server",
            pr=3204,
            policy_sha="c" * 40,
            policy_blob_sha="d" * 40,
            routing_policy_blob_sha="e" * 40,
            resolver_blob_sha="f" * 40,
            observer_workflow_blob_sha="1" * 40,
            policy_inputs_sha256="2" * 64,
            trusted_execution=MODULE.TRUSTED_EXECUTION,
            gate_workflow_path=".github/workflows/pr-gate.yml",
            gate_run_id=123,
            gate_run_attempt=2,
            gate_run_conclusion="success",
        )
        identity = MODULE.observation_identity(args)
        self.assertEqual(identity["observer_workflow_blob_sha"], "1" * 40)
        self.assertEqual(identity["policy_inputs_sha256"], "2" * 64)

        for attribute, invalid in (
            ("routing_policy_blob_sha", "short"),
            ("policy_inputs_sha256", "short"),
            ("trusted_execution", "candidate-controlled"),
            ("gate_run_conclusion", "in_progress"),
        ):
            original = getattr(args, attribute)
            setattr(args, attribute, invalid)
            with self.subTest(attribute=attribute), self.assertRaises(MODULE.PolicyError):
                MODULE.observation_identity(args)
            setattr(args, attribute, original)


if __name__ == "__main__":
    unittest.main()
