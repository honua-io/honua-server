#!/usr/bin/env python3
"""Exact nightly producer-run identity for the CNG conformance lane.

honua-release's nightly orchestrator dispatches every protocol producer with a
`nightly_dispatch_id` input and later requires each executed evidence receipt to
name, in its hashed identity, the exact run it dispatched:

    {"repository", "workflow", "run_id", "run_attempt", "dispatch_id"}

(honua-release certification/RECEIPT-V2.md, "Nightly producer run identity").
This module derives that object from the GitHub Actions environment and fails
closed on anything that is not the reviewed `cng-conformance.yml` workflow
running under `workflow_dispatch` in honua-io/honua-server. An empty dispatch id
means the run was not dispatched by the nightly and yields no identity at all.

Run as a script it performs the same admission check, so the lane can refuse a
malformed dispatch before it spends forty minutes building and validating.
"""
from __future__ import annotations

import os
import re
import sys
from collections.abc import Mapping

REPOSITORY = "honua-io/honua-server"
WORKFLOW = "cng-conformance.yml"
DISPATCH_ENV = "HONUA_NIGHTLY_DISPATCH_ID"
PRODUCER_RUN_FIELDS = ("repository", "workflow", "run_id", "run_attempt", "dispatch_id")

_DISPATCH_ID = re.compile(r"nightly-certification-[1-9][0-9]*-[1-9][0-9]*")
_POSITIVE = re.compile(r"[1-9][0-9]*")
_WORKFLOW_REF = re.compile(
    re.escape(f"{REPOSITORY}/.github/workflows/{WORKFLOW}@") + r"refs/(heads|tags)/\S+"
)


def producer_run(dispatch_id: str | None, environ: Mapping[str, str] | None = None) -> dict | None:
    """Return the exact five-field producer run, or None when not nightly-dispatched."""
    env = os.environ if environ is None else environ
    if not dispatch_id:
        return None
    if len(dispatch_id) > 128 or not _DISPATCH_ID.fullmatch(dispatch_id):
        raise ValueError(
            "nightly_dispatch_id must match nightly-certification-<run>-<attempt>")
    if env.get("GITHUB_EVENT_NAME") != "workflow_dispatch":
        raise ValueError("nightly_dispatch_id is only accepted on a workflow_dispatch run")
    if env.get("GITHUB_REPOSITORY") != REPOSITORY:
        raise ValueError(f"nightly_dispatch_id is only accepted in {REPOSITORY}")
    if not _WORKFLOW_REF.fullmatch(env.get("GITHUB_WORKFLOW_REF", "")):
        raise ValueError(
            f"nightly_dispatch_id requires workflow ref {REPOSITORY}/.github/workflows/"
            f"{WORKFLOW}@refs/(heads|tags)/...")
    run_id, attempt = env.get("GITHUB_RUN_ID", ""), env.get("GITHUB_RUN_ATTEMPT", "")
    if not _POSITIVE.fullmatch(run_id) or not _POSITIVE.fullmatch(attempt):
        raise ValueError("nightly_dispatch_id requires a positive GitHub run id and attempt")
    return {
        "repository": REPOSITORY,
        "workflow": WORKFLOW,
        "run_id": int(run_id),
        "run_attempt": int(attempt),
        "dispatch_id": dispatch_id,
    }


def main() -> int:
    try:
        identity = producer_run(os.environ.get(DISPATCH_ENV, ""))
    except ValueError as error:
        print(f"::error::{error}", file=sys.stderr)
        return 1
    print("not nightly-dispatched; no producer_run" if identity is None
          else f"nightly producer_run: {identity}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
