#!/bin/bash
set -euo pipefail

usage() {
    cat <<'EOF'
Usage: ./scripts/render-control-plane-config-from-terraform.sh --terraform-output-json <path> [options]

Required:
  --terraform-output-json <path>   Path to `terraform output -json` payload

Optional:
  --target-id <id>                 Control-plane target id (default: derived from environment/service)
  --telemetry-connection-id <id>   Telemetry connection id (default: terraform-prometheus)
  --output <path>                  Write JSON to a file instead of stdout

This script renders a JSON fragment shaped for appsettings-style configuration:

{
  "ControlPlane": {
    "TelemetryConnections": [ ... ],
    "DeployTargets": [ ... ]
  }
}

It consumes Terraform outputs such as:
  environment
  control_plane_target_id
  prometheus_url
  control_plane_target_kind
  control_plane_backend_name
  control_plane_target_id
  control_plane_target_name
  control_plane_target_resource_id
  control_plane_current_revision
  control_plane_telemetry_policy
  control_plane_telemetry_prometheus_job
  control_plane_telemetry_prometheus_canary_job
  aws_region
  resource_group_name
  control_plane_target_resource_group
  lambda_alias_name
  lambda_alias_arn
  lambda_alias_invoke_arn
  lambda_alias_function_version
  lambda_current_version
  control_plane_current_image
  control_plane_desired_image
  control_plane_namespace
  ecs_service_name
  service_name
  lambda_function_name
  function_app_name
  function_app_slot_name
  container_app_name
EOF
}

TF_OUTPUT_JSON=""
TARGET_ID=""
TELEMETRY_CONNECTION_ID="terraform-prometheus"
OUTPUT_PATH=""

while [[ $# -gt 0 ]]; do
    case "$1" in
        --terraform-output-json)
            TF_OUTPUT_JSON="${2:-}"
            shift 2
            ;;
        --target-id)
            TARGET_ID="${2:-}"
            shift 2
            ;;
        --telemetry-connection-id)
            TELEMETRY_CONNECTION_ID="${2:-}"
            shift 2
            ;;
        --output)
            OUTPUT_PATH="${2:-}"
            shift 2
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

if [[ -z "$TF_OUTPUT_JSON" ]]; then
    echo "--terraform-output-json is required." >&2
    usage
    exit 1
fi

if [[ ! -f "$TF_OUTPUT_JSON" ]]; then
    echo "Terraform output JSON file not found: $TF_OUTPUT_JSON" >&2
    exit 1
fi

if ! command -v jq >/dev/null 2>&1; then
    echo "jq is required." >&2
    exit 1
fi

resolve_output() {
    local key=$1
    jq -r --arg key "$key" '
        if has($key) and .[$key].value != null then
            .[$key].value
        else
            empty
        end
    ' "$TF_OUTPUT_JSON"
}

first_non_empty() {
    local value=""
    for key in "$@"; do
        value="$(resolve_output "$key")"
        if [[ -n "$value" && "$value" != "null" ]]; then
            printf '%s' "$value"
            return 0
        fi
    done

    return 1
}

