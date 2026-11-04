# Verify readiness

Follow the [quickstart health checks](https://honua.io/docs/get-started/quickstart/)
and the [health capability](https://honua.io/docs/okf/capabilities/ops.health/).
Use the image's built-in health command; do not install diagnostic packages
inside a running container or assume extra HTTP clients are present.

Check `docker compose ps`, then readiness. Connect a real MCP client and list
tools separately: a successful health check does not prove MCP authorization
or publication. On failure, capture a bounded, redacted log excerpt. Preserve
volumes and `secrets/`; reinstalling credentials can strand the data.
