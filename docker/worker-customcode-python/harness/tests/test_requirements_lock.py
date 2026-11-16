"""The Python custom-code worker image installs only hash-pinned packages.

The image runs untrusted operator code next to everything baked into it, so the
packages it bakes in are installed from requirements.lock with --require-hashes,
binary-only and without dependency resolution. These tests keep the Dockerfile,
the lock, requirements.in and the harness's own honua-sdk pin in step.
"""

from __future__ import annotations

import re
import tomllib
from pathlib import Path

_IMAGE_ROOT = Path(__file__).resolve().parents[2]
_DOCKERFILE = _IMAGE_ROOT / "Dockerfile"
_LOCK = _IMAGE_ROOT / "requirements.lock"
_INPUT = _IMAGE_ROOT / "requirements.in"
_PYPROJECT = _IMAGE_ROOT / "harness" / "pyproject.toml"
_BASE_IMAGE_MARK = "# provided by the base image"

_ENTRY = re.compile(r"^([a-z0-9][a-z0-9._-]*)==([^\s\\]+)((?:\s*\\?\s*--hash=sha256:[0-9a-f]{64})+)\s*$")


def _normalize(name: str) -> str:
    return re.sub(r"[-_.]+", "-", name).lower()


def _lock_entries() -> dict[str, str]:
    text = "\n".join(line for line in _LOCK.read_text(encoding="utf-8").splitlines()
                     if not line.startswith("#"))
    entries = {}
    for block in re.split(r"\n(?=\S)", text.strip()):
        match = _ENTRY.match(block.replace("\n", " "))
        assert match, f"requirements.lock entry is not name==version with sha256 hashes: {block[:80]!r}"
        entries[_normalize(match.group(1))] = match.group(2)
    return entries


def _pip_installs() -> list[list[str]]:
    """The arguments after ``install`` of every pip invocation, in Dockerfile order."""
    joined = _DOCKERFILE.read_text(encoding="utf-8").replace("\\\n", " ")
    return [match.group(1).split() for match in re.finditer(r"\bpip3?\s+install\b([^\n&;|]*)", joined)]


def test_every_lock_entry_is_pinned_and_hashed() -> None:
    assert len(_lock_entries()) > 0


def test_lock_covers_every_top_level_package() -> None:
    # A constraint on a package the base image already provides (numpy) is
    # satisfied by the base image and so is not in the lock.
    top_level = [re.split(r"[<>=!~\s;\[]", line, maxsplit=1)[0]
                 for line in _INPUT.read_text(encoding="utf-8").splitlines()
                 if line.strip() and not line.startswith("#") and not line.endswith(_BASE_IMAGE_MARK)]
    missing = sorted(_normalize(name) for name in top_level if _normalize(name) not in _lock_entries())
    assert not missing, f"requirements.in packages missing from requirements.lock: {missing}"


def test_base_image_packages_are_not_locked() -> None:
    provided = [_normalize(re.split(r"[<>=!~\s]", line, maxsplit=1)[0])
                for line in _INPUT.read_text(encoding="utf-8").splitlines() if line.endswith(_BASE_IMAGE_MARK)]
    assert "numpy" in provided
    assert not set(provided) & set(_lock_entries()), "a base-image package would be replaced by pip"


def test_lock_matches_the_harness_sdk_pin() -> None:
    dependencies = tomllib.loads(_PYPROJECT.read_text(encoding="utf-8"))["project"]["dependencies"]
    pin = next(dep for dep in dependencies if _normalize(dep.split("==")[0]) == "honua-sdk")
    assert _lock_entries()["honua-sdk"] == pin.split("==", 1)[1]


def test_dockerfile_installs_only_from_the_lock() -> None:
    # Exact argument sets: any extra package or dropped flag fails, not only a missing one.
    common = ["--no-cache-dir", "--break-system-packages"]
    assert _pip_installs() == [
        [*common, "--require-hashes", "--no-deps", "--only-binary=:all:", "-r", "/opt/honua-customcode-requirements.lock"],
        [*common, "--no-deps", "--no-build-isolation", "/opt/honua-customcode-harness"],
    ]
