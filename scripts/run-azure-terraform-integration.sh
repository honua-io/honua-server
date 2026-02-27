#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(dirname "$SCRIPT_DIR")"

STACK="both"
LOCATION="${AZURE_LOCATION:-westus}"
ENVIRONMENT="${AZURE_TF_ENVIRONMENT:-it}"
NAME_PREFIX_BASE="${AZURE_TF_NAME_PREFIX_BASE:-hnu$(date -u +%m%d%H%M)}"
ACA_IMAGE="${HONUA_ACA_IMAGE:-ghcr.io/honua-io/honua-server:latest}"
FUNCTIONS_IMAGE="${HONUA_FUNCTIONS_IMAGE:-ghcr.io/honua-io/honua-server:latest}"
ACA_PREVIOUS_IMAGE="${HONUA_ACA_PREVIOUS_IMAGE:-}"
FUNCTIONS_PREVIOUS_IMAGE="${HONUA_FUNCTIONS_PREVIOUS_IMAGE:-}"
FUNCTIONS_PLAN_SKU="${HONUA_FUNCTIONS_PLAN_SKU:-EP1}"
AUTO_DESTROY=true
QUICK_SCALE=true
CHECK_IDEMPOTENCY=true
CHECK_PROTOCOLS=true
RUN_DB_RESILIENCE=true
RUN_UPGRADE_ROLLBACK=false
RUN_QUOTA_PREFLIGHT=true
TIMEOUT_SECONDS="${HONUA_AZURE_TEST_TIMEOUT_SECONDS:-900}"
LOAD_REQUESTS="${HONUA_AZURE_LOAD_REQUESTS:-120}"
LOAD_CONCURRENCY="${HONUA_AZURE_LOAD_CONCURRENCY:-20}"
ACA_MIN_REPLICAS=1
ACA_MAX_REPLICAS=3
ACA_SCALE_TARGET_MIN_REPLICAS=2
READY_SLO_SECONDS="${HONUA_READY_SLO_SECONDS:-600}"
MAX_LOAD_ERROR_RATE_PERCENT="${HONUA_MAX_LOAD_ERROR_RATE_PERCENT:-0}"
MAX_RUN_COST_USD="${HONUA_MAX_RUN_COST_USD:-0}"
TF_IMAGE="${HONUA_TERRAFORM_IMAGE:-honua-terraform-psql:1.8.5}"
AZ_CLI_IMAGE="${HONUA_AZ_CLI_IMAGE:-mcr.microsoft.com/azure-cli:2.65.0}"
PLAN_ARTIFACT_DIR="${HONUA_TF_PLAN_ARTIFACT_DIR:-}"
ALLOW_DESTROY_PLAN="${HONUA_ALLOW_DESTROY_PLAN:-false}"
TTL_HOURS="${HONUA_TTL_HOURS:-8}"
VALIDATION_RUN_ID="${HONUA_VALIDATION_RUN_ID:-az-$(date -u +%Y%m%d%H%M%S)}"
DB_FIREWALL_START_IP="${HONUA_AZURE_DB_FIREWALL_START_IP:-}"
DB_FIREWALL_END_IP="${HONUA_AZURE_DB_FIREWALL_END_IP:-}"
EXISTING_DB_FQDN="${HONUA_AZURE_EXISTING_DB_FQDN:-}"
EXISTING_DB_CONNECTION_STRING="${HONUA_AZURE_EXISTING_DB_CONNECTION_STRING:-}"
EXISTING_REDIS_CONNECTION_STRING="${HONUA_AZURE_EXISTING_REDIS_CONNECTION_STRING:-}"
AUTO_PROVISION_DATA_STACK=true
DATA_DB_SKU_NAME="${HONUA_AZURE_DATA_DB_SKU_NAME:-B_Standard_B1ms}"
DATA_DB_STORAGE_MB="${HONUA_AZURE_DATA_DB_STORAGE_MB:-32768}"
DATA_DB_PUBLIC_NETWORK_ACCESS="${HONUA_AZURE_DATA_DB_PUBLIC_NETWORK_ACCESS:-true}"
DATA_REDIS_SKU_NAME="${HONUA_AZURE_DATA_REDIS_SKU_NAME:-Basic}"
DATA_REDIS_FAMILY="${HONUA_AZURE_DATA_REDIS_FAMILY:-C}"
DATA_REDIS_CAPACITY="${HONUA_AZURE_DATA_REDIS_CAPACITY:-0}"
DATA_REDIS_PUBLIC_NETWORK_ACCESS_ENABLED="${HONUA_AZURE_DATA_REDIS_PUBLIC_NETWORK_ACCESS_ENABLED:-true}"

TEMP_TF_ROOT=""
DATA_APPLIED=false
ACA_APPLIED=false
FUNCTIONS_APPLIED=false

DATA_NAME_PREFIX=""
ACA_NAME_PREFIX=""
FUNCTIONS_NAME_PREFIX=""
DATA_RESOURCE_GROUP=""
EXPIRES_AT_UTC=""

usage() {
  cat <<USAGE
Run live Terraform integration tests for Azure ACA and Azure Functions.

Usage:
  ./scripts/run-azure-terraform-integration.sh [options]

When existing DB/Redis settings are not provided, the script provisions 'examples/azure-data'
first and then feeds those outputs into ACA/Functions validation applies.

Options:
  --stack <aca|functions|both>        Stack to test (default: both)
  --location <azure-region>           Azure region (default: westus)
  --environment <name>                Environment suffix in names (default: it)
  --name-prefix-base <prefix>         Base prefix for generated resource names
  --aca-image <image>                 ACA image tag
  --functions-image <image>           Functions image tag
  --aca-previous-image <image>        Previous ACA image for upgrade/rollback validation
  --functions-previous-image <image>  Previous Functions image for upgrade/rollback validation
  --upgrade-rollback                  Enable upgrade/rollback validation sequence
  --functions-plan <EP1|EP2|EP3|Y1>   Functions plan SKU (default: EP1)
  --timeout-seconds <n>               Health wait timeout per stack (default: 900)
  --max-ready-seconds <n>             Ready SLO threshold (default: 600)
  --max-load-error-rate <percent>     Max allowed load error rate (default: 0)
  --max-run-cost-usd <n>              Max allowed estimated run cost (0 disables cap)
  --data-db-sku <name>                Azure data stack PostgreSQL SKU (default: B_Standard_B1ms)
  --data-db-storage-mb <n>            Azure data stack PostgreSQL storage in MB (default: 32768)
  --data-redis-sku <Basic|Standard>   Azure data stack Redis SKU (default: Basic)
  --data-redis-family <char>          Azure data stack Redis family (default: C)
  --data-redis-capacity <n>           Azure data stack Redis capacity (default: 0)
  --existing-db-fqdn <fqdn>           Reuse existing PostgreSQL server FQDN
  --existing-db-connection <string>   Reuse existing PostgreSQL connection string
  --existing-redis-connection <str>   Reuse existing Redis connection string
  --plan-artifact-dir <path>          Directory to persist plan artifacts
  --allow-destroy-plan                Allow plans containing resource destroys
  --ttl-hours <n>                     TTL tag value for provisioned resources (default: 8)
  --skip-quota-preflight              Skip Azure quota preflight checks
  --skip-idempotency                  Skip post-apply zero-drift plan assertion
  --skip-protocol-checks              Skip REST/OGC/OData/admin auth + admin CRUD/query smoke checks
  --skip-db-resilience                Skip DB backup/restore drill
  --no-scale-check                    Skip quick ACA scale check
  --no-destroy                        Keep resources after test run
  --help, -h                          Show this help

Required environment variables:
  ARM_CLIENT_ID
  ARM_CLIENT_SECRET
  ARM_TENANT_ID
  ARM_SUBSCRIPTION_ID
  HONUA_ADMIN_PASSWORD
  HONUA_DB_PASSWORD
USAGE
}

