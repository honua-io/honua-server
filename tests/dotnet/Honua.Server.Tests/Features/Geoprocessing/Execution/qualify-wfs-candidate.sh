#!/usr/bin/env bash
set -euo pipefail

# A disposable, real HTTP/PostGIS/Redis qualification environment. The caller
# supplies the release manifest's immutable server image, never a rebuilt binary.
export HONUA_GP_CANDIDATE_IMAGE="${1:?usage: qualify-wfs-candidate.sh image@sha256:digest receipt.json}"
receipt="${2:?supply a receipt path outside the temporary stack}"
[[ "$HONUA_GP_CANDIDATE_IMAGE" =~ @sha256:[0-9a-f]{64}$ ]]
export GP_PROOF_SOURCE_DIR
GP_PROOF_SOURCE_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
export GP_PROOF_CERT_DIR
GP_PROOF_CERT_DIR="$(mktemp -d /tmp/honua-wfs-proof.XXXXXX)"
project="gp-wfs-proof-$$"
compose=(docker compose -p "$project" -f "$GP_PROOF_SOURCE_DIR/wfs-candidate.compose.yml")
cleanup() {
    status=$?
    trap - EXIT
    "${compose[@]}" down --volumes --remove-orphans || status=1
    rm -f "$GP_PROOF_CERT_DIR/ca.pem" "$GP_PROOF_CERT_DIR/key.pem"
    rmdir "$GP_PROOF_CERT_DIR"
    exit "$status"
}
trap cleanup EXIT
openssl req -x509 -newkey rsa:2048 -nodes -days 2 \
    -keyout "$GP_PROOF_CERT_DIR/key.pem" -out "$GP_PROOF_CERT_DIR/ca.pem" \
    -subj '/CN=GP WFS qualification fixture' \
    -addext 'subjectAltName=IP:11.97.39.10,IP:127.0.0.1' >/dev/null 2>&1
"${compose[@]}" up -d
export HONUA_GP_ADMIN_KEY=gp-proof-admin-local
python3 - <<'PY'
import time, urllib.request
for attempt in range(90):
    try:
        with urllib.request.urlopen('http://127.0.0.1:18449/healthz/ready', timeout=2) as response:
            if response.status == 200:
                break
    except OSError:
        pass
    time.sleep(1)
else:
    raise SystemExit('candidate did not become ready')
PY
python3 "$GP_PROOF_SOURCE_DIR/qualify_wfs_candidate.py" \
    --base-url http://127.0.0.1:18449 --container "$("${compose[@]}" ps -q server)" \
    --image "$HONUA_GP_CANDIDATE_IMAGE" --fixture-address 11.97.39.10 \
    --certificate "$GP_PROOF_CERT_DIR/ca.pem" \
    --observations-url https://127.0.0.1:18450/observations --receipt "$receipt"
