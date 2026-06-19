#!/usr/bin/env bash
# Demo B — flagship safe-rollback sequence (Beat 8).
#
# Drives the AI-DevOps safe layer-evolution loop end to end against a running
# Honua server: submit an additive layer change → the post-publish Smoke health
# gate fails (deterministically injected) → the deploy rolls back safely (metadata
# revision reactivated + reversible down-script reverts the schema) → the script
# emits the same JSON the DevOps AI `inspect_metadata_release` tool reads so the
# agent can detect, diagnose, and propose a human-approved resolve.
#
# The L3 E2E harness owns the pass/fail ASSERTIONS; this script owns the SEQUENCE
# and prints a machine-readable detect summary the harness can assert on.
#
# Prerequisites:
#   * A Honua server reachable at $BASE with Redis-backed durable op storage.
#   * Fault injection ARMED for the target environment (demo only):
#       ControlPlane__MetadataRelease__FaultInjection__Enabled=true
#       ControlPlane__MetadataRelease__FaultInjection__ForceSmokeFailure=true
#       ControlPlane__MetadataRelease__FaultInjection__AllowedEnvironments__0=staging
#   * $HONUA_DEMO_API_KEY exported (admin X-API-Key). Never echoed by this script.
#
# Env (all optional, with safe demo defaults):
#   BASE                 default http://localhost:8080
#   TARGET_ENVIRONMENT   default staging   (MUST be on the fault-injection allow-list)
#   PACKAGE_ID           default demo-b-add-owner-email
#   RESOURCE_ID          default maui-parcels
#   NEW_FIELD            default owner_email
#   NEW_FIELD_TYPE       default String
#   ETL_WORKLOAD         default populate-owner-email   (empty to skip ETL populate)
#   POLL_ATTEMPTS        default 40
#   POLL_INTERVAL_SECS   default 3
#
# Exit codes: 0 = safe rollback detected (metadata + DB), 2 = unexpected terminal
# state, 3 = never reached terminal, 4 = submit failed, 5 = missing prerequisites.

set -euo pipefail

BASE="${BASE:-http://localhost:8080}"
TARGET_ENVIRONMENT="${TARGET_ENVIRONMENT:-staging}"
PACKAGE_ID="${PACKAGE_ID:-demo-b-add-owner-email}"
RESOURCE_ID="${RESOURCE_ID:-maui-parcels}"
NEW_FIELD="${NEW_FIELD:-owner_email}"
NEW_FIELD_TYPE="${NEW_FIELD_TYPE:-String}"
ETL_WORKLOAD="${ETL_WORKLOAD:-populate-owner-email}"
POLL_ATTEMPTS="${POLL_ATTEMPTS:-40}"
POLL_INTERVAL_SECS="${POLL_INTERVAL_SECS:-3}"

if ! command -v jq >/dev/null 2>&1; then
  echo "FATAL: jq is required." >&2
  exit 5
fi
if [[ -z "${HONUA_DEMO_API_KEY:-}" ]]; then
  echo "FATAL: export HONUA_DEMO_API_KEY (admin X-API-Key) first." >&2
  exit 5
fi

AUTH=(-H "X-API-Key: ${HONUA_DEMO_API_KEY}")
OP_URL="${BASE}/api/v1/admin/metadata/releases"

echo "== Demo B safe-rollback sequence =="
echo "base=${BASE} target=${TARGET_ENVIRONMENT} package=${PACKAGE_ID} resource=${RESOURCE_ID} field=${NEW_FIELD}"

# ---- 1. Submit the additive layer change (+ optional ETL) --------------------
populate_field=""
if [[ -n "${ETL_WORKLOAD}" ]]; then
  populate_field=", \"dataPopulateWorkloadId\": \"${ETL_WORKLOAD}\""
fi

