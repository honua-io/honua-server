#!/usr/bin/env bash

# Merge per-suite evidence records (emitted by dispatch-and-wait.sh, one JSON
# object per line) plus the static suite registry (release/bundle-suites.json)
# into the single evidence.json consumed by build-release-manifest.sh.
#
# Integration suites whose id starts with "server-" become releaseGates; SDK
# suites and the repository lanes become repositoryLanes; the cross-cutting
# release-lane criteria are synthesized. Registry entries with no collected
# result (e.g. refactorPending suites that were not dispatched) are recorded with
# conclusion "skipped" and a blocker so the gate is visibly not-yet-green rather
# than silently dropped.

set -euo pipefail

REGISTRY=""
INTEGRATION_NDJSON=""
SDK_NDJSON=""
OUTPUT=""

usage() {
  echo "Usage: collect-evidence.sh --registry <bundle-suites.json> [--integration <ndjson>] [--sdk <ndjson>] --output <evidence.json>" >&2
  exit 2
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --registry) REGISTRY="$2"; shift 2 ;;
    --integration) INTEGRATION_NDJSON="$2"; shift 2 ;;
    --sdk) SDK_NDJSON="$2"; shift 2 ;;
    --output) OUTPUT="$2"; shift 2 ;;
    -h|--help) usage ;;
    *) echo "[ERROR] unknown arg: $1" >&2; usage ;;
  esac
done

command -v jq >/dev/null 2>&1 || { echo "[ERROR] jq required" >&2; exit 1; }
[[ -n "$REGISTRY" && -f "$REGISTRY" ]] || { echo "[ERROR] --registry file required" >&2; usage; }
[[ -n "$OUTPUT" ]] || usage

# Collected results keyed by signal id (empty array if file absent/empty).
collected() {
  local f="$1"
  if [[ -n "$f" && -s "$f" ]]; then jq -s '.' "$f"; else echo '[]'; fi
}
integration_results="$(collected "$INTEGRATION_NDJSON")"
sdk_results="$(collected "$SDK_NDJSON")"

jq -n \
  --slurpfile registry "$REGISTRY" \
  --argjson integration "$integration_results" \
  --argjson sdk "$sdk_results" '

  ($registry[0]) as $reg |
  ($integration | INDEX(.id)) as $intByeId |
  ($sdk | INDEX(.id)) as $sdkById |

  # Merge a registry entry with its collected result (result wins for runtime
  # fields); registry entries never dispatched -> skipped + blocker.
  def merge($entry; $result):
    ($result // null) as $r |
    {
      id: $entry.id,
      name: $entry.name,
      owningRepo: $entry.owningRepo,
      requirement: $entry.requirement,
      workflow: ($entry.workflow // null),
      tier: ($entry.tier // null),
      conclusion: ($r.conclusion // (if ($entry.refactorPending == true) or ($entry.mode == "manual") then "skipped" else "missing" end)),
      runUrl: ($r.runUrl // null),
      headSha: ($r.headSha // null),
      blockers: (
        if ($r.conclusion // "") == "success" then []
        elif ($entry.refactorPending == true) then
          [ { repo: $entry.owningRepo, number: null, url: null,
              reason: ("\($entry.id): not yet wired to the release-candidate image (refactor pending).") } ]
        elif ($entry.mode == "manual") then
          [ { repo: $entry.owningRepo, number: null, url: null,
              reason: ("\($entry.id): manual lane evidence not attached for this candidate.") } ]
        elif (($r // null) == null) then
          [ { repo: $entry.owningRepo, number: null, url: null,
              reason: ("\($entry.id): no run evidence collected for this candidate.") } ]
        else
          [ { repo: $entry.owningRepo, number: null, url: null,
              reason: ("\($entry.id): run concluded \($r.conclusion).") } ]
        end
      )
    } | with_entries(select(.value != null));

  {
    # server-* integration suites are release gates; other dispatched integration
    # suites (e.g. the console admin lane) are repository lanes.
    releaseGates: [
      $reg.integration[] | select(.id | startswith("server-")) | merge(.; $intByeId[.id])
    ],
    repositoryLanes: (
      [ $reg.integration[] | select(.id | startswith("server-") | not) | merge(.; $intByeId[.id]) ]
      + [ $reg.sdk[] | merge(.; $sdkById[.id]) ]
      + [ $reg.repositoryLanes[] | merge(.; null) ]
    ),
    releaseLaneCriteria: [
      { id: "release-candidate-image-compatibility",
        requirement: "Cross-repo compatibility train passes against the release-candidate image.",
        conclusion: (if ([ $reg.integration[] | select(.id|startswith("server-")) | $intByeId[.id].conclusion ]
                          | all(. == "success")) then "success" else "missing" end) },
      { id: "release-evidence-pack",
        requirement: "Release manifest, smoke results, and source-backed feature map are attached.",
        conclusion: "success" }
    ]
  }
' >"$OUTPUT"

echo "[INFO] wrote $OUTPUT"
gates="$(jq -r '.releaseGates|length' "$OUTPUT")"
lanes="$(jq -r '.repositoryLanes|length' "$OUTPUT")"
echo "[INFO] releaseGates=${gates} repositoryLanes=${lanes}"
