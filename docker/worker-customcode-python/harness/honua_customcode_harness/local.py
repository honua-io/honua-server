"""Local filesystem adapter for the production execute(context) contract.

This executes trusted developer code. Receipts and logs are local development
evidence, not sanitized support bundles. No network client or cloud uploader is
constructed, and dependencies must already be installed in the selected Python.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import math
import shutil
import signal
import sys
import threading
import uuid
from collections.abc import Mapping
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

from .context import CancellationToken, GpContext, JobCancelled, OutputSink, OutputSizeExceeded
from .harness import _coerce_result
from .loader import load_entrypoint


class _Journal:
    def __init__(self, path: Path) -> None:
        self.path = path
        self.count = 0
        self.truncated = False
        self._lock = threading.Lock()

    def event(self, phase: str, *, terminal: bool = False, **values: Any) -> None:
        with self._lock:
            if self.count >= 1000 and not terminal:
                self.truncated = True
                return
            event = {"sequence": self.count, "timestamp": datetime.now(timezone.utc).isoformat(),
                     "phase": str(phase)[:500], **values}
            with self.path.open("a", encoding="utf-8") as stream:
                stream.write(json.dumps(event, allow_nan=False) + "\n")
            self.count += 1

    def report(self, pct: float, phase: str) -> None:
        pct = float(pct)
        if not math.isfinite(pct):
            raise ValueError("Progress must be finite.")
        self.event(phase, percentComplete=max(0.0, min(100.0, pct)))

    def info(self, message: str) -> None:
        self.event("log", level="info", message=str(message)[:2000])

    def warn(self, message: str) -> None:
        self.event("log", level="warning", message=str(message)[:2000])


def run_local(*, source: Path, entrypoint: str, params: Mapping[str, Any],
              inputs: Mapping[str, Path], output: Path,
              max_output_bytes: int = 100 * 1024 * 1024) -> int:
    """Run trusted local code once; write receipt.json even on tool/setup failure.

    Input paths are explicitly supplied by the developer. Published artifacts
    must be regular files beneath the fresh work directory. Each invocation has
    a unique run ID; previous results are never overwritten.
    """
    run_id = uuid.uuid4().hex
    run_dir = output.resolve() / run_id
    run_dir.mkdir(parents=True, exist_ok=False)
    workdir = run_dir / "work"
    workdir.mkdir()
    journal = _Journal(run_dir / "events.jsonl")
    result: dict[str, Any] = {
        "schemaVersion": "1.0", "runId": run_id, "entrypoint": entrypoint,
        "executionMode": "local", "contentClassification": "local-development-unsanitized",
        "artifacts": [], "events": "events.jsonl",
    }
    phase = "setup"
    cancellation = CancellationToken()
    previous_handlers = {}
    if threading.current_thread() is threading.main_thread():
        for signum in (signal.SIGTERM, signal.SIGINT):
            previous_handlers[signum] = signal.getsignal(signum)
            signal.signal(signum, lambda _signum, _frame: cancellation.cancel())
    journal.event(phase)
    try:
        source = source.resolve(strict=True)
        if not source.is_dir():
            raise ValueError("Source must be a directory.")
        resolved_inputs = {name: path.resolve(strict=True) for name, path in inputs.items()}
        if any(not path.is_file() for path in resolved_inputs.values()):
            raise ValueError("Inputs must be files.")
        sink = OutputSink(max_total_bytes=max_output_bytes)
        context = GpContext(params=dict(params), inputs=resolved_inputs, client=None,
                            output=sink, progress=journal, log=journal,
                            cancellation=cancellation, workdir=workdir)
        func = load_entrypoint(source, entrypoint)
        phase = "execute"
        journal.event(phase)
        cancellation.raise_if_cancelled()
        outcome = _coerce_result(func(context), journal)
        cancellation.raise_if_cancelled()
        if not outcome.ok:
            raise RuntimeError(outcome.message or "Tool reported failure.")
        phase = "artifacts"
        journal.event(phase)
        # Recheck size and paths after execution: registration is only intent.
        staged = []
        total = 0
        for artifact in sink.artifacts:
            path = artifact.path.resolve(strict=True)
            if not path.is_relative_to(workdir) or not path.is_file():
                raise ValueError("Output artifacts must be files inside the run work directory.")
            total += path.stat().st_size
            if total > max_output_bytes:
                raise OutputSizeExceeded("Artifacts exceed the configured output cap.")
            staged.append((artifact.name, path))
        artifacts_dir = run_dir / "artifacts"
        artifacts_dir.mkdir()
        artifacts = []
        for name, path in staged:
            destination = artifacts_dir / name
            shutil.copyfile(path, destination)
            with destination.open("rb") as stream:
                digest = hashlib.file_digest(stream, "sha256").hexdigest()
            artifacts.append({"name": name, "path": f"artifacts/{name}",
                              "sizeBytes": destination.stat().st_size, "sha256": digest})
        result.update(status="succeeded", artifacts=artifacts)
        code = 0
    except (JobCancelled, KeyboardInterrupt):
        result.update(status="cancelled", failedPhase=phase)
        code = 3
    except Exception as exc:
        result.update(status="setupFailed" if phase == "setup" else "failed",
                      failedPhase=phase, error={"type": type(exc).__name__, "message": str(exc)[:2000]})
        code = 2 if phase == "setup" else 1
    finally:
        for signum, handler in previous_handlers.items():
            signal.signal(signum, handler)
    journal.event(result["status"], terminal=True, exitCode=code)
    result["eventsTruncated"] = journal.truncated
    result["exitCode"] = code
    (run_dir / "receipt.json").write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"runId": run_id, "status": result["status"], "receipt": str(run_dir / "receipt.json"),
                      "failedPhase": result.get("failedPhase"), "error": result.get("error")}))
    return code


def main(argv: list[str] | None = None) -> int:
    """Run the filesystem adapter from an installed tool or Python debugger."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", type=Path, default=Path.cwd())
    parser.add_argument("--entrypoint", required=True, help="module:function")
    parser.add_argument("--params", type=Path, help="JSON object file")
    parser.add_argument("--input", action="append", default=[], metavar="NAME=PATH")
    parser.add_argument("--output", type=Path, default=Path(".honua/runs"))
    parser.add_argument("--max-output-bytes", type=int, default=100 * 1024 * 1024)
    args = parser.parse_args(argv)
    try:
        params = json.loads(args.params.read_text(encoding="utf-8")) if args.params else {}
        if not isinstance(params, dict):
            raise ValueError("Parameters must be a JSON object.")
        inputs = {}
        for value in args.input:
            name, separator, path = value.partition("=")
            if not separator or not name or not path or name in inputs:
                raise ValueError("Inputs must have unique non-empty NAME=PATH values.")
            inputs[name] = Path(path)
        if args.max_output_bytes <= 0:
            raise ValueError("Output cap must be positive.")
    except (ValueError, OSError) as exc:
        parser.error(str(exc))
    return run_local(source=args.source, entrypoint=args.entrypoint, params=params,
                     inputs=inputs, output=args.output, max_output_bytes=args.max_output_bytes)


if __name__ == "__main__":
    sys.exit(main())
