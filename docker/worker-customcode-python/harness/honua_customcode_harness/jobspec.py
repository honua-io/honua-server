"""Job-input parsing for the custom-code harness.

THE CONTRACT (image/harness half). The Batch container receives the job
parameters in **one of two interchangeable ways**, resolved in this order:

1. **A mounted job-spec file** — if ``CUSTOMCODE_JOB_SPEC`` points at a readable
   JSON file, every field is read from it. This is the preferred path for
   anything non-trivial (params_json/deps can be large; a file avoids env-size
   limits and keeps secrets out of ``/proc/<pid>/environ`` for the spec body).
2. **Discrete environment variables** — otherwise each field is read from a
   ``CUSTOMCODE_*`` env var. This is the cleanest path for the common small
   job and is what local/dev invocations use.

In BOTH modes the auth spine is always env-only and never in the spec file:
``HONUA_BASE_URL`` and ``HONUA_JOB_TOKEN`` are read from the environment so the
scoped token follows the standard Batch secret-injection path and is strippable
(see :mod:`honua_customcode_harness.harness`).

Field mapping (env var -> spec key):
    CUSTOMCODE_RUNTIME         -> runtime          (e.g. "python")
    CUSTOMCODE_REPO_URL        -> repo_url
    CUSTOMCODE_GIT_REF         -> git_ref          (40-hex SHA, validated)
    CUSTOMCODE_ENTRYPOINT      -> entrypoint       ("module:func")
    CUSTOMCODE_DEPS_MANIFEST   -> deps_manifest    (path, relative to repo root)
    CUSTOMCODE_PARAMS_JSON     -> params_json      (inline JSON string OR @path)
    CUSTOMCODE_OUTPUT_PREFIX   -> output_prefix    (s3://bucket/prefix)
    CUSTOMCODE_DECLARED_SCOPE  -> declared_scope   (advisory; comma/space list)
    CUSTOMCODE_OUTPUT_MAX_BYTES-> output_max_bytes (int; default 1 GiB)
"""

from __future__ import annotations

import json
import os
import re
from collections.abc import Mapping
from dataclasses import dataclass
from pathlib import Path
from typing import Any

# A git_ref MUST be a fully-pinned 40-hex commit SHA. Symbolic refs, tags,
# branches and abbreviated SHAs are rejected: the server resolves to a pinned
# SHA before dispatch and the harness re-validates as defense-in-depth.
_GIT_SHA_RE = re.compile(r"\A[0-9a-f]{40}\Z")

_DEFAULT_OUTPUT_MAX_BYTES = 1 * 1024 * 1024 * 1024  # 1 GiB

# The highest serving<->worker job-contract version this harness can run (ADR-0060
# principle #3b). Must stay in lockstep with the server's
# CustomCodeJobContract.ContractVersion (pinned by CustomCodeJobContractDriftTests).
# A submitted job whose injected HONUA_CONTRACT_VERSION exceeds this is rejected —
# the vX-server / vY-worker safety gate. Absent is treated as version 1 for back-compat.
SUPPORTED_CONTRACT_VERSION = 1
CONTRACT_VERSION_ENV = "HONUA_CONTRACT_VERSION"


class JobSpecError(ValueError):
    """Raised when the job inputs are missing or malformed."""


@dataclass(frozen=True)
class JobSpec:
    """Validated, fully-resolved job inputs (the harness half of the contract)."""

    runtime: str
    repo_url: str
    git_ref: str
    entrypoint: str
    deps_manifest: str | None
    params: Mapping[str, Any]
    output_prefix: str
    declared_scope: tuple[str, ...]
    output_max_bytes: int
    base_url: str
    job_token: str
    contract_version: int = 1

    @property
    def entrypoint_module(self) -> str:
        return self.entrypoint.split(":", 1)[0]

    @property
    def entrypoint_func(self) -> str:
        return self.entrypoint.split(":", 1)[1]


def is_valid_git_sha(value: str | None) -> bool:
    """Return True iff ``value`` is a fully-pinned 40-character lowercase SHA."""
    return bool(value) and bool(_GIT_SHA_RE.fullmatch(value or ""))


def validate_entrypoint(entrypoint: str | None) -> str:
    if not entrypoint or ":" not in entrypoint:
        raise JobSpecError(
            f"entrypoint must be 'module:func', got {entrypoint!r}."
        )
    module, _, func = entrypoint.partition(":")
    if not module or not func or "/" in module or " " in entrypoint:
        raise JobSpecError(f"entrypoint {entrypoint!r} is malformed.")
    return entrypoint


