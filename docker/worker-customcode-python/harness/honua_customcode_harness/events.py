"""Versioned structured harness events (``honua.gp.events/v1``).

Progress, log, artifact, failure, and terminal records share one JSON Lines
contract across the production harnesses, the local runners, and the devkit so
that local and remote evidence have the same shape. The journal is bounded: once
the event or byte cap is reached, further non-terminal records are counted and a
single ``truncated`` record discloses the count before the ``terminal`` record,
which is always written. Readers treat lines that are not v1 records as text.

The human-readable stderr lines are unchanged; the journal is an additional,
machine-readable channel, not a replacement.
"""
from __future__ import annotations

import json
import math
import threading
import time
from collections.abc import Iterator
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

SCHEMA = "honua.gp.events/v1"
SCHEMA_VERSION = 1
EVENTS_FILE_NAME = "honua-events.v1.jsonl"
EVENTS_PATH_ENV = "CUSTOMCODE_EVENTS_PATH"
ATTEMPT_ENVS = ("HONUA_JOB_ATTEMPT", "HONUA_ATTEMPT_ID", "AWS_BATCH_JOB_ATTEMPT")
CORRELATION_ENV = "HONUA_CORRELATION_ID"
TRACEPARENT_ENVS = ("HONUA_TRACEPARENT", "TRACEPARENT")
DEFAULT_MAX_EVENTS = 1000
DEFAULT_MAX_BYTES = 256 * 1024
MESSAGE_LIMIT = 2000
PHASE_LIMIT = 500
KINDS = frozenset({"phase", "progress", "log", "artifact", "failure", "truncated", "terminal"})


def attempt_from_env(env: Any) -> str:
    """The attempt identity recorded on every event (``1`` when nothing declares it)."""
    for name in ATTEMPT_ENVS:
        value = env.get(name) if env is not None else None
        if value:
            return str(value)[:64]
    return "1"


def correlation_from_env(env: Any) -> str | None:
    """The admitted correlation id, or ``None`` when the environment has none that is safe to record."""
    value = env.get(CORRELATION_ENV) if env is not None else None
    return _correlation(value)


def trace_from_env(env: Any) -> str | None:
    """The 32-hex trace id from ``HONUA_TRACEPARENT`` or ``TRACEPARENT``."""
    if env is None:
        return None
    for name in TRACEPARENT_ENVS:
        trace = _trace_id(env.get(name))
        if trace:
            return trace
    return None


def _correlation(value: Any) -> str | None:
    if value is None:
        return None
    text = str(value).strip()
    if not text or len(text) > 128:
        return None
    for character in text:
        if character not in "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789._:-":
            return None
    return text


def _hex(value: str) -> bool:
    return bool(value) and all(character in "0123456789abcdef" for character in value)


def _bare_trace(value: Any) -> str | None:
    if value is None:
        return None
    text = str(value).strip().lower()
    if len(text) != 32 or not _hex(text) or text == "0" * 32:
        return None
    return text


def _trace_id(value: Any) -> str | None:
    """Accept a W3C traceparent and return its trace id. All-zero ids are rejected."""
    if value is None:
        return None
    parts = str(value).strip().lower().split("-")
    if len(parts) != 4 or parts[0] != "00" or len(parts[1]) != 32 or len(parts[2]) != 16 or len(parts[3]) != 2:
        return None
    if not all(_hex(part) for part in parts) or parts[1] == "0" * 32 or parts[2] == "0" * 16:
        return None
    return parts[1]


def _limit(value: Any, length: int) -> str:
    text = str(value)
    return text if len(text) <= length else text[:length]


