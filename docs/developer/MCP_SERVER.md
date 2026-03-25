# MCP Server

Honua ships an MCP server package in the `honua-sdk-js` repository (path `mcp`, package `@honua/mcp-server`) so AI clients can safely discover services, inspect layer schema, and run filtered geospatial queries.

This document covers the public/open-core MCP data-access surface. It does **not** describe Honua's private operator tooling or AI DevOps rollout automation layer.

## Capabilities

- Service discovery: list available services and metadata
- Layer introspection: fetch field/schema details for a layer
- Query workflows: query features, counts, extents, and statistics
- Transport choice: `grpc-web` (default) or `rest`

## Runtime Configuration

Set these environment variables before launching the MCP server:

- `HONUA_BASE_URL` (required): absolute server URL, for example `https://honua.example.com`
- `HONUA_TRANSPORT` (optional): `grpc-web` (default) or `rest`
- `HONUA_API_KEY` (optional): API key if your deployment requires it
- `HONUA_TIMEOUT_MS` (optional): request timeout in milliseconds (default `30000`)
- `HONUA_RETRY_MAX_RETRIES` (optional): retry attempts for transient failures (default `2`)

When `HONUA_API_KEY` is set, use `https://` for non-localhost servers.

## Source Repository

- GitHub: `https://github.com/honua-io/honua-sdk-js`
- Package path: `mcp/`

## Run

```bash
git clone https://github.com/honua-io/honua-sdk-js.git
cd honua-sdk-js/mcp
npm ci
npm run build
HONUA_BASE_URL="https://honua.example.com" HONUA_TRANSPORT="grpc-web" node dist/src/index.js
```

## Exposed MCP Tools

- `honua_list_services`
- `honua_describe_layer`
- `honua_query_features`
- `honua_count_features`
- `honua_get_extent`
- `honua_statistics`

## Exposed MCP Resources

- `honua://services`
- `honua://services/{encodedServiceId}/layers/{layerId}`

## Certification

> **Not yet active.** The server-side CI jobs and seed data are landed, but the SDK-side certification scripts (`test:certification`, `test:certification:artifact`, `test:llm-smoke`) are not yet present in `honua-sdk-js` `trunk`. Until those scripts land, the CI jobs skip with a warning annotation and **no certification artifacts are produced**. See [Known gaps](#known-gaps).

Once the SDK-side scripts are available, Honua's CI will run an MCP certification lane on every push and pull request to `trunk` (and on manual dispatch). The suite exercises all 6 tools and 2 resources across both `grpc-web` and `rest` transports. When the `test:certification:artifact` script is also present, the lane produces machine-readable (JSON) and human-readable (Markdown) evidence artifacts; if only `test:certification` is landed, tests run but a CI warning notes the missing artifacts.

| Area | What is tested |
|------|----------------|
| Tools | `honua_list_services`, `honua_describe_layer`, `honua_query_features`, `honua_count_features`, `honua_get_extent`, `honua_statistics` |
| Resources | `honua://services`, `honua://services/{encodedServiceId}/layers/{layerId}` |
| Transports | `grpc-web`, `rest` (CI matrix, one run each) |
| Cross-cutting | Auth (skipped under dev-auth — see [Known gaps](#known-gaps)), timeout (`HonuaTimeoutError`), retry (429/5xx backoff), failure cases (bad serviceId, bad layerId, invalid WHERE) |

Certification artifacts are uploaded per-transport as `mcp-certification-{transport}` with 30-day retention. These are separate from the [Client Template Version Matrix](CLIENT_TEMPLATE_VERSION_MATRIX.md).

A non-blocking LLM smoke lane runs after certification passes, connecting OpenAI `gpt-4o` to the MCP server to prove the interface is usable by an actual agent. Smoke transcripts are stored in a separate `mcp-llm-smoke-transcripts` artifact.

See [MCP Certification](../contributor/mcp-certification.md) for contributor guidance on seed data, CI jobs, and test structure.

### Known gaps

- Auth certification is skipped when `HONUA_DEV_AUTH=true` (CI default). Full auth certification requires a non-dev-auth server lane.
- Cache-invalidation testing deferred until anonymous writes are available in dev auth mode.
- C# SDK interop lane deferred to follow-up work.
- Certification test code lives in `honua-sdk-js`. The SDK ref is controlled by the `MCP_SDK_REF` env var in `ci.yml` (currently `trunk`). While set to a branch name, certification is useful for development but artifacts are not reproducible release evidence. Pin `MCP_SDK_REF` to a specific tag or commit SHA for release-grade certification; `workflow_dispatch` `sdk_ref` overrides for one-off replays.
- MCP certification and LLM smoke jobs skip cleanly (with a CI warning annotation) when the required `test:certification` / `test:llm-smoke` scripts are not yet present in the checked-out SDK ref.

## Notes

- Prefer `grpc-web` for performance when available.
- Use server-side auth controls (API key or OIDC) exactly as you do for direct client calls.
