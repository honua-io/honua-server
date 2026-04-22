#!/bin/bash
set -euo pipefail

TF_OUTPUT_JSON=""
INCLUDE_SCALE_TESTS="${INCLUDE_SCALE_TESTS:-false}"

usage() {
    cat <<'EOF'
Usage: ./scripts/run-cloud-post-apply-validation.sh [--terraform-output-json <path>] [--include-scale-tests]

Environment variables:
  HONUA_CLOUD_TEST_BASE_URL                    Required unless derived from Terraform output.
  HONUA_CLOUD_TEST_ADMIN_API_KEY               Optional for public checks, recommended for admin/control-plane checks.
  HONUA_CLOUD_TEST_EXPECTED_ENVIRONMENT        Optional expected environment name.
  HONUA_CLOUD_TEST_EXPECTED_DEPLOYMENT_MODE    Optional expected deployment mode (SingleInstance or MultiNode).
  HONUA_CLOUD_TEST_EXPECT_READY_FOR_COORDINATED_DEPLOY
                                               Optional expected deploy readiness flag.
  HONUA_CLOUD_TEST_PLATFORM                    Optional platform hint (kubernetes, aws-ecs, aws-lambda, azure-functions, azure-container-apps).
  HONUA_CLOUD_TEST_EXPECT_DEPLOY_PLAN_SUPPORT  Optional true/false override for deploy-plan cloud tests.
  HONUA_CLOUD_TEST_EXPECT_MUTATION_SUPPORT     Optional true/false override for staged-import mutation cloud tests.
  HONUA_CLOUD_TEST_DEPLOY_TARGET_ID            Optional deploy target id for a live deploy-plan check.
  HONUA_CLOUD_TEST_DEPLOY_DESIRED_REVISION     Optional desired revision for a live deploy-plan check.
  HONUA_CLOUD_TEST_DEPLOY_CURRENT_REVISION     Optional current revision for a live deploy-plan check.
  HONUA_CLOUD_TEST_EXECUTE_DEPLOY_OPERATION    Optional true/false flag to execute a real deploy operation through the admin API.
  HONUA_CLOUD_TEST_VERIFY_DEPLOY_ROLLBACK      Optional true/false flag to request rollback after the live deploy operation and then restore the desired revision.
  HONUA_CLOUD_TEST_DEPLOY_TIMEOUT_SECONDS      Optional timeout for live deploy-operation polling.
  HONUA_CLOUD_TEST_EXTRA_HEADER_NAME           Optional HTTP header name applied to all post-apply validation requests.
  HONUA_CLOUD_TEST_EXTRA_HEADER_VALUE          Optional HTTP header value applied to all post-apply validation requests.
  HONUA_CLOUD_TEST_IMPORT_TABLE_PREFIX         Optional table prefix to enable the live cloud-staged import mutation test.
  HONUA_CLOUD_TEST_IMPORT_TIMEOUT_SECONDS      Optional timeout for the live import mutation test.
  HONUA_CLOUD_TEST_PUBLISH_DB_HOST             Required for the live import publish/query round-trip when import mutation checks are enabled.
  HONUA_CLOUD_TEST_PUBLISH_DB_PORT             Optional DB port for the live import publish/query round-trip.
  HONUA_CLOUD_TEST_PUBLISH_DB_NAME             Required for the live import publish/query round-trip when import mutation checks are enabled.
  HONUA_CLOUD_TEST_PUBLISH_DB_USERNAME         Required for the live import publish/query round-trip when import mutation checks are enabled.
  HONUA_CLOUD_TEST_PUBLISH_DB_PASSWORD         Required for the live import publish/query round-trip when import mutation checks are enabled.
  HONUA_CLOUD_TEST_PUBLISH_DB_SSL_MODE         Optional DB SSL mode for the live import publish/query round-trip.
  HONUA_CLOUD_TEST_PUBLISH_DB_SSL_REQUIRED     Optional DB SSL-required flag for the live import publish/query round-trip.

Optional scale validation environment variables:
  INCLUDE_SCALE_TESTS=true                     Enables existing multi-node scale tests.
  HONUA_SCALE_TEST_BASE_URL                    Required for scale tests.
  HONUA_SCALE_TEST_ADMIN_API_KEY               Required for replica scale tests.
  HONUA_SCALE_TEST_SERVICE_ID                  Required for replica scale tests.
  HONUA_SCALE_TEST_REDIS                       Optional but required for Redis cache assertions.

Terraform output key candidates:
  base_url, public_base_url, service_url
  admin_api_key
  environment
  deployment_mode
  ready_for_coordinated_deploy
  platform
  control_plane_target_id
  control_plane_current_revision, lambda_alias_function_version
  control_plane_desired_revision, lambda_function_version
  canary_verification_header_name
  canary_verification_header_value
  scale_test_base_url
  scale_test_admin_api_key
  scale_test_service_id
  redis_connection_string, scale_test_redis
EOF
}

