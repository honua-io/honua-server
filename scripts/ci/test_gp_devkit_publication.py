"""Publication identity for the GP devkit. Candidate packs are not published."""

import hashlib
import json
import tempfile
import unittest
import zipfile
from pathlib import Path
from unittest.mock import patch

import yaml

import gp_devkit_publication as publication

ROOT = Path(__file__).resolve().parents[2]
SHA = "a" * 40
ENVIRONMENT = {
    "GITHUB_SHA": SHA,
    "GITHUB_RUN_ID": "1234",
    "GITHUB_RUN_ATTEMPT": "1",
    "GITHUB_REPOSITORY": publication.REPOSITORY,
    "GITHUB_EVENT_NAME": "workflow_dispatch",
    "GITHUB_REF": "refs/heads/trunk",
    "PUBLISH_DEVKIT": "true",
    "NUGET_AUTH_TOKEN": "token-must-not-leak",
}


def nupkg(directory, version="2026.1.1", package_id="Honua.Geoprocessing.Cli"):
    path = Path(directory) / f"{package_id}.{version}.nupkg"
    nuspec = f"""<?xml version="1.0" encoding="utf-8"?>
<package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
  <metadata>
    <id>{package_id}</id>
    <version>{version}</version>
  </metadata>
</package>
"""
    with zipfile.ZipFile(path, "w") as package:
        package.writestr(f"{package_id}.nuspec", nuspec)
    return path


