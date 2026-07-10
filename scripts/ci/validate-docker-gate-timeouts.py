#!/usr/bin/env python3
"""Validate bounded timeout headroom for the full Docker integration gate."""

from pathlib import Path
import re


WORKFLOW_PATH = Path(".github/workflows/ci.yml")
MINIMUM_JOB_TIMEOUT_MINUTES = 30
REQUIRED_STEP_TIMEOUTS = {
    "Pre-pull BuildKit image with retries": 3,
    "Build Docker image": 20,
    "Generate SBOM (SPDX JSON) for honua-server:test": 3,
    "Trivy image scan (full CI, HIGH/CRITICAL, fixed only)": 5,
    "Trivy filesystem scan (full CI, HIGH/CRITICAL, fixed only)": 5,
    "Run Docker container": 3,
    "Test Docker container endpoints": 2,
}


def fail(message: str) -> None:
    raise SystemExit(f"Docker integration gate timeout validation failed: {message}")


workflow = WORKFLOW_PATH.read_text(encoding="utf-8")
job_match = re.search(
    r"(?ms)^  docker-build:\n(?P<body>.*?)(?=^  [a-zA-Z0-9_-]+:\n)",
    workflow,
)
if job_match is None:
    fail("docker-build job not found")

job_body = job_match.group("body")
job_timeout_match = re.search(
    r"(?m)^    timeout-minutes:\s*(?P<minutes>\d+)\s*$",
    job_body,
)
if job_timeout_match is None:
    fail("docker-build job has no literal timeout-minutes value")

job_timeout = int(job_timeout_match.group("minutes"))
if job_timeout < MINIMUM_JOB_TIMEOUT_MINUTES:
    fail(
        f"docker-build timeout is {job_timeout} minutes; "
        f"expected at least {MINIMUM_JOB_TIMEOUT_MINUTES}"
    )

for step_name, expected_timeout in REQUIRED_STEP_TIMEOUTS.items():
    step_match = re.search(
        rf"(?ms)^      - name: {re.escape(step_name)}\n"
        r"(?P<body>.*?)(?=^      - (?:name:|uses:)|\Z)",
        job_body,
    )
    if step_match is None:
        fail(f"required step not found: {step_name}")

    step_timeout_match = re.search(
        r"(?m)^        timeout-minutes:\s*(?P<minutes>\d+)\s*$",
        step_match.group("body"),
    )
    if step_timeout_match is None:
        fail(f"required step is unbounded: {step_name}")

    actual_timeout = int(step_timeout_match.group("minutes"))
    if actual_timeout != expected_timeout:
        fail(
            f"{step_name!r} timeout is {actual_timeout} minutes; "
            f"expected {expected_timeout}"
        )

print(
    "Docker integration gate timeout budget is valid: "
    f"job={job_timeout}m, bounded_steps={len(REQUIRED_STEP_TIMEOUTS)}"
)
