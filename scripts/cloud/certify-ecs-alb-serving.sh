#!/usr/bin/env bash
# Certify that the service behind the certification ALB is HONUA, and that it
# serves (honua-server#4414, defect 3).
#
# `AwsEcsAlbRealCertificationTests` drives weighted cutover, convergence and
# rollback against real ELBv2/ECS, and those control-plane assertions are precise
# and worth keeping. But the ECS service under that ALB is
# `public.ecr.aws/nginx/nginx:stable-alpine` (honua-iac
# `infrastructure/terraform/examples/aws-cert/ecs-alb-cert.tf`), so ZERO HTTP
# requests have ever reached Honua through it: no row count, no write
# round-trip, no authorization denial. A run of that cell is evidence about
# weighted-cutover MECHANICS, not an "ECS certification" of the product.
#
# This script closes that gap from the honua-server side. Point
# HONUA_REALAWS_CERT_ALB_BASE_URL at the cert ALB and it certifies, through the
# real data path:
#   1. the service identifies as Honua (and FAILS LOUDLY, naming the placeholder,
#      when the ALB is still fronting nginx or any other non-Honua service),
#   2. readiness reports Ready,
#   3. the GeoServices catalog serves a real `currentVersion`,
#   4. a FeatureServer query returns the required expected row count,
#   5. an unauthenticated request to an admin route is denied.
#
# It never weakens the lane: when the base URL is not configured the caller
# records that the run certified mechanics against a placeholder service. What it
# refuses to do is let a green run be read as "Honua was certified on ECS".
#
# Environment:
#   HONUA_REALAWS_CERT_ALB_BASE_URL          Required. Public base URL of the cert ALB.
#   HONUA_REALAWS_CERT_ALB_ADMIN_API_KEY     Optional. Enables the authenticated admin check.
#   HONUA_REALAWS_CERT_ALB_QUERY_PATH        Required. FeatureServer query path (with query string).
#   HONUA_REALAWS_CERT_ALB_EXPECTED_COUNT    Required. Expected feature count for that query.
#   HONUA_REALAWS_CERT_ALB_TIMEOUT_SECONDS   Optional. Per-request timeout (default 20).

set -Eeuo pipefail

BASE_URL="${HONUA_REALAWS_CERT_ALB_BASE_URL:-}"
ADMIN_API_KEY="${HONUA_REALAWS_CERT_ALB_ADMIN_API_KEY:-}"
QUERY_PATH="${HONUA_REALAWS_CERT_ALB_QUERY_PATH:-}"
EXPECTED_COUNT="${HONUA_REALAWS_CERT_ALB_EXPECTED_COUNT:-}"
TIMEOUT="${HONUA_REALAWS_CERT_ALB_TIMEOUT_SECONDS:-20}"

if [[ -z "${BASE_URL}" ]]; then
    echo "::error::HONUA_REALAWS_CERT_ALB_BASE_URL is required for the Honua serving certification." >&2
    exit 1