log_info() {
  echo "[INFO] $1"
}

log_warn() {
  echo "[WARN] $1"
}

log_error() {
  echo "[ERROR] $1" >&2
}

require_command() {
  if ! command -v "$1" >/dev/null 2>&1; then
    log_error "Required command not found: $1"
    exit 1
  fi
}

require_env() {
  local name
  for name in "$@"; do
    if [[ -z "${!name:-}" ]]; then
      log_error "Missing required environment variable: $name"
      exit 1
    fi
  done
}

normalize_identifiers() {
  ENVIRONMENT="$(echo "$ENVIRONMENT" | tr '[:upper:]' '[:lower:]' | tr -cd 'a-z0-9-')"
  NAME_PREFIX_BASE="$(echo "$NAME_PREFIX_BASE" | tr '[:upper:]' '[:lower:]' | tr -cd 'a-z0-9')"

  if [[ -z "$ENVIRONMENT" || -z "$NAME_PREFIX_BASE" ]]; then
    log_error "Environment/name prefix became empty after normalization"
    exit 1
  fi

  NAME_PREFIX_BASE="${NAME_PREFIX_BASE:0:10}"
  DATA_NAME_PREFIX="${NAME_PREFIX_BASE}"
  ACA_NAME_PREFIX="${NAME_PREFIX_BASE}aca"
  FUNCTIONS_NAME_PREFIX="${NAME_PREFIX_BASE}fn"

  ACA_NAME_PREFIX="${ACA_NAME_PREFIX:0:20}"
  FUNCTIONS_NAME_PREFIX="${FUNCTIONS_NAME_PREFIX:0:20}"
}

parse_args() {
  while [[ $# -gt 0 ]]; do
    case "$1" in
      --stack)
        STACK="$2"
        shift 2
        ;;
      --location)
        LOCATION="$2"
        shift 2
        ;;
      --environment)
        ENVIRONMENT="$2"
        shift 2
        ;;
      --name-prefix-base)
        NAME_PREFIX_BASE="$2"
        shift 2
        ;;
      --aca-image)
        ACA_IMAGE="$2"
        shift 2
        ;;
      --functions-image)
        FUNCTIONS_IMAGE="$2"
        shift 2
        ;;
      --aca-previous-image)
        ACA_PREVIOUS_IMAGE="$2"
        shift 2
        ;;
      --functions-previous-image)
        FUNCTIONS_PREVIOUS_IMAGE="$2"
        shift 2
        ;;
      --upgrade-rollback)
        RUN_UPGRADE_ROLLBACK=true
        shift
        ;;
      --functions-plan)
        FUNCTIONS_PLAN_SKU="$2"
        shift 2
        ;;
      --timeout-seconds)
        TIMEOUT_SECONDS="$2"
        shift 2
        ;;
      --max-ready-seconds)
        READY_SLO_SECONDS="$2"
        shift 2
        ;;
      --max-load-error-rate)
        MAX_LOAD_ERROR_RATE_PERCENT="$2"
        shift 2
        ;;
      --max-run-cost-usd)
        MAX_RUN_COST_USD="$2"
        shift 2
        ;;
      --data-db-sku)
        DATA_DB_SKU_NAME="$2"
        shift 2
        ;;
      --data-db-storage-mb)
        DATA_DB_STORAGE_MB="$2"
        shift 2
        ;;
      --data-redis-sku)
        DATA_REDIS_SKU_NAME="$2"
        shift 2
        ;;
      --data-redis-family)
        DATA_REDIS_FAMILY="$2"
        shift 2
        ;;
      --data-redis-capacity)
        DATA_REDIS_CAPACITY="$2"
        shift 2
        ;;
      --existing-db-fqdn)
        EXISTING_DB_FQDN="$2"
        shift 2
        ;;
      --existing-db-connection)
        EXISTING_DB_CONNECTION_STRING="$2"
        shift 2
        ;;
      --existing-redis-connection)
        EXISTING_REDIS_CONNECTION_STRING="$2"
        shift 2
        ;;
      --plan-artifact-dir)
        PLAN_ARTIFACT_DIR="$2"
        shift 2
        ;;
      --allow-destroy-plan)
        ALLOW_DESTROY_PLAN=true
        shift
        ;;
      --ttl-hours)
        TTL_HOURS="$2"
        shift 2
        ;;
      --skip-quota-preflight)
        RUN_QUOTA_PREFLIGHT=false
        shift
        ;;
      --skip-idempotency)
        CHECK_IDEMPOTENCY=false
        shift
        ;;
      --skip-protocol-checks)
        CHECK_PROTOCOLS=false
        shift
        ;;
      --skip-db-resilience)
        RUN_DB_RESILIENCE=false
        shift
        ;;
      --no-scale-check)
        QUICK_SCALE=false
        shift
        ;;
      --no-destroy)
        AUTO_DESTROY=false
        shift
        ;;
      --help|-h)
        usage
        exit 0
        ;;
      *)
        log_error "Unknown argument: $1"
        usage
        exit 1
        ;;
    esac
  done

  if [[ "$STACK" != "aca" && "$STACK" != "functions" && "$STACK" != "both" ]]; then
    log_error "Invalid --stack value: $STACK"
    exit 1
  fi
}

build_tf_image_if_needed() {
  if docker image inspect "$TF_IMAGE" >/dev/null 2>&1; then
    return
  fi

  log_info "Building Terraform image with psql client: $TF_IMAGE"
  docker build -t "$TF_IMAGE" - <<'DOCKERFILE'
FROM hashicorp/terraform:1.8.5
RUN apk add --no-cache postgresql-client
DOCKERFILE
}

prepare_tf_workspace() {
  TEMP_TF_ROOT="$(mktemp -d)"
  cp -R "$REPO_ROOT/infrastructure/terraform" "$TEMP_TF_ROOT/terraform"
}

run_tf() {
  docker run --rm \
    -e ARM_CLIENT_ID \
    -e ARM_CLIENT_SECRET \
    -e ARM_TENANT_ID \
    -e ARM_SUBSCRIPTION_ID \
    -e TF_VAR_location \
    -e TF_VAR_environment \
    -e TF_VAR_name_prefix \
    -e TF_VAR_honua_admin_password \
    -e TF_VAR_db_admin_password \
    -e TF_VAR_db_sku_name \
    -e TF_VAR_db_storage_mb \
    -e TF_VAR_db_public_network_access \
    -e TF_VAR_honua_image \
    -e TF_VAR_enable_postgis \
    -e TF_VAR_redis_enabled \
    -e TF_VAR_redis_sku_name \
    -e TF_VAR_redis_family \
    -e TF_VAR_redis_capacity \
    -e TF_VAR_redis_public_network_access_enabled \
    -e TF_VAR_redis_connection_string \
    -e TF_VAR_existing_db_fqdn \
    -e TF_VAR_existing_db_connection_string \
    -e TF_VAR_db_firewall_start_ip \
    -e TF_VAR_db_firewall_end_ip \
    -e TF_VAR_min_replicas \
    -e TF_VAR_max_replicas \
    -e TF_VAR_key_vault_default_action \
    -e TF_VAR_plan_sku_name \
    -e TF_VAR_skip_migrations \
    -e TF_VAR_tags \
    -e TF_IN_AUTOMATION=true \
    -v "$TEMP_TF_ROOT/terraform:/workspace" \
    -w /workspace \
    "$TF_IMAGE" "$@"
}