class EventJournal:
    """Append-only, bounded ``honua.gp.events/v1`` journal.

    Also satisfies the ``ProgressReporter`` and ``Logger`` protocols so a tool's
    ``context.progress`` / ``context.log`` calls land in the journal. When a
    ``mirror`` logger is given, the same human-readable lines are still written
    to it (the production harness keeps its stderr text).
    """

    def __init__(self, path: str | Path, *, attempt: str = "1", correlation: str | None = None,
                 trace: str | None = None, max_events: int = DEFAULT_MAX_EVENTS,
                 max_bytes: int = DEFAULT_MAX_BYTES, mirror: Any = None) -> None:
        if max_events <= 0 or max_bytes <= 0:
            raise ValueError("Event caps must be positive.")
        self.path = Path(path)
        self.attempt = _limit(attempt, 64)
        self.correlation = _correlation(correlation)
        self.trace = _bare_trace(trace)
        self.max_events = max_events
        self.max_bytes = max_bytes
        self.count = 0
        self.bytes = 0
        self.dropped = 0
        self._mirror = mirror
        self._lock = threading.Lock()
        self._start = time.monotonic()
        self.path.parent.mkdir(parents=True, exist_ok=True)
        self.path.touch()

    @property
    def truncated(self) -> bool:
        return self.dropped > 0

    def emit(self, kind: str, *, terminal: bool = False, **fields: Any) -> bool:
        """Append one record; returns False when the record was dropped by a cap."""
        if kind not in KINDS:
            raise ValueError(f"Unknown event kind {kind!r}.")
        with self._lock:
            record: dict[str, Any] = {
                "v": SCHEMA_VERSION, "seq": self.count,
                "at": datetime.now(timezone.utc).isoformat(timespec="milliseconds").replace("+00:00", "Z"),
                "elapsedMs": int((time.monotonic() - self._start) * 1000),
                "attempt": self.attempt,
            }
            if self.correlation:
                record["correlation"] = self.correlation
            if self.trace:
                record["trace"] = self.trace
            record["kind"] = kind
            for key, value in fields.items():
                if value is not None:
                    record[key] = value
            encoded = (json.dumps(record, allow_nan=False, separators=(",", ":")) + "\n").encode("utf-8")
            if not terminal and (self.count >= self.max_events or self.bytes + len(encoded) > self.max_bytes):
                self.dropped += 1
                return False
            with self.path.open("ab") as stream:
                stream.write(encoded)
            self.count += 1
            self.bytes += len(encoded)
            return True

    # --- ProgressReporter / Logger surface -------------------------------
    def report(self, pct: float, phase: str) -> None:
        pct = float(pct)
        if not math.isfinite(pct):
            raise ValueError("Progress must be finite.")
        clamped = max(0.0, min(100.0, pct))
        self.emit("progress", percent=clamped, phase=_limit(phase, PHASE_LIMIT))
        if self._mirror is not None:
            self._mirror.info(f"progress {clamped:5.1f}% — {phase}")

    def info(self, message: str) -> None:
        self.emit("log", level="info", message=_limit(message, MESSAGE_LIMIT))
        if self._mirror is not None:
            self._mirror.info(message)

    def warn(self, message: str) -> None:
        self.emit("log", level="warning", message=_limit(message, MESSAGE_LIMIT))
        if self._mirror is not None:
            self._mirror.warn(message)

    # --- harness-side records ---------------------------------------------
    def phase(self, name: str) -> None:
        self.emit("phase", phase=_limit(name, PHASE_LIMIT))

    def artifact(self, name: str, size_bytes: int, *, sha256: str | None = None, uri: str | None = None) -> None:
        self.emit("artifact", artifact={"name": _limit(name, 255), "sizeBytes": int(size_bytes),
                                        **({"sha256": sha256} if sha256 else {}), **({"uri": uri} if uri else {})})

    def failure(self, error_type: str, message: str, *, phase: str | None = None) -> None:
        self.emit("failure", phase=_limit(phase, PHASE_LIMIT) if phase else None,
                  error={"type": _limit(error_type, 200), "message": _limit(message, MESSAGE_LIMIT)})

    def terminal(self, status: str, exit_code: int, *, phase: str | None = None) -> None:
        """Always written: the truncation disclosure (when needed) and the terminal record."""
        if self.dropped:
            self.emit("truncated", terminal=True, dropped=self.dropped,
                      maxEvents=self.max_events, maxBytes=self.max_bytes)
        self.emit("terminal", terminal=True, status=_limit(status, 64), exitCode=int(exit_code),
                  phase=_limit(phase, PHASE_LIMIT) if phase else None)


def read_events(path: str | Path) -> Iterator[dict[str, Any]]:
    """Yield v1 records; any other line is yielded as ``{"kind": "text", "line": ...}``."""
    with Path(path).open("r", encoding="utf-8", errors="replace") as stream:
        for raw in stream:
            line = raw.rstrip("\r\n")
            if not line.strip():
                continue
            try:
                record = json.loads(line)
            except ValueError:
                yield {"kind": "text", "line": _limit(line, MESSAGE_LIMIT)}
                continue
            if isinstance(record, dict) and record.get("v") == SCHEMA_VERSION and record.get("kind") in KINDS:
                yield record
            else:
                yield {"kind": "text", "line": _limit(line, MESSAGE_LIMIT)}
