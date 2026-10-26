#!/usr/bin/env python3
"""Budget, fleet-block and path checks for AGENTS.md (called by check-instructions-sync.sh).

AGENTS.md is loaded into every agent session, so it is a budgeted digest. Detail lives in
docs/ and is linked from it. This script fails when any of the following holds:
  - AGENTS.md exceeds 150 lines or 10240 bytes;
  - the generated fleet block (`<!-- fleet:begin vN sha256=... -->` ... `<!-- fleet:end -->`,
    source: honua-flow agents/fleet-rules.md) is missing, or its body does not hash to the
    sha256 in its begin marker (it was hand-edited instead of regenerated);
  - a repository path it references does not exist. Only paths whose first segment exists
    at the repository root are checked, so `src/Honua.Nope` fails while honua-flow paths
    are skipped. The fleet block is exempt.

    check-agents-md.py [--root DIR]
    check-agents-md.py --self-test
"""
from __future__ import annotations

import argparse
import hashlib
import re
import sys
import tempfile
from pathlib import Path

MAX_LINES = 150
MAX_BYTES = 10240
BEGIN_RE = re.compile(r"^<!-- fleet:begin v(\d+) sha256=([0-9a-f]{64})(?: [^>]*)? -->$")
END_LINE = "<!-- fleet:end -->"
INLINE_CODE = re.compile(r"`([^`\n]+)`")
LINK_TARGET = re.compile(r"\]\(([^)\s#]+)(?:#[^)]*)?\)")
SKIP_CHARS = set("<>*{}$|=")


def split_fleet_block(lines: list[str]) -> tuple[list[str], list[str]]:
    """Return (problems, lines outside the fleet block)."""
    begins = [i for i, line in enumerate(lines) if line.startswith("<!-- fleet:begin")]
    if not begins:
        return ["no generated fleet block; render it with honua-flow `agents/gen-agents-md --write .`"], lines
    if len(begins) > 1:
        return ["more than one fleet:begin marker"], lines
    b = begins[0]
    match = BEGIN_RE.match(lines[b])
    ends = [i for i in range(b + 1, len(lines)) if lines[i] == END_LINE]
    if not match or not ends:
        return ["malformed fleet block markers"], lines
    e = ends[0]
    body = "".join(line + "\n" for line in lines[b + 1:e])
    problems = []
    if hashlib.sha256(body.encode("utf-8")).hexdigest() != match.group(2):
        problems.append(
            "the fleet block was edited by hand (its body does not match the sha256 in its marker); "
            "edit honua-flow agents/fleet-rules.md and regenerate"
        )
    return problems, lines[:b] + lines[e + 1:]


def candidate_paths(lines: list[str]) -> set[str]:
    tokens: list[str] = []
    in_fence = False
    for line in lines:
        if line.lstrip().startswith("```"):
            in_fence = not in_fence
            continue
        if in_fence:
            tokens.extend(line.split())
            continue
        for span in INLINE_CODE.findall(line):
            tokens.extend(span.split())
        tokens.extend(LINK_TARGET.findall(line))
    paths = set()
    for token in tokens:
        token = token.strip("\"'(),;:.")
        if "/" not in token or "://" in token or "..." in token or SKIP_CHARS & set(token):
            continue
        paths.add(token)
    return paths


def check(root: Path) -> list[str]:
    agents = root / "AGENTS.md"
    raw = agents.read_bytes()
    text = raw.decode("utf-8")
    lines = text.splitlines()
    problems = []
    if len(lines) > MAX_LINES:
        problems.append(f"AGENTS.md has {len(lines)} lines; the budget is {MAX_LINES}. Move detail to docs/ and link it.")
    if len(raw) > MAX_BYTES:
        problems.append(f"AGENTS.md has {len(raw)} bytes; the budget is {MAX_BYTES}. Move detail to docs/ and link it.")
    block_problems, outside = split_fleet_block(lines)
    problems.extend(block_problems)
    for path in sorted(candidate_paths(outside)):
        first = path.lstrip("./").split("/", 1)[0]
        if not first or not (root / first).exists():
            continue
        if not (root / path).exists():
            problems.append(f"AGENTS.md references `{path}`, which does not exist")
    return problems


def self_test() -> int:
    body = "## Fleet rules\n1. Rule.\n"
    sha = hashlib.sha256(body.encode()).hexdigest()
    block = f"<!-- fleet:begin v1 sha256={sha} source=x -->\n{body}{END_LINE}\n"
    cases = {
        "clean": ("# r\n`src/a.cs` and [doc](docs/d.md#x) and `tools/lane-bin/` (foreign)\n" + block, 0),
        "missing path": ("# r\n`src/missing.cs`\n" + block, 1),
        "fenced command path": ("# r\n```\nscripts/run.sh --flag\nscripts/nope.sh\n```\n" + block, 1),
        "hand edit": ("# r\n" + block.replace("Rule.", "Rule!"), 1),
        "no block": ("# r\n", 1),
        "line budget": ("x\n" * 151 + block, 1),
        "byte budget": ("y" * 10300 + "\n" + block, 1),
        "block paths exempt": ("# r\n" + block.replace("Rule.", "`src/gone.cs`").replace(sha, hashlib.sha256(body.replace("Rule.", "`src/gone.cs`").encode()).hexdigest()), 0),
    }
    failed = 0
    with tempfile.TemporaryDirectory() as tmp:
        root = Path(tmp)
        (root / "src").mkdir()
        (root / "src/a.cs").write_text("")
        (root / "docs").mkdir()
        (root / "docs/d.md").write_text("")
        (root / "scripts").mkdir()
        (root / "scripts/run.sh").write_text("")
        for name, (text, expected) in cases.items():
            (root / "AGENTS.md").write_text(text)
            got = len(check(root))
            if (got > 0) != bool(expected):
                failed += 1
                print(f"self-test FAILED: {name}: {check(root)}", file=sys.stderr)
    print(f"self-test: {len(cases) - failed}/{len(cases)} cases pass")
    return 1 if failed else 0


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--root", type=Path, default=Path("."))
    parser.add_argument("--self-test", action="store_true")
    args = parser.parse_args()
    if args.self_test:
        return self_test()
    problems = check(args.root)
    for problem in problems:
        print(f"::error file=AGENTS.md::{problem}")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
