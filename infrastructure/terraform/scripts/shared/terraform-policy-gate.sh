#!/usr/bin/env bash

set -euo pipefail

ROOT="${1:-infrastructure/terraform}"

log_info() {
  echo "[INFO] $1"
}

log_warn() {
  echo "[WARN] $1"
}

log_error() {
  echo "[ERROR] $1" >&2
}

require_dir() {
  if [[ ! -d "$1" ]]; then
    log_error "Directory not found: $1"
    exit 1
  fi
}

run_tflint() {
  if ! command -v tflint >/dev/null 2>&1; then
    log_warn "tflint is not installed; skipping tflint checks"
    return
  fi

  local roots=(
    "$ROOT/examples/aws"
    "$ROOT/examples/aws-serverless"
    "$ROOT/examples/aws-eks"
    "$ROOT/examples/azure"
    "$ROOT/examples/azure-data"
    "$ROOT/examples/azure-functions"
    "$ROOT/examples/azure-aks"
    "$ROOT/examples/observability"
  )

  local root
  for root in "${roots[@]}"; do
    [[ -d "$root" ]] || continue
    log_info "tflint: $root"
    (
      cd "$root"
      tflint --init >/dev/null
      tflint
    )
  done
}

run_checkov() {
  if command -v checkov >/dev/null 2>&1; then
    log_info "Running checkov"
    checkov -d "$ROOT/modules" --download-external-modules true --compact
    checkov -d "$ROOT/examples" --download-external-modules true --compact
    return
  fi

  if command -v docker >/dev/null 2>&1; then
    log_info "Running checkov via docker"
    docker run --rm -v "$PWD:/workspace" -w /workspace bridgecrew/checkov:latest \
      checkov -d "$ROOT/modules" --download-external-modules true --compact
    docker run --rm -v "$PWD:/workspace" -w /workspace bridgecrew/checkov:latest \
      checkov -d "$ROOT/examples" --download-external-modules true --compact
    return
  fi

  log_warn "checkov unavailable (no binary, no docker); skipping"
}

run_tfsec() {
  if command -v tfsec >/dev/null 2>&1; then
    log_info "Running tfsec"
    tfsec "$ROOT/modules"
    tfsec "$ROOT/examples"
    return
  fi

  if command -v docker >/dev/null 2>&1; then
    log_info "Running tfsec via docker"
    docker run --rm -v "$PWD:/src" aquasec/tfsec:latest /src/"$ROOT/modules"
    docker run --rm -v "$PWD:/src" aquasec/tfsec:latest /src/"$ROOT/examples"
    return
  fi

  log_warn "tfsec unavailable (no binary, no docker); skipping"
}

assert_regex_absent() {
  local pattern="$1"
  local scope="$2"
  local label="$3"

  if rg -n "$pattern" "$scope" -S >/tmp/policy-match.txt 2>&1; then
    log_error "Policy check failed ($label): disallowed pattern found"
    cat /tmp/policy-match.txt
    rm -f /tmp/policy-match.txt
    exit 1
  fi
  rm -f /tmp/policy-match.txt
}

assert_regex_present() {
  local pattern="$1"
  local file="$2"
  local label="$3"

  if ! rg -q "$pattern" "$file" -S; then
    log_error "Policy check failed ($label): expected pattern not found in $file"
    exit 1
  fi
}

run_custom_policy_checks() {
  log_info "Running custom policy checks"

  assert_regex_absent 'actions\\s*=\\s*\\[\\s*"\\*"\\s*\\]' "$ROOT" "least-privilege-actions"
  assert_regex_absent 'Action"\\s*:\\s*"\\*"' "$ROOT" "least-privilege-actions-json"

  local tag_files=(
    "$ROOT/modules/aws-ecs/variables.tf"
    "$ROOT/modules/aws-serverless/variables.tf"
    "$ROOT/modules/aws-eks/variables.tf"
    "$ROOT/modules/azure-aca/variables.tf"
    "$ROOT/modules/azure-data/variables.tf"
    "$ROOT/modules/azure-functions/variables.tf"
    "$ROOT/modules/azure-aks/variables.tf"
    "$ROOT/examples/aws/variables.tf"
    "$ROOT/examples/aws-serverless/variables.tf"
    "$ROOT/examples/aws-eks/variables.tf"
    "$ROOT/examples/azure/variables.tf"
    "$ROOT/examples/azure-data/variables.tf"
    "$ROOT/examples/azure-functions/variables.tf"
    "$ROOT/examples/azure-aks/variables.tf"
  )

  local file
  for file in "${tag_files[@]}"; do
    [[ -f "$file" ]] || continue
    assert_regex_present 'variable "tags"' "$file" "mandatory-tags-variable"
  done

  assert_regex_present 'storage_encrypted\\s*=\\s*true' "$ROOT/modules/aws-ecs/main.tf" "aws-ecs-rds-encryption"
  assert_regex_present 'storage_encrypted\\s*=\\s*true' "$ROOT/modules/aws-serverless/main.tf" "aws-serverless-rds-encryption"
  assert_regex_present 'transit_encryption_enabled\\s*=\\s*true' "$ROOT/modules/aws-ecs/main.tf" "aws-ecs-redis-transit-encryption"
  assert_regex_present 'transit_encryption_enabled\\s*=\\s*true' "$ROOT/modules/aws-serverless/main.tf" "aws-serverless-redis-transit-encryption"
  assert_regex_present 'minimum_tls_version\\s*=\\s*"1\\.2"' "$ROOT/modules/azure-aca/main.tf" "azure-aca-redis-tls12"
  assert_regex_present 'minimum_tls_version\\s*=\\s*"1\\.2"' "$ROOT/modules/azure-data/main.tf" "azure-data-redis-tls12"
  assert_regex_present 'minimum_tls_version\\s*=\\s*"1\\.2"' "$ROOT/modules/azure-functions/main.tf" "azure-functions-redis-tls12"
}

main() {
  require_dir "$ROOT"
  require_dir "$ROOT/modules"
  require_dir "$ROOT/examples"

  run_tflint
  run_checkov
  run_tfsec
  run_custom_policy_checks

  log_info "Terraform policy gate checks completed successfully"
}

main "$@"
