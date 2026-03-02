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

LOCATION="${AZURE_LOCATION:-westus}"
ENVIRONMENT="${AKS_TF_ENVIRONMENT:-it}"
NAME_PREFIX_BASE="${AKS_TF_NAME_PREFIX_BASE:-hnu$(date -u +%m%d%H%M)}"
NODE_COUNT="${AKS_NODE_COUNT:-2}"
NODE_VM_SIZE="${AKS_NODE_VM_SIZE:-Standard_D2s_v3}"
DEFAULT_HONUA_IMAGE="ghcr.io/honua-io/honua-server:latest"
DEFAULT_HONUA_AOT_IMAGE="ghcr.io/honua-io/honua-server:latest-aot"
USE_AOT="${HONUA_USE_AOT:-false}"
K8S_IMAGE="${HONUA_K8S_IMAGE:-$DEFAULT_HONUA_IMAGE}"
K8S_PREVIOUS_IMAGE="${HONUA_K8S_PREVIOUS_IMAGE:-}"
AUTO_DESTROY=true
CHECK_IDEMPOTENCY=true
CHECK_PROTOCOLS=true
RUN_OBSERVABILITY=true
QUICK_SCALE=true
RUN_UPGRADE_ROLLBACK=false
RUN_DB_RESILIENCE=true
RUN_QUOTA_PREFLIGHT=true
HELM_STATIC_VALIDATE=true
MAX_RUN_COST_USD="${HONUA_MAX_RUN_COST_USD:-0}"
READY_SLO_SECONDS="${HONUA_READY_SLO_SECONDS:-600}"
MAX_LOAD_ERROR_RATE_PERCENT="${HONUA_MAX_LOAD_ERROR_RATE_PERCENT:-0}"
TF_IMAGE="${HONUA_TERRAFORM_IMAGE:-hashicorp/terraform:1.8.5}"
AZ_CLI_IMAGE="${HONUA_AZ_CLI_IMAGE:-mcr.microsoft.com/azure-cli:2.65.0}"
PLAN_ARTIFACT_DIR="${HONUA_TF_PLAN_ARTIFACT_DIR:-}"
ALLOW_DESTROY_PLAN="${HONUA_ALLOW_DESTROY_PLAN:-false}"
TTL_HOURS="${HONUA_TTL_HOURS:-8}"
VALIDATION_RUN_ID="${HONUA_VALIDATION_RUN_ID:-aks-$(date -u +%Y%m%d%H%M%S)}"

TEMP_TF_ROOT=""
TEMP_KUBECONFIG_DIR=""
CLUSTER_APPLIED=false

NAME_PREFIX=""
RESOURCE_GROUP_NAME=""
CLUSTER_NAME=""
EXPIRES_AT_UTC=""
USE_DOCKER_TF=false
USE_DOCKER_AZ_CLI=false
AZ_SESSION_INITIALIZED=false

usage() {
  cat <<USAGE
Run AKS Terraform integration checks and execute Kubernetes validation against the provisioned cluster.

Usage:
  ./infrastructure/terraform/scripts/azure/run-aks-terraform-integration.sh [options]

Options:
  --location <azure-region>            Azure region (default: westus)
  --environment <name>                 Environment suffix (default: it)
  --name-prefix-base <prefix>          Base prefix for generated resource names
  --node-count <n>                     AKS node count (default: 2)
  --node-vm-size <sku>                 AKS node VM size (default: Standard_D2s_v3)
  --aot                                Use latest-aot when image is default
  --image <repo:tag>                   Honua image for Kubernetes checks
  --previous-image <repo:tag>          Previous image used for upgrade/rollback checks
  --upgrade-rollback                   Enable upgrade/rollback validation sequence
  --skip-idempotency                   Skip Terraform idempotency checks
  --skip-protocol-checks               Skip REST/OGC/OData/admin endpoint smoke checks
  --skip-observability                 Skip Terraform observability module checks
  --skip-db-resilience                 Skip DB backup/restore drill
  --skip-helm-static-validation        Skip helm lint/template/kubeconform checks
  --skip-quota-preflight               Skip Azure quota preflight checks
  --max-run-cost-usd <n>               Max allowed estimated run cost (0 disables cap)
  --max-ready-seconds <n>              Ready SLO threshold (default: 600)
  --max-load-error-rate <percent>      Max allowed load error rate (default: 0)
  --plan-artifact-dir <path>           Directory to persist Terraform plan artifacts
  --allow-destroy-plan                 Allow plans containing resource destroys
  --ttl-hours <n>                      TTL tag value for provisioned resources (default: 8)
  --no-scale-check                     Skip quick scale checks
  --no-destroy                         Keep AKS cluster/resources after test run
  --help, -h                           Show this help

Required environment variables:
  ARM_CLIENT_ID
  ARM_CLIENT_SECRET
  ARM_TENANT_ID
  ARM_SUBSCRIPTION_ID
  HONUA_ADMIN_PASSWORD
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

apply_aot_mode() {
  if [[ "$USE_AOT" != "true" ]]; then
    return
  fi

  if [[ "$K8S_IMAGE" == "$DEFAULT_HONUA_IMAGE" ]]; then
    K8S_IMAGE="$DEFAULT_HONUA_AOT_IMAGE"
  fi
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

parse_args() {
  while [[ $# -gt 0 ]]; do
    case "$1" in
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
      --node-count)
        NODE_COUNT="$2"
        shift 2
        ;;
      --node-vm-size)
        NODE_VM_SIZE="$2"
        shift 2
        ;;
      --aot)
        USE_AOT=true
        shift
        ;;
      --image)
        K8S_IMAGE="$2"
        shift 2
        ;;
      --previous-image)
        K8S_PREVIOUS_IMAGE="$2"
        shift 2
        ;;
      --upgrade-rollback)
        RUN_UPGRADE_ROLLBACK=true
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
      --skip-observability)
        RUN_OBSERVABILITY=false
        shift
        ;;
      --skip-db-resilience)
        RUN_DB_RESILIENCE=false
        shift
        ;;
      --skip-helm-static-validation)
        HELM_STATIC_VALIDATE=false
        shift
        ;;
      --skip-quota-preflight)
        RUN_QUOTA_PREFLIGHT=false
        shift
        ;;
      --max-run-cost-usd)
        MAX_RUN_COST_USD="$2"
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
}

