#!/usr/bin/env bash
# Run both bounded-roster verifiers and return their combined status.
# Arguments: evidence-dir source-sha image-digest cut-at producer-sha
record_verdicts() {
  local evidence="$1" source_sha="$2" image_digest="$3" cut_at="$4" producer_sha="$5"
  local release_status contract_status
  mkdir -p "$evidence"
  set +e
  python3 scripts/certification/verify-client-certification-receipts.py --mode release \
    --receipts "$evidence/receipts" --source-sha "$source_sha" --image-digest "$image_digest" \
    --cut-at "$cut_at" --producer-source-sha "$producer_sha" \
    --output "$evidence/verdict.release.json" | tee "$evidence/verdict.release.txt"
  release_status=$?
  python3 scripts/certification/verify-client-certification-receipts.py \
    --output "$evidence/verdict.contract.json" > "$evidence/verdict.contract.txt"
  contract_status=$?
  set -e
  if [[ "$release_status" -ne 0 || "$contract_status" -ne 0 ]]; then
    echo "client certification verdict failed: release=${release_status} contract=${contract_status}" >&2
    return 1
  fi
}
