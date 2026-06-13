#!/usr/bin/env bash

# Build (or refresh) a Honua release-train manifest from release-bundle evidence.
#
# This replaces the hand-editing of release/honua-<id>.json documented in
# docs/contributor/RELEASE_CHECKLIST.md. The release-bundle orchestrator
# (.github/workflows/release-bundle.yml) builds ONE Native AOT release-candidate
# image, runs the integration/conformance/SDK evidence against that exact image
# digest, then calls this script to assemble the manifest. The manifest is the
# producer side of the contract consumed by honua-devops'
# compat-train-release-validation.sh (which emits the release-train scoreboard).
#
# Inputs are an evidence JSON (the per-gate/lane run results collected by the
# orchestrator) plus scalar args for the candidate identity. An optional --base
# manifest carries forward static fields (releaseIssue, compatibilityBaseline,
# childTickets, ...) so a refresh only updates the dynamic evidence.
#
# Determinism: OBSERVED_AT is an input (never wall-clock) so the same evidence
# reproduces byte-identical output — required for the smoke self-test.

set -euo pipefail

usage() {
  cat >&2 <<'EOF'
Usage: build-release-manifest.sh --evidence <file> [options]

Required:
  --evidence <file>        Evidence JSON (releaseGates/repositoryLanes/
                           releaseLaneCriteria/waivers/meta). See evidence shape below.
  --release-id <id>        e.g. honua-2026-06-preview
  --channel <channel>      preview | rc | ga

Candidate identity (image is what makes candidate.image.evidenceState pass):
  --server-ref <sha>       Release-candidate server commit
  --ref-source <label>     Optional source label (branch/tag) for the ref
  --image-repository <r>   e.g. ghcr.io/honua-io/honua-server
  --image-tag <tag>        Immutable RC tag (e.g. rc-honua-2026-06-preview-<sha>)
  --image-digest <sha256>  Immutable image digest (sha256:...)

Other:
  --observed-at <iso8601>  Timestamp recorded in the manifest (default: env OBSERVED_AT, else error)
  --base <manifest>        Existing manifest to carry static fields forward from
  --output <path>          Output path (default: release/<release-id>.json)

Evidence JSON shape (all arrays optional; entries replace the base when present):
  {
    "meta": { "releaseIssue": {...}, "sdkCompatibilityManifest": "...",
              "compatibilityBaseline": {...}, "childTickets": [...] },
    "releaseGates":        [ <signal> ],
    "repositoryLanes":     [ <signal> ],
    "releaseLaneCriteria": [ <signal> ],
    "waivers":             [ {...} ]
  }
  <signal> = {
    "id": "server-sdk-compatibility",         # required
    "name": "SDK Server Compatibility",
    "owningRepo": "honua-io/honua-server",
    "requirement": "...",
    "workflow": ".github/workflows/sdk-server-compatibility.yml",
    "tier": "nightly",
    "conclusion": "success|failure|skipped|cancelled|missing",   # drives evidenceState
    "runUrl": "https://github.com/.../actions/runs/123",
    "artifact": "sdk-compatibility-matrix-123",
    "headSha": "abc123",
    "createdAt": "...", "updatedAt": "...", "source": "...",
    "evidence": { ... },                       # criteria-style structured evidence (optional)
    "blockers": [ { "repo": "...", "number": 1, "url": "...", "reason": "..." } ],
    "waiver": { "approvedBy": "...", "reason": "..." }            # optional
  }
A conclusion of success/passed/green/ok -> evidenceState "passed"; anything else
-> "blocked". A signal with no conclusion but a non-empty blockers[] -> "blocked".
EOF
  exit 2
}

EVIDENCE_FILE=""
RELEASE_ID="${RELEASE_ID:-}"
CHANNEL="${CHANNEL:-}"
SERVER_REF="${SERVER_REF:-}"
REF_SOURCE="${REF_SOURCE:-}"
IMAGE_REPOSITORY="${IMAGE_REPOSITORY:-}"
IMAGE_TAG="${IMAGE_TAG:-}"
IMAGE_DIGEST="${IMAGE_DIGEST:-}"
OBSERVED_AT="${OBSERVED_AT:-}"
BASE_MANIFEST=""
OUTPUT=""