ENVIRONMENT="$(first_non_empty environment || true)"
TARGET_NAME="$(first_non_empty control_plane_target_name ecs_service_name service_name lambda_function_name function_app_name container_app_name honua_metrics_target || true)"
TARGET_KIND="$(first_non_empty control_plane_target_kind || true)"
BACKEND_NAME="$(first_non_empty control_plane_backend_name || true)"
TARGET_ID_FROM_OUTPUT="$(first_non_empty control_plane_target_id || true)"
TARGET_RESOURCE_ID="$(first_non_empty control_plane_target_resource_id lambda_function_arn function_app_id container_app_id container_app_environment_id || true)"
TARGET_NAMESPACE="$(first_non_empty control_plane_namespace namespace || true)"
PROMETHEUS_URL="$(first_non_empty prometheus_url || true)"
TELEMETRY_POLICY="$(first_non_empty control_plane_telemetry_policy || true)"
PROMETHEUS_JOB="$(first_non_empty control_plane_telemetry_prometheus_job honua_prometheus_job_name || true)"
PROMETHEUS_SELECTOR="$(first_non_empty honua_prometheus_selector || true)"
PROMETHEUS_CANARY_JOB="$(first_non_empty control_plane_telemetry_prometheus_canary_job || true)"
AWS_REGION="$(first_non_empty aws_region control_plane_region || true)"
AZURE_RESOURCE_GROUP="$(first_non_empty control_plane_target_resource_group resource_group_name || true)"
LAMBDA_ALIAS_NAME="$(first_non_empty lambda_alias_name control_plane_alias_name || true)"
LAMBDA_ALIAS_ARN="$(first_non_empty lambda_alias_arn || true)"
LAMBDA_ALIAS_INVOKE_ARN="$(first_non_empty lambda_alias_invoke_arn || true)"
CURRENT_REVISION="$(first_non_empty control_plane_current_revision lambda_alias_function_version lambda_current_version || true)"
LAMBDA_CURRENT_VERSION="$(first_non_empty lambda_alias_function_version lambda_current_version control_plane_current_revision || true)"
CURRENT_IMAGE="$(first_non_empty control_plane_current_image || true)"
DESIRED_IMAGE="$(first_non_empty control_plane_desired_image || true)"
FUNCTION_APP_SLOT_NAME="$(first_non_empty control_plane_slot_name function_app_slot_name || true)"

if [[ -z "$ENVIRONMENT" ]]; then
    ENVIRONMENT="default"
fi

if [[ -z "$TARGET_NAME" ]]; then
    TARGET_NAME="honua"
fi

if [[ -z "$TARGET_KIND" ]]; then
    echo "Terraform outputs did not include control_plane_target_kind." >&2
    exit 1
fi

if [[ -z "$BACKEND_NAME" ]]; then
    echo "Terraform outputs did not include control_plane_backend_name." >&2
    exit 1
fi

if [[ -z "$TARGET_ID" ]]; then
    TARGET_ID="$(first_non_empty control_plane_target_id || true)"
fi

if [[ -z "$TARGET_ID" ]]; then
    if [[ -n "$TARGET_ID_FROM_OUTPUT" ]]; then
        TARGET_ID="$TARGET_ID_FROM_OUTPUT"
    else
        normalized_name="$(printf '%s' "$TARGET_NAME" | tr '[:upper:]' '[:lower:]' | sed -E 's/[^a-z0-9]+/-/g; s/^-+//; s/-+$//')"
        normalized_env="$(printf '%s' "$ENVIRONMENT" | tr '[:upper:]' '[:lower:]' | sed -E 's/[^a-z0-9]+/-/g; s/^-+//; s/-+$//')"
        TARGET_ID="${normalized_env:-default}-${normalized_name:-honua}"
    fi
fi

