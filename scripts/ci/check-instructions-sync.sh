#!/usr/bin/env bash
set -euo pipefail

if [[ ! -f "AGENTS.md" ]]; then
  echo "::error::Missing canonical instruction file: AGENTS.md"
  exit 1
fi

# AGENTS.md is the only agent-instructions file. CLAUDE.md and CODEX.md must be
# absent: no duplicate copy and no symlink bridge (retired 2026-10-26; agents are
# pointed at AGENTS.md by their workspace configuration instead).
for stale_file in CLAUDE.md CODEX.md; do
  if [[ -e "$stale_file" || -L "$stale_file" ]] ||
     [[ -n "$(git ls-files -- "$stale_file" 2>/dev/null)" ]]; then
    echo "::error::Stale instruction file found: $stale_file. AGENTS.md is the only canonical agent-instructions file; delete $stale_file (git rm $stale_file)."
    exit 1
  fi
done

# AGENTS.md is a budgeted digest: <= 120 lines / 8 KB, an unmodified generated
# fleet block (honua-flow agents/fleet-rules.md), and no references to repo paths
# that do not exist. See scripts/ci/check-agents-md.py.
python3 "$(dirname "${BASH_SOURCE[0]}")/check-agents-md.py" --root .

echo "Canonical instruction file is present, within budget, and its fleet block is intact."