normalize_identifiers() {
  ENVIRONMENT="$(echo "$ENVIRONMENT" | tr '[:upper:]' '[:lower:]' | tr -cd 'a-z0-9-')"
  NAME_PREFIX_BASE="$(echo "$NAME_PREFIX_BASE" | tr '[:upper:]' '[:lower:]' | tr -cd 'a-z0-9')"

  if [[ -z "$ENVIRONMENT" || -z "$NAME_PREFIX_BASE" ]]; then
    log_error "Environment/name prefix became empty after normalization"
    exit 1
  fi

  NAME_PREFIX_BASE="${NAME_PREFIX_BASE:0:10}"
  NAME_PREFIX="${NAME_PREFIX_BASE}ak"
  NAME_PREFIX="${NAME_PREFIX:0:20}"
}

prepare_workspace() {
  TEMP_TF_ROOT="$(mktemp -d)"
  TEMP_KUBECONFIG_DIR="$(mktemp -d)"
  cp -R "$REPO_ROOT/infrastructure/terraform" "$TEMP_TF_ROOT/terraform"
}

configure_runtime_tools() {
  if command -v terraform >/dev/null 2>&1; then
    USE_DOCKER_TF=false
  else
    require_command docker
    USE_DOCKER_TF=true
  fi

  if command -v az >/dev/null 2>&1; then
    USE_DOCKER_AZ_CLI=false
  else
    require_command docker
    USE_DOCKER_AZ_CLI=true
  fi

  log_info "Terraform executor: $([[ "$USE_DOCKER_TF" == "true" ]] && echo docker || echo local)"
  log_info "Azure CLI executor: $([[ "$USE_DOCKER_AZ_CLI" == "true" ]] && echo docker || echo local)"
}

