#!/usr/bin/env python3
"""Refusal controls for archived installation, credential, health and teardown proofs."""
import hashlib
import io
import json
from pathlib import Path
import re
import subprocess
import tarfile
import tempfile
import unittest
from unittest.mock import Mock, patch

import validate_shipped_quickstart as proof


class ArchiveTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.archive = self.root / "package.tar.gz"
        self.destination = self.root / "install"
        self.destination.mkdir()
        self.lock = dict.fromkeys(("server", "executor", "worker"),
                                  "ghcr.io/honua-io/honua-server@sha256:" + "a" * 64)
        self.files = {name: b"example" for name in proof.REQUIRED}
        self.lock.update(consoleWeb=proof.CONSOLE_WEB_IMAGE, consoleBundle={"sha256": "b" * 64, "files": 3})
        self.files.update({"console/index.html": b"html", "console/config.json": b"{}",
                           "console/_framework/dotnet.js": b"js"})
        self.files["images.json"] = json.dumps(self.lock).encode()

    def package(self, extra=None):
        with tarfile.open(self.archive, "w:gz") as archive:
            for name, contents in self.files.items():
                member = tarfile.TarInfo(name)
                member.size = len(contents)
                archive.addfile(member, io.BytesIO(contents))
            if extra:
                archive.addfile(extra)
        digest = hashlib.sha256(self.archive.read_bytes()).hexdigest()
        self.archive.with_name(self.archive.name + ".sha256").write_text(f"{digest}  {self.archive.name}\n")

    def test_checked_archive_extracts_immutable_lock(self):
        self.package()
        self.assertEqual(self.lock, proof.unpack(self.archive, self.destination))

    def test_changed_archive_fails_checksum(self):
        self.package()
        self.archive.write_bytes(self.archive.read_bytes() + b"changed")
        with self.assertRaisesRegex(ValueError, "checksum"):
            proof.unpack(self.archive, self.destination)

    def test_archive_without_console_overlay_is_not_a_customer_journey(self):
        del self.files["compose.console.yaml"]
        self.package()
        with self.assertRaisesRegex(ValueError, "required package files"):
            proof.unpack(self.archive, self.destination)

    def test_missing_or_mutable_console_edge_pin_is_refused(self):
        for value in ("", "ghcr.io/honua-io/honua-console:latest"):
            with self.subTest(value=value):
                self.lock["consoleWeb"] = value
                self.files["images.json"] = json.dumps(self.lock).encode()
                self.package()
                with self.assertRaisesRegex(ValueError, "static Console bundle"):
                    proof.unpack(self.archive, self.destination)

    def test_unsafe_archive_entries_are_refused_before_extraction(self):
        for name in ("../escape", "/absolute", "quickstart.py"):
            with self.subTest(name=name):
                self.package(tarfile.TarInfo(name))
                with self.assertRaises(ValueError):
                    proof.unpack(self.archive, self.destination)
        self.assertFalse((self.root / "escape").exists())

    def test_symlinks_are_not_installed(self):
        member = tarfile.TarInfo("linked-secret")
        member.type = tarfile.SYMTYPE
        member.linkname = "/etc/passwd"
        self.package(member)
        with self.assertRaisesRegex(ValueError, "unsafe entry"):
            proof.unpack(self.archive, self.destination)

    def test_package_cannot_carry_an_existing_environment_or_credentials(self):
        for name in (".env", "secrets/honua_admin_password"):
            with self.subTest(name=name):
                self.package(tarfile.TarInfo(name))
                with self.assertRaisesRegex(ValueError, "saved credentials"):
                    proof.unpack(self.archive, self.destination)


