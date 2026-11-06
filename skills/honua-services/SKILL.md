---
name: honua-services
description: Use when importing a small dataset, publishing it as a Honua layer, and checking a filtered feature query.
---

# Import, publish, and query

Read the install `AGENTS.md`. Call serving MCP at `http://127.0.0.1:<HONUA_HTTP_PORT>/mcp` with header `X-API-Key` from `secrets/honua_admin_password`. Do not print that file. If `tools/list` lacks `honua_ingest_dataset` and `honua_publish_service`, reconnect with `?view=configure`.

1. For inline data of at most 4 MB, call `honua_ingest_dataset` with `format` `geojson` or `csv` and a `datasetName`. Keep `connectionId`, `schema`, `table`, `geometryColumn`, and `primaryKey` from the result. The same `datasetName` replaces that table. When `connectionId` is omitted, register the quickstart catalog once and do not ingest again. POST `/api/v1/admin/connections` on the serving port with `name` `catalog`, `host` `postgres`, `port` 5432, `databaseName` `honua`, `username` `honua`, `provider` `postgis`, `sslRequired` false, and `sslMode` `Disable`. A script may read `secrets/postgres_password` into that request and `secrets/honua_admin_password` into `X-API-Key`. Print neither. Pass the returned `name` as `connectionId`.
2. Call `honua_publish_service` with `connectionId`, `schema`, `table`, and `layerName`. Pass `geometryColumn` and `primaryKey` when ingest returned them. `Completed` includes the published service and layer. `RequiresApproval` is not published. `Queued` is a job id to poll.
3. If the reply is lost, call `honua_list_layers` before publishing again. A second `honua_publish_service` creates another layer.
4. Verify with [a feature query](references/query.md).
5. Success is the new layer in `honua_list_layers` and a query whose features or `count` match the filter you sent. Say when the count could not be checked.

Common failure: HTTP 401 means the API key header was missing. An import refusal for admin write means the configure view was not selected. A timeout after ingest, with no `Completed` publish, means the table is there and publication did not finish. Continue from that table. Do not ingest it again.

Leave service access unchanged unless the user asked to change it.

Layer publication behaviour: [Publish layers](https://honua.gitbook.io/honuaio/work-with-your-data/publish-data/publish-layers).
