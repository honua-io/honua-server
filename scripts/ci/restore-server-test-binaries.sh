#!/usr/bin/env bash
# Fail-closed restoration of an exact-head server-test binary artifact.

set -euo pipefail

CONTRACT="honua.server-test-binaries.v1"
# Explicit opt-in keeps rerun-cache v1 readable while build-once requires same-run proof.
workflow_run_id="${HONUA_SERVER_TEST_ARTIFACT_WORKFLOW_RUN_ID:-}"
build_properties="${HONUA_SERVER_TEST_ARTIFACT_BUILD_PROPERTIES:-}"
if [[ -n "${workflow_run_id}" ]]; then
  [[ "${workflow_run_id}" =~ ^[1-9][0-9]*$ && -n "${build_properties}" ]] || {
    echo "::error::Strict artifacts require a positive workflow run id and explicit build properties." >&2; exit 2;
  }
  [[ "$(uname -s)/$(uname -m)" == Linux/x86_64 ]] || {
    echo "::error::Artifact native payload supports Linux/x86_64 only." >&2; exit 2;
  }
  CONTRACT="honua.server-test-binaries.v2"
fi

# Must match package-server-test-binaries.sh (#4453 re-base).
MAX_ARCHIVE_BYTES="${HONUA_SERVER_TEST_ARTIFACT_MAX_ARCHIVE_BYTES:-335544320}"
MAX_UNPACKED_BYTES="${HONUA_SERVER_TEST_ARTIFACT_MAX_UNPACKED_BYTES:-805306368}"
MAX_EVIDENCE_TTL_SECONDS="${HONUA_SERVER_TEST_ARTIFACT_MAX_TTL_SECONDS:-86400}"
manifest=""
destination=""
expected_project=""
expected_source_sha=""
timing_file="${HONUA_SERVER_TEST_ARTIFACT_TIMING_FILE:-}"

usage() {
  echo "Usage: $0 --manifest <file> --destination <repo-root> --project <relative.csproj> --source-sha <commit>" >&2
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --manifest) manifest="${2:-}"; shift 2 ;;
    --destination) destination="${2:-}"; shift 2 ;;
    --project) expected_project="${2:-}"; shift 2 ;;
    --source-sha) expected_source_sha="${2:-}"; shift 2 ;;
    *) usage; exit 2 ;;
  esac
done

if [[ -z "${manifest}" || -z "${destination}" || -z "${expected_project}" || -z "${expected_source_sha}" ]]; then
  usage
  exit 2
fi
for command in date jq python3 sha256sum tar timeout; do
  command -v "${command}" >/dev/null || { echo "::error::Required command '${command}' is unavailable." >&2; exit 2; }
done
REGISTRY="${HONUA_SERVER_TEST_ARTIFACT_REGISTRY:-${destination}/.github/server-test-artifact-projects.json}"
verify_start_ns="$(date +%s%N)"
[[ -f "${manifest}" ]] || { echo "::error::Artifact manifest is missing." >&2; exit 1; }
mkdir -p "${destination}"
destination="$(cd "${destination}" && pwd)"
manifest_dir="$(cd "$(dirname "${manifest}")" && pwd)"

dotnet_sdk="${HONUA_SERVER_TEST_ARTIFACT_DOTNET_SDK:-}"
if [[ -z "${dotnet_sdk}" ]]; then
  command -v dotnet >/dev/null || { echo "::error::Required command 'dotnet' is unavailable." >&2; exit 2; }
  dotnet_sdk="$(dotnet --version)"
fi
if [[ -n "${workflow_run_id}" && ! "${dotnet_sdk}" =~ ^[0-9]+\.[0-9]+\.[0-9]+([.-][A-Za-z0-9.-]+)?$ ]]; then
  echo "::error::Strict artifacts require a valid SDK version." >&2; exit 2
fi

now_epoch="${HONUA_SERVER_TEST_ARTIFACT_NOW_EPOCH:-$(date +%s)}"
if [[ ! "${now_epoch}" =~ ^[0-9]+$ ]] || [[ ! "${MAX_EVIDENCE_TTL_SECONDS}" =~ ^[0-9]+$ ]]; then
  echo "::error::Evidence time bounds must be positive integers." >&2
  exit 2