while [[ $# -gt 0 ]]; do
    case "$1" in
        --terraform-output-json)
            TF_OUTPUT_JSON="${2:-}"
            shift 2
            ;;
        --include-scale-tests)
            INCLUDE_SCALE_TESTS="true"
            shift
            ;;
        -h|--help)
            usage
            exit 0
            ;;
        *)
            echo "Unknown argument: $1" >&2
            usage
            exit 1
            ;;
    esac
done

require_tool() {
    local name=$1
    if ! command -v "$name" >/dev/null 2>&1; then
        echo "Required tool '$name' is not installed." >&2
        exit 1
    fi
}

normalize_base_url() {
    local base_url="${1%/}"

    if [[ "$base_url" =~ ^https?:// ]]; then
        printf '%s\n' "$base_url"
        return 0
    fi

    printf 'https://%s\n' "$base_url"
}

platform_capability_default() {
    local platform="${1:-}"
    local capability="${2:-}"

    case "${platform}:${capability}" in
        aws-lambda:deploy-plan|aws-lambda:mutation)
            printf 'false\n'
            ;;
        azure-functions:deploy-plan|azure-functions:mutation)
            printf 'false\n'
            ;;
        azure-container-apps:deploy-plan|azure-container-apps:mutation)
            printf 'false\n'
            ;;
        kubernetes:deploy-plan)
            printf 'false\n'
            ;;
    esac
}

apply_platform_capability_defaults() {
    local platform="${HONUA_CLOUD_TEST_PLATFORM:-}"
    local deploy_plan_support=""
    local mutation_support=""

    if [[ -z "$platform" ]]; then
        return 0
    fi

    if [[ -z "${HONUA_CLOUD_TEST_EXPECT_DEPLOY_PLAN_SUPPORT:-}" ]]; then
        deploy_plan_support="$(platform_capability_default "$platform" "deploy-plan")"
        if [[ -n "$deploy_plan_support" ]]; then
            export HONUA_CLOUD_TEST_EXPECT_DEPLOY_PLAN_SUPPORT="$deploy_plan_support"
        fi
    fi

    if [[ -z "${HONUA_CLOUD_TEST_EXPECT_MUTATION_SUPPORT:-}" ]]; then
        mutation_support="$(platform_capability_default "$platform" "mutation")"
        if [[ -n "$mutation_support" ]]; then
            export HONUA_CLOUD_TEST_EXPECT_MUTATION_SUPPORT="$mutation_support"
        fi
    fi
}
resolve_tf_output() {
    local key=$1
    jq -r --arg key "$key" '
        if has($key) and .[$key].value != null then
            .[$key].value
        else
            empty
        end
    ' "$TF_OUTPUT_JSON"
}

set_from_tf_candidates() {
    local env_name=$1
    shift

    if [[ -n "${!env_name:-}" || -z "$TF_OUTPUT_JSON" ]]; then
        return 0
    fi

    local value=""
    for key in "$@"; do
        value=$(resolve_tf_output "$key")
        if [[ -n "$value" ]]; then
            export "$env_name=$value"
            return 0
        fi
    done
}

