#!/usr/bin/env python3
"""Check the archived quickstart customer journey on a dedicated empty Docker host."""
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import tarfile
import tempfile

from compose_quickstart import ALL_SECRET_KEYS
from package_quickstart import CONSOLE_WEB_IMAGE, CONSOLE_MAX_FILES, CONSOLE_MAX_BYTES, ROOT

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "docs-validation"))
from validate_public_install import public_blocks, publication_filename, publish_sample, wait_ready

REQUIRED = {"compose.yaml", "compose.executor.yaml", "compose.console.yaml", "quickstart.py", "images.json"}
IMAGE = re.compile(r"ghcr\.io/honua-io/[a-z0-9.-]+@sha256:[a-f0-9]{64}\Z")


def unpack_image_source(archive: Path, destination: Path) -> dict:
    checksum = archive.with_name(archive.name + ".sha256").read_text().split()
    digest = hashlib.sha256(archive.read_bytes()).hexdigest()
    if checksum != [digest, archive.name]:
        raise ValueError("Quickstart archive checksum does not match")
    with tarfile.open(archive, "r:gz") as package:
        members = package.getmembers()
        names = [member.name for member in members]
        if len(names) != len(set(names)) or len(names) > CONSOLE_MAX_FILES + 1000:
            raise ValueError("Quickstart archive contains duplicate or excessive entries")
        if sum(member.size for member in members) > CONSOLE_MAX_BYTES + 64 * 1024 * 1024:
            raise ValueError("Quickstart archive exceeds the installation size limit")
        for member in members:
            path = Path(member.name)
            if path.is_absolute() or ".." in path.parts or not (member.isfile() or member.isdir()):
                raise ValueError("Quickstart archive contains an unsafe entry")
            if path.parts and path.parts[0] in (".env", "secrets"):
                raise ValueError("A clean-install package must not ship saved credentials")
        if not REQUIRED <= set(names):
            raise ValueError("Quickstart archive lacks required package files, including Console")
        package.extractall(destination, filter="data")
    lock = json.loads((destination / "images.json").read_text())
    if any(not IMAGE.fullmatch(lock.get(name, "")) for name in ("server", "executor", "worker")):
        raise ValueError("Quickstart archive lacks immutable release image pins, including Console")
    return lock


def unpack(archive: Path, destination: Path) -> dict:
    lock = unpack_image_source(archive, destination)
    bundle = lock.get("consoleBundle", {})
    if not isinstance(bundle, dict):
        raise ValueError("Quickstart archive has invalid static Console metadata")
    files = [path for path in (destination / "console").rglob("*") if path.is_file()]
    if (lock.get("consoleWeb") != CONSOLE_WEB_IMAGE or
            not re.fullmatch(r"[a-f0-9]{64}", bundle.get("sha256", "")) or
            bundle.get("files") != len(files) or
            not all((destination / "console" / name).is_file() for name in ("index.html", "config.json")) or
            not any(path.is_file() for path in (destination / "console/_framework").rglob("*"))):
        raise ValueError("Quickstart archive lacks its verified static Console bundle")
    return lock


def check_no_secrets(text: str, credentials: dict[str, str]) -> None:
    if any(secret in text for secret in credentials.values()):
        raise ValueError("Generated credential appears in rendered configuration")


def check_fresh_credentials(previous: dict[str, str], current: dict[str, str]) -> None:
    if not previous or previous.keys() != current.keys() or set(previous.values()) & set(current.values()):
        raise ValueError("The second installation must generate a fresh complete credential set")


def check_services(configuration: dict, observed: list[dict]) -> None:
    expected = configuration["services"]
    by_name = {row["Service"]: row for row in observed}
    if len(observed) != len(by_name) or by_name.keys() != expected.keys():
        raise ValueError("Not every configured service has a container")
    initializers = {name for service in expected.values()
                    for name, dependency in service.get("depends_on", {}).items()
                    if dependency.get("condition") == "service_completed_successfully"}
    for name, row in by_name.items():
        if name in initializers:
            if row.get("State") != "exited" or row.get("ExitCode") != 0:
                raise ValueError("A package initialization service did not complete successfully")
        elif row.get("State") != "running" or row.get("Health") != "healthy":
            raise ValueError("A package service is not running and healthy")


# Only fixed labels cross the subprocess boundary; captured output may hold secrets.
FAILURE_SIGNALS = {
    "permission-denied": "permission denied",
    "registry-unauthorized": "unauthorized",
    "registry-rate-limit": "toomanyrequests",
    "image-not-found": "manifest unknown",
    "image-pull-denied": "pull access denied",
    "build-failed": "failed to solve",
    "dockerfile-missing": "failed to read dockerfile",
    "daemon-unavailable": "cannot connect to the docker daemon",
    "platform-unavailable": "no matching manifest",
    "port-conflict": "address already in use",
    "operation-denied": "operation not permitted",
    "registry-status-error": "unexpected status",
    "registry-too-many-requests": "too many requests",
    "network-unreachable": "network is unreachable",
    "dns-failed": "no such host",
    "connection-refused": "connection refused",
    "download-failed": "curl:",
    "no-disk-space": "no space left on device",
    "container-unhealthy": "is unhealthy",
    "dependency-failed": "dependency failed to start",
    "initializer-failed": "didn't complete successfully",
    "compose-unavailable": "is not a docker command",
    "initializer-refused": "quickstart initialization failed",
}


