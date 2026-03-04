#!/usr/bin/env bash

set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
WORKSPACE_ROOT="${WORKSPACE_ROOT:-$(cd "${ROOT_DIR}/.." && pwd)}"
WORK_DIR="${WORK_DIR:-${ROOT_DIR}/tmp/repo-migrations}"
ORG="${ORG:-honua-io}"
BASE_REF="${BASE_REF:-HEAD}"
BRANCH="${BRANCH:-trunk}"
COMMIT_NAME="${COMMIT_NAME:-Honua Repo Migration}"
COMMIT_EMAIL="${COMMIT_EMAIL:-honua-repo-migration@users.noreply.github.com}"
SKIP_CREATE=false
SKIP_PUSH=false

declare -a SELECTED_REPOS=()
declare -a ALL_REPOS=(
  "honua-terraform"
  "geobench"
  "honua-sdk-js"
  "honua-sdk-python"
  "honua-sdk-dotnet"
  "honua-helm"
  "honua-site"
  "honua-sales"
)

usage() {
  cat <<EOF
Usage: $(basename "$0") [options]

Options:
  --repo <name>      Migrate a single repo (repeatable)
  --skip-create      Do not create remote GitHub repos
  --skip-push        Prepare local migration repos only (no push)
  -h, --help         Show this help

Environment:
  ORG                GitHub org (default: honua-io)
  WORK_DIR           Local scratch dir (default: tmp/repo-migrations)
  WORKSPACE_ROOT     Parent workspace containing sibling honua-* repos
  BASE_REF           Git ref to archive from (default: HEAD)
  BRANCH             Branch name for new repos (default: trunk)
  COMMIT_NAME        Commit author name for migration commits
  COMMIT_EMAIL       Commit author email for migration commits
EOF
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --repo)
      [[ $# -lt 2 ]] && { echo "Missing value for --repo" >&2; exit 1; }
      SELECTED_REPOS+=("$2")
      shift 2
      ;;
    --skip-create)
      SKIP_CREATE=true
      shift
      ;;
    --skip-push)
      SKIP_PUSH=true
      shift
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    *)
      echo "Unknown argument: $1" >&2
      usage
      exit 1
      ;;
  esac
done

require_cmd() {
  if ! command -v "$1" >/dev/null 2>&1; then
    echo "Required command not found: $1" >&2
    exit 1
  fi
}

repo_visibility() {
  case "$1" in
    honua-sales) echo "private" ;;
    *) echo "public" ;;
  esac
}

repo_description() {
  case "$1" in
    honua-terraform) echo "Terraform modules, environments, and validation CI for Honua." ;;
    geobench) echo "Benchmark suite for Honua." ;;
    honua-sdk-js) echo "JavaScript SDKs for Honua (core SDK + MCP server)." ;;
    honua-sdk-python) echo "Python SDK for Honua." ;;
    honua-sdk-dotnet) echo ".NET SDKs for Honua." ;;
    honua-helm) echo "Helm chart for deploying Honua." ;;
    honua-site) echo "Honua public site." ;;
    honua-sales) echo "Private sales and marketing operating docs for Honua." ;;
    *) echo "Monorepo split target." ;;
  esac
}

create_remote_repo_if_missing() {
  local repo="$1"
  if gh repo view "${ORG}/${repo}" >/dev/null 2>&1; then
    echo "[skip] ${ORG}/${repo} already exists"
    return
  fi

  local visibility
  visibility="$(repo_visibility "${repo}")"

  echo "[create] ${ORG}/${repo} (${visibility})"
  gh repo create "${ORG}/${repo}" \
    "--${visibility}" \
    --description "$(repo_description "${repo}")" \
    --disable-issues \
    --disable-wiki
}

resolve_archive_ref() {
  local repo_dir="$1"
  local requested_ref="${2:-$BASE_REF}"
  if git -C "${repo_dir}" rev-parse --verify "${requested_ref}^{commit}" >/dev/null 2>&1; then
    printf '%s' "${requested_ref}"
    return 0
  fi

  if [[ "${requested_ref}" != "HEAD" ]]; then
    echo "[warn] ${repo_dir}: ref '${requested_ref}' not found, falling back to HEAD"
  fi
  printf '%s' "HEAD"
}

extract_prefix_from_repo() {
  local repo_dir="$1"
  local source_prefix="$2"
  local target_dir="$3"
  local strip_components="$4"
  local archive_ref

  archive_ref="$(resolve_archive_ref "${repo_dir}" "${BASE_REF}")"
  mkdir -p "${target_dir}"
  git -C "${repo_dir}" archive "${archive_ref}" "${source_prefix}" \
    | tar -x -C "${target_dir}" --strip-components="${strip_components}"
}

extract_prefix() {
  local source_prefix="$1"
  local target_dir="$2"
  local strip_components="$3"
  extract_prefix_from_repo "${ROOT_DIR}" "${source_prefix}" "${target_dir}" "${strip_components}"
}