class AssertionTests(unittest.TestCase):
    def test_documented_publication_cannot_overwrite_initializer(self):
        self.assertEqual("publish-quickstart.py", proof.publication_filename({"query": "python publish-quickstart.py"}))
        for query in ("python quickstart.py", "python ../outside.py", "python p.py; echo ignored"):
            with self.subTest(query=query), self.assertRaises(ValueError):
                proof.publication_filename({"query": query})

    def test_any_secret_in_rendered_text_fails_without_repeating_it(self):
        secret = "private-example-credential"
        proof.check_no_secrets("password_file: /run/secrets/password", {"password": secret})
        with self.assertRaises(ValueError) as failure:
            proof.check_no_secrets("value=" + secret, {"password": secret})
        self.assertNotIn(secret, str(failure.exception))

    def test_second_install_requires_all_new_credentials(self):
        proof.check_fresh_credentials({"admin": "first", "database": "second"},
                                      {"admin": "third", "database": "fourth"})
        for current in ({"admin": "third"}, {"admin": "second", "database": "third"}, {}):
            with self.subTest(current=current), self.assertRaises(ValueError):
                proof.check_fresh_credentials({"admin": "first", "database": "second"}, current)

    def test_every_runtime_service_is_healthy_and_initializers_exit_zero(self):
        config = {"services": {"init": {}, "server": {"depends_on": {"init": {
            "condition": "service_completed_successfully"}}}}}
        states = [{"Service": "init", "State": "exited", "ExitCode": 0},
                  {"Service": "server", "State": "running", "Health": "healthy"}]
        proof.check_services(config, states)
        for invalid in (states[:1], states + [states[1]],
                        [dict(states[0], ExitCode=1), states[1]],
                        [states[0], dict(states[1], Health="unhealthy")]):
            with self.subTest(invalid=invalid), self.assertRaises(ValueError):
                proof.check_services(config, invalid)

    def test_stopped_containers_and_hidden_images_prevent_clean_host_claim(self):
        installation = Mock()
        installation.run.side_effect = lambda command: "stopped-id" if "--all" in command else ""
        for resource in ("container", "image"):
            with self.subTest(resource=resource), self.assertRaisesRegex(ValueError, "leftover " + resource):
                proof.check_empty(installation, (resource,))

    def test_leftover_volumes_prevent_teardown_claim(self):
        installation = Mock()
        installation.run.side_effect = lambda command: "volume-id" if "volume" in command else ""
        with self.assertRaisesRegex(ValueError, "leftover volume"):
            proof.check_empty(installation, ("container", "volume"))

    def test_subprocess_failure_withholds_captured_credentials(self):
        with tempfile.TemporaryDirectory() as directory:
            installation = proof.Installation(Path(directory))
            failure = subprocess.CompletedProcess([], 1, "private stdout", "private stderr")
            with patch.object(proof.subprocess, "run", return_value=failure), self.assertRaises(RuntimeError) as result:
                installation.run(["docker", "compose", "config"])
            self.assertNotIn("private", str(result.exception))

    def test_failure_diagnostics_only_emit_fixed_labels(self):
        result = subprocess.CompletedProcess([], 1, "private token", "failed to solve: private password")
        error = proof.CommandFailure(["python3", "quickstart.py"], result)
        self.assertEqual({"operation": "initialize", "exitCode": 1, "signals": ["build-failed"],
                          "localBuildStarted": False}, error.diagnostic)
        self.assertNotIn("private", json.dumps(error.diagnostic))

    def test_build_tooling_failure_emits_only_fixed_labels(self):
        result = subprocess.CompletedProcess([], 1, "private stdout",
            "postgres Warning pull access denied for private/image\n"
            "Compose build requires buildx 0.17 or later; private path")
        error = proof.CommandFailure(["python3", "quickstart.py"], result)
        self.assertEqual(["image-pull-denied", "buildx-required"], error.diagnostic["signals"])
        self.assertNotIn("private", json.dumps(error.diagnostic))

    def test_local_image_build_fallback_is_not_reported_as_a_registry_failure(self):
        denied = "Error response from daemon: pull access denied for {}, repository does not exist\n"
        building = "[+] Building 4.1s (9/9)\n"
        fallback = denied.format("honua-postgis-proj-grids") + building + "container private-db is unhealthy"
        self.assertEqual(["container-unhealthy"], proof.failure_signals(fallback))
        # Docker shortens official Hub images to a bare name; a real denial must still count.
        for output in (denied.format("busybox") + building, denied.format("ghcr.io/honua-io/private") + building,
                       building + denied.format("honua-postgis-proj-grids"),
                       denied.format("honua-postgis-proj-grids") + " Image honua-postgis-proj-grids Rebuilding"):
            with self.subTest(output=output):
                self.assertEqual(["image-pull-denied", "docker-daemon-error"], proof.failure_signals(output))
        self.assertEqual((True, False), (proof.local_build_started(fallback),
                                         proof.local_build_started("Image x Rebuilding")))

    def test_locally_built_images_match_the_packaged_compose_files(self):
        source = (proof.ROOT / "docs/get-started/quickstart.md").read_text()
        compose = re.search(r"<!-- docs-validation:install.compose mode=run -->\s*```yaml\n(.*?)\n```", source, re.S)
        texts = [compose.group(1)] + [(proof.ROOT / "docs/get-started" / name).read_text()
                                      for name in ("compose.executor.yaml", "compose.console.yaml")]
        built = set()
        for text in texts:
            lines = text.splitlines()
            for index, line in enumerate(lines):
                if line.strip().startswith("build:"):
                    indent = len(line) - len(line.lstrip())
                    for previous in reversed(lines[:index]):
                        if len(previous) - len(previous.lstrip()) < indent:
                            break
                        image = re.match(r"\s*image:\s*(\S+)", previous)
                        if image:
                            built.add(image.group(1).split(":")[0])
        self.assertEqual(set(proof.LOCALLY_BUILT_IMAGES), built)

    def test_container_start_failure_is_reported_without_its_path(self):
        for runtime in ("OCI runtime create failed: runc create failed", "OCI runtime exec failed"):
            with self.subTest(runtime=runtime):
                stderr = ("Error response from daemon: failed to create task for container: " + runtime +
                          ': exec: "/app/private": no such file or directory\n')
                error = proof.CommandFailure(["python3", "quickstart.py"], subprocess.CompletedProcess([], 1, "", stderr))
                self.assertEqual(["docker-daemon-error", "container-start-failed"], error.diagnostic["signals"])
                self.assertNotIn("private", json.dumps(error.diagnostic))

    def test_non_list_or_non_object_service_rows_are_withheld(self):
        withheld = dict.fromkeys(("service", "state", "health", "exitCode"), "withheld")
        with tempfile.TemporaryDirectory() as directory:
            installation = proof.Installation(Path(directory))
            with patch.object(installation, "compose", return_value='[null, "private", {"Service": "honua"}]'):
                self.assertEqual([withheld, withheld, dict(withheld, service="honua")], installation.service_states())
            with patch.object(installation, "compose", return_value="null"):
                self.assertEqual([withheld], installation.service_states())
            with patch.object(installation, "compose", return_value="not json"), self.assertRaises(ValueError):
                installation.service_states()

    def test_compose_version_is_reported_only_when_it_looks_like_a_version(self):
        with tempfile.TemporaryDirectory() as directory:
            installation = proof.Installation(Path(directory))
            for output, expected in (("2.40.3\n", "2.40.3"), ("v5.7.1", "v5.7.1"), ("private output", "withheld")):
                with patch.object(installation, "compose", return_value=output):
                    self.assertEqual(expected, installation.compose_version())

    def test_service_states_keep_only_bounded_identifiers(self):
        rows = [{"Service": "postgres", "State": "exited", "Health": "", "ExitCode": 3,
                 "Name": "private-name", "Labels": "private=secret", "Command": "private"},
                {"Service": "honua", "State": "running", "Health": "unhealthy: private detail", "ExitCode": 0}]
        with tempfile.TemporaryDirectory() as directory:
            installation = proof.Installation(Path(directory))
            for output in (json.dumps(rows), "\n".join(json.dumps(row) for row in rows)):
                with patch.object(installation, "compose", return_value=output):
                    states = installation.service_states()
                self.assertEqual([{"service": "postgres", "state": "exited", "health": "", "exitCode": 3},
                                  {"service": "honua", "state": "running", "health": "withheld", "exitCode": 0}],
                                 states)
                self.assertNotIn("private", json.dumps(states))

    def test_inherited_credentials_and_compose_overrides_are_not_forwarded(self):
        with tempfile.TemporaryDirectory() as directory, patch.dict(proof.os.environ, {
                "GH_TOKEN": "private", "COMPOSE_FILE": "/outside", "HONUA_ADMIN_PASSWORD": "private"}):
            installation = proof.Installation(Path(directory))
            self.assertNotIn("GH_TOKEN", installation.environment)
            self.assertNotIn("COMPOSE_FILE", installation.environment)
            self.assertNotIn("HONUA_ADMIN_PASSWORD", installation.environment)


class LifecycleTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.archive = self.root / "archive.tar.gz"
        self.archive.write_bytes(b"fixture")
        self.receipt = self.root / "receipt.json"
        self.input = self.root / "input.json"
        self.metadata = {"packageKind": "prospective-pr", "packageSourceSha": "b" * 40,
                         "serverSourceSha": "a" * 40, "publishedRunId": 123,
                         "publishedArchiveSha256": "c" * 64,
                         "archiveSha256": hashlib.sha256(self.archive.read_bytes()).hexdigest()}
        self.input.write_text(json.dumps(self.metadata))
        self.arguments = ["proof", "--archive", str(self.archive), "--document", str(self.root / "quickstart.md"),
                          "--receipt", str(self.receipt), "--input", str(self.input)]

    def execute(self, installation, outcome):
        with patch.object(proof.sys, "argv", self.arguments), \
                patch.object(proof, "public_blocks", return_value={"query": "python publish.py"}), \
                patch.object(proof, "unpack", return_value={"server": "image", "version": "fixture"}), \
                patch.object(proof, "Installation", side_effect=installation), \
                patch.object(proof, "exercise", side_effect=outcome), \
                patch.object(proof, "check_empty"):
            return proof.main()

    def test_failed_start_or_health_read_records_version_and_states(self):
        for command in (["python3", "quickstart.py"], ["docker", "compose", "ps"]):
            with self.subTest(command=command):
                failure = proof.CommandFailure(command, subprocess.CompletedProcess([], 1, "", ""))
                installation = Mock()
                installation.compose_version.return_value = "2.40.3"
                installation.service_states.return_value = [{"service": "honua"}]
                self.assertEqual(1, self.execute([installation], [failure]))
                receipt = json.loads(self.receipt.read_text())
                self.assertEqual("2.40.3", receipt["composeVersion"])
                self.assertEqual([{"service": "honua"}], receipt["serviceStates"])
                installation.down.assert_called_once()

    def test_unreadable_state_is_reported_unavailable_and_teardown_still_runs(self):
        failure = proof.CommandFailure(["python3", "quickstart.py"], subprocess.CompletedProcess([], 1, "", ""))
        installation = Mock()
        installation.compose_version.side_effect = subprocess.TimeoutExpired(["docker"], 60)
        installation.service_states.side_effect = ValueError("private")
        self.assertEqual(1, self.execute([installation], [failure]))
        receipt = json.loads(self.receipt.read_text())
        self.assertEqual(("unavailable", "unavailable"), (receipt["composeVersion"], receipt["serviceStates"]))
        self.assertEqual(failure.diagnostic, receipt["commandFailure"])
        self.assertNotIn("private", self.receipt.read_text())
        installation.down.assert_called_once()

    def test_unexpected_state_error_cannot_skip_teardown(self):
        failure = proof.CommandFailure(["python3", "quickstart.py"], subprocess.CompletedProcess([], 1, "", ""))
        installation = Mock()
        installation.compose_version.side_effect = AttributeError("unexpected")
        with self.assertRaises(AttributeError):
            self.execute([installation], [failure])
        installation.down.assert_called_once()

    def test_other_failures_do_not_query_service_state(self):
        configuration = proof.CommandFailure(["docker", "compose", "config"], subprocess.CompletedProcess([], 1, "", ""))
        for outcome in (RuntimeError("private failure"), configuration):
            with self.subTest(outcome=type(outcome).__name__):
                installation = Mock()
                self.assertEqual(1, self.execute([installation], [outcome]))
                installation.service_states.assert_not_called()
                installation.compose_version.assert_not_called()
                receipt = json.loads(self.receipt.read_text())
                self.assertNotIn("serviceStates", receipt)
                self.assertNotIn("composeVersion", receipt)

    def test_failed_install_still_tears_down_and_never_reports_success(self):
        installation = Mock()
        self.assertEqual(1, self.execute([installation], RuntimeError("withheld private failure")))
        installation.down.assert_called_once()
        receipt = json.loads(self.receipt.read_text())
        self.assertFalse(receipt["passed"])
        self.assertNotIn("private", self.receipt.read_text())

    def test_teardown_failure_invalidates_an_otherwise_passing_install(self):
        installation = Mock()
        installation.run.return_value = "a" * 40
        installation.credentials.return_value = {"admin": "first"}
        installation.down.side_effect = RuntimeError("cleanup failed")
        self.assertEqual(1, self.execute([installation], [{}]))
        self.assertFalse(json.loads(self.receipt.read_text())["passed"])

    def test_two_complete_installs_are_required_for_authenticated_journey(self):
        installations = [Mock(), Mock()]
        for index, installation in enumerate(installations):
            installation.run.return_value = "a" * 40
            installation.credentials.return_value = {"admin": str(index)}
        self.assertEqual(0, self.execute(installations, [{}, {}]))
        for installation in installations:
            installation.down.assert_called_once()
        receipt = json.loads(self.receipt.read_text())
        self.assertTrue(receipt["passed"])
        self.assertTrue(receipt["freshSecondInstallCredentials"])
        self.assertEqual(2, len(receipt["installations"]))
        self.assertTrue(receipt["customerJourneyComplete"])
        self.assertEqual("prospective-pr", receipt["packageKind"])
        self.assertEqual(123, receipt["publishedRunId"])
        self.assertEqual("c" * 64, receipt["publishedArchiveSha256"])

    def test_cleanup_failure_preserves_original_bounded_command_diagnostic(self):
        installation = Mock()
        installation.service_states.return_value = [{"service": "postgres", "state": "exited", "exitCode": 1}]
        installation.compose_version.return_value = "2.40.3"
        installation.down.side_effect = RuntimeError("private teardown output")
        failure = proof.CommandFailure(["python3", "quickstart.py"], subprocess.CompletedProcess(
            [], 1, "private stdout", "permission denied: private path"))
        self.assertEqual(1, self.execute([installation], [failure]))
        receipt = json.loads(self.receipt.read_text())
        self.assertEqual(failure.diagnostic, receipt["commandFailure"])
        self.assertEqual([{"service": "postgres", "state": "exited", "exitCode": 1}], receipt["serviceStates"])
        self.assertTrue(receipt["cleanupFailed"])
        self.assertNotIn("private", self.receipt.read_text())
        installation.down.assert_called_once()

    def test_second_failure_preserves_sanitized_first_installation_evidence(self):
        installations = [Mock(), Mock()]
        for index, installation in enumerate(installations):
            installation.run.return_value = "a" * 40
            installation.credentials.return_value = {"admin": f"private-{index}"}
        self.assertEqual(1, self.execute(installations, [{"publishQuery": True}, RuntimeError("private failure")]))
        receipt = json.loads(self.receipt.read_text())
        self.assertEqual([{"publishQuery": True}], receipt["installations"])
        self.assertEqual("fixture", receipt["version"])
        self.assertEqual("installation-2", receipt["failedStage"])
        self.assertFalse(receipt["passed"])
        self.assertFalse(receipt["customerJourneyComplete"])
        self.assertNotIn("private", self.receipt.read_text())
        for installation in installations:
            installation.down.assert_called_once()

    def test_mismatched_or_missing_image_revision_never_passes(self):
        for label in ("b" * 40, "<no value>"):
            installations = [Mock(), Mock()]
            for index, installation in enumerate(installations):
                installation.run.return_value = label
                installation.credentials.return_value = {"admin": str(index)}
            with self.subTest(label=label):
                self.assertEqual(1, self.execute(installations, [{}, {}]))
                self.assertFalse(json.loads(self.receipt.read_text())["passed"])

    def test_transported_archive_identity_cannot_be_changed(self):
        self.archive.write_bytes(b"different archive")
        with self.assertRaisesRegex(ValueError, "transported input identity"):
            proof.proof_inputs(self.input, self.archive)
        self.assertEqual(1, self.execute([], []))
        self.assertEqual("validate-input", json.loads(self.receipt.read_text())["failedStage"])

    def test_invalid_provenance_fields_are_refused(self):
        invalid = {"packageSourceSha": "short", "serverSourceSha": None, "publishedRunId": 0,
                   "archiveSha256": "invalid", "publishedArchiveSha256": "z" * 64, "packageKind": "shipped"}
        for key, value in invalid.items():
            with self.subTest(key=key):
                self.input.write_text(json.dumps(dict(self.metadata, **{key: value})))
                with self.assertRaises(ValueError):
                    proof.proof_inputs(self.input, self.archive)

    def test_candidate_identity_requires_the_official_archive_and_source(self):
        self.metadata["packageKind"] = "candidate"
        self.metadata["packageSourceSha"] = self.metadata["serverSourceSha"]
        self.input.write_text(json.dumps(self.metadata))
        with self.assertRaisesRegex(ValueError, "official publication"):
            proof.proof_inputs(self.input, self.archive)
        self.metadata["packageSourceSha"] = "b" * 40
        self.metadata["publishedArchiveSha256"] = self.metadata["archiveSha256"]
        self.input.write_text(json.dumps(self.metadata))
        with self.assertRaisesRegex(ValueError, "official publication"):
            proof.proof_inputs(self.input, self.archive)
        self.metadata["packageSourceSha"] = self.metadata["serverSourceSha"]
        self.input.write_text(json.dumps(self.metadata))
        self.assertEqual("candidate", proof.proof_inputs(self.input, self.archive)["packageKind"])


