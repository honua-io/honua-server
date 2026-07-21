#!/usr/bin/env bash
# Fails visibly when docs/cite-status.md — the authoritative OGC CITE
# evidence snapshot — has gone stale beyond a threshold. #2944: the CITE
# Evidence Report workflow now runs on a weekly schedule, but the workflow
# regenerating the raw evidence bundle does not by itself guarantee that a
# human/agent updates the hand-maintained docs/cite-status.md summary. This
# check gives that drift a visible, failing signal instead of letting the
# "authoritative" snapshot silently go quiet.
#
# Usage: scripts/ci/check-cite-status-freshness.sh [path-to-cite-status.md]
#
# Env:
#   CITE_STATUS_FRESHNESS_DAYS — staleness threshold in days (default: 14).
#
# Exit codes:
#   0 — snapshot is fresh (or the "Last reviewed" date could not be parsed;
#       see the warning — the workflow's own suite-pass check is the primary
#       gate, this is a secondary drift assertion).
#   1 — snapshot is older than the threshold.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"

cite_status_path="${1:-${REPO_ROOT}/docs/cite-status.md}"
threshold_days="${CITE_STATUS_FRESHNESS_DAYS:-14}"

if [[ ! -f "${cite_status_path}" ]]; then
  echo "::error::CITE status file not found: ${cite_status_path}" >&2
  exit 1
fi

last_reviewed="$(grep -m1 -oE 'Last reviewed:[[:space:]]*[0-9]{4}-[0-9]{2}-[0-9]{2}' "${cite_status_path}" \
  | grep -oE '[0-9]{4}-[0-9]{2}-[0-9]{2}' || true)"

if [[ -z "${last_reviewed}" ]]; then
  echo "::warning::Could not find a 'Last reviewed: YYYY-MM-DD' line in ${cite_status_path}; skipping freshness check." >&2
  exit 0
fi

last_reviewed_epoch="$(date -u -d "${last_reviewed}" +%s 2>/dev/null || true)"
if [[ -z "${last_reviewed_epoch}" ]]; then
  echo "::warning::Could not parse 'Last reviewed' date '${last_reviewed}'; skipping freshness check." >&2
  exit 0
fi

now_epoch="$(date -u +%s)"
age_days=$(( (now_epoch - last_reviewed_epoch) / 86400 ))

echo "CITE status last reviewed: ${last_reviewed} (${age_days} day(s) ago; threshold ${threshold_days} day(s))"

if (( age_days > threshold_days )); then
  echo "::error::docs/cite-status.md was last reviewed ${age_days} day(s) ago (${last_reviewed}), exceeding the ${threshold_days}-day freshness threshold. Update the snapshot from the latest CITE Evidence Report run (honua-server#2944)." >&2
  exit 1
fi

echo "CITE status freshness OK."
