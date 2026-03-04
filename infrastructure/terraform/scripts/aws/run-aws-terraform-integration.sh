#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT=""
SEARCH_DIR="$SCRIPT_DIR"
while [[ "$SEARCH_DIR" != "/" ]]; do
  if [[ -f "$SEARCH_DIR/Honua.sln" ]]; then
    REPO_ROOT="$SEARCH_DIR"
    break
  fi
  SEARCH_DIR="$(dirname "$SEARCH_DIR")"
done

if [[ -z "$REPO_ROOT" ]]; then
  REPO_ROOT="$(git -C "$SCRIPT_DIR" rev-parse --show-toplevel 2>/dev/null || true)"
fi

if [[ -z "$REPO_ROOT" ]]; then
  echo "[ERROR] Could not determine repository root from $SCRIPT_DIR" >&2
  exit 1
fi

STACK="both"
REGION="${AWS_REGION_OVERRIDE:-us-east-1}"
ENVIRONMENT="${AWS_TF_ENVIRONMENT:-it}"
NAME_PREFIX_BASE="${AWS_TF_NAME_PREFIX_BASE:-h$(date -u +%m%d%H%M)$((RANDOM % 10))}"
DEFAULT_HONUA_IMAGE="ghcr.io/honua-io/honua-server:latest"
DEFAULT_HONUA_AOT_IMAGE="ghcr.io/honua-io/honua-server:latest-aot"
DEFAULT_LAMBDA_TAG_SUFFIX="-lambda"
DEFAULT_LAMBDA_AOT_TAG_SUFFIX="-lambda-aot"
USE_AOT="${HONUA_USE_AOT:-false}"
ECS_IMAGE="${HONUA_AWS_ECS_IMAGE:-$DEFAULT_HONUA_IMAGE}"
SERVERLESS_IMAGE="${HONUA_AWS_SERVERLESS_IMAGE:-}"
ECS_PREVIOUS_IMAGE="${HONUA_AWS_ECS_PREVIOUS_IMAGE:-}"
SERVERLESS_PREVIOUS_IMAGE="${HONUA_AWS_SERVERLESS_PREVIOUS_IMAGE:-}"
AUTO_DESTROY=true
DESTROY_DATA="${HONUA_AWS_DESTROY_DATA:-false}"
QUICK_SCALE=true
CHECK_IDEMPOTENCY=true
CHECK_PROTOCOLS=true
RUN_DB_RESILIENCE=true
RUN_UPGRADE_ROLLBACK=false
RUN_QUOTA_PREFLIGHT=true
TIMEOUT_SECONDS="${HONUA_AWS_TEST_TIMEOUT_SECONDS:-900}"
LOAD_REQUESTS="${HONUA_AWS_LOAD_REQUESTS:-120}"
LOAD_CONCURRENCY="${HONUA_AWS_LOAD_CONCURRENCY:-20}"
ECS_DESIRED_COUNT=1
ECS_SCALE_TARGET_DESIRED_COUNT=2
READY_SLO_SECONDS="${HONUA_READY_SLO_SECONDS:-600}"
MAX_LOAD_ERROR_RATE_PERCENT="${HONUA_MAX_LOAD_ERROR_RATE_PERCENT:-0}"
MAX_RUN_COST_USD="${HONUA_MAX_RUN_COST_USD:-0}"
DB_INGRESS_CIDR="${HONUA_AWS_DB_INGRESS_CIDR:-}"
EXISTING_DB_ENDPOINT="${HONUA_AWS_EXISTING_DB_ENDPOINT:-}"
EXISTING_DB_CONNECTION_STRING="${HONUA_AWS_EXISTING_DB_CONNECTION_STRING:-}"
EXISTING_REDIS_CONNECTION_STRING="${HONUA_AWS_EXISTING_REDIS_CONNECTION_STRING:-}"
EXISTING_VPC_ID="${HONUA_AWS_EXISTING_VPC_ID:-}"
EXISTING_VPC_CIDR="${HONUA_AWS_EXISTING_VPC_CIDR:-}"
EXISTING_PUBLIC_SUBNET_IDS="${HONUA_AWS_EXISTING_PUBLIC_SUBNET_IDS:-}"
EXISTING_PRIVATE_SUBNET_IDS="${HONUA_AWS_EXISTING_PRIVATE_SUBNET_IDS:-}"
POSTGIS_READINESS_MAX_ATTEMPTS="${HONUA_AWS_POSTGIS_READINESS_MAX_ATTEMPTS:-120}"
POSTGIS_READINESS_SLEEP_SECONDS="${HONUA_AWS_POSTGIS_READINESS_SLEEP_SECONDS:-10}"
TF_IMAGE="${HONUA_TERRAFORM_IMAGE:-honua-terraform-psql:1.8.5}"
AWS_CLI_IMAGE="${HONUA_AWS_CLI_IMAGE:-amazon/aws-cli:2.17.61}"
PLAN_ARTIFACT_DIR="${HONUA_TF_PLAN_ARTIFACT_DIR:-}"
ALLOW_DESTROY_PLAN="${HONUA_ALLOW_DESTROY_PLAN:-false}"
TTL_HOURS="${HONUA_TTL_HOURS:-8}"
VALIDATION_RUN_ID="${HONUA_VALIDATION_RUN_ID:-aws-$(date -u +%Y%m%d%H%M%S)}"
FORCE_DOCKER_TF="${HONUA_FORCE_DOCKER_TF:-false}"
FORCE_DOCKER_PG_TOOLS="${HONUA_FORCE_DOCKER_PG_TOOLS:-false}"
DATA_CACHE_FILE="${HONUA_AWS_DATA_CACHE_FILE:-/tmp/honua-aws-data-reuse.env}"
FORCE_NEW_DATA=false

TEMP_TF_ROOT=""
ECS_APPLIED=false
SERVERLESS_APPLIED=false
DATA_APPLIED=false
DATA_CREATED=false

ECS_NAME_PREFIX=""
SERVERLESS_NAME_PREFIX=""
DATA_NAME_PREFIX=""
EXPIRES_AT_UTC=""
DB_PASSWORD_EFFECTIVE=""
USE_DOCKER_TF=false
USE_DOCKER_AWS_CLI=false
USE_DOCKER_PG_TOOLS=false

