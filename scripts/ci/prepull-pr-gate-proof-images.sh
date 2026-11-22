#!/usr/bin/env bash
# Prepare the secondary image consumed by the required execution proofs.
# These linux/amd64 manifest digests were verified against Docker Hub. Mirrors
# avoid its shared-runner rate limits; the same upstream digest is a fallback.
# Retain the test consumers' image names so no test or runtime configuration
# changes. Unlike the background canonical PostGIS warmup, this is required:
# an exhausted download fails here before an otherwise passing test suite.
set -euo pipefail

prepare_image() {
  local target="$1" digest="$2" mirror="$3" source attempt
  for source in "${mirror}@${digest}" "${target}@${digest}"; do
    for attempt in 1 2; do
      echo "Preparing ${target} from ${source} (attempt ${attempt}/2)."
      if timeout 60s docker pull --platform linux/amd64 "${source}"; then
        docker tag "${source}" "${target}"
        docker image inspect "${target}" --format '{{.Id}}'
        return 0
      fi
      if [[ "${attempt}" == 1 ]]; then
        sleep 5
      fi
    done
  done
  echo "::error::Unable to prepare required proof image ${target}." >&2
  return 1
}

# PR Gate currently runs on the linux/amd64 ubuntu-latest runner. Keep these
# exact test tags paired with their verified manifests when refreshing images.
prepare_image 'postgis/postgis:17-3.5' \
  'sha256:8dfee83d8bd4c2873dc4a233c13ba2799a44f2edb16a0552d58715917fac32ba' \
  'mirror.gcr.io/postgis/postgis:17-3.5'
