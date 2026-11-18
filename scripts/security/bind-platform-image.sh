#!/usr/bin/env bash
# Pull one runnable platform manifest and write existing-image-identity.json.
#
# SCAN_IMAGE in the job environment stays the digest the caller named. A release
# OpenVEX document has to name that digest. This process passes the resolved
# manifest to the identity command only; it never appends SCAN_IMAGE to GITHUB_ENV.
set -euo pipefail

: "${SCAN_IMAGE:?An immutable image digest is required}"
: "${SCAN_PLATFORM:?A platform is required}"
: "${REGISTRY_TOKEN:?A registry token is required}"
: "${GITHUB_ACTOR:?A registry actor is required}"
: "${GITHUB_ENV:?GITHUB_ENV is required}"

printf '%s' "$REGISTRY_TOKEN" | docker login ghcr.io -u "$GITHUB_ACTOR" --password-stdin >/dev/null 2>&1

requested="$SCAN_IMAGE"
docker buildx imagetools inspect --raw "$requested" > requested-manifest.json
resolved="$(python3 scripts/security/release_images.py resolve-scan-image \
  --image "$requested" --platform "$SCAN_PLATFORM" < requested-manifest.json)"

{
  echo "REQUESTED_IMAGE=$requested"
  echo "RESOLVED_IMAGE=$resolved"
} >> "$GITHUB_ENV"

docker pull --platform "$SCAN_PLATFORM" "$resolved"
docker image inspect "$resolved" > existing-image-inspect.json
REQUESTED_IMAGE="$requested" SCAN_IMAGE="$resolved" \
  python3 scripts/security/existing-image-scan.py identity