usage() {
  cat <<USAGE
Run live Terraform integration tests for AWS data, ECS, and AWS serverless.

Usage:
  ./infrastructure/terraform/scripts/aws/run-aws-terraform-integration.sh [options]

Options:
  --stack <data|ecs|serverless|both>   Stack to test (default: both)
  --region <aws-region>                AWS region (default: us-east-1)
  --environment <name>                 Environment suffix in names (default: it)
  --name-prefix-base <prefix>          Base prefix for generated resource names
  --aot                                Use latest-aot for ECS; map serverless tag '*-lambda' -> '*-lambda-aot' when provided (JIT is debug fallback)
  --ecs-image <image>                  ECS container image
  --serverless-image <ecr-uri>         Lambda container image URI (ECR)
  --ecs-previous-image <image>         Previous ECS image for upgrade/rollback validation
  --serverless-previous-image <image>  Previous serverless image for upgrade/rollback validation
  --upgrade-rollback                   Enable upgrade/rollback validation sequence
  --db-ingress-cidr <cidr>             CIDR allowed to reach RDS for PostGIS enablement
  --existing-db-endpoint <endpoint>    Reuse existing PostgreSQL endpoint
  --existing-db-connection <string>    Reuse existing PostgreSQL connection string
  --existing-redis-connection <str>    Reuse existing Redis connection string
  --existing-vpc-id <vpc-id>           Reuse existing VPC ID
  --existing-vpc-cidr <cidr>           CIDR for existing VPC
  --existing-public-subnets <json>     JSON list of existing public subnet IDs
  --existing-private-subnets <json>    JSON list of existing private subnet IDs
  --timeout-seconds <n>                Health wait timeout per stack (default: 900)
  --max-ready-seconds <n>              Ready SLO threshold (default: 600)
  --max-load-error-rate <percent>      Max allowed load error rate (default: 0)
  --max-run-cost-usd <n>               Max allowed estimated run cost (0 disables cap)
  --plan-artifact-dir <path>           Directory to persist plan artifacts
  --allow-destroy-plan                 Allow plans containing resource destroys
  --ttl-hours <n>                      TTL tag value for provisioned resources (default: 8)
  --skip-quota-preflight               Skip AWS quota preflight checks
  --skip-idempotency                   Skip post-apply zero-drift plan assertion
  --skip-protocol-checks               Skip REST/OGC/OData/admin auth + admin CRUD/query smoke checks
  --skip-db-resilience                 Skip DB backup/restore drill
  --no-scale-check                     Skip quick ECS scale check
  --destroy-data                       Destroy auto-created data stack during cleanup (default: keep for reuse)
  --force-new-data                     Ignore cached/existing data inputs and create a fresh data stack
  --no-destroy                         Keep resources after test run
  --help, -h                           Show this help

Required environment variables:
  AWS_ACCESS_KEY_ID
  AWS_SECRET_ACCESS_KEY
  HONUA_ADMIN_PASSWORD (at least 32 chars)
  HONUA_DB_PASSWORD

Optional environment variables:
  AWS_SESSION_TOKEN
  HONUA_AWS_EXISTING_DB_ENDPOINT
  HONUA_AWS_EXISTING_DB_CONNECTION_STRING
  HONUA_AWS_EXISTING_REDIS_CONNECTION_STRING
  HONUA_AWS_EXISTING_VPC_ID
  HONUA_AWS_EXISTING_VPC_CIDR
  HONUA_AWS_EXISTING_PUBLIC_SUBNET_IDS
  HONUA_AWS_EXISTING_PRIVATE_SUBNET_IDS
  HONUA_AWS_DESTROY_DATA
  HONUA_AWS_DATA_CACHE_FILE
  HONUA_AWS_POSTGIS_READINESS_MAX_ATTEMPTS
  HONUA_AWS_POSTGIS_READINESS_SLEEP_SECONDS
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

extract_connection_string_password() {
  local connection_string="$1"
  local field

  IFS=';' read -r -a fields <<< "$connection_string"
  for field in "${fields[@]}"; do
    field="${field#"${field%%[![:space:]]*}"}"
    case "$field" in
      [Pp]assword=*)
        printf '%s' "${field#*=}"
        return 0
        ;;
    esac
  done

  return 1
}

resolve_db_password_for_checks() {
  DB_PASSWORD_EFFECTIVE="$HONUA_DB_PASSWORD"

  if [[ -n "$EXISTING_DB_CONNECTION_STRING" ]]; then
    local parsed_password
    if parsed_password="$(extract_connection_string_password "$EXISTING_DB_CONNECTION_STRING")" && [[ -n "$parsed_password" ]]; then
      DB_PASSWORD_EFFECTIVE="$parsed_password"
      log_info "Using DB password parsed from existing DB connection string for smoke checks"
    else
      log_warn "Could not parse password from existing DB connection string; using HONUA_DB_PASSWORD for smoke checks"
    fi
  fi
}

validate_admin_password() {
  if (( ${#HONUA_ADMIN_PASSWORD} < 32 )); then
    log_error "HONUA_ADMIN_PASSWORD must be at least 32 characters."
    log_error "Reason: this value is used for both HONUA_ADMIN_PASSWORD and Security__ConnectionEncryption__MasterKey in Terraform app modules."
    exit 1
  fi
}

parse_args() {
  while [[ $# -gt 0 ]]; do
    case "$1" in
      --stack)
        STACK="$2"
        shift 2
        ;;
      --region)
        REGION="$2"
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
      --aot)
        USE_AOT=true
        shift
        ;;
      --ecs-image)
        ECS_IMAGE="$2"
        shift 2
        ;;
      --serverless-image)
        SERVERLESS_IMAGE="$2"
        shift 2
        ;;
      --ecs-previous-image)
        ECS_PREVIOUS_IMAGE="$2"
        shift 2
        ;;
      --serverless-previous-image)
        SERVERLESS_PREVIOUS_IMAGE="$2"
        shift 2
        ;;
      --upgrade-rollback)
        RUN_UPGRADE_ROLLBACK=true
        shift
        ;;
      --db-ingress-cidr)
        DB_INGRESS_CIDR="$2"
        shift 2
        ;;
      --existing-db-endpoint)
        EXISTING_DB_ENDPOINT="$2"
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
      --existing-vpc-id)
        EXISTING_VPC_ID="$2"
        shift 2
        ;;
      --existing-vpc-cidr)
        EXISTING_VPC_CIDR="$2"
        shift 2
        ;;
      --existing-public-subnets)
        EXISTING_PUBLIC_SUBNET_IDS="$2"
        shift 2
        ;;
      --existing-private-subnets)
        EXISTING_PRIVATE_SUBNET_IDS="$2"
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
      --destroy-data)
        DESTROY_DATA=true
        shift
        ;;
      --force-new-data)
        FORCE_NEW_DATA=true
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

  if [[ "$STACK" != "data" && "$STACK" != "ecs" && "$STACK" != "serverless" && "$STACK" != "both" ]]; then
    log_error "Invalid --stack value: $STACK"
    exit 1
  fi
}

apply_aot_mode() {
  if [[ "$USE_AOT" != "true" ]]; then
    return
  fi

  if [[ "$ECS_IMAGE" == "$DEFAULT_HONUA_IMAGE" ]]; then
    ECS_IMAGE="$DEFAULT_HONUA_AOT_IMAGE"
  fi

  if [[ -n "$SERVERLESS_IMAGE" && "$SERVERLESS_IMAGE" == *:* ]]; then
    local serverless_tag
    serverless_tag="${SERVERLESS_IMAGE##*:}"
    if [[ "$serverless_tag" == *"$DEFAULT_LAMBDA_TAG_SUFFIX" && "$serverless_tag" != *"$DEFAULT_LAMBDA_AOT_TAG_SUFFIX" ]]; then
      SERVERLESS_IMAGE="${SERVERLESS_IMAGE%:*}:${serverless_tag}-aot"
    fi
  fi
}

normalize_identifiers() {
  ENVIRONMENT="$(echo "$ENVIRONMENT" | tr '[:upper:]' '[:lower:]' | tr -cd 'a-z0-9-')"
  NAME_PREFIX_BASE="$(echo "$NAME_PREFIX_BASE" | tr '[:upper:]' '[:lower:]' | tr -cd 'a-z0-9')"

  if [[ -z "$ENVIRONMENT" || -z "$NAME_PREFIX_BASE" ]]; then
    log_error "Environment/name prefix became empty after normalization"
    exit 1
  fi

  NAME_PREFIX_BASE="${NAME_PREFIX_BASE:0:10}"
  ECS_NAME_PREFIX="${NAME_PREFIX_BASE}ecs"
  SERVERLESS_NAME_PREFIX="${NAME_PREFIX_BASE}sl"
  DATA_NAME_PREFIX="${NAME_PREFIX_BASE}data"
}

