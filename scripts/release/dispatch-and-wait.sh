#!/usr/bin/env bash

# Dispatch a GitHub Actions workflow (optionally in another repo), wait for the
# run it created, and print a one-line evidence JSON describing the result:
#   { "id": "<signal-id>", "repo": "...", "workflow": "...", "runUrl": "...",
#     "conclusion": "success|failure|...", "headSha": "..." }
#
# Used by the release-bundle orchestrator to drive each integration/SDK suite
# against the release-candidate image and collect its evidence. Cross-repo
# dispatch needs a token with `actions:write` on the target repo (pass via
# GH_TOKEN; the repo-scoped GITHUB_TOKEN cannot trigger other repos).
#
# --dry-run prints a synthetic "skipped" evidence record without calling gh, so
# the orchestrator wiring and this script can be exercised without GitHub.

set -euo pipefail

SIGNAL_ID=""
REPO=""
WORKFLOW=""
REF="trunk"
DRY_RUN="${DISPATCH_DRY_RUN:-false}"
TIMEOUT="${DISPATCH_TIMEOUT_SECONDS:-3600}"
declare -a INPUTS=()

usage() {
  cat >&2 <<'EOF'
Usage: dispatch-and-wait.sh --id <signal-id> --repo <owner/name> --workflow <file.yml> [options]
  --ref <ref>            Ref to run the workflow on (default: trunk)
  --input k=v            workflow_dispatch input (repeatable)
  --timeout <seconds>    Max wait for the run (default: 3600)
  --dry-run              Do not call gh; emit a synthetic skipped record
Prints one evidence JSON object to stdout. Requires gh + GH_TOKEN unless --dry-run.
EOF
  exit 2
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --id) SIGNAL_ID="$2"; shift 2 ;;
    --repo) REPO="$2"; shift 2 ;;
    --workflow) WORKFLOW="$2"; shift 2 ;;
    --ref) REF="$2"; shift 2 ;;
    --input) INPUTS+=("$2"); shift 2 ;;
    --timeout) TIMEOUT="$2"; shift 2 ;;
    --dry-run) DRY_RUN="true"; shift ;;
    -h|--help) usage ;;
    *) echo "[ERROR] unknown arg: $1" >&2; usage ;;
  esac
done

command -v jq >/dev/null 2>&1 || { echo "[ERROR] jq required" >&2; exit 1; }
[[ -n "$SIGNAL_ID" && -n "$REPO" && -n "$WORKFLOW" ]] || usage

emit() {
  # emit <conclusion> <runUrl> <headSha>
  jq -nc \
    --arg id "$SIGNAL_ID" --arg repo "$REPO" --arg wf "$WORKFLOW" \
    --arg conclusion "$1" --arg runUrl "$2" --arg headSha "$3" \
    '{id:$id, owningRepo:("honua-io/" + ($repo|sub("^.*/";""))), workflow:$wf,
      conclusion:$conclusion, runUrl:$runUrl, headSha:$headSha}
     | with_entries(select(.value != null and .value != ""))'
}

if [[ "$DRY_RUN" == "true" ]]; then
  emit "skipped" "" ""
  exit 0
fi

command -v gh >/dev/null 2>&1 || { echo "[ERROR] gh required (or use --dry-run)" >&2; exit 1; }

dispatch_args=(workflow run "$WORKFLOW" -R "$REPO" --ref "$REF")
for kv in "${INPUTS[@]}"; do
  dispatch_args+=(-f "$kv")
done

# Record a high-water mark so we can find the run this dispatch created.
before="$(gh run list -R "$REPO" --workflow "$WORKFLOW" --limit 1 --json databaseId --jq '.[0].databaseId // 0' 2>/dev/null || echo 0)"
gh "${dispatch_args[@]}" >&2

# Poll for the new run id (dispatch is async; the run appears shortly after).
run_id=""
deadline=$(( $(date +%s) + 120 ))
while [[ -z "$run_id" && $(date +%s) -lt $deadline ]]; do
  sleep 5
  candidate="$(gh run list -R "$REPO" --workflow "$WORKFLOW" --limit 1 --json databaseId --jq '.[0].databaseId // 0' 2>/dev/null || echo 0)"
  if [[ "$candidate" != "$before" && "$candidate" != "0" ]]; then
    run_id="$candidate"
  fi
done

if [[ -z "$run_id" ]]; then
  echo "[WARN] could not locate dispatched run for $REPO/$WORKFLOW" >&2
  emit "missing" "" ""
  exit 0
fi

# Wait for completion (gh run watch exits when the run finishes).
gh run watch "$run_id" -R "$REPO" --exit-status >&2 2>&1 || true

result="$(gh run view "$run_id" -R "$REPO" --json conclusion,url,headSha --jq '{conclusion,url,headSha}' 2>/dev/null || echo '{}')"
emit \
  "$(jq -r '.conclusion // "missing"' <<<"$result")" \
  "$(jq -r '.url // ""' <<<"$result")" \
  "$(jq -r '.headSha // ""' <<<"$result")"
