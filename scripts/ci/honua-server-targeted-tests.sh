#!/usr/bin/env bash
# Compute the server-tests shard subset to execute on a pull request based on
# the changed files in the diff. Owned by ADR-0037. The shard map and watched
# prefix lists are sourced from .github/ci-shards.json.
#
# Behaviour (in evaluation order):
#   1. When no files changed (empty diff), emit
#      {"run_all": true, "reason": "no_changed_files"} so a manual replay still
#      exercises the full matrix.
#   2. Compute the `targeted_override_prefixes` set: changed files under one of
#      those prefixes are "claimed" by an explicit smoke/feature-area shard
#      subset instead of escalating to run_all. This narrows the over-broad
#      run_all triggers for endpoint-registration PLUMBING
#      (EndpointRegistry.cs, Program.cs registration, Startup/JsonContextRegistration.cs)
#      and for shared FEATURE-AREA dirs (e.g. Honua.Hosting/Features/Authentication/,
#      .../Security/). Files matched here are removed from the infrastructure_paths
#      escalation (step 3) and the unmapped-source net (step 5), and the override's
#      shard list is unioned into the targeted result. The always-on architecture/
#      governance guards (EndpointRegistry/OperationRegistry drift + coverage,
#      proof-ledger) run on every PR regardless and catch registration mistakes,
#      which is what makes targeting these plumbing paths safe rather than run_all.
#   3. When the diff touches a path under `infrastructure_paths` in
#      ci-shards.json (TestKit, Core/Postgres project and shared
#      infrastructure paths, Honua.ServiceDefaults,
#      src/Honua.Server/Features/Infrastructure/, src/Honua.Server/Startup/) that
#      is NOT already claimed by a `targeted_override_prefixes` entry, emit
#      {"run_all": true, "reason": "infrastructure_change"}.
#   4. Otherwise, walk every changed file, match it against shard `paths`
#      prefixes, and union the shard names that claim it.
#   5. When the diff touches a path under `unmapped_source_run_all_prefixes`
#      (src/Honua.Server/, src/Honua.DuckDB/, tests/dotnet/Honua.Server.Tests/)
#      that no shard's `paths` claims AND that is not claimed by a
#      `targeted_override_prefixes` entry, emit
#      {"run_all": true, "reason": "unmapped_source_change"} so a new feature
#      directory does not silently fall back to the Core shard whose filter
#      excludes Honua.Server.Tests.Features.*.
#   6. When the union from steps 4 + 2 is empty (e.g. doc-only or CI-only diffs
#      that did not trigger steps 3 or 5), fall back to
#      `default_shards_when_no_match` (currently ["Core"]) with reason
#      "no_path_match" so the smoke shard still runs.
#   7. Otherwise emit {"run_all": false, "shards": [...], "reason": "targeted"}
#      with the matched + override shard set.
#
# Usage:
#   honua-server-targeted-tests.sh                    # auto-detect base ref
#   honua-server-targeted-tests.sh --base origin/trunk
#   git diff --name-only HEAD~1 HEAD | honua-server-targeted-tests.sh --stdin
#
# Exit codes:
#   0  — JSON descriptor written to stdout.
#   2  — usage / configuration error (logged to stderr).

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"
CONFIG_FILE="${REPO_ROOT}/.github/ci-shards.json"

usage() {
  cat <<USAGE >&2
Usage: $(basename "$0") [--base <ref>] [--stdin] [--config <path>]

Outputs a JSON descriptor for the Honua.Server.Tests targeted shard subset.

Options:
  --base <ref>     Compute diff against this ref (default: origin/trunk).
  --stdin          Read changed files from stdin (one per line) instead of
                   running git diff. Useful inside CI when the runner has
                   already computed the file list.
  --config <path>  Override the path to ci-shards.json (default:
                   .github/ci-shards.json relative to the repo root).
USAGE
}

BASE_REF=""
READ_STDIN="false"
CUSTOM_CONFIG=""