detect_db_ingress_cidr() {
  if [[ -n "$EXISTING_DB_CONNECTION_STRING" ]]; then
    DB_INGRESS_CIDR=""
    log_info "Using existing DB connection string; skipping DB ingress CIDR detection"
    return
  fi

  if [[ -n "$DB_INGRESS_CIDR" ]]; then
    return
  fi

  local ip
  ip="$(curl -fsS https://checkip.amazonaws.com | tr -d '[:space:]')"
  if [[ -z "$ip" ]]; then
    log_error "Failed to detect public IP for db ingress"
    exit 1
  fi

  DB_INGRESS_CIDR="${ip}/32"
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

configure_runtime_tools() {
  if [[ "$FORCE_DOCKER_TF" == "true" ]]; then
    require_command docker
    build_tf_image_if_needed
    USE_DOCKER_TF=true
  elif command -v terraform >/dev/null 2>&1; then
    USE_DOCKER_TF=false
  else
    require_command docker
    build_tf_image_if_needed
    USE_DOCKER_TF=true
  fi

  if command -v aws >/dev/null 2>&1; then
    USE_DOCKER_AWS_CLI=false
  else
    require_command docker
    USE_DOCKER_AWS_CLI=true
  fi

  if [[ "$FORCE_DOCKER_PG_TOOLS" == "true" ]]; then
    require_command docker
    USE_DOCKER_PG_TOOLS=true
  elif command -v psql >/dev/null 2>&1 && command -v pg_dump >/dev/null 2>&1 && command -v pg_restore >/dev/null 2>&1; then
    USE_DOCKER_PG_TOOLS=false
  else
    require_command docker
    USE_DOCKER_PG_TOOLS=true
  fi

  log_info "Terraform executor: $([[ "$USE_DOCKER_TF" == "true" ]] && echo docker || echo local)"
  log_info "AWS CLI executor: $([[ "$USE_DOCKER_AWS_CLI" == "true" ]] && echo docker || echo local)"
  log_info "Postgres tools executor: $([[ "$USE_DOCKER_PG_TOOLS" == "true" ]] && echo docker || echo local)"
}

prepare_tf_workspace() {
  TEMP_TF_ROOT="$(mktemp -d)"
  cp -R "$REPO_ROOT/infrastructure/terraform" "$TEMP_TF_ROOT/terraform"
}

run_tf() {
  if [[ "$USE_DOCKER_TF" == "true" ]]; then
    docker run --rm \
      -e AWS_ACCESS_KEY_ID \
      -e AWS_SECRET_ACCESS_KEY \
      -e AWS_SESSION_TOKEN \
      -e AWS_REGION \
      -e AWS_DEFAULT_REGION \
      -e TF_VAR_region \
      -e TF_VAR_environment \
      -e TF_VAR_name_prefix \
      -e TF_VAR_honua_admin_password \
      -e TF_VAR_db_password \
      -e TF_VAR_existing_db_endpoint \
      -e TF_VAR_existing_db_connection_string \
      -e TF_VAR_existing_vpc_id \
      -e TF_VAR_existing_vpc_cidr \
      -e TF_VAR_existing_public_subnet_ids \
      -e TF_VAR_existing_private_subnet_ids \
      -e TF_VAR_honua_image \
      -e TF_VAR_honua_image_uri \
      -e TF_VAR_enable_postgis \
      -e TF_VAR_redis_enabled \
      -e TF_VAR_redis_connection_string \
      -e TF_VAR_db_publicly_accessible \
      -e TF_VAR_db_additional_ingress_cidrs \
      -e TF_VAR_desired_count \
      -e TF_VAR_skip_migrations \
      -e TF_VAR_tags \
      -e TF_IN_AUTOMATION=true \
      -v "$TEMP_TF_ROOT/terraform:/workspace" \
      -w /workspace \
      "$TF_IMAGE" "$@"
    return
  fi

  (
    cd "$TEMP_TF_ROOT/terraform"
    TF_IN_AUTOMATION=true terraform "$@"
  )
}

run_aws() {
  if [[ "$USE_DOCKER_AWS_CLI" == "true" ]]; then
    docker run --rm \
      -e AWS_ACCESS_KEY_ID \
      -e AWS_SECRET_ACCESS_KEY \
      -e AWS_SESSION_TOKEN \
      -e AWS_REGION \
      -e AWS_DEFAULT_REGION \
      "$AWS_CLI_IMAGE" "$@"
    return
  fi

  AWS_PAGER="" aws --region "$REGION" "$@"
}

pg_conn() {
  local db_host="$1"
  local db_name="$2"
  printf "host=%s port=5432 dbname=%s user=honua sslmode=require" "$db_host" "$db_name"
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
  run_tf_apply_with_auth_retry "$root" "$plan_file"
}

run_tf_apply_with_auth_retry() {
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

    if grep -Eq "ExpiredToken|ExpiredTokenException|RequestExpired" "$apply_log" && [[ "$attempt" -lt 2 ]]; then
      log_warn "Terraform apply failed due to expired AWS credentials; retrying apply once"
      rm -f "$apply_log"
      continue
    fi

    rm -f "$apply_log"
    return "$exit_code"
  done
}

normalize_base_url() {
  local base_url="${1%/}"
  if [[ "$base_url" =~ ^https?:// ]]; then
    printf '%s\n' "$base_url"
    return
  fi

  printf 'https://%s\n' "$base_url"
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
      if ! curl -fsSL --max-time 20 "$target_url" >/dev/null; then
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
  local admin_api_key
  local status

  normalized="$(normalize_base_url "$base_url")"
  admin_api_key="${HONUA_ADMIN_PASSWORD}"

  check_endpoint() {
    local endpoint="$1"
    local endpoint_status

    endpoint_status="$(curl -sS -o /dev/null -w "%{http_code}" --max-time 20 "$endpoint" || true)"
    if [[ "$endpoint_status" == 2* || "$endpoint_status" == 3* ]]; then
      return 0
    fi

    if [[ "$endpoint_status" == "401" || "$endpoint_status" == "403" ]]; then
      curl -fsSL --max-time 20 \
        -H "X-API-Key: $admin_api_key" \
        "$endpoint" >/dev/null
      return 0
    fi

    log_error "Protocol smoke endpoint failed: $endpoint returned HTTP $endpoint_status"
    return 1
  }

  check_endpoint "${normalized}/rest/services?f=pjson"
  check_endpoint "${normalized}/ogc/features"
  check_endpoint "${normalized}/odata"

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

sql_escape() {
  printf '%s' "$1" | sed "s/'/''/g"
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

  if [[ "$USE_DOCKER_PG_TOOLS" == "true" ]]; then
    docker run --rm \
      -e PGPASSWORD="$DB_PASSWORD_EFFECTIVE" \
      -v "$sql_file:/tmp/smoke.sql:ro" \
      postgres:16-alpine \
      sh -c "psql '$(pg_conn "$db_host" "honua")' -v ON_ERROR_STOP=1 -f /tmp/smoke.sql" >/dev/null
  else
    PGPASSWORD="$DB_PASSWORD_EFFECTIVE" \
      psql "$(pg_conn "$db_host" "honua")" -v ON_ERROR_STOP=1 -f "$sql_file" >/dev/null
  fi

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

    local cleanup_db_host="${db_host:-}"
    local cleanup_table_name="${table_name:-}"
    local cleanup_layer_id="${layer_id:-}"
    local cleanup_service_name="${service_name:-}"
    local cleanup_connection_id="${connection_id:-}"
    local cleanup_normalized="${normalized:-}"

    if [[ -z "$cleanup_db_host" ]]; then
      return 0
    fi

    run_db_sql "$cleanup_db_host" "DROP TABLE IF EXISTS public.${cleanup_table_name};" || true

    if [[ -n "$cleanup_layer_id" ]]; then
      run_db_sql "$cleanup_db_host" "
        DELETE FROM features WHERE layer_id = ${cleanup_layer_id};
        DELETE FROM honua.layer_fields WHERE layer_id = ${cleanup_layer_id};
        DELETE FROM honua.service_layers WHERE layer_id = ${cleanup_layer_id};
        DELETE FROM honua.layers WHERE layer_id = ${cleanup_layer_id};
      " || true
    fi

    run_db_sql "$cleanup_db_host" "DELETE FROM honua.services WHERE service_name = '$(sql_escape "$cleanup_service_name")';" || true

    if [[ -n "$cleanup_connection_id" ]]; then
      curl -sSL --max-time 20 -X DELETE \
        -H "X-API-Key: $HONUA_ADMIN_PASSWORD" \
        "${cleanup_normalized}/api/v1/admin/connections/${cleanup_connection_id}" >/dev/null || true
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
{"name":"$(json_escape "$connection_name")","description":"Terraform smoke test connection","host":"$(json_escape "$db_host")","port":5432,"databaseName":"honua","username":"honua","password":"$(json_escape "$DB_PASSWORD_EFFECTIVE")","sslRequired":true,"sslMode":"Require"}
JSON
)"

  create_connection_response="$(curl -fsSL --max-time 20 -X POST \
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

  publish_layer_response="$(curl -fsSL --max-time 20 -X POST \
    -H "Content-Type: application/json" \
    -H "X-API-Key: $HONUA_ADMIN_PASSWORD" \
    -d "$publish_layer_payload" \
    "${normalized}/api/v1/admin/connections/${connection_id}/layers")"

  layer_id="$(extract_json_number_field "$publish_layer_response" "layerId")"
  if [[ -z "$layer_id" ]]; then
    log_error "Admin CRUD smoke failed: could not parse layerId from publish response"
    return 1
  fi

  run_db_sql "$db_host" "
    INSERT INTO features (layer_id, geometry, attributes)
    VALUES (
      ${layer_id},
      ST_SetSRID(ST_Point(1, 1), 4326),
      jsonb_build_object('id', 1, 'name', 'Smoke Feature', 'population', 1)
    );
  "

  query_url="${normalized}/rest/services/${service_name}/FeatureServer/${layer_id}/query?where=1%3D1&outFields=id,name,population&f=pjson"
  query_response="$(curl -fsSL --max-time 20 \
    -H "X-API-Key: $HONUA_ADMIN_PASSWORD" \
    "$query_url")"

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

verify_postgis_extensions() {
  local db_endpoint="$1"
  local extensions

  if [[ "$USE_DOCKER_PG_TOOLS" == "true" ]]; then
    extensions="$(docker run --rm \
      -e PGPASSWORD="$DB_PASSWORD_EFFECTIVE" \
      postgres:16-alpine \
      sh -c "psql '$(pg_conn "$db_endpoint" "honua")' -v ON_ERROR_STOP=1 -tA -c \"SELECT extname FROM pg_extension WHERE extname IN ('postgis','postgis_raster') ORDER BY extname;\"" || true)"
  else
    extensions="$(PGPASSWORD="$DB_PASSWORD_EFFECTIVE" \
      psql "$(pg_conn "$db_endpoint" "honua")" -v ON_ERROR_STOP=1 -tA -c "SELECT extname FROM pg_extension WHERE extname IN ('postgis','postgis_raster') ORDER BY extname;" || true)"
  fi

  if [[ "$extensions" != *"postgis"* || "$extensions" != *"postgis_raster"* ]]; then
    log_error "Expected postgis + postgis_raster extensions not both present on $db_endpoint"
    log_error "Observed extensions output: ${extensions:-<none>}"
    return 1
  fi

  log_info "Verified extensions on $db_endpoint: postgis + postgis_raster"
}

verify_db_backup_restore() {
  local db_endpoint="$1"
  local extensions_count
  local dump_file

  dump_file="$(mktemp)"

  if [[ "$USE_DOCKER_PG_TOOLS" == "true" ]]; then
    docker run --rm \
      -e PGPASSWORD="$DB_PASSWORD_EFFECTIVE" \
      -v "$dump_file:/tmp/honua.dump" \
      postgres:16-alpine \
      sh -c "set -e; \
        pg_dump '$(pg_conn "$db_endpoint" "honua")' -Fc -f /tmp/honua.dump; \
        psql '$(pg_conn "$db_endpoint" "postgres")' -v ON_ERROR_STOP=1 -c 'DROP DATABASE IF EXISTS honua_restore_check'; \
        psql '$(pg_conn "$db_endpoint" "postgres")' -v ON_ERROR_STOP=1 -c 'CREATE DATABASE honua_restore_check'; \
        pg_restore --no-owner --no-privileges -d '$(pg_conn "$db_endpoint" "honua_restore_check")' /tmp/honua.dump >/dev/null;" >/dev/null

    extensions_count="$(docker run --rm \
      -e PGPASSWORD="$DB_PASSWORD_EFFECTIVE" \
      postgres:16-alpine \
      sh -c "psql '$(pg_conn "$db_endpoint" "honua_restore_check")' -tA -c \"SELECT COUNT(*) FROM pg_extension WHERE extname IN ('postgis','postgis_raster');\"" | tr -d '[:space:]')"

    docker run --rm \
      -e PGPASSWORD="$DB_PASSWORD_EFFECTIVE" \
      postgres:16-alpine \
      sh -c "psql '$(pg_conn "$db_endpoint" "postgres")' -v ON_ERROR_STOP=1 -c 'DROP DATABASE IF EXISTS honua_restore_check'" >/dev/null
  else
    PGPASSWORD="$DB_PASSWORD_EFFECTIVE" pg_dump "$(pg_conn "$db_endpoint" "honua")" -Fc -f "$dump_file"
    PGPASSWORD="$DB_PASSWORD_EFFECTIVE" psql "$(pg_conn "$db_endpoint" "postgres")" -v ON_ERROR_STOP=1 -c 'DROP DATABASE IF EXISTS honua_restore_check' >/dev/null
    PGPASSWORD="$DB_PASSWORD_EFFECTIVE" psql "$(pg_conn "$db_endpoint" "postgres")" -v ON_ERROR_STOP=1 -c 'CREATE DATABASE honua_restore_check' >/dev/null
    PGPASSWORD="$DB_PASSWORD_EFFECTIVE" pg_restore --no-owner --no-privileges -d "$(pg_conn "$db_endpoint" "honua_restore_check")" "$dump_file" >/dev/null
    extensions_count="$(PGPASSWORD="$DB_PASSWORD_EFFECTIVE" psql "$(pg_conn "$db_endpoint" "honua_restore_check")" -tA -c "SELECT COUNT(*) FROM pg_extension WHERE extname IN ('postgis','postgis_raster');" | tr -d '[:space:]')"
    PGPASSWORD="$DB_PASSWORD_EFFECTIVE" psql "$(pg_conn "$db_endpoint" "postgres")" -v ON_ERROR_STOP=1 -c 'DROP DATABASE IF EXISTS honua_restore_check' >/dev/null
  fi

  rm -f "$dump_file"

  if [[ "$extensions_count" != "2" ]]; then
    log_error "DB backup/restore drill failed: expected 2 PostGIS extensions in restored DB, got ${extensions_count:-<none>}"
    return 1
  fi

  log_info "DB backup/restore drill passed"
}

wait_for_ecs_running_count() {
  local cluster_name="$1"
  local service_name="$2"
  local expected_min="$3"
  local timeout="$4"
  local start_epoch
  local current

  start_epoch="$(date +%s)"
  while true; do
    current="$(run_aws ecs describe-services --cluster "$cluster_name" --services "$service_name" --query 'services[0].runningCount' --output text 2>/dev/null || echo 0)"

    if [[ -n "$current" ]] && [[ "$current" != "None" ]] && (( current >= expected_min )); then
      log_info "ECS running count reached target: $current >= $expected_min"
      return 0
    fi

    if (( $(date +%s) - start_epoch > timeout )); then
      log_error "Timed out waiting for ECS running count >= $expected_min (current: ${current:-unknown})"
      return 1
    fi

    sleep 15
  done
}

estimate_stack_cost() {
  local stack_name="$1"
  local base=0
  local include_data=false

  case "$stack_name" in
    data) base=25 ;;
    ecs) base=50 ;;
    serverless) base=25 ;;
    both) base=75 ;;
    *) base=0 ;;
  esac

  if [[ "$stack_name" == "data" ]]; then
    include_data=true
  elif ! has_existing_data_inputs; then
    include_data=true
  fi

  if [[ "$include_data" == "true" && "$stack_name" != "data" ]]; then
    base=$((base + 25))
  fi

  echo "$base"
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

