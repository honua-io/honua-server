#!/usr/bin/env python3
"""Isolated source/input snapshots around the installed GP devkit, not a GP executor.

A digest-pinned image must contain honua-gp-devkit, Python/.NET SDKs and the
project's locked dependency cache. All restore/build/test phases run with NO
network. Populate image caches separately from project code before selecting it.
"""
from __future__ import annotations

import argparse
from dataclasses import asdict, dataclass
from typing import Callable, Iterator
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import signal
import stat
import subprocess
import sys
import threading
import time
import uuid

MAX_FILES = 10000
MAX_BYTES = 128 * 1024 * 1024  # Compatibility alias; operational budgets are independent below.
EXCLUDED = {".git", "bin", "obj", "node_modules", ".venv", "__pycache__"}


@dataclass(frozen=True)
class WorkspaceLimits:
    source_bytes: int = MAX_BYTES
    fixture_bytes: int = MAX_BYTES
    snapshot_bytes: int = MAX_BYTES
    output_bytes: int = MAX_BYTES
    scratch_bytes: int = 1024 * 1024 * 1024
    max_artifact_bytes: int = MAX_BYTES
    max_files: int = MAX_FILES
    chunk_bytes: int = 1024 * 1024

    def __post_init__(self) -> None:
        if any(type(value) is not int or value <= 0 for value in asdict(self).values()):
            raise ValueError("Workspace limits must be positive integers.")
        if self.chunk_bytes > 1024 * 1024 or self.max_files > MAX_FILES:
            raise ValueError("Chunk or file-count limit exceeds the supported bounded profile.")
        if self.max_artifact_bytes > self.output_bytes:
            raise ValueError("Artifact limit exceeds its output budget.")
        # No claim of multi-GB execution until operator-provisioned, quota-backed
        # disk scratch is available and its actual capacity/profile is qualified.
        maxima = {"source_bytes": MAX_BYTES, "fixture_bytes": MAX_BYTES, "snapshot_bytes": MAX_BYTES,
                  "output_bytes": MAX_BYTES, "scratch_bytes": 1024 * 1024 * 1024, "max_artifact_bytes": MAX_BYTES}
        if any(getattr(self, key) > value for key, value in maxima.items()):
            raise ValueError("Large workspace profiles require verified quota-backed disk scratch; not qualified.")

    def record(self) -> dict:
        return asdict(self)


DEFAULT_LIMITS = WorkspaceLimits()


def _identity(info: os.stat_result) -> tuple:
    return (info.st_dev, info.st_ino, info.st_size, info.st_mtime_ns, info.st_ctime_ns)


