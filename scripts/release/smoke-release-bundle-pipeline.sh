#!/usr/bin/env bash

# End-to-end smoke for the release-bundle data pipeline:
#   bundle-suites.json + per-suite results
#     -> collect-evidence.sh -> build-release-manifest.sh -> (validator)
# Proves the registry, the evidence merge, and the manifest generator stay
# mutually consistent and that the produced manifest is consumable downstream.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
REGISTRY="$REPO_ROOT/eng/release/bundle-suites.v1.json"
COLLECT="$SCRIPT_DIR/collect-evidence.sh"
GEN="$SCRIPT_DIR/build-release-manifest.sh"

command -v jq >/dev/null 2>&1 || { echo "[ERROR] jq required" >&2; exit 1; }
[[ -f "$REGISTRY" ]] || { echo "[ERROR] registry not found: $REGISTRY" >&2; exit 1; }

W="$(mktemp -d)"; trap 'rm -rf "$W"' EXIT

echo "Validating: registry is well-formed and references known modes"
jq -e '.integration and .sdk and .repositoryLanes' "$REGISTRY" >/dev/null \
  || { echo "[ERROR] registry missing required sections" >&2; exit 1; }
bad_mode="$(jq -r '[.integration[].mode] | map(select(. as $m | ["dispatch","dispatch-cross-repo","build-own-image","trunk-status"] | index($m) | not)) | length' "$REGISTRY")"
[[ "$bad_mode" == "0" ]] || { echo "[ERROR] registry has unknown integration mode(s)" >&2; exit 1; }
echo "[OK] registry valid"

# Synthesize a green result for every dispatchable suite in the registry.
: > "$W/integration.ndjson"
jq -r '.integration[].id' "$REGISTRY" | while read -r id; do
  jq -nc --arg id "$id" '{id:$id, conclusion:"success", runUrl:("https://example/"+$id)}' >> "$W/integration.ndjson"
done
: > "$W/sdk.ndjson"
jq -r '.sdk[].id' "$REGISTRY" | while read -r id; do
  jq -nc --arg id "$id" '{id:$id, conclusion:"success"}' >> "$W/sdk.ndjson"
done

echo
echo "Validating: collect-evidence merges registry + results with consistent counts"
"$COLLECT" --registry "$REGISTRY" --integration "$W/integration.ndjson" --sdk "$W/sdk.ndjson" --output "$W/evidence.json" >/dev/null
exp_gates="$(jq -r '[.integration[] | select(.id|startswith("server-"))] | length' "$REGISTRY")"
got_gates="$(jq -r '.releaseGates | length' "$W/evidence.json")"
[[ "$exp_gates" == "$got_gates" ]] || { echo "[ERROR] releaseGates count $got_gates != registry server-* $exp_gates" >&2; exit 1; }
exp_lanes="$(jq -r '(.sdk|length) + (.repositoryLanes|length) + ([.integration[]|select(.id|startswith("server-")|not)]|length)' "$REGISTRY")"
got_lanes="$(jq -r '.repositoryLanes | length' "$W/evidence.json")"
[[ "$exp_lanes" == "$got_lanes" ]] || { echo "[ERROR] repositoryLanes count $got_lanes != registry sdk+lanes $exp_lanes" >&2; exit 1; }
echo "[OK] evidence merged (gates=$got_gates lanes=$got_lanes)"


echo
echo "Validating: a nested preview SDK profile cannot be masked by another profile"
for profiles in \
  '{"profile":"preview-http-baseseed"}' \
  '{"receipt":{"profile":"preview-http-baseseed"}}' \
  '{"evidence_receipt":{"profile":"preview-http-baseseed"}}' \
  '{"profile":"release","receipt":{"profile":"preview-http-baseseed"}}' \
  '{"profile":"release","evidence_receipt":{"profile":"preview-http-baseseed"}}' \
  '{"receipt":{"profile":"release"},"evidence_receipt":{"profile":"preview-http-baseseed"}}' \
  '{"profile":"","receipt":{"profile":"preview-http-baseseed"}}'; do
  jq -nc --argjson profiles "$profiles" \
    '{id:"sdk-profile-negative-control",conclusion:"success"} + $profiles' > "$W/preview.ndjson"
  if "$COLLECT" --registry "$REGISTRY" --sdk "$W/preview.ndjson" \
    --output "$W/preview-evidence.json" >"$W/preview.log" 2>&1; then
    echo "[ERROR] preview-profile SDK receipt accepted: $profiles" >&2
    exit 1
  fi
  if ! grep -q 'preview-profile SDK receipt' "$W/preview.log"; then
    echo "[ERROR] SDK profile rejection did not report the preview profile" >&2
    exit 1
  fi