fi

registry_sha256=""
if [[ -n "${workflow_run_id}" ]]; then
  registry_sha256="$(sha256sum "${REGISTRY}" | cut -d' ' -f1)"
fi
dependency_roots_json='[]'
host_output_contract=""
if [[ "${CONTRACT}" == honua.server-test-binaries.v2 && "${expected_project}" == tests/dotnet/Honua.Server.Tests/Honua.Server.Tests.csproj ]]; then
  dependency_roots_json="$(jq -nc --arg root "src/Honua.Server/bin/${HONUA_SERVER_TEST_ARTIFACT_CONFIGURATION:-Release}" '[$root]')"
  host_output_contract="honua.server-boot-output.v1"
fi
jq -e \
  --arg host_output_contract "${host_output_contract}" \
  --argjson dependency_roots "${dependency_roots_json}" \
  --arg registry_sha256 "${registry_sha256}" \
  --arg workflow_run_id "${workflow_run_id}" \
  --arg build_properties "${build_properties}" \
  --arg configuration "${HONUA_SERVER_TEST_ARTIFACT_CONFIGURATION:-Release}" \
  --arg contract "${CONTRACT}" \
  --arg project "${expected_project}" \
  --arg source_sha "${expected_source_sha,,}" \
  --arg dotnet_sdk "${dotnet_sdk}" '
    .contract == $contract and
    ((.required_dependency_output_roots // []) == $dependency_roots) and
    (if $host_output_contract != "" then .host_output_contract == $host_output_contract else true end) and
    (if $contract == "honua.server-test-binaries.v2" then
      .build_identity == {workflow_run_id:$workflow_run_id,build_properties:$build_properties,
        configuration:$configuration,os:"Linux",architecture:"x86_64",registry_sha256:$registry_sha256}
    else true end) and
    .project == $project and
    .source_sha == $source_sha and
    .dotnet_sdk == $dotnet_sdk and
    (.archive_file | type == "string" and test("^server-test-binaries-[a-z0-9-]+\\.tar\\.gz$")) and
    (.archive_sha256 | type == "string" and test("^[0-9a-f]{64}$")) and
    (.archive_bytes | type == "number" and . > 0) and
    (.unpacked_bytes | type == "number" and . > 0) and
    (.file_count | type == "number" and . > 0)
    and ((.created_at_epoch | type) == "number" and .created_at_epoch > 0)
    and ((.expires_at_epoch | type) == "number" and .expires_at_epoch > .created_at_epoch)
    and (.repo_root | type == "string" and startswith("/"))
    and ((.static_web_asset_content_roots | type) == "object")
    and ((.static_web_asset_content_roots.checkout | type) == "array")
    and ((.static_web_asset_content_roots.nuget | type) == "array")
    and ((.static_web_asset_content_roots.payload | type) == "array")
    and all(.static_web_asset_content_roots.payload[];
      type == "string"
      and test("^[A-Za-z0-9_.@+-]+(/[A-Za-z0-9_.@+-]+)*$")
      and (split("/") | all(.[]; . != "." and . != "..") and (index("bin") != null or index("obj") != null)))
  ' "${manifest}" >/dev/null || {
  echo "::error::Artifact manifest does not match the exact source/project/toolchain contract." >&2
  exit 1
}

created_at_epoch="$(jq -r '.created_at_epoch' "${manifest}")"
expires_at_epoch="$(jq -r '.expires_at_epoch' "${manifest}")"
if (( created_at_epoch > now_epoch || expires_at_epoch < now_epoch || expires_at_epoch - created_at_epoch > MAX_EVIDENCE_TTL_SECONDS )); then
  echo "::error::Artifact evidence is not within its accepted validity window." >&2
  exit 1
fi

archive_file="$(jq -r '.archive_file' "${manifest}")"
archive_path="${manifest_dir}/${archive_file}"
[[ -f "${archive_path}" ]] || { echo "::error::Artifact archive is missing." >&2; exit 1; }
actual_bytes="$(stat -c %s "${archive_path}")"
manifest_bytes="$(jq -r '.archive_bytes' "${manifest}")"
unpacked_bytes="$(jq -r '.unpacked_bytes' "${manifest}")"
if (( actual_bytes != manifest_bytes || actual_bytes > MAX_ARCHIVE_BYTES || unpacked_bytes > MAX_UNPACKED_BYTES )); then
  echo "::error::Artifact size does not match the bounded manifest." >&2
  exit 1
fi
expected_digest="$(jq -r '.archive_sha256' "${manifest}")"
actual_digest="$(sha256sum "${archive_path}" | cut -d' ' -f1)"
[[ "${actual_digest}" == "${expected_digest}" ]] || { echo "::error::Artifact SHA-256 integrity check failed." >&2; exit 1; }

configuration="${HONUA_SERVER_TEST_ARTIFACT_CONFIGURATION:-Release}"
project_relative_dir="$(dirname "${expected_project}")"
allowed_root_args=(
  --allowed-root "${project_relative_dir}/bin/${configuration}"
  --allowed-root "${project_relative_dir}/obj"
)
while IFS= read -r dependency_root; do
  allowed_root_args+=(--allowed-root "${dependency_root}")
done < <(jq -r '.required_dependency_output_roots[]?' "${manifest}")
while IFS= read -r payload_root; do
  allowed_root_args+=(--allowed-root "${payload_root}")
done < <(jq -r '.static_web_asset_content_roots.payload[]' "${manifest}")

# Inspect every entry before extraction. Paths must be normalized and unique, and
# must lie inside the test project's output or a declared static web asset payload
# root; symlinks, hardlinks, devices, FIFOs, control characters, and expansion bombs
# are rejected even when their archive digest is otherwise valid.
timeout 120 python3 "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/validate-server-test-archive.py" \
  --archive "${archive_path}" \
  --max-unpacked-bytes "${MAX_UNPACKED_BYTES}" \
  "${allowed_root_args[@]}"

verify_end_ns="$(date +%s%N)"
unpack_start_ns="${verify_end_ns}"
stage_root="$(mktemp -d "${destination}/.honua-artifact-stage.XXXXXX")"
trap 'rm -rf "${stage_root}"' EXIT
# A group TERM can arrive while the installer is rolling back. Bash's EXIT
# cleanup must wait for that child; otherwise it can delete the backup journal.
# wait without a PID also covers delivery between background spawn and $! capture.
cancel_restore() {
  trap '' TERM INT
  wait || true
  exit 143
}
trap cancel_restore TERM INT
timeout 120 tar --no-same-owner --no-same-permissions -xzf "${archive_path}" -C "${stage_root}"
unpack_end_ns="$(date +%s%N)"
[[ "$(find "${stage_root}" -type f | wc -l)" -eq "$(jq -r '.file_count' "${manifest}")" ]] || {
  echo "::error::Restored file count does not match manifest." >&2; exit 1;
}
while IFS= read -r payload_root; do
  [[ -d "${stage_root}/${payload_root}" ]] || {
    echo "::error::Missing staged static asset payload root: ${payload_root}" >&2; exit 1;
  }
done < <(jq -r '.static_web_asset_content_roots.payload[]' "${manifest}")
project_dir="${stage_root}/${project_relative_dir}"
[[ -f "${project_dir}/obj/project.assets.json" ]] || { echo "::error::Restored project assets are missing." >&2; exit 1; }
find "${project_dir}/bin/${configuration}" -type f -name '*.dll' -print -quit | grep -q . || {
  echo "::error::Restored test binaries are missing." >&2
  exit 1
}
find "${project_dir}/bin/${configuration}" -type f -name '*.pdb' -print -quit | grep -q . || {
  echo "::error::Restored PDB failure-attribution evidence is missing." >&2
  exit 1
}

for pattern in '*.deps.json' '*.runtimeconfig.json'; do
  find "${project_dir}/bin/${configuration}" -type f -name "${pattern}" -print -quit | grep -q . || {
    echo "::error::Restored output missing ${pattern}." >&2; exit 1;
  }
done

if [[ -n "${host_output_contract}" ]]; then
  host_output="${stage_root}/src/Honua.Server/bin/${configuration}/net10.0"
  python3 - "${host_output}" <<'HOSTPY'
import json
from pathlib import Path
import sys
root = Path(sys.argv[1])
for name in ('Honua.Server.dll', 'Honua.Server.pdb', 'Honua.Server.deps.json', 'Honua.Server.runtimeconfig.json'):
    if not (root / name).is_file():
        raise ValueError(f'Missing required canonical boot-host output: {name}')
config = json.loads((root / 'Honua.Server.runtimeconfig.json').read_text())
if not isinstance(config.get('runtimeOptions'), dict):
    raise ValueError('Invalid boot-host runtime configuration')
deps = json.loads((root / 'Honua.Server.deps.json').read_text())
targets = deps.get('targets')
if not isinstance(targets, dict) or not targets:
    raise ValueError('Invalid boot-host dependency manifest')
for target in targets.values():
    for library in target.values():
        for section in ('runtime', 'native', 'resources', 'runtimeTargets'):
            for asset, metadata in library.get(section, {}).items():
                if section == 'runtimeTargets' and metadata.get('rid') not in ('linux', 'linux-x64', 'unix'):
                    continue
                path = Path(asset)
                if path.is_absolute() or '..' in path.parts:
                    raise ValueError(f'Unsafe host dependency path: {asset}')
                if path.name == '_._':
                    continue
                candidates = [root / path, root / path.name]
                if section == 'resources':
                    locale = metadata.get('locale')
                    if not isinstance(locale, str) or '/' in locale or '\\' in locale or locale in ('.', '..'):
                        raise ValueError(f'Invalid resource locale: {asset}')
                    candidates = [root / path, root / locale / path.name]
                if not any(candidate.is_file() for candidate in candidates):
                    raise ValueError(f'Missing boot-host dependency: {asset}')
HOSTPY
fi

# Every static web asset content root the restored host opens must exist, or this
# shard would silently serve 404 where a building shard serves 200 (#4453). Roots
# under the packaging repository are checked at the same relative path here.
packaged_repo_root="$(jq -r '.repo_root' "${manifest}")"
missing_roots=()
while IFS= read -r -d '' swa_manifest; do
  roots="$(jq -r '.ContentRoots
    | if type == "array" and all(.[]; type == "string") then .[] else error("ContentRoots is not a string array") end
  ' "${swa_manifest}")" || {
    echo "::error::Restored static web assets manifest '${swa_manifest}' has no readable ContentRoots." >&2
    exit 1
  }
  while IFS= read -r root; do
    root="${root%/}"
    [[ -n "${root}" ]] || continue
    if [[ "${root}" == "${packaged_repo_root}"/* ]]; then
      relative_root="${root#"${packaged_repo_root}/"}"
      if [[ -d "${stage_root}/${relative_root}" ]]; then
        root="${stage_root}/${relative_root}"
      else
        root="${destination}/${relative_root}"
      fi
    fi
    [[ -d "${root}" ]] || missing_roots+=("${root}")
  done <<<"${roots}"
done < <(find "${stage_root}" -type f -name '*.staticwebassets.runtime.json' -print0)
if (( ${#missing_roots[@]} > 0 )); then
  echo "::error::Restored payload is missing static web asset content roots a local build serves from: ${missing_roots[*]}" >&2
  exit 1
fi

# Install only after all validation, replacing whole output roots. Roll back every
# replaced root if installation fails; staging resides on the destination filesystem.
python3 "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/install-server-test-artifact.py" \
  "${stage_root}" "${destination}" "${manifest}" "${project_relative_dir}" "${configuration}" &
installer_pid=$!
wait "${installer_pid}"

if [[ -n "${timing_file}" ]]; then
  mkdir -p "$(dirname "${timing_file}")"
  jq -nS \
    --argjson integrity_check_ms "$(( (verify_end_ns - verify_start_ns) / 1000000 ))" \
    --argjson unpack_ms "$(( (unpack_end_ns - unpack_start_ns) / 1000000 ))" \
    '{integrity_check_ms:$integrity_check_ms,unpack_ms:$unpack_ms}' > "${timing_file}"
fi

echo "Restored ${expected_project} from ${archive_file} (${actual_bytes} bytes)."
