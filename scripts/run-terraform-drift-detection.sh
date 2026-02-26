#!/usr/bin/env bash

set -euo pipefail

ROOTS=()
VAR_FILES=()
PLAN_ARTIFACT_DIR=""
BACKEND=true

usage() {
  cat <<USAGE
Run Terraform drift detection (plan -detailed-exitcode) against one or more roots.

Usage:
  ./scripts/run-terraform-drift-detection.sh --root <path> [--root <path> ...] [options]

Options:
  --root <path>               Terraform root (can be specified multiple times)
  --var-file <path>           Terraform var-file passed to plan (can be specified multiple times)
  --plan-artifact-dir <path>  Directory to store drift plan output logs
  --backend-false             Run init with -backend=false (default: backend enabled)
  --help, -h                  Show this help
USAGE
}

log_info() {
  echo "[INFO] $1"
}

log_error() {
  echo "[ERROR] $1" >&2
}

parse_args() {
  while [[ $# -gt 0 ]]; do
    case "$1" in
      --root)
        ROOTS+=("$2")
        shift 2
        ;;
      --var-file)
        VAR_FILES+=("$2")
        shift 2
        ;;
      --plan-artifact-dir)
        PLAN_ARTIFACT_DIR="$2"
        shift 2
        ;;
      --backend-false)
        BACKEND=false
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

  if [[ "${#ROOTS[@]}" -eq 0 ]]; then
    log_error "At least one --root is required"
    usage
    exit 1
  fi
}

run_drift_check_for_root() {
  local root="$1"
  local init_args
  local plan_args
  local log_file
  local exit_code
  local name

  if [[ ! -d "$root" ]]; then
    log_error "Terraform root does not exist: $root"
    return 1
  fi

  name="$(echo "$root" | tr '/:' '__')"
  log_file="${PLAN_ARTIFACT_DIR%/}/${name}.drift-plan.txt"
  mkdir -p "${PLAN_ARTIFACT_DIR%/}"

  init_args=(-input=false -no-color)
  if [[ "$BACKEND" != "true" ]]; then
    init_args+=(-backend=false)
  fi

  log_info "terraform init ($root)"
  terraform -chdir="$root" init "${init_args[@]}"

  plan_args=(-input=false -no-color -detailed-exitcode)
  local vf
  for vf in "${VAR_FILES[@]}"; do
    plan_args+=(-var-file="$vf")
  done

  log_info "terraform plan -detailed-exitcode ($root)"
  set +e
  terraform -chdir="$root" plan "${plan_args[@]}" >"$log_file" 2>&1
  exit_code=$?
  set -e

  if [[ "$exit_code" -eq 0 ]]; then
    log_info "Drift check passed for $root (no changes)"
    return 0
  fi

  if [[ "$exit_code" -eq 2 ]]; then
    log_error "Drift detected for $root"
    cat "$log_file"
    return 1
  fi

  log_error "Drift check errored for $root"
  cat "$log_file"
  return 1
}

main() {
  parse_args "$@"

  if ! command -v terraform >/dev/null 2>&1; then
    log_error "terraform command is required"
    exit 1
  fi

  if [[ -z "$PLAN_ARTIFACT_DIR" ]]; then
    PLAN_ARTIFACT_DIR="$(mktemp -d)"
  fi

  local root
  for root in "${ROOTS[@]}"; do
    run_drift_check_for_root "$root"
  done

  log_info "Terraform drift detection completed successfully"
}

main "$@"
