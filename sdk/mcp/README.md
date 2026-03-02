# `@honua/mcp-server`

Model Context Protocol (MCP) server for Honua geospatial feature services.

This package exposes a focused MCP surface for discovery and query workflows on top of Honua's FeatureServer APIs.

## Requirements

- Node.js `>=20`
- A reachable Honua server URL

## Environment Variables

- `HONUA_BASE_URL` (required): absolute Honua base URL, for example `https://honua.example.com`
- `HONUA_TRANSPORT` (optional): `grpc-web` (default) or `rest`
- `HONUA_API_KEY` (optional): admin/API key when your deployment requires it

## Run Locally

```bash
npm install
npm run build
HONUA_BASE_URL="https://honua.example.com" HONUA_TRANSPORT="grpc-web" node dist/src/index.js
```

## MCP Tools

- `honua_list_services`
- `honua_describe_layer`
- `honua_query_features`
- `honua_count_features`
- `honua_get_extent`
- `honua_statistics`

## MCP Resources

- `honua://services`
- `honua://services/{encodedServiceId}/layers/{layerId}/schema`
