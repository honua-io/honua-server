#!/usr/bin/env bash
set -euo pipefail

# Standing limits (release#210, verbatim): plan summaries to the evidence thread BEFORE apply, STOP on any destroy beyond the lane's own teardown-of-what-it-created, no IAM trust widening, fingerprints only.

# Invalidate any previous receipt before validating the remaining inputs.
if [[ -n "${HONUA_LAMBDA_PREVIEW_RECEIPT:-}" ]]; then
mkdir -p "$(dirname "$HONUA_LAMBDA_PREVIEW_RECEIPT")"
printf '%s\n' '{"schema":"honua.lambda-preview-certification/v1","result":"fail","serving":{"result":"noProof"}}' > "$HONUA_LAMBDA_PREVIEW_RECEIPT"
fi

required=(
  HONUA_LAMBDA_SOURCE_IMAGE
  HONUA_LAMBDA_ARCHITECTURE
  REALAWS_CERT_LAMBDA_FUNCTION
  REALAWS_CERT_LAMBDA_ALIAS
  HONUA_LAMBDA_WRITE_BASE_URL
  HONUA_DEMO_BASE_URL
  HONUA_LAMBDA_CERT_ADMIN_KEY
  HONUA_LAMBDA_CERT_DENIED_KEY
  AWS_REGION
  HONUA_LAMBDA_SOURCE_DIGEST
  HONUA_LAMBDA_SERVER_REVISION
  HONUA_LAMBDA_PREVIEW_REPOSITORY
  HONUA_LAMBDA_PREVIEW_EXECUTION_ROLE_ARN
  HONUA_LAMBDA_PREVIEW_RECEIPT
  GITHUB_RUN_ID
  GITHUB_RUN_ATTEMPT
)
for name in "${required[@]}"; do
  if [[ -z "${!name:-}" ]]; then
    echo "required input is empty: ${name}" >&2
    exit 2
  fi
done

if [[ ! "$HONUA_LAMBDA_SOURCE_DIGEST" =~ ^sha256:[0-9a-f]{64}$ ]]; then
  echo "source digest must be sha256:<64 lowercase hex>" >&2
  exit 2
fi
if [[ ! "$HONUA_LAMBDA_SERVER_REVISION" =~ ^[0-9a-f]{40}$ ]]; then
  echo "server revision must be an exact 40-character lowercase commit SHA" >&2
  exit 2
fi
if [[ "$HONUA_LAMBDA_SOURCE_IMAGE" != ghcr.io/honua-io/honua-server:* ]]; then
  echo "source image must be the Honua Lambda GHCR path" >&2
  exit 2
fi
if [[ "$HONUA_LAMBDA_SOURCE_IMAGE" == *@* ]]; then
  echo "source image input must be a tag; the independently supplied digest binds it" >&2
  exit 2
fi
if [[ ! "$HONUA_LAMBDA_PREVIEW_REPOSITORY" =~ ^[0-9]{12}\.dkr\.ecr\.[a-z0-9-]+\.amazonaws\.com/honua-cert-cert-lambda-preview$ ]]; then
  echo "repository is outside the dedicated Lambda Preview certification namespace" >&2
  exit 2
fi

case "$HONUA_LAMBDA_ARCHITECTURE" in
  arm64) docker_architecture=arm64 ;;
  x86_64) docker_architecture=amd64 ;;
  *) echo "candidate architecture must be arm64 or x86_64" >&2; exit 2 ;;
esac
if [[ ! "$GITHUB_RUN_ID" =~ ^[0-9]+$ || ! "$GITHUB_RUN_ATTEMPT" =~ ^[0-9]+$ ]]; then
  echo "run id and attempt must be numeric" >&2
  exit 2
fi
script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
umask 077
scratch="$(mktemp -d)"

run_token="${GITHUB_RUN_ID}-${GITHUB_RUN_ATTEMPT}"
function_name="honua-certrun-lambda-${run_token}"
log_group="/aws/lambda/${function_name}"
source_ref="${HONUA_LAMBDA_SOURCE_IMAGE}@${HONUA_LAMBDA_SOURCE_DIGEST}"
target_tag="candidate-${HONUA_LAMBDA_SERVER_REVISION:0:12}-${HONUA_LAMBDA_SOURCE_DIGEST:7:12}"
target_ref="${HONUA_LAMBDA_PREVIEW_REPOSITORY}:${target_tag}"
repository_name="${HONUA_LAMBDA_PREVIEW_REPOSITORY#*/}"
registry="${HONUA_LAMBDA_PREVIEW_REPOSITORY%%/*}"
function_created=false
log_group_created=false
function_arn=""

