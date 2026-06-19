#!/usr/bin/env bash
# Demo B (ops champion) live-probe helper.
#
# Runs the read-only probes used by the Demo B recording runbook against the
# live demo (https://demo.honua.io) and prints a tidy, screen-friendly result
# for each beat. Use it to (a) warm the Lambda before recording and (b) confirm
# every beat will return what the runbook says it does.
#
# The admin X-API-Key is NEVER hard-coded. It is read at runtime from AWS
# Secrets Manager (secret id: honua-demo-demo/admin-password). You must have AWS
# credentials with secretsmanager:GetSecretValue on that secret, OR export
# HONUA_DEMO_API_KEY yourself before running.
#
# Usage:
#   ./demo-b-probes.sh                 # run every probe
#   BASE=https://demo.honua.io ./demo-b-probes.sh
#   HONUA_DEMO_API_KEY=... ./demo-b-probes.sh   # skip the AWS lookup
#
# Exit code is non-zero if any probe returns an unexpected status.
set -uo pipefail

BASE="${BASE:-https://demo.honua.io}"
SECRET_ID="${HONUA_DEMO_SECRET_ID:-honua-demo-demo/admin-password}"
FAIL=0

hr()  { printf '%s\n' "------------------------------------------------------------"; }
ok()  { printf '  [OK]   %s\n' "$1"; }
bad() { printf '  [FAIL] %s\n' "$1"; FAIL=1; }

# Resolve the admin API key (env wins; otherwise AWS Secrets Manager).
API_KEY="${HONUA_DEMO_API_KEY:-}"
if [ -z "$API_KEY" ]; then
  API_KEY="$(aws secretsmanager get-secret-value \
    --secret-id "$SECRET_ID" --query SecretString --output text 2>/dev/null || true)"
fi
if [ -z "$API_KEY" ]; then
  echo "WARNING: no admin API key (set HONUA_DEMO_API_KEY or grant access to $SECRET_ID)."
  echo "         Key-gated beats (2b, 3, 6) will be skipped."
fi

code() { curl -s -o /dev/null -w '%{http_code}' "$@"; }

echo "Demo B probes against: $BASE"
hr

# ── Beat 1: stand-up / serverless health ───────────────────────────────────
echo "Beat 1  Stand-up / serverless (scale-to-zero Lambda)"
LIVE=$(code "$BASE/healthz/live")
READY=$(code "$BASE/healthz/ready")
[ "$LIVE" = "200" ]  && ok "/healthz/live  -> 200"  || bad "/healthz/live  -> $LIVE (want 200)"
[ "$READY" = "200" ] && ok "/healthz/ready -> 200"  || bad "/healthz/ready -> $READY (want 200)"
hr

# ── Beat 2: security posture ────────────────────────────────────────────────
echo "Beat 2  Security headers + TLS + admin gate"
HDRS=$(curl -sI "$BASE/")
for h in 'strict-transport-security' 'x-frame-options: deny' 'cross-origin-opener-policy' 'x-content-type-options: nosniff' 'referrer-policy'; do
  if grep -qi "$h" <<<"$HDRS"; then ok "header present: $h"; else bad "header MISSING: $h"; fi
done
M_NOKEY=$(code "$BASE/metrics")
[ "$M_NOKEY" = "401" ] && ok "/metrics without key -> 401 (gated)" || bad "/metrics without key -> $M_NOKEY (want 401)"
if [ -n "$API_KEY" ]; then
  M_KEY=$(code -H "X-API-Key: $API_KEY" "$BASE/metrics")
  [ "$M_KEY" = "200" ] && ok "/metrics with X-API-Key -> 200" || bad "/metrics with key -> $M_KEY (want 200)"
fi
hr

# ── Beat 3: telemetry / observability ───────────────────────────────────────
echo "Beat 3  Telemetry (Prometheus + admin metrics API)"
if [ -n "$API_KEY" ]; then
  curl -s -H "X-API-Key: $API_KEY" "$BASE/metrics" -o /tmp/demob_metrics.$$ 2>/dev/null
  if grep -q 'honua_lambda_memory_limit' /tmp/demob_metrics.$$ 2>/dev/null; then
    ok "Prometheus exposes honua_lambda_* (proves Lambda runtime)"
    grep -m1 'honua_lambda_memory_limit' /tmp/demob_metrics.$$ | sed 's/^/         /'
  else
    bad "honua_lambda_* metric not found in /metrics"
  fi
  H=$(code -H "X-API-Key: $API_KEY" "$BASE/api/v1/metrics/health")
  [ "$H" = "200" ] && ok "/api/v1/metrics/health -> 200" || bad "/api/v1/metrics/health -> $H (want 200)"
  rm -f /tmp/demob_metrics.$$
else
  echo "  (skipped — no API key)"
fi
hr

# ── Render + query (proves the demo actually serves data) ───────────────────
echo "Render + query smoke (maui-roads)"
PNG=$(curl -s -o /tmp/demob_map.$$ -w '%{http_code}' \
  "$BASE/rest/services/maui-roads/MapServer/export?bbox=-156.7,20.7,-156.3,21.0&size=400,400&format=png&f=image")
if [ "$PNG" = "200" ] && file /tmp/demob_map.$$ 2>/dev/null | grep -qi png; then
  ok "MapServer export -> 200 PNG ($(file -b /tmp/demob_map.$$ 2>/dev/null))"
else
  bad "MapServer export -> $PNG (want a 200 PNG)"
fi
rm -f /tmp/demob_map.$$
CNT=$(curl -s "$BASE/rest/services/maui-roads/FeatureServer/3/query?where=1%3D1&returnCountOnly=true&f=json")
echo "  FeatureServer count query: $CNT"
grep -q '"count"' <<<"$CNT" && ok "FeatureServer query returns a count" || bad "FeatureServer query did not return a count"
hr

# ── Beat 6: GitOps deploy-control preflight (admin-gated, on trunk) ──────────
echo "Beat 6  Deploy-control preflight (server actuation surface)"
if [ -n "$API_KEY" ]; then
  PF_NOKEY=$(code "$BASE/api/v1/admin/deploy/preflight")
  [ "$PF_NOKEY" = "401" ] && ok "deploy/preflight without key -> 401" || bad "deploy/preflight without key -> $PF_NOKEY (want 401)"
  PF=$(curl -s -H "X-API-Key: $API_KEY" "$BASE/api/v1/admin/deploy/preflight")
  echo "  $PF"
  grep -q 'readyForCoordinatedDeploy' <<<"$PF" && ok "preflight returned coordination readiness" || bad "preflight payload unexpected"
else
  echo "  (skipped — no API key)"
fi
hr

if [ "$FAIL" = "0" ]; then echo "ALL PROBES OK"; else echo "ONE OR MORE PROBES FAILED"; fi
exit "$FAIL"
