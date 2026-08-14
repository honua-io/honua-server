"""Regression coverage for the observe-only native-image impact selector."""

from __future__ import annotations

import importlib.util
import json
import tempfile
import unittest
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

    def test_report_is_explicitly_non_mutating(self) -> None:
        result = report("src/Honua.Core/Models/Resource.cs")
        encoded = json.dumps(result)
        self.assertEqual(result["mode"], "observe")
        self.assertEqual(result["mutation"], "none")
        self.assertNotIn("cancel", encoded)


if __name__ == "__main__":
    unittest.main()
