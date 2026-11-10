#!/usr/bin/env bash
# Run both bounded-roster verifiers and return their combined status.
# Arguments: evidence-dir candidate
# candidate names an associative array with source_sha, image_digest, cut_at
# and producer_sha.
record_verdicts() {
  local evidence="$1"
  local -n verdict_candidate="$2"
  local source_sha="${verdict_candidate[source_sha]}"
  local image_digest="${verdict_candidate[image_digest]}"
  local cut_at="${verdict_candidate[cut_at]}"
  local producer_sha="${verdict_candidate[producer_sha]}"
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
