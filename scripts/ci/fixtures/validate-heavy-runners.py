#!/usr/bin/env python3
"""Keep configurable compute routing separate from hosted control jobs and OS pins."""
from pathlib import Path
import unittest

import yaml

ROOT = Path(__file__).resolve().parents[3]
HEAVY_JOBS = {
    "build", "format", "dotnet-foundation-family-tests", "server-tests", "runtime-proofs",
    "mcp-certification", "mcp-llm-smoke", "postgres-compat",
    "core-package-compatibility", "aot-build", "docker-build", "quickstart-console-smoke",
}
HOSTED_JOBS = {
    "pr-template-check", "pr-readiness", "changes", "targeted-shards",
    "ci-router-validation", "dotnet-foundation-tests", "js-typecheck", "test-all",
    "merge-queue-gate", "ci-gate",
}
PINNED_JOBS = {
    "worker-gdal-tests", "python-integration-tests", "js-integration-tests",
    "esri-leaflet-browser-tests", "maplibre-compat",
}


class HeavyRunnerContractTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.jobs = yaml.safe_load((ROOT / ".github/workflows/ci.yml").read_text())["jobs"]

    def test_compute_jobs_use_operator_label_with_hosted_fallback(self):
        for name in HEAVY_JOBS:
            with self.subTest(job=name):
                self.assertEqual(self.jobs[name]["runs-on"],
                                 "${{ vars.HONUA_CI_HEAVY_RUNNER || 'ubuntu-latest' }}")

    def test_hosted_jobs_stay_on_ubuntu_latest(self):
        for name in HOSTED_JOBS:
            with self.subTest(job=name):
                self.assertEqual(self.jobs[name]["runs-on"], "ubuntu-latest")

    def test_native_and_browser_jobs_retain_supported_os_pins(self):
        for name in PINNED_JOBS:
            with self.subTest(job=name):
                self.assertEqual(self.jobs[name]["runs-on"], "ubuntu-24.04")

    def test_every_ci_job_has_a_runner_class(self):
        self.assertEqual(set(self.jobs), HEAVY_JOBS | HOSTED_JOBS | PINNED_JOBS)


if __name__ == "__main__":
    unittest.main()
