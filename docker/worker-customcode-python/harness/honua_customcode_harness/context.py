"""The ``execute(context)`` contract surface for custom-code GP tools.

These types are the *only* thing a tool author imports. A user tool ships a
module exposing ``def execute(context) -> GpResult`` (or any callable named in
the ``customcode.entrypoint`` ``module:func`` spec). The harness constructs the
context, calls the function, and turns the returned :class:`GpResult` into a
terminal job status.

Stability note: this module is the SDK-facing API. Keep it additive — adding
attributes is fine; renaming/removing breaks user tools pinned to an image.
"""

from __future__ import annotations

import os
from collections.abc import Mapping
from dataclasses import dataclass, field
from pathlib import Path
from typing import TYPE_CHECKING, Any, Protocol

if TYPE_CHECKING:  # pragma: no cover - typing only
    from honua_sdk import HonuaClient


class GpStatus:
    """Terminal status labels for a :class:`GpResult`."""

    SUCCEEDED = "succeeded"
    FAILED = "failed"


@dataclass(frozen=True)
class GpResult:
    """The terminal result a tool returns from ``execute(context)``.

    Construct via the classmethods rather than the initializer so the status
    label stays canonical.
    """

    status: str
    message: str | None = None

    @classmethod
    def succeeded(cls, message: str | None = None) -> "GpResult":
        return cls(status=GpStatus.SUCCEEDED, message=message)

    @classmethod
    def failed(cls, message: str) -> "GpResult":
        if not message:
            raise ValueError("GpResult.failed() requires a non-empty message.")
        return cls(status=GpStatus.FAILED, message=message)

    @property
    def ok(self) -> bool:
        return self.status == GpStatus.SUCCEEDED


@dataclass(frozen=True)
class Artifact:
    """A named output file the tool wants uploaded to ``output_prefix``."""

    name: str
    path: Path
    size_bytes: int


class OutputSink:
    """Collects artifacts a tool wants persisted to the job's output prefix.

    The harness is responsible for the actual upload (to S3) after
    ``execute`` returns; this sink only records intent and enforces the
    per-job output-size cap eagerly so a runaway tool fails fast rather than
    after writing gigabytes.
    """

    def __init__(self, *, max_total_bytes: int) -> None:
        if max_total_bytes <= 0:
            raise ValueError("max_total_bytes must be positive.")
        self._max_total_bytes = max_total_bytes
        self._artifacts: list[Artifact] = []
        self._total_bytes = 0

    def add_artifact(self, name: str, path: str | os.PathLike[str]) -> Artifact:
        """Register a file on disk for upload under ``name``.

        Raises if the file is missing or if adding it would exceed the
        configured output-size cap.
        """
        if not name or "/" in name or name in {".", ".."}:
            raise ValueError(f"Artifact name {name!r} must be a simple file name.")
        resolved = Path(path)
        if not resolved.is_file():
            raise FileNotFoundError(f"Artifact {name!r} path does not exist: {resolved}")
        size = resolved.stat().st_size
        projected = self._total_bytes + size
        if projected > self._max_total_bytes:
            raise OutputSizeExceeded(
                f"Adding artifact {name!r} ({size} bytes) would exceed the "
                f"output cap of {self._max_total_bytes} bytes "
                f"(already staged {self._total_bytes} bytes)."
            )
        artifact = Artifact(name=name, path=resolved, size_bytes=size)
        self._artifacts.append(artifact)
        self._total_bytes = projected
        return artifact

    @property
    def artifacts(self) -> tuple[Artifact, ...]:
        return tuple(self._artifacts)

    @property
    def total_bytes(self) -> int:
        return self._total_bytes

    @property
    def max_total_bytes(self) -> int:
        return self._max_total_bytes


class OutputSizeExceeded(RuntimeError):
    """Raised when staged artifacts exceed the job's output-size cap."""


class ProgressReporter(Protocol):
    """Reports coarse progress back to the job. The default impl logs."""

    def report(self, pct: float, phase: str) -> None: ...


class _LoggingProgressReporter:
    def __init__(self, log: "Logger") -> None:
        self._log = log

    def report(self, pct: float, phase: str) -> None:
        clamped = max(0.0, min(100.0, float(pct)))
        self._log.info(f"progress {clamped:5.1f}% — {phase}")


class Logger(Protocol):
    """Structured-ish logger surface for tools. Lines are captured as job logs."""

    def info(self, message: str) -> None: ...

    def warn(self, message: str) -> None: ...


class _StdLogger:
    """Tiny stderr logger so tool output is interleaved into job logs.

    Intentionally dependency-free (no ``logging`` config) so a user tool's own
    logging setup cannot suppress harness/job lines.
    """

    def __init__(self, stream: Any = None) -> None:
        import sys

        self._stream = stream if stream is not None else sys.stderr

    def info(self, message: str) -> None:
        print(f"[tool] INFO  {message}", file=self._stream, flush=True)

    def warn(self, message: str) -> None:
        print(f"[tool] WARN  {message}", file=self._stream, flush=True)


class CancellationToken:
    """Cooperative cancellation a long-running tool can poll.

    The harness flips this when the job is cancelled (signal handler). Tools
    should call :meth:`raise_if_cancelled` at loop boundaries.
    """

    def __init__(self) -> None:
        self._cancelled = False

    def cancel(self) -> None:
        self._cancelled = True

    @property
    def is_cancelled(self) -> bool:
        return self._cancelled

    def raise_if_cancelled(self) -> None:
        if self._cancelled:
            raise JobCancelled("Job was cancelled.")


class JobCancelled(RuntimeError):
    """Raised by :meth:`CancellationToken.raise_if_cancelled`."""


@dataclass(frozen=True)
class GpContext:
    """The object passed to a tool's ``execute(context)``.

    Attributes:
        params: Parsed ``params_json`` dictionary (tool-defined schema).
        inputs: Resolved input artifacts (name -> local path). For Phase 1 this
            is whatever the server pre-staged; tools that need server data pull
            it via :attr:`client`.
        client: A pre-authenticated, *scoped* :class:`HonuaClient`. Its bearer
            token is job-bound and least-privilege; the raw token is never
            exposed to tool code.
        output: Sink to register artifacts for upload to ``output_prefix``.
        progress: Coarse progress reporter.
        log: Job logger.
        cancellation: Cooperative cancellation token.
        workdir: A writable scratch directory the tool owns for the job.
    """

    params: Mapping[str, Any]
    inputs: Mapping[str, Path]
    client: "HonuaClient"
    output: OutputSink
    progress: ProgressReporter
    log: Logger
    cancellation: CancellationToken
    workdir: Path = field(default_factory=lambda: Path("/work/out"))