while [[ $# -gt 0 ]]; do
  case "$1" in
    --evidence) EVIDENCE_FILE="$2"; shift 2 ;;
    --release-id) RELEASE_ID="$2"; shift 2 ;;
    --channel) CHANNEL="$2"; shift 2 ;;
    --server-ref) SERVER_REF="$2"; shift 2 ;;
    --ref-source) REF_SOURCE="$2"; shift 2 ;;
    --image-repository) IMAGE_REPOSITORY="$2"; shift 2 ;;
    --image-tag) IMAGE_TAG="$2"; shift 2 ;;
    --image-digest) IMAGE_DIGEST="$2"; shift 2 ;;
    --observed-at) OBSERVED_AT="$2"; shift 2 ;;
    --base) BASE_MANIFEST="$2"; shift 2 ;;
    --output) OUTPUT="$2"; shift 2 ;;
    -h|--help) usage ;;
    *) echo "[ERROR] Unknown argument: $1" >&2; usage ;;
  esac
done

command -v jq >/dev/null 2>&1 || { echo "[ERROR] jq is required" >&2; exit 1; }

[[ -n "$EVIDENCE_FILE" ]] || { echo "[ERROR] --evidence is required" >&2; usage; }
[[ -f "$EVIDENCE_FILE" ]] || { echo "[ERROR] evidence file not found: $EVIDENCE_FILE" >&2; exit 1; }
jq -e . "$EVIDENCE_FILE" >/dev/null 2>&1 || { echo "[ERROR] evidence file is not valid JSON: $EVIDENCE_FILE" >&2; exit 1; }
[[ -n "$RELEASE_ID" ]] || { echo "[ERROR] --release-id is required" >&2; usage; }
[[ -n "$CHANNEL" ]] || { echo "[ERROR] --channel is required" >&2; usage; }
[[ -n "$OBSERVED_AT" ]] || { echo "[ERROR] --observed-at (or OBSERVED_AT env) is required for deterministic output" >&2; usage; }

base_json="{}"
if [[ -n "$BASE_MANIFEST" ]]; then
  [[ -f "$BASE_MANIFEST" ]] || { echo "[ERROR] base manifest not found: $BASE_MANIFEST" >&2; exit 1; }
  jq -e . "$BASE_MANIFEST" >/dev/null 2>&1 || { echo "[ERROR] base manifest is not valid JSON: $BASE_MANIFEST" >&2; exit 1; }
  base_json="$(cat "$BASE_MANIFEST")"
fi

OUTPUT="${OUTPUT:-release/${RELEASE_ID}.json}"