fi
BASE_URL="${BASE_URL%/}"
if [[ ! "${BASE_URL}" =~ ^https?:// ]]; then
    BASE_URL="https://${BASE_URL}"
fi

command -v jq >/dev/null 2>&1 || { echo "::error::jq is required." >&2; exit 1; }

failures=0
summary_lines=()

record() {
    local status="$1" message="$2"
    summary_lines+=("- ${status} ${message}")
    if [[ "${status}" == "FAIL" ]]; then
        echo "::error::${message}" >&2
        failures=$((failures + 1))
    else
        echo "${status}: ${message}"
    fi
}

fetch_status() {
    curl --silent --output /dev/null --write-out '%{http_code}' \
        --max-time "${TIMEOUT}" --connect-timeout 10 "$@" || echo "000"
}

fetch_body() {
    curl --silent --fail --max-time "${TIMEOUT}" --connect-timeout 10 "$@" || true
}

echo "Certifying Honua serving through ${BASE_URL}"

# 1. Identity. The placeholder cert substrate answers / with the nginx welcome
#    page and has no /healthz route at all, so this is the check that tells a
#    Honua deployment apart from the stand-in.
live_status="$(fetch_status "${BASE_URL}/healthz/live")"
root_body="$(fetch_body "${BASE_URL}/")"
if [[ "${live_status}" != "200" ]]; then
    if printf '%s' "${root_body}" | grep -qiE 'welcome to nginx|<title>[[:space:]]*nginx'; then
        record FAIL "the service behind this ALB is the nginx placeholder, not Honua — /healthz/live returned HTTP ${live_status} and / served the nginx welcome page. A weighted-cutover run against this substrate certifies ALB/ECS mechanics only; it is not an ECS certification of Honua."
    else
        record FAIL "GET ${BASE_URL}/healthz/live returned HTTP ${live_status}; the service behind this ALB does not expose Honua's health surface."
    fi
else
    record PASS "the ALB serves Honua's liveness surface (/healthz/live -> 200)."
fi

# 2. Readiness (migrations applied, dependencies reachable).
ready_body="$(fetch_body "${BASE_URL}/healthz/ready")"
if [[ "${ready_body}" == Ready ]]; then
    record PASS "readiness through the ALB reports Ready."
else
    record FAIL "GET ${BASE_URL}/healthz/ready did not report Ready (got: ${ready_body:-<empty>})."
fi

# 3. A real protocol response, not just a probe.
info_body="$(fetch_body "${BASE_URL}/rest/info")"
current_version="$(printf '%s' "${info_body}" | jq -r '.currentVersion // empty' 2>/dev/null || true)"
if [[ -n "${current_version}" ]]; then
    record PASS "the GeoServices catalog served currentVersion=${current_version} through the ALB."
else
    record FAIL "GET ${BASE_URL}/rest/info did not return a GeoServices document with currentVersion — the data path through the ALB is not serving Honua."
fi

# 4. Row count through the data path. This is the assertion the lane has never
#    had: a number of features returned by the deployed service.
if [[ -n "${QUERY_PATH}" && -n "${EXPECTED_COUNT}" ]]; then
    query_url="${BASE_URL}/${QUERY_PATH#/}"
    query_body="$(fetch_body "${query_url}")"
    actual_count="$(printf '%s' "${query_body}" | jq -r '
        if (.features | type) == "array" then (.features | length)
        elif (.count | type) == "number" then .count
        else empty end' 2>/dev/null || true)"
    if [[ -z "${actual_count}" ]]; then
        record FAIL "the FeatureServer query ${query_url} returned no features array and no count."
    elif [[ "${actual_count}" != "${EXPECTED_COUNT}" ]]; then
        record FAIL "the FeatureServer query ${query_url} returned ${actual_count} feature(s), expected ${EXPECTED_COUNT}."
    else
        record PASS "the FeatureServer query returned the expected ${EXPECTED_COUNT} feature(s) through the ALB."
    fi
else
    record FAIL "row-count certification requires both HONUA_REALAWS_CERT_ALB_QUERY_PATH and HONUA_REALAWS_CERT_ALB_EXPECTED_COUNT."
fi

# 5. Authorization is enforced on the deployed service, not just in tests.
admin_status="$(fetch_status "${BASE_URL}/api/v1/admin/services")"
if [[ "${admin_status}" == "401" || "${admin_status}" == "403" ]]; then
    record PASS "an unauthenticated admin request through the ALB was denied (HTTP ${admin_status})."
elif [[ "${admin_status}" == "404" ]]; then
    record FAIL "an unauthenticated admin request returned HTTP 404 — the admin surface is not present behind this ALB."
else
    record FAIL "an unauthenticated admin request through the ALB returned HTTP ${admin_status}; expected 401 or 403."
fi

if [[ -n "${ADMIN_API_KEY}" ]]; then
    authed_status="$(fetch_status -H "X-API-Key: ${ADMIN_API_KEY}" "${BASE_URL}/api/v1/admin/services")"
    if [[ "${authed_status}" == "200" ]]; then
        record PASS "an authenticated admin request through the ALB succeeded (HTTP 200)."
    else
        record FAIL "an authenticated admin request through the ALB returned HTTP ${authed_status}; expected 200."
    fi
fi

if [[ -n "${GITHUB_STEP_SUMMARY:-}" ]]; then
    {
        echo '### Honua serving certification through the cert ALB'
        echo
        printf '%s\n' "${summary_lines[@]}"
        echo
    } >>"${GITHUB_STEP_SUMMARY}"
fi

if (( failures > 0 )); then
    echo "::error::${failures} Honua serving certification check(s) failed against ${BASE_URL}." >&2
    exit 1
fi

echo "Honua serving certification passed against ${BASE_URL}."
