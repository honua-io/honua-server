---
name: honua-install
description: Use when installing or checking the Honua Compose quickstart with private secret files and separate serving and management endpoints.
---

# Install the Honua quickstart

Work in the extracted quickstart package. Read its `AGENTS.md` before operating it.
Use Docker Compose v2, Python 3 and OpenSSL on a dedicated Docker host.

1. Inspect `images.json` for the release version and immutable image references.
   Run `python3 quickstart.py --init-only` to initialize the install, then
   `python3 quickstart.py` to start it. Run these commands from the package directory.
2. Credentials are created in individual files under `secrets/`, including the
   keyring. Preserve that directory and the database volumes. `.env` contains
   settings; an older install can still contain credentials there until migration.
   Never display either file contents or container environments. Never run
   `docker compose config --environment`. For a service inventory use
   `docker compose config --services`.
3. Check `docker compose ps` and the serving readiness endpoint at
   `http://localhost:18080/healthz/ready`. Use the image's own health command when
   checking from inside a container; see [readiness](references/readiness.md).
4. Connect the MCP client to the serving endpoint `http://localhost:18080/mcp`
   and list tools. Management operations use `http://localhost:18082/mcp`.
   Configure credentials by environment reference in the client, never by pasting
   keys into chat or a checked-in configuration.
5. Report the release pins, service health and successful MCP tool discovery.
   Startup alone does not prove readiness. If startup fails, report a bounded,
   redacted diagnostic; do not dump configuration or secret files.

Creating proposer and approver credentials is an operator step on the management
API. Keep their values in the protected secret channel. Installing this skill
neither grants credentials nor permits self-approval.