manifest="$(
  jq -n \
    --argjson base "$base_json" \
    --slurpfile evidence "$EVIDENCE_FILE" \
    --arg releaseId "$RELEASE_ID" \
    --arg channel "$CHANNEL" \
    --arg serverRef "$SERVER_REF" \
    --arg refSource "$REF_SOURCE" \
    --arg imageRepo "$IMAGE_REPOSITORY" \
    --arg imageTag "$IMAGE_TAG" \
    --arg imageDigest "$IMAGE_DIGEST" \
    --arg observedAt "$OBSERVED_AT" '

    ($evidence[0]) as $ev |

    # Map a CI conclusion to an evidenceState.
    def state_from($conclusion; $blockers):
      ($conclusion // "" | ascii_downcase) as $c |
      if   ($c | IN("success","passed","green","ok","completed")) then "passed"
      elif ($c == "" and (($blockers // []) | length) == 0) then "blocked"
      else "blocked" end;

    # Normalize one signal entry into a manifest gate/lane record.
    def to_record($s):
      ($s.blockers // []) as $blockers |
      state_from($s.conclusion; $blockers) as $estate |
      {
        id: $s.id,
        name: ($s.name // $s.id),
        owningRepo: $s.owningRepo,
        requirement: $s.requirement,
        workflow: $s.workflow,
        tier: $s.tier,
        evidenceState: $estate,
        latestEvidence: (
          if ($s.runUrl // $s.artifact // $s.headSha // $s.conclusion // $s.evidence) != null then
            ({
              runUrl: $s.runUrl,
              workflow: $s.workflow,
              status: (if $s.conclusion != null then "completed" else null end),
              conclusion: $s.conclusion,
              headSha: $s.headSha,
              artifact: $s.artifact,
              createdAt: $s.createdAt,
              updatedAt: $s.updatedAt,
              source: $s.source
            } + (if $s.evidence != null then { evidence: $s.evidence } else {} end))
            | with_entries(select(.value != null))
          else null end
        ),
        waiver: ($s.waiver // null),
        blockers: $blockers
      }
      # Drop null scalar keys (owningRepo/requirement/workflow/tier) when absent,
      # but always keep evidenceState, blockers, waiver, id, name.
      | { id, name, evidenceState }
        + (if .owningRepo != null then { owningRepo } else {} end)
        + (if .requirement != null then { requirement } else {} end)
        + (if .workflow != null then { workflow } else {} end)
        + (if .tier != null then { tier } else {} end)
        + { latestEvidence: .latestEvidence, waiver: .waiver, blockers: .blockers };

    # Criteria entries use `evidence` rather than `latestEvidence`.
    def to_criterion($s):
      ($s.blockers // []) as $blockers |
      {
        id: $s.id,
        requirement: ($s.requirement // $s.name // $s.id),
        evidenceState: state_from($s.conclusion; $blockers),
        evidence: ($s.evidence // null),
        waiver: ($s.waiver // null),
        blockers: $blockers
      };

    ($imageDigest != "" and $imageTag != "") as $imageReady |

    {
      "$schema": ($base["$schema"] // "https://honua.io/schemas/release-train-manifest.v1.json"),
      schemaVersion: 1,
      releaseId: $releaseId,
      channel: $channel,
      releaseIssue: ($ev.meta.releaseIssue // $base.releaseIssue // null),
      candidate: {
        ref: (if $serverRef != "" then $serverRef else $base.candidate.ref end),
        refSource: (if $refSource != "" then $refSource else ($base.candidate.refSource // null) end),
        image: {
          repository: (if $imageRepo != "" then $imageRepo else ($base.candidate.image.repository // null) end),
          tag: (if $imageTag != "" then $imageTag else null end),
          digest: (if $imageDigest != "" then $imageDigest else null end),
          evidenceState: (if $imageReady then "passed" else "blocked" end),
          blockers: (if $imageReady then [] else
            [ { repo: "honua-io/honua-devops", number: 41,
                url: "https://github.com/honua-io/honua-devops/issues/41",
                reason: "Release-candidate image tag and digest are not yet published." } ] end)
        }
      },
      sdkCompatibilityManifest: ($ev.meta.sdkCompatibilityManifest // $base.sdkCompatibilityManifest // "docs/developer/sdk-compatibility-versions.json"),
      compatibilityBaseline: ($ev.meta.compatibilityBaseline // $base.compatibilityBaseline // null),
      observedAt: $observedAt,
      releaseGates: (
        if ($ev.releaseGates != null) then [ $ev.releaseGates[] | to_record(.) ]
        else ($base.releaseGates // []) end
      ),
      repositoryLanes: (
        if ($ev.repositoryLanes != null) then [ $ev.repositoryLanes[] | to_record(.) ]
        else ($base.repositoryLanes // []) end
      ),
      releaseLaneCriteria: (
        if ($ev.releaseLaneCriteria != null) then [ $ev.releaseLaneCriteria[] | to_criterion(.) ]
        else ($base.releaseLaneCriteria // []) end
      ),
      childTickets: ($ev.meta.childTickets // $base.childTickets // []),
      missingBoundedChildTickets: ($ev.meta.missingBoundedChildTickets // $base.missingBoundedChildTickets // []),
      waivers: ($ev.waivers // $base.waivers // [])
    }
    # Drop top-level null optionals for a clean manifest.
    | with_entries(select(.value != null))
  '
)"

mkdir -p "$(dirname "$OUTPUT")"
printf '%s\n' "$manifest" >"$OUTPUT"

# Human summary.
verdict_gates="$(jq -r '[.releaseGates[],.repositoryLanes[],.releaseLaneCriteria[]] | map(select(.evidenceState=="passed")) | length' <<<"$manifest")"
total="$(jq -r '[.releaseGates[],.repositoryLanes[],.releaseLaneCriteria[]] | length' <<<"$manifest")"
image_state="$(jq -r '.candidate.image.evidenceState' <<<"$manifest")"
echo "[INFO] wrote $OUTPUT"
echo "[INFO] release=${RELEASE_ID} channel=${CHANNEL} candidate.image=${image_state}"
echo "[INFO] signals passed: ${verdict_gates}/${total} (gates+lanes+criteria)"
