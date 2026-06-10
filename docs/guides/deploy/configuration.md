# Configure Honua Server

You'll understand how Honua's configuration model works so you can set any option in any deployment target. The full variable table lives in the [environment variable reference](../../reference/configuration/environment-variables.md).

**Prerequisites:** None.

## Environment variables are the configuration surface

Honua is configured entirely through environment variables ([ADR-0008](../../internal/contributor/adr/0008-env-var-configuration.md)). `appsettings.json` is for local development only; containers, Kubernetes, and serverless all receive the same flat env-var contract. There is no config file to mount and no config service to run.

## The `Section__Key` convention

Env vars map onto the .NET configuration tree with double underscores as the path separator, and array elements use numeric suffixes:

```bash
ConnectionStrings__DefaultConnection="Host=db;Database=honua;Username=honua;Password=secret"
Limits__Query__MaxRecordCount=5000
Cors__AllowedOrigins__0=https://app.example.com
Cors__AllowedOrigins__1=https://admin.example.com
```

Docs that write a setting as `Section:Key` (the .NET colon form) mean the same thing — replace `:` with `__` in env vars.

## Startup validation

Options are validated at startup (`ValidateOnStart`), so a malformed value fails the process immediately instead of surfacing later:

- Out-of-range `Limits__*` values, malformed control-plane URLs, and invalid TTLs abort startup with a descriptive error.
- `Security__ConnectionEncryption__MasterKey` must be at least 32 characters.
- `HONUA_DEV_AUTH=true` in a Production environment refuses to start.
- A missing database connection string skips migrations and logs an error in Production.

Confirm effective values at runtime with `GET /api/v1/admin/config` (admin auth).

## Secret references

Secrets don't have to be inlined. Two mechanisms exist:

1. **Connection-string references** — `ConnectionStrings__DefaultConnection` accepts provider-prefixed references such as `aws:secretsmanager:...` or `env:...`, resolved at startup before migrations run.
2. **Metadata secret references** — connection metadata stores a structured reference instead of a value: `{"provider": "env", "ref": "MY_DB_PASSWORD"}` (optional `version`), with providers like `env`, `azure-key-vault`, or `connection-registry`.

## `.env` files: Compose vs Kubernetes

- **Docker / Compose**: `.env` files are first-class — `docker compose --env-file .env.production up` or `env_file:` on the service. The repo ships [`.env.example`](../../../.env.example) (annotated catalog) and [`.env.production.example`](../../../.env.production.example) (production-tuned baseline) as starting points.
- **Kubernetes**: there is no `.env` file at runtime. Put secrets in a `Secret`, non-secrets in a `ConfigMap`, and mount both with `envFrom`; the keys are the same `Section__Key` names.

## Next steps

- [Environment variable reference](../../reference/configuration/environment-variables.md)
- [Deploy with Docker Compose](docker-compose.md)
- [Deploy on Kubernetes](kubernetes.md)
