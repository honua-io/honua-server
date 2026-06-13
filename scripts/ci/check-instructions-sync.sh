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
  if [[ -f "$stale_file" ]]; then
    echo "::error::Stale duplicate instruction file found: $stale_file. Use AGENTS.md as the canonical source (a symlink to AGENTS.md is allowed)."
    exit 1
  fi
done

echo "Canonical instruction file is present."
