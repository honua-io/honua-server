# Quickstart: AI setup to a saved map

This journey starts Honua in Docker, configures and publishes data through the
Admin API/admin MCP control plane, runs geoprocessing, saves a Studio composition,
and finishes at the focused Console approval checkpoint.

**Prerequisites:** Docker with Compose v2, Git, Node.js 20.19 or newer, `curl`,
and an MCP-capable agent. The first Docker build takes a few minutes.

## 1. Start the control plane

```bash
git clone https://github.com/honua-io/honua-server.git
cd honua-server
docker compose up -d
```

The repository Compose profile starts PostGIS, Redis, and Honua Server with
development-only credentials. Wait until both commands succeed:

```bash
docker compose ps
curl --fail http://localhost:8080/healthz/ready
```

The important endpoints are now:

- Admin API and explorer: `http://localhost:8080/api/v1/admin` and
  `http://localhost:8080/docs`
- MCP: `http://localhost:8080/mcp`
- OGC API Processes: `http://localhost:8080/ogc/processes`

Install the generated command-line client and the stdio-to-HTTP MCP bridge:

```bash
npm install --global @honua/sdk-js @honua/mcp-server
export HONUA_BASE_URL=http://localhost:8080
export HONUA_ADMIN_KEY=quickstart-admin-password
export HONUA_MCP_REMOTE_URL=http://localhost:8080/mcp
```

`honua admin` and the `honua_admin_*` tools are projections of the same Admin
OpenAPI operation inventory. They share validation, authorization, approval,
problem-detail, and audit behavior; CI rejects inventory drift. Console consumes
that control plane but does not define its completeness.

## 2. Connect an AI agent

For an HTTP-capable MCP client, register `http://localhost:8080/mcp` and send
`X-API-Key: ${HONUA_ADMIN_KEY}`. For a stdio-only client, add:

```json
{
  "mcpServers": {
    "honua": {
      "command": "honua-mcp-proxy",
      "env": {
        "HONUA_MCP_REMOTE_URL": "http://localhost:8080/mcp",
        "HONUA_ADMIN_KEY": "quickstart-admin-password"
      }
    }
  }
}
```

Restart the agent, ask it to list tools, and select the `set_up_and_publish`
prompt. Deterministic `honua_admin_*` tools are published by default. AI-assisted
operation descriptors remain off. Ask the agent to dry-run first and to stop for
approval on credentials, public-access changes, cost-bearing infrastructure, or
destructive actions.

See [Set up Honua with an AI agent](../guides/connect/ai-control-plane-setup.md)
for secret references, Docker/cloud profiles, and approval behavior.

## 3. Configure and publish data

Create the Compose database connection through the generated Admin API client:

```bash
cat > connection.json <<'JSON'
{
  "name": "local",
  "host": "postgres",
  "port": 5432,
  "databaseName": "honua_dev",
  "username": "honua_user",
  "password": "honua_password",
  "sslRequired": false,
  "sslMode": "Prefer"
}
JSON

honua admin connect createConnection --body @connection.json --yes --json
honua admin connect testConnection --path id=local --yes --json
```

The literal password is acceptable only for this disposable local profile. In a
shared environment use the operation schema's `secret_ref` input or the Admin
API's `secretReference` plus `secretType`; never place secret values in an agent
prompt, tool transcript, shell history, or committed JSON.

Create and upload a three-feature GeoJSON file. Multipart upload remains a direct
Admin API call; the response identifies the imported `honua_data.quickstart_points`
table.

```bash
cat > points.geojson <<'GEOJSON'
{"type":"FeatureCollection","features":[
 {"type":"Feature","properties":{"name":"Ferry Building"},"geometry":{"type":"Point","coordinates":[-122.3937,37.7955]}},
 {"type":"Feature","properties":{"name":"Coit Tower"},"geometry":{"type":"Point","coordinates":[-122.4058,37.8024]}},
 {"type":"Feature","properties":{"name":"Painted Ladies"},"geometry":{"type":"Point","coordinates":[-122.4330,37.7762]}}]}
GEOJSON

curl --fail-with-body \
  -H "X-API-Key: $HONUA_ADMIN_KEY" \
  -F "file=@points.geojson" \
  -F "TableName=quickstart_points" \
  http://localhost:8080/api/v1/admin/import/upload
```

Publish the table and make only the resulting service readable without a key:

