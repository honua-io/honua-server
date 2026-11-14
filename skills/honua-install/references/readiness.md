# Readiness without extra container packages

The quickstart's image includes a compiled health probe. Do not assume a
shell, `wget`, `curl`, or a package manager exists inside it.

From the quickstart directory, check the serving container with:

```bash
docker compose exec -T honua /app/honua-healthcheck --ready
```

This invokes the same readiness probe as the image health check.
A zero exit status confirms that health check, not MCP authorization or a
successful publish. Connect a real MCP client and list tools separately.

If the check fails, inspect `docker compose ps` and a bounded log excerpt,
redacting connection strings and credentials before sharing. Preserve the
install's volumes and `secrets/`; reinstalling credentials can strand the data.