run_tf() {
  if [[ "$USE_DOCKER_TF" == "true" ]]; then
    docker run --rm \
      -e ARM_CLIENT_ID \
      -e ARM_CLIENT_SECRET \
      -e ARM_TENANT_ID \
      -e ARM_SUBSCRIPTION_ID \
      -e TF_VAR_location \
      -e TF_VAR_environment \
      -e TF_VAR_name_prefix \
      -e TF_VAR_node_count \
      -e TF_VAR_node_vm_size \
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

run_az() {
  if [[ "$USE_DOCKER_AZ_CLI" == "true" ]]; then
    docker run --rm \
      -e ARM_CLIENT_ID \
      -e ARM_CLIENT_SECRET \
      -e ARM_TENANT_ID \
      -e ARM_SUBSCRIPTION_ID \
      -e AZURE_CORE_ONLY_SHOW_ERRORS=true \
      -v "$TEMP_KUBECONFIG_DIR:$TEMP_KUBECONFIG_DIR" \
      "$AZ_CLI_IMAGE" \
      sh -c 'set -e; az config set extension.use_dynamic_install=yes_without_prompt >/dev/null; az login --service-principal -u "$ARM_CLIENT_ID" -p "$ARM_CLIENT_SECRET" --tenant "$ARM_TENANT_ID" >/dev/null; az account set -s "$ARM_SUBSCRIPTION_ID"; az "$@"' \
      sh "$@"
    return
  fi

  if [[ "$AZ_SESSION_INITIALIZED" != "true" ]]; then
    AZURE_CORE_ONLY_SHOW_ERRORS=true az config set extension.use_dynamic_install=yes_without_prompt >/dev/null
    AZURE_CORE_ONLY_SHOW_ERRORS=true az login --service-principal -u "$ARM_CLIENT_ID" -p "$ARM_CLIENT_SECRET" --tenant "$ARM_TENANT_ID" >/dev/null
    AZURE_CORE_ONLY_SHOW_ERRORS=true az account set -s "$ARM_SUBSCRIPTION_ID"
    AZ_SESSION_INITIALIZED=true
  fi

  AZURE_CORE_ONLY_SHOW_ERRORS=true az "$@"
}

set_tf_vars() {
  EXPIRES_AT_UTC="$(date -u -d "+${TTL_HOURS} hours" +%Y-%m-%dT%H:%M:%SZ)"

  export TF_VAR_location="$LOCATION"
  export TF_VAR_environment="$ENVIRONMENT"
  export TF_VAR_name_prefix="$NAME_PREFIX"
  export TF_VAR_node_count="$NODE_COUNT"
  export TF_VAR_node_vm_size="$NODE_VM_SIZE"
  export TF_VAR_tags="{\"ValidationRunId\":\"$VALIDATION_RUN_ID\",\"TTLHours\":\"$TTL_HOURS\",\"ExpiresAtUTC\":\"$EXPIRES_AT_UTC\",\"Owner\":\"terraform-validation\"}"
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
  run_tf -chdir="$root" apply -input=false -auto-approve -no-color "$plan_file"
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

estimate_run_cost() {
  awk -v n="$NODE_COUNT" 'BEGIN { printf "%.2f", n * 25.0 }'
}

assert_cost_guardrail() {
  local estimated

  if ! awk -v m="$MAX_RUN_COST_USD" 'BEGIN { exit !(m > 0) }'; then
    return
  fi

  estimated="$(estimate_run_cost)"
  if awk -v e="$estimated" -v m="$MAX_RUN_COST_USD" 'BEGIN { exit !(e <= m) }'; then
    log_info "Estimated run cost ($estimated USD) is within cap ($MAX_RUN_COST_USD USD)"
    return
  fi

  log_error "Estimated run cost ($estimated USD) exceeds cap ($MAX_RUN_COST_USD USD)"
  exit 1
}

run_quota_preflight() {
  local vm_vcpu
  local cores
  local current
  local limit
  local required

  if [[ "$RUN_QUOTA_PREFLIGHT" != "true" ]]; then
    return
  fi

  vm_vcpu="$(run_az vm list-skus -l "$LOCATION" --resource-type virtualMachines --query "[?name=='$NODE_VM_SIZE'].capabilities[?name=='vCPUs'].value | [0]" -o tsv 2>/dev/null || echo '')"
  if [[ -z "$vm_vcpu" || "$vm_vcpu" == "[]" ]]; then
    vm_vcpu=2
  fi

  cores="$(run_az vm list-usage -l "$LOCATION" --query "[?name.value=='cores'] | [0]" -o json)"
  current="$(echo "$cores" | sed -n 's/.*"currentValue":\([0-9][0-9]*\).*/\1/p')"
  limit="$(echo "$cores" | sed -n 's/.*"limit":\([0-9][0-9]*\).*/\1/p')"

  required=$(( NODE_COUNT * vm_vcpu ))
  if [[ -n "$current" && -n "$limit" ]] && (( current + required > limit )); then
    log_error "AKS quota preflight failed: cores usage $current/$limit, estimated required +$required"
    exit 1
  fi

  log_info "AKS quota preflight passed (cores current=${current:-unknown}, limit=${limit:-unknown}, required=+$required)"
}

apply_cluster() {
  set_tf_vars

  run_tf -chdir=examples/azure-aks init -input=false -no-color
  plan_apply "examples/azure-aks" "aks.tfplan" "aks"

  CLUSTER_APPLIED=true

  RESOURCE_GROUP_NAME="$(run_tf -chdir=examples/azure-aks output -raw resource_group_name)"
  CLUSTER_NAME="$(run_tf -chdir=examples/azure-aks output -raw cluster_name)"

  if [[ "$CHECK_IDEMPOTENCY" == "true" ]]; then
    assert_idempotent_plan "examples/azure-aks"
  fi
}

fetch_kubeconfig() {
  local kubeconfig_path="$TEMP_KUBECONFIG_DIR/config"
  run_az aks get-credentials \
    --resource-group "$RESOURCE_GROUP_NAME" \
    --name "$CLUSTER_NAME" \
    --file "$kubeconfig_path" \
    --overwrite-existing
}

run_k8s_checks() {
  local args
  args=(
    --cluster-mode external
    --access-mode port-forward
    --cluster-name "$CLUSTER_NAME"
    --kubeconfig "$TEMP_KUBECONFIG_DIR/config"
    --max-ready-seconds "$READY_SLO_SECONDS"
    --max-load-error-rate "$MAX_LOAD_ERROR_RATE_PERCENT"
  )

  if [[ "$RUN_OBSERVABILITY" != "true" ]]; then
    args+=(--skip-observability)
  fi

  if [[ "$RUN_DB_RESILIENCE" != "true" ]]; then
    args+=(--skip-db-resilience)
  fi

  if [[ "$HELM_STATIC_VALIDATE" != "true" ]]; then
    args+=(--skip-helm-static-validation)
  fi

  if [[ "$QUICK_SCALE" != "true" ]]; then
    args+=(--no-scale-check)
  fi

  if [[ "$CHECK_IDEMPOTENCY" != "true" ]]; then
    args+=(--skip-idempotency)
  fi

  if [[ "$CHECK_PROTOCOLS" != "true" ]]; then
    args+=(--skip-protocol-checks)
  fi

  if [[ "$RUN_UPGRADE_ROLLBACK" == "true" ]]; then
    if [[ -z "$K8S_PREVIOUS_IMAGE" || "$K8S_PREVIOUS_IMAGE" == "$K8S_IMAGE" ]]; then
      log_error "AKS upgrade/rollback requires --previous-image different from --image"
      return 1
    fi
    args+=(--upgrade-rollback --previous-image "$K8S_PREVIOUS_IMAGE")
  fi

  if [[ "$AUTO_DESTROY" != "true" ]]; then
    args+=(--no-destroy)
  fi

  HONUA_K8S_IMAGE="$K8S_IMAGE" \
    KUBECONFIG="$TEMP_KUBECONFIG_DIR/config" \
    "$SCRIPT_DIR/../k8s/run-k8s-terraform-integration.sh" "${args[@]}"
}

destroy_cluster() {
  if [[ "$CLUSTER_APPLIED" != "true" ]]; then
    return
  fi

  log_info "Destroying AKS integration cluster"
  set_tf_vars
  run_tf -chdir=examples/azure-aks destroy -input=false -auto-approve -no-color || log_warn "AKS destroy encountered errors"
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
    destroy_cluster
    verify_no_leaks || exit_code=1
  else
    log_warn "Auto-destroy disabled; AKS resources were left running"
  fi

  if [[ -n "$TEMP_TF_ROOT" && -d "$TEMP_TF_ROOT" ]]; then
    rm -rf "$TEMP_TF_ROOT" || true
  fi
  if [[ -n "$TEMP_KUBECONFIG_DIR" && -d "$TEMP_KUBECONFIG_DIR" ]]; then
    rm -rf "$TEMP_KUBECONFIG_DIR" || true
  fi

  if [[ "$exit_code" -ne 0 ]]; then
    log_error "AKS Terraform integration run failed"
  fi

  exit "$exit_code"
}

main() {
  parse_args "$@"
  apply_aot_mode

  require_command kubectl
  require_command helm
  require_command curl
  require_env \
    ARM_CLIENT_ID \
    ARM_CLIENT_SECRET \
    ARM_TENANT_ID \
    ARM_SUBSCRIPTION_ID \
    HONUA_ADMIN_PASSWORD

  configure_runtime_tools
  normalize_identifiers
  assert_cost_guardrail
  run_quota_preflight
  prepare_workspace

  trap cleanup EXIT

  log_info "Starting AKS Terraform integration"
  log_info "Validation run ID: $VALIDATION_RUN_ID"
  log_info "Location: $LOCATION"
  log_info "Environment: $ENVIRONMENT"
  log_info "Name prefix: $NAME_PREFIX"
  log_info "Node count: $NODE_COUNT"
  log_info "Node VM size: $NODE_VM_SIZE"
  log_info "AOT mode: $USE_AOT"
  log_info "K8S image: $K8S_IMAGE"
  log_info "Ready SLO seconds: $READY_SLO_SECONDS"
  log_info "Max load error rate: ${MAX_LOAD_ERROR_RATE_PERCENT}%"

  apply_cluster
  fetch_kubeconfig
  run_k8s_checks

  log_info "AKS Terraform integration checks completed successfully"
}

main "$@"