run_az() {
  docker run --rm \
    -e ARM_CLIENT_ID \
    -e ARM_CLIENT_SECRET \
    -e ARM_TENANT_ID \
    -e ARM_SUBSCRIPTION_ID \
    -e AZURE_CORE_ONLY_SHOW_ERRORS=true \
    "$AZ_CLI_IMAGE" \
    sh -c 'set -e; az config set extension.use_dynamic_install=yes_without_prompt >/dev/null; az login --service-principal -u "$ARM_CLIENT_ID" -p "$ARM_CLIENT_SECRET" --tenant "$ARM_TENANT_ID" >/dev/null; az account set -s "$ARM_SUBSCRIPTION_ID"; az "$@"' \
    sh "$@"
}

parse_plan_destroy_count() {
  local plan_txt="$1"
  local summary

  summary="$(grep -E '^Plan: ' "$plan_txt" | tail -1 || true)"
  if [[ -z "$summary" ]]; then
    echo "0"
    return
  fi

  echo "$summary" | sed -n 's/.*Plan: [0-9][0-9]* to add, [0-9][0-9]* to change, \([0-9][0-9]*\) to destroy.*/\1/p'
}

analyze_plan() {
  local root="$1"
  local plan_file="$2"
  local label="$3"
  local artifacts_path
  local plan_txt
  local destroy_count

  artifacts_path="${PLAN_ARTIFACT_DIR%/}"
  if [[ -n "$artifacts_path" ]]; then
    mkdir -p "$artifacts_path"
  fi

  plan_txt="$(mktemp)"
  run_tf -chdir="$root" show -no-color "$plan_file" > "$plan_txt"

  destroy_count="$(parse_plan_destroy_count "$plan_txt")"
  destroy_count="${destroy_count:-0}"

  if [[ -n "$artifacts_path" ]]; then
    cp "$TEMP_TF_ROOT/terraform/$root/$plan_file" "$artifacts_path/${label}.tfplan"
    cp "$plan_txt" "$artifacts_path/${label}.plan.txt"
  fi

  rm -f "$plan_txt"

  if [[ "$ALLOW_DESTROY_PLAN" != "true" ]] && [[ "$destroy_count" =~ ^[0-9]+$ ]] && (( destroy_count > 0 )); then
    log_error "Plan '$label' includes $destroy_count destroy actions; refusing apply without --allow-destroy-plan"
    return 1
  fi
}

plan_apply() {
  local root="$1"
  local plan_file="$2"
  local label="$3"

  run_tf -chdir="$root" plan -input=false -no-color -out="$plan_file"
  analyze_plan "$root" "$plan_file" "$label"
  run_tf_apply_with_token_retry "$root" "$plan_file"
}

run_tf_apply_with_token_retry() {
  local root="$1"
  local plan_file="$2"
  local attempt
  local apply_log
  local exit_code

  for attempt in 1 2; do
    apply_log="$(mktemp)"

    set +e
    run_tf -chdir="$root" apply -input=false -auto-approve -no-color "$plan_file" 2>&1 | tee "$apply_log"
    exit_code=${PIPESTATUS[0]}
    set -e

    if [[ "$exit_code" -eq 0 ]]; then
      rm -f "$apply_log"
      return 0
    fi

    if grep -q "ExpiredAuthenticationToken" "$apply_log" && [[ "$attempt" -lt 2 ]]; then
      log_warn "Terraform apply failed with ExpiredAuthenticationToken; retrying apply once"
      rm -f "$apply_log"
      continue
    fi

    rm -f "$apply_log"
    return "$exit_code"
  done
}

wait_for_ready() {
  local base_url="$1"
  local timeout="$2"
  local normalized_base
  local ready_url
  local start_epoch
  local elapsed

  normalized_base="$(normalize_base_url "$base_url")"
  ready_url="${normalized_base}/healthz/ready"

  start_epoch="$(date +%s)"
  while true; do
    if curl -fsSL --max-time 20 "$ready_url" >/dev/null; then
      elapsed=$(( $(date +%s) - start_epoch ))
      if (( elapsed > READY_SLO_SECONDS )); then
        log_error "Ready SLO failed: ${elapsed}s exceeds ${READY_SLO_SECONDS}s ($ready_url)"
        return 1
      fi
      log_info "Ready check passed in ${elapsed}s: $ready_url"
      return 0
    fi

    if (( $(date +%s) - start_epoch > timeout )); then
      log_error "Timed out waiting for readiness: $ready_url"
      return 1
    fi

    sleep 10
  done
}

run_load_probe() {
  local base_url="$1"
  local requests="$2"
  local concurrency="$3"
  local normalized_base
  local target_url
  local fail_file
  local failures
  local error_rate

  normalized_base="$(normalize_base_url "$base_url")"
  target_url="${normalized_base}/healthz/ready"

  fail_file="$(mktemp)"

  for ((i = 1; i <= requests; i++)); do
    (
      if ! curl -fsS --max-time 20 "$target_url" >/dev/null; then
        echo "1" >> "$fail_file"
      fi
    ) &

    if (( i % concurrency == 0 )); then
      wait
    fi
  done

  wait

  failures="$(wc -l < "$fail_file" | tr -d ' ')"
  rm -f "$fail_file"

  error_rate="$(awk -v f="$failures" -v r="$requests" 'BEGIN { printf "%.4f", (f*100)/r }')"
  if awk -v e="$error_rate" -v m="$MAX_LOAD_ERROR_RATE_PERCENT" 'BEGIN { exit !(e <= m) }'; then
    log_info "Load probe passed: $requests requests, concurrency $concurrency, error rate ${error_rate}%"
    return 0
  fi

  log_error "Load probe failed SLO: error rate ${error_rate}% exceeds ${MAX_LOAD_ERROR_RATE_PERCENT}%"
  return 1
}

assert_idempotent_plan() {
  local root="$1"
  local log_file
  local exit_code

  log_file="$(mktemp)"
  set +e
  run_tf -chdir="$root" plan -input=false -no-color -detailed-exitcode >"$log_file" 2>&1
  exit_code=$?
  set -e

  if [[ "$exit_code" -eq 0 ]]; then
    log_info "Idempotency check passed for $root (no changes)"
    rm -f "$log_file"
    return 0
  fi

  if [[ "$exit_code" -eq 2 ]]; then
    log_error "Idempotency check failed for $root (terraform reports pending changes)"
    cat "$log_file"
    rm -f "$log_file"
    return 1
  fi

  log_error "Idempotency plan errored for $root"
  cat "$log_file"
  rm -f "$log_file"
  return 1
}

