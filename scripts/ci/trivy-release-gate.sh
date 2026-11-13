#!/usr/bin/env bash
# Blocking vulnerability gate for v* tag image releases. Fails on HIGH/CRITICAL
# findings that have an upstream fix; unfixed findings are ignored by design, which
# is looser than the release-bundle container scan (that one also blocks on unfixed).
# Exceptions come only from .trivyignore-release in the tagged commit.
#
# Usage: TRIVY_IMAGE=<pinned trivy image> trivy-release-gate.sh <image@sha256:digest>
# The image is pulled into the local Docker daemon and scanned there, so the scan
# reads exactly the digest that is about to receive public tags.

set -euo pipefail

image="${1:?usage: trivy-release-gate.sh <image@sha256:digest>}"
if [[ "$image" != *@sha256:* ]]; then
  echo "::error::release gate needs a digest reference, got $image" >&2
  exit 2
fi
: "${TRIVY_IMAGE:?TRIVY_IMAGE must name the pinned Trivy image}"
cache_dir="${TRIVY_CACHE_DIR:-${RUNNER_TEMP:-/tmp}/trivy-cache}"
mkdir -p "$cache_dir"

docker pull --quiet "$image" >/dev/null
docker run --rm \
  -v /var/run/docker.sock:/var/run/docker.sock \
  -v "$PWD:/work:ro" \
  -v "$cache_dir:/root/.cache/trivy" \
  "$TRIVY_IMAGE" image \
  --scanners vuln \
  --skip-version-check \
  --severity HIGH,CRITICAL \
  --ignore-unfixed \
  --ignorefile /work/.trivyignore-release \
  --exit-code 1 \
  --format table \
  "$image"
