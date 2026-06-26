from __future__ import annotations

import subprocess

import pytest

from honua_customcode_harness.deps import DepsRestoreError, restore_requirements


def test_no_manifest_is_noop(tmp_path) -> None:
    assert restore_requirements(tmp_path, None) is None


def test_restore_runs_pip_for_manifest(tmp_path) -> None:
    (tmp_path / "requirements.txt").write_text("requests==2.31.0\n", encoding="utf-8")
    calls = []

    def fake_runner(cmd, capture_output=True, text=True, check=False):
        calls.append(cmd)
        return subprocess.CompletedProcess(cmd, 0, stdout="ok", stderr="")

    manifest = restore_requirements(tmp_path, "requirements.txt", runner=fake_runner)
    assert manifest == (tmp_path / "requirements.txt").resolve()
    assert calls and "-r" in calls[0] and "pip" in calls[0]


def test_missing_manifest_raises(tmp_path) -> None:
    with pytest.raises(DepsRestoreError, match="not found"):
        restore_requirements(tmp_path, "nope.txt")


def test_manifest_escape_rejected(tmp_path) -> None:
    with pytest.raises(DepsRestoreError, match="escapes"):
        restore_requirements(tmp_path, "../evil.txt")


def test_pip_failure_raises(tmp_path) -> None:
    (tmp_path / "requirements.txt").write_text("bad-pkg\n", encoding="utf-8")

    def fake_runner(cmd, capture_output=True, text=True, check=False):
        return subprocess.CompletedProcess(cmd, 1, stdout="", stderr="resolution failed")

    with pytest.raises(DepsRestoreError, match="pip install"):
        restore_requirements(tmp_path, "requirements.txt", runner=fake_runner)