while [[ $# -gt 0 ]]; do
  case "$1" in
    --base)
      BASE_REF="${2:-}"
      if [[ -z "${BASE_REF}" ]]; then usage; exit 2; fi
      shift 2
      ;;
    --stdin)
      READ_STDIN="true"
      shift
      ;;
    --config)
      CUSTOM_CONFIG="${2:-}"
      if [[ -z "${CUSTOM_CONFIG}" ]]; then usage; exit 2; fi
      shift 2
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    *)
      echo "unknown argument: $1" >&2
      usage
      exit 2
      ;;
  esac
done

if [[ -n "${CUSTOM_CONFIG}" ]]; then
  CONFIG_FILE="${CUSTOM_CONFIG}"
fi

if [[ ! -f "${CONFIG_FILE}" ]]; then
  echo "ci-shards config not found: ${CONFIG_FILE}" >&2
  exit 2
fi

if ! command -v jq >/dev/null 2>&1; then
  echo "jq is required" >&2
  exit 2
fi

# CR-safe jq: strip the CRLF the Windows jq binary emits in text mode so the
# emitted JSON descriptor and every captured value stays clean (no-op on Linux).
# Sourced AFTER the presence guard above so it still probes the real jq binary.
source "${SCRIPT_DIR}/lib/jq-cr-safe.sh"

# Compute the changed-file list.
if [[ "${READ_STDIN}" == "true" ]]; then
  CHANGED_FILES="$(cat || true)"
else
  if [[ -z "${BASE_REF}" ]]; then
    BASE_REF="origin/trunk"
  fi
  if ! git -C "${REPO_ROOT}" rev-parse --verify "${BASE_REF}" >/dev/null 2>&1; then
    echo "base ref not found: ${BASE_REF}" >&2
    exit 2
  fi
  CHANGED_FILES="$(git -C "${REPO_ROOT}" diff --name-only "${BASE_REF}"...HEAD || true)"
fi

# Strip blank lines and trim.
CHANGED_FILES="$(printf '%s\n' "${CHANGED_FILES}" | sed '/^$/d')"

ALL_SHARD_NAMES_JSON="$(jq -c '[.shards[].name]' "${CONFIG_FILE}")"

# When no files changed (e.g. workflow_dispatch with no diff), be conservative
# and run the full matrix.
if [[ -z "${CHANGED_FILES}" ]]; then
  jq -nc --argjson shards "${ALL_SHARD_NAMES_JSON}" \
    '{run_all: true, shards: $shards, reason: "no_changed_files"}'
  exit 0
fi

# Compute the targeted-override contribution: the union of shard names declared
# by every `targeted_override_prefixes` entry whose prefix matches a changed
# file. These prefixes deliberately route endpoint-registration PLUMBING and
# shared FEATURE-AREA dirs to a small representative subset instead of run_all.
# A file under such a prefix is "override-claimed" — it must NOT escalate the
# infrastructure_paths short-circuit (step 3) nor trip the unmapped-source net
# (step 5). The shards collected here are unioned into the targeted result.
OVERRIDE_SHARDS_JSON="$(
  printf '%s\n' "${CHANGED_FILES}" \
    | jq -Rsc --slurpfile cfg "${CONFIG_FILE}" '
        split("\n")
        | map(select(length > 0)) as $files
        | ($cfg[0].targeted_override_prefixes // [])
        | map(
            . as $entry
            | select($files | any(. as $f | $f | startswith($entry.prefix)))
            | $entry.shards[]
          )
        | unique
      '
)"

# Detect whether any shared-infrastructure path was touched. A file that is also
# claimed by a targeted-override prefix is excluded so registration plumbing
# (e.g. src/Honua.Server/Startup/JsonContextRegistration.cs, which sits under the
# infrastructure_paths prefix src/Honua.Server/Startup/) routes to the override's
# smoke subset instead of forcing run_all. Non-override infrastructure files
# (the TestKit harness, the Core/Postgres canonical pipeline, etc.) still
# escalate as before.
INFRA_HIT="$(
  printf '%s\n' "${CHANGED_FILES}" \
    | jq -Rsc --slurpfile cfg "${CONFIG_FILE}" '
        split("\n")
        | map(select(length > 0)) as $files
        | ($cfg[0].targeted_override_prefixes // []) as $overrides
        | ($files | map(select(. as $f | ($overrides | any(. as $o | $f | startswith($o.prefix))) | not))) as $unclaimed
        | $cfg[0].infrastructure_paths
        | any(. as $prefix | $unclaimed | any(startswith($prefix)))
      '
)"