run_quota_preflight() {
  local quota
  local required
  local needs_data_stack

  if [[ "$RUN_QUOTA_PREFLIGHT" != "true" ]]; then
    return
  fi

  quota="$(run_aws service-quotas get-service-quota --service-code ec2 --quota-code L-1216C47A --query 'Quota.Value' --output text 2>/dev/null || echo '')"

  required=0
  needs_data_stack=false
  if [[ "$STACK" == "data" ]]; then
    needs_data_stack=true
  elif [[ -z "$EXISTING_DB_CONNECTION_STRING" || -z "$EXISTING_REDIS_CONNECTION_STRING" || -z "$EXISTING_VPC_ID" ]]; then
    needs_data_stack=true
  fi

  if [[ "$needs_data_stack" == "true" ]]; then
    required=$((required + 2))
  fi

  if [[ "$STACK" == "ecs" || "$STACK" == "both" ]]; then
    required=$((required + 4))
  fi
  if [[ "$STACK" == "serverless" || "$STACK" == "both" ]]; then
    required=$((required + 2))
  fi

  if [[ -n "$quota" && "$quota" != "None" ]] && awk -v q="$quota" -v r="$required" 'BEGIN { exit !(r > q) }'; then
    log_error "AWS quota preflight failed: estimated required vCPU $required exceeds EC2 regional quota $quota"
    exit 1
  fi

  log_info "AWS quota preflight passed (EC2 regional vCPU quota=${quota:-unknown}, required~$required)"
}

