# Readiness without extra container packages

The quickstart's image has Bash and a built-in Bash TCP health check; do
not assume `wget`, `curl`, or a package manager exists inside it.

From the quickstart directory, check the serving container with:

```bash
docker compose exec -T honua bash -c 'exec 3<>/dev/tcp/127.0.0.1/8080 && printf "GET /healthz/ready HTTP/1.0\r\nHost: 127.0.0.1\r\nConnection: close\r\n\r\n" >&3 && IFS= read -r status <&3 && case "$status" in *" 200 "*) exit 0 ;; *) exit 1 ;; esac'
```

This uses the same Bash TCP mechanism as the image health check, targeting
readiness rather than liveness.
A zero exit status confirms that health check, not MCP authorization or a
successful publish. Connect a real MCP client and list tools separately.

If the check fails, inspect `docker compose ps` and a bounded log excerpt,
redacting connection strings and credentials before sharing. Preserve the
install's volumes and `secrets/`; reinstalling credentials can strand the data.
