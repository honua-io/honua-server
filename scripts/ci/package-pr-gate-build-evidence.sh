#!/usr/bin/env bash
# Package only repeated server-test projects from the PR Gate build already in memory.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"
CONFIG="${REPO_ROOT}/.github/server-test-prebuild-observe.json"
SHARDS="${REPO_ROOT}/.github/ci-shards.json"
REGISTRY="${REPO_ROOT}/.github/server-test-artifact-projects.json"
CONTRACT="honua.pr-gate-server-test-metadata/v1"

base_sha=""
head_sha=""
merge_sha=""
repository=""
pull_request=""
run_id=""
run_attempt=""
runner_os=""
runner_arch=""
runner_image=""
output_dir=""

usage() {
  echo "Usage: $0 --base-sha <sha> --head-sha <sha> --merge-sha <sha> --repository <owner/repo> --pull-request <number> --run-id <number> --run-attempt <number> --runner-os <name> --runner-arch <arch> --runner-image <image> --output <directory>" >&2
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --base-sha) base_sha="${2:-}"; shift 2 ;;
    --head-sha) head_sha="${2:-}"; shift 2 ;;
    --merge-sha) merge_sha="${2:-}"; shift 2 ;;
    --repository) repository="${2:-}"; shift 2 ;;
    --pull-request) pull_request="${2:-}"; shift 2 ;;
    --run-id) run_id="${2:-}"; shift 2 ;;
    --run-attempt) run_attempt="${2:-}"; shift 2 ;;
    --runner-os) runner_os="${2:-}"; shift 2 ;;
    --runner-arch) runner_arch="${2:-}"; shift 2 ;;
    --runner-image) runner_image="${2:-}"; shift 2 ;;
    --output) output_dir="${2:-}"; shift 2 ;;
    *) usage; exit 2 ;;
  esac
done

for value in base_sha head_sha merge_sha; do
  candidate="${!value}"
  if [[ ! "${candidate}" =~ ^[0-9a-f]{40}$ ]]; then
    echo "::error::${value} must be a lowercase full commit SHA." >&2
    exit 2
  fi
done
if [[ ! "${repository}" =~ ^[^/]+/[^/]+$ ]] ||
   [[ ! "${pull_request}" =~ ^[1-9][0-9]*$ ]] ||
   [[ ! "${run_id}" =~ ^[1-9][0-9]*$ ]] ||
   [[ ! "${run_attempt}" =~ ^[1-9][0-9]*$ ]] ||
   [[ -z "${runner_os}" || -z "${runner_arch}" || -z "${runner_image}" || -z "${output_dir}" ]]; then
  usage
  exit 2
fi
for command in cp git jq python3; do
  command -v "${command}" >/dev/null || {
    echo "::error::Required command '${command}' is unavailable." >&2
    exit 2
  }
done

# shellcheck source=scripts/ci/lib/pr-gate-ancestry.sh
. "${SCRIPT_DIR}/lib/pr-gate-ancestry.sh"
honua_ensure_pr_gate_ancestry \
  "${REPO_ROOT}" "${base_sha}" "${head_sha}" "${merge_sha}"

mkdir -p "${output_dir}"
output_dir="$(cd "${output_dir}" && pwd)"
if find "${output_dir}" -mindepth 1 -print -quit | grep -q .; then
  echo "::error::PR Gate build evidence output directory must start empty." >&2
  exit 1
fi
mkdir -p "${output_dir}/metadata/manifests" "${output_dir}/payload"

# Route the exact synthetic merge tree that PR Gate built. This avoids needing
# unbounded branch history merely to rediscover the base/head merge-base.
git -C "${REPO_ROOT}" diff --name-only "${base_sha}" "${merge_sha}" \
  | "${SCRIPT_DIR}/honua-server-targeted-tests.sh" --stdin --config "${SHARDS}" \
    > "${output_dir}/metadata/descriptor.json"
python3 "${SCRIPT_DIR}/plan-server-test-prebuild.py" \
  --config "${CONFIG}" \
  --shards "${SHARDS}" \
  --registry "${REGISTRY}" \
  --descriptor "${output_dir}/metadata/descriptor.json" \
  --output "${output_dir}/metadata/plan.json"

producer_count="$(jq -r '.producers | length' "${output_dir}/metadata/plan.json")"
if (( producer_count == 0 )); then
  echo "PR Gate selected no repeated registered server-test project."
  if [[ -n "${GITHUB_OUTPUT:-}" ]]; then
    echo "has_evidence=false" >> "${GITHUB_OUTPUT}"
  fi
  exit 0
fi

dotnet_sdk="$(dotnet --version)"
merge_tree_sha="$(git -C "${REPO_ROOT}" rev-parse "${merge_sha}^{tree}")"
jq -nS \
  --arg contract "${CONTRACT}" \
  --arg repository "${repository}" \
  --argjson pull_request "${pull_request}" \
  --arg base_sha "${base_sha}" \
  --arg head_sha "${head_sha}" \
  --arg merge_sha "${merge_sha}" \
  --arg merge_tree_sha "${merge_tree_sha}" \
  --argjson run_id "${run_id}" \
  --argjson run_attempt "${run_attempt}" \
  --arg workflow_path '.github/workflows/pr-gate.yml' \
  --arg configuration 'Release' \
  --arg dotnet_sdk "${dotnet_sdk}" \
  --arg runner_os "${runner_os}" \
  --arg runner_arch "${runner_arch}" \
  --arg runner_image "${runner_image}" \
  '{contract:$contract,repository:$repository,pull_request:$pull_request,
    base_sha:$base_sha,head_sha:$head_sha,merge_sha:$merge_sha,
    merge_tree_sha:$merge_tree_sha,run_id:$run_id,run_attempt:$run_attempt,
    workflow_path:$workflow_path,configuration:$configuration,dotnet_sdk:$dotnet_sdk,
    runner_os:$runner_os,runner_arch:$runner_arch,runner_image:$runner_image}' \
  > "${output_dir}/metadata/context.json"

while IFS= read -r producer; do
  project="$(jq -r '.project' <<<"${producer}")"
  suffix="$(jq -r '.project_suffix' <<<"${producer}")"
  "${SCRIPT_DIR}/package-server-test-binaries.sh" \
    --project "${project}" \
    --output "${output_dir}/payload" \
    --source-sha "${merge_sha}"
  cp "${output_dir}/payload/server-test-binaries-${suffix}.manifest.json" \
    "${output_dir}/metadata/manifests/"
done < <(jq -c '.producers[]' "${output_dir}/metadata/plan.json")

if [[ -n "${GITHUB_OUTPUT:-}" ]]; then
  {
    echo "has_evidence=true"
    echo "producer_count=${producer_count}"
    echo "metadata_dir=${output_dir}/metadata"
    echo "payload_dir=${output_dir}/payload"
  } >> "${GITHUB_OUTPUT}"
fi
echo "PR Gate build evidence packaged ${producer_count} repeated project(s) from tree ${merge_tree_sha}."
