from __future__ import annotations

import json

import pytest

from honua_customcode_harness.jobspec import (
    JobSpecError,
    is_valid_git_sha,
    load_job_spec,
    validate_entrypoint,
)

GOOD_SHA = "a" * 40
BASE_ENV = {
    "HONUA_BASE_URL": "https://api.honua.test",
    "HONUA_JOB_TOKEN": "scoped-token-123",
    "CUSTOMCODE_REPO_URL": "https://github.com/acme/tool.git",
    "CUSTOMCODE_GIT_REF": GOOD_SHA,
    "CUSTOMCODE_ENTRYPOINT": "tool.main:execute",
    "CUSTOMCODE_OUTPUT_PREFIX": "s3://bucket/jobs/42",
}


@pytest.mark.parametrize(
    "value",
    [GOOD_SHA, "0123456789abcdef0123456789abcdef01234567"],
)
def test_valid_sha_accepted(value: str) -> None:
    assert is_valid_git_sha(value)


@pytest.mark.parametrize(
    "value",
    [
        None,
        "",
        "main",
        "v1.2.3",
        "HEAD",
        "a" * 39,  # too short
        "a" * 41,  # too long
        "A" * 40,  # uppercase rejected
        "g" * 40,  # non-hex
        "feature/x",
        "abc123",  # abbreviated
    ],
)
def test_non_sha_rejected(value) -> None:
    assert not is_valid_git_sha(value)


def test_load_rejects_non_sha_ref() -> None:
    env = dict(BASE_ENV, CUSTOMCODE_GIT_REF="main")
    with pytest.raises(JobSpecError, match="40-character hex"):
        load_job_spec(env)


def test_load_happy_path_from_env() -> None:
    env = dict(
        BASE_ENV,
        CUSTOMCODE_PARAMS_JSON=json.dumps({"k": 1}),
        CUSTOMCODE_DECLARED_SCOPE="read:features write:jobs",
        CUSTOMCODE_DEPS_MANIFEST="requirements.txt",
    )
    spec = load_job_spec(env)
    assert spec.git_ref == GOOD_SHA
    assert spec.repo_url == "https://github.com/acme/tool.git"
    assert spec.entrypoint_module == "tool.main"
    assert spec.entrypoint_func == "execute"
    assert spec.params == {"k": 1}
    assert spec.declared_scope == ("read:features", "write:jobs")
    assert spec.deps_manifest == "requirements.txt"
    assert spec.base_url == "https://api.honua.test"
    assert spec.job_token == "scoped-token-123"
    assert spec.output_max_bytes == 1 * 1024 * 1024 * 1024


def test_load_from_spec_file_with_env_auth(tmp_path) -> None:
    spec_file = tmp_path / "job.json"
    spec_file.write_text(
        json.dumps(
            {
                "runtime": "python",
                "repo_url": "https://github.com/acme/tool.git",
                "git_ref": GOOD_SHA,
                "entrypoint": "tool:execute",
                "params_json": {"buffer": 2},
                "output_prefix": "s3://bucket/p",
                "output_max_bytes": 1024,
            }
        ),
        encoding="utf-8",
    )
    env = {
        "CUSTOMCODE_JOB_SPEC": str(spec_file),
        "HONUA_BASE_URL": "https://api.honua.test",
        "HONUA_JOB_TOKEN": "tok",
    }
    spec = load_job_spec(env)
    assert spec.params == {"buffer": 2}
    assert spec.output_max_bytes == 1024
    assert spec.job_token == "tok"


def test_spec_file_cannot_override_token(tmp_path) -> None:
    # Even if the spec file tries to smuggle a token, the auth spine is env-only.
    spec_file = tmp_path / "job.json"
    spec_file.write_text(
        json.dumps(
            {
                "repo_url": "https://github.com/acme/tool.git",
                "git_ref": GOOD_SHA,
                "entrypoint": "tool:execute",
                "output_prefix": "s3://bucket/p",
                "HONUA_JOB_TOKEN": "evil-token",
                "base_url": "https://evil.test",
            }
        ),
        encoding="utf-8",
    )
    env = {
        "CUSTOMCODE_JOB_SPEC": str(spec_file),
        "HONUA_BASE_URL": "https://api.honua.test",
        "HONUA_JOB_TOKEN": "real-token",
    }
    spec = load_job_spec(env)
    assert spec.job_token == "real-token"
    assert spec.base_url == "https://api.honua.test"


def test_missing_required_field_raises() -> None:
    env = dict(BASE_ENV)
    del env["HONUA_JOB_TOKEN"]
    with pytest.raises(JobSpecError, match="HONUA_JOB_TOKEN"):
        load_job_spec(env)


def test_output_prefix_must_be_s3() -> None:
    env = dict(BASE_ENV, CUSTOMCODE_OUTPUT_PREFIX="/local/path")
    with pytest.raises(JobSpecError, match="s3://"):
        load_job_spec(env)


def test_params_json_from_at_file(tmp_path) -> None:
    params_file = tmp_path / "p.json"
    params_file.write_text(json.dumps({"a": [1, 2]}), encoding="utf-8")
    env = dict(BASE_ENV, CUSTOMCODE_PARAMS_JSON=f"@{params_file}")
    spec = load_job_spec(env)
    assert spec.params == {"a": [1, 2]}


def test_bad_params_json_raises() -> None:
    env = dict(BASE_ENV, CUSTOMCODE_PARAMS_JSON="{not json")
    with pytest.raises(JobSpecError, match="valid JSON"):
        load_job_spec(env)


@pytest.mark.parametrize("ep", ["nocolon", "", ":func", "mod:", "mod with space:f"])
def test_validate_entrypoint_rejects_bad(ep: str) -> None:
    with pytest.raises(JobSpecError):
        validate_entrypoint(ep)


def test_validate_entrypoint_accepts_good() -> None:
    assert validate_entrypoint("pkg.mod:execute") == "pkg.mod:execute"
