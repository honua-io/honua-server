#!/usr/bin/env python3
"""Offline tests for the additions-only documentation example surface gate."""

from __future__ import annotations

import json
import subprocess
import tempfile
from pathlib import Path

SCRIPT = Path(__file__).with_name("check-doc-example-surfaces.py")


def run(*args: str, cwd: Path) -> subprocess.CompletedProcess[str]:
    return subprocess.run(args, cwd=cwd, text=True, capture_output=True, check=False)


def commit(repo: Path, message: str) -> None:
    subprocess.run(["git", "add", "."], cwd=repo, check=True)
    subprocess.run(["git", "-c", "user.name=Test", "-c", "user.email=test@example.com", "commit", "--allow-empty", "-m", message], cwd=repo, check=True, capture_output=True)


def case(markdown: str, entries: list[dict[str, str]] | None = None) -> subprocess.CompletedProcess[str]:
    root = Path(tempfile.mkdtemp())
    subprocess.run(["git", "init", "-q", "-b", "trunk"], cwd=root, check=True)
    (root / "docs").mkdir()
    (root / "scripts/ci").mkdir(parents=True)
    (root / "docs/guides").mkdir()
    (root / "docs/guides/page.md").write_text("# Page\n", encoding="utf-8")
    allowlist = root / "scripts/ci/doc-wire-reference-allowlist.v1.json"
    allowlist.write_text(json.dumps({"schemaVersion": 1, "entries": entries or []}), encoding="utf-8")
    commit(root, "base")
    (root / "docs/guides/page.md").write_text(markdown, encoding="utf-8")
    commit(root, "change")
    return run("python3", str(SCRIPT), "--repo-root", str(root), "--base", "HEAD^", cwd=root)


def main() -> int:
    rejected = case("# Page\n\n```bash\ncurl https://example.test\n```\n")
    assert rejected.returncode == 1 and "raw curl added" in rejected.stderr
    allowed = [{"path": "docs/guides/page.md", "reason": "This page documents the HTTP wire protocol."}]
    accepted = case("# Page\n\n<!-- wire-reference -->\n```bash\ncurl https://example.test\n```\n", allowed)
    assert accepted.returncode == 0, accepted.stderr
    unmarked = case("# Page\n\n```bash\ncurl https://example.test\n```\n", allowed)
    assert unmarked.returncode == 1 and "lacks <!-- wire-reference -->" in unmarked.stderr
    grpcurl = case("# Page\n\n```bash\ngrpcurl server:8081 list\n```\n")
    assert grpcurl.returncode == 0, grpcurl.stderr
    malformed = case("# Page\n", [{"path": "docs/guides/page.md", "reason": ""}])
    assert malformed.returncode == 1 and "invalid wire-reference" in malformed.stderr
    stale = case("# Page\n\nNo raw HTTP remains.\n", allowed)
    assert stale.returncode == 1 and "stale wire-reference" in stale.stderr
    print("documentation example surface gate tests passed")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