fingerprint() {
  printf '%s' "$1" | sha256sum | awk '{print "sha256:" $1}'
}

cleanup() {
  local status=$?
  set +e
  if $function_created; then
    if [[ "$function_name" != honua-certrun-lambda-* ]]; then
      echo "STOP: refusing function destroy outside the lane run namespace: ${function_name}" >&2
      exit 90
    fi
    if [[ -z "$function_arn" ]]; then
      function_arn="$(aws lambda get-function --function-name "$function_name" --query 'Configuration.FunctionArn' --output text 2>/dev/null)"
    fi
    tags="$(aws lambda list-tags --resource "$function_arn" --query Tags --output json 2>/dev/null)"
    if [[ "$(jq -r '."honua-cert-run" // empty' <<<"$tags")" != "$run_token" ]]; then
      echo "STOP: refusing to delete a function without this run's ownership tag" >&2
      exit 91
    fi
    aws lambda delete-function --function-name "$function_name" || status=11
    aws lambda wait function-not-exists --function-name "$function_name" || status=11
  fi
  if $log_group_created; then
    if [[ "$log_group" != /aws/lambda/honua-certrun-lambda-* ]]; then
      echo "STOP: refusing log-group destroy outside the lane run namespace: ${log_group}" >&2
      exit 92
    fi
    aws logs delete-log-group --log-group-name "$log_group" || status=12
  fi
  rm -rf "$scratch"
  if (( status != 0 )); then
    exit "$status"
  fi
}
trap cleanup EXIT

python3 "$script_dir/lambda-certification.py" prepare "$scratch"

source_manifest="$(docker buildx imagetools inspect --raw "$source_ref")"
source_config="$(jq -er '.config.digest' <<<"$source_manifest")"
docker pull --platform "linux/$docker_architecture" "$source_ref"
source_revision="$(docker image inspect "$source_ref" --format '{{ index .Config.Labels "org.opencontainers.image.revision" }}')"
source_architecture="$(docker image inspect "$source_ref" --format '{{ .Architecture }}')"
if [[ "$source_revision" != "$HONUA_LAMBDA_SERVER_REVISION" || "$source_architecture" != "$docker_architecture" ]]; then
  echo "source image config does not match the declared server revision and candidate architecture" >&2
  exit 3
fi
docker run --rm --entrypoint /bin/sh "$source_ref" -c \
  'test -x /opt/extensions/lambda-adapter && test -x /var/task/Honua.Server'

existing="$(aws ecr describe-images --repository-name "$repository_name" \
  --image-ids imageTag="$target_tag" --query 'imageDetails[0].imageDigest' --output text 2>/dev/null || true)"
if [[ -z "$existing" || "$existing" == "None" ]]; then
  aws ecr get-login-password | docker login --username AWS --password-stdin "$registry"
  docker tag "$source_ref" "$target_ref"
  docker push "$target_ref"
fi

ecr_digest="$(aws ecr describe-images --repository-name "$repository_name" \
  --image-ids imageTag="$target_tag" --query 'imageDetails[0].imageDigest' --output text)"
if [[ ! "$ecr_digest" =~ ^sha256:[0-9a-f]{64}$ ]]; then
  echo "ECR did not return an exact image digest" >&2
  exit 3
fi
ecr_manifest="$(aws ecr batch-get-image --repository-name "$repository_name" \
  --image-ids imageDigest="$ecr_digest" \
  --accepted-media-types application/vnd.oci.image.manifest.v1+json application/vnd.docker.distribution.manifest.v2+json \
  --query 'images[0].imageManifest' --output text)"
ecr_config="$(jq -er '.config.digest' <<<"$ecr_manifest")"
if [[ "$ecr_config" != "$source_config" || "$(jq -c '[.layers[].digest]' <<<"$source_manifest")" != "$(jq -c '[.layers[].digest]' <<<"$ecr_manifest")" ]]; then
  echo "ECR mirror config digest does not match the exact source artifact" >&2
  exit 4
fi

