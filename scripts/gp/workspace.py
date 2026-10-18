#!/usr/bin/env python3
"""Isolated source/input snapshots around the installed GP devkit, not a GP executor.

A digest-pinned image must contain honua-gp-devkit, Python/.NET SDKs and the
project's locked dependency cache. All restore/build/test phases run with NO
network. Populate image caches separately from project code before selecting it.
"""
from __future__ import annotations

import argparse
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
MAX_BYTES = 128 * 1024 * 1024
EXCLUDED = {".git", "bin", "obj", "node_modules", ".venv", "__pycache__"}


def sanitized(text: str) -> str:
    text = re.sub(r"\x1b\[[0-?]*[ -/]*[@-~]", "", text)
    text = re.sub(r"(?i)(authorization\s*[:=]\s*bearer\s+|(?:api[_-]?key|password|token|secret)\s*[:=]\s*)[^\s,;]+", r"\1[redacted]", text)
    text = re.sub(r"https?://[^\s/@:]+:[^\s/@]+@", "https://[redacted]@", text)
    return text[-16384:]


def read_regular(path: Path, limit: int) -> bytes:
    """Never block on a FIFO or allocate from an untrusted stat size."""
    descriptor = os.open(path, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK)
    try:
        if not stat.S_ISREG(os.fstat(descriptor).st_mode):
            raise ValueError("Only regular files are accepted.")
        with os.fdopen(descriptor, "rb", closefd=False) as stream:
            data = stream.read(limit + 1)
        if len(data) > limit: raise ValueError("File exceeds the byte budget.")
        return data
    finally:
        os.close(descriptor)


def snapshot(project: Path, destination: Path) -> dict:
    """Copy regular source AND fixture input bytes; reject links/special files."""
    project = project.resolve(strict=True)
    if not (project / "honua-gp.yaml").is_file():
        raise ValueError("A GP devkit project descriptor is required.")
    destination.mkdir(parents=True, exist_ok=False)
    files = []
    total = 0
    for directory, directories, names in os.walk(project, followlinks=False):
        directories[:] = sorted(name for name in directories if name not in EXCLUDED)
        root = Path(directory)
        if root.relative_to(project).parts == (".honua",):
            # Installed devkit projects carry their canonical harness here. Retain
            # it in the immutable snapshot, but never retain prior run output.
            directories[:] = [name for name in directories if name == "toolchain"]
            names = []
        for name in directories + sorted(names):
            source = root / name
            if source.is_symlink():
                raise ValueError(f"Snapshot refuses a symlink: {source.relative_to(project)}")
        for name in sorted(names):
            source = root / name
            if not source.is_file():
                raise ValueError("Snapshot refuses non-regular files.")
            relative = source.relative_to(project)
            data = read_regular(source, MAX_BYTES - total)
            total += len(data)
            if total > MAX_BYTES or len(files) >= MAX_FILES:
                raise ValueError("Source/input snapshot exceeds the size budget.")
            target = destination / relative
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(data)
            target.chmod(0o444)
            files.append({"path": relative.as_posix(), "bytes": len(data), "sha256": hashlib.sha256(data).hexdigest()})
    encoded = json.dumps(files, sort_keys=True, separators=(",", ":")).encode()
    return {"schema": "honua.gp.workspace-snapshot/v1", "digest": hashlib.sha256(encoded).hexdigest(), "files": files}