overlay_tracked_changes() {
  local repo_dir="$1"
  local source_prefix="$2"
  local target_dir="$3"
  local changed_path
  local relative_path
  local source_path
  local target_path

  while IFS= read -r changed_path; do
    [[ -z "${changed_path}" ]] && continue

    relative_path="${changed_path#${source_prefix}/}"
    source_path="${repo_dir}/${changed_path}"
    target_path="${target_dir}/${relative_path}"

    if [[ -e "${source_path}" ]]; then
      mkdir -p "$(dirname "${target_path}")"
      cp -a "${source_path}" "${target_path}"
    else
      rm -f "${target_path}"
    fi
  done < <(git -C "${repo_dir}" diff --name-only "${BASE_REF}" -- "${source_prefix}")
}

seed_from_workspace_repo_if_present() {
  local repo="$1"
  local dir="$2"
  local source_repo="${WORKSPACE_ROOT}/${repo}"
  local source_ref

  if [[ ! -d "${source_repo}/.git" ]]; then
    return 1
  fi

  source_ref="$(resolve_archive_ref "${source_repo}" "${BASE_REF}")"
  echo "[info] ${repo}: sourcing from sibling repo ${source_repo} (${source_ref})"
  git -C "${source_repo}" archive "${source_ref}" | tar -x -C "${dir}"
  return 0
}

ensure_readme() {
  local repo="$1"
  local dir="$2"
  if [[ -f "${dir}/README.md" ]]; then
    return
  fi

  case "$repo" in
    honua-terraform)
      cat > "${dir}/README.md" <<'EOF'
# Honua Terraform

Terraform modules, examples, bootstrap identities, and validation CI extracted from `honua-io/honua-server` (issue #336).
EOF
      ;;
    geobench)
      cat > "${dir}/README.md" <<'EOF'
# Geobench

Geospatial benchmarking suite extracted from `honua-io/honua-server` (issue #336).
EOF
      ;;
    honua-sdk-dotnet)
      cat > "${dir}/README.md" <<'EOF'
# Honua .NET SDK

`.NET` SDK packages extracted from `honua-io/honua-server` (issue #336).
EOF
      ;;
    honua-site)
      cat > "${dir}/README.md" <<'EOF'
# Honua Site

Static site extracted from `honua-io/honua-server` (issue #336).
EOF
      ;;
    honua-sales)
      cat > "${dir}/README.md" <<'EOF'
# Honua Sales

Private sales and marketing operating docs extracted from `honua-io/honua-server` (issue #336).
EOF
      ;;
    *)
      cat > "${dir}/README.md" <<EOF
# ${repo}