verify_protocol_endpoints() {
  local base_url="$1"
  local normalized
  local status

  normalized="$(normalize_base_url "$base_url")"

  curl -fsSL --max-time 20 "${normalized}/rest/services?f=pjson" >/dev/null
  curl -fsSL --max-time 20 "${normalized}/ogc/features" >/dev/null
  curl -fsSL --max-time 20 "${normalized}/odata" >/dev/null

  status="$(curl -sSL -o /dev/null -w "%{http_code}" --max-time 20 "${normalized}/api/v1/admin/config")"
  if [[ "$status" != "401" && "$status" != "403" ]]; then
    log_error "Expected unauthenticated admin endpoint to return 401/403, got $status"
    return 1
  fi

  log_info "Protocol/admin smoke checks passed for $normalized"
}

json_escape() {
  local value="$1"
  value="${value//\\/\\\\}"
  value="${value//\"/\\\"}"
  value="${value//$'\n'/\\n}"
  value="${value//$'\r'/\\r}"
  value="${value//$'\t'/\\t}"
  printf '%s' "$value"
}

normalize_base_url() {
  local base_url="${1%/}"
  if [[ "$base_url" =~ ^https?:// ]]; then
    printf '%s\n' "$base_url"
    return
  fi

  printf 'https://%s\n' "$base_url"
}

extract_json_string_field() {
  local payload="$1"
  local field="$2"
  local compact

  compact="$(printf '%s' "$payload" | tr -d '\n\r')"
  printf '%s' "$compact" | sed -n "s/.*\"$field\":\"\\([^\"]*\\)\".*/\\1/p" | head -1
}

extract_json_number_field() {
  local payload="$1"
  local field="$2"
  local compact

  compact="$(printf '%s' "$payload" | tr -d '\n\r')"
  printf '%s' "$compact" | sed -n "s/.*\"$field\":\\([0-9][0-9]*\\).*/\\1/p" | head -1
}

run_db_sql() {
  local db_host="$1"
  local sql="$2"
  local sql_file

  sql_file="$(mktemp)"
  printf '%s\n' "$sql" > "$sql_file"

  docker run --rm \
    -e PGPASSWORD="$HONUA_DB_PASSWORD" \
    -v "$sql_file:/tmp/smoke.sql:ro" \
    postgres:16-alpine \
    sh -c "psql 'host=$db_host port=5432 dbname=honua user=honua sslmode=require' -v ON_ERROR_STOP=1 -f /tmp/smoke.sql" >/dev/null

  rm -f "$sql_file"
}

run_admin_api_crud_smoke() {
  local base_url="$1"
  local db_host="$2"
  local normalized
  local suffix
  local table_name
  local layer_name
  local service_name
  local connection_name
  local connection_id=""
  local layer_id=""
  local query_url
  local query_response
  local feature_count=0
  local create_connection_payload
  local publish_layer_payload
  local create_connection_response
  local publish_layer_response

  normalized="$(normalize_base_url "$base_url")"
  suffix="$(date -u +%m%d%H%M%S)$RANDOM"
  table_name="smoke_${suffix}"
  layer_name="Smoke Layer ${suffix}"
  service_name="smoke${suffix}"
  connection_name="smoke-conn-${suffix}"

  cleanup_smoke() {
    trap - RETURN
    set +e

    run_db_sql "$db_host" "DROP TABLE IF EXISTS public.${table_name};" || true

    if [[ -n "$layer_id" ]]; then
      run_db_sql "$db_host" "
        DELETE FROM honua.layer_fields WHERE layer_id = ${layer_id};
        DELETE FROM honua.service_layers WHERE layer_id = ${layer_id};
        DELETE FROM honua.layers WHERE layer_id = ${layer_id};
      " || true
    fi

    run_db_sql "$db_host" "DELETE FROM honua.services WHERE service_name = '$(json_escape "$service_name")';" || true

    if [[ -n "$connection_id" ]]; then
      curl -sS --max-time 20 -X DELETE \
        -H "X-API-Key: $HONUA_ADMIN_PASSWORD" \
        "${normalized}/api/v1/admin/connections/${connection_id}" >/dev/null || true
    fi
  }

  trap cleanup_smoke RETURN

  run_db_sql "$db_host" "
    CREATE TABLE public.${table_name} (
      id SERIAL PRIMARY KEY,
      name TEXT NOT NULL,
      population INTEGER,
      geom geometry(Point, 4326) NOT NULL
    );
    INSERT INTO public.${table_name} (name, population, geom)
    VALUES ('Smoke Feature', 1, ST_SetSRID(ST_Point(1, 1), 4326));
  "

  create_connection_payload="$(cat <<JSON
{"name":"$(json_escape "$connection_name")","description":"Terraform smoke test connection","host":"$(json_escape "$db_host")","port":5432,"databaseName":"honua","username":"honua","password":"$(json_escape "$HONUA_DB_PASSWORD")","sslRequired":true,"sslMode":"Require"}
JSON
)"

  create_connection_response="$(curl -fsS --max-time 20 -X POST \
    -H "Content-Type: application/json" \
    -H "X-API-Key: $HONUA_ADMIN_PASSWORD" \
    -d "$create_connection_payload" \
    "${normalized}/api/v1/admin/connections")"

  connection_id="$(extract_json_string_field "$create_connection_response" "connectionId")"
  if [[ -z "$connection_id" ]]; then
    log_error "Admin CRUD smoke failed: could not parse connectionId from create response"
    return 1
  fi

  publish_layer_payload="$(cat <<JSON
{"schema":"public","table":"$(json_escape "$table_name")","layerName":"$(json_escape "$layer_name")","description":"Terraform smoke test layer","geometryColumn":"geom","geometryType":"Point","srid":4326,"primaryKey":"id","fields":["id","name","population"],"serviceName":"$(json_escape "$service_name")","enabled":true}
JSON
)"

  publish_layer_response="$(curl -fsS --max-time 20 -X POST \
    -H "Content-Type: application/json" \
    -H "X-API-Key: $HONUA_ADMIN_PASSWORD" \
    -d "$publish_layer_payload" \
    "${normalized}/api/v1/admin/connections/${connection_id}/layers")"

  layer_id="$(extract_json_number_field "$publish_layer_response" "layerId")"
  if [[ -z "$layer_id" ]]; then
    log_error "Admin CRUD smoke failed: could not parse layerId from publish response"
    return 1
  fi

  query_url="${normalized}/rest/services/${service_name}/FeatureServer/${layer_id}/query?where=1%3D1&outFields=id,name,population&f=pjson"
  query_response="$(curl -fsS --max-time 20 "$query_url")"

  if command -v jq >/dev/null 2>&1; then
    feature_count="$(printf '%s' "$query_response" | jq -r '(.features // []) | length' 2>/dev/null || echo 0)"
  else
    feature_count="$(printf '%s' "$query_response" | tr -d '\n\r' | grep -o '"attributes":' | wc -l | tr -d ' ')"
  fi

  if [[ -z "$feature_count" || "$feature_count" == "0" ]]; then
    log_error "Admin CRUD smoke failed: query returned no features"
    return 1
  fi

  log_info "Admin CRUD/query smoke passed for $normalized (service=${service_name}, layerId=${layer_id}, features=${feature_count})"
}

verify_redis_exists() {
  local resource_group="$1"
  local count

  if [[ -n "$EXISTING_REDIS_CONNECTION_STRING" && "$DATA_APPLIED" != "true" ]]; then
    log_info "Using existing Redis connection string; skipping Azure Redis resource check"
    return 0
  fi

  count="$(run_az redis list -g "$resource_group" --query "length(@)" -o tsv)"
  if [[ -z "$count" || "$count" == "0" ]]; then
    log_error "Redis instance not found in resource group: $resource_group"
    return 1
  fi

  log_info "Redis instance count in $resource_group: $count"
}