def open_regular_beneath(root: Path, relative: str) -> int:
    """Pin every component beneath the trusted root, refusing links and FIFOs."""
    parts = Path(relative).parts
    if not parts or Path(relative).is_absolute() or any(part in {".", ".."} for part in parts):
        raise ValueError("Invalid immutable artifact path.")
    directory = os.open(root, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
    try:
        for part in parts[:-1]:
            child = os.open(part, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW, dir_fd=directory)
            os.close(directory); directory = child
        descriptor = os.open(parts[-1], os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK, dir_fd=directory)
        if not stat.S_ISREG(os.fstat(descriptor).st_mode) or os.fstat(descriptor).st_nlink != 1:
            os.close(descriptor)
            raise ValueError("Only regular immutable artifacts are accepted.")
        return descriptor
    finally:
        os.close(directory)


class ArtifactReader:
    """A verified immutable receipt and the SAME open file throughout transmission."""
    def __init__(self, root: Path, item: dict, limits: WorkspaceLimits = DEFAULT_LIMITS,
                 authorize: Callable[[], None] = lambda: None, binding: dict | None = None, relative: str | None = None):
        self.item = dict(item)
        self.binding = dict(binding or {})
        self.limits = limits
        self.authorize = authorize
        self.descriptor = -1
        self.authorize()
        size, digest = item.get("bytes"), item.get("sha256")
        if type(size) is not int or size < 0 or size > limits.max_artifact_bytes or not isinstance(digest, str) or not re.fullmatch(r"[a-f0-9]{64}", digest):
            raise ValueError("Invalid immutable artifact receipt.")
        self.descriptor = open_regular_beneath(root, relative or item["path"])
        self.identity = _identity(os.fstat(self.descriptor))
        if self.identity[2] != size:
            self.close(); raise ValueError("Artifact size changed.")
        try:
            for _ in self.chunks(): pass
            os.lseek(self.descriptor, 0, os.SEEK_SET)
        except BaseException:
            self.close(); raise

    def _check(self) -> None:
        self.authorize()
        if self.descriptor < 0 or _identity(os.fstat(self.descriptor)) != self.identity:
            raise ValueError("Immutable artifact changed during reading.")

    def chunks(self) -> Iterator[bytes]:
        self._check()
        remaining, digest = self.item["bytes"], hashlib.sha256()
        while remaining:
            self._check()
            data = os.read(self.descriptor, min(self.limits.chunk_bytes, remaining))
            self._check()
            if not data: raise ValueError("Immutable artifact was truncated.")
            remaining -= len(data)
            digest.update(data)
            if remaining == 0:
                # Withhold the final chunk until the complete streamed digest is
                # verified. A mid-stream mutation cannot complete Content-Length.
                if os.read(self.descriptor, 1) or digest.hexdigest() != self.item["sha256"]:
                    raise ValueError("Immutable artifact digest changed during reading.")
                self._check()
            yield data
        if self.item["bytes"] == 0 and digest.hexdigest() != self.item["sha256"]:
            raise ValueError("Immutable empty artifact digest changed.")
        self._check()

    def close(self) -> None:
        if self.descriptor >= 0:
            os.close(self.descriptor); self.descriptor = -1

    def __enter__(self) -> ArtifactReader:
        return self

    def __exit__(self, *_: object) -> None:
        self.close()


def sanitized(text: str) -> str:
    text = re.sub(r"\x1b\[[0-?]*[ -/]*[@-~]", "", text)
    text = re.sub(r"(?i)(authorization\s*[:=]\s*bearer\s+|(?:api[_-]?key|password|token|secret)\s*[:=]\s*)[^\s,;]+", r"\1[redacted]", text)
    text = re.sub(r"https?://[^\s/@:]+:[^\s/@]+@", "https://[redacted]@", text)
    return text[-16384:]


def read_regular(path: Path, limit: int, root: Path | None = None) -> bytes:
    """Never block on a FIFO or allocate from an untrusted stat size."""
    descriptor = open_regular_beneath(root, path.relative_to(root).as_posix()) if root is not None else os.open(path, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK)
    try:
        if not stat.S_ISREG(os.fstat(descriptor).st_mode) or os.fstat(descriptor).st_nlink != 1:
            raise ValueError("Only regular files are accepted.")
        with os.fdopen(descriptor, "rb", closefd=False) as stream:
            data = stream.read(limit + 1)
        if len(data) > limit: raise ValueError("File exceeds the byte budget.")
        return data
    finally:
        os.close(descriptor)


CREDENTIAL_DIRECTORIES = {".aws", ".azure", ".ssh", ".gnupg", ".kube", ".docker", ".config"}
CREDENTIAL_NAMES = {".npmrc", ".pypirc", ".netrc", ".git-credentials", ".yarnrc.yml", "application_default_credentials.json", "credentials", "credentials.json", "secrets.json", "id_rsa", "id_dsa", "id_ecdsa", "id_ed25519"}


def secret_path(path: Path) -> bool:
    names = [part.lower() for part in path.parts]
    name = names[-1] if names else ""
    return any(part in CREDENTIAL_DIRECTORIES for part in names) or name in CREDENTIAL_NAMES or name.startswith((".env", "client_secret", "service-account", "service_account", "secrets.")) or path.suffix.lower() in {".pem", ".key", ".pfx", ".p12"}


def stream_regular(path: Path, limit: int, destination: Path | None = None, chunk_bytes: int = 1024 * 1024, root: Path | None = None) -> tuple[int, str]:
    descriptor = open_regular_beneath(root, path.relative_to(root).as_posix()) if root is not None else os.open(path, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK)
    target = None
    try:
        before = os.fstat(descriptor)
        if not stat.S_ISREG(before.st_mode) or before.st_size > limit:
            raise ValueError("Regular file exceeds its independent byte budget.")
        if destination is not None:
            destination.parent.mkdir(parents=True, exist_ok=True)
            target = destination.open("xb")
        total, digest = 0, hashlib.sha256()
        while data := os.read(descriptor, min(chunk_bytes, limit - total + 1)):
            total += len(data)
            if total > limit: raise ValueError("File grew beyond its independent byte budget.")
            digest.update(data)
            if target is not None: target.write(data)
        if _identity(os.fstat(descriptor)) != _identity(before): raise ValueError("File changed during immutable capture.")
        if destination is not None: destination.chmod(0o444)
        return total, digest.hexdigest()
    finally:
        if target is not None: target.close()
        os.close(descriptor)


def source_manifest(project: Path, limits: WorkspaceLimits = DEFAULT_LIMITS) -> dict:
    return _capture(project, None, limits)


def snapshot(project: Path, destination: Path, limits: WorkspaceLimits = DEFAULT_LIMITS) -> dict:
    return _capture(project, destination, limits)


def _capture(project: Path, destination: Path | None, limits: WorkspaceLimits) -> dict:
    """Stream immutable regular source AND fixture bytes under separate limits."""
    project = project.resolve(strict=True)
    if not (project / "honua-gp.yaml").is_file():
        raise ValueError("A GP devkit project descriptor is required.")
    if destination is not None: destination.mkdir(parents=True, exist_ok=False)
    files = []
    totals = {"source": 0, "fixture": 0}
    for directory, directories, names in os.walk(project, followlinks=False):
        directories[:] = sorted(name for name in directories if name not in EXCLUDED and not secret_path(Path(directory).relative_to(project) / name))
        root = Path(directory)
        if root.relative_to(project).parts == (".honua",):
            directories[:] = [name for name in directories if name == "toolchain"]
            names = []
        names = [name for name in names if not secret_path(root.relative_to(project) / name)]
        for name in directories + sorted(names):
            if (root / name).is_symlink(): raise ValueError("Snapshot refuses symbolic links.")
        for name in sorted(names):
            source = root / name
            relative = source.relative_to(project)
            if source.name.lower() in {"nuget.config", "pip.conf"}:
                config = read_regular(source, 256 * 1024, project)
                if re.search(rb"(?i)(packagesourcecredentials|cleartextpassword|\[apikeys\]|https?://[^/\s]+:[^/\s]+@)", config): continue
            kind = "fixture" if relative.parts[0] == "fixtures" else "source"
            budget = limits.fixture_bytes if kind == "fixture" else limits.source_bytes
            remaining = min(budget - totals[kind], limits.snapshot_bytes - sum(totals.values()))
            if len(files) >= limits.max_files: raise ValueError("Snapshot file-count budget exceeded.")
            size, digest = stream_regular(source, remaining, None if destination is None else destination / relative, limits.chunk_bytes, project)
            totals[kind] += size
            files.append({"path": relative.as_posix(), "bytes": size, "sha256": digest})
    identity = {"files": files, "limits": limits.record()}
    encoded = json.dumps(identity, sort_keys=True, separators=(",", ":")).encode()
    return {"schema": "honua.gp.workspace-snapshot/v2", "digest": hashlib.sha256(encoded).hexdigest(), **identity}


def command(image: str, source: Path, output: Path, name: str, limits: WorkspaceLimits = DEFAULT_LIMITS) -> list[str]:
    if os.getuid() == 0: raise ValueError("Run the local workspace bridge as a non-root user.")
    if not re.fullmatch(r"(?:[a-zA-Z0-9./:_-]+@)?sha256:[a-f0-9]{64}", image):
        raise ValueError("Select an immutable Docker image digest, never a mutable tag.")
    return ["docker", "run", "--pull", "never", "--name", name, "--rm", "--read-only", "--network", "none",
            "--cap-drop", "ALL", "--security-opt", "no-new-privileges", "--pids-limit", "128",
            "--memory", "1g", "--cpus", "1", "--ulimit", f"fsize={limits.max_artifact_bytes}:{limits.max_artifact_bytes}",
            "--ulimit", "nofile=256:256", "--user", f"{os.getuid()}:{os.getgid()}",
            "--tmpfs", "/tmp:rw,nosuid,nodev,size=268435456,mode=1777",
            "--tmpfs", f"/work:rw,nosuid,nodev,size={limits.scratch_bytes},mode=1777",
            "--mount", f"type=bind,source={source},target=/snapshot,readonly",
            "--mount", f"type=bind,source={output},target=/out",
            "--mount", f"type=bind,source={Path(__file__).resolve()},target=/workspace-runner.py,readonly",
            "--env", "HONUA_GP_WORKSPACE_LIMITS=" + json.dumps(limits.record(), separators=(",", ":")),
            "--env", "HOME=/tmp/home", "--env", "DOTNET_CLI_HOME=/tmp/home", "--env", "PYTHONDONTWRITEBYTECODE=1",
            "--entrypoint", "python3", image, "/workspace-runner.py", "--inside-container"]


def inside_container() -> int:
    project = Path("/work/project")
    shutil.copytree("/snapshot", project)
    for root, _, names in os.walk(project):
        for name in names:
            (Path(root) / name).chmod(0o644)
    lock = project / "requirements.lock"
    if lock.is_file():
        restored = subprocess.run([sys.executable, "-m", "pip", "install", "--no-index", "--find-links", "/opt/wheelhouse", "--target", "/work/dependencies", "-r", str(lock)], check=False, timeout=120)
        if restored.returncode: return restored.returncode
        os.environ["PYTHONPATH"] = "/work/dependencies"
    # Vulnerability feeds are not a restore source and cannot be contacted offline.
    os.environ["NuGetAudit"] = "false"
    os.environ["NUGET_CERT_REVOCATION_MODE"] = "offline"
    # Dependency restoration and builds are executed by the SAME installed devkit
    # used at the terminal. Network is denied at the container boundary throughout.
    completed = subprocess.run(["honua-gp-devkit", "test", "--project", str(project)], check=False, timeout=280)
    runs = project / ".honua" / "test-runs"
    if runs.exists():
        for root, directories, names in os.walk(runs, followlinks=False):
            for name in directories + names:
                if not stat.S_ISREG((Path(root) / name).lstat().st_mode) and name in names:
                    raise ValueError("Sandbox output contains a non-regular file.")
                if (Path(root) / name).is_symlink():
                    raise ValueError("Sandbox output contains a symlink.")
        shutil.copytree(runs, "/out/runs", dirs_exist_ok=True)
    return completed.returncode


def collect_artifacts(output: Path, limits: WorkspaceLimits = DEFAULT_LIMITS) -> list[dict]:
    """Validate all output budgets; hash declared artifacts without buffering them."""
    receipts, total, count = [], 0, 0
    for root, directories, names in os.walk(output, followlinks=False):
        for name in directories + names:
            if (Path(root) / name).is_symlink(): raise ValueError("Refusing linked sandbox output.")
        for name in names:
            count += 1
            if count > limits.max_files: raise ValueError("Output file-count budget exceeded.")
            path = Path(root) / name
            descriptor = open_regular_beneath(output, path.relative_to(output).as_posix())
            try: total += os.fstat(descriptor).st_size
            finally: os.close(descriptor)
            if total > limits.output_bytes: raise ValueError("Output byte budget exceeded.")
            if name == "receipt.json": receipts.append(path)
    artifacts, seen = [], set()
    for path in receipts:
        receipt = json.loads(read_regular(path, 4 * 1024 * 1024))
        if receipt.get("schemaVersion") != "1.0" or receipt.get("executionMode") != "local" or receipt.get("status") != "succeeded": continue
        for item in receipt.get("artifacts", []):
            relative = Path(item["path"])
            if relative.is_absolute() or ".." in relative.parts or not relative.parts or relative.parts[0] != "artifacts":
                raise ValueError("Invalid declared artifact path.")
            key = (path.parent / relative).relative_to(output).as_posix()
            if key in seen: raise ValueError("Duplicate declared artifact.")
            declared = {"path": key, "bytes": item["sizeBytes"], "sha256": item["sha256"]}
            with ArtifactReader(output, declared, limits): pass
            seen.add(key); artifacts.append(declared)
    return artifacts


def run(project: Path, image: str, runs: Path, timeout: float, cancelled: threading.Event | None = None, run_id: str | None = None, limits: WorkspaceLimits = DEFAULT_LIMITS) -> dict:
    cancellation = cancelled or threading.Event()
    run_id = run_id or uuid.uuid4().hex
    if runs.resolve().is_relative_to(project.resolve()):
        raise ValueError("Run storage must be outside the editable project.")
    if timeout <= 0 or timeout > 3600: raise ValueError("Timeout must be in (0,3600] seconds.")
    if shutil.disk_usage(runs.resolve().parent).free < limits.snapshot_bytes + limits.output_bytes + 1024 * 1024:
        raise ValueError("Insufficient free disk for bounded snapshot and output; no execution submitted.")
    directory = runs.resolve() / run_id
    source = directory / "snapshot"
    output = directory / "output"
    directory.mkdir(parents=True, mode=0o700)
    manifest = snapshot(project, source, limits)
    (directory / "snapshot.json").write_text(json.dumps(manifest, indent=2))
    output.mkdir()
    name = f"honua-gp-workspace-{run_id}"
    argv = command(image, source, output, name, limits)
    started = time.monotonic()
    status = "failed"
    log = directory / "diagnostics.local.txt"
    logs = bytearray()
    process = subprocess.Popen(argv, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
    def drain() -> None:
        assert process.stdout is not None
        while chunk := process.stdout.read(4096):
            logs.extend(chunk)
            if len(logs) > 32768: del logs[:-32768]
    reader = threading.Thread(target=drain, daemon=True)
    reader.start()
    try:
        while process.poll() is None:
            output_bytes = 0
            output_count = 0
            for root, directories, names in os.walk(output, followlinks=False):
                for filename in names:
                    path = Path(root) / filename
                    if not path.is_symlink():
                        try: output_bytes += path.stat().st_size
                        except FileNotFoundError: pass
                    output_count += 1
            over_budget = output_bytes > limits.output_bytes or output_count > limits.max_files
            if over_budget or cancellation.wait(0.1) or time.monotonic() - started >= timeout:
                status = "failed" if over_budget else "cancelled" if cancellation.is_set() else "timed-out"
                subprocess.run(["docker", "kill", name], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, check=False)
                try:
                    process.terminate()
                except ProcessLookupError:
                    pass
                try:
                    process.wait(timeout=5)
                except subprocess.TimeoutExpired:
                    process.kill()
                    process.wait(timeout=5)
                break
        else:
            status = "succeeded" if process.returncode == 0 else "failed"
    finally:
        subprocess.run(["docker", "rm", "-f", name], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, check=False)
    reader.join(timeout=5)
    log.write_bytes(logs)
    # The immutable snapshot is mounted read-only. Check retained bytes independently.
    for item in manifest["files"]:
        if stream_regular(source / item["path"], item["bytes"], chunk_bytes=limits.chunk_bytes, root=source) != (item["bytes"], item["sha256"]):
            raise ValueError("Immutable snapshot changed during execution.")
    artifacts = collect_artifacts(output, limits)
    with log.open("rb") as stream:
        stream.seek(max(0, log.stat().st_size - 32768))
        diagnostics = sanitized(stream.read().decode("utf-8", errors="replace"))
    receipt = {"schema": "honua.gp.workspace-run/v1", "runId": run_id, "snapshotDigest": manifest["digest"],
               "image": image, "network": "none", "limits": limits.record(), "status": status, "elapsedMs": round((time.monotonic() - started) * 1000),
               "diagnostics": diagnostics, "artifacts": artifacts if status == "succeeded" else []}
    (directory / "receipt.json").write_text(json.dumps(receipt, indent=2))
    return receipt


def main() -> int:
    if "--inside-container" in sys.argv:
        return inside_container()
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--project", type=Path, required=True)
    parser.add_argument("--image", required=True)
    parser.add_argument("--runs", type=Path, required=True)
    parser.add_argument("--timeout", type=float, default=300)
    options = parser.parse_args()
    cancellation = threading.Event()
    for signum in (signal.SIGINT, signal.SIGTERM):
        signal.signal(signum, lambda *_: cancellation.set())
    receipt = run(options.project, options.image, options.runs, options.timeout, cancellation)
    print(json.dumps(receipt))
    return 0 if receipt["status"] == "succeeded" else 3 if receipt["status"] == "cancelled" else 1


if __name__ == "__main__":
    sys.exit(main())