submit_body=$(cat <<JSON
{
  "packageId": "${PACKAGE_ID}",
  "targetEnvironment": "${TARGET_ENVIRONMENT}",
  "resourceSemanticId": "${RESOURCE_ID}",
  "newFieldName": "${NEW_FIELD}",
  "newFieldType": "${NEW_FIELD_TYPE}"${populate_field},
  "reason": "Demo B: additive layer evolution with reversible rollback",
  "idempotencyKey": "${PACKAGE_ID}"
}
JSON
)

echo "-- submit POST ${OP_URL}/operations"
submit_resp=$(curl -s -w '\n%{http_code}' -X POST "${OP_URL}/operations" \
  "${AUTH[@]}" -H 'Content-Type: application/json' -d "${submit_body}")
submit_code=$(tail -n1 <<<"${submit_resp}")
submit_json=$(sed '$d' <<<"${submit_resp}")

if [[ "${submit_code}" != "201" && "${submit_code}" != "200" ]]; then
  echo "FATAL: submit failed (HTTP ${submit_code}): ${submit_json}" >&2
  exit 4
fi

operation_id=$(jq -r '.operationId // empty' <<<"${submit_json}")
echo "submitted operationId=${operation_id} status=$(jq -r '.status // "?"' <<<"${submit_json}")"

# ---- 2. Poll the lifecycle until terminal (reconciles on read) ---------------
terminal_json=""
for ((attempt = 1; attempt <= POLL_ATTEMPTS; attempt++)); do
  op_json=$(curl -s "${OP_URL}/${PACKAGE_ID}/operation" "${AUTH[@]}")
  status=$(jq -r '.status // "unknown"' <<<"${op_json}")
  stage=$(jq -r '.metadataRelease.currentStage // "unknown"' <<<"${op_json}")
  echo "  poll ${attempt}/${POLL_ATTEMPTS}: status=${status} stage=${stage}"
  case "${status}" in
    Succeeded|Failed|RolledBack|ManualInterventionRequired)
      terminal_json="${op_json}"; break ;;
  esac
  sleep "${POLL_INTERVAL_SECS}"
done

if [[ -z "${terminal_json}" ]]; then
  echo "FATAL: operation never reached a terminal state." >&2
  exit 3
fi

# ---- 3. Emit the detect summary the AI / harness asserts on ------------------
detect=$(jq '{
  operationId: .operationId,
  detected_rolled_back: (.status == "RolledBack"),
  status: .status,
  stage: .metadataRelease.currentStage,
  health_gate_ran: ([.metadataRelease.evidenceRefs[]?.kind] | index("smoke") != null),
  db_inclusive_revert: ((.currentPhase // "") | test("Reversible rollback complete")),
  rollback_class: (.metadataRelease.rollbackPlan.class // "unclassified"),
  current_phase: .currentPhase
}' <<<"${terminal_json}")

echo "== detect summary (same fields the inspect_metadata_release tool reads) =="
echo "${detect}"

final_status=$(jq -r '.status' <<<"${detect}")
rolled_back=$(jq -r '.detected_rolled_back' <<<"${detect}")
db_revert=$(jq -r '.db_inclusive_revert' <<<"${detect}")
gate_ran=$(jq -r '.health_gate_ran' <<<"${detect}")

if [[ "${rolled_back}" == "true" && "${db_revert}" == "true" && "${gate_ran}" == "true" ]]; then
  echo "RESULT: SAFE ROLLBACK CONFIRMED — health gate fired, metadata + DB reverted."
  echo "Next: run the DevOps agent to diagnose + propose a human-approved resolve:"
  echo "  (honua-devops) dotnet run --project src/Honua.DevOps.Agent -- \\"
  echo "    --prompt \"inspect the metadata release for package ${PACKAGE_ID} and propose a resolve\""
  exit 0
fi

echo "RESULT: unexpected terminal state '${final_status}' (expected RolledBack with DB revert)." >&2
echo "Hint: is fault injection ARMED for target '${TARGET_ENVIRONMENT}'? It is off by default." >&2
exit 2
