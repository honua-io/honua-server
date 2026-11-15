#!/usr/bin/env bash
# Chooses where the heavy PR Gate jobs run: the build-host runner label when the
# host reports enough idle runners, otherwise ubuntu-latest. It wakes a stopped
# host for later runs but never waits longer than GATE_RUNNER_WAIT_SECONDS, and
# every failure selects the hosted runner, so the gate cannot hang on the host.
# Idle heavy runners beyond the shard reserve also take the jobs that would
# otherwise wait for a hosted runner (start_heavy), longest hosted job first.
#
# Inputs: HONUA_PR_GATE_RUNNER (label; empty disables), HONUA_HEAVY_RUNNER (the
# heavy pool's runs-on JSON; empty keeps every start job hosted), AWS
# credentials for the wake role, GITHUB_OUTPUT.
# Contract: honua-iac bootstrap/aws-build-host README.

set -uo pipefail

readonly HOSTED_RUNNER=ubuntu-latest
readonly BUILD_HOST_GROUP=honua-lane-build-host
readonly HEARTBEAT_PARAMETER=/honua-lane-build-host/gate-runner/heartbeat
readonly WAIT_SECONDS="${GATE_RUNNER_WAIT_SECONDS:-180}"
readonly POLL_SECONDS="${GATE_RUNNER_POLL_SECONDS:-10}"
# Producer admission is independent of the heavy shard pool.
readonly REQUIRED_IDLE_RUNNERS=2
# Idle heavy runners never planned: two for this gate's even affected shards,
# which start after Build and tests and have no hosted fallback, and one for
# advisory and AOT jobs that share the pool.
readonly HEAVY_RESERVE=3
# A gate spends half of the idle runners above the reserve, at most this many,
# so a second gate reading the same heartbeat still finds the reserve free.
readonly START_HEAVY_CAP=2
# Longest hosted run first (gate-model.md lists the measurements).
# server-governance starts PostGIS and unit-fast is shortest; both stay hosted.
readonly START_JOBS=(analyzers-server architecture server-fast analyzers-other)

heavy_ready=false
start_heavy=","

select_runner() {
  echo "runner=$1" >> "${GITHUB_OUTPUT}"
  echo "heavy_ready=${heavy_ready}" >> "${GITHUB_OUTPUT}"
  echo "start_heavy=${start_heavy}" >> "${GITHUB_OUTPUT}"
  echo "PR Gate compute: $1 ($2); heavy start jobs: ${start_heavy}"
  if [[ -n "${GITHUB_STEP_SUMMARY:-}" ]]; then
    echo "PR Gate compute: \`$1\` ($2); heavy start jobs: \`${start_heavy}\`" >> "${GITHUB_STEP_SUMMARY}"
  fi
  exit 0
}

# Prints the one label that alone selects the heavy pool (the group's own
# label, or a sole label), else nothing: a generic label such as self-hosted
# would match runners outside the group.
heavy_label() {
  jq -rs 'if length != 1 then empty else .[0] |
    if type == "object" then
      .group as $group | (.labels // []) as $labels |
      if ($group | type) == "string" and ($labels | type) == "array" and any($labels[]; . == $group) then $group
      elif ($labels | type) == "array" and ($labels | length) == 1 then $labels[0] else empty end
    elif type == "array" and length == 1 then .[0]
    elif type == "string" then . else empty end end' <<< "${HONUA_HEAVY_RUNNER:-}" 2>/dev/null |
    grep -xE '[A-Za-z0-9._-]+' || true
}

# Prints "<producer runner> <start_heavy>" for $1 idle heavy runners and the
# producers' current runner $2. The budget is half the idle runners above the
# reserve, capped; a busy gate pool moves both producers first (they gate the
# shards), then START_JOBS take what is left.
plan_heavy() {
  local idle="$1" producer="$2" planned="," pool budget job
  pool="$(heavy_label)"
  budget=$(( (idle - HEAVY_RESERVE) / 2 ))
  (( budget <= START_HEAVY_CAP )) || budget=${START_HEAVY_CAP}
  [[ -n "${pool}" ]] || budget=0
  if [[ "${producer}" == "${HOSTED_RUNNER}" ]] && (( budget >= 2 )); then
    producer="${pool}"
    budget=$((budget - 2))
  fi
  for job in "${START_JOBS[@]}"; do
    (( budget > 0 )) || break
    planned+="${job},"
    budget=$((budget - 1))
  done
  echo "${producer} ${planned}"
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
  heavy_idle="$(jq -r 'if (.heavy_idle | type) == "number" then .heavy_idle else 0 end' <<< "${proof}")"
  state="$(jq -r '.state' <<< "${proof}")"
  case "${state}" in
    ready)
      read -r producer start_heavy <<< "$(plan_heavy "${heavy_idle}" "${label}")"
      select_runner "${producer}" "build host has ${REQUIRED_IDLE_RUNNERS} idle runners" ;;
    busy)
      read -r producer start_heavy <<< "$(plan_heavy "${heavy_idle}" "${HOSTED_RUNNER}")"
      select_runner "${producer}" "build-host gate runners busy; ${heavy_idle} idle heavy runners" ;;
  esac
  (( SECONDS < deadline )) || select_runner "${HOSTED_RUNNER}" "build host not ready within ${WAIT_SECONDS}s (${state})"
  sleep "${POLL_SECONDS}"
done
