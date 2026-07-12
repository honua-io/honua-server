#!/usr/bin/env bash
# Build-output packaging contract for exact-head server-test shard consumers.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DEFAULT_REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"
REPO_ROOT="${HONUA_SERVER_TEST_ARTIFACT_REPO_ROOT:-${DEFAULT_REPO_ROOT}}"
REGISTRY="${HONUA_SERVER_TEST_ARTIFACT_REGISTRY:-${REPO_ROOT}/.github/server-test-artifact-projects.json}"
CONFIGURATION="${HONUA_SERVER_TEST_ARTIFACT_CONFIGURATION:-Release}"
MAX_ARCHIVE_BYTES="${HONUA_SERVER_TEST_ARTIFACT_MAX_ARCHIVE_BYTES:-268435456}"
MAX_UNPACKED_BYTES="${HONUA_SERVER_TEST_ARTIFACT_MAX_UNPACKED_BYTES:-536870912}"
MAX_PACKAGE_MILLISECONDS="${HONUA_SERVER_TEST_ARTIFACT_MAX_PACKAGE_MILLISECONDS:-120000}"
EVIDENCE_TTL_SECONDS="${HONUA_SERVER_TEST_ARTIFACT_TTL_SECONDS:-86400}"
CONTRACT="honua.server-test-binaries.v1"

project=""
output_dir=""
source_sha=""

usage() {
  echo "Usage: $0 --project <relative.csproj> --output <directory> --source-sha <commit>" >&2
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --project) project="${2:-}"; shift 2 ;;
    --output) output_dir="${2:-}"; shift 2 ;;
    --source-sha) source_sha="${2:-}"; shift 2 ;;
    *) usage; exit 2 ;;
  esac
done

if [[ -z "${project}" || -z "${output_dir}" || -z "${source_sha}" ]]; then
  usage
  exit 2
fi
if [[ ! "${source_sha}" =~ ^[0-9a-fA-F]{40}$ ]]; then
  echo "::error::Source SHA must be a full 40-character hexadecimal commit id." >&2
  exit 2
fi
if [[ ! "${EVIDENCE_TTL_SECONDS}" =~ ^[0-9]+$ ]] || (( EVIDENCE_TTL_SECONDS < 1 || EVIDENCE_TTL_SECONDS > 86400 )); then
  echo "::error::Evidence TTL must be between 1 and 86400 seconds." >&2
  exit 2
fi
for command in cp date du find gzip jq sha256sum tar; do
  command -v "${command}" >/dev/null || { echo "::error::Required command '${command}' is unavailable." >&2; exit 2; }
done

artifact_suffix="$(jq -er --arg project "${project}" '.projects[] | select(.csproj == $project) | .artifact_suffix' "${REGISTRY}")" || {
  echo "::error::Project '${project}' is not registered for server-test artifacts." >&2
  exit 2
}
project_path="${REPO_ROOT}/${project}"
project_dir="$(dirname "${project_path}")"
bin_dir="${project_dir}/bin/${CONFIGURATION}"
obj_dir="${project_dir}/obj"

if [[ ! -f "${project_path}" || ! -d "${bin_dir}" || ! -f "${obj_dir}/project.assets.json" ]]; then
  echo "::error::Project '${project}' must be restored and built in ${CONFIGURATION} before packaging." >&2
  exit 1
fi
for pattern in '*.dll' '*.pdb' '*.deps.json' '*.runtimeconfig.json'; do
  if ! find "${bin_dir}" -type f -name "${pattern}" -print -quit | grep -q .; then
    echo "::error::Project '${project}' output is missing required ${pattern} files." >&2
    exit 1
  fi
done

mkdir -p "${output_dir}"
output_dir="$(cd "${output_dir}" && pwd)"
archive_name="server-test-binaries-${artifact_suffix}.tar.gz"
manifest_name="server-test-binaries-${artifact_suffix}.manifest.json"
archive_path="${output_dir}/${archive_name}"
manifest_path="${output_dir}/${manifest_name}"
rm -f "${archive_path}" "${manifest_path}"

stage_root="$(mktemp -d "${RUNNER_TEMP:-/tmp}/honua-server-test-artifact.XXXXXX")"
cleanup() { rm -rf "${stage_root}"; }
trap cleanup EXIT

start_ns="$(date +%s%N)"
stage_project_dir="${stage_root}/${project_dir#"${REPO_ROOT}/"}"
mkdir -p "${stage_project_dir}/bin"
cp -a --reflink=auto "${bin_dir}" "${stage_project_dir}/bin/${CONFIGURATION}"
cp -a --reflink=auto "${obj_dir}" "${stage_project_dir}/obj"

# GitHub shard runners are ubuntu-latest x64. Keep neutral Unix assets and the exact
# Linux/Linux-x64 native payload; remove mobile, browser, Windows, macOS, musl and
# other-architecture RID directories. PDBs, test data and project assets are retained.
while IFS= read -r -d '' runtimes_dir; do
  while IFS= read -r -d '' runtime_dir; do
    runtime_id="$(basename "${runtime_dir}")"
    case "${runtime_id}" in
      linux|linux-x64|unix) ;;
      *) rm -rf "${runtime_dir}" ;;
    esac
  done < <(find "${runtimes_dir}" -mindepth 1 -maxdepth 1 -type d -print0)
