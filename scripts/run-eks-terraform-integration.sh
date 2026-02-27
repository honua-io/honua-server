#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(dirname "$SCRIPT_DIR")"

REGION="${AWS_REGION_OVERRIDE:-us-east-1}"
ENVIRONMENT="${EKS_TF_ENVIRONMENT:-it}"
NAME_PREFIX_BASE="${EKS_TF_NAME_PREFIX_BASE:-hnu$(date -u +%m%d%H%M)}"
NODE_INSTANCE_TYPE="${EKS_NODE_INSTANCE_TYPE:-t3.small}"
NODE_MIN_SIZE="${EKS_NODE_MIN_SIZE:-1}"
NODE_MAX_SIZE="${EKS_NODE_MAX_SIZE:-3}"
NODE_DESIRED_SIZE="${EKS_NODE_DESIRED_SIZE:-2}"
K8S_IMAGE="${HONUA_K8S_IMAGE:-ghcr.io/honua-io/honua-server:latest}"
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
PLAN_ARTIFACT_DIR="${HONUA_TF_PLAN_ARTIFACT_DIR:-}"
ALLOW_DESTROY_PLAN="${HONUA_ALLOW_DESTROY_PLAN:-false}"
TTL_HOURS="${HONUA_TTL_HOURS:-8}"
VALIDATION_RUN_ID="${HONUA_VALIDATION_RUN_ID:-eks-$(date -u +%Y%m%d%H%M%S)}"

TEMP_TF_ROOT=""
TEMP_KUBECONFIG_PATH=""
CLUSTER_APPLIED=false

NAME_PREFIX=""
CLUSTER_NAME=""
EXPIRES_AT_UTC=""

usage() {
  cat <<USAGE
Run EKS Terraform integration checks and execute Kubernetes validation against the provisioned cluster.

Usage:
  ./scripts/run-eks-terraform-integration.sh [options]

Options:
  --region <aws-region>                AWS region (default: us-east-1)
  --environment <name>                 Environment suffix (default: it)
  --name-prefix-base <prefix>          Base prefix for generated resource names
  --node-instance-type <type>          EKS node instance type (default: t3.small)
  --node-min-size <n>                  EKS node group min size (default: 1)
  --node-max-size <n>                  EKS node group max size (default: 3)
  --node-desired-size <n>              EKS node group desired size (default: 2)
  --image <repo:tag>                   Honua image for Kubernetes checks
  --previous-image <repo:tag>          Previous image used for upgrade/rollback checks
  --upgrade-rollback                   Enable upgrade/rollback validation sequence
  --skip-idempotency                   Skip Terraform idempotency checks
  --skip-protocol-checks               Skip REST/OGC/OData/admin endpoint smoke checks
  --skip-observability                 Skip Terraform observability module checks
  --skip-db-resilience                 Skip DB backup/restore drill
  --skip-helm-static-validation        Skip helm lint/template/kubeconform checks
  --skip-quota-preflight               Skip AWS quota preflight checks
  --max-run-cost-usd <n>               Max allowed estimated run cost (0 disables cap)
  --max-ready-seconds <n>              Ready SLO threshold (default: 600)
  --max-load-error-rate <percent>      Max allowed load error rate (default: 0)
  --plan-artifact-dir <path>           Directory to persist Terraform plan artifacts
  --allow-destroy-plan                 Allow plans containing resource destroys
  --ttl-hours <n>                      TTL tag value for provisioned resources (default: 8)
  --no-scale-check                     Skip quick scale checks
  --no-destroy                         Keep EKS cluster/resources after test run
  --help, -h                           Show this help

Required environment variables:
  AWS_ACCESS_KEY_ID
  AWS_SECRET_ACCESS_KEY
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
      --node-instance-type)
        NODE_INSTANCE_TYPE="$2"
        shift 2
        ;;
      --node-min-size)
        NODE_MIN_SIZE="$2"
        shift 2
        ;;
      --node-max-size)
        NODE_MAX_SIZE="$2"
        shift 2
        ;;
      --node-desired-size)
        NODE_DESIRED_SIZE="$2"
        shift 2
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

  NAME_PREFIX_BASE="${NAME_PREFIX_BASE:0:8}"
  NAME_PREFIX="${NAME_PREFIX_BASE}ek"
}

