#!/usr/bin/env bash
set -euo pipefail

if [[ ! -f "AGENTS.md" ]]; then
  echo "::error::Missing canonical instruction file: AGENTS.md"
  exit 1
fi

# A symlink to the canonical file (e.g. CLAUDE.md -> AGENTS.md) is a sanctioned
# bridge so agent tooling that looks for its own filename resolves to AGENTS.md.
# Only a *regular* duplicate file is a stale copy that can drift out of sync.
for stale_file in CLAUDE.md CODEX.md; do
  if [[ -L "$stale_file" ]]; then
    target="$(readlink "$stale_file")"
    if [[ "$target" != "AGENTS.md" ]]; then
      echo "::error::$stale_file is a symlink to '$target'; it must point at the canonical AGENTS.md."
      exit 1
    fi
    continue
  fi
  # Windows checkouts with core.symlinks=false materialize an indexed symlink
  # as a regular file containing the symlink target. Treat that exact Git
  # placeholder as equivalent to the sanctioned symlink so local gates can run
  # on Windows without weakening the duplicate-file guard.
  if [[ -f "$stale_file" ]] &&
     git ls-files -s -- "$stale_file" | grep -q '^120000 ' &&
     [[ "$(cat "$stale_file")" == "AGENTS.md" ]]; then
    continue
  fi
  if [[ -f "$stale_file" ]]; then
    echo "::error::Stale duplicate instruction file found: $stale_file. Use AGENTS.md as the canonical source (a symlink to AGENTS.md is allowed)."
    exit 1
  fi
done

# CLAUDE.md must exist as that bridge: Claude Code loads CLAUDE.md, not AGENTS.md.
if [[ ! -L CLAUDE.md ]] &&
   ! { [[ -f CLAUDE.md ]] && git ls-files -s -- CLAUDE.md | grep -q '^120000 '; }; then
  echo "::error::CLAUDE.md must be a symlink to AGENTS.md (ln -s AGENTS.md CLAUDE.md)."
  exit 1
fi

# AGENTS.md is a budgeted digest: <= 150 lines / 10 KB, an unmodified generated
# fleet block (honua-flow agents/fleet-rules.md), and no references to repo paths
# that do not exist. See scripts/ci/check-agents-md.py.
python3 "$(dirname "${BASH_SOURCE[0]}")/check-agents-md.py" --root .

echo "Canonical instruction file is present, within budget, and its fleet block is intact."
