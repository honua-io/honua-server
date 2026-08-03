#!/usr/bin/env python3
"""Resolve the Admin OpenAPI breaking-change acknowledgement for CI.

The repository variable is a temporary pre-publication escape hatch. Once it is
reset for the first published control-plane release, an intentional break must
update an expected migration/deprecation document and be acknowledged on the
pull request that introduces it by checking the exact
``OPENAPI_BREAKING_CHANGE_APPROVED`` marker in the PR template.
"""

from __future__ import annotations

import os
import re
from dataclasses import dataclass
from pathlib import Path
from typing import Iterable


APPROVAL_MARKER = "OPENAPI_BREAKING_CHANGE_APPROVED"
BREAKING_CHANGE_DOCUMENTATION_PATHS = frozenset(
    {
        "docs/internal/contributor/RELEASE_CHECKLIST.md",
        "docs/reference/control-plane-migration-guide.md",
        "docs/reference/versioning-and-support.md",
    }
)
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
LIST_CONTAINER_PATTERN = re.compile(
    r"^[ ]{0,3}(?:[-+*]|\d{1,9}[.)])[ \t]+"
)
RAW_HTML_CONTAINER_START_PATTERN = re.compile(
    r"^[ ]{0,3}<(?P<tag>pre|script|style|textarea)(?:[ \t]|>|$)",
    re.IGNORECASE,
)
RAW_HTML_DELIMITED_BLOCK_PATTERNS = (
    (re.compile(r"^[ ]{0,3}<\?"), re.compile(r"\?>")),
    (re.compile(r"^[ ]{0,3}<![A-Z]"), re.compile(r">")),
    (re.compile(r"^[ ]{0,3}<!\[CDATA\["), re.compile(r"\]\]>")),
)
HTML_BLOCK_TAG_PATTERN = re.compile(
    r"^[ ]{0,3}</?(?:address|article|aside|base|basefont|blockquote|body|caption|"
    r"center|col|colgroup|dd|details|dialog|dir|div|dl|dt|fieldset|figcaption|"
    r"figure|footer|form|frame|frameset|h[1-6]|head|header|hr|html|iframe|legend|"
    r"li|link|main|menu|menuitem|nav|noframes|ol|optgroup|option|p|param|search|"
    r"section|summary|table|tbody|td|tfoot|th|thead|title|tr|track|ul)"
    r"(?:[ \t]|/?>|$)",
    re.IGNORECASE,
)
HTML_COMPLETE_TAG_PATTERN = re.compile(
    r"^[ ]{0,3}(?:"
    r"<[A-Za-z][A-Za-z0-9-]*"
    r"(?:[ \t]+[A-Za-z_:][A-Za-z0-9_.:-]*"
    r"(?:[ \t]*=[ \t]*(?:[^ \t\"'=<>`]+|'[^']*'|\"[^\"]*\"))?)*"
    r"[ \t]*/?>|</[A-Za-z][A-Za-z0-9-]*[ \t]*>)"
    r"[ \t]*$"
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


def breaking_change_documentation_updated(changed_files: Iterable[str]) -> bool:
    """Return whether the PR updates an expected migration/deprecation document."""

    normalized_paths = {
        path.strip().replace("\\", "/")
        for path in changed_files
        if path.strip()
    }
    return not BREAKING_CHANGE_DOCUMENTATION_PATHS.isdisjoint(normalized_paths)


def _remove_non_rendered_markdown(markdown: str) -> str:
    """Remove raw HTML, HTML comments, and fences before marker matching."""

    uncommented = HTML_COMMENT_PATTERN.sub("", markdown)
    visible_lines: list[str] = []
    fence_character: str | None = None
    fence_length = 0
    raw_html_end_pattern: re.Pattern[str] | None = None
    raw_html_until_blank = False

    for line in uncommented.splitlines():
        if raw_html_end_pattern is not None:
            if raw_html_end_pattern.search(line) is not None:
                raw_html_end_pattern = None
            continue

        if raw_html_until_blank:
            if not line.strip():
                raw_html_until_blank = False
            continue

        if fence_character is not None:
            closing_fence = re.fullmatch(
                rf"[ ]{{0,3}}{re.escape(fence_character)}{{{fence_length},}}[ \t]*",
                line,
            )
            if closing_fence is not None:
                fence_character = None
                fence_length = 0
            continue

        block_line = line
        while (list_container := LIST_CONTAINER_PATTERN.match(block_line)) is not None:
            block_line = block_line[list_container.end():]

        opening_fence = FENCE_START_PATTERN.fullmatch(block_line)
        if opening_fence is not None:
            fence = opening_fence.group("fence")
            info = opening_fence.group("info")
            if not (fence[0] == "`" and "`" in info):
                fence_character = fence[0]
                fence_length = len(fence)
                continue

        raw_html_container = RAW_HTML_CONTAINER_START_PATTERN.match(block_line)
        if raw_html_container is not None:
            tag = raw_html_container.group("tag")
            closing_tag = re.compile(rf"</{re.escape(tag)}[ \t]*>", re.IGNORECASE)
            if closing_tag.search(block_line, raw_html_container.end()) is None:
                raw_html_end_pattern = closing_tag
            continue

        delimited_html_block = False
        for opening_pattern, closing_pattern in RAW_HTML_DELIMITED_BLOCK_PATTERNS:
            opening_match = opening_pattern.match(block_line)
            if opening_match is None:
                continue

            if closing_pattern.search(block_line, opening_match.end()) is None:
                raw_html_end_pattern = closing_pattern
            delimited_html_block = True
            break
        if delimited_html_block:
            continue

        if (
            HTML_BLOCK_TAG_PATTERN.match(block_line) is not None
            or HTML_COMPLETE_TAG_PATTERN.fullmatch(block_line) is not None
        ):
            raw_html_until_blank = True
            continue

        visible_lines.append(line)

    return "\n".join(visible_lines)


def resolve_policy(
    repository_override: str,
    pr_body: str,
    changed_files: Iterable[str] = (),
) -> PolicyDecision:
    """Resolve the temporary repository override and steady-state PR marker."""

    if parse_repository_override(repository_override):
        return PolicyDecision(
            allow_breaking_changes=True,
            source="repository variable OPENAPI_ALLOW_BREAKING_CHANGES",
        )

    if (
        pr_body_acknowledges_breaking_change(pr_body)
        and breaking_change_documentation_updated(changed_files)
    ):
        return PolicyDecision(
            allow_breaking_changes=True,
            source=f"pull-request marker {APPROVAL_MARKER}",
        )

    return PolicyDecision(allow_breaking_changes=False, source="none")


def read_changed_files(path_value: str) -> tuple[str, ...]:
    """Read paths whose blob content changed from a workflow-produced raw diff."""

    normalized_path = path_value.strip()
    if not normalized_path:
        return ()

    content_changed_paths: list[str] = []
    for line in Path(normalized_path).read_text(encoding="utf-8").splitlines():
        try:
            metadata, changed_path = line.split("\t", maxsplit=1)
        except ValueError as error:
            raise ValueError(f"Malformed git raw-diff line: {line!r}.") from error

        fields = metadata.split()
        if len(fields) != 5 or not fields[0].startswith(":"):
            raise ValueError(f"Malformed git raw-diff metadata: {metadata!r}.")

        old_blob, new_blob = fields[2], fields[3]
        if old_blob != new_blob:
            content_changed_paths.append(changed_path)

    return tuple(content_changed_paths)


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
        read_changed_files(os.environ.get("OPENAPI_CHANGED_FILES_FILE", "")),
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
