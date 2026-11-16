#!/usr/bin/env bash
# Read-only, bounded heavy-pool admission. Never wakes or resizes the host.
set -uo pipefail
export AWS_MAX_ATTEMPTS=1 AWS_EC2_METADATA_DISABLED=true

emit() {
  echo "runner=$1" >> "${GITHUB_OUTPUT}"
  echo "Advisory compute: $1 ($2)"
  exit 0
}

readonly RESERVED_HEAVY_RUNNERS=2

hosted="$(jq -cn --arg label "${HOSTED_RUNNER:-ubuntu-latest}" '$label')"
label="${HONUA_HEAVY_RUNNER:-}"
jq -e '
  def labels: (type == "string" and length > 0) or
    (type == "array" and length > 0 and all(.[]; type == "string" and length > 0));
  (type == "array" and labels) or
  (type == "object" and (.group | type == "string" and length > 0) and
    ((has("labels") | not) or (.labels | labels)))'  <<< "$label" >/dev/null 2>&1 || emit "$hosted" 'heavy pool not configured'
[[ -n "${AWS_ACCESS_KEY_ID:-}" ]] || emit "$hosted" 'no health credential'
instance="$(aws --cli-connect-timeout 5 --cli-read-timeout 5 autoscaling describe-auto-scaling-groups \
  --auto-scaling-group-names honua-lane-build-host \
  --query "AutoScalingGroups[0].Instances[?LifecycleState=='InService'] | [0].InstanceId" --output text)" || emit "$hosted" 'host unreadable'
heartbeat="$(aws --cli-connect-timeout 5 --cli-read-timeout 5 ssm get-parameter \
  --name /honua-lane-build-host/gate-runner/heartbeat --query Parameter.Value --output text)" || emit "$hosted" 'heartbeat unreadable'
proof="$(bash "$(dirname "${BASH_SOURCE[0]}")/gate-runner-heartbeat.sh" "$instance" <<< "$heartbeat")" || emit "$hosted" 'invalid heartbeat'
jq -e '.heavy_ready == true' <<< "$proof" >/dev/null || emit "$hosted" 'heavy pool busy or stale'
# Required PR Gate jobs come first: advisory work takes a heavy runner only
# while this many others stay idle for them. The proof above validated the count.
jq -e --argjson reserved "$RESERVED_HEAVY_RUNNERS" '.heavy.idle > $reserved' <<< "$heartbeat" >/dev/null ||
  emit "$hosted" 'idle heavy runners are reserved for required jobs'
emit "$(jq -c . <<< "$label")" 'fresh idle heavy runner'