prepare_workspace() {
  TEMP_TF_ROOT="$(mktemp -d)"
  TEMP_KUBECONFIG_PATH="$(mktemp)"
  cp -R "$REPO_ROOT/infrastructure/terraform" "$TEMP_TF_ROOT/terraform"
}

run_tf() {
  docker run --rm \
    -e AWS_ACCESS_KEY_ID \
    -e AWS_SECRET_ACCESS_KEY \
    -e AWS_SESSION_TOKEN \
    -e AWS_REGION \
    -e AWS_DEFAULT_REGION \
    -e TF_VAR_region \
    -e TF_VAR_environment \
    -e TF_VAR_name_prefix \
    -e TF_VAR_node_instance_types \
    -e TF_VAR_node_min_size \
    -e TF_VAR_node_max_size \
    -e TF_VAR_node_desired_size \
    -e TF_VAR_tags \
    -e TF_IN_AUTOMATION=true \
    -v "$TEMP_TF_ROOT/terraform:/workspace" \
    -w /workspace \
    "$TF_IMAGE" "$@"
}

set_tf_vars() {
  EXPIRES_AT_UTC="$(date -u -d "+${TTL_HOURS} hours" +%Y-%m-%dT%H:%M:%SZ)"

  export AWS_REGION="$REGION"
  export AWS_DEFAULT_REGION="$REGION"
  export TF_VAR_region="$REGION"
  export TF_VAR_environment="$ENVIRONMENT"
  export TF_VAR_name_prefix="$NAME_PREFIX"
  export TF_VAR_node_instance_types="[\"$NODE_INSTANCE_TYPE\"]"
  export TF_VAR_node_min_size="$NODE_MIN_SIZE"
  export TF_VAR_node_max_size="$NODE_MAX_SIZE"
  export TF_VAR_node_desired_size="$NODE_DESIRED_SIZE"
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
  awk -v n="$NODE_DESIRED_SIZE" 'BEGIN { printf "%.2f", n * 35.0 }'
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
  local quota
  local vcpu_per_node
  local required

  if [[ "$RUN_QUOTA_PREFLIGHT" != "true" ]]; then
    return
  fi

  quota="$(aws service-quotas get-service-quota --service-code ec2 --quota-code L-1216C47A --query 'Quota.Value' --output text 2>/dev/null || echo '')"
  vcpu_per_node="$(aws ec2 describe-instance-types --instance-types "$NODE_INSTANCE_TYPE" --query 'InstanceTypes[0].VCpuInfo.DefaultVCpus' --output text 2>/dev/null || echo '')"

  if [[ -z "$vcpu_per_node" || "$vcpu_per_node" == "None" ]]; then
    vcpu_per_node=2
  fi

  required=$(( NODE_DESIRED_SIZE * vcpu_per_node ))

  if [[ -n "$quota" && "$quota" != "None" ]] && awk -v q="$quota" -v r="$required" 'BEGIN { exit !(r > q) }'; then
    log_error "EKS quota preflight failed: required vCPU $required exceeds EC2 regional quota $quota"
    exit 1
  fi

  log_info "EKS quota preflight passed (EC2 regional vCPU quota=${quota:-unknown}, required=$required)"
}

apply_cluster() {
  set_tf_vars

  run_tf -chdir=examples/aws-eks init -input=false -no-color
  plan_apply "examples/aws-eks" "eks.tfplan" "eks"

  CLUSTER_APPLIED=true
  CLUSTER_NAME="$(run_tf -chdir=examples/aws-eks output -raw cluster_name)"

  if [[ "$CHECK_IDEMPOTENCY" == "true" ]]; then
    assert_idempotent_plan "examples/aws-eks"
  fi
}

fetch_kubeconfig() {
  aws eks update-kubeconfig \
    --name "$CLUSTER_NAME" \
    --region "$REGION" \
    --kubeconfig "$TEMP_KUBECONFIG_PATH" \
    --alias "$CLUSTER_NAME" >/dev/null
}