class CommandFailure(RuntimeError):
    def __init__(self, command: list[str], result: subprocess.CompletedProcess):
        super().__init__("Archived installation command failed; output withheld")
        known = {
            ("python3", "quickstart.py"): "initialize",
            ("docker", "compose", "down"): "teardown",
            ("docker", "compose", "config"): "configuration",
            ("docker", "compose", "ps"): "service-health",
        }
        operation = next((label for prefix, label in known.items()
                          if tuple(command[:len(prefix)]) == prefix), "other")
        output = (result.stdout + result.stderr).lower()
        self.diagnostic = {"operation": operation, "exitCode": result.returncode,
                           "signals": [label for label, marker in FAILURE_SIGNALS.items() if marker in output]}


class Installation:
    def __init__(self, directory: Path):
        self.directory = directory
        self.environment = {key: os.environ[key] for key in ("PATH", "HOME", "LANG", "TMPDIR") if key in os.environ}
        anonymous = directory / "anonymous-docker"
        anonymous.mkdir()
        self.environment["DOCKER_CONFIG"] = str(anonymous)

    def run(self, command: list[str], timeout: int = 600) -> str:
        result = subprocess.run(command, cwd=self.directory, env=self.environment, capture_output=True,
                                text=True, timeout=timeout)
        if result.returncode:
            raise CommandFailure(command, result)
        return result.stdout

    def compose(self, *arguments: str) -> str:
        return self.run(["docker", "compose", *arguments])

    def credentials(self) -> dict[str, str]:
        credentials = {path.name: path.read_text().strip() for path in (self.directory / "secrets").iterdir()
                       if path.is_file() and path.suffix != ".pfx"}
        if not {key.lower() for key in ALL_SECRET_KEYS} <= credentials.keys() or any(
                len(value) < 32 for value in credentials.values()):
            raise ValueError("Generated credential files are missing or invalid")
        return credentials

    def health(self) -> int:
        credentials = self.credentials()
        rendered = self.compose("config", "--format", "json")
        check_no_secrets(rendered + self.compose("config", "--environment") +
                         (self.directory / ".env").read_text(), credentials)
        configuration = json.loads(rendered)
        states = self.compose("ps", "--all", "--format", "json").strip()
        observed = json.loads(states) if states.startswith("[") else [json.loads(line) for line in states.splitlines()]
        check_services(configuration, observed)
        return len(observed)

    def publish(self, blocks: dict[str, str]) -> None:
        publish_sample(self.directory, blocks, self.run)

    def browser(self) -> None:
        # No credentials in argv, reports, screenshots, traces or inherited environment.
        environment = dict(self.environment, HONUA_SHIPPING_PASSWORD_FILE=str(
            self.directory / "secrets/honua_admin_password"))
        browser = ROOT / "tests/js-browser"
        result = subprocess.run(["node", str(browser / "node_modules/@playwright/test/cli.js"), "test",
                                 "--config", "playwright.quickstart.config.ts"], cwd=browser,
                                env=environment, capture_output=True, text=True, timeout=240)
        if result.returncode:
            raise RuntimeError("Installed Console/Studio browser journey failed; output withheld")

    def down(self) -> None:
        if not (self.directory / ".env").exists():
            return
        self.compose("down", "--volumes", "--remove-orphans")
        check_empty(self, ("container", "volume"))


def check_empty(installation: Installation, resources: tuple[str, ...]) -> None:
    for resource in resources:
        command = ["docker", resource, "ls", "--quiet"]
        if resource in ("image", "container"):
            command.append("--all")
        if installation.run(command).strip():
            raise ValueError("The dedicated Docker host contains leftover " + resource + " resources")


def exercise(installation: Installation, blocks: dict[str, str]) -> dict:
    original = (installation.directory / "quickstart.py").read_bytes()
    installation.run(["python3", "quickstart.py"])
    default_count = installation.health()
    wait_ready("http://localhost:18080")
    installation.run(["python3", "quickstart.py", "--profile", "console"])
    console_count = installation.health()
    if console_count <= default_count:
        raise ValueError("Console profile did not add a service")
    installation.publish(blocks)
    if (installation.directory / "quickstart.py").read_bytes() != original:
        raise ValueError("Documented publication overwrote the packaged initializer")
    installation.browser()
    credentials = installation.credentials()
    installation.run(["python3", "quickstart.py"])
    installation.health()
    if installation.credentials() != credentials:
        raise ValueError("Reinitialization changed the installation's saved credentials")
    return {"defaultServices": default_count, "consoleServices": console_count,
            "secretFreeConfiguration": True, "publishQuery": True, "authenticatedConsoleStudio": True, "reinitializePreservesCredentials": True}