def command(image: str, source: Path, output: Path, name: str) -> list[str]:
    if os.getuid() == 0: raise ValueError("Run the local workspace bridge as a non-root user.")
    if not re.fullmatch(r"(?:[a-zA-Z0-9./:_-]+@)?sha256:[a-f0-9]{64}", image):
        raise ValueError("Select an immutable Docker image digest, never a mutable tag.")
    return ["docker", "run", "--pull", "never", "--name", name, "--rm", "--read-only", "--network", "none",
            "--cap-drop", "ALL", "--security-opt", "no-new-privileges", "--pids-limit", "128",
            "--memory", "1g", "--cpus", "1", "--ulimit", "fsize=134217728:134217728",
            "--ulimit", "nofile=256:256", "--user", f"{os.getuid()}:{os.getgid()}",
            "--tmpfs", "/tmp:rw,nosuid,nodev,size=268435456,mode=1777",
            "--tmpfs", "/work:rw,nosuid,nodev,size=1073741824,mode=1777",
            "--mount", f"type=bind,source={source},target=/snapshot,readonly",
            "--mount", f"type=bind,source={output},target=/out",
            "--mount", f"type=bind,source={Path(__file__).resolve()},target=/workspace-runner.py,readonly",
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


def collect_artifacts(output: Path) -> list[dict]:
    """Expose only declared successful harness artifacts, never work/input files."""
    receipts = []
    total = 0
    count = 0
    for root, directories, names in os.walk(output, followlinks=False):
        for name in directories + names:
            if (Path(root) / name).is_symlink(): raise ValueError("Refusing linked sandbox output.")
        for name in names:
            count += 1
            if count > MAX_FILES: raise ValueError("Output file budget exceeded.")
            path = Path(root) / name
            data = read_regular(path, MAX_BYTES - total)
            total += len(data)
            if name == "receipt.json": receipts.append(path)
    artifacts = []
    seen = set()
    for path in receipts:
        receipt = json.loads(read_regular(path, 4 * 1024 * 1024))
        if receipt.get("schemaVersion") != "1.0" or receipt.get("executionMode") != "local" or receipt.get("status") != "succeeded": continue
        for item in receipt.get("artifacts", []):
            relative = Path(item["path"])
            if relative.is_absolute() or ".." in relative.parts or not relative.parts or relative.parts[0] != "artifacts":
                raise ValueError("Invalid declared artifact path.")
            artifact = path.parent / relative
            data = read_regular(artifact, MAX_BYTES)
            digest = hashlib.sha256(data).hexdigest()
            if len(data) != item["sizeBytes"] or digest != item["sha256"]:
                raise ValueError("Declared artifact bytes do not match the canonical harness receipt.")
            key = artifact.relative_to(output).as_posix()
            if key in seen: raise ValueError("Duplicate declared artifact.")
            seen.add(key)
            artifacts.append({"path": key, "bytes": len(data), "sha256": digest})
    return artifacts


def run(project: Path, image: str, runs: Path, timeout: float, cancelled: threading.Event | None = None, run_id: str | None = None) -> dict:
    cancellation = cancelled or threading.Event()
    run_id = run_id or uuid.uuid4().hex
    if runs.resolve().is_relative_to(project.resolve()):
        raise ValueError("Run storage must be outside the editable project.")
    if timeout <= 0 or timeout > 3600: raise ValueError("Timeout must be in (0,3600] seconds.")
    directory = runs.resolve() / run_id
    source = directory / "snapshot"
    output = directory / "output"
    directory.mkdir(parents=True, mode=0o700)
    manifest = snapshot(project, source)
    (directory / "snapshot.json").write_text(json.dumps(manifest, indent=2))
    output.mkdir()
    name = f"honua-gp-workspace-{run_id}"
    argv = command(image, source, output, name)
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
            over_budget = output_bytes > MAX_BYTES or output_count > MAX_FILES
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
        if hashlib.sha256(read_regular(source / item["path"], item["bytes"])).hexdigest() != item["sha256"]:
            raise ValueError("Immutable snapshot changed during execution.")
    artifacts = collect_artifacts(output)
    with log.open("rb") as stream:
        stream.seek(max(0, log.stat().st_size - 32768))
        diagnostics = sanitized(stream.read().decode("utf-8", errors="replace"))
    receipt = {"schema": "honua.gp.workspace-run/v1", "runId": run_id, "snapshotDigest": manifest["digest"],
               "image": image, "network": "none", "status": status, "elapsedMs": round((time.monotonic() - started) * 1000),
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