run_k8s_checks() {
  local args
  args=(
    --cluster-mode external
    --access-mode port-forward
    --cluster-name "$CLUSTER_NAME"
    --kubeconfig "$TEMP_KUBECONFIG_PATH"
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
      log_error "EKS upgrade/rollback requires --previous-image different from --image"
      return 1
    fi
    args+=(--upgrade-rollback --previous-image "$K8S_PREVIOUS_IMAGE")
  fi

  if [[ "$AUTO_DESTROY" != "true" ]]; then
    args+=(--no-destroy)
  fi

  HONUA_K8S_IMAGE="$K8S_IMAGE" \
    KUBECONFIG="$TEMP_KUBECONFIG_PATH" \
    "$SCRIPT_DIR/run-k8s-terraform-integration.sh" "${args[@]}"
}

destroy_cluster() {
  if [[ "$CLUSTER_APPLIED" != "true" ]]; then
    return
  fi

  log_info "Destroying EKS integration cluster"
  set_tf_vars
  run_tf -chdir=examples/aws-eks destroy -input=false -auto-approve -no-color || log_warn "EKS destroy encountered errors"
}

verify_no_leaks() {
  local count
  local i

  for i in {1..10}; do
    count="$(aws resourcegroupstaggingapi get-resources --tag-filters Key=ValidationRunId,Values="$VALIDATION_RUN_ID" --query 'length(ResourceTagMappingList)' --output text 2>/dev/null || echo 0)"
    if [[ "$count" == "0" || "$count" == "None" ]]; then
      log_info "Leak janitor check passed (no tagged resources remain)"
      return 0
    fi
    sleep 15
  done

  log_error "Leak janitor check failed: resources tagged ValidationRunId=$VALIDATION_RUN_ID still exist"
  aws resourcegroupstaggingapi get-resources --tag-filters Key=ValidationRunId,Values="$VALIDATION_RUN_ID" --output json || true
  return 1
}

cleanup() {
  local exit_code="$?"

  if [[ "$AUTO_DESTROY" == "true" ]]; then
    destroy_cluster
    verify_no_leaks || exit_code=1
  else
    log_warn "Auto-destroy disabled; EKS resources were left running"
  fi

  if [[ -n "$TEMP_TF_ROOT" && -d "$TEMP_TF_ROOT" ]]; then
    rm -rf "$TEMP_TF_ROOT" || true
  fi
  if [[ -n "$TEMP_KUBECONFIG_PATH" && -f "$TEMP_KUBECONFIG_PATH" ]]; then
    rm -f "$TEMP_KUBECONFIG_PATH" || true
  fi

  if [[ "$exit_code" -ne 0 ]]; then
    log_error "EKS Terraform integration run failed"
  fi

  exit "$exit_code"
}

main() {
  parse_args "$@"

  require_command docker
  require_command aws
  require_command kubectl
  require_command helm
  require_command terraform
  require_command curl
  require_env \
    AWS_ACCESS_KEY_ID \
    AWS_SECRET_ACCESS_KEY \
    HONUA_ADMIN_PASSWORD

  normalize_identifiers
  assert_cost_guardrail
  run_quota_preflight
  prepare_workspace

  trap cleanup EXIT

  log_info "Starting EKS Terraform integration"
  log_info "Validation run ID: $VALIDATION_RUN_ID"
  log_info "Region: $REGION"
  log_info "Environment: $ENVIRONMENT"
  log_info "Name prefix: $NAME_PREFIX"
  log_info "Node type: $NODE_INSTANCE_TYPE"
  log_info "Node sizes: min=$NODE_MIN_SIZE desired=$NODE_DESIRED_SIZE max=$NODE_MAX_SIZE"
  log_info "Ready SLO seconds: $READY_SLO_SECONDS"
  log_info "Max load error rate: ${MAX_LOAD_ERROR_RATE_PERCENT}%"

  apply_cluster
  fetch_kubeconfig
  run_k8s_checks

  log_info "EKS Terraform integration checks completed successfully"
}

main "$@"