verify_postgis_extensions() {
  local db_fqdn="$1"
  local extensions

  extensions="$(docker run --rm \
    -e PGPASSWORD="$HONUA_DB_PASSWORD" \
    postgres:16-alpine \
    sh -c "psql 'host=$db_fqdn port=5432 dbname=honua user=honua sslmode=require' -v ON_ERROR_STOP=1 -tA -c \"SELECT extname FROM pg_extension WHERE extname IN ('postgis','postgis_raster') ORDER BY extname;\"" || true)"

  if [[ "$extensions" != *"postgis"* || "$extensions" != *"postgis_raster"* ]]; then
    log_error "Expected postgis + postgis_raster extensions not both present on $db_fqdn"
    log_error "Observed extensions output: ${extensions:-<none>}"
    return 1
  fi

  log_info "Verified extensions on $db_fqdn: postgis + postgis_raster"
}

verify_db_backup_restore() {
  local db_fqdn="$1"
  local extensions_count

  docker run --rm \
    -e PGPASSWORD="$HONUA_DB_PASSWORD" \
    postgres:16-alpine \
    sh -c "set -e; \
      pg_dump 'host=$db_fqdn port=5432 dbname=honua user=honua sslmode=require' -Fc -f /tmp/honua.dump; \
      psql 'host=$db_fqdn port=5432 dbname=postgres user=honua sslmode=require' -v ON_ERROR_STOP=1 -c 'DROP DATABASE IF EXISTS honua_restore_check'; \
      psql 'host=$db_fqdn port=5432 dbname=postgres user=honua sslmode=require' -v ON_ERROR_STOP=1 -c 'CREATE DATABASE honua_restore_check'; \
      pg_restore 'host=$db_fqdn port=5432 dbname=honua_restore_check user=honua sslmode=require' -v /tmp/honua.dump >/dev/null;" >/dev/null

  extensions_count="$(docker run --rm \
    -e PGPASSWORD="$HONUA_DB_PASSWORD" \
    postgres:16-alpine \
    sh -c "psql 'host=$db_fqdn port=5432 dbname=honua_restore_check user=honua sslmode=require' -tA -c \"SELECT COUNT(*) FROM pg_extension WHERE extname IN ('postgis','postgis_raster');\"" | tr -d '[:space:]')"

  docker run --rm \
    -e PGPASSWORD="$HONUA_DB_PASSWORD" \
    postgres:16-alpine \
    sh -c "psql 'host=$db_fqdn port=5432 dbname=postgres user=honua sslmode=require' -v ON_ERROR_STOP=1 -c 'DROP DATABASE IF EXISTS honua_restore_check'" >/dev/null

  if [[ "$extensions_count" != "2" ]]; then
    log_error "DB backup/restore drill failed: expected 2 PostGIS extensions in restored DB, got ${extensions_count:-<none>}"
    return 1
  fi

  log_info "DB backup/restore drill passed"
}

wait_for_aca_replicas() {
  local resource_group="$1"
  local app_name="$2"
  local expected_min="$3"
  local timeout="$4"
  local start_epoch
  local current

  start_epoch="$(date +%s)"
  while true; do
    current="$(run_az containerapp replica list -g "$resource_group" -n "$app_name" --query "length(@)" -o tsv || echo 0)"

    if [[ -n "$current" ]] && (( current >= expected_min )); then
      log_info "Container App replicas reached target: $current >= $expected_min"
      return 0
    fi

    if (( $(date +%s) - start_epoch > timeout )); then
      log_error "Timed out waiting for ACA replicas >= $expected_min (current: ${current:-unknown})"
      return 1
    fi

    sleep 15
  done
}

estimate_stack_cost() {
  local stack_name="$1"
  case "$stack_name" in
    aca) echo "45" ;;
    functions) echo "30" ;;
    both) echo "75" ;;
    *) echo "0" ;;
  esac
}

assert_cost_guardrail() {
  local estimated

  if ! awk -v m="$MAX_RUN_COST_USD" 'BEGIN { exit !(m > 0) }'; then
    return
  fi

  estimated="$(estimate_stack_cost "$STACK")"
  if awk -v e="$estimated" -v m="$MAX_RUN_COST_USD" 'BEGIN { exit !(e <= m) }'; then
    log_info "Estimated run cost ($estimated USD) is within cap ($MAX_RUN_COST_USD USD)"
    return
  fi

  log_error "Estimated run cost ($estimated USD) exceeds cap ($MAX_RUN_COST_USD USD)"
  exit 1
}

validate_existing_resource_inputs() {
  if [[ -n "$EXISTING_DB_FQDN" && -z "$EXISTING_DB_CONNECTION_STRING" ]]; then
    log_error "--existing-db-connection is required when --existing-db-fqdn is provided"
    exit 1
  fi

  if [[ -z "$EXISTING_DB_FQDN" && -n "$EXISTING_DB_CONNECTION_STRING" ]]; then
    log_error "--existing-db-fqdn is required when --existing-db-connection is provided"
    exit 1
  fi
}

configure_data_stack_mode() {
  if [[ -z "$EXISTING_DB_CONNECTION_STRING" && -z "$EXISTING_REDIS_CONNECTION_STRING" ]]; then
    AUTO_PROVISION_DATA_STACK=true
    return
  fi

  AUTO_PROVISION_DATA_STACK=false

  if [[ -n "$EXISTING_DB_CONNECTION_STRING" && -n "$EXISTING_REDIS_CONNECTION_STRING" ]]; then
    log_info "Using caller-provided DB/Redis connections; skipping azure-data bootstrap stack"
    return
  fi

  log_warn "Partial existing data inputs detected; skipping azure-data bootstrap stack and using mixed data wiring"
}

run_quota_preflight() {
  local usage_json
  local current
  local limit
  local required

  if [[ "$RUN_QUOTA_PREFLIGHT" != "true" ]]; then
    return
  fi

  usage_json="$(run_az vm list-usage -l "$LOCATION" --query "[?name.value=='cores'] | [0]" -o json)"
  current="$(echo "$usage_json" | sed -n 's/.*"currentValue":\([0-9][0-9]*\).*/\1/p')"
  limit="$(echo "$usage_json" | sed -n 's/.*"limit":\([0-9][0-9]*\).*/\1/p')"

  required=0
  if [[ "$STACK" == "aca" || "$STACK" == "both" ]]; then
    required=$((required + 4))
  fi
  if [[ "$STACK" == "functions" || "$STACK" == "both" ]]; then
    required=$((required + 2))
  fi

  if [[ -n "$current" && -n "$limit" ]] && (( current + required > limit )); then
    log_error "Azure quota preflight failed: cores usage $current/$limit, estimated required +$required"
    exit 1
  fi

  log_info "Azure quota preflight passed (cores current=${current:-unknown}, limit=${limit:-unknown}, required=+$required)"
}

