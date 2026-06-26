#!/bin/sh
# Control-plane reconcile Lambda entrypoint.
#
# Starts the full Honua.Server host in the background — fronted by the AWS Lambda Web Adapter, it
# serves the internal /internal/control-plane reconcile routes on the loopback port — then execs the
# thin custom-runtime bootstrap, which deserializes each EventBridge invocation and forwards a single
# reconcile/backstop request to the host. The host carries the fully-wired reconcile graph; the
# bootstrap stays graph-free for a fast cold start.
set -eu

PORT="${AWS_LWA_PORT:-${PORT:-8080}}"

# Launch the full host (LWA reads $AWS_LWA_PORT). It runs with ControlPlane__TriggerMode=Event, so its
# in-process poll/backstop timers are disabled and it only serves the on-demand reconcile routes.
"${LAMBDA_TASK_ROOT}/server/Honua.Server" &
SERVER_PID=$!

# Wait for the host to accept connections before handing invocations to the bootstrap. Bounded so a
# wedged host fails the invocation rather than hanging until the Lambda timeout.
i=0
until curl -fsS "http://127.0.0.1:${PORT}/healthz/live" >/dev/null 2>&1; do
  i=$((i + 1))
  if [ "$i" -ge 60 ]; then
    echo "control-plane host did not become ready on port ${PORT}" >&2
    break
  fi
  if ! kill -0 "$SERVER_PID" 2>/dev/null; then
    echo "control-plane host exited during startup" >&2
    break
  fi
  sleep 0.5
done

# Hand control to the custom-runtime bootstrap (the Lambda runtime client loop).
exec "${LAMBDA_TASK_ROOT}/bootstrap"
