#!/usr/bin/env bash
# Chooses where the heavy PR Gate jobs run: the build-host runner label when the
# host reports enough idle runners, otherwise ubuntu-latest. It wakes a stopped
# host for later runs but never waits longer than GATE_RUNNER_WAIT_SECONDS, and
# every failure selects the hosted runner, so the gate cannot hang on the host.
#
# Inputs: HONUA_PR_GATE_RUNNER (label; empty disables), AWS credentials for the
# wake role, GITHUB_OUTPUT. Contract: honua-iac bootstrap/aws-build-host README.

set -uo pipefail

readonly HOSTED_RUNNER=ubuntu-latest
readonly BUILD_HOST_GROUP=honua-lane-build-host
readonly HEARTBEAT_PARAMETER=/honua-lane-build-host/gate-runner/heartbeat
readonly WAIT_SECONDS="${GATE_RUNNER_WAIT_SECONDS:-180}"
readonly POLL_SECONDS="${GATE_RUNNER_POLL_SECONDS:-10}"
# Producer admission is independent of the heavy shard pool.
readonly REQUIRED_IDLE_RUNNERS=2

heavy_ready=false

select_runner() {
  echo "runner=$1" >> "${GITHUB_OUTPUT}"
  echo "heavy_ready=${heavy_ready}" >> "${GITHUB_OUTPUT}"
  echo "PR Gate compute: $1 ($2)"
  if [[ -n "${GITHUB_STEP_SUMMARY:-}" ]]; then
    echo "PR Gate compute: \`$1\` ($2)" >> "${GITHUB_STEP_SUMMARY}"
  fi
  exit 0
}

group_field() {
  aws autoscaling describe-auto-scaling-groups --auto-scaling-group-names "${BUILD_HOST_GROUP}" \
    --query "AutoScalingGroups[0].$1" --output text
}

# Prints ready, busy or booting for the in-service instance's latest heartbeat.
host_state() {
  local instance heartbeat
  instance="$(group_field "Instances[?LifecycleState=='InService'] | [0].InstanceId")" || return 1
  heartbeat="$(aws ssm get-parameter --name "${HEARTBEAT_PARAMETER}" --query Parameter.Value --output text)" || return 1
  bash "$(dirname "${BASH_SOURCE[0]}")/gate-runner-heartbeat.sh" "${instance}" <<< "${heartbeat}"
}

label="${HONUA_PR_GATE_RUNNER:-}"
[[ -n "${label}" ]] || select_runner "${HOSTED_RUNNER}" "build-host runner not configured"
[[ -n "${AWS_ACCESS_KEY_ID:-}" ]] || select_runner "${HOSTED_RUNNER}" "no build-host wake credentials"

desired="$(group_field DesiredCapacity)" || select_runner "${HOSTED_RUNNER}" "build host unreadable"
if [[ "${desired}" == 0 ]]; then
  aws autoscaling set-desired-capacity --auto-scaling-group-name "${BUILD_HOST_GROUP}" --desired-capacity 1 ||
    select_runner "${HOSTED_RUNNER}" "build host wake failed"
fi

deadline=$((SECONDS + WAIT_SECONDS))
while true; do
  proof="$(host_state)" || proof='{"state":"unreadable","heavy_ready":false}'
  heavy_ready="$(jq -r '.heavy_ready == true' <<< "${proof}")"
  state="$(jq -r '.state' <<< "${proof}")"
  case "${state}" in
    ready) select_runner "${label}" "build host has ${REQUIRED_IDLE_RUNNERS} idle runners" ;;
    busy) select_runner "${HOSTED_RUNNER}" "build-host runners busy" ;;
  esac
  (( SECONDS < deadline )) || select_runner "${HOSTED_RUNNER}" "build host not ready within ${WAIT_SECONDS}s (${state})"
  sleep "${POLL_SECONDS}"
done
