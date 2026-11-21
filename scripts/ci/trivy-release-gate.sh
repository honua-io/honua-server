#!/usr/bin/env bash
# Blocking vulnerability gate for v* tag image releases, the nightly Compose executor that
# the customer quickstart pins, and the install images (.github/workflows/install-images.yml). Fails on HIGH/CRITICAL findings that have
# an upstream fix; unfixed findings are ignored by design, which is looser than the
# release-bundle container scan (that one also blocks on unfixed). Exceptions come only
# from the committed OpenVEX decisions and .trivyignore-release.
#
# Usage: TRIVY_IMAGE=<pinned trivy image> trivy-release-gate.sh <image@sha256:digest>
# Set RELEASE_GATE_PLATFORM to linux/amd64 or linux/arm64 to scan that platform of a
# multi-platform index; when it is unset, the runner's platform is scanned.
# The image is pulled into the local Docker daemon and scanned there, so the scan
# reads exactly the digest that is about to receive public tags.

set -euo pipefail

image="${1:?usage: trivy-release-gate.sh <image@sha256:digest>}"
if [[ ! "$image" =~ ^[^[:space:]@]+@sha256:[0-9a-f]{64}$ ]]; then
  echo "::error::release gate needs a digest reference, got $image" >&2
  exit 2
fi
: "${TRIVY_IMAGE:?TRIVY_IMAGE must name the pinned Trivy image}"
if [[ -n "${RELEASE_GATE_PLATFORM:-}" ]]; then
  if [[ ! "$RELEASE_GATE_PLATFORM" =~ ^linux/(amd64|arm64)$ ]]; then
    echo "::error::release gate platform must be linux/amd64 or linux/arm64, got $RELEASE_GATE_PLATFORM" >&2
    exit 2
  fi
  # Scan the platform's own manifest digest: some image stores resolve an index pulled with
  # --platform to the host platform when a scanner later reads it back by the index digest.
  child="$(docker buildx imagetools inspect --raw "$image" | python3 -c '
import json, sys
wanted = sys.argv[1].split("/")[1]
digests = [m["digest"] for m in json.load(sys.stdin).get("manifests", [])
           if m.get("platform", {}).get("os") == "linux" and m["platform"].get("architecture") == wanted]
print(digests[0] if len(digests) == 1 else "")' "$RELEASE_GATE_PLATFORM")"
  if [[ ! "$child" =~ ^sha256:[0-9a-f]{64}$ ]]; then
    echo "::error::$image has no single $RELEASE_GATE_PLATFORM manifest" >&2
    exit 2
  fi
  image="${image%@*}@${child}"
fi
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cache_dir="${TRIVY_CACHE_DIR:-${RUNNER_TEMP:-/tmp}/trivy-cache}"
mkdir -p "$cache_dir"
vex_dir="$(mktemp -d)"
trap 'rm -f "$vex_dir/release-gate.openvex.json"; rmdir "$vex_dir"' EXIT
python3 "$repo_root/scripts/security/release_images.py" vex-generate \
  --name tag-release-gate --output "$vex_dir/release-gate.openvex.json"

docker pull --quiet "$image" >/dev/null
docker run --rm \
  -v /var/run/docker.sock:/var/run/docker.sock \
  -v "$repo_root:/work:ro" \
  -v "$vex_dir:/vex:ro" \
  -v "$cache_dir:/root/.cache/trivy" \
  "$TRIVY_IMAGE" image \
  --scanners vuln \
  --skip-version-check \
  --severity HIGH,CRITICAL \
  --ignore-unfixed \
  --ignorefile /work/.trivyignore-release \
  --vex /vex/release-gate.openvex.json \
  --show-suppressed \
  --exit-code 1 \
  --format table \
  "$image"
