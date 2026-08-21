#!/usr/bin/env bash
# Single source of truth for the .NET base images that the nightly container
# build mirrors from MCR into GHCR.
#
# The digests live exactly once, in the `ARG DOTNET_*_IMAGE` defaults of the
# Dockerfiles that consume them. This script derives the mirror set from those
# defaults so a digest refresh in a Dockerfile can never diverge from what the
# `mirror-base-images` job pushes (and therefore from what the build jobs pull
# back out of GHCR).
#
# Usage:
#   scripts/ci/base-image-mirrors.sh            # print "<mirror-tag>\t<source-ref>" lines
#   scripts/ci/base-image-mirrors.sh --verify <workflow.yml>...
#                                               # fail if the workflow consumes a
#                                               # BASE_REPO tag this map does not
#                                               # mirror (or mirrors an unused tag)
#   scripts/ci/base-image-mirrors.sh --inventory-markdown
#                                               # render every digest-pinned .NET
#                                               # Dockerfile ARG as Markdown
#   scripts/ci/base-image-mirrors.sh --verify-inventory-doc <markdown-file>
#                                               # fail if the marked generated
#                                               # inventory differs from Dockerfiles
#   scripts/ci/base-image-mirrors.sh --self-test # exercise portable discovery,
#                                               # Dockerfile ARG grammar, and
#                                               # discovery-failure propagation

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"

# "<mirror tag>|<repo-relative Dockerfile>|<ARG name>"
MIRROR_MAP=(
  "sdk-10.0|Dockerfile|DOTNET_SDK_IMAGE"
  "aspnet-10.0|Dockerfile|DOTNET_ASPNET_IMAGE"
  "sdk-10.0-alpine|docker/Dockerfile.aot|DOTNET_SDK_IMAGE"
  "runtime-deps-10.0-alpine|docker/Dockerfile.aot|DOTNET_RUNTIME_DEPS_IMAGE"
  "sdk-10.0-lambda|docker/Dockerfile.lambda.aot|DOTNET_SDK_IMAGE"
  "runtime-deps-10.0|docker/Dockerfile.lambda.aot|DOTNET_RUNTIME_DEPS_IMAGE"
)

DIGEST_REF_PATTERN='^[A-Za-z0-9._/-]+:[A-Za-z0-9._-]+@sha256:[0-9a-f]{64}$'

