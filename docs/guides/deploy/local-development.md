# Run a local development environment

You'll run Honua from source with .NET Aspire — one command that starts PostGIS, Redis, and the server with a dashboard for logs, traces, and metrics.

**Prerequisites:** .NET 10 SDK, Docker (Aspire starts the database and cache as containers), and a clone of [honua-server](https://github.com/honua-io/honua-server).

## Steps

1. Start the Aspire app host from the repo root.

```bash
dotnet run --project src/Honua.AppHost
```

2. Open the Aspire dashboard at the URL printed in the console output. It shows every resource's state, console logs, distributed traces, and metrics in one place.

The app host (`src/Honua.AppHost/Program.cs`) orchestrates:

| Resource | Details |
|---|---|
| `postgres` | PostGIS + pgRouting (`pgrouting/pgrouting:17-3.5-3.7.3`) with a persistent `honua-postgres-data` volume and pgAdmin |
| `honua` database | Created on the postgres resource and injected as the connection string |
| `redis` | Redis with Redis Commander |
| `honua-server` | The server project, started after postgres and redis are healthy, with both connection strings wired automatically |

3. Iterate. Stop with `Ctrl+C` and re-run after code changes; the data volume persists between runs.

```bash
dotnet build src/Honua.Server/Honua.Server.csproj
```

## Plain Docker Compose alternative

If you don't want the .NET SDK in the loop, the repo-root `docker-compose.yml` builds the server from source and starts PostGIS (`docker compose up -d`), with optional `--profile redis` and `--profile minio`. The [quickstart](../../get-started/quickstart.md) walks through that path end to end, including the `docker-compose.override.yml` that sets the admin password, master key, and CORS origin.

## Verify

```bash
curl -s http://localhost:8080/healthz/ready
```

Expected output: `Ready`.

## Troubleshoot

- **`dotnet run` fails resolving Aspire workloads** — update the .NET SDK to the version pinned in `global.json` and restore: `dotnet restore Honua.sln`.
- **Postgres container conflicts with a local Postgres on 5432** — stop the local service or let Aspire's dynamic port mapping stand; connect through the dashboard's listed endpoint, not `localhost:5432`.
- **Admin endpoints return 401** — local dev runs without an admin password unless you set `HONUA_ADMIN_PASSWORD`; set it before `dotnet run` if you need admin APIs.
- **Stale schema after switching branches** — migrations only roll forward; drop the `honua-postgres-data` volume (`docker volume rm honua-postgres-data`) to rebuild from scratch.

## Next steps

- [Go from zero to a map in your browser](../../get-started/quickstart.md)
- [Configure Honua Server](configuration.md)
- [Deploy with Docker Compose](docker-compose.md)