CONFIG_JSON="$(jq -n \
    --arg targetId "$TARGET_ID" \
    --arg targetKind "$TARGET_KIND" \
    --arg backend "$BACKEND_NAME" \
    --arg environment "$ENVIRONMENT" \
    --arg targetName "$TARGET_NAME" \
    --arg telemetryConnectionId "$TELEMETRY_CONNECTION_ID" \
    --arg prometheusUrl "$PROMETHEUS_URL" \
    --arg telemetryPolicy "$TELEMETRY_POLICY" \
    --arg prometheusJob "$PROMETHEUS_JOB" \
    --arg prometheusSelector "$PROMETHEUS_SELECTOR" \
    --arg prometheusCanaryJob "$PROMETHEUS_CANARY_JOB" \
    --arg targetResourceId "$TARGET_RESOURCE_ID" \
    --arg targetNamespace "$TARGET_NAMESPACE" \
    --arg awsRegion "$AWS_REGION" \
    --arg azureResourceGroup "$AZURE_RESOURCE_GROUP" \
    --arg functionAppSlotName "$FUNCTION_APP_SLOT_NAME" \
    --arg currentImage "$CURRENT_IMAGE" \
    --arg desiredImage "$DESIRED_IMAGE" \
    --arg lambdaAliasName "$LAMBDA_ALIAS_NAME" \
    --arg lambdaAliasArn "$LAMBDA_ALIAS_ARN" \
    --arg lambdaAliasInvokeArn "$LAMBDA_ALIAS_INVOKE_ARN" \
    --arg currentRevision "$CURRENT_REVISION" \
    --arg lambdaCurrentVersion "$LAMBDA_CURRENT_VERSION" '
    {
      ControlPlane: {
        TelemetryConnections: (
          if $prometheusUrl == "" then
            []
          else
            [
              {
                ConnectionId: $telemetryConnectionId,
                Provider: "prometheus",
                BaseUrl: $prometheusUrl
              }
            ]
          end
        ),
        DeployTargets: [
          {
            TargetId: $targetId,
            TargetKind: $targetKind,
            Backend: $backend,
            Environment: $environment,
            TargetName: $targetName,
            Parameters: (
              {}
              + (if $targetResourceId != "" then { "target.resource_id": $targetResourceId } else {} end)
              + (if $targetNamespace != "" then { "target.namespace": $targetNamespace } else {} end)
              + (if $awsRegion != "" then { "aws.region": $awsRegion } else {} end)
              + (if $azureResourceGroup != "" then { "azure.resource_group": $azureResourceGroup } else {} end)
              + (if $targetKind == "AzureFunctions" and $targetName != "" then { "functions.app_name": $targetName, "azure.functions.app_name": $targetName } else {} end)
              + (if $targetKind == "AzureFunctions" and $functionAppSlotName != "" then { "functions.slot_name": $functionAppSlotName, "azure.functions.slot_name": $functionAppSlotName } else {} end)
              + (if $targetKind == "AzureFunctions" and $currentImage != "" then { "functions.current_image": $currentImage, "azure.functions.current_image": $currentImage } else {} end)
              + (if $targetKind == "AzureFunctions" and $desiredImage != "" then { "functions.desired_image": $desiredImage, "azure.functions.desired_image": $desiredImage } else {} end)
              + (if $targetKind == "AzureContainerApps" and $targetName != "" then { "container_apps.app_name": $targetName, "azure.container_apps.app_name": $targetName } else {} end)
              + (if $lambdaAliasName != "" then { "lambda.alias_name": $lambdaAliasName, "aws.lambda.alias_name": $lambdaAliasName } else {} end)
              + (if $lambdaAliasArn != "" then { "lambda.alias_arn": $lambdaAliasArn } else {} end)
              + (if $lambdaAliasInvokeArn != "" then { "lambda.alias_invoke_arn": $lambdaAliasInvokeArn } else {} end)
              + (if $currentRevision != "" then { "deployment.current_revision": $currentRevision } else {} end)
              + (if $targetKind == "AwsLambda" and $lambdaCurrentVersion != "" then { "lambda.current_version": $lambdaCurrentVersion, "aws.lambda.alias_current_version": $lambdaCurrentVersion } else {} end)
              + (if $prometheusUrl != "" then { "telemetry.connection": $telemetryConnectionId } else {} end)
              + (if $telemetryPolicy != "" then { "telemetry.policy": $telemetryPolicy } else {} end)
              + (if $prometheusJob != "" then { "telemetry.prometheus.job": $prometheusJob } else {} end)
              + (if $prometheusSelector != "" then { "telemetry.prometheus.selector": $prometheusSelector } else {} end)
              + (if $prometheusCanaryJob != "" then { "telemetry.prometheus.canary_job": $prometheusCanaryJob } else {} end)
            )
          }
        ]
      }
    }')"

if [[ -n "$OUTPUT_PATH" ]]; then
    printf '%s\n' "$CONFIG_JSON" > "$OUTPUT_PATH"
else
    printf '%s\n' "$CONFIG_JSON"
fi
