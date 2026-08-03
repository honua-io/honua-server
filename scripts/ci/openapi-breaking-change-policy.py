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
    rf"^[ ]{{0,3}}-[ \t]+\[[xX]\][ \t]+`{re.escape(APPROVAL_MARKER)}`"
    r"(?:[ \t]*(?:-|—).*)?$",
    re.MULTILINE,
)
HTML_COMMENT_PATTERN = re.compile(r"<!--.*?(?:-->|$)", re.DOTALL)
FENCE_START_PATTERN = re.compile(
    r"^[ ]{0,3}(?P<fence>`{3,}|~{3,})(?P<info>.*)$"
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
    """Return whether rendered PR Markdown contains the checked approval marker."""

    visible_markdown = _remove_non_rendered_markdown(pr_body)
    return APPROVAL_PATTERN.search(visible_markdown) is not None


def _remove_non_rendered_markdown(markdown: str) -> str:
    """Remove HTML comments and fenced code before policy-marker matching."""

    uncommented = HTML_COMMENT_PATTERN.sub("", markdown)
    visible_lines: list[str] = []
    fence_character: str | None = None
    fence_length = 0

    for line in uncommented.splitlines():
        if fence_character is not None:
            closing_fence = re.fullmatch(
                rf"[ ]{{0,3}}{re.escape(fence_character)}{{{fence_length},}}[ \t]*",
                line,
            )
            if closing_fence is not None:
                fence_character = None
                fence_length = 0
            continue

        opening_fence = FENCE_START_PATTERN.fullmatch(line)
        if opening_fence is not None:
            fence = opening_fence.group("fence")
            info = opening_fence.group("info")
            if not (fence[0] == "`" and "`" in info):
                fence_character = fence[0]
                fence_length = len(fence)
                continue

        visible_lines.append(line)

    return "\n".join(visible_lines)


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