detect_db_firewall_ips() {
  if [[ -n "$EXISTING_DB_CONNECTION_STRING" ]]; then
    DB_FIREWALL_START_IP=""
    DB_FIREWALL_END_IP=""
    log_info "Using existing DB connection string; skipping DB firewall configuration"
    return
  fi

  if [[ -n "$DB_FIREWALL_START_IP" && -z "$DB_FIREWALL_END_IP" ]]; then
    log_error "HONUA_AZURE_DB_FIREWALL_END_IP must be set when HONUA_AZURE_DB_FIREWALL_START_IP is provided"
    exit 1
  fi

  if [[ -z "$DB_FIREWALL_START_IP" && -n "$DB_FIREWALL_END_IP" ]]; then
    log_error "HONUA_AZURE_DB_FIREWALL_START_IP must be set when HONUA_AZURE_DB_FIREWALL_END_IP is provided"
    exit 1
  fi

  if [[ -n "$DB_FIREWALL_START_IP" && -n "$DB_FIREWALL_END_IP" ]]; then
    log_info "Using provided DB firewall range: $DB_FIREWALL_START_IP - $DB_FIREWALL_END_IP"
    return
  fi

  DB_FIREWALL_START_IP="0.0.0.0"
  DB_FIREWALL_END_IP="255.255.255.255"
  log_warn "No DB firewall range provided; using open DB firewall range for this ephemeral validation run"
}

set_common_tf_vars() {
  EXPIRES_AT_UTC="$(date -u -d "+${TTL_HOURS} hours" +%Y-%m-%dT%H:%M:%SZ)"

  export TF_VAR_location="$LOCATION"
  export TF_VAR_environment="$ENVIRONMENT"
  export TF_VAR_honua_admin_password="$HONUA_ADMIN_PASSWORD"
  export TF_VAR_db_admin_password="$HONUA_DB_PASSWORD"
  export TF_VAR_enable_postgis="true"
  export TF_VAR_redis_enabled="true"
  export TF_VAR_redis_connection_string="$EXISTING_REDIS_CONNECTION_STRING"
  export TF_VAR_existing_db_fqdn="$EXISTING_DB_FQDN"
  export TF_VAR_existing_db_connection_string="$EXISTING_DB_CONNECTION_STRING"
  export TF_VAR_db_firewall_start_ip="$DB_FIREWALL_START_IP"
  export TF_VAR_db_firewall_end_ip="$DB_FIREWALL_END_IP"
  export TF_VAR_tags="{\"ValidationRunId\":\"$VALIDATION_RUN_ID\",\"TTLHours\":\"$TTL_HOURS\",\"ExpiresAtUTC\":\"$EXPIRES_AT_UTC\",\"Owner\":\"terraform-validation\"}"
}

set_aca_tf_vars() {
  set_common_tf_vars
  export TF_VAR_name_prefix="$ACA_NAME_PREFIX"
  export TF_VAR_honua_image="$ACA_IMAGE"
  export TF_VAR_min_replicas="$ACA_MIN_REPLICAS"
  export TF_VAR_max_replicas="$ACA_MAX_REPLICAS"
  export TF_VAR_key_vault_default_action="Allow"

  unset TF_VAR_plan_sku_name
  unset TF_VAR_skip_migrations
  unset TF_VAR_db_sku_name
  unset TF_VAR_db_storage_mb
  unset TF_VAR_db_public_network_access
  unset TF_VAR_redis_sku_name
  unset TF_VAR_redis_family
  unset TF_VAR_redis_capacity
  unset TF_VAR_redis_public_network_access_enabled
}

set_functions_tf_vars() {
  set_common_tf_vars
  export TF_VAR_name_prefix="$FUNCTIONS_NAME_PREFIX"
  export TF_VAR_honua_image="$FUNCTIONS_IMAGE"
  export TF_VAR_plan_sku_name="$FUNCTIONS_PLAN_SKU"
  export TF_VAR_skip_migrations="true"

  unset TF_VAR_min_replicas
  unset TF_VAR_max_replicas
  unset TF_VAR_key_vault_default_action
  unset TF_VAR_db_sku_name
  unset TF_VAR_db_storage_mb
  unset TF_VAR_db_public_network_access
  unset TF_VAR_redis_sku_name
  unset TF_VAR_redis_family
  unset TF_VAR_redis_capacity
  unset TF_VAR_redis_public_network_access_enabled
}

set_data_tf_vars() {
  set_common_tf_vars
  export TF_VAR_name_prefix="$DATA_NAME_PREFIX"
  export TF_VAR_key_vault_default_action="Allow"
  export TF_VAR_db_sku_name="$DATA_DB_SKU_NAME"
  export TF_VAR_db_storage_mb="$DATA_DB_STORAGE_MB"
  export TF_VAR_db_public_network_access="$DATA_DB_PUBLIC_NETWORK_ACCESS"
  export TF_VAR_redis_sku_name="$DATA_REDIS_SKU_NAME"
  export TF_VAR_redis_family="$DATA_REDIS_FAMILY"
  export TF_VAR_redis_capacity="$DATA_REDIS_CAPACITY"
  export TF_VAR_redis_public_network_access_enabled="$DATA_REDIS_PUBLIC_NETWORK_ACCESS_ENABLED"

  unset TF_VAR_honua_image
  unset TF_VAR_plan_sku_name
  unset TF_VAR_skip_migrations
  unset TF_VAR_min_replicas
  unset TF_VAR_max_replicas
}

apply_data_stack() {
  if [[ "$AUTO_PROVISION_DATA_STACK" != "true" ]]; then
    return
  fi

  log_info "Applying Azure data stack"
  set_data_tf_vars

  run_tf -chdir=examples/azure-data init -input=false -no-color
  # Mark stack as applied before first plan/apply so cleanup destroys partial resources on failed apply.
  DATA_APPLIED=true

  plan_apply "examples/azure-data" "data.tfplan" "azure-data"

  EXISTING_DB_FQDN="$(run_tf -chdir=examples/azure-data output -raw db_fqdn)"
  EXISTING_DB_CONNECTION_STRING="$(run_tf -chdir=examples/azure-data output -raw db_connection_string)"
  EXISTING_REDIS_CONNECTION_STRING="$(run_tf -chdir=examples/azure-data output -raw redis_connection_string)"
  DATA_RESOURCE_GROUP="$(run_tf -chdir=examples/azure-data output -raw resource_group_name)"

  if [[ -z "$EXISTING_DB_FQDN" || -z "$EXISTING_DB_CONNECTION_STRING" ]]; then
    log_error "Azure data stack output validation failed: db_fqdn/db_connection_string must be non-empty"
    return 1
  fi

  if [[ -z "$EXISTING_REDIS_CONNECTION_STRING" ]]; then
    log_error "Azure data stack output validation failed: redis_connection_string must be non-empty"
    return 1
  fi

  if [[ "$CHECK_IDEMPOTENCY" == "true" ]]; then
    assert_idempotent_plan "examples/azure-data"
  fi

  log_info "Azure data stack ready: resource_group=$DATA_RESOURCE_GROUP"
}

run_aca_checks() {
  local url="$1"
  local db_fqdn="$2"
  local resource_group="$3"
  local redis_resource_group="$resource_group"

  if [[ "$DATA_APPLIED" == "true" && -n "$DATA_RESOURCE_GROUP" ]]; then
    redis_resource_group="$DATA_RESOURCE_GROUP"
  fi

  wait_for_ready "$url" "$TIMEOUT_SECONDS"
  if [[ "$CHECK_PROTOCOLS" == "true" ]]; then
    verify_protocol_endpoints "$url"
    run_admin_api_crud_smoke "$url" "$db_fqdn"
  fi
  verify_redis_exists "$redis_resource_group"
  verify_postgis_extensions "$db_fqdn"
  if [[ "$RUN_DB_RESILIENCE" == "true" ]]; then
    verify_db_backup_restore "$db_fqdn"
  fi
  run_load_probe "$url" "$LOAD_REQUESTS" "$LOAD_CONCURRENCY"
}

