#!/usr/bin/env bash
# Build-output packaging contract for exact-head server-test shard consumers.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DEFAULT_REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"
REPO_ROOT="${HONUA_SERVER_TEST_ARTIFACT_REPO_ROOT:-${DEFAULT_REPO_ROOT}}"
REGISTRY="${HONUA_SERVER_TEST_ARTIFACT_REGISTRY:-${REPO_ROOT}/.github/server-test-artifact-projects.json}"
CONFIGURATION="${HONUA_SERVER_TEST_ARTIFACT_CONFIGURATION:-Release}"
# 320 MiB / 768 MiB since #4453 staged hosted Blazor content roots; measured basis in
# docs/internal/ci/server-test-binary-artifacts.md ("Bounds and integrity").
MAX_ARCHIVE_BYTES="${HONUA_SERVER_TEST_ARTIFACT_MAX_ARCHIVE_BYTES:-335544320}"
MAX_UNPACKED_BYTES="${HONUA_SERVER_TEST_ARTIFACT_MAX_UNPACKED_BYTES:-805306368}"
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

# Static web asset content roots (#4453). Every *.staticwebassets.runtime.json in the test
# output names the directories the host serves static web assets from, as absolute paths on
# the building machine. A consumer must find each of them exactly as the builder did, or a
# materialized shard silently 404s where a building shard serves 200. Every referenced root
# is therefore classified, and anything a consumer could not reproduce fails packaging:
#   payload  - another project's bin/ or obj/ build output inside the repository; staged here
#   checkout - repository content outside bin/obj; must hold nothing a clean checkout lacks
#   nuget    - a restored package's staticwebassets/ folder; provided by the NuGet cache
# The test project's own bin/<Configuration> and obj are already staged above.
nuget_root="${NUGET_PACKAGES:-${HOME}/.nuget/packages}"
nuget_root="${nuget_root%/}"
referenced_roots=""
while IFS= read -r -d '' swa_manifest; do
  roots="$(jq -r '.ContentRoots
    | if type == "array" and all(.[]; type == "string") then .[] else error("ContentRoots is not a string array") end
  ' "${swa_manifest}")" || {
    echo "::error::Static web assets manifest '${swa_manifest#"${REPO_ROOT}/"}' has no readable ContentRoots." >&2
    exit 1
  }
  # Blank lines between manifests are skipped by the classification loop below.
  referenced_roots="$(printf '%s\n%s' "${referenced_roots}" "${roots}")"
done < <(find "${bin_dir}" -type f -name '*.staticwebassets.runtime.json' -print0 | LC_ALL=C sort -z)

payload_roots=()
checkout_roots=()
nuget_roots=()
while IFS= read -r root; do
  root="${root%/}"
  [[ -n "${root}" ]] || continue
  case "${root}" in
    "${bin_dir}" | "${bin_dir}"/* | "${obj_dir}" | "${obj_dir}"/*)
      mkdir -p "${stage_root}/${root#"${REPO_ROOT}/"}"
      continue
      ;;
  esac
  if [[ "/${root}/" == */../* || "/${root}/" == */./* ]]; then
    echo "::error::Static web asset content root '${root}' is not a normalized path." >&2
    exit 1
  fi
  if [[ "${root}" == "${REPO_ROOT}"/* ]]; then
    relative="${root#"${REPO_ROOT}/"}"
    if [[ "/${relative}/" == */bin/* || "/${relative}/" == */obj/* ]]; then
      payload_roots+=("${relative}")
    else
      checkout_roots+=("${relative}")
    fi
  elif [[ "${root}" == "${nuget_root}"/* ]]; then
    nuget_roots+=("${root}")
  else
    echo "::error::Static web asset content root '${root}' is outside the repository and the NuGet package folder, so no consumer of this payload can reproduce it." >&2
    exit 1
  fi
done < <(printf '%s' "${referenced_roots}" | LC_ALL=C sort -u)

payload_raw_bytes=0
for relative in "${payload_roots[@]}"; do
  mkdir -p "${stage_root}/${relative}"
  # A root the build never materialized is staged empty: that is exactly what the building
  # host sees once LoadHostedBlazorStaticWebAssets pre-creates it (#2904).
  if [[ -d "${REPO_ROOT}/${relative}" ]]; then
    cp -a --reflink=auto "${REPO_ROOT}/${relative}/." "${stage_root}/${relative}/"
    payload_raw_bytes="$(( payload_raw_bytes + $(du -sb "${REPO_ROOT}/${relative}" | cut -f1) ))"
  fi
done
for relative in "${checkout_roots[@]}"; do
  if [[ ! -d "${REPO_ROOT}/${relative}" ]]; then
    echo "::error::Static web asset content root '${relative}' is referenced but absent from the repository." >&2
    exit 1
  fi
  command -v git >/dev/null && git -C "${REPO_ROOT}" rev-parse --is-inside-work-tree >/dev/null 2>&1 || {
    echo "::error::Cannot prove checkout content root '${relative}' matches a clean checkout: '${REPO_ROOT}' is not a git work tree." >&2
    exit 1
  }
  # Without --exclude-standard this lists ignored files too: both are absent from a clean checkout.
  untracked="$(git -C "${REPO_ROOT}" ls-files --others -- "${relative}" | awk 'NR <= 5' | paste -sd, -)"
  if [[ -n "${untracked}" ]]; then
    echo "::error::Static web asset content root '${relative}' holds files a clean checkout does not have: ${untracked}" >&2
    exit 1
  fi
done
for root in "${nuget_roots[@]}"; do
  [[ -d "${root}" ]] || {
    echo "::error::Static web asset content root '${root}' names a NuGet package folder that is not restored." >&2
    exit 1
  }
done

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

raw_bytes="$(( $(du -sb "${bin_dir}" | cut -f1) + $(du -sb "${obj_dir}" | cut -f1) + payload_raw_bytes ))"
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
json_array() {
  if (( $# == 0 )); then printf '[]'; else printf '%s\n' "$@" | jq -R . | jq -sc .; fi
}
jq -nS \
  --arg repo_root "${REPO_ROOT}" \
  --argjson payload_roots "$(json_array "${payload_roots[@]}")" \
  --argjson checkout_roots "$(json_array "${checkout_roots[@]}")" \
  --argjson nuget_roots "$(json_array "${nuget_roots[@]}")" \
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
    retained_runtime_ids: ["linux", "linux-x64", "unix"],
    repo_root: $repo_root,
    static_web_asset_content_roots: {
      payload: $payload_roots,
      checkout: $checkout_roots,
      nuget: $nuget_roots
    }
  }' > "${manifest_path}"

echo "Packaged ${project}: raw=${raw_bytes} staged=${unpacked_bytes} archive=${archive_bytes} bytes duration=${package_milliseconds}ms"
echo "${manifest_path}"