resolve_arg_default() {
  local dockerfile="$1" arg_name="$2" path value
  path="${REPO_ROOT}/${dockerfile}"

  if [[ ! -f "${path}" ]]; then
    echo "::error::base-image mirror map references missing file ${dockerfile}" >&2
    return 1
  fi

  value="$(grep -m1 -E "^ARG[[:space:]]+${arg_name}=" "${path}" | cut -d= -f2- | tr -d '\r' || true)"
  value="${value%"${value##*[![:space:]]}"}"

  if [[ -z "${value}" ]]; then
    echo "::error::${dockerfile} has no default for ARG ${arg_name}" >&2
    return 1
  fi

  if [[ ! "${value}" =~ ${DIGEST_REF_PATTERN} ]]; then
    echo "::error::${dockerfile} ARG ${arg_name} must be digest-pinned (repo:tag@sha256:...), got '${value}'" >&2
    return 1
  fi

  printf '%s\n' "${value}"
}

print_mirror_set() {
  local entry tag dockerfile arg_name source_ref
  for entry in "${MIRROR_MAP[@]}"; do
    IFS='|' read -r tag dockerfile arg_name <<<"${entry}"
    source_ref="$(resolve_arg_default "${dockerfile}" "${arg_name}")"
    printf '%s\t%s\n' "${tag}" "${source_ref}"
  done
}

print_dotnet_inventory_markdown() {
  local path dockerfile line arg_name source_ref discovered_paths

  printf '%s\n' '| Dockerfile | Build argument | Image reference |'
  printf '%s\n' '| --- | --- | --- |'

  if ! discovered_paths="$(
    {
      printf '%s\n' "${REPO_ROOT}/Dockerfile"
      find "${REPO_ROOT}/docker" -type f -name 'Dockerfile*' -print
    } | LC_ALL=C sort
  )"; then
    echo "::error::failed to discover Dockerfiles for the .NET base-image inventory" >&2
    return 1
  fi

  while IFS= read -r path; do
    [[ -n "${path}" ]] || continue
    if [[ ! -f "${path}" ]]; then
      echo "::error::discovered inventory Dockerfile ${path} is missing" >&2
      return 1
    fi
    dockerfile="${path#"${REPO_ROOT}/"}"
    while IFS= read -r line; do
      line="${line%$'\r'}"
      if [[ "${line}" =~ ^[[:space:]]*[Aa][Rr][Gg][[:space:]]+(DOTNET_(SDK|ASPNET|RUNTIME_DEPS)_IMAGE)=(.*)$ ]]; then
        arg_name="${BASH_REMATCH[1]}"
        source_ref="${BASH_REMATCH[3]}"
        source_ref="${source_ref%"${source_ref##*[![:space:]]}"}"
        if [[ "${source_ref}" =~ ${DIGEST_REF_PATTERN} ]]; then
          printf '| `%s` | `%s` | `%s` |\n' "${dockerfile}" "${arg_name}" "${source_ref}"
        fi
      fi
    done < "${path}"
  done <<< "${discovered_paths}"
}

verify_inventory_doc() {
  local doc="$1" begin_marker end_marker begin_count end_count expected actual
  begin_marker='<!-- BEGIN GENERATED DOTNET BASE IMAGE INVENTORY -->'
  end_marker='<!-- END GENERATED DOTNET BASE IMAGE INVENTORY -->'

  if [[ ! -f "${doc}" ]]; then
    echo "::error::inventory document ${doc} not found" >&2
    return 1
  fi

  begin_count="$(grep -Fxc "${begin_marker}" "${doc}" || true)"
  end_count="$(grep -Fxc "${end_marker}" "${doc}" || true)"
  if [[ "${begin_count}" -ne 1 || "${end_count}" -ne 1 ]]; then
    echo "::error::${doc} must contain exactly one generated .NET inventory marker pair" >&2
    return 1
  fi

  expected="$(print_dotnet_inventory_markdown)"
  actual="$(awk -v begin="${begin_marker}" -v end="${end_marker}" '
    $0 == begin { capture = 1; next }
    $0 == end { capture = 0; next }
    capture { sub(/\r$/, ""); print }
  ' "${doc}")"

  if [[ "${actual}" != "${expected}" ]]; then
    echo "::error::${doc} .NET base-image inventory differs from digest-pinned Dockerfile ARG defaults" >&2
    diff -u <(printf '%s\n' "${actual}") <(printf '%s\n' "${expected}") || true
    return 1
  fi

  echo "Generated .NET base-image inventory is current in ${doc}."
}

run_self_tests() {
  local fixture_root failure_root output
  fixture_root="$(mktemp -d)"
  failure_root="$(mktemp -d)"

  mkdir -p "${fixture_root}/scripts/ci" "${fixture_root}/docker/nested" \
    "${failure_root}/scripts/ci"
  cp "${BASH_SOURCE[0]}" "${fixture_root}/scripts/ci/base-image-mirrors.sh"
  cp "${BASH_SOURCE[0]}" "${failure_root}/scripts/ci/base-image-mirrors.sh"

  printf '  arg DOTNET_SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0@sha256:%064d\n' 0 \
    > "${fixture_root}/Dockerfile"
  printf 'ArG DOTNET_RUNTIME_DEPS_IMAGE=mcr.microsoft.com/dotnet/runtime-deps:10.0-alpine@sha256:%064d\n' 1 \
    > "${fixture_root}/docker/nested/Dockerfile.aot"
  printf 'ARG DOTNET_SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0@sha256:%064d\n' 0 \
    > "${failure_root}/Dockerfile"

  output="$(bash "${fixture_root}/scripts/ci/base-image-mirrors.sh" --inventory-markdown)"
  if ! grep -Fq '| `Dockerfile` | `DOTNET_SDK_IMAGE` |' <<< "${output}" ||
     ! grep -Fq '| `docker/nested/Dockerfile.aot` | `DOTNET_RUNTIME_DEPS_IMAGE` |' <<< "${output}"; then
    echo "::error::inventory self-test omitted lowercase, indented, or recursively discovered ARG" >&2
    rm -rf "${fixture_root}" "${failure_root}"
    return 1
  fi

  if output="$(bash "${failure_root}/scripts/ci/base-image-mirrors.sh" --inventory-markdown 2>&1)"; then
    echo "::error::inventory self-test expected missing docker/ traversal to fail" >&2
    rm -rf "${fixture_root}" "${failure_root}"
    return 1
  fi
  if ! grep -Fq 'failed to discover Dockerfiles' <<< "${output}"; then
    echo "::error::inventory self-test did not receive an explicit discovery failure" >&2
    rm -rf "${fixture_root}" "${failure_root}"
    return 1
  fi

  rm -rf "${fixture_root}" "${failure_root}"
  echo "Base-image inventory portability and fail-closed self-tests passed."
}

verify_consumers() {
  local status=0 workflow entry tag consumed mirrored used

  mirrored=()
  for entry in "${MIRROR_MAP[@]}"; do
    mirrored+=("${entry%%|*}")
  done

  for workflow in "$@"; do
    if [[ ! -f "${workflow}" ]]; then
      echo "::error::workflow ${workflow} not found" >&2
      status=1
      continue
    fi

    while IFS= read -r consumed; do
      used=0
      for tag in "${mirrored[@]}"; do
        [[ "${tag}" == "${consumed}" ]] && used=1 && break
      done
      if [[ "${used}" -eq 0 ]]; then
        echo "::error::${workflow} pulls '${consumed}' from the base mirror, but no Dockerfile ARG maps to that tag" >&2
        status=1
      fi
    done < <(grep -oE 'env\.BASE_REPO[[:space:]]*\}\}:[A-Za-z0-9._-]+' "${workflow}" | sed 's/.*}}://' | LC_ALL=C sort -u)

    for tag in "${mirrored[@]}"; do
      if ! grep -qE "env\.BASE_REPO[[:space:]]*\}\}:${tag}([^A-Za-z0-9._-]|$)" "${workflow}"; then
        echo "::error::mirror tag '${tag}' is never consumed by ${workflow}; drop it from the map or wire it into a build" >&2
        status=1
      fi
    done
  done

  return "${status}"
}

main() {
  if [[ "${1:-}" == "--self-test" ]]; then
    shift
    if [[ "$#" -gt 0 ]]; then
      echo "::error::--self-test does not accept additional arguments" >&2
      exit 2
    fi
    run_self_tests
    exit 0
  fi

  if [[ "${1:-}" == "--inventory-markdown" ]]; then
    shift
    if [[ "$#" -gt 0 ]]; then
      echo "::error::--inventory-markdown does not accept additional arguments" >&2
      exit 2
    fi
    print_dotnet_inventory_markdown
    exit 0
  fi

  if [[ "${1:-}" == "--verify-inventory-doc" ]]; then
    shift
    if [[ "$#" -ne 1 ]]; then
      echo "::error::--verify-inventory-doc requires exactly one Markdown file" >&2
      exit 2
    fi
    verify_inventory_doc "$1"
    exit 0
  fi

  if [[ "${1:-}" == "--verify" ]]; then
    shift
    if [[ "$#" -eq 0 ]]; then
      echo "::error::--verify requires at least one workflow path" >&2
      exit 2
    fi
    verify_consumers "$@"
    echo "Base-image mirror map is consistent with $# workflow file(s)."
    exit 0
  fi

  if [[ "$#" -gt 0 ]]; then
    echo "::error::unknown argument '$1'" >&2
    exit 2
  fi

  print_mirror_set
}

main "$@"
