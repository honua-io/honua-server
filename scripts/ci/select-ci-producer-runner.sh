#!/usr/bin/env bash
# One read-only readiness snapshot. Never wake, wait for, or mutate a host.
set -uo pipefail
export AWS_MAX_ATTEMPTS=1 AWS_RETRY_MODE=standard

select_runner() {
  echo "runner=$1" >> "${GITHUB_OUTPUT}"
  echo "CI producer compute: $1 ($2)"
  exit 0
}

[[ "${HONUA_CI_PRODUCER_TRUSTED:-false}" == true ]] || select_runner ubuntu-latest "untrusted event"
[[ "${HONUA_CI_PRODUCER_RUNNER:-}" == honua-build ]] || select_runner ubuntu-latest "compatible pool not enabled"
[[ -n "${AWS_ACCESS_KEY_ID:-}" ]] || select_runner ubuntu-latest "read credentials unavailable"

instance="$(aws autoscaling describe-auto-scaling-groups \
  --auto-scaling-group-names honua-lane-build-host \
  --query "AutoScalingGroups[0].Instances[?LifecycleState=='InService'] | [0].InstanceId" \
  --output text --cli-connect-timeout 5 --cli-read-timeout 10)" ||
  select_runner ubuntu-latest "current instance unreadable"
[[ "${instance}" == i-* ]] || select_runner ubuntu-latest "no in-service instance"
heartbeat="$(aws ssm get-parameter --name /honua-lane-build-host/gate-runner/heartbeat \
  --query Parameter.Value --output text --cli-connect-timeout 5 --cli-read-timeout 10)" ||
  select_runner ubuntu-latest "heartbeat unreadable"
if jq -e --arg instance "${instance}" --argjson now "$(date +%s)" '
  def integer: if type == "number" then floor == . else false end;
  if type != "object" then false else
    .instance == $instance and (.at | integer) and .at <= $now and ($now - .at) <= 90
    and (.idle | integer) and .idle >= 2
  end' <<< "${heartbeat}" >/dev/null 2>&1; then
  select_runner honua-build "current heartbeat has two idle gate runners"
fi
select_runner ubuntu-latest "heartbeat stale, invalid, or pool busy"
