"""End-to-end harness flow, fully offline (fake git/pip/SDK/uploader)."""

from __future__ import annotations

import json
import os

from honua_customcode_harness import harness
from honua_customcode_harness.harness import (
    EXIT_HARNESS_ERROR,
    EXIT_OK,
    EXIT_TOOL_FAILED,
    run,
)

GOOD_SHA = "d" * 40


class FakeClient:
    def __init__(self) -> None:
        self.closed = False

    def close(self) -> None:
        self.closed = True


class FakeUploader:
    instances: list["FakeUploader"] = []

    def __init__(self, output_prefix: str) -> None:
        self.output_prefix = output_prefix
        self.uploaded: list = []
        FakeUploader.instances.append(self)

    def upload(self, artifacts):
        from honua_customcode_harness.upload import UploadResult

        results = []
        for a in artifacts:
            self.uploaded.append(a)
            results.append(UploadResult(a.name, f"{self.output_prefix}/{a.name}", a.size_bytes))
        return results


def _env(tmp_path, entrypoint="tool:execute", params=None):
    return {
        "HONUA_BASE_URL": "https://api.honua.test",
        "HONUA_JOB_TOKEN": "scoped-token",
        "AWS_CONTAINER_CREDENTIALS_RELATIVE_URI": "/v2/creds",
        "CUSTOMCODE_REPO_URL": "https://github.com/acme/tool.git",
        "CUSTOMCODE_GIT_REF": GOOD_SHA,
        "CUSTOMCODE_ENTRYPOINT": entrypoint,
        "CUSTOMCODE_OUTPUT_PREFIX": "s3://bucket/jobs/1",
        "CUSTOMCODE_PARAMS_JSON": json.dumps(params or {}),
    }


def _make_clone_fn(tool_body: str):
    def clone_fn(repo_url, git_ref, dest):
        dest.mkdir(parents=True, exist_ok=True)
        (dest / "tool.py").write_text(tool_body, encoding="utf-8")
        return dest

    return clone_fn


def test_e2e_success_uploads_artifact(tmp_path) -> None:
    FakeUploader.instances.clear()
    tool = (
        "from honua_customcode_harness import GpResult\n"
        "def execute(context):\n"
        "    p = context.workdir / 'r.txt'\n"
        "    p.write_text('result')\n"
        "    context.output.add_artifact('r.txt', p)\n"
        "    context.progress.report(100, 'done')\n"
        "    return GpResult.succeeded('ok')\n"
    )
    rc = run(
        env=_env(tmp_path),
        source_root=tmp_path / "src",
        workdir=tmp_path / "out",
        client_factory=lambda b, t: FakeClient(),
        uploader_factory=lambda prefix: FakeUploader(prefix),
        clone_fn=_make_clone_fn(tool),
        restore_fn=lambda root, manifest: None,
    )
    assert rc == EXIT_OK
    assert len(FakeUploader.instances) == 1
    assert len(FakeUploader.instances[0].uploaded) == 1
    assert FakeUploader.instances[0].uploaded[0].name == "r.txt"


def test_e2e_strips_credentials_before_user_code(tmp_path, monkeypatch) -> None:
    # Real os.environ carries the full job spec + token + IMDS var; the tool
    # must not see the token or the IMDS var after the strip.
    for key, value in _env(tmp_path).items():
        monkeypatch.setenv(key, value)
    tool = (
        "import os\n"
        "from honua_customcode_harness import GpResult\n"
        "def execute(context):\n"
        "    if 'HONUA_JOB_TOKEN' in os.environ:\n"
        "        return GpResult.failed('token leaked')\n"
        "    if 'AWS_CONTAINER_CREDENTIALS_RELATIVE_URI' in os.environ:\n"
        "        return GpResult.failed('imds leaked')\n"
        "    return GpResult.succeeded('clean')\n"
    )
    rc = run(
        env=None,  # use real os.environ so the strip is observable
        source_root=tmp_path / "src",
        workdir=tmp_path / "out",
        client_factory=lambda b, t: FakeClient(),
        uploader_factory=lambda prefix: FakeUploader(prefix),
        clone_fn=_make_clone_fn(tool),
        restore_fn=lambda root, manifest: None,
    )
    assert rc == EXIT_OK
    # And after the run the token + IMDS var are gone from the process env.
    assert "HONUA_JOB_TOKEN" not in os.environ
    assert "AWS_CONTAINER_CREDENTIALS_RELATIVE_URI" not in os.environ


def test_e2e_tool_failure_maps_to_exit_1(tmp_path) -> None:
    tool = (
        "from honua_customcode_harness import GpResult\n"
        "def execute(context):\n"
        "    return GpResult.failed('boom')\n"
    )
    rc = run(
        env=_env(tmp_path),
        source_root=tmp_path / "src",
        workdir=tmp_path / "out",
        client_factory=lambda b, t: FakeClient(),
        uploader_factory=lambda prefix: FakeUploader(prefix),
        clone_fn=_make_clone_fn(tool),
        restore_fn=lambda root, manifest: None,
    )
    assert rc == EXIT_TOOL_FAILED


def test_e2e_tool_exception_maps_to_exit_1(tmp_path) -> None:
    tool = (
        "def execute(context):\n"
        "    raise RuntimeError('kaboom')\n"
    )
    rc = run(
        env=_env(tmp_path),
        source_root=tmp_path / "src",
        workdir=tmp_path / "out",
        client_factory=lambda b, t: FakeClient(),
        uploader_factory=lambda prefix: FakeUploader(prefix),
        clone_fn=_make_clone_fn(tool),
        restore_fn=lambda root, manifest: None,
    )
    assert rc == EXIT_TOOL_FAILED


def test_e2e_bad_git_ref_is_harness_error(tmp_path) -> None:
    env = _env(tmp_path)
    env["CUSTOMCODE_GIT_REF"] = "main"
    rc = run(
        env=env,
        source_root=tmp_path / "src",
        workdir=tmp_path / "out",
        client_factory=lambda b, t: FakeClient(),
        uploader_factory=lambda prefix: FakeUploader(prefix),
        clone_fn=_make_clone_fn("def execute(c): pass"),
        restore_fn=lambda root, manifest: None,
    )
    assert rc == EXIT_HARNESS_ERROR


def test_e2e_closes_client(tmp_path) -> None:
    client = FakeClient()
    tool = (
        "from honua_customcode_harness import GpResult\n"
        "def execute(context):\n"
        "    return GpResult.succeeded()\n"
    )
    run(
        env=_env(tmp_path),
        source_root=tmp_path / "src",
        workdir=tmp_path / "out",
        client_factory=lambda b, t: client,
        uploader_factory=lambda prefix: FakeUploader(prefix),
        clone_fn=_make_clone_fn(tool),
        restore_fn=lambda root, manifest: None,
    )
    assert client.closed is True
