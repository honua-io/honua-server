#!/usr/bin/env python3
"""Read the one local pre-PR manifest: committed, staged, unstaged, deleted, and untracked paths."""
import os
from pathlib import Path
import subprocess

CLASSIFIER = Path(__file__).resolve().parent / "classify-pre-pr-changes.sh"


def read_local_manifest(root, base="origin/trunk"):
    repo = Path(root).resolve()
    env = os.environ.copy()
    env["HONUA_MANIFEST_ROOT"] = str(repo)
    completed = subprocess.run(
        ["bash", str(CLASSIFIER), str(base)],
        cwd=repo,
        env=env,
        check=False,
        capture_output=True,
        text=True,
    )
    if completed.returncode != 0:
        detail = (completed.stderr or completed.stdout).strip()
        raise ValueError(f"local change classifier failed: {detail}")
    lines = completed.stdout.splitlines()
    if not lines or lines[0] not in {"CI_ONLY", "NORMAL", "UNKNOWN"}:
        raise ValueError("local change classifier returned an unrecognized status")
    if lines[0] == "UNKNOWN":
        raise ValueError("local change classifier could not freeze a complete manifest")
    return lines[1:]
