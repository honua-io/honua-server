"""The fixture must migrate before seeding and seed before any client runs."""
from pathlib import Path
import unittest

import yaml

ROOT = Path(__file__).resolve().parents[3]


class ClientCompatBootstrapTests(unittest.TestCase):
    def test_dependency_graph_preserves_migration_and_seed_barriers(self):
        services = yaml.safe_load((ROOT / "docker/client-compat/compose.yml").read_text())["services"]
        self.assertNotIn("seed", services["honua"]["depends_on"])
        self.assertEqual(services["seed"]["depends_on"]["honua"]["condition"], "service_healthy")
        lanes = {"gdal", "pyqgis", "openlayers", "cesium", "arcgis-stub", "geopandas",
                 "owslib", "duckdb", "r-sf", "pystac", "multidim-fixture"}
        for lane in lanes:
            with self.subTest(lane=lane):
                self.assertEqual(services[lane]["depends_on"]["seed"]["condition"],
                                 "service_completed_successfully")
                self.assertEqual(services[lane]["depends_on"]["honua"]["condition"], "service_healthy")
        self.assertNotIn("HONUA_SKIP_MIGRATIONS", services["honua"]["environment"])
        self.assertIn("/healthz/ready", services["honua"]["healthcheck"]["test"][-1])

    def test_auth_fixture_does_not_overwrite_transaction_scratch_layers(self):
        auth = (ROOT / "tests/seed/client-compat-auth-wave1.yaml").read_text()
        self.assertNotRegex(auth, r"\b10\b")
        services = yaml.safe_load((ROOT / "docker/client-compat/compose.yml").read_text())["services"]
        for lane in ("geopandas", "owslib"):
            self.assertEqual(services[lane]["environment"]["HONUA_CERT_VECTOR_COLLECTION_ID"], "2011")
        base = (ROOT / "tests/seed/client-compat-v1.sql").read_text()
        self.assertIn("(10, 'WFS-T Insert Scratch'", base)

    def test_producer_failure_is_red_after_evidence_upload(self):
        workflow = (ROOT / ".github/workflows/client-interop-nightly.yml").read_text()
        upload = workflow.index("- name: Upload lane evidence")
        failure = workflow.index("- name: Fail failed lane after preserving evidence")
        self.assertLess(upload, failure)
        self.assertIn("exit 1", workflow[failure:workflow.index("  baseline-diff:")])
        self.assertIn("always() && needs.prepare.result == 'success'", workflow)
        self.assertIn("--strict", workflow)
        self.assertNotIn("logs --no-color --tail=", workflow)
        multidim = (ROOT / ".github/workflows/multidim-raster-fixture.yml").read_text()
        self.assertNotIn("logs --no-color --tail=", multidim)


if __name__ == "__main__":
    unittest.main()
