#!/usr/bin/env python3
"""Run the release memory soak on the shared Native AOT container fixture."""

from __future__ import annotations

import os
import subprocess
import sys
from pathlib import Path

from aot_container_fixture import (
    ContainerOptions,
    container_was_oom_killed,
    start_container_stack,
    stop_container_stack,
)
from emit_rc2_rigor_proof import main as emit_proof


MEMORY_LIMIT_BYTES = 1024**3
SCORECARD = "aot-soak-scorecard.json"
SOAK_SCRIPT = Path(__file__).with_name("run-aot-soak.py")


def main() -> int:
    image = f"ghcr.io/honua-io/honua-server@{os.environ['AMD64_DIGEST']}"
    options = ContainerOptions(
        environment=(
            "RateLimiting__Enabled=false",
            "Limits__Connections__AnonymousPressureAdmissionEnabled=false",
            f"HONUA_ADMIN_PASSWORD=SoakAdmin-{os.environ['GITHUB_RUN_ID']}-Aa1!",
        ),
        memory_limit_bytes=MEMORY_LIMIT_BYTES,
    )
    session = start_container_stack(image, os.environ["POSTGIS_IMAGE"], 90, options=options)
    try:
        subprocess.run([
            sys.executable, str(SOAK_SCRIPT),
            "--base-url", session.base_url,
            "--container-name", session.app_container,
            "--duration", os.environ["SOAK_DURATION"],
            "--concurrency", "8",
            "--max-rss-drift-pct", "20.0",
            "--max-error-rate", "0.0",
            "--scorecard", SCORECARD,
        ], check=True)
        oom_count = int(container_was_oom_killed(session.app_container))
        return emit_proof([
            "aot-soak",
            "--server-sha", os.environ["SERVER_SHA"],
            "--image-digest", os.environ["SERVER_DIGEST"],
            "--lock-digest", os.environ["LOCK_DIGEST"],
            "--soak-scorecard", SCORECARD,
            "--memory-limit-bytes", str(MEMORY_LIMIT_BYTES),
            "--swap-disabled",
            "--oom-kill-count", str(oom_count),
            "--out", "proofs/aot-memory-soak-proof.json",
        ])
    finally:
        stop_container_stack(session)


if __name__ == "__main__":
    raise SystemExit(main())