validate_existing_resource_inputs() {
  local any_existing
  any_existing=false

  if [[ -n "$EXISTING_DB_ENDPOINT" || -n "$EXISTING_DB_CONNECTION_STRING" || -n "$EXISTING_REDIS_CONNECTION_STRING" || -n "$EXISTING_VPC_ID" || -n "$EXISTING_VPC_CIDR" || -n "$EXISTING_PUBLIC_SUBNET_IDS" || -n "$EXISTING_PRIVATE_SUBNET_IDS" ]]; then
    any_existing=true
  fi

  if [[ -n "$EXISTING_DB_ENDPOINT" && -z "$EXISTING_DB_CONNECTION_STRING" ]]; then
    log_error "--existing-db-connection is required when --existing-db-endpoint is provided"
    exit 1
  fi

  if [[ -z "$EXISTING_DB_ENDPOINT" && -n "$EXISTING_DB_CONNECTION_STRING" ]]; then
    log_error "--existing-db-endpoint is required when --existing-db-connection is provided"
    exit 1
  fi

  if [[ "$any_existing" == "true" ]]; then
    if [[ -z "$EXISTING_DB_ENDPOINT" || -z "$EXISTING_DB_CONNECTION_STRING" || -z "$EXISTING_REDIS_CONNECTION_STRING" || -z "$EXISTING_VPC_ID" || -z "$EXISTING_VPC_CIDR" || -z "$EXISTING_PUBLIC_SUBNET_IDS" || -z "$EXISTING_PRIVATE_SUBNET_IDS" ]]; then
      log_error "When reusing AWS data, all values are required: DB endpoint/connection, Redis connection, VPC ID/CIDR, and public/private subnet lists"
      exit 1
    fi
  fi
}

has_existing_data_inputs() {
  [[ -n "$EXISTING_DB_ENDPOINT" &&
    -n "$EXISTING_DB_CONNECTION_STRING" &&
    -n "$EXISTING_REDIS_CONNECTION_STRING" &&
    -n "$EXISTING_VPC_ID" &&
    -n "$EXISTING_VPC_CIDR" &&
    -n "$EXISTING_PUBLIC_SUBNET_IDS" &&
    -n "$EXISTING_PRIVATE_SUBNET_IDS" ]]
}

load_data_reuse_cache() {
  if [[ "$FORCE_NEW_DATA" == "true" ]]; then
    log_info "Force-new-data enabled; ignoring cached AWS data inputs"
    return
  fi

  if has_existing_data_inputs; then
    return
  fi

  if [[ ! -f "$DATA_CACHE_FILE" ]]; then
    return
  fi

  # shellcheck disable=SC1090
  source "$DATA_CACHE_FILE"

  if has_existing_data_inputs; then
    log_info "Loaded AWS data reuse inputs from $DATA_CACHE_FILE"
  else
    log_warn "Data cache file exists but is incomplete: $DATA_CACHE_FILE"
  fi
}

persist_data_reuse_cache() {
  local cache_dir
  local tmp_file

  cache_dir="$(dirname "$DATA_CACHE_FILE")"
  mkdir -p "$cache_dir"

  tmp_file="$(mktemp)"
  chmod 600 "$tmp_file"
  cat > "$tmp_file" <<EOF
EXISTING_DB_ENDPOINT=$(printf '%q' "$EXISTING_DB_ENDPOINT")
EXISTING_DB_CONNECTION_STRING=$(printf '%q' "$EXISTING_DB_CONNECTION_STRING")
EXISTING_REDIS_CONNECTION_STRING=$(printf '%q' "$EXISTING_REDIS_CONNECTION_STRING")
EXISTING_VPC_ID=$(printf '%q' "$EXISTING_VPC_ID")
EXISTING_VPC_CIDR=$(printf '%q' "$EXISTING_VPC_CIDR")
EXISTING_PUBLIC_SUBNET_IDS=$(printf '%q' "$EXISTING_PUBLIC_SUBNET_IDS")
EXISTING_PRIVATE_SUBNET_IDS=$(printf '%q' "$EXISTING_PRIVATE_SUBNET_IDS")
EOF
  mv "$tmp_file" "$DATA_CACHE_FILE"

  log_info "Saved AWS data reuse inputs to $DATA_CACHE_FILE"
}

clear_data_reuse_cache() {
  if [[ -f "$DATA_CACHE_FILE" ]]; then
    rm -f "$DATA_CACHE_FILE"
    log_info "Cleared AWS data reuse cache: $DATA_CACHE_FILE"
  fi
}

set_common_tf_vars() {
  EXPIRES_AT_UTC="$(date -u -d "+${TTL_HOURS} hours" +%Y-%m-%dT%H:%M:%SZ)"

  export AWS_REGION="$REGION"
  export AWS_DEFAULT_REGION="$REGION"
  export TF_VAR_region="$REGION"
  export TF_VAR_environment="$ENVIRONMENT"
  export TF_VAR_honua_admin_password="$HONUA_ADMIN_PASSWORD"
  export TF_VAR_db_password="$HONUA_DB_PASSWORD"
  export TF_VAR_existing_db_endpoint="$EXISTING_DB_ENDPOINT"
  export TF_VAR_existing_db_connection_string="$EXISTING_DB_CONNECTION_STRING"
  export TF_VAR_existing_vpc_id="$EXISTING_VPC_ID"
  export TF_VAR_existing_vpc_cidr="$EXISTING_VPC_CIDR"
  export TF_VAR_existing_public_subnet_ids="${EXISTING_PUBLIC_SUBNET_IDS:-[]}"
  export TF_VAR_existing_private_subnet_ids="${EXISTING_PRIVATE_SUBNET_IDS:-[]}"
  export TF_VAR_enable_postgis="true"
  export TF_VAR_postgis_readiness_max_attempts="$POSTGIS_READINESS_MAX_ATTEMPTS"
  export TF_VAR_postgis_readiness_sleep_seconds="$POSTGIS_READINESS_SLEEP_SECONDS"
  export TF_VAR_redis_enabled="true"
  export TF_VAR_redis_connection_string="$EXISTING_REDIS_CONNECTION_STRING"
  export TF_VAR_db_publicly_accessible="true"
  if [[ -n "$EXISTING_DB_CONNECTION_STRING" ]]; then
    export TF_VAR_db_additional_ingress_cidrs="[]"
  else
    export TF_VAR_db_additional_ingress_cidrs="[\"$DB_INGRESS_CIDR\"]"
  fi
  export TF_VAR_tags="{\"ValidationRunId\":\"$VALIDATION_RUN_ID\",\"TTLHours\":\"$TTL_HOURS\",\"ExpiresAtUTC\":\"$EXPIRES_AT_UTC\",\"Owner\":\"terraform-validation\"}"
}

