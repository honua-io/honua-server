#!/usr/bin/env bash

# Self-test for scripts/release/build-release-manifest.sh.
#
# Verifies the generator emits a schema-faithful release-train manifest, that the
# image digest gates candidate.image, that CI conclusions map to evidenceState,
# that output is deterministic, and (when available) that the produced manifest
# round-trips through honua-devops' compat-train-release-validation.sh — proving
# the producer/consumer contract holds end to end.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
GEN="$SCRIPT_DIR/build-release-manifest.sh"

command -v jq >/dev/null 2>&1 || { echo "[ERROR] jq required" >&2; exit 1; }

WORKDIR="$(mktemp -d)"
trap 'rm -rf "$WORKDIR"' EXIT

cat >"$WORKDIR/evidence.json" <<'EOF'
{
  "meta": {
    "releaseIssue": { "repo": "honua-io/honua-server", "number": 876,
      "url": "https://github.com/honua-io/honua-server/issues/876" },
    "childTickets": [
      { "repo": "honua-io/honua-devops", "number": 41,
        "url": "https://github.com/honua-io/honua-devops/issues/41",
        "scope": "Release-candidate validation surface.", "state": "open" }
    ]
  },
  "releaseGates": [
    { "id": "server-sdk-compatibility", "name": "SDK Server Compatibility",
      "owningRepo": "honua-io/honua-server", "tier": "nightly",
      "workflow": ".github/workflows/sdk-server-compatibility.yml",
      "requirement": "Green for the release-candidate image.",
      "conclusion": "success", "runUrl": "https://github.com/honua-io/honua-server/actions/runs/1",
      "artifact": "sdk-compatibility-matrix-1", "headSha": "deadbeef" },
    { "id": "server-real-client-interop", "name": "Real-Client Interop Matrix",
      "owningRepo": "honua-io/honua-server", "tier": "nightly",
      "requirement": "Zero baseline regressions.",
      "conclusion": "success", "runUrl": "https://github.com/honua-io/honua-server/actions/runs/2" },
    { "id": "server-esri-sdk-certification", "name": "Esri SDK Certification",
      "owningRepo": "honua-io/honua-esri-compat", "tier": "nightly",
      "requirement": "License-free Esri SDKs certify against the candidate image.",
      "conclusion": "success", "runUrl": "https://github.com/honua-io/honua-esri-compat/actions/runs/9" },
    { "id": "server-security-nightly", "name": "Security Nightly",
      "owningRepo": "honua-io/honua-server", "tier": "nightly",
      "conclusion": "failure", "runUrl": "https://github.com/honua-io/honua-server/actions/runs/3",
      "blockers": [ { "repo": "honua-io/honua-server", "number": 939,
        "url": "https://github.com/honua-io/honua-server/issues/939", "reason": "Security nightly failing." } ] }
  ],
  "repositoryLanes": [
    { "id": "server-integration-tests", "name": "Server integration tests",
      "owningRepo": "honua-io/honua-server",
      "requirement": "ci.yml integration suite green for the candidate.",
      "conclusion": "success", "runUrl": "https://github.com/honua-io/honua-server/actions/runs/10" },
    { "id": "sdk-js-trunk-ci", "owningRepo": "honua-io/honua-sdk-js",
      "conclusion": "success", "runUrl": "https://github.com/honua-io/honua-sdk-js/actions/runs/4" },
    { "id": "sdk-python-staging-integration", "owningRepo": "honua-io/honua-sdk-python",
      "conclusion": "failure",
      "blockers": [ { "repo": "honua-io/honua-sdk-python", "number": 50,
        "url": "https://github.com/honua-io/honua-sdk-python/issues/50", "reason": "Staging integration failing." } ] },
    { "id": "mobile-dotnet-sdk-train", "owningRepo": "honua-io/honua-mobile",
      "conclusion": "success", "runUrl": "https://github.com/honua-io/honua-mobile/actions/runs/5" },
    { "id": "admin-docs", "owningRepo": "honua-io/honua-console",
      "conclusion": "success", "runUrl": "https://github.com/honua-io/honua-console/actions/runs/6" },
    { "id": "helm-rc-metadata", "owningRepo": "honua-io/honua-helm",
      "conclusion": "success", "runUrl": "https://github.com/honua-io/honua-helm/actions/runs/7" },
    { "id": "terraform-modules", "owningRepo": "honua-io/honua-iac",
      "conclusion": "success", "runUrl": "https://github.com/honua-io/honua-iac/actions/runs/8" }
  ],
  "releaseLaneCriteria": [
    { "id": "release-candidate-image-compatibility",
      "requirement": "Cross-repo compatibility train passes against the RC image.",
      "conclusion": "success" },
    { "id": "release-evidence-pack",
      "requirement": "Manifest, smoke results, and feature map attached.",
      "conclusion": "success",
      "evidence": { "manifest": "release/honua-test-rc.json" } }
  ]
}
EOF

COMMON_ARGS=(
  --evidence "$WORKDIR/evidence.json"
  --release-id "honua-test-rc"
  --channel "preview"
  --server-ref "deadbeefcafe"
  --ref-source "trunk"
  --image-repository "ghcr.io/honua-io/honua-server"
  --observed-at "2026-06-07T00:00:00Z"
)

