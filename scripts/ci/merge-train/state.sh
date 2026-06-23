#!/usr/bin/env bash
# Step 8: state — persist the train's progress to a "Merge Train State" GitHub
# issue (label train:state) carrying a fenced JSON block, written BEFORE each
# side-effecting step so a crash is resumable: on startup the train reads the
# phase and resumes. Per-PR labels (train:landing / train:escalated / train:hold)
# also carry transient state.
#
# The JSON shape:
#   {
#     "active_batch": {
#       "branch": "...", "trunk_base": "<sha>", "included": [<pr>...],
#       "phase": "select|assemble|smart-ci|forward-fix|attribute|land|done",
#       "run_id": <id|null>, "fwdfix_attempts": <n>, "flake_reruns": <n>
#     },
#     "config": { "max_batch": <n>, "flake_signatures": "<regex>" },
#     "last_landed_trunk": "<sha|null>"
#   }
#
# Issue title: "Merge Train State". We find-or-create by the train:state label.

TRAIN_STATE_TITLE="${TRAIN_STATE_TITLE:-Merge Train State}"

# train_state_render <branch> <trunk_base> <included-csv> <phase> <run_id> \
#   <fwdfix> <flake_reruns> <last_landed>: emit the fenced-JSON issue body.
train_state_render() {
  local branch="$1" trunk_base="$2" included_csv="$3" phase="$4" \
        run_id="$5" fwdfix="$6" flake_reruns="$7" last_landed="$8"

  local included_json
  if [[ -z "${included_csv}" ]]; then
    included_json="[]"
  else
    included_json="$(printf '%s' "${included_csv}" \
      | jq -Rc 'split(",") | map(select(length>0) | tonumber)')"
  fi
  local run_id_json="null"; [[ -n "${run_id}" && "${run_id}" != "null" ]] && run_id_json="${run_id}"
  local last_json="null"; [[ -n "${last_landed}" && "${last_landed}" != "null" ]] && last_json="\"${last_landed}\""

  local json
  json="$(jq -n \
    --arg branch "${branch}" \
    --arg tb "${trunk_base}" \
    --argjson inc "${included_json}" \
    --arg phase "${phase}" \
    --argjson rid "${run_id_json}" \
    --argjson fwd "${fwdfix:-0}" \
    --argjson fr "${flake_reruns:-0}" \
    --argjson mb "${MAX_BATCH}" \
    --arg flake "${TRAIN_FLAKE_REGEX}" \
    --argjson last "${last_json}" \
    '{
      active_batch: {
        branch: $branch, trunk_base: $tb, included: $inc, phase: $phase,
        run_id: $rid, fwdfix_attempts: $fwd, flake_reruns: $fr
      },
      config: { max_batch: $mb, flake_signatures: $flake },
      last_landed_trunk: $last
    }')"

  printf 'Machine-managed state for the optimistic batch merge train. Do not edit by hand.\n\n```json\n%s\n```\n' "${json}"
}

# train_state_issue_number: find the state issue number (or empty). READ-ONLY.
train_state_issue_number() {
  if [[ -n "${TRAIN_STATE_ISSUE_OVERRIDE:-}" ]]; then
    echo "${TRAIN_STATE_ISSUE_OVERRIDE}"; return 0
  fi
  gh issue list --label "${TRAIN_LABEL_STATE}" --state open \
    --json number --jq '.[0].number // empty' 2>/dev/null || echo ""
}

# train_state_write <body-file>: create-or-update the state issue. Side-effecting.
train_state_write() {
  local body_file="$1"
  local num
  num="$(train_state_issue_number)"
  if [[ -n "${num}" ]]; then
    train_side_effect gh issue edit "${num}" --body-file "${body_file}"
  else
    train_side_effect gh issue create --title "${TRAIN_STATE_TITLE}" \
      --label "${TRAIN_LABEL_STATE}" --body-file "${body_file}"
  fi
}

# train_state_read: emit the parsed JSON block of the state issue (or empty).
# READ-ONLY; used on startup to resume.
train_state_read() {
  local num body
  num="$(train_state_issue_number)"
  [[ -z "${num}" ]] && return 0
  if [[ -n "${TRAIN_STATE_BODY_OVERRIDE:-}" ]]; then
    body="${TRAIN_STATE_BODY_OVERRIDE}"
  else
    body="$(gh issue view "${num}" --json body --jq '.body' 2>/dev/null || echo "")"
  fi
  # Extract the fenced ```json ... ``` block.
  printf '%s\n' "${body}" | awk '
    /^```json/ { inblk=1; next }
    /^```/     { if (inblk) { inblk=0 } ; next }
    inblk      { print }
  '
}
