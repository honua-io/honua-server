---
type: guide
title: "Run a local development environment"
description: "Build Honua from source and run the local Docker Compose stack."
---
# Run a local development environment

Use the repo-root Docker Compose stack to run PostGIS, Redis, and Honua Server from a source build.

**Prerequisites:** Docker with Compose v2, Python 3.11 or later, and GitHub CLI authenticated with package-read access. Clone [honua-server](https://github.com/honua-io/honua-server) and run the following commands from its root.

## Build and start

Build the local image with a GitHub token that can read the repository's packages, then initialize private per-install credentials and start the stack:

```bash
GH_TOKEN=$(gh auth token) bash scripts/docker/build-with-github-packages.sh -t honua-server:local .
python3 scripts/docker/quickstart.py
docker compose ps
```

The bootstrap writes random database credentials to a private `.env` file and preserves them on restart. Keep that file with the persistent volumes; editing a saved password does not rotate the database's existing credentials. Ports bind to loopback by default.

The Compose stack includes development-only admin credentials, connection encryption settings, Redis control-plane configuration, and browser origins. Follow the [source quickstart](../../internal/developer/source-quickstart.md) to publish a sample layer and display a map.

## Verify and iterate

Open `http://localhost:8080/healthz/ready`. Expected response: `Ready`.

After source changes, repeat the image build and bootstrap commands. Inspect logs or stop the stack while retaining its data:

```bash
docker compose logs --tail 100 honua
docker compose stop
```

For .NET hot reload against local PostGIS, follow the [contributor setup guide](../../internal/contributor/development/getting-started.md).

## Troubleshoot

- **A host port is occupied:** set `HONUA_HTTP_PORT`, `POSTGRES_PORT`, or `REDIS_PORT` in `.env`, then restart the stack. See the [Compose guide](docker-compose.md) for configuration.
- **The image build cannot restore private packages:** check GitHub CLI authentication and package-read access.
- **A service is unhealthy:** inspect `docker compose ps` and `docker compose logs --tail 100` before retrying.

## Next steps

- [Go from zero to a map in your browser](../../get-started/quickstart.md)
- [Configure Honua Server](configuration.md)
- [Deploy with Docker Compose](docker-compose.md)
