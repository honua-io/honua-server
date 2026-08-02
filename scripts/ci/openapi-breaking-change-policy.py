#!/usr/bin/env python3
"""Resolve the Admin OpenAPI breaking-change acknowledgement for CI.

The repository variable is a temporary pre-publication escape hatch. Once it is
reset for the first published control-plane release, an intentional break must
be acknowledged on the pull request that introduces it by checking the exact
``OPENAPI_BREAKING_CHANGE_APPROVED`` marker in the PR template.
"""

from __future__ import annotations

import os
import re
from dataclasses import dataclass
from pathlib import Path


APPROVAL_MARKER = "OPENAPI_BREAKING_CHANGE_APPROVED"
TRUE_VALUES = frozenset({"1", "true", "yes", "on"})
FALSE_VALUES = frozenset({"", "0", "false", "no", "off"})
APPROVAL_PATTERN = re.compile(
    rf"^\s*-\s*\[[xX]\]\s*`{re.escape(APPROVAL_MARKER)}`(?:\s*(?:-|—).*)?$",
    re.MULTILINE,
)


@dataclass(frozen=True)
class PolicyDecision:
    """The resolved breaking-change policy passed to contract validation."""

    allow_breaking_changes: bool
    source: str


def parse_repository_override(raw_value: str) -> bool:
    """Parse the repository-variable value without silently accepting typos."""

    normalized = raw_value.strip().lower()
    if normalized in TRUE_VALUES:
        return True
    if normalized in FALSE_VALUES:
        return False
    raise ValueError(
        "OPENAPI_ALLOW_BREAKING_CHANGES must be a boolean value "
        f"(received {raw_value!r})."
    )


def pr_body_acknowledges_breaking_change(pr_body: str) -> bool:
    """Return whether the PR body contains the exact checked approval marker."""

    return APPROVAL_PATTERN.search(pr_body) is not None


def resolve_policy(repository_override: str, pr_body: str) -> PolicyDecision:
    """Resolve the temporary repository override and steady-state PR marker."""

    if parse_repository_override(repository_override):
        return PolicyDecision(
            allow_breaking_changes=True,
            source="repository variable OPENAPI_ALLOW_BREAKING_CHANGES",
        )

    if pr_body_acknowledges_breaking_change(pr_body):
        return PolicyDecision(
            allow_breaking_changes=True,
            source=f"pull-request marker {APPROVAL_MARKER}",
        )

    return PolicyDecision(allow_breaking_changes=False, source="none")


def append_github_outputs(output_path: Path, decision: PolicyDecision) -> None:
    """Append the fixed policy outputs consumed by the workflow's validator step."""

    with output_path.open("a", encoding="utf-8") as output:
        output.write(
            f"allow={'true' if decision.allow_breaking_changes else 'false'}\n"
        )
        output.write(f"source={decision.source}\n")


def main() -> int:
    """Resolve policy from the workflow environment and publish step outputs."""

    decision = resolve_policy(
        os.environ.get("OPENAPI_REPO_ALLOW_BREAKING_CHANGES", "false"),
        os.environ.get("OPENAPI_PR_BODY", ""),
    )

    output_path = os.environ.get("GITHUB_OUTPUT", "").strip()
    if output_path:
        append_github_outputs(Path(output_path), decision)
    else:
        print(f"allow={'true' if decision.allow_breaking_changes else 'false'}")
        print(f"source={decision.source}")

    print(f"OpenAPI breaking-change acknowledgement source: {decision.source}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
