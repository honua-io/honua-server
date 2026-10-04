#!/usr/bin/env bash

# Dispatch a workflow and emit evidence bound to the returned run, attempt 1,
# and (for image suites) a target-produced release-suite-receipt artifact.
# Cross-repo dispatch requires GH_TOKEN with actions:write on the target repo.
# --dry-run emits skipped evidence without calling GitHub.

set -euo pipefail

SIGNAL_ID=""
REPO=""
WORKFLOW=""
REF="trunk"
EXPECTED_IMAGE=""
IMAGE_INPUT=""
DRY_RUN="${DISPATCH_DRY_RUN:-false}"
TIMEOUT="${DISPATCH_TIMEOUT_SECONDS:-3600}"
declare -a INPUTS=()
run_id=""
run_attempt=1
head_sha=""
image_digest=""
artifact_id=""

usage() {
  cat >&2 <<'USAGE'
Usage: dispatch-and-wait.sh --id <signal-id> --repo <owner/name> --workflow <file.yml> [options]
  --ref <ref>            Target workflow ref (default: trunk)
  --input k=v            workflow_dispatch input (repeatable)
  --expected-image <ref@sha256:digest>  Immutable candidate image to verify
  --image-input <key>    Input that must contain exactly --expected-image
  --timeout <seconds>    Max wait for completion (default: 3600)
  --dry-run             Emit skipped evidence without calling gh
Image suites require exactly one release-suite-receipt artifact containing
release-suite-receipt.json with id, owningRepo, workflow (file name), runId,
runAttempt, headSha, image, and imageDigest describing the image actually tested.
Prints one evidence JSON object to stdout. Requires gh + GH_TOKEN unless --dry-run.
USAGE
  exit 2
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --id) SIGNAL_ID="$2"; shift 2 ;;
    --repo) REPO="$2"; shift 2 ;;
    --workflow) WORKFLOW="$2"; shift 2 ;;
    --ref) REF="$2"; shift 2 ;;
    --input) INPUTS+=("$2"); shift 2 ;;
    --expected-image) EXPECTED_IMAGE="$2"; shift 2 ;;
    --image-input) IMAGE_INPUT="$2"; shift 2 ;;
    --timeout) TIMEOUT="$2"; shift 2 ;;
    --dry-run) DRY_RUN="true"; shift ;;
    -h|--help) usage ;;
    *) echo "[ERROR] unknown arg: $1" >&2; usage ;;
  esac
done

command -v jq >/dev/null 2>&1 || { echo "[ERROR] jq required" >&2; exit 1; }
[[ -n "$SIGNAL_ID" && -n "$REPO" && -n "$WORKFLOW" ]] || usage
[[ "$TIMEOUT" =~ ^[1-9][0-9]*$ ]] || usage
WORKFLOW="$(basename "$WORKFLOW")"

emit() {
  jq -nc \
    --arg id "$SIGNAL_ID" --arg repo "$REPO" --arg wf "$WORKFLOW" \
    --arg conclusion "$1" --arg error "${2:-}" --arg headSha "$head_sha" \
    --arg runId "$run_id" --argjson runAttempt "$run_attempt" \
    --arg image "$EXPECTED_IMAGE" --arg imageDigest "$image_digest" --arg artifactId "$artifact_id" '
    {id:$id, owningRepo:$repo, workflow:$wf, conclusion:$conclusion,
     runId:(if $runId == "" then null else ($runId|tonumber) end),
     runAttempt:(if $runId == "" then null else $runAttempt end),
     runUrl:(if $runId == "" then null else "https://github.com/\($repo)/actions/runs/\($runId)/attempts/\($runAttempt)" end),
     headSha:$headSha, image:$image, imageDigest:$imageDigest,
     receiptArtifactId:(if $artifactId == "" then null else ($artifactId|tonumber) end), verificationError:$error}
    | with_entries(select(.value != null and .value != ""))'
}

refuse() {
  echo "[ERROR] $1" >&2
  emit "missing" "$1"
  exit 0
}

if [[ "$DRY_RUN" == "true" ]]; then
  emit "skipped"
  exit 0
fi