done
jq -nc '{id:"sdk-profile-positive-control",conclusion:"success",profile:"release",
         receipt:{profile:"release"},evidence_receipt:{profile:"release"}}' > "$W/release.ndjson"
"$COLLECT" --registry "$REGISTRY" --sdk "$W/release.ndjson" \
  --output "$W/release-evidence.json" >/dev/null
echo "[OK] every preview SDK profile location is rejected, including mixed profiles"

echo
echo "Validating: duplicate suite receipts are rejected in either order"
for kind in integration sdk; do
  suite_id="$(jq -r --arg kind "$kind" '.[$kind][0].id' "$REGISTRY")"
  for outcomes in '["failure","success"]' '["success","failure"]' '["success","success"]'; do
    jq -nc --arg id "$suite_id" --argjson outcomes "$outcomes" \
      '$outcomes[] | {id:$id, conclusion:.}' > "$W/duplicate.ndjson"
    if "$COLLECT" --registry "$REGISTRY" "--$kind" "$W/duplicate.ndjson" \
      --output "$W/duplicate-evidence.json" >"$W/duplicate.log" 2>&1; then
      echo "[ERROR] duplicate $kind suite receipts accepted: $outcomes" >&2
      exit 1
    fi
    if ! grep -q 'Duplicate suite receipt id' "$W/duplicate.log"; then
      echo "[ERROR] duplicate receipt rejection did not identify ambiguity" >&2
      exit 1
    fi
  done
done
echo "[OK] duplicate integration and SDK receipts are rejected"

echo
echo "Validating: manifest builds from the merged evidence with a passed candidate image"
"$GEN" --evidence "$W/evidence.json" \
  --release-id honua-pipeline-test --channel preview --server-ref abc123 \
  --image-repository ghcr.io/honua-io/honua-server \
  --image-tag rc-honua-pipeline-test-abc123 \
  --image-digest "sha256:3333333333333333333333333333333333333333333333333333333333333333" \
  --observed-at 2026-06-07T00:00:00Z --output "$W/manifest.json" >/dev/null
jq -e '.candidate.image.evidenceState=="passed"' "$W/manifest.json" >/dev/null \
  || { echo "[ERROR] candidate.image should be passed" >&2; exit 1; }
jq -e '[.releaseGates[]|select(.evidenceState=="passed")]|length == '"$exp_gates" "$W/manifest.json" >/dev/null \
  || { echo "[ERROR] all server gates should be passed for all-green input" >&2; exit 1; }
echo "[OK] manifest built; all server gates passed"

# Optional downstream consume check.
VALIDATOR="$REPO_ROOT/../honua-devops/scripts/compat-train-release-validation.sh"
if [[ -x "$VALIDATOR" ]]; then
  echo
  echo "Validating: validator covers server+sdk; manual admin/helm/terraform lanes are the only gaps"
  COMPAT_TRAIN_MODE=advisory COMPAT_TRAIN_BUNDLE_OUTPUT="$W/bundle.json" \
    "$VALIDATOR" "$W/manifest.json" >/dev/null
  jq -e '(.summary.surfacesCovered|index("server")) and (.summary.surfacesCovered|index("sdk"))' "$W/bundle.json" >/dev/null \
    || { echo "[ERROR] server+sdk surfaces should be covered" >&2; jq '.summary' "$W/bundle.json"; exit 1; }
  # The only blocked checks should be the manual lanes (admin/helm/terraform) not yet wired.
  unexpected="$(jq -r '[.checks[] | select(.state=="blocked") | select(.surface as $s | ["admin","helm","terraform"] | index($s) | not)] | length' "$W/bundle.json")"
  [[ "$unexpected" == "0" ]] \
    || { echo "[ERROR] unexpected blocked checks beyond manual lanes" >&2; jq '[.checks[]|select(.state=="blocked")|{id,surface}]' "$W/bundle.json"; exit 1; }
  echo "[OK] server+sdk covered; only manual lanes pending (expected foundation state)"
else
  echo "[SKIP] honua-devops validator not a sibling; skipping downstream consume check"
fi

echo
echo "release-bundle pipeline smoke check passed."