set_ecs_tf_vars() {
  set_common_tf_vars
  export TF_VAR_name_prefix="$ECS_NAME_PREFIX"
  export TF_VAR_honua_image="$ECS_IMAGE"
  export TF_VAR_desired_count="$ECS_DESIRED_COUNT"

  unset TF_VAR_honua_image_uri
  unset TF_VAR_skip_migrations
}

set_serverless_tf_vars() {
  set_common_tf_vars
  export TF_VAR_name_prefix="$SERVERLESS_NAME_PREFIX"
  export TF_VAR_honua_image_uri="$SERVERLESS_IMAGE"
  export TF_VAR_skip_migrations="true"

  unset TF_VAR_honua_image
  unset TF_VAR_desired_count
}

set_data_tf_vars() {
  set_common_tf_vars
  export TF_VAR_name_prefix="$DATA_NAME_PREFIX"
  export TF_VAR_existing_db_endpoint=""
  export TF_VAR_existing_db_connection_string=""
  export TF_VAR_existing_vpc_id=""
  export TF_VAR_existing_vpc_cidr=""
  export TF_VAR_existing_public_subnet_ids="[]"
  export TF_VAR_existing_private_subnet_ids="[]"
  export TF_VAR_db_publicly_accessible="true"
  export TF_VAR_db_additional_ingress_cidrs="[\"$DB_INGRESS_CIDR\"]"
  export TF_VAR_redis_enabled="true"

  unset TF_VAR_honua_image
  unset TF_VAR_honua_image_uri
  unset TF_VAR_desired_count
  unset TF_VAR_skip_migrations
}

apply_data_stack() {
  log_info "Applying AWS data stack (RDS + Redis)"
  set_data_tf_vars

  run_tf -chdir=examples/aws-data init -input=false -no-color
  plan_apply "examples/aws-data" "data.tfplan" "data"

  EXISTING_DB_ENDPOINT="$(run_tf -chdir=examples/aws-data output -raw db_endpoint)"
  EXISTING_DB_CONNECTION_STRING="$(run_tf -chdir=examples/aws-data output -raw db_connection_string)"
  EXISTING_REDIS_CONNECTION_STRING="$(run_tf -chdir=examples/aws-data output -raw redis_connection_string)"
  EXISTING_VPC_ID="$(run_tf -chdir=examples/aws-data output -raw vpc_id)"
  EXISTING_VPC_CIDR="$(run_tf -chdir=examples/aws-data output -raw vpc_cidr)"
  EXISTING_PUBLIC_SUBNET_IDS="$(run_tf -chdir=examples/aws-data output -json public_subnet_ids | tr -d '\n')"
  EXISTING_PRIVATE_SUBNET_IDS="$(run_tf -chdir=examples/aws-data output -json private_subnet_ids | tr -d '\n')"

  if [[ -z "$EXISTING_DB_ENDPOINT" || -z "$EXISTING_DB_CONNECTION_STRING" || -z "$EXISTING_REDIS_CONNECTION_STRING" || -z "$EXISTING_VPC_ID" || -z "$EXISTING_VPC_CIDR" || -z "$EXISTING_PUBLIC_SUBNET_IDS" || -z "$EXISTING_PRIVATE_SUBNET_IDS" ]]; then
    log_error "AWS data stack output validation failed (missing DB/Redis/VPC output)"
    return 1
  fi

  DATA_APPLIED=true
  DATA_CREATED=true

  if [[ "$CHECK_IDEMPOTENCY" == "true" ]]; then
    assert_idempotent_plan "examples/aws-data"
  fi

  persist_data_reuse_cache
  log_info "AWS data stack ready: vpc=$EXISTING_VPC_ID db=$EXISTING_DB_ENDPOINT"
}

run_ecs_checks() {
  local url="$1"
  local db_endpoint="$2"

  wait_for_ready "$url" "$TIMEOUT_SECONDS"
  if [[ "$CHECK_PROTOCOLS" == "true" ]]; then
    verify_protocol_endpoints "$url"
    run_admin_api_crud_smoke "$url" "$db_endpoint"
  fi
  verify_postgis_extensions "$db_endpoint"
  if [[ "$RUN_DB_RESILIENCE" == "true" ]]; then
    verify_db_backup_restore "$db_endpoint"
  fi
  run_load_probe "$url" "$LOAD_REQUESTS" "$LOAD_CONCURRENCY"
}

run_serverless_checks() {
  local url="$1"
  local db_endpoint="$2"

  wait_for_ready "$url" "$TIMEOUT_SECONDS"
  if [[ "$CHECK_PROTOCOLS" == "true" ]]; then
    verify_protocol_endpoints "$url"
    run_admin_api_crud_smoke "$url" "$db_endpoint"
  fi
  verify_postgis_extensions "$db_endpoint"
  if [[ "$RUN_DB_RESILIENCE" == "true" ]]; then
    verify_db_backup_restore "$db_endpoint"
  fi
  run_load_probe "$url" "$LOAD_REQUESTS" "$LOAD_CONCURRENCY"
}

