"""Restore user-declared extra dependencies.

The image already pre-installs the SDK + geospatial stack, so the common case
needs no restore. When a tool ships a ``deps_manifest`` (a requirements file
relative to the repo root), we ``pip install -r`` it into the runtime so the
user's extras are importable. We never let pip touch the pre-installed pinned
SDK/geo packages destructively beyond what the user explicitly requests.
"""

from __future__ import annotations

import subprocess
import sys
from pathlib import Path


class DepsRestoreError(RuntimeError):
    """Raised when restoring user-declared dependencies fails."""


def restore_requirements(
    source_root: Path,
    deps_manifest: str | None,
    *,
    runner=subprocess.run,
) -> Path | None:
    """``pip install -r <deps_manifest>`` if one is declared.

    ``deps_manifest`` is resolved relative to ``source_root`` and must stay
    inside it (no ``..`` escapes). Returns the resolved manifest path, or
    ``None`` when no manifest was declared.
    """
    if not deps_manifest:
        return None

    manifest = (source_root / deps_manifest).resolve()
    root = source_root.resolve()
    if root != manifest and root not in manifest.parents:
        raise DepsRestoreError(
            f"deps_manifest {deps_manifest!r} escapes the source root."
        )
    if not manifest.is_file():
        raise DepsRestoreError(f"deps_manifest not found: {manifest}")

    cmd = [
        sys.executable,
        "-m",
        "pip",
        "install",
        "--no-input",
        "--disable-pip-version-check",
        "-r",
        str(manifest),
    ]
    proc = runner(cmd, capture_output=True, text=True, check=False)
    if proc.returncode != 0:
        raise DepsRestoreError(
            f"pip install -r {manifest} failed ({proc.returncode}): "
            f"{proc.stderr.strip()[-2000:]}"
        )
    return manifest