def proof_inputs(path: Path, archive: Path) -> dict:
    metadata = json.loads(path.read_text())
    for key in ("packageSourceSha", "serverSourceSha"):
        if not isinstance(metadata.get(key), str) or not re.fullmatch(r"[a-f0-9]{40}", metadata[key]):
            raise ValueError("Package and server source revisions must be full commit identifiers")
    for key in ("archiveSha256", "publishedArchiveSha256"):
        if not isinstance(metadata.get(key), str) or not re.fullmatch(r"[a-f0-9]{64}", metadata[key]):
            raise ValueError("Input archive digests must be full SHA256 values")
    if type(metadata.get("publishedRunId")) is not int or metadata["publishedRunId"] <= 0:
        raise ValueError("Input must identify its official candidate publication run")
    if metadata.get("packageKind") not in ("candidate", "prospective-pr"):
        raise ValueError("Input must distinguish candidate and prospective PR archives")
    if hashlib.sha256(archive.read_bytes()).hexdigest() != metadata["archiveSha256"]:
        raise ValueError("Downloaded archive does not match its transported input identity")
    if metadata["packageKind"] == "candidate" and (
            metadata["archiveSha256"] != metadata["publishedArchiveSha256"] or
            metadata["packageSourceSha"] != metadata["serverSourceSha"]):
        raise ValueError("Candidate archive identity differs from its official publication")
    return {key: metadata[key] for key in ("packageSourceSha", "serverSourceSha", "archiveSha256",
                                          "publishedArchiveSha256", "publishedRunId", "packageKind")}


class CustomerJourney:
    def __init__(self, archive: Path, blocks: dict[str, str], server_source: str):
        self.archive = archive
        self.blocks = blocks
        self.server_source = server_source
        self.receipt = {"installations": []}
        self.stage = "installations"

    def install(self, number: int) -> dict:
        with tempfile.TemporaryDirectory(prefix="honua-package-") as work:
            directory = Path(work)
            lock = unpack(self.archive, directory)
            installation = Installation(directory)
            try:
                resources = ("image", "container", "volume") if number == 1 else ("container", "volume")
                check_empty(installation, resources)
                result = exercise(installation, self.blocks)
                revision = installation.run(["docker", "image", "inspect", lock["server"], "--format",
                                             '{{index .Config.Labels "org.opencontainers.image.revision"}}']).strip()
                if revision != self.server_source:
                    raise ValueError("Installed serving image does not match the selected candidate source")
                installed = {"credentials": installation.credentials(), "result": result, "version": lock["version"]}
            except BaseException:
                # Preserve the original cause if teardown fails as well.
                try:
                    installation.down()
                except (OSError, ValueError, RuntimeError, subprocess.SubprocessError):
                    self.receipt["cleanupFailed"] = True
                raise
            else:
                installation.down()
            return installed

    def run(self) -> None:
        previous = None
        for number in (1, 2):
            self.stage = f"installation-{number}"
            current = self.install(number)
            if previous is not None:
                check_fresh_credentials(previous, current["credentials"])
            previous = current["credentials"]
            self.receipt["installations"].append(current["result"])
            self.receipt["version"] = current["version"]
        self.receipt["freshSecondInstallCredentials"] = True


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--archive", type=Path, required=True)
    parser.add_argument("--document", type=Path, required=True)
    parser.add_argument("--receipt", type=Path, required=True)
    parser.add_argument("--input", type=Path, required=True)
    args = parser.parse_args()
    receipt = {"scope": "archived-customer-journey", "passed": False, "customerJourneyComplete": False,
               "authenticatedConsoleStudio": False,
               "installations": []}
    args.receipt.parent.mkdir(parents=True, exist_ok=True)
    stage = "validate-input"
    journey = None
    try:
        receipt.update(proof_inputs(args.input, args.archive))
        stage = "validate-document"
        blocks = public_blocks(args.document)
        publication_filename(blocks)
        stage = "installations"
        journey = CustomerJourney(args.archive, blocks, receipt["serverSourceSha"])
        journey.run()
        receipt.update(passed=True, cleanup=True, customerJourneyComplete=True, authenticatedConsoleStudio=True)
    except (OSError, ValueError, KeyError, RuntimeError, subprocess.SubprocessError) as error:
        stage = journey.stage if journey is not None else stage
        receipt.update(failedStage=stage, errorType=type(error).__name__)
        if isinstance(error, CommandFailure):
            receipt["commandFailure"] = error.diagnostic
        print("Archived quickstart proof failed at " + stage + "; command output withheld")
    finally:
        if journey is not None:
            receipt.update(journey.receipt)
        args.receipt.write_text(json.dumps(receipt, indent=2) + "\n")
    return 0 if receipt["passed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