done < <(find "${stage_root}" -type d -name runtimes -print0)

if find "${stage_root}" -type d -path '*/runtimes/*' \
    ! -path '*/runtimes/linux' ! -path '*/runtimes/linux/*' \
    ! -path '*/runtimes/linux-x64' ! -path '*/runtimes/linux-x64/*' \
    ! -path '*/runtimes/unix' ! -path '*/runtimes/unix/*' -print -quit | grep -q .; then
  echo "::error::Staged artifact contains a prohibited runtime identifier." >&2
  exit 1
fi

raw_bytes="$(( $(du -sb "${bin_dir}" | cut -f1) + $(du -sb "${obj_dir}" | cut -f1) ))"
unpacked_bytes="$(du -sb "${stage_root}" | cut -f1)"
file_count="$(find "${stage_root}" -type f | wc -l)"
if (( unpacked_bytes > MAX_UNPACKED_BYTES )); then
  echo "::error::Staged payload ${unpacked_bytes} bytes exceeds ${MAX_UNPACKED_BYTES}." >&2
  exit 1
fi

# Stable metadata plus gzip -n make identical build outputs reproducible. Level 1 is the
# measured speed/size point: materially smaller than level-0 without level-6 CPU cost.
tar --sort=name --mtime='UTC 1970-01-01' --owner=0 --group=0 --numeric-owner \
  -C "${stage_root}" -cf - . | gzip -1 -n > "${archive_path}"
archive_bytes="$(stat -c %s "${archive_path}")"
archive_sha256="$(sha256sum "${archive_path}" | cut -d' ' -f1)"
end_ns="$(date +%s%N)"
package_milliseconds="$(( (end_ns - start_ns) / 1000000 ))"

if (( archive_bytes > MAX_ARCHIVE_BYTES )); then
  echo "::error::Archive ${archive_bytes} bytes exceeds ${MAX_ARCHIVE_BYTES}." >&2
  exit 1
fi
if (( package_milliseconds > MAX_PACKAGE_MILLISECONDS )); then
  echo "::error::Packaging ${package_milliseconds}ms exceeds ${MAX_PACKAGE_MILLISECONDS}ms." >&2
  exit 1
fi

dotnet_sdk="${HONUA_SERVER_TEST_ARTIFACT_DOTNET_SDK:-}"
if [[ -z "${dotnet_sdk}" ]]; then
  command -v dotnet >/dev/null || { echo "::error::Required command 'dotnet' is unavailable." >&2; exit 2; }
  dotnet_sdk="$(dotnet --version)"
fi
created_at_epoch="${HONUA_SERVER_TEST_ARTIFACT_NOW_EPOCH:-$(date +%s)}"
if [[ ! "${created_at_epoch}" =~ ^[0-9]+$ ]] || (( created_at_epoch < 1 )); then
  echo "::error::Evidence creation time must be a positive Unix epoch." >&2
  exit 2
fi
expires_at_epoch="$(( created_at_epoch + EVIDENCE_TTL_SECONDS ))"
jq -nS \
  --arg contract "${CONTRACT}" \
  --arg source_sha "${source_sha,,}" \
  --arg dotnet_sdk "${dotnet_sdk}" \
  --arg project "${project}" \
  --arg artifact_suffix "${artifact_suffix}" \
  --arg archive_file "${archive_name}" \
  --arg archive_sha256 "${archive_sha256}" \
  --argjson raw_bytes "${raw_bytes}" \
  --argjson unpacked_bytes "${unpacked_bytes}" \
  --argjson archive_bytes "${archive_bytes}" \
  --argjson file_count "${file_count}" \
  --argjson package_milliseconds "${package_milliseconds}" \
  --argjson created_at_epoch "${created_at_epoch}" \
  --argjson expires_at_epoch "${expires_at_epoch}" \
  '{
    contract: $contract,
    source_sha: $source_sha,
    dotnet_sdk: $dotnet_sdk,
    project: $project,
    artifact_suffix: $artifact_suffix,
    archive_file: $archive_file,
    archive_sha256: $archive_sha256,
    raw_bytes: $raw_bytes,
    unpacked_bytes: $unpacked_bytes,
    archive_bytes: $archive_bytes,
    file_count: $file_count,
    package_milliseconds: $package_milliseconds,
    created_at_epoch: $created_at_epoch,
    expires_at_epoch: $expires_at_epoch,
    retained_runtime_ids: ["linux", "linux-x64", "unix"]
  }' > "${manifest_path}"

echo "Packaged ${project}: raw=${raw_bytes} staged=${unpacked_bytes} archive=${archive_bytes} bytes duration=${package_milliseconds}ms"
echo "${manifest_path}"
