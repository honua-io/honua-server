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

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"

# "<mirror tag>|<repo-relative Dockerfile>|<ARG name>"
MIRROR_MAP=(
  "sdk-10.0|Dockerfile|DOTNET_SDK_IMAGE"
  "aspnet-10.0-alpine|Dockerfile|DOTNET_ASPNET_IMAGE"
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