apply_ecs_stack() {
  local url
  local db_endpoint
  local redis_endpoint
  local cluster_name
  local service_name

  log_info "Applying AWS ECS stack"
  set_ecs_tf_vars

  run_tf -chdir=examples/aws init -input=false -no-color
  # Mark stack as applied before first plan/apply so cleanup destroys partial resources on failed apply.
  ECS_APPLIED=true

  if [[ "$RUN_UPGRADE_ROLLBACK" == "true" ]]; then
    if [[ -z "$ECS_PREVIOUS_IMAGE" || "$ECS_PREVIOUS_IMAGE" == "$ECS_IMAGE" ]]; then
      log_error "ECS upgrade/rollback requires --ecs-previous-image different from --ecs-image"
      return 1
    fi

    export TF_VAR_honua_image="$ECS_PREVIOUS_IMAGE"
    plan_apply "examples/aws" "ecs-prev.tfplan" "ecs-previous"

    url="$(run_tf -chdir=examples/aws output -raw honua_url)"
    db_endpoint="$(run_tf -chdir=examples/aws output -raw db_endpoint)"
    cluster_name="$(run_tf -chdir=examples/aws output -raw ecs_cluster_name)"
    service_name="$(run_tf -chdir=examples/aws output -raw ecs_service_name)"

    if [[ -n "$EXISTING_REDIS_CONNECTION_STRING" ]]; then
      log_info "Using existing Redis connection string; skipping ECS Redis endpoint creation check"
    else
      redis_endpoint="$(run_tf -chdir=examples/aws output -raw redis_primary_endpoint)"
      if [[ -z "$redis_endpoint" || "$redis_endpoint" == "null" ]]; then
        log_error "Redis endpoint was empty for ECS stack"
        return 1
      fi
    fi

    run_ecs_checks "$url" "$db_endpoint"

    export TF_VAR_honua_image="$ECS_IMAGE"
    plan_apply "examples/aws" "ecs-upgrade.tfplan" "ecs-upgrade"
    url="$(run_tf -chdir=examples/aws output -raw honua_url)"
    db_endpoint="$(run_tf -chdir=examples/aws output -raw db_endpoint)"
    run_ecs_checks "$url" "$db_endpoint"

    if [[ "$QUICK_SCALE" == "true" ]]; then
      log_info "Running quick ECS scale validation by raising desired_count to $ECS_SCALE_TARGET_DESIRED_COUNT"
      export TF_VAR_desired_count="$ECS_SCALE_TARGET_DESIRED_COUNT"
      plan_apply "examples/aws" "ecs-scale.tfplan" "ecs-scale"
      wait_for_ecs_running_count "$cluster_name" "$service_name" "$ECS_SCALE_TARGET_DESIRED_COUNT" 900
      export TF_VAR_desired_count="$ECS_DESIRED_COUNT"
      plan_apply "examples/aws" "ecs-scale-reset.tfplan" "ecs-scale-reset"
      if [[ "$ECS_DESIRED_COUNT" =~ ^[0-9]+$ ]] && (( ECS_DESIRED_COUNT > 0 )); then
        wait_for_ecs_running_count "$cluster_name" "$service_name" "$ECS_DESIRED_COUNT" 900
      fi
    fi

    export TF_VAR_honua_image="$ECS_PREVIOUS_IMAGE"
    plan_apply "examples/aws" "ecs-rollback.tfplan" "ecs-rollback"
    run_ecs_checks "$url" "$db_endpoint"

    if [[ "$AUTO_DESTROY" != "true" ]]; then
      export TF_VAR_honua_image="$ECS_IMAGE"
      plan_apply "examples/aws" "ecs-restore-current.tfplan" "ecs-restore-current"
      run_ecs_checks "$url" "$db_endpoint"
    fi

    export TF_VAR_honua_image="$ECS_IMAGE"
  else
    plan_apply "examples/aws" "ecs.tfplan" "ecs"

    url="$(run_tf -chdir=examples/aws output -raw honua_url)"
    db_endpoint="$(run_tf -chdir=examples/aws output -raw db_endpoint)"
    cluster_name="$(run_tf -chdir=examples/aws output -raw ecs_cluster_name)"
    service_name="$(run_tf -chdir=examples/aws output -raw ecs_service_name)"

    if [[ -n "$EXISTING_REDIS_CONNECTION_STRING" ]]; then
      log_info "Using existing Redis connection string; skipping ECS Redis endpoint creation check"
    else
      redis_endpoint="$(run_tf -chdir=examples/aws output -raw redis_primary_endpoint)"
      if [[ -z "$redis_endpoint" || "$redis_endpoint" == "null" ]]; then
        log_error "Redis endpoint was empty for ECS stack"
        return 1
      fi
    fi

    run_ecs_checks "$url" "$db_endpoint"

    if [[ "$QUICK_SCALE" == "true" ]]; then
      log_info "Running quick ECS scale validation by raising desired_count to $ECS_SCALE_TARGET_DESIRED_COUNT"
      export TF_VAR_desired_count="$ECS_SCALE_TARGET_DESIRED_COUNT"
      plan_apply "examples/aws" "ecs-scale.tfplan" "ecs-scale"
      wait_for_ecs_running_count "$cluster_name" "$service_name" "$ECS_SCALE_TARGET_DESIRED_COUNT" 900
      export TF_VAR_desired_count="$ECS_DESIRED_COUNT"
      plan_apply "examples/aws" "ecs-scale-reset.tfplan" "ecs-scale-reset"
      if [[ "$ECS_DESIRED_COUNT" =~ ^[0-9]+$ ]] && (( ECS_DESIRED_COUNT > 0 )); then
        wait_for_ecs_running_count "$cluster_name" "$service_name" "$ECS_DESIRED_COUNT" 900
      fi
    fi
  fi

  ECS_APPLIED=true

  if [[ "$CHECK_IDEMPOTENCY" == "true" ]]; then
    assert_idempotent_plan "examples/aws"
  fi

  log_info "ECS stack checks passed"
  log_info "ECS URL: $(run_tf -chdir=examples/aws output -raw honua_url)"
}

apply_serverless_stack() {
  local url
  local db_endpoint
  local redis_connection

  if [[ -z "$SERVERLESS_IMAGE" ]]; then
    log_error "Serverless image is required. Set HONUA_AWS_SERVERLESS_IMAGE or pass --serverless-image"
    return 1
  fi

  log_info "Applying AWS serverless stack"
  set_serverless_tf_vars

  run_tf -chdir=examples/aws-serverless init -input=false -no-color
  # Mark stack as applied before first plan/apply so cleanup destroys partial resources on failed apply.
  SERVERLESS_APPLIED=true

  if [[ "$RUN_UPGRADE_ROLLBACK" == "true" ]]; then
    if [[ -z "$SERVERLESS_PREVIOUS_IMAGE" || "$SERVERLESS_PREVIOUS_IMAGE" == "$SERVERLESS_IMAGE" ]]; then
      log_error "Serverless upgrade/rollback requires --serverless-previous-image different from --serverless-image"
      return 1
    fi

    export TF_VAR_honua_image_uri="$SERVERLESS_PREVIOUS_IMAGE"
    plan_apply "examples/aws-serverless" "serverless-prev.tfplan" "serverless-previous"

    url="$(run_tf -chdir=examples/aws-serverless output -raw honua_url)"
    db_endpoint="$(run_tf -chdir=examples/aws-serverless output -raw db_endpoint)"
    redis_connection="$(run_tf -chdir=examples/aws-serverless output -raw redis_connection_string)"

    if [[ -z "$redis_connection" || "$redis_connection" == "null" ]]; then
      log_error "Redis connection string was empty for serverless stack"
      return 1
    fi

    run_serverless_checks "$url" "$db_endpoint"

    export TF_VAR_honua_image_uri="$SERVERLESS_IMAGE"
    plan_apply "examples/aws-serverless" "serverless-upgrade.tfplan" "serverless-upgrade"
    url="$(run_tf -chdir=examples/aws-serverless output -raw honua_url)"
    db_endpoint="$(run_tf -chdir=examples/aws-serverless output -raw db_endpoint)"
    run_serverless_checks "$url" "$db_endpoint"

    export TF_VAR_honua_image_uri="$SERVERLESS_PREVIOUS_IMAGE"
    plan_apply "examples/aws-serverless" "serverless-rollback.tfplan" "serverless-rollback"
    run_serverless_checks "$url" "$db_endpoint"

    if [[ "$AUTO_DESTROY" != "true" ]]; then
      export TF_VAR_honua_image_uri="$SERVERLESS_IMAGE"
      plan_apply "examples/aws-serverless" "serverless-restore-current.tfplan" "serverless-restore-current"
      run_serverless_checks "$url" "$db_endpoint"
    fi

    export TF_VAR_honua_image_uri="$SERVERLESS_IMAGE"
  else
    plan_apply "examples/aws-serverless" "serverless.tfplan" "serverless"

    url="$(run_tf -chdir=examples/aws-serverless output -raw honua_url)"
    db_endpoint="$(run_tf -chdir=examples/aws-serverless output -raw db_endpoint)"
    redis_connection="$(run_tf -chdir=examples/aws-serverless output -raw redis_connection_string)"

    if [[ -z "$redis_connection" || "$redis_connection" == "null" ]]; then
      log_error "Redis connection string was empty for serverless stack"
      return 1
    fi

    run_serverless_checks "$url" "$db_endpoint"
  fi

  if [[ "$CHECK_IDEMPOTENCY" == "true" ]]; then
    assert_idempotent_plan "examples/aws-serverless"
  fi

  log_info "Serverless stack checks passed"
  log_info "Serverless URL: $(run_tf -chdir=examples/aws-serverless output -raw honua_url)"
}

destroy_ecs_stack() {
  if [[ "$ECS_APPLIED" != "true" ]]; then
    return
  fi

  log_info "Destroying AWS ECS stack"
  set_ecs_tf_vars
  run_tf -chdir=examples/aws destroy -input=false -auto-approve -no-color || log_warn "ECS destroy encountered errors"
}

