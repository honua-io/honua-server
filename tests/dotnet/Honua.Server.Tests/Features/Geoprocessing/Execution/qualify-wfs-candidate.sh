#!/usr/bin/env bash
set -euo pipefail

# A disposable, real HTTP/PostGIS/Redis qualification environment. The caller
# supplies the release manifest's immutable server image, never a rebuilt binary.
export HONUA_GP_CANDIDATE_IMAGE="${1:?usage: qualify-wfs-candidate.sh image@sha256:digest receipt.json}"
receipt="${2:?supply a receipt path outside the temporary stack}"
[[ "$HONUA_GP_CANDIDATE_IMAGE" =~ @sha256:[0-9a-f]{64}$ ]]
export GP_PROOF_SOURCE_DIR
GP_PROOF_SOURCE_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
export GP_PROOF_REPO_ROOT
GP_PROOF_REPO_ROOT="$(git -C "$GP_PROOF_SOURCE_DIR" rev-parse --show-toplevel)"
export GP_PROOF_CERT_DIR
GP_PROOF_CERT_DIR="$(mktemp -d /tmp/honua-wfs-proof.XXXXXX)"
project="gp-wfs-proof-$$"
compose=(docker compose -p "$project" -f "$GP_PROOF_SOURCE_DIR/wfs-candidate.compose.yml")
cleanup() {
    status=$?
    trap - EXIT
    "${compose[@]}" logs --no-color > "${receipt%.json}.server.log" 2>&1 || true
    cleanup_status=0
    "${compose[@]}" down --volumes --remove-orphans || cleanup_status=1
    if [[ "$cleanup_status" != 0 ]]; then status=1; fi
    python3 - "$receipt" "$status" "$cleanup_status" <<'PYRECEIPT'
import json, sys
from pathlib import Path
path = Path(sys.argv[1])
receipt = json.loads(path.read_text()) if path.exists() else {
    "schema": "honua.wfs-candidate-proof.v1", "outcome": "fail", "scenarios": [],
    "error": "qualification preflight failed; inspect the adjacent server log"}
receipt["cleanup"] = {"outcome": "pass" if sys.argv[3] == "0" else "fail"}
if sys.argv[2] != "0":
    receipt["outcome"] = "fail"
path.write_text(json.dumps(receipt, indent=2, ensure_ascii=False) + "\n")
PYRECEIPT
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
import os, time, urllib.request, urllib.error
last_error = None
for attempt in range(90):
    try:
        request = urllib.request.Request('http://127.0.0.1:18449/healthz/ready', headers={'X-API-Key': os.environ['HONUA_GP_ADMIN_KEY']})
        with urllib.request.urlopen(request, timeout=2) as response:
            if response.status == 200:
                break
    except urllib.error.HTTPError as error:
        last_error = (error.code, error.read().decode())
    except OSError as error:
        last_error = str(error)
    time.sleep(1)
else:
    raise SystemExit(f'candidate did not become ready: {last_error}')
PY
python3 "$GP_PROOF_SOURCE_DIR/qualify_wfs_candidate.py" \
    --base-url http://127.0.0.1:18449 --container "$("${compose[@]}" ps -q server)" \
    --image "$HONUA_GP_CANDIDATE_IMAGE" --fixture-address 11.97.39.10 \
    --certificate "$GP_PROOF_CERT_DIR/ca.pem" \
    --observations-url https://127.0.0.1:18450/observations --receipt "$receipt"
