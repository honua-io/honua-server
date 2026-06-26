"""Honua custom-code GP harness (Python runtime).

Public, SDK-facing surface = the ``execute(context)`` contract types. Tool
authors import only these. The orchestration entrypoint is :func:`run` / the
``honua-customcode-harness`` console script.
"""

from __future__ import annotations

from .context import (
    Artifact,
    CancellationToken,
    GpContext,
    GpResult,
    GpStatus,
    JobCancelled,
    Logger,
    OutputSink,
    OutputSizeExceeded,
    ProgressReporter,
)
from .harness import run

__all__ = [
    "Artifact",
    "CancellationToken",
    "GpContext",
    "GpResult",
    "GpStatus",
    "JobCancelled",
    "Logger",
    "OutputSink",
    "OutputSizeExceeded",
    "ProgressReporter",
    "run",
]

__version__ = "0.1.0"
