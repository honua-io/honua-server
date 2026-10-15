"""End-to-end harness flow, fully offline (fake git/pip/SDK/uploader)."""

from __future__ import annotations

import json
import os

from honua_customcode_harness.harness import (
    EXIT_HARNESS_ERROR,
    EXIT_OK,
    EXIT_TOOL_FAILED,
    run,
)

GOOD_SHA = "d" * 40
NL = chr(10)


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
        uploader_factory=FakeUploader,
        clone_fn=_make_clone_fn(tool),
        restore_fn=lambda root, manifest: None,
    )
    assert rc == EXIT_OK
    assert len(FakeUploader.instances) == 1
    uploaded = FakeUploader.instances[0].uploaded
    assert [a.name for a in uploaded] == ["r.txt", "honua-events.v1.jsonl"]
    events = [json.loads(line) for line in uploaded[1].path.read_text(encoding="utf-8").splitlines()]
    assert any(e["kind"] == "progress" and e["percent"] == 100 and e["phase"] == "done" for e in events)
    assert any(e["kind"] == "artifact" and e["artifact"]["name"] == "r.txt" for e in events)
    assert events[-1]["kind"] == "terminal" and events[-1]["status"] == "succeeded" and events[-1]["exitCode"] == 0
    assert all(e["v"] == 1 and e["attempt"] == "1" for e in events)


def test_e2e_events_path_and_attempt_come_from_env(tmp_path) -> None:
    FakeUploader.instances.clear()
    tool = "from honua_customcode_harness import GpResult" + NL + "def execute(context):" + NL + "    return GpResult.succeeded()" + NL
    env = {**_env(tmp_path), "CUSTOMCODE_EVENTS_PATH": str(tmp_path / "evidence" / "ev.jsonl"), "AWS_BATCH_JOB_ATTEMPT": "3"}
    rc = run(env=env, source_root=tmp_path / "src", workdir=tmp_path / "out", client_factory=lambda b, t: FakeClient(),
             uploader_factory=FakeUploader, clone_fn=_make_clone_fn(tool), restore_fn=lambda root, manifest: None)
    assert rc == EXIT_OK
    events = [json.loads(line) for line in (tmp_path / "evidence" / "ev.jsonl").read_text(encoding="utf-8").splitlines()]
    assert events and all(e["attempt"] == "3" for e in events)
    assert [e["phase"] for e in events if e["kind"] == "phase"] == ["inputs", "source", "dependencies", "client", "execute", "upload"]


def test_e2e_tool_failure_writes_failure_and_terminal_without_upload(tmp_path) -> None:
    FakeUploader.instances.clear()
    tool = "from honua_customcode_harness import GpResult" + NL + "def execute(context):" + NL + "    raise ValueError('broken')" + NL
    rc = run(env=_env(tmp_path), source_root=tmp_path / "src", workdir=tmp_path / "out", client_factory=lambda b, t: FakeClient(),
             uploader_factory=FakeUploader, clone_fn=_make_clone_fn(tool), restore_fn=lambda root, manifest: None)
    assert rc == EXIT_TOOL_FAILED
    assert FakeUploader.instances == []
    events = [json.loads(line) for line in (tmp_path / "out" / "honua-events.v1.jsonl").read_text(encoding="utf-8").splitlines()]
    assert events[-2]["kind"] == "failure" and events[-2]["error"]["type"] == "ValueError"
    assert events[-1]["kind"] == "terminal" and events[-1]["status"] == "failed" and events[-1]["phase"] == "execute"


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
        uploader_factory=FakeUploader,
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
        uploader_factory=FakeUploader,
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
        uploader_factory=FakeUploader,
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
        uploader_factory=FakeUploader,
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
        uploader_factory=FakeUploader,
        clone_fn=_make_clone_fn(tool),
        restore_fn=lambda root, manifest: None,
    )
    assert client.closed is True
