from __future__ import annotations

import subprocess

import pytest

from honua_customcode_harness.deps import DepsRestoreError, restore_requirements


def test_no_manifest_is_noop(tmp_path) -> None:
    assert restore_requirements(tmp_path, None) is None


def test_restore_runs_pip_for_manifest(tmp_path) -> None:
    (tmp_path / "requirements.txt").write_text("requests==2.31.0\n", encoding="utf-8")
    calls = []

    def fake_runner(cmd, **kwargs):
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

    def fake_runner(cmd, **kwargs):
        return subprocess.CompletedProcess(cmd, 1, stdout="", stderr="resolution failed")

    with pytest.raises(DepsRestoreError, match="pip install"):
        restore_requirements(tmp_path, "requirements.txt", runner=fake_runner)


def test_restore_runs_pip_without_job_credentials(tmp_path, monkeypatch) -> None:
    (tmp_path / "requirements.txt").write_text("requests==2.31.0\n", encoding="utf-8")
    monkeypatch.setenv("HONUA_JOB_TOKEN", "scoped")
    monkeypatch.setenv("HONUA_JOB_REDEMPTION_CODE", "code")
    seen = {}

    def fake_runner(cmd, **kwargs):
        seen["env"] = kwargs.get("env")
        return subprocess.CompletedProcess(cmd, 0, stdout="ok", stderr="")

    restore_requirements(tmp_path, "requirements.txt", runner=fake_runner)
    assert seen["env"] is not None
    assert "HONUA_JOB_TOKEN" not in seen["env"] and "HONUA_JOB_REDEMPTION_CODE" not in seen["env"]