Extracted from \`honua-io/honua-server\` (issue #336).
EOF
      ;;
  esac
}

seed_repo_content() {
  local repo="$1"
  local dir="$2"
  rm -rf "${dir}"
  mkdir -p "${dir}"

  case "${repo}" in
    geobench|honua-sdk-js|honua-sdk-python|honua-sdk-dotnet|honua-helm|honua-site|honua-sales)
      if seed_from_workspace_repo_if_present "${repo}" "${dir}"; then
        if [[ ! -f "${dir}/LICENSE" && -f "${ROOT_DIR}/LICENSE" ]]; then
          cp "${ROOT_DIR}/LICENSE" "${dir}/LICENSE"
        fi
        ensure_readme "${repo}" "${dir}"
        return
      fi
      ;;
  esac

  case "$repo" in
    honua-terraform)
      mkdir -p "${dir}/infrastructure/terraform" "${dir}/.github/workflows" "${dir}/scripts" "${dir}/docs/devops"
      extract_prefix "infrastructure/terraform" "${dir}/infrastructure/terraform" 2
      overlay_tracked_changes "${ROOT_DIR}" "infrastructure/terraform" "${dir}/infrastructure/terraform"

      # Terraform-centric CI/workflows.
      cp "${ROOT_DIR}/.github/workflows/terraform-manual-validation.yml" "${dir}/.github/workflows/"
      if [[ -f "${ROOT_DIR}/.github/workflows/terraform-ci.yml" ]]; then
        cp "${ROOT_DIR}/.github/workflows/terraform-ci.yml" "${dir}/.github/workflows/"
      fi

      # Convenience wrappers and local secret template used by Terraform docs/workflows.
      cp "${ROOT_DIR}/scripts/run-azure-terraform-integration.sh" "${dir}/scripts/"
      cp "${ROOT_DIR}/scripts/run-aws-terraform-integration.sh" "${dir}/scripts/"
      cp "${ROOT_DIR}/scripts/run-aks-terraform-integration.sh" "${dir}/scripts/"
      cp "${ROOT_DIR}/scripts/run-eks-terraform-integration.sh" "${dir}/scripts/"
      cp "${ROOT_DIR}/scripts/run-k8s-terraform-integration.sh" "${dir}/scripts/"
      cp "${ROOT_DIR}/scripts/run-terraform-drift-detection.sh" "${dir}/scripts/"
      cp "${ROOT_DIR}/scripts/terraform-policy-gate.sh" "${dir}/scripts/"
      cp "${ROOT_DIR}/scripts/tf-secrets.local.example.sh" "${dir}/scripts/"

      # Terraform operational docs.
      cp "${ROOT_DIR}/docs/devops/terraform-validation.md" "${dir}/docs/devops/"
      if [[ -n "$(git -C "${ROOT_DIR}" ls-tree -d --name-only "$(resolve_archive_ref "${ROOT_DIR}" "${BASE_REF}")" "docs/devops/examples")" ]]; then
        mkdir -p "${dir}/docs/devops/examples"
        extract_prefix "docs/devops/examples" "${dir}/docs/devops/examples" 3
        overlay_tracked_changes "${ROOT_DIR}" "docs/devops/examples" "${dir}/docs/devops/examples"
      fi
      cp "${ROOT_DIR}/infrastructure/terraform/README.md" "${dir}/infrastructure/terraform/"
      ;;
    geobench)
      extract_prefix "benchmarks" "${dir}" 1
      ;;
    honua-sdk-js)
      mkdir -p "${dir}/js" "${dir}/mcp"
      extract_prefix "sdk/js" "${dir}/js" 2
      extract_prefix "sdk/mcp" "${dir}/mcp" 2
      ;;
    honua-sdk-python)
      extract_prefix "sdk/python" "${dir}" 2
      ;;
    honua-sdk-dotnet)
      extract_prefix "sdk/dotnet" "${dir}" 2
      ;;
    honua-helm)
      extract_prefix "infrastructure/helm" "${dir}" 2
      ;;
    honua-site)
      extract_prefix "site" "${dir}" 1
      ;;
    honua-sales)
      mkdir -p "${dir}/docs/user"
      if [[ -f "${ROOT_DIR}/docs/user/MVP_LAUNCH_GTM_PLAYBOOK.md" ]]; then
        cp "${ROOT_DIR}/docs/user/MVP_LAUNCH_GTM_PLAYBOOK.md" "${dir}/docs/user/"
      fi
      ;;
    *)
      echo "Unsupported repo: ${repo}" >&2
      exit 1
      ;;
  esac

  if [[ ! -f "${dir}/LICENSE" ]]; then
    cp "${ROOT_DIR}/LICENSE" "${dir}/LICENSE"
  fi
  ensure_readme "${repo}" "${dir}"
}

init_and_push_repo() {
  local repo="$1"
  local dir="$2"
  local remote_url="https://github.com/${ORG}/${repo}.git"
  local push_token
  push_token="$(gh auth token)"
  local push_url="https://x-access-token:${push_token}@github.com/${ORG}/${repo}.git"

  git -C "${dir}" init -q
  git -C "${dir}" symbolic-ref HEAD "refs/heads/${BRANCH}"
  git -C "${dir}" add .

  if git -C "${dir}" diff --cached --quiet; then
    echo "[skip] ${repo} has no files to commit"
    return
  fi

  git -C "${dir}" -c user.name="${COMMIT_NAME}" -c user.email="${COMMIT_EMAIL}" \
    commit -q -m "chore: initial import from honua-server (#336)"
  git -C "${dir}" remote add origin "${push_url}"

  if [[ "${SKIP_PUSH}" == "true" ]]; then
    echo "[dry] ${repo} prepared locally at ${dir}"
    return
  fi

  echo "[push] ${repo} -> ${remote_url}"
  git -C "${dir}" push -u origin "${BRANCH}"
}

validate_repo_selection() {
  local repo="$1"
  for known in "${ALL_REPOS[@]}"; do
    if [[ "${repo}" == "${known}" ]]; then
      return
    fi
  done
  echo "Unsupported --repo value: ${repo}" >&2
  exit 1
}

main() {
  require_cmd git
  require_cmd gh
  require_cmd tar

  if [[ ${#SELECTED_REPOS[@]} -eq 0 ]]; then
    SELECTED_REPOS=("${ALL_REPOS[@]}")
  fi

  for repo in "${SELECTED_REPOS[@]}"; do
    validate_repo_selection "${repo}"
  done

  mkdir -p "${WORK_DIR}"

  for repo in "${SELECTED_REPOS[@]}"; do
    echo
    echo "==> Migrating ${repo}"
    if [[ "${SKIP_CREATE}" == "false" ]]; then
      create_remote_repo_if_missing "${repo}"
    fi

    local local_dir
    local_dir="${WORK_DIR}/${repo}"
    seed_repo_content "${repo}" "${local_dir}"
    init_and_push_repo "${repo}" "${local_dir}"
    echo "[done] https://github.com/${ORG}/${repo}"
  done
}

main "$@"