run_functions_checks() {
  local url="$1"
  local db_fqdn="$2"
  local resource_group="$3"
  local redis_resource_group="$resource_group"

  if [[ "$DATA_APPLIED" == "true" && -n "$DATA_RESOURCE_GROUP" ]]; then
    redis_resource_group="$DATA_RESOURCE_GROUP"
  fi

  wait_for_ready "$url" "$TIMEOUT_SECONDS"
  if [[ "$CHECK_PROTOCOLS" == "true" ]]; then
    verify_protocol_endpoints "$url"
    run_admin_api_crud_smoke "$url" "$db_fqdn"
  fi
  verify_redis_exists "$redis_resource_group"
  verify_postgis_extensions "$db_fqdn"
  if [[ "$RUN_DB_RESILIENCE" == "true" ]]; then
    verify_db_backup_restore "$db_fqdn"
  fi
  run_load_probe "$url" "$LOAD_REQUESTS" "$LOAD_CONCURRENCY"
}

apply_aca_stack() {
  local url
  local db_fqdn
  local resource_group
  local app_name

  log_info "Applying Azure ACA stack"
  set_aca_tf_vars

  run_tf -chdir=examples/azure init -input=false -no-color
  # Mark stack as applied before first plan/apply so cleanup destroys partial resources on failed apply.
  ACA_APPLIED=true

  if [[ "$RUN_UPGRADE_ROLLBACK" == "true" ]]; then
    if [[ -z "$ACA_PREVIOUS_IMAGE" || "$ACA_PREVIOUS_IMAGE" == "$ACA_IMAGE" ]]; then
      log_error "ACA upgrade/rollback requires --aca-previous-image different from --aca-image"
      return 1
    fi

    export TF_VAR_honua_image="$ACA_PREVIOUS_IMAGE"
    plan_apply "examples/azure" "aca-prev.tfplan" "aca-previous"
    url="$(run_tf -chdir=examples/azure output -raw honua_url)"
    db_fqdn="$(run_tf -chdir=examples/azure output -raw database_fqdn)"
    resource_group="$(run_tf -chdir=examples/azure output -raw resource_group_name)"
    run_aca_checks "$url" "$db_fqdn" "$resource_group"

    export TF_VAR_honua_image="$ACA_IMAGE"
    plan_apply "examples/azure" "aca-upgrade.tfplan" "aca-upgrade"
    url="$(run_tf -chdir=examples/azure output -raw honua_url)"
    db_fqdn="$(run_tf -chdir=examples/azure output -raw database_fqdn)"
    resource_group="$(run_tf -chdir=examples/azure output -raw resource_group_name)"
    app_name="$(run_tf -chdir=examples/azure output -raw container_app_name)"
    run_aca_checks "$url" "$db_fqdn" "$resource_group"

    if [[ "$QUICK_SCALE" == "true" ]]; then
      log_info "Running quick ACA scale validation by raising min replicas to $ACA_SCALE_TARGET_MIN_REPLICAS"
      export TF_VAR_min_replicas="$ACA_SCALE_TARGET_MIN_REPLICAS"
      plan_apply "examples/azure" "aca-scale.tfplan" "aca-scale"
      wait_for_aca_replicas "$resource_group" "$app_name" "$ACA_SCALE_TARGET_MIN_REPLICAS" 600
      export TF_VAR_min_replicas="$ACA_MIN_REPLICAS"
    fi

    export TF_VAR_honua_image="$ACA_PREVIOUS_IMAGE"
    plan_apply "examples/azure" "aca-rollback.tfplan" "aca-rollback"
    run_aca_checks "$url" "$db_fqdn" "$resource_group"

    if [[ "$AUTO_DESTROY" != "true" ]]; then
      export TF_VAR_honua_image="$ACA_IMAGE"
      plan_apply "examples/azure" "aca-restore-current.tfplan" "aca-restore-current"
      run_aca_checks "$url" "$db_fqdn" "$resource_group"
    fi

    export TF_VAR_honua_image="$ACA_IMAGE"
  else
    plan_apply "examples/azure" "aca.tfplan" "aca"

    url="$(run_tf -chdir=examples/azure output -raw honua_url)"
    db_fqdn="$(run_tf -chdir=examples/azure output -raw database_fqdn)"
    resource_group="$(run_tf -chdir=examples/azure output -raw resource_group_name)"
    app_name="$(run_tf -chdir=examples/azure output -raw container_app_name)"

    run_aca_checks "$url" "$db_fqdn" "$resource_group"

    if [[ "$QUICK_SCALE" == "true" ]]; then
      log_info "Running quick ACA scale validation by raising min replicas to $ACA_SCALE_TARGET_MIN_REPLICAS"
      export TF_VAR_min_replicas="$ACA_SCALE_TARGET_MIN_REPLICAS"
      plan_apply "examples/azure" "aca-scale.tfplan" "aca-scale"
      wait_for_aca_replicas "$resource_group" "$app_name" "$ACA_SCALE_TARGET_MIN_REPLICAS" 600
      export TF_VAR_min_replicas="$ACA_MIN_REPLICAS"
    fi
  fi

  if [[ "$CHECK_IDEMPOTENCY" == "true" ]]; then
    assert_idempotent_plan "examples/azure"
  fi

  log_info "ACA stack checks passed"
  log_info "ACA URL: $(run_tf -chdir=examples/azure output -raw honua_url)"
}

apply_functions_stack() {
  local url
  local db_fqdn
  local resource_group

  log_info "Applying Azure Functions stack"
  set_functions_tf_vars

  run_tf -chdir=examples/azure-functions init -input=false -no-color
  # Mark stack as applied before first plan/apply so cleanup destroys partial resources on failed apply.
  FUNCTIONS_APPLIED=true

  if [[ "$RUN_UPGRADE_ROLLBACK" == "true" ]]; then
    if [[ -z "$FUNCTIONS_PREVIOUS_IMAGE" || "$FUNCTIONS_PREVIOUS_IMAGE" == "$FUNCTIONS_IMAGE" ]]; then
      log_error "Functions upgrade/rollback requires --functions-previous-image different from --functions-image"
      return 1
    fi

    export TF_VAR_honua_image="$FUNCTIONS_PREVIOUS_IMAGE"
    plan_apply "examples/azure-functions" "functions-prev.tfplan" "functions-previous"
    url="$(run_tf -chdir=examples/azure-functions output -raw honua_url)"
    db_fqdn="$(run_tf -chdir=examples/azure-functions output -raw db_fqdn)"
    resource_group="$(run_tf -chdir=examples/azure-functions output -raw resource_group_name)"
    run_functions_checks "$url" "$db_fqdn" "$resource_group"

    export TF_VAR_honua_image="$FUNCTIONS_IMAGE"
    plan_apply "examples/azure-functions" "functions-upgrade.tfplan" "functions-upgrade"
    url="$(run_tf -chdir=examples/azure-functions output -raw honua_url)"
    db_fqdn="$(run_tf -chdir=examples/azure-functions output -raw db_fqdn)"
    resource_group="$(run_tf -chdir=examples/azure-functions output -raw resource_group_name)"
    run_functions_checks "$url" "$db_fqdn" "$resource_group"

    export TF_VAR_honua_image="$FUNCTIONS_PREVIOUS_IMAGE"
    plan_apply "examples/azure-functions" "functions-rollback.tfplan" "functions-rollback"
    run_functions_checks "$url" "$db_fqdn" "$resource_group"

    if [[ "$AUTO_DESTROY" != "true" ]]; then
      export TF_VAR_honua_image="$FUNCTIONS_IMAGE"
      plan_apply "examples/azure-functions" "functions-restore-current.tfplan" "functions-restore-current"
      run_functions_checks "$url" "$db_fqdn" "$resource_group"
    fi

    export TF_VAR_honua_image="$FUNCTIONS_IMAGE"
  else
    plan_apply "examples/azure-functions" "functions.tfplan" "functions"

    url="$(run_tf -chdir=examples/azure-functions output -raw honua_url)"
    db_fqdn="$(run_tf -chdir=examples/azure-functions output -raw db_fqdn)"
    resource_group="$(run_tf -chdir=examples/azure-functions output -raw resource_group_name)"

    run_functions_checks "$url" "$db_fqdn" "$resource_group"
  fi

  if [[ "$CHECK_IDEMPOTENCY" == "true" ]]; then
    assert_idempotent_plan "examples/azure-functions"
  fi

  log_info "Functions stack checks passed"
  log_info "Functions URL: $(run_tf -chdir=examples/azure-functions output -raw honua_url)"
}

