#!/usr/bin/env python3
"""Check archived quickstart bootstrap and data access on a dedicated empty Docker host."""
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
import urllib.request

from compose_quickstart import ALL_SECRET_KEYS

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "docs-validation"))
from validate_public_install import public_blocks, publication_filename, publish_sample, wait_ready

REQUIRED = {"compose.yaml", "compose.executor.yaml", "compose.console.yaml", "quickstart.py", "images.json"}
IMAGE = re.compile(r"ghcr\.io/honua-io/[a-z0-9.-]+@sha256:[a-f0-9]{64}\Z")


def unpack(archive: Path, destination: Path) -> dict:
    checksum = archive.with_name(archive.name + ".sha256").read_text().split()
    digest = hashlib.sha256(archive.read_bytes()).hexdigest()
    if checksum != [digest, archive.name]:
        raise ValueError("Quickstart archive checksum does not match")
    with tarfile.open(archive, "r:gz") as package:
        members = package.getmembers()
        names = [member.name for member in members]
        if len(names) != len(set(names)) or len(names) > 1000:
            raise ValueError("Quickstart archive contains duplicate or excessive entries")
        if sum(member.size for member in members) > 64 * 1024 * 1024:
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
    if any(not IMAGE.fullmatch(lock.get(name, "")) for name in ("server", "executor", "worker", "console")):
        raise ValueError("Quickstart archive lacks immutable release image pins, including Console")
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
            raise RuntimeError(f"{command[0]} returned exit code {result.returncode}; output withheld")
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
    with urllib.request.urlopen("http://localhost:18083/version.json", timeout=15) as response:
        if response.status != 200 or not isinstance(json.load(response), dict):
            raise ValueError("Installed Console did not expose its version document")
    installation.publish(blocks)
    if (installation.directory / "quickstart.py").read_bytes() != original:
        raise ValueError("Documented publication overwrote the packaged initializer")
    credentials = installation.credentials()
    installation.run(["python3", "quickstart.py"])
    installation.health()
    if installation.credentials() != credentials:
        raise ValueError("Reinitialization changed the installation's saved credentials")
    return {"defaultServices": default_count, "consoleServices": console_count,
            "secretFreeConfiguration": True, "publishQuery": True, "reinitializePreservesCredentials": True}


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


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--archive", type=Path, required=True)
    parser.add_argument("--document", type=Path, required=True)
    parser.add_argument("--receipt", type=Path, required=True)
    parser.add_argument("--input", type=Path, required=True)
    args = parser.parse_args()
    receipt = {"scope": "archived-bootstrap-and-data-plane", "passed": False, "customerJourneyComplete": False,
               "authenticatedConsoleStudio": "pending-package-and-identity-journey",
               "installations": []}
    args.receipt.parent.mkdir(parents=True, exist_ok=True)
    stage = "validate-input"
    try:
        receipt.update(proof_inputs(args.input, args.archive))
        stage = "validate-document"
        blocks = public_blocks(args.document)
        publication_filename(blocks)
        previous = None
        for number in (1, 2):
            stage = f"installation-{number}"
            with tempfile.TemporaryDirectory(prefix="honua-package-") as work:
                directory = Path(work)
                lock = unpack(args.archive, directory)
                installation = Installation(directory)
                try:
                    if number == 1:
                        check_empty(installation, ("image", "container", "volume"))
                    else:
                        check_empty(installation, ("container", "volume"))
                    result = exercise(installation, blocks)
                    revision = installation.run(["docker", "image", "inspect", lock["server"], "--format",
                                                 '{{index .Config.Labels "org.opencontainers.image.revision"}}']).strip()
                    if revision != receipt["serverSourceSha"]:
                        raise ValueError("Installed serving image does not match the selected candidate source")
                    current = installation.credentials()
                    if previous is not None:
                        check_fresh_credentials(previous, current)
                        receipt["freshSecondInstallCredentials"] = True
                    previous = current
                    receipt["installations"].append(result)
                    receipt["version"] = lock["version"]
                finally:
                    installation.down()
        receipt.update(passed=True, cleanup=True)
    except (OSError, ValueError, KeyError, RuntimeError, subprocess.SubprocessError) as error:
        receipt.update(failedStage=stage, errorType=type(error).__name__)
        print("Archived quickstart proof failed at " + stage + "; command output withheld")
    finally:
        args.receipt.write_text(json.dumps(receipt, indent=2) + "\n")
    return 0 if receipt["passed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