if [[ -n "$TF_OUTPUT_JSON" ]]; then
    require_tool jq

    if [[ ! -f "$TF_OUTPUT_JSON" ]]; then
        echo "Terraform output JSON file not found: $TF_OUTPUT_JSON" >&2
        exit 1
    fi

    set_from_tf_candidates HONUA_CLOUD_TEST_BASE_URL base_url public_base_url service_url
    set_from_tf_candidates HONUA_CLOUD_TEST_ADMIN_API_KEY admin_api_key
    set_from_tf_candidates HONUA_CLOUD_TEST_EXPECTED_ENVIRONMENT environment
    set_from_tf_candidates HONUA_CLOUD_TEST_EXPECTED_DEPLOYMENT_MODE deployment_mode
    set_from_tf_candidates HONUA_CLOUD_TEST_EXPECT_READY_FOR_COORDINATED_DEPLOY ready_for_coordinated_deploy
    set_from_tf_candidates HONUA_CLOUD_TEST_PLATFORM platform
    set_from_tf_candidates HONUA_CLOUD_TEST_DEPLOY_TARGET_ID control_plane_target_id
    set_from_tf_candidates HONUA_CLOUD_TEST_DEPLOY_DESIRED_REVISION control_plane_desired_revision lambda_function_version control_plane_current_revision
    set_from_tf_candidates HONUA_CLOUD_TEST_DEPLOY_CURRENT_REVISION lambda_alias_function_version control_plane_current_revision lambda_function_version
    set_from_tf_candidates HONUA_CLOUD_TEST_EXTRA_HEADER_NAME canary_verification_header_name
    set_from_tf_candidates HONUA_CLOUD_TEST_EXTRA_HEADER_VALUE canary_verification_header_value
    set_from_tf_candidates HONUA_SCALE_TEST_BASE_URL scale_test_base_url
    set_from_tf_candidates HONUA_SCALE_TEST_ADMIN_API_KEY scale_test_admin_api_key
    set_from_tf_candidates HONUA_SCALE_TEST_SERVICE_ID scale_test_service_id
    set_from_tf_candidates HONUA_SCALE_TEST_REDIS redis_connection_string scale_test_redis redis
fi

if [[ -z "${HONUA_CLOUD_TEST_BASE_URL:-}" ]]; then
    echo "HONUA_CLOUD_TEST_BASE_URL is required." >&2
    usage
    exit 1
fi

HONUA_CLOUD_TEST_BASE_URL="$(normalize_base_url "$HONUA_CLOUD_TEST_BASE_URL")"
export HONUA_CLOUD_TEST_BASE_URL

case "${HONUA_CLOUD_TEST_PLATFORM:-}" in
    azure-functions)
        export HONUA_CLOUD_TEST_EXPECT_DEPLOY_PLAN_SUPPORT="${HONUA_CLOUD_TEST_EXPECT_DEPLOY_PLAN_SUPPORT:-false}"
        export HONUA_CLOUD_TEST_EXPECT_MUTATION_SUPPORT="${HONUA_CLOUD_TEST_EXPECT_MUTATION_SUPPORT:-false}"
        ;;
esac

require_tool dotnet
require_tool curl

echo "Running post-apply validation for ${HONUA_CLOUD_TEST_BASE_URL}"

export BASE_URL="$HONUA_CLOUD_TEST_BASE_URL"
export ENVIRONMENT="${HONUA_CLOUD_TEST_EXPECTED_ENVIRONMENT:-cloud}"
if [[ -n "${HONUA_CLOUD_TEST_ADMIN_API_KEY:-}" ]]; then
    export ADMIN_API_KEY="$HONUA_CLOUD_TEST_ADMIN_API_KEY"
fi
if [[ -n "${HONUA_CLOUD_TEST_EXTRA_HEADER_NAME:-}" && -n "${HONUA_CLOUD_TEST_EXTRA_HEADER_VALUE:-}" ]]; then
    export EXTRA_CURL_HEADER="${HONUA_CLOUD_TEST_EXTRA_HEADER_NAME}: ${HONUA_CLOUD_TEST_EXTRA_HEADER_VALUE}"
fi

chmod +x scripts/post-deployment-verification.sh
chmod +x scripts/run-cloud-post-apply-validation.sh

scripts/post-deployment-verification.sh

dotnet test tests/dotnet/Honua.Server.Tests/Honua.Server.Tests.csproj \
    -p:RunAnalyzers=false \
    --filter "Category=Cloud"

if [[ "$INCLUDE_SCALE_TESTS" == "true" ]]; then
    if [[ -z "${HONUA_SCALE_TEST_BASE_URL:-}" ]]; then
        echo "INCLUDE_SCALE_TESTS=true but HONUA_SCALE_TEST_BASE_URL is not set." >&2
        exit 1
    fi

    echo "Running scale validation against ${HONUA_SCALE_TEST_BASE_URL}"
    dotnet test tests/dotnet/Honua.Server.Tests/Honua.Server.Tests.csproj \
        -p:RunAnalyzers=false \
        --filter "Category=Scale"
fi

echo "Cloud post-apply validation completed successfully."