command -v gh >/dev/null 2>&1 || { echo "[ERROR] gh required (or use --dry-run)" >&2; exit 1; }
inputs='{}'
for kv in "${INPUTS[@]}"; do
  [[ "$kv" == *=* && -n "${kv%%=*}" ]] || refuse "Invalid workflow input"
  key="${kv%%=*}"
  jq -e --arg key "$key" 'has($key)' <<<"$inputs" >/dev/null && refuse "Duplicate workflow input: $key"
  inputs="$(jq -c --arg key "$key" --arg value "${kv#*=}" '. + {($key):$value}' <<<"$inputs")"
done
if [[ -n "$EXPECTED_IMAGE" || -n "$IMAGE_INPUT" ]]; then
  [[ -n "$IMAGE_INPUT" && "$EXPECTED_IMAGE" =~ ^[^[:space:]@]+@sha256:[a-f0-9]{64}$ ]] \
    || refuse "Image suites require an immutable image and its input key"
  image_digest="${EXPECTED_IMAGE##*@}"
  jq -e --arg key "$IMAGE_INPUT" --arg image "$EXPECTED_IMAGE" '.[$key] == $image' <<<"$inputs" >/dev/null \
    || refuse "Dispatched image input does not match the candidate"
fi

# Resolve the target ref before dispatch; reject ref movement rather than bind
# a different source revision to this candidate. No list/latest/time fallback.
revision="$(gh api "repos/$REPO/commits/$(jq -rn --arg ref "$REF" '$ref|@uri')")" \
  || refuse "Could not resolve target ref"
head_sha="$(jq -r '.sha // empty' <<<"$revision")"
[[ "$head_sha" =~ ^[a-f0-9]{40}$ ]] || refuse "Invalid target revision"
payload="$(jq -nc --arg ref "$REF" --argjson inputs "$inputs" \
  '{ref:$ref, inputs:$inputs, return_run_details:true}')"
# Opt into run details on the stable API. The response is the dispatch identity:
# https://docs.github.com/en/rest/actions/workflows?apiVersion=2022-11-28#create-a-workflow-dispatch-event
response="$(gh api --method POST "repos/$REPO/actions/workflows/$WORKFLOW/dispatches" \
  -H 'X-GitHub-Api-Version: 2022-11-28' --input - <<<"$payload")" \
  || refuse "Workflow dispatch failed"
run_id="$(jq -er 'select(type == "object") | .workflow_run_id | select(type == "number" and . > 0 and . == floor)' <<<"$response")" \
  || refuse "Dispatch did not return one exact run id"
[[ "$run_id" =~ ^[1-9][0-9]*$ ]] || { run_id=""; refuse "Dispatch run identity is not unique"; }

valid_run() {
  jq -e --argjson runId "$run_id" --argjson attempt "$run_attempt" \
    --arg repo "$REPO" --arg workflow ".github/workflows/$WORKFLOW" --arg sha "$head_sha" '
    .id == $runId and .run_attempt == $attempt and .event == "workflow_dispatch" and
    .repository.full_name == $repo and .path == $workflow and .head_sha == $sha' <<<"$1" >/dev/null
}

result=""
deadline=$(( $(date +%s) + TIMEOUT ))
while [[ $(date +%s) -lt $deadline ]]; do
  result="$(gh api "repos/$REPO/actions/runs/$run_id")" || refuse "Could not read dispatched run"
  valid_run "$result" || refuse "Run identity, source, workflow or attempt changed"
  if [[ "$(jq -r '.status' <<<"$result")" == "completed" ]]; then
    break
  fi
  sleep 5
done
[[ "${result:-}" != "" && "$(jq -r '.status' <<<"$result")" == "completed" ]] \
  || refuse "Dispatched run timed out"
conclusion="$(jq -r '.conclusion // "missing"' <<<"$result")"

if [[ "$conclusion" == "success" && -n "$EXPECTED_IMAGE" ]]; then
  command -v python3 >/dev/null 2>&1 || refuse "python3 required for image receipt verification"
  temporary="$(mktemp -d)"
  trap 'rm -rf "$temporary"' EXIT
  # Paginate and require exactly one named artifact, including expired matches.
  artifacts="$(gh api --paginate --slurp "repos/$REPO/actions/runs/$run_id/artifacts?per_page=100")" \
    || refuse "Could not enumerate suite receipts"
  artifact="$(jq -ce '[.[].artifacts[] | select(.name == "release-suite-receipt")] | select(length == 1) | .[0] | select(.expired == false)' <<<"$artifacts")" \
    || refuse "Suite receipt is missing, expired or not unique"
  artifact_id="$(jq -r '.id' <<<"$artifact")"
  [[ "$artifact_id" =~ ^[1-9][0-9]*$ ]] || refuse "Invalid receipt artifact id"
  gh api "repos/$REPO/actions/artifacts/$artifact_id/zip" > "$temporary/receipt.zip" \
    || refuse "Could not download suite receipt"
  # Read only the named file without extracting arbitrary archive paths.
  python3 - "$temporary/receipt.zip" "$temporary/receipt.json" <<'PYTHON' || refuse "Suite receipt file is missing, invalid or not unique"
import sys
import zipfile
from pathlib import Path

with zipfile.ZipFile(sys.argv[1]) as archive:
    receipts = [entry for entry in archive.infolist() if entry.filename == "release-suite-receipt.json"]
    if len(receipts) != 1 or receipts[0].file_size > 65536:
        sys.exit(1)
    Path(sys.argv[2]).write_bytes(archive.read(receipts[0]))
PYTHON
  # -s also rejects multiple JSON documents: a receipt must be unambiguous.
  jq -se --arg id "$SIGNAL_ID" --arg repo "$REPO" --arg wf "$WORKFLOW" \
    --argjson runId "$run_id" --argjson attempt "$run_attempt" --arg sha "$head_sha" \
    --arg image "$EXPECTED_IMAGE" --arg digest "$image_digest" '
    length == 1 and (.[0] | .id == $id and .owningRepo == $repo and .workflow == $wf and
    .runId == $runId and .runAttempt == $attempt and .headSha == $sha and
    .image == $image and .imageDigest == $digest)' "$temporary/receipt.json" >/dev/null \
    || refuse "Suite receipt does not match the dispatched run, attempt and candidate image"
  # Detect a rerun that started during artifact collection as well as in polling.
  result="$(gh api "repos/$REPO/actions/runs/$run_id")" || refuse "Could not recheck dispatched run"
  valid_run "$result" && jq -e '.status == "completed" and .conclusion == "success"' <<<"$result" >/dev/null \
    || refuse "Run changed while collecting the suite receipt"
fi

emit "$conclusion"
