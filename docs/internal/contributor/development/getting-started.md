# Getting Started

This guide gets you from zero to a running Honua Server with a working development loop.

## Prerequisites

- **Docker + Docker Compose v2** (the fastest path)
- **Git**

For building from source outside Docker you also need:
- **.NET 10.0 SDK** or later
- **PostgreSQL 14+** with the PostGIS extension

## 1. Clone and start

```bash
git clone https://github.com/honua-io/honua-server.git
cd honua-server

# Start PostGIS + Honua Server (builds from source)
docker compose up -d
```

The root `docker-compose.yml` provisions:

| Service | Default port | Credentials |
|---------|-------------|-------------|
| PostGIS | 5432 | `honua_user` / `honua_password`, database `honua_dev` |
| Honua Server | 8080 | — |

Database migrations run automatically on first boot.

## 2. Verify it works

```bash
# Health check (should return 200)
curl http://localhost:8080/healthz/ready

# List configured services (empty on a fresh install)
curl http://localhost:8080/rest/services?f=json
```

## 3. Optional services

Add these as needed — none are required for basic development:

```bash
# Redis (distributed caching)
HONUA_REDIS_URL=redis:6379 docker compose --profile redis up -d

# MinIO (S3-compatible storage for file imports)
HONUA_STORAGE_PROVIDER=AwsS3 HONUA_S3_BUCKET=honua-dev \
  HONUA_S3_SERVICE_URL=http://minio:9000 \
  HONUA_S3_ACCESS_KEY_ID=minioadmin HONUA_S3_SECRET_ACCESS_KEY=minioadmin \
  docker compose --profile minio up -d

# Reusable external test database (separate PostGIS instance on port 5433)
docker compose -f docker/docker-compose.test-db.yml up -d
```

Port overrides: `POSTGRES_PORT`, `REDIS_PORT`, `HONUA_HTTP_PORT`.

## 4. Run tests

Integration tests use [Testcontainers](https://dotnet.testcontainers.org/) and spin up their own ephemeral PostGIS instance — you don't need any running database to run them.

```bash
dotnet test
```

Run by execution tier (preferred — matches the CI dispatch in [ADR-0037](../adr/0037-unified-ci-test-tier-strategy.md)):
```bash
dotnet test --filter "Tier=Fast"          # Unit-style tests, no DB/HTTP
dotnet test --filter "Tier=Integration"   # Most of the existing suite
dotnet test --filter "Tier=Slow"          # Scale/External/Emulator/Cloud (env vars required)
```

Run by legacy category (still supported; `Tier` is additive):
```bash
dotnet test --filter Category=Unit
dotnet test --filter Category=Integration
dotnet test tests/dotnet/Honua.Architecture.Tests/
```

Run only the `server-tests` shards a PR diff would target (mirrors the CI matrix selection):
```bash
scripts/ci/honua-server-targeted-tests.sh --base origin/trunk
# Emits {"run_all": ..., "shards": [...], "reason": "..."} — see ci-shards.json
```

## 5. Development loop

**With Docker (rebuild on change):**
```bash
docker compose up -d --build
```

**Without Docker (hot reload):**
```bash
# Set the connection string to point at the Docker PostGIS instance
export ConnectionStrings__DefaultConnection="Host=localhost;Database=honua_dev;Username=honua_user;Password=honua_password"

# Hot reload on file changes
dotnet watch run --project src/Honua.Server
```

**With .NET Aspire (dashboard with traces, logs, metrics):**
```bash
dotnet run --project src/Honua.AppHost
```

## Manual setup (no Docker)

If you prefer running PostgreSQL directly instead of through Docker:

### Install dependencies

**Ubuntu/Debian:**
```bash
# .NET 10
wget https://packages.microsoft.com/config/ubuntu/22.04/packages-microsoft-prod.deb
sudo dpkg -i packages-microsoft-prod.deb
sudo apt update && sudo apt install -y dotnet-sdk-10.0

# PostgreSQL + PostGIS
sudo apt install -y postgresql-14 postgresql-14-postgis-3
```

**macOS:**
```bash
brew install dotnet postgresql@14 postgis
brew services start postgresql@14
```

### Create the database

```bash
sudo -u postgres psql <<'EOF'
CREATE DATABASE honua_dev;
CREATE USER honua_user WITH ENCRYPTED PASSWORD 'honua_password';
GRANT ALL PRIVILEGES ON DATABASE honua_dev TO honua_user;
ALTER USER honua_user CREATEDB;
\c honua_dev
CREATE EXTENSION IF NOT EXISTS postgis;
CREATE EXTENSION IF NOT EXISTS postgis_topology;
EOF
```

### Run the server

```bash
export ConnectionStrings__DefaultConnection="Host=localhost;Database=honua_dev;Username=honua_user;Password=honua_password"
dotnet run --project src/Honua.Server
```

Migrations run automatically on startup.

## Project structure

```
src/
├── Honua.Server/          # ASP.NET Core host (Minimal APIs, vertical slices)
│   └── Features/          # FeatureServer, OGC, OData, Admin, Import, Tiles
├── Honua.Core/            # Domain models and abstractions (no infrastructure deps)
├── Honua.Postgres/        # PostGIS implementation of Core interfaces
└── Honua.DuckDB/          # DuckDB read-only provider

tests/
├── Honua.Server.Tests/    # Integration tests (Testcontainers + real PostGIS)
├── Honua.Core.Tests/      # Unit tests
├── Honua.Architecture.Tests/  # Dependency and pattern enforcement
└── Honua.TestKit/         # Shared test infrastructure
```

Dependency flow: **Server** depends on **Postgres** depends on **Core**. Never the reverse.

## Useful endpoints

Once the server is running:

| URL | Description |
|-----|-------------|
| `http://localhost:8080/healthz/ready` | Readiness probe |
| `http://localhost:8080/rest/services` | FeatureServer service catalog |
| `http://localhost:8080/ogc/features/collections` | OGC API collections |
| `http://localhost:8080/odata` | OData service root |
| `http://localhost:8080/openapi.json` | OGC API Features OpenAPI spec |
| `http://localhost:8080/docs` | Interactive API explorer (Scalar; dev mode only, or set `HONUA_SERVE_API_DOCS=true`) |

## Debugging

```bash
# Verbose logging
export Logging__LogLevel__Default=Debug
export Logging__LogLevel__Honua=Trace

# Npgsql command logging
export Logging__LogLevel__Npgsql=Information

# Application logs (Docker)
docker logs honua-honua-1 -f

# Direct database access
docker exec -it honua-postgres-1 psql -U honua_user -d honua_dev
```

## Common issues

**Tests fail with connection errors** — Integration tests use Testcontainers and need Docker running. Make sure Docker is started.

**Build fails with package restore errors:**
```bash
dotnet nuget locals all --clear && dotnet restore --force
```

**Port already in use** — Override with env vars: `POSTGRES_PORT=5433 HONUA_HTTP_PORT=9090 docker compose up -d`

## Next steps

- [Contributing](contributing.md) — code style, architecture rules, PR process
- [Architecture](../ARCHITECTURE.md) — system design and component interaction
- [ADRs](../adr/README.md) — rationale behind key design decisions