destroy_serverless_stack() {
  if [[ "$SERVERLESS_APPLIED" != "true" ]]; then
    return
  fi

  log_info "Destroying AWS serverless stack"
  set_serverless_tf_vars
  run_tf -chdir=examples/aws-serverless destroy -input=false -auto-approve -no-color || log_warn "Serverless destroy encountered errors"
}

destroy_data_stack() {
  if [[ "$DATA_APPLIED" != "true" || "$DATA_CREATED" != "true" ]]; then
    return
  fi

  log_info "Destroying AWS data stack"
  set_data_tf_vars
  if run_tf -chdir=examples/aws-data destroy -input=false -auto-approve -no-color; then
    clear_data_reuse_cache
  else
    log_warn "Data stack destroy encountered errors"
  fi
}

verify_no_leaks() {
  local arns_raw
  local -a arns
  local -a leaking_arns
  local arn
  local i

  is_non_leaking_tagged_arn() {
    local resource_arn="$1"
    local status
    local deleted_date
    local key_state
    local cluster_name
    local service_name
    local service_path

    case "$resource_arn" in
      arn:aws:ecs:*:cluster/*)
        cluster_name="${resource_arn##*/}"
        status="$(run_aws ecs describe-clusters --clusters "$cluster_name" --query 'clusters[0].status' --output text 2>/dev/null || echo MISSING)"
        [[ "$status" != "ACTIVE" ]]
        return
        ;;
      arn:aws:ecs:*:service/*/*)
        service_path="${resource_arn#*:service/}"
        cluster_name="${service_path%%/*}"
        service_name="${service_path#*/}"
        status="$(run_aws ecs describe-services --cluster "$cluster_name" --services "$service_name" --query 'services[0].status' --output text 2>/dev/null || echo MISSING)"
        [[ "$status" != "ACTIVE" ]]
        return
        ;;
      arn:aws:ecs:*:task-definition/*)
        status="$(run_aws ecs describe-task-definition --task-definition "$resource_arn" --query 'taskDefinition.status' --output text 2>/dev/null || echo MISSING)"
        [[ "$status" == "INACTIVE" || "$status" == "MISSING" ]]
        return
        ;;
      arn:aws:secretsmanager:*)
        deleted_date="$(run_aws secretsmanager describe-secret --secret-id "$resource_arn" --query 'DeletedDate' --output text 2>/dev/null || echo MISSING)"
        [[ "$deleted_date" != "None" ]]
        return
        ;;
      arn:aws:kms:*)
        key_state="$(run_aws kms describe-key --key-id "$resource_arn" --query 'KeyMetadata.KeyState' --output text 2>/dev/null || echo MISSING)"
        [[ "$key_state" == "PendingDeletion" || "$key_state" == "PendingReplicaDeletion" || "$key_state" == "MISSING" ]]
        return
        ;;
      *)
        return 1
        ;;
    esac
  }

  collect_leaking_arns() {
    arns_raw="$(run_aws resourcegroupstaggingapi get-resources --tag-filters Key=ValidationRunId,Values="$VALIDATION_RUN_ID" --query 'ResourceTagMappingList[].ResourceARN' --output text 2>/dev/null || echo '')"

    arns=()
    if [[ -n "$arns_raw" && "$arns_raw" != "None" ]]; then
      # AWS CLI text output is tab-delimited for lists.
      # shellcheck disable=SC2206
      arns=( ${arns_raw//$'\t'/$'\n'} )
    fi

    leaking_arns=()
    for arn in "${arns[@]}"; do
      if ! is_non_leaking_tagged_arn "$arn"; then
        leaking_arns+=("$arn")
      fi
    done
  }

  for i in {1..20}; do
    collect_leaking_arns
    if [[ "${#leaking_arns[@]}" -eq 0 ]]; then
      log_info "Leak janitor check passed (no tagged resources remain)"
      return 0
    fi

    log_info "Leak janitor waiting for ${#leaking_arns[@]} resource(s) to clear (attempt $i/20)"
    sleep 15
  done

  log_error "Leak janitor check failed: resources tagged ValidationRunId=$VALIDATION_RUN_ID still exist as active/leaking resources"
  printf '%s\n' "${leaking_arns[@]}" >&2
  return 1
}

cleanup() {
  local exit_code="$?"
  local skip_leak_check=false

  if [[ "$AUTO_DESTROY" == "true" ]]; then
    destroy_serverless_stack
    destroy_ecs_stack
    if [[ "$DESTROY_DATA" == "true" ]]; then
      destroy_data_stack
    elif [[ "$DATA_APPLIED" == "true" && "$DATA_CREATED" == "true" ]]; then
      log_warn "Keeping AWS data stack for reuse (set --destroy-data to tear it down)"
      skip_leak_check=true
    fi

    if [[ "$skip_leak_check" == "false" ]]; then
      verify_no_leaks || exit_code=1
    fi
  else
    log_warn "Auto-destroy disabled; resources were left in AWS"
  fi

  if [[ -n "$TEMP_TF_ROOT" && -d "$TEMP_TF_ROOT" ]]; then
    rm -rf "$TEMP_TF_ROOT" || true
  fi

  if [[ "$exit_code" -ne 0 ]]; then
    log_error "AWS Terraform integration run failed"
  fi

  exit "$exit_code"
}

main() {
  parse_args "$@"
  apply_aot_mode
  require_command curl
  require_env \
    AWS_ACCESS_KEY_ID \
    AWS_SECRET_ACCESS_KEY \
    HONUA_ADMIN_PASSWORD \
    HONUA_DB_PASSWORD

  export AWS_REGION="$REGION"
  export AWS_DEFAULT_REGION="$REGION"

  validate_admin_password
  resolve_db_password_for_checks
  load_data_reuse_cache
  validate_existing_resource_inputs
  normalize_identifiers
  detect_db_ingress_cidr
  configure_runtime_tools
  assert_cost_guardrail
  run_quota_preflight
  prepare_tf_workspace

  trap cleanup EXIT

  log_info "Starting AWS Terraform integration test"
  log_info "Validation run ID: $VALIDATION_RUN_ID"
  log_info "Stack selection: $STACK"
  log_info "AOT mode: $USE_AOT"
  log_info "ECS image: $ECS_IMAGE"
  if [[ -n "$SERVERLESS_IMAGE" ]]; then
    log_info "Serverless image: $SERVERLESS_IMAGE"
  fi
  log_info "Region: $REGION"
  log_info "Environment: $ENVIRONMENT"
  log_info "Data prefix: $DATA_NAME_PREFIX"
  log_info "ECS prefix: $ECS_NAME_PREFIX"
  log_info "Serverless prefix: $SERVERLESS_NAME_PREFIX"
  log_info "DB ingress CIDR: $DB_INGRESS_CIDR"
  log_info "Ready SLO seconds: $READY_SLO_SECONDS"
  log_info "Max load error rate: ${MAX_LOAD_ERROR_RATE_PERCENT}%"
  log_info "Destroy data on cleanup: $DESTROY_DATA"
  log_info "Data cache file: $DATA_CACHE_FILE"

  if [[ "$STACK" == "data" ]]; then
    if has_existing_data_inputs && [[ "$FORCE_NEW_DATA" != "true" ]]; then
      log_info "Existing data inputs already available; skipping new data provisioning (--force-new-data to recreate)"
      log_info "Existing DB endpoint: $EXISTING_DB_ENDPOINT"
      return
    fi
    apply_data_stack
    log_info "AWS data stack provisioning completed successfully"
    return
  fi

  if has_existing_data_inputs; then
    log_info "Using caller-provided DB/Redis/VPC data stack inputs"
    DATA_CREATED=false
  else
    apply_data_stack
  fi

  if [[ "$STACK" == "ecs" || "$STACK" == "both" ]]; then
    apply_ecs_stack
  fi

  if [[ "$STACK" == "serverless" || "$STACK" == "both" ]]; then
    apply_serverless_stack
  fi

  log_info "AWS Terraform integration checks completed successfully"
}

main "$@"
