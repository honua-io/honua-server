#!/usr/bin/env bash
# GET one http URL from inside a server container and print the body.
# Exit 0 only when the status line contains " 200 ". Callers pass the URL as
# argv and this file on stdin:
#   timeout 15 docker compose exec -T honua-server bash -s -- "$url" < scripts/docker/container-http-get.sh
# The runtime images do not install an HTTP client (CVE-2021-31879). No temp files:
# cite containers run with a read-only root and a noexec /tmp.
set -euo pipefail

url="${1:?url}"
rest="${url#http://}"
if [[ "$rest" == "$url" ]]; then
  echo "only http URLs are supported: ${url}" >&2
  exit 2
fi

hostport="${rest%%/*}"
if [[ "$rest" == */* ]]; then
  path="/${rest#*/}"
else
  path="/"
fi
host="${hostport%%:*}"
if [[ "$hostport" == *:* ]]; then
  port="${hostport##*:}"
else
  port=80
fi

connect_host="$host"
if [[ "$host" == "localhost" ]]; then
  connect_host="127.0.0.1"
fi

exec 3<>"/dev/tcp/${connect_host}/${port}"
printf 'GET %s HTTP/1.0\r\nHost: %s\r\nConnection: close\r\n\r\n' "$path" "$host" >&3

IFS= read -r status <&3 || true
status="${status%$'\r'}"
if [[ -z "$status" ]]; then
  exit 1
fi

in_body=0
while IFS= read -r line || [[ -n "${line:-}" ]]; do
  line="${line%$'\r'}"
  if [[ "$in_body" -eq 0 ]]; then
    if [[ -z "$line" ]]; then
      in_body=1
    fi
    continue
  fi
  printf '%s\n' "$line"
done <&3

case "$status" in
  *' 200 '*) exit 0 ;;
  *) exit 1 ;;
esac
