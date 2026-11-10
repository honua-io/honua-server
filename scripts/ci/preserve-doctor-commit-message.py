#!/usr/bin/env python3
"""Compatibility fix for gh-aw v1.0.1 filtered patches; retain all file exclusions."""

import sys
from pathlib import Path


ORIGINAL = '"-m", "Apply filtered changes"'
REPLACEMENT = '"-m", execGitSync(["show", "-s", "--format=%B", tip], { cwd }).trim()'


def preserve_message(path: Path) -> None:
    """Patch the pinned runtime once, and reject unexpected upstream changes."""
    source = path.read_text()
    if source.count(ORIGINAL) != 1:
        raise RuntimeError("Unexpected gh-aw filtered commit implementation; review compatibility fix")
    path.write_text(source.replace(ORIGINAL, REPLACEMENT))


if __name__ == "__main__":
    for runtime_path in sys.argv[1:]:
        preserve_message(Path(runtime_path))