if [[ "${INFRA_HIT}" == "true" ]]; then
  jq -nc --argjson shards "${ALL_SHARD_NAMES_JSON}" \
    '{run_all: true, shards: $shards, reason: "infrastructure_change"}'
  exit 0
fi

# Match each changed file against shard prefixes and union the results. An override
# may opt into exclusive ownership for an exact test file whose broad directory is
# shared by several filtered shards; those files contribute only the override's
# declared shards. Existing non-exclusive smoke/feature overrides retain union
# semantics.
TARGETED_SHARDS_JSON="$(
  printf '%s\n' "${CHANGED_FILES}" \
    | jq -Rsc --slurpfile cfg "${CONFIG_FILE}" '
        split("\n")
        | map(select(length > 0)) as $all_files
        | ($cfg[0].targeted_override_prefixes // [])
            | map(select(.exclusive // false)) as $exclusive_overrides
        | ($all_files
            | map(select(
                . as $f
                | ($exclusive_overrides | any(. as $o | $f | startswith($o.prefix))) | not
              ))) as $files
        | $cfg[0].shards
        | map(
            . as $shard
            | $shard
            | select(
                ($files | any(. as $f | $shard.paths | any(. as $p | $f | startswith($p))))
              )
            | .name
          )
        | unique
      '
)"

# Detect changed files that look like source code under a watched prefix but
# were not matched by any shard. This prevents an unmapped feature directory
# (e.g. src/Honua.Server/Features/Export/) from silently falling back to the
# Core shard whose filter excludes Honua.Server.Tests.Features.*.
UNMAPPED_SOURCE_HIT="$(
  printf '%s\n' "${CHANGED_FILES}" \
    | jq -Rsc --slurpfile cfg "${CONFIG_FILE}" '
        split("\n")
        | map(select(length > 0)) as $files
        | ($cfg[0].unmapped_source_run_all_prefixes // []) as $watched
        | ($cfg[0].shards | map(.paths) | flatten) as $known_paths
        | ($cfg[0].targeted_override_prefixes // []) as $overrides
        | $files
        | any(
            . as $f
            | ($watched | any(. as $p | $f | startswith($p)))
              and (($known_paths | any(. as $p | $f | startswith($p))) | not)
              and (($overrides | any(. as $o | $f | startswith($o.prefix))) | not)
          )
      '
)"

if [[ "${UNMAPPED_SOURCE_HIT}" == "true" ]]; then
  jq -nc --argjson shards "${ALL_SHARD_NAMES_JSON}" \
    '{run_all: true, shards: $shards, reason: "unmapped_source_change"}'
  exit 0
fi

# Union the targeted-override shards (step 2) into the matched set. An
# endpoint-adding PR that touches both a registration-plumbing override prefix
# AND a feature dir under a shard's `paths` thus runs the override smoke subset
# PLUS that feature's owning shard(s).
TARGETED_SHARDS_JSON="$(
  jq -nc \
    --argjson matched "${TARGETED_SHARDS_JSON}" \
    --argjson overrides "${OVERRIDE_SHARDS_JSON}" \
    '($matched + $overrides) | unique'
)"

# Apply default-when-no-match. Only reached when no source under a watched
# prefix was touched AND no override prefix matched (e.g. docs- or workflow-only
# diffs that did not already trigger the infrastructure_paths short-circuit).
SHARDS_LEN="$(printf '%s' "${TARGETED_SHARDS_JSON}" | jq 'length')"
if [[ "${SHARDS_LEN}" == "0" ]]; then
  TARGETED_SHARDS_JSON="$(jq -c '.default_shards_when_no_match' "${CONFIG_FILE}")"
  REASON="no_path_match"
else
  REASON="targeted"
fi

jq -nc \
  --argjson shards "${TARGETED_SHARDS_JSON}" \
  --arg reason "${REASON}" \
  '{run_all: false, shards: $shards, reason: $reason}'