aws ecr get-login-password | docker login --username AWS --password-stdin "$registry"
docker pull --platform "linux/$docker_architecture" "${HONUA_LAMBDA_PREVIEW_REPOSITORY}@${ecr_digest}"
if [[ "$(docker image inspect "${HONUA_LAMBDA_PREVIEW_REPOSITORY}@${ecr_digest}" --format '{{ .Architecture }}')" != "$docker_architecture" ]]; then
  echo "ECR image platform does not match candidate architecture" >&2
  exit 4
fi

if aws lambda get-function --function-name "$function_name" >/dev/null 2>&1; then
  echo "STOP: run-namespaced function already exists; ownership is ambiguous" >&2
  exit 93
fi
if [[ "$(aws logs describe-log-groups --log-group-name-prefix "$log_group" \
  --query "length(logGroups[?logGroupName=='${log_group}'])" --output text)" != "0" ]]; then
  echo "STOP: run-namespaced log group already exists; ownership is ambiguous" >&2
  exit 94
fi

aws logs create-log-group --log-group-name "$log_group"
log_group_created=true
aws logs put-retention-policy --log-group-name "$log_group" --retention-in-days 1


create_json="$(aws lambda create-function \
  --function-name "$function_name" \
  --package-type Image \
  --code "ImageUri=${HONUA_LAMBDA_PREVIEW_REPOSITORY}@${ecr_digest}" \
  --role "$HONUA_LAMBDA_PREVIEW_EXECUTION_ROLE_ARN" \
  --architectures "$HONUA_LAMBDA_ARCHITECTURE" \
  --environment "file://$scratch/environment.json" \
  --vpc-config "file://$scratch/vpc.json" \
  --memory-size 1024 \
  --timeout 60 \
  --tags "honua-cert-run=${run_token}" "honua-purpose=lambda-preview-certification" 2>"$scratch/create-error.log")" || {
  echo "Lambda create failed (configuration diagnostics suppressed)" >&2
  exit 5
}
function_created=true
function_arn="$(jq -er '.FunctionArn' <<<"$create_json")"
aws lambda wait function-active-v2 --function-name "$function_name"

resolved_image="$(aws lambda get-function --function-name "$function_name" --query 'Code.ResolvedImageUri' --output text)"
if [[ "$resolved_image" != "${HONUA_LAMBDA_PREVIEW_REPOSITORY}@${ecr_digest}" ]]; then
  echo "Lambda did not resolve the expected ECR artifact digest" >&2
  exit 5
fi

payload='{"version":"2.0","routeKey":"GET /healthz/live","rawPath":"/healthz/live","rawQueryString":"","headers":{"accept":"application/json","host":"lambda-cert.invalid"},"requestContext":{"http":{"method":"GET","path":"/healthz/live","protocol":"HTTP/1.1","sourceIp":"127.0.0.1","userAgent":"honua-lambda-preview-cert"}},"isBase64Encoded":false}'
invoke_meta="$(aws lambda invoke --function-name "$function_name" --cli-binary-format raw-in-base64-out \
  --log-type Tail --payload "$payload" "$scratch/response.json")"
if [[ "$(jq -r '.StatusCode' <<<"$invoke_meta")" != "200" || "$(jq -r '.FunctionError // empty' <<<"$invoke_meta")" != "" ]]; then
  echo "Lambda invocation failed" >&2
  exit 6
fi
if [[ "$(jq -r '.statusCode' "$scratch/response.json")" != "200" ]]; then
  echo "representative HTTP operation did not return status 200" >&2
  exit 7
fi
response_body="$(jq -r '.body' "$scratch/response.json")"
if [[ "$response_body" != *Healthy* && "$response_body" != *healthy* ]]; then
  echo "representative HTTP operation did not return a healthy response" >&2
  exit 8
fi

tail_log="$(jq -er '.LogResult' <<<"$invoke_meta" | base64 -d)"
request_id="$(sed -nE 's/^REPORT RequestId: ([^[:space:]]+).*/\1/p' <<<"$tail_log" | tail -n 1)"
if [[ -z "$request_id" ]]; then
  echo "Lambda invoke tail did not contain a REPORT request id" >&2
  exit 9
fi