echo "Validating: with an image digest, candidate.image is passed and schema is faithful"
"$GEN" "${COMMON_ARGS[@]}" \
  --image-tag "rc-honua-test-rc-deadbee" \
  --image-digest "sha256:1111111111111111111111111111111111111111111111111111111111111111" \
  --output "$WORKDIR/manifest-ready.json" >/dev/null

jq -e '.candidate.image.evidenceState == "passed"' "$WORKDIR/manifest-ready.json" >/dev/null \
  || { echo "[ERROR] candidate.image should be passed when digest present" >&2; exit 1; }
jq -e '.schemaVersion == 1 and .releaseId == "honua-test-rc" and .channel == "preview"' "$WORKDIR/manifest-ready.json" >/dev/null \
  || { echo "[ERROR] top-level identity fields wrong" >&2; exit 1; }
jq -e '.candidate.ref == "deadbeefcafe" and .candidate.image.digest != null' "$WORKDIR/manifest-ready.json" >/dev/null \
  || { echo "[ERROR] candidate ref/digest not carried" >&2; exit 1; }
jq -e '.releaseIssue.number == 876 and (.childTickets|length==1)' "$WORKDIR/manifest-ready.json" >/dev/null \
  || { echo "[ERROR] meta (releaseIssue/childTickets) not carried from evidence" >&2; exit 1; }
echo "[OK] image-ready manifest generated"

echo
echo "Validating: CI conclusions map to evidenceState and blockers are preserved"
jq -e '.releaseGates[] | select(.id=="server-sdk-compatibility") | .evidenceState=="passed" and .latestEvidence.conclusion=="success"' "$WORKDIR/manifest-ready.json" >/dev/null \
  || { echo "[ERROR] success gate not mapped to passed" >&2; exit 1; }
jq -e '.releaseGates[] | select(.id=="server-security-nightly") | .evidenceState=="blocked" and (.blockers[0].url|test("939"))' "$WORKDIR/manifest-ready.json" >/dev/null \
  || { echo "[ERROR] failing gate not mapped to blocked with blocker" >&2; exit 1; }
jq -e '.releaseGates[] | select(.id=="server-esri-sdk-certification") | .evidenceState=="passed" and .owningRepo=="honua-io/honua-esri-compat"' "$WORKDIR/manifest-ready.json" >/dev/null \
  || { echo "[ERROR] esri-sdk-certification gate missing/wrong" >&2; exit 1; }
echo "[OK] conclusion->evidenceState mapping correct"

echo
echo "Validating: without a digest, candidate.image is blocked with the #41 follow-up"
"$GEN" "${COMMON_ARGS[@]}" --output "$WORKDIR/manifest-noimage.json" >/dev/null
jq -e '.candidate.image.evidenceState == "blocked" and (.candidate.image.blockers[0].url|test("honua-devops/issues/41"))' "$WORKDIR/manifest-noimage.json" >/dev/null \
  || { echo "[ERROR] missing-digest manifest should block candidate.image with #41" >&2; exit 1; }
echo "[OK] missing-digest manifest blocks candidate.image"

echo
echo "Validating: output is deterministic for identical inputs"
"$GEN" "${COMMON_ARGS[@]}" \
  --image-tag "rc-honua-test-rc-deadbee" \
  --image-digest "sha256:1111111111111111111111111111111111111111111111111111111111111111" \
  --output "$WORKDIR/manifest-ready-2.json" >/dev/null
diff -q "$WORKDIR/manifest-ready.json" "$WORKDIR/manifest-ready-2.json" >/dev/null \
  || { echo "[ERROR] generator output is not deterministic" >&2; exit 1; }
echo "[OK] deterministic output"

echo
echo "Validating: emitted manifest is well-formed JSON with required v1 keys"
jq -e '.["$schema"] and .schemaVersion and .candidate and .releaseGates and .repositoryLanes and .releaseLaneCriteria and .observedAt and (.waivers|type=="array")' "$WORKDIR/manifest-ready.json" >/dev/null \
  || { echo "[ERROR] manifest missing required keys" >&2; exit 1; }
echo "[OK] manifest shape valid"

# Round-trip through the honua-devops validator if it is checked out as a sibling.
VALIDATOR="$SCRIPT_DIR/../../../honua-devops/scripts/compat-train-release-validation.sh"
if [[ -x "$VALIDATOR" ]]; then
  echo
  echo "Validating: produced manifest round-trips through honua-devops validator (producer<->consumer)"
  COMPAT_TRAIN_MODE=advisory \
  COMPAT_TRAIN_REQUIRED_SURFACES="server sdk admin helm terraform" \
  COMPAT_TRAIN_BUNDLE_OUTPUT="$WORKDIR/bundle.json" \
    "$VALIDATOR" "$WORKDIR/manifest-ready.json" >/dev/null
  jq -e '.verdict and (.summary.surfacesCovered|index("server")) and (.summary.surfacesCovered|index("sdk")) and (.summary.surfacesCovered|index("helm")) and (.summary.surfacesCovered|index("terraform"))' "$WORKDIR/bundle.json" >/dev/null \
    || { echo "[ERROR] validator did not recognize surfaces from the generated manifest" >&2; exit 1; }
  echo "[OK] validator consumed the generated manifest (verdict: $(jq -r .verdict "$WORKDIR/bundle.json"))"
else
  echo
  echo "[SKIP] honua-devops validator not found as sibling; skipping round-trip check"
fi

echo
echo "build-release-manifest smoke check passed."
