# MCP Server

Honua ships an MCP server package in the `honua-sdk-js` repository (path `mcp`, package `@honua/mcp-server`) so AI clients can safely discover services, inspect layer schema, and run filtered geospatial queries.

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

## Notes

- Prefer `grpc-web` for performance when available.
- Use server-side auth controls (API key or OIDC) exactly as you do for direct client calls.
