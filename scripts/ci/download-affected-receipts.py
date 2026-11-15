#!/usr/bin/env python3
"""Download the newest receipt artifact for each selected PR Gate shard.

A failed-jobs rerun keeps earlier artifacts beside the new attempt. The
pattern download asks for every match, and a stale signed URL fails the
aggregate even when the newest receipt is intact. Artifact ids are not
ordered, so the attempt number in the name chooses the copy. An expired or
missing newest receipt is not replaced by an older one.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import zipfile
from pathlib import Path


PREFIX = "pr-gate-affected-shard-"
SUFFIX = re.compile(r"[A-Za-z0-9_-]+")


class DownloadRequest:
    """One aggregate download, bounded to the current run attempt."""

    def __init__(self, output: Path) -> None:
        matrix = json.loads(os.environ.get("SELECT_MATRIX") or "[]")
        if not isinstance(matrix, list):
            raise ValueError("selected matrix must be a list")
        self.repository = os.environ["GITHUB_REPOSITORY"]
        self.run_id = int(os.environ["GITHUB_RUN_ID"])
        self.attempt = int(os.environ["GITHUB_RUN_ATTEMPT"])
        self.matrix = matrix
        self.output = output


def select_artifacts(matrix: list[dict], artifacts: list[dict], attempt: int) -> list[dict]:
    """Return one newest, unexpired artifact per selected suffix."""
    selected = []
    names = set()
    for entry in matrix:
        suffix = entry.get("artifact_suffix")
        if not isinstance(suffix, str) or not SUFFIX.fullmatch(suffix) or suffix in names:
            raise ValueError("selected artifact suffixes must be safe and unique")
        names.add(suffix)
        pattern = re.compile(re.escape(PREFIX + suffix) + r"-attempt-([1-9][0-9]*)$")
        candidates = []
        for artifact in artifacts:
            match = pattern.fullmatch(str(artifact.get("name", "")))
            if match and int(match[1]) <= attempt:
                candidates.append((int(match[1]), artifact))
        if not candidates:
            raise ValueError(f"no receipt artifact for selected shard {suffix}")
        newest = max(number for number, _ in candidates)
        latest = [artifact for number, artifact in candidates if number == newest]
        if len(latest) != 1 or latest[0].get("expired") is True:
            raise ValueError(f"newest receipt is ambiguous or expired for {suffix}")
        artifact_id = latest[0].get("id")
        if type(artifact_id) is not int or artifact_id <= 0:
            raise ValueError(f"invalid artifact ID for {suffix}")
        selected.append(latest[0])
    return selected


def extract_archive(archive_path: Path, destination: Path) -> None:
    """Extract a receipt zip into a fresh directory, rejecting path escape."""
    with zipfile.ZipFile(archive_path) as archive:
        names = [entry.filename for entry in archive.infolist()]
        for name in names:
            path = Path(name)
            if path.is_absolute() or ".." in path.parts or "\\" in name:
                raise ValueError("unsafe receipt archive path")
        files = [name for name in names if not name.endswith("/")]
        if len(files) != len(set(files)):
            raise ValueError("duplicate receipt archive path")
        archive.extractall(destination)


def _catalog(request: DownloadRequest) -> list[dict]:
    catalog = subprocess.run(
        ["gh", "api", "--method", "GET", "--paginate", "--slurp",
         f"repos/{request.repository}/actions/runs/{request.run_id}/artifacts?per_page=100"],
        check=True, stdout=subprocess.PIPE,
    )
    pages = json.loads(catalog.stdout)
    if not isinstance(pages, list):
        raise ValueError("artifact catalog must be a page list")
    artifacts = []
    for page in pages:
        artifacts.extend(page["artifacts"])
    return artifacts


def _verify_digest(archive: Path, artifact: dict) -> None:
    digest = artifact.get("digest")
    if not digest:
        return
    with archive.open("rb") as handle:
        actual = "sha256:" + hashlib.sha256(handle.read()).hexdigest()
    if digest != actual:
        raise ValueError(f"archive digest mismatch for {artifact['name']}")


def _save_archive(request: DownloadRequest, artifact: dict) -> None:
    """Download one artifact id into its own attempt directory."""
    with tempfile.TemporaryDirectory(dir=request.output.parent) as temporary:
        temporary_path = Path(temporary)
        archive = temporary_path / "receipt.zip"
        print(f"Downloading {artifact['name']} (ID {artifact['id']})", flush=True)
        with archive.open("wb") as handle:
            subprocess.run(
                ["gh", "api", "--method", "GET",
                 f"repos/{request.repository}/actions/artifacts/{artifact['id']}/zip"],
                check=True, stdout=handle,
            )
        _verify_digest(archive, artifact)
        contents = temporary_path / "contents"
        extract_archive(archive, contents)
        shutil.move(str(contents), request.output / artifact["name"])


def download(request: DownloadRequest) -> None:
    """Download newest selected receipts. An empty matrix writes nothing."""
    if not re.fullmatch(r"[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+", request.repository):
        raise ValueError("invalid repository")
    if request.run_id <= 0 or request.attempt <= 0:
        raise ValueError("run ID and attempt must be positive")
    if request.output.exists() and any(request.output.iterdir()):
        raise ValueError("receipt destination must be empty")
    if not request.matrix:
        return
    request.output.parent.mkdir(parents=True, exist_ok=True)
    selected = select_artifacts(request.matrix, _catalog(request), request.attempt)
    request.output.mkdir(parents=True, exist_ok=True)
    for artifact in selected:
        _save_archive(request, artifact)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    download(DownloadRequest(args.output))


if __name__ == "__main__":
    try:
        main()
    except (ValueError, KeyError, OSError, subprocess.CalledProcessError,
            json.JSONDecodeError, zipfile.BadZipFile) as error:
        print(f"::error::affected receipt download failed: {error}", file=sys.stderr)
        sys.exit(1)