class GpDevkitPublicationTests(unittest.TestCase):
    def test_candidate_pack_does_not_establish_published_availability(self):
        with tempfile.TemporaryDirectory() as directory:
            for version in ("2026.1.1", "2026.1.1-installed.99.1"):
                with self.subTest(version=version):
                    receipt = publication.candidate_receipt(nupkg(directory, version))
                    self.assertEqual(publication.SCHEMA, receipt["schema"])
                    self.assertEqual(publication.PACKAGE_ID, receipt["package_id"])
                    self.assertEqual(version, receipt["version"])
                    self.assertEqual(publication.FEED, receipt["feed"])
                    self.assertEqual("honua-gp-devkit", receipt["tool_command"])
                    self.assertIs(False, receipt["published"])
                    self.assertIs(False, receipt["establishes_published_availability"])
                    self.assertEqual("honua-release#615", receipt["handed_to"])
                    self.assertIn("installed", receipt["reason"].lower())

    def test_publish_requires_explicit_trunk_dispatch_and_omits_the_token(self):
        with tempfile.TemporaryDirectory() as directory:
            package = nupkg(directory)
            output = Path(directory) / "publication.json"
            pushed = []

            def push(command, check):
                self.assertTrue(check)
                self.assertEqual("dotnet", command[0])
                self.assertIn("nuget", command)
                self.assertIn("push", command)
                self.assertIn(str(package), command)
                self.assertIn(publication.FEED, command)
                self.assertIn("token-must-not-leak", command)
                pushed.append(command)

            with patch.object(publication.subprocess, "run", side_effect=push):
                receipt = publication.publish(package, ENVIRONMENT, output)
            self.assertEqual(1, len(pushed))
            self.assertIs(True, receipt["published"])
            self.assertIs(True, receipt["establishes_published_availability"])
            self.assertEqual(SHA, receipt["source_sha"])
            self.assertEqual(publication.FEED, receipt["feed"])
            self.assertEqual(hashlib.sha256(package.read_bytes()).hexdigest(), receipt["nupkg_sha256"])
            self.assertEqual("honua-release#615", receipt["handed_to"])
            self.assertIs(False, receipt["host_debug"]["establishes_published_availability"])
            text = output.read_text(encoding="utf-8")
            self.assertNotIn("token-must-not-leak", text)
            self.assertEqual(receipt, json.loads(text))

        for changes in (
            {"GITHUB_EVENT_NAME": "pull_request"},
            {"GITHUB_EVENT_NAME": "push"},
            {"GITHUB_REF": "refs/heads/feature"},
            {"PUBLISH_DEVKIT": "false"},
            {"PUBLISH_DEVKIT": ""},
            {"GITHUB_REPOSITORY": "other/honua-server"},
            {"NUGET_AUTH_TOKEN": ""},
        ):
            with self.subTest(changes=changes), tempfile.TemporaryDirectory() as directory:
                package = nupkg(directory)
                with patch.object(publication.subprocess, "run") as run:
                    with self.assertRaises(ValueError):
                        publication.publish(package, {**ENVIRONMENT, **changes}, Path(directory) / "out.json")
                    run.assert_not_called()

    def test_installed_suffix_and_wrong_package_are_not_pushed(self):
        with tempfile.TemporaryDirectory() as directory:
            for version, package_id in (("2026.1.1-installed.1.1", publication.PACKAGE_ID), ("2026.1.1", "Other.Package")):
                with self.subTest(version=version, package_id=package_id):
                    with patch.object(publication.subprocess, "run") as run:
                        with self.assertRaises(ValueError):
                            publication.publish(nupkg(directory, version, package_id), ENVIRONMENT, Path(directory) / "out.json")
                        run.assert_not_called()

    def test_host_debug_handoff_requires_three_oses_and_is_not_publication(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            systems = {"linux": "Linux", "windows": "Windows", "macos": "Darwin"}
            paths = []
            for name, system in systems.items():
                with patch.object(publication.platform, "system", return_value=system):
                    path = root / f"{name}.json"
                    publication.write_host_debug_receipt(path, "2026.1.1")
                    paths.append(path)
                    receipt = json.loads(path.read_text(encoding="utf-8"))
                    self.assertEqual(name, receipt["os"])
                    self.assertIs(False, receipt["published"])
                    self.assertIs(False, receipt["establishes_published_availability"])
            handoff = publication.host_debug_handoff(paths)
            self.assertEqual(publication.HANDOFF_SCHEMA, handoff["schema"])
            self.assertIs(False, handoff["published"])
            self.assertIs(False, handoff["establishes_published_availability"])
            self.assertEqual(["linux", "macos", "windows"], handoff["operating_systems"])
            self.assertEqual("honua-release#615", handoff["handed_to"])
            self.assertEqual(publication.HOST_DEBUG_WORKFLOW, handoff["workflow"])

            claimed = json.loads(paths[0].read_text(encoding="utf-8"))
            claimed["published"] = True
            paths[0].write_text(json.dumps(claimed), encoding="utf-8")
            with self.assertRaises(ValueError):
                publication.host_debug_handoff(paths)
            paths[0].unlink()
            with self.assertRaises(ValueError):
                publication.host_debug_handoff(paths[1:])

    def test_workflows_separate_publication_from_installed_candidates(self):
        publish_workflow = yaml.safe_load((ROOT / ".github/workflows/gp-devkit-publish.yml").read_text(encoding="utf-8"))
        events = publish_workflow.get("on", publish_workflow.get(True))
        self.assertEqual({"workflow_dispatch"}, set(events))
        self.assertIs(False, events["workflow_dispatch"]["inputs"]["publish"]["default"])
        job = publish_workflow["jobs"]["publish"]
        for term in (
            "github.event_name == 'workflow_dispatch'",
            "github.ref == 'refs/heads/trunk'",
            "github.repository == 'honua-io/honua-server'",
            "inputs.publish == true",
        ):
            self.assertIn(term, job["if"])
        self.assertEqual("write", job["permissions"]["packages"])
        rendered = json.dumps(job["steps"])
        self.assertIn("scripts/ci/gp_devkit_publication.py publish", rendered)
        self.assertNotIn("VersionSuffix", rendered)
        self.assertNotIn("dotnet nuget push", rendered)

        installed = (ROOT / ".github/workflows/gp-installed-authoring.yml").read_text(encoding="utf-8")
        self.assertNotIn("dotnet nuget push", installed)
        self.assertIn("gp_devkit_publication.py", installed)
        self.assertIn("--host-debug-receipt", installed)
        self.assertIn("gp_devkit_publication.py handoff", installed)
        self.assertIn("scripts/ci/gp_devkit_publication.py", installed)

        verifier = (ROOT / "scripts/ci/verify-gp-installed-authoring.py").read_text(encoding="utf-8")
        self.assertIn("write_host_debug_receipt", verifier)
        self.assertIn("--host-debug-receipt", verifier)

        guide = (ROOT / "docs/guides/query-analyze/gp-devkit-authoring.md").read_text(encoding="utf-8")
        self.assertIn(publication.FEED, guide)
        self.assertIn("do not establish published availability", guide)
        self.assertIn("honua-release#615", guide)

        inventory = (ROOT / "docs/internal/ci/workflow-inventory.md").read_text(encoding="utf-8")
        self.assertIn("`gp-devkit-publish.yml`", inventory)


if __name__ == "__main__":
    unittest.main()