```bash
cat > layer.json <<'JSON'
{
  "schema": "honua_data",
  "table": "quickstart_points",
  "layerName": "quickstart-points",
  "srid": 4326,
  "serviceName": "default"
}
JSON

honua admin publish publishLayer --path id=local --body @layer.json --yes --json
honua admin configure updateServiceAccessPolicy \
  --path serviceName=default \
  --body '{"allowAnonymous":true}' \
  --yes --json
```

The agent can perform the same sequence with the matching `honua_admin_*` tools.
Use `secret_ref` for credentials and retain the returned operation, resource, and
audit identifiers.

## 4. Run geoprocessing

Discover `geometry.buffer`, then submit the sample San Francisco point as an
asynchronous OGC job:

```bash
curl --fail http://localhost:8080/ogc/processes/processes/geometry.buffer

curl --fail-with-body \
  -H "X-API-Key: $HONUA_ADMIN_KEY" \
  -H "Content-Type: application/json" \
  -H "Prefer: respond-async" \
  -d '{"inputs":{"wkb":"AQEAAABQ/Bhz15pewNDVVuwv40JA","srid":4326,"distance":500}}' \
  http://localhost:8080/ogc/processes/processes/geometry.buffer/execution
```

Keep the `jobID` or `Location` from the response. Poll
`GET /ogc/processes/jobs/{jobId}` until it is `successful`, then read
`GET /ogc/processes/jobs/{jobId}/results`. An agent can discover, submit, poll,
and attach the same result through MCP. The full contract is in
[Run geoprocessing](../guides/query-analyze/run-geoprocessing.md).

## 5. Compose and save in Studio

Ask the connected agent:

> Create a Studio map draft named “San Francisco quickstart”. Add the published
> `default/quickstart-points` layer and the `geometry.buffer` result, set a view
> over San Francisco, validate and preview it, save it, then propose publication.
> Return the draft id, generation, immutable version or content id, and proposal id.

The agent uses the `honua_studio_*` family. Treat the returned `generation` as an
optimistic-concurrency token: re-read the draft before retrying a stale update.
The server-backed draft is the saved composition; verify it by reading it again
after restarting the server process or from another replica. Publication remains
a proposal until a human decision.

## 6. Inspect and approve in Console

Console is a required focused client for the 2026.1 journey. Start the image pin
from the matching platform release manifest, then open
`http://localhost:5174/operate`:

```bash
: "${HONUA_CONSOLE_IMAGE:?set the Console image pinned by the candidate manifest}"
docker compose --profile console up -d
```

Sign in and inspect the same connection, service/layer, GP job/result, saved
Studio artifact, and proposal identifiers returned above. Review the plan,
diff/dry-run, risk, and audit identifiers before approving or rejecting the
proposal. Also check Operate health and release/recovery guidance.

For a service-bound Console credential, mint exactly
`["admin:read","admin:approve"]`; do not give it general admin write authority.
Interactive operator sign-in uses operator RBAC. See
[Focused Console operation](../guides/operate/focused-console.md).

## Verify

The journey is complete when you have all of these receipts:

- healthy deployment and working `/mcp` handshake;
- connection test plus published service/layer identifiers;
- successful `geometry.buffer` job and result artifact;
- durable Studio draft/content identity and publication proposal;
- Console approval or rejection plus its audit identifier.

## Troubleshoot

- **`honua_admin_*` tools are absent** — confirm the operations toolset is composed,
  `Mcp__PublishOperations__AdminFamilyEnabled=true`, and deterministic publication
  has not been disabled. Re-run `tools/list` after restarting the server.
- **A write returns `RequiresApproval`** — this is a successful governed outcome,
  not a transport failure. Open the proposal in Console and have a different
  authorized operator decide it.
- **Jobs remain `accepted` or return 503** — Redis backs durable jobs, imports,
  proposals, and workflows. Restore it before retrying.
- **Browser reads return 401** — the `default` access-policy update did not succeed,
  or the draft references another service.
- More help: [Troubleshooting](../guides/deploy/troubleshooting.md).

## Next steps

- [Set up Honua with an AI agent](../guides/connect/ai-control-plane-setup.md)
- [Connect AI agents over MCP](../guides/connect/ai-agents-mcp.md)
- [Admin CLI reference](../reference/admin-api/cli.md)
- [Your first dataset](first-dataset.md)
