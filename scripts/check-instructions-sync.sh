#!/usr/bin/env bash
set -euo pipefail

check_pair() {
  local left="$1"
  local right="$2"

  if [[ ! -f "$left" ]]; then
    echo "::error::Missing file: $left"
    exit 1
  fi

  if [[ ! -f "$right" ]]; then
    echo "::error::Missing file: $right"
    exit 1
  fi

  if ! diff -q "$left" "$right" >/dev/null; then
    echo "::error::Instruction files out of sync: $left vs $right"
    diff -u "$left" "$right" || true
    exit 1
  fi
}

# Only sync root instruction documents that provide project context.
check_pair "CLAUDE.md" "CODEX.md"

echo "Instruction files are in sync."