destroy_aca_stack() {
  if [[ "$ACA_APPLIED" != "true" ]]; then
    return
  fi

  log_info "Destroying Azure ACA stack"
  set_aca_tf_vars
  run_tf -chdir=examples/azure destroy -input=false -auto-approve -no-color || log_warn "ACA destroy encountered errors"
}

destroy_functions_stack() {
  if [[ "$FUNCTIONS_APPLIED" != "true" ]]; then
    return
  fi

  log_info "Destroying Azure Functions stack"
  set_functions_tf_vars
  run_tf -chdir=examples/azure-functions destroy -input=false -auto-approve -no-color || log_warn "Functions destroy encountered errors"
}

destroy_data_stack() {
  if [[ "$DATA_APPLIED" != "true" ]]; then
    return
  fi

  log_info "Destroying Azure data stack"
  set_data_tf_vars
  run_tf -chdir=examples/azure-data destroy -input=false -auto-approve -no-color || log_warn "Data stack destroy encountered errors"
}

delete_rg_if_exists() {
  local resource_group="$1"

  if run_az group show -n "$resource_group" >/dev/null 2>&1; then
    log_warn "Janitor submitting resource group delete for $resource_group"
    run_az group delete --name "$resource_group" --yes --no-wait || log_warn "Failed to submit delete for $resource_group"
  fi
}

janitor_delete_resource_groups() {
  if [[ "$DATA_APPLIED" == "true" ]]; then
    if [[ -n "$DATA_RESOURCE_GROUP" ]]; then
      delete_rg_if_exists "$DATA_RESOURCE_GROUP"
    else
      delete_rg_if_exists "${DATA_NAME_PREFIX}-${ENVIRONMENT}-data-rg"
    fi
  fi

  if [[ "$STACK" == "aca" || "$STACK" == "both" ]]; then
    delete_rg_if_exists "${ACA_NAME_PREFIX}-${ENVIRONMENT}-rg"
  fi

  if [[ "$STACK" == "functions" || "$STACK" == "both" ]]; then
    delete_rg_if_exists "${FUNCTIONS_NAME_PREFIX}-${ENVIRONMENT}-rg"
  fi
}

verify_no_leaks() {
  local count
  local i

  for i in {1..10}; do
    count="$(run_az resource list --tag ValidationRunId="$VALIDATION_RUN_ID" --query "length(@)" -o tsv || echo 0)"
    if [[ "$count" == "0" ]]; then
      log_info "Leak janitor check passed (no tagged resources remain)"
      return 0
    fi
    sleep 15
  done

  log_error "Leak janitor check failed: resources tagged ValidationRunId=$VALIDATION_RUN_ID still exist"
  run_az resource list --tag ValidationRunId="$VALIDATION_RUN_ID" -o table || true
  return 1
}

cleanup() {
  local exit_code="$?"

  if [[ "$AUTO_DESTROY" == "true" ]]; then
    destroy_functions_stack
    destroy_aca_stack
    destroy_data_stack
    janitor_delete_resource_groups
    verify_no_leaks || exit_code=1
  else
    log_warn "Auto-destroy disabled; resources were left in Azure"
  fi

  if [[ -n "$TEMP_TF_ROOT" && -d "$TEMP_TF_ROOT" ]]; then
    rm -rf "$TEMP_TF_ROOT"
  fi

  if [[ "$exit_code" -ne 0 ]]; then
    log_error "Azure Terraform integration run failed"
  fi

  exit "$exit_code"
}

main() {
  parse_args "$@"
  require_command docker
  require_command curl
  require_env \
    ARM_CLIENT_ID \
    ARM_CLIENT_SECRET \
    ARM_TENANT_ID \
    ARM_SUBSCRIPTION_ID \
    HONUA_ADMIN_PASSWORD \
    HONUA_DB_PASSWORD

  normalize_identifiers
  validate_existing_resource_inputs
  configure_data_stack_mode
  assert_cost_guardrail
  run_quota_preflight
  detect_db_firewall_ips
  build_tf_image_if_needed
  prepare_tf_workspace

  trap cleanup EXIT

  log_info "Starting Azure Terraform integration test"
  log_info "Validation run ID: $VALIDATION_RUN_ID"
  log_info "Stack selection: $STACK"
  log_info "Region: $LOCATION"
  log_info "Environment: $ENVIRONMENT"
  log_info "Data prefix: $DATA_NAME_PREFIX"
  log_info "Data DB SKU/storage: $DATA_DB_SKU_NAME / ${DATA_DB_STORAGE_MB}MB"
  log_info "Data Redis SKU/family/capacity: $DATA_REDIS_SKU_NAME/$DATA_REDIS_FAMILY/$DATA_REDIS_CAPACITY"
  log_info "ACA prefix: $ACA_NAME_PREFIX"
  log_info "Functions prefix: $FUNCTIONS_NAME_PREFIX"
  log_info "DB firewall range: $DB_FIREWALL_START_IP - $DB_FIREWALL_END_IP"
  if [[ -n "$EXISTING_DB_FQDN" ]]; then
    log_info "Reusing existing DB FQDN: $EXISTING_DB_FQDN"
  fi
  if [[ -n "$EXISTING_REDIS_CONNECTION_STRING" ]]; then
    log_info "Reusing existing Redis connection string"
  fi
  log_info "Ready SLO seconds: $READY_SLO_SECONDS"
  log_info "Max load error rate: ${MAX_LOAD_ERROR_RATE_PERCENT}%"

  apply_data_stack

  if [[ "$STACK" == "aca" || "$STACK" == "both" ]]; then
    apply_aca_stack
  fi

  if [[ "$STACK" == "functions" || "$STACK" == "both" ]]; then
    apply_functions_stack
  fi

  log_info "Azure Terraform integration checks completed successfully"
}

main "$@"
