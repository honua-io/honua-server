#!/usr/bin/env bash
# Prepare immutable upstream-equivalent images under the existing fixture tags.
# GitHub service containers must use mirrored references in the workflow itself:
# those pulls happen before checkout, so this script cannot prepare them.
set -euo pipefail

pull_image() {
  local source attempt
  for source in "$@"; do
    for attempt in 1 2; do
      if timeout 60s docker pull --platform linux/amd64 "${source}"; then
        return 0
      fi
      if [[ "${attempt}" == 1 ]]; then sleep 5; fi
    done
  done
  echo "::error::Could not pull required test image from any verified source." >&2
  return 1
}

prepare_image() {
  local target="$1" mirror="$2" upstream="$3" digest="$4"
  pull_image "${mirror}@${digest}" "${upstream}@${digest}"
  # Both references name the same immutable manifest; select the pulled source.
  if docker image inspect "${mirror}@${digest}" >/dev/null 2>&1; then
    docker tag "${mirror}@${digest}" "${target}"
  else
    docker tag "${upstream}@${digest}" "${target}"
  fi
  docker image inspect "${target}" >/dev/null
}

[[ "$#" -gt 0 ]] || { echo "Usage: $0 {ryuk|postgis18|postgis17|redis|mysql|postgres16} ..." >&2; exit 2; }
# Reject unknown inputs before mutating the image cache.
for image in "$@"; do
  case "${image}" in ryuk|postgis18|postgis17|redis|mysql|postgres16) ;; *) echo "Unknown test image: ${image}" >&2; exit 2 ;; esac
done
for image in "$@"; do
  case "${image}" in
    ryuk)
      # Keep the complete upstream index digest, matching .NET Testcontainers.
      # The workflow selects this mirror explicitly; retain resource cleanup.
      ryuk='mirror.gcr.io/testcontainers/ryuk:0.14.0@sha256:7c1a8a9a47c780ed0f983770a662f80deb115d95cce3e2daa3d12115b8cd28f0'
      pull_image "${ryuk}"
      docker image inspect "${ryuk}" >/dev/null
      ;;
    postgis18) prepare_image 'postgis/postgis:18-3.6' 'mirror.gcr.io/postgis/postgis:18-3.6' 'docker.io/postgis/postgis:18-3.6' 'sha256:7e00e8c3539fdd43f513b98806c8204714dcd09dea683c259e333d7690317119' ;;
    postgis17) prepare_image 'postgis/postgis:17-3.5' 'mirror.gcr.io/postgis/postgis:17-3.5' 'docker.io/postgis/postgis:17-3.5' 'sha256:8dfee83d8bd4c2873dc4a233c13ba2799a44f2edb16a0552d58715917fac32ba' ;;
    redis) prepare_image 'redis:7.2-alpine' 'public.ecr.aws/docker/library/redis:7.2-alpine' 'docker.io/library/redis:7.2-alpine' 'sha256:84bab713067f5494d94c24e99ae3fa3ae2388c037152edcbcf301f7cfaeb3048' ;;
    mysql) prepare_image 'mysql:8.0.36' 'public.ecr.aws/docker/library/mysql:8.0.36' 'docker.io/library/mysql:8.0.36' 'sha256:65ce0889751900d2dd5fc5aa5a5fb59073d401fc43df2eae6bd6afb18e7626dd' ;;
    postgres16) prepare_image 'postgres:16-alpine' 'public.ecr.aws/docker/library/postgres:16-alpine' 'docker.io/library/postgres:16-alpine' 'sha256:1a66d744c1b459e13b05a8fca341da84cb63383e99ce262210efee5a319d4551' ;;
  esac
done
