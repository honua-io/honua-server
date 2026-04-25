#!/usr/bin/env bash
set -euo pipefail

if [[ ! -f "AGENTS.md" ]]; then
  echo "::error::Missing canonical instruction file: AGENTS.md"
  exit 1
fi

for stale_file in CLAUDE.md CODEX.md; do
  if [[ -f "$stale_file" ]]; then
    echo "::error::Stale duplicate instruction file found: $stale_file. Use AGENTS.md as the canonical source."
    exit 1
  fi
done

echo "Canonical instruction file is present."