def _load_spec_file(path: str) -> Mapping[str, Any]:
    spec_path = Path(path)
    if not spec_path.is_file():
        raise JobSpecError(f"CUSTOMCODE_JOB_SPEC points at a missing file: {spec_path}")
    try:
        data = json.loads(spec_path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as exc:  # pragma: no cover - io error
        raise JobSpecError(f"Failed to read/parse job spec {spec_path}: {exc}") from exc
    if not isinstance(data, Mapping):
        raise JobSpecError("Job spec file must contain a JSON object.")
    return data


def _coerce_params(raw: Any) -> Mapping[str, Any]:
    """Parse params_json into a dict.

    Accepts an already-parsed mapping (from a spec file), an inline JSON
    string, or an ``@/path/to/params.json`` reference.
    """
    if raw is None or raw == "":
        return {}
    if isinstance(raw, Mapping):
        return dict(raw)
    if isinstance(raw, str):
        text = raw
        if text.startswith("@"):
            params_path = Path(text[1:])
            if not params_path.is_file():
                raise JobSpecError(f"params_json @file is missing: {params_path}")
            text = params_path.read_text(encoding="utf-8")
        try:
            parsed = json.loads(text)
        except json.JSONDecodeError as exc:
            raise JobSpecError(f"params_json is not valid JSON: {exc}") from exc
        if not isinstance(parsed, Mapping):
            raise JobSpecError("params_json must decode to a JSON object.")
        return dict(parsed)
    raise JobSpecError(f"params_json has unsupported type {type(raw)!r}.")


def _coerce_scope(raw: Any) -> tuple[str, ...]:
    if not raw:
        return ()
    if isinstance(raw, (list, tuple)):
        items = [str(s).strip() for s in raw]
    else:
        items = re.split(r"[,\s]+", str(raw))
    return tuple(s for s in items if s)


def _coerce_contract_version(raw: Any) -> int:
    """Parse HONUA_CONTRACT_VERSION and fail closed if it exceeds this worker's max.

    Absent means a pre-versioning server (version 1). This is the defense-in-depth
    half of the submit-side contract gate (ADR-0060 #3b).
    """
    if raw is None or raw == "":
        return 1
    try:
        value = int(raw)
    except (TypeError, ValueError) as exc:
        raise JobSpecError(
            f"{CONTRACT_VERSION_ENV} must be a positive integer, got {raw!r}."
        ) from exc
    if value <= 0:
        raise JobSpecError(f"{CONTRACT_VERSION_ENV} must be a positive integer, got {raw!r}.")
    if value > SUPPORTED_CONTRACT_VERSION:
        raise JobSpecError(
            f"{CONTRACT_VERSION_ENV}={value} exceeds the version this worker can run "
            f"({SUPPORTED_CONTRACT_VERSION}). The worker image must be upgraded before it "
            "can run this job."
        )
    return value


def _coerce_max_bytes(raw: Any) -> int:
    if raw is None or raw == "":
        return _DEFAULT_OUTPUT_MAX_BYTES
    try:
        value = int(raw)
    except (TypeError, ValueError) as exc:
        raise JobSpecError(f"output_max_bytes must be an integer, got {raw!r}.") from exc
    if value <= 0:
        raise JobSpecError("output_max_bytes must be positive.")
    return value


def load_job_spec(env: Mapping[str, str] | None = None) -> JobSpec:
    """Resolve and validate the job inputs from a spec file or env vars.

    See module docstring for the precedence and field mapping.
    """
    env = os.environ if env is None else env

    if env.get("CUSTOMCODE_JOB_SPEC"):
        source: Mapping[str, Any] = _load_spec_file(env["CUSTOMCODE_JOB_SPEC"])

        def field(key: str) -> Any:
            return source.get(key)
    else:
        def field(key: str) -> Any:
            # Spec keys map 1:1 to CUSTOMCODE_<UPPER> env vars.
            return env.get(f"CUSTOMCODE_{key.upper()}")

    runtime = field("runtime") or "python"
    repo_url = field("repo_url")
    git_ref = field("git_ref")
    entrypoint = field("entrypoint")
    deps_manifest = field("deps_manifest") or None
    output_prefix = field("output_prefix")

    # Auth spine is ALWAYS env-only — never sourced from the spec file.
    base_url = env.get("HONUA_BASE_URL")
    job_token = env.get("HONUA_JOB_TOKEN")

    # Serving<->worker job-contract gate (ADR-0060 #3b), env-only like the auth spine.
    contract_version = _coerce_contract_version(env.get(CONTRACT_VERSION_ENV))

    _require(repo_url, "repo_url / CUSTOMCODE_REPO_URL")
    _require(git_ref, "git_ref / CUSTOMCODE_GIT_REF")
    _require(output_prefix, "output_prefix / CUSTOMCODE_OUTPUT_PREFIX")
    _require(base_url, "HONUA_BASE_URL")
    _require(job_token, "HONUA_JOB_TOKEN")

    if not is_valid_git_sha(git_ref):
        raise JobSpecError(
            f"git_ref must be a 40-character hex commit SHA (got {git_ref!r}). "
            "Branches, tags, and abbreviated SHAs are rejected."
        )
    validate_entrypoint(entrypoint)

    if not str(repo_url).startswith(("https://", "git@", "ssh://", "http://")):
        raise JobSpecError(f"repo_url has an unsupported scheme: {repo_url!r}.")
    if not str(output_prefix).startswith("s3://"):
        raise JobSpecError(f"output_prefix must be an s3:// URI, got {output_prefix!r}.")

    return JobSpec(
        runtime=str(runtime),
        repo_url=str(repo_url),
        git_ref=str(git_ref),
        entrypoint=str(entrypoint),
        deps_manifest=str(deps_manifest) if deps_manifest else None,
        params=_coerce_params(field("params_json")),
        output_prefix=str(output_prefix),
        declared_scope=_coerce_scope(field("declared_scope")),
        output_max_bytes=_coerce_max_bytes(field("output_max_bytes")),
        base_url=str(base_url),
        job_token=str(job_token),
        contract_version=contract_version,
    )


def _require(value: Any, label: str) -> None:
    if not value:
        raise JobSpecError(f"Required job input is missing: {label}.")