cold_start_ms="$(sed -nE 's/^REPORT .*Init Duration: ([0-9]+([.][0-9]+)?) ms.*/\1/p' <<<"$tail_log")"
if [[ -z "$cold_start_ms" ]] || ! awk -v value="$cold_start_ms" 'BEGIN { exit !(value > 0) }'; then
  echo "first invoke REPORT has no positive cold-start Init Duration" >&2
  exit 14
fi

cloudwatch_verified=false
for _ in {1..12}; do
  event_count="$(aws logs filter-log-events --log-group-name "$log_group" \
    --filter-pattern "\"${request_id}\"" --query 'length(events)' --output text)"
  if [[ "$event_count" =~ ^[1-9][0-9]*$ ]]; then
    cloudwatch_verified=true
    break
  fi
  sleep 5
done
if ! $cloudwatch_verified; then
  echo "matching invocation evidence did not arrive in CloudWatch Logs" >&2
  exit 10
fi

python3 "$script_dir/lambda-certification.py" certify "$scratch" "$function_name" "${HONUA_LAMBDA_PREVIEW_REPOSITORY}@${ecr_digest}"
serving_proof="$(cat "$scratch/serving.json")"

# Teardown is explicit here so the receipt can assert and record its result.
cleanup
trap - EXIT
function_created=false
log_group_created=false

if aws lambda get-function --function-name "$function_name" >/dev/null 2>&1; then
  echo "function still exists after teardown" >&2
  exit 11
fi
if [[ "$(aws logs describe-log-groups --log-group-name-prefix "$log_group" \
  --query "length(logGroups[?logGroupName=='${log_group}'])" --output text)" != "0" ]]; then
  echo "log group still exists after teardown" >&2
  exit 12
fi

mkdir -p "$(dirname "$HONUA_LAMBDA_PREVIEW_RECEIPT")"
jq -n \
  --arg architecture "$HONUA_LAMBDA_ARCHITECTURE" \
  --argjson cold_start_ms "$cold_start_ms" \
  --argjson serving "$serving_proof" \
  --arg schema "honua.lambda-preview-certification/v1" \
  --arg server_revision "$HONUA_LAMBDA_SERVER_REVISION" \
  --arg source_digest "$HONUA_LAMBDA_SOURCE_DIGEST" \
  --arg source_config_digest "$source_config" \
  --arg ecr_digest "$ecr_digest" \
  --arg region_fingerprint "$(fingerprint "${AWS_REGION:-${AWS_DEFAULT_REGION:-}}")" \
  --arg account_fingerprint "$(fingerprint "$(aws sts get-caller-identity --query Account --output text)")" \
  --arg repository_fingerprint "$(fingerprint "$HONUA_LAMBDA_PREVIEW_REPOSITORY")" \
  --arg function_fingerprint "$(fingerprint "$function_name")" \
  --arg request_fingerprint "$(fingerprint "$request_id")" \
  --arg run_url "${GITHUB_SERVER_URL:-https://github.com}/${GITHUB_REPOSITORY:-honua-io/honua-server}/actions/runs/${GITHUB_RUN_ID}" \
  '{schema:$schema,result:"pass",serverRevision:$server_revision,artifact:{sourceDigest:$source_digest,sourceConfigDigest:$source_config_digest,ecrDigest:$ecr_digest,repositoryFingerprint:$repository_fingerprint,runtimeAdapterVerified:true},deployment:{regionFingerprint:$region_fingerprint,accountFingerprint:$account_fingerprint,functionFingerprint:$function_fingerprint,architecture:$architecture},serving:$serving,verification:{coldStartInitDurationMs:$cold_start_ms,operation:"GET /healthz/live",httpStatus:200,responseVerified:true,cloudWatchLogsVerified:true,requestFingerprint:$request_fingerprint},teardown:{functionDeleted:true,logGroupDeleted:true},runUrl:$run_url}' \
  > "$HONUA_LAMBDA_PREVIEW_RECEIPT"

jq -e '.result == "pass" and (.artifact.ecrDigest | test("^sha256:[0-9a-f]{64}$")) and .verification.responseVerified and .verification.cloudWatchLogsVerified and .teardown.functionDeleted and .teardown.logGroupDeleted and .serving.result == "pass" and .verification.coldStartInitDurationMs > 0' \
  "$HONUA_LAMBDA_PREVIEW_RECEIPT" >/dev/null
echo "Lambda Preview certification passed; ECR digest: ${ecr_digest}"
