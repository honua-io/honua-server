"""Publication guard and exact-image binding regressions; no Docker execution."""

import json
import subprocess
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

import yaml

import worker_publication as publication

ROOT = Path(__file__).resolve().parents[2]
SHA = "a" * 40
IMAGE_ID = "sha256:" + "b" * 64
ENVIRONMENT = {"GITHUB_SHA": SHA, "GITHUB_RUN_ID": "1234", "GITHUB_RUN_ATTEMPT": "1",
               "GITHUB_REPOSITORY": publication.REPOSITORY, "GITHUB_EVENT_NAME": "workflow_dispatch",
               "GITHUB_REF": "refs/heads/trunk", "PUBLISH_NIGHTLY": "true"}
IMAGE = {"Id": IMAGE_ID, "Os": "linux", "Architecture": "amd64", "Size": 1024,
         "Config": {"Labels": {"org.opencontainers.image.revision": SHA}}}


class WorkerPublicationTests(unittest.TestCase):
    def test_only_explicit_canonical_trunk_dispatch_can_publish(self):
        self.assertEqual(SHA, publication.require_publication(ENVIRONMENT)["source_sha"])
        for changes in ({"GITHUB_EVENT_NAME": "pull_request"}, {"GITHUB_EVENT_NAME": "schedule"},
                        {"GITHUB_REF": "refs/heads/feature"}, {"GITHUB_REF": "refs/tags/v1.0"},
                        {"PUBLISH_NIGHTLY": "false"}, {"PUBLISH_NIGHTLY": ""},
                        {"GITHUB_REPOSITORY": "other/honua-server"}, {"GITHUB_SHA": "trunk"},
                        {"GITHUB_RUN_ID": "../escape"}, {"GITHUB_RUN_ATTEMPT": "0"}):
            with self.subTest(changes=changes), patch.object(publication, "load_verified") as load:
                with self.assertRaises(ValueError):
                    publication.publish(Path("unused"), {**ENVIRONMENT, **changes})
                load.assert_not_called()

    def test_image_must_be_the_tested_platform_revision_and_config(self):
        with patch.object(publication, "command", return_value=json.dumps([IMAGE])):
            self.assertEqual(IMAGE_ID, publication.inspect_image("image", SHA, IMAGE_ID)["Id"])
        for change in ({"Id": "sha256:" + "c" * 64}, {"Architecture": "arm64"}, {"Os": "windows"},
                       {"Config": {"Labels": {"org.opencontainers.image.revision": "d" * 40}}}):
            with self.subTest(change=change), patch.object(publication, "command", return_value=json.dumps([{**IMAGE, **change}])):
                with self.assertRaises(ValueError):
                    publication.inspect_image("image", SHA, IMAGE_ID)

    def test_actual_trx_cases_must_all_pass_and_be_nonempty(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "test.trx"
            for outcomes in (["Passed"], [], ["NotExecuted"], ["Passed", "Failed"], ["Passed", "NotExecuted"]):
                with self.subTest(outcomes=outcomes):
                    path.write_text('<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><Results>'
                                    + "".join(f'<UnitTestResult outcome="{outcome}"/>' for outcome in outcomes)
                                    + '</Results></TestRun>', encoding="utf-8")
                    if outcomes == ["Passed"]:
                        self.assertEqual(1, publication.test_receipt(path)["passed"])
                    else:
                        with self.assertRaises(ValueError):
                            publication.test_receipt(path)

    def bundle(self, root):
        (root / "worker-image.tar").write_bytes(b"tested-image-archive")
        (root / "trivy-worker-gdal.sarif").write_text('{"runs":[{}]}', encoding="utf-8")
        for name in publication.TRXS:
            (root / name).write_text('<TestRun><Results><UnitTestResult outcome="Passed"/></Results></TestRun>', encoding="utf-8")
        receipt = {"schema": "honua.worker-publication-bundle.v1", "qualification": False,
                   **publication.identity(ENVIRONMENT), "image_id": IMAGE_ID, "platform": "linux/amd64",
                   "archive_sha256": publication.digest(root / "worker-image.tar"),
                   "scan_sha256": publication.digest(root / "trivy-worker-gdal.sarif"),
                   "tests": [publication.test_receipt(root / name) for name in publication.TRXS],
                   "scan_policy": {"severity": ["CRITICAL", "HIGH"], "ignore_unfixed": True, "exit_code": 1}}
        publication.write_json(root / "bundle.json", receipt)
        return receipt

    def test_bundle_corruption_or_cross_run_reuse_fails_before_docker_load(self):
        for mutation in ("archive", "scan", "tests", "source", "run", "attempt", "policy", "qualification"):
            with self.subTest(mutation=mutation), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                receipt = self.bundle(root)
                if mutation == "archive":
                    (root / "worker-image.tar").write_bytes(b"different-image")
                elif mutation == "scan":
                    (root / "trivy-worker-gdal.sarif").write_text("{}", encoding="utf-8")
                elif mutation == "tests":
                    (root / publication.TRXS[0]).write_text("<TestRun/>", encoding="utf-8")
                else:
                    key, value = {"source": ("source_sha", "e" * 40), "run": ("run_id", "999"),
                                  "attempt": ("run_attempt", "2"), "policy": ("scan_policy", {}),
                                  "qualification": ("qualification", True)}[mutation]
                    receipt[key] = value
                    publication.write_json(root / "bundle.json", receipt)
                with patch.object(publication.subprocess, "run") as run:
                    with self.assertRaises(ValueError):
                        publication.load_verified(root, ENVIRONMENT)
                    run.assert_not_called()

    def test_verified_archive_is_loaded_without_rebuilding(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            receipt = self.bundle(root)
            with patch.object(publication.subprocess, "run") as run, patch.object(publication, "inspect_image") as inspect:
                self.assertEqual(receipt, publication.load_verified(root, ENVIRONMENT))
                run.assert_called_once_with(["docker", "load", "--input", str(root / "worker-image.tar")], check=True)
                inspect.assert_called_once_with(publication.LOCAL_IMAGE, SHA, IMAGE_ID)

    def test_remote_manifest_must_contain_exact_tested_config(self):
        reference = publication.SUBJECT + "@sha256:" + "f" * 64
        for config, expected in ((IMAGE_ID, True), ("sha256:" + "c" * 64, False)):
            with self.subTest(config=config), tempfile.TemporaryDirectory() as directory:
                with patch.object(publication, "command", return_value=json.dumps({"config": {"digest": config}})), \
                     patch.object(publication.subprocess, "run") as run, patch.object(publication, "inspect_image") as inspect:
                    if expected:
                        publication.verify_remote(Path(directory), {"image_id": IMAGE_ID, "source_sha": SHA}, reference)
                        run.assert_called_once_with(["docker", "pull", reference], check=True)
                        inspect.assert_called_once_with(reference, SHA, IMAGE_ID)
                    else:
                        with self.assertRaises(ValueError):
                            publication.verify_remote(Path(directory), {"image_id": IMAGE_ID, "source_sha": SHA}, reference)
                        run.assert_not_called()

    def test_workflow_preserves_gates_and_restricts_publication_authority(self):
        workflow = yaml.safe_load((ROOT / ".github/workflows/worker-gdal-image.yml").read_text(encoding="utf-8"))
        events = workflow.get("on", workflow.get(True))
        self.assertIs(False, events["workflow_dispatch"]["inputs"]["publish_nightly"]["default"])
        policy = json.loads((ROOT / ".github/native-image-impact.json").read_text(encoding="utf-8"))
        self.assertEqual(events["pull_request"]["paths"], policy["legacy"]["worker_patterns"])
        for path in ("scripts/ci/worker_publication.py", "scripts/ci/test_worker_publication.py",
                     "scripts/qualification/gp_native_observer.py",
                     "scripts/qualification/verify_native_entry_image.py",
                     "tests/dotnet/Honua.Worker.Gdal.Tests/NativeEntryQualificationTests.cs"):
            self.assertIn(path, policy["worker_native_patterns"])
            self.assertIn(path, events["pull_request"]["paths"])
        jobs = workflow["jobs"]
        publish = jobs["publish-nightly"]
        for term in ("github.event_name == 'workflow_dispatch'", "inputs.publish_nightly == true",
                     "github.repository == 'honua-io/honua-server'", "github.ref == 'refs/heads/trunk'",
                     "needs.build-and-scan.result == 'success'", "needs.publish-sarif.result == 'success'"):
            self.assertIn(term, publish["if"])
        self.assertEqual(["build-and-scan", "publish-sarif"], publish["needs"])
        self.assertNotIn("id-token", workflow["permissions"])
        self.assertNotIn("attestations", workflow["permissions"])
        self.assertEqual("write", publish["permissions"]["id-token"])
        steps = jobs["build-and-scan"]["steps"]
        names = [step.get("name") for step in steps]
        build = steps[names.index("Build isolated native worker")]["with"]
        self.assertEqual("native-tools", build["no-cache-filters"])
        self.assertNotIn("no-cache", build)
        export = names.index("Prepare exact tested worker publication bundle")
        for name in ("Prove managed native entry cleanup and loader floor",
                     "Prove actual image native entry hold and cancellation",
                     "Verify native tools and smoke the real worker entrypoint",
                     "Prove the container handoff cases ran, not skipped", "Prove the PDAL cases ran, not skipped",
                     "Prove native public API submit poll and decoded output", "Enforce worker vulnerability policy"):
            self.assertLess(names.index(name), export)
        self.assertEqual("github.event_name == 'workflow_dispatch'", steps[export]["if"])
        scan = steps[names.index("Enforce worker vulnerability policy")]["with"]
        self.assertEqual("1", scan["exit-code"])
        self.assertEqual("CRITICAL,HIGH", scan["severity"])
        self.assertIs(True, scan["ignore-unfixed"])
        published_steps = json.dumps(publish["steps"])
        self.assertNotIn("docker/build-push-action", published_steps)
        self.assertIn("--source-ref refs/heads/trunk --source-digest", published_steps)
        self.assertIn("worker-gdal-image.yml", published_steps)
        self.assertEqual("", events["workflow_dispatch"]["inputs"]["candidate_sha"]["default"])
        self.assertEqual("${{ inputs.candidate_sha }}", publish["env"]["CANDIDATE_SHA"])
        self.assertLess(names.index("Resolve worker source commit"), names.index("Build isolated native worker"))
        self.assertLess(names.index("Resolve worker source commit"),
                        names.index("Check out the candidate the worker image must match"))
        self.assertLess(names.index("Check out the candidate the worker image must match"),
                        names.index("Build isolated native worker"))
        workflow_text = (ROOT / ".github/workflows/worker-gdal-image.yml").read_text(encoding="utf-8")
        self.assertIn("HONUA_GIT_SHA=${{ steps.source.outputs.source_sha }}", workflow_text)
        self.assertIn('--source-sha "${{ steps.source.outputs.source_sha }}"', workflow_text)
        self.assertIn("trivy-worker-gdal-${{ github.sha }}", workflow_text)
        self.assertIn("python3 scripts/ci/worker_publication.py resolve-source", workflow_text)
        self.assertEqual(1, workflow_text.count("resolve-source"))
        contract = jobs["publication-contract"]
        self.assertNotIn("candidate", json.dumps(contract["steps"]))

    def test_empty_candidate_preserves_dispatch_commit(self):
        with patch.object(publication, "candidate_reachable") as reachable:
            self.assertEqual(SHA, publication.require_publication({**ENVIRONMENT, "CANDIDATE_SHA": ""})["source_sha"])
            self.assertEqual(SHA, publication.require_publication({**ENVIRONMENT, "CANDIDATE_SHA": SHA})["source_sha"])
            reachable.assert_not_called()

    def test_candidate_must_be_full_lowercase_hex_on_trunk(self):
        for value in ("trunk", "A" * 40, "a" * 39, "g" * 40, "a" * 41):
            with self.subTest(value=value), patch.object(publication, "candidate_reachable") as reachable:
                with self.assertRaises(ValueError):
                    publication.require_publication({**ENVIRONMENT, "CANDIDATE_SHA": value})
                reachable.assert_not_called()
        with patch.object(publication, "candidate_reachable") as reachable:
            with self.assertRaises(ValueError):
                publication.require_publication({**ENVIRONMENT, "CANDIDATE_SHA": "c" * 40, "GITHUB_REF": "refs/heads/feature"})
            reachable.assert_not_called()

    def test_unreachable_candidate_is_refused_before_registry_mutation(self):
        environment = {**ENVIRONMENT, "CANDIDATE_SHA": "e" * 40}
        with patch.object(publication, "candidate_reachable", return_value=False), \
             patch.object(publication, "load_verified") as load, \
             patch.object(publication.subprocess, "run") as run:
            with self.assertRaisesRegex(ValueError, "not reachable from trunk"):
                publication.publish(Path("unused"), environment)
            load.assert_not_called()
            run.assert_not_called()

    def test_reachable_candidate_binds_publication_to_that_commit(self):
        candidate = "c" * 40
        environment = {**ENVIRONMENT, "CANDIDATE_SHA": candidate}
        with tempfile.TemporaryDirectory() as directory, \
             patch.object(publication, "candidate_reachable", return_value=True) as reachable, \
             patch.object(publication, "load_verified", return_value={"image_id": IMAGE_ID, "source_sha": candidate}), \
             patch.object(publication.subprocess, "run"), \
             patch.object(publication, "command", return_value="sha256:" + "f" * 64), \
             patch.object(publication, "verify_remote"):
            root = Path(directory)
            (root / "bundle.json").write_text("{}", encoding="utf-8")
            environment = {**environment, "GITHUB_OUTPUT": str(root / "out.txt")}
            self.assertEqual(candidate, publication.require_publication(environment)["source_sha"])
            publication.publish(root, environment)
            receipt = json.loads((root / "publication.json").read_text(encoding="utf-8"))
            self.assertEqual(candidate, receipt["source_sha"])
            self.assertIn(f":nightly-{candidate}-1234-1", receipt["tag"])
            self.assertEqual(candidate, publication.resolve_source(environment))
            self.assertIn(f"source_sha={candidate}\n", (root / "out.txt").read_text(encoding="utf-8"))
            reachable.assert_called()

    def test_compare_status_accepts_only_trunk_ancestors(self):
        for status, accepted in (("ahead", True), ("identical", True), ("behind", False), ("diverged", False)):
            with self.subTest(status=status):
                with patch.object(publication, "_local_ancestor", return_value=None), \
                     patch.object(publication, "command", return_value=json.dumps({"status": status})):
                    self.assertEqual(accepted, publication.candidate_reachable("c" * 40, ENVIRONMENT))

    def test_local_history_refuses_without_calling_the_api(self):
        def run(args, check, capture_output):
            if args[1] == "merge-base":
                return subprocess.CompletedProcess(args, 1)
            return subprocess.CompletedProcess(args, 0)

        with patch.object(publication.subprocess, "run", side_effect=run), \
             patch.object(publication, "command") as command:
            self.assertFalse(publication.candidate_reachable("c" * 40, ENVIRONMENT))
            command.assert_not_called()


if __name__ == "__main__":
    unittest.main()
