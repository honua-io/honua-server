"""The custom-code harness entrypoint.

Flow (each step is a separately-testable function above):

  1. Load + validate job inputs (:mod:`jobspec`): repo_url, git_ref (40-hex SHA),
     entrypoint, deps_manifest, params_json, output_prefix, HONUA_BASE_URL,
     HONUA_JOB_TOKEN.
  2. Clone the user code at the pinned SHA and assert HEAD == SHA
     (:mod:`sourcefetch`).
  3. Restore user-declared extra deps (:mod:`deps`) — the SDK + geo stack are
     already baked into the image.
  4. Build the SCOPED HonuaClient from HONUA_JOB_TOKEN, then STRIP IMDS/token env
     (:mod:`sandbox`) BEFORE any user code is imported.
  5. Import the entrypoint (:mod:`loader`), build a :class:`GpContext`, call it.
  6. Upload returned artifacts to output_prefix (:mod:`upload`); map the
     :class:`GpResult` to a terminal exit code.

Exit codes: 0 = succeeded, 1 = tool returned/raised failure, 2 = harness/setup
error (bad inputs, clone/verify failure), 3 = job cancelled.
"""

from __future__ import annotations

import signal
import sys
import traceback
from collections.abc import Mapping
from pathlib import Path
from typing import Any

from .context import (
    CancellationToken,
    GpContext,
    GpResult,
    JobCancelled,
    OutputSink,
    OutputSizeExceeded,
    _LoggingProgressReporter,
    _StdLogger,
)
from .deps import DepsRestoreError, restore_requirements
from .jobspec import JobSpec, JobSpecError, load_job_spec
from .loader import EntrypointError, load_entrypoint
from .sandbox import (
    assert_credentials_stripped,
    build_scoped_client,
    strip_credential_env,
)
from .sourcefetch import SourceFetchError, clone_pinned
from .upload import ArtifactUploader

EXIT_OK = 0
EXIT_TOOL_FAILED = 1
EXIT_HARNESS_ERROR = 2
EXIT_CANCELLED = 3

DEFAULT_SOURCE_ROOT = Path("/work/src")
DEFAULT_WORKDIR = Path("/work/out")


def run(
    *,
    env: Mapping[str, str] | None = None,
    source_root: Path = DEFAULT_SOURCE_ROOT,
    workdir: Path = DEFAULT_WORKDIR,
    client_factory: Any = None,
    uploader_factory: Any = None,
    clone_fn=clone_pinned,
    restore_fn=restore_requirements,
    strip_env: bool = True,
) -> int:
    """Run one custom-code job end-to-end and return a process exit code.

    Most collaborators are injectable so the whole flow is testable offline.
    In production the defaults wire to git, pip, the real SDK, and boto3.
    """
    log = _StdLogger()
    cancellation = CancellationToken()
    _install_cancellation_handler(cancellation, log)

    # --- 1. Inputs ---------------------------------------------------------
    try:
        spec = load_job_spec(env)
    except JobSpecError as exc:
        log.warn(f"invalid job inputs: {exc}")
        return EXIT_HARNESS_ERROR

    # --- 2. Pinned source --------------------------------------------------
    try:
        log.info(f"cloning {spec.repo_url} @ {spec.git_ref}")
        clone_fn(spec.repo_url, spec.git_ref, source_root)
    except SourceFetchError as exc:
        log.warn(f"source fetch failed: {exc}")
        return EXIT_HARNESS_ERROR

    # --- 3. Restore extra deps (SDK + geo already baked in) ----------------
    try:
        manifest = restore_fn(source_root, spec.deps_manifest)
        if manifest is not None:
            log.info(f"restored extra deps from {manifest}")
    except DepsRestoreError as exc:
        log.warn(f"dependency restore failed: {exc}")
        return EXIT_HARNESS_ERROR

    # --- 4. Scoped client, THEN scrub credentials --------------------------
    try:
        client = build_scoped_client(
            spec.base_url, spec.job_token, client_factory=client_factory
        )
    except Exception as exc:  # noqa: BLE001 - surface SDK construction errors
        log.warn(f"failed to construct scoped client: {exc}")
        return EXIT_HARNESS_ERROR

    if strip_env:
        removed = strip_credential_env()
        log.info(f"stripped credential env before user code: {list(removed)}")
        # Invariant: nothing sensitive may remain visible to the tool.
        assert_credentials_stripped()

    # --- 5. Load entrypoint + run -----------------------------------------
    workdir.mkdir(parents=True, exist_ok=True)
    output = OutputSink(max_total_bytes=spec.output_max_bytes)
    context = GpContext(
        params=spec.params,
        inputs={},
        client=client,
        output=output,
        progress=_LoggingProgressReporter(log),
        log=log,
        cancellation=cancellation,
        workdir=workdir,
    )

    try:
        func = load_entrypoint(source_root, spec.entrypoint)
    except EntrypointError as exc:
        log.warn(f"entrypoint load failed: {exc}")
        return EXIT_HARNESS_ERROR

    try:
        result = func(context)
    except JobCancelled as exc:
        log.warn(f"job cancelled: {exc}")
        return EXIT_CANCELLED
    except Exception:  # noqa: BLE001 - any tool error is a tool failure
        log.warn("tool raised an unhandled exception:")
        log.warn(traceback.format_exc())
        return EXIT_TOOL_FAILED
    finally:
        _close_quietly(client)

    result = _coerce_result(result, log)
    if not result.ok:
        log.warn(f"tool reported failure: {result.message}")
        return EXIT_TOOL_FAILED

    # --- 6. Upload artifacts ----------------------------------------------
    try:
        uploader = _build_uploader(spec, uploader_factory)
        uploaded = uploader.upload(output.artifacts)
        for u in uploaded:
            log.info(f"uploaded {u.name} -> {u.uri} ({u.size_bytes} bytes)")
    except OutputSizeExceeded as exc:
        log.warn(f"output cap exceeded: {exc}")
        return EXIT_TOOL_FAILED
    except Exception as exc:  # noqa: BLE001 - upload failure is terminal
        log.warn(f"artifact upload failed: {exc}")
        return EXIT_HARNESS_ERROR

    log.info(f"job succeeded: {result.message or 'ok'}")
    return EXIT_OK


def _build_uploader(spec: JobSpec, uploader_factory: Any) -> Any:
    if uploader_factory is not None:
        return uploader_factory(spec.output_prefix)
    return ArtifactUploader(spec.output_prefix)


def _coerce_result(result: Any, log: Any) -> GpResult:
    if isinstance(result, GpResult):
        return result
    if result is None:
        # A tool that returns nothing but didn't raise is treated as success.
        return GpResult.succeeded()
    log.warn(
        f"tool returned {type(result)!r}, expected GpResult; treating as failure."
    )
    return GpResult.failed(f"entrypoint returned non-GpResult {type(result)!r}.")


def _install_cancellation_handler(token: CancellationToken, log: Any) -> None:
    def _handler(signum: int, _frame: Any) -> None:
        log.warn(f"received signal {signum}; requesting cancellation.")
        token.cancel()

    try:
        signal.signal(signal.SIGTERM, _handler)
        signal.signal(signal.SIGINT, _handler)
    except ValueError:  # pragma: no cover - not on main thread (tests)
        pass


def _close_quietly(client: Any) -> None:
    close = getattr(client, "close", None)
    if callable(close):
        try:
            close()
        except Exception:  # noqa: BLE001 - best-effort cleanup
            pass


def main(argv: list[str] | None = None) -> int:  # pragma: no cover - thin wrapper
    return run()


if __name__ == "__main__":  # pragma: no cover
    sys.exit(main())
