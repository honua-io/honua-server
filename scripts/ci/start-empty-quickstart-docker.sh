#!/usr/bin/env bash
set -euo pipefail

if [[ "${GITHUB_ACTIONS:-}" != true || "${RUNNER_ENVIRONMENT:-}" != github-hosted ]]; then
  echo 'This setup requires a disposable GitHub-hosted Linux runner.' >&2
  exit 1
fi

daemon_dir=$(mktemp -d "${RUNNER_TEMP:?}/quickstart-docker.XXXXXX")
registry_mirror=https://mirror.gcr.io
sudo systemctl stop docker.service docker.socket
sudo nohup dockerd --host unix:///var/run/docker.sock \
  --registry-mirror "${registry_mirror}" \
  --data-root "${daemon_dir}/data" --exec-root "${daemon_dir}/exec" \
  --pidfile "${daemon_dir}/daemon.pid" > "${daemon_dir}/daemon.log" 2>&1 < /dev/null &

for attempt in {1..30}; do
  if mirrors=$(docker info --format '{{range .RegistryConfig.Mirrors}}{{println .}}{{end}}' 2>/dev/null); then
    if [[ "${mirrors%/}" != "${registry_mirror}" ]]; then
      echo 'The empty installation Docker daemon did not activate the Docker Hub mirror.' >&2
      exit 1
    fi
    exit 0
  fi
  sleep 1
done
echo 'The empty installation Docker daemon did not start.' >&2
exit 1