class InstallationTests(unittest.TestCase):
    def test_credential_inventory_requires_every_secret_and_sufficient_length(self):
        with tempfile.TemporaryDirectory() as directory:
            work = Path(directory)
            secrets = work / "secrets"
            secrets.mkdir()
            installation = proof.Installation(work)
            for key in proof.ALL_SECRET_KEYS:
                (secrets / key.lower()).write_text("s" * 32)
            self.assertEqual(len(proof.ALL_SECRET_KEYS), len(installation.credentials()))
            path = secrets / proof.ALL_SECRET_KEYS[0].lower()
            path.write_text("short")
            with self.assertRaises(ValueError):
                installation.credentials()
            path.unlink()
            with self.assertRaises(ValueError):
                installation.credentials()

    def test_health_scans_each_rendered_secret_surface(self):
        for surface in ("config", "environment", "dotenv"):
            with self.subTest(surface=surface), tempfile.TemporaryDirectory() as directory:
                work = Path(directory)
                secret = "s" * 32
                (work / ".env").write_text(secret if surface == "dotenv" else "SETTING=value")
                config = {"services": {"server": {}}}
                if surface == "config":
                    config["leaked"] = secret
                rendered = {("config", "--format", "json"): json.dumps(config),
                            ("config", "--environment"): secret if surface == "environment" else "SETTING=value",
                            ("ps", "--all", "--format", "json"): json.dumps([
                                {"Service": "server", "State": "running", "Health": "healthy"}])}
                installation = proof.Installation(work)
                with patch.object(installation, "credentials", return_value={"admin": secret}), \
                        patch.object(installation, "compose", side_effect=lambda *args: rendered[args]), \
                        self.assertRaisesRegex(ValueError, "rendered configuration"):
                    installation.health()

    def test_exercise_refuses_missing_console_changed_initializer_or_credentials(self):
        for defect in ("none", "console", "initializer", "credentials"):
            with self.subTest(defect=defect), tempfile.TemporaryDirectory() as directory:
                work = Path(directory)
                (work / "quickstart.py").write_text("initializer")
                installation = Mock()
                installation.directory = work
                installation.health.side_effect = [5, 5 if defect == "console" else 6, 6]
                installation.credentials.side_effect = [{"admin": "first"}, {
                    "admin": "changed" if defect == "credentials" else "first"}]
                if defect == "initializer":
                    installation.publish.side_effect = lambda _: (work / "quickstart.py").write_text("overwritten")
                response = io.BytesIO(b"{}")
                response.status = 200
                with patch.object(proof, "wait_ready"):
                    if defect == "none":
                        result = proof.exercise(installation, {})
                        self.assertTrue(result["reinitializePreservesCredentials"])
                        self.assertNotIn("restart", result)
                    else:
                        with self.assertRaises(ValueError):
                            proof.exercise(installation, {})

    def test_shared_publish_reader_validates_real_output_shape(self):
        blocks = {"seed": "seed", "clients": "clients", "publish": "example", "query": "python publish.py"}
        features = [{"attributes": {"id": 1, "name": "west"}, "geometry": {"x": -157.875, "y": 21.3125}},
                    {"attributes": {"id": 2, "name": "east"}, "geometry": {"x": -155.0625, "y": 19.6875}}]
        for output in ("malformed", "published service quickstart layer 3\n[]",
                       "published service quickstart layer 3\n" + json.dumps(features)):
            with self.subTest(output=output), tempfile.TemporaryDirectory() as directory:
                run = Mock(side_effect=["", "", output])
                if output.endswith(json.dumps(features)):
                    self.assertEqual(("3", features), proof.publish_sample(Path(directory), blocks, run))
                else:
                    with self.assertRaises(ValueError):
                        proof.publish_sample(Path(directory), blocks, run)


if __name__ == "__main__":
    unittest.main()
